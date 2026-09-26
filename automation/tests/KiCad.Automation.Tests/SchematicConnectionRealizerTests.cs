using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using static KiCad.Automation.Tests.SchematicConnectionIntentBuilderTests;
using DocumentRevision = KiCad.Automation.Protocol.DocumentRevision;

namespace KiCad.Automation.Tests;

// CN-1 milestone 1 label-stub realization (cn1-wiring-intent.md §6) and its resolution (§9.4). The end-to-end proof is
// native: KiCad advertises schematic.connection-realization.v1 for a project (CN-1 §8.3), and the psu-cpu-connected journey
// (NativeXmlComponentCreationJourney.VerifyPsuCpuConnectedRealization) applies the PSU/CPU fixture's 11 nets through the
// production MCP server, with KiCad's connectivity assertion, a forced mismatch, undo, redo, save and reload, while
// McpReattachmentJourney realizes a revision on a real editor through the automatic worker and apply. The creation journeys
// also record live measurements (automation/tests/fixtures/connection-realization) for the replay test below. These unit
// tests stay because a live editor cannot be steered into each case: the admission rules, every refusal code and the
// resolution comparisons need exact control over geometry, which only a synthetic measurement gives, and every must-catch
// case here has a false-positive guard next to it. Lane 2A created this file with
// SchematicConnectionRealizer.cs and SchematicConnectionResolution.cs under decision n2c2ef8777f8ace77.
[TestClass]
public sealed class SchematicConnectionRealizerTests
{
    private const long Grid = 1_270_000;
    internal static readonly SchematicConnectionPolicy Policy = SchematicConnectionPolicy.FromSnapshot(new SchematicHierarchyData
    {
        Instances = { new SchematicScreenData { Metadata = new() { Formatting = SchematicFormattingTests.Formatting() } } }
    });

    [TestMethod]
    public async Task RootAdditionDrawsOneStubAndLabelPerNewPinAndEndsWithTheAssertion()
    {
        // §15 "Root addition": R1.1 is in SIG with a local label; TP1.1 is bare. The XML adds R2, puts R2.1 in SIG
        // and makes {R2.2, TP1.1} a new net /OUT. The bench draws its pins off the 1.27 mm connection grid, so /OUT, which
        // §7 would join with wires, falls back to label stubs and says why; SIG has one new pin and keeps its stub (no fallback).
        var scene = RootAddition();
        var realization = await scene.Realize();
        var screen = scene.Intent.Screens.Single();
        var candidate = scene.Plan.Candidate!;
        // Three wires and three local labels, each 2 grids from its pin and facing away from the body.
        var wires = Generated<SchematicLine>(realization);
        var labels = Generated<LocalLabel>(realization);
        Assert.HasCount(3, wires); Assert.HasCount(3, labels);
        Assert.IsEmpty(Generated<GlobalLabel>(realization)); Assert.IsEmpty(Generated<HierarchicalLabel>(realization));
        foreach (var wire in wires)
        {
            Assert.AreEqual(SchematicLineType.SltWire, wire.Type); Assert.AreEqual(LockedState.LsUnlocked, wire.Locked);
            Assert.AreEqual(wire.Start.YNm, wire.End.YNm); Assert.AreEqual(2 * Grid, wire.Start.XNm - wire.End.XNm);
            var label = labels.Single(l => l.Position.Equals(wire.End));
            Assert.AreEqual(SchematicLabelSpinStyle.SlssLeft, label.SpinStyle);
            Assert.AreEqual(Policy.TextSizeNm, label.Text.Attributes.Size.XNm); Assert.IsFalse(label.Text.Attributes.Multiline);
            Assert.AreEqual(HorizontalAlignment.HaRight, label.Text.Attributes.HorizontalAlignment, "A left-facing label is right-justified on its anchor.");
            Assert.AreEqual(LockedState.LsUnlocked, label.Locked);
            Assert.IsEmpty(label.Fields);
            Assert.IsTrue(label.FieldsAutoplaced, "KiCad marks every label it loads without fields as auto-placed, so the label survives save and reload.");
        }
        CollectionAssert.AreEquivalent(new[] { "SIG", "OUT", "OUT" }, labels.Select(l => l.Text.Text_).ToArray());
        // Identities: the §6.7 construction, keyed by the attached placed pin.
        var stubbed = screen.Islands.SelectMany(i => i.Members.Where(m => m.RequiresStub)).ToArray();
        Assert.HasCount(3, stubbed);
        foreach (var member in stubbed)
        {
            string key = SchematicConnectionIdentity.PinAnchorKey(member.Pin.PlacedPinId);
            var wireId = Generated(scene, screen.ScreenId, GeneratedConnectionRole.StubWire, key);
            var labelId = Generated(scene, screen.ScreenId, GeneratedConnectionRole.StubLabel, key);
            Assert.IsTrue(realization.Generated.Any(g => g.Id == wireId && g.Role == GeneratedConnectionRole.StubWire && g.PlacedPinId == member.Pin.PlacedPinId));
            Assert.IsTrue(realization.Generated.Any(g => g.Id == labelId && g.Role == GeneratedConnectionRole.StubLabel
                && g.TypeUrl == Any.Pack(new LocalLabel()).TypeUrl && g.NetIds.SequenceEqual(new[] { screen.Islands.Single(i => i.Members.Contains(member)).NetId })));
        }
        // Operations: the created symbol and every generated item, then the assertion with the intent's groups.
        var operations = realization.Operations;
        var assertion = operations[^1].AssertConnectivity;
        Assert.IsNotNull(assertion);
        Assert.AreEqual(1u, assertion.Version);
        Assert.IsNull(operations[^1].TargetDocument);
        CollectionAssert.AreEqual(scene.Intent.ExpectedGroups.Select(g => string.Join(",", g.Select(k => k.PlacedPinId))).ToArray(),
            assertion.ExpectedGroups.Select(g => string.Join(",", g.Pins.Select(p => p.Pin.Value))).ToArray());
        Assert.IsTrue(assertion.ExpectedGroups.SelectMany(g => g.Pins).All(p => p.Path.Equals(scene.Saved.Observed.Instances[0].Metadata.Document.SheetPath)));
        var created = operations.Where(o => o.Create is not null).Select(o => o.Create).ToArray();
        Assert.AreEqual(1, created.Count(c => c.Is(SchematicSymbolInstance.Descriptor)));
        Assert.AreEqual(6, created.Count(c => !c.Is(SchematicSymbolInstance.Descriptor)));
        Assert.IsTrue(operations.Take(operations.Count - 1).All(o => o.OperationCase is SchematicItemOperation.OperationOneofCase.Create
            or SchematicItemOperation.OperationOneofCase.Update or SchematicItemOperation.OperationOneofCase.ReplaceLibraryCache));
        // The realized design holds exactly the candidate plus the generated items, and round-trips.
        Assert.AreEqual(candidate.Schematic.Instances[0].Items.Count + 6, realization.Design.Schematic.Instances[0].Items.Count);
        Assert.AreEqual(candidate.Engineering, realization.Design.Engineering);
        string xml = SchematicDesignXml.Write(realization.Design, []);
        Assert.AreEqual(xml, SchematicDesignXml.Write(SchematicDesignXml.Read(xml, []), []));
        CollectionAssert.AreEquivalent(scene.Intent.Nets.Select(n => n.NetId).ToArray(), realization.Outcomes.Select(o => o.NetId).ToArray());
        Assert.IsTrue(realization.Outcomes.All(o => o.Strategy == ConnectionRealizationStrategy.LabelStub && !o.AttachedCarrier));
        var output = scene.Intent.Nets.Single(n => n.Name == "/OUT").NetId;
        Assert.IsNull(realization.Outcomes.Single(o => o.NetId != output).FallbackReason, "One new pin is drawn with a stub; that is no fallback.");
        StringAssert.Contains(realization.Outcomes.Single(o => o.NetId == output).FallbackReason, "is not on the connection grid");
        Assert.AreEqual(6, realization.Outcomes.Sum(o => o.GeneratedIds.Count));
        Assert.IsFalse(realization.Diagnostics.Any(d => d.Code == SchematicConnectionErrors.ExistingNetNamedByRealization));
        Assert.IsTrue(realization.Diagnostics.Any(d => d.Code == SchematicConnectionErrors.RealizationPageReservationsUnspecified));
        // Round 2 measured one prototype per distinct label and never commits a probe identity.
        var prototypes = scene.Geometry.Requests.SelectMany(r => r.ItemCandidates).Select(i => i.Unpack<LocalLabel>()).ToArray();
        CollectionAssert.AreEquivalent(new[] { "OUT", "SIG" }, prototypes.Select(p => p.Text.Text_).ToArray());
        Assert.IsFalse(prototypes.Any(p => realization.Generated.Any(g => g.Id.ToString("D") == p.Id.Value)));
        Assert.IsTrue(scene.Geometry.Requests.All(r => r.ExpectedRevision.Equals(scene.Checkpoint.State.Revision)));
        // Determinism (I9): the same inputs give the same operations, byte for byte.
        var again = await scene.Realize();
        CollectionAssert.AreEqual(realization.Operations.Select(o => o.ToByteString()).ToArray(), again.Operations.Select(o => o.ToByteString()).ToArray());
    }

    [TestMethod]
    public async Task AHierarchyCrossingGetsAnUplinkLabelAndASheetPinWithItsOwnStub()
    {
        // §15 "Hierarchy crossing": new U1.3 on the child sheet joins DATA with bare root pin R5.2.
        var scene = HierarchyCrossing();
        var realization = await scene.Realize();
        var hierarchical = Generated<HierarchicalLabel>(realization).Single();
        Assert.AreEqual("DATA", hierarchical.Text.Text_);
        Assert.AreEqual(SchematicLabelShape.SlshPassive, hierarchical.Shape);
        Assert.AreEqual(VerticalAlignment.VaCenter, hierarchical.Text.Attributes.VerticalAlignment);
        var locals = Generated<LocalLabel>(realization);
        Assert.HasCount(2, locals, "The root side carries a sheet-pin label and the R5.2 stub label.");
        Assert.IsTrue(locals.All(l => l.Text.Text_ == "DATA"));
        var update = SheetUpdates(realization).Single();
        Assert.AreEqual(scene.Bench!.ChildSheetSymbol.ToString("D"), update.Id.Value);
        var pin = update.Pins.Single();
        Assert.AreEqual("DATA", pin.Text.Text_);
        Assert.AreEqual(SheetSide.ShsLeft, pin.Side, "The root member sits left of the sheet symbol's centre.");
        Assert.AreEqual(SchematicLabelSpinStyle.SlssRight, pin.SpinStyle);
        Assert.AreEqual(SchematicLabelShape.SlshPassive, pin.Shape);
        Assert.AreEqual(150_000_000L, pin.Position.XNm);
        Assert.AreEqual(50_000_000L + Policy.SheetPinPitchNm, pin.Position.YNm, "The first slot is one pitch below the top edge.");
        var sheetWire = Generated<SchematicLine>(realization).Single(w => w.Start.Equals(pin.Position));
        Assert.AreEqual(pin.Position.XNm - 2 * Grid, sheetWire.End.XNm);
        Assert.AreEqual(SchematicLabelSpinStyle.SlssLeft, locals.Single(l => l.Position.Equals(sheetWire.End)).SpinStyle);
        CollectionAssert.AreEquivalent(new[] { GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel, GeneratedConnectionRole.StubWire,
            GeneratedConnectionRole.StubLabel, GeneratedConnectionRole.SheetPin, GeneratedConnectionRole.SheetPinWire, GeneratedConnectionRole.SheetPinLabel },
            realization.Generated.Select(g => g.Role).ToArray());
        string key = SchematicConnectionIdentity.SheetPinAnchorKey(scene.Bench.ChildSheetSymbol, "DATA");
        Assert.AreEqual(Generated(scene, scene.Bench.ScreenId(BenchSheet.Root), GeneratedConnectionRole.SheetPin, key).ToString("D"), pin.Id.Value);
        // The realized design shows the new pin on the sheet symbol.
        var rootItems = realization.Design.Schematic.Instances.Single(s => s.Metadata.Document.SheetPath.Path.Count == 1).Items;
        Assert.AreEqual(pin, rootItems.Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>()).Single().Pins.Single());
    }

    [TestMethod]
    public async Task AnUnlabelledConnectionIsNamedAtItsOwnPinAndReported()
    {
        // R1.1 and R2.1 are joined by a wire without a label; the XML adds TP1.1 to that net. The existing connection
        // must be named where it already is, so a join stub goes on R1.1 before TP1.1 gets its own stub.
        var scene = Link();
        var island = scene.Intent.Screens.Single().Islands.Single();
        Assert.IsTrue(island.JoinRequired);
        // An editor that does not measure its drawing sheet (the frozen §6.3 (a) order): the first join candidate takes a join stub.
        var realization = await WithDrawingSheet(scene, null).Realize();
        Assert.HasCount(2, Generated<LocalLabel>(realization));
        var join = realization.Generated.Where(g => g.PlacedPinId == island.JoinCandidates[0].PlacedPinId).ToArray();
        CollectionAssert.AreEquivalent(new[] { GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel }, join.Select(g => g.Role).ToArray(),
            "The first join candidate takes a join stub.");
        var named = realization.Diagnostics.Single(d => d.Code == SchematicConnectionErrors.ExistingNetNamedByRealization);
        Assert.AreEqual("info", named.Severity); Assert.AreEqual(island.NetId, named.NetId);
        // The frame preference (ledger ped4439a665d260ef) with KiCad's default drawing sheet: R1.1 sits 14.92 mm from the page edge,
        // where the label naming the connection would cross the inner border at every stub length and on the pin itself, so R2.1,
        // whose join stub and label lie inside the border, names it whichever of the two is the first candidate (the candidates are
        // tried in component order, and the bench's identities are new on every run); nothing is drawn at R1.1.
        var components = scene.Plan.Candidate!.Engineering.Circuit.Components;
        var r1 = island.JoinCandidates.Single(c => c.Endpoint.ComponentId == components.Single(x => x.Reference == "R1").Id);
        var r2 = island.JoinCandidates.Single(c => c.Endpoint.ComponentId == components.Single(x => x.Reference == "R2").Id);
        var framed = await scene.Realize();
        CollectionAssert.AreEquivalent(new[] { GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel },
            framed.Generated.Where(g => g.PlacedPinId == r2.PlacedPinId).Select(g => g.Role).ToArray(), "R2.1, inside the border, takes the join stub.");
        Assert.IsFalse(framed.Generated.Any(g => g.PlacedPinId == r1.PlacedPinId), "Nothing is drawn at R1.1.");
        Assert.AreEqual(island.NetId, framed.Diagnostics.Single(d => d.Code == SchematicConnectionErrors.ExistingNetNamedByRealization).NetId);
    }

    [TestMethod]
    public async Task AJoinFallsBackToAnAnchorLabelThenToTheNextCandidate()
    {
        // Must-catch and guards: with the first candidate's stub room blocked but room at the pin itself, the label
        // sits on the pin; with that blocked too the next candidate is used; with no room anywhere the join fails.
        // Labels reach as far behind their anchor as KiCad draws them, so a label on the pin lies a little over its
        // own symbol's pin, and KiCad's bounds of the symbol reach past the pin's end by the target it draws on an
        // unconnected pin (PinTargetReachNm, 381,100 nm on every visible pin of the live sheets). Within that target
        // the label may lie on its own symbol, never further in front. The first candidate sits 14.92 mm from the page edge, where
        // the frame preference would move the join to the next candidate inside the drawing sheet's border
        // (AnUnlabelledConnectionIsNamedAtItsOwnPinAndReported); these frozen §6.3 (a) and §6.4 rules are checked with an editor that
        // does not measure its drawing sheet, and so has no border to prefer.
        var scene = WithDrawingSheet(Link(), null);
        var candidates = scene.Intent.Screens.Single().Islands.Single().JoinCandidates;
        Assert.HasCount(2, candidates);
        var first = scene.PinAt(candidates[0]);
        var anchored = scene.Obstacle(new(first.X - 12 * Grid, first.Y - Grid, first.X - 4 * Grid, first.Y + Grid));
        Assert.IsGreaterThan(0L, Geometry.MeasuredBehind[typeof(LocalLabel)]);
        Assert.IsNull(anchored.Geometry.LabelBehind, "The label on the pin is measured with the local label's real back extent.");
        var realization = await anchored.Realize();
        var anchorLabel = realization.Generated.Single(g => g.Role == GeneratedConnectionRole.AnchorLabel);
        Assert.AreEqual(candidates[0].PlacedPinId, anchorLabel.PlacedPinId);
        Assert.AreEqual(Generated(anchored, anchored.Intent.Screens.Single().ScreenId, GeneratedConnectionRole.AnchorLabel,
            SchematicConnectionIdentity.PinAnchorKey(candidates[0].PlacedPinId)), anchorLabel.Id);
        Assert.AreEqual(first.X, Generated<LocalLabel>(realization).Single(l => l.Id.Value == anchorLabel.Id.ToString("D")).Position.XNm);
        Assert.IsFalse(realization.Generated.Any(g => g.PlacedPinId == candidates[0].PlacedPinId && g.Role == GeneratedConnectionRole.StubWire));

        // Must-catch: the pin's own symbol reaching one grid in front of the pin (a field drawn there) leaves no room
        // for the label on the pin, so the next candidate is joined.
        var owner = candidates[0].SymbolId;
        var fronted = anchored with { Geometry = anchored.Geometry.Copy() };
        fronted.Geometry.Body[owner] = new(first.X - Grid, first.Y - 3 * Grid, first.X + 8 * Grid, first.Y + 3 * Grid);
        var next = await fronted.Realize();
        Assert.IsFalse(next.Generated.Any(g => g.PlacedPinId == candidates[0].PlacedPinId), "The label would overlap its own symbol in front of the pin.");
        Assert.IsTrue(next.Generated.Any(g => g.PlacedPinId == candidates[1].PlacedPinId && g.Role == GeneratedConnectionRole.StubLabel));
        // Must-catch: 100 nm beyond the pin's target in front of the pin is already too far, and so is half a grid (the
        // band an earlier rule allowed, which let a label cover text drawn up to 0.25 mm past the target).
        foreach (long reach in new[] { SchematicConnectionRealizer.PinTargetReachNm + 100, Policy.LabelBackToleranceNm })
        {
            var beyond = anchored with { Geometry = anchored.Geometry.Copy() };
            beyond.Geometry.Body[owner] = new(first.X - reach, first.Y - 3 * Grid, first.X + 8 * Grid, first.Y + 3 * Grid);
            Assert.IsFalse((await beyond.Realize()).Generated.Any(g => g.PlacedPinId == candidates[0].PlacedPinId), "Owner reaching " + reach + " nm past the pin.");
        }
        // Guards: the symbol ending exactly at the pin, or reaching past it exactly as far as its pin target (as every
        // visible pin of the live sheets does), admits the label on the pin.
        foreach (long reach in new long[] { 0, SchematicConnectionRealizer.PinTargetReachNm })
        {
            var flush = anchored with { Geometry = anchored.Geometry.Copy() };
            flush.Geometry.Body[owner] = new(first.X - reach, first.Y - 3 * Grid, first.X + 8 * Grid, first.Y + 3 * Grid);
            Assert.AreEqual(candidates[0].PlacedPinId, (await flush.Realize()).Generated.Single(g => g.Role == GeneratedConnectionRole.AnchorLabel).PlacedPinId,
                "Owner reaching " + reach + " nm past the pin.");
        }
        // Must-catch: the other candidate's pin drawn on this one, so another symbol of the same connection shares the anchor.
        // Only the owning symbol's bounds may hold the anchor (§6.4 rule 5) and only the owner gets the pin-target band, so
        // the label is refused on both stacked pins, whether the other symbol's bounds stop exactly at the pin or reach just
        // as far as its pin target; with their stubs blocked as well, the join has no anchor.
        var sharer = candidates[1];
        var sharing = anchored.Plan.Candidate!.Schematic.Instances.Single(s => Key(s) == sharer.SheetPathKey).Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Single(x => Guid.Parse(x.Id.Value) == sharer.SymbolId);
        foreach (long reach in new long[] { 0, SchematicConnectionRealizer.PinTargetReachNm })
        {
            var shared = anchored with { Geometry = anchored.Geometry.Copy() };
            int above = 0;
            foreach (var pin in SchematicPlacedPins.Active(sharing, sharing.Unit?.Unit ?? 1))
            {
                var pinId = Guid.Parse(pin.Id.Value);
                shared.Geometry.Place[pinId] = pinId == sharer.PlacedPinId ? first : new(first.X, first.Y - 2 * Grid * ++above);
            }
            shared.Geometry.Body[sharer.SymbolId] = new(first.X - reach, first.Y - 3 * Grid, first.X + 8 * Grid, first.Y + 3 * Grid);
            await RequireRefusal(shared, SchematicConnectionErrors.RealizationNoJoinAnchor, "no existing pin of it has room",
                "Another symbol of the connection stacked on the pin, reaching " + reach + " nm past it.");
        }

        var blocked = scene.Obstacle(new(first.X - 12 * Grid, first.Y - Grid, first.X - Grid / 2, first.Y + Grid));
        var second = await blocked.Realize();
        Assert.IsTrue(second.Generated.Any(g => g.PlacedPinId == candidates[1].PlacedPinId && g.Role == GeneratedConnectionRole.StubLabel));
        Assert.IsFalse(second.Generated.Any(g => g.PlacedPinId == candidates[0].PlacedPinId));

        var other = scene.PinAt(candidates[1]);
        var none = blocked.Obstacle(new(other.X - 12 * Grid, other.Y - Grid, other.X - Grid / 2, other.Y + Grid));
        await RequireRefusal(none, SchematicConnectionErrors.RealizationNoJoinAnchor, "no existing pin of it has room");
    }

    // Lay a pin at `at` pointing left out of a small body to its right, as the Pair test does, and every other pin of its symbol
    // two grids further down each, so the whole symbol sits on the connection grid.
    private static void LayLeft(Scene target, ConnectionPlacedPin pin, Point at)
    {
        var symbol = target.Plan.Candidate!.Schematic.Instances.Single(s => Key(s) == pin.SheetPathKey).Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Single(x => Guid.Parse(x.Id.Value) == pin.SymbolId);
        var others = SchematicPlacedPins.Active(symbol, symbol.Unit?.Unit ?? 1).Select(p => Guid.Parse(p.Id.Value)).Where(id => id != pin.PlacedPinId)
            .Order().ToArray();
        target.Geometry.Place[pin.PlacedPinId] = at;
        target.Geometry.Direction[pin.PlacedPinId] = (1, 0);
        for (int i = 0; i < others.Length; i++)
        {
            target.Geometry.Place[others[i]] = new(at.X, at.Y + 2 * Grid * (i + 1));
            target.Geometry.Direction[others[i]] = (1, 0);
        }
        target.Geometry.Body[pin.SymbolId] = new(at.X, at.Y - Grid / 4, at.X + 4 * Grid, at.Y + 2 * Grid * others.Length + Grid / 4);
    }

    [TestMethod]
    public async Task TwoNewPinsAreJoinedByWiresNamedByOneLabelAtTheFirstPin()
    {
        // CN-1 §7: two test points, ten grids apart one above the other on the connection grid and both pointing left, form the
        // new net X. The tree grows from the upper pin (first by escape y), whose two-grid stub carries the one label X; the
        // lower pin leaves by one grid, turns up and lands on the inside of that stub, where the wire is split and a junction
        // drawn. Every wire, the junction and the label have their §6.7 identities.
        var scene = Pair();
        var stubbed = scene.Intent.Screens.Single().Islands.Single().Members.Where(m => m.RequiresStub).Select(m => m.Pin).ToArray();
        var (upper, lower) = (stubbed[0], stubbed[1]);
        LayLeft(scene, upper, new(80 * Grid, 60 * Grid));
        LayLeft(scene, lower, new(80 * Grid, 70 * Grid));
        var (realization, measured) = await scene.RealizeMeasured();
        var outcome = realization.Outcomes.Single();
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, outcome.Strategy, outcome.FallbackReason);
        Assert.IsNull(outcome.FallbackReason);
        var problems = RoutedProblems(realization, scene.Intent, measured, Policy);
        Assert.IsEmpty(problems, string.Join("\n", problems));
        // Wires by segment ordinal (their identity order): by start, then end, each wire starting at its lower-left end.
        string netKey = SchematicConnectionIdentity.NetKey([upper.PlacedPinId, lower.PlacedPinId]);
        var screen = scene.Intent.Screens.Single().ScreenId;
        Guid RouteId(int ordinal) => SchematicConnectionIdentity.Generated(scene.Intent.OriginId, scene.Intent.NativeRevision, scene.Intent.DesiredSha256,
            screen, GeneratedConnectionRole.RouteWire, SchematicConnectionIdentity.RouteAnchorKey(netKey, ordinal), ordinal);
        var byId = Generated<SchematicLine>(realization).ToDictionary(w => Guid.Parse(w.Id.Value));
        Assert.HasCount(4, byId);
        var wires = Enumerable.Range(0, 4).Select(ordinal => byId[RouteId(ordinal)]).ToArray();
        (long, long, long, long) Wire(SchematicLine w) => (w.Start.XNm / Grid, w.Start.YNm / Grid, w.End.XNm / Grid, w.End.YNm / Grid);
        CollectionAssert.AreEqual(new[] { (78L, 60L, 79L, 60L), (79L, 60L, 79L, 70L), (79L, 60L, 80L, 60L), (79L, 70L, 80L, 70L) },
            wires.Select(Wire).ToArray(), "Wires in identity order, split at the junction.");
        Assert.IsTrue(wires.All(w => w.Type == SchematicLineType.SltWire && w.Locked == LockedState.LsUnlocked));
        var junction = Generated<Junction>(realization).Single();
        Assert.AreEqual(new Vector2 { XNm = 79 * Grid, YNm = 60 * Grid }, junction.Position);
        var label = Generated<LocalLabel>(realization).Single();
        Assert.AreEqual("X", label.Text.Text_);
        Assert.AreEqual(new Vector2 { XNm = 78 * Grid, YNm = 60 * Grid }, label.Position);
        Assert.AreEqual(SchematicLabelSpinStyle.SlssLeft, label.SpinStyle);
        Assert.IsTrue(label.FieldsAutoplaced);
        // §6.7: the junction by net key and position, the label by its pin.
        Assert.AreEqual(Generated(scene, screen, GeneratedConnectionRole.Junction, SchematicConnectionIdentity.JunctionAnchorKey(netKey, 79 * Grid, 60 * Grid))
            .ToString("D"), junction.Id.Value);
        var named = realization.Generated.Single(g => g.Role == GeneratedConnectionRole.StubLabel);
        Assert.AreEqual(upper.PlacedPinId, named.PlacedPinId);
        Assert.AreEqual(Generated(scene, screen, GeneratedConnectionRole.StubLabel, SchematicConnectionIdentity.PinAnchorKey(upper.PlacedPinId)), named.Id);
        CollectionAssert.AreEquivalent(realization.Generated.Select(g => g.Id).ToArray(), outcome.GeneratedIds.ToArray());
        Assert.AreEqual(4, realization.Generated.Count(g => g.Role == GeneratedConnectionRole.RouteWire));
        // The batch creates exactly these items and ends with the assertion; the same measurements give the same batch (I9).
        Assert.IsTrue(realization.Operations.Take(realization.Operations.Count - 1).All(o => o.Create is not null));
        Assert.AreEqual(6, realization.Operations.Count - 1);
        Assert.IsNotNull(realization.Operations[^1].AssertConnectivity);
        var again = await scene.Realize();
        CollectionAssert.AreEqual(realization.Operations.Select(o => o.ToByteString()).ToArray(), again.Operations.Select(o => o.ToByteString()).ToArray());
        string xml = SchematicDesignXml.Write(realization.Design, []);
        Assert.AreEqual(xml, SchematicDesignXml.Write(SchematicDesignXml.Read(xml, []), []), "Wires and junctions round-trip through the design file.");

        // E4 of decision nfa2005d67574bfea: the root is the first pin in order whose name-label stub covers no other pin's escape,
        // by its wire as well as by its label. The upper pin points down and its own symbol reaches three fifths of a grid past it,
        // so its corridor is two grids and its label stub three. Must-catch: the lower pin sits one grid right of that stub and
        // leaves leftwards onto it, so the stub's wire (not its label) would cover the lower pin's escape; the tree grows from the
        // lower pin, which carries the label, and the upper pin joins its stub. Guard: with the lower pin away from that stub,
        // the upper pin, first in order, is the root.
        async Task<SchematicConnectionRealization> Rooted(Point second, bool lowerDown = false, long? labelHalf = null)
        {
            var laid = scene with { Geometry = scene.Geometry.Copy() };
            laid.Geometry.LabelHalf = labelHalf;
            Lay(laid, upper, new(80 * Grid, 40 * Grid), (0, 1));
            laid.Geometry.Body[upper.SymbolId] = new(80 * Grid - Grid / 4, 36 * Grid, 80 * Grid + Grid / 4, 40 * Grid + 3 * Grid / 5);
            if (lowerDown) Lay(laid, lower, second, (0, 1));
            else LayLeft(laid, lower, second);
            var (drawn, drawnMeasured) = await laid.RealizeMeasured();
            Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, drawn.Outcomes.Single().Strategy, drawn.Outcomes.Single().FallbackReason);
            var found = RoutedProblems(drawn, laid.Intent, drawnMeasured, Policy);
            Assert.IsEmpty(found, string.Join("\n", found));
            return drawn;
        }
        var covering = await Rooted(new(81 * Grid, 42 * Grid));
        Assert.AreEqual(lower.PlacedPinId, covering.Generated.Single(g => g.Role == GeneratedConnectionRole.StubLabel).PlacedPinId,
            "The upper pin's label stub would run across the lower pin's escape, so the lower pin is the root.");
        Assert.AreEqual(new Vector2 { XNm = 80 * Grid, YNm = 42 * Grid }, Generated<Junction>(covering).Single().Position,
            "The upper pin joins the root's stub where its corridor meets it.");
        var apart = await Rooted(new(86 * Grid, 50 * Grid));
        Assert.AreEqual(upper.PlacedPinId, apart.Generated.Single(g => g.Role == GeneratedConnectionRole.StubLabel).PlacedPinId,
            "With nothing on its label stub, the first pin in order is the root.");
        Assert.AreEqual(new Vector2 { XNm = 80 * Grid, YNm = 43 * Grid }, Generated<LocalLabel>(apart).Single().Position,
            "The root's label sits at the end of its shortest admissible stub, one grid past its corridor.");
        // E4 asks for a pin whose name-label stub covers no other pin's escape, at any admissible length, not only the shortest.
        // Must-catch: with labels two and a half grids across (as a larger text would draw them) and the lower pin one grid right
        // of the upper pin's stub, leaving downwards, the label on the upper pin's three-grid stub reaches over the lower pin's
        // escape, but its four-grid stub carries the label past it and covers nothing: the upper pin, first in order, is still the
        // root, with that longer stub, and the lower pin joins its stub one grid above the label.
        var wide = await Rooted(new(81 * Grid, 42 * Grid), lowerDown: true, labelHalf: 5 * Grid / 4);
        Assert.AreEqual(upper.PlacedPinId, wide.Generated.Single(g => g.Role == GeneratedConnectionRole.StubLabel).PlacedPinId,
            "A longer stub of the first pin carries the label past the lower pin's escape, so the first pin is still the root.");
        Assert.AreEqual(new Vector2 { XNm = 80 * Grid, YNm = 44 * Grid }, Generated<LocalLabel>(wide).Single().Position,
            "The label sits at the end of the four-grid stub, past the lower pin's escape.");
        Assert.AreEqual(new Vector2 { XNm = 80 * Grid, YNm = 43 * Grid }, Generated<Junction>(wide).Single().Position,
            "The lower pin joins the root's stub one grid above the label.");
    }

    [TestMethod]
    public async Task AConnectionNoRouteFitsFallsBackToLabelStubsAndSaysWhy()
    {
        var scene = Pair();
        var stubbed = scene.Intent.Screens.Single().Islands.Single().Members.Where(m => m.RequiresStub).Select(m => m.Pin).ToArray();
        LayLeft(scene, stubbed[0], new(80 * Grid, 60 * Grid));
        LayLeft(scene, stubbed[1], new(80 * Grid, 70 * Grid));
        // Must-catch: a foreign junction within the clearance of the lower pin's one-grid escape leaves it no way out, though its
        // two-grid stub and label still fit: the connection is drawn with label stubs and says why.
        var blocked = scene.Junction(new(79 * Grid, 70 * Grid + Policy.ClearanceNm));
        var (fallback, measured) = await blocked.RealizeMeasured();
        var outcome = fallback.Outcomes.Single();
        Assert.AreEqual(ConnectionRealizationStrategy.LabelStub, outcome.Strategy);
        // The reason a person reads names the pin by its reference, never by its component's identity.
        StringAssert.Contains(outcome.FallbackReason, "pin " + PinName(blocked.Plan.Candidate!, stubbed[1].Endpoint) + " has no room to leave its symbol by one grid step");
        Assert.IsFalse(outcome.FallbackReason!.Contains(stubbed[1].Endpoint.ComponentId.ToString("D"), StringComparison.Ordinal), outcome.FallbackReason);
        Assert.HasCount(2, Generated<SchematicLine>(fallback)); Assert.HasCount(2, Generated<LocalLabel>(fallback));
        Assert.IsEmpty(Generated<Junction>(fallback));
        Assert.IsTrue(fallback.Generated.All(g => g.Role is GeneratedConnectionRole.StubWire or GeneratedConnectionRole.StubLabel));
        Assert.IsEmpty(RoutedProblems(fallback, blocked.Intent, measured, Policy));
        // Guard: 100 nm further away the junction is no contact and the pins are routed.
        var clear = await scene.Junction(new(79 * Grid, 70 * Grid + Policy.ClearanceNm + 100)).Realize();
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, clear.Outcomes.Single().Strategy, clear.Outcomes.Single().FallbackReason);
        // The budget: a search that may expand no node falls back as well.
        var (starved, _) = await scene.RealizeMeasured(new(0, 4));
        StringAssert.Contains(starved.Outcomes.Single().FallbackReason, "expanded more than 0 grid nodes");
        Assert.HasCount(2, Generated<SchematicLine>(starved));
    }

    // Lay a pin at `at` leaving its symbol along `outward`, with a thin body four grids long behind it; every other pin of its
    // symbol sits at the far end of that body and leaves the other way.
    private static void Lay(Scene target, ConnectionPlacedPin pin, Point at, (int Dx, int Dy) outward)
    {
        var symbol = target.Plan.Candidate!.Schematic.Instances.Single(s => Key(s) == pin.SheetPathKey).Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Single(x => Guid.Parse(x.Id.Value) == pin.SymbolId);
        var (bx, by) = (-outward.Dx, -outward.Dy);
        var far = new Point(at.X + bx * 4 * Grid, at.Y + by * 4 * Grid);
        target.Geometry.Place[pin.PlacedPinId] = at;
        target.Geometry.Direction[pin.PlacedPinId] = (bx, by);
        foreach (var other in SchematicPlacedPins.Active(symbol, symbol.Unit?.Unit ?? 1).Select(p => Guid.Parse(p.Id.Value)).Where(id => id != pin.PlacedPinId))
        {
            target.Geometry.Place[other] = far;
            target.Geometry.Direction[other] = (-bx, -by);
        }
        long across = Grid / 4;
        target.Geometry.Body[pin.SymbolId] = new(Math.Min(at.X, far.X) - (bx == 0 ? across : 0), Math.Min(at.Y, far.Y) - (by == 0 ? across : 0),
            Math.Max(at.X, far.X) + (bx == 0 ? across : 0), Math.Max(at.Y, far.Y) + (by == 0 ? across : 0));
    }

    private static Scene WithDrawingSheet(Scene scene, SchematicWiringDrawingSheet? sheet)
    {
        var copy = scene with { Geometry = scene.Geometry.Copy() };
        copy.Geometry.DrawingSheet = sheet;
        return copy;
    }

    [TestMethod]
    public void TheRouteRegionIsTheDrawingSheetsInnerBorderWithItsTitleBlockKeptOut()
    {
        // CN-1 §7 region (the erratum lane 2A reported): the inside of the innermost border KiCad draws, less the clearance; the
        // title block's box, rows and texts merge into one keep-out; marks between the borders are outside the region already.
        long c = Policy.ClearanceNm;
        var page = new SchematicConnectionRealizer.Box(0, 0, 297_000_000, 210_000_000);
        var (region, keepOuts) = SchematicConnectionRealizer.RouteArea(page, Geometry.DefaultDrawingSheet(), c);
        Assert.AreEqual(new SchematicConnectionRealizer.Box(12_000_000 + c, 12_000_000 + c, 285_000_000 - c, 198_000_000 - c), region);
        CollectionAssert.AreEqual(new[] { new SchematicConnectionRealizer.Box(177_000_000, 166_000_000, 285_000_000, 198_000_000) }, keepOuts.ToArray());
        // Must-catch: a logo drawn inside the frame is kept out too, and a rectangle around less than half the page is art, not a border.
        var decorated = Geometry.DefaultDrawingSheet();
        decorated.Items.Add(new SchematicWiringDrawingSheetItem { Kind = SchematicWiringDrawingSheetItemKind.SwrDrawingSheetItemBitmap,
            Bounds = new() { Position = new() { XNm = 20_000_000, YNm = 20_000_000 }, Size = new() { XNm = 15_000_000, YNm = 10_000_000 } } });
        decorated.Items.Add(new SchematicWiringDrawingSheetItem { Kind = SchematicWiringDrawingSheetItemKind.SwrDrawingSheetItemRectangle,
            Bounds = new() { Position = new() { XNm = 100_000_000, YNm = 60_000_000 }, Size = new() { XNm = 100_000_000, YNm = 80_000_000 } } });
        var (same, more) = SchematicConnectionRealizer.RouteArea(page, decorated, c);
        Assert.AreEqual(region, same, "Neither the logo nor the small rectangle is a border.");
        CollectionAssert.AreEqual(new[] { new SchematicConnectionRealizer.Box(20_000_000, 20_000_000, 35_000_000, 30_000_000),
            new SchematicConnectionRealizer.Box(100_000_000, 60_000_000, 200_000_000, 140_000_000),
            new SchematicConnectionRealizer.Box(177_000_000, 166_000_000, 285_000_000, 198_000_000) }, more.ToArray());
        // An editor that does not measure its drawing sheet: KiCad's default inner border and the bottom 50 mm kept for the title block.
        var (fallback, none) = SchematicConnectionRealizer.RouteArea(page, null, c);
        Assert.AreEqual(new SchematicConnectionRealizer.Box(12_000_000 + c, 12_000_000 + c, 285_000_000 - c, 160_000_000 - c), fallback);
        Assert.IsEmpty(none);
    }

    [TestMethod]
    public async Task RoutedWiresKeepInsideTheDrawingSheetFrameAndClearOfItsTitleBlock()
    {
        // CN-1 §7 region on whole realizations. The synthetic sheets report KiCad's default A4 drawing sheet (inner border 12 mm
        // in, title block 177-285 mm by 166-198 mm), which the 2-grid page inset alone would let wires run over.
        var scene = Pair();
        var pins = scene.Intent.Screens.Single().Islands.Single().Members.Where(m => m.RequiresStub).Select(m => m.Pin).ToArray();
        async Task<(ConnectionIslandOutcome Outcome, SchematicConnectionRealization Realization, IReadOnlyDictionary<string, SchematicPlacementGeometry> Measured)> Drawn(
            Scene at, Point first, Point second, (int, int) outward)
        {
            var laid = at with { Geometry = at.Geometry.Copy() };
            Lay(laid, pins[0], first, outward);
            Lay(laid, pins[1], second, outward);
            var (realization, measured) = await laid.RealizeMeasured();
            return (realization.Outcomes.Single(), realization, measured);
        }
        // Must-catch: two pins just above the title block, leaving downwards: their escapes reach within the clearance of it, so
        // they are drawn with label stubs and say why. Guard: on a drawing sheet with the same borders and no title block they
        // are routed.
        var (aboveTitle, _, _) = await Drawn(scene, new(150 * Grid, 130 * Grid), new(190 * Grid, 130 * Grid), (0, 1));
        Assert.AreEqual(ConnectionRealizationStrategy.LabelStub, aboveTitle.Strategy);
        StringAssert.Contains(aboveTitle.FallbackReason, "the drawing sheet's title block");
        var bordersOnly = Geometry.DefaultDrawingSheet();
        foreach (var item in bordersOnly.Items.Where(i => i.Bounds.Position.XNm >= 177_000_000 || i.Bounds.Position.YNm >= 166_000_000).ToArray())
            bordersOnly.Items.Remove(item);
        Assert.HasCount(4, bordersOnly.Items);
        var (untitled, untitledRealization, untitledMeasured) = await Drawn(WithDrawingSheet(scene, bordersOnly), new(150 * Grid, 130 * Grid), new(190 * Grid, 130 * Grid), (0, 1));
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, untitled.Strategy, untitled.FallbackReason);
        Assert.IsEmpty(RoutedProblems(untitledRealization, scene.Intent, untitledMeasured, Policy));
        // E1 of decision nfa2005d67574bfea: the label naming a routed connection keeps the clearance from the title block too,
        // not only its wire. Must-catch: two pins three grids higher, leaving downwards: every wire keeps clear, but the label at
        // the end of the two-grid stub would end 0.37 mm inside that clearance (without overlapping the title block itself), and
        // every longer stub comes nearer still, so no pin can carry it and the connection is drawn with label stubs. Guard: one
        // grid higher the label keeps the clearance and the pins are routed, the label's whole box clear of the inflated keep-out.
        var (nearTitle, _, _) = await Drawn(scene, new(150 * Grid, 127 * Grid), new(170 * Grid, 127 * Grid), (0, 1));
        Assert.AreEqual(ConnectionRealizationStrategy.LabelStub, nearTitle.Strategy);
        StringAssert.Contains(nearTitle.FallbackReason, "has no room at any of its new pins");
        StringAssert.Contains(nearTitle.FallbackReason, "the drawing sheet's title block");
        var (clearOfTitle, clearRealization, clearMeasured) = await Drawn(scene, new(150 * Grid, 126 * Grid), new(170 * Grid, 126 * Grid), (0, 1));
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, clearOfTitle.Strategy, clearOfTitle.FallbackReason);
        Assert.IsEmpty(RoutedProblems(clearRealization, scene.Intent, clearMeasured, Policy));
        var clearLabel = scene.Geometry.LabelBox(Generated<LocalLabel>(clearRealization).Single());
        Assert.IsLessThan(166_000_000 - Policy.ClearanceNm, clearLabel.B, "The label keeps the clearance above the title block.");
        // The creation journey's own check of KiCad's drawing (NativeSessionTests.DrawingSheetProblems), which judges each routed
        // name label by the box KiCad measures for it. Guard: this routed connection, its label by the box measured for it, passes.
        // Must-catch: that box one nanometre inside the clearance of the title block, or of the inner frame, is reported; exactly
        // at the clearance from the frame it is not (a label may touch the frame shrunk by the clearance, E1).
        long c1 = Policy.ClearanceNm;
        var (sheetPath, sheetGeometry) = clearMeasured.Single();
        var routedWires = Generated<SchematicLine>(clearRealization).ToArray();
        var routedJunctions = Generated<Junction>(clearRealization).ToArray();
        List<string> SheetProblems(Rect label) => NativeSessionTests.DrawingSheetProblems(sheetPath, sheetGeometry, routedWires, routedJunctions,
            [("name label", new Box2 { Position = new() { XNm = label.L, YNm = label.T }, Size = new() { XNm = label.R - label.L, YNm = label.B - label.T } })],
            c1, Policy.PageInsetNm).Problems;
        Assert.IsEmpty(SheetProblems(clearLabel), string.Join("; ", SheetProblems(clearLabel)));
        Rect Moved(long dx, long dy) => new(clearLabel.L + dx, clearLabel.T + dy, clearLabel.R + dx, clearLabel.B + dy);
        var intoTitle = SheetProblems(Moved(0, 166_000_000 - c1 + 1 - clearLabel.B));
        Assert.HasCount(1, intoTitle, string.Join("; ", intoTitle));
        StringAssert.Contains(intoTitle[0], "within the clearance of the drawing sheet's title block");
        Assert.IsEmpty(SheetProblems(Moved(0, 166_000_000 - c1 - 1 - clearLabel.B)), "One nanometre further up the label keeps the clearance.");
        long up = 100_000_000 - clearLabel.T;
        var intoFrame = SheetProblems(Moved(285_000_000 - c1 + 1 - clearLabel.R, up));
        Assert.HasCount(1, intoFrame, string.Join("; ", intoFrame));
        StringAssert.Contains(intoFrame[0], "is not inside the drawing sheet's frame");
        Assert.IsEmpty(SheetProblems(Moved(285_000_000 - c1 - clearLabel.R, up)), "A label may touch the frame shrunk by the clearance.");
        // Must-catch: two pins near the top border, leaving upwards: the label naming the connection would reach past the inner
        // border at every stub length, so they are drawn with label stubs (which the page inset still admits) and say why.
        // Guard: one grid lower the label fits inside the border and the pins are routed.
        var (atTop, topRealization, _) = await Drawn(scene, new(80 * Grid, 13 * Grid), new(100 * Grid, 13 * Grid), (0, -1));
        Assert.AreEqual(ConnectionRealizationStrategy.LabelStub, atTop.Strategy);
        StringAssert.Contains(atTop.FallbackReason, "outside the drawing sheet's frame");
        Assert.HasCount(2, Generated<SchematicLine>(topRealization));
        var (belowTop, belowRealization, belowMeasured) = await Drawn(scene, new(80 * Grid, 14 * Grid), new(100 * Grid, 14 * Grid), (0, -1));
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, belowTop.Strategy, belowTop.FallbackReason);
        Assert.IsEmpty(RoutedProblems(belowRealization, scene.Intent, belowMeasured, Policy));
        Assert.AreEqual(pins[0].PlacedPinId, belowRealization.Generated.Single(g => g.Role == GeneratedConnectionRole.StubLabel).PlacedPinId,
            "The first pin carries the label.");
        // A first pin whose label would leave the frame does not stop the route: the tree grows from the next pin in order whose
        // label fits, which carries it.
        var (rerooted, rerootedRealization, rerootedMeasured) = await Drawn(scene, new(80 * Grid, 13 * Grid), new(100 * Grid, 30 * Grid), (0, -1));
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, rerooted.Strategy, rerooted.FallbackReason);
        Assert.AreEqual(pins[1].PlacedPinId, rerootedRealization.Generated.Single(g => g.Role == GeneratedConnectionRole.StubLabel).PlacedPinId,
            "The second pin carries the label.");
        Assert.IsEmpty(RoutedProblems(rerootedRealization, scene.Intent, rerootedMeasured, Policy));
        // An editor that does not measure its drawing sheet: the bottom 50 mm stay free of wires. Must-catch: pins at 165 mm are
        // not routed. Guard: with the drawing sheet measured, the same pins (well left of the title block) are.
        var (unmeasured, _, _) = await Drawn(WithDrawingSheet(scene, null), new(80 * Grid, 130 * Grid), new(80 * Grid, 136 * Grid), (-1, 0));
        Assert.AreEqual(ConnectionRealizationStrategy.LabelStub, unmeasured.Strategy);
        StringAssert.Contains(unmeasured.FallbackReason, "outside the drawing sheet's frame");
        var (measuredLow, lowRealization, lowMeasured) = await Drawn(scene, new(80 * Grid, 130 * Grid), new(80 * Grid, 136 * Grid), (-1, 0));
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, measuredLow.Strategy, measuredLow.FallbackReason);
        Assert.IsEmpty(RoutedProblems(lowRealization, scene.Intent, lowMeasured, Policy));
    }

    // Two-pin nets TP1-TP2 named `names[0]`, TP3-TP4 named `names[1]` and so on, with fixed identities in that order, so that the
    // realizer draws their islands in that order (§6.3 draws islands in NetId order).
    private static Scene Nets(params string[] names)
    {
        var bench = new Bench();
        Guid tp = bench.Part("TP", Passive("1"));
        var nets = names.Select((name, i) => new CircuitNet(Guid.Parse($"7e57f1c5-0000-4000-8000-0000000000{i + 1:x2}"), name,
            [new(bench.Component(tp, "TP" + (2 * i + 1)), "1"), new(bench.Component(tp, "TP" + (2 * i + 2)), "1")])).ToArray();
        return Scene.Of(bench, WithFormatting(bench.State([])), design => WithNets(design, nets));
    }

    private static SchematicWiringDrawingSheet WithArt(SchematicWiringDrawingSheet sheet, Rect art)
    {
        sheet.Items.Add(new SchematicWiringDrawingSheetItem { Kind = SchematicWiringDrawingSheetItemKind.SwrDrawingSheetItemText,
            Bounds = new() { Position = new() { XNm = art.L, YNm = art.T }, Size = new() { XNm = art.R - art.L, YNm = art.B - art.T } } });
        return sheet;
    }

    [TestMethod]
    public async Task LabelStubsPreferTheInsideOfTheDrawingSheetsInnerBorder()
    {
        // The frame preference for label stubs (ledger ped4439a665d260ef; the CN-1 §6.3/§6.4 erratum lane 2A reported): a label stub
        // and its label are tried inside the inner border of the drawing sheet KiCad measures and clear of its title block first,
        // every stub length in the §6.3 (f) order; a stub that fits only at the page inset is replaced by a label on the pin itself
        // when that label fits inside the border; otherwise the page inset decides as before.
        // The live proof is the psu-cpu-connected journey (every generated stub and label inside the border KiCad draws); these
        // cases need geometry a live editor cannot be steered into, so they are synthetic. The routing budget is zero, so every
        // net falls back to label stubs (§7), as a net with no route does. The synthetic sheets report KiCad's default A4 drawing
        // sheet: inner border 12 mm in, title block 177-285 mm by 166-198 mm.
        long near = 13 * Grid;
        var scene = Nets("A1", "C1", "D1");
        var a = Member(scene, "TP1").Pin; var c = Member(scene, "TP3").Pin; var d = Member(scene, "TP5").Pin;
        Lay(scene, a, new(near, 48 * Grid), (-1, 0));
        Lay(scene, Member(scene, "TP2").Pin, new(100 * Grid, 60 * Grid), (-1, 0));
        var cAt = new Point(80 * Grid, 100 * Grid);
        Lay(scene, c, cAt, (-1, 0));
        Lay(scene, Member(scene, "TP4").Pin, new(100 * Grid, 70 * Grid), (-1, 0));
        var dAt = new Point(158 * Grid, 129 * Grid);
        Lay(scene, d, dAt, (0, 1));
        Lay(scene, Member(scene, "TP6").Pin, new(100 * Grid, 80 * Grid), (-1, 0));
        // Drawing-sheet art (a text) just below C1's stub line, 3 to 5 mm in front of TP3.1: the labels of the 2, 3 and 4 grid stubs
        // overlap it, the 6 grid stub's label is past it, and no stub wire meets it.
        var framed = WithDrawingSheet(scene, WithArt(Geometry.DefaultDrawingSheet(), new(cAt.X - 5_000_000, cAt.Y + 200_000, cAt.X - 3_000_000, cAt.Y + 2_000_000)));
        var (realization, measured) = await framed.RealizeMeasured(new(0, 4));
        var screen = framed.Intent.Screens.Single().ScreenId;
        Assert.IsTrue(realization.Outcomes.All(o => o.Strategy == ConnectionRealizationStrategy.LabelStub && o.FallbackReason is not null));
        var labels = Generated<LocalLabel>(realization);
        var wires = Generated<SchematicLine>(realization);
        Assert.HasCount(6, labels);
        // Must-catch: TP1.1 is 13 grids from the page edge, leaving left. Every stub would carry its label across the inner border
        // (the page inset alone admits the two-grid one), so its label sits on the pin itself, inside the border, with no wire.
        // TP3.1 and TP5.1 below show the other two orders: a longer stub inside the border first, the page inset last.
        var onPin = realization.Generated.Single(g => g.PlacedPinId == a.PlacedPinId);
        Assert.AreEqual(GeneratedConnectionRole.AnchorLabel, onPin.Role);
        Assert.AreEqual(Generated(framed, screen, GeneratedConnectionRole.AnchorLabel, SchematicConnectionIdentity.PinAnchorKey(a.PlacedPinId)), onPin.Id);
        var aLabel = labels.Single(l => l.Id.Value == onPin.Id.ToString("D"));
        Assert.AreEqual(new Vector2 { XNm = near, YNm = 48 * Grid }, aLabel.Position);
        Assert.AreEqual(SchematicLabelSpinStyle.SlssLeft, aLabel.SpinStyle);
        Assert.IsTrue(framed.Geometry.LabelBox(aLabel).L >= 12_000_000, "The label on TP1.1 lies inside the inner border.");
        // Must-catch: a longer stub inside the border comes before a label on the pin: TP3.1's labels at 2, 3 and 4 grids overlap the
        // art, so it takes the 6-grid stub, although a label on its pin would fit as well.
        var cStub = StubOf(realization, c.PlacedPinId);
        Assert.AreEqual(cAt.X - 6 * Grid, cStub.End.XNm);
        Assert.IsTrue(labels.Any(l => l.Position.Equals(cStub.End)));
        // TP5.1 leaves downwards into the title block, where no stub and no label on the pin fit: it keeps the two-grid stub the page
        // inset admits, and the sheet's diagnostic names it.
        var dStub = StubOf(realization, d.PlacedPinId);
        Assert.AreEqual(dAt.Y + 2 * Grid, dStub.End.YNm);
        var reservations = realization.Diagnostics.Single(x => x.Code == SchematicConnectionErrors.RealizationPageReservationsUnspecified).Message;
        StringAssert.Contains(reservations, "the label stub of pin " + PinName(framed.Plan.Candidate!, d.Endpoint) + " has no room inside the border");
        Assert.IsFalse(reservations.Contains("TP1.", StringComparison.Ordinal) || reservations.Contains("TP3.", StringComparison.Ordinal), reservations);
        // The second pins, well inside the border, keep their two-grid stubs.
        foreach (var other in new[] { "TP2", "TP4", "TP6" })
            Assert.AreEqual(2 * Grid, Math.Abs(StubOf(realization, Member(framed, other).Pin.PlacedPinId).End.XNm - framed.PinAt(Member(framed, other).Pin).X), other);
        // The creation journey's own check of KiCad's drawing (NativeSessionTests.LabelStubDrawingSheetProblems) on these items, each
        // label by its measured box: it reports TP5.1's stub and label, which meet the title block, and nothing else.
        var (sheetPath, sheetGeometry) = measured.Single();
        var boxes = labels.Select(l => (l.Text.Text_ + " at " + l.Position, Box(framed.Geometry.LabelBox(l)))).ToArray();
        var reported = NativeSessionTests.LabelStubDrawingSheetProblems(sheetPath, sheetGeometry, wires, boxes).Problems;
        Assert.HasCount(2, reported, string.Join("; ", reported));
        Assert.IsTrue(reported.Any(p => p.Contains("stub " + dStub.Id.Value, StringComparison.Ordinal) && p.Contains("title block", StringComparison.Ordinal)));
        Assert.IsTrue(reported.Any(p => p.Contains("D1 at", StringComparison.Ordinal) && p.Contains("overlaps the drawing sheet's title block", StringComparison.Ordinal)));
        // Must-catch for that check: the label TP1.1 would carry at the end of the two-grid stub crosses the border.
        var crossing = labels.Single(l => l.Id.Equals(aLabel.Id)).Clone();
        crossing.Position = new() { XNm = near - 2 * Grid, YNm = 48 * Grid };
        var outside = NativeSessionTests.LabelStubDrawingSheetProblems(sheetPath, sheetGeometry, [], [("A1 on a stub", Box(framed.Geometry.LabelBox(crossing)))]).Problems;
        Assert.HasCount(1, outside, string.Join("; ", outside));
        StringAssert.Contains(outside[0], "is not inside the drawing sheet's inner border");
        // Guard: an editor that does not measure its drawing sheet gives nothing to prefer: every pin keeps its two-grid stub at the
        // page inset, exactly as the frozen §6.3 draws it, and the diagnostic says why.
        var (plain, _) = await WithDrawingSheet(scene, null).RealizeMeasured(new(0, 4));
        Assert.HasCount(6, Generated<SchematicLine>(plain));
        Assert.IsTrue(plain.Generated.All(g => g.Role is GeneratedConnectionRole.StubWire or GeneratedConnectionRole.StubLabel));
        Assert.AreEqual(near - 2 * Grid, StubOf(plain, a.PlacedPinId).End.XNm);
        Assert.AreEqual(cAt.X - 2 * Grid, StubOf(plain, c.PlacedPinId).End.XNm);
        StringAssert.Contains(plain.Diagnostics.Single(x => x.Code == SchematicConnectionErrors.RealizationPageReservationsUnspecified).Message,
            "did not measure the drawing sheet");
        // Determinism (I9).
        var again = await framed.RealizeMeasured(new(0, 4));
        CollectionAssert.AreEqual(realization.Operations.Select(o => o.ToByteString()).ToArray(), again.Realization.Operations.Select(o => o.ToByteString()).ToArray());

        static Box2 Box(Rect r) => new() { Position = new() { XNm = r.L, YNm = r.T }, Size = new() { XNm = r.R - r.L, YNm = r.B - r.T } };
    }

    [TestMethod]
    public async Task TheFramePreferenceNeverRefusesWhatThePageInsetDraws()
    {
        // The frame preference must never refuse a sheet that label stubs at the page inset alone would draw: when anything on a
        // sheet has no room while its label stubs are tried inside the border first, the whole sheet is drawn again at the page inset
        // alone, exactly as without the preference. TP1.1 (net A1, drawn first) sits 13 grids from the page edge leaving left, so
        // inside the border only a label on the pin fits; TP3.1 (net B1) sits one grid left of it and a little below, leaving up.
        // Zero routing budget: both nets fall back to label stubs.
        long near = 13 * Grid, row = 48 * Grid, labelLength = 3 * Grid * 3 / 4;
        var scene = Nets("A1", "B1");
        var a = Member(scene, "TP1").Pin; var b = Member(scene, "TP3").Pin;
        var bAt = new Point(near - Grid, row + 100_000 + labelLength + 2 * Grid);
        Lay(scene, a, new(near, row), (-1, 0));
        Lay(scene, Member(scene, "TP2").Pin, new(100 * Grid, 60 * Grid), (-1, 0));
        Lay(scene, b, bAt, (0, -1));
        Lay(scene, Member(scene, "TP4").Pin, new(100 * Grid, 70 * Grid), (-1, 0));
        // Must-catch: every stub of TP3.1 ends in a label that the label on TP1.1 overlaps (or crosses it), while its two-grid stub
        // clears TP1.1's own two-grid stub and label 0.1 mm below them. The sheet is drawn at the page inset alone instead: nothing
        // is refused, TP1.1 keeps the two-grid stub, the batch is exactly the one an editor without a measured drawing sheet gets,
        // and the diagnostic says why.
        var (drawn, _) = await scene.RealizeMeasured(new(0, 4));
        Assert.IsTrue(drawn.Generated.All(g => g.Role is GeneratedConnectionRole.StubWire or GeneratedConnectionRole.StubLabel));
        Assert.AreEqual(near - 2 * Grid, StubOf(drawn, a.PlacedPinId).End.XNm, "TP1.1 keeps the two-grid stub of the page inset.");
        Assert.AreEqual(bAt.Y - 2 * Grid, StubOf(drawn, b.PlacedPinId).End.YNm);
        StringAssert.Contains(drawn.Diagnostics.Single(x => x.Code == SchematicConnectionErrors.RealizationPageReservationsUnspecified).Message,
            "left something on the sheet without room");
        var (unmeasured, _) = await WithDrawingSheet(scene, null).RealizeMeasured(new(0, 4));
        CollectionAssert.AreEqual(unmeasured.Operations.Select(o => o.ToByteString()).ToArray(), drawn.Operations.Select(o => o.ToByteString()).ToArray(),
            "The page inset alone draws exactly what an editor without a measured drawing sheet gets.");
        // Guard: one grid lower, TP3.1's two-grid stub and label clear the label on TP1.1, so nothing is drawn again: TP1.1's label
        // stays on its pin inside the border and TP3.1 keeps its two-grid stub.
        var lower = scene with { Geometry = scene.Geometry.Copy() };
        Lay(lower, b, new(bAt.X, bAt.Y + Grid), (0, -1));
        var (roomy, _) = await lower.RealizeMeasured(new(0, 4));
        Assert.AreEqual(GeneratedConnectionRole.AnchorLabel, roomy.Generated.Single(g => g.PlacedPinId == a.PlacedPinId).Role);
        Assert.AreEqual(bAt.Y + Grid - 2 * Grid, StubOf(roomy, b.PlacedPinId).End.YNm);
        StringAssert.Contains(roomy.Diagnostics.Single(x => x.Code == SchematicConnectionErrors.RealizationPageReservationsUnspecified).Message,
            "so does every label stub and label.");
    }

    [TestMethod]
    public async Task RoutedWiresKeepClearOfTheirOwnSymbolsFields()
    {
        // CN-1 §7 escape corridor: a pin leaves its own symbol's measured bounds straight out, and never within the clearance of
        // that symbol's own visible field text, which KiCad reports inside those bounds.
        var scene = Pair();
        var pins = scene.Intent.Screens.Single().Islands.Single().Members.Where(m => m.RequiresStub).Select(m => m.Pin).ToArray();
        LayLeft(scene, pins[0], new(80 * Grid, 60 * Grid));
        LayLeft(scene, pins[1], new(80 * Grid, 70 * Grid));
        Scene WithField(Rect field)
        {
            var copy = scene with { Geometry = scene.Geometry.Copy() };
            copy.Geometry.Fields[pins[0].SymbolId] = [field];
            return copy;
        }
        // Must-catch: a field right in front of the upper pin, which its own bounds take in: the corridor out of those bounds
        // would run through the text, so the connection is drawn with label stubs and says why.
        var (inFront, inFrontMeasured) = await WithField(new(77 * Grid, 60 * Grid - Grid / 4, 79 * Grid, 60 * Grid + Grid / 4)).RealizeMeasured();
        var blocked = inFront.Outcomes.Single();
        Assert.AreEqual(ConnectionRealizationStrategy.LabelStub, blocked.Strategy);
        StringAssert.Contains(blocked.FallbackReason, "own field text");
        Assert.IsEmpty(Generated<Junction>(inFront));
        // Guard: the same field one grid above the pin's line leaves the corridor (still four grids out of the wider bounds) clear
        // of it, and the pins are routed without touching the text.
        var (clear, clearMeasured) = await WithField(new(77 * Grid, 58 * Grid, 79 * Grid, 59 * Grid)).RealizeMeasured();
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, clear.Outcomes.Single().Strategy, clear.Outcomes.Single().FallbackReason);
        Assert.IsEmpty(RoutedProblems(clear, scene.Intent, clearMeasured, Policy));
        Assert.IsFalse(Generated<SchematicLine>(clear).Any(w => SchematicConnectionRealizer.SegmentMeets(new(w.Start.XNm, w.Start.YNm), new(w.End.XNm, w.End.YNm),
            new SchematicConnectionRealizer.Box(77 * Grid, 58 * Grid, 79 * Grid, 59 * Grid).Inflate(Policy.ClearanceNm), open: false)), "No wire comes near the field.");
        // Must-catch: an editor that does not report field positions cannot keep wires off them, so nothing is routed.
        var older = scene with { Geometry = scene.Geometry.Copy() };
        older.Geometry.FieldsReported = false;
        StringAssert.Contains((await older.Realize()).Outcomes.Single().FallbackReason, "does not report where symbol fields are drawn");

        // E3 holds where a route reaches an already connected pin as well (decision nfa2005d67574bfea): the one-grid attach wire
        // out of R1.1 or R2.1 keeps the clearance from that symbol's own field text. TP1.1 and TP2.1 join the unlabelled
        // connection of R1.1 and R2.1 (LinkedPins), which a route reaches through R1.1's corridor when nothing is in the way.
        // The field is a small text just below the pin's end, inside the symbol's bounds, which leaves the attach wire's grid
        // node free to reach.
        var (linked, _, member, r1, r2) = LinkedPins();
        var r1Pin = member(r1);
        var r2Pin = member(r2);
        var r1At = new Point(80 * Grid, 80 * Grid);
        var r2At = new Point(120 * Grid, 80 * Grid);
        static Rect Below(Point at, long gap) => new(at.X - 2 * Grid / 5, at.Y + gap, at.X + 2 * Grid / 5, at.Y + gap + 2 * Grid / 5);
        Scene Fielded(params (ConnectionPlacedPin Pin, Rect Field)[] fields)
        {
            var copy = linked with { Geometry = linked.Geometry.Copy() };
            foreach (var (pin, field) in fields) copy.Geometry.Fields[pin.SymbolId] = [field];
            return copy;
        }
        bool Reaches(SchematicConnectionRealization realization, Point at) => Generated<SchematicLine>(realization)
            .Any(w => (w.Start.XNm == at.X && w.Start.YNm == at.Y) || (w.End.XNm == at.X && w.End.YNm == at.Y));
        // Guard: the texts three fifths of a grid below the pins keep the clearance from the attach wires; the route reaches R1.1
        // as before.
        long clearGap = 3 * Grid / 5, nearGap = Grid / 5;
        Assert.IsTrue(clearGap > Policy.ClearanceNm && nearGap < Policy.ClearanceNm, "The two gaps straddle the clearance.");
        var (besideText, besideMeasured) = await Fielded((r1Pin, Below(r1At, clearGap)), (r2Pin, Below(r2At, clearGap))).RealizeMeasured();
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, besideText.Outcomes.Single().Strategy, besideText.Outcomes.Single().FallbackReason);
        Assert.IsTrue(Reaches(besideText, r1At), "The route reaches R1.1 through its corridor.");
        Assert.IsEmpty(RoutedProblems(besideText, linked.Intent, besideMeasured, Policy));
        // Must-catch: R1's text one fifth of a grid below R1.1 would lie within the clearance of the attach wire out of R1.1, so the
        // route reaches the connection at R2.1 instead and never draws a wire out of R1.1.
        var (pastR1, pastR1Measured) = await Fielded((r1Pin, Below(r1At, nearGap)), (r2Pin, Below(r2At, clearGap))).RealizeMeasured();
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, pastR1.Outcomes.Single().Strategy, pastR1.Outcomes.Single().FallbackReason);
        Assert.IsFalse(Reaches(pastR1, r1At), "No wire leaves R1.1 beside its own field text.");
        Assert.IsTrue(Reaches(pastR1, r2At), "The route reaches the connection at R2.1.");
        Assert.IsEmpty(RoutedProblems(pastR1, linked.Intent, pastR1Measured, Policy));
        var nearText = Below(r1At, nearGap);
        Assert.IsFalse(Generated<SchematicLine>(pastR1).Any(w => SchematicConnectionRealizer.SegmentMeets(new(w.Start.XNm, w.Start.YNm), new(w.End.XNm, w.End.YNm),
            new SchematicConnectionRealizer.Box(nearText.L, nearText.T, nearText.R, nearText.B).Inflate(Policy.ClearanceNm), open: false)),
            "No wire comes within the clearance of R1's field.");
        // Must-catch: texts that near both connected pins leave the route nowhere to attach, so the connection is drawn with its
        // join and label stubs and says why.
        var nowhere = (await Fielded((r1Pin, Below(r1At, nearGap)), (r2Pin, Below(r2At, nearGap))).Realize()).Outcomes.Single();
        Assert.AreEqual(ConnectionRealizationStrategy.LabelStub, nowhere.Strategy);
        StringAssert.Contains(nowhere.FallbackReason, "existing wires and pins offer no free point to attach to");
    }

    [TestMethod]
    public async Task RoutesKeepTheLabelStubRoomOfAConnectionTheyWouldOtherwiseCrowdOut()
    {
        // Routing never refuses what label stubs alone would draw (CN-1 §7, clarification lane 2A reported). Net A (TP1.1 above,
        // TP2.1 below, ordered first) is routed; net SIGNAL joins new TP3.1, pointing left between them, to R1.1's labelled
        // connection, so TP3.1 gets a label stub. A route keeps clear only the shortest stub room of connections still to be
        // drawn, so A's wire passes left of TP3.1's two-grid stub and its label.
        var bench = new Bench();
        Guid tp = bench.Part("TP", Passive("1")), r = bench.Part("R", Passive("1"), Passive("2"));
        Guid tp1 = bench.Component(tp, "TP1"), tp2 = bench.Component(tp, "TP2"), tp3 = bench.Component(tp, "TP3"), r1 = bench.Component(r, "R1");
        string wire = bench.Wire(BenchSheet.Root), label = bench.LocalLabel(BenchSheet.Root, "SIGNAL");
        var signal = new CircuitNet(Guid.Parse("00000000-0000-4000-8000-0000000000b2"), "SIGNAL", [new(r1, "1")]);
        var a = new CircuitNet(Guid.Parse("00000000-0000-4000-8000-0000000000a1"), "A", [new(tp1, "1"), new(tp2, "1")]);
        var state = WithFormatting(bench.State([signal], new() { [signal.Id] = [(BenchSheet.Root, wire), (BenchSheet.Root, label)] }));
        var scene = Scene.Of(bench, state, design => WithNets(design, a, signal with { Pins = [.. signal.Pins, new(tp3, "1")] }));
        var islands = scene.Intent.Screens.Single().Islands.OrderBy(i => i.NetId).ToArray();
        CollectionAssert.AreEqual(new[] { "A", "SIGNAL" }, islands.Select(i => i.LabelText).ToArray(), "A is drawn first.");
        ConnectionPlacedPin PinOf(Guid component) => islands.SelectMany(i => i.Members).Single(m => m.Pin.Endpoint.ComponentId == component).Pin;
        var q = PinOf(tp3);
        LayLeft(scene, q, new(100 * Grid, 60 * Grid));
        Lay(scene, PinOf(tp1), new(96 * Grid, 50 * Grid), (0, 1));
        Lay(scene, PinOf(tp2), new(96 * Grid, 70 * Grid), (0, -1));
        long StubLength(SchematicConnectionRealization realization)
        {
            var stub = StubOf(realization, q.PlacedPinId);
            return Math.Abs(stub.End.XNm - stub.Start.XNm) / Grid;
        }
        // A's wires that cross TP3.1's line (y = 60 grids), by x.
        long[] Crossings(SchematicConnectionRealization realization) => [.. Generated<SchematicLine>(realization)
            .Where(w => realization.Generated.Any(g => g.Id.ToString("D") == w.Id.Value && g.Role == GeneratedConnectionRole.RouteWire))
            .Where(w => w.Start.XNm == w.End.XNm && Math.Min(w.Start.YNm, w.End.YNm) < 60 * Grid && Math.Max(w.Start.YNm, w.End.YNm) > 60 * Grid)
            .Select(w => w.Start.XNm)];
        // Guard: TP3.1's two-grid stub fits beside A's wire, drawn in one pass.
        var (plain, plainMeasured) = await scene.RealizeMeasured();
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, plain.Outcomes.Single(o => o.NetId == a.Id).Strategy);
        Assert.AreEqual(2, StubLength(plain));
        CollectionAssert.AreEqual(new[] { 92 * Grid }, Crossings(plain), "A passes just left of the two-grid stub's label.");
        Assert.IsEmpty(RoutedProblems(plain, scene.Intent, plainMeasured, Policy));
        // Must-catch (review finding 2): a junction within the clearance of the two-grid stub end leaves TP3.1 needing three grids,
        // right where A's wire runs. Drawn in one pass, TP3.1 would have no stub at any length and the whole apply would be
        // refused, though label stubs alone fit. The sheet is drawn again with all of TP3.1's label-stub room kept clear, so A
        // goes round it and TP3.1 gets its three-grid stub.
        var crowded = scene.Junction(new(98 * Grid, 60 * Grid + 2 * Grid / 5));
        var (drawn, drawnMeasured) = await crowded.RealizeMeasured();
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, drawn.Outcomes.Single(o => o.NetId == a.Id).Strategy);
        Assert.AreEqual(3, StubLength(drawn));
        Assert.IsTrue(Crossings(drawn).All(x => x < 86 * Grid || x > 100 * Grid), "A keeps clear of every stub length and label of TP3.1: "
            + string.Join(", ", Crossings(drawn)));
        Assert.IsEmpty(RoutedProblems(drawn, crowded.Intent, drawnMeasured, Policy));
        // Must-catch: when label stubs cannot draw TP3.1 either (junctions beside every stub end), the refusal is milestone 1's
        // own, from a drawing with no routes, after the redraw with TP3.1's room protected is refused again.
        var hopeless = scene;
        foreach (int multiple in SchematicConnectionPolicy.StubMultiples) hopeless = hopeless.Junction(new((100 - multiple) * Grid, 60 * Grid + 2 * Grid / 5));
        var refusal = await Assert.ThrowsExactlyAsync<AutomationException>(hopeless.Realize);
        Assert.AreEqual(SchematicConnectionErrors.RealizationNoFreeStub, refusal.Code, refusal.Message);
        StringAssert.Contains(refusal.Message, "Pin TP3.1 of net 'SIGNAL'", "The refusal names the pin by its reference.");
    }

    [TestMethod]
    public async Task ARouteKeepsTheRoomOfItsOwnSheetPinWhenItWouldCrowdItOut()
    {
        // Net DATA joins R5.2 and R6.2 on the root sheet to the child sheet's U1.3, so it is routed on the root and gets a sheet
        // pin on the child sheet symbol's left edge (x = 150 mm, y 50-90 mm). Its pins sit one grid left of that edge, above
        // and below the sheet symbol, so the straight route runs down the edge through the room of every sheet-pin slot.
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2")), t = bench.Part("T3", Passive("1"), Passive("2"), Passive("3"));
        Guid r5 = bench.Component(r, "R5"), r6 = bench.Component(r, "R6");
        bench.Component(t, "U9", BenchSheet.Child);
        var state = WithFormatting(bench.State([]));
        var created = bench.Create(state.Baseline, t, "U1", BenchSheet.Child);
        var data = new CircuitNet(Guid.NewGuid(), "DATA", [new(created.Component, "3"), new(r5, "2"), new(r6, "2")]);
        var scene = Scene.Of(bench, state, design => WithNets(Adopt(design, created.Design), data));
        var root = scene.Intent.Screens.SelectMany(s => s.Islands).Single(i => i.ChildSheetSymbolIds.Count != 0);
        ConnectionPlacedPin PinOf(Guid component) => root.Members.Single(m => m.Pin.Endpoint.ComponentId == component).Pin;
        var child = scene.Intent.Screens.SelectMany(s => s.Islands).Single(i => i.UplinkSheetSymbolId is not null).Members.Single(m => m.RequiresStub).Pin;
        async Task<(SchematicConnectionRealization Realization, IReadOnlyDictionary<string, SchematicPlacementGeometry> Measured)> Drawn(long x)
        {
            var laid = scene with { Geometry = scene.Geometry.Copy() };
            Lay(laid, PinOf(r5), new(x, 30 * Grid), (0, 1));
            Lay(laid, PinOf(r6), new(x, 80 * Grid), (0, -1));
            LayLeft(laid, child, new(60 * Grid, 100 * Grid));
            return await laid.RealizeMeasured();
        }
        SheetPin Pin(SchematicConnectionRealization realization) => SheetUpdates(realization).Single().Pins.Single(p => p.Text.Text_ == "DATA");
        // Guard: pins well left of the sheet symbol are joined by a straight route in one pass, and the sheet pin takes the first slot.
        var (far, farMeasured) = await Drawn(100 * Grid);
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, far.Outcomes.Single(o => o.NetId == root.NetId && o.ScreenId == root.ScreenId).Strategy);
        Assert.AreEqual(50_000_000 + Policy.SheetPinPitchNm, Pin(far).Position.YNm);
        Assert.IsEmpty(RoutedProblems(far, scene.Intent, farMeasured, Policy));
        // Must-catch (review finding 2): hugging the edge, the route would leave the sheet pin no slot and the apply would be
        // refused. The root sheet is drawn again with DATA's sheet-pin room kept clear of its own route, which goes round it.
        var (near, nearMeasured) = await Drawn(117 * Grid);
        var outcome = near.Outcomes.Single(o => o.NetId == root.NetId && o.ScreenId == root.ScreenId);
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, outcome.Strategy, outcome.FallbackReason);
        Assert.AreEqual(50_000_000 + Policy.SheetPinPitchNm, Pin(near).Position.YNm, "The sheet pin takes the first slot.");
        Assert.IsEmpty(RoutedProblems(near, scene.Intent, nearMeasured, Policy));
        var routeWires = near.Generated.Where(g => g.Role == GeneratedConnectionRole.RouteWire).Select(g => g.Id.ToString("D")).ToHashSet();
        // The room of the slots (every grid from 52.54 mm to 86.83 mm): from the edge out past the longest stub's label (8 grids
        // and 4.76 mm of "DATA"), from the first slot's label top to the last slot's label bottom.
        var room = new SchematicConnectionRealizer.Box(135_100_000, 52_000_000, 150_000_000, 87_400_000);
        var nearWires = Generated<SchematicLine>(near).Where(w => routeWires.Contains(w.Id.Value)).ToArray();
        Assert.IsFalse(nearWires.Any(w => SchematicConnectionRealizer.SegmentMeets(new(w.Start.XNm, w.Start.YNm), new(w.End.XNm, w.End.YNm), room, open: false)),
            "The route keeps clear of the sheet-pin room: " + string.Join(" ", nearWires.Select(w => "(" + w.Start.XNm / 1e6 + ", " + w.Start.YNm / 1e6 + ")-("
                + w.End.XNm / 1e6 + ", " + w.End.YNm / 1e6 + ")")));
    }

    [TestMethod]
    public async Task AGlobalNetIsRoutedWithOneGlobalLabel()
    {
        // GND holds the existing power symbol #PWR01; the XML joins R1.2 and R2.2 to it. They are routed together and named by
        // one global label at the first pin, which joins them to the power symbol by name; no local label is drawn.
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid gnd = bench.Part("GND", SchematicSymbolType.SstGlobalPower, new BenchPin("1", "GND", 1, ElectricalPinType.EptPowerInput, false));
        Guid pwr = bench.Component(gnd, "#PWR01", value: "GND");
        Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2");
        var ground = new CircuitNet(Guid.NewGuid(), "GND", [new(pwr, "1")]);
        var scene = Scene.Of(bench, WithFormatting(bench.State([ground])), design => WithNets(design, ground with { Pins = [.. ground.Pins, new(r1, "2"), new(r2, "2")] }));
        var island = scene.Intent.Screens.Single().Islands.Single();
        Assert.AreEqual(ConnectionScope.Global, island.Scope);
        var pins = island.Members.Where(m => m.RequiresStub).Select(m => m.Pin).ToArray();
        LayLeft(scene, pins[0], new(80 * Grid, 60 * Grid));
        LayLeft(scene, pins[1], new(80 * Grid, 66 * Grid));
        var (realization, measured) = await scene.RealizeMeasured();
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, realization.Outcomes.Single().Strategy, realization.Outcomes.Single().FallbackReason);
        var label = Generated<GlobalLabel>(realization).Single();
        Assert.AreEqual("GND", label.Text.Text_);
        Assert.AreEqual(SchematicLabelShape.SlshPassive, label.Shape);
        Assert.IsEmpty(Generated<LocalLabel>(realization));
        Assert.IsFalse(realization.Outcomes.Single().AttachedCarrier);
        Assert.IsEmpty(RoutedProblems(realization, scene.Intent, measured, Policy));
    }

    // R1.1 and R2.1 joined by an unlabelled wire elsewhere on the sheet; the XML adds TP1.1 and TP2.1 to that net (a join,
    // §6.3 (a)). Laid out pointing left: TP1.1 at (80, 60) and TP2.1 at (80, 66) grids, R1.1 at (80, 80) and R2.1 at (120, 80).
    private static (Scene Scene, ConnectionIsland Island, Func<Guid, ConnectionPlacedPin> Member, Guid R1, Guid R2) LinkedPins()
    {
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2")), tp = bench.Part("TP", Passive("1"));
        Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2"), tp1 = bench.Component(tp, "TP1"), tp2 = bench.Component(tp, "TP2");
        string wire = bench.Wire(BenchSheet.Root);
        var link = new CircuitNet(Guid.NewGuid(), "LINK", [new(r1, "1"), new(r2, "1")]);
        var state = WithFormatting(bench.State([link], new() { [link.Id] = [(BenchSheet.Root, wire)] }));
        var scene = Scene.Of(bench, state, design => WithNets(design, link with { Pins = [.. link.Pins, new(tp1, "1"), new(tp2, "1")] }));
        var island = scene.Intent.Screens.Single().Islands.Single();
        Assert.IsTrue(island.JoinRequired);
        ConnectionPlacedPin Member(Guid component) => island.Members.Single(m => m.Pin.Endpoint.ComponentId == component).Pin;
        LayLeft(scene, Member(tp1), new(80 * Grid, 60 * Grid));
        LayLeft(scene, Member(tp2), new(80 * Grid, 66 * Grid));
        LayLeft(scene, Member(r1), new(80 * Grid, 80 * Grid));
        LayLeft(scene, Member(r2), new(120 * Grid, 80 * Grid));
        return (scene, island, Member, r1, r2);
    }

    [TestMethod]
    public async Task AnUnlabelledExistingConnectionIsReachedByWireAndNamedByTheRoutesLabel()
    {
        // R1.1 and R2.1 are joined by an unlabelled wire (elsewhere on the sheet); the XML adds TP1.1 and TP2.1 to that net
        // (a join, §6.3 (a)). The new pins are routed together and the route reaches the existing connection through the
        // one-grid corridor of R1.1 (§7: an existing island attaches only at its own connection points); the route's one label
        // names the whole connection, which is reported.
        var (scene, island, _, _, _) = LinkedPins();
        var (realization, measured) = await scene.RealizeMeasured();
        var outcome = realization.Outcomes.Single();
        Assert.AreEqual(ConnectionRealizationStrategy.OrthogonalWire, outcome.Strategy, outcome.FallbackReason);
        var wires = Generated<SchematicLine>(realization);
        var r1At = new Vector2 { XNm = 80 * Grid, YNm = 80 * Grid };
        Assert.IsTrue(wires.Any(w => w.Start.Equals(r1At) || w.End.Equals(r1At)), "The route reaches R1.1 through its corridor.");
        Assert.AreEqual("LINK", Generated<LocalLabel>(realization).Single().Text.Text_);
        Assert.IsTrue(realization.Diagnostics.Any(d => d.Code == SchematicConnectionErrors.ExistingNetNamedByRealization && d.NetId == island.NetId));
        Assert.IsEmpty(RoutedProblems(realization, scene.Intent, measured, Policy));
        // Must-catch: with keep-outs above and below the rows of R1.1 and R2.1 and a wall across each row beyond the room a join
        // label needs, nothing can reach the existing connection; the island falls back to its join and stubs and says why.
        var hemmed = scene;
        foreach (long x in new long[] { 80 * Grid, 120 * Grid })
            hemmed = hemmed.Obstacle(new(x - 10 * Grid, 70 * Grid, x - Grid / 2, 79 * Grid)).Obstacle(new(x - 10 * Grid, 81 * Grid, x - Grid / 2, 90 * Grid))
                .Obstacle(new(x - 8 * Grid - Grid / 5, 70 * Grid, x - 8 * Grid + Grid / 5, 90 * Grid));
        var fallback = (await hemmed.RealizeMeasured()).Realization.Outcomes.Single();
        Assert.AreEqual(ConnectionRealizationStrategy.LabelStub, fallback.Strategy);
        StringAssert.Contains(fallback.FallbackReason, "existing");
    }

    [TestMethod]
    public async Task ForeignContactsMoveTheStubOutwardAndRefuseItWhenNoLengthIsFree()
    {
        var scene = RootAddition();
        var member = Member(scene, "TP1");
        var a = scene.PinAt(member.Pin);
        // Guard: a foreign connection point just outside the clearance of the 2-grid end is no contact.
        var outside = scene.Junction(new(a.X - 2 * Grid, a.Y - Policy.ClearanceNm - 100));
        Assert.AreEqual(a.X - 2 * Grid, StubOf(await outside.Realize(), Member(outside, "TP1").Pin.PlacedPinId).End.XNm);
        // Must-catch: one within the clearance moves the stub to 3 grids; one near every stub end refuses it.
        var near = scene.Junction(new(a.X - 2 * Grid, a.Y - Policy.ClearanceNm));
        Assert.AreEqual(a.X - 3 * Grid, StubOf(await near.Realize(), Member(near, "TP1").Pin.PlacedPinId).End.XNm);
        var everywhere = scene;
        foreach (int multiple in SchematicConnectionPolicy.StubMultiples) everywhere = everywhere.Junction(new(a.X - multiple * Grid, a.Y - Policy.ClearanceNm));
        await RequireRefusal(everywhere, SchematicConnectionErrors.RealizationNoFreeStub, "has no free room");
        // A foreign point on the stub itself blocks every length through it.
        await RequireRefusal(scene.Junction(new(a.X - Grid, a.Y)), SchematicConnectionErrors.RealizationNoFreeStub, "has no free room");
        // A foreign wire crossing the stub, or running within the clearance beside it, is a contact as well.
        await RequireRefusal(scene.Wire(new(a.X - Grid, a.Y - 20 * Grid), new(a.X - Grid, a.Y + 20 * Grid)), SchematicConnectionErrors.RealizationNoFreeStub, "has no free room");
        await RequireRefusal(scene.Wire(new(a.X, a.Y + Policy.ClearanceNm), new(a.X - 20 * Grid, a.Y + Policy.ClearanceNm)),
            SchematicConnectionErrors.RealizationNoFreeStub, "has no free room");
        // Guard: a parallel wire just beyond the clearance does not touch the stub or its label.
        var beside = scene.Wire(new(a.X, a.Y + 2 * Grid), new(a.X - 20 * Grid, a.Y + 2 * Grid));
        Assert.AreEqual(a.X - 2 * Grid, StubOf(await beside.Realize(), Member(beside, "TP1").Pin.PlacedPinId).End.XNm);
    }

    [TestMethod]
    public async Task LabelsNeverOverlapObstaclesOrEachOtherButMayTouch()
    {
        var scene = RootAddition();
        var member = Member(scene, "TP1");
        var a = scene.PinAt(member.Pin);
        var free = await scene.Realize();
        var label = Generated<LocalLabel>(free).Single(l => l.Position.Equals(StubOf(free, member.Pin.PlacedPinId).End));
        var envelope = scene.Geometry.LabelBox(label);
        // Guard: an obstacle touching the label's far edge is admitted.
        var touching = scene.Obstacle(new(envelope.L - 10 * Grid, envelope.T, envelope.L, envelope.B));
        Assert.AreEqual(label.Position, StubOf(await touching.Realize(), Member(touching, "TP1").Pin.PlacedPinId).End);
        // Must-catch: one reaching 100 nm into the label's inner end moves the stub one grid outward, where the
        // label only touches it.
        var overlapping = scene.Obstacle(new(envelope.R - Grid, envelope.T - 10 * Grid, envelope.R, envelope.T + 100));
        var moved = StubOf(await overlapping.Realize(), Member(overlapping, "TP1").Pin.PlacedPinId);
        Assert.AreEqual(a.X - 3 * Grid, moved.End.XNm);
        Assert.AreEqual(a.Y, moved.End.YNm);
        // A stub crossing an obstacle is refused at every length.
        await RequireRefusal(scene.Obstacle(new(a.X - 9 * Grid, a.Y - Grid, a.X - Grid / 2, a.Y + Grid)), SchematicConnectionErrors.RealizationNoFreeStub, "has no free room");
        // Two new pins of one symbol sit two grids apart; their labels stay clear of each other, so both keep 2-grid stubs.
        var r2 = scene.Intent.Screens.Single().Islands.SelectMany(i => i.Members).Where(m => m.Pin.CreatedSymbol).ToArray();
        Assert.HasCount(2, r2);
        Assert.IsTrue(r2.All(m => StubOf(free, m.Pin.PlacedPinId).Start.XNm - StubOf(free, m.Pin.PlacedPinId).End.XNm == 2 * Grid));
    }

    [TestMethod]
    public async Task NewStubsAndLabelsKeepClearOfEachOther()
    {
        // Two test points joined by a new net X, drawn close together: every stub and label drawn so far is an
        // obstacle for the next one, exactly like the items that were already on the sheet.
        var scene = Pair();
        var island = scene.Intent.Screens.Single().Islands.Single();
        var stubbed = island.Members.Where(m => m.RequiresStub).Select(m => m.Pin).ToArray();
        Assert.HasCount(2, stubbed);
        // stubbed[0] is drawn first: island members keep their planned order.
        var (first, second) = (stubbed[0], stubbed[1]);
        var a = new Point(150_000_000, 100_000_000);
        // Each pin points left out of a small body to its right; `below` puts the second pin that far lower.
        Scene Layout(long below)
        {
            var copy = scene with { Geometry = scene.Geometry.Copy() };
            Lay(copy, first, a, (1, 0), new(a.X, a.Y - Grid / 4, a.X + 4 * Grid, a.Y + Grid / 4));
            var b = new Point(a.X, a.Y + below);
            Lay(copy, second, b, (1, 0), new(b.X, b.Y - Grid / 4, b.X + 4 * Grid, b.Y + Grid / 4));
            return copy;
        }
        long Length(SchematicConnectionRealization realization, ConnectionPlacedPin pin)
        {
            var wire = StubOf(realization, pin.PlacedPinId);
            return wire.Start.XNm - wire.End.XNm;
        }
        // Guard: one grid apart, the two labels only touch, so both keep the shortest stub.
        var touching = await Layout(Grid).Realize();
        Assert.AreEqual(2 * Grid, Length(touching, first)); Assert.AreEqual(2 * Grid, Length(touching, second));
        var labels = Generated<LocalLabel>(touching).Select(scene.Geometry.LabelBox).ToArray();
        Assert.AreEqual(labels.Max(l => l.T), labels.Min(l => l.B), "The two label envelopes share one edge.");
        // Must-catch: three quarters of a grid apart, the second label would overlap the first one at 2 and 3 grids;
        // its stub moves out to 4 grids, where the labels no longer overlap, while the first keeps 2 grids.
        var overlapping = await Layout(3 * Grid / 4).Realize();
        Assert.AreEqual(2 * Grid, Length(overlapping, first));
        Assert.AreEqual(4 * Grid, Length(overlapping, second), "The second label must not overlap the first one.");

        // Must-catch: the second pin points up from below the first stub, one grid to its left. Its 2-grid label would
        // lie across the first stub, and every longer stub would cross that stub, so it is refused. Nothing else is in
        // the way: the first label, both pins and the first stub's end all lie outside the second label.
        var crossing = scene with { Geometry = scene.Geometry.Copy() };
        Lay(crossing, first, a, (1, 0), new(a.X, a.Y - Grid / 4, a.X + 4 * Grid, a.Y + Grid / 4));
        var up = new Point(a.X - Grid, a.Y + 3 * Grid);
        Lay(crossing, second, up, (0, 1), new(up.X - Grid, up.Y, up.X + Grid, up.Y + 2 * Grid));
        var refusal = await Assert.ThrowsExactlyAsync<AutomationException>(crossing.Realize);
        Assert.AreEqual(SchematicConnectionErrors.RealizationNoFreeStub, refusal.Code, refusal.Message);
        StringAssert.Contains(refusal.Message, "Pin " + PinName(crossing.Plan.Candidate!, second.Endpoint));
        // Guard: moved two grids further left, the second pin's label clears the first stub and both are drawn.
        var beside = scene with { Geometry = scene.Geometry.Copy() };
        Lay(beside, first, a, (1, 0), new(a.X, a.Y - Grid / 4, a.X + 4 * Grid, a.Y + Grid / 4));
        var clear = new Point(a.X - 6 * Grid, a.Y + 3 * Grid);
        Lay(beside, second, clear, (0, 1), new(clear.X - Grid, clear.Y, clear.X + Grid, clear.Y + 2 * Grid));
        var drawn = await beside.Realize();
        Assert.AreEqual(2 * Grid, Length(drawn, first));
        var upward = StubOf(drawn, second.PlacedPinId);
        Assert.AreEqual(clear.Y - 2 * Grid, upward.End.YNm);

        static void Lay(Scene target, ConnectionPlacedPin pin, Point at, (int Dx, int Dy) body, Rect drawn)
        {
            target.Geometry.Place[pin.PlacedPinId] = at;
            target.Geometry.Direction[pin.PlacedPinId] = body;
            target.Geometry.Body[pin.SymbolId] = drawn;
        }
    }

    [TestMethod]
    public async Task EveryMeasurementMustAnswerExactlyWhatWasAsked()
    {
        foreach (var (problem, tamper, code) in new (string, Func<MeasureSchematicPlacement, SchematicPlacementGeometry, SchematicPlacementGeometry>, string)[]
        {
            ("another revision", (_, g) => { g.Revision.Sequence++; return g; }, SchematicConnectionErrors.RealizationMeasurementStale),
            ("another sheet", (_, g) => { g.ScreenId = new() { Value = Guid.NewGuid().ToString("D") }; return g; }, SchematicConnectionErrors.RealizationMeasurementStale),
            ("a missing obstacle", (_, g) => { g.Obstacles.RemoveAt(0); return g; }, SchematicConnectionErrors.RealizationMeasurementIncomplete),
            ("a duplicated obstacle", (_, g) => { g.Obstacles.Add(g.Obstacles[0].Clone()); return g; }, SchematicConnectionErrors.RealizationMeasurementIncomplete),
            ("an extra obstacle", (_, g) => { var extra = g.Obstacles[0].Clone(); extra.Id.Value = Guid.NewGuid().ToString("D"); g.Obstacles.Add(extra); return g; },
                SchematicConnectionErrors.RealizationMeasurementIncomplete),
            ("a missing candidate", (r, g) => { if (r.Candidates.Count != 0) g.Candidates.Clear(); return g; }, SchematicConnectionErrors.RealizationMeasurementIncomplete),
            ("a moved label prototype", (r, g) => { if (r.ItemCandidates.Count != 0) g.ItemCandidates[0].Anchor.XNm += 100; return g; },
                SchematicConnectionErrors.RealizationMeasurementIncomplete),
            ("a missing label prototype", (r, g) => { if (r.ItemCandidates.Count != 0) g.ItemCandidates.RemoveAt(0); return g; },
                SchematicConnectionErrors.RealizationMeasurementIncomplete),
            ("an older editor without pin geometry", (_, g) => { g.PinGeometryAvailable = false; return g; }, SchematicConnectionErrors.RealizationMeasurementUnsupported),
            ("a symbol field outside the symbol's bounds", (_, g) =>
            {
                var symbol = g.Obstacles.Concat(g.Candidates).First(o => o.SymbolPins is not null);
                symbol.VisibleFieldBounds.Add(new Box2 { Position = new() { XNm = symbol.Bounds.Position.XNm - 10 * Grid, YNm = symbol.Bounds.Position.YNm },
                    Size = new() { XNm = Grid, YNm = Grid } });
                return g;
            }, SchematicConnectionErrors.RealizationMeasurementIncomplete),
            ("a drawing-sheet item with a negative size", (_, g) => { g.DrawingSheet.Items[0].Bounds.Size.XNm = -1; return g; },
                SchematicConnectionErrors.RealizationMeasurementIncomplete)
        })
        {
            var scene = RootAddition();
            scene.Geometry.Tamper = tamper;
            await RequireRefusal(scene, code, "", problem);
        }
        // An older editor refusing the label prototypes as unknown fields cannot realize.
        var older = RootAddition();
        older.Geometry.Tamper = (r, g) => r.ItemCandidates.Count != 0 ? throw new NativeApiException(3, "Placement measurement contains unsupported fields") : g;
        await RequireRefusal(older, SchematicConnectionErrors.RealizationMeasurementUnsupported, "cannot measure");
        // Every other native refusal of the measurement becomes a realization code as well, never a raw native error.
        foreach (var (status, message, code, detail) in new (int, string, string, string)[]
        {
            ((int)Kiapi.Common.ApiStatusCode.AsBusy, "KiCad is busy", SchematicConnectionErrors.RealizationMeasurementStale, "was busy"),
            ((int)Kiapi.Common.ApiStatusCode.AsNotReady, "Schematic connectivity is unavailable", SchematicConnectionErrors.RealizationMeasurementStale, "was busy"),
            ((int)Kiapi.Common.ApiStatusCode.AsUnhandled, "No handler for request", SchematicConnectionErrors.RealizationMeasurementUnsupported, "cannot measure"),
            (3, "Unsupported native pin drawing orientation", SchematicConnectionErrors.RealizationMeasurementUnsupported, "refused to measure")
        })
        {
            var refused = RootAddition();
            refused.Geometry.Tamper = (_, _) => throw new NativeApiException(status, message);
            await RequireRefusal(refused, code, detail, message);
            var error = await Assert.ThrowsExactlyAsync<AutomationException>(refused.Realize);
            StringAssert.Contains(error.Message, message, "The editor's own reason is kept.");
        }
    }

    [TestMethod]
    public async Task PinsMustBeCompleteIdenticalAndPoweredAsPlanned()
    {
        var scene = RootAddition();
        var member = scene.Intent.Screens.Single().Islands.SelectMany(i => i.Members).First(m => m.RequiresStub && m.Pin.CreatedSymbol);
        scene.Geometry.Incomplete[member.Pin.SymbolId] = SchematicPinGeometryIncompleteReason.SpgirVariantPinMappingUnresolved;
        await RequireRefusal(scene, SchematicConnectionErrors.RealizationVariantPinIdentityUnresolved, "cannot report exact pin positions");
        scene.Geometry.Incomplete[member.Pin.SymbolId] = SchematicPinGeometryIncompleteReason.SpgirPlacedIdentityMissing;
        await RequireRefusal(scene, SchematicConnectionErrors.RealizationPinGeometryIncomplete, "cannot report exact pin positions");
        scene.Geometry.Incomplete.Clear();
        // Guard: an unrelated existing symbol with incomplete pins is allowed; its whole body stays an obstacle. That now
        // includes a symbol whose library definition KiCad cannot resolve: KiCad measures it by its own bounds, with no
        // pins and SPGIR_DEFINITION_UNRESOLVED, instead of refusing to measure the whole sheet.
        var unrelated = SymbolId(scene, "R9");
        Assert.IsFalse(scene.Intent.Screens.Single().Islands.SelectMany(i => i.Members).Any(m => m.Pin.SymbolId == unrelated));
        foreach (var reason in new[] { SchematicPinGeometryIncompleteReason.SpgirPlacedIdentityMissing, SchematicPinGeometryIncompleteReason.SpgirVariantPinMappingUnresolved,
            SchematicPinGeometryIncompleteReason.SpgirDefinitionUnresolved })
        {
            scene.Geometry.Incomplete[unrelated] = reason;
            Assert.HasCount(3, Generated<LocalLabel>(await scene.Realize()), reason.ToString());
        }
        // Must-catch: the unresolved symbol's whole body is a keep-out. Drawn across TP1's stub room, it leaves TP1 no stub.
        var tp1 = Member(scene, "TP1").Pin;
        var at = scene.PinAt(tp1);
        var covering = scene with { Geometry = scene.Geometry.Copy() };
        covering.Geometry.Body[unrelated] = new(at.X - 9 * Grid, at.Y - Grid, at.X - Grid / 2, at.Y + Grid);
        await RequireRefusal(covering, SchematicConnectionErrors.RealizationNoFreeStub, "Pin TP1.1");
        // Guard: the same body clear of the stub room admits it.
        var clear = scene with { Geometry = scene.Geometry.Copy() };
        clear.Geometry.Body[unrelated] = new(at.X - 9 * Grid, at.Y + 2 * Grid, at.X - Grid / 2, at.Y + 4 * Grid);
        Assert.HasCount(3, Generated<LocalLabel>(await clear.Realize()));
        // The refusal a person reads names what is in the way by reference, never by identity. TP1's longest stub (eight grids
        // to the left) crosses R9's body and R1's, whichever the measurement lists first: it names that symbol R9 or R1.
        static string LongestTried(AutomationException error)
        {
            const string From = "(at the longest stub length tried, ", To = "). Move the symbol";
            int from = error.Message.IndexOf(From, StringComparison.Ordinal), to = error.Message.IndexOf(To, StringComparison.Ordinal);
            Assert.IsTrue(from >= 0 && to > from, error.Message);
            return error.Message[(from + From.Length)..to];
        }
        var crossed = LongestTried(await Assert.ThrowsExactlyAsync<AutomationException>(covering.Realize));
        CollectionAssert.Contains(new[] { "the stub crosses symbol R1", "the stub crosses symbol R9" }, crossed);
        // A pin in the way is named by its symbol's reference and its number: R9 measured whole, with its pin 1 three grids left of
        // TP1.1 on TP1's stub room (pin 2 two grids above it, its body a thin box between them), is the first thing on that
        // stub, and the refusal says "pin R9.1 lies on the stub", never the pin's or the symbol's identity.
        var pinned = scene with { Geometry = scene.Geometry.Copy() };
        pinned.Geometry.Incomplete.Clear();
        var r9 = pinned.Plan.Candidate!.Schematic.Instances.SelectMany(s => s.Items).Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => Guid.Parse(s.Id.Value) == unrelated);
        var r9Pins = SchematicPlacedPins.Active(r9, r9.Unit?.Unit ?? 1).OrderBy(p => p.Number, StringComparer.Ordinal).ToArray();
        Assert.AreEqual("1", r9Pins[0].Number);
        pinned.Geometry.Place[Guid.Parse(r9Pins[0].Id.Value)] = new(at.X - 3 * Grid, at.Y);
        pinned.Geometry.Place[Guid.Parse(r9Pins[1].Id.Value)] = new(at.X - 3 * Grid, at.Y - 2 * Grid);
        pinned.Geometry.Body[unrelated] = new(at.X - 3 * Grid - Grid / 4, at.Y - 2 * Grid, at.X - 3 * Grid + Grid / 4, at.Y);
        var inTheWay = await Assert.ThrowsExactlyAsync<AutomationException>(pinned.Realize);
        Assert.AreEqual(SchematicConnectionErrors.RealizationNoFreeStub, inTheWay.Code, inTheWay.Message);
        StringAssert.Contains(inTheWay.Message, "Pin TP1.1 ");
        Assert.AreEqual("pin R9.1 lies on the stub", LongestTried(inTheWay));
        foreach (var identity in new[] { unrelated.ToString("D"), r9Pins[0].Id.Value })
            Assert.IsFalse(inTheWay.Message.Contains(identity, StringComparison.Ordinal), inTheWay.Message);
        scene.Geometry.Incomplete.Clear();
        // Must-catch: a new symbol with no connection at all must still report its pins, which must be proven to touch
        // no existing connection point; guard: reported completely, it is simply created.
        var loose = RootAddition(loose: true);
        var r3 = SymbolId(loose, "R3");
        CollectionAssert.Contains(loose.Intent.CreatedSymbolIds.ToArray(), r3);
        Assert.IsFalse(loose.Intent.Screens.SelectMany(s => s.Islands).SelectMany(i => i.Members).Any(m => m.Pin.SymbolId == r3));
        Assert.AreEqual(2, (await loose.Realize()).Operations.Count(o => o.Create is not null && o.Create.Is(SchematicSymbolInstance.Descriptor)));
        loose.Geometry.Incomplete[r3] = SchematicPinGeometryIncompleteReason.SpgirPlacedIdentityMissing;
        await RequireRefusal(loose, SchematicConnectionErrors.RealizationPinGeometryIncomplete, "cannot prove the symbol touches no existing connection");
        loose.Geometry.Incomplete[r3] = SchematicPinGeometryIncompleteReason.SpgirVariantPinMappingUnresolved;
        await RequireRefusal(loose, SchematicConnectionErrors.RealizationVariantPinIdentityUnresolved, "new symbol R3 on sheet");
        // Must-catch: the editor reports a member as a power connection the plan does not expect.
        scene.Geometry.Power[member.Pin.PlacedPinId] = (SchematicPinPowerScope.SppsGlobal, "VCC");
        await RequireRefusal(scene, SchematicConnectionErrors.RealizationImplicitPowerMismatch, "global power 'VCC'");
        scene.Geometry.Power.Clear();
        // Must-catch: a pin whose direction is not along one axis, or whose library identity is not the planned one.
        scene.Geometry.Direction[member.Pin.PlacedPinId] = (1, 1);
        await RequireRefusal(scene, SchematicConnectionErrors.RealizationPinGeometryMismatch, "");
        scene.Geometry.Direction.Clear();
        scene.Geometry.Library[member.Pin.PlacedPinId] = Guid.NewGuid();
        await RequireRefusal(scene, SchematicConnectionErrors.RealizationPinGeometryMismatch, "another library pin identity");
    }

    [TestMethod]
    public async Task ARepeatedSheetMustShowTheSamePinsOnEveryInstance()
    {
        // The plan fixture's channel sheet is used twice; SIG joins U1.1 on one instance with U2.1 on the other, which
        // share one physical pin. One hierarchical label on the shared sheet serves both instances, and each channel's
        // sheet symbol on the root gains its own sheet pin. If one instance reports the pin elsewhere, one drawing
        // cannot serve both.
        var scene = Scene.Repeated();
        var channel = scene.Intent.Screens.Single(s => s.InstancePathKeys.Count == 2);
        var member = channel.Islands.Single().Members.Single(m => m.RequiresStub);
        // The fixture's symbols sit near the left page edge; the shared pin points down, well below the body.
        var symbol = scene.Plan.Candidate!.Schematic.Instances.First(s => Key(s) == member.Pin.SheetPathKey).Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Single(s => Guid.Parse(s.Id.Value) == member.Pin.SymbolId);
        scene.Geometry.Place[member.Pin.PlacedPinId] = new(symbol.Position.XNm, symbol.Position.YNm + 20 * Grid);
        scene.Geometry.Direction[member.Pin.PlacedPinId] = (0, -1);
        var realization = await scene.Realize();
        var shared = Generated<HierarchicalLabel>(realization).Single();
        Assert.AreEqual(SchematicLabelSpinStyle.SlssBottom, shared.SpinStyle);
        Assert.HasCount(2, SheetUpdates(realization), "One new sheet pin on each channel's sheet symbol.");
        Assert.IsTrue(SheetUpdates(realization).All(s => s.Pins.Single().Side == SheetSide.ShsRight), "A crossing with no pins on the root faces right.");
        foreach (var copy in realization.Design.Schematic.Instances.Where(s => Guid.Parse(s.Metadata.ScreenId.Value) == channel.ScreenId))
            Assert.AreEqual(1, copy.Items.Count(i => i.Is(HierarchicalLabel.Descriptor) && i.Unpack<HierarchicalLabel>().Id.Equals(shared.Id)),
                "Every instance copy holds the one shared label.");
        Assert.AreEqual(1, realization.Operations.Count(o => o.Create is not null && o.Create.Is(HierarchicalLabel.Descriptor)),
            "The shared label is created once.");
        Assert.IsTrue(scene.Geometry.Requests.Select(r => string.Join('/', r.Document.SheetPath.Path.Select(p => p.Value))).ToHashSet()
            .IsSupersetOf(channel.InstancePathKeys), "Every instance path is measured.");
        scene.Geometry.Shift[(channel.InstancePathKeys[1], member.Pin.PlacedPinId)] = new(Grid, 0);
        await RequireRefusal(scene, SchematicConnectionErrors.RealizationPinGeometryMismatch, "drawn differently on instances");
    }

    [TestMethod]
    public async Task GeneratedIdentitiesMustBeNew()
    {
        var scene = RootAddition();
        var member = Member(scene, "TP1");
        var wireId = Generated(scene, scene.Intent.Screens.Single().ScreenId, GeneratedConnectionRole.StubWire, SchematicConnectionIdentity.PinAnchorKey(member.Pin.PlacedPinId));
        // Guard: an unrelated item on the sheet changes nothing but the identities' salt.
        var decorated = scene.Junction(new(250_000_000, 190_000_000));
        Assert.AreEqual(3, Generated<LocalLabel>(await decorated.Realize()).Length);
        // Must-catch: an item the editor already holds under the identity a stub wire would receive.
        var collision = scene.InCheckpoint(data => Root(data).Items.Add(Any.Pack(new Junction { Id = new() { Value = wireId.ToString("D") },
            Position = new() { XNm = 250_000_000, YNm = 190_000_000 }, Locked = LockedState.LsUnlocked })));
        await RequireRefusal(collision, SchematicConnectionErrors.RealizationIdentityCollision, wireId.ToString("D"));
    }

    [TestMethod]
    public async Task LabelsMustFaceAwayFromTheirPin()
    {
        var scene = RootAddition();
        scene.Geometry.LabelBehind = Policy.LabelBackToleranceNm;
        await scene.Realize();
        scene.Geometry.LabelBehind = Policy.LabelBackToleranceNm + 100;
        await RequireRefusal(scene, SchematicConnectionErrors.RealizationLabelOrientationMismatch, "behind its anchor");
    }

    [TestMethod]
    public async Task PreexistingContactsAreRefusedBeforeAnythingIsDrawn()
    {
        var scene = RootAddition();
        var a = scene.PinAt(Member(scene, "TP1").Pin);
        var marked = scene.InCheckpoint(data => Root(data).Items.Add(Any.Pack(new NoConnectMarker { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = new() { XNm = a.X, YNm = a.Y }, Locked = LockedState.LsUnlocked })));
        await RequireRefusal(marked, SchematicConnectionErrors.RealizationNoConnectConflict, "no-connect marker");
        Assert.IsFalse(marked.Geometry.Requests.Any(r => r.ItemCandidates.Count != 0), "Nothing is prototyped once a contact is found.");
        // A created pin on an existing wire joins something the plan never asked for.
        var createdPin = scene.PinAt(scene.Intent.Screens.Single().Islands.SelectMany(i => i.Members).First(m => m.Pin.CreatedSymbol).Pin);
        var wired = scene.InCheckpoint(data => Root(data).Items.Add(Any.Pack(new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Start = new() { XNm = createdPin.X, YNm = createdPin.Y + 10 * Grid }, End = new() { XNm = createdPin.X, YNm = createdPin.Y - 10 * Grid },
            Type = SchematicLineType.SltWire, Locked = LockedState.LsUnlocked })));
        await RequireRefusal(wired, SchematicConnectionErrors.RealizationCreatedPinContact, "lands on an existing connection point");
        // Two created pins stacked on one point: allowed when the plan joins them anyway, and the second needs no stub.
        var stacked = Stacked(joined: true);
        var pins = stacked.Intent.Screens.Single().Islands.Single().Members.Where(m => m.Pin.CreatedSymbol).Select(m => m.Pin).ToArray();
        Assert.HasCount(2, pins);
        stacked.Geometry.Place[pins[1].PlacedPinId] = stacked.PinAt(pins[0]);
        var realization = await stacked.Realize();
        CollectionAssert.AreEquivalent(new[] { GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel },
            realization.Generated.Where(g => pins.Any(p => p.PlacedPinId == g.PlacedPinId)).Select(g => g.Role).ToArray(), "One stub for the stacked pair.");
        // TP1.1 is labelled too. It sits 14.92 mm from the page edge, where its two-grid stub would carry the label across the drawing
        // sheet's inner border, so the frame preference puts the label on the pin itself, inside the border (ledger ped4439a665d260ef).
        Assert.AreEqual(GeneratedConnectionRole.AnchorLabel, realization.Generated.Single(g => g.PlacedPinId == Member(stacked, "TP1").Pin.PlacedPinId).Role);
        Assert.HasCount(1, Generated<SchematicLine>(realization), "The stacked pair's stub is the only wire.");
        // Must-catch: stacked on a pin the plan leaves unconnected, they would join natively.
        var apart = Stacked(joined: false);
        var single = apart.Intent.Screens.Single().Islands.Single().Members.Single(m => m.Pin.CreatedSymbol).Pin;
        var loose = apart.Plan.Candidate!.Schematic.Instances[0].Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Single(s => Guid.Parse(s.Id.Value) == single.SymbolId).Definition.Items.Select(c => c.Item.Unpack<SchematicPin>())
            .Single(p => Guid.Parse(p.Id.Value) != single.PlacedPinId);
        apart.Geometry.Place[Guid.Parse(loose.Id.Value)] = apart.PinAt(single);
        await RequireRefusal(apart, SchematicConnectionErrors.RealizationCreatedPinContact, "lands on an existing connection point");
    }

    [TestMethod]
    public async Task SheetPinsNeedAFreeSlotOnTheFacingEdge()
    {
        var scene = HierarchyCrossing();
        var sheet = scene.Bench!.ChildSheetSymbol;
        // Guard: an existing sheet pin at the first slot moves the new one a pitch further down.
        var existing = new SheetPin { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = new() { XNm = 150_000_000, YNm = 50_000_000 + Policy.SheetPinPitchNm },
            Text = new() { Text_ = "OTHER", Attributes = new() { Multiline = false } }, Side = SheetSide.ShsLeft,
            SpinStyle = SchematicLabelSpinStyle.SlssRight, Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked };
        var occupied = scene.Edited(data => EditSheet(data, sheet, s => s.Pins.Add(existing.Clone())));
        var moved = SheetUpdates(await occupied.Realize()).Single().Pins.Single(p => p.Text.Text_ == "DATA");
        Assert.AreEqual(50_000_000 + 2 * Policy.SheetPinPitchNm, moved.Position.YNm);
        // Must-catch: an obstacle along the whole left edge leaves no slot.
        await RequireRefusal(scene.Obstacle(new(140_000_000, 40_000_000, 149_999_900, 100_000_000)), SchematicConnectionErrors.RealizationNoFreeSheetPinSlot, "no free slot");
        // Must-catch: a sheet symbol too short for any slot.
        await RequireRefusal(scene.Edited(data => EditSheet(data, sheet, s => s.Size.YNm = Policy.SheetPinPitchNm)),
            SchematicConnectionErrors.RealizationNoFreeSheetPinSlot, "no free slot");
    }

    [TestMethod]
    public async Task AGlobalNameIsCarriedByGlobalLabelsAndAStubEndingOnItsPowerSymbolAttaches()
    {
        // GND contains the existing power symbol #PWR01 (global, value GND); a new pin of R1 joins GND.
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid gnd = bench.Part("GND", SchematicSymbolType.SstGlobalPower, new BenchPin("1", "GND", 1, ElectricalPinType.EptPowerInput, false));
        Guid pwr = bench.Component(gnd, "#PWR01", value: "GND");
        Guid r1 = bench.Component(r, "R1");
        var ground = new CircuitNet(Guid.NewGuid(), "GND", [new(pwr, "1")]);
        var scene = Scene.Of(bench, WithFormatting(bench.State([ground])), design => WithNets(design, ground with { Pins = [.. ground.Pins, new(r1, "2")] }));
        var island = scene.Intent.Screens.Single().Islands.Single();
        Assert.AreEqual(ConnectionScope.Global, island.Scope);
        var carrier = island.Members.Single(m => m.Role == ConnectionMemberRole.PowerCarrier);
        var stubbed = island.Members.Single(m => m.RequiresStub);
        var realization = await scene.Realize();
        var label = Generated<GlobalLabel>(realization).Single();
        Assert.AreEqual("GND", label.Text.Text_); Assert.AreEqual(SchematicLabelShape.SlshPassive, label.Shape);
        Assert.IsFalse(realization.Outcomes.Single().AttachedCarrier);
        // The editor must confirm the power symbol pin as global GND; reported as anything else, nothing is drawn.
        scene.Geometry.Power[carrier.Pin.PlacedPinId] = (SchematicPinPowerScope.SppsLocal, "GND");
        await RequireRefusal(scene, SchematicConnectionErrors.RealizationImplicitPowerMismatch, "local power 'GND'");
        scene.Geometry.Power.Clear();
        // With the power symbol's pin exactly at the 2-grid stub end, the stub attaches to it and needs no label.
        var a = scene.PinAt(stubbed.Pin);
        scene.Geometry.Place[carrier.Pin.PlacedPinId] = new(a.X - 2 * Grid, a.Y);
        var attached = await scene.Realize();
        Assert.IsEmpty(Generated<GlobalLabel>(attached));
        Assert.AreEqual(a.X - 2 * Grid, Generated<SchematicLine>(attached).Single().End.XNm);
        Assert.IsTrue(attached.Outcomes.Single().AttachedCarrier);
    }

    [TestMethod]
    public async Task AHierarchicalLabelGoesOnTheFirstStubWithRoomForIt()
    {
        // The local power symbol #LP0 names VLOC on the child sheet; the XML joins R5.1 (and R6.1) there with R1.1 on the
        // root, so the child's island needs a hierarchical label. R5.1's shortest stub ends exactly on #LP0's pin: it
        // attaches without a label, and the power pin on its line blocks every labelled stub from R5.1.
        Scene Local(bool second)
        {
            var bench = new Bench();
            Guid r = bench.Part("R", Passive("1"), Passive("2"));
            Guid local = bench.Part("LOCAL", SchematicSymbolType.SstLocalPower, new BenchPin("1", "~", 1, ElectricalPinType.EptPowerInput, false));
            Guid lp0 = bench.Component(local, "#LP0", BenchSheet.Child, value: "VLOC");
            Guid r5 = bench.Component(r, "R5", BenchSheet.Child), r6 = bench.Component(r, "R6", BenchSheet.Child);
            Guid r1 = bench.Component(r, "R1");
            var vloc = new CircuitNet(Guid.NewGuid(), "VLOC", [new(lp0, "1")]);
            PinEndpoint[] added = second ? [new(r5, "1"), new(r6, "1"), new(r1, "1")] : [new(r5, "1"), new(r1, "1")];
            var scene = Scene.Of(bench, WithFormatting(bench.State([vloc])), design => WithNets(design, vloc with { Pins = [.. vloc.Pins, .. added] }));
            var island = scene.Intent.Screens.SelectMany(s => s.Islands).Single(i => i.UplinkSheetSymbolId is not null);
            Assert.AreEqual(ConnectionScope.Local, island.Scope);
            var carrier = island.Members.Single(m => m.Role == ConnectionMemberRole.PowerCarrier);
            var r5Pin = island.Members.Single(m => m.Pin.Endpoint.ComponentId == r5).Pin;
            var at = scene.PinAt(r5Pin);
            var end = new Point(at.X - 2 * Grid, at.Y);
            scene.Geometry.Place[carrier.Pin.PlacedPinId] = end;
            scene.Geometry.Body[carrier.Pin.SymbolId] = new(end.X - 2 * Grid, end.Y - Grid, end.X, end.Y + Grid);
            return scene;
        }
        // Guard: R6.1's stub has room, so it carries the hierarchical label while R5.1 attaches.
        var shared = Local(second: true);
        var realization = await shared.Realize();
        var childScreen = shared.Bench!.ScreenId(BenchSheet.Child);
        var hierarchical = Generated<HierarchicalLabel>(realization).Single();
        Assert.AreEqual("VLOC", hierarchical.Text.Text_);
        var carried = realization.Generated.Single(g => g.Id.ToString("D") == hierarchical.Id.Value);
        Assert.AreEqual(shared.Intent.Screens.SelectMany(s => s.Islands).SelectMany(i => i.Members)
            .Single(m => m.Pin.Endpoint.ComponentId == shared.Plan.Candidate!.Engineering.Circuit.Components.Single(c => c.Reference == "R6").Id).Pin.PlacedPinId,
            carried.PlacedPinId);
        Assert.IsTrue(realization.Outcomes.Single(o => o.ScreenId == childScreen).AttachedCarrier);
        Assert.IsFalse(Generated<LocalLabel>(realization).Any(l => realization.Generated.Any(g => g.ScreenId == childScreen && g.Id.ToString("D") == l.Id.Value)),
            "R5.1's stub carries no label of its own.");
        // Must-catch: with R5.1 alone, its only stub attaches, so nothing on the child sheet can carry the label.
        var alone = Local(second: false);
        await RequireRefusal(alone, SchematicConnectionErrors.RealizationNoFreeStub, "every new stub there ends on one of its power symbols");
    }

    [TestMethod]
    public async Task FixedLabelsAreDrawnBeforeAHierarchicalLabelThatAnotherStubCanCarry()
    {
        // KiCad's own label shapes (the recorded PSU/CPU measurements): a local label reaches 2.0957 mm above its wire and
        // 0.2652 mm below it, a hierarchical label 0.81 mm to either side. On the child sheet U1.1 (net UPLINK_NET, which
        // crosses to root R1.1 and so needs a hierarchical label there) sits one pin pitch (2.54 mm) above U1.2 (net
        // LOCAL_SIGNAL_NET, local to the child sheet), both pointing left. UPLINK_NET is realized after LOCAL_SIGNAL_NET
        // although its net identity sorts first: a hierarchical label on U1.1 would overlap U1.2's local label at every stub
        // length, which is what happened to the LP3982's pins 1 and 8 in the PSU/CPU journey before this rule (drawn first, the
        // hierarchical label left U1.2 no room, and the whole realization was refused). So U1.2 gets its local label, U1.1's
        // stub (no room for the hierarchical label) carries a local label as well, and U3.1, further up the sheet, carries the
        // hierarchical label. Must-catch: without U3, nothing on the child sheet can carry it and the refusal names U1.1's stub.
        Scene Build(bool carrier)
        {
            var bench = new Bench();
            Guid u = bench.Part("U", Passive("1"), Passive("2")), r = bench.Part("R", Passive("1"), Passive("2"));
            // U1 sorts before U3, so U1.1 is UPLINK_NET's first stub on the child sheet.
            Guid u1 = bench.Component(u, "U1", BenchSheet.Child, id: Guid.Parse("00000000-0000-4000-8000-0000000000a1"));
            Guid u4 = bench.Component(u, "U4", BenchSheet.Child);
            Guid u3 = carrier ? bench.Component(u, "U3", BenchSheet.Child, id: Guid.Parse("00000000-0000-4000-8000-0000000000a3")) : Guid.Empty;
            Guid r1 = bench.Component(r, "R1");
            var uplink = new CircuitNet(Guid.Parse("00000000-0000-4000-8000-000000000001"), "UPLINK_NET",
                [new(u1, "1"), new(r1, "1"), .. carrier ? [new PinEndpoint(u3, "1")] : Array.Empty<PinEndpoint>()]);
            var local = new CircuitNet(Guid.Parse("00000000-0000-4000-8000-000000000002"), "LOCAL_SIGNAL_NET", [new(u1, "2"), new(u4, "1")]);
            var scene = Scene.Of(bench, WithFormatting(bench.State([])), design => WithNets(design, uplink, local));
            var members = scene.Intent.Screens.SelectMany(s => s.Islands).Where(i => i.ScreenId == bench.ScreenId(BenchSheet.Child))
                .SelectMany(i => i.Members).ToDictionary(m => (m.Pin.Endpoint.ComponentId, m.Pin.Endpoint.Pin));
            void Draw(Guid component, long x, long y)
            {
                var pin1 = members.TryGetValue((component, "1"), out var first) ? first.Pin : null;
                var pin2 = members.TryGetValue((component, "2"), out var second) ? second.Pin : null;
                var symbol = (pin1 ?? pin2)!.SymbolId;
                var symbolPins = scene.Plan.Candidate!.Schematic.Instances.First(i => i.Metadata.ScreenId.Value == bench.ScreenId(BenchSheet.Child).ToString("D")).Items
                    .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()).Single(i => Guid.Parse(i.Id.Value) == symbol)
                    .Definition.Items.Select(c => c.Item.Unpack<SchematicPin>()).ToDictionary(p => p.Number, p => Guid.Parse(p.Id.Value));
                scene.Geometry.Place[symbolPins["1"]] = new(x, y);
                scene.Geometry.Place[symbolPins["2"]] = new(x, y + 2 * Grid);
                scene.Geometry.Body[symbol] = new(x, y - Grid, x + 8 * Grid, y + 3 * Grid);
            }
            Draw(u1, 100_000_000, 100_000_000);
            Draw(u4, 100_000_000, 150_000_000);
            if (carrier) Draw(u3, 100_000_000, 40_000_000);
            scene.Geometry.Tamper = (request, reply) =>
            {
                for (int i = 0; i < reply.ItemCandidates.Count; i++)
                {
                    var measured = reply.ItemCandidates[i];
                    long y = measured.Anchor.YNm, x0 = measured.Bounds.Position.XNm, x1 = x0 + measured.Bounds.Size.XNm;
                    bool hierarchical = request.ItemCandidates[i].Is(HierarchicalLabel.Descriptor);
                    long top = y - (hierarchical ? 809_600 : 2_095_700), bottom = y + (hierarchical ? 809_700 : 265_200);
                    measured.Bounds = new() { Position = new() { XNm = x0, YNm = top }, Size = new() { XNm = x1 - x0, YNm = bottom - top } };
                }
                return reply;
            };
            return scene;
        }
        var guarded = Build(carrier: true);
        var realization = await guarded.Realize();
        var child = guarded.Bench!.ScreenId(BenchSheet.Child);
        var circuit = guarded.Plan.Candidate!.Engineering.Circuit;
        Guid Pin(string reference, string number) => guarded.Intent.Screens.SelectMany(s => s.Islands).SelectMany(i => i.Members)
            .Single(m => m.Pin.Endpoint == new PinEndpoint(circuit.Components.Single(c => c.Reference == reference).Id, number)).Pin.PlacedPinId;
        var hierarchical = realization.Generated.Single(g => g.ScreenId == child && g.TypeUrl == Any.Pack(new HierarchicalLabel()).TypeUrl);
        Assert.AreEqual(Pin("U3", "1"), hierarchical.PlacedPinId, "U3.1's stub has room for the hierarchical label.");
        string LabelOn(string reference, string number) => realization.Generated
            .Single(g => g.PlacedPinId == Pin(reference, number) && g.Role == GeneratedConnectionRole.StubLabel).TypeUrl;
        Assert.AreEqual(Any.Pack(new LocalLabel()).TypeUrl, LabelOn("U1", "2"), "U1.2 keeps its local label.");
        Assert.AreEqual(Any.Pack(new LocalLabel()).TypeUrl, LabelOn("U1", "1"), "U1.1 has no room for a hierarchical label beside U1.2's local label.");
        Assert.AreEqual(2 * Grid, 100_000_000 - StubOf(realization, Pin("U1", "2")).End.XNm, "U1.2 keeps its shortest stub.");
        Assert.AreEqual(2 * Grid, 100_000_000 - StubOf(realization, Pin("U1", "1")).End.XNm, "U1.1's local label fits its shortest stub.");
        await RequireRefusal(Build(carrier: false), SchematicConnectionErrors.RealizationNoFreeStub, "none of its new stubs there has room for one");
    }

    [TestMethod]
    public async Task AJoinWithoutRoomForTheHierarchicalLabelNamesItsConnectionLocallyAndALaterStubCarriesIt()
    {
        // On the child sheet R1.1 and R2.1 are joined by an unlabelled wire. The XML joins root R5.1 to that connection (net
        // LINK) and, with `carrier`, the child's unconnected TP1.1 as well. The child's island must name its connection (a join,
        // §6.3 (a)) and needs a hierarchical label for its crossing to the root, which its first labelled stub carries when it
        // has room. A join candidate without room for the hierarchical label, at any stub length or as a label on its pin, but
        // with room for a local label is joined with a local label; a later stub of the island then carries the hierarchical
        // label, and when none can, the refusal names the join (the §6.3 (d) clarification requested from the integration owner).
        // R1.1 sits 14.92 mm from the page edge, where the frame preference would name the connection at R2.1 inside the drawing
        // sheet's border instead (AnUnlabelledConnectionIsNamedAtItsOwnPinAndReported); this clarification is checked with an editor
        // that does not measure its drawing sheet, and so has no border to prefer.
        Scene Build(bool carrier)
        {
            var bench = new Bench();
            Guid r = bench.Part("R", Passive("1"), Passive("2")), tp = bench.Part("TP", Passive("1"));
            Guid r1 = bench.Component(r, "R1", BenchSheet.Child), r2 = bench.Component(r, "R2", BenchSheet.Child);
            Guid tp1 = bench.Component(tp, "TP1", BenchSheet.Child), r5 = bench.Component(r, "R5");
            string wire = bench.Wire(BenchSheet.Child);
            var link = new CircuitNet(Guid.NewGuid(), "LINK", [new(r1, "1"), new(r2, "1")]);
            var state = WithFormatting(bench.State([link], new() { [link.Id] = [(BenchSheet.Child, wire)] }));
            PinEndpoint[] added = [new(r5, "1"), .. carrier ? [new PinEndpoint(tp1, "1")] : Array.Empty<PinEndpoint>()];
            var scene = Scene.Of(bench, state, design => WithNets(design, link with { Pins = [.. link.Pins, .. added] }));
            scene.Geometry.DrawingSheet = null;
            return scene;
        }
        static ConnectionIsland ChildIsland(Scene scene) =>
            scene.Intent.Screens.SelectMany(s => s.Islands).Single(i => i.ScreenId == scene.Bench!.ScreenId(BenchSheet.Child));
        var roomy = Build(carrier: true);
        var child = roomy.Bench!.ScreenId(BenchSheet.Child);
        var island = ChildIsland(roomy);
        Assert.IsTrue(island.JoinRequired, "LINK is drawn on the child sheet without a label.");
        Assert.IsNotNull(island.UplinkSheetSymbolId, "LINK crosses from the child sheet to the root.");
        var candidates = island.JoinCandidates;
        Assert.HasCount(2, candidates);
        var tp1 = island.Members.Single(m => m.RequiresStub).Pin;
        string hierarchical = Any.Pack(new HierarchicalLabel()).TypeUrl, local = Any.Pack(new LocalLabel()).TypeUrl;
        static GeneratedConnectionItem LabelOn(SchematicConnectionRealization realization, Guid pin) => realization.Generated
            .Single(g => g.PlacedPinId == pin && g.Role is GeneratedConnectionRole.StubLabel or GeneratedConnectionRole.AnchorLabel);
        static int HierarchicalOn(SchematicConnectionRealization realization, Guid screen) =>
            realization.Generated.Count(g => g.ScreenId == screen && g.TypeUrl == Any.Pack(new HierarchicalLabel()).TypeUrl);

        // Guard: with room, the join is the island's first labelled stub and carries the hierarchical label; TP1.1's is local.
        var joined = await roomy.Realize();
        Assert.AreEqual(GeneratedConnectionRole.StubLabel, LabelOn(joined, candidates[0].PlacedPinId).Role);
        Assert.AreEqual(hierarchical, LabelOn(joined, candidates[0].PlacedPinId).TypeUrl, "The join carries the hierarchical label.");
        Assert.AreEqual(local, LabelOn(joined, tp1.PlacedPinId).TypeUrl);
        Assert.AreEqual(1, HierarchicalOn(joined, child));

        // A keep-out left of both candidates that a local label on the pin just clears and a hierarchical one (a grid longer)
        // does not; every stub's label runs into it. Must-catch for the anchor branch: the first candidate gets a local label
        // on its pin, and TP1.1's stub carries the hierarchical label.
        static Scene Anchored(Scene scene)
        {
            foreach (var candidate in ChildIsland(scene).JoinCandidates)
            {
                var a = scene.PinAt(candidate);
                scene = scene.Obstacle(BenchSheet.Child, new(a.X - 12 * Grid, a.Y - Grid, a.X - 4 * Grid - Grid / 4, a.Y + Grid));
            }
            return scene;
        }
        var anchored = await Anchored(roomy).Realize();
        var anchorLabel = LabelOn(anchored, candidates[0].PlacedPinId);
        Assert.AreEqual(GeneratedConnectionRole.AnchorLabel, anchorLabel.Role);
        Assert.AreEqual(local, anchorLabel.TypeUrl, "No room for the hierarchical label at either candidate: the join is named locally.");
        Assert.IsFalse(anchored.Generated.Any(g => g.PlacedPinId == candidates[1].PlacedPinId), "One join names the connection.");
        Assert.AreEqual(hierarchical, LabelOn(anchored, tp1.PlacedPinId).TypeUrl, "The island's next labelled stub carries the hierarchical label.");
        Assert.AreEqual(1, HierarchicalOn(anchored, child));

        // Must-catch for the join-stub branch: KiCad's hierarchical label drawn two grids to either side of its wire (a local
        // one half a grid), and a keep-out strip from one to two grids above each candidate. No hierarchical label fits at either
        // candidate, as a stub or on the pin, while the first candidate's shortest join stub has room for a local label.
        static Scene Strips(Scene scene)
        {
            foreach (var candidate in ChildIsland(scene).JoinCandidates)
            {
                var a = scene.PinAt(candidate);
                scene = scene.Obstacle(BenchSheet.Child, new(a.X - 12 * Grid, a.Y - 2 * Grid, a.X - Grid, a.Y - Grid));
            }
            scene.Geometry.Tamper = (request, reply) =>
            {
                for (int i = 0; i < reply.ItemCandidates.Count; i++)
                {
                    if (!request.ItemCandidates[i].Is(HierarchicalLabel.Descriptor)) continue;
                    var measured = reply.ItemCandidates[i];
                    measured.Bounds = new() { Position = new() { XNm = measured.Bounds.Position.XNm, YNm = measured.Anchor.YNm - 2 * Grid },
                        Size = new() { XNm = measured.Bounds.Size.XNm, YNm = 4 * Grid } };
                }
                return reply;
            };
            return scene;
        }
        var stubbed = await Strips(roomy).Realize();
        var joinLabel = LabelOn(stubbed, candidates[0].PlacedPinId);
        Assert.AreEqual(GeneratedConnectionRole.StubLabel, joinLabel.Role);
        Assert.AreEqual(local, joinLabel.TypeUrl);
        Assert.AreEqual(2 * Grid, roomy.PinAt(candidates[0]).X - StubOf(stubbed, candidates[0].PlacedPinId).End.XNm, "The shortest join stub.");
        Assert.AreEqual(hierarchical, LabelOn(stubbed, tp1.PlacedPinId).TypeUrl);
        Assert.AreEqual(1, HierarchicalOn(stubbed, child));

        // Must-catch: without TP1.1 nothing else on the child sheet can carry the hierarchical label, and the refusal names the
        // join that carries a local label instead, for either branch.
        var alone = Build(carrier: false);
        var first = ChildIsland(alone).JoinCandidates[0].Endpoint;
        string join = "so the stub of pin " + PinName(alone.Plan.Candidate!, first) + " carries a local label instead";
        await RequireRefusal(Anchored(alone), SchematicConnectionErrors.RealizationNoFreeStub, join, "A local label on the joined pin.");
        await RequireRefusal(Strips(alone), SchematicConnectionErrors.RealizationNoFreeStub, join, "A local label on the join stub.");
        // Must-catch: with no room even for a local label at either candidate, the join itself is refused, naming both tries.
        var boxed = roomy;
        foreach (var candidate in candidates)
        {
            var a = roomy.PinAt(candidate);
            boxed = boxed.Obstacle(BenchSheet.Child, new(a.X - 12 * Grid, a.Y - Grid, a.X - Grid / 2, a.Y + Grid));
        }
        await RequireRefusal(boxed, SchematicConnectionErrors.RealizationNoJoinAnchor, "(hierarchical label)");
        await RequireRefusal(boxed, SchematicConnectionErrors.RealizationNoJoinAnchor, "(local label)");
    }

    [TestMethod]
    public async Task ASheetPinWithoutRoomForTheHierarchicalLabelCarriesALocalOneAndTheNextCrossingCarriesIt()
    {
        // Net PASS joins root R5.1 with R7.1 and R8.1 on the child sheet's two grandchildren, so the child sheet has no pin of
        // PASS: its island only crosses up to the root (a hierarchical label) and down through two new sheet pins, one on each
        // grandchild's sheet symbol, allocated in port-text then sheet-symbol order. Their stubs run right from the symbols'
        // right edges (x = 190 mm), since the island has no members (§6.5), and the first one carries the hierarchical label.
        // A crossing whose stub has no room for the hierarchical label at any slot, but room for a local one, carries a local
        // label, and the next crossing carries the hierarchical label; when none can, the refusal names every crossing (the
        // §6.3 (d) clarification requested from the integration owner).
        var bench = new Bench(secondGrand: true);
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid r5 = bench.Component(r, "R5"), r7 = bench.Component(r, "R7", BenchSheet.Grand), r8 = bench.Component(r, "R8", BenchSheet.Grand2);
        var pass = new CircuitNet(Guid.NewGuid(), "PASS", [new(r5, "1"), new(r7, "1"), new(r8, "1")]);
        var scene = Scene.Of(bench, WithFormatting(bench.State([])), design => WithNets(design, pass));
        Guid child = bench.ScreenId(BenchSheet.Child);
        var island = scene.Intent.Screens.SelectMany(s => s.Islands).Single(i => i.ScreenId == child);
        Assert.IsEmpty(island.Members);
        Assert.AreEqual(bench.ChildSheetSymbol, island.UplinkSheetSymbolId);
        Guid[] crossings = [.. new[] { bench.SheetSymbolOf(BenchSheet.Grand), bench.SheetSymbolOf(BenchSheet.Grand2) }.Order()];
        CollectionAssert.AreEquivalent(crossings, island.ChildSheetSymbolIds.ToArray());
        string hierarchical = Any.Pack(new HierarchicalLabel()).TypeUrl, local = Any.Pack(new LocalLabel()).TypeUrl;
        long TopOf(Guid sheet) => sheet == bench.SheetSymbolOf(BenchSheet.Grand) ? 50_000_000 : 100_000_000;
        static string LabelAt(SchematicConnectionRealization realization, Guid sheet) =>
            realization.Generated.Single(g => g.SheetSymbolId == sheet && g.Role == GeneratedConnectionRole.SheetPinLabel).TypeUrl;
        // A keep-out right of a crossing's sheet symbol, over its whole edge: a local label at the end of the shortest stub
        // (3.75 grids long for PASS) clears it by half a grid, a hierarchical one (a grid longer) does not, and every longer
        // stub's label runs into it. Without `localFits` it starts right after the shortest stub, so no label fits at all.
        Scene Blocked(Scene from, Guid sheet, bool localFits = true) => from.Obstacle(BenchSheet.Child,
            new(190_000_000 + 2 * Grid + (localFits ? 3 * Grid + 3 * Grid / 4 + Grid / 2 : Grid), TopOf(sheet) - Grid, 230_000_000, TopOf(sheet) + 40_000_000 + Grid));

        // Guard: the first crossing carries the hierarchical label and the second a local one.
        var free = await scene.Realize();
        Assert.AreEqual(hierarchical, LabelAt(free, crossings[0]));
        Assert.AreEqual(local, LabelAt(free, crossings[1]));
        // Must-catch: with no room for the hierarchical label beside the first crossing, it carries a local label and the
        // second crossing carries the hierarchical one, each at its first slot.
        var second = await Blocked(scene, crossings[0]).Realize();
        Assert.AreEqual(local, LabelAt(second, crossings[0]));
        Assert.AreEqual(hierarchical, LabelAt(second, crossings[1]));
        Assert.AreEqual(1, second.Generated.Count(g => g.ScreenId == child && g.TypeUrl == hierarchical));
        foreach (var sheet in crossings)
            Assert.AreEqual(TopOf(sheet) + Policy.SheetPinPitchNm, SheetUpdates(second).Single(u => u.Id.Value == sheet.ToString("D")).Pins.Single().Position.YNm);
        // Must-catch: with no room for it beside either crossing, the refusal names both, which carry local labels instead.
        var neither = Blocked(Blocked(scene, crossings[0]), crossings[1]);
        foreach (var sheet in crossings)
            await RequireRefusal(neither, SchematicConnectionErrors.RealizationNoFreeStub, "sheet pin 'PASS' on sheet symbol " + sheet.ToString("D"));
        await RequireRefusal(neither, SchematicConnectionErrors.RealizationNoFreeStub, "none of its new stubs there has room for one");
        // Must-catch: a crossing with no room for any label at any slot has no free slot, as before.
        await RequireRefusal(Blocked(scene, crossings[0], localFits: false), SchematicConnectionErrors.RealizationNoFreeSheetPinSlot, "no free slot");
    }

    [TestMethod]
    public async Task ANewPinStackedOnAnExistingConnectionCarriesTheHierarchicalLabelOnlyWhenItHasRoom()
    {
        // VLOC is named on the child sheet by the local power symbol #LP0. The XML creates R5 on the child sheet with R5.1
        // drawn exactly on #LP0's pin, so KiCad joins R5.1 to VLOC by that contact alone, and joins root R1.1 to VLOC: the
        // child's island needs a hierarchical label. With `later`, it also creates R6 and joins R6.1 there; with `grand`,
        // R7.1 on the grandchild sheet joins as well, so the child sheet gains a new sheet pin with its own stub.
        (Scene Scene, ConnectionPlacedPin Stacked, ConnectionPlacedPin? Later, Point At) Build(bool later, bool grand)
        {
            var bench = new Bench();
            Guid r = bench.Part("R", Passive("1"), Passive("2"));
            Guid local = bench.Part("LOCAL", SchematicSymbolType.SstLocalPower, new BenchPin("1", "~", 1, ElectricalPinType.EptPowerInput, false));
            Guid lp0 = bench.Component(local, "#LP0", BenchSheet.Child, value: "VLOC");
            Guid r1 = bench.Component(r, "R1");
            Guid r7 = grand ? bench.Component(r, "R7", BenchSheet.Grand) : Guid.Empty;
            var vloc = new CircuitNet(Guid.NewGuid(), "VLOC", [new(lp0, "1")]);
            var state = WithFormatting(bench.State([vloc]));
            var r5 = bench.Create(state.Baseline, r, "R5", BenchSheet.Child);
            var r6 = later ? bench.Create(r5.Design, r, "R6", BenchSheet.Child) : r5;
            PinEndpoint[] added = [new(r5.Component, "1"), .. later ? [new PinEndpoint(r6.Component, "1")] : Array.Empty<PinEndpoint>(), new(r1, "1"),
                .. grand ? [new PinEndpoint(r7, "1")] : Array.Empty<PinEndpoint>()];
            var scene = Scene.Of(bench, state, design => WithNets(Adopt(design, r6.Design), vloc with { Pins = [.. vloc.Pins, .. added] }));
            var island = scene.Intent.Screens.SelectMany(s => s.Islands).Single(i => i.ScreenId == bench.ScreenId(BenchSheet.Child));
            Assert.IsNotNull(island.UplinkSheetSymbolId);
            Assert.IsFalse(island.JoinRequired, "#LP0 already names VLOC on the child sheet.");
            var carrier = island.Members.Single(m => m.Role == ConnectionMemberRole.PowerCarrier);
            Assert.IsTrue(carrier.AlreadyConnected);
            // With two new pins the one drawn first sits on #LP0's pin, so the other one is the later stub.
            var stubbed = island.Members.Where(m => m.RequiresStub).Select(m => m.Pin).ToArray();
            Assert.HasCount(later ? 2 : 1, stubbed);
            Assert.IsTrue(stubbed.All(p => p.CreatedSymbol));
            // The new resistor drawn first puts its pin 1 exactly on #LP0's pin, pointing left like it, with its body to the
            // right and pin 2 two grids below; the other one is drawn the same way eight grids further down.
            var at = scene.PinAt(carrier.Pin);
            void Draw(ConnectionPlacedPin pin, Point p1)
            {
                var symbol = scene.Plan.Candidate!.Schematic.Instances.First(x => Key(x) == pin.SheetPathKey).Items
                    .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                    .Single(x => Guid.Parse(x.Id.Value) == pin.SymbolId);
                var pin2 = symbol.Definition.Items.Select(c => c.Item.Unpack<SchematicPin>()).Single(p => p.Number == "2");
                scene.Geometry.Place[pin.PlacedPinId] = p1;
                scene.Geometry.Place[Guid.Parse(pin2.Id.Value)] = new(p1.X, p1.Y + 2 * Grid);
                scene.Geometry.Body[pin.SymbolId] = new(p1.X, p1.Y - Grid, p1.X + 4 * Grid, p1.Y + 3 * Grid);
            }
            Draw(stubbed[0], at);
            if (later) Draw(stubbed[1], new(at.X, at.Y + 8 * Grid));
            return (scene, stubbed[0], later ? stubbed[1] : null, at);
        }
        GeneratedConnectionItem Hierarchical(Scene scene, SchematicConnectionRealization realization) => realization.Generated.Single(g =>
            g.ScreenId == scene.Bench!.ScreenId(BenchSheet.Child) && g.TypeUrl == Any.Pack(new HierarchicalLabel()).TypeUrl);
        // The stub room to the left of the stacked pins on the child sheet: a keep-out over every stub length and its label.
        static Scene Boxed(Scene scene, Point at)
        {
            var id = Guid.NewGuid();
            var child = scene.Bench!.ScreenId(BenchSheet.Child).ToString("D");
            var copy = scene.Edited(data => data.Instances.Single(s => s.Metadata.ScreenId.Value == child).Items.Add(Any.Pack(new SchematicText
            {
                Id = new() { Value = id.ToString("D") }, Locked = LockedState.LsUnlocked,
                Text = new() { Text_ = "keep out", Position = new() { XNm = at.X - 9 * Grid, YNm = at.Y - Grid }, Attributes = new() { Multiline = true } }
            })));
            copy.Geometry.Sized[id] = new(at.X - 9 * Grid, at.Y - Grid, at.X - Grid / 2, at.Y + Grid);
            return copy;
        }

        // With room, the stacked pin's stub starts on #LP0's pin and carries the hierarchical label.
        var (alone, stacked, _, at) = Build(later: false, grand: false);
        var carried = await alone.Realize();
        var label = Hierarchical(alone, carried);
        Assert.AreEqual(stacked.PlacedPinId, label.PlacedPinId);
        Assert.AreEqual(GeneratedConnectionRole.StubLabel, label.Role);
        var wire = StubOf(carried, stacked.PlacedPinId);
        Assert.AreEqual((at.X, at.Y), (wire.Start.XNm, wire.Start.YNm), "The stub starts on the existing connection it joins.");
        Assert.AreEqual(at.X - 2 * Grid, wire.End.XNm);

        // KiCad's bounds of #LP0 reach past its pin by the target drawn on an unconnected pin end (PinTargetReachNm, as every
        // visible pin of the live sheets does), towards the stub. Guard: a stub starting across exactly that target still
        // carries the label. Must-catch: #LP0 drawing 100 nm further leaves the stub crossing #LP0 itself at every length,
        // so nothing on the child sheet can carry the label and the refusal names the stacked pin.
        var lp0 = alone.Intent.Screens.SelectMany(s => s.Islands).Single(i => i.ScreenId == alone.Bench!.ScreenId(BenchSheet.Child))
            .Members.Single(m => m.Role == ConnectionMemberRole.PowerCarrier).Pin.SymbolId;
        Scene Reaching(long reach)
        {
            var reaching = alone with { Geometry = alone.Geometry.Copy() };
            reaching.Geometry.Body[lp0] = new(at.X - reach, at.Y - 2 * Grid, at.X + 8 * Grid, at.Y + 2 * Grid);
            return reaching;
        }
        var acrossTarget = await Reaching(SchematicConnectionRealizer.PinTargetReachNm).Realize();
        Assert.AreEqual(stacked.PlacedPinId, Hierarchical(alone, acrossTarget).PlacedPinId, "#LP0 reaching exactly its pin target.");
        var acrossWire = StubOf(acrossTarget, stacked.PlacedPinId);
        Assert.AreEqual((at.X, at.Y, at.X - 2 * Grid), (acrossWire.Start.XNm, acrossWire.Start.YNm, acrossWire.End.XNm));
        await RequireRefusal(Reaching(SchematicConnectionRealizer.PinTargetReachNm + 100), SchematicConnectionErrors.RealizationNoFreeStub,
            "new pin " + PinName(alone.Plan.Candidate!, stacked.Endpoint) + " sits on an existing connection");

        // Must-catch: without room there, and with nothing else on the child sheet to carry it, the label cannot be drawn.
        // The refusal names the stacked pin (it used to surface as an internal inconsistency once the stub became optional).
        var boxed = Boxed(alone, at);
        await RequireRefusal(boxed, SchematicConnectionErrors.RealizationNoFreeStub, "new pin " + PinName(alone.Plan.Candidate!, stacked.Endpoint) + " sits on an existing connection");

        // Guard: without room for the stacked pin's stub, the later new pin's stub carries the label, and the stacked pin
        // gets nothing: KiCad joins it through the contact.
        var (withLater, first, second, laterAt) = Build(later: true, grand: false);
        var byLater = await Boxed(withLater, laterAt).Realize();
        Assert.AreEqual(second!.PlacedPinId, Hierarchical(withLater, byLater).PlacedPinId);
        Assert.IsFalse(byLater.Generated.Any(g => g.PlacedPinId == first.PlacedPinId));
        Assert.IsTrue(byLater.Outcomes.All(o => o.FallbackReason is null));

        // Guard: without room for the stacked pin's stub, the child sheet's new sheet pin for the grandchild carries it.
        var (withGrand, onCarrier, _, grandAt) = Build(later: false, grand: true);
        var bySheetPin = await Boxed(withGrand, grandAt).Realize();
        var sheetLabel = Hierarchical(withGrand, bySheetPin);
        Assert.AreEqual(GeneratedConnectionRole.SheetPinLabel, sheetLabel.Role);
        Assert.AreEqual(withGrand.Bench!.ScreenId(BenchSheet.Child), sheetLabel.ScreenId);
        Assert.IsFalse(bySheetPin.Generated.Any(g => g.PlacedPinId == onCarrier.PlacedPinId));
    }

    [TestMethod]
    public async Task ALibraryCacheChangesOnlyByTheDefinitionsOfSymbolsCreatedOnItsSheet()
    {
        // The batch check (I6): a sheet's library cache may change only on a sheet that receives a new symbol, and only by the
        // definitions of the symbols created on that sheet. Every definition KiCad already holds there must stay exactly as it is.
        static SchematicCachedSymbol Entry(SchematicHierarchyData data, string reference)
        {
            var symbol = data.Instances.SelectMany(s => s.Items).Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .First(s => s.ReferenceField.Text.Text_ == reference);
            // As KiCad keeps a cached definition: every child names its unit and body style.
            var definition = symbol.Definition.Clone();
            foreach (var child in definition.Items) { child.Unit ??= new(); child.BodyStyle ??= new(); }
            return new SchematicCachedSymbol { CacheKey = symbol.Definition.Id.LibraryNickname + ":" + symbol.Definition.Id.EntryName,
                Definition = definition, ShowPinNames = true, ShowPinNumbers = true };
        }
        SchematicCachedSymbol[] Replacements(SchematicConnectionRealization realization) =>
            [.. realization.Operations.Where(o => o.ReplaceLibraryCache is not null).SelectMany(o => o.ReplaceLibraryCache.Definitions)];

        // Guard: R2 is created on the root sheet, whose cache the checkpoint lacks R's definition for; the batch adds it.
        var scene = RootAddition();
        var entry = Entry(scene.Saved.Observed, "R1");
        var cached = scene.Edited(data => Root(data).CachedSymbols.Add(entry.Clone()));
        var adding = cached.InCheckpoint(data => Root(data).CachedSymbols.Clear());
        var added = Replacements(await adding.Realize());
        Assert.AreEqual(entry.CacheKey, added.Single().CacheKey, "The new symbol's definition is added.");
        // Guard: the cache holding it already is kept as KiCad holds it.
        Assert.IsTrue(SchematicLibraryCacheEquivalence.Equal(entry, Replacements(await cached.Realize()).Single()));

        // Must-catch: KiCad holds another version of that definition; the replacement would silently change it.
        var changed = cached.InCheckpoint(data => Root(data).CachedSymbols[0].Definition.Keywords = "edited in KiCad");
        await RequireRefusal(changed, SchematicConnectionErrors.ConnectedInternalInconsistency, "would change the library definition '" + entry.CacheKey + "'");
        // Must-catch: KiCad holds a definition the plan does not; the replacement would drop it.
        var extra = Entry(scene.Saved.Observed, "TP1");
        var dropped = cached.InCheckpoint(data => Root(data).CachedSymbols.Add(extra.Clone()));
        await RequireRefusal(dropped, SchematicConnectionErrors.ConnectedInternalInconsistency, "would drop the library definition '" + extra.CacheKey + "'");
        // Must-catch: beside the new R2's definition, the replacement would add TP's, which no new symbol on the root sheet
        // uses (an earlier rule accepted any added definition once the sheet received some new symbol).
        var smuggled = scene.Edited(data => { Root(data).CachedSymbols.Add(entry.Clone()); Root(data).CachedSymbols.Add(extra.Clone()); })
            .InCheckpoint(data => Root(data).CachedSymbols.Clear());
        await RequireRefusal(smuggled, SchematicConnectionErrors.ConnectedInternalInconsistency,
            "would add the library definition '" + extra.CacheKey + "' to sheet " + scene.Bench!.Path(BenchSheet.Root) + ", which no new symbol on that sheet uses");

        // Must-catch: U1 is created on the child sheet only, yet the root sheet's cache would be replaced (an earlier rule
        // accepted any replacement once some symbol was created anywhere).
        var crossing = HierarchyCrossing();
        var rootEntry = Entry(crossing.Saved.Observed, "R5");
        var elsewhere = crossing.InCheckpoint(data => Root(data).CachedSymbols.Add(rootEntry.Clone()));
        await RequireRefusal(elsewhere, SchematicConnectionErrors.ConnectedInternalInconsistency, "which receives no new symbol");
        // Must-catch: with no new symbol at all, no cache may be replaced.
        var link = Link();
        var linkEntry = Entry(link.Saved.Observed, "R1");
        await RequireRefusal(link.InCheckpoint(data => Root(data).CachedSymbols.Add(linkEntry.Clone())),
            SchematicConnectionErrors.ConnectedInternalInconsistency, "which receives no new symbol");
        // Guards: unchanged caches are never replaced where nothing is created.
        Assert.IsEmpty(Replacements(await crossing.Realize()).Where(d => d.CacheKey == rootEntry.CacheKey));
        Assert.IsEmpty(Replacements(await link.Realize()));
    }

    [TestMethod]
    public async Task TheBatchCreatesOnlyPlannedItemsAndNeverChangesAnExistingOne()
    {
        // I6 is checked before the assertion is added, so KiCad never commits an edit of an existing item that only the
        // resolution would notice afterwards. Guard: a new sheet pin is an update of its sheet symbol by exactly that pin.
        var crossing = HierarchyCrossing();
        var realization = await crossing.Realize();
        var update = realization.Operations.Single(o => o.Update is not null).Update.Unpack<SheetSymbol>();
        var before = Root(crossing.Checkpoint.Electrical.Hierarchy.Data).Items.Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>())
            .Single(s => s.Id.Equals(update.Id));
        Assert.AreEqual(before.Pins.Count + 1, update.Pins.Count);
        // Must-catch: the editor's sheet symbol is not the planned one, so the update would also resize it.
        var resized = crossing.InCheckpoint(data => EditSheet(data, crossing.Bench!.ChildSheetSymbol, s => s.Size.YNm += Grid));
        await RequireRefusal(resized, SchematicConnectionErrors.ConnectedInternalInconsistency, "beyond adding its generated sheet pins");
        // Must-catch: an existing symbol the editor holds elsewhere would be moved back by the batch.
        var scene = RootAddition();
        var moved = scene.InCheckpoint(data => EditItems(data, item => item is SchematicSymbolInstance s && s.Id.Value == SymbolId(scene, "R9").ToString("D")
            ? Edit(s, x => x.Position.YNm += Grid) : item));
        await RequireRefusal(moved, SchematicConnectionErrors.ConnectedInternalInconsistency, "which it must never touch");
        // Must-catch: an item the plan holds but the editor does not would be created by the batch.
        var missing = scene.InCheckpoint(data =>
        {
            var root = Root(data);
            root.Items.RemoveAt(root.Items.ToList().FindIndex(i => i.Is(SchematicLine.Descriptor)));
        });
        await RequireRefusal(missing, SchematicConnectionErrors.ConnectedInternalInconsistency, "neither a new symbol nor a generated connection item");
        Assert.IsTrue(missing.Geometry.Requests.Count != 0, "The refusal comes after measuring, before any assertion is built.");
    }

    [TestMethod]
    public async Task TheEntryPointReturnsTheRoundTrippedDesignAndStopsWithoutTheCapability()
    {
        var scene = RootAddition();
        var session = Realizing(scene.Saved);
        var prepared = await SchematicConnectedAddition.RealizeAsync(scene.Measure, session, scene.Saved, scene.Plan, scene.Checkpoint);
        var realization = await scene.Realize();
        CollectionAssert.AreEqual(realization.Operations.Select(o => o.ToByteString()).ToArray(), prepared.Operations.Select(o => o.ToByteString()).ToArray());
        Assert.AreEqual(SchematicDesignXml.Write(realization.Design, []), Encoding.UTF8.GetString(prepared.PlannedDesignFileBytes));
        // A checkpoint without the project's grid cannot be drawn on.
        var gridless = scene.InCheckpoint(data => { foreach (var screen in data.Instances) screen.Metadata.Formatting = null; });
        Assert.AreEqual(SchematicConnectionErrors.RealizationGridUnavailable, (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            SchematicConnectedAddition.RealizeAsync(gridless.Measure, session, scene.Saved, scene.Plan, gridless.Checkpoint))).Code);
        var other = session.Clone(); other.Capabilities.Remove(SchematicConnectedAddition.NativeCapability);
        int before = scene.Geometry.Requests.Count;
        Assert.AreEqual(SchematicConnectionErrors.NativeCapabilityMissing, (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            SchematicConnectedAddition.RealizeAsync(scene.Measure, other, scene.Saved, scene.Plan, scene.Checkpoint))).Code);
        Assert.AreEqual(before, scene.Geometry.Requests.Count, "Nothing is measured without the capability.");
    }

    // ---- §9.4 resolution ----

    [TestMethod]
    public async Task ResolutionAdoptsOnlyTheListedNativeValues()
    {
        var scene = HierarchyCrossing();
        var realization = await scene.Realize();
        var batch = Batch(scene, realization);
        var committed = Committed(scene, realization);
        var before = scene.Checkpoint.Electrical.Hierarchy.Data;
        var result = new SchematicItemBatchResult { ConnectivityAssertionVerified = true };
        Assert.AreEqual(realization.Design.Schematic, SchematicConnectionResolution.Resolve(realization.Design, before, committed, batch, result, []).Schematic);
        // Guard: KiCad filling in presentation details the plan leaves open is adopted, and the resolved design is KiCad's.
        var adopted = committed.Clone();
        EditItems(adopted.Hierarchy.Data, item => item switch
        {
            SchematicLine line when IsGenerated(realization, line.Id) => Edit(line, l => l.Stroke = new() { Width = new() { ValueNm = 152_400 } }),
            LocalLabel label when IsGenerated(realization, label.Id) => Edit(label, l =>
            {
                l.Text.Attributes.StrokeWidth = new() { ValueNm = 0 }; l.Text.Attributes.LineSpacing = 1; l.FieldsAutoplaced = true;
                l.Text.Position = l.Position.Clone();
            }),
            HierarchicalLabel label when IsGenerated(realization, label.Id) => Edit(label, l => l.Text.Attributes.FontName = "KiCad Font"),
            SheetSymbol sheet when sheet.Pins.Any(p => IsGenerated(realization, p.Id)) => Edit(sheet, s =>
            {
                foreach (var pin in s.Pins.Where(p => IsGenerated(realization, p.Id))) pin.Text.Attributes.LineSpacing = 1;
            }),
            _ => item
        });
        var resolved = SchematicConnectionResolution.Resolve(realization.Design, before, adopted, batch, result, []);
        Assert.AreEqual(adopted.Hierarchy.Data, resolved.Schematic);
        Assert.AreEqual(realization.Design.Engineering, resolved.Engineering);
        // Must-catch: every other difference is refused with the pending state kept.
        foreach (var (problem, edit) in new (string, Func<IMessage, IMessage>)[]
        {
            ("a moved generated label", item => item is LocalLabel l && IsGenerated(realization, l.Id) ? Edit(l, x => x.Position.XNm += Grid) : item),
            ("a renamed generated label", item => item is LocalLabel l && IsGenerated(realization, l.Id) ? Edit(l, x => x.Text.Text_ = "OTHER") : item),
            ("a turned label", item => item is HierarchicalLabel l && IsGenerated(realization, l.Id) ? Edit(l, x => x.SpinStyle = SchematicLabelSpinStyle.SlssUp) : item),
            ("a reshaped label", item => item is HierarchicalLabel l && IsGenerated(realization, l.Id) ? Edit(l, x => x.Shape = SchematicLabelShape.SlshInput) : item),
            ("a locked label", item => item is LocalLabel l && IsGenerated(realization, l.Id) ? Edit(l, x => x.Locked = LockedState.LsLocked) : item),
            ("a label property", item => item is LocalLabel l && IsGenerated(realization, l.Id) ? Edit(l, x => x.CustomProperties.Add(new CustomProperty { Key = "k", Value = "v" })) : item),
            ("a stretched wire", item => item is SchematicLine w && IsGenerated(realization, w.Id) ? Edit(w, x => x.End.XNm -= Grid) : item),
            ("a bus instead of a wire", item => item is SchematicLine w && IsGenerated(realization, w.Id) ? Edit(w, x => x.Type = SchematicLineType.SltBus) : item),
            ("a moved sheet pin", item => item is SheetSymbol s && s.Pins.Any(p => IsGenerated(realization, p.Id))
                ? Edit(s, x => x.Pins.Single(p => IsGenerated(realization, p.Id)).Position.YNm += Grid) : item),
            ("a sheet pin on the other side", item => item is SheetSymbol s && s.Pins.Any(p => IsGenerated(realization, p.Id))
                ? Edit(s, x => x.Pins.Single(p => IsGenerated(realization, p.Id)).Side = SheetSide.ShsRight) : item),
            ("a resized sheet symbol", item => item is SheetSymbol s && s.Pins.Any(p => IsGenerated(realization, p.Id)) ? Edit(s, x => x.Size.XNm += Grid) : item),
            ("a moved existing symbol", item => item is SchematicSymbolInstance s && !IsCreated(scene, s.Id) ? Edit(s, x => x.Position.XNm += Grid) : item),
            ("a changed created symbol", item => item is SchematicSymbolInstance s && IsCreated(scene, s.Id) ? Edit(s, x => x.Position.YNm += Grid) : item)
        })
        {
            var native = committed.Clone();
            EditItems(native.Hierarchy.Data, edit);
            Assert.AreNotEqual(committed, native, problem);
            var error = Assert.ThrowsExactly<AutomationException>(() => SchematicConnectionResolution.Resolve(realization.Design, before, native, batch, result, []), problem);
            Assert.AreEqual(SchematicConnectionErrors.RealizationResolutionMismatch, error.Code, problem + ": " + error.Message);
            StringAssert.Contains(error.Message, "pending operation is kept", problem);
        }
        var missing = committed.Clone();
        var root = Root(missing.Hierarchy.Data);
        root.Items.RemoveAt(root.Items.ToList().FindIndex(i => i.Is(LocalLabel.Descriptor)));
        Assert.AreEqual(SchematicConnectionErrors.RealizationResolutionMismatch, Assert.ThrowsExactly<AutomationException>(() =>
            SchematicConnectionResolution.Resolve(realization.Design, before, missing, batch, result, [])).Code);
        var extra = committed.Clone();
        Root(extra.Hierarchy.Data).Items.Add(Any.Pack(new Junction { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = new() { XNm = 1_270_000, YNm = 1_270_000 }, Locked = LockedState.LsUnlocked }));
        Assert.AreEqual(SchematicConnectionErrors.RealizationResolutionMismatch, Assert.ThrowsExactly<AutomationException>(() =>
            SchematicConnectionResolution.Resolve(realization.Design, before, extra, batch, result, [])).Code);
        // Committed connections that do not match the plan are refused even when every item matches.
        var split = committed.Clone(); split.Nets.Clear();
        Assert.AreEqual(SchematicConnectionErrors.RealizationResolutionMismatch, Assert.ThrowsExactly<AutomationException>(() =>
            SchematicConnectionResolution.Resolve(realization.Design, before, split, batch, result, [])).Code);
        // A commit without the editor's confirmation is not resolved.
        Assert.AreEqual(SchematicConnectionErrors.RealizationAssertionUnverified, Assert.ThrowsExactly<AutomationException>(() =>
            SchematicConnectionResolution.Resolve(realization.Design, before, committed, batch, new(), [])).Code);
    }

    [TestMethod]
    public void ReceiptsAreCheckedBeforeTheExecutorTouchesTheRecoveryRecord()
    {
        // A realization journaled over the recovery-store fixture's real checked native state.
        using var publication = new DesignPublicationRecoveryTests.Fixture();
        var state = publication.Saved.State;
        var guard = state.PendingNativeState!;
        var batch = new ApplySchematicItemBatch { Document = guard.Document.Clone(), DocumentEpoch = guard.Revision.Epoch,
            ExpectedRevision = guard.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), OriginId = state.OriginId.ToString("D"),
            Description = SchematicConnectionRealizer.BatchDescription };
        batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(new Junction { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = new() { XNm = 2_540_000, YNm = 2_540_000 }, Locked = LockedState.LsUnlocked }) });
        batch.Operations.Add(new SchematicItemOperation { AssertConnectivity = new SchematicConnectivityAssertion { Version = 1 } });
        var store = new DesignRecoveryStore(publication.RecordPath + ".realization.json");
        var pending = store.Save(state with { PendingMutation = batch, PendingPublication = null, PendingNativeSave = null,
            PendingLayout = DesignLayoutIntent.Create(publication.Intent.DesignPath, publication.Intent.ExpectedFileBytes, publication.Intent.CandidateFileBytes,
                Guid.NewGuid(), publication.Saved.RevisionToken, DesignLayoutIntent.ConnectionRealizationLane) }, null);
        Assert.IsTrue(SchematicConnectedAddition.IsRealization(pending.State));
        Assert.IsFalse(SchematicConnectedAddition.IsRealization(pending.State with { PendingLayout = pending.State.PendingLayout! with { Lane = DesignLayoutIntent.RebuildLane } }),
            "Never claim a layout another lane recorded.");
        var withoutAssertion = batch.Clone(); withoutAssertion.Operations.RemoveAt(withoutAssertion.Operations.Count - 1);
        Assert.IsFalse(SchematicConnectedAddition.IsRealization(pending.State with { PendingMutation = withoutAssertion }));
        Assert.IsFalse(SchematicConnectedAddition.IsRealization(state), "A publication is no realization.");
        CheckedSchematicBatchReceipt Receipt(CheckedSchematicBatchStatus status, string code) => new()
        {
            Document = batch.Document.Clone(), ProcessEpoch = guard.ProcessEpoch, OperationId = batch.OperationId, Status = status,
            ExpectedRequestVerified = true, ObservedBefore = guard.Clone(), ObservedAfter = guard.Clone(),
            ErrorCode = code, ErrorMessage = code.Length == 0 ? "" : code + ": expected=3 mismatches=1 first=unexpected_join:" + new string('x', 3000)
        };
        // Completed without the editor's confirmation: refused, the pending operation is kept.
        var unverified = Receipt(CheckedSchematicBatchStatus.CsbsCompleted, "");
        unverified.Result = new();
        Assert.AreEqual(SchematicConnectionErrors.RealizationAssertionUnverified, Assert.ThrowsExactly<AutomationException>(() =>
            SchematicConnectedAddition.CheckReceipt(store, pending, unverified)).Code);
        Assert.AreEqual(pending.RevisionToken, store.Read()!.RevisionToken);
        var verified = unverified.Clone(); verified.Result.ConnectivityAssertionVerified = true;
        SchematicConnectedAddition.CheckReceipt(store, pending, verified);
        // Other outcomes are left to the executor.
        SchematicConnectedAddition.CheckReceipt(store, pending, Receipt(CheckedSchematicBatchStatus.CsbsIndeterminate, SchematicConnectionErrors.ConnectivityAssertionUnverified));
        Assert.AreEqual(pending.RevisionToken, store.Read()!.RevisionToken);
        // A rejection that does not prove KiCad was left unchanged is not abandoned.
        var unproven = Receipt(CheckedSchematicBatchStatus.CsbsRejected, SchematicConnectionErrors.ConnectivityPostconditionFailed);
        unproven.Result = new();
        Assert.AreEqual(SchematicConnectionErrors.InvalidRealizationRejection, Assert.ThrowsExactly<AutomationException>(() =>
            SchematicConnectedAddition.CheckReceipt(store, pending, unproven)).Code);
        var unverifiedRequest = Receipt(CheckedSchematicBatchStatus.CsbsRejected, SchematicConnectionErrors.ConnectivityPostconditionFailed);
        unverifiedRequest.ExpectedRequestVerified = false;
        Assert.AreEqual(SchematicConnectionErrors.InvalidRealizationRejection, Assert.ThrowsExactly<AutomationException>(() =>
            SchematicConnectedAddition.CheckReceipt(store, pending, unverifiedRequest)).Code);
        var otherOperation = Receipt(CheckedSchematicBatchStatus.CsbsRejected, SchematicConnectionErrors.ConnectivityPostconditionFailed);
        otherOperation.OperationId = Guid.NewGuid().ToString("D");
        Assert.AreEqual(SchematicConnectionErrors.InvalidRealizationRejection, Assert.ThrowsExactly<AutomationException>(() =>
            SchematicConnectedAddition.CheckReceipt(store, pending, otherOperation)).Code);
        Assert.AreEqual(pending.RevisionToken, store.Read()!.RevisionToken);
        // Must-catch: KiCad's state after (or before) the rejected request is not the journaled state, so the receipt
        // does not prove KiCad was left unchanged; refused with the realization code before the store is asked.
        foreach (var (problem, edit) in new (string, Action<CheckedSchematicBatchReceipt>)[]
        {
            ("changed after", r => r.ObservedAfter.Revision.Sequence++),
            ("changed before", r => r.ObservedBefore.Revision.Sequence++),
            ("no state after", r => r.ObservedAfter = null)
        })
        {
            var changed = Receipt(CheckedSchematicBatchStatus.CsbsRejected, SchematicConnectionErrors.ConnectivityPostconditionFailed);
            edit(changed);
            var refusal = Assert.ThrowsExactly<AutomationException>(() => SchematicConnectedAddition.CheckReceipt(store, pending, changed), problem);
            Assert.AreEqual(SchematicConnectionErrors.InvalidRealizationRejection, refusal.Code, problem + ": " + refusal.Message);
            Assert.AreEqual(pending.RevisionToken, store.Read()!.RevisionToken, problem);
        }
        // The native assertion's verified rejection clears only the pending request and reports KiCad's bounded detail.
        var rejected = Receipt(CheckedSchematicBatchStatus.CsbsRejected, SchematicConnectionErrors.ConnectivityPostconditionFailed);
        var mismatch = Assert.ThrowsExactly<AutomationException>(() => SchematicConnectedAddition.CheckReceipt(store, pending, rejected));
        Assert.AreEqual(SchematicConnectionErrors.RealizationConnectivityMismatch, mismatch.Code);
        StringAssert.Contains(mismatch.Message, "first=unexpected_join");
        Assert.IsLessThan(2400, mismatch.Message.Length);
        var abandoned = store.Read()!.State;
        Assert.IsNull(abandoned.PendingLayout); Assert.IsNull(abandoned.PendingMutation); Assert.IsNull(abandoned.PendingNativeState);
        CollectionAssert.AreEqual(state.DesiredFileBytes, abandoned.DesiredFileBytes);
        Assert.AreEqual(SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries), SchematicDesignXml.Write(abandoned.Baseline, abandoned.KnowledgeLibraries));
    }

    // ---- scenes ----

    // With <paramref name="loose"/>, the revision also adds R3, a new resistor with no connection.
    private static Scene RootAddition(bool loose = false)
    {
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid tp = bench.Part("TP", Passive("1"));
        Guid r1 = bench.Component(r, "R1"), tp1 = bench.Component(tp, "TP1");
        bench.Component(r, "R9");
        string wire = bench.Wire(BenchSheet.Root), label = bench.LocalLabel(BenchSheet.Root, "SIG");
        var sig = new CircuitNet(Guid.NewGuid(), "SIG", [new(r1, "1")]);
        var output = new CircuitNet(Guid.NewGuid(), "/OUT", []);
        var state = WithFormatting(bench.State([sig], new() { [sig.Id] = [(BenchSheet.Root, wire), (BenchSheet.Root, label)] }));
        var created = bench.Create(state.Baseline, r, "R2", BenchSheet.Root);
        var design = loose ? bench.Create(created.Design, r, "R3", BenchSheet.Root).Design : created.Design;
        return Scene.Of(bench, state, revision => WithNets(Adopt(revision, design), sig with { Pins = [.. sig.Pins, new(created.Component, "1")] },
            output with { Pins = [new(created.Component, "2"), new(tp1, "1")] }));
    }

    private static Scene HierarchyCrossing()
    {
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2")), t = bench.Part("T3", Passive("1"), Passive("2"), Passive("3"));
        Guid r5 = bench.Component(r, "R5");
        bench.Component(t, "U9", BenchSheet.Child);
        var state = WithFormatting(bench.State([]));
        var created = bench.Create(state.Baseline, t, "U1", BenchSheet.Child);
        var data = new CircuitNet(Guid.NewGuid(), "DATA", [new(created.Component, "3"), new(r5, "2")]);
        return Scene.Of(bench, state, design => WithNets(Adopt(design, created.Design), data));
    }

    private static Scene Link()
    {
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2")), tp = bench.Part("TP", Passive("1"));
        Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2"), tp1 = bench.Component(tp, "TP1");
        string wire = bench.Wire(BenchSheet.Root);
        var link = new CircuitNet(Guid.NewGuid(), "LINK", [new(r1, "1"), new(r2, "1")]);
        var state = WithFormatting(bench.State([link], new() { [link.Id] = [(BenchSheet.Root, wire)] }));
        return Scene.Of(bench, state, design => WithNets(design, link with { Pins = [.. link.Pins, new(tp1, "1")] }));
    }

    // Two existing test points joined by a new net X; tests lay out their pins and bodies.
    private static Scene Pair()
    {
        var bench = new Bench();
        Guid tp = bench.Part("TP", Passive("1"));
        Guid tp1 = bench.Component(tp, "TP1"), tp2 = bench.Component(tp, "TP2");
        var x = new CircuitNet(Guid.NewGuid(), "X", [new(tp1, "1"), new(tp2, "1")]);
        return Scene.Of(bench, WithFormatting(bench.State([])), design => WithNets(design, x));
    }

    // A created two-pin part S1 whose pins can be stacked; joined, both pins are in net X with TP1.1.
    private static Scene Stacked(bool joined)
    {
        var bench = new Bench();
        Guid s = bench.Part("S", Passive("1"), Passive("2")), tp = bench.Part("TP", Passive("1"));
        Guid tp1 = bench.Component(tp, "TP1");
        bench.Component(s, "S9");
        var state = WithFormatting(bench.State([]));
        var created = bench.Create(state.Baseline, s, "S1", BenchSheet.Root);
        PinEndpoint[] pins = joined ? [new(created.Component, "1"), new(created.Component, "2"), new(tp1, "1")] : [new(created.Component, "1"), new(tp1, "1")];
        var x = new CircuitNet(Guid.NewGuid(), "X", pins);
        return Scene.Of(bench, state, design => WithNets(Adopt(design, created.Design), x));
    }

    // How the realizer's reasons and refusals name a pin and a component for a person: by the reference the planned design gives
    // the component (U4.4), never by its identity.
    internal static string Reference(SchematicDesign design, Guid component) => design.Engineering.Circuit.Components.Single(c => c.Id == component).Reference;

    internal static string PinName(SchematicDesign design, PinEndpoint pin) => Reference(design, pin.ComponentId) + "." + pin.Pin;

    // How they name a placed symbol: by the references of every component it draws, in ordinal order (one per instance of a
    // repeated sheet, TP802/TP803).
    internal static string SymbolName(SchematicDesign design, Guid nativeSymbol)
    {
        var circuit = design.Engineering.Circuit;
        var occurrences = design.SymbolBindings.Where(b => b.NativeObjectId == nativeSymbol).Select(b => b.SymbolOccurrenceId).ToHashSet();
        var components = circuit.Symbols.Where(s => occurrences.Contains(s.Id)).Select(s => s.ComponentId).ToHashSet();
        return string.Join("/", circuit.Components.Where(c => components.Contains(c.Id)).Select(c => c.Reference).Distinct().Order(StringComparer.Ordinal));
    }

    // Keep a component created once against the unedited baseline when the scene's sheets are later decorated.
    private static SchematicDesign Adopt(SchematicDesign design, SchematicDesign created) =>
        design with { Engineering = design.Engineering with { Circuit = created.Engineering.Circuit } };

    internal sealed record Scene(Bench? Bench, DesignRecoveryState Base, Func<SchematicDesign, SchematicDesign> Revision,
        DesignRecoveryState Saved, SchematicSynchronizationPlan Plan, CheckedSchematicState Checkpoint, Geometry Geometry)
    {
        public SchematicConnectionIntent Intent => Plan.Connections!;

        // The plan fixture's repeated channel sheets, with sheet symbols given a place and size on the root.
        public static Scene Repeated()
        {
            var state = SchematicConnectionRealizerTests.Edited(WithFormatting(SchematicSynchronizationPlanTests.Fixture()), data =>
            {
                int index = 0;
                foreach (var screen in data.Instances)
                    for (int i = 0; i < screen.Items.Count; i++)
                    {
                        if (!screen.Items[i].Is(SheetSymbol.Descriptor)) continue;
                        var sheet = screen.Items[i].Unpack<SheetSymbol>();
                        sheet.Position = new() { XNm = 150_000_000, YNm = 50_000_000 + 60_000_000L * index++ };
                        sheet.Size = new() { XNm = 40_000_000, YNm = 40_000_000 };
                        screen.Items[i] = Any.Pack(sheet);
                    }
            });
            var circuit = state.Baseline.Engineering.Circuit;
            var sig = new CircuitNet(Guid.NewGuid(), "SIG", [new(circuit.Components[0].Id, "1"), new(circuit.Components[1].Id, "1")]);
            return Of(null, state, design => WithNets(design, [.. design.Engineering.Circuit.Nets, sig]));
        }

        public static Scene Of(Bench? bench, DesignRecoveryState state, Func<SchematicDesign, SchematicDesign> revision, Geometry? geometry = null)
        {
            var (saved, _) = Revise(state, revision);
            var plan = SchematicConnectionIntentBuilderTests.Plan(saved);
            _ = RequireRealizationPlan(plan);
            return new(bench, state, revision, saved, plan, SchematicConnectionRealizerTests.Checkpoint(saved), geometry ?? new Geometry());
        }

        /// <summary>The same revision over a baseline whose every copy of the native sheets is edited identically.</summary>
        public Scene Edited(Action<SchematicHierarchyData> change) =>
            Of(Bench, SchematicConnectionRealizerTests.Edited(Base, change), Revision, Geometry.Copy());

        /// <summary>This plan against a checkpoint that differs from the saved observation, as a changed editor would.</summary>
        public Scene InCheckpoint(Action<SchematicHierarchyData> change)
        {
            var checkpoint = Checkpoint.Clone();
            change(checkpoint.Electrical.Hierarchy.Data);
            return this with { Checkpoint = checkpoint, Geometry = Geometry.Copy() };
        }

        /// <summary>This scene with a keep-out of exactly <paramref name="rect"/> on <paramref name="sheet"/> of its bench.</summary>
        public Scene Obstacle(BenchSheet sheet, Rect rect)
        {
            var id = Guid.NewGuid();
            string screen = Bench!.ScreenId(sheet).ToString("D");
            var copy = Edited(data => data.Instances.Single(s => s.Metadata.ScreenId.Value == screen).Items.Add(Any.Pack(new SchematicText
            {
                Id = new() { Value = id.ToString("D") }, Locked = LockedState.LsUnlocked,
                Text = new() { Text_ = "keep out", Position = new() { XNm = rect.L, YNm = rect.T }, Attributes = new() { Multiline = true } }
            })));
            copy.Geometry.Sized[id] = rect;
            return copy;
        }

        public Scene Obstacle(Rect rect)
        {
            var id = Guid.NewGuid();
            var copy = Edited(data => Root(data).Items.Add(Any.Pack(new SchematicText { Id = new() { Value = id.ToString("D") },
                Text = new() { Text_ = "keep out", Position = new() { XNm = rect.L, YNm = rect.T }, Attributes = new() { Multiline = true } },
                Locked = LockedState.LsUnlocked })));
            copy.Geometry.Sized[id] = rect;
            return copy;
        }

        // Every copy of the native schematic must receive the same item, so identities are chosen once.
        public Scene Junction(Point at)
        {
            var item = Any.Pack(new Junction { Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = new() { XNm = at.X, YNm = at.Y },
                Locked = LockedState.LsUnlocked });
            return Edited(data => Root(data).Items.Add(item.Clone()));
        }

        public Scene Wire(Point a, Point b)
        {
            var item = Any.Pack(new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") }, Start = new() { XNm = a.X, YNm = a.Y },
                End = new() { XNm = b.X, YNm = b.Y }, Type = SchematicLineType.SltWire, Locked = LockedState.LsUnlocked });
            return Edited(data => Root(data).Items.Add(item.Clone()));
        }

        public Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> Measure =>
            (request, token) => Geometry.Measure(this, request, token);

        public Point PinAt(ConnectionPlacedPin pin) => Geometry.PinAt(this, pin);

        public Task<SchematicConnectionRealization> Realize() =>
            SchematicConnectionRealizer.RealizeAsync(Intent, Plan.Candidate!, Checkpoint, Measure, Policy);

        /// <summary>Realize with <paramref name="limits"/> (the contract's by default), keeping every measurement and its answer.</summary>
        public async Task<(SchematicConnectionRealization Realization, IReadOnlyDictionary<string, SchematicPlacementGeometry> Measured)> RealizeMeasured(
            SchematicRoutingLimits? limits = null)
        {
            var measured = new List<(MeasureSchematicPlacement, SchematicPlacementGeometry)>();
            var realization = await SchematicConnectionRealizer.RealizeAsync(Intent, Plan.Candidate!, Checkpoint, async (request, token) =>
            {
                var reply = await Measure(request, token);
                measured.Add((request.Clone(), reply.Clone()));
                return reply;
            }, Policy, limits ?? SchematicRoutingLimits.Contract);
            return (realization, SchematicConnectionRealizerTests.Measured(measured));
        }
    }

    /// <summary>A stable recovery record whose every sheet reports the standard formatting (1.27 mm grid and text).</summary>
    internal static DesignRecoveryState WithFormatting(DesignRecoveryState state) => Edited(state, data =>
    {
        foreach (var screen in data.Instances) screen.Metadata.Formatting ??= SchematicFormattingTests.Formatting();
    });

    /// <summary><paramref name="state"/> with every copy of its native schematic changed the same way.</summary>
    internal static DesignRecoveryState Edited(DesignRecoveryState state, Action<SchematicHierarchyData> change)
    {
        SchematicHierarchyData Apply(SchematicHierarchyData data) { var copy = data.Clone(); change(copy); return copy; }
        SchematicElectricalState? Electrical(SchematicElectricalState? electrical)
        {
            if (electrical is null) return null;
            var copy = electrical.Clone(); copy.Hierarchy.Data = Apply(copy.Hierarchy.Data); return copy;
        }
        var baseline = state.Baseline with { Schematic = Apply(state.Baseline.Schematic) };
        return state with { Baseline = baseline, Observed = Apply(state.Observed), BaselineElectrical = Electrical(state.BaselineElectrical),
            ObservedElectrical = Electrical(state.ObservedElectrical),
            DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, state.KnowledgeLibraries)) };
    }

    /// <summary>The checked native state the executor would capture for <paramref name="state"/>: its observed
    /// schematic at its recorded revision, with the standard formatting on sheets that report none.</summary>
    internal static CheckedSchematicState Checkpoint(DesignRecoveryState state)
    {
        var electrical = state.ObservedElectrical!.Clone();
        foreach (var screen in electrical.Hierarchy.Data.Instances) screen.Metadata.Formatting ??= SchematicFormattingTests.Formatting();
        return new()
        {
            State = new() { Document = state.Baseline.Schematic.Document.Clone(), ProcessEpoch = "bench-process",
                Revision = new DocumentRevision { Epoch = state.NativeRevision.Epoch, Sequence = state.NativeRevision.Sequence } },
            Electrical = electrical
        };
    }

    // ---- synthetic native measurement ----

    internal readonly record struct Point(long X, long Y);
    internal readonly record struct Rect(long L, long T, long R, long B);

    /// <summary>A deterministic stand-in for the editor's placement measurement of a checkpoint. Symbols are 8 grids
    /// wide with every active pin on the left edge, two grids apart and pointing left; a symbol's bounds include its
    /// pins, as KiCad's do. Labels extend from their anchor in the direction they face, (length + 1) × 0.75 grid long,
    /// plus one grid for a global or hierarchical shape, one grid across, and as far behind the anchor as KiCad draws
    /// them (<see cref="MeasuredBehind"/>). Tests move pins, redraw symbol bodies, override power facts or reasons,
    /// size obstacles, or tamper with replies.</summary>
    internal sealed class Geometry
    {
        /// <summary>How far KiCad draws each label kind behind its anchor with 1.27 mm text, as the NativeXmlComponentCreation
        /// journey measures it in a live editor. The replay test holds the local and hierarchical values to the committed
        /// recordings (automation/tests/fixtures/connection-realization).</summary>
        public static readonly IReadOnlyDictionary<System.Type, long> MeasuredBehind = new Dictionary<System.Type, long>
        {
            [typeof(LocalLabel)] = 158_800, [typeof(GlobalLabel)] = 79_400, [typeof(HierarchicalLabel)] = 304_800
        };

        public List<MeasureSchematicPlacement> Requests { get; private set; } = [];
        public Dictionary<Guid, Rect> Sized { get; private set; } = [];
        public Dictionary<Guid, SchematicPinGeometryIncompleteReason> Incomplete { get; private set; } = [];
        public Dictionary<Guid, (SchematicPinPowerScope Scope, string Name)> Power { get; private set; } = [];
        public Dictionary<Guid, (int Dx, int Dy)> Direction { get; private set; } = [];
        public Dictionary<Guid, Guid> Library { get; private set; } = [];
        public Dictionary<Guid, Point> Place { get; private set; } = [];
        public Dictionary<(string Path, Guid Pin), Point> Shift { get; private set; } = [];
        /// <summary>A symbol's drawn body in place of the default 8-grid box; its pins still extend its bounds.</summary>
        public Dictionary<Guid, Rect> Body { get; private set; } = [];
        /// <summary>Overrides <see cref="MeasuredBehind"/> for every label kind.</summary>
        public long? LabelBehind { get; set; }
        /// <summary>Half the size of every label across its facing direction, in place of half a grid (as a larger label text
        /// would draw it).</summary>
        public long? LabelHalf { get; set; }
        public Func<MeasureSchematicPlacement, SchematicPlacementGeometry, SchematicPlacementGeometry>? Tamper { get; set; }
        /// <summary>Visible fields of a symbol, which its bounds take in as KiCad's do.</summary>
        public Dictionary<Guid, List<Rect>> Fields { get; private set; } = [];
        /// <summary>The drawing sheet every measurement reports (KiCad's default A4 drawing sheet), or null for an editor that
        /// predates the drawing-sheet measurement.</summary>
        public SchematicWiringDrawingSheet? DrawingSheet { get; set; } = DefaultDrawingSheet();
        /// <summary>Whether the measurements report every symbol's visible field bounds; false for an older editor.</summary>
        public bool FieldsReported { get; set; } = true;

        public Geometry Copy() => new()
        {
            Requests = [], Sized = new(Sized), Incomplete = new(Incomplete), Power = new(Power), Direction = new(Direction),
            Library = new(Library), Place = new(Place), Shift = new(Shift), Body = new(Body), LabelBehind = LabelBehind, LabelHalf = LabelHalf, Tamper = Tamper,
            Fields = Fields.ToDictionary(f => f.Key, f => f.Value.ToList()), DrawingSheet = DrawingSheet?.Clone(), FieldsReported = FieldsReported
        };

        /// <summary>KiCad's default drawing sheet on the 297 mm by 210 mm page, as its drawing-sheet measurement reports it: the
        /// 10 mm margin frame, the outer border on it and the inner border 2 mm further in with a zone mark and number between
        /// them, and the title block (its box 110 mm by 32 mm in the bottom right corner of the inner border, one of its rows
        /// and one of its texts).</summary>
        public static SchematicWiringDrawingSheet DefaultDrawingSheet()
        {
            static Box2 Mm(double l, double t, double r, double b) => Box(new((long)(l * 1_000_000), (long)(t * 1_000_000), (long)(r * 1_000_000), (long)(b * 1_000_000)));
            var sheet = new SchematicWiringDrawingSheet { MarginFrame = Mm(10, 10, 287, 200) };
            foreach (var (kind, bounds) in new (SchematicWiringDrawingSheetItemKind, Box2)[]
            {
                (SchematicWiringDrawingSheetItemKind.SwrDrawingSheetItemRectangle, Mm(177, 166, 285, 198)),
                (SchematicWiringDrawingSheetItemKind.SwrDrawingSheetItemRectangle, Mm(10, 10, 287, 200)),
                (SchematicWiringDrawingSheetItemKind.SwrDrawingSheetItemRectangle, Mm(12, 12, 285, 198)),
                (SchematicWiringDrawingSheetItemKind.SwrDrawingSheetItemLine, Mm(60, 10, 60, 12)),
                (SchematicWiringDrawingSheetItemKind.SwrDrawingSheetItemText, Mm(34.35, 10.35, 35.65, 11.65)),
                (SchematicWiringDrawingSheetItemKind.SwrDrawingSheetItemLine, Mm(177, 190, 285, 190)),
                (SchematicWiringDrawingSheetItemKind.SwrDrawingSheetItemText, Mm(178, 191, 200, 194))
            })
                sheet.Items.Add(new SchematicWiringDrawingSheetItem { Kind = kind, Bounds = bounds });
            return sheet;
        }

        public Task<SchematicPlacementGeometry> Measure(Scene scene, MeasureSchematicPlacement request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request.Clone());
            string path = string.Join('/', request.Document.SheetPath.Path.Select(p => p.Value));
            var screen = scene.Checkpoint.Electrical.Hierarchy.Data.Instances.Single(s => Key(s) == path);
            var reply = new SchematicPlacementGeometry { Document = request.Document.Clone(), Revision = request.ExpectedRevision.Clone(),
                ScreenId = screen.Metadata.ScreenId.Clone(), PinGeometryAvailable = true, FieldBoundsReported = FieldsReported,
                PageBounds = new() { Position = new(), Size = new() { XNm = 297_000_000, YNm = 210_000_000 } }, DrawingSheet = DrawingSheet?.Clone() };
            reply.Limitations.Add("Synthetic test geometry");
            foreach (var (id, item) in SchematicItemDelta.Index(screen.Items).OrderBy(p => p.Key))
                if (item is not Group) reply.Obstacles.Add(Bounds(path, id, item));
            foreach (var candidate in request.Candidates) reply.Candidates.Add(Bounds(path, Guid.Parse(candidate.Id.Value), candidate));
            foreach (var packed in request.ItemCandidates)
            {
                var (id, label) = SchematicItemDelta.Index([packed]).Single();
                reply.ItemCandidates.Add(Bounds(path, id, label));
            }
            return Task.FromResult(Tamper is null ? reply : Tamper(request, reply));
        }

        public Point PinAt(Scene scene, ConnectionPlacedPin pin)
        {
            var screen = scene.Plan.Candidate!.Schematic.Instances.Single(s => Key(s) == pin.SheetPathKey);
            var symbol = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .Single(s => Guid.Parse(s.Id.Value) == pin.SymbolId);
            return Pins(pin.SheetPathKey, symbol).Single(p => p.Pin.Id.Value == pin.PlacedPinId.ToString("D")).At;
        }

        public Rect LabelBox(IMessage label) => Rect(Bounds("", Guid.Empty, label).Bounds);

        private IEnumerable<(SchematicPin Pin, Point At, int Dx, int Dy)> Pins(string path, SchematicSymbolInstance symbol)
        {
            var active = SchematicPlacedPins.Active(symbol, symbol.Unit?.Unit ?? 1).OrderBy(p => p.Number, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < active.Length; i++)
            {
                var id = Guid.Parse(active[i].Id.Value);
                var offset = Turn(symbol, -4 * Grid, (2 * i - (active.Length - 1)) * Grid);
                var at = Place.TryGetValue(id, out var placed) ? placed : new Point(symbol.Position.XNm + offset.X, symbol.Position.YNm + offset.Y);
                if (Shift.TryGetValue((path, id), out var shift)) at = new(at.X + shift.X, at.Y + shift.Y);
                var turned = Turn(symbol, 1, 0);
                var (dx, dy) = Direction.TryGetValue(id, out var direction) ? direction : ((int)turned.X, (int)turned.Y);
                yield return (active[i], at, dx, dy);
            }
        }

        private SchematicPlacementBounds Bounds(string path, Guid id, IMessage item)
        {
            var result = new SchematicPlacementBounds { Id = new() { Value = id.ToString("D") } };
            if (Sized.TryGetValue(id, out var sized))
            {
                result.Anchor = new() { XNm = sized.L, YNm = sized.T }; result.Bounds = Box(sized);
                return result;
            }
            switch (item)
            {
                case SchematicSymbolInstance symbol:
                {
                    var pins = Pins(path, symbol).ToArray();
                    long half = Math.Max(1, pins.Length) * Grid + Grid;
                    var (a, b) = (Turn(symbol, -4 * Grid, -half), Turn(symbol, 4 * Grid, half));
                    var box = Body.TryGetValue(id, out var body) ? body
                        : new Rect(symbol.Position.XNm + Math.Min(a.X, b.X), symbol.Position.YNm + Math.Min(a.Y, b.Y),
                            symbol.Position.XNm + Math.Max(a.X, b.X), symbol.Position.YNm + Math.Max(a.Y, b.Y));
                    // KiCad draws a symbol whose definition it cannot resolve without pins, and measures it by its own bounds.
                    bool unresolved = Incomplete.TryGetValue(id, out var missing) && missing == SchematicPinGeometryIncompleteReason.SpgirDefinitionUnresolved;
                    if (!unresolved)
                        foreach (var pin in pins) box = new(Math.Min(box.L, pin.At.X), Math.Min(box.T, pin.At.Y), Math.Max(box.R, pin.At.X), Math.Max(box.B, pin.At.Y));
                    foreach (var field in Fields.GetValueOrDefault(id, []))
                    {
                        box = new(Math.Min(box.L, field.L), Math.Min(box.T, field.T), Math.Max(box.R, field.R), Math.Max(box.B, field.B));
                        result.VisibleFieldBounds.Add(Box(field));
                    }
                    result.Anchor = symbol.Position.Clone(); result.Bounds = Box(box);
                    var geometry = new SchematicSymbolPinGeometry();
                    if (Incomplete.TryGetValue(id, out var reason)) { geometry.IncompleteReason = reason; geometry.Limitations.Add("Synthetic incomplete pins"); }
                    else
                    {
                        geometry.Complete = true;
                        foreach (var (pin, at, dx, dy) in pins)
                        {
                            var pinId = Guid.Parse(pin.Id.Value);
                            var (scope, name) = Power.TryGetValue(pinId, out var power) ? power : NativePower(symbol, pin);
                            geometry.Pins.Add(new SchematicPinAnchor { Id = pin.Id.Clone(),
                                LibraryPinId = Library.TryGetValue(pinId, out var library) ? new() { Value = library.ToString("D") } : pin.LibraryPinId.Clone(),
                                Number = pin.Number, Name = pin.Name, Position = new() { XNm = at.X, YNm = at.Y }, BodyDirectionX = dx, BodyDirectionY = dy,
                                Unit = 1, Visible = pin.Visible, ElectricalType = pin.ElectricalType, PowerScope = scope, PowerNet = name });
                        }
                    }
                    result.SymbolPins = geometry;
                    return result;
                }
                case SchematicLine line:
                    result.Anchor = line.Start.Clone(); result.Bounds = Box(Span(Of(line.Start), Of(line.End))); return result;
                case LocalLabel label: return Label(result, label.Position, label.Text.Text_, label.SpinStyle, 0, Behind(label), LabelHalf ?? Grid / 2);
                case GlobalLabel label: return Label(result, label.Position, label.Text.Text_, label.SpinStyle, Grid, Behind(label), LabelHalf ?? Grid / 2);
                case HierarchicalLabel label: return Label(result, label.Position, label.Text.Text_, label.SpinStyle, Grid, Behind(label), LabelHalf ?? Grid / 2);
                case SheetSymbol sheet:
                    result.Anchor = sheet.Position.Clone();
                    result.Bounds = Box(new(sheet.Position.XNm, sheet.Position.YNm, sheet.Position.XNm + sheet.Size.XNm, sheet.Position.YNm + sheet.Size.YNm));
                    return result;
                default:
                {
                    var position = item.Descriptor.FindFieldByName("position")?.Accessor.GetValue(item) as Vector2 ?? new Vector2();
                    result.Anchor = position.Clone(); result.Bounds = Box(new(position.XNm, position.YNm, position.XNm, position.YNm));
                    return result;
                }
            }
        }

        private long Behind(IMessage label) => LabelBehind ?? MeasuredBehind[label.GetType()];

        // A symbol turned by its orientation, as KiCad turns it on a sheet whose y grows down: 90 degrees takes (x, y) to (y, -x).
        // Mirrors are not modelled; the default unturned symbol is unchanged.
        private static Point Turn(SchematicSymbolInstance symbol, long x, long y) => symbol.Transform?.Orientation switch
        {
            SchematicSymbolOrientation.Sso90 => new(y, -x),
            SchematicSymbolOrientation.Sso180 => new(-x, -y),
            SchematicSymbolOrientation.Sso270 => new(-y, x),
            _ => new(x, y)
        };

        private static SchematicPlacementBounds Label(SchematicPlacementBounds result, Vector2 position, string text, SchematicLabelSpinStyle spin,
            long shape, long behind, long half)
        {
            long length = (text.Length + 1) * Grid * 3 / 4 + shape;
            var (x, y) = (position.XNm, position.YNm);
            var box = spin switch
            {
                SchematicLabelSpinStyle.SlssLeft => new Rect(x - length, y - half, x + behind, y + half),
                SchematicLabelSpinStyle.SlssUp => new Rect(x - half, y - length, x + half, y + behind),
                SchematicLabelSpinStyle.SlssBottom => new Rect(x - half, y - behind, x + half, y + length),
                _ => new Rect(x - behind, y - half, x + length, y + half)
            };
            result.Anchor = position.Clone(); result.Bounds = Box(box);
            return result;
        }

        // The editor's implicit connection of a pin, as SCH_PIN::IsGlobalPower/IsLocalPower report it.
        private static (SchematicPinPowerScope, string) NativePower(SchematicSymbolInstance symbol, SchematicPin pin)
        {
            if (pin.ElectricalType != ElectricalPinType.EptPowerInput) return (SchematicPinPowerScope.SppsNone, "");
            var type = symbol.Definition.Type;
            if (type == SchematicSymbolType.SstGlobalPower) return (SchematicPinPowerScope.SppsGlobal, symbol.ValueField.Text.Text_);
            if (type == SchematicSymbolType.SstLocalPower) return (SchematicPinPowerScope.SppsLocal, symbol.ValueField.Text.Text_);
            return pin.Visible ? (SchematicPinPowerScope.SppsNone, "") : (SchematicPinPowerScope.SppsGlobal, pin.Name);
        }

        private static Point Of(Vector2 v) => new(v.XNm, v.YNm);
        private static Rect Span(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        private static Box2 Box(Rect r) => new() { Position = new() { XNm = r.L, YNm = r.T }, Size = new() { XNm = r.R - r.L, YNm = r.B - r.T } };
        private static Rect Rect(Box2 b) => new(b.Position.XNm, b.Position.YNm, b.Position.XNm + b.Size.XNm, b.Position.YNm + b.Size.YNm);
    }

    // ---- recorded live measurements ----

    /// <summary>A realization recording: the planned revision's circuit (its nets listed again for reading) and exact
    /// desired-file digest, the checkpoint, every measurement request with the editor's reply in order, and the operations
    /// and generated items the realizer produced from them. The recovery record the revision was planned from is kept once
    /// beside it, under the name <paramref name="recovery"/> that the recording carries. A realization that failed has no
    /// operations or generated items and carries its <paramref name="failure"/> instead: the refusal's code and message for
    /// a refusal (an <see cref="AutomationException"/>), else the failure's type and message with no code. It is evidence,
    /// never loaded as a replay fixture (those are named *.measurement.json).</summary>
    internal static string FormatRecording(string scenario, DesignRecoveryState revision, CheckedSchematicState checkpoint,
        IReadOnlyList<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)> measurements, SchematicConnectionRealization? realization,
        string recovery = "editor.recovery.json", Exception? failure = null)
    {
        static System.Text.Json.Nodes.JsonNode Proto(IMessage message) => System.Text.Json.Nodes.JsonNode.Parse(SchematicJson.Formatter.Format(message))!;
        var desired = DesignRecoveryStore.ReadDesired(revision);
        var circuit = desired.Engineering.Circuit;
        var nets = circuit.Nets;
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["fixture"] = "connection-realization", ["version"] = 1, ["scenario"] = scenario, ["recovery"] = recovery,
            ["desiredSha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(revision.DesiredFileBytes)),
            ["nets"] = new System.Text.Json.Nodes.JsonArray([.. nets.Select(n => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = n.Id.ToString("D"), ["name"] = n.Name,
                ["pins"] = new System.Text.Json.Nodes.JsonArray([.. n.Pins.Select(p => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                    { ["component"] = p.ComponentId.ToString("D"), ["number"] = p.Pin })])
            })]),
            ["circuit"] = CircuitXml.Write(circuit),
            ["checkpoint"] = Proto(checkpoint),
            ["measurements"] = new System.Text.Json.Nodes.JsonArray([.. measurements.Select(m =>
                (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["request"] = Proto(m.Request), ["response"] = Proto(m.Reply) })]),
            ["operations"] = new System.Text.Json.Nodes.JsonArray([.. (realization?.Operations ?? Array.Empty<SchematicItemOperation>()).Select(o => Proto(o))]),
            ["generated"] = new System.Text.Json.Nodes.JsonArray([.. (realization?.Generated ?? Array.Empty<GeneratedConnectionItem>()).Select(g => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = g.Id.ToString("D"), ["role"] = g.Role.ToString(), ["screen"] = g.ScreenId.ToString("D"),
                ["placedPin"] = g.PlacedPinId?.ToString("D"), ["sheetSymbol"] = g.SheetSymbolId?.ToString("D"), ["typeUrl"] = g.TypeUrl
            })])
        };
        // A revision that also declares part symbols (a new part with its own definition) keeps its declared part symbols.
        if (!(desired.PartSymbols ?? []).SequenceEqual(revision.Baseline.PartSymbols ?? []))
            root["partSymbols"] = new System.Text.Json.Nodes.JsonArray([.. (desired.PartSymbols ?? []).Select(p => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["part"] = p.PartId.ToString("D"), ["library"] = Proto(p.LibraryId), ["symbol"] = Proto(p.Symbol), ["bodyStyle"] = p.BodyStyle
            })]);
        if (failure is AutomationException refusal)
            root["refusal"] = new System.Text.Json.Nodes.JsonObject { ["code"] = refusal.Code, ["message"] = refusal.Message };
        else if (failure is not null)
            root["failure"] = new System.Text.Json.Nodes.JsonObject { ["type"] = failure.GetType().FullName, ["message"] = failure.Message };
        return root.ToJsonString();
    }

    /// <summary>A measurement that expects exactly the recorded requests, in the recorded order, and answers each with
    /// its recorded reply. A different, extra or reordered request fails the test.</summary>
    internal static Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> Replay(
        IReadOnlyList<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)> measurements)
    {
        int next = 0;
        return (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (next >= measurements.Count || !measurements[next].Request.Equals(request))
                throw new AssertFailedException("Measurement " + next + " differs from the recorded request: " + SchematicJson.Formatter.Format(request));
            return Task.FromResult(measurements[next++].Reply.Clone());
        };
    }

    [TestMethod]
    public async Task RecordedLiveMeasurementsReplayToTheRecordedBatchAndStillCatchEveryFault()
    {
        // Each recording is a real editor's checkpoint and its answers to every measurement the realizer asked for,
        // captured by the NativeXmlComponentCreation journey. Replayed, the realizer must ask exactly the same
        // questions and produce exactly the recorded batch; faults injected into the real answers must still be caught.
        var recordings = Recordings();
        Assert.IsNotEmpty(recordings);
        // Which scenarios ran the conditional must-catches below, so that none of them can go silent.
        var covering = new List<string>();
        var ownNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var anchorBranches = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var recording in recordings)
        {
            int asked = 0;
            var replay = Replay(recording.Measurements);
            var realization = await SchematicConnectionRealizer.RealizeAsync(recording.Intent, recording.Plan.Candidate!, recording.Checkpoint,
                (request, token) => { asked++; return replay(request, token); }, SchematicConnectionPolicy.FromSnapshot(recording.Checkpoint.Electrical.Hierarchy.Data));
            Assert.AreEqual(recording.Measurements.Count, asked, recording.Scenario + ": every recorded measurement, and no other, is asked for.");
            // The synthetic geometry draws labels as far behind their anchor as this real editor did.
            foreach (var (request, reply) in recording.Measurements)
                for (int i = 0; i < request.ItemCandidates.Count; i++)
                {
                    var prototype = SchematicItemDelta.Index([request.ItemCandidates[i]]).Single().Value;
                    var spin = prototype switch { LocalLabel l => l.SpinStyle, GlobalLabel l => l.SpinStyle, HierarchicalLabel l => l.SpinStyle,
                        _ => throw new AssertFailedException("Only label prototypes are measured.") };
                    var measured = reply.ItemCandidates[i];
                    long l0 = measured.Bounds.Position.XNm - measured.Anchor.XNm, t0 = measured.Bounds.Position.YNm - measured.Anchor.YNm;
                    long r0 = l0 + measured.Bounds.Size.XNm, b0 = t0 + measured.Bounds.Size.YNm;
                    var (fx, fy) = SchematicConnectionRealizer.Facing(spin);
                    Assert.AreEqual(Geometry.MeasuredBehind[prototype.GetType()], fx > 0 ? -l0 : fx < 0 ? r0 : fy > 0 ? -t0 : b0,
                        recording.Scenario + ": " + prototype.GetType().Name + " " + spin);
                }
            CollectionAssert.AreEqual(recording.Operations.Select(o => SchematicJson.Formatter.Format(o)).ToArray(),
                realization.Operations.Select(o => SchematicJson.Formatter.Format(o)).ToArray(), recording.Scenario);
            Assert.AreEqual(recording.Generated.Count, realization.Generated.Count, recording.Scenario);
            // Must-catch: an answer for another revision, a missing existing item, and a label reaching behind its pin.
            await RequireRecordingRefusal(recording.With((request, reply) => { reply.Revision.Sequence++; return reply; }),
                SchematicConnectionErrors.RealizationMeasurementStale);
            await RequireRecordingRefusal(recording.With((request, reply) => { reply.Obstacles.RemoveAt(reply.Obstacles.Count - 1); return reply; }),
                SchematicConnectionErrors.RealizationMeasurementIncomplete);
            await RequireRecordingRefusal(recording.With((request, reply) =>
            {
                foreach (var prototype in reply.ItemCandidates)
                {
                    prototype.Bounds.Position.XNm -= 20 * Grid; prototype.Bounds.Size.XNm += 40 * Grid;
                    prototype.Bounds.Position.YNm -= 20 * Grid; prototype.Bounds.Size.YNm += 40 * Grid;
                }
                return reply;
            }), SchematicConnectionErrors.RealizationLabelOrientationMismatch);
            // Must-catch: a stubbed pin reported one grid away on one instance of a repeated sheet.
            var repeated = recording.Intent.Screens.FirstOrDefault(s => s.InstancePathKeys.Count > 1 && s.Islands.Any(i => i.Members.Any(m => m.RequiresStub)));
            if (repeated is not null)
            {
                var pin = repeated.Islands.SelectMany(i => i.Members).First(m => m.RequiresStub).Pin;
                string other = repeated.InstancePathKeys[1];
                await RequireRecordingRefusal(recording.With((request, reply) =>
                {
                    if (string.Join('/', request.Document.SheetPath.Path.Select(p => p.Value)) != other) return reply;
                    foreach (var symbol in reply.Obstacles.Where(o => o.SymbolPins is not null))
                        foreach (var anchor in symbol.SymbolPins.Pins.Where(p => p.Id.Value == pin.PlacedPinId.ToString("D"))) anchor.Position.XNm += Grid;
                    return reply;
                }), SchematicConnectionErrors.RealizationPinGeometryMismatch);
            }
            // Must-catch: the editor reports a stubbed pin as a power connection.
            var stubbed = recording.Intent.Screens.SelectMany(s => s.Islands).SelectMany(i => i.Members).First(m => m.RequiresStub).Pin;
            await RequireRecordingRefusal(recording.With((request, reply) =>
            {
                foreach (var symbol in reply.Obstacles.Concat(reply.Candidates).Where(o => o.SymbolPins is not null))
                    foreach (var anchor in symbol.SymbolPins.Pins.Where(p => p.Id.Value == stubbed.PlacedPinId.ToString("D")))
                    { anchor.PowerScope = SchematicPinPowerScope.SppsGlobal; anchor.PowerNet = "INJECTED"; }
                return reply;
            }), SchematicConnectionErrors.RealizationImplicitPowerMismatch);
            // On the real sheet: a foreign junction beside the recorded stub end, within the clearance, moves that stub
            // further out, and junctions beside every stub end leave no room at all.
            var policy = SchematicConnectionPolicy.FromSnapshot(recording.Checkpoint.Electrical.Hierarchy.Data);
            long grid = policy.GridNm;
            // A stub of a connection drawn with label stubs by design (fewer than two new pins on its sheet), not one that fell back
            // from routing, whose drawing a junction beside it could change; recordings made before routing have only those.
            var byDesign = realization.Outcomes.Where(o => o.Strategy == ConnectionRealizationStrategy.LabelStub && o.FallbackReason is null)
                .SelectMany(o => o.GeneratedIds).ToHashSet();
            var stubbedPin = (realization.Generated.FirstOrDefault(g => g.Role == GeneratedConnectionRole.StubWire && byDesign.Contains(g.Id))
                ?? realization.Generated.First(g => g.Role == GeneratedConnectionRole.StubWire)).PlacedPinId!.Value;
            var recordedWire = realization.Operations.Where(o => o.Create is not null && o.Create.Is(SchematicLine.Descriptor))
                .Select(o => (Target: o.TargetDocument, Line: o.Create.Unpack<SchematicLine>()))
                .Single(w => realization.Generated.Any(g => g.PlacedPinId == stubbedPin && g.Role == GeneratedConnectionRole.StubWire && g.Id.ToString("D") == w.Line.Id.Value));
            var screen = recording.Checkpoint.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(recordedWire.Target)).Metadata.ScreenId;
            var (dx, dy) = (Math.Sign(recordedWire.Line.End.XNm - recordedWire.Line.Start.XNm), Math.Sign(recordedWire.Line.End.YNm - recordedWire.Line.Start.YNm));
            Kiapi.Common.Types.Vector2 Along(long length, long aside = 0) => new()
                { XNm = recordedWire.Line.Start.XNm + dx * length - dy * aside, YNm = recordedWire.Line.Start.YNm + dy * length + dx * aside };
            // The recorded stub has one of the policy's lengths: the shortest one with room on the real sheet.
            long recordedLength = Math.Abs(recordedWire.Line.End.XNm - recordedWire.Line.Start.XNm) + Math.Abs(recordedWire.Line.End.YNm - recordedWire.Line.Start.YNm);
            int shortest = Array.IndexOf(SchematicConnectionPolicy.StubMultiples, (int)(recordedLength / grid));
            Assert.IsTrue(recordedLength % grid == 0 && shortest >= 0, recording.Scenario + ": the recorded stub has one of the policy's lengths.");
            Assert.AreEqual(Along(recordedLength), recordedWire.Line.End, recording.Scenario);
            if (shortest + 1 < SchematicConnectionPolicy.StubMultiples.Length)
            {
                var blocked = recording.WithJunction(Along(recordedLength, policy.ClearanceNm), screen);
                var moved = await blocked.Realize();
                var movedWire = moved.Operations.Where(o => o.Create is not null && o.Create.Is(SchematicLine.Descriptor)).Select(o => o.Create.Unpack<SchematicLine>())
                    .Single(l => moved.Generated.Any(g => g.PlacedPinId == stubbedPin && g.Role == GeneratedConnectionRole.StubWire && g.Id.ToString("D") == l.Id.Value));
                long movedLength = Math.Abs(movedWire.End.XNm - movedWire.Start.XNm) + Math.Abs(movedWire.End.YNm - movedWire.Start.YNm);
                Assert.IsTrue(movedLength > recordedLength && SchematicConnectionPolicy.StubMultiples.Contains((int)(movedLength / grid)),
                    recording.Scenario + ": the stub moves to a longer policy length.");
                Assert.AreEqual(Along(movedLength), movedWire.End, recording.Scenario);
                if (shortest == 0)
                    Assert.AreEqual(SchematicConnectionPolicy.StubMultiples[1] * grid, movedLength, recording.Scenario + ": the next length has room.");
            }
            var everywhere = recording;
            foreach (int multiple in SchematicConnectionPolicy.StubMultiples) everywhere = everywhere.WithJunction(Along(multiple * grid, policy.ClearanceNm), screen);
            var (_, refusal, answered) = await everywhere.Attempt();
            Assert.AreEqual(SchematicConnectionErrors.RealizationNoFreeStub, refusal?.Code, recording.Scenario + ": " + refusal?.Message);
            // The evidence the journey keeps for a refused realization carries the refusal's own code and message, what the
            // editor was asked and answered, and no operations or generated items.
            var kept = System.Text.Json.Nodes.JsonNode.Parse(FormatRecording(recording.Scenario, everywhere.State, everywhere.Checkpoint, answered, null,
                recording.Scenario + ".recovery.json", refusal))!;
            Assert.AreEqual(refusal!.Code, kept["refusal"]!["code"]!.GetValue<string>(), recording.Scenario);
            Assert.AreEqual(refusal.Message, kept["refusal"]!["message"]!.GetValue<string>(), recording.Scenario);
            Assert.IsEmpty(kept["operations"]!.AsArray(), recording.Scenario);
            Assert.IsEmpty(kept["generated"]!.AsArray(), recording.Scenario);
            Assert.HasCount(answered.Count, kept["measurements"]!.AsArray(), recording.Scenario);
            Assert.IsGreaterThan(0, answered.Count, recording.Scenario + ": the refusal came after measuring.");
            Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(SchematicJson.Formatter.Format(answered[^1].Reply)),
                kept["measurements"]!.AsArray()[^1]!["response"]), recording.Scenario + ": the last answer kept is the editor's last answer.");
            // A realization that fails without a refusal (here a measurement the editor stopped answering) is kept the same way,
            // with the failure's type and message and no error code, since only a refusal has one.
            var stopped = new InvalidOperationException("The editor stopped answering placement measurements.");
            var failed = System.Text.Json.Nodes.JsonNode.Parse(FormatRecording(recording.Scenario, everywhere.State, everywhere.Checkpoint, answered, null,
                recording.Scenario + ".recovery.json", stopped))!;
            Assert.IsNull(failed["refusal"], recording.Scenario);
            Assert.AreEqual(typeof(InvalidOperationException).FullName, failed["failure"]!["type"]!.GetValue<string>(), recording.Scenario);
            Assert.AreEqual(stopped.Message, failed["failure"]!["message"]!.GetValue<string>(), recording.Scenario);
            Assert.HasCount(answered.Count, failed["measurements"]!.AsArray(), recording.Scenario);
            Assert.IsNull(kept["failure"], recording.Scenario + ": a refusal is recorded as a refusal.");
            // Must-catch for the likely cause of the refusal in governed run t20260924T114705Z-b90471, whose own recording was
            // not kept (the journey wrote it only after a successful realization; it now keeps a failed one too): a new probe
            // copied from a symbol whose reference field the journey's manual field check had dragged 60.96 mm aside and
            // 121.666 mm in front of its pin, as it left one in governed run t20260924T154856Z-66ef97. KiCad measures a symbol
            // as one rectangle around its body, pins and visible fields, so that rectangle would cover the probe pin's whole
            // stub room: a correct refusal, which must say the pin's own symbol is in the way. New symbols no longer copy such
            // a field, but a symbol that covers its own pin must still be refused. The same field is added here to the real
            // measured bounds of a recorded stub's symbol, on a connection with no join.
            var stubbedByDesign = realization.Outcomes.Where(o => o.Strategy == ConnectionRealizationStrategy.LabelStub && o.FallbackReason is null)
                .Select(o => (o.NetId, o.ScreenId)).ToHashSet();
            var unjoined = recording.Intent.Screens.SelectMany(s => s.Islands).Where(i => !i.JoinRequired).ToArray();
            var covered = unjoined.Where(i => stubbedByDesign.Contains((i.NetId, i.ScreenId))).SelectMany(i => i.Members).FirstOrDefault(m => m.RequiresStub)
                ?? unjoined.SelectMany(i => i.Members).FirstOrDefault(m => m.RequiresStub);
            if (covered is not null)
            {
                covering.Add(recording.Scenario);
                const long Aside = 60_960_000, Ahead = 121_666_000;
                var faraway = recording.With((request, reply) =>
                {
                    foreach (var owner in reply.Obstacles.Concat(reply.Candidates).Where(o => o.Id.Value == covered.Pin.SymbolId.ToString("D")))
                    {
                        var pin = owner.SymbolPins.Pins.Single(p => p.Id.Value == covered.Pin.PlacedPinId.ToString("D"));
                        var (ox, oy) = SchematicConnectionGeometry.Outward(pin);
                        long fx = pin.Position.XNm + ox * Ahead + oy * Aside, fy = pin.Position.YNm + oy * Ahead - ox * Aside;
                        long l = Math.Min(owner.Bounds.Position.XNm, fx), t = Math.Min(owner.Bounds.Position.YNm, fy);
                        long r = Math.Max(owner.Bounds.Position.XNm + owner.Bounds.Size.XNm, fx + 6_088_500);
                        long b = Math.Max(owner.Bounds.Position.YNm + owner.Bounds.Size.YNm, fy + 2_043_300);
                        owner.Bounds = new() { Position = new() { XNm = l, YNm = t }, Size = new() { XNm = r - l, YNm = b - t } };
                    }
                    return reply;
                });
                var error = await Assert.ThrowsExactlyAsync<AutomationException>(faraway.Realize, recording.Scenario);
                Assert.AreEqual(SchematicConnectionErrors.RealizationNoFreeStub, error.Code, recording.Scenario + ": " + error.Message);
                // The covering bounds are the symbol's, so the refusal names the covered pin or another pin of that symbol whose
                // stub is drawn before it.
                StringAssert.Contains(error.Message, "Pin " + Reference(recording.Plan.Candidate!, covered.Pin.Endpoint.ComponentId) + ".", recording.Scenario);
                // The symbol is named by the reference of every component it draws: on the repeated channel sheet (channel-pair)
                // one drawn probe is TP802 on one instance and TP803 on the other.
                string ownName = SymbolName(recording.Plan.Candidate!, covered.Pin.SymbolId);
                ownNames.Add(recording.Scenario, ownName);
                StringAssert.Contains(ownName, Reference(recording.Plan.Candidate!, covered.Pin.Endpoint.ComponentId), recording.Scenario);
                StringAssert.Contains(error.Message, "the label overlaps the bounds KiCad measures for its own symbol " + ownName
                    + ", which take in all of that symbol's visible fields", recording.Scenario);
                Assert.IsFalse(error.Message.Contains(covered.Pin.SymbolId.ToString("D"), StringComparison.Ordinal),
                    recording.Scenario + ": the refusal names the symbol by its reference, never by its identity: " + error.Message);
            }
            // On a real sheet where an existing connection was named by a label on a join candidate's pin (no join stub had
            // room): a junction one grid inside that label leaves it no room there, so the realizer never draws over the
            // junction. When a later candidate exists, it is named by a label on its own pin (join-anchor-label: the link's wire
            // runs straight out of both probe pins, so the first is named). In the join-turned-link scene the link's wire turns one grid
            // in front of the first probe pin, so no label fits there, and runs straight out of the second: the label on the
            // second pin was the last candidate's, and blocking it leaves the connection nothing to be named by, with each
            // pin's reason in the refusal. (A label on a new pin's own pin, which the frame preference draws in place of a stub that
            // would cross the drawing sheet's border, as on the PSU/CPU sheets, names no existing connection and is not one of these.)
            var joinCandidates = recording.Intent.Screens.SelectMany(s => s.Islands).SelectMany(i => i.JoinCandidates).Select(c => c.PlacedPinId).ToHashSet();
            if (realization.Generated.SingleOrDefault(g => g.Role == GeneratedConnectionRole.AnchorLabel && joinCandidates.Contains(g.PlacedPinId!.Value)) is { } anchor)
            {
                var island = recording.Intent.Screens.SelectMany(s => s.Islands).Single(i => i.JoinCandidates.Any(c => c.PlacedPinId == anchor.PlacedPinId));
                int at = island.JoinCandidates.ToList().FindIndex(c => c.PlacedPinId == anchor.PlacedPinId);
                var (target, label) = realization.Operations.Where(o => o.Create?.Is(LocalLabel.Descriptor) == true)
                    .Select(o => (o.TargetDocument, Label: o.Create.Unpack<LocalLabel>())).Single(x => x.Label.Id.Value == anchor.Id.ToString("D"));
                var (fx, fy) = SchematicConnectionRealizer.Facing(label.SpinStyle);
                var labelScreen = recording.Checkpoint.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(target)).Metadata.ScreenId;
                var blockedLabel = recording.WithJunction(new() { XNm = label.Position.XNm + fx * grid, YNm = label.Position.YNm + fy * grid }, labelScreen);
                anchorBranches.Add(recording.Scenario, at + 1 < island.JoinCandidates.Count ? "next-candidate" : "no-join-anchor");
                if (at + 1 < island.JoinCandidates.Count)
                {
                    var renamed = await blockedLabel.Realize();
                    var named = renamed.Generated.Single(g => island.JoinCandidates.Any(c => c.PlacedPinId == g.PlacedPinId)
                        && g.Role is GeneratedConnectionRole.AnchorLabel or GeneratedConnectionRole.StubLabel);
                    Assert.AreEqual(island.JoinCandidates[at + 1].PlacedPinId, named.PlacedPinId, recording.Scenario + ": the next join candidate is named.");
                    Assert.AreEqual(GeneratedConnectionRole.AnchorLabel, named.Role, recording.Scenario + ": by a label on its own pin.");
                    Assert.IsFalse(renamed.Generated.Any(g => g.PlacedPinId == anchor.PlacedPinId), recording.Scenario + ": nothing is drawn at the blocked pin.");
                }
                else
                {
                    Assert.IsGreaterThan(0, at, recording.Scenario + ": a scene naming its only candidate would test nothing here.");
                    var error = await Assert.ThrowsExactlyAsync<AutomationException>(blockedLabel.Realize, recording.Scenario);
                    Assert.AreEqual(SchematicConnectionErrors.RealizationNoJoinAnchor, error.Code, recording.Scenario + ": " + error.Message);
                    foreach (var candidate in island.JoinCandidates)
                        StringAssert.Contains(error.Message, "pin " + PinName(recording.Plan.Candidate!, candidate.Endpoint) + ": ", recording.Scenario);
                    StringAssert.Contains(error.Message, "junction point of ", recording.Scenario);
                }
            }
        }
        Assert.IsNotEmpty(covering, "Some recording has a new pin whose own symbol can be made to cover its stub room.");
        // Must-catch: the probe drawn once on the repeated channel sheet is named by both of its references, never by one of them
        // alone nor by its identity.
        Assert.AreEqual(2, ownNames.GetValueOrDefault("channel-pair", "").Split('/').Length,
            "channel-pair names its repeated-sheet probe by both references: " + ownNames.GetValueOrDefault("channel-pair"));
        // The live scenes are built so that each branch is taken on every recording of them.
        Assert.AreEqual("next-candidate", anchorBranches.GetValueOrDefault("join-anchor-label"),
            "join-anchor-label names its first candidate, so blocking that label names the next one.");
        Assert.AreEqual("no-join-anchor", anchorBranches.GetValueOrDefault("join-turned-link"),
            "join-turned-link names its last candidate, so blocking that label leaves nothing to name the connection.");
    }

    [TestMethod]
    public async Task ThePsuCpuSheetsAreWiredWhereARouteFits()
    {
        // CN-1 §7 on the PSU/CPU fixture's Complete stage exactly as a live KiCad measured it (the psu-cpu-connected journey's
        // recording): every connection with two or more new pins on a sheet is joined by orthogonal wires with one name label,
        // unless no route fits, which is recorded as its fallback reason; every other connection keeps its label stubs. Routing
        // asks KiCad nothing beyond what label stubs need, and the same measurements give the same batch (I9).
        var recording = LoadRecording(Path.Combine(RecordingDirectory, "psu-cpu-complete.measurement.json.gz"));
        var policy = SchematicConnectionPolicy.FromSnapshot(recording.Checkpoint.Electrical.Hierarchy.Data);
        int asked = 0;
        var replay = Replay(recording.Measurements);
        var realization = await SchematicConnectionRealizer.RealizeAsync(recording.Intent, recording.Plan.Candidate!, recording.Checkpoint,
            (request, token) => { asked++; return replay(request, token); }, policy);
        Assert.AreEqual(recording.Measurements.Count, asked, "Routing asks KiCad nothing beyond what label stubs need.");
        var measured = Measured(recording.Measurements);
        var names = recording.Desired.Engineering.Circuit.Nets.ToDictionary(n => n.Id, n => n.Name);
        foreach (var outcome in realization.Outcomes.OrderBy(o => o.RepresentativePathKey, StringComparer.Ordinal).ThenBy(o => names[o.NetId], StringComparer.Ordinal))
            Console.WriteLine($"{names[outcome.NetId]} on {outcome.RepresentativePathKey}: {outcome.Strategy}, {outcome.GeneratedIds.Count} items"
                + (outcome.FallbackReason is null ? "" : "; fallback: " + outcome.FallbackReason));
        var routed = realization.Outcomes.Where(o => o.Strategy == ConnectionRealizationStrategy.OrthogonalWire).ToArray();
        var problems = RoutedProblems(realization, recording.Intent, measured, policy);
        Assert.IsEmpty(problems, string.Join("\n", problems));
        foreach (var outcome in realization.Outcomes)
        {
            int terminals = Terminals(recording.Intent, outcome, measured).Count;
            if (terminals < 2)
                Assert.IsTrue(outcome.Strategy == ConnectionRealizationStrategy.LabelStub && outcome.FallbackReason is null,
                    names[outcome.NetId] + ": fewer than two new pins on a sheet keep their label stubs, which is no fallback.");
            else
                Assert.IsTrue(outcome.Strategy == ConnectionRealizationStrategy.OrthogonalWire || outcome.FallbackReason is not null,
                    names[outcome.NetId] + ": a connection with " + terminals + " new pins is routed or says why not.");
        }
        // Exactly the connections the fixture's layout leaves room for inside the drawing sheet's frame are routed, the same list
        // the live journey requires. Two fall back and say why: PSU_SCL, whose U3.6 the wires routed before it leave no way
        // through, and RAIL_B, whose hierarchical label fits at none of its new pins (U3.8's would leave the frame; U2.4's would
        // overlap an earlier label).
        var sheetOf = PsuCpuFixture.ExpectedNative(PsuCpuStage.Complete).Sheets.Where(s => s.NativeSheetSymbol is not null)
            .ToDictionary(s => s.NativeSheetSymbol!.Value.ToString("D"), s => s.Key);
        string Sheet(string path) => sheetOf.GetValueOrDefault(path[(path.LastIndexOf('/') + 1)..], "ROOT");
        CollectionAssert.AreEquivalent(NativeSessionTests.PsuCpuRoutedConnections.ToArray(),
            routed.Select(o => (Sheet(o.RepresentativePathKey), names[o.NetId])).ToArray());
        var scl = realization.Outcomes.Single(o => names[o.NetId] == "PSU_SCL");
        StringAssert.Contains(scl.FallbackReason, "cannot reach the rest of its connection");
        var railB = realization.Outcomes.Single(o => names[o.NetId] == "RAIL_B" && Sheet(o.RepresentativePathKey) == "PSU");
        StringAssert.Contains(railB.FallbackReason, "has no room at any of its new pins");
        StringAssert.Contains(railB.FallbackReason, "outside the drawing sheet's frame");
        // Every wire routed on the PSU/CPU sheets lies inside the drawing sheet's inner border and clear of its title block as
        // KiCad measured them (RoutedProblems above), and the inner border is KiCad's default one, 12 mm in.
        foreach (var (path, geometry) in measured)
            Assert.AreEqual(new SchematicConnectionRealizer.Box(12_000_000 + policy.ClearanceNm, 12_000_000 + policy.ClearanceNm,
                geometry.PageBounds.Size.XNm - 12_000_000 - policy.ClearanceNm, geometry.PageBounds.Size.YNm - 12_000_000 - policy.ClearanceNm),
                SchematicConnectionRealizer.RouteArea(new(0, 0, geometry.PageBounds.Size.XNm, geometry.PageBounds.Size.YNm), geometry.DrawingSheet,
                    policy.ClearanceNm).Region, path);
        // The frame preference (ledger ped4439a665d260ef): every other generated stub and label lies inside that inner border and
        // overlaps none of its title block, each label by the box KiCad measured for its prototype (§6.2 round 2) at its position,
        // as the creation journey's own check reads them (NativeSessionTests.LabelStubDrawingSheetProblems). Before the preference
        // RAIL_B and PSU_SCL on PSU (at U3.8 and U3.6) and RAIL_A on CPU_POWER crossed the left border and RAIL_B on CPU the top one;
        // their labels now sit on their pins.
        var framing = FrameProblems(realization, recording.Measurements, measured);
        Assert.IsEmpty(framing.Problems, string.Join("\n", framing.Problems));
        foreach (var (sheet, net) in new[] { ("PSU", "RAIL_B"), ("PSU", "PSU_SCL"), ("CPU", "RAIL_B"), ("CPU_POWER", "RAIL_A") })
            Assert.IsTrue(realization.Outcomes.Where(o => Sheet(o.RepresentativePathKey) == sheet && names[o.NetId] == net).SelectMany(o => o.GeneratedIds)
                .Any(id => realization.Generated.Single(g => g.Id == id).Role == GeneratedConnectionRole.AnchorLabel), $"{net} on {sheet} is named on its pin.");
        Assert.IsTrue(realization.Diagnostics.Where(d => d.Code == SchematicConnectionErrors.RealizationPageReservationsUnspecified)
            .All(d => d.Message.EndsWith("so does every label stub and label.", StringComparison.Ordinal)), "Nothing on the PSU/CPU sheets is left outside the border.");
        // The recorded batch, which KiCad drew and verified in the psu-cpu-connected journey, is reproduced exactly: every
        // operation byte for byte, ending with the assertion KiCad must prove, and every generated item with its role, sheet,
        // pin and sheet symbol.
        Assert.IsTrue(recording.Generated.Any(g => g!["role"]!.GetValue<string>() == nameof(GeneratedConnectionRole.RouteWire)),
            "The recording was made by a realizer that routes.");
        CollectionAssert.AreEqual(recording.Operations.Select(o => o.ToByteString()).ToArray(), realization.Operations.Select(o => o.ToByteString()).ToArray(),
            "The recorded batch is reproduced byte for byte.");
        CollectionAssert.AreEqual(recording.Generated.Select(g => g!.ToJsonString()).ToArray(), realization.Generated.Select(g => new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = g.Id.ToString("D"), ["role"] = g.Role.ToString(), ["screen"] = g.ScreenId.ToString("D"),
            ["placedPin"] = g.PlacedPinId?.ToString("D"), ["sheetSymbol"] = g.SheetSymbolId?.ToString("D"), ["typeUrl"] = g.TypeUrl
        }.ToJsonString()).ToArray(), "The recorded generated items are reproduced exactly.");
        var again = await SchematicConnectionRealizer.RealizeAsync(recording.Intent, recording.Plan.Candidate!, recording.Checkpoint, Replay(recording.Measurements), policy);
        CollectionAssert.AreEqual(realization.Operations.Select(o => o.ToByteString()).ToArray(), again.Operations.Select(o => o.ToByteString()).ToArray(),
            "The same measurements give the same batch (I9).");
    }

    // The first answer for each sheet instance: the one that lists its items and pins.
    internal static IReadOnlyDictionary<string, SchematicPlacementGeometry> Measured(
        IReadOnlyList<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)> measurements)
    {
        var result = new Dictionary<string, SchematicPlacementGeometry>(StringComparer.Ordinal);
        foreach (var (request, reply) in measurements)
            result.TryAdd(string.Join('/', request.Document.SheetPath.Path.Select(p => p.Value)), reply);
        return result;
    }

    // The anchors of an island's new pins on its representative sheet, one per point, as §7 joins them.
    internal static IReadOnlyCollection<RoutedConnectionChecks.P> Terminals(SchematicConnectionIntent intent, ConnectionIslandOutcome outcome,
        IReadOnlyDictionary<string, SchematicPlacementGeometry> measured)
    {
        var island = intent.Screens.SelectMany(s => s.Islands).Single(i => i.NetId == outcome.NetId && i.ScreenId == outcome.ScreenId);
        var pins = PinsAt(measured[outcome.RepresentativePathKey]);
        var connected = island.Members.Where(m => m.AlreadyConnected).Select(m => pins[m.Pin.PlacedPinId]).ToHashSet();
        return island.Members.Where(m => m.RequiresStub).Select(m => pins[m.Pin.PlacedPinId]).Where(p => !connected.Contains(p)).ToHashSet();
    }

    private static Dictionary<Guid, RoutedConnectionChecks.P> PinsAt(SchematicPlacementGeometry geometry) =>
        geometry.Obstacles.Concat(geometry.Candidates).Where(o => o.SymbolPins is not null).SelectMany(o => o.SymbolPins.Pins)
            .ToDictionary(p => Guid.Parse(p.Id.Value), p => new RoutedConnectionChecks.P(p.Position.XNm, p.Position.YNm));

    /// <summary>Everything wrong with the routed connections of a realization (RoutedConnectionChecks), measured against the
    /// sheets as KiCad reported them: the realizer's own drawing, or with <paramref name="drawn"/> what KiCad holds after
    /// committing it (every planned item must be there with the planned geometry's form).</summary>
    internal static List<string> RoutedProblems(SchematicConnectionRealization realization, SchematicConnectionIntent intent,
        IReadOnlyDictionary<string, SchematicPlacementGeometry> measured, SchematicConnectionPolicy policy, SchematicHierarchyData? drawn = null)
    {
        var problems = new List<string>();
        static RoutedConnectionChecks.P P(Vector2 v) => new(v.XNm, v.YNm);
        foreach (var outcome in realization.Outcomes.Where(o => o.Strategy == ConnectionRealizationStrategy.OrthogonalWire))
        {
            string path = outcome.RepresentativePathKey;
            var geometry = measured[path];
            var screen = (drawn ?? realization.Design.Schematic).Instances.Single(i => string.Join('/', i.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == path);
            var items = SchematicItemDelta.Index(screen.Items);
            // The route's own wires, junctions and label; the island's sheet pins, if any, are drawn as stubs of their own.
            var ids = realization.Generated.Where(g => outcome.GeneratedIds.Contains(g.Id)
                && g.Role is GeneratedConnectionRole.RouteWire or GeneratedConnectionRole.Junction or GeneratedConnectionRole.StubLabel).Select(g => g.Id).ToHashSet();
            foreach (var missing in ids.Where(id => !items.ContainsKey(id)))
                problems.Add(outcome.NetId.ToString("D") + " on " + path + ": generated item " + missing.ToString("D") + " is not drawn");
            var mine = ids.Where(items.ContainsKey).Select(id => items[id]).ToArray();
            var wires = mine.OfType<SchematicLine>().Select(l => (P(l.Start), P(l.End))).ToArray();
            var others = items.Where(i => !ids.Contains(i.Key)).Select(i => i.Value).OfType<SchematicLine>()
                .Where(l => l.Type is SchematicLineType.SltWire or SchematicLineType.SltBus).Select(l => (P(l.Start), P(l.End))).ToArray();
            var labels = mine.Select(i => i switch
            {
                LocalLabel l => (P(l.Position), SchematicConnectionRealizer.Facing(l.SpinStyle)),
                GlobalLabel l => (P(l.Position), SchematicConnectionRealizer.Facing(l.SpinStyle)),
                HierarchicalLabel l => (P(l.Position), SchematicConnectionRealizer.Facing(l.SpinStyle)),
                _ => ((RoutedConnectionChecks.P, (int, int))?)null
            }).OfType<(RoutedConnectionChecks.P, (int, int))>().ToArray();
            var symbolsAt = geometry.Obstacles.Concat(geometry.Candidates).Where(o => o.SymbolPins is not null)
                .SelectMany(o => o.SymbolPins.Pins.Select(p => (Symbol: o.Id.Value, At: new RoutedConnectionChecks.P(p.Position.XNm, p.Position.YNm))))
                .GroupBy(x => x.At).ToDictionary(g => g.Key, g => g.Select(x => x.Symbol).Distinct().Count());
            // The §7 region and title block of this sheet as KiCad measured its drawing sheet (the realizer's own reading of it;
            // the journey also checks KiCad's drawing against the drawn frame and title block directly).
            var page = geometry.PageBounds;
            var (region, keepOuts) = SchematicConnectionRealizer.RouteArea(new(page.Position.XNm, page.Position.YNm, page.Position.XNm + page.Size.XNm,
                page.Position.YNm + page.Size.YNm), geometry.DrawingSheet, policy.ClearanceNm);
            problems.AddRange(RoutedConnectionChecks.Problems(outcome.NetId.ToString("D") + " on " + path, policy.GridNm, (region.L, region.T, region.R, region.B), wires,
                mine.OfType<Junction>().Select(j => P(j.Position)).ToArray(), labels, symbolsAt, Terminals(intent, outcome, measured), others,
                keepOuts: [.. keepOuts.Select(k => (k.L, k.T, k.R, k.B))], clearance: policy.ClearanceNm));
        }
        return problems;
    }

    /// <summary>The frame preference on a realization (ledger ped4439a665d260ef): every generated stub wire other than a routed
    /// connection's, and every generated label, against the drawing sheet KiCad measured on its sheet, as the creation journey's own
    /// check reads it (<see cref="NativeSessionTests.LabelStubDrawingSheetProblems"/>); each label by the box KiCad measured for its
    /// prototype (§6.2 round 2), moved to the label's position.</summary>
    internal static (List<string> Problems, List<object> Evidence) FrameProblems(SchematicConnectionRealization realization,
        IReadOnlyList<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)> measurements,
        IReadOnlyDictionary<string, SchematicPlacementGeometry> measured)
    {
        static (string Text, SchematicLabelSpinStyle Spin, Vector2 Position) LabelOf(IMessage item) => item switch
        {
            LocalLabel l => (l.Text.Text_, l.SpinStyle, l.Position),
            HierarchicalLabel l => (l.Text.Text_, l.SpinStyle, l.Position),
            GlobalLabel l => (l.Text.Text_, l.SpinStyle, l.Position),
            _ => throw new ArgumentException("Not a label.")
        };
        static string PathOf(DocumentSpecifier document) => string.Join('/', document.SheetPath.Path.Select(p => p.Value));
        var prototypes = new Dictionary<(string Path, string Type, string Text, SchematicLabelSpinStyle Spin), (long L, long T, long R, long B)>();
        foreach (var (request, reply) in measurements)
            for (int i = 0; i < request.ItemCandidates.Count; i++)
            {
                var (text, spin, _) = LabelOf(SchematicItemDelta.Index([request.ItemCandidates[i]]).Single().Value);
                var (box, at) = (reply.ItemCandidates[i].Bounds, reply.ItemCandidates[i].Anchor);
                prototypes[(PathOf(request.Document), request.ItemCandidates[i].TypeUrl, text, spin)] = (box.Position.XNm - at.XNm, box.Position.YNm - at.YNm,
                    box.Position.XNm + box.Size.XNm - at.XNm, box.Position.YNm + box.Size.YNm - at.YNm);
            }
        var routedWires = realization.Generated.Where(g => g.Role == GeneratedConnectionRole.RouteWire).Select(g => g.Id).ToHashSet();
        var generated = realization.Generated.Select(g => g.Id).ToHashSet();
        var problems = new List<string>();
        var evidence = new List<object>();
        foreach (var (path, geometry) in measured.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            var screen = realization.Design.Schematic.Instances.Single(s => PathOf(s.Metadata.Document) == path);
            var items = SchematicItemDelta.Index(screen.Items).Where(p => generated.Contains(p.Key)).OrderBy(p => p.Key).ToArray();
            var stubs = items.Select(p => p.Value).OfType<SchematicLine>().Where(l => !routedWires.Contains(Guid.Parse(l.Id.Value))).ToArray();
            var labels = new List<(string What, Box2 Bounds)>();
            foreach (var (id, item) in items.Where(p => p.Value is LocalLabel or HierarchicalLabel or GlobalLabel))
            {
                var (text, spin, position) = LabelOf(item);
                string what = item.Descriptor.Name + " '" + text + "' " + id.ToString("D");
                if (!prototypes.TryGetValue((path, Any.Pack(item).TypeUrl, text, spin), out var r))
                {
                    problems.Add(path + ": KiCad measured no prototype of " + what);
                    continue;
                }
                labels.Add((what, new Box2 { Position = new() { XNm = position.XNm + r.L, YNm = position.YNm + r.T }, Size = new() { XNm = r.R - r.L, YNm = r.B - r.T } }));
            }
            var (found, frame) = NativeSessionTests.LabelStubDrawingSheetProblems(path, geometry, stubs, labels);
            problems.AddRange(found);
            evidence.Add(frame);
        }
        return (problems, evidence);
    }

    /// <summary>One recorded live realization (automation/tests/fixtures/connection-realization), optionally with a
    /// fault injected into the editor's recorded answers.</summary>
    internal sealed record Recording(string Scenario, DesignRecoveryState Saved, SchematicDesign Desired, DesignRecoveryState State,
        SchematicSynchronizationPlan Plan, CheckedSchematicState Checkpoint,
        IReadOnlyList<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)> Measurements, IReadOnlyList<SchematicItemOperation> Operations,
        IReadOnlyList<System.Text.Json.Nodes.JsonNode?> Generated, Func<MeasureSchematicPlacement, SchematicPlacementGeometry, SchematicPlacementGeometry>? Fault = null)
    {
        public SchematicConnectionIntent Intent => Plan.Connections!;

        public Recording With(Func<MeasureSchematicPlacement, SchematicPlacementGeometry, SchematicPlacementGeometry> fault) => this with { Fault = fault };

        /// <summary>The same scenario on a sheet that also holds a junction at <paramref name="at"/>: added to the saved
        /// record, the checkpoint and every recorded measurement of that screen, as the editor would report it.</summary>
        public Recording WithJunction(Kiapi.Common.Types.Vector2 at, KIID screen)
        {
            var junction = new Junction { Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = at.Clone(), Locked = LockedState.LsUnlocked };
            var item = Any.Pack(junction);
            void Add(SchematicHierarchyData data)
            {
                foreach (var copy in data.Instances.Where(s => s.Metadata.ScreenId.Equals(screen))) copy.Items.Add(item.Clone());
            }
            var saved = SchematicConnectionRealizerTests.Edited(Saved, Add);
            var state = Revision(saved, Desired);
            var plan = SchematicConnectionIntentBuilderTests.Plan(state);
            _ = RequireRealizationPlan(plan);
            var checkpoint = Checkpoint.Clone();
            Add(checkpoint.Electrical.Hierarchy.Data);
            var bounds = new SchematicPlacementBounds { Id = junction.Id.Clone(), Anchor = at.Clone(),
                Bounds = new() { Position = at.Clone(), Size = new() } };
            var fault = Fault;
            return this with { Saved = saved, State = state, Plan = plan, Checkpoint = checkpoint, Fault = (request, reply) =>
            {
                if (reply.ScreenId.Equals(screen)) reply.Obstacles.Add(bounds.Clone());
                return fault is null ? reply : fault(request, reply);
            } };
        }

        /// <summary>The revision the journey planned: the saved record with only its circuit (its nets, and any component
        /// the revision adds) and its declared part symbols replaced by the desired design's.</summary>
        public static DesignRecoveryState Revision(DesignRecoveryState saved, SchematicDesign desired)
        {
            var design = saved.Baseline with { Engineering = saved.Baseline.Engineering with { Circuit = desired.Engineering.Circuit },
                PartSymbols = desired.PartSymbols };
            return saved with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, saved.KnowledgeLibraries)) };
        }

        /// <summary>Realize as <see cref="Realize"/> does, keeping every measurement asked and answered, and the refusal instead of
        /// throwing it, as the journey keeps them for a refused realization.</summary>
        public async Task<(SchematicConnectionRealization? Realization, AutomationException? Refusal,
            IReadOnlyList<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)> Asked)> Attempt()
        {
            var replay = Replay(Measurements);
            var asked = new List<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)>();
            try
            {
                var realization = await SchematicConnectionRealizer.RealizeAsync(Intent, Plan.Candidate!, Checkpoint, async (request, token) =>
                {
                    var reply = await replay(request, token);
                    reply = Fault is null ? reply : Fault(request, reply);
                    asked.Add((request.Clone(), reply.Clone()));
                    return reply;
                }, SchematicConnectionPolicy.FromSnapshot(Checkpoint.Electrical.Hierarchy.Data));
                return (realization, null, asked);
            }
            catch (AutomationException refusal) { return (null, refusal, asked); }
        }

        public Task<SchematicConnectionRealization> Realize()
        {
            var replay = Replay(Measurements);
            return SchematicConnectionRealizer.RealizeAsync(Intent, Plan.Candidate!, Checkpoint, async (request, token) =>
            {
                var reply = await replay(request, token);
                return Fault is null ? reply : Fault(request, reply);
            }, SchematicConnectionPolicy.FromSnapshot(Checkpoint.Electrical.Hierarchy.Data));
        }
    }

    internal static string RecordingDirectory => Path.Combine(PsuCpuFixture.RepositoryRoot, "automation", "tests", "fixtures", "connection-realization");

    /// <summary>Every replay fixture of the recording directory, plain (<c>*.measurement.json</c>) or compressed
    /// (<c>*.measurement.json.gz</c>, the PSU/CPU Complete stage), in name order.</summary>
    internal static IReadOnlyList<Recording> Recordings() =>
        [.. Directory.GetFiles(RecordingDirectory, "*.measurement.json").Concat(Directory.GetFiles(RecordingDirectory, "*.measurement.json.gz"))
            .Order(StringComparer.Ordinal).Select(LoadRecording)];

    /// <summary>One recording of the fixture directory, plain or compressed (<c>.gz</c>); a compressed recording keeps its
    /// saved record compressed beside it too, under the recorded name plus <c>.gz</c>.</summary>
    internal static Recording LoadRecording(string file)
    {
        static string Text(string path)
        {
            if (!path.EndsWith(".gz", StringComparison.Ordinal)) return File.ReadAllText(path);
            using var input = new System.IO.Compression.GZipStream(File.OpenRead(path), System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(input, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        var root = System.Text.Json.Nodes.JsonNode.Parse(Text(file))!;
        Assert.AreEqual("connection-realization", root["fixture"]!.GetValue<string>());
        Assert.AreEqual(1, root["version"]!.GetValue<int>());
        // Every scenario was planned from its saved record (shared by the scenarios of one editor state) with only its
        // nets replaced, exactly as the journey did. Recordings made before records were named use editor.recovery.json.
        string record = root["recovery"]?.GetValue<string>() ?? "editor.recovery.json";
        Assert.AreEqual(Path.GetFileName(record), record);
        string recordPath = Path.Combine(RecordingDirectory, record);
        if (!File.Exists(recordPath) && File.Exists(recordPath + ".gz"))
        {
            // The recovery store reads a plain file; a compressed record is read from a private copy.
            string copy = Path.Combine(Directory.CreateTempSubdirectory("connection-realization-").FullName, record);
            File.WriteAllText(copy, Text(recordPath + ".gz"));
            recordPath = copy;
        }
        var saved = new DesignRecoveryStore(recordPath).Read()!.State;
        var nets = root["nets"]!.AsArray().Select(n => new CircuitNet(Guid.Parse(n!["id"]!.GetValue<string>()), n["name"]!.GetValue<string>(),
            [.. n["pins"]!.AsArray().Select(p => new PinEndpoint(Guid.Parse(p!["component"]!.GetValue<string>()), p["number"]!.GetValue<string>()))])).ToArray();
        // Recordings made before the circuit was kept changed only the nets; a revision that declares part symbols keeps
        // all of them.
        var circuit = root["circuit"] is { } kept ? CircuitXml.Read(kept.GetValue<string>()) : saved.Baseline.Engineering.Circuit with { Nets = nets };
        T Parse<T>(System.Text.Json.Nodes.JsonNode node) where T : IMessage<T>, new() => SchematicJson.Parser.Parse<T>(node.ToJsonString());
        var desired = saved.Baseline with { Engineering = saved.Baseline.Engineering with { Circuit = circuit },
            PartSymbols = root["partSymbols"] is { } declared ? declared.AsArray().Select(d => new SchematicPartSymbol(Guid.Parse(d!["part"]!.GetValue<string>()),
                Parse<Kiapi.Common.Types.LibraryIdentifier>(d["library"]!), Parse<SchematicCachedSymbol>(d["symbol"]!), d["bodyStyle"]!.GetValue<int>())).ToArray()
                : saved.Baseline.PartSymbols };
        CollectionAssert.AreEqual(nets.Select(n => n.Id).ToArray(), circuit.Nets.Select(n => n.Id).ToArray());
        var state = Recording.Revision(saved, desired);
        Assert.AreEqual(root["desiredSha256"]!.GetValue<string>(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(state.DesiredFileBytes)),
            "The recorded revision is reconstructed byte for byte.");
        var plan = SchematicConnectionIntentBuilderTests.Plan(state);
        _ = RequireRealizationPlan(plan);
        return new(root["scenario"]!.GetValue<string>(), saved, desired, state, plan, Parse<CheckedSchematicState>(root["checkpoint"]!),
            [.. root["measurements"]!.AsArray().Select(m => (Parse<MeasureSchematicPlacement>(m!["request"]!), Parse<SchematicPlacementGeometry>(m!["response"]!)))],
            [.. root["operations"]!.AsArray().Select(o => Parse<SchematicItemOperation>(o!))], [.. root["generated"]!.AsArray()]);
    }

    private static async Task RequireRecordingRefusal(Recording recording, string code)
    {
        var error = await Assert.ThrowsExactlyAsync<AutomationException>(recording.Realize, recording.Scenario + ": " + code);
        Assert.AreEqual(code, error.Code, recording.Scenario + ": " + error.Message);
    }

    // ---- helpers ----

    private static string Key(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(p => p.Value));

    internal static SchematicScreenData Root(SchematicHierarchyData data) => data.Instances.Single(s => s.Metadata.Document.SheetPath.Path.Count == 1);

    private static Guid Generated(Scene scene, Guid screen, GeneratedConnectionRole role, string key) =>
        SchematicConnectionIdentity.Generated(scene.Intent.OriginId, scene.Intent.NativeRevision, scene.Intent.DesiredSha256, screen, role, key);

    private static ConnectionMember Member(Scene scene, string reference)
    {
        var component = scene.Saved.Baseline.Engineering.Circuit.Components.Concat(scene.Plan.Candidate!.Engineering.Circuit.Components)
            .First(c => c.Reference == reference).Id;
        return scene.Intent.Screens.SelectMany(s => s.Islands).SelectMany(i => i.Members).Single(m => m.Pin.Endpoint.ComponentId == component && m.RequiresStub);
    }

    private static Guid SymbolId(Scene scene, string reference)
    {
        var circuit = scene.Plan.Candidate!.Engineering.Circuit;
        var component = circuit.Components.Single(c => c.Reference == reference).Id;
        var occurrence = circuit.Symbols.First(s => s.ComponentId == component).Id;
        return scene.Plan.Candidate.SymbolBindings.Single(b => b.SymbolOccurrenceId == occurrence).NativeObjectId;
    }

    private static T[] Generated<T>(SchematicConnectionRealization realization) where T : IMessage<T>, new()
    {
        var descriptor = new T().Descriptor;
        var ids = realization.Generated.Select(g => g.Id.ToString("D")).ToHashSet(StringComparer.Ordinal);
        return realization.Operations.Where(o => o.Create is not null && o.Create.Is(descriptor)).Select(o => o.Create.Unpack<T>())
            .Where(item => ids.Contains(((KIID)item.Descriptor.FindFieldByName("id").Accessor.GetValue(item)).Value)).ToArray();
    }

    private static SheetSymbol[] SheetUpdates(SchematicConnectionRealization realization) =>
        realization.Operations.Where(o => o.Update is not null && o.Update.Is(SheetSymbol.Descriptor)).Select(o => o.Update.Unpack<SheetSymbol>()).ToArray();

    private static SchematicLine StubOf(SchematicConnectionRealization realization, Guid placedPin)
    {
        var id = realization.Generated.Single(g => g.PlacedPinId == placedPin && g.Role == GeneratedConnectionRole.StubWire).Id.ToString("D");
        return Generated<SchematicLine>(realization).Single(w => w.Id.Value == id);
    }

    private static async Task RequireRefusal(Scene scene, string code, string detail, string? problem = null)
    {
        var error = await Assert.ThrowsExactlyAsync<AutomationException>(scene.Realize, problem ?? code);
        Assert.AreEqual(code, error.Code, (problem ?? code) + ": " + error.Message);
        StringAssert.Contains(error.Message, detail, problem ?? code);
    }

    private static bool IsGenerated(SchematicConnectionRealization realization, KIID id) => realization.Generated.Any(g => g.Id.ToString("D") == id.Value);

    private static bool IsCreated(Scene scene, KIID id) => scene.Intent.CreatedSymbolIds.Any(c => c.ToString("D") == id.Value);

    private static T Edit<T>(T item, Action<T> edit) where T : IMessage<T>
    {
        var copy = item.Clone(); edit(copy); return copy;
    }

    private static void EditItems(SchematicHierarchyData data, Func<IMessage, IMessage> edit)
    {
        foreach (var screen in data.Instances)
            for (int i = 0; i < screen.Items.Count; i++)
            {
                var item = SchematicItemDelta.Index([screen.Items[i]]).Single().Value;
                var changed = edit(item);
                if (!ReferenceEquals(changed, item)) screen.Items[i] = Any.Pack(changed);
            }
    }

    private static void EditSheet(SchematicHierarchyData data, Guid sheet, Action<SheetSymbol> edit) => EditItems(data, item =>
        item is SheetSymbol s && s.Id.Value == sheet.ToString("D") ? Edit(s, edit) : item);

    private static ApplySchematicItemBatch Batch(Scene scene, SchematicConnectionRealization realization)
    {
        var batch = new ApplySchematicItemBatch { Document = scene.Checkpoint.State.Document.Clone(), DocumentEpoch = scene.Checkpoint.State.Revision.Epoch,
            ExpectedRevision = scene.Checkpoint.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"),
            OriginId = scene.Intent.OriginId.ToString("D"), Description = SchematicConnectionRealizer.BatchDescription };
        batch.Operations.Add(realization.Operations);
        return batch;
    }

    // What an editor that honoured the batch would report: the realized design's schematic, with the intent's groups as nets.
    private static SchematicElectricalState Committed(Scene scene, SchematicConnectionRealization realization)
    {
        var electrical = scene.Checkpoint.Electrical.Clone();
        electrical.Hierarchy.Data = realization.Design.Schematic.Clone();
        electrical.Nets.Clear();
        int index = 0;
        foreach (var group in scene.Intent.ExpectedGroups.Where(g => g.Count > 1))
        {
            var net = new SchematicNet { Name = "/NET" + index++ };
            foreach (var byPath in group.GroupBy(k => k.SheetPathKey))
            {
                var screen = realization.Design.Schematic.Instances.Single(s => Key(s) == byPath.Key);
                var contents = new SchematicNetSheetContents { Path = screen.Metadata.Document.SheetPath.Clone() };
                contents.Items.Add(byPath.Select(k => new KIID { Value = k.PlacedPinId.ToString("D") }));
                net.Sheets.Add(contents);
            }
            electrical.Nets.Add(net);
        }
        return electrical;
    }
}
