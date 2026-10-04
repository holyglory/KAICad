using System.Text.Json;
using System.Text.Json.Serialization;
using Kiapi.Board.Jobs;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

internal static class PcbExportDtoValidation
{
    public static void Enum<T>(T value, string name) where T : struct, System.Enum
    {
        if (!System.Enum.IsDefined(value))
            throw new AutomationException("invalid_pcb_export_enum", $"{name} is not a defined value.");
    }

    public static BoardLayer Layer(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new AutomationException("invalid_pcb_export_enum", $"{name} contains an empty board layer.");

        string compact = string.Concat(value.Where(char.IsLetterOrDigit));
        string enumName = compact.StartsWith("BL", StringComparison.OrdinalIgnoreCase)
            ? "Bl" + compact[2..]
            : "Bl" + compact;
        if (!System.Enum.TryParse(enumName, ignoreCase: true, out BoardLayer layer)
            || !System.Enum.IsDefined(layer))
            throw new AutomationException("invalid_pcb_export_enum", $"{name} contains unknown board layer '{value}'.");
        return layer;
    }
}

/// <summary>
/// The public board protobuf uses names such as <c>BL_F_Cu</c>, while the
/// generated CLR enum is named <c>BlFCu</c>. MCP clients commonly send the
/// protobuf spelling, so accept that spelling alongside KiCad names and the
/// existing numeric enum representation without weakening enum validation.
/// </summary>
internal sealed class BoardLayerListJsonConverter : JsonConverter<List<BoardLayer>>
{
    public override List<BoardLayer> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Board layer selections must be an array.");

        var result = new List<BoardLayer>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return result;

            BoardLayer layer;
            if (reader.TokenType == JsonTokenType.String)
            {
                try { layer = PcbExportDtoValidation.Layer(reader.GetString()!, "Board layer selection"); }
                catch (AutomationException error) { throw new JsonException(error.Message, error); }
            }
            else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
            {
                layer = (BoardLayer)value;
                if (!System.Enum.IsDefined(layer))
                    throw new JsonException($"Board layer enum value {value} is not defined.");
            }
            else
            {
                throw new JsonException("Board layer selections must contain a protobuf name or defined enum value.");
            }

            result.Add(layer);
        }

        throw new JsonException("Board layer selections must be a complete array.");
    }

    public override void Write(Utf8JsonWriter writer, List<BoardLayer> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (BoardLayer layer in value)
            writer.WriteNumberValue((int)layer);
        writer.WriteEndArray();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExport3DInput
{
    public string Variant { get; set; } = "";
    public Board3DFormat Format { get; set; }
    public string NetFilter { get; set; } = "";
    public string ComponentFilter { get; set; } = "";
    public bool HasUserOrigin { get; set; }
    public Vector2Input? Origin { get; set; } = null;
    public bool Overwrite { get; set; }
    public bool UseGridOrigin { get; set; }
    public bool UseDrillOrigin { get; set; }
    public bool UseDefinedOrigin { get; set; }
    public bool UsePcbCenterOrigin { get; set; }
    public bool IncludeUnspecified { get; set; }
    public bool IncludeDnp { get; set; }
    public bool SubstituteModels { get; set; }
    public double BoardOutlinesChainingEpsilon { get; set; }
    public bool BoardOnly { get; set; }
    public bool CutViasInBody { get; set; }
    public bool ExportBoardBody { get; set; }
    public bool ExportComponents { get; set; }
    public bool ExportTracksAndVias { get; set; }
    public bool ExportPads { get; set; }
    public bool ExportZones { get; set; }
    public bool ExportInnerCopper { get; set; }
    public bool ExportSilkscreen { get; set; }
    public bool ExportSoldermask { get; set; }
    public bool FuseShapes { get; set; }
    public bool FillAllVias { get; set; }
    public bool OptimizeStep { get; set; }
    public bool ExtraPadThickness { get; set; }
    public Units VrmlUnits { get; set; }
    public string VrmlModelDir { get; set; } = "";
    public bool VrmlRelativePaths { get; set; }

    internal RunBoardJobExport3D ToNative()
    {
        var result = new RunBoardJobExport3D();
        result.Variant = Variant;
        PcbExportDtoValidation.Enum(Format, nameof(Format));
        result.Format = Format;
        result.NetFilter = NetFilter;
        result.ComponentFilter = ComponentFilter;
        result.HasUserOrigin = HasUserOrigin;
        if (Origin is not null) result.Origin = Origin.ToNative();
        result.Overwrite = Overwrite;
        result.UseGridOrigin = UseGridOrigin;
        result.UseDrillOrigin = UseDrillOrigin;
        result.UseDefinedOrigin = UseDefinedOrigin;
        result.UsePcbCenterOrigin = UsePcbCenterOrigin;
        result.IncludeUnspecified = IncludeUnspecified;
        result.IncludeDnp = IncludeDnp;
        result.SubstituteModels = SubstituteModels;
        result.BoardOutlinesChainingEpsilon = BoardOutlinesChainingEpsilon;
        result.BoardOnly = BoardOnly;
        result.CutViasInBody = CutViasInBody;
        result.ExportBoardBody = ExportBoardBody;
        result.ExportComponents = ExportComponents;
        result.ExportTracksAndVias = ExportTracksAndVias;
        result.ExportPads = ExportPads;
        result.ExportZones = ExportZones;
        result.ExportInnerCopper = ExportInnerCopper;
        result.ExportSilkscreen = ExportSilkscreen;
        result.ExportSoldermask = ExportSoldermask;
        result.FuseShapes = FuseShapes;
        result.FillAllVias = FillAllVias;
        result.OptimizeStep = OptimizeStep;
        result.ExtraPadThickness = ExtraPadThickness;
        PcbExportDtoValidation.Enum(VrmlUnits, nameof(VrmlUnits));
        result.VrmlUnits = VrmlUnits;
        result.VrmlModelDir = VrmlModelDir;
        result.VrmlRelativePaths = VrmlRelativePaths;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportRenderInput
{
    public RenderFormat Format { get; set; }
    public RenderQuality Quality { get; set; }
    public RenderBackgroundStyle BackgroundStyle { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string AppearancePreset { get; set; } = "";
    public bool UseBoardStackupColors { get; set; }
    public RenderSide Side { get; set; }
    public double Zoom { get; set; }
    public bool Perspective { get; set; }
    public Vector3DInput? Rotation { get; set; } = null;
    public Vector3DInput? Pan { get; set; } = null;
    public Vector3DInput? Pivot { get; set; } = null;
    public bool ProceduralTextures { get; set; }
    public bool Floor { get; set; }
    public bool AntiAlias { get; set; }
    public bool PostProcess { get; set; }
    public Vector3DInput? LightTopIntensity { get; set; } = null;
    public Vector3DInput? LightBottomIntensity { get; set; } = null;
    public Vector3DInput? LightCameraIntensity { get; set; } = null;
    public Vector3DInput? LightSideIntensity { get; set; } = null;
    public int LightSideElevation { get; set; }

    internal RunBoardJobExportRender ToNative()
    {
        var result = new RunBoardJobExportRender();
        PcbExportDtoValidation.Enum(Format, nameof(Format));
        result.Format = Format;
        PcbExportDtoValidation.Enum(Quality, nameof(Quality));
        result.Quality = Quality;
        PcbExportDtoValidation.Enum(BackgroundStyle, nameof(BackgroundStyle));
        result.BackgroundStyle = BackgroundStyle;
        result.Width = Width;
        result.Height = Height;
        result.AppearancePreset = AppearancePreset;
        result.UseBoardStackupColors = UseBoardStackupColors;
        PcbExportDtoValidation.Enum(Side, nameof(Side));
        result.Side = Side;
        result.Zoom = Zoom;
        result.Perspective = Perspective;
        if (Rotation is not null) result.Rotation = Rotation.ToNative();
        if (Pan is not null) result.Pan = Pan.ToNative();
        if (Pivot is not null) result.Pivot = Pivot.ToNative();
        result.ProceduralTextures = ProceduralTextures;
        result.Floor = Floor;
        result.AntiAlias = AntiAlias;
        result.PostProcess = PostProcess;
        if (LightTopIntensity is not null) result.LightTopIntensity = LightTopIntensity.ToNative();
        if (LightBottomIntensity is not null) result.LightBottomIntensity = LightBottomIntensity.ToNative();
        if (LightCameraIntensity is not null) result.LightCameraIntensity = LightCameraIntensity.ToNative();
        if (LightSideIntensity is not null) result.LightSideIntensity = LightSideIntensity.ToNative();
        result.LightSideElevation = LightSideElevation;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportSvgInput
{
    public BoardPlotSettingsInput? PlotSettings { get; set; } = null;
    public bool FitPageToBoard { get; set; }
    public uint Precision { get; set; }
    public BoardJobPaginationMode PageMode { get; set; }

    internal RunBoardJobExportSvg ToNative()
    {
        var result = new RunBoardJobExportSvg();
        if (PlotSettings is not null) result.PlotSettings = PlotSettings.ToNative();
        result.FitPageToBoard = FitPageToBoard;
        result.Precision = Precision;
        PcbExportDtoValidation.Enum(PageMode, nameof(PageMode));
        result.PageMode = PageMode;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportDxfInput
{
    public BoardPlotSettingsInput? PlotSettings { get; set; } = null;
    public bool PlotGraphicItemsUsingContours { get; set; }
    public bool PolygonMode { get; set; }
    public Units Units { get; set; }
    public BoardJobPaginationMode PageMode { get; set; }

    internal RunBoardJobExportDxf ToNative()
    {
        var result = new RunBoardJobExportDxf();
        if (PlotSettings is not null) result.PlotSettings = PlotSettings.ToNative();
        result.PlotGraphicItemsUsingContours = PlotGraphicItemsUsingContours;
        result.PolygonMode = PolygonMode;
        PcbExportDtoValidation.Enum(Units, nameof(Units));
        result.Units = Units;
        PcbExportDtoValidation.Enum(PageMode, nameof(PageMode));
        result.PageMode = PageMode;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportPdfInput
{
    public BoardPlotSettingsInput? PlotSettings { get; set; } = null;
    public bool FrontFootprintPropertyPopups { get; set; }
    public bool BackFootprintPropertyPopups { get; set; }
    public bool IncludeMetadata { get; set; }
    public bool SingleDocument { get; set; }
    public BoardJobPaginationMode PageMode { get; set; }
    public string BackgroundColor { get; set; } = "";

    internal RunBoardJobExportPdf ToNative()
    {
        var result = new RunBoardJobExportPdf();
        if (PlotSettings is not null) result.PlotSettings = PlotSettings.ToNative();
        result.FrontFootprintPropertyPopups = FrontFootprintPropertyPopups;
        result.BackFootprintPropertyPopups = BackFootprintPropertyPopups;
        result.IncludeMetadata = IncludeMetadata;
        result.SingleDocument = SingleDocument;
        PcbExportDtoValidation.Enum(PageMode, nameof(PageMode));
        result.PageMode = PageMode;
        result.BackgroundColor = BackgroundColor;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportPsInput
{
    public BoardPlotSettingsInput? PlotSettings { get; set; } = null;
    public BoardJobPaginationMode PageMode { get; set; }
    public double TrackWidthCorrection { get; set; }
    public double XScaleAdjust { get; set; }
    public double YScaleAdjust { get; set; }
    public bool ForceA4 { get; set; }
    public bool UseGlobalSettings { get; set; }

    internal RunBoardJobExportPs ToNative()
    {
        var result = new RunBoardJobExportPs();
        if (PlotSettings is not null) result.PlotSettings = PlotSettings.ToNative();
        PcbExportDtoValidation.Enum(PageMode, nameof(PageMode));
        result.PageMode = PageMode;
        result.TrackWidthCorrection = TrackWidthCorrection;
        result.XScaleAdjust = XScaleAdjust;
        result.YScaleAdjust = YScaleAdjust;
        result.ForceA4 = ForceA4;
        result.UseGlobalSettings = UseGlobalSettings;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportGerbersInput
{
    public BoardPlotSettingsInput? PlotSettings { get; set; } = null;
    public bool UseBoardPlotParams { get; set; }
    public bool CreateGerberJobFile { get; set; }
    public bool IncludeNetlistAttributes { get; set; }
    public bool UseX2Format { get; set; }
    public bool DisableApertureMacros { get; set; }
    public bool UseProtelFileExtensions { get; set; }
    public GerberPrecision Precision { get; set; }

    internal RunBoardJobExportGerbers ToNative()
    {
        var result = new RunBoardJobExportGerbers();
        if (PlotSettings is not null) result.PlotSettings = PlotSettings.ToNative();
        result.UseBoardPlotParams = UseBoardPlotParams;
        result.CreateGerberJobFile = CreateGerberJobFile;
        result.IncludeNetlistAttributes = IncludeNetlistAttributes;
        result.UseX2Format = UseX2Format;
        result.DisableApertureMacros = DisableApertureMacros;
        result.UseProtelFileExtensions = UseProtelFileExtensions;
        PcbExportDtoValidation.Enum(Precision, nameof(Precision));
        result.Precision = Precision;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportDrillInput
{
    public DrillFormat Format { get; set; }
    public Units Units { get; set; }
    public DrillOrigin Origin { get; set; }
    public DrillZerosFormat ZerosFormat { get; set; }
    public ExcellonFormatOptionsInput? Excellon { get; set; } = null;
    public DrillMapFormat MapFormat { get; set; }
    public DrillGerberPrecision GerberPrecision { get; set; }
    public bool? GerberGenerateTenting { get; set; } = null;
    public DrillReportFormat ReportFormat { get; set; }
    public string? ReportFilename { get; set; } = null;

    internal RunBoardJobExportDrill ToNative()
    {
        var result = new RunBoardJobExportDrill();
        PcbExportDtoValidation.Enum(Format, nameof(Format));
        result.Format = Format;
        PcbExportDtoValidation.Enum(Units, nameof(Units));
        result.Units = Units;
        PcbExportDtoValidation.Enum(Origin, nameof(Origin));
        result.Origin = Origin;
        PcbExportDtoValidation.Enum(ZerosFormat, nameof(ZerosFormat));
        result.ZerosFormat = ZerosFormat;
        if (Excellon is not null) result.Excellon = Excellon.ToNative();
        PcbExportDtoValidation.Enum(MapFormat, nameof(MapFormat));
        result.MapFormat = MapFormat;
        PcbExportDtoValidation.Enum(GerberPrecision, nameof(GerberPrecision));
        result.GerberPrecision = GerberPrecision;
        if (GerberGenerateTenting.HasValue) result.GerberGenerateTenting = GerberGenerateTenting.Value;
        PcbExportDtoValidation.Enum(ReportFormat, nameof(ReportFormat));
        result.ReportFormat = ReportFormat;
        if (ReportFilename is not null) result.ReportFilename = ReportFilename;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportPositionInput
{
    public bool? UseDrillPlaceFileOrigin { get; set; } = null;
    public bool SmdOnly { get; set; }
    public bool ExcludeFootprintsWithTh { get; set; }
    public bool ExcludeDnp { get; set; }
    public bool ExcludeFromBom { get; set; }
    public bool NegateBottomX { get; set; }
    public bool SingleFile { get; set; }
    public bool NakedFilename { get; set; }
    public PositionSide Side { get; set; }
    public Units Units { get; set; }
    public PositionFormat Format { get; set; }
    public bool? IncludeBoardEdgeForGerber { get; set; } = null;
    public string Variant { get; set; } = "";

    internal RunBoardJobExportPosition ToNative()
    {
        var result = new RunBoardJobExportPosition();
        if (UseDrillPlaceFileOrigin.HasValue) result.UseDrillPlaceFileOrigin = UseDrillPlaceFileOrigin.Value;
        result.SmdOnly = SmdOnly;
        result.ExcludeFootprintsWithTh = ExcludeFootprintsWithTh;
        result.ExcludeDnp = ExcludeDnp;
        result.ExcludeFromBom = ExcludeFromBom;
        result.NegateBottomX = NegateBottomX;
        result.SingleFile = SingleFile;
        result.NakedFilename = NakedFilename;
        PcbExportDtoValidation.Enum(Side, nameof(Side));
        result.Side = Side;
        PcbExportDtoValidation.Enum(Units, nameof(Units));
        result.Units = Units;
        PcbExportDtoValidation.Enum(Format, nameof(Format));
        result.Format = Format;
        if (IncludeBoardEdgeForGerber.HasValue) result.IncludeBoardEdgeForGerber = IncludeBoardEdgeForGerber.Value;
        result.Variant = Variant;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportGencadInput
{
    public bool FlipBottomPads { get; set; }
    public bool UseIndividualShapes { get; set; }
    public bool StoreOriginCoords { get; set; }
    public bool UseDrillOrigin { get; set; }
    public bool UseUniquePins { get; set; }

    internal RunBoardJobExportGencad ToNative()
    {
        var result = new RunBoardJobExportGencad();
        result.FlipBottomPads = FlipBottomPads;
        result.UseIndividualShapes = UseIndividualShapes;
        result.StoreOriginCoords = StoreOriginCoords;
        result.UseDrillOrigin = UseDrillOrigin;
        result.UseUniquePins = UseUniquePins;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportIpc2581Input
{
    public string DrawingSheet { get; set; } = "";
    public string Variant { get; set; } = "";
    public Units Units { get; set; }
    public Ipc2581Version Version { get; set; }
    public int? Precision { get; set; } = null;
    public bool Compress { get; set; }
    public string InternalIdColumn { get; set; } = "";
    public string ManufacturerPartNumberColumn { get; set; } = "";
    public string ManufacturerColumn { get; set; } = "";
    public string DistributorPartNumberColumn { get; set; } = "";
    public string DistributorColumn { get; set; } = "";
    public string BomRevision { get; set; } = "";

    internal RunBoardJobExportIpc2581 ToNative()
    {
        var result = new RunBoardJobExportIpc2581();
        result.DrawingSheet = DrawingSheet;
        result.Variant = Variant;
        PcbExportDtoValidation.Enum(Units, nameof(Units));
        result.Units = Units;
        PcbExportDtoValidation.Enum(Version, nameof(Version));
        result.Version = Version;
        if (Precision.HasValue) result.Precision = Precision.Value;
        result.Compress = Compress;
        result.InternalIdColumn = InternalIdColumn;
        result.ManufacturerPartNumberColumn = ManufacturerPartNumberColumn;
        result.ManufacturerColumn = ManufacturerColumn;
        result.DistributorPartNumberColumn = DistributorPartNumberColumn;
        result.DistributorColumn = DistributorColumn;
        result.BomRevision = BomRevision;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportIpcD356Input
{

    internal RunBoardJobExportIpcD356 ToNative()
    {
        var result = new RunBoardJobExportIpcD356();
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportODBInput
{
    public string DrawingSheet { get; set; } = "";
    public string Variant { get; set; } = "";
    public Units Units { get; set; }
    public int? Precision { get; set; } = null;
    public OdbCompression Compression { get; set; }

    internal RunBoardJobExportODB ToNative()
    {
        var result = new RunBoardJobExportODB();
        result.DrawingSheet = DrawingSheet;
        result.Variant = Variant;
        PcbExportDtoValidation.Enum(Units, nameof(Units));
        result.Units = Units;
        if (Precision.HasValue) result.Precision = Precision.Value;
        PcbExportDtoValidation.Enum(Compression, nameof(Compression));
        result.Compression = Compression;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunBoardJobExportStatsInput
{
    public StatsOutputFormat Format { get; set; }
    public Units Units { get; set; }
    public bool ExcludeFootprintsWithoutPads { get; set; }
    public bool SubtractHolesFromBoardArea { get; set; }
    public bool SubtractHolesFromCopperAreas { get; set; }

    internal RunBoardJobExportStats ToNative()
    {
        var result = new RunBoardJobExportStats();
        PcbExportDtoValidation.Enum(Format, nameof(Format));
        result.Format = Format;
        PcbExportDtoValidation.Enum(Units, nameof(Units));
        result.Units = Units;
        result.ExcludeFootprintsWithoutPads = ExcludeFootprintsWithoutPads;
        result.SubtractHolesFromBoardArea = SubtractHolesFromBoardArea;
        result.SubtractHolesFromCopperAreas = SubtractHolesFromCopperAreas;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class BoardPlotSettingsInput
{
    [JsonConverter(typeof(BoardLayerListJsonConverter))]
    public List<BoardLayer> Layers { get; set; } = [];
    [JsonConverter(typeof(BoardLayerListJsonConverter))]
    public List<BoardLayer> CommonLayers { get; set; } = [];
    public string ColorTheme { get; set; } = "";
    public string DrawingSheet { get; set; } = "";
    public string Variant { get; set; } = "";
    public bool Mirror { get; set; }
    public bool BlackAndWhite { get; set; }
    public bool Negative { get; set; }
    public double Scale { get; set; }
    public bool SketchPadsOnFabLayers { get; set; }
    public bool HideDnpFootprintsOnFabLayers { get; set; }
    public bool SketchDnpFootprintsOnFabLayers { get; set; }
    public bool CrossoutDnpFootprintsOnFabLayers { get; set; }
    public bool PlotFootprintValues { get; set; }
    public bool PlotReferenceDesignators { get; set; }
    public bool PlotDrawingSheet { get; set; }
    public bool SubtractSolderMaskFromSilk { get; set; }
    public bool PlotPadNumbers { get; set; }
    public PlotDrillMarks DrillMarks { get; set; }
    public bool UseDrillOrigin { get; set; }
    public bool CheckZonesBeforePlot { get; set; }

    internal BoardPlotSettings ToNative()
    {
        var result = new BoardPlotSettings();
        foreach (var value in Layers) PcbExportDtoValidation.Enum(value, nameof(Layers));
        result.Layers.Add(Layers);
        foreach (var value in CommonLayers) PcbExportDtoValidation.Enum(value, nameof(CommonLayers));
        result.CommonLayers.Add(CommonLayers);
        result.ColorTheme = ColorTheme;
        result.DrawingSheet = DrawingSheet;
        result.Variant = Variant;
        result.Mirror = Mirror;
        result.BlackAndWhite = BlackAndWhite;
        result.Negative = Negative;
        result.Scale = Scale;
        result.SketchPadsOnFabLayers = SketchPadsOnFabLayers;
        result.HideDnpFootprintsOnFabLayers = HideDnpFootprintsOnFabLayers;
        result.SketchDnpFootprintsOnFabLayers = SketchDnpFootprintsOnFabLayers;
        result.CrossoutDnpFootprintsOnFabLayers = CrossoutDnpFootprintsOnFabLayers;
        result.PlotFootprintValues = PlotFootprintValues;
        result.PlotReferenceDesignators = PlotReferenceDesignators;
        result.PlotDrawingSheet = PlotDrawingSheet;
        result.SubtractSolderMaskFromSilk = SubtractSolderMaskFromSilk;
        result.PlotPadNumbers = PlotPadNumbers;
        PcbExportDtoValidation.Enum(DrillMarks, nameof(DrillMarks));
        result.DrillMarks = DrillMarks;
        result.UseDrillOrigin = UseDrillOrigin;
        result.CheckZonesBeforePlot = CheckZonesBeforePlot;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ExcellonFormatOptionsInput
{
    public bool? MirrorY { get; set; } = null;
    public bool? MinimalHeader { get; set; } = null;
    public bool? CombinePthNpth { get; set; } = null;
    public bool? RouteOvalHoles { get; set; } = null;

    internal ExcellonFormatOptions ToNative()
    {
        var result = new ExcellonFormatOptions();
        if (MirrorY.HasValue) result.MirrorY = MirrorY.Value;
        if (MinimalHeader.HasValue) result.MinimalHeader = MinimalHeader.Value;
        if (CombinePthNpth.HasValue) result.CombinePthNpth = CombinePthNpth.Value;
        if (RouteOvalHoles.HasValue) result.RouteOvalHoles = RouteOvalHoles.Value;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Vector2Input
{
    public long XNm { get; set; }
    public long YNm { get; set; }

    internal Vector2 ToNative()
    {
        var result = new Vector2();
        result.XNm = XNm;
        result.YNm = YNm;
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Vector3DInput
{
    public double XNm { get; set; }
    public double YNm { get; set; }
    public double ZNm { get; set; }

    internal Vector3D ToNative()
    {
        var result = new Vector3D();
        result.XNm = XNm;
        result.YNm = YNm;
        result.ZNm = ZNm;
        return result;
    }
}
