using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Admission and readback checks for the typed PCB rule-area/keepout surface.
/// Keepout restrictions are authored explicitly; filled polygons, parent links
/// and placement ownership remain native-derived state.
/// </summary>
internal static class PcbKeepoutAuthoringContract
{
    internal const int MaxPolygons = 256;
    internal const int MaxNodesPerRing = 4096;
    internal const int MaxTotalNodes = 8192;

    internal static void ValidateDocument(DocumentSpecifier? document)
    {
        DocumentStateTools.ValidateTarget(document);
        if (document!.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_keepout_target", "Keepout authoring requires an explicit PCB document.");
    }

    internal static void ValidateState(DocumentSpecifier document, DocumentLifecycleState? expected)
    {
        if (expected is null || expected.Scope != DocumentLifecycleScope.DlsPcb
            || expected.Document is null || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch)
            || !Guid.TryParseExact(expected.ProcessEpoch, "D", out var processEpoch) || processEpoch == Guid.Empty
            || string.IsNullOrWhiteSpace(expected.NativeIdentity)
            || !Guid.TryParseExact(expected.NativeIdentity, "D", out var nativeIdentity) || nativeIdentity == Guid.Empty
            || expected.Revision is null
            || !Guid.TryParseExact(expected.Revision.Epoch, "D", out var revisionEpoch) || revisionEpoch == Guid.Empty
            || expected.StateSha256.Length != 64 || !expected.StateSha256.All(char.IsAsciiHexDigitLower))
            throw new AutomationException("invalid_pcb_keepout_state",
                "expectedStateJson must identify the same PCB with a complete native lifecycle revision and digest.");
    }

    internal static void ValidateOperationId(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "D", out var value) || value == Guid.Empty)
            throw new AutomationException("invalid_pcb_keepout_operation", "Keepout authoring requires a canonical operation UUID.");
    }

    internal static void ValidateCreate(Zone zone) => ValidateZone(zone, requireIdentity: true, allowNativeParent: false, nativeIdentity: null);

    internal static void ValidateUpdate(Zone zone) => ValidateZone(zone, requireIdentity: true, allowNativeParent: false, nativeIdentity: null);

    /// <summary>
    /// Validates an existing native item before an update. Native board ownership
    /// may populate Parent during readback; this method still rejects foreign
    /// item types, derived fill state and placement-owned rule areas.
    /// </summary>
    internal static void ValidateExisting(Zone zone, string nativeIdentity) => ValidateZone(zone, requireIdentity: true, allowNativeParent: true, nativeIdentity);

    internal static void ValidateDeleteId(string keepoutId)
    {
        if (!Guid.TryParseExact(keepoutId, "D", out var value) || value == Guid.Empty)
            throw new AutomationException("invalid_pcb_keepout_identity", "Supply one canonical keepout UUID.");
    }

    private static void ValidateZone(Zone? zone, bool requireIdentity, bool allowNativeParent, string? nativeIdentity)
    {
        if (zone is null)
            throw new AutomationException("invalid_pcb_keepout", "Supply one typed PCB keepout.");
        if (requireIdentity && (zone.Id is null || !Guid.TryParseExact(zone.Id.Value, "D", out var id) || id == Guid.Empty))
            throw new AutomationException("invalid_pcb_keepout_identity", "The keepout needs one canonical UUID.");
        if (zone.Type != ZoneType.ZtRuleArea)
            throw new AutomationException("unsupported_pcb_keepout_type", "This authoring surface accepts PCB rule areas (keepouts) only.");
        if (zone.Layers.Count == 0 || zone.Layers.Count > 96)
            throw new AutomationException("invalid_pcb_keepout_layers", "A keepout needs one to 96 physical board layers.");

        var layers = new HashSet<int>();
        foreach (BoardLayer layer in zone.Layers)
        {
            int value = (int) layer;
            if (value is < (int) BoardLayer.BlFCu or > (int) BoardLayer.BlUser45 || !layers.Add(value))
                throw new AutomationException("invalid_pcb_keepout_layers", "Keepout layers must be distinct physical BoardLayer values.");
        }

        if (zone.Outline is null || zone.Outline.Polygons.Count == 0 || zone.Outline.Polygons.Count > MaxPolygons)
            throw new AutomationException("invalid_pcb_keepout_outline", "A keepout needs one to 256 polygon outlines.");
        int totalNodes = 0;
        foreach (PolygonWithHoles polygon in zone.Outline.Polygons)
        {
            if (polygon is null)
                throw new AutomationException("invalid_pcb_keepout_outline", "A keepout polygon entry cannot be empty.");
            ValidateRing(polygon.Outline, "outline", ref totalNodes);
            foreach (PolyLine hole in polygon.Holes)
                ValidateRing(hole, "hole", ref totalNodes);
        }

        if (zone.Filled || zone.FilledPolygons.Count != 0)
            throw new AutomationException("unsupported_pcb_keepout_fill", "Filled polygons are native-derived state; omit them from keepout authoring.");
        if (zone.Parent is not null && !string.IsNullOrWhiteSpace(zone.Parent.Value))
        {
            if (!allowNativeParent)
                throw new AutomationException("unsupported_pcb_keepout_parent", "A keepout parent is native-owned and cannot be authored.");
            if (string.IsNullOrWhiteSpace(nativeIdentity) || zone.Parent.Value != nativeIdentity)
                throw new AutomationException("unsupported_pcb_keepout_parent", "The existing keepout belongs to a different native board container.");
        }

        if (zone.RuleAreaSettings is null)
            throw new AutomationException("invalid_pcb_keepout_settings", "A keepout needs RuleAreaSettings.");
        RuleAreaSettings settings = zone.RuleAreaSettings;
        if (!settings.KeepoutCopper && !settings.KeepoutVias && !settings.KeepoutTracks
            && !settings.KeepoutPads && !settings.KeepoutFootprints)
            throw new AutomationException("invalid_pcb_keepout_settings", "A keepout must disallow at least one native object class.");
        if (settings.PlacementEnabled || !string.IsNullOrWhiteSpace(settings.PlacementSource))
            throw new AutomationException("unsupported_pcb_keepout_placement", "Placement-owned rule areas are not accepted by this keepout authoring surface.");
        if (!Enum.IsDefined(settings.PlacementSourceType))
            throw new AutomationException("invalid_pcb_keepout_settings", "RuleAreaSettings.PlacementSourceType must be a defined native enum value.");
        if (zone.Border is not null && !Enum.IsDefined(zone.Border.Style))
            throw new AutomationException("invalid_pcb_keepout_settings", "Keepout border style must be a defined native enum value.");
        ValidateDistance(zone.Border?.Pitch, "borderPitch");

        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (CustomProperty property in zone.CustomProperties)
        {
            if (string.IsNullOrWhiteSpace(property.Key) || !properties.Add(property.Key))
                throw new AutomationException("invalid_pcb_keepout_properties", "Keepout custom-property keys must be non-empty and unique.");
        }
    }

    private static void ValidateRing(PolyLine? ring, string role, ref int totalNodes)
    {
        if (ring is null || !ring.Closed || ring.Nodes.Count is < 3 or > MaxNodesPerRing)
            throw new AutomationException("invalid_pcb_keepout_outline", $"Each keepout {role} must be a closed point polygon with three to {MaxNodesPerRing} nodes.");
        totalNodes = checked(totalNodes + ring.Nodes.Count);
        if (totalNodes > MaxTotalNodes)
            throw new AutomationException("invalid_pcb_keepout_outline", $"The keepout outline exceeds the {MaxTotalNodes}-node bound.");

        var points = new List<Vector2>(ring.Nodes.Count);
        foreach (PolyLineNode node in ring.Nodes)
        {
            if (node.GeometryCase != PolyLineNode.GeometryOneofCase.Point || node.Point is null)
                throw new AutomationException("unsupported_pcb_keepout_outline", "Arc and empty outline nodes are unsupported; provide point-only polygon rings.");
            ValidateCoordinate(node.Point.XNm, "xNm");
            ValidateCoordinate(node.Point.YNm, "yNm");
            points.Add(node.Point);
        }
        for (int index = 1; index < points.Count; ++index)
        {
            if (points[index].XNm == points[index - 1].XNm && points[index].YNm == points[index - 1].YNm)
                throw new AutomationException("invalid_pcb_keepout_outline", "Adjacent keepout polygon points must be distinct.");
        }
        if (points[0].XNm == points[^1].XNm && points[0].YNm == points[^1].YNm)
            throw new AutomationException("invalid_pcb_keepout_outline", "Do not repeat the first point at the end of a closed keepout polygon.");

        decimal area = 0;
        for (int index = 0; index < points.Count; ++index)
        {
            Vector2 current = points[index];
            Vector2 next = points[(index + 1) % points.Count];
            area += (decimal) current.XNm * next.YNm - (decimal) next.XNm * current.YNm;
        }
        if (area == 0)
            throw new AutomationException("invalid_pcb_keepout_outline", $"Each keepout {role} polygon must enclose a non-zero area.");
    }

    private static void ValidateCoordinate(long value, string field)
    {
        if (value < int.MinValue || value > int.MaxValue)
            throw new AutomationException("invalid_pcb_keepout_coordinate", $"{field} exceeds KiCad's native 32-bit geometry range.");
    }

    private static void ValidateDistance(Distance? value, string field)
    {
        if (value is not null && (value.ValueNm < 0 || value.ValueNm > int.MaxValue))
            throw new AutomationException("invalid_pcb_keepout_distance", $"{field} exceeds KiCad's native non-negative 32-bit distance range.");
    }

    internal static void ValidateReadback(Zone expected, Zone actual, string? nativeIdentity = null)
    {
        ValidateCreate(expected);
        if (actual.Id is null || actual.Id.Value != expected.Id.Value || actual.Type != ZoneType.ZtRuleArea)
            throw new AutomationException("pcb_keepout_readback_mismatch", "Native readback returned a different keepout identity or type.");
        if (actual.Parent is not null && !string.IsNullOrWhiteSpace(actual.Parent.Value)
            && (string.IsNullOrWhiteSpace(nativeIdentity) || actual.Parent.Value != nativeIdentity))
            throw new AutomationException("pcb_keepout_readback_mismatch", "Native readback returned a keepout parented to a different board.");
        if (!expected.Layers.OrderBy(layer => (int) layer).SequenceEqual(actual.Layers.OrderBy(layer => (int) layer)))
            throw new AutomationException("pcb_keepout_readback_mismatch", "Native readback changed the keepout physical layers.");
        if (expected.Name != actual.Name)
            throw new AutomationException("pcb_keepout_readback_mismatch", "Native readback changed the keepout name.");
        if (!CanonicalPolySet(expected.Outline).SequenceEqual(CanonicalPolySet(actual.Outline), StringComparer.Ordinal))
            throw new AutomationException("pcb_keepout_readback_mismatch", "Native readback changed the keepout outline geometry.");

        RuleAreaSettings expectedSettings = expected.RuleAreaSettings!;
        RuleAreaSettings? actualSettings = actual.RuleAreaSettings;
        if (actualSettings is null
            || expectedSettings.KeepoutCopper != actualSettings.KeepoutCopper
            || expectedSettings.KeepoutVias != actualSettings.KeepoutVias
            || expectedSettings.KeepoutTracks != actualSettings.KeepoutTracks
            || expectedSettings.KeepoutPads != actualSettings.KeepoutPads
            || expectedSettings.KeepoutFootprints != actualSettings.KeepoutFootprints
            || actualSettings.PlacementEnabled
            || !string.IsNullOrWhiteSpace(actualSettings.PlacementSource))
            throw new AutomationException("pcb_keepout_readback_mismatch", "Native readback changed keepout restrictions or returned placement-owned rule-area state.");
        if (expected.Border is not null && (actual.Border is null || expected.Border.Style != actual.Border.Style
            || expected.Border.Pitch?.ValueNm != actual.Border.Pitch?.ValueNm))
            throw new AutomationException("pcb_keepout_readback_mismatch", "Native readback changed the keepout border settings.");
        // KiCad may derive filled polygons for a rule area during board
        // refresh. These fields are deliberately excluded from the authoring
        // contract; compare only the persisted outline/settings below.
        var expectedProperties = expected.CustomProperties.OrderBy(property => property.Key, StringComparer.Ordinal)
            .ThenBy(property => property.Value, StringComparer.Ordinal).Select(property => property.Key + "\0" + property.Value);
        var actualProperties = actual.CustomProperties.OrderBy(property => property.Key, StringComparer.Ordinal)
            .ThenBy(property => property.Value, StringComparer.Ordinal).Select(property => property.Key + "\0" + property.Value);
        if (!expectedProperties.SequenceEqual(actualProperties, StringComparer.Ordinal))
            throw new AutomationException("pcb_keepout_readback_mismatch", "Native readback changed keepout custom properties.");
    }

    private static IReadOnlyList<string> CanonicalPolySet(PolySet? polySet) => (polySet?.Polygons ?? [])
        .Select(polygon => CanonicalRing(polygon.Outline) + "|" + string.Join(";", polygon.Holes.Select(CanonicalRing).Order(StringComparer.Ordinal)))
        .Order(StringComparer.Ordinal).ToArray();

    private static string CanonicalRing(PolyLine? ring)
    {
        if (ring is null || !ring.Closed || ring.Nodes.Count < 3
            || ring.Nodes.Any(node => node.GeometryCase != PolyLineNode.GeometryOneofCase.Point || node.Point is null))
            throw new AutomationException("pcb_keepout_readback_mismatch", "Native readback returned an open or non-point keepout ring.");
        var points = ring.Nodes.Select(node => $"{node.Point.XNm},{node.Point.YNm}").ToArray();
        string forward = Rotate(points, MinimalRotation(points));
        string[] reversed = points.Reverse().ToArray();
        string backward = Rotate(reversed, MinimalRotation(reversed));
        return StringComparer.Ordinal.Compare(forward, backward) <= 0 ? forward : backward;
    }

    // Booth's algorithm keeps ring canonicalization linear in the number of vertices.
    private static int MinimalRotation(string[] values)
    {
        int length = values.Length, first = 0, second = 1, offset = 0;
        while (first < length && second < length && offset < length)
        {
            int comparison = StringComparer.Ordinal.Compare(values[(first + offset) % length], values[(second + offset) % length]);
            if (comparison == 0) { ++offset; continue; }
            if (comparison > 0)
            {
                first += offset + 1;
                if (first <= second) first = second + 1;
            }
            else
            {
                second += offset + 1;
                if (second <= first) second = first + 1;
            }
            offset = 0;
        }
        return Math.Min(first, second) % length;
    }

    private static string Rotate(string[] values, int start)
    {
        var rotated = new string[values.Length];
        for (int index = 0; index < values.Length; ++index)
            rotated[index] = values[(start + index) % values.Length];
        return string.Join(";", rotated);
    }
}
