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

// CN-1 milestone 1 label-stub realization (cn1-wiring-intent.md §6) and its resolution (§9.4). Why unit tests: no
// editor can apply a realization until lane 2C's native connectivity assertion exists and KiCad advertises
// schematic.connection-realization.v1, so the end-to-end journeys can only measure. They do: the NativeXmlComponentCreation
// journey realizes real intents against a live editor's measurements without applying them and records those
// measurements (automation/tests/fixtures/connection-realization) for the replay test. The admission rules, refusal
// codes and resolution comparisons are isolated logic that needs exact control over geometry, which only a synthetic
// measurement gives: every must-catch case here has a false-positive guard next to it. Lane 2A created this file with
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
        // and makes {R2.2, TP1.1} a new net /OUT.
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
        Assert.IsTrue(realization.Outcomes.All(o => o.Strategy == ConnectionRealizationStrategy.LabelStub && !o.AttachedCarrier && o.FallbackReason is null));
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
        var realization = await scene.Realize();
        Assert.HasCount(2, Generated<LocalLabel>(realization));
        var join = realization.Generated.Where(g => g.PlacedPinId == island.JoinCandidates[0].PlacedPinId).ToArray();
        CollectionAssert.AreEquivalent(new[] { GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel }, join.Select(g => g.Role).ToArray(),
            "The first join candidate takes a join stub.");
        var named = realization.Diagnostics.Single(d => d.Code == SchematicConnectionErrors.ExistingNetNamedByRealization);
        Assert.AreEqual("info", named.Severity); Assert.AreEqual(island.NetId, named.NetId);
    }

    [TestMethod]
    public async Task AJoinFallsBackToAnAnchorLabelThenToTheNextCandidate()
    {
        // Must-catch and guards: with the first candidate's stub room blocked but room at the pin itself, the label
        // sits on the pin; with that blocked too the next candidate is used; with no room anywhere the join fails.
        // Labels reach as far behind their anchor as KiCad draws them, so a label on the pin lies a little over its
        // own symbol's pin, and KiCad's bounds of the symbol reach past the pin's end by the target it draws on an
        // unconnected pin (PinTargetReachNm, 381,100 nm on every visible pin of the live sheets). Within that target
        // the label may lie on its own symbol, never further in front.
        var scene = Link();
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
        StringAssert.Contains(refusal.Message, "Pin " + second.Endpoint.ComponentId.ToString("D") + "." + second.Endpoint.Pin);
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
            ("an older editor without pin geometry", (_, g) => { g.PinGeometryAvailable = false; return g; }, SchematicConnectionErrors.RealizationMeasurementUnsupported)
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
        await RequireRefusal(covering, SchematicConnectionErrors.RealizationNoFreeStub, "Pin " + tp1.Endpoint.ComponentId.ToString("D") + ".1");
        // Guard: the same body clear of the stub room admits it.
        var clear = scene with { Geometry = scene.Geometry.Copy() };
        clear.Geometry.Body[unrelated] = new(at.X - 9 * Grid, at.Y + 2 * Grid, at.X - Grid / 2, at.Y + 4 * Grid);
        Assert.HasCount(3, Generated<LocalLabel>(await clear.Realize()));
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
        await RequireRefusal(loose, SchematicConnectionErrors.RealizationVariantPinIdentityUnresolved, "new symbol " + r3.ToString("D"));
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
        Assert.HasCount(2, Generated<SchematicLine>(realization), "One stub for the stacked pair and one for TP1.1.");
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
            "new pin " + stacked.Endpoint.ComponentId.ToString("D") + ".1 sits on an existing connection");

        // Must-catch: without room there, and with nothing else on the child sheet to carry it, the label cannot be drawn.
        // The refusal names the stacked pin (it used to surface as an internal inconsistency once the stub became optional).
        var boxed = Boxed(alone, at);
        await RequireRefusal(boxed, SchematicConnectionErrors.RealizationNoFreeStub, "new pin " + stacked.Endpoint.ComponentId.ToString("D") + ".1 sits on an existing connection");

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
    public async Task ALibraryCacheIsReplacedOnlyToAddDefinitionsOnASheetReceivingANewSymbol()
    {
        // The batch check (I6): a sheet's library cache may change only on a sheet that receives a new symbol, and only by
        // adding definitions. Every definition KiCad already holds there must stay exactly as it is.
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
        public Func<MeasureSchematicPlacement, SchematicPlacementGeometry, SchematicPlacementGeometry>? Tamper { get; set; }

        public Geometry Copy() => new()
        {
            Requests = [], Sized = new(Sized), Incomplete = new(Incomplete), Power = new(Power), Direction = new(Direction),
            Library = new(Library), Place = new(Place), Shift = new(Shift), Body = new(Body), LabelBehind = LabelBehind, Tamper = Tamper
        };

        public Task<SchematicPlacementGeometry> Measure(Scene scene, MeasureSchematicPlacement request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request.Clone());
            string path = string.Join('/', request.Document.SheetPath.Path.Select(p => p.Value));
            var screen = scene.Checkpoint.Electrical.Hierarchy.Data.Instances.Single(s => Key(s) == path);
            var reply = new SchematicPlacementGeometry { Document = request.Document.Clone(), Revision = request.ExpectedRevision.Clone(),
                ScreenId = screen.Metadata.ScreenId.Clone(), PinGeometryAvailable = true,
                PageBounds = new() { Position = new(), Size = new() { XNm = 297_000_000, YNm = 210_000_000 } } };
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
                var at = Place.TryGetValue(id, out var placed) ? placed
                    : new Point(symbol.Position.XNm - 4 * Grid, symbol.Position.YNm + (2 * i - (active.Length - 1)) * Grid);
                if (Shift.TryGetValue((path, id), out var shift)) at = new(at.X + shift.X, at.Y + shift.Y);
                var (dx, dy) = Direction.TryGetValue(id, out var direction) ? direction : (1, 0);
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
                    var box = Body.TryGetValue(id, out var body) ? body
                        : new Rect(symbol.Position.XNm - 4 * Grid, symbol.Position.YNm - half, symbol.Position.XNm + 4 * Grid, symbol.Position.YNm + half);
                    // KiCad draws a symbol whose definition it cannot resolve without pins, and measures it by its own bounds.
                    bool unresolved = Incomplete.TryGetValue(id, out var missing) && missing == SchematicPinGeometryIncompleteReason.SpgirDefinitionUnresolved;
                    if (!unresolved)
                        foreach (var pin in pins) box = new(Math.Min(box.L, pin.At.X), Math.Min(box.T, pin.At.Y), Math.Max(box.R, pin.At.X), Math.Max(box.B, pin.At.Y));
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
                case LocalLabel label: return Label(result, label.Position, label.Text.Text_, label.SpinStyle, 0, Behind(label));
                case GlobalLabel label: return Label(result, label.Position, label.Text.Text_, label.SpinStyle, Grid, Behind(label));
                case HierarchicalLabel label: return Label(result, label.Position, label.Text.Text_, label.SpinStyle, Grid, Behind(label));
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

        private static SchematicPlacementBounds Label(SchematicPlacementBounds result, Vector2 position, string text, SchematicLabelSpinStyle spin,
            long shape, long behind)
        {
            long length = (text.Length + 1) * Grid * 3 / 4 + shape, half = Grid / 2;
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
    /// beside it, under the name <paramref name="recovery"/> that the recording carries. A refused realization has no
    /// operations or generated items and carries the <paramref name="refusal"/> instead; it is evidence, never loaded as a
    /// replay fixture (those are named *.measurement.json).</summary>
    internal static string FormatRecording(string scenario, DesignRecoveryState revision, CheckedSchematicState checkpoint,
        IReadOnlyList<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)> measurements, SchematicConnectionRealization? realization,
        string recovery = "editor.recovery.json", AutomationException? refusal = null)
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
        if (refusal is not null) root["refusal"] = new System.Text.Json.Nodes.JsonObject { ["code"] = refusal.Code, ["message"] = refusal.Message };
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
            var stubbedPin = Guid.Parse(recording.Generated.First(g => g!["role"]!.GetValue<string>() == nameof(GeneratedConnectionRole.StubWire))!["placedPin"]!.GetValue<string>());
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
            await RequireRecordingRefusal(everywhere, SchematicConnectionErrors.RealizationNoFreeStub);
            // Must-catch from governed run t20260924T114705Z-b90471, whose own recording was not kept (the journey wrote it
            // only after a successful realization; it now keeps a failed one too). There the new probe was copied from a
            // symbol whose reference field an earlier check had dragged 60.96 mm aside and 121.666 mm in front of its pin.
            // KiCad measures a symbol as one rectangle around its body, pins and visible fields, so that rectangle covered
            // the probe pin's whole stub room: a correct refusal, which must say the pin's own symbol is in the way. The same
            // field is added here to the real measured bounds of a recorded stub's symbol, on a connection with no join.
            var covered = recording.Intent.Screens.SelectMany(s => s.Islands).Where(i => !i.JoinRequired).SelectMany(i => i.Members)
                .FirstOrDefault(m => m.RequiresStub);
            if (covered is not null)
            {
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
                StringAssert.Contains(error.Message, "Pin " + covered.Pin.Endpoint.ComponentId.ToString("D") + "." + covered.Pin.Endpoint.Pin + " of net", recording.Scenario);
                StringAssert.Contains(error.Message, "the label overlaps the bounds KiCad measures for its own symbol " + covered.Pin.SymbolId.ToString("D")
                    + ", which take in all of that symbol's visible fields", recording.Scenario);
            }
            // On a real sheet where an existing connection was named by a label on a join candidate's pin (no join stub had
            // room): a junction one grid inside that label leaves it no room there, so the realizer never draws over the
            // junction. When a later candidate exists, it is named by a label on its own pin (join-anchor-label, recorded from
            // an earlier scene where both probe pins had room). In the join-turned-link scene the link's wire turns one grid
            // in front of the first probe pin, so no label fits there, and runs straight out of the second: the label on the
            // second pin was the last candidate's, and blocking it leaves the connection nothing to be named by, with each
            // pin's reason in the refusal.
            if (realization.Generated.SingleOrDefault(g => g.Role == GeneratedConnectionRole.AnchorLabel) is { } anchor)
            {
                var island = recording.Intent.Screens.SelectMany(s => s.Islands).Single(i => i.JoinCandidates.Any(c => c.PlacedPinId == anchor.PlacedPinId));
                int at = island.JoinCandidates.ToList().FindIndex(c => c.PlacedPinId == anchor.PlacedPinId);
                var (target, label) = realization.Operations.Where(o => o.Create?.Is(LocalLabel.Descriptor) == true)
                    .Select(o => (o.TargetDocument, Label: o.Create.Unpack<LocalLabel>())).Single(x => x.Label.Id.Value == anchor.Id.ToString("D"));
                var (fx, fy) = SchematicConnectionRealizer.Facing(label.SpinStyle);
                var labelScreen = recording.Checkpoint.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(target)).Metadata.ScreenId;
                var blockedLabel = recording.WithJunction(new() { XNm = label.Position.XNm + fx * grid, YNm = label.Position.YNm + fy * grid }, labelScreen);
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
                        StringAssert.Contains(error.Message, "pin " + candidate.Endpoint.ComponentId.ToString("D") + "." + candidate.Endpoint.Pin + ": ", recording.Scenario);
                    StringAssert.Contains(error.Message, "junction point of ", recording.Scenario);
                }
            }
        }
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

    internal static IReadOnlyList<Recording> Recordings()
    {
        var result = new List<Recording>();
        foreach (var file in Directory.GetFiles(RecordingDirectory, "*.measurement.json").Order(StringComparer.Ordinal))
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            Assert.AreEqual("connection-realization", root["fixture"]!.GetValue<string>());
            Assert.AreEqual(1, root["version"]!.GetValue<int>());
            // Every scenario was planned from its saved record (shared by the scenarios of one editor state) with only its
            // nets replaced, exactly as the journey did. Recordings made before records were named use editor.recovery.json.
            string record = root["recovery"]?.GetValue<string>() ?? "editor.recovery.json";
            Assert.AreEqual(Path.GetFileName(record), record);
            var saved = new DesignRecoveryStore(Path.Combine(RecordingDirectory, record)).Read()!.State;
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
            result.Add(new(root["scenario"]!.GetValue<string>(), saved, desired, state, plan, Parse<CheckedSchematicState>(root["checkpoint"]!),
                [.. root["measurements"]!.AsArray().Select(m => (Parse<MeasureSchematicPlacement>(m!["request"]!), Parse<SchematicPlacementGeometry>(m!["response"]!)))],
                [.. root["operations"]!.AsArray().Select(o => Parse<SchematicItemOperation>(o!))], [.. root["generated"]!.AsArray()]));
        }
        return result;
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
