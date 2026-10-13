using System.ComponentModel;
using System.Text.Json.Serialization;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Project;
using Kiapi.Common.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

// These writable inputs describe the settings NETCLASS actually stores.  Its
// via defaults are circular diameters, not general-purpose padstack geometry.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProjectNetClassesInput : NativeDocumentInputObject
{
    private List<ProjectNetClassInput?>? netClasses;
    public List<ProjectNetClassInput?>? NetClasses { get => netClasses; set => netClasses = BindOnce(value); }

    internal static NetClassesResponse ToNative(ProjectNetClassesInput? input)
    {
        if (input?.NetClasses is null || input.NetClasses.Count > 4096)
            throw Invalid("Provide a netClasses array containing at most 4096 explicit netclasses; an empty array is allowed.");
        var result = new NetClassesResponse();
        foreach (ProjectNetClassInput? netClass in input.NetClasses)
            result.NetClasses.Add(ProjectNetClassInput.ToNative(netClass));
        if (result.NetClasses.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != result.NetClasses.Count)
            throw Invalid("Each requested netclass must have a distinct name.");
        return result;
    }

    internal static AutomationException Invalid(string message) => new("invalid_project_net_classes_request", message);

    internal static Distance? Distance(long? value, string field)
    {
        if (value is null) return null;
        if (value < 0 || value > int.MaxValue)
            throw Invalid(field + " must be a nonnegative distance in nanometers within the native coordinate range.");
        return new Distance { ValueNm = value.Value };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProjectNetClassInput : NativeDocumentInputObject
{
    private string? name;
    private int? priority;
    private ProjectNetClassBoardInput? board;
    private ProjectNetClassSchematicInput? schematic;
    private ProjectNetClassTypeInput type = ProjectNetClassTypeInput.Explicit;
    private List<string>? constituents;

    public string? Name { get => name; set => name = BindOnce(value); }
    public int? Priority { get => priority; set => priority = BindOnce(value); }
    public ProjectNetClassBoardInput? Board { get => board; set => board = BindOnce(value); }
    public ProjectNetClassSchematicInput? Schematic { get => schematic; set => schematic = BindOnce(value); }
    [Description("Only NCT_EXPLICIT netclasses can be authored. Effective composite netclasses are calculated by KiCad.")]
    public ProjectNetClassTypeInput Type { get => type; set => type = BindOnce(value); }
    [Description("Must be omitted or empty for explicitly authored netclasses.")]
    public List<string>? Constituents { get => constituents; set => constituents = BindOnce(value); }

    internal static NetClass ToNative(ProjectNetClassInput? input)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.Name) || input.Name.Contains('\0')
            || input.Type != ProjectNetClassTypeInput.Explicit || input.Constituents is { Count: > 0 })
            throw ProjectNetClassesInput.Invalid("Each netclass requires a nonempty name, type NCT_EXPLICIT and no constituents.");
        var result = new NetClass { Name = input.Name, Type = NetClassType.NctExplicit };
        if (input.Priority is { } priority) result.Priority = priority;
        if (input.Board is { } board) result.Board = ProjectNetClassBoardInput.ToNative(board);
        if (input.Schematic is { } schematic) result.Schematic = ProjectNetClassSchematicInput.ToNative(schematic);
        return result;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<ProjectNetClassTypeInput>))]
public enum ProjectNetClassTypeInput
{
    [JsonStringEnumMemberName("NCT_UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("NCT_EXPLICIT")] Explicit = 1,
    [JsonStringEnumMemberName("NCT_IMPLICIT")] Implicit = 2
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProjectNetClassBoardInput : NativeDocumentInputObject
{
    private long? clearanceNm;
    private long? trackWidthNm;
    private long? diffPairTrackWidthNm;
    private long? diffPairGapNm;
    private long? diffPairViaGapNm;
    private ProjectNetClassViaInput? viaStack;
    private ProjectNetClassViaInput? microviaStack;
    private ProjectNetClassColorInput? color;
    private string? tuningProfile;

    [Description("Copper clearance in nanometers. Omit to inherit; zero is an explicit value.")]
    public long? ClearanceNm { get => clearanceNm; set => clearanceNm = BindOnce(value); }
    public long? TrackWidthNm { get => trackWidthNm; set => trackWidthNm = BindOnce(value); }
    public long? DiffPairTrackWidthNm { get => diffPairTrackWidthNm; set => diffPairTrackWidthNm = BindOnce(value); }
    public long? DiffPairGapNm { get => diffPairGapNm; set => diffPairGapNm = BindOnce(value); }
    public long? DiffPairViaGapNm { get => diffPairViaGapNm; set => diffPairViaGapNm = BindOnce(value); }
    public ProjectNetClassViaInput? ViaStack { get => viaStack; set => viaStack = BindOnce(value); }
    public ProjectNetClassViaInput? MicroviaStack { get => microviaStack; set => microviaStack = BindOnce(value); }
    public ProjectNetClassColorInput? Color { get => color; set => color = BindOnce(value); }
    public string? TuningProfile { get => tuningProfile; set => tuningProfile = BindOnce(value); }

    internal static NetClassBoardSettings ToNative(ProjectNetClassBoardInput input)
    {
        var result = new NetClassBoardSettings
        {
            Clearance = ProjectNetClassesInput.Distance(input.ClearanceNm, "clearanceNm"),
            TrackWidth = ProjectNetClassesInput.Distance(input.TrackWidthNm, "trackWidthNm"),
            DiffPairTrackWidth = ProjectNetClassesInput.Distance(input.DiffPairTrackWidthNm, "diffPairTrackWidthNm"),
            DiffPairGap = ProjectNetClassesInput.Distance(input.DiffPairGapNm, "diffPairGapNm"),
            DiffPairViaGap = ProjectNetClassesInput.Distance(input.DiffPairViaGapNm, "diffPairViaGapNm")
        };
        if (input.ViaStack is { } via) result.ViaStack = ProjectNetClassViaInput.ToNative(via);
        if (input.MicroviaStack is { } microvia) result.MicroviaStack = ProjectNetClassViaInput.ToNative(microvia);
        if (input.Color is { } color) result.Color = ProjectNetClassColorInput.ToNative(color);
        if (input.TuningProfile is { } profile)
        {
            if (profile.Contains('\0')) throw ProjectNetClassesInput.Invalid("tuningProfile cannot contain a NUL character.");
            result.TuningProfile = profile;
        }
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProjectNetClassViaInput : NativeDocumentInputObject
{
    private long? diameterNm;
    private long? drillDiameterNm;
    [Description("Circular copper diameter on all layers, in nanometers. Omit to inherit.")]
    public long? DiameterNm { get => diameterNm; set => diameterNm = BindOnce(value); }
    [Description("Circular drilled-hole diameter, in nanometers. Omit to inherit.")]
    public long? DrillDiameterNm { get => drillDiameterNm; set => drillDiameterNm = BindOnce(value); }

    internal static PadStack ToNative(ProjectNetClassViaInput input)
    {
        ProjectNetClassesInput.Distance(input.DiameterNm, "diameterNm");
        ProjectNetClassesInput.Distance(input.DrillDiameterNm, "drillDiameterNm");
        if (input.DiameterNm is { } diameter && input.DrillDiameterNm is { } drill && drill > diameter)
            throw ProjectNetClassesInput.Invalid("The via drill diameter cannot exceed its copper diameter.");
        var result = new PadStack();
        if (input.DiameterNm is { } size)
            result.CopperLayers.Add(new PadStackLayer
            {
                Layer = BoardLayer.BlFCu, Shape = PadStackShape.PssCircle,
                Size = new Vector2 { XNm = size, YNm = size }
            });
        if (input.DrillDiameterNm is { } hole)
            result.Drill = new DrillProperties { Diameter = new Vector2 { XNm = hole, YNm = hole } };
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProjectNetClassSchematicInput : NativeDocumentInputObject
{
    private long? wireWidthNm;
    private long? busWidthNm;
    private ProjectNetClassColorInput? color;
    private ProjectNetClassLineStyleInput? lineStyle;
    public long? WireWidthNm { get => wireWidthNm; set => wireWidthNm = BindOnce(value); }
    public long? BusWidthNm { get => busWidthNm; set => busWidthNm = BindOnce(value); }
    public ProjectNetClassColorInput? Color { get => color; set => color = BindOnce(value); }
    public ProjectNetClassLineStyleInput? LineStyle { get => lineStyle; set => lineStyle = BindOnce(value); }

    internal static NetClassSchematicSettings ToNative(ProjectNetClassSchematicInput input)
    {
        var result = new NetClassSchematicSettings
        {
            WireWidth = ProjectNetClassesInput.Distance(input.WireWidthNm, "wireWidthNm"),
            BusWidth = ProjectNetClassesInput.Distance(input.BusWidthNm, "busWidthNm")
        };
        if (input.Color is { } color) result.Color = ProjectNetClassColorInput.ToNative(color);
        if (input.LineStyle is { } style)
        {
            if (!Enum.IsDefined(style) || style == ProjectNetClassLineStyleInput.Unknown)
                throw ProjectNetClassesInput.Invalid("A supplied lineStyle must be a supported native stroke style.");
            result.LineStyle = (StrokeLineStyle)style;
        }
        return result;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<ProjectNetClassLineStyleInput>))]
public enum ProjectNetClassLineStyleInput
{
    [JsonStringEnumMemberName("SLS_UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("SLS_DEFAULT")] Default = 1,
    [JsonStringEnumMemberName("SLS_SOLID")] Solid = 2,
    [JsonStringEnumMemberName("SLS_DASH")] Dash = 3,
    [JsonStringEnumMemberName("SLS_DOT")] Dot = 4,
    [JsonStringEnumMemberName("SLS_DASHDOT")] DashDot = 5,
    [JsonStringEnumMemberName("SLS_DASHDOTDOT")] DashDotDot = 6
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProjectNetClassColorInput : NativeDocumentInputObject
{
    private double r, g, b, a;
    public double R { get => r; set => r = BindOnce(value); }
    public double G { get => g; set => g = BindOnce(value); }
    public double B { get => b; set => b = BindOnce(value); }
    public double A { get => a; set => a = BindOnce(value); }

    internal static Color ToNative(ProjectNetClassColorInput input)
    {
        if (new[] { input.R, input.G, input.B, input.A }.Any(value => !double.IsFinite(value) || value < 0 || value > 1))
            throw ProjectNetClassesInput.Invalid("Netclass color channels must be finite values between zero and one.");
        return new Color { R = input.R, G = input.G, B = input.B, A = input.A };
    }
}
