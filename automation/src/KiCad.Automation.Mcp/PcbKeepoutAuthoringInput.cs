using System.ComponentModel;
using System.Text.Json.Serialization;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Task-oriented rule-area (keepout) input. Filled polygons, parent links and
/// placement ownership are native-derived and intentionally absent.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbKeepoutAuthoringInput
{
    [Description("Canonical keepout UUID in D format.")]
    public string Id { get; set; } = "";

    [Description("Physical board layer names, for example F.Cu, F.Mask, Edge.Cuts or User.Drawings.")]
    public List<string> Layers { get; set; } = [];

    [Description("Closed point rings. The first ring is an outline; later rings in each polygon are holes.")]
    public List<PcbKeepoutPolygonInput> Polygons { get; set; } = [];

    public string Name { get; set; } = "";

    [Description("Disallow copper zone fills inside the keepout.")]
    public bool KeepoutCopper { get; set; }

    [Description("Disallow vias inside the keepout.")]
    public bool KeepoutVias { get; set; }

    [Description("Disallow tracks inside the keepout.")]
    public bool KeepoutTracks { get; set; }

    [Description("Disallow pads inside the keepout.")]
    public bool KeepoutPads { get; set; }

    [Description("Disallow footprints inside the keepout.")]
    public bool KeepoutFootprints { get; set; }

    public string BorderStyle { get; set; } = "ZBS_SOLID";
    public long BorderPitchNm { get; set; }

    internal Zone ToNative()
    {
        if (!PcbKeepoutAuthoringContract.CanonicalUuid(Id))
            throw new AutomationException("invalid_pcb_keepout_identity", "The keepout needs one canonical UUID.");
        if (Layers is null || Layers.Count == 0)
            throw new AutomationException("invalid_pcb_keepout_layers", "Supply at least one physical board layer.");
        if (Polygons is null || Polygons.Count == 0)
            throw new AutomationException("invalid_pcb_keepout_outline", "Supply at least one polygon outline.");

        var keepout = new Zone
        {
            Id = new KIID { Value = Id },
            Type = ZoneType.ZtRuleArea,
            Name = Name ?? "",
            Outline = new PolySet(),
            RuleAreaSettings = new RuleAreaSettings
            {
                KeepoutCopper = KeepoutCopper,
                KeepoutVias = KeepoutVias,
                KeepoutTracks = KeepoutTracks,
                KeepoutPads = KeepoutPads,
                KeepoutFootprints = KeepoutFootprints,
                PlacementEnabled = false,
                PlacementSource = "",
                PlacementSourceType = PlacementRuleSourceType.PrstUnknown
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
                throw new AutomationException("invalid_pcb_keepout_layers", "Keepout layer names must be non-empty.");
            keepout.Layers.Add(ParseLayer(layer!));
        }

        foreach (PcbKeepoutPolygonInput polygon in Polygons)
        {
            if (polygon is null)
                throw new AutomationException("invalid_pcb_keepout_outline", "Keepout polygon entries must be objects.");
            var packed = new PolygonWithHoles { Outline = ToRing(polygon.Outline, "outline") };
            foreach (List<PcbKeepoutPointInput> hole in polygon.Holes ?? [])
                packed.Holes.Add(ToRing(hole, "hole"));
            keepout.Outline.Polygons.Add(packed);
        }

        return keepout;
    }

    private static PolyLine ToRing(List<PcbKeepoutPointInput> points, string role)
    {
        if (points is null)
            throw new AutomationException("invalid_pcb_keepout_outline", $"A keepout {role} ring cannot be null.");
        var ring = new PolyLine { Closed = true };
        foreach (PcbKeepoutPointInput point in points)
        {
            if (point is null)
                throw new AutomationException("invalid_pcb_keepout_outline", $"A keepout {role} point cannot be null.");
            ring.Nodes.Add(new PolyLineNode { Point = point.ToNative() });
        }
        return ring;
    }

    private static BoardLayer ParseLayer(string value)
    {
        string compact = string.Concat(value.Where(char.IsLetterOrDigit));
        string canonical = compact switch
        {
            "UserDrawings" => "DwgsUser",
            "UserComments" => "CmtsUser",
            "UserEco1" => "Eco1User",
            "UserEco2" => "Eco2User",
            "FCourtyard" => "FCrtYd",
            "BCourtyard" => "BCrtYd",
            _ => compact
        };
        string? name = Enum.GetNames<BoardLayer>().FirstOrDefault(candidate =>
        {
            string candidateCompact = string.Concat(candidate.Where(char.IsLetterOrDigit));
            return candidateCompact.Equals(canonical, StringComparison.OrdinalIgnoreCase)
                || (candidateCompact.StartsWith("Bl", StringComparison.Ordinal)
                    && candidateCompact[2..].Equals(canonical, StringComparison.OrdinalIgnoreCase));
        });

        if (name is null || !Enum.TryParse(name, out BoardLayer parsed)
            || (int) parsed < (int) BoardLayer.BlFCu || (int) parsed > (int) BoardLayer.BlUser45)
            throw new AutomationException("invalid_pcb_keepout_layers", $"Unknown physical board layer '{value}'.");
        return parsed;
    }

    private static T ParseEnum<T>(string value, string field) where T : struct, Enum
    {
        string compact = string.Concat((value ?? "").Where(char.IsLetterOrDigit));
        string? name = Enum.GetNames<T>().FirstOrDefault(candidate =>
            string.Concat(candidate.Where(char.IsLetterOrDigit)).Equals(compact, StringComparison.OrdinalIgnoreCase));
        return name is not null && Enum.TryParse(name, out T parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new AutomationException("invalid_pcb_keepout_settings", $"{field} must be a defined native enum value.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbKeepoutPolygonInput
{
    public List<PcbKeepoutPointInput> Outline { get; set; } = [];
    public List<List<PcbKeepoutPointInput>> Holes { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbKeepoutPointInput
{
    public long XNm { get; set; }
    public long YNm { get; set; }

    internal Vector2 ToNative() => new() { XNm = XNm, YNm = YNm };
}
