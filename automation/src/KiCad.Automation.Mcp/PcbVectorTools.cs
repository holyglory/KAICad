using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiapi.Board.Types;
using KiCad.Automation.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Read-only checks for the vector half of the visual-asset pipeline.  This
/// intentionally stops before the native board boundary: a successful result
/// proves that the source can be converted to bounded, integer-nanometre
/// documentation geometry, but does not claim electrical or manufacturing
/// validity.
/// </summary>
[McpServerToolType]
public sealed class PcbVectorTools
{
    [McpServerTool(Name = "kicad_pcb_svg_validate", ReadOnly = true),
     Description("Validate an SVG visual asset before a PCB guide or generated-artwork commit. The strict parser accepts only line, polyline, polygon and M/L/H/V/Z path geometry, rejects transforms/images/text/scripts and out-of-viewBox geometry, verifies an optional source SHA-256, validates optional non-copper layer ownership, and converts every segment to exact integer nanometres. This is a read-only vector check; it does not create board objects or claim DRC, pin/pad, electrical, or manufacturing correctness."),
     KiCadCapability("pcb-guide", "compiled-mcp plus deterministic-svg-parser", "strict SVG geometry, source SHA-256, optional layer ownership, integer-nanometre conversion"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public CallToolResult ValidateSvg(string svg, string sourceSha256 = "", string layer = "",
        string originXNm = "0", string originYNm = "0", string nanometersPerSvgUnit = "1",
        long strokeWidthNm = 100_000, int maximumSegments = 20_000)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(svg))
                throw new AutomationException("invalid_pcb_svg_guide", "SVG source is required.");
            if (maximumSegments is < 1 or > 20_000)
                throw new AutomationException("invalid_pcb_svg_guide", "maximumSegments must be between 1 and 20,000.");
            if (strokeWidthNm <= 0)
                throw new AutomationException("invalid_pcb_svg_guide", "strokeWidthNm must be positive.");

            byte[] sourceBytes = Encoding.UTF8.GetBytes(svg);
            string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
            bool provenanceBound = !string.IsNullOrWhiteSpace(sourceSha256);
            if (provenanceBound && (!IsSha256(sourceSha256) ||
                !string.Equals(actualSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)))
                throw new AutomationException("pcb_guide_source_changed", "The supplied SVG bytes do not match sourceSha256.");

            PcbSvgGuide parsed = PcbSvgGuideParser.Parse(svg, maximumSegments);
            decimal originX = Decimal(originXNm, "originXNm");
            decimal originY = Decimal(originYNm, "originYNm");
            decimal scale = Decimal(nanometersPerSvgUnit, "nanometersPerSvgUnit");
            if (scale <= 0)
                throw new AutomationException("invalid_pcb_svg_guide", "nanometersPerSvgUnit must be positive.");

            string? layerName = null;
            if (!string.IsNullOrWhiteSpace(layer))
            {
                BoardLayer boardLayer = ParseGuideLayer(layer);
                ValidateGuideLayer(boardLayer);
                layerName = boardLayer.ToString();
            }

            var segments = parsed.Segments.Select(segment => new ConvertedSegment(
                Point(segment.StartX, segment.StartY, originX, originY, scale),
                Point(segment.EndX, segment.EndY, originX, originY, scale))).ToArray();
            long minX = segments.SelectMany(segment => new[] { segment.Start.XNm, segment.End.XNm }).Min();
            long minY = segments.SelectMany(segment => new[] { segment.Start.YNm, segment.End.YNm }).Min();
            long maxX = segments.SelectMany(segment => new[] { segment.Start.XNm, segment.End.XNm }).Max();
            long maxY = segments.SelectMany(segment => new[] { segment.Start.YNm, segment.End.YNm }).Max();
            TopologyAnalysis topology = AnalyzeTopology(segments, parsed.Shapes);
            StyleAnalysis style = AnalyzeStyle(parsed.Shapes);

            var result = new
            {
                vectorValidation = true,
                sourceSha256 = actualSha256,
                provenanceBound,
                sourceBytes = sourceBytes.Length,
                viewBox = new { x = parsed.ViewBoxX, y = parsed.ViewBoxY, width = parsed.ViewBoxWidth, height = parsed.ViewBoxHeight },
                segmentCount = segments.Length,
                layer = layerName,
                layerOwnershipValidated = layerName is not null,
                conversion = new { originXNm = originX, originYNm = originY, nanometersPerSvgUnit = scale, strokeWidthNm },
                boundsNm = new { minXNm = minX, minYNm = minY, maxXNm = maxX, maxYNm = maxY },
                segments,
                topology,
                style,
                authoritativeGeometryReady = style.ReadyForOutlineCommit,
                boardMutation = false,
                nativeCommit = false,
                electricalValidation = false,
                manufacturabilityValidation = false
            };
            return Data(result);
        }
        catch (Exception error) when (error is AutomationException or ArgumentException or FormatException or OverflowException)
        {
            return Data(new
            {
                errorCode = error is AutomationException known ? known.Code : "invalid_pcb_svg_guide",
                errorMessage = error.Message,
                boardMutation = false,
                nativeCommit = false
            }, true);
        }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static decimal Decimal(string value, string name) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)
            ? parsed
            : throw new AutomationException("invalid_pcb_svg_guide", $"{name} must be a finite decimal.");

    private static NanometrePoint Point(decimal x, decimal y, decimal originX, decimal originY, decimal scale)
    {
        decimal convertedX;
        decimal convertedY;
        try
        {
            convertedX = originX + x * scale;
            convertedY = originY + y * scale;
        }
        catch (OverflowException)
        {
            throw new AutomationException("invalid_pcb_svg_guide_coordinates", "SVG coordinates exceed decimal conversion bounds.");
        }
        if (decimal.Truncate(convertedX) != convertedX || decimal.Truncate(convertedY) != convertedY
            || convertedX < long.MinValue || convertedX > long.MaxValue
            || convertedY < long.MinValue || convertedY > long.MaxValue)
            throw new AutomationException("invalid_pcb_svg_guide_coordinates", "SVG guide coordinates must convert exactly to integer nanometres.");
        return new NanometrePoint(checked((long)convertedX), checked((long)convertedY));
    }

    private readonly record struct NanometrePoint(long XNm, long YNm);

    private readonly record struct ConvertedSegment(NanometrePoint Start, NanometrePoint End);

    private readonly record struct TopologyAnalysis(
        int ShapeCount,
        int ClosedShapeCount,
        int OpenShapeCount,
        int SegmentCount,
        int VertexCount,
        int ConnectedComponents,
        int DanglingEndpoints,
        int BranchPoints,
        bool ExactEndpointGraph,
        bool SelfIntersectionsChecked,
        string Limitation);

    private readonly record struct StyleAnalysis(
        int UnsupportedShapeCount,
        int FillNoneCount,
        int FillPaintCount,
        int FillUnspecifiedCount,
        int FillUnsupportedCount,
        int StrokeNoneCount,
        int StrokePaintCount,
        int StrokeUnspecifiedCount,
        int StrokeUnsupportedCount,
        int StrokeWidthSpecifiedCount,
        bool FillSemanticsExplicit,
        bool StrokeSemanticsExplicit,
        bool ReadyForOutlineCommit,
        string Limitation);

    private static TopologyAnalysis AnalyzeTopology(
        IReadOnlyList<ConvertedSegment> segments, IReadOnlyList<PcbSvgGuideShape> shapes)
    {
        // This graph is deliberately built from converted integer coordinates.
        // It proves exact endpoint connectivity, while crossings in the middle
        // of segments remain outside this inexpensive managed boundary.
        var vertices = new Dictionary<(long X, long Y), int>();
        var degree = new Dictionary<(long X, long Y), int>();
        var parent = new List<int>();

        int Vertex((long X, long Y) point)
        {
            if (vertices.TryGetValue(point, out int existing))
                return existing;
            int id = parent.Count;
            vertices.Add(point, id);
            parent.Add(id);
            degree[point] = 0;
            return id;
        }

        int Root(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }

        void Union(int left, int right)
        {
            left = Root(left); right = Root(right);
            if (left != right) parent[right] = left;
        }

        foreach (ConvertedSegment segment in segments)
        {
            (long X, long Y) start = (segment.Start.XNm, segment.Start.YNm);
            (long X, long Y) end = (segment.End.XNm, segment.End.YNm);
            int left = Vertex(start), right = Vertex(end);
            degree[start]++; degree[end]++;
            Union(left, right);
        }

        int components = vertices.Count == 0 ? 0 : vertices.Values.Select(Root).Distinct().Count();
        return new(
            shapes.Count,
            shapes.Count(shape => shape.Closed),
            shapes.Count(shape => !shape.Closed),
            segments.Count,
            vertices.Count,
            components,
            degree.Values.Count(value => value == 1),
            degree.Values.Count(value => value > 2),
            true,
            false,
            "Exact converted endpoints are grouped; segment crossings and filled-area topology require native geometry analysis.");
    }

    private static StyleAnalysis AnalyzeStyle(IReadOnlyList<PcbSvgGuideShape> shapes)
    {
        int unsupported = shapes.Count(shape => shape.UnsupportedStyle);
        int fillNone = shapes.Count(shape => shape.Fill == PcbSvgGuidePaint.None);
        int fillPaint = shapes.Count(shape => shape.Fill == PcbSvgGuidePaint.Paint);
        int fillUnspecified = shapes.Count(shape => shape.Fill == PcbSvgGuidePaint.Unspecified);
        int fillUnsupported = shapes.Count(shape => shape.Fill == PcbSvgGuidePaint.Unsupported);
        int strokeNone = shapes.Count(shape => shape.Stroke == PcbSvgGuidePaint.None);
        int strokePaint = shapes.Count(shape => shape.Stroke == PcbSvgGuidePaint.Paint);
        int strokeUnspecified = shapes.Count(shape => shape.Stroke == PcbSvgGuidePaint.Unspecified);
        int strokeUnsupported = shapes.Count(shape => shape.Stroke == PcbSvgGuidePaint.Unsupported);
        bool fillExplicit = shapes.Count > 0 && shapes.All(shape => shape.Fill != PcbSvgGuidePaint.Unspecified);
        bool strokeExplicit = shapes.Count > 0 && shapes.All(shape => shape.Stroke != PcbSvgGuidePaint.Unspecified);
        bool ready = shapes.Count > 0 && !shapes.Any(shape => shape.UnsupportedStyle)
            && shapes.All(shape => shape.Fill == PcbSvgGuidePaint.None);
        return new(
            unsupported, fillNone, fillPaint, fillUnspecified, fillUnsupported,
            strokeNone, strokePaint, strokeUnspecified, strokeUnsupported,
            shapes.Count(shape => shape.StrokeWidthSpecified), fillExplicit, strokeExplicit, ready,
            "The managed conversion emits outline strokes and does not preserve SVG fill paint, stroke paint, CSS inheritance or gradients.");
    }

    private static void ValidateGuideLayer(BoardLayer layer)
    {
        if (layer is BoardLayer.BlUnknown or BoardLayer.BlUndefined)
            throw new AutomationException("invalid_pcb_svg_guide_layer", "Layer must name a concrete non-copper board layer.");
        if ((int)layer >= 3 && (int)layer <= 34)
            throw new AutomationException("invalid_pcb_svg_guide_layer", "SVG visual assets must use a non-copper board layer.");
    }

    private static BoardLayer ParseGuideLayer(string value) => value switch
    {
        "Dwgs.User" => BoardLayer.BlDwgsUser,
        "Cmts.User" => BoardLayer.BlCmtsUser,
        "Eco1.User" => BoardLayer.BlEco1User,
        "Eco2.User" => BoardLayer.BlEco2User,
        "F.SilkS" => BoardLayer.BlFSilkS,
        "B.SilkS" => BoardLayer.BlBSilkS,
        _ when Enum.TryParse<BoardLayer>(value, true, out var parsed) => parsed,
        _ => throw new AutomationException("invalid_pcb_svg_guide_layer", "Use a named non-copper board layer such as Dwgs.User or Cmts.User.")
    };

    private static CallToolResult Data(object value, bool error = false)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new CallToolResult
        {
            IsError = error,
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
            StructuredContent = structured
        };
    }
}
