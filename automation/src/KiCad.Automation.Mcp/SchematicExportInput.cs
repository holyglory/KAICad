using System.Text.Json;
using System.Text.Json.Serialization;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using Kiapi.Schematic.Jobs;

namespace KiCad.Automation.Mcp;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicExportDxfInput : SchematicExportPlotInput
{
    internal RunSchematicJobExportDxf ToNative() => new()
    {
        JobSettings = ToJobSettings(),
        PlotSettings = PlotSettings?.ToNative() ?? new SchematicPlotSettings()
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicExportPdfInput : SchematicExportPlotInput
{
    private bool propertyPopups;
    private bool hierarchicalLinks;
    private bool includeMetadata;

    public bool PropertyPopups { get => propertyPopups; set => propertyPopups = BindOnce(value); }
    public bool HierarchicalLinks { get => hierarchicalLinks; set => hierarchicalLinks = BindOnce(value); }
    public bool IncludeMetadata { get => includeMetadata; set => includeMetadata = BindOnce(value); }

    internal RunSchematicJobExportPdf ToNative() => new()
    {
        JobSettings = ToJobSettings(),
        PlotSettings = PlotSettings?.ToNative() ?? new SchematicPlotSettings(),
        PropertyPopups = PropertyPopups,
        HierarchicalLinks = HierarchicalLinks,
        IncludeMetadata = IncludeMetadata
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicExportPsInput : SchematicExportPlotInput
{
    internal RunSchematicJobExportPs ToNative() => new()
    {
        JobSettings = ToJobSettings(),
        PlotSettings = PlotSettings?.ToNative() ?? new SchematicPlotSettings()
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicExportNetlistInput : SchematicExportInputBase
{
    private SchematicNetlistFormatInput format;
    private string? variantName;

    [JsonConverter(typeof(JsonStringEnumConverter<SchematicNetlistFormatInput>))]
    public SchematicNetlistFormatInput Format { get => format; set => format = BindOnce(value); }
    public string? VariantName { get => variantName; set => variantName = BindOnce(value); }

    internal RunSchematicJobExportNetlist ToNative()
    {
        SchematicNetlistFormat nativeFormat = (SchematicNetlistFormat)(int)Format;
        if (!Enum.IsDefined(nativeFormat))
            throw new AutomationException("invalid_schematic_export_format", "format is not a defined schematic netlist format.");
        return new RunSchematicJobExportNetlist
        {
            JobSettings = ToJobSettings(),
            Format = nativeFormat,
            VariantName = VariantName ?? ""
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicExportBomInput : SchematicExportInputBase
{
    private SchematicBomFormatInput? format;
    private SchematicBomFieldsInput? fields;
    private bool excludeDnp;
    private bool groupSymbols;
    private string? variantName;

    public SchematicBomFormatInput? Format { get => format; set => format = BindOnce(value); }
    public SchematicBomFieldsInput? Fields { get => fields; set => fields = BindOnce(value); }
    public bool ExcludeDnp { get => excludeDnp; set => excludeDnp = BindOnce(value); }
    public bool GroupSymbols { get => groupSymbols; set => groupSymbols = BindOnce(value); }
    public string? VariantName { get => variantName; set => variantName = BindOnce(value); }

    internal RunSchematicJobExportBOM ToNative() => new()
    {
        JobSettings = ToJobSettings(),
        Format = Format?.ToNative() ?? new BOMFormatSettings(),
        Fields = Fields?.ToNative() ?? new BOMFieldSettings(),
        ExcludeDnp = ExcludeDnp,
        GroupSymbols = GroupSymbols,
        VariantName = VariantName ?? ""
    };
}

/// <summary>Common explicit document and output path carried by netlist and BOM jobs.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract class SchematicExportInputBase : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private string? outputPath;

    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public string? OutputPath { get => outputPath; set => outputPath = BindOnce(value); }

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
public sealed class SchematicBomFormatInput : NativeDocumentInputObject
{
    private string? presetName;
    private string? fieldDelimiter;
    private string? stringDelimiter;
    private string? refDelimiter;
    private string? refRangeDelimiter;
    private bool keepTabs;
    private bool keepLineBreaks;
    private bool includeByteOrderMark;

    public string? PresetName { get => presetName; set => presetName = BindOnce(value); }
    public string? FieldDelimiter { get => fieldDelimiter; set => fieldDelimiter = BindOnce(value); }
    public string? StringDelimiter { get => stringDelimiter; set => stringDelimiter = BindOnce(value); }
    public string? RefDelimiter { get => refDelimiter; set => refDelimiter = BindOnce(value); }
    public string? RefRangeDelimiter { get => refRangeDelimiter; set => refRangeDelimiter = BindOnce(value); }
    public bool KeepTabs { get => keepTabs; set => keepTabs = BindOnce(value); }
    public bool KeepLineBreaks { get => keepLineBreaks; set => keepLineBreaks = BindOnce(value); }
    public bool IncludeByteOrderMark { get => includeByteOrderMark; set => includeByteOrderMark = BindOnce(value); }

    internal BOMFormatSettings ToNative() => new()
    {
        PresetName = PresetName ?? "",
        FieldDelimiter = FieldDelimiter ?? "",
        StringDelimiter = StringDelimiter ?? "",
        RefDelimiter = RefDelimiter ?? "",
        RefRangeDelimiter = RefRangeDelimiter ?? "",
        KeepTabs = KeepTabs,
        KeepLineBreaks = KeepLineBreaks,
        IncludeByteOrderMark = IncludeByteOrderMark
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicBomFieldInput : NativeDocumentInputObject
{
    private string? name;
    private string? label;
    private bool groupBy;

    public string? Name { get => name; set => name = BindOnce(value); }
    public string? Label { get => label; set => label = BindOnce(value); }
    public bool GroupBy { get => groupBy; set => groupBy = BindOnce(value); }

    internal BOMField ToNative() => new()
    {
        Name = Name ?? "",
        Label = Label ?? "",
        GroupBy = GroupBy
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicBomFieldsInput : NativeDocumentInputObject
{
    private string? presetName;
    private List<SchematicBomFieldInput?>? fields;
    private string? sortField;
    private SchematicBomSortDirectionInput sortDirection;
    private string? filter;
    private SchematicBomFilterScopeInput filterScope;

    public string? PresetName { get => presetName; set => presetName = BindOnce(value); }
    public List<SchematicBomFieldInput?>? Fields { get => fields; set => fields = BindOnce(value); }
    public string? SortField { get => sortField; set => sortField = BindOnce(value); }

    [JsonConverter(typeof(JsonStringEnumConverter<SchematicBomSortDirectionInput>))]
    public SchematicBomSortDirectionInput SortDirection { get => sortDirection; set => sortDirection = BindOnce(value); }

    public string? Filter { get => filter; set => filter = BindOnce(value); }

    [JsonConverter(typeof(JsonStringEnumConverter<SchematicBomFilterScopeInput>))]
    public SchematicBomFilterScopeInput FilterScope { get => filterScope; set => filterScope = BindOnce(value); }

    internal BOMFieldSettings ToNative()
    {
        BOMSortDirection nativeSort = (BOMSortDirection)(int)SortDirection;
        BOMFilterScope nativeScope = (BOMFilterScope)(int)FilterScope;
        if (!Enum.IsDefined(nativeSort) || !Enum.IsDefined(nativeScope))
            throw new AutomationException("invalid_schematic_export_fields", "BOM sortDirection and filterScope must be defined.");
        if (Fields is not null && Fields.Any(field => field is null))
            throw new AutomationException("invalid_schematic_export_fields", "BOM fields cannot contain null entries.");
        var result = new BOMFieldSettings
        {
            PresetName = PresetName ?? "",
            SortField = SortField ?? "",
            SortDirection = nativeSort,
            Filter = Filter ?? "",
            FilterScope = nativeScope
        };
        if (Fields is not null)
            result.Fields.Add(Fields.Select(field => field!.ToNative()));
        return result;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<SchematicNetlistFormatInput>))]
public enum SchematicNetlistFormatInput
{
    [JsonStringEnumMemberName("SNF_UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("SNF_KICAD_XML")] KicadXml = 1,
    [JsonStringEnumMemberName("SNF_KICAD_SEXPR")] KicadSexpr = 2,
    [JsonStringEnumMemberName("SNF_ORCAD_PCB2")] OrcadPcb2 = 3,
    [JsonStringEnumMemberName("SNF_CADSTAR")] Cadstar = 4,
    [JsonStringEnumMemberName("SNF_SPICE")] Spice = 5,
    [JsonStringEnumMemberName("SNF_SPICE_MODEL")] SpiceModel = 6,
    [JsonStringEnumMemberName("SNF_PADS")] Pads = 7,
    [JsonStringEnumMemberName("SNF_ALLEGRO")] Allegro = 8
}

[JsonConverter(typeof(JsonStringEnumConverter<SchematicBomSortDirectionInput>))]
public enum SchematicBomSortDirectionInput
{
    [JsonStringEnumMemberName("BSD_UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("BSD_ASCENDING")] Ascending = 1,
    [JsonStringEnumMemberName("BSD_DESCENDING")] Descending = 2
}

[JsonConverter(typeof(JsonStringEnumConverter<SchematicBomFilterScopeInput>))]
public enum SchematicBomFilterScopeInput
{
    [JsonStringEnumMemberName("BFS_REFERENCE")] Reference = 0,
    [JsonStringEnumMemberName("BFS_VISIBLE")] Visible = 1,
    [JsonStringEnumMemberName("BFS_ALL")] All = 2
}
