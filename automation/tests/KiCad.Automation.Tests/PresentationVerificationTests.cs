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
    // Pure geometry of the overlap, reading-direction and annotation rules. The live PSU/CPU journey
    // (NativeXmlComponentCreationJourney, VerifyPsuCpuPresentationFindings) proves them on KiCad's own
    // measurements; these cases pin the exact thresholds and the intentional patterns that must stay quiet,
    // which a native seed cannot vary finely (touching within the tolerance, a field on its own body).
    private static long Mm(decimal millimetres) => (long)(millimetres * 1_000_000m);
    private static PresentationBounds Box(decimal left, decimal top, decimal right, decimal bottom) => new(Mm(left), Mm(top), Mm(right), Mm(bottom));
    private sealed record Layout(PresentationObject U1, PresentationObject U1Reference, PresentationObject U1Value,
        PresentationObject U2, PresentationObject Label, PresentationObject LabelField, PresentationObject Sheet,
        PresentationObject SheetName)
    {
        public IReadOnlyList<PresentationObject> All => [U1, U1Reference, U1Value, U2, Label, LabelField, Sheet, SheetName];
    }
    private static Layout CleanLayout()
    {
        Guid u1 = Guid.NewGuid(), label = Guid.NewGuid(), sheet = Guid.NewGuid();
        PresentationObject Field(Guid owner, PresentationBounds glyphs, string text, bool reference = false) => new(Guid.NewGuid(),
            reference ? PresentationObjectKind.ReferenceDesignator : PresentationObjectKind.Text, glyphs, true, 1.27m, text,
            Role: PresentationRole.Field, OwnerId: owner, ReadingAngleDegrees: 0);
        return new(
            new(u1, PresentationObjectKind.Graphic, Box(10, 10, 30, 30), true, Role: PresentationRole.Symbol),
            Field(u1, Box(12, 5, 16, 7), "U1", reference: true),
            // A value the library draws inside its own body is intentional.
            Field(u1, Box(15, 18, 25, 20), "LM2595S-ADJ"),
            new(Guid.NewGuid(), PresentationObjectKind.Graphic, Box(40, 10, 60, 30), true, Role: PresentationRole.Symbol),
            // A label anchored on U1's pin end at x = 30 mm reaches 0.2 mm back over the pin: touching, not overlapping.
            new(label, PresentationObjectKind.Text, Box(29.8m, 18.6m, 36, 20.4m), true, 1.27m, "VIN", Role: PresentationRole.Label,
                ReadingAngleDegrees: 0),
            Field(label, Box(31, 18.8m, 34, 20.2m), "[2]"),
            new(sheet, PresentationObjectKind.Graphic, Box(70, 10, 100, 40), true, Role: PresentationRole.Sheet),
            Field(sheet, Box(70, 7, 80, 9.5m), "PSU"));
    }
    private static PresentationReport VerifyLayout(IEnumerable<PresentationObject> objects, PresentationPolicy? policy = null,
        IReadOnlyList<Guid>? required = null) =>
        PresentationVerifier.Verify(Snapshot(new PresentationSheet([Guid.NewGuid()], Box(0, 0, 297, 210), [.. objects], required ?? [], [], [], "/PSU/")),
            policy ?? Policy);

    [TestMethod]
    public void OverlapRulesCatchCoveredBodiesLabelsAndFieldsWithDepthAndTolerance()
    {
        var clean = CleanLayout();
        var quiet = VerifyLayout(clean.All, required: [clean.U1Reference.Id]);
        Assert.IsTrue(quiet.Clear, string.Join("; ", quiet.Findings.Select(f => f.Rule + " " + f.Measured)));

        PresentationFinding Single(IEnumerable<PresentationObject> objects, string rule, PresentationPolicy? policy = null)
        {
            var findings = VerifyLayout(objects, policy).Findings;
            Assert.HasCount(1, findings, string.Join("; ", findings.Select(f => f.Rule)));
            Assert.AreEqual(rule, findings[0].Rule);
            return findings[0];
        }
        void Pair(PresentationFinding finding, PresentationObject first, PresentationObject second) =>
            CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, finding.ObjectIds.ToArray());

        // U2 slid 11 mm left: its body covers U1's right edge by 1 mm over the full 20 mm height.
        var u2 = clean.U2 with { FullBounds = Box(29, 10, 49, 30) };
        var bodies = Single([clean.U1, clean.U1Reference, clean.U1Value, u2, clean.Sheet, clean.SheetName], "body_overlap");
        Pair(bodies, clean.U1, u2);
        Assert.AreEqual(1m, bodies.Measured); Assert.AreEqual(0.5m, bodies.Limit);
        Assert.AreEqual(Box(29, 10, 30, 30), bodies.Bounds);
        Assert.AreEqual(PresentationSeverity.Error, bodies.Severity);
        Assert.AreEqual("/PSU/", bodies.SheetName);
        Assert.AreEqual(new DocumentRevision("fixture-epoch", 5), bodies.Revision);

        // A label turned back over its own symbol reaches 2 mm into U1's body.
        var label = Single(clean.All.Select(o => o == clean.Label ? o with { FullBounds = Box(28, 18.6m, 36, 20.4m) } : o), "label_overlap");
        Pair(label, clean.U1, clean.Label);
        Assert.AreEqual(1.8m, label.Measured);

        // U1's reference dragged onto U2, and U1's value dragged onto U1's own reference.
        var covered = Single(clean.All.Select(o => o == clean.U1Reference ? o with { FullBounds = Box(41, 12, 45, 14) } : o), "field_overlap");
        Pair(covered, clean.U1Reference, clean.U2);
        Assert.AreEqual(2m, covered.Measured);
        var stacked = Single(clean.All.Select(o => o == clean.U1Value ? o with { FullBounds = Box(12.5m, 5.5m, 20, 7.5m) } : o), "field_overlap");
        Pair(stacked, clean.U1Reference, clean.U1Value);
        Assert.AreEqual(1.5m, stacked.Measured);
        Assert.AreEqual(Box(12.5m, 5.5m, 16, 7), stacked.Bounds);

        // The tolerance is inclusive; one hundred micrometres more is an overlap. A zero tolerance reports the touching label.
        Assert.IsTrue(VerifyLayout(clean.All.Select(o => o == clean.Label ? o with { FullBounds = Box(29.5m, 18.6m, 36, 20.4m) } : o)).Clear);
        Assert.AreEqual(0.6m, Single(clean.All.Select(o => o == clean.Label ? o with { FullBounds = Box(29.4m, 18.6m, 36, 20.4m) } : o),
            "label_overlap").Measured);
        var strict = Single(clean.All, "label_overlap", Policy with { OverlapToleranceMm = 0 });
        Assert.AreEqual(0.2m, strict.Measured); Assert.AreEqual(0m, strict.Limit);

        // Hidden or empty field text paints nothing, and boxes that only touch share no area.
        foreach (var quietField in new[] { clean.U1Reference with { FullBounds = Box(41, 12, 45, 14), Visible = false },
            clean.U1Reference with { FullBounds = Box(41, 12, 45, 14), Text = "" } })
            Assert.IsTrue(VerifyLayout(clean.All.Select(o => o == clean.U1Reference ? quietField : o)).Clear);
        Assert.IsTrue(VerifyLayout(clean.All.Select(o => o == clean.U2 ? o with { FullBounds = Box(30, 10, 50, 30) } : o)
            .Where(o => o != clean.Label && o != clean.LabelField)).Clear);
        Assert.ThrowsExactly<AutomationException>(() => VerifyLayout([clean.U1Reference with { OwnerId = null }]));
    }

    [TestMethod]
    public void UpsideDownOrTopToBottomTextAndUnannotatedDesignatorsAreCaught()
    {
        var clean = CleanLayout();
        foreach (var (angle, rule) in new (decimal, string?)[] { (0, null), (90, null), (359.9995m, null), (180, "upside down"),
            (270, "top to bottom"), (-90, "top to bottom"), (135, "upside down") })
        {
            var report = VerifyLayout(clean.All.Select(o => o == clean.U1Value ? o with { ReadingAngleDegrees = angle } : o));
            if (rule is null) { Assert.IsTrue(report.Clear, angle.ToString()); continue; }
            var finding = report.Findings.Single();
            Assert.AreEqual("text_orientation", finding.Rule);
            CollectionAssert.AreEqual(new[] { clean.U1Value.Id }, finding.ObjectIds.ToArray());
            Assert.AreEqual(angle < 0 ? angle + 360 : angle, finding.Measured);
            Assert.AreEqual(90m, finding.Limit);
            StringAssert.Contains(finding.Message, rule);
        }
        Assert.IsTrue(VerifyLayout(clean.All.Select(o => o == clean.U1Value ? o with { ReadingAngleDegrees = 180, Visible = false } : o)).Clear);

        foreach (var (text, unannotated) in new[] { ("R?", true), ("U?A", true), ("U1", false), ("U5A", false) })
        {
            var report = VerifyLayout(clean.All.Select(o => o == clean.U1Reference ? o with { Text = text } : o), required: [clean.U1Reference.Id]);
            Assert.AreEqual(unannotated, report.Findings.Any(f => f.Rule == "designator_unannotated" && f.ObjectIds.Single() == clean.U1Reference.Id), text);
            Assert.AreEqual(unannotated ? 1 : 0, report.Findings.Count, text);
        }
        // A power symbol's hidden '#PWR' reference is not a required designator.
        var power = clean.U1Reference with { Id = Guid.NewGuid(), Text = "#PWR01", Visible = false, FullBounds = Box(100, 100, 104, 102) };
        Assert.IsTrue(VerifyLayout([.. clean.All, power], required: [clean.U1Reference.Id]).Clear);
        Assert.AreEqual("designator_not_visible", VerifyLayout([.. clean.All, power], required: [power.Id]).Findings.Single().Rule);
    }

    [TestMethod]
    public void OverflowFindingsMeasureTheDistanceBeyondThePageForEverySheetInstance()
    {
        var clean = CleanLayout();
        var moved = clean.U1 with { FullBounds = Box(-5, 10, 15, 30) };
        var objects = clean.All.Select(o => o == clean.U1 ? moved : o).ToArray();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var report = PresentationVerifier.Verify(Snapshot(
            new PresentationSheet([first], Box(0, 0, 297, 210), objects, [], [], [], "/"),
            new PresentationSheet([first, second], Box(0, 0, 297, 210), clean.All, [], [], [], "/PSU/")), Policy);
        var overflow = report.Findings.Single();
        Assert.AreEqual("page_overflow", overflow.Rule);
        Assert.AreEqual(5m, overflow.Measured); Assert.AreEqual(0m, overflow.Limit);
        Assert.AreEqual(first.ToString("D"), overflow.SheetPath); Assert.AreEqual("/", overflow.SheetName);
        CollectionAssert.AreEqual(new[] { first.ToString("D"), first.ToString("D") + "/" + second.ToString("D") },
            report.Sheets!.Select(s => s.SheetPath).ToArray());
        CollectionAssert.AreEqual(new[] { "/", "/PSU/" }, report.Sheets!.Select(s => s.SheetName).ToArray());
        Assert.AreEqual(objects.Length, report.Sheets![0].Objects);
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
