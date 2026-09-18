using System.Text.Json;
using KiCad.Automation.Model;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class InitialSchematicLayoutTests
{
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private static readonly InitialLayoutPolicy Policy = new(100, 100, 100);
    private static InitialLayoutBody Body(int id, PresentationBounds? bounds = null, PresentationPoint? fixedAnchor = null,
        Guid? group = null) => new(Id(id), Id(100), [Id(1000 + id)], bounds ?? new(-100, -100, 200, 200), fixedAnchor, group);
    private static InitialLayoutSheet Sheet(params InitialLayoutObstacle[] obstacles) => new(Id(100), new(0, 0, 2000, 2000), obstacles);

    [TestMethod]
    public void PlacesMeasuredBodiesOnGridWithoutMovingExistingOrPinnedGeometry()
    {
        var sheet = Sheet(new InitialLayoutObstacle(Id(90), new(0, 0, 500, 800)));
        var pinned = Body(1, fixedAnchor: new(1100, 1300));
        var policy = Policy with { GridNm = 200 };
        InitialLayoutBody[] bodies = [Body(3), pinned, Body(2)];
        string original = JsonSerializer.Serialize(new { sheet, bodies });
        var result = InitialSchematicLayout.Propose([sheet], bodies, policy);
        Assert.IsTrue(result.CanPropose); Assert.IsTrue(result.RequiresVisualReview);
        Assert.AreEqual(pinned.FixedAnchor, result.Placements!.Single(p => p.BodyId == pinned.Id).Anchor);
        foreach (var placement in result.Placements!.Where(p => !p.Fixed))
        {
            Assert.AreEqual(0L, placement.Anchor.XNm % policy.GridNm);
            Assert.AreEqual(0L, placement.Anchor.YNm % policy.GridNm);
        }
        RequireFits(sheet, result.Placements!, policy);
        Assert.AreEqual(original, JsonSerializer.Serialize(new { sheet, bodies }));
    }

    [TestMethod]
    public void EnumerationDoesNotChangeProposalsAndRepeatedOccurrencesShareOnePlacement()
    {
        var sheet = Sheet(new(Id(90), new(0, 0, 500, 800)), new(Id(91), new(1200, 800, 1900, 1900)));
        InitialLayoutBody[] bodies = [Body(3), Body(1) with { SymbolOccurrences = [Id(1001), Id(2001)] }, Body(2)];
        var first = InitialSchematicLayout.Propose([sheet], bodies, Policy);
        var second = InitialSchematicLayout.Propose([sheet with { Obstacles = sheet.Obstacles.Reverse().ToArray() }],
            bodies.Reverse().Select(b => b with { SymbolOccurrences = b.SymbolOccurrences.Reverse().ToArray() }).ToArray(), Policy);
        Assert.IsTrue(first.CanPropose);
        Assert.AreEqual(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.HasCount(2, first.Placements!.Single(p => p.BodyId == Id(1)).SymbolOccurrences);
    }

    [TestMethod]
    public void GroupAffinityUsesAvailableSpaceNearItsPinnedMember()
    {
        var sheet = Sheet();
        var pinned = Body(1, fixedAnchor: new(1400, 1400), group: Id(800));
        var near = Body(2, group: Id(800));
        var result = InitialSchematicLayout.Propose([sheet], [pinned, near], Policy);
        Assert.IsTrue(result.CanPropose);
        var placed = result.Placements!.Single(p => p.BodyId == near.Id);
        Assert.IsTrue(Math.Abs(placed.Anchor.XNm - 1400) + Math.Abs(placed.Anchor.YNm - 1400) < 1000);
        RequireFits(sheet, result.Placements!, Policy);
    }

    [TestMethod]
    public void NoSpaceOrPinnedConflictNeverReturnsAPartialLayout()
    {
        var sheet = Sheet();
        var result = InitialSchematicLayout.Propose([sheet], [Body(1), Body(2, new(0, 0, 4000, 4000))], Policy);
        Assert.IsFalse(result.CanPropose); Assert.IsNull(result.Placements);
        Assert.AreEqual("no_free_region", result.Issues.Single().Code);
        result = InitialSchematicLayout.Propose([sheet], [Body(1, fixedAnchor: new(0, 0))], Policy);
        Assert.AreEqual("pinned_page_overflow", result.Issues.Single().Code);
        sheet = Sheet(new InitialLayoutObstacle(Id(90), new(0, 0, 800, 800)));
        result = InitialSchematicLayout.Propose([sheet], [Body(1, fixedAnchor: new(500, 500))], Policy);
        Assert.AreEqual("pinned_obstacle_overlap", result.Issues.Single().Code);
        Assert.AreEqual(Id(90), result.Issues.Single().ObstacleIds.Single());
    }

    [TestMethod]
    public void InvalidMeasurementOrIdentityFailsBeforeProducingAProposal()
    {
        var sheet = Sheet(); var body = Body(1);
        foreach (var bad in new[] { body with { Id = Guid.Empty }, body with { SheetId = Id(99) },
            body with { SymbolOccurrences = [] }, body with { SymbolOccurrences = [Id(42), Id(42)] },
            body with { RelativeBounds = new(0, 0, 0, 100) }, body with { RelativeBounds = new(10, 0, 0, 100) },
            body with { FixedAnchor = new(1, 100) }, body with { FunctionalGroupId = Guid.Empty } })
            Assert.ThrowsExactly<AutomationException>(() => InitialSchematicLayout.Propose([sheet], [bad], Policy));
        foreach (var bad in new[] { Policy with { GridNm = 0 }, Policy with { GridNm = 101 },
            Policy with { ClearanceNm = -1 }, Policy with { PageInsetNm = 3000 } })
            Assert.ThrowsExactly<AutomationException>(() => InitialSchematicLayout.Propose([sheet], [body], bad));
        Assert.ThrowsExactly<AutomationException>(() => InitialSchematicLayout.Propose([sheet, sheet], [body], Policy));
        Assert.ThrowsExactly<AutomationException>(() => InitialSchematicLayout.Propose([sheet], [body, body], Policy));
        Assert.ThrowsExactly<AutomationException>(() => InitialSchematicLayout.Propose([Sheet(new InitialLayoutObstacle(body.Id, new(0, 0, 100, 100)))], [body], Policy));
        Assert.ThrowsExactly<AutomationException>(() => InitialSchematicLayout.Propose(
            [new(Id(100), new(long.MaxValue - 100, 0, long.MaxValue, 100), [])], [body], Policy));
        Assert.ThrowsExactly<OperationCanceledException>(() => InitialSchematicLayout.Propose([sheet], [body], Policy, new(true)));
    }

    [TestMethod]
    public void ExistingOverlapDoesNotPreventUsingAnotherRegionAndNegativePagesStayOnGrid()
    {
        var sheet = new InitialLayoutSheet(Id(100), new(-1000, -1000, 1000, 1000),
            [new(Id(80), new(-800, -800, -200, -200)), new(Id(81), new(-600, -600, 0, 0))]);
        var result = InitialSchematicLayout.Propose([sheet], [Body(1), Body(2)], Policy);
        Assert.IsTrue(result.CanPropose); RequireFits(sheet, result.Placements!, Policy);
        Assert.IsTrue(result.Placements!.Any(p => p.Anchor.XNm < 0 || p.Anchor.YNm < 0));
    }

    [TestMethod]
    public void SmallGridSearchAgreesWithAnIndependentExhaustiveOccupancyOracle()
    {
        var boxes = (from left in Enumerable.Range(0, 4) from top in Enumerable.Range(0, 4)
            from width in Enumerable.Range(1, 4 - left) from height in Enumerable.Range(1, 4 - top)
            select new PresentationBounds(left * 100, top * 100, (left + width) * 100, (top + height) * 100)).ToArray();
        // Deterministic sampling covers disjoint, touching, overlapping and enclosing obstacles.
        foreach (int index in Enumerable.Range(0, boxes.Length))
        foreach (int gap in new[] { 0, 100 })
        {
            var obstacles = new[] { new InitialLayoutObstacle(Id(80), boxes[index]), new InitialLayoutObstacle(Id(81), boxes[(index * 37 + 11) % boxes.Length]) };
            var sheet = new InitialLayoutSheet(Id(100), new(0, 0, 400, 400), obstacles);
            var body = Body(1, new(-100, 0, 100, 100));
            bool expected = Enumerable.Range(0, 5).Any(x => Enumerable.Range(0, 5).Any(y =>
            {
                var bounds = new PresentationBounds(x * 100 - 100, y * 100, x * 100 + 100, y * 100 + 100);
                return bounds.LeftNm >= 0 && bounds.TopNm >= 0 && bounds.RightNm <= 400 && bounds.BottomNm <= 400
                    && obstacles.All(o => Separated(bounds, o.Bounds, gap));
            }));
            var policy = new InitialLayoutPolicy(100, gap, 0);
            var actual = InitialSchematicLayout.Propose([sheet], [body], policy);
            Assert.AreEqual(expected, actual.CanPropose, $"case {index}, gap {gap}");
            if (actual.CanPropose) RequireFits(sheet, actual.Placements!, policy);
        }
    }

    private static bool Separated(PresentationBounds a, PresentationBounds b, long gap) =>
        a.RightNm + gap <= b.LeftNm || b.RightNm + gap <= a.LeftNm
        || a.BottomNm + gap <= b.TopNm || b.BottomNm + gap <= a.TopNm;
    private static void RequireFits(InitialLayoutSheet sheet, IReadOnlyList<InitialLayoutPlacement> placements, InitialLayoutPolicy policy)
    {
        var page = sheet.AvailableBounds;
        foreach (var p in placements)
        {
            Assert.IsTrue(p.Bounds.LeftNm >= page.LeftNm + policy.PageInsetNm && p.Bounds.TopNm >= page.TopNm + policy.PageInsetNm
                && p.Bounds.RightNm <= page.RightNm - policy.PageInsetNm && p.Bounds.BottomNm <= page.BottomNm - policy.PageInsetNm);
            Assert.IsTrue(sheet.Obstacles.All(o => Separated(p.Bounds, o.Bounds, policy.ClearanceNm)));
            Assert.IsTrue(placements.Where(other => other.BodyId != p.BodyId).All(other => Separated(p.Bounds, other.Bounds, policy.ClearanceNm)));
        }
    }
}
