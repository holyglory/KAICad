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

            var segments = parsed.Segments.Select(segment => new
            {
                start = Point(segment.StartX, segment.StartY, originX, originY, scale),
                end = Point(segment.EndX, segment.EndY, originX, originY, scale)
            }).ToArray();
            long minX = segments.SelectMany(segment => new[] { segment.start.XNm, segment.end.XNm }).Min();
            long minY = segments.SelectMany(segment => new[] { segment.start.YNm, segment.end.YNm }).Min();
            long maxX = segments.SelectMany(segment => new[] { segment.start.XNm, segment.end.XNm }).Max();
            long maxY = segments.SelectMany(segment => new[] { segment.start.YNm, segment.end.YNm }).Max();

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
