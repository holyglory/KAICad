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
    // fixture in a live editor, but that fixture has no power symbols or repeated sheets, and a live editor never answers with
    // a defective reply. Only a synthetic measurement can put a power symbol's pin, a blocked seat, a missing rotation, a second
    // sheet instance, a sheet pin's or join's room or a corrupted reply exactly where each rule decides; every must-catch case
    // here has its false-positive guard.

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
        // Must-catch: an editor that can draw connections still gets the refusal, with the actual reason. Here the XML also takes
        // R8.1 out of the existing connection LINK, which XML cannot do.
        var cut = new Bench();
        Guid cr = cut.Part("R", Passive("1"), Passive("2"));
        Guid r9 = cut.Component(cr, "R9"), r8 = cut.Component(cr, "R8");
        var link = new CircuitNet(Guid.NewGuid(), "LINK", [new(r9, "1"), new(r8, "1")]);
        var cutState = SchematicConnectionRealizerTests.WithFormatting(cut.State([link], new() { [link.Id] = [(BenchSheet.Root, cut.Wire(BenchSheet.Root))] }));
        var (cutDesign, cutR1) = cut.Create(cutState.Baseline, cr, "R1", BenchSheet.Root);
        var (disconnecting, _) = Revise(cutState, _ => WithNets(Unplaced(cutDesign, cutR1), link with { Pins = [new(r9, "1"), new(cutR1, "1")] }));
        var cutGeometry = new SchematicConnectionRealizerTests.Geometry();
        var reason = await Assert.ThrowsAsync<AutomationException>(() => ProposeConnected(disconnecting, Root(cut), cutGeometry));
        Assert.AreEqual("unsupported_layout_creation", reason.Code);
        StringAssert.Contains(reason.Message, "The saved XML removes pin R8.1 from net 'LINK'.");
        Assert.IsEmpty(cutGeometry.Requests);
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
    // An optional item is drawn on the root sheet at <paramref name="obstacle"/> (measured by <paramref name="geometry"/>).
    private static (Bench Bench, DesignRecoveryState Saved, Guid Tp1, Guid Pwr2) Power(SchematicConnectionRealizerTests.Rect? obstacle = null,
        SchematicConnectionRealizerTests.Geometry? geometry = null)
    {
        var bench = new Bench();
        Guid gnd = bench.Part("GND", SchematicSymbolType.SstGlobalPower, new BenchPin("1", "GND", 1, ElectricalPinType.EptPowerInput, false));
        Guid tp = bench.Part("TP", Passive("1"));
        Guid pwr1 = bench.Component(gnd, "#PWR01", value: "GND");
        bench.Component(tp, "TP9");
        var ground = new CircuitNet(Guid.NewGuid(), "GND", [new(pwr1, "1")]);
        var state = SchematicConnectionRealizerTests.WithFormatting(bench.State([ground]));
        if (obstacle is { } rect)
        {
            var id = Guid.NewGuid();
            state = SchematicConnectionRealizerTests.Edited(state, data => SchematicConnectionRealizerTests.Root(data).Items.Add(
                Google.Protobuf.WellKnownTypes.Any.Pack(new SchematicText { Id = new() { Value = id.ToString("D") }, Locked = LockedState.LsUnlocked,
                    Text = new() { Text_ = "keep out", Position = new() { XNm = rect.L, YNm = rect.T }, Attributes = new() { Multiline = true } } })));
            (geometry ?? throw new ArgumentNullException(nameof(geometry))).Sized[id] = rect;
        }
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

        // An explicitly placed partner keeps its position, and the seat beside it is claimed before the free layout on the sheet
        // itself: TP1.1 is 4 grids left of TP1, the stub takes 2 more, and #PWR02's pin, turned half round, is 4 grids right of
        // its anchor.
        var placedTp1 = new SymbolPlacement(150, 100, 0, false, false, false);
        var wanted = DesignRecoveryStore.ReadDesired(saved);
        var pinnedPartner = saved with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(wanted with { Engineering = wanted.Engineering with
            { Circuit = wanted.Engineering.Circuit with { Symbols = [.. wanted.Engineering.Circuit.Symbols
                .Select(s => s.ComponentId == tp1 ? s with { Placement = placedTp1 } : s)] } } }, saved.KnowledgeLibraries)) };
        var beside = await ProposeConnected(pinnedPartner, Root(bench), new());
        Assert.IsTrue(beside.CanPropose, string.Join("; ", beside.Layout.Issues.Select(i => i.Code)));
        var seat = beside.PowerAttachments.Single();
        Assert.IsTrue(seat.Attached, seat.DroppedReason);
        Assert.AreEqual(180, seat.RotationDegrees);
        Assert.AreEqual(new PresentationPoint(150_000_000 - 10 * ConnectionGrid, 100_000_000), seat.Anchor);
        Assert.AreEqual(placedTp1, beside.DesiredDesign!.Engineering.Circuit.Symbols.Single(s => s.ComponentId == tp1).Placement);
        var besidePlacement = beside.DesiredDesign.Engineering.Circuit.Symbols.Single(s => s.ComponentId == pwr2).Placement!;
        Assert.AreEqual(new SymbolPlacement(137.3m, 100m, 180, false, false, false), besidePlacement);
        var besideRealization = await SchematicConnectionRealizerTests.Scene.Of(null, pinnedPartner, _ => beside.DesiredDesign!).Realize();
        Assert.IsTrue(besideRealization.Outcomes.Single().AttachedCarrier, "The stub attaches to the power symbol seated beside the placed TP1.");
    }

    // Decision n2ad655250c5716f5 rule 3 (review finding 1 of the placement re-review): a seat beside an explicitly placed new symbol
    // joins that symbol's reserved room, and the layout checks that room as one rectangle, like every explicitly placed symbol. With
    // #PWR02 measured 12 grids tall when turned half round (TP1 is 4 grids tall), the rectangle around TP1's room and the seat
    // reaches 4 grids above TP1, where neither TP1's room nor the seat is. An item there, clear of both, used to refuse the whole
    // layout (pinned_obstacle_overlap on TP1); now only the pairing is dropped as a collision and everything is laid out.
    [TestMethod]
    public async Task ASeatThatWouldGrowAnExplicitlyPlacedPartnerOntoAnItemIsDroppedAlone()
    {
        SchematicConnectionRealizerTests.Geometry Tall() => new()
        {
            Tamper = (request, reply) =>
            {
                foreach (var candidate in reply.Candidates)
                    if (request.Candidates.Single(c => c.Id.Equals(candidate.Id)).Transform?.Orientation is SchematicSymbolOrientation.Sso180)
                    {
                        candidate.Bounds.Position.YNm = candidate.Anchor.YNm - 6 * ConnectionGrid;
                        candidate.Bounds.Size.YNm = 12 * ConnectionGrid;
                    }
                return reply;
            }
        };
        var placedTp1 = new SymbolPlacement(150, 100, 0, false, false, false);
        async Task<(SchematicInitialLayoutResult Result, Guid Tp1, Guid Pwr2)> Propose(SchematicConnectionRealizerTests.Rect item)
        {
            var geometry = Tall();
            var (bench, saved, tp1, pwr2) = Power(item, geometry);
            var wanted = DesignRecoveryStore.ReadDesired(saved);
            var pinned = saved with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(wanted with { Engineering = wanted.Engineering with
                { Circuit = wanted.Engineering.Circuit with { Symbols = [.. wanted.Engineering.Circuit.Symbols
                    .Select(s => s.ComponentId == tp1 ? s with { Placement = placedTp1 } : s)] } } }, saved.KnowledgeLibraries)) };
            return (await ProposeConnected(pinned, Root(bench), geometry), tp1, pwr2);
        }
        // TP1's body spans x 144.92..155.08 mm and y 97.46..102.54 mm; its stub and label room lies left of x 146.2 mm. The seat
        // spans x 132.22..142.38 mm and, measured tall, y 92.38..107.62 mm, so the grown room reaches up to y 92.38 mm right across TP1.
        // Guard: the item 3.4 mm above that, outside the grown room by more than the 1.27 mm layout clearance, leaves the seat attached.
        var (clear, _, _) = await Propose(new(149_000_000, 88_000_000, 152_000_000, 89_000_000));
        Assert.IsTrue(clear.CanPropose, string.Join("; ", clear.Layout.Issues.Select(i => i.Code)));
        var seated = clear.PowerAttachments.Single();
        Assert.IsTrue(seated.Attached, seated.DroppedReason);
        Assert.AreEqual(new PresentationPoint(150_000_000 - 10 * ConnectionGrid, 100_000_000), seated.Anchor);
        // Must-catch: the item inside the grown room, 3.46 mm above TP1's body and 6.6 mm right of the seat, clear of both.
        var (blocked, tp1, pwr2) = await Propose(new(149_000_000, 93_000_000, 152_000_000, 94_000_000));
        Assert.IsTrue(blocked.CanPropose, "Only the pairing is dropped, never the layout: " + string.Join("; ", blocked.Layout.Issues.Select(i => i.Code)));
        var dropped = blocked.PowerAttachments.Single();
        Assert.IsFalse(dropped.Attached);
        Assert.AreEqual("collision", dropped.DroppedReason);
        Assert.AreEqual(new PresentationPoint(150_000_000 - 10 * ConnectionGrid, 100_000_000), dropped.Anchor, "The refused seat is reported.");
        Assert.AreEqual(placedTp1, blocked.DesiredDesign!.Engineering.Circuit.Symbols.Single(s => s.ComponentId == tp1).Placement,
            "TP1 keeps its explicit position.");
        Assert.AreEqual(0, blocked.DesiredDesign.Engineering.Circuit.Symbols.Single(s => s.ComponentId == pwr2).Placement!.RotationDegrees,
            "The dropped power symbol is laid out freely, unturned.");
        Assert.HasCount(2, blocked.Layout.Placements!);
    }

    // Decision n2ad655250c5716f5 rule 3 (review finding 3 of the placement re-review): TP1 with #PWR02 on the root sheet and TP2 with
    // #PWR03 on the child sheet, all coordinate-free and joining GND. The root's usable region has room for TP1 (17.78 mm wide with
    // its stub and label) but not for TP1 with #PWR02 seated on it (22.86 mm); the child sheet has room for everything.
    [TestMethod]
    public async Task AFailedFirstLayoutDropsSeatsOnlyOnTheSheetsThatFoundNoRoom()
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
        (design, var tp2) = bench.Create(design, tp, "TP2", BenchSheet.Child);
        (design, var pwr3) = bench.Create(design, gnd, "#PWR03", BenchSheet.Child, value: "GND");
        design = WithNets(Unplaced(Unplaced(Unplaced(Unplaced(design, tp1), pwr2), tp2), pwr3),
            ground with { Pins = [.. ground.Pins, new(tp1, "1"), new(pwr2, "1"), new(tp2, "1"), new(pwr3, "1")] });
        var (saved, _) = Revise(state, _ => design);
        Guid root = bench.ScreenId(BenchSheet.Root), child = bench.ScreenId(BenchSheet.Child);
        SchematicLayoutRegion[] Regions(PresentationBounds rootRegion) =>
            [new(root, rootRegion, []), new(child, new(0, 0, 297_000_000, 210_000_000), [])];

        // Guard: with room on both sheets, both power symbols are seated.
        var roomy = await ProposeConnected(saved, Regions(new(0, 0, 297_000_000, 210_000_000)), new());
        Assert.IsTrue(roomy.CanPropose, string.Join("; ", roomy.Layout.Issues.Select(i => i.Code)));
        Assert.IsTrue(roomy.PowerAttachments.All(a => a.Attached), string.Join(",", roomy.PowerAttachments.Select(a => a.DroppedReason)));

        // Must-catch: the crowded root sheet fails the first layout. Only its seat is dropped (page_overflow); the child sheet, which
        // was laid out, keeps its seat attached, exactly where the roomy layout seated it.
        var crowded = await ProposeConnected(saved, Regions(new(100_000_000, 100_000_000, 121_000_000, 112_000_000)), new());
        Assert.IsTrue(crowded.CanPropose, string.Join("; ", crowded.Layout.Issues.Select(i => i.Code)));
        Assert.HasCount(2, crowded.PowerAttachments);
        var onRoot = crowded.PowerAttachments.Single(a => a.ScreenId == root);
        Assert.AreEqual(pwr2, onRoot.CarrierComponentId);
        Assert.IsFalse(onRoot.Attached); Assert.AreEqual("page_overflow", onRoot.DroppedReason);
        var onChild = crowded.PowerAttachments.Single(a => a.ScreenId == child);
        Assert.AreEqual(pwr3, onChild.CarrierComponentId);
        Assert.AreEqual(new PinEndpoint(tp2, "1"), onChild.Partner);
        Assert.IsTrue(onChild.Attached, "The child sheet found room, so its seat stays: " + onChild.DroppedReason);
        var roomyChild = roomy.PowerAttachments.Single(a => a.ScreenId == child);
        Assert.AreEqual(roomyChild.Anchor, onChild.Anchor, "The child sheet is laid out exactly as with room everywhere.");
        Assert.AreEqual(roomyChild.RotationDegrees, onChild.RotationDegrees);
        var childPlacements = crowded.Layout.Placements!.Where(p => p.SheetId == child).Select(p => (p.BodyId, p.Anchor)).ToArray();
        CollectionAssert.AreEqual(roomy.Layout.Placements!.Where(p => p.SheetId == child).Select(p => (p.BodyId, p.Anchor)).ToArray(), childPlacements);
        Assert.AreEqual(180, crowded.DesiredDesign!.Engineering.Circuit.Symbols.Single(s => s.ComponentId == pwr3).Placement!.RotationDegrees);
        Assert.AreEqual(0, crowded.DesiredDesign.Engineering.Circuit.Symbols.Single(s => s.ComponentId == pwr2).Placement!.RotationDegrees);
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

        // Must-catch 3: a region with room for TP1 (17.78 mm wide with its stub and label) but not for TP1 with #PWR02 seated on its
        // stub (22.86 mm): rather than refusing the layout, the seat is dropped and both symbols are laid out freely.
        var tight = new SchematicLayoutRegion[] { new(bench.ScreenId(BenchSheet.Root), new(100_000_000, 100_000_000, 121_000_000, 112_000_000), []) };
        var crowded = await ProposeConnected(saved, tight, new());
        Assert.IsTrue(crowded.CanPropose, string.Join("; ", crowded.Layout.Issues.Select(i => i.Code)));
        var overflow = crowded.PowerAttachments.Single();
        Assert.IsFalse(overflow.Attached); Assert.AreEqual("page_overflow", overflow.DroppedReason);
        Assert.AreEqual(180, overflow.RotationDegrees);
        var tp1At = crowded.Layout.Placements!.Single(p => p.BodyId != overflow.CarrierBodyId).Anchor;
        Assert.AreEqual(new PresentationPoint(tp1At.XNm - 10 * ConnectionGrid, tp1At.YNm), overflow.Anchor, "The refused seat beside TP1 is reported.");
        Assert.AreEqual(0, crowded.DesiredDesign!.Engineering.Circuit.Symbols.Single(s => s.ComponentId == pwr2).Placement!.RotationDegrees);
        Assert.HasCount(2, crowded.Layout.Placements!);
    }

    // #PWR01 (global GND) is in GND and TP9 sits right of it; TP9's pin is moved to the right edge of its body and faces away
    // from it. The XML adds TP9.1, a coordinate-free TP1 and a coordinate-free #PWR02 to GND. TP9 has the smallest component
    // identity, so #PWR02 is paired with TP9.1. An optional item is drawn at <paramref name="obstacle"/>.
    private static (Bench Bench, DesignRecoveryState Saved, SchematicConnectionRealizerTests.Geometry Geometry, Guid Tp9, Guid Tp1, Guid Pwr2)
        BesideAnExistingPin(SchematicConnectionRealizerTests.Rect? obstacle = null)
    {
        var bench = new Bench();
        Guid gnd = bench.Part("GND", SchematicSymbolType.SstGlobalPower, new BenchPin("1", "GND", 1, ElectricalPinType.EptPowerInput, false));
        Guid tp = bench.Part("TP", Passive("1"));
        Guid pwr1 = bench.Component(gnd, "#PWR01", value: "GND");
        Guid tp9 = bench.Component(tp, "TP9", id: Guid.Parse("00000000-0000-4000-8000-000000000001"));
        var ground = new CircuitNet(Guid.NewGuid(), "GND", [new(pwr1, "1")]);
        var state = SchematicConnectionRealizerTests.WithFormatting(bench.State([ground]));
        var geometry = new SchematicConnectionRealizerTests.Geometry();
        if (obstacle is { } rect)
        {
            var id = Guid.NewGuid();
            state = SchematicConnectionRealizerTests.Edited(state, data => SchematicConnectionRealizerTests.Root(data).Items.Add(
                Google.Protobuf.WellKnownTypes.Any.Pack(new SchematicText { Id = new() { Value = id.ToString("D") }, Locked = LockedState.LsUnlocked,
                    Text = new() { Text_ = "keep out", Position = new() { XNm = rect.L, YNm = rect.T }, Attributes = new() { Multiline = true } } })));
            geometry.Sized[id] = rect;
        }
        var (design, tp1) = bench.Create(state.Baseline, tp, "TP1", BenchSheet.Root);
        (design, var pwr2) = bench.Create(design, gnd, "#PWR02", BenchSheet.Root, value: "GND");
        design = WithNets(Unplaced(Unplaced(design, tp1), pwr2),
            ground with { Pins = [.. ground.Pins, new(tp9, "1"), new(tp1, "1"), new(pwr2, "1")] });
        var (saved, _) = Revise(state, _ => design);
        // TP9 is drawn 8 grids wide around x = 40 mm, y = 20 mm.
        var pin = bench.PinId(tp9, "1");
        geometry.Place[pin] = new(40_000_000 + 4 * ConnectionGrid, 20_000_000);
        geometry.Direction[pin] = (-1, 0);
        return (bench, saved, geometry, tp9, tp1, pwr2);
    }

    // A band of the root sheet 10 mm tall around the row of existing symbols: every new symbol must go beside them in that row.
    private static SchematicLayoutRegion[] Strip(Bench bench, long top = 15_000_000, long bottom = 25_000_000) =>
        [new(bench.ScreenId(BenchSheet.Root), new(0, top, 297_000_000, bottom), [])];

    [TestMethod]
    public async Task ASeatBesideAnExistingPinIsClaimedBeforeAnyFreeSymbolIsPlaced()
    {
        // #PWR02, not turned (its pin already faces TP9.1's stub), sits with its pin on the stub's end, 2 grids right of the moved pin.
        var (bench, saved, geometry, tp9, tp1, pwr2) = BesideAnExistingPin();
        var seatAnchor = new PresentationPoint(40_000_000 + 4 * ConnectionGrid + 2 * ConnectionGrid + 4 * ConnectionGrid, 20_000_000);
        var result = await ProposeConnected(saved, Strip(bench), geometry);
        Assert.IsTrue(result.CanPropose, string.Join("; ", result.Layout.Issues.Select(i => i.Code)));
        var attachment = result.PowerAttachments.Single();
        Assert.IsTrue(attachment.Attached, attachment.DroppedReason);
        Assert.AreEqual(new PinEndpoint(tp9, "1"), attachment.Partner);
        Assert.AreEqual(0, attachment.RotationDegrees);
        Assert.AreEqual(seatAnchor, attachment.Anchor);
        var seat = result.Layout.Placements!.Single(p => p.BodyId == attachment.CarrierBodyId);
        Assert.AreEqual(seatAnchor, seat.Anchor);
        Assert.IsFalse(result.PreferredAnchors.Any(a => a.BodyId == attachment.CarrierBodyId), "A seated power symbol is not aimed.");
        // TP1 is aimed between #PWR01 and TP9 but must stay in their row; its nearest free place without the seat would overlap
        // the seat. Claimed first, the seat keeps the layout clearance from TP1.
        var free = result.Layout.Placements!.Single(p => p.BodyId != attachment.CarrierBodyId);
        Assert.IsFalse(Near(free.Bounds, seat.Bounds, Policy.ClearanceNm), "TP1 keeps clear of the claimed seat.");
        Assert.IsTrue(free.Anchor.XNm > seat.Anchor.XNm, "TP1 goes past the seat.");
        var placement = result.DesiredDesign!.Engineering.Circuit.Symbols.Single(s => s.ComponentId == pwr2).Placement!;
        Assert.AreEqual(new PresentationPoint(Coordinates.MillimetersToNanometers(placement.XMillimeters),
            Coordinates.MillimetersToNanometers(placement.YMillimeters)), seatAnchor);
        Assert.AreEqual(0, placement.RotationDegrees);
        Assert.AreEqual(result.DesiredXml, (await ProposeConnected(saved, Strip(bench), geometry.Copy())).DesiredXml, "Deterministic.");
        Assert.IsTrue(result.DesiredDesign.Engineering.Circuit.Symbols.Any(s => s.ComponentId == tp1 && s.Placement is not null));

        // Must-catch: a seat that leaves the usable page is dropped before the layout; #PWR02 is then placed freely.
        var narrow = Strip(bench, 17_500_000, 22_900_000);
        var overflow = await ProposeConnected(saved, narrow, geometry.Copy());
        Assert.IsTrue(overflow.CanPropose, string.Join("; ", overflow.Layout.Issues.Select(i => i.Code)));
        var overflowed = overflow.PowerAttachments.Single();
        Assert.IsFalse(overflowed.Attached); Assert.AreEqual("page_overflow", overflowed.DroppedReason);
        Assert.AreEqual(seatAnchor, overflowed.Anchor, "The refused seat is reported.");
        Assert.AreNotEqual(seatAnchor, overflow.Layout.Placements!.Single(p => p.BodyId == overflowed.CarrierBodyId).Anchor);

        // Must-catch: an item just right of the seat, within the layout clearance, drops the seat before the layout.
        var (blockedBench, blocked, blockedGeometry, _, _, _) = BesideAnExistingPin(new SchematicConnectionRealizerTests.Rect(58_500_000, 18_000_000, 60_000_000, 22_000_000));
        var collision = await ProposeConnected(blocked, Strip(blockedBench), blockedGeometry);
        Assert.IsTrue(collision.CanPropose, string.Join("; ", collision.Layout.Issues.Select(i => i.Code)));
        var collided = collision.PowerAttachments.Single();
        Assert.IsFalse(collided.Attached); Assert.AreEqual("collision", collided.DroppedReason);
        Assert.AreEqual(seatAnchor, collided.Anchor);
    }

    [TestMethod]
    public async Task APowerSymbolWhoseSeatIsDroppedKeepsItsPartnerFromOtherPowerSymbols()
    {
        // Two new power symbols and two new test points join GND. §10 pairs them first, in component order, and only then checks
        // each seat, so a dropped seat never hands its test point to the next power symbol.
        var bench = new Bench();
        Guid gnd = bench.Part("GND", SchematicSymbolType.SstGlobalPower, new BenchPin("1", "GND", 1, ElectricalPinType.EptPowerInput, false));
        Guid tp = bench.Part("TP", Passive("1"));
        Guid pwr1 = bench.Component(gnd, "#PWR01", value: "GND");
        bench.Component(tp, "TP9");
        var ground = new CircuitNet(Guid.NewGuid(), "GND", [new(pwr1, "1")]);
        var state = SchematicConnectionRealizerTests.WithFormatting(bench.State([ground]));
        var (design, tpA) = bench.Create(state.Baseline, tp, "TP1", BenchSheet.Root);
        (design, var tpB) = bench.Create(design, tp, "TP2", BenchSheet.Root);
        (design, var pwrA) = bench.Create(design, gnd, "#PWR02", BenchSheet.Root, value: "GND");
        (design, var pwrB) = bench.Create(design, gnd, "#PWR03", BenchSheet.Root, value: "GND");
        design = WithNets(Unplaced(Unplaced(Unplaced(Unplaced(design, tpA), tpB), pwrA), pwrB),
            ground with { Pins = [.. ground.Pins, new(tpA, "1"), new(tpB, "1"), new(pwrA, "1"), new(pwrB, "1")] });
        var (saved, _) = Revise(state, _ => design);
        Guid[] carriers = [.. new[] { pwrA, pwrB }.Order()];
        Guid[] partners = [.. new[] { tpA, tpB }.Order()];
        string first = carriers[0] == pwrA ? "#PWR02" : "#PWR03";

        // Guard: each power symbol is seated on its own test point, in order.
        var both = await ProposeConnected(saved, Root(bench), new());
        Assert.IsTrue(both.CanPropose, string.Join("; ", both.Layout.Issues.Select(i => i.Code)));
        CollectionAssert.AreEqual(carriers, both.PowerAttachments.Select(a => a.CarrierComponentId).ToArray());
        CollectionAssert.AreEqual(partners, both.PowerAttachments.Select(a => a.Partner.ComponentId).ToArray());
        Assert.IsTrue(both.PowerAttachments.All(a => a.Attached), string.Join(",", both.PowerAttachments.Select(a => a.DroppedReason)));

        // Must-catch: the first power symbol, turned, is measured far wider than its seat and would overlap its test point.
        var wide = new SchematicConnectionRealizerTests.Geometry
        {
            Tamper = (request, reply) =>
            {
                foreach (var candidate in reply.Candidates)
                {
                    var asked = request.Candidates.Single(c => c.Id.Equals(candidate.Id));
                    if (asked.Transform?.Orientation is SchematicSymbolOrientation.Sso180 && asked.ReferenceField.Text.Text_ == first)
                        candidate.Bounds.Size.XNm += 30 * ConnectionGrid;
                }
                return reply;
            }
        };
        var blocked = await ProposeConnected(saved, Root(bench), wide);
        Assert.IsTrue(blocked.CanPropose, string.Join("; ", blocked.Layout.Issues.Select(i => i.Code)));
        var dropped = blocked.PowerAttachments.Single(a => a.CarrierComponentId == carriers[0]);
        Assert.IsFalse(dropped.Attached); Assert.AreEqual("collision", dropped.DroppedReason);
        Assert.AreEqual(partners[0], dropped.Partner.ComponentId);
        var second = blocked.PowerAttachments.Single(a => a.CarrierComponentId == carriers[1]);
        Assert.IsTrue(second.Attached, second.DroppedReason);
        Assert.AreEqual(partners[1], second.Partner.ComponentId, "The dropped pairing keeps its test point.");
        Assert.AreEqual(0, blocked.DesiredDesign!.Engineering.Circuit.Symbols.Single(s => s.ComponentId == carriers[0]).Placement!.RotationDegrees);
        Assert.AreEqual(180, blocked.DesiredDesign.Engineering.Circuit.Symbols.Single(s => s.ComponentId == carriers[1]).Placement!.RotationDegrees);
    }

    [TestMethod]
    public async Task WrongOrIncompleteConnectionMeasurementNeverProducesACandidate()
    {
        // The second measurement round asks KiCad for label prototypes and turned power symbols at the recorded revision. Each
        // defect below is refused with its own code and nothing is proposed. The untampered replies are the false-positive guard:
        // the same scenes propose a layout in the tests above.
        var (bench, saved, _, _) = Signals();
        (string Problem, string Code, Action<SchematicPlacementGeometry> Corrupt)[] labelCases =
        [
            ("label reply for another revision", "stale_layout_measurement", m => m.Revision.Sequence++),
            ("label reply for another document", "stale_layout_measurement", m => m.Document.Project.Name += "foreign"),
            ("label reply for another sheet", "stale_layout_measurement", m => m.ScreenId.Value = Guid.NewGuid().ToString("D")),
            ("missing label reply", "incomplete_layout_measurement", m => m.ItemCandidates.RemoveAt(m.ItemCandidates.Count - 1)),
            ("reordered label replies", "incomplete_layout_measurement", m =>
            {
                var head = m.ItemCandidates[0]; m.ItemCandidates.RemoveAt(0); m.ItemCandidates.Add(head);
            }),
            ("label reply carrying symbol pins", "incomplete_layout_measurement", m => m.ItemCandidates[0].SymbolPins = new() { Complete = true }),
            ("label reply at another anchor", "invalid_layout_anchor", m => m.ItemCandidates[0].Anchor.XNm += ConnectionGrid),
            ("label reply without bounds", "invalid_layout_geometry", m => m.ItemCandidates[0].Bounds = null)
        ];
        foreach (var (problem, code, corrupt) in labelCases)
            await RequireRefused(saved, Root(bench), new() { Tamper = (request, reply) =>
            {
                if (request.ItemCandidates.Count != 0) corrupt(reply);
                return reply;
            } }, code, problem);

        var (powerBench, power, _, _) = Power();
        (string Problem, string Code, Action<SchematicPlacementBounds> Corrupt)[] probeCases =
        [
            ("turned power symbol with incomplete pins", "incomplete_layout_measurement", c => c.SymbolPins.Complete = false),
            ("turned power symbol without pins", "incomplete_layout_measurement", c => c.SymbolPins = null),
            ("turned power symbol with a second pin", "incomplete_layout_measurement", c => c.SymbolPins.Pins.Add(c.SymbolPins.Pins[0].Clone())),
            ("turned power symbol at another anchor", "invalid_layout_anchor", c => c.Anchor.YNm += ConnectionGrid)
        ];
        foreach (var (problem, code, corrupt) in probeCases)
            await RequireRefused(power, Root(powerBench), new() { Tamper = (request, reply) =>
            {
                foreach (var candidate in reply.Candidates)
                    if (request.Candidates.Single(c => c.Id.Equals(candidate.Id)).Transform?.Orientation is SchematicSymbolOrientation.Sso90)
                        corrupt(candidate);
                return reply;
            } }, code, problem);
    }

    // The plan fixture's channel sheet is used twice, U1 on one instance and U2 on the other, drawn by one shared symbol per unit.
    // The XML adds a coordinate-free component to each channel (again one shared symbol per unit) and joins its pin 1 with that
    // channel's pin 7: the same connection on both instances.
    private static (DesignRecoveryState Saved, SchematicLayoutRegion[] Regions, Guid[] Channels, Guid[] Created) RepeatedChannels()
    {
        var state = SchematicConnectionRealizerTests.WithFormatting(SchematicSynchronizationPlanTests.Fixture());
        var circuit = state.Baseline.Engineering.Circuit;
        var creation = SchematicNativeCreationProjectionTests.AddComponent(state.Baseline, coordinateFree: true);
        var channels = circuit.Components.Take(2).ToArray();
        var created = channels.Select(channel => creation.Circuit.Components
            .Single(c => !circuit.Components.Any(o => o.Id == c.Id) && c.SheetInstanceId == channel.SheetInstanceId)).ToArray();
        var probes = channels.Select((channel, i) => new CircuitNet(Guid.NewGuid(), "/" + channel.Reference + "/PROBE",
            [new(created[i].Id, "1"), new(channel.Id, "7")])).ToArray();
        var (saved, _) = Revise(state, baseline => baseline with { Engineering = creation with { Circuit = creation.Circuit with
            { Nets = [.. creation.Circuit.Nets, .. probes] } } });
        Guid screen = saved.Baseline.Schematic.Instances.Where(s => s.Metadata.Document.SheetPath.Path.Count > 1)
            .Select(s => Guid.Parse(s.Metadata.ScreenId.Value)).Distinct().Single();
        return (saved, [new(screen, new(0, 0, 297_000_000, 210_000_000), [])], [.. channels.Select(c => c.Id)], [.. created.Select(c => c.Id)]);
    }

    [TestMethod]
    public async Task RepeatedSheetsShareOneConnectedPlacementMeasuredAlikeOnEveryInstance()
    {
        var (saved, regions, channels, created) = RepeatedChannels();
        var geometry = new SchematicConnectionRealizerTests.Geometry();
        var result = await ProposeConnected(saved, regions, geometry);
        Assert.IsTrue(result.CanPropose, string.Join("; ", result.Layout.Issues.Select(i => i.Code)));
        var plan = SchematicConnectionIntentBuilderTests.Plan(saved with { DesiredFileBytes = Encoding.UTF8.GetBytes(result.DesiredXml!) });
        var channel = RequireRealizationPlan(plan).Screens.Single(s => s.InstancePathKeys.Count == 2);
        foreach (string path in channel.InstancePathKeys)
        {
            Assert.IsTrue(geometry.Requests.Any(r => Path(r.Document) == path && r.Candidates.Count != 0 && r.ItemCandidates.Count == 0),
                "The new symbols are measured on instance " + path);
            Assert.IsTrue(geometry.Requests.Any(r => Path(r.Document) == path && r.ItemCandidates.Count != 0), "Labels are measured on instance " + path);
        }
        // Unit 1 of the new component is one body for both channels, aimed at the channels' one physical pin 7 and placed once.
        var design = result.DesiredDesign!;
        var unitOne = design.Engineering.Circuit.Symbols.Where(s => created.Contains(s.ComponentId) && s.Unit == 1).Select(s => s.Id).Order().ToArray();
        Assert.HasCount(2, unitOne);
        var aim = result.PreferredAnchors.Single();
        CollectionAssert.AreEqual(unitOne, aim.SymbolOccurrences.ToArray());
        var pinSeven = Keys(plan.Candidate!, (channels[0], "7"), (channels[1], "7"));
        Assert.AreEqual(1, pinSeven.Select(k => k.PlacedPinId).Distinct().Count(), "Both channels draw pin 7 as one physical pin.");
        CollectionAssert.AreEqual(new[] { pinSeven[0].PlacedPinId }, aim.PartnerPins.ToArray());
        CollectionAssert.AreEqual(unitOne, result.Layout.Placements!.Single(p => p.BodyId == aim.BodyId).SymbolOccurrences.ToArray());
        Assert.HasCount(1, design.Engineering.Circuit.Symbols.Where(s => unitOne.Contains(s.Id)).Select(s => s.Placement).Distinct().ToArray(),
            "Both channels' occurrences get one position.");
        Assert.AreEqual(result.DesiredXml, (await ProposeConnected(saved, regions, new())).DesiredXml, "Deterministic.");

        // Must-catch: one drawing serves both instances, so each instance must measure the connected pins alike.
        string second = channel.InstancePathKeys[1];
        Guid partner = pinSeven[0].PlacedPinId, own = Keys(plan.Candidate!, (created[0], "1")).Single().PlacedPinId;
        var movedPartner = new SchematicConnectionRealizerTests.Geometry(); movedPartner.Shift[(second, partner)] = new(ConnectionGrid, 0);
        await RequireRefused(saved, regions, movedPartner, "inconsistent_layout_measurement", "pin 7 drawn elsewhere on one instance");
        var movedOwn = new SchematicConnectionRealizerTests.Geometry(); movedOwn.Shift[(second, own)] = new(0, ConnectionGrid);
        await RequireRefused(saved, regions, movedOwn, "inconsistent_layout_measurement", "new pin 1 measured elsewhere on one instance");
        await RequireRefused(saved, regions, OnSecond(reply =>
        {
            foreach (var pin in reply.Obstacles.Where(o => o.SymbolPins is not null).SelectMany(o => o.SymbolPins.Pins).Where(p => p.Id.Value == partner.ToString("D")))
            {
                pin.BodyDirectionX = 0; pin.BodyDirectionY = 1;
            }
        }), "inconsistent_layout_measurement", "pin 7 facing another way on one instance");
        // Must-catch: one instance measured without pin geometry, or without the pins of the symbol pin 7 belongs to.
        await RequireRefused(saved, regions, OnSecond(reply => reply.PinGeometryAvailable = false), "incomplete_layout_measurement",
            "one instance without pin geometry");
        await RequireRefused(saved, regions, OnSecond(reply =>
        {
            foreach (var symbol in reply.Obstacles.Where(o => o.SymbolPins?.Pins.Any(p => p.Id.Value == partner.ToString("D")) == true))
                symbol.SymbolPins.Complete = false;
        }), "incomplete_layout_measurement", "one instance without pin 7's symbol pins");

        SchematicConnectionRealizerTests.Geometry OnSecond(Action<SchematicPlacementGeometry> corrupt) => new()
        {
            Tamper = (request, reply) =>
            {
                if (Path(request.Document) == second) corrupt(reply);
                return reply;
            }
        };
    }

    // R5 on the root sits just above and left of the child sheet's box (ChildSheetBox). The XML adds R1, coordinate-free, to DATA
    // with R5.1 and, when crossing, with R9.1 on the child sheet, so the child's sheet symbol gains a sheet pin.
    private static readonly PresentationBounds ChildSheetBox = new(27_000_000, 24_000_000, 67_000_000, 64_000_000);

    private static (Bench Bench, DesignRecoveryState Saved, Guid R5) BesideTheChildSheet(bool crossing)
    {
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid r5 = bench.Component(r, "R5"), r9 = bench.Component(r, "R9", BenchSheet.Child);
        var state = SchematicConnectionRealizerTests.Edited(SchematicConnectionRealizerTests.WithFormatting(bench.State([])), data =>
        {
            var root = SchematicConnectionRealizerTests.Root(data);
            for (int i = 0; i < root.Items.Count; i++)
            {
                if (!root.Items[i].Is(SheetSymbol.Descriptor)) continue;
                var sheet = root.Items[i].Unpack<SheetSymbol>();
                sheet.Position = new() { XNm = ChildSheetBox.LeftNm, YNm = ChildSheetBox.TopNm };
                sheet.Size = new() { XNm = ChildSheetBox.RightNm - ChildSheetBox.LeftNm, YNm = ChildSheetBox.BottomNm - ChildSheetBox.TopNm };
                root.Items[i] = Google.Protobuf.WellKnownTypes.Any.Pack(sheet);
            }
        });
        var (design, r1) = bench.Create(state.Baseline, r, "R1", BenchSheet.Root);
        var pins = new List<PinEndpoint> { new(r1, "1"), new(r5, "1") };
        if (crossing) pins.Add(new(r9, "1"));
        var (saved, _) = Revise(state, _ => WithNets(Unplaced(design, r1), new CircuitNet(Guid.NewGuid(), "DATA", [.. pins])));
        return (bench, saved, r5);
    }

    [TestMethod]
    public async Task ANewSymbolLeavesTheRoomBesideASheetThatGainsASheetPin()
    {
        // The room a sheet pin on either side of the box needs: its shortest stub and the local label DATA facing away from the box.
        var label = new SchematicConnectionRealizerTests.Geometry().LabelBox(new LocalLabel { Text = new() { Text_ = "DATA" }, Position = new(),
            SpinStyle = SchematicLabelSpinStyle.SlssLeft });
        long reach = 2 * ConnectionGrid - label.L;
        PresentationBounds[] sides =
        [
            new(ChildSheetBox.LeftNm - reach, ChildSheetBox.TopNm, ChildSheetBox.LeftNm, ChildSheetBox.BottomNm),
            new(ChildSheetBox.RightNm, ChildSheetBox.TopNm, ChildSheetBox.RightNm + reach, ChildSheetBox.BottomNm)
        ];
        // Guard: without the crossing no sheet pin is added, and R1's nearest free place, just below R5, takes some of that room.
        var (plainBench, plain, _) = BesideTheChildSheet(crossing: false);
        var local = await ProposeConnected(plain, Root(plainBench), new());
        Assert.IsTrue(local.CanPropose, string.Join("; ", local.Layout.Issues.Select(i => i.Code)));
        Assert.IsTrue(Overlaps(local.Layout.Placements!.Single().Bounds, sides[0]), "Guard: nothing reserves that room when no sheet pin is added.");

        // With DATA crossing into the child sheet, R1 is aimed at R5.1 alike but keeps out of the room on both sides of the box.
        var (bench, crossing, r5) = BesideTheChildSheet(crossing: true);
        var result = await ProposeConnected(crossing, Root(bench), new());
        Assert.IsTrue(result.CanPropose, string.Join("; ", result.Layout.Issues.Select(i => i.Code)));
        var root = RequireRealizationPlan(SchematicConnectionIntentBuilderTests.Plan(crossing with { DesiredFileBytes = Encoding.UTF8.GetBytes(result.DesiredXml!) }))
            .Screens.Single(s => s.ScreenId == bench.ScreenId(BenchSheet.Root)).Islands.Single();
        CollectionAssert.AreEqual(new[] { bench.ChildSheetSymbol }, root.ChildSheetSymbolIds.ToArray(), "The child's sheet symbol gains a sheet pin.");
        var placed = result.Layout.Placements!.Single();
        foreach (var side in sides) Assert.IsFalse(Overlaps(placed.Bounds, side), "R1 keeps out of the room a new sheet pin needs.");
        Assert.IsFalse(Overlaps(placed.Bounds, ChildSheetBox));
        CollectionAssert.AreEqual(new[] { bench.PinId(r5, "1") }, result.PreferredAnchors.Single().PartnerPins.ToArray());
        Assert.AreEqual(local.PreferredAnchors.Single().Anchor, result.PreferredAnchors.Single().Anchor, "Both are aimed at R5.1 alike.");
    }

    // LINK = {R2.1, R3.1} on the root is joined by a bare wire, or also named by a local label LINK. The XML adds R1,
    // coordinate-free, to LINK. R2 has the smaller component identity, so it is the first join candidate; it sits right of R3
    // and its pin 1 faces down.
    private static (Bench Bench, DesignRecoveryState Saved, SchematicConnectionRealizerTests.Geometry Geometry, Guid R2) Joined(bool labelled)
    {
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid r3 = bench.Component(r, "R3", id: Guid.Parse("00000000-0000-4000-8000-000000000002"));
        Guid r2 = bench.Component(r, "R2", id: Guid.Parse("00000000-0000-4000-8000-000000000001"));
        var link = new CircuitNet(Guid.NewGuid(), "LINK", [new(r2, "1"), new(r3, "1")]);
        var items = new List<(BenchSheet Sheet, string Id)> { (BenchSheet.Root, bench.Wire(BenchSheet.Root)) };
        if (labelled) items.Add((BenchSheet.Root, bench.LocalLabel(BenchSheet.Root, "LINK")));
        var state = SchematicConnectionRealizerTests.WithFormatting(bench.State([link], new() { [link.Id] = [.. items] }));
        var (design, r1) = bench.Create(state.Baseline, r, "R1", BenchSheet.Root);
        var (saved, _) = Revise(state, _ => WithNets(Unplaced(design, r1), link with { Pins = [.. link.Pins, new(r1, "1")] }));
        var geometry = new SchematicConnectionRealizerTests.Geometry();
        geometry.Direction[bench.PinId(r2, "1")] = (0, -1);
        return (bench, saved, geometry, r2);
    }

    [TestMethod]
    public async Task ANewSymbolLeavesTheRoomWhereAnUnlabelledConnectionWillBeNamed()
    {
        // R2 sits at x = 40 mm, y = 20 mm and the bench draws its pin 1 four grids left of and one grid above it. Facing down, the
        // pin's shortest stub and the local label LINK below it need this room.
        var label = new SchematicConnectionRealizerTests.Geometry().LabelBox(new LocalLabel { Text = new() { Text_ = "LINK" }, Position = new(),
            SpinStyle = SchematicLabelSpinStyle.SlssBottom });
        var pin = new PresentationPoint(40_000_000 - 4 * ConnectionGrid, 20_000_000 - ConnectionGrid);
        var room = new PresentationBounds(pin.XNm + label.L, pin.YNm, pin.XNm + label.R, pin.YNm + 2 * ConnectionGrid + label.B);

        // Guard: a local label already names LINK, so nothing is joined, and R1's nearest free place, below the pair, takes that room.
        var (namedBench, named, namedGeometry, _) = Joined(labelled: true);
        var labelled = await ProposeConnected(named, Root(namedBench), namedGeometry);
        Assert.IsTrue(labelled.CanPropose, string.Join("; ", labelled.Layout.Issues.Select(i => i.Code)));
        Assert.IsFalse(RequireRealizationPlan(SchematicConnectionIntentBuilderTests.Plan(named with { DesiredFileBytes = Encoding.UTF8.GetBytes(labelled.DesiredXml!) }))
            .Screens.Single().Islands.Single().JoinRequired);
        Assert.IsTrue(Overlaps(labelled.Layout.Placements!.Single().Bounds, room), "Guard: nothing reserves that room when no join is needed.");

        // With only a bare wire, realization names LINK with a stub and label at R2.1, and R1 keeps out of that room.
        var (bench, bare, geometry, r2) = Joined(labelled: false);
        var result = await ProposeConnected(bare, Root(bench), geometry);
        Assert.IsTrue(result.CanPropose, string.Join("; ", result.Layout.Issues.Select(i => i.Code)));
        var island = RequireRealizationPlan(SchematicConnectionIntentBuilderTests.Plan(bare with { DesiredFileBytes = Encoding.UTF8.GetBytes(result.DesiredXml!) }))
            .Screens.Single().Islands.Single();
        Assert.IsTrue(island.JoinRequired);
        Assert.AreEqual(bench.PinId(r2, "1"), island.JoinCandidates[0].PlacedPinId);
        Assert.IsFalse(Overlaps(result.Layout.Placements!.Single().Bounds, room), "R1 keeps out of the room where LINK will be named.");
        Assert.AreEqual(labelled.PreferredAnchors.Single().Anchor, result.PreferredAnchors.Single().Anchor, "Both are aimed at the pair alike.");
    }

    private static async Task RequireRefused(DesignRecoveryState saved, SchematicLayoutRegion[] regions,
        SchematicConnectionRealizerTests.Geometry geometry, string code, string problem)
    {
        byte[] before = saved.DesiredFileBytes.ToArray();
        var error = await Assert.ThrowsAsync<AutomationException>(() => ProposeConnected(saved, regions, geometry));
        Assert.AreEqual(code, error.Code, problem + ": " + error.Message);
        CollectionAssert.AreEqual(before, saved.DesiredFileBytes, problem);
    }

    private static bool Overlaps(PresentationBounds a, PresentationBounds b) =>
        a.LeftNm < b.RightNm && b.LeftNm < a.RightNm && a.TopNm < b.BottomNm && b.TopNm < a.BottomNm;

    private static bool Near(PresentationBounds a, PresentationBounds b, long gap) =>
        a.LeftNm - gap < b.RightNm && a.RightNm + gap > b.LeftNm && a.TopNm - gap < b.BottomNm && a.BottomNm + gap > b.TopNm;

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
