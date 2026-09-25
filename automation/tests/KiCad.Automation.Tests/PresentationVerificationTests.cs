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
        // A designator that cannot be read is measured as 0 against the 1 required, in designators.
        var notShown = report.Findings.Single(f => f.Rule == "designator_not_visible" && f.ObjectIds.Contains(hidden));
        Assert.AreEqual(0m, notShown.Measured); Assert.AreEqual(1m, notShown.Limit); Assert.AreEqual(PresentationUnits.Count, notShown.Unit);
        var absent = report.Findings.Single(f => f.Rule == "designator_missing" && f.ObjectIds.Contains(missing));
        Assert.AreEqual(0m, absent.Measured); Assert.AreEqual(1m, absent.Limit); Assert.AreEqual(PresentationUnits.Count, absent.Unit);
        Assert.AreEqual(PresentationUnits.Millimetres, font.Unit);
        Assert.AreEqual(0.000001m, report.Findings.Single(f => f.Rule == "image_cropped").Measured);
        Assert.IsTrue(report.Findings.All(f => f.SheetPath == sheet.SheetPath[0].ToString("D")));
        Assert.IsTrue(report.Findings.All(f => f.Measured is not null && f.Limit is not null && f.Unit is not null),
            "Every measurable finding carries its measured value, threshold and unit.");
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
        // Clipped by 1 nm: measured by how far the painted text reaches beyond its clip.
        var clipped = report.Findings.Single(f => f.Rule == "designator_not_visible");
        Assert.AreEqual(0.000001m, clipped.Measured); Assert.AreEqual(0m, clipped.Limit); Assert.AreEqual(PresentationUnits.Millimetres, clipped.Unit);
        StringAssert.Contains(clipped.Message, "clipped");
        Assert.IsFalse(report.Findings.Any(f => f.Rule == "page_overflow"));
        // Cut off by the left page edge by 5 nm, and empty.
        var cut = Text(Guid.NewGuid()) with { FullBounds = new(-5, 10, 20, 20) };
        var edge = PresentationVerifier.Verify(Snapshot(Sheet() with { Objects = [cut], RequiredDesignators = [cut.Id] }), Policy)
            .Findings.Single(f => f.Rule == "designator_not_visible");
        Assert.AreEqual(0.000005m, edge.Measured); Assert.AreEqual(0m, edge.Limit); Assert.AreEqual(PresentationUnits.Millimetres, edge.Unit);
        StringAssert.Contains(edge.Message, "page edge");
        var blank = Text(Guid.NewGuid()) with { Text = " " };
        var empty = PresentationVerifier.Verify(Snapshot(Sheet() with { Objects = [blank], RequiredDesignators = [blank.Id] }), Policy)
            .Findings.Single();
        Assert.AreEqual("designator_not_visible", empty.Rule);
        Assert.AreEqual(0m, empty.Measured); Assert.AreEqual(1m, empty.Limit); Assert.AreEqual(PresentationUnits.Count, empty.Unit);
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
        Assert.IsTrue(report.Findings.All(f => f.Measured == 2 && f.Limit == 1 && f.Unit == PresentationUnits.Count),
            "A conflicting junction is measured by the distinct signals it joins against the one allowed.");
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
        var shared = findings.Single(f => f.Rule == "overlapping_signals");
        Assert.AreEqual(new PresentationBounds(10, 20, 30, 20), shared.Bounds);
        Assert.AreEqual(0.00002m, shared.Measured); Assert.AreEqual(0m, shared.Limit); Assert.AreEqual(PresentationUnits.Millimetres, shared.Unit);
        Assert.AreEqual(0.00001m, findings.Single(f => f.Rule == "page_overflow").Measured);
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

        // Every report states the policy it applied, so one without findings still says which tolerance let objects touch.
        Assert.AreEqual(Policy, quiet.Policy);
        Assert.AreEqual(0.634m, VerifyLayout(clean.All, Policy with { OverlapToleranceMm = 0.634m }).Policy!.OverlapToleranceMm);
        // A tolerance of half the 1.27 mm grid or more would let real overlaps pass (a caller could switch the rules off with
        // 1000 mm), and a negative one means nothing: both are refused, as they are before KiCad is asked (NativePresentationChecks).
        foreach (decimal invalid in new[] { PresentationPolicy.OverlapToleranceLimitMm, 1000m, -0.1m })
        {
            var refused = Assert.ThrowsExactly<AutomationException>(() => VerifyLayout(clean.All, Policy with { OverlapToleranceMm = invalid }));
            Assert.AreEqual("invalid_presentation", refused.Code);
            Assert.ThrowsExactly<AutomationException>(() => PresentationVerifier.RequireValid(Policy with { OverlapToleranceMm = invalid }));
        }

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
            Assert.AreEqual(unannotated, report.Findings.Any(f => f.Rule == "designator_unannotated" && f.ObjectIds.Single() == clean.U1Reference.Id
                && f.Measured == 0 && f.Limit == 1 && f.Unit == PresentationUnits.Count), text);
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

    // The rendered wire check of generated connections (CN-1 §6.4, §7), on exact synthetic rendering facts: unit-tested because
    // each boundary (the tolerance at a wire end, touching versus crossing a box, each object role) needs exact geometry that a
    // live sheet cannot be steered into. The psu-cpu-connected journey runs it on KiCad's own rendering of every generated wire,
    // and proves there that it reports a probe wire into a symbol body and one through a label.
    [TestMethod]
    public void WireOverlapsCatchWiresOverBodiesAndTextButNotWiresEndingOnWhatTheyConnect()
    {
        const long Mm = 1_000_000;
        Guid symbol = Guid.NewGuid(), field = Guid.NewGuid(), label = Guid.NewGuid(), hidden = Guid.NewGuid(), graphic = Guid.NewGuid();
        var objects = new List<PresentationObject>
        {
            new(symbol, PresentationObjectKind.Graphic, new(20 * Mm, 20 * Mm, 40 * Mm, 40 * Mm), true, Role: PresentationRole.Symbol),
            new(field, PresentationObjectKind.ReferenceDesignator, new(20 * Mm, 14 * Mm, 26 * Mm, 16 * Mm), true, 1.27m, "U1",
                Role: PresentationRole.Field, OwnerId: symbol),
            new(label, PresentationObjectKind.Text, new(60 * Mm - 160_000, 57_900_000, 70 * Mm, 60 * Mm + 265_000), true, 1.27m, "VIN",
                Role: PresentationRole.Label),
            new(hidden, PresentationObjectKind.Text, new(80 * Mm, 20 * Mm, 90 * Mm, 40 * Mm), false, 1.27m, "hidden", Role: PresentationRole.Field, OwnerId: symbol),
            new(graphic, PresentationObjectKind.Graphic, new(100 * Mm, 20 * Mm, 110 * Mm, 40 * Mm), true)
        };
        var wires = new List<PresentationWire>();
        Guid Wire(long x0, long y0, long x1, long y1)
        {
            var id = Guid.NewGuid();
            wires.Add(new(id, "net", new(x0, y0), new(x1, y1)));
            return id;
        }
        // Guards: a wire leaving a pin on the body's edge, one ending on a label's anchor from behind (reaching the 0.16 mm the
        // label's box extends behind it), one passing just beside the body, one touching the body's edge, and wires over a hidden
        // field or a plain graphic.
        Guid pin = Wire(20 * Mm, 30 * Mm, 10 * Mm, 30 * Mm), anchor = Wire(55 * Mm, 60 * Mm, 60 * Mm, 60 * Mm),
            beside = Wire(10 * Mm, 41 * Mm, 50 * Mm, 41 * Mm), edge = Wire(10 * Mm, 40 * Mm, 50 * Mm, 40 * Mm),
            overHidden = Wire(85 * Mm, 10 * Mm, 85 * Mm, 50 * Mm), overGraphic = Wire(105 * Mm, 10 * Mm, 105 * Mm, 50 * Mm);
        // Must-catch: a wire across the body, one into it from a pin by 3 mm, one through the reference text, and one from the
        // label's anchor along its text.
        Guid across = Wire(10 * Mm, 25 * Mm, 50 * Mm, 25 * Mm), into = Wire(20 * Mm, 35 * Mm, 23 * Mm, 35 * Mm),
            throughField = Wire(23 * Mm, 10 * Mm, 23 * Mm, 18 * Mm), alongLabel = Wire(60 * Mm, 60 * Mm, 63 * Mm, 60 * Mm);
        var sheet = Sheet() with { Objects = objects, Wires = wires };
        var findings = PresentationVerifier.WireOverlaps(sheet, [pin, anchor, beside, edge, overHidden, overGraphic, across, into, throughField, alongLabel], 0.5m);
        string Rules(Guid wire) => string.Join(",", findings.Where(f => f.ObjectIds[0] == wire).Select(f => f.Rule + ":" + f.ObjectIds[1].ToString("D")[..4]));
        foreach (var guard in new[] { pin, anchor, beside, edge, overHidden, overGraphic })
            Assert.AreEqual("", Rules(guard), "No finding for a guard wire.");
        var body = findings.Single(f => f.ObjectIds[0] == across);
        Assert.AreEqual(PresentationVerifier.WireOverlapsSymbol, body.Rule);
        Assert.AreEqual(symbol, body.ObjectIds[1]);
        Assert.AreEqual(20m, body.Measured);
        Assert.AreEqual(PresentationSeverity.Error, body.Severity);
        Assert.AreEqual(new PresentationBounds(20 * Mm, 25 * Mm, 40 * Mm, 25 * Mm), body.Bounds);
        var inward = findings.Single(f => f.ObjectIds[0] == into);
        Assert.AreEqual(PresentationVerifier.WireOverlapsSymbol, inward.Rule);
        Assert.AreEqual(2m, inward.Measured, "The 0.5 mm at each end (the pin, and the end inside the body) does not count; the other 2 mm do.");
        var reference = findings.Single(f => f.ObjectIds[0] == throughField);
        Assert.AreEqual(PresentationVerifier.WireOverlapsText, reference.Rule);
        Assert.AreEqual(field, reference.ObjectIds[1]);
        var text = findings.Single(f => f.ObjectIds[0] == alongLabel);
        Assert.AreEqual(PresentationVerifier.WireOverlapsText, text.Rule);
        Assert.AreEqual(label, text.ObjectIds[1]);
        Assert.AreEqual(2m, text.Measured);
        // A tolerance of zero counts everything inside, and one of half a grid or more is refused, as is a diagonal wire or one
        // missing from the rendering facts.
        Assert.AreEqual(3m, PresentationVerifier.WireOverlaps(sheet, [into], 0m).Single().Measured);
        Assert.ThrowsExactly<AutomationException>(() => PresentationVerifier.WireOverlaps(sheet, [into], 0.635m));
        var diagonal = Wire(0, 0, 5 * Mm, 5 * Mm);
        Assert.ThrowsExactly<AutomationException>(() => PresentationVerifier.WireOverlaps(Sheet() with { Wires = wires }, [diagonal], 0.5m));
        Assert.ThrowsExactly<AutomationException>(() => PresentationVerifier.WireOverlaps(sheet, [Guid.NewGuid()], 0.5m));
    }

    // The same check on a symbol KiCad reports in parts (its body without the pins, and each pin as drawn), in the shape of
    // the PSU/CPU fixture's STM32C011J on the PSU sheet: its right-hand pins end 2.54 mm from the body, one of them a grid
    // further out, so the whole box reaches past the shorter pins' ends where nothing is drawn. Unit-tested for the exact
    // boundary cases (touching a pin only at its connection point, crossing or running along it anywhere else); the
    // psu-cpu-connected journey measures KiCad's own parts for every generated wire.
    [TestMethod]
    public void WireOverlapsMeasureASymbolReportedInPartsByItsBodyAndItsPins()
    {
        const long Mm = 1_000_000;
        Guid symbol = Guid.NewGuid();
        PresentationSegment Pin(decimal x0, decimal y0, decimal x1, decimal y1) =>
            new(new((long)(x0 * Mm), (long)(y0 * Mm)), new((long)(x1 * Mm), (long)(y1 * Mm)));
        var parts = new PresentationObject(symbol, PresentationObjectKind.Graphic, new(22_460_000, 20 * Mm, 41_270_000, 40 * Mm), true,
            Role: PresentationRole.Symbol, BodyBounds: new(25 * Mm, 20 * Mm, 37_460_000, 40 * Mm),
            PinLines: [Pin(40m, 25m, 37.46m, 25m), Pin(41.27m, 35m, 37.46m, 35m), Pin(22.46m, 30m, 25m, 30m)]);
        var wires = new List<PresentationWire>();
        Guid Wire(decimal x0, decimal y0, decimal x1, decimal y1)
        {
            var id = Guid.NewGuid();
            wires.Add(new(id, "net", new((long)(x0 * Mm), (long)(y0 * Mm)), new((long)(x1 * Mm), (long)(y1 * Mm))));
            return id;
        }
        // Guards: a wire leaving the short pin's connection point outward, past where the longer pin reaches; and one arriving at
        // the longer pin's connection point at a right angle.
        Guid outward = Wire(40m, 25m, 50m, 25m), arriving = Wire(41.27m, 30m, 41.27m, 35m);
        // Must-catch: a wire run from a pin's connection point into the symbol along that pin, one crossing a pin, one passing
        // over a pin's connection point without ending there, and one across the body.
        Guid inward = Wire(40m, 25m, 37m, 25m), crossing = Wire(39m, 22m, 39m, 28m), passing = Wire(40m, 22m, 40m, 28m),
            across = Wire(20m, 32m, 45m, 32m);
        var sheet = Sheet() with { Objects = [parts], Wires = wires };
        var findings = PresentationVerifier.WireOverlaps(sheet, [outward, arriving, inward, crossing, passing, across], 0.5m);
        IReadOnlyList<decimal?> Measured(Guid wire) => [.. findings.Where(f => f.ObjectIds[0] == wire).Select(f => f.Measured)];
        Assert.IsEmpty(Measured(outward), "A wire leaving a pin's connection point outward is clear of the symbol.");
        Assert.IsEmpty(Measured(arriving), "A wire ending on a pin's connection point is clear of the symbol.");
        Assert.IsTrue(findings.All(f => f.Rule == PresentationVerifier.WireOverlapsSymbol && f.ObjectIds[1] == symbol && f.Severity == PresentationSeverity.Error));
        CollectionAssert.AreEqual(new[] { 2.54m }, Measured(inward).ToArray(), "Along its own pin for the pin's 2.54 mm; the rest is in the tolerance.");
        CollectionAssert.AreEqual(new[] { 0m }, Measured(crossing).ToArray(), "A crossing is reported as a point.");
        Assert.AreEqual(new PresentationBounds(39 * Mm, 25 * Mm, 39 * Mm, 25 * Mm), findings.Single(f => f.ObjectIds[0] == crossing).Bounds);
        CollectionAssert.AreEqual(new[] { 0m }, Measured(passing).ToArray(), "Over a connection point it does not end on.");
        CollectionAssert.AreEquivalent(new[] { 12.46m }, Measured(across).ToArray(), "Across the body, clear of every pin line.");
        // The same symbol without its parts is measured by its whole box, which reports the outward wire: the false positive the
        // parts remove.
        var whole = sheet with { Objects = [parts with { BodyBounds = null, PinLines = null }] };
        CollectionAssert.AreEqual(new[] { 0.77m }, PresentationVerifier.WireOverlaps(whole, [outward], 0.5m).Select(f => f.Measured).ToArray());
        // A pin line that is not straight along one axis is refused.
        var bent = sheet with { Objects = [parts with { PinLines = [Pin(40m, 25m, 37.46m, 26m)] }] };
        Assert.ThrowsExactly<AutomationException>(() => PresentationVerifier.WireOverlaps(bent, [outward], 0.5m));
    }
}
