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
        var scene = Link();
        var candidates = scene.Intent.Screens.Single().Islands.Single().JoinCandidates;
        Assert.HasCount(2, candidates);
        var first = scene.PinAt(candidates[0]);
        var anchored = scene.Obstacle(new(first.X - 12 * Grid, first.Y - Grid, first.X - 4 * Grid, first.Y + Grid));
        var realization = await anchored.Realize();
        var anchorLabel = realization.Generated.Single(g => g.Role == GeneratedConnectionRole.AnchorLabel);
        Assert.AreEqual(candidates[0].PlacedPinId, anchorLabel.PlacedPinId);
        Assert.AreEqual(Generated(anchored, anchored.Intent.Screens.Single().ScreenId, GeneratedConnectionRole.AnchorLabel,
            SchematicConnectionIdentity.PinAnchorKey(candidates[0].PlacedPinId)), anchorLabel.Id);
        Assert.AreEqual(first.X, Generated<LocalLabel>(realization).Single(l => l.Id.Value == anchorLabel.Id.ToString("D")).Position.XNm);
        Assert.IsFalse(realization.Generated.Any(g => g.PlacedPinId == candidates[0].PlacedPinId && g.Role == GeneratedConnectionRole.StubWire));

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
        // Guard: an unrelated symbol with incomplete pins is allowed; its whole body stays an obstacle.
        var unrelated = SymbolId(scene, "R9");
        Assert.IsFalse(scene.Intent.Screens.Single().Islands.SelectMany(i => i.Members).Any(m => m.Pin.SymbolId == unrelated));
        scene.Geometry.Incomplete[unrelated] = SchematicPinGeometryIncompleteReason.SpgirDefinitionUnresolved;
        await scene.Realize();
        scene.Geometry.Incomplete.Clear();
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

    private static Scene RootAddition()
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
        return Scene.Of(bench, state, design => WithNets(Adopt(design, created.Design), sig with { Pins = [.. sig.Pins, new(created.Component, "1")] },
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
    /// wide with every active pin on the left edge, two grids apart and pointing left; labels extend from their anchor
    /// in the direction they face, (length + 1) × 0.75 grid long, plus one grid for a global or hierarchical shape.
    /// Tests move pins, override power facts or reasons, size obstacles, or tamper with replies.</summary>
    internal sealed class Geometry
    {
        public List<MeasureSchematicPlacement> Requests { get; private set; } = [];
        public Dictionary<Guid, Rect> Sized { get; private set; } = [];
        public Dictionary<Guid, SchematicPinGeometryIncompleteReason> Incomplete { get; private set; } = [];
        public Dictionary<Guid, (SchematicPinPowerScope Scope, string Name)> Power { get; private set; } = [];
        public Dictionary<Guid, (int Dx, int Dy)> Direction { get; private set; } = [];
        public Dictionary<Guid, Guid> Library { get; private set; } = [];
        public Dictionary<Guid, Point> Place { get; private set; } = [];
        public Dictionary<(string Path, Guid Pin), Point> Shift { get; private set; } = [];
        public long LabelBehind { get; set; }
        public Func<MeasureSchematicPlacement, SchematicPlacementGeometry, SchematicPlacementGeometry>? Tamper { get; set; }

        public Geometry Copy() => new()
        {
            Requests = [], Sized = new(Sized), Incomplete = new(Incomplete), Power = new(Power), Direction = new(Direction),
            Library = new(Library), Place = new(Place), Shift = new(Shift), LabelBehind = LabelBehind, Tamper = Tamper
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
                    var box = new Rect(symbol.Position.XNm - 4 * Grid, symbol.Position.YNm - half, symbol.Position.XNm + 4 * Grid, symbol.Position.YNm + half);
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
                case LocalLabel label: return Label(result, label.Position, label.Text.Text_, label.SpinStyle, 0);
                case GlobalLabel label: return Label(result, label.Position, label.Text.Text_, label.SpinStyle, Grid);
                case HierarchicalLabel label: return Label(result, label.Position, label.Text.Text_, label.SpinStyle, Grid);
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

        private SchematicPlacementBounds Label(SchematicPlacementBounds result, Vector2 position, string text, SchematicLabelSpinStyle spin, long shape)
        {
            long length = (text.Length + 1) * Grid * 3 / 4 + shape, half = Grid / 2;
            var (x, y) = (position.XNm, position.YNm);
            var box = spin switch
            {
                SchematicLabelSpinStyle.SlssLeft => new Rect(x - length, y - half, x + LabelBehind, y + half),
                SchematicLabelSpinStyle.SlssUp => new Rect(x - half, y - length, x + half, y + LabelBehind),
                SchematicLabelSpinStyle.SlssBottom => new Rect(x - half, y - LabelBehind, x + half, y + length),
                _ => new Rect(x - LabelBehind, y - half, x + length, y + half)
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

    /// <summary>A realization recording: the planned revision's nets and exact desired-file digest, the checkpoint,
    /// every measurement request with the editor's reply in order, and the operations and generated items the
    /// realizer produced from them. The recovery record the revision was planned from is kept once beside it.</summary>
    internal static string FormatRecording(string scenario, DesignRecoveryState revision, CheckedSchematicState checkpoint,
        IReadOnlyList<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)> measurements, SchematicConnectionRealization realization)
    {
        static System.Text.Json.Nodes.JsonNode Proto(IMessage message) => System.Text.Json.Nodes.JsonNode.Parse(SchematicJson.Formatter.Format(message))!;
        var nets = DesignRecoveryStore.ReadDesired(revision).Engineering.Circuit.Nets;
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["fixture"] = "connection-realization", ["version"] = 1, ["scenario"] = scenario,
            ["desiredSha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(revision.DesiredFileBytes)),
            ["nets"] = new System.Text.Json.Nodes.JsonArray([.. nets.Select(n => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = n.Id.ToString("D"), ["name"] = n.Name,
                ["pins"] = new System.Text.Json.Nodes.JsonArray([.. n.Pins.Select(p => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                    { ["component"] = p.ComponentId.ToString("D"), ["number"] = p.Pin })])
            })]),
            ["checkpoint"] = Proto(checkpoint),
            ["measurements"] = new System.Text.Json.Nodes.JsonArray([.. measurements.Select(m =>
                (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["request"] = Proto(m.Request), ["response"] = Proto(m.Reply) })]),
            ["operations"] = new System.Text.Json.Nodes.JsonArray([.. realization.Operations.Select(o => Proto(o))]),
            ["generated"] = new System.Text.Json.Nodes.JsonArray([.. realization.Generated.Select(g => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = g.Id.ToString("D"), ["role"] = g.Role.ToString(), ["screen"] = g.ScreenId.ToString("D"),
                ["placedPin"] = g.PlacedPinId?.ToString("D"), ["sheetSymbol"] = g.SheetSymbolId?.ToString("D"), ["typeUrl"] = g.TypeUrl
            })])
        };
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
            // one grid further out, and junctions beside every stub end leave no room at all.
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
            Assert.AreEqual(Along(SchematicConnectionPolicy.StubMultiples[0] * grid), recordedWire.Line.End, "The recorded stub has the shortest length.");
            var blocked = recording.WithJunction(Along(SchematicConnectionPolicy.StubMultiples[0] * grid, policy.ClearanceNm), screen);
            var moved = await blocked.Realize();
            var movedWire = moved.Operations.Where(o => o.Create is not null && o.Create.Is(SchematicLine.Descriptor)).Select(o => o.Create.Unpack<SchematicLine>())
                .Single(l => moved.Generated.Any(g => g.PlacedPinId == stubbedPin && g.Role == GeneratedConnectionRole.StubWire && g.Id.ToString("D") == l.Id.Value));
            Assert.AreEqual(Along(SchematicConnectionPolicy.StubMultiples[1] * grid), movedWire.End, recording.Scenario);
            var everywhere = recording;
            foreach (int multiple in SchematicConnectionPolicy.StubMultiples) everywhere = everywhere.WithJunction(Along(multiple * grid, policy.ClearanceNm), screen);
            await RequireRecordingRefusal(everywhere, SchematicConnectionErrors.RealizationNoFreeStub);
        }
    }

    /// <summary>One recorded live realization (automation/tests/fixtures/connection-realization), optionally with a
    /// fault injected into the editor's recorded answers.</summary>
    internal sealed record Recording(string Scenario, DesignRecoveryState Saved, IReadOnlyList<CircuitNet> Nets, DesignRecoveryState State,
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
            var state = Revision(saved, Nets);
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

        /// <summary>The revision the journey planned: the saved record with only its nets replaced.</summary>
        public static DesignRecoveryState Revision(DesignRecoveryState saved, IReadOnlyList<CircuitNet> nets)
        {
            var design = saved.Baseline with { Engineering = saved.Baseline.Engineering with { Circuit = saved.Baseline.Engineering.Circuit with { Nets = nets } } };
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
            // Every scenario was planned from the same saved record with only its nets replaced, exactly as the journey did.
            var saved = new DesignRecoveryStore(Path.Combine(RecordingDirectory, "editor.recovery.json")).Read()!.State;
            var nets = root["nets"]!.AsArray().Select(n => new CircuitNet(Guid.Parse(n!["id"]!.GetValue<string>()), n["name"]!.GetValue<string>(),
                [.. n["pins"]!.AsArray().Select(p => new PinEndpoint(Guid.Parse(p!["component"]!.GetValue<string>()), p["number"]!.GetValue<string>()))])).ToArray();
            var state = Recording.Revision(saved, nets);
            Assert.AreEqual(root["desiredSha256"]!.GetValue<string>(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(state.DesiredFileBytes)),
                "The recorded revision is reconstructed byte for byte.");
            var plan = SchematicConnectionIntentBuilderTests.Plan(state);
            _ = RequireRealizationPlan(plan);
            T Parse<T>(System.Text.Json.Nodes.JsonNode node) where T : IMessage<T>, new() => SchematicJson.Parser.Parse<T>(node.ToJsonString());
            result.Add(new(root["scenario"]!.GetValue<string>(), saved, nets, state, plan, Parse<CheckedSchematicState>(root["checkpoint"]!),
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
