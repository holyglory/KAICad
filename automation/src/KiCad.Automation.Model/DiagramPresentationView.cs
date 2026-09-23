using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KiCad.Automation.Model;

public enum DiagramPortSide { Left, Right, Top, Bottom }

/// <summary>A presentation point in diagram units (x right, y down). Never PCB or schematic geometry.</summary>
public sealed record DiagramPoint(decimal X, decimal Y);

/// <summary>A presentation rectangle in diagram units. Width and height are strictly positive.</summary>
public sealed record DiagramRect(decimal X, decimal Y, decimal Width, decimal Height);

public sealed record DiagramBlockPlacement(Guid BlockId, DiagramRect Rect, bool Locked = false, uint? FillRgb = null);

/// <summary>A boundary port on a block's rectangle (or on the level frame for the level's own
/// interfaces). The offset runs from the top for Left/Right and from the left for Top/Bottom.</summary>
public sealed record DiagramPortPlacement(Guid BlockId, Guid InterfaceId, DiagramPortSide Side, decimal Offset);

/// <summary>The drawn path from Endpoints[0] to Endpoints[EndpointIndex] of one connection.</summary>
public sealed record DiagramConnectionRoute(Guid ConnectionId, int EndpointIndex, ImmutableArray<DiagramPoint> Waypoints,
    DiagramPoint? Label = null, bool Locked = false)
{
    [JsonIgnore] public ImmutableArray<DiagramPoint> Points => Waypoints.IsDefault ? [] : Waypoints;

    public bool SameContents(DiagramConnectionRoute? other) => other is not null && ConnectionId == other.ConnectionId
        && EndpointIndex == other.EndpointIndex && Label == other.Label && Locked == other.Locked && Points.SequenceEqual(other.Points);
}

/// <summary>One diagram level's layout, versioned with the owning block revision (contract rbg-v2
/// section 4.3). A missing entry means "unplaced"; the model never stores fallback positions.
/// Block order is z-order (later paints on top); port and route order is not significant.</summary>
public sealed record DiagramPresentationView(ImmutableArray<DiagramBlockPlacement> Blocks, ImmutableArray<DiagramPortPlacement> Ports,
    ImmutableArray<DiagramConnectionRoute> Routes, DiagramRect? Frame = null)
{
    public const int MaximumWaypoints = 256;
    public static DiagramPresentationView Empty { get; } = new([], [], []);

    [JsonIgnore] public ImmutableArray<DiagramBlockPlacement> BlockPlacements => Blocks.IsDefault ? [] : Blocks;
    [JsonIgnore] public ImmutableArray<DiagramPortPlacement> PortPlacements => Ports.IsDefault ? [] : Ports;
    [JsonIgnore] public ImmutableArray<DiagramConnectionRoute> ConnectionRoutes => Routes.IsDefault ? [] : Routes;
    [JsonIgnore] public bool IsEmpty => Frame is null && BlockPlacements.IsEmpty && PortPlacements.IsEmpty && ConnectionRoutes.IsEmpty;
    /// <summary>Number of keyed entries (blocks, ports and routes); the frame is not keyed.</summary>
    [JsonIgnore] public int EntryCount => BlockPlacements.Length + PortPlacements.Length + ConnectionRoutes.Length;

    /// <summary>PV1, PV2, PV4 and the context-free part of PV3.</summary>
    public void Validate() => Validate(null);

    /// <summary>PV1-PV4. With the owning level's block identity, PV3 is exact: a port on the level
    /// itself sits on the frame, every other port on its block's placement.</summary>
    public void Validate(Guid? scopeBlockId)
    {
        var blocks = new Dictionary<Guid, DiagramRect>();
        foreach (var block in BlockPlacements)
        {
            if (block is null || block.BlockId == Guid.Empty || block.Rect is null)
                throw Invalid("A block placement needs an exact block identity and a rectangle.");
            DiagramCoordinates.CheckRect(block.Rect);
            if (block.FillRgb is > 0xFFFFFFu) throw DiagramCoordinates.Invalid("A block fill must be a 24-bit RGB value.");
            if (block.BlockId == scopeBlockId) throw Invalid("A level's own outline is its frame, not a block placement.");
            if (!blocks.TryAdd(block.BlockId, block.Rect)) throw Invalid("Each block is placed at most once in a level layout.");
        }
        if (Frame is { } frame) DiagramCoordinates.CheckRect(frame);
        var ports = new HashSet<(Guid, Guid)>();
        foreach (var port in PortPlacements)
        {
            if (port is null || port.BlockId == Guid.Empty || port.InterfaceId == Guid.Empty || !Enum.IsDefined(port.Side))
                throw Invalid("A port placement needs exact block and interface identities and a supported side.");
            if (!ports.Add((port.BlockId, port.InterfaceId))) throw Invalid("Each port is placed at most once in a level layout.");
            DiagramCoordinates.Check(port.Offset);
            DiagramRect? rect = scopeBlockId is { } scope
                ? port.BlockId == scope ? Frame : blocks.GetValueOrDefault(port.BlockId)
                : blocks.GetValueOrDefault(port.BlockId) ?? Frame;
            if (rect is null)
                throw Invalid(scopeBlockId == port.BlockId
                    ? "A port on the level's own boundary needs the level frame."
                    : "A port placement needs its block's placement in the same level layout.");
            decimal side = port.Side is DiagramPortSide.Left or DiagramPortSide.Right ? rect.Height : rect.Width;
            if (port.Offset < 0 || port.Offset > side)
                throw DiagramCoordinates.Invalid("A port offset must lie on its side: between zero and the side length.");
        }
        var routes = new HashSet<(Guid, int)>();
        foreach (var route in ConnectionRoutes)
        {
            if (route is null || route.ConnectionId == Guid.Empty || route.EndpointIndex < 1)
                throw Invalid("A route needs an exact connection identity and an endpoint index of one or more.");
            if (!routes.Add((route.ConnectionId, route.EndpointIndex))) throw Invalid("Each connection endpoint is routed at most once in a level layout.");
            if (route.Points.Length > MaximumWaypoints) throw Invalid($"A route keeps at most {MaximumWaypoints} waypoints.");
            foreach (var point in route.Points) DiagramCoordinates.CheckPoint(point);
            if (route.Label is { } label) DiagramCoordinates.CheckPoint(label);
        }
    }

    /// <summary>Ports sorted by (block, interface) and routes by (connection, endpoint index), using
    /// ordinal canonical UUID strings. Block order is z-order and stays as authored.</summary>
    public DiagramPresentationView Canonical() => new(BlockPlacements,
        [.. PortPlacements.OrderBy(p => p.BlockId.ToString("D"), StringComparer.Ordinal).ThenBy(p => p.InterfaceId.ToString("D"), StringComparer.Ordinal)],
        [.. ConnectionRoutes.OrderBy(r => r.ConnectionId.ToString("D"), StringComparer.Ordinal).ThenBy(r => r.EndpointIndex)
            .Select(r => r with { Waypoints = r.Points })], Frame);

    /// <summary>Content equality in canonical order; null and <see cref="Empty"/> are equal.</summary>
    public bool SameContents(DiagramPresentationView? other) => Same(this, other);

    public static bool Same(DiagramPresentationView? left, DiagramPresentationView? right)
    {
        var a = (left ?? Empty).Canonical(); var b = (right ?? Empty).Canonical();
        return a.Frame == b.Frame && a.Blocks.SequenceEqual(b.Blocks) && a.Ports.SequenceEqual(b.Ports)
            && a.Routes.Length == b.Routes.Length && a.Routes.Zip(b.Routes).All(p => p.First.SameContents(p.Second));
    }

    /// <summary>Element keys used by merges and notices: block:&lt;id&gt;, port:&lt;block&gt;:&lt;interface&gt;,
    /// route:&lt;connection&gt;:&lt;index&gt; and frame.</summary>
    public static string Key(DiagramBlockPlacement block) => "block:" + block.BlockId.ToString("D");
    public static string Key(DiagramPortPlacement port) => "port:" + port.BlockId.ToString("D") + ":" + port.InterfaceId.ToString("D");
    public static string Key(DiagramConnectionRoute route) => "route:" + route.ConnectionId.ToString("D") + ":"
        + route.EndpointIndex.ToString(CultureInfo.InvariantCulture);

    internal static AutomationException Invalid(string message) => new("invalid_presentation_view", message);
}

/// <summary>Presentation decimals (contract rbg-v2 section 3): diagram units in [-1e9, 1e9] with a
/// 0.001 quantum. XML carries only the canonical text; protocol text is accepted when exact and is
/// canonicalized. Nothing passes through binary floating point.</summary>
public static partial class DiagramCoordinates
{
    public const decimal Limit = 1_000_000_000m;
    public const long NanometresPerDiagramUnit = 100_000;

    [GeneratedRegex("^(?:-?[1-9][0-9]{0,9}(?:\\.[0-9]{0,2}[1-9])?|0(?:\\.[0-9]{0,2}[1-9])?|-0\\.[0-9]{0,2}[1-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalPattern();
    [GeneratedRegex("^[+-]?(?<integer>[0-9]+)(?:\\.(?<fraction>[0-9]+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProtocolPattern();

    public static void Check(decimal value)
    {
        if (value is < -Limit or > Limit || decimal.Truncate(value * 1000m) != value * 1000m)
            throw Invalid("Presentation coordinates are diagram units within [-1e9, 1e9] and exact to 0.001.");
    }

    public static void CheckPoint(DiagramPoint point)
    {
        if (point is null) throw Invalid("A presentation point needs both coordinates.");
        Check(point.X); Check(point.Y);
    }

    public static void CheckRect(DiagramRect rect)
    {
        if (rect is null) throw Invalid("A presentation rectangle needs a position and a size.");
        Check(rect.X); Check(rect.Y); Check(rect.Width); Check(rect.Height);
        if (rect.Width <= 0 || rect.Height <= 0 || rect.X + rect.Width > Limit || rect.Y + rect.Height > Limit)
            throw Invalid("A presentation rectangle needs a positive size that stays within the diagram-unit range.");
    }

    /// <summary>The canonical text: no exponent, no '+', no "-0", trailing fractional zeros trimmed.</summary>
    public static string Format(decimal value)
    {
        Check(value);
        if (value == 0) return "0";
        string text = value.ToString("0.###", CultureInfo.InvariantCulture);
        return text;
    }

    /// <summary>Reads the canonical XML form only.</summary>
    public static decimal ParseCanonical(string? text)
    {
        if (text is null || !CanonicalPattern().IsMatch(text))
            throw Invalid("Presentation coordinates must use the canonical decimal text (for example 12.5, -0.25 or 0).");
        decimal value = decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        Check(value); return value;
    }

    /// <summary>Reads protocol text such as "12.500000" from C++ std::to_string. Digits beyond the
    /// 0.001 quantum must be zero; the value is never rounded.</summary>
    public static decimal ParseProtocol(string? text)
    {
        var match = text is null ? null : ProtocolPattern().Match(text);
        if (match is null || !match.Success)
            throw Invalid("Presentation coordinates must be plain decimal text without exponent.");
        string integer = match.Groups["integer"].Value.TrimStart('0'), fraction = match.Groups["fraction"].Value;
        if (integer.Length > 10 || fraction.Length > 3 && fraction[3..].Any(c => c != '0'))
            throw Invalid("Presentation coordinates are diagram units within [-1e9, 1e9] and exact to 0.001.");
        string exact = (text![0] == '-' ? "-" : "") + (integer.Length == 0 ? "0" : integer)
            + (fraction.Length == 0 ? "" : "." + fraction[..Math.Min(3, fraction.Length)]);
        decimal value = decimal.Parse(exact, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        Check(value); return value == 0 ? 0m : value;
    }

    internal static AutomationException Invalid(string message) => new("invalid_diagram_coordinate", message);
}
