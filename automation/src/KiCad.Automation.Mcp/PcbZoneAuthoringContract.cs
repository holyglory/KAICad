using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Admission checks for the bounded typed copper-zone authoring surface.
///
/// Zone fill polygons, parent links and arc outlines are native-owned details that
/// this contract deliberately does not accept as authoring input. Rejecting them
/// keeps a caller from accidentally writing a stale derived snapshot back to KiCad.
/// </summary>
internal static class PcbZoneAuthoringContract
{
    internal const int MaxPolygons = 256;
    internal const int MaxNodesPerRing = 4096;
    internal const int MaxTotalNodes = 8192;

    internal static void ValidateDocument(DocumentSpecifier? document)
    {
        DocumentStateTools.ValidateTarget(document);
        if (document!.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_zone_target", "Zone authoring requires an explicit PCB document.");
    }

    internal static void ValidateState(DocumentSpecifier document, DocumentLifecycleState? expected)
    {
        if (expected is null || expected.Scope != DocumentLifecycleScope.DlsPcb
            || expected.Document is null || !expected.Document.Equals(document)
            || !CanonicalUuid(expected.ProcessEpoch)
            || !CanonicalUuid(expected.NativeIdentity)
            || expected.Revision is null
            || !CanonicalUuid(expected.Revision.Epoch)
            || expected.Revision.Epoch != expected.NativeIdentity
            || expected.StateSha256.Length != 64 || !expected.StateSha256.All(char.IsAsciiHexDigitLower))
            throw new AutomationException("invalid_pcb_zone_state",
                "expectedStateJson must identify the same PCB with a complete native lifecycle revision and digest.");
    }

    internal static void ValidateOperationId(string operationId)
    {
        if (!CanonicalUuid(operationId))
            throw new AutomationException("invalid_pcb_zone_operation", "Zone authoring requires a canonical operation UUID.");
    }

    internal static void ValidateCreate(Zone zone) => ValidateZone(zone, requireIdentity: true,
        allowNativeParent: false, nativeIdentity: null);

    internal static void ValidateUpdate(Zone zone) => ValidateZone(zone, requireIdentity: true,
        allowNativeParent: false, nativeIdentity: null);

    /// <summary>
    /// Validates an existing native zone before an update or read. Native board
    /// ownership may populate Parent and Filled/FilledPolygons; those derived
    /// fields are accepted only when they identify this exact native board.
    /// </summary>
    internal static void ValidateExisting(Zone zone, string nativeIdentity) => ValidateZone(zone,
        requireIdentity: true, allowNativeParent: true, nativeIdentity, allowDerivedFill: true);

    internal static void ValidateDeleteId(string zoneId)
    {
        if (!CanonicalUuid(zoneId))
            throw new AutomationException("invalid_pcb_zone_identity", "Supply one canonical zone UUID.");
    }

    private static void ValidateZone(Zone? zone, bool requireIdentity, bool allowNativeParent,
        string? nativeIdentity, bool allowDerivedFill = false)
    {
        if (zone is null)
            throw new AutomationException("invalid_pcb_zone", "Supply one typed PCB zone.");
        if (requireIdentity && (zone.Id is null || !CanonicalUuid(zone.Id.Value)))
            throw new AutomationException("invalid_pcb_zone_identity", "The zone needs one canonical UUID.");
        if (zone.Type != ZoneType.ZtCopper)
            throw new AutomationException("unsupported_pcb_zone_type", "This bounded authoring surface accepts copper zones only; graphical, rule-area and teardrop zones are rejected.");
        if (zone.Layers.Count == 0 || zone.Layers.Count > 32)
            throw new AutomationException("invalid_pcb_zone_layers", "A copper zone needs one to 32 copper layers.");

        var layers = new HashSet<int>();
        foreach (BoardLayer layer in zone.Layers)
        {
            int value = (int)layer;
            if (value is < (int)BoardLayer.BlFCu or > (int)BoardLayer.BlBCu || !layers.Add(value))
                throw new AutomationException("invalid_pcb_zone_layers", "Zone layers must be distinct copper layers from F.Cu through B.Cu.");
        }

        if (zone.Outline is null || zone.Outline.Polygons.Count == 0 || zone.Outline.Polygons.Count > MaxPolygons)
            throw new AutomationException("invalid_pcb_zone_outline", "A copper zone needs one to 256 polygon outlines.");
        int totalNodes = 0;
        foreach (PolygonWithHoles polygon in zone.Outline.Polygons)
        {
            if (polygon is null)
                throw new AutomationException("invalid_pcb_zone_outline", "A zone polygon entry cannot be empty.");
            ValidateRing(polygon.Outline, "outline", ref totalNodes);
            foreach (PolyLine hole in polygon.Holes)
                ValidateRing(hole, "hole", ref totalNodes);
        }

        if (!allowDerivedFill && (zone.Filled || zone.FilledPolygons.Count != 0))
            throw new AutomationException("unsupported_pcb_zone_fill", "Filled polygons are native-derived state; omit them and refill the zone through the native operation.");
        if (zone.Parent is not null && !string.IsNullOrWhiteSpace(zone.Parent.Value))
        {
            if (!allowNativeParent)
                throw new AutomationException("unsupported_pcb_zone_parent", "A zone parent is native-owned and cannot be changed by this authoring surface.");
            if (!CanonicalUuid(nativeIdentity) || zone.Parent.Value != nativeIdentity)
                throw new AutomationException("unsupported_pcb_zone_parent", "The existing zone belongs to a different native board container.");
        }
        if (zone.CopperSettings is null)
            throw new AutomationException("invalid_pcb_zone_settings", "A copper zone needs CopperSettings.");
        if (zone.CopperSettings.Net is null)
            throw new AutomationException("invalid_pcb_zone_net", "CopperSettings must include a net object; use an empty name only for an intentional unconnected zone.");
        if (!Enum.IsDefined(zone.CopperSettings.FillMode) || zone.CopperSettings.FillMode == ZoneFillMode.ZfmUnknown)
            throw new AutomationException("invalid_pcb_zone_settings", "CopperSettings.FillMode must be explicit.");
        if (!Enum.IsDefined(zone.CopperSettings.Connection?.ZoneConnection ?? ZoneConnectionStyle.ZcsUnknown)
            || !Enum.IsDefined(zone.CopperSettings.IslandMode)
            || (zone.Border is not null && !Enum.IsDefined(zone.Border.Style)))
            throw new AutomationException("invalid_pcb_zone_settings", "Zone settings contain an unknown native enum value.");
        ValidateDistance(zone.CopperSettings.Clearance, "clearance");
        ValidateDistance(zone.CopperSettings.MinThickness, "minThickness");
        ValidateDistance(zone.Border?.Pitch, "borderPitch");
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (CustomProperty property in zone.CustomProperties)
        {
            if (string.IsNullOrWhiteSpace(property.Key) || !properties.Add(property.Key))
                throw new AutomationException("invalid_pcb_zone_properties", "Zone custom-property keys must be non-empty and unique.");
        }
    }

    private static void ValidateRing(PolyLine? ring, string role, ref int totalNodes)
    {
        if (ring is null || !ring.Closed || ring.Nodes.Count is < 3 or > MaxNodesPerRing)
            throw new AutomationException("invalid_pcb_zone_outline", $"Each zone {role} must be a closed point polygon with three to {MaxNodesPerRing} nodes.");
        totalNodes = checked(totalNodes + ring.Nodes.Count);
        if (totalNodes > MaxTotalNodes)
            throw new AutomationException("invalid_pcb_zone_outline", $"The zone outline exceeds the {MaxTotalNodes}-node bound.");

        var points = new List<Vector2>(ring.Nodes.Count);
        foreach (PolyLineNode node in ring.Nodes)
        {
            if (node.GeometryCase != PolyLineNode.GeometryOneofCase.Point || node.Point is null)
                throw new AutomationException("unsupported_pcb_zone_outline", "Arc and empty outline nodes are unsupported; provide point-only polygon rings.");
            ValidateCoordinate(node.Point.XNm, "xNm");
            ValidateCoordinate(node.Point.YNm, "yNm");
            points.Add(node.Point);
        }
        for (int index = 1; index < points.Count; ++index)
        {
            if (points[index].XNm == points[index - 1].XNm && points[index].YNm == points[index - 1].YNm)
                throw new AutomationException("invalid_pcb_zone_outline", "Adjacent zone polygon points must be distinct.");
        }
        if (points[0].XNm == points[^1].XNm && points[0].YNm == points[^1].YNm)
            throw new AutomationException("invalid_pcb_zone_outline", "Do not repeat the first point at the end of a closed zone polygon.");

        decimal area = 0;
        for (int index = 0; index < points.Count; ++index)
        {
            Vector2 current = points[index];
            Vector2 next = points[(index + 1) % points.Count];
            area += (decimal)current.XNm * next.YNm - (decimal)next.XNm * current.YNm;
        }
        if (area == 0)
            throw new AutomationException("invalid_pcb_zone_outline", $"Each zone {role} polygon must enclose a non-zero area.");
    }

    private static void ValidateCoordinate(long value, string field)
    {
        if (value < int.MinValue || value > int.MaxValue)
            throw new AutomationException("invalid_pcb_zone_coordinate", $"{field} exceeds KiCad's native 32-bit geometry range.");
    }

    private static void ValidateDistance(Distance? value, string field)
    {
        if (value is not null && (value.ValueNm < 0 || value.ValueNm > int.MaxValue))
            throw new AutomationException("invalid_pcb_zone_distance", $"{field} exceeds KiCad's native non-negative 32-bit distance range.");
    }

    private static bool CanonicalUuid(string? value) => !string.IsNullOrWhiteSpace(value)
        && Guid.TryParseExact(value, "D", out var id)
        && id != Guid.Empty
        && id.ToString("D") == value;

    internal static void ValidateReadback(Zone expected, Zone actual)
    {
        ValidateCreate(expected);
        if (actual.Id is null || actual.Id.Value != expected.Id.Value || actual.Type != ZoneType.ZtCopper)
            throw new AutomationException("pcb_zone_readback_mismatch", "Native readback returned a different zone identity or type.");
        if (!expected.Layers.OrderBy(layer => (int)layer).SequenceEqual(actual.Layers.OrderBy(layer => (int)layer)))
            throw new AutomationException("pcb_zone_readback_mismatch", "Native readback changed the zone copper layers.");
        if (expected.Name != actual.Name || expected.Priority != actual.Priority)
            throw new AutomationException("pcb_zone_readback_mismatch", "Native readback changed the zone name or priority.");
        if (!CanonicalPolySet(expected.Outline).SequenceEqual(CanonicalPolySet(actual.Outline), StringComparer.Ordinal))
            throw new AutomationException("pcb_zone_readback_mismatch", "Native readback changed the zone outline geometry.");
        var expectedSettings = expected.CopperSettings!;
        var actualSettings = actual.CopperSettings;
        if (actualSettings is null || expectedSettings.Net?.Name != actualSettings.Net?.Name
            || expectedSettings.FillMode != actualSettings.FillMode
            || expectedSettings.Connection.ZoneConnection != actualSettings.Connection.ZoneConnection
            || expectedSettings.IslandMode != actualSettings.IslandMode
            || expectedSettings.Clearance?.ValueNm != actualSettings.Clearance?.ValueNm
            || expectedSettings.MinThickness?.ValueNm != actualSettings.MinThickness?.ValueNm)
            throw new AutomationException("pcb_zone_readback_mismatch", "Native readback changed the zone copper settings.");
        if (expected.Border is not null && (actual.Border is null || expected.Border.Style != actual.Border.Style
            || expected.Border.Pitch?.ValueNm != actual.Border.Pitch?.ValueNm))
            throw new AutomationException("pcb_zone_readback_mismatch", "Native readback changed the zone border settings.");
        var expectedProperties = expected.CustomProperties.OrderBy(property => property.Key, StringComparer.Ordinal)
            .ThenBy(property => property.Value, StringComparer.Ordinal).Select(property => property.Key + "\0" + property.Value);
        var actualProperties = actual.CustomProperties.OrderBy(property => property.Key, StringComparer.Ordinal)
            .ThenBy(property => property.Value, StringComparer.Ordinal).Select(property => property.Key + "\0" + property.Value);
        if (!expectedProperties.SequenceEqual(actualProperties, StringComparer.Ordinal))
            throw new AutomationException("pcb_zone_readback_mismatch", "Native readback changed zone custom properties.");
    }

    private static IReadOnlyList<string> CanonicalPolySet(PolySet? polySet) => (polySet?.Polygons ?? [])
        .Select(polygon => CanonicalRing(polygon.Outline) + "|" + string.Join(";", polygon.Holes.Select(CanonicalRing).Order(StringComparer.Ordinal)))
        .Order(StringComparer.Ordinal).ToArray();

    private static string CanonicalRing(PolyLine? ring)
    {
        if (ring is null || !ring.Closed || ring.Nodes.Count < 3
            || ring.Nodes.Any(node => node.GeometryCase != PolyLineNode.GeometryOneofCase.Point || node.Point is null))
            throw new AutomationException("pcb_zone_readback_mismatch", "Native readback returned an open or non-point zone ring.");
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
