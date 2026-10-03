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
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch)
            || expected.Revision is null
            || !Guid.TryParseExact(expected.Revision.Epoch, "D", out _)
            || expected.StateSha256.Length != 64 || !expected.StateSha256.All(char.IsAsciiHexDigitLower))
            throw new AutomationException("invalid_pcb_zone_state",
                "expectedStateJson must identify the same PCB with a complete native lifecycle revision and digest.");
    }

    internal static void ValidateOperationId(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "D", out var value) || value == Guid.Empty)
            throw new AutomationException("invalid_pcb_zone_operation", "Zone authoring requires a canonical operation UUID.");
    }

    internal static void ValidateCreate(Zone zone) => ValidateZone(zone, requireIdentity: true);

    internal static void ValidateUpdate(Zone zone) => ValidateZone(zone, requireIdentity: true);

    internal static void ValidateDeleteId(string zoneId)
    {
        if (!Guid.TryParseExact(zoneId, "D", out var value) || value == Guid.Empty)
            throw new AutomationException("invalid_pcb_zone_identity", "Supply one canonical zone UUID.");
    }

    private static void ValidateZone(Zone? zone, bool requireIdentity)
    {
        if (zone is null)
            throw new AutomationException("invalid_pcb_zone", "Supply one typed PCB zone.");
        if (requireIdentity && (zone.Id is null || !Guid.TryParseExact(zone.Id.Value, "D", out var id) || id == Guid.Empty))
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

        if (zone.Filled || zone.FilledPolygons.Count != 0)
            throw new AutomationException("unsupported_pcb_zone_fill", "Filled polygons are native-derived state; omit them and refill the zone through the native operation.");
        if (zone.Parent is not null && !string.IsNullOrWhiteSpace(zone.Parent.Value))
            throw new AutomationException("unsupported_pcb_zone_parent", "A zone parent is native-owned and cannot be changed by this authoring surface.");
        if (zone.CopperSettings is null)
            throw new AutomationException("invalid_pcb_zone_settings", "A copper zone needs CopperSettings.");
        if (zone.CopperSettings.Net is null)
            throw new AutomationException("invalid_pcb_zone_net", "CopperSettings must include a net object; use an empty name only for an intentional unconnected zone.");
        if (zone.CopperSettings.FillMode == ZoneFillMode.ZfmUnknown)
            throw new AutomationException("invalid_pcb_zone_settings", "CopperSettings.FillMode must be explicit.");
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
}
