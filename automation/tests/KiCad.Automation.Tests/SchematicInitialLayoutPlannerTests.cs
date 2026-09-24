using System.Text;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static KiCad.Automation.Tests.SchematicConnectionIntentBuilderTests;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicInitialLayoutPlannerTests
{
    private static readonly InitialLayoutPolicy Policy = new(1_270_000, 1_270_000, 0);

    [TestMethod]
    public async Task MeasuredCandidatePreservesExistingGeometryRequirementsAndNativeBindings()
    {
        var state = Fixture(); byte[] original = state.DesiredFileBytes.ToArray();
        var result = await Propose(state);
        Assert.IsTrue(result.CanPropose);
        Assert.IsTrue(result.RequiresVisualReview && result.RequiresNativeConnectivityValidation);
        Assert.IsNotNull(result.DesiredDesign); Assert.IsNotNull(result.Refinement);
        var old = state.Baseline.Engineering.Circuit.Symbols.ToDictionary(s => s.Id);
        foreach (var symbol in result.DesiredDesign.Engineering.Circuit.Symbols)
        {
            if (old.TryGetValue(symbol.Id, out var unchanged)) Assert.AreEqual(unchanged, symbol);
            else
            {
                Assert.IsNotNull(symbol.Placement);
                Assert.AreEqual(0, Coordinates.MillimetersToNanometers(symbol.Placement.XMillimeters) % Policy.GridNm);
                Assert.AreEqual(0, Coordinates.MillimetersToNanometers(symbol.Placement.YMillimeters) % Policy.GridNm);
            }
        }
        Assert.AreEqual("Keep the new group together.", result.Refinement.UserInstructions);
        CollectionAssert.AreEquivalent(result.DesiredDesign.Engineering.Circuit.Symbols.Where(s => !old.ContainsKey(s.Id))
            .Select(s => s.Id).ToArray(), result.Refinement.AffectedSymbols.ToArray());
        Assert.AreEqual(state.Baseline.Schematic, result.DesiredDesign.Schematic);
        CollectionAssert.AreEqual(DesignRecoveryStore.ReadDesired(state).SymbolBindings.ToArray(), result.DesiredDesign.SymbolBindings.ToArray(),
            "Preserve the desired XML's canonical binding enumeration, not its pre-serialization construction order.");
        CollectionAssert.AreEqual(original, state.DesiredFileBytes);
        Assert.AreEqual(EngineeringDesignXml.Write(DesignRecoveryStore.ReadDesired(state).Engineering, state.KnowledgeLibraries),
            EngineeringDesignXml.Write(result.DesiredDesign.Engineering with { Circuit = result.DesiredDesign.Engineering.Circuit with
            { Symbols = DesignRecoveryStore.ReadDesired(state).Engineering.Circuit.Symbols } }, state.KnowledgeLibraries));
        Assert.IsTrue(SchematicSynchronizationPlanner.Plan(state with { DesiredFileBytes = Encoding.UTF8.GetBytes(result.DesiredXml!) }).CanPrepare,
            "The proposal must retain pre-creation bindings so normal synchronization can admit it.");
        var retry = await Propose(state);
        Assert.AreEqual(result.DesiredXml, retry.DesiredXml);
        foreach (var body in result.Layout.Placements!)
        {
            Assert.AreEqual(7_000_000, body.Bounds.RightNm - body.Bounds.LeftNm,
                "Repeated instance envelopes must use the widest measured reference, not the displayed instance.");
            Assert.IsTrue(body.SymbolOccurrences.Count > 1);
        }
    }

    [TestMethod]
    public async Task ExplicitPlacementOnOneRepeatedOccurrenceIsPreservedForAllAliases()
    {
        var state = Fixture(); var wanted = DesignRecoveryStore.ReadDesired(state);
        var first = wanted.Engineering.Circuit.Symbols.First(s => s.Placement is null);
        var pinned = new SymbolPlacement(70.01m, 65.03m, 90, true, false, true);
        wanted = wanted with { Engineering = wanted.Engineering with { Circuit = wanted.Engineering.Circuit with
        { Symbols = wanted.Engineering.Circuit.Symbols.Select(s => s.Id == first.Id ? s with { Placement = pinned } : s).ToArray() } } };
        state = state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(wanted, state.KnowledgeLibraries)) };
        var result = await Propose(state);
        Assert.IsTrue(result.CanPropose);
        var fixedBody = result.Layout.Placements!.Single(p => p.SymbolOccurrences.Contains(first.Id));
        Assert.IsTrue(fixedBody.Fixed);
        foreach (Guid id in fixedBody.SymbolOccurrences)
            Assert.AreEqual(pinned, result.DesiredDesign!.Engineering.Circuit.Symbols.Single(s => s.Id == id).Placement);
    }

    // Isolated grouping rule behind the rendered NativeXmlComponentCreation journey, which lays out the
    // same arrangement natively: units moved from the repeated channels to the single-instance root are
    // separate bodies, so one explicit position never propagates to the other component's unit.
    [TestMethod]
    public async Task UnitsMovedFromRepeatedSheetsToOneSheetAreSeparateBodies()
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        Guid root = state.Baseline.Engineering.Circuit.SheetInstances.Single(s => s.ParentId is null).Id;
        var old = state.Baseline.Engineering.Circuit.Symbols.Select(s => s.Id).ToHashSet();
        var added = SchematicNativeCreationProjectionTests.AddComponent(state.Baseline, coordinateFree: true);
        var moved = added.Circuit.Symbols.Where(s => !old.Contains(s.Id) && s.Unit == 2).OrderBy(s => s.Id).ToArray();
        var stays = added.Circuit.Symbols.Where(s => !old.Contains(s.Id) && s.Unit == 1).Select(s => s.Id).ToArray();
        Assert.HasCount(2, moved);
        var pinned = new SymbolPlacement(101.6m, 50.8m, 0, false, false, false);
        EngineeringDesign Move(params SymbolOccurrence[] units) => added with { Circuit = added.Circuit with { Symbols = added.Circuit.Symbols
            .Select(s => !units.Any(u => u.Id == s.Id) ? s : s with { SheetInstanceId = root, Placement = s.Id == moved[0].Id ? pinned : null })
            .ToArray() } };
        state = SchematicNetReconciliationTests.Desired(state, Move(moved));
        var result = await Propose(state);
        Assert.IsTrue(result.CanPropose);
        var bodies = result.Layout.Placements!;
        foreach (var unit in moved)
            Assert.AreEqual(unit.Id, bodies.Single(b => b.SymbolOccurrences.Contains(unit.Id)).SymbolOccurrences.Single());
        Assert.IsTrue(bodies.Single(b => b.SymbolOccurrences.Contains(moved[0].Id)).Fixed);
        Assert.IsFalse(bodies.Single(b => b.SymbolOccurrences.Contains(moved[1].Id)).Fixed);
        CollectionAssert.AreEquivalent(stays, bodies.Single(b => b.SymbolOccurrences.Contains(stays[0])).SymbolOccurrences.ToArray(),
            "The unit left on the repeated channels is still one shared body.");
        var placed = result.DesiredDesign!.Engineering.Circuit.Symbols.ToDictionary(s => s.Id);
        Assert.AreEqual(pinned, placed[moved[0].Id].Placement);
        Assert.AreNotEqual(pinned, placed[moved[1].Id].Placement);
        Assert.IsTrue(moved.All(m => placed[m.Id].SheetInstanceId == root));
        Assert.IsTrue(SchematicSynchronizationPlanner.Plan(state with { DesiredFileBytes = Encoding.UTF8.GetBytes(result.DesiredXml!) }).CanPrepare,
            "The proposal must be directly applicable by ordinary creation.");

        // Moving only one channel's unit would leave the other channel's copy of the shared symbol
        // without an occurrence; the proposal refuses it before measuring anything.
        int calls = 0;
        var partial = SchematicNetReconciliationTests.Desired(state, Move(moved[0]));
        var error = await Assert.ThrowsAsync<AutomationException>(() => SchematicInitialLayoutPlanner.ProposeMeasuredAsync(
            partial, Policy, Regions(partial), "", (request, token) => { calls++; return Task.FromResult(Measurement(partial, request, 7_000_000)); }));
        Assert.AreEqual("created_unit_sheet_coverage_mismatch", error.Code);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task WrongOrIncompleteMeasurementNeverProducesAnApplicableCandidate()
    {
        Action<SchematicPlacementGeometry>[] corruptions =
        [
            m => m.Revision.Sequence++,
            m => m.Revision.Epoch = Guid.NewGuid().ToString("D"),
            m => m.Document.Project.Name += "foreign",
            m => m.ScreenId.Value = Guid.NewGuid().ToString("D"),
            m => m.Candidates.Clear(),
            m => m.Candidates.Add(m.Candidates[0].Clone()),
            m => m.Candidates[0].Anchor.XNm++,
            m => m.Candidates[0].Bounds.Size.XNm = -1,
            m => m.Candidates[0].Bounds.Position.XNm = long.MaxValue,
            m => m.Obstacles.Clear(),
            m => m.Obstacles.Add(new SchematicPlacementBounds { Id = new() { Value = Guid.NewGuid().ToString("D") } })
        ];
        foreach (var corrupt in corruptions)
        {
            var state = Fixture(); var before = state.DesiredFileBytes.ToArray();
            await Assert.ThrowsAsync<AutomationException>(() => Propose(state, corrupt));
            CollectionAssert.AreEqual(before, state.DesiredFileBytes);
        }
    }

    [TestMethod]
    public async Task NoSpaceOrMissingPageReservationsReturnsNoPartialApplicableLayout()
    {
        var state = Fixture(); var regions = Regions(state);
        await Assert.ThrowsAsync<AutomationException>(() => Propose(state, regions: []));
        var small = regions.Select(r => r with { UsableBounds = new(0, 0, 500, 500), Reservations = [] }).ToArray();
        var result = await Propose(state, regions: small);
        Assert.IsFalse(result.CanPropose); Assert.IsNull(result.DesiredXml); Assert.IsNull(result.Refinement);
        Assert.IsNull(result.Layout.Placements); Assert.IsNotEmpty(result.Layout.Issues);
        await Assert.ThrowsAsync<AutomationException>(() => Propose(state,
            regions: regions.Select(r => r with { UsableBounds = new(-100, 0, 1_000_000, 1_000_000) }).ToArray()));
    }

    [TestMethod]
    public async Task ChangedNativeCheckpointAndConflictingXmlAreRejectedBeforeMeasurement()
    {
        var state = Fixture(); int calls = 0;
        Task<SchematicPlacementGeometry> Measure(MeasureSchematicPlacement request, CancellationToken token)
        { calls++; return Task.FromResult(Measurement(state, request, 7_000_000)); }
        await Assert.ThrowsAsync<AutomationException>(() => SchematicInitialLayoutPlanner.ProposeMeasuredAsync(
            state with { NativeRevision = new(state.NativeRevision.Epoch, state.NativeRevision.Sequence + 1) }, Policy,
            Regions(state), "", Measure));
        await Assert.ThrowsAsync<AutomationException>(() => SchematicInitialLayoutPlanner.ProposeMeasuredAsync(
            state with { PendingMutation = new() }, Policy, Regions(state), "", Measure));
        var observed = state.Observed.Clone(); observed.Instances[0].Metadata.TitleBlock.Title = "Concurrent edit";
        var electrical = state.ObservedElectrical!.Clone(); electrical.Hierarchy.Data = observed.Clone();
        await Assert.ThrowsAsync<AutomationException>(() => SchematicInitialLayoutPlanner.ProposeMeasuredAsync(
            state with { Observed = observed, ObservedElectrical = electrical }, Policy, Regions(state), "", Measure));

        // A new unit of an existing component beside a new component leaves that component's definition only
        // partly supplied. The planner computes no identities for it and reports the ownership refusal that
        // synchronization gives the same record, before measuring anything. The code is fixed, not taken from
        // Plan, so a regression that made Plan ask for layout could not make layout refuse circularly.
        // The saved design draws only unit 1 of both channel components: their shared unit-2 symbol is absent
        // from KiCad and the model alike, and the drawn unit declares the undrawn unit's pins, so the record is
        // aligned (a clean no-op plan) and creation's own checks decide.
        var basis = SchematicSynchronizationPlanTests.Fixture();
        var circuit = basis.Baseline.Engineering.Circuit;
        var undrawn = circuit.Symbols.Where(s => s.Unit == 2).OrderBy(s => s.Id).ToArray();
        var removedSymbols = basis.Baseline.SymbolBindings.Where(b => undrawn.Any(s => s.Id == b.SymbolOccurrenceId))
            .Select(b => b.NativeObjectId.ToString("D")).ToHashSet(StringComparer.Ordinal);
        Assert.HasCount(1, removedSymbols, "The repeated channels share one unit-2 symbol.");
        var channelDefinition = circuit.Components.Single(c => c.Id == undrawn[0].ComponentId).DefinitionId;
        var channelPart = circuit.Parts.Single(p => p.Id == circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == channelDefinition).PartId);
        var unitTwoPins = channelPart.Pins.Where(p => p.Unit == 2).Select(p => p.Number).ToHashSet(StringComparer.Ordinal);
        Assert.IsNotEmpty(unitTwoPins);
        var declaredIds = new Dictionary<(string Symbol, string Pin), string>();
        var undrawnBaseline = basis.Baseline with
        {
            Engineering = basis.Baseline.Engineering with { Circuit = circuit with { Symbols = circuit.Symbols.Where(s => s.Unit != 2).ToArray() } },
            Schematic = Undraw(basis.Baseline.Schematic),
            SymbolBindings = basis.Baseline.SymbolBindings.Where(b => undrawn.All(s => s.Id != b.SymbolOccurrenceId)).ToArray()
        };
        var partial = basis with { Baseline = undrawnBaseline, Observed = Undraw(basis.Observed),
            BaselineElectrical = UndrawElectrical(basis.BaselineElectrical!), ObservedElectrical = UndrawElectrical(basis.ObservedElectrical!),
            DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(undrawnBaseline, basis.KnowledgeLibraries)) };
        var aligned = SchematicSynchronizationPlanner.Plan(partial);
        Assert.IsTrue(aligned.CanPrepare, aligned.ErrorCode + ": " + aligned.ErrorMessage);
        Assert.IsEmpty(aligned.NativeOperations);
        var creation = SchematicNativeCreationProjectionTests.AddComponent(partial.Baseline, coordinateFree: true);
        var redrawn = SchematicNetReconciliationTests.Desired(partial, creation with { Circuit = creation.Circuit with
            { Symbols = [.. creation.Circuit.Symbols, undrawn[0] with { Placement = null }] } });
        Assert.IsNotEmpty(SchematicNativeCreationProjection.OmittedOccurrences(DesignRecoveryStore.ReadDesired(redrawn).Engineering.Circuit,
            [.. DesignRecoveryStore.ReadDesired(redrawn).Engineering.Circuit.Symbols.Where(x => !partial.Baseline.Engineering.Circuit.Symbols.Any(o => o.Id == x.Id))]));
        var synchronization = SchematicSynchronizationPlanner.Plan(redrawn);
        Assert.AreEqual(SchematicConnectionErrors.SymbolOwnerRequiresResolution, synchronization.ErrorCode, synchronization.ErrorMessage);
        var refused = await Assert.ThrowsAsync<AutomationException>(() => SchematicInitialLayoutPlanner.ProposeMeasuredAsync(
            redrawn, Policy, Regions(redrawn), "", Measure));
        Assert.AreEqual(SchematicConnectionErrors.SymbolOwnerRequiresResolution, refused.Code, refused.Message);
        Assert.AreEqual(0, calls);

        // Remove the unit-2 symbol and let each remaining unit declare the undrawn unit's pins as inactive
        // children (their own stable identities, the library pin identities of the removed unit), as KiCad does.
        SchematicHierarchyData Undraw(SchematicHierarchyData data)
        {
            var result = data.Clone();
            foreach (var screen in result.Instances)
            {
                var symbols = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()).ToArray();
                var templates = symbols.Where(symbol => removedSymbols.Contains(symbol.Id.Value))
                    .SelectMany(symbol => symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)).Select(c => c.Item.Unpack<SchematicPin>()))
                    .Where(pin => unitTwoPins.Contains(pin.Number)).GroupBy(pin => pin.Number).ToDictionary(g => g.Key, g => g.First());
                if (!symbols.Any(symbol => removedSymbols.Contains(symbol.Id.Value))) continue;
                Assert.IsTrue(unitTwoPins.SetEquals(templates.Keys), "Every channel sheet instance shows the whole shared unit-2 symbol.");
                for (int i = screen.Items.Count - 1; i >= 0; i--)
                {
                    if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
                    var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
                    if (removedSymbols.Contains(symbol.Id.Value)) { screen.Items.RemoveAt(i); continue; }
                    foreach (var number in unitTwoPins.Order(StringComparer.Ordinal))
                    {
                        var pin = templates[number].Clone();
                        if (!declaredIds.TryGetValue((symbol.Id.Value, number), out var id)) declaredIds.Add((symbol.Id.Value, number), id = Guid.NewGuid().ToString("D"));
                        pin.Id = new() { Value = id };
                        symbol.Definition.Items.Add(new SchematicSymbolChild { Unit = new() { Unit = 2 }, Item = Google.Protobuf.WellKnownTypes.Any.Pack(pin) });
                    }
                    screen.Items[i] = Google.Protobuf.WellKnownTypes.Any.Pack(symbol);
                }
            }
            return result;
        }

        // The removed symbol's placed pins also leave KiCad's nets on each sheet instance.
        SchematicElectricalState UndrawElectrical(SchematicElectricalState electrical)
        {
            var result = electrical.Clone();
            foreach (var screen in result.Hierarchy.Data.Instances)
            {
                var pins = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                    .Where(symbol => removedSymbols.Contains(symbol.Id.Value))
                    .SelectMany(symbol => symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)).Select(c => c.Item.Unpack<SchematicPin>().Id.Value))
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var sheet in result.Nets.SelectMany(n => n.Sheets).Where(sheet => sheet.Path.Equals(screen.Metadata.Document.SheetPath)))
                    foreach (var pin in sheet.Items.Where(p => pins.Contains(p.Value)).ToArray()) sheet.Items.Remove(pin);
            }
            result.Hierarchy.Data = Undraw(result.Hierarchy.Data);
            return result;
        }
    }

    [TestMethod]
    public async Task CancellationAfterMeasurementDoesNotPublishAnXmlCandidate()
    {
        var state = Fixture(); using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => SchematicInitialLayoutPlanner.ProposeMeasuredAsync(
            state, Policy, Regions(state), "", (request, token) =>
            {
                var value = Measurement(state, request, 7_000_000);
                cancellation.Cancel(); return Task.FromResult(value);
            }, cancellation.Token));
        Assert.IsTrue(DesignRecoveryStore.ReadDesired(state).Engineering.Circuit.Symbols.Any(s => s.Placement is null));
    }

    // ---- connection-aware placement (cn1-wiring-intent.md §10) ----
    // Why unit tests beside the live journey: the NativeXmlComponentCreation journey proves the same rules on the PSU/CPU
    // fixture in a live editor, but only a synthetic measurement can put a power symbol's pin, a blocked seat or a
    // missing rotation exactly where each rule decides; every must-catch case here has its false-positive guard.

    private const long ConnectionGrid = 1_270_000;

    // An existing IC U1 and a template R9 on the root; the XML adds R1, coordinate-free, joining U1.1 (SIG) and U1.2 (OUT).
    private static (Bench Bench, DesignRecoveryState Saved, Guid U1, Guid R1) Signals(bool coordinateFree = true)
    {
        var bench = new Bench();
        Guid ic = bench.Part("IC", Passive("1"), Passive("2"), Passive("3"));
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid u1 = bench.Component(ic, "U1");
        bench.Component(r, "R9");
        var state = SchematicConnectionRealizerTests.WithFormatting(bench.State([]));
        var (design, r1) = bench.Create(state.Baseline, r, "R1", BenchSheet.Root);
        if (coordinateFree) design = Unplaced(design, r1);
        design = WithNets(design, new CircuitNet(Guid.NewGuid(), "SIG", [new(u1, "1"), new(r1, "1")]),
            new CircuitNet(Guid.NewGuid(), "OUT", [new(u1, "2"), new(r1, "2")]));
        var (saved, _) = Revise(state, _ => design);
        return (bench, saved, u1, r1);
    }

    private static SchematicDesign Unplaced(SchematicDesign design, Guid component) => design with { Engineering = design.Engineering with
        { Circuit = design.Engineering.Circuit with { Symbols = [.. design.Engineering.Circuit.Symbols
            .Select(s => s.ComponentId == component ? s with { Placement = null } : s)] } } };

    private static SchematicLayoutRegion[] Root(Bench bench) =>
        [new(bench.ScreenId(BenchSheet.Root), new(0, 0, 297_000_000, 210_000_000), [])];

    private static SchematicConnectionRealizerTests.Scene Measuring(DesignRecoveryState saved, SchematicConnectionRealizerTests.Geometry geometry) =>
        new(null, saved, d => d, saved, null!, SchematicConnectionRealizerTests.Checkpoint(saved), geometry);

    private static Task<SchematicInitialLayoutResult> ProposeConnected(DesignRecoveryState saved, SchematicLayoutRegion[] regions,
        SchematicConnectionRealizerTests.Geometry geometry, AutomationSession? session = null) =>
        SchematicInitialLayoutPlanner.ProposeMeasuredAsync(saved, Policy, regions, "Keep connections short.",
            (request, token) => geometry.Measure(Measuring(saved, geometry), request, token), session ?? Realizing(saved));

    [TestMethod]
    public async Task ConnectedAdditionsNeedAnEditorThatCanDrawTheirConnections()
    {
        var (bench, saved, _, _) = Signals();
        var geometry = new SchematicConnectionRealizerTests.Geometry();
        var plain = new AutomationSession { InstanceId = saved.InstanceId.ToString("D") };
        var refused = await Assert.ThrowsAsync<AutomationException>(() => ProposeConnected(saved, Root(bench), geometry, plain));
        Assert.AreEqual("unsupported_layout_creation", refused.Code);
        StringAssert.Contains(refused.Message, "draw and verify XML connections");
        var foreign = Realizing(saved); foreign.InstanceId = Guid.NewGuid().ToString("D");
        Assert.AreEqual("unsupported_layout_creation", (await Assert.ThrowsAsync<AutomationException>(() => ProposeConnected(saved, Root(bench), geometry, foreign))).Code,
            "Another instance's capability is no evidence for this one.");
        Assert.IsEmpty(geometry.Requests, "Nothing is measured before admission.");
        // Guard: the same revision without its connections is an ordinary addition, laid out with or without the capability.
        var unconnected = saved with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(
            WithNets(DesignRecoveryStore.ReadDesired(saved)), saved.KnowledgeLibraries)) };
        var ordinary = await ProposeConnected(unconnected, Root(bench), new(), plain);
        Assert.IsTrue(ordinary.CanPropose);
        Assert.IsEmpty(ordinary.PreferredAnchors); Assert.IsEmpty(ordinary.PowerAttachments);
        Assert.AreEqual(ordinary.DesiredXml, (await ProposeConnected(unconnected, Root(bench), new())).DesiredXml,
            "An unconnected addition is laid out exactly as before, whatever the editor advertises.");
    }

    [TestMethod]
    public async Task ANewSymbolIsAimedAtItsPartnersAndEveryConnectionKeepsItsShortestStub()
    {
        var (bench, saved, u1, r1) = Signals();
        var geometry = new SchematicConnectionRealizerTests.Geometry();
        var result = await ProposeConnected(saved, Root(bench), geometry);
        Assert.IsTrue(result.CanPropose, string.Join("; ", result.Layout.Issues.Select(i => i.Code)));
        var design = result.DesiredDesign!;
        var baseline = saved.Baseline.Engineering.Circuit.Symbols.ToDictionary(s => s.Id);
        Assert.IsTrue(design.Engineering.Circuit.Symbols.Where(s => baseline.ContainsKey(s.Id)).All(s => baseline[s.Id] == s), "Existing symbols stay.");
        // §10: snap(mean partner anchor − mean own member-pin offset). The bench draws every pin on the symbol's left edge,
        // two grids apart: U1.1 and U1.2 sit at U1 − (4, 2) and U1 − (4, 0) grids, R1's pins at R1 − (4, ±1) grids.
        var u1Symbol = saved.Baseline.Schematic.Instances[0].Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.ReferenceField.Text.Text_ == "U1");
        var aim = result.PreferredAnchors.Single();
        static long Snap(long value) => (long)(decimal.Round((decimal)value / Policy.GridNm, MidpointRounding.AwayFromZero) * Policy.GridNm);
        Assert.AreEqual(new PresentationPoint(Snap(u1Symbol.Position.XNm), Snap(u1Symbol.Position.YNm - ConnectionGrid)), aim.Anchor,
            "The mean partner pin minus the mean own pin offset, snapped to the layout grid.");
        CollectionAssert.AreEquivalent(new[] { bench.PinId(u1, "1"), bench.PinId(u1, "2") }, aim.PartnerPins.ToArray());
        // The aimed spot is U1 itself, so R1 goes to the nearest free place that also keeps U1's stubs and labels clear.
        var placed = result.Layout.Placements!.Single();
        Assert.AreEqual(aim.BodyId, placed.BodyId);
        long off = Math.Abs(placed.Anchor.XNm - aim.Anchor.XNm) + Math.Abs(placed.Anchor.YNm - aim.Anchor.YNm);
        Assert.IsTrue(off > 0 && off <= 40 * ConnectionGrid, "R1 lands next to U1, " + off + " nm from the aimed spot.");
        Assert.AreEqual(result.DesiredXml, (await ProposeConnected(saved, Root(bench), new())).DesiredXml, "Deterministic.");

        // The proposal realizes: every stub, from R1 and from U1, has its shortest length, and R1's reserved room held it.
        var scene = SchematicConnectionRealizerTests.Scene.Of(null, saved, _ => design);
        var realization = await scene.Realize();
        var wires = realization.Design.Schematic.Instances.SelectMany(s => s.Items).Where(i => i.Is(SchematicLine.Descriptor))
            .Select(i => i.Unpack<SchematicLine>()).ToDictionary(l => Guid.Parse(l.Id.Value));
        var stubs = realization.Generated.Where(g => g.Role == GeneratedConnectionRole.StubWire).ToArray();
        Assert.HasCount(4, stubs);
        Assert.IsTrue(stubs.All(g => Math.Abs(wires[g.Id].End.XNm - wires[g.Id].Start.XNm) == 2 * ConnectionGrid && wires[g.Id].End.YNm == wires[g.Id].Start.YNm));
        // R1's occupied bounds reach past its pins (4 grids left of its anchor) by the 2-grid stub and the 3-grid label the
        // bench measures for "SIG" and "OUT", which the layout asked the editor to measure as label prototypes.
        Assert.IsTrue(placed.Bounds.LeftNm <= placed.Anchor.XNm - 9 * ConnectionGrid, "R1 reserves its stubs and labels left of its pins.");
        Assert.IsTrue(geometry.Requests.Any(r => r.ItemCandidates.Count == 2 && r.Candidates.Count == 0), "Both labels were measured.");

        // U1's own stubs keep their room too: R1 covers neither stub end.
        foreach (var pin in new[] { "1", "2" })
        {
            var wire = wires[stubs.Single(g => g.PlacedPinId == bench.PinId(u1, pin)).Id];
            Assert.IsFalse(placed.Bounds.LeftNm < wire.End.XNm && wire.End.XNm < placed.Bounds.RightNm
                && placed.Bounds.TopNm < wire.End.YNm && wire.End.YNm < placed.Bounds.BottomNm, "U1." + pin + " keeps its stub room.");
        }
    }

    [TestMethod]
    public async Task AnExplicitlyPlacedConnectedSymbolKeepsItsPositionAndBecomesAPartner()
    {
        var (bench, saved, _, r1) = Signals();
        // R2, explicitly placed and locked, joins OUT; R1 stays coordinate-free and is also aimed at R2.2.
        Guid r = DesignRecoveryStore.ReadDesired(saved).Engineering.Circuit.Parts.Single(p => p.Name == "R").Id;
        var pinned = new SymbolPlacement(200, 150, 0, false, false, true);
        var desired = DesignRecoveryStore.ReadDesired(saved);
        var (withR2, r2) = bench.Create(desired, r, "R2", BenchSheet.Root);
        withR2 = withR2 with { Engineering = withR2.Engineering with { Circuit = withR2.Engineering.Circuit with
        {
            Symbols = [.. withR2.Engineering.Circuit.Symbols.Select(s => s.ComponentId == r2 ? s with { Placement = pinned } : s)],
            Nets = [.. withR2.Engineering.Circuit.Nets.Select(n => n.Name == "OUT" ? n with { Pins = [.. n.Pins, new(r2, "2")] } : n)]
        } } };
        var revision = saved with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(withR2, saved.KnowledgeLibraries)) };
        var result = await ProposeConnected(revision, Root(bench), new());
        Assert.IsTrue(result.CanPropose, string.Join("; ", result.Layout.Issues.Select(i => i.Code)));
        Assert.AreEqual(pinned, result.DesiredDesign!.Engineering.Circuit.Symbols.Single(s => s.ComponentId == r2).Placement);
        Assert.IsTrue(result.Layout.Placements!.Single(p => p.Fixed).Bounds.RightNm > 0);
        Assert.HasCount(1, result.PreferredAnchors, "Only coordinate-free R1 is aimed.");
        var r2Pin = Keys(SchematicConnectionIntentBuilderTests.Plan(revision with { DesiredFileBytes = Encoding.UTF8.GetBytes(result.DesiredXml!) }).Candidate!,
            (r2, "2")).Single().PlacedPinId;
        CollectionAssert.Contains(result.PreferredAnchors.Single().PartnerPins.ToArray(), r2Pin, "The explicitly placed R2 is one of R1's partners.");
        // Must-catch: a pinned position whose own stub room leaves the page is refused without a partial candidate.
        var edge = revision with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(withR2 with { Engineering = withR2.Engineering with
            { Circuit = withR2.Engineering.Circuit with { Symbols = [.. withR2.Engineering.Circuit.Symbols.Select(s => s.ComponentId == r2
                ? s with { Placement = pinned with { XMillimeters = 6.35m } } : s)] } } }, saved.KnowledgeLibraries)) };
        var overflow = await ProposeConnected(edge, Root(bench), new());
        Assert.IsFalse(overflow.CanPropose); Assert.IsNull(overflow.DesiredXml); Assert.IsNull(overflow.Layout.Placements);
        Assert.AreEqual("pinned_page_overflow", overflow.Layout.Issues.Single().Code);
    }

    [TestMethod]
    public async Task NoSpaceForAConnectedAdditionReturnsNoPartialCandidate()
    {
        var (bench, saved, _, _) = Signals();
        // Narrower than R1 with the room its stubs and labels need.
        var small = new SchematicLayoutRegion[] { new(bench.ScreenId(BenchSheet.Root), new(0, 0, 15_000_000, 40_000_000), []) };
        var result = await ProposeConnected(saved, small, new());
        Assert.IsFalse(result.CanPropose); Assert.IsNull(result.DesiredXml); Assert.IsNull(result.Refinement);
        Assert.IsNull(result.Layout.Placements); Assert.AreEqual("no_free_region", result.Layout.Issues.Single().Code);
        Assert.IsEmpty(result.PowerAttachments);
    }

    // #PWR01 (global GND) is in GND; the XML adds TP1 and #PWR02, both coordinate-free, to GND. TP9 is the test-point template.
    private static (Bench Bench, DesignRecoveryState Saved, Guid Tp1, Guid Pwr2) Power()
    {
        var bench = new Bench();
        Guid gnd = bench.Part("GND", SchematicSymbolType.SstGlobalPower, new BenchPin("1", "GND", 1, ElectricalPinType.EptPowerInput, false));
        Guid tp = bench.Part("TP", Passive("1"));
        Guid pwr1 = bench.Component(gnd, "#PWR01", value: "GND");
        bench.Component(tp, "TP9");
        var ground = new CircuitNet(Guid.NewGuid(), "GND", [new(pwr1, "1")]);
        var state = SchematicConnectionRealizerTests.WithFormatting(bench.State([ground]));
        var (design, tp1) = bench.Create(state.Baseline, tp, "TP1", BenchSheet.Root);
        (design, var pwr2) = bench.Create(design, gnd, "#PWR02", BenchSheet.Root, value: "GND");
        design = WithNets(Unplaced(Unplaced(design, tp1), pwr2), ground with { Pins = [.. ground.Pins, new(tp1, "1"), new(pwr2, "1")] });
        var (saved, _) = Revise(state, _ => design);
        return (bench, saved, tp1, pwr2);
    }

    [TestMethod]
    public async Task ANewPowerSymbolIsTurnedAndSeatedOnItsPartnerStubEnd()
    {
        var (bench, saved, tp1, pwr2) = Power();
        var result = await ProposeConnected(saved, Root(bench), new());
        Assert.IsTrue(result.CanPropose, string.Join("; ", result.Layout.Issues.Select(i => i.Code)));
        var attachment = result.PowerAttachments.Single();
        Assert.IsTrue(attachment.Attached, attachment.DroppedReason);
        Assert.AreEqual(new PinEndpoint(tp1, "1"), attachment.Partner);
        Assert.AreEqual(pwr2, attachment.CarrierComponentId);
        // The bench draws a pin on the symbol's left edge facing into the body (+x): only a half turn makes it face the
        // stub arriving from TP1.1's left.
        Assert.AreEqual(180, attachment.RotationDegrees);
        var placement = result.DesiredDesign!.Engineering.Circuit.Symbols.Single(s => s.ComponentId == pwr2).Placement!;
        Assert.AreEqual(180, placement.RotationDegrees);
        Assert.AreEqual(attachment.Anchor, new PresentationPoint(Coordinates.MillimetersToNanometers(placement.XMillimeters),
            Coordinates.MillimetersToNanometers(placement.YMillimeters)));
        // Realized, TP1.1's shortest stub ends on #PWR02's pin: a wire with no label.
        var scene = SchematicConnectionRealizerTests.Scene.Of(null, saved, _ => result.DesiredDesign!);
        var realization = await scene.Realize();
        var outcome = realization.Outcomes.Single();
        Assert.IsTrue(outcome.AttachedCarrier, "The stub attaches to the seated power symbol.");
        Assert.IsFalse(realization.Generated.Any(g => g.Role == GeneratedConnectionRole.StubLabel));
        Assert.AreEqual(result.DesiredXml, (await ProposeConnected(saved, Root(bench), new())).DesiredXml, "Deterministic.");
    }

    [TestMethod]
    public async Task APowerSymbolPairingIsDroppedWhenNoTurnFacesTheStubOrItsSeatIsTaken()
    {
        var (bench, saved, tp1, pwr2) = Power();
        // The created pin identities do not depend on where the symbols go; take them from the seated proposal's plan.
        var seated = await ProposeConnected(saved, Root(bench), new());
        var pin = Keys(SchematicConnectionIntentBuilderTests.Plan(saved with { DesiredFileBytes = Encoding.UTF8.GetBytes(seated.DesiredXml!) }).Candidate!,
            (pwr2, "1")).Single().PlacedPinId;
        Assert.IsTrue(seated.PowerAttachments.Single().Attached, "Guard: with its real geometry the power symbol is seated.");
        // Must-catch 1: a power symbol whose pin faces up in every turn cannot meet a stub arriving from the side.
        var upright = new SchematicConnectionRealizerTests.Geometry(); upright.Direction[pin] = (0, 1);
        var turned = await ProposeConnected(saved, Root(bench), upright);
        Assert.IsTrue(turned.CanPropose);
        var dropped = turned.PowerAttachments.Single();
        Assert.IsFalse(dropped.Attached); Assert.AreEqual("no_matching_rotation", dropped.DroppedReason);
        Assert.AreEqual(0, turned.DesiredDesign!.Engineering.Circuit.Symbols.Single(s => s.ComponentId == pwr2).Placement!.RotationDegrees,
            "A dropped pairing keeps the free placement.");
        // Must-catch 2: a turned power symbol measured far wider than its seat, reaching past its pin over TP1, is left
        // where it was (still inside the page, so only the overlap refuses it).
        var large = new SchematicConnectionRealizerTests.Geometry
        {
            Tamper = (request, reply) =>
            {
                foreach (var candidate in reply.Candidates)
                    if (request.Candidates.Single(c => c.Id.Equals(candidate.Id)).Transform?.Orientation is SchematicSymbolOrientation.Sso180)
                        candidate.Bounds.Size.XNm += 30 * ConnectionGrid;
                return reply;
            }
        };
        var blocked = await ProposeConnected(saved, Root(bench), large);
        Assert.IsTrue(blocked.CanPropose);
        var collision = blocked.PowerAttachments.Single();
        Assert.IsFalse(collision.Attached); Assert.AreEqual("collision", collision.DroppedReason);
        Assert.AreEqual(180, collision.RotationDegrees);
        Assert.AreEqual(0, blocked.DesiredDesign!.Engineering.Circuit.Symbols.Single(s => s.ComponentId == pwr2).Placement!.RotationDegrees,
            "A dropped pairing keeps the free placement.");
        Assert.AreEqual(new PinEndpoint(tp1, "1"), collision.Partner);
    }

    private static DesignRecoveryState Fixture()
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        return SchematicNetReconciliationTests.Desired(state,
            SchematicNativeCreationProjectionTests.AddComponent(state.Baseline, coordinateFree: true));
    }

    private static SchematicLayoutRegion[] Regions(DesignRecoveryState state)
    {
        var wanted = DesignRecoveryStore.ReadDesired(state).Engineering.Circuit;
        var components = wanted.Components.ToDictionary(c => c.Id);
        var affected = wanted.Symbols.Where(s => !state.Baseline.Engineering.Circuit.Symbols.Any(old => old.Id == s.Id))
            .Select(s => s.EffectiveSheetInstanceId(components[s.ComponentId])).ToHashSet();
        var paths = state.Baseline.SheetBindings.Where(b => affected.Contains(b.SheetInstanceId))
            .Select(b => SchematicDesignBindings.PathKey(b.NativePath)).ToHashSet();
        return state.Baseline.Schematic.Instances.Where(s => paths.Contains(Path(s.Metadata.Document)))
            .Select(s => Guid.Parse(s.Metadata.ScreenId.Value)).Distinct()
            .Select(id => new SchematicLayoutRegion(id, new(0, 0, 300_000_000, 200_000_000),
                [new(Guid.Parse("d0aef585-dddf-441f-9480-2765f53106fb"), new(250_000_000, 150_000_000, 300_000_000, 200_000_000))])).ToArray();
    }

    private static Task<SchematicInitialLayoutResult> Propose(DesignRecoveryState state,
        Action<SchematicPlacementGeometry>? corrupt = null, SchematicLayoutRegion[]? regions = null)
    {
        int calls = 0;
        return SchematicInitialLayoutPlanner.ProposeMeasuredAsync(state, Policy, regions ?? Regions(state),
            "Keep the new group together.", (request, token) =>
            {
                var result = Measurement(state, request, ++calls == 1 ? 3_000_000 : 7_000_000);
                corrupt?.Invoke(result); return Task.FromResult(result);
            });
    }

    // Explicitly synthetic native geometry: these tests qualify the adapter's
    // admission/union logic; the rendered native journey proves actual bounds.
    private static SchematicPlacementGeometry Measurement(DesignRecoveryState state, MeasureSchematicPlacement request, long width)
    {
        var screen = state.Baseline.Schematic.Instances.Single(s => Path(s.Metadata.Document) == Path(request.Document));
        var result = new SchematicPlacementGeometry { Document = request.Document.Clone(), Revision = request.ExpectedRevision.Clone(),
            ScreenId = screen.Metadata.ScreenId.Clone(), PageBounds = new() { Position = new(), Size = new() { XNm = 300_000_000, YNm = 200_000_000 } } };
        foreach (Guid id in SchematicItemDelta.Index(screen.Items).Where(p => p.Value is not Group).Select(p => p.Key))
            result.Obstacles.Add(new SchematicPlacementBounds { Id = new() { Value = id.ToString("D") }, Anchor = new() { XNm = 10_000_000, YNm = 20_000_000 },
                Bounds = new() { Position = new() { XNm = 8_000_000, YNm = 18_000_000 }, Size = new() { XNm = 5_000_000, YNm = 5_000_000 } } });
        foreach (var symbol in request.Candidates)
            result.Candidates.Add(new SchematicPlacementBounds { Id = symbol.Id.Clone(), Anchor = symbol.Position.Clone(), Bounds = new()
            { Position = new() { XNm = symbol.Position.XNm - 1_000_000, YNm = symbol.Position.YNm - 2_000_000 },
                Size = new() { XNm = width, YNm = 5_000_000 } } });
        result.Limitations.Add("Synthetic adapter fixture, not native rendered evidence.");
        return result;
    }

    private static string Path(DocumentSpecifier document) => string.Join('/', document.SheetPath.Path.Select(p => p.Value));
}
