using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kiapi.Common.Types;
using Kiapi.Schematic.Jobs;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Structured input for the schematic SVG job.  This is deliberately a DTO
/// rather than a protobuf message: repeated JSON members are writable and the
/// conversion below is the only boundary that creates the native request.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicExportSvgInput : SchematicExportPlotInput
{
    internal RunSchematicJobExportSvg ToNative()
    {
        var result = new RunSchematicJobExportSvg
        {
            JobSettings = ToJobSettings(),
            PlotSettings = PlotSettings?.ToNative() ?? new SchematicPlotSettings()
        };
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract class SchematicExportPlotInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private string? outputPath;
    private SchematicPlotSettingsInput? plotSettings;

    [Description("The explicit schematic root document to export.")]
    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }

    [Description("Absolute file or directory path selected for the schematic export job.")]
    public string? OutputPath { get => outputPath; set => outputPath = BindOnce(value); }

    public SchematicPlotSettingsInput? PlotSettings { get => plotSettings; set => plotSettings = BindOnce(value); }

    protected RunJobSettings ToJobSettings()
    {
        if (string.IsNullOrWhiteSpace(OutputPath) || !Path.IsPathFullyQualified(OutputPath))
            throw new AutomationException("invalid_schematic_export_path",
                "The schematic export output path must be absolute.");
        return new RunJobSettings
        {
            Document = NativeDocumentInput.ToNative(Document),
            OutputPath = OutputPath!
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicPlotSettingsInput : NativeDocumentInputObject
{
    private string? drawingSheet;
    private string? defaultFont;
    private string? variant;
    private bool plotAll;
    private bool plotDrawingSheet;
    private List<string?>? plotPages;
    private bool showHopOver;
    private bool blackAndWhite;
    private SchematicJobPageSizeInput pageSize;
    private bool useBackgroundColor;
    private int minPenWidth;
    private string? theme;

    public string? DrawingSheet { get => drawingSheet; set => drawingSheet = BindOnce(value); }
    public string? DefaultFont { get => defaultFont; set => defaultFont = BindOnce(value); }
    public string? Variant { get => variant; set => variant = BindOnce(value); }
    public bool PlotAll { get => plotAll; set => plotAll = BindOnce(value); }
    public bool PlotDrawingSheet { get => plotDrawingSheet; set => plotDrawingSheet = BindOnce(value); }
    public List<string?>? PlotPages { get => plotPages; set => plotPages = BindOnce(value); }
    public bool ShowHopOver { get => showHopOver; set => showHopOver = BindOnce(value); }
    public bool BlackAndWhite { get => blackAndWhite; set => blackAndWhite = BindOnce(value); }

    [JsonConverter(typeof(JsonStringEnumConverter<SchematicJobPageSizeInput>))]
    public SchematicJobPageSizeInput PageSize { get => pageSize; set => pageSize = BindOnce(value); }

    public bool UseBackgroundColor { get => useBackgroundColor; set => useBackgroundColor = BindOnce(value); }
    public int MinPenWidth { get => minPenWidth; set => minPenWidth = BindOnce(value); }
    public string? Theme { get => theme; set => theme = BindOnce(value); }

    internal SchematicPlotSettings ToNative()
    {
        if (MinPenWidth < 0)
            throw new AutomationException("invalid_schematic_export_settings", "minPenWidth cannot be negative.");
        if (PlotPages is not null && PlotPages.Any(page => page is null))
            throw new AutomationException("invalid_schematic_export_settings", "plotPages cannot contain null entries.");

        SchematicJobPageSize pageSize = (SchematicJobPageSize)(int)PageSize;
        if (!Enum.IsDefined(pageSize))
            throw new AutomationException("invalid_schematic_export_settings", "pageSize is not defined.");

        var result = new SchematicPlotSettings
        {
            DrawingSheet = DrawingSheet ?? "",
            DefaultFont = DefaultFont ?? "",
            Variant = Variant ?? "",
            PlotAll = PlotAll,
            PlotDrawingSheet = PlotDrawingSheet,
            ShowHopOver = ShowHopOver,
            BlackAndWhite = BlackAndWhite,
            PageSize = pageSize,
            UseBackgroundColor = UseBackgroundColor,
            MinPenWidth = MinPenWidth,
            Theme = Theme ?? ""
        };
        if (PlotPages is not null)
            result.PlotPages.Add(PlotPages.Select(page => page!));
        return result;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<SchematicJobPageSizeInput>))]
public enum SchematicJobPageSizeInput
{
    [JsonStringEnumMemberName("SJPS_UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("SJPS_AUTO")] Auto = 1,
    [JsonStringEnumMemberName("SJPS_A4")] A4 = 2,
    [JsonStringEnumMemberName("SJPS_A")] A = 3
}
