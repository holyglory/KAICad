using System.ComponentModel;
using System.Text.Json.Serialization;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

/// <summary>Task-oriented copper-zone input. Native-derived fields such as fill polygons and parent links are intentionally absent.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbZoneAuthoringInput
{
    [Description("Canonical zone UUID in D format.")]
    public string Id { get; set; } = "";
    [Description("Copper layer names, for example F.Cu or In1.Cu.")]
    public List<string> Layers { get; set; } = [];
    [Description("Closed point rings. The first ring is an outline; later rings in each polygon are holes.")]
    public List<PcbZonePolygonInput> Polygons { get; set; } = [];
    public string Name { get; set; } = "";
    public uint Priority { get; set; }
    [Description("Board net name. An empty name creates an intentionally unconnected copper zone.")]
    public string NetName { get; set; } = "";
    public long ClearanceNm { get; set; }
    public long MinThicknessNm { get; set; }
    [Description("Supported values are ZFM_SOLID or ZFM_HATCHED.")]
    public string FillMode { get; set; } = "ZFM_SOLID";
    public string Connection { get; set; } = "ZCS_INHERITED";
    public string IslandMode { get; set; } = "IRM_ALWAYS";
    public string BorderStyle { get; set; } = "ZBS_SOLID";
    public long BorderPitchNm { get; set; }

    internal Zone ToNative()
    {
        if (!Guid.TryParseExact(Id, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_pcb_zone_identity", "The zone needs one canonical UUID.");
        if (Layers is null || Layers.Count == 0)
            throw new AutomationException("invalid_pcb_zone_layers", "Supply at least one copper layer.");
        if (Polygons is null || Polygons.Count == 0)
            throw new AutomationException("invalid_pcb_zone_outline", "Supply at least one polygon outline.");

        var zone = new Zone
        {
            Id = new KIID { Value = Id }, Type = ZoneType.ZtCopper, Name = Name ?? "", Priority = Priority,
            Outline = new PolySet(),
            CopperSettings = new CopperZoneSettings
            {
                Net = new Net { Name = NetName ?? "" },
                Clearance = new Distance { ValueNm = ClearanceNm },
                MinThickness = new Distance { ValueNm = MinThicknessNm },
                FillMode = ParseEnum<ZoneFillMode>(FillMode, "fillMode"),
                Connection = new ZoneConnectionSettings { ZoneConnection = ParseEnum<ZoneConnectionStyle>(Connection, "connection") },
                IslandMode = ParseEnum<IslandRemovalMode>(IslandMode, "islandMode")
            },
            Border = new ZoneBorderSettings
            {
                Style = ParseEnum<ZoneBorderStyle>(BorderStyle, "borderStyle"),
                Pitch = new Distance { ValueNm = BorderPitchNm }
            }
        };
        foreach (string? layer in Layers)
        {
            if (string.IsNullOrWhiteSpace(layer))
                throw new AutomationException("invalid_pcb_zone_layers", "Zone layer names must be non-empty.");
            zone.Layers.Add(ParseLayer(layer!));
        }
        foreach (PcbZonePolygonInput polygon in Polygons)
        {
            if (polygon is null)
                throw new AutomationException("invalid_pcb_zone_outline", "Zone polygon entries must be objects.");
            var packed = new PolygonWithHoles { Outline = ToRing(polygon.Outline, "outline") };
            foreach (List<PcbZonePointInput> hole in polygon.Holes ?? [])
                packed.Holes.Add(ToRing(hole, "hole"));
            zone.Outline.Polygons.Add(packed);
        }
        return zone;
    }

    private static PolyLine ToRing(List<PcbZonePointInput> points, string role)
    {
        if (points is null)
            throw new AutomationException("invalid_pcb_zone_outline", $"A zone {role} ring cannot be null.");
        var ring = new PolyLine { Closed = true };
        foreach (PcbZonePointInput point in points)
            ring.Nodes.Add(new PolyLineNode { Point = point.ToNative() });
        return ring;
    }

    private static BoardLayer ParseLayer(string value)
    {
        string compact = string.Concat(value.Where(char.IsLetterOrDigit));
        string? name = Enum.GetNames<BoardLayer>().FirstOrDefault(candidate =>
            string.Concat(candidate.Where(char.IsLetterOrDigit)).Equals(compact, StringComparison.OrdinalIgnoreCase));
        if (name is null || !Enum.TryParse(name, out BoardLayer parsed)
            || (int)parsed < (int)BoardLayer.BlFCu || (int)parsed > (int)BoardLayer.BlBCu)
            throw new AutomationException("invalid_pcb_zone_layers", $"Unknown copper layer '{value}'.");
        return parsed;
    }

    private static T ParseEnum<T>(string value, string field) where T : struct, Enum
    {
        string compact = string.Concat((value ?? "").Where(char.IsLetterOrDigit));
        string? name = Enum.GetNames<T>().FirstOrDefault(candidate =>
            string.Concat(candidate.Where(char.IsLetterOrDigit)).Equals(compact, StringComparison.OrdinalIgnoreCase));
        return name is not null && Enum.TryParse(name, out T parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new AutomationException("invalid_pcb_zone_settings", $"{field} must be a defined native enum value.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbZonePolygonInput
{
    public List<PcbZonePointInput> Outline { get; set; } = [];
    public List<List<PcbZonePointInput>> Holes { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbZonePointInput
{
    public long XNm { get; set; }
    public long YNm { get; set; }

    internal Vector2 ToNative() => new() { XNm = XNm, YNm = YNm };
}
