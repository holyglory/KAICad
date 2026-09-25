using KiCad.Automation.Native;
using static KiCad.Automation.Native.SchematicConnectionRealizer;

namespace KiCad.Automation.Tests;

// CN-1 §7 grid router (SchematicOrthogonalRouter) on synthetic geometry. These unit tests exist because the router is an
// isolated algorithm whose rules (costs, tie-breaks, blocking, crossings, junctions, budgets) need exact control over the
// geometry that no live sheet gives; the end-to-end proof is the psu-cpu-connected journey
// (NativeXmlComponentCreationJourney.VerifyPsuCpuConnectedRealization), which draws the PSU/CPU fixture with these routes in
// KiCad, and SchematicConnectionRealizerTests.ThePsuCpuSheetsAreWiredWhereARouteFits, which replays that editor's
// measurements. Every must-catch case has a false-positive guard next to it. Lane 2A created this file for CN-1 §7.
[TestClass]
public sealed class SchematicOrthogonalRouterTests
{
    private const long G = 1_270_000, C = 600_000;
    private static readonly Box Page = new(-100 * G, -100 * G, 100 * G, 100 * G);

    private static RouteTerminal Pin(int id, long x, long y, (int, int) outward) =>
        new(new Guid(id, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]), new(x * G, y * G), outward);

    private static OrthogonalRouteRequest Request(IEnumerable<RouteTerminal> terminals, long rootStub = G, Box? envelope = null,
        RouteExistingPart? existing = null, IReadOnlyList<RouteObstacle>? obstacles = null, IReadOnlyList<Pt>? points = null,
        IReadOnlyList<RouteSegment>? segments = null, IReadOnlyList<Box>? envelopes = null, SchematicRoutingLimits? limits = null) =>
        new(G, C, Page, SchematicOrthogonalRouter.Order(terminals, G), rootStub, envelope, existing, obstacles ?? [], points ?? [], segments ?? [],
            envelopes ?? [], limits ?? SchematicRoutingLimits.Contract);

    private static OrthogonalRoute Routed(OrthogonalRouteRequest request)
    {
        var result = SchematicOrthogonalRouter.Route(request);
        Assert.IsNotNull(result.Route, result.FallbackReason);
        Assert.IsNull(result.FallbackReason);
        return result.Route;
    }

    private static string Fallback(OrthogonalRouteRequest request)
    {
        var result = SchematicOrthogonalRouter.Route(request);
        Assert.IsNull(result.Route, "The route was expected to fall back.");
        Assert.IsNotNull(result.FallbackReason);
        return result.FallbackReason;
    }

    private static (Pt, Pt) S(long x0, long y0, long x1, long y1) => (new(x0 * G, y0 * G), new(x1 * G, y1 * G));

    private static Box Grid(long l, long t, long r, long b) => new(l * G, t * G, r * G, b * G);

    [TestMethod]
    public void TwoFacingPinsAreJoinedByOneStraightWire()
    {
        var route = Routed(Request([Pin(1, 0, 0, (1, 0)), Pin(2, 10, 0, (-1, 0))]));
        CollectionAssert.AreEqual(new[] { S(0, 0, 10, 0) }, route.Segments.ToArray(), "Both escapes and the path merge into one maximal wire.");
        Assert.IsEmpty(route.Junctions);
        Assert.AreEqual(8 * G, route.RoutedLengthNm, "Only the path between the escapes counts, not the escapes.");
        Assert.AreEqual(8 * G, route.SpanningTreeLengthNm);
    }

    [TestMethod]
    public void EveryBendCostsTwoSoTheRouteTurnsOnce()
    {
        // From (10,9) heading up to the root's escape (0,1): up then left turns once (cost 8 + 10 + 2); left first would turn
        // twice (cost 22).
        var route = Routed(Request([Pin(1, 0, 0, (0, 1)), Pin(2, 10, 10, (0, -1))]));
        CollectionAssert.AreEqual(new[] { S(0, 0, 0, 1), S(0, 1, 10, 1), S(10, 1, 10, 10) }, route.Segments.ToArray());
        Assert.IsEmpty(route.Junctions, "Bends need no junction.");
    }

    [TestMethod]
    public void EqualCostsPreferHorizontalMovesThenLowerXThenLowerY()
    {
        // A keep-out straight between the pins leaves two mirror-image detours of equal cost; the tie-break takes the one on
        // the lower x side. Guard: mirrored, the same tie-break still takes the lower x side.
        var wall = new RouteObstacle(Grid(-3, 5, 3, 15), []);
        var route = Routed(Request([Pin(1, 0, 0, (0, 1)), Pin(2, 0, 20, (0, -1))], obstacles: [wall]));
        Assert.IsTrue(route.Segments.All(s => s.Start.X <= 0 && s.End.X <= 0), "The detour runs on the lower x side: " + Describe(route));
        Assert.IsTrue(route.Segments.Any(s => s.Start.X < 0), "It does detour.");
        var mirrored = Routed(Request([Pin(1, 0, 20, (0, -1)), Pin(2, 0, 0, (0, 1))], obstacles: [wall]));
        Assert.IsTrue(mirrored.Segments.All(s => s.Start.X <= 0 && s.End.X <= 0), Describe(mirrored));
        // The same inputs in any order give the same route.
        CollectionAssert.AreEqual(route.Segments.ToArray(), Routed(Request([Pin(2, 0, 20, (0, -1)), Pin(1, 0, 0, (0, 1))], obstacles: [wall])).Segments.ToArray());
    }

    [TestMethod]
    public void AForeignWireIsCrossedOnlyAtARightAngleAwayFromItsEnds()
    {
        var pins = new[] { Pin(1, 0, 0, (1, 0)), Pin(2, 10, 0, (-1, 0)) };
        // A foreign wire across the straight path: crossing it (10) is cheaper than going round it (three bends and eight
        // more steps), so the route crosses it, straight, with no junction there.
        var across = new RouteSegment(new(5 * G, -3 * G), new(5 * G, 3 * G), true);
        var crossed = Routed(Request(pins, segments: [across]));
        CollectionAssert.AreEqual(new[] { S(0, 0, 10, 0) }, crossed.Segments.ToArray());
        Assert.IsEmpty(crossed.Junctions, "A crossing joins nothing.");
        // Must-catch: a bus, or an existing wire of the same connection, is never crossed; the route goes round it.
        var bus = Routed(Request(pins, segments: [across with { Crossable = false }]));
        Assert.IsFalse(bus.Segments.Any(s => s.Start.Y == 0 && s.End.Y == 0 && s.Start.X < 5 * G && s.End.X > 5 * G), Describe(bus));
        // Must-catch: never through a foreign wire's end: a wire ending on the path's line is gone round.
        var ending = Routed(Request(pins, segments: [new(new(5 * G, 0), new(5 * G, 10 * G), true)]));
        Assert.IsFalse(ending.Segments.Any(s => s.Start.Y == 0 && s.End.Y == 0 && s.Start.X < 5 * G && s.End.X > 5 * G), Describe(ending));
        // Must-catch: a crossing within the clearance of a foreign wire's end is refused even between grid nodes.
        var near = new RouteSegment(new(5 * G + G / 2, -C + 100), new(5 * G + G / 2, 10 * G), true);
        var avoided = Routed(Request(pins, segments: [near]));
        Assert.IsFalse(avoided.Segments.Any(s => s.Start.Y == 0 && s.End.Y == 0 && s.Start.X < 6 * G && s.End.X > 5 * G), Describe(avoided));
        // Guard: the same off-grid wire starting further away is crossed between two nodes.
        var between = Routed(Request(pins, segments: [near with { A = new(5 * G + G / 2, -3 * G) }]));
        CollectionAssert.AreEqual(new[] { S(0, 0, 10, 0) }, between.Segments.ToArray());
        // Must-catch: a route never ends, turns or branches on a foreign wire. A third pin straight above the crossing joins the
        // tree elsewhere, although the crossing node is the tree's nearest node.
        var branch = Routed(Request([.. pins, Pin(3, 5, -6, (0, 1))], segments: [across]));
        Assert.IsFalse(branch.Segments.SelectMany(s => new[] { s.Start, s.End }).Any(p => p.X == 5 * G && p.Y > -3 * G && p.Y < 3 * G),
            "No wire end, bend or junction lies on the foreign wire: " + Describe(branch));
        Assert.IsFalse(branch.Junctions.Any(j => j.X == 5 * G), Describe(branch));
    }

    [TestMethod]
    public void RunsAlongAForeignWireWithinTheClearanceAreBlocked()
    {
        var pins = new[] { Pin(1, 0, 0, (1, 0)), Pin(2, 10, 0, (-1, 0)) };
        // A foreign wire half a clearance above the straight path (off the grid): the path may not run beside it.
        var beside = Routed(Request(pins, segments: [new(new(3 * G, -C), new(7 * G, -C), true)]));
        Assert.IsFalse(beside.Segments.Any(s => s.Start.Y == 0 && s.End.Y == 0 && s.Start.X < 7 * G && s.End.X > 3 * G), Describe(beside));
        // Guard: one grid above, it is no hindrance.
        CollectionAssert.AreEqual(new[] { S(0, 0, 10, 0) },
            Routed(Request(pins, segments: [new(new(3 * G, -G), new(7 * G, -G), true)])).Segments.ToArray());
    }

    [TestMethod]
    public void ObstaclesAreInflatedByTheClearanceExceptAPinsOwnEscape()
    {
        var pins = new[] { Pin(1, 0, 0, (1, 0)), Pin(2, 10, 0, (-1, 0)) };
        // Must-catch: an obstacle one clearance below the path line blocks it (its inflated bounds reach the line).
        var below = Routed(Request(pins, obstacles: [new(new(3 * G, C, 7 * G, 5 * G), [])]));
        Assert.IsFalse(below.Segments.Any(s => s.Start.Y == 0 && s.End.Y == 0 && s.Start.X < 7 * G && s.End.X > 3 * G), Describe(below));
        // Guard: 100 nm further away it does not.
        CollectionAssert.AreEqual(new[] { S(0, 0, 10, 0) },
            Routed(Request(pins, obstacles: [new(new(3 * G, C + 100, 7 * G, 5 * G), [])])).Segments.ToArray());
        // A pin inside its own symbol's bounds leaves through its escape corridor; without that exemption it cannot leave.
        // Here the first pin's own symbol (a field in front of it) reaches its escape node.
        var owner = new Box(-5 * G, -2 * G, G, 2 * G);
        Routed(Request(pins, obstacles: [new(owner, [new(0, 0), new(G, 0)])]));
        StringAssert.Contains(Fallback(Request(pins, obstacles: [new(owner, [])])), "no free grid node");
    }

    [TestMethod]
    public void ABranchOnTheInsideOfAWireGetsAJunctionAndSplitsIt()
    {
        // The root (0,0) and (10,0) face each other; (5,5) points up and lands on the middle of their wire.
        var route = Routed(Request([Pin(1, 0, 0, (1, 0)), Pin(2, 10, 0, (-1, 0)), Pin(3, 5, 5, (0, -1))]));
        CollectionAssert.AreEqual(new[] { new Pt(5 * G, 0) }, route.Junctions.ToArray());
        CollectionAssert.AreEquivalent(new[] { S(0, 0, 5, 0), S(5, 0, 10, 0), S(5, 0, 5, 5) }, route.Segments.ToArray(),
            "Wires are split at the junction, so KiCad's clean-up finds nothing to merge or break.");
    }

    [TestMethod]
    public void TheRootsLabelStubIsPartOfTheTree()
    {
        // The root carries its label at the end of a three-grid stub; the second pin joins the stub's inside with a junction.
        var envelope = Grid(3, -1, 8, 1) with { L = 3 * G - 150_000 };
        var route = Routed(Request([Pin(1, 0, 0, (1, 0)), Pin(2, 2, 6, (0, -1))], rootStub: 3 * G, envelope: envelope));
        Assert.IsTrue(route.Segments.Any(s => s.End == new Pt(3 * G, 0)), "The stub reaches the label: " + Describe(route));
        Assert.AreEqual(1, route.Junctions.Count, Describe(route));
        var junction = route.Junctions[0];
        Assert.AreEqual(0, junction.Y);
        Assert.IsTrue(junction.X is > 0 and < 3 * G, "The branch lands on the inside of the stub, before the label.");
        // Must-catch: nothing lands on the label's own anchor or runs through the label.
        Assert.IsFalse(route.Segments.Any(s => s.Start.X > 3 * G || s.End.X > 3 * G), Describe(route));
    }

    [TestMethod]
    public void TheNearestRemainingPinIsJoinedFirst()
    {
        // Routing order is by escape x; pin 3 is nearer to the root than pin 2 and is joined first, so pin 2 lands on pin 3's
        // path rather than on the root's escape.
        var route = Routed(Request([Pin(1, 0, 0, (0, -1)), Pin(3, 2, -3, (-1, 0)), Pin(2, 10, -6, (-1, 0))]));
        // Joined first, pin 2 would run to the root's escape, and pin 3 would land on it at (0,-3) instead.
        CollectionAssert.AreEqual(new[] { new Pt(G, -3 * G) }, route.Junctions.ToArray(), "Pin 2 lands on pin 3's escape: " + Describe(route));
    }

    [TestMethod]
    public void BudgetsSendARouteBackToLabelStubs()
    {
        var pins = new[] { Pin(1, 0, 0, (1, 0)), Pin(2, 10, 0, (-1, 0)) };
        StringAssert.Contains(Fallback(Request(pins, limits: new(5, 4))), "expanded more than 5 grid nodes");
        // Guard: enough nodes route it.
        Routed(Request(pins, limits: new(1000, 4)));
        // A wall that forces a long detour: 8 grids apart, the detour runs far beyond four times that.
        var wall = new RouteObstacle(Grid(4, -60, 6, 60), []);
        StringAssert.Contains(Fallback(Request(pins, obstacles: [wall])), "more than 4 times");
        // Guard: a larger factor accepts the same detour.
        Routed(Request(pins, obstacles: [wall], limits: new(200_000, 40)));
    }

    [TestMethod]
    public void PinsOffTheGridOrOutsideThePageInsetAreNotRouted()
    {
        StringAssert.Contains(Fallback(Request([Pin(1, 0, 0, (1, 0)), new(Guid.NewGuid(), new(10 * G + 50_000, 0), (-1, 0))])), "not on the");
        StringAssert.Contains(Fallback(Request([Pin(1, 0, 0, (1, 0)), Pin(2, 100, 0, (1, 0))])), "outside the page inset");
    }

    [TestMethod]
    public void GeneratedLabelsAndDiagonalWiresAreNeverCrossed()
    {
        var pins = new[] { Pin(1, 0, 0, (1, 0)), Pin(2, 10, 0, (-1, 0)) };
        var label = Routed(Request(pins, envelopes: [new(4 * G, -G / 3, 6 * G, G / 3)]));
        Assert.IsFalse(label.Segments.Any(s => s.Start.Y == 0 && s.End.Y == 0 && s.Start.X < 6 * G && s.End.X > 4 * G), Describe(label));
        var diagonal = Routed(Request(pins, segments: [new(new(4 * G, -2 * G), new(6 * G, 2 * G), true)]));
        Assert.IsFalse(diagonal.Segments.Any(s => s.Start.Y == 0 && s.End.Y == 0 && s.Start.X < 6 * G && s.End.X > 4 * G), Describe(diagonal));
        // Foreign points block every node within the clearance.
        var point = Routed(Request(pins, points: [new(5 * G, C)]));
        Assert.IsFalse(point.Segments.Any(s => s.Start.Y == 0 && s.End.Y == 0 && s.Start.X < 5 * G && s.End.X > 5 * G), Describe(point));
        CollectionAssert.AreEqual(new[] { S(0, 0, 10, 0) }, Routed(Request(pins, points: [new(5 * G, C + 100)])).Segments.ToArray());
        // Must-catch: this clearance is less than half the grid, so a point midway between two grid nodes is further than the
        // clearance from both; the edge between them still passes over it and is not used.
        var midway = Routed(Request(pins, points: [new(5 * G + G / 2, 0)]));
        Assert.IsFalse(midway.Segments.Any(s => s.Start.Y == 0 && s.End.Y == 0 && s.Start.X <= 5 * G && s.End.X >= 6 * G), Describe(midway));
        // Guard: the same point just beyond the clearance of that edge leaves the straight wire in place.
        CollectionAssert.AreEqual(new[] { S(0, 0, 10, 0) }, Routed(Request(pins, points: [new(5 * G + G / 2, C + 100)])).Segments.ToArray());
    }

    [TestMethod]
    public void AnExistingConnectionIsReachedAtAWireEndWithoutContinuingThatWire()
    {
        // The connection's existing wire runs down from (20,0); its free top end (20,0) is where the new pins attach. Going on
        // up from it would make two collinear wires meet alone, which KiCad merges (changing the existing wire), so the route
        // arrives from the side.
        var existingWire = new RouteSegment(new(20 * G, 0), new(20 * G, 5 * G), false);
        var end = new RouteAttachPoint(new(20 * G, 0), null, null, [(0, 1)], 1);
        var pins = new[] { Pin(1, 0, -4, (1, 0)), Pin(2, 10, -4, (-1, 0)) };
        var route = Routed(Request(pins, existing: new("zz", [end]), segments: [existingWire]));
        Assert.IsTrue(route.AttachedExisting);
        Assert.IsTrue(route.Segments.Any(s => s.Start == end.Node || s.End == end.Node), Describe(route));
        var last = route.Segments.Single(s => s.Start == end.Node || s.End == end.Node);
        Assert.AreEqual(0, last.Start.Y - last.End.Y, "The attaching wire arrives horizontally: " + Describe(route));
        CollectionAssert.DoesNotContain(route.Junctions.ToArray(), end.Node, "A bend at a wire end needs no junction.");
        // With the side ways blocked, it cannot attach.
        StringAssert.Contains(Fallback(Request(pins, existing: new("zz", [end]), segments: [existingWire],
            obstacles: [new(Grid(16, -2, 19, 2), []), new(Grid(21, -2, 24, 2), [])])), "cannot reach");
    }

    [TestMethod]
    public void AnExistingPinIsReachedThroughItsCorridorWithAJunctionOnThePin()
    {
        // The connection's existing pin (20,0) points right and already has a wire going down; its corridor is (20,0)-(21,0).
        var pin = new RouteAttachPoint(new(21 * G, 0), new(20 * G, 0), (1, 0), [(0, 1)], 2);
        var route = Routed(Request([Pin(1, 0, -4, (1, 0)), Pin(2, 10, -4, (-1, 0))], existing: new("zz", [pin]),
            segments: [new(new(20 * G, 0), new(20 * G, 5 * G), false)]));
        Assert.IsTrue(route.Segments.Any(s => (s.Start == new Pt(20 * G, 0)) || s.End == new Pt(20 * G, 0)), Describe(route));
        CollectionAssert.Contains(route.Junctions.ToArray(), new Pt(20 * G, 0), "Pin, its wire and the corridor meet: " + Describe(route));
    }

    private static string Describe(OrthogonalRoute route) => string.Join(" ", route.Segments.Select(s =>
        "(" + s.Start.X / G + "," + s.Start.Y / G + ")-(" + s.End.X / G + "," + s.End.Y / G + ")"))
        + " junctions " + string.Join(" ", route.Junctions.Select(j => "(" + j.X / G + "," + j.Y / G + ")"));
}
