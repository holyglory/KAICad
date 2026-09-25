using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PresentationVerificationTests
{
    private static readonly PresentationPolicy Policy = new(1, 3);
    private static PresentationSheet Sheet() => new([Guid.NewGuid()], new(0, 0, 200_000_000, 300_000_000), [], [], [], []);
    private static PresentationSnapshot Snapshot(params PresentationSheet[] sheets) => new(Guid.NewGuid(), new("fixture-epoch", 5), true, sheets);
    private static PresentationObject Text(Guid id, bool visible = true) => new(id,
        PresentationObjectKind.ReferenceDesignator, new(10, 10, 20, 20), visible, 1.27m, "U1");

    [TestMethod]
    public void TextOverflowUsesTextBoundsNotJustItsContainer()
    {
        var item = Text(Guid.NewGuid()) with { Kind = PresentationObjectKind.Text,
            TextBounds = new(10, 10, 20, 400_000_000) };
        var report = PresentationVerifier.Verify(Snapshot(Sheet() with { Objects = [item] }), Policy);
        Assert.AreEqual(2, report.Findings.Count);
        Assert.IsTrue(report.Findings.Any(f => f.Rule == "text_container_overflow"));
        Assert.IsTrue(report.Findings.Any(f => f.Rule == "page_overflow"));
        Assert.IsTrue(report.Findings.All(f => f.Bounds == item.TextBounds && f.ObjectIds.Single() == item.Id));
        foreach (var valid in new[] { item with { Visible = false }, item with { Text = "" },
            item with { TextBounds = item.FullBounds }, item with { TextBounds = null } })
            Assert.IsTrue(PresentationVerifier.Verify(Snapshot(Sheet() with { Objects = [valid] }), Policy).Clear);
    }

    [TestMethod]
    public void PageImageFontAndDesignatorDefectsHaveExactTargets()
    {
        Guid image = Guid.NewGuid(), hidden = Guid.NewGuid(), tiny = Guid.NewGuid(), missing = Guid.NewGuid();
        var sheet = Sheet() with
        {
            Objects = [new(image, PresentationObjectKind.Image, new(-1, 0, 20, 20), true),
                Text(hidden, false), Text(tiny) with { TextHeightMm = 0.2m }],
            RequiredDesignators = [hidden, tiny, missing]
        };
        var snapshot = Snapshot(sheet);
        var report = PresentationVerifier.Verify(snapshot, Policy);
        Assert.IsFalse(report.Clear);
        Assert.AreEqual(snapshot.Revision, report.Revision);
        var font = report.Findings.Single(f => f.Rule == "text_size");
        Assert.AreEqual(0.2m, font.Measured); Assert.AreEqual(1m, font.Limit);
        CollectionAssert.AreEqual(new[] { tiny }, font.ObjectIds.ToArray());
        Assert.IsTrue(report.Findings.Any(f => f.Rule == "page_overflow" && f.ObjectIds.Contains(image)));
        Assert.IsTrue(report.Findings.Any(f => f.Rule == "image_cropped" && f.ObjectIds.Contains(image)));
        Assert.IsTrue(report.Findings.Any(f => f.Rule == "designator_not_visible" && f.ObjectIds.Contains(hidden)));
        Assert.IsTrue(report.Findings.Any(f => f.Rule == "designator_missing" && f.ObjectIds.Contains(missing)));
        Assert.IsTrue(report.Findings.All(f => f.SheetPath == sheet.SheetPath[0].ToString("D")));
    }

    [TestMethod]
    public void InternalImageClipAndPartialDesignatorAreNotMissed()
    {
        var image = new PresentationObject(Guid.NewGuid(), PresentationObjectKind.Image, new(10, 10, 30, 30), true,
            ClipBounds: new(10, 10, 25, 30));
        var reference = Text(Guid.NewGuid()) with { ClipBounds = new(10, 10, 19, 20) };
        var report = PresentationVerifier.Verify(Snapshot(Sheet() with
        {
            Objects = [image, reference], RequiredDesignators = [reference.Id]
        }), Policy);
        Assert.IsTrue(report.Findings.Any(f => f.Rule == "image_cropped"));
        Assert.IsTrue(report.Findings.Any(f => f.Rule == "designator_not_visible"));
        Assert.IsFalse(report.Findings.Any(f => f.Rule == "page_overflow"));
    }

    [TestMethod]
    public void PageEdgesFontLimitsAndRepeatedSheetsAreValid()
    {
        var first = Sheet();
        var reference = Text(Guid.NewGuid()) with { FullBounds = first.PageBounds, TextHeightMm = 1 };
        first = first with { Objects = [reference], RequiredDesignators = [reference.Id] };
        var second = first with { SheetPath = [Guid.NewGuid()], Objects = [reference with { TextHeightMm = 3 }] };
        Assert.IsTrue(PresentationVerifier.Verify(Snapshot(first, second), Policy).Clear);
        Assert.ThrowsExactly<AutomationException>(() => PresentationVerifier.Verify(Snapshot(first, first), Policy));
    }

    [TestMethod]
    public void MissingMetricsAndIncompleteExtractionCannotPass()
    {
        var sheet = Sheet() with { Objects = [Text(Guid.NewGuid()) with { TextHeightMm = null }] };
        var report = PresentationVerifier.Verify(Snapshot(sheet), Policy);
        Assert.IsFalse(report.Clear); Assert.IsFalse(report.CoverageComplete);
        Assert.AreEqual("text_metrics_missing", report.Findings.Single().Rule);
        report = PresentationVerifier.Verify(Snapshot(Sheet()) with { CoverageComplete = false }, Policy);
        Assert.IsFalse(report.Clear);
        Assert.AreEqual("coverage_incomplete", report.Findings.Single().Rule);
    }

    [TestMethod]
    public void MoreThanTwoCrossingsWarnWithLocationAndSignalIdentity()
    {
        string signal = "main-signal";
        var wire = new PresentationWire(Guid.NewGuid(), signal, new(0, 10), new(100, 10));
        var crossing = Enumerable.Range(1, 3).Select(i => new PresentationWire(Guid.NewGuid(), "crossing-" + i, new(i * 20, 0), new(i * 20, 20))).ToArray();
        var sheet = Sheet() with { Wires = [wire, .. crossing] };
        var report = PresentationVerifier.Verify(Snapshot(sheet), Policy);
        var finding = report.Findings.Single();
        Assert.AreEqual("excessive_crossings", finding.Rule);
        Assert.AreEqual(signal, finding.SignalKey);
        Assert.AreEqual(3m, finding.Measured); Assert.AreEqual(2m, finding.Limit);
        Assert.AreEqual(new PresentationBounds(20, 10, 60, 10), finding.Bounds);
        Assert.AreEqual(4, finding.ObjectIds.Count);
        Assert.IsNotNull(finding.Locations);
        Assert.AreEqual(3, finding.Locations.Count);
        for (int i = 0; i < 3; ++i)
        {
            Assert.AreEqual(new PresentationBounds((i + 1) * 20, 10, (i + 1) * 20, 10), finding.Locations[i].Bounds);
            CollectionAssert.AreEquivalent(new[] { wire.Id, crossing[i].Id }, finding.Locations[i].ObjectIds.ToArray());
        }
        Assert.IsTrue(PresentationVerifier.Verify(Snapshot(sheet with { Wires = [wire, .. crossing.Take(2)] }), Policy).Clear);
        Assert.IsTrue(PresentationVerifier.Verify(Snapshot(sheet), Policy with { MaximumCrossingsPerSignal = 3 }).Clear);
    }

    [TestMethod]
    public void SegmentationSameSignalAndJunctionsDoNotInflateCrossings()
    {
        string firstNet = "first", secondNet = "second";
        PresentationWire Wire(string net, long x1, long y1, long x2, long y2) => new(Guid.NewGuid(), net, new(x1, y1), new(x2, y2));
        var sheet = Sheet() with { Wires = [Wire(firstNet, 0, 10, 20, 10), Wire(firstNet, 20, 10, 40, 10),
            Wire(secondNet, 20, 0, 20, 10), Wire(secondNet, 20, 10, 20, 20)] };
        Assert.IsTrue(PresentationVerifier.Verify(Snapshot(sheet), Policy with { MaximumCrossingsPerSignal = 1 }).Clear);
        var report = PresentationVerifier.Verify(Snapshot(sheet), Policy with { MaximumCrossingsPerSignal = 0 });
        Assert.AreEqual(2, report.Findings.Count);
        Assert.IsTrue(report.Findings.All(f => f.Measured == 1));
        Assert.IsTrue(report.Findings.All(f => f.Locations is { Count: 1 }
            && f.Locations[0].Bounds == new PresentationBounds(20, 10, 20, 10)
            && f.Locations[0].ObjectIds.Count == 4));
        Assert.IsTrue(PresentationVerifier.Verify(Snapshot(sheet with
        {
            Wires = sheet.Wires.Select(w => w with { SignalKey = firstNet }).ToArray(), Junctions = [new(20, 10)]
        }), Policy with { MaximumCrossingsPerSignal = 0 }).Clear);
        report = PresentationVerifier.Verify(Snapshot(sheet with { Junctions = [new(20, 10)] }), Policy);
        Assert.IsTrue(report.Findings.All(f => f.Rule == "junction_net_conflict"));
        Assert.IsFalse(report.Clear);
    }

    [TestMethod]
    public void DiagonalIntersectionsAreExactAndCoordinateArithmeticDoesNotOverflow()
    {
        var sheet = Sheet() with { PageBounds = new(long.MinValue, -1, long.MaxValue, 2), Wires = [
            new(Guid.NewGuid(), "first", new(long.MinValue, 0), new(long.MaxValue, 0)),
            new(Guid.NewGuid(), "second", new(0, -1), new(1, 2))] };
        var findings = PresentationVerifier.Verify(Snapshot(sheet), Policy with { MaximumCrossingsPerSignal = 0 }).Findings;
        Assert.AreEqual(2, findings.Count);
        Assert.IsTrue(findings.All(f => f.Bounds == new PresentationBounds(0, 0, 1, 0)));
    }

    [TestMethod]
    public void WiresOutsidePageAndOverlappingSignalsAreLocalized()
    {
        var first = new PresentationWire(Guid.NewGuid(), "first", new(-10, 20), new(30, 20));
        var second = new PresentationWire(Guid.NewGuid(), "second", new(10, 20), new(40, 20));
        var sheet = Sheet() with { Wires = [first, second] };
        var findings = PresentationVerifier.Verify(Snapshot(sheet), Policy).Findings;
        Assert.AreEqual(first.Id, findings.Single(f => f.Rule == "page_overflow").ObjectIds.Single());
        Assert.AreEqual(new PresentationBounds(10, 20, 30, 20), findings.Single(f => f.Rule == "overlapping_signals").Bounds);
        var valid = sheet with { PageBounds = new(-10, 0, 40, 40), Wires = [first, second with { SignalKey = first.SignalKey }] };
        Assert.IsTrue(PresentationVerifier.Verify(Snapshot(valid), Policy).Clear);
        Assert.IsTrue(PresentationVerifier.Verify(Snapshot(valid with { Wires = [first, second with { Start = new(30, 20) }] }), Policy).Clear);
    }
    // Isolated geometry of NativePresentationChecks.CheckSymbolPlacementAsync. The live PSU/CPU creation
    // journey exercises it only on a clean layout; the layout planner never produces the must-catch cases,
    // so they are proven here.
    [TestMethod]
    public void SymbolPlacementCatchesBodyOverlapAndRegionExitButAllowsFieldOverhang()
    {
        var usable = new PresentationBounds(10, 10, 1_000, 800);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        PresentationBounds[] bodies = [new(100, 100, 200, 200), new(200, 100, 300, 200), new(400, 100, 500, 200)];
        // Fields overhang onto a neighbour's body and fields, but every body only touches or clears the others.
        PresentationBounds[] fields = [new(90, 90, 260, 210), new(150, 90, 330, 210), new(380, 60, 520, 220)];
        Assert.IsEmpty(NativePresentationChecks.SymbolPlacementIssues([a, b, c], bodies, fields, usable));

        var overlap = NativePresentationChecks.SymbolPlacementIssues([a, b, c],
            [bodies[0], bodies[1] with { LeftNm = 199 }, bodies[2]], fields, usable);
        Assert.AreEqual(NativePresentationChecks.SymbolBodiesOverlap, overlap.Single().Code);
        CollectionAssert.AreEqual(new[] { a, b }, overlap.Single().Symbols.ToArray());

        foreach (var (problem, body, field) in new (string, PresentationBounds, PresentationBounds)[]
        {
            ("a field in the title-block reserve", bodies[2], fields[2] with { BottomNm = 801 }),
            ("a field in the page inset", bodies[2], fields[2] with { TopNm = 9 }),
            ("a body outside the region", bodies[2] with { RightNm = 1_001 }, fields[2] with { RightNm = 1_001 })
        })
        {
            var issues = NativePresentationChecks.SymbolPlacementIssues([a, b, c], [bodies[0], bodies[1], body], [fields[0], fields[1], field], usable);
            Assert.AreEqual(NativePresentationChecks.SymbolOutsideUsableRegion, issues.Single().Code, problem);
            CollectionAssert.AreEqual(new[] { c }, issues.Single().Symbols.ToArray(), problem);
        }
        Assert.ThrowsExactly<ArgumentException>(() => NativePresentationChecks.SymbolPlacementIssues([a, b], bodies, fields, usable));
    }
}
