using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace KiCad.Automation.Model;

public sealed record PcbSvgGuideSegment(decimal StartX, decimal StartY, decimal EndX, decimal EndY);

public enum PcbSvgGuidePaint
{
    Unspecified = 0,
    None = 1,
    Paint = 2,
    Unsupported = 3
}

/// <summary>
/// The source element that produced one contiguous range of guide segments.
/// Paint values are intentionally conservative: an omitted presentation value
/// remains <see cref="PcbSvgGuidePaint.Unspecified"/> because the conversion
/// boundary emits outline strokes and cannot silently preserve SVG fills or
/// inherited CSS.
/// </summary>
public sealed record PcbSvgGuideShape(
    string ElementName,
    int SegmentStart,
    int SegmentCount,
    bool Closed,
    PcbSvgGuidePaint Fill,
    PcbSvgGuidePaint Stroke,
    bool StrokeWidthSpecified,
    bool UnsupportedStyle);

public sealed record PcbSvgGuide(decimal ViewBoxX, decimal ViewBoxY, decimal ViewBoxWidth, decimal ViewBoxHeight,
    ImmutableArray<PcbSvgGuideSegment> Segments)
{
    public ImmutableArray<PcbSvgGuideShape> Shapes { get; init; } = ImmutableArray<PcbSvgGuideShape>.Empty;

    public void Validate()
    {
        if (ViewBoxWidth <= 0 || ViewBoxHeight <= 0 || Segments.IsDefault || Segments.IsEmpty)
            throw Invalid("An SVG guide needs a positive viewBox and at least one vector segment.");
        foreach (var segment in Segments)
        {
            if (segment.StartX < ViewBoxX || segment.StartX > ViewBoxX + ViewBoxWidth
                || segment.EndX < ViewBoxX || segment.EndX > ViewBoxX + ViewBoxWidth
                || segment.StartY < ViewBoxY || segment.StartY > ViewBoxY + ViewBoxHeight
                || segment.EndY < ViewBoxY || segment.EndY > ViewBoxY + ViewBoxHeight)
                throw Invalid("SVG guide geometry must remain inside its declared viewBox.");
            if (segment.StartX == segment.EndX && segment.StartY == segment.EndY)
                throw Invalid("SVG guide geometry cannot contain zero-length segments.");
        }

        foreach (PcbSvgGuideShape shape in Shapes)
        {
            if (string.IsNullOrWhiteSpace(shape.ElementName)
                || shape.SegmentStart < 0 || shape.SegmentCount <= 0
                || shape.SegmentStart > Segments.Length - shape.SegmentCount)
                throw Invalid("SVG guide shape metadata must identify a contiguous segment range.");
        }
    }

    private static AutomationException Invalid(string message) => new("invalid_pcb_svg_guide", message);
}

/// <summary>Strict, deterministic SVG subset for PCB visual guides. It deliberately
/// accepts only line/polyline/polygon and M/L/H/V/Z path geometry; transforms,
/// scripts, images, text and styling that could hide geometry are rejected. The
/// result is documentation geometry, never electrical copper.</summary>
public static class PcbSvgGuideParser
{
    private static readonly Regex NumberToken = new(@"[-+]?(?:(?:\d+(?:\.\d*)?)|(?:\.\d+))(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PathToken = new(@"[MmLlHhVvZz]|[-+]?(?:(?:\d+(?:\.\d*)?)|(?:\.\d+))(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static PcbSvgGuide Parse(string svg, int maximumSegments = 20_000)
    {
        if (string.IsNullOrWhiteSpace(svg) || svg.Length > 2_000_000) throw Invalid("SVG source is empty or exceeds the supported size.");
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
            var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
            if (root is null || root.Name.LocalName != "svg") throw Invalid("The source must contain an SVG root element.");
            if (root.DescendantsAndSelf().Any(e => e.Attribute("transform") is not null
                || e.Name.LocalName is "script" or "image" or "text" or "use" or "foreignObject"))
                throw Invalid("SVG transforms, scripts, images, text and external references are not allowed in a PCB guide.");
            var viewBox = Numbers((string?)root.Attribute("viewBox"), 4, "viewBox");
            var result = ImmutableArray.CreateBuilder<PcbSvgGuideSegment>();
            var shapes = ImmutableArray.CreateBuilder<PcbSvgGuideShape>();
            foreach (var element in root.Descendants())
            {
                int segmentStart = result.Count;
                bool closed = false;
                switch (element.Name.LocalName)
                {
                    case "line": AddLine(result, Numbers((string?)element.Attribute("x1"), 1, "line x1")[0], Numbers((string?)element.Attribute("y1"), 1, "line y1")[0],
                        Numbers((string?)element.Attribute("x2"), 1, "line x2")[0], Numbers((string?)element.Attribute("y2"), 1, "line y2")[0], maximumSegments); break;
                    case "polyline": AddPoints(result, Numbers((string?)element.Attribute("points"), -1, "polyline points"), false, maximumSegments); break;
                    case "polygon": AddPoints(result, Numbers((string?)element.Attribute("points"), -1, "polygon points"), true, maximumSegments); closed = true; break;
                    case "path":
                        string path = (string?)element.Attribute("d") ?? throw Invalid("SVG path requires a d attribute.");
                        AddPath(result, path, maximumSegments); closed = path.Any(character => character is 'Z' or 'z'); break;
                    case "svg": break;
                    case "g": break;
                    default: throw Invalid($"SVG element '{element.Name.LocalName}' is not supported in a PCB guide.");
                }
                if (result.Count > segmentStart)
                    shapes.Add(Shape(element, segmentStart, result.Count - segmentStart, closed));
            }
            var guide = new PcbSvgGuide(viewBox[0], viewBox[1], viewBox[2], viewBox[3], result.ToImmutable())
            {
                Shapes = shapes.ToImmutable()
            };
            guide.Validate(); return guide;
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is XmlException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid("The SVG guide is not well-formed: " + error.Message); }
    }

    private static void AddLine(ImmutableArray<PcbSvgGuideSegment>.Builder result, decimal x1, decimal y1, decimal x2, decimal y2, int max)
    { if (result.Count >= max) throw Invalid("SVG guide exceeds the segment limit."); result.Add(new(x1, y1, x2, y2)); }

    private static void AddPoints(ImmutableArray<PcbSvgGuideSegment>.Builder result, decimal[] numbers, bool closed, int max)
    {
        if (numbers.Length < 4 || numbers.Length % 2 != 0) throw Invalid("Polyline points must contain coordinate pairs.");
        for (int i = 2; i < numbers.Length; i += 2) AddLine(result, numbers[i - 2], numbers[i - 1], numbers[i], numbers[i + 1], max);
        if (closed) AddLine(result, numbers[^2], numbers[^1], numbers[0], numbers[1], max);
    }

    private static void AddPath(ImmutableArray<PcbSvgGuideSegment>.Builder result, string? data, int max)
    {
        if (string.IsNullOrWhiteSpace(data)) throw Invalid("SVG path requires a d attribute.");
        var tokens = PathToken.Matches(data).ToArray(); int consumed = 0;
        foreach (var token in tokens) { if (data[consumed..token.Index].Trim(" ,\t\r\n".ToCharArray()).Length != 0) throw Invalid("SVG path contains unsupported syntax."); consumed = token.Index + token.Length; }
        if (data[consumed..].Trim(" ,\t\r\n".ToCharArray()).Length != 0) throw Invalid("SVG path contains unsupported syntax.");
        int index = 0; char command = '\0'; decimal x = 0, y = 0, startX = 0, startY = 0;
        while (index < tokens.Length)
        {
            if (tokens[index].Value.Length == 1 && char.IsLetter(tokens[index].Value[0])) command = tokens[index++].Value[0];
            if (command is 'Z' or 'z') { AddLine(result, x, y, startX, startY, max); x = startX; y = startY; command = '\0'; continue; }
            bool relative = char.IsLower(command); char upper = char.ToUpperInvariant(command);
            if (upper is not ('M' or 'L' or 'H' or 'V')) throw Invalid("Only M, L, H, V and Z SVG path commands are supported.");
            int required = upper is 'H' or 'V' ? 1 : 2;
            if (index + required > tokens.Length || Enumerable.Range(index, required).Any(i => tokens[i].Value.Length == 1 && char.IsLetter(tokens[i].Value[0])))
                throw Invalid("SVG path command has incomplete coordinates.");
            decimal nextX = x, nextY = y;
            if (upper == 'H') nextX = Number(tokens[index++].Value) + (relative ? x : 0);
            else if (upper == 'V') nextY = Number(tokens[index++].Value) + (relative ? y : 0);
            else { nextX = Number(tokens[index++].Value) + (relative ? x : 0); nextY = Number(tokens[index++].Value) + (relative ? y : 0); }
            if (upper == 'M') { x = nextX; y = nextY; startX = x; startY = y; command = relative ? 'l' : 'L'; }
            else { AddLine(result, x, y, nextX, nextY, max); x = nextX; y = nextY; }
        }
    }

    private static PcbSvgGuideShape Shape(XElement element, int segmentStart, int segmentCount, bool closed)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool unsupportedStyle = false;
        foreach (XElement ancestor in element.AncestorsAndSelf().Reverse())
        {
            if (ancestor.Attribute("style") is XAttribute style)
            {
                // CSS inheritance and selectors are outside this deterministic
                // subset. Retain the fact that a style exists instead of
                // pretending the child has the fully-resolved presentation.
                unsupportedStyle = true;
                foreach (string declaration in style.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] pair = declaration.Split(':', 2);
                    if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]))
                        continue;
                    attributes[pair[0].Trim()] = pair[1].Trim();
                }
            }
        }

        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.Name.LocalName is "fill" or "stroke" or "stroke-width")
                attributes[attribute.Name.LocalName] = attribute.Value.Trim();
            else if (attribute.Name.LocalName == "style")
                continue;
        }

        bool styleHasUnknownProperty = attributes.Keys.Any(key => key is not ("fill" or "stroke" or "stroke-width"));
        unsupportedStyle |= styleHasUnknownProperty;
        PcbSvgGuidePaint fill = Paint(attributes, "fill", ref unsupportedStyle);
        PcbSvgGuidePaint stroke = Paint(attributes, "stroke", ref unsupportedStyle);
        bool strokeWidthSpecified = attributes.ContainsKey("stroke-width");
        if (strokeWidthSpecified && (!decimal.TryParse(attributes["stroke-width"], NumberStyles.Float,
                CultureInfo.InvariantCulture, out decimal width) || width <= 0))
            unsupportedStyle = true;

        return new(element.Name.LocalName, segmentStart, segmentCount, closed, fill, stroke,
            strokeWidthSpecified, unsupportedStyle);
    }

    private static PcbSvgGuidePaint Paint(Dictionary<string, string> attributes, string name, ref bool unsupported)
    {
        if (!attributes.TryGetValue(name, out string? value) || value.Length == 0)
            return PcbSvgGuidePaint.Unspecified;
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            return PcbSvgGuidePaint.None;
        if (value.Contains("url(", StringComparison.OrdinalIgnoreCase)
            || value.Contains("var(", StringComparison.OrdinalIgnoreCase)
            || value.Contains("gradient", StringComparison.OrdinalIgnoreCase)
            || value.Contains("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            unsupported = true;
            return PcbSvgGuidePaint.Unsupported;
        }
        return PcbSvgGuidePaint.Paint;
    }

    private static decimal[] Numbers(string? text, int expected, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) throw Invalid($"SVG {name} is required.");
        var matches = NumberToken.Matches(text).ToArray(); int consumed = 0;
        foreach (var match in matches) { if (text[consumed..match.Index].Trim(" ,\t\r\n".ToCharArray()).Length != 0) throw Invalid($"SVG {name} contains invalid syntax."); consumed = match.Index + match.Length; }
        if (text[consumed..].Trim(" ,\t\r\n".ToCharArray()).Length != 0 || (expected >= 0 && matches.Length != expected)) throw Invalid($"SVG {name} has the wrong number of values.");
        return matches.Select(m => Number(m.Value)).ToArray();
    }
    private static decimal Number(string value) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw Invalid("SVG coordinates must be finite decimal values.");
    private static AutomationException Invalid(string message) => new("invalid_pcb_svg_guide", message);
}
