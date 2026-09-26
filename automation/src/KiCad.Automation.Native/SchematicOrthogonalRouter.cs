using System.Globalization;
using static KiCad.Automation.Native.SchematicConnectionRealizer;

namespace KiCad.Automation.Native;

// CN-1 milestone 2 (automation/design/contracts/cn1-wiring-intent.md §7): the grid router that joins the pins of one
// connection on one sheet with orthogonal wires. Lane 2A created this file for CN-1 §7. It is pure geometry: the realizer
// (SchematicConnectionRealizer) decides which connections it routes, checks each pin's one-grid escape and the name
// label at the tree root with the §6.4 admission rules, hands this router everything already on the sheet, and turns
// the returned wires and junctions into native items. A connection this router cannot join falls back to label stubs.

/// <summary>A wire or bus segment the route must respect: already on the sheet, or generated earlier for another
/// connection. A <paramref name="Crossable"/> segment is a foreign wire, which a route may cross at a right angle away
/// from its ends; any other segment (a bus, or a wire of this connection that already exists) may not be crossed.</summary>
internal readonly record struct RouteSegment(Pt A, Pt B, bool Crossable);

/// <summary>A pin the route joins. The route leaves it along <paramref name="Outward"/> for exactly one grid step
/// (its escape) before it may turn. <paramref name="Items"/> counts what already connects at the pin as KiCad counts it
/// when it decides whether a point needs a junction: each symbol with a pin there once, however many of its pins are
/// stacked there.</summary>
internal sealed record RouteTerminal(Guid PlacedPinId, Pt Anchor, (int Dx, int Dy) Outward, int Items = 1)
{
    public Pt Escape(long grid) => Anchor.Step(Outward, grid);
}

/// <summary>Where the route may attach to the connection's existing part: an existing wire end
/// (<paramref name="Pin"/> null; <paramref name="Node"/> is that end), or an existing pin whose one-grid corridor
/// leaves it along <paramref name="Corridor"/> (<paramref name="Node"/> is the corridor's end).
/// <paramref name="ExistingExits"/> are the directions in which existing wires leave <paramref name="Node"/> (for a wire
/// end) or the pin (for a corridor), and <paramref name="Items"/> counts, as KiCad does, what already connects there: each
/// wire direction and each symbol with a pin there once.</summary>
internal sealed record RouteAttachPoint(Pt Node, Pt? Pin, (int Dx, int Dy)? Corridor, IReadOnlyList<(int Dx, int Dy)> ExistingExits, int Items);

/// <summary>The connection's existing part on this sheet, which the route must reach at one of its points.
/// <paramref name="TieKey"/> orders it among the pins when distances tie.</summary>
internal sealed record RouteExistingPart(string TieKey, IReadOnlyList<RouteAttachPoint> Points);

/// <summary>A measured obstacle and the nodes it does not block (a pin's escape corridor through its own symbol).</summary>
internal sealed record RouteObstacle(Box Bounds, IReadOnlyCollection<Pt> ExemptNodes);

/// <summary>Everything one routing needs. <paramref name="Terminals"/> are in routing order (see
/// <see cref="SchematicOrthogonalRouter.Order"/>): the first is the tree root, whose escape is extended to
/// <paramref name="RootStubLengthNm"/> when it carries the connection's name label with envelope
/// <paramref name="RootEnvelope"/>.</summary>
internal sealed record OrthogonalRouteRequest(long GridNm, long ClearanceNm, Box Region, IReadOnlyList<RouteTerminal> Terminals,
    long RootStubLengthNm, Box? RootEnvelope, RouteExistingPart? Existing, IReadOnlyList<RouteObstacle> Obstacles,
    IReadOnlyList<Pt> Points, IReadOnlyList<RouteSegment> Segments, IReadOnlyList<Box> Envelopes, SchematicRoutingLimits Limits);

/// <summary>A routed connection: maximal straight wire segments in their identity order, the junctions, and the measured
/// cost of the search (§7 budgets).</summary>
internal sealed record OrthogonalRoute(IReadOnlyList<(Pt Start, Pt End)> Segments, IReadOnlyList<Pt> Junctions, long RoutedLengthNm,
    long SpanningTreeLengthNm, int ExpandedNodes, bool AttachedExisting);

/// <summary>Either a route, or the reason the connection falls back to label stubs.</summary>
internal sealed record OrthogonalRouteResult(OrthogonalRoute? Route, string? FallbackReason);

/// <summary>The §7 routing budgets: at most <paramref name="MaxExpandedNodes"/> expanded search nodes per connection and a
/// routed length of at most <paramref name="LengthFactor"/> times the Manhattan minimum spanning tree of its pins.</summary>
public sealed record SchematicRoutingLimits(int MaxExpandedNodes, int LengthFactor)
{
    /// <summary>The contract budgets: 200,000 expanded nodes and four times the spanning tree (CN-1 §7).</summary>
    public static readonly SchematicRoutingLimits Contract = new(200_000, 4);
}

/// <summary>Orthogonal wiring of one connection on one sheet (CN-1 §7). Wires run on the connection grid inside the route
/// region (the drawing sheet's frame interior less the clearance). A grid node is blocked when it lies within an obstacle's
/// bounds inflated by the clearance (except a pin's own escape corridor through its own symbol), within the clearance of a
/// foreign connection point, on a segment that may not be crossed, or inside a generated label; an edge is blocked when it
/// meets an obstacle's inflated bounds (except between two nodes of an escape corridor), runs parallel to a foreign segment
/// within the clearance, passes within the clearance of a foreign connection point, or meets a generated label. A foreign
/// wire is crossed only at a right angle, away from its ends, and never where the route turns, ends or branches. Every step costs 1, every bend 2 and every crossing 10. The tree grows from the first
/// pin by joining the Manhattan-nearest remaining pin (ties by pin identity) to any node of the tree with A*, whose ties
/// prefer horizontal moves, then lower x, then lower y. Collinear steps merge into maximal segments, and a junction is
/// placed wherever three or more wire ends and pins meet, which includes every branch on the inside of a segment. The
/// result is stable under KiCad's own schematic clean-up: no two collinear wires meet without a junction.</summary>
internal static class SchematicOrthogonalRouter
{
    // Directions: 0 right (+x), 1 left (-x), 2 down (+y), 3 up (-y); 4 = no incoming direction.
    private static readonly (int Dx, int Dy)[] Directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private const int None = 4;

    /// <summary>The routing order of the pins: by escape x, then escape y, then pin identity (ordinal text).</summary>
    public static IReadOnlyList<RouteTerminal> Order(IEnumerable<RouteTerminal> terminals, long grid) => terminals
        .OrderBy(t => t.Escape(grid).X).ThenBy(t => t.Escape(grid).Y).ThenBy(t => t.PlacedPinId.ToString("D"), StringComparer.Ordinal).ToArray();

    public static OrthogonalRouteResult Route(OrthogonalRouteRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Search(request, token).Run();
    }

    private static int DirectionOf((int Dx, int Dy) d) => d switch
    {
        (1, 0) => 0, (-1, 0) => 1, (0, 1) => 2, (0, -1) => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(d), d, "A route direction is one schematic axis.")
    };

    private static int Opposite(int d) => d ^ 1;

    private static bool Vertical(int d) => d >= 2;

    /// <summary>At most this many grid nodes are laid out for one sheet: about 7 bytes each (node and edge flags, crossing
    /// counts and the reused distance field), so at most about 56 MB, whatever the page. The search state itself is kept only
    /// for the nodes a search reaches, which the expanded-node budget bounds.</summary>
    internal const int MaxGridNodes = 8_000_000;

    // Node flags, one byte per grid node. Edge flags index the edge leaving a node to the right (horizontal) or downwards
    // (vertical). A crossing node lies on the inside of a crossable foreign segment: crossing it costs 10 and the route must
    // pass straight through.
    private const byte BlockedFlag = 1, HBlockedFlag = 2, VBlockedFlag = 4, CrossVFlag = 8, CrossHFlag = 16;

    private sealed class Search(OrthogonalRouteRequest request, CancellationToken token)
    {
        private readonly long g = request.GridNm, c = request.ClearanceNm;
        private long i0, j0;
        private int width, height;
        private byte[] flags = [];
        private byte[] hCross = [], vCross = [];
        // Manhattan distance of every node to the nearest tree node, recomputed in place for each pin the tree joins.
        private int[] distance = [];
        private readonly HashSet<int> goals = [];
        private readonly Dictionary<(int A, int B), bool> edges = [];
        private int expanded;
        private long routed;

        public OrthogonalRouteResult Run()
        {
            if (g <= 0 || c < 0 || c >= g) throw new ArgumentOutOfRangeException(nameof(request), "The clearance must be less than the grid.");
            if (request.Terminals.Count < 2 && request.Existing is null)
                throw new ArgumentException("A route joins at least two pins, or a pin and an existing connection.", nameof(request));
            if (!Layout(out string? reason)) return new(null, reason);
            foreach (var t in request.Terminals)
            {
                if (t.Anchor.X % g != 0 || t.Anchor.Y % g != 0)
                    return new(null, "pin " + t.PlacedPinId.ToString("D") + " is not on the " + g.ToString(CultureInfo.InvariantCulture) + " nm connection grid");
                if (Index(t.Escape(g)) < 0)
                    return new(null, "pin " + t.PlacedPinId.ToString("D") + " would leave its symbol outside the page inset");
            }
            Mark();
            // The root: its escape, or the label stub that extends it, is the tree to start from.
            var root = request.Terminals[0];
            long rootLength = Math.Max(request.RootStubLengthNm, g);
            if (rootLength % g != 0) throw new ArgumentOutOfRangeException(nameof(request), "The root stub is a whole number of grid steps.");
            var previous = root.Anchor;
            for (long step = g; step <= rootLength; step += g)
            {
                var next = root.Anchor.Step(root.Outward, step);
                AddEdge(previous, next);
                if (step < rootLength || rootLength == g) AddGoal(next);
                previous = next;
            }
            var remaining = request.Terminals.Skip(1).ToList();
            bool existingPending = request.Existing is not null;
            bool attached = false;
            while (remaining.Count > 0 || existingPending)
            {
                token.ThrowIfCancellationRequested();
                if (goals.Count == 0) return new(null, "the connection's first pin has no free grid node to join");
                var nearest = Distances();
                // The Manhattan-nearest remaining pin (or the existing connection), ties by identity.
                int best = -1; long bestDistance = long.MaxValue; string bestKey = "";
                for (int k = 0; k < remaining.Count; k++)
                {
                    long d = nearest[Index(remaining[k].Escape(g))];
                    string key = remaining[k].PlacedPinId.ToString("D");
                    if (d < bestDistance || (d == bestDistance && string.CompareOrdinal(key, bestKey) < 0)) { best = k; bestDistance = d; bestKey = key; }
                }
                if (existingPending)
                {
                    long d = request.Existing!.Points.Select(p => Index(p.Node)).Where(n => n >= 0).Select(n => (long)nearest[n]).DefaultIfEmpty(long.MaxValue).Min();
                    if (d < bestDistance || (d == bestDistance && string.CompareOrdinal(request.Existing.TieKey, bestKey) < 0)) { best = -1; bestDistance = d; }
                }
                if (best >= 0)
                {
                    var terminal = remaining[best];
                    remaining.RemoveAt(best);
                    var start = Index(terminal.Escape(g));
                    AddEdge(terminal.Anchor, terminal.Escape(g));
                    var path = AStar([(start, DirectionOf(terminal.Outward), (IReadOnlyCollection<int>?)null)], nearest, out string? failure);
                    if (path is null) return new(null, failure ?? "pin " + terminal.PlacedPinId.ToString("D") + " cannot reach the rest of its connection");
                    Commit(path, goal: start);
                }
                else
                {
                    existingPending = false;
                    var starts = new List<(int Node, int Incoming, IReadOnlyCollection<int>? Allowed)>();
                    foreach (var point in request.Existing!.Points)
                    {
                        int node = Index(point.Node);
                        if (node < 0) continue;
                        if (point.Corridor is { } corridor)
                        {
                            if (IsBlocked(node)) continue;
                            starts.Add((node, DirectionOf(corridor), null));
                        }
                        else
                        {
                            // No second wire end may continue an existing wire straight on (KiCad's clean-up would merge
                            // them into one wire, changing the existing one); any other free direction branches or bends.
                            var exits = point.ExistingExits.Select(DirectionOf).ToHashSet();
                            var allowed = Enumerable.Range(0, 4).Where(d => !exits.Contains(d) && !(exits.Count == 1 && exits.Contains(Opposite(d)))).ToArray();
                            if (allowed.Length != 0) starts.Add((node, None, allowed));
                        }
                    }
                    if (starts.Count == 0) return new(null, "the connection's existing wires and pins offer no free point to attach to");
                    var path = AStar(starts, nearest, out string? failure);
                    if (path is null) return new(null, failure ?? "the new pins cannot reach the connection's existing wires and pins");
                    var from = request.Existing.Points.First(p => Index(p.Node) == path[0]);
                    if (from is { Pin: { } pin, Corridor: { } }) AddEdge(pin, from.Node);
                    Commit(path, goal: -1);
                    attached = true;
                }
                if (expanded > request.Limits.MaxExpandedNodes) return new(null, Exhausted());
            }
            long spanning = SpanningTree();
            if (routed > (long)request.Limits.LengthFactor * spanning)
                return new(null, "the wires would total " + routed.ToString(CultureInfo.InvariantCulture) + " nm, more than "
                    + request.Limits.LengthFactor.ToString(CultureInfo.InvariantCulture) + " times the " + spanning.ToString(CultureInfo.InvariantCulture)
                    + " nm minimum spanning tree of its pins");
            var (segments, junctions) = Canonical();
            return new(new(segments, junctions, routed, spanning, expanded, attached), null);
        }

        // ---- grid ----

        private bool Layout(out string? reason)
        {
            reason = null;
            var r = request.Region;
            long first = CeilDiv(r.L, g), last = FloorDiv(r.R, g), top = CeilDiv(r.T, g), bottom = FloorDiv(r.B, g);
            if (last < first || bottom < top) { reason = "the page inset leaves no grid to route on"; return false; }
            if ((last - first + 1) * (bottom - top + 1) > MaxGridNodes) { reason = "the page is too large to route on its connection grid"; return false; }
            i0 = first; j0 = top; width = (int)(last - first + 1); height = (int)(bottom - top + 1);
            int n = width * height;
            flags = new byte[n]; hCross = new byte[n]; vCross = new byte[n]; distance = new int[n];
            return true;
        }

        private bool IsBlocked(int n) => (flags[n] & BlockedFlag) != 0;
        private void Block(int n) => flags[n] |= BlockedFlag;
        private void BlockH(int n) => flags[n] |= HBlockedFlag;
        private void BlockV(int n) => flags[n] |= VBlockedFlag;

        private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
        private static long CeilDiv(long a, long b) => -FloorDiv(-a, b);

        private int Index(Pt p)
        {
            if (p.X % g != 0 || p.Y % g != 0) return -1;
            long i = p.X / g - i0, j = p.Y / g - j0;
            return i < 0 || j < 0 || i >= width || j >= height ? -1 : (int)(i + j * width);
        }

        private Pt At(int n) => new((n % width + i0) * g, (n / width + j0) * g);

        private int Neighbour(int n, int d)
        {
            int i = n % width, j = n / width;
            var (dx, dy) = Directions[d];
            i += dx; j += dy;
            return i < 0 || j < 0 || i >= width || j >= height ? -1 : i + j * width;
        }

        // Nodes whose coordinates fall inside [lo, hi] on each axis.
        private IEnumerable<int> Nodes(long left, long top, long right, long bottom)
        {
            long a = Math.Max(CeilDiv(left, g) - i0, 0), b = Math.Min(FloorDiv(right, g) - i0, width - 1);
            long t = Math.Max(CeilDiv(top, g) - j0, 0), u = Math.Min(FloorDiv(bottom, g) - j0, height - 1);
            for (long j = t; j <= u; j++)
                for (long i = a; i <= b; i++)
                    yield return (int)(i + j * width);
        }

        private void Mark()
        {
            foreach (var obstacle in request.Obstacles)
            {
                var exempt = obstacle.ExemptNodes.Select(Index).Where(n => n >= 0).ToHashSet();
                var inflated = obstacle.Bounds.Inflate(c);
                foreach (int n in Nodes(inflated.L, inflated.T, inflated.R, inflated.B))
                    if (!exempt.Contains(n)) Block(n);
                // No edge meets the inflated bounds either, except along an escape corridor: a box thinner than the grid
                // (a drawing-sheet line, a thin symbol) is never stepped over, and a route leaves a pin's own symbol only
                // straight along its corridor, never sideways out of it.
                foreach (int n in Nodes(inflated.L - g, inflated.T, inflated.R, inflated.B))
                {
                    int right = Neighbour(n, 0);
                    if (right >= 0 && !(exempt.Contains(n) && exempt.Contains(right))) BlockH(n);
                }
                foreach (int n in Nodes(inflated.L, inflated.T - g, inflated.R, inflated.B))
                {
                    int down = Neighbour(n, 2);
                    if (down >= 0 && !(exempt.Contains(n) && exempt.Contains(down))) BlockV(n);
                }
            }
            // The connection's own pins are connection points too: a route reaches them only through their escapes.
            var points = request.Points.Concat(request.Segments.SelectMany(s => new[] { s.A, s.B }))
                .Concat(request.Terminals.Select(t => t.Anchor))
                .Concat(request.Existing?.Points.Where(p => p.Pin is not null).Select(p => p.Pin!.Value) ?? []);
            foreach (var p in points)
            {
                foreach (int n in Nodes(p.X - c, p.Y - c, p.X + c, p.Y + c)) Block(n);
                MarkPointEdges(p);
            }
            foreach (var segment in request.Segments) MarkSegment(segment);
            foreach (var envelope in request.Envelopes.Concat(request.RootEnvelope is { } root ? [root] : Array.Empty<Box>()))
                MarkEnvelope(envelope);
        }

        // A point between two grid nodes, further than the clearance from both (possible only when the clearance is less than
        // half the grid), still lies within the clearance of the edge joining them: that edge is blocked too, so no wire runs
        // over or beside the point. Edges with a node within the clearance are left alone: that node is blocked already, and
        // an existing wire end the route starts from keeps its first step.
        private void MarkPointEdges(Pt p)
        {
            bool Clear(Pt q) => Math.Abs(q.X - p.X) > c || Math.Abs(q.Y - p.Y) > c;
            foreach (int n in Nodes(p.X - c - g, p.Y - c, p.X + c, p.Y + c))
            {
                var q = At(n);
                if (Neighbour(n, 0) >= 0 && Clear(q) && Clear(new(q.X + g, q.Y))) BlockH(n);
            }
            foreach (int n in Nodes(p.X - c, p.Y - c - g, p.X + c, p.Y + c))
            {
                var q = At(n);
                if (Neighbour(n, 2) >= 0 && Clear(q) && Clear(new(q.X, q.Y + g))) BlockV(n);
            }
        }

        private void MarkSegment(RouteSegment s)
        {
            if (s.A == s.B) return;
            if (s.A.X == s.B.X || s.A.Y == s.B.Y)
            {
                bool vertical = s.A.X == s.B.X;
                // Work in (along, across) coordinates: `along` runs with the segment.
                long across = vertical ? s.A.X : s.A.Y;
                long lo = vertical ? Math.Min(s.A.Y, s.B.Y) : Math.Min(s.A.X, s.B.X), hi = vertical ? Math.Max(s.A.Y, s.B.Y) : Math.Max(s.A.X, s.B.X);
                // Parallel runs within the clearance: edges along the segment's direction whose line lies within c of it and
                // whose span overlaps it for a positive length.
                foreach (int n in vertical ? Nodes(across - c, lo - g, across + c, hi) : Nodes(lo - g, across - c, hi, across + c))
                {
                    var p = At(n);
                    long a0 = vertical ? p.Y : p.X, a1 = a0 + g;
                    if (Math.Min(a1, hi) - Math.Max(a0, lo) <= 0) continue;
                    if (vertical) BlockV(n); else BlockH(n);
                }
                // Crossings: grid lines perpendicular to the segment strictly inside its span.
                foreach (int n in vertical ? Nodes(across - g, lo, across, hi) : Nodes(lo, across - g, hi, across))
                {
                    var p = At(n);
                    long along = vertical ? p.Y : p.X, line = vertical ? p.X : p.Y;
                    if (along <= lo || along >= hi) continue;
                    bool near = along - lo <= c || hi - along <= c;
                    if (line == across)
                    {
                        // The node lies on the inside of the segment.
                        if (!s.Crossable || near) Block(n);
                        else flags[n] |= vertical ? CrossVFlag : CrossHFlag;
                    }
                    else if (line < across && across < line + g)
                    {
                        // The perpendicular edge leaving this node crosses the segment between two nodes.
                        if (!s.Crossable || near) { if (vertical) BlockH(n); else BlockV(n); }
                        else if (vertical) hCross[n]++; else vCross[n]++;
                    }
                }
                return;
            }
            // A diagonal segment is never crossed: every node within one grid step of it is blocked, which keeps every
            // remaining edge clear of it.
            var box = Box.Segment(s.A, s.B).Inflate(g);
            foreach (int n in Nodes(box.L, box.T, box.R, box.B))
                if (DistanceSquared(At(n), s.A, s.B) <= (double)g * g) Block(n);
        }

        private static double DistanceSquared(Pt p, Pt a, Pt b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
            t = Math.Clamp(t, 0, 1);
            double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return x * x + y * y;
        }

        // A generated label: no node inside it and no edge meeting it (they may not touch).
        private void MarkEnvelope(Box envelope)
        {
            foreach (int n in Nodes(envelope.L - g, envelope.T - g, envelope.R, envelope.B))
            {
                var p = At(n);
                if (envelope.Contains(p)) Block(n);
                if (SegmentMeets(p, new(p.X + g, p.Y), envelope, open: false)) BlockH(n);
                if (SegmentMeets(p, new(p.X, p.Y + g), envelope, open: false)) BlockV(n);
            }
        }

        private bool EdgeBlocked(int n, int d, int m) => d switch
        {
            0 => (flags[n] & HBlockedFlag) != 0, 1 => (flags[m] & HBlockedFlag) != 0, 2 => (flags[n] & VBlockedFlag) != 0, _ => (flags[m] & VBlockedFlag) != 0
        };

        private int EdgeCrossings(int n, int d, int m) => d switch
        {
            0 => hCross[n], 1 => hCross[m], 2 => vCross[n], _ => vCross[m]
        };

        private bool Crossing(int n) => (flags[n] & (CrossVFlag | CrossHFlag)) != 0;

        private bool CrossV(int n) => (flags[n] & CrossVFlag) != 0;

        private bool CrossH(int n) => (flags[n] & CrossHFlag) != 0;

        // ---- tree ----

        private void AddEdge(Pt a, Pt b)
        {
            if (a == b) return;
            // Unit steps between neighbouring grid points; the anchors of the pins are grid points too.
            var (dx, dy) = (Math.Sign(b.X - a.X), Math.Sign(b.Y - a.Y));
            var p = a;
            while (p != b)
            {
                var q = new Pt(p.X + dx * g, p.Y + dy * g);
                edges[Key(p, q)] = true;
                p = q;
            }
        }

        private (int, int) Key(Pt a, Pt b)
        {
            int x = Raw(a), y = Raw(b);
            return x < y ? (x, y) : (y, x);
        }

        // Pins may lie outside the routing region (their escape lies inside); number every grid point uniquely.
        private readonly Dictionary<Pt, int> outside = [];
        private int Raw(Pt p)
        {
            int n = Index(p);
            if (n >= 0) return n;
            if (!outside.TryGetValue(p, out int id)) outside.Add(p, id = -2 - outside.Count);
            return id;
        }

        private Pt PointOf(int raw) => raw >= 0 ? At(raw) : outside.First(o => o.Value == raw).Key;

        private void AddGoal(Pt p)
        {
            int n = Index(p);
            if (n >= 0 && !IsBlocked(n) && !Crossing(n)) goals.Add(n);
        }

        private void Commit(IReadOnlyList<int> path, int goal)
        {
            for (int k = 1; k < path.Count; k++) edges[Key(At(path[k - 1]), At(path[k]))] = true;
            routed += (path.Count - 1) * g;
            foreach (int n in path) if (!IsBlocked(n) && !Crossing(n)) goals.Add(n);
            if (goal >= 0 && !IsBlocked(goal) && !Crossing(goal)) goals.Add(goal);
        }

        // Manhattan distance of every node to the nearest goal, in grid steps (two-pass L1 distance transform), into the one
        // distance field of this search.
        private int[] Distances()
        {
            var d = distance;
            Array.Fill(d, int.MaxValue / 4);
            foreach (int n in goals) d[n] = 0;
            for (int j = 0; j < height; j++)
                for (int i = 0; i < width; i++)
                {
                    int n = i + j * width;
                    if (i > 0) d[n] = Math.Min(d[n], d[n - 1] + 1);
                    if (j > 0) d[n] = Math.Min(d[n], d[n - width] + 1);
                }
            for (int j = height - 1; j >= 0; j--)
                for (int i = width - 1; i >= 0; i--)
                {
                    int n = i + j * width;
                    if (i < width - 1) d[n] = Math.Min(d[n], d[n + 1] + 1);
                    if (j < height - 1) d[n] = Math.Min(d[n], d[n + width] + 1);
                }
            return d;
        }

        private readonly record struct Priority(long F, int Vertical, int I, int J, int D);

        private sealed class PriorityOrder : IComparer<Priority>
        {
            public static readonly PriorityOrder Instance = new();
            public int Compare(Priority a, Priority b)
            {
                int c = a.F.CompareTo(b.F);
                if (c == 0) c = a.Vertical.CompareTo(b.Vertical);
                if (c == 0) c = a.I.CompareTo(b.I);
                if (c == 0) c = a.J.CompareTo(b.J);
                return c == 0 ? a.D.CompareTo(b.D) : c;
            }
        }

        // A* from the starts to any goal node, with `h` the Manhattan distance to the nearest goal. States are (node, direction
        // of the last move); a start's direction is its escape or corridor, or None for an existing wire end, whose first move
        // is limited to `allowed`. The state of only the states the search reaches is kept, so its size follows the
        // expanded-node budget, not the page.
        private List<int>? AStar(IReadOnlyList<(int Node, int Incoming, IReadOnlyCollection<int>? Allowed)> starts, int[] h, out string? failure)
        {
            failure = null;
            var cost = new Dictionary<int, long>();
            var parent = new Dictionary<int, int>();
            var closed = new HashSet<int>();
            var open = new PriorityQueue<int, Priority>(PriorityOrder.Instance);
            var allowedAt = new Dictionary<int, IReadOnlyCollection<int>>();
            foreach (var (node, incoming, allowed) in starts)
            {
                int s = node * 5 + incoming;
                if (cost.TryGetValue(s, out long known) && known == 0) continue;
                cost[s] = 0; parent[s] = -1;
                if (allowed is not null) allowedAt[s] = allowed;
                open.Enqueue(s, new(h[node], incoming is 2 or 3 ? 1 : 0, node % width, node / width, incoming));
            }
            while (open.TryDequeue(out int s, out _))
            {
                if (!closed.Add(s)) continue;
                token.ThrowIfCancellationRequested();
                if (++expanded > request.Limits.MaxExpandedNodes) { failure = Exhausted(); return null; }
                int n = s / 5, din = s % 5;
                if (goals.Contains(n))
                {
                    var path = new List<int>();
                    for (int t = s; t >= 0; t = parent[t]) path.Add(t / 5);
                    path.Reverse();
                    return path;
                }
                for (int d = 0; d < 4; d++)
                {
                    if (din != None && d == Opposite(din)) continue;
                    // Through a crossing, straight on only.
                    if (Crossing(n) && d != din) continue;
                    if (allowedAt.TryGetValue(s, out var allowed) && !allowed.Contains(d)) continue;
                    int m = Neighbour(n, d);
                    if (m < 0 || IsBlocked(m) || EdgeBlocked(n, d, m)) continue;
                    // A crossing is entered at a right angle to the segment it lies on.
                    if ((CrossV(m) && Vertical(d)) || (CrossH(m) && !Vertical(d))) continue;
                    long step = 1 + (din != None && d != din ? 2 : 0) + 10L * EdgeCrossings(n, d, m) + (Crossing(m) ? 10 : 0);
                    int t = m * 5 + d;
                    long next = cost[s] + step;
                    if (closed.Contains(t) || (cost.TryGetValue(t, out long reached) && next >= reached)) continue;
                    cost[t] = next; parent[t] = s;
                    open.Enqueue(t, new(next + h[m], Vertical(d) ? 1 : 0, m % width, m / width, d));
                }
            }
            return null;
        }

        private string Exhausted() => "the route search expanded more than " + request.Limits.MaxExpandedNodes.ToString(CultureInfo.InvariantCulture)
            + " grid nodes";

        // ---- result ----

        // The Manhattan minimum spanning tree of the pins' escapes (and the existing part, at its nearest point).
        private long SpanningTree()
        {
            var nodes = request.Terminals.Select(t => new[] { t.Escape(g) }).ToList();
            if (request.Existing is { } existing) nodes.Add([.. existing.Points.Select(p => p.Node)]);
            long Distance(Pt[] a, Pt[] b) => a.Min(p => b.Min(q => Math.Abs(p.X - q.X) + Math.Abs(p.Y - q.Y)));
            var inTree = new bool[nodes.Count];
            var best = Enumerable.Repeat(long.MaxValue, nodes.Count).ToArray();
            best[0] = 0;
            long total = 0;
            for (int k = 0; k < nodes.Count; k++)
            {
                int pick = -1;
                for (int v = 0; v < nodes.Count; v++) if (!inTree[v] && (pick < 0 || best[v] < best[pick])) pick = v;
                inTree[pick] = true;
                total += best[pick];
                for (int v = 0; v < nodes.Count; v++) if (!inTree[v]) best[v] = Math.Min(best[v], Distance(nodes[pick], nodes[v]));
            }
            return total;
        }

        // Maximal straight segments split where anything else meets them, and a junction wherever three or more wire ends
        // and pins meet (KiCad keeps exactly such junctions when it cleans a schematic up, and never merges wires there).
        private (IReadOnlyList<(Pt Start, Pt End)> Segments, IReadOnlyList<Pt> Junctions) Canonical()
        {
            var adjacency = new Dictionary<Pt, List<int>>();
            void Link(Pt p, int d)
            {
                if (!adjacency.TryGetValue(p, out var list)) adjacency.Add(p, list = []);
                list.Add(d);
            }
            foreach (var ((a, b), _) in edges)
            {
                Pt p = PointOf(a), q = PointOf(b);
                int d = DirectionOf((Math.Sign(q.X - p.X), Math.Sign(q.Y - p.Y)));
                Link(p, d); Link(q, Opposite(d));
            }
            // Items outside the generated wires that meet a point: pins and existing wire ends.
            var external = new Dictionary<Pt, int>();
            foreach (var t in request.Terminals) external[t.Anchor] = Math.Max(external.GetValueOrDefault(t.Anchor), t.Items);
            if (request.Existing is { } existing)
                foreach (var point in existing.Points)
                {
                    var at = point.Pin ?? point.Node;
                    if (adjacency.ContainsKey(at)) external[at] = Math.Max(external.GetValueOrDefault(at), point.Items);
                }
            bool Break(Pt p)
            {
                var ds = adjacency[p];
                return ds.Count != 2 || ds[0] != Opposite(ds[1]) || external.GetValueOrDefault(p) > 0;
            }
            var segments = new List<(Pt Start, Pt End)>();
            var used = new HashSet<(Pt, int)>();
            foreach (var p in adjacency.Keys.Where(Break).OrderBy(p => p.X).ThenBy(p => p.Y))
                foreach (int d in adjacency[p].Order())
                {
                    if (used.Contains((p, d))) continue;
                    var q = p;
                    do
                    {
                        used.Add((q, d));
                        q = q.Step(Directions[d], g);
                        used.Add((q, Opposite(d)));
                    }
                    while (!Break(q));
                    segments.Add(Less(p, q) ? (p, q) : (q, p));
                }
            var junctions = adjacency.Where(p => p.Value.Count + external.GetValueOrDefault(p.Key) >= 3).Select(p => p.Key)
                .OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
            return ([.. segments.Distinct().OrderBy(s => s.Start.X).ThenBy(s => s.Start.Y).ThenBy(s => s.End.X).ThenBy(s => s.End.Y)], junctions);
        }

        private static bool Less(Pt a, Pt b) => a.X < b.X || (a.X == b.X && a.Y < b.Y);
    }
}
