using System.Text;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
