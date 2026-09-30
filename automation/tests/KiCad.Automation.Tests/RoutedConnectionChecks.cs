namespace KiCad.Automation.Tests;

// The drawn form of one connection that CN-1 §7 routes (automation/design/contracts/cn1-wiring-intent.md): checked on the
// realizer's output in SchematicConnectionRealizerTests and on KiCad's own committed sheets in the PSU/CPU connected
// realization journey. Lane 2A created this file for CN-1 §7.
internal static class RoutedConnectionChecks
{
    internal readonly record struct P(long X, long Y)
    {
        public override string ToString() => "(" + X + ", " + Y + ")";
    }

    /// <summary>What is wrong with one routed connection on one sheet, or nothing:
    /// <list type="bullet">
    /// <item>every wire is straight along one axis, has length, ends on the connection grid, lies inside the route region
    /// (<paramref name="region"/>: the drawing sheet's frame interior less the clearance) and keeps the clearance from every
    /// drawing-sheet keep-out (<paramref name="keepOuts"/>, the title block);</item>
    /// <item>the wires form one tree, with no wire end on the inside of another of its wires and no two of its wires sharing
    /// a stretch, that reaches every new pin (<paramref name="terminals"/>);</item>
    /// <item>a junction sits exactly where three or more wire ends and symbols meet (a symbol counted once however many of its
    /// pins are there), and nowhere else, so KiCad keeps every junction when it cleans the sheet up; and no point joins exactly
    /// two collinear wire ends, which KiCad would merge into one wire;</item>
    /// <item>exactly one label names it, at the free end of one of its wires, facing away from that wire;</item>
    /// <item>it meets every other wire on the sheet (<paramref name="otherWires"/>) at most by crossing it at a right angle
    /// away from both wires' ends, and the existing wires of its own connection it attaches to (<paramref name="attached"/>)
    /// only at their ends.</item>
    /// </list>
    /// <paramref name="symbolsAt"/> counts, for every pin point of the sheet, the symbols with a pin there.</summary>
    public static List<string> Problems(string name, long grid, (long L, long T, long R, long B) region, IReadOnlyList<(P A, P B)> wires,
        IReadOnlyCollection<P> junctions, IReadOnlyList<(P At, (int Dx, int Dy) Facing)> labels, IReadOnlyDictionary<P, int> symbolsAt,
        IReadOnlyCollection<P> terminals, IReadOnlyList<(P A, P B)> otherWires, IReadOnlyList<(P A, P B)>? attached = null,
        IReadOnlyList<(long L, long T, long R, long B)>? keepOuts = null, long clearance = 0)
    {
        attached ??= [];
        keepOuts ??= [];
        var problems = new List<string>();
        void Problem(string text) => problems.Add(name + ": " + text);
        if (wires.Count == 0) { Problem("no wire is drawn"); return problems; }
        foreach (var (a, b) in wires)
        {
            if (a == b || (a.X != b.X && a.Y != b.Y)) Problem("wire " + a + "-" + b + " is not a straight step along one axis");
            foreach (var p in new[] { a, b })
            {
                if (p.X % grid != 0 || p.Y % grid != 0) Problem("wire end " + p + " is off the connection grid");
                if (p.X < region.L || p.X > region.R || p.Y < region.T || p.Y > region.B) Problem("wire end " + p + " lies outside the drawing sheet's frame");
            }
            foreach (var k in keepOuts)
                if (Math.Max(a.X, b.X) >= k.L - clearance && Math.Min(a.X, b.X) <= k.R + clearance && Math.Max(a.Y, b.Y) >= k.T - clearance
                    && Math.Min(a.Y, b.Y) <= k.B + clearance)
                    Problem("wire " + a + "-" + b + " runs within the clearance of the drawing sheet's title block");
        }
        // Shape: one tree reaching every new pin.
        for (int i = 0; i < wires.Count; i++)
            for (int j = 0; j < wires.Count; j++)
            {
                if (i == j) continue;
                foreach (var end in new[] { wires[j].A, wires[j].B })
                    if (Inside(end, wires[i])) Problem("wire end " + end + " lies on the inside of another wire of the connection");
                if (j > i && SharedStretch(wires[i], wires[j])) Problem("two wires of the connection share a stretch at " + wires[i].A + "-" + wires[i].B);
            }
        var ends = wires.SelectMany(w => new[] { w.A, w.B }).ToHashSet();
        var parent = ends.ToDictionary(p => p, p => p);
        P Find(P p) { while (parent[p] != p) p = parent[p] = parent[parent[p]]; return p; }
        foreach (var (a, b) in wires) parent[Find(a)] = Find(b);
        if (ends.Select(Find).Distinct().Count() != 1) Problem("its wires do not form one connected drawing");
        int points = ends.Count;
        if (wires.Count != points - 1) Problem(wires.Count + " wires between " + points + " points do not form a tree");
        foreach (var terminal in terminals)
            if (!ends.Contains(terminal)) Problem("no wire reaches new pin " + terminal);
        // Junctions exactly where three or more things meet; no two collinear wire ends alone at a point.
        foreach (var p in ends)
        {
            var exits = wires.Concat(attached).Where(w => w.A == p || w.B == p).Select(w => Direction(p, w.A == p ? w.B : w.A)).ToList();
            int items = exits.Count + 2 * attached.Count(w => Inside(p, w)) + symbolsAt.GetValueOrDefault(p);
            if (items >= 3 && !junctions.Contains(p)) Problem("no junction where " + items + " wire ends and symbols meet at " + p);
            if (items < 3 && junctions.Contains(p)) Problem("a junction at " + p + " where only " + items + " wire ends and symbols meet, which KiCad removes");
            if (items == 2 && exits.Count == 2 && exits[0] == (-exits[1].Dx, -exits[1].Dy))
                Problem("two collinear wires meet alone at " + p + ", which KiCad would merge into one");
        }
        foreach (var junction in junctions)
            if (!ends.Contains(junction)) Problem("junction " + junction + " is not at a wire end of the connection");
        // One label, at a free wire end, facing away from its wire.
        if (labels.Count != 1) Problem(labels.Count + " labels name it instead of one");
        foreach (var (at, facing) in labels)
        {
            var own = wires.Where(w => w.A == at || w.B == at).ToArray();
            if (own.Length != 1 || symbolsAt.ContainsKey(at)) { Problem("its label at " + at + " is not at the free end of one wire"); continue; }
            var from = own[0].A == at ? own[0].B : own[0].A;
            if (Direction(from, at) != facing) Problem("its label at " + at + " does not face away from its wire");
        }
        foreach (var existing in attached)
            foreach (var wire in wires)
            {
                if (SharedStretch(wire, existing)) Problem("wire " + wire.A + "-" + wire.B + " runs along an existing wire of the connection");
                else if (Intersection(wire, existing) is { } touch && touch != existing.A && touch != existing.B)
                    Problem("wire " + wire.A + "-" + wire.B + " meets an existing wire of the connection away from its ends at " + touch);
            }
        // Other wires are only crossed at right angles, away from every end.
        foreach (var other in otherWires)
            foreach (var wire in wires)
            {
                if (SharedStretch(wire, other)) { Problem("wire " + wire.A + "-" + wire.B + " runs along another wire"); continue; }
                if (Intersection(wire, other) is not { } cross) continue;
                if (cross == wire.A || cross == wire.B || cross == other.A || cross == other.B)
                    Problem("wire " + wire.A + "-" + wire.B + " touches another wire at " + cross);
            }
        return problems;
    }

    private static (int Dx, int Dy) Direction(P from, P to) => (Math.Sign(to.X - from.X), Math.Sign(to.Y - from.Y));

    private static bool Inside(P p, (P A, P B) w) =>
        p != w.A && p != w.B && Math.Min(w.A.X, w.B.X) <= p.X && p.X <= Math.Max(w.A.X, w.B.X) && Math.Min(w.A.Y, w.B.Y) <= p.Y
        && p.Y <= Math.Max(w.A.Y, w.B.Y) && (Int128)(w.B.X - w.A.X) * (p.Y - w.A.Y) == (Int128)(w.B.Y - w.A.Y) * (p.X - w.A.X);

    // Two axis-parallel or collinear wires that share more than a point.
    private static bool SharedStretch((P A, P B) a, (P A, P B) b)
    {
        Int128 Cross(P o, P p, P q) => (Int128)(p.X - o.X) * (q.Y - o.Y) - (Int128)(p.Y - o.Y) * (q.X - o.X);
        if (Cross(a.A, a.B, b.A) != 0 || Cross(a.A, a.B, b.B) != 0) return false;
        bool horizontal = a.A.Y == a.B.Y && a.A.X != a.B.X;
        long a0 = horizontal ? Math.Min(a.A.X, a.B.X) : Math.Min(a.A.Y, a.B.Y), a1 = horizontal ? Math.Max(a.A.X, a.B.X) : Math.Max(a.A.Y, a.B.Y);
        long b0 = horizontal ? Math.Min(b.A.X, b.B.X) : Math.Min(b.A.Y, b.B.Y), b1 = horizontal ? Math.Max(b.A.X, b.B.X) : Math.Max(b.A.Y, b.B.Y);
        return Math.Min(a1, b1) - Math.Max(a0, b0) > 0;
    }

    // The single point two non-collinear wires share, if any (only axis-aligned connection wires are routed; another wire may
    // be diagonal, in which case a shared point is reported by its nearest grid-free exact value only when it is integral).
    private static P? Intersection((P A, P B) a, (P A, P B) b)
    {
        Int128 rx = a.B.X - a.A.X, ry = a.B.Y - a.A.Y, sx = b.B.X - b.A.X, sy = b.B.Y - b.A.Y;
        Int128 denominator = rx * sy - ry * sx;
        if (denominator == 0) return null;
        Int128 qx = b.A.X - a.A.X, qy = b.A.Y - a.A.Y;
        Int128 t = qx * sy - qy * sx, u = qx * ry - qy * rx;
        if (denominator < 0) { denominator = -denominator; t = -t; u = -u; }
        if (t < 0 || t > denominator || u < 0 || u > denominator) return null;
        Int128 x = a.A.X * denominator + rx * t, y = a.A.Y * denominator + ry * t;
        if (x % denominator != 0 || y % denominator != 0) return new P(long.MinValue, long.MinValue);
        return new P((long)(x / denominator), (long)(y / denominator));
    }
}
