using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SymbolSheetOwnershipTests
{
    internal static DesignRecoveryState Fixture()
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        var design = state.Baseline; var circuit = design.Engineering.Circuit;
        var native = state.ObservedElectrical!.Clone(); var hierarchy = native.Hierarchy.Data;
        var root = hierarchy.Instances.Single(s => s.Metadata.Document.Equals(hierarchy.Document));
        Guid rootModel = design.SheetBindings.Single(b => b.NativePath.Count == 1).SheetInstanceId;
        var locations = design.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var screens = hierarchy.Instances.ToDictionary(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)));
        var bindings = design.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId);
        var symbols = circuit.Symbols.ToDictionary(s => s.Id);
        foreach (var occurrence in circuit.Symbols.Where(s => s.Unit == 2))
        {
            var component = circuit.Components.Single(c => c.Id == occurrence.ComponentId);
            var screen = screens[locations[component.SheetInstanceId]];
            var oldBinding = bindings[occurrence.Id];
            var packed = screen.Items.Single(i => i.Is(SchematicSymbolInstance.Descriptor)
                && i.Unpack<SchematicSymbolInstance>().Id.Value == oldBinding.NativeObjectId.ToString("D"));
            screen.Items.Remove(packed);
            var symbol = packed.Unpack<SchematicSymbolInstance>();
            symbol.Id.Value = Guid.NewGuid().ToString("D"); symbol.Path = hierarchy.Document.SheetPath.Clone();
            symbol.InstanceRecords = new();
            var record = new SymbolSheetRecord { ProjectName = "fixture", Reference = component.Reference, Unit = occurrence.Unit, Variants = new() };
            record.Path.Add(symbol.Path.Path.Select(p => p.Clone())); symbol.InstanceRecords.Records.Add(record);
            foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>(); string former = pin.Id.Value;
                pin.Id.Value = Guid.NewGuid().ToString("D"); child.Item = Any.Pack(pin);
                foreach (var net in native.Nets)
                {
                    var from = net.Sheets.SingleOrDefault(s => s.Path.Equals(screen.Metadata.Document.SheetPath));
                    var old = from?.Items.SingleOrDefault(p => p.Value == former);
                    if (old is null) continue;
                    from!.Items.Remove(old);
                    var destination = net.Sheets.SingleOrDefault(s => s.Path.Equals(hierarchy.Document.SheetPath));
                    if (destination is null)
                    {
                        destination = new() { Path = hierarchy.Document.SheetPath.Clone() };
                        net.Sheets.Add(destination);
                    }
                    destination.Items.Add(pin.Id.Clone());
                }
            }
            root.Items.Add(Any.Pack(symbol));
            bindings[occurrence.Id] = oldBinding with { NativeObjectId = Guid.Parse(symbol.Id.Value) };
            symbols[occurrence.Id] = occurrence with { SheetInstanceId = rootModel };
        }
        circuit = circuit with { Symbols = circuit.Symbols.Select(s => symbols[s.Id]).ToArray() };
        design = design with { Engineering = design.Engineering with { Circuit = circuit }, Schematic = hierarchy.Clone(),
            SymbolBindings = design.SymbolBindings.Select(b => bindings[b.SymbolOccurrenceId]).ToArray() };
        return state with { Baseline = design, Observed = hierarchy.Clone(), BaselineElectrical = native.Clone(), ObservedElectrical = native,
            DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries)) };
    }

    [TestMethod]
    public void OldDocumentsKeepTheirEncodingAndCrossSheetUnitsKeepOneComponentIdentity()
    {
        var old = CircuitXmlTests.Fixture(); string xml = CircuitXml.Write(old);
        Assert.IsFalse(xml.Contains("sheet-instance=", StringComparison.Ordinal));
        Assert.AreEqual(xml, CircuitXml.Write(CircuitXml.Read(xml)));
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit;
        string changed = CircuitXml.Write(circuit);
        Assert.AreEqual(changed, CircuitXml.Write(CircuitXml.Read(changed)));
        Assert.AreEqual(2, circuit.Components.Count); Assert.AreEqual(4, circuit.Symbols.Count);
        Assert.AreEqual(2, circuit.Symbols.Count(s => s.SheetInstanceId is not null));
        Assert.IsTrue(circuit.WithoutPlacement().Symbols.Where(s => s.Unit == 2).All(s => s.SheetInstanceId is not null));
        var bindings = SchematicDesignBindings.Inspect(state.Baseline, state.KnowledgeLibraries);
        Assert.IsTrue(bindings.IdentitiesResolved); Assert.IsEmpty(bindings.Differences);
        var electrical = SchematicElectricalComparison.Compare(state.Baseline, state.ObservedElectrical!, state.KnowledgeLibraries);
        Assert.IsTrue(electrical.PinBindingsComplete); Assert.IsTrue(electrical.ConnectivityEquivalent);
        Assert.IsTrue(SchematicSynchronizationPlanner.Plan(state).CanPrepare);
    }

    [TestMethod]
    public void PropertyAndPlacementProjectionTargetTheUnitLocationNotTheDefinitionSheet()
    {
        var state = Fixture(); var design = state.Baseline;
        var target = design.Engineering.Circuit.Symbols.First(s => s.Unit == 2);
        var wanted = design.Engineering with { Circuit = design.Engineering.Circuit with
        {
            Components = design.Engineering.Circuit.Components.Select(c => c.Id == target.ComponentId ? c with { Reference = "U900" } : c).ToArray(),
            Symbols = design.Engineering.Circuit.Symbols.Select(s => s.Id == target.Id
                ? s with { Placement = s.Placement! with { XMillimeters = s.Placement.XMillimeters + 2.54m } } : s).ToArray()
        } };
        var plan = SchematicSynchronizationPlanner.PlanForExecution(SchematicNetReconciliationTests.Desired(state, wanted));
        Assert.IsTrue(plan.CanPrepare, plan.ErrorMessage);
        var move = plan.NativeOperations.Single(o => o.MoveConnectedSymbols is not null);
        Assert.AreEqual(design.Schematic.Document, move.TargetDocument);
        Assert.AreEqual(design.SymbolBindings.Single(b => b.SymbolOccurrenceId == target.Id).NativeObjectId.ToString("D"),
            move.MoveConnectedSymbols.Symbols.Single().Value);
        var projected = SchematicModelProjection.NativeSymbols(plan.Candidate!, plan.Candidate!.Schematic);
        foreach (var unit in design.Engineering.Circuit.Symbols.Where(s => s.ComponentId == target.ComponentId))
            Assert.AreEqual("U900", projected[unit.Id].ReferenceField.Text.Text_);
        var observed = state.Observed.Clone();
        var root = observed.Instances.Single(s => s.Metadata.Document.Equals(observed.Document));
        var binding = design.SymbolBindings.Single(b => b.SymbolOccurrenceId == target.Id);
        int index = root.Items.ToList().FindIndex(i => i.Is(SchematicSymbolInstance.Descriptor)
            && i.Unpack<SchematicSymbolInstance>().Id.Value == binding.NativeObjectId.ToString("D"));
        var native = root.Items[index].Unpack<SchematicSymbolInstance>(); native.Position.XNm += 1270000; root.Items[index] = Any.Pack(native);
        var reverse = SchematicModelProjection.Reconcile(design, design.Engineering, observed, state.KnowledgeLibraries);
        Assert.IsNotNull(reverse.Candidate);
        var moved = reverse.Candidate.Circuit.Symbols.Single(s => s.Id == target.Id);
        Assert.AreEqual(target.SheetInstanceId, moved.SheetInstanceId);
        Assert.AreEqual(target.Placement!.XMillimeters + 1.27m, moved.Placement!.XMillimeters);
    }

    [TestMethod]
    public void InvalidLocationsAndImplicitRebindingRemainExplicitFailures()
    {
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit;
        var symbol = circuit.Symbols[0];
        foreach (Guid id in new[] { Guid.Empty, Guid.NewGuid() })
            Assert.ThrowsExactly<AutomationException>(() => (circuit with { Symbols = circuit.Symbols.Select(s => s.Id == symbol.Id
                ? s with { SheetInstanceId = id } : s).ToArray() }).Validate());
        Assert.ThrowsExactly<AutomationException>(() => symbol.EffectiveSheetInstanceId(circuit.Components.Single(c => c.Id != symbol.ComponentId)));
        var wanted = state.Baseline.Engineering with { Circuit = circuit with
            { Symbols = circuit.Symbols.Select(s => s.Unit == 2 ? s with { SheetInstanceId = null } : s).ToArray() } };
        var changed = SchematicNetReconciliationTests.Desired(state, wanted);
        Assert.IsFalse(SchematicSynchronizationPlanner.PlanForExecution(changed).CanPrepare);
    }

    // Symbols placed in KiCad become design components, and design components reach the block of their sheet (ledger
    // p74ee7c1da24272d9). The PSU/CPU ownership journey (check ownership-sync) proves both end to end through KiCad, the
    // automatic worker and the MCP tools; these offline cases pin the exact identity rules and every refusal the journey
    // cannot reach cheaply. No existing test covered them: a new symbol was refused as electrical_ownership_changed before.
    internal static SchematicSymbolInstance PlacedCopy(SchematicSymbolInstance template, string reference, long dx, long dy = 0)
    {
        var symbol = template.Clone();
        symbol.Id = new() { Value = Guid.NewGuid().ToString("D") };
        symbol.Position.XNm += dx; symbol.Position.YNm += dy;
        foreach (var field in new[] { symbol.ReferenceField, symbol.ValueField, symbol.FootprintField, symbol.DatasheetField, symbol.DescriptionField }
                     .Concat(symbol.UserFields))
            if (field?.Text?.Position is { } position) { position.XNm += dx; position.YNm += dy; }
        symbol.ReferenceField.Text.Text_ = reference;
        foreach (var record in symbol.InstanceRecords?.Records ?? []) record.Reference = reference;
        foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
        {
            var pin = child.Item.Unpack<SchematicPin>();
            if (pin.LibraryPinId is not null) pin.Id = new() { Value = Guid.NewGuid().ToString("D") };
            child.Item = Any.Pack(pin);
        }
        return symbol;
    }

    private static (DesignRecoveryState State, SchematicSymbolInstance Added, IReadOnlyList<Guid> Path) PlacedResistor(
        Func<SchematicDesign, SchematicDesign>? baselineEdit = null)
    {
        var baseline = SchematicRebuildTests.Placed();
        if (baselineEdit is not null) baseline = baselineEdit(baseline);
        var observed = baseline.Schematic.Clone();
        var psu = baseline.SheetBindings.Single(b => b.SheetInstanceId == PsuCpuIds.Id(0x05, 2)).NativePath;
        var screen = observed.Instances.Single(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == SchematicDesignBindings.PathKey(psu));
        string r1 = baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == PsuCpuIds.Id(0x09, 3)).NativeObjectId.ToString("D");
        var template = screen.Items.Select(i => i.Unpack<SchematicSymbolInstance>()).Single(x => x.Id.Value == r1);
        var added = PlacedCopy(template, "R2", 0, 25_400_000);
        screen.Items.Add(Any.Pack(added));
        var state = SchematicRebuildTests.State(baseline, baseline, observed);
        return (state with { BaselineElectrical = Isolated(baseline.Schematic, state.BaselineElectrical!.Hierarchy.Revision),
            ObservedElectrical = Isolated(observed, state.ObservedElectrical!.Hierarchy.Revision) }, added, psu);
    }

    // The pin connectivity KiCad reports for a drawing without wires: every placed pin alone in its net, except the pins
    // one symbol's own definition draws at one point, which KiCad joins (decision kicad-stacked-pins-one-node-20260924).
    internal static KiCad.Automation.Protocol.SchematicElectricalState Isolated(SchematicHierarchyData data, KiCad.Automation.Protocol.DocumentRevision revision)
    {
        var state = new KiCad.Automation.Protocol.SchematicElectricalState { Hierarchy = new() { Data = data.Clone(), Revision = revision.Clone() } };
        foreach (var screen in data.Instances)
        foreach (var symbol in screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()))
        {
            int style = symbol.BodyStyle?.Style is > 0 ? symbol.BodyStyle.Style : 1;
            var placed = symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)
                    && !(c.Unit?.Unit is > 0 && c.Unit.Unit != symbol.Unit.Unit) && !(c.BodyStyle?.Style is > 0 && c.BodyStyle.Style != style))
                .Select(c => c.Item.Unpack<SchematicPin>()).Where(p => p.LibraryPinId is not null);
            foreach (var point in placed.GroupBy(p => (p.Position?.XNm ?? 0, p.Position?.YNm ?? 0)))
            {
                var sheet = new SchematicNetSheetContents { Path = screen.Metadata.Document.SheetPath.Clone() };
                sheet.Items.Add(point.Select(p => p.Id.Clone()));
                var net = new SchematicNet { Name = "unconnected-" + point.First().Id.Value };
                net.Sheets.Add(sheet); state.Nets.Add(net);
            }
        }
        return state;
    }

    [TestMethod]
    public void ASymbolPlacedInKiCadBecomesAComponentWithStableIdentitiesAndItsExactPart()
    {
        var (state, added, path) = PlacedResistor();
        var result = SchematicNetReconciliation.Plan(state, [], CancellationToken.None);
        Assert.IsNotNull(result.Candidate, result.ErrorCode + ": " + result.ErrorMessage);
        Guid circuit = state.Baseline.Engineering.Circuit.Id, native = Guid.Parse(added.Id.Value);
        Guid component = SchematicNativeAdditionProjection.AdoptedIdentity("component", circuit, path, native);
        Guid occurrence = SchematicNativeAdditionProjection.AdoptedIdentity("occurrence", circuit, path, native);
        CollectionAssert.AreEqual(new[] { component }, result.AddedComponents!.ToArray());
        CollectionAssert.AreEqual(new[] { occurrence }, result.AddedSymbolOccurrences!.ToArray());
        Assert.IsEmpty(result.AddedParts!, "The resistor is the fixture's R part: same library symbol, same pins.");
        var adopted = result.Candidate.Circuit.Components.Single(c => c.Id == component);
        Assert.AreEqual("R2", adopted.Reference);
        Assert.AreEqual(PsuCpuIds.Id(0x05, 2), adopted.SheetInstanceId, "It sits on the PSU sheet KiCad shows it on.");
        var definition = result.Candidate.Circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == adopted.DefinitionId);
        Assert.AreEqual(PsuCpuIds.Id(0x03, 3), definition.PartId);
        Assert.AreEqual(SchematicModelProjection.Placement(added), result.Candidate.Circuit.Symbols.Single(s => s.Id == occurrence).Placement);
        Assert.IsFalse(result.Candidate.Circuit.Nets.Any(n => n.Pins.Any(p => p.ComponentId == component)), "Its unconnected pins make no net.");
        Assert.AreEqual(native, result.Restoration!.BindingCandidate.SymbolBindings.Single(b => b.SymbolOccurrenceId == occurrence).NativeObjectId);
        // A repeated plan derives the same identities.
        CollectionAssert.AreEqual(result.AddedComponents!.ToArray(), SchematicNetReconciliation.Plan(state, [], CancellationToken.None).AddedComponents!.ToArray());
        // Without history the planner first asks for it; history that restores other owners is not a match either.
        Assert.AreEqual("electrical_ownership_changed", SchematicNetReconciliation.Plan(state).ErrorCode);
    }

    [TestMethod]
    public void AnUndecidablePartIsARequestAndANewLibrarySymbolIsANewPart()
    {
        // Two parts drawn with the same library symbol and pins: the new symbol could be either.
        var (ambiguous, added, _) = PlacedResistor(baseline =>
        {
            var circuit = baseline.Engineering.Circuit;
            var r = circuit.Parts.Single(p => p.Id == PsuCpuIds.Id(0x03, 3));
            var sense = r with { Id = Guid.NewGuid(), Name = "R_sense" };
            var declaration = baseline.PartSymbols!.Single(p => p.PartId == r.Id) with { PartId = sense.Id };
            return baseline with { Engineering = baseline.Engineering with { Circuit = circuit with { Parts = [.. circuit.Parts, sense] } },
                PartSymbols = [.. baseline.PartSymbols!, declaration] };
        });
        var result = SchematicNetReconciliation.Plan(ambiguous, [], CancellationToken.None);
        Assert.IsNull(result.Candidate);
        Assert.AreEqual(SchematicNativeAdditionProjection.ResolutionRequired, result.ErrorCode, result.ErrorMessage);
        var request = result.ResolutionRequests!.Single();
        Assert.AreEqual(SchematicNativeAdditionProjection.PartAmbiguous, request.Code);
        Assert.AreEqual(Guid.Parse(added.Id.Value), request.NativeObjectId);
        Assert.HasCount(2, request.CandidatePartIds);
        CollectionAssert.Contains(request.CandidatePartIds.ToArray(), PsuCpuIds.Id(0x03, 3));

        // Another library symbol with the same pins is not the fixture part: it becomes a new part of its own.
        var (state, other, path) = PlacedResistor();
        bool IsOther(Any i) => i.Is(SchematicSymbolInstance.Descriptor) && i.Unpack<SchematicSymbolInstance>().Id.Value == other.Id.Value;
        var screen = state.Observed.Instances.Single(s => s.Items.Any(IsOther));
        int index = screen.Items.ToList().FindIndex(IsOther);
        var changed = screen.Items[index].Unpack<SchematicSymbolInstance>();
        changed.LibraryId = new() { LibraryNickname = "Device", EntryName = "R_Small" };
        if (changed.Definition.Id is not null) changed.Definition.Id = changed.LibraryId.Clone();
        screen.Items[index] = Any.Pack(changed);
        state = state with { ObservedElectrical = Isolated(state.Observed, state.ObservedElectrical!.Hierarchy.Revision) };
        var created = SchematicNetReconciliation.Plan(state, [], CancellationToken.None);
        Assert.IsNotNull(created.Candidate, created.ErrorCode + ": " + created.ErrorMessage);
        var part = created.Candidate.Circuit.Parts.Single(p => p.Id == created.AddedParts!.Single());
        Assert.AreEqual(SchematicNativeAdditionProjection.AdoptedPartIdentity(state.Baseline.Engineering.Circuit.Id, "Device:R_Small", part.Units, part.Pins), part.Id);
        Assert.AreEqual(PsuCpuFixture.Engineering().Circuit.Parts.Single(p => p.Id == PsuCpuIds.Id(0x03, 3)).Pins.Count, part.Pins.Count);
    }

    [TestMethod]
    public void UnownedComponentsBelongToTheBlockOfTheirSheetByExactIdentity()
    {
        var graph = PsuCpuFixture.Graph();
        var components = PsuCpuFixture.Engineering(PsuCpuStage.Components);
        var circuit = components.Circuit;
        var design = new SchematicDesign(components, new SchematicHierarchyData(), [], []);
        var owned = BlockOwnershipSynchronization.Plan(graph, PsuCpuIds.Id(0x01, 2), design);
        Assert.IsEmpty(owned.Assignments); Assert.IsEmpty(owned.Requests); Assert.IsEmpty(owned.DetachedComponents);
        // One new component on each sheet, and U6 removed.
        var r = circuit.Parts.Single(p => p.Id == PsuCpuIds.Id(0x03, 3));
        ComponentInstance New(int n, Guid sheet) => new(Guid.NewGuid(), Guid.NewGuid(), sheet, "R" + (10 + n));
        var added = new[] { New(1, PsuCpuIds.Id(0x05, 2)), New(2, PsuCpuIds.Id(0x05, 3)), New(3, PsuCpuIds.Id(0x05, 4)), New(4, PsuCpuIds.Id(0x05, 1)) };
        var definitionsBySheet = circuit.SheetInstances.ToDictionary(i => i.Id, i => i.DefinitionId);
        var changed = circuit with
        {
            Sheets = [.. circuit.Sheets.Select(s => s with { Components = [.. s.Components.Where(c => c.Id != PsuCpuIds.Id(0x06, 8)),
                .. added.Where(a => definitionsBySheet[a.SheetInstanceId] == s.Id).Select(a => new ComponentDefinition(a.DefinitionId, r.Id, "R"))] })],
            Components = [.. circuit.Components.Where(c => c.Id != PsuCpuIds.Id(0x07, 8)), .. added],
            Symbols = [.. circuit.Symbols.Where(s => s.ComponentId != PsuCpuIds.Id(0x07, 8)), .. added.Select(a => new SymbolOccurrence(Guid.NewGuid(), a.Id, 1, null))]
        };
        changed.Validate();
        var plan = BlockOwnershipSynchronization.Plan(graph, PsuCpuIds.Id(0x01, 2), design with { Engineering = components with { Circuit = changed } });
        Guid Block(int n) => PsuCpuIds.Id(0x11, n);
        // A sheet names a block only when one block owns everything else it shows. Sheets are not blocks (contract §0 rule 3),
        // so the block hierarchy never decides: without the memory the CPU sheet shows only the processor, and CPU_POWER
        // draws only the processor's power unit.
        CollectionAssert.AreEquivalent(new[] { (added[1].Id, Block(8)), (added[2].Id, Block(8)) },
            plan.Assignments.Select(a => (a.ComponentId, a.BlockId)).ToArray());
        Assert.IsTrue(plan.Assignments.All(a => a.BlockName == "Processor" && a.Reason == "Every other component on its sheet belongs to this block."));
        var requests = plan.Requests.ToDictionary(q => q.ComponentId);
        Assert.HasCount(2, requests);
        // Must-not-claim: the PSU sheet shows J1 (PSU) and the parts of PSU's four children, so no single block owns what it
        // shows. The request offers every block from the root down to each owner; the person decides.
        Assert.AreEqual(BlockOwnershipSynchronization.OwnerUnresolved, requests[added[0].Id].Code);
        CollectionAssert.AreEqual(new[] { Block(1), Block(2), Block(4), Block(5), Block(6), Block(7) }, requests[added[0].Id].CandidateBlockIds.ToArray());
        Assert.AreEqual(PsuCpuIds.Id(0x05, 2), requests[added[0].Id].SheetInstanceId);
        // The root sheet holds no owned component: nothing to offer.
        Assert.AreEqual(BlockOwnershipSynchronization.OwnerUnresolved, requests[added[3].Id].Code);
        Assert.IsEmpty(requests[added[3].Id].CandidateBlockIds);
        // With U6 kept the CPU sheet shows the processor and the memory: the same new component is a request, not a guess
        // that changes with what else sits on the sheet.
        var withMemory = circuit with
        {
            Sheets = [.. circuit.Sheets.Select(s => s.Id == definitionsBySheet[added[1].SheetInstanceId]
                ? s with { Components = [.. s.Components, new ComponentDefinition(added[1].DefinitionId, r.Id, "R")] } : s)],
            Components = [.. circuit.Components, added[1]],
            Symbols = [.. circuit.Symbols, new SymbolOccurrence(Guid.NewGuid(), added[1].Id, 1, null)]
        };
        withMemory.Validate();
        var shared = BlockOwnershipSynchronization.Plan(graph, PsuCpuIds.Id(0x01, 2), design with { Engineering = components with { Circuit = withMemory } });
        Assert.IsEmpty(shared.Assignments);
        CollectionAssert.AreEqual(new[] { Block(1), Block(3), Block(8), Block(9) }, shared.Requests.Single().CandidateBlockIds.ToArray(),
            "System, CPU, Processor and Memory.");
        CollectionAssert.AreEqual(new[] { PsuCpuIds.Id(0x07, 8) }, plan.DetachedComponents.ToArray(), "U6 stays bound to Memory, detached.");
        // Must-catch: a component two blocks claim is a request, never silently kept or moved.
        var draft = graph.StartDraft(graph.Walk(graph.SelectedRoot).Single(s => s.BlockId == Block(5)));
        var twice = draft with { ComponentBindings = new([.. draft.EffectiveComponentBindings.Targets,
            new ComponentRealization(PsuCpuIds.Id(0x01, 2), circuit.Id, PsuCpuIds.Id(0x07, 3))]) };
        var path = graph.Walk(graph.SelectedRoot).Where(s => s.BlockId is var id && (id == Block(1) || id == Block(2) || id == Block(5))).ToImmutableArray();
        var claimed = graph.SaveDraft(graph.SelectedRoot, path, twice, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()],
            new(RequirementRevisionActor.User, "test", DateTimeOffset.UtcNow, "Claim R1 twice", [], [])).Graph;
        var conflict = BlockOwnershipSynchronization.Plan(claimed, PsuCpuIds.Id(0x01, 2), design);
        Assert.AreEqual(BlockOwnershipSynchronization.OwnerAmbiguous, conflict.Requests.Single().Code);
        Assert.AreEqual(PsuCpuIds.Id(0x07, 3), conflict.Requests.Single().ComponentId);
    }

    // The block-owner tools over the production MCP STDIO server, as an agent calls them (ledger p74ee7c1da24272d9). The
    // PSU/CPU ownership journey drives them against KiCad; this process-level case pins their arguments, refusals and file
    // effects without KiCad. The design is the PSU/CPU Components stage; the block graph leaves J1 (on the PSU sheet, whose
    // other parts four blocks own) and U6 (on the CPU sheet, whose other part only Processor owns) unowned. Lane-owned home
    // for these cases, so the parent's McpProcessTests seam is untouched.
    [TestMethod]
    public async Task BlockOwnerToolsOverStdioBindOnlyWhatExactIdentitiesDecide()
    {
        string root = Directory.CreateTempSubdirectory("kicad-block-owners-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            string recoveryPath = Path.Combine(root, "recovery.json"), blocksPath = Path.Combine(root, "system.blocks.xml");
            var design = SchematicRebuildTests.Placed();
            var saved = new DesignRecoveryStore(recoveryPath).Save(SchematicRebuildTests.State(design, design), null);
            Guid Block(int n) => PsuCpuIds.Id(0x11, n);
            var graph = PsuCpuFixture.Graph();
            foreach (int block in new[] { 2, 9 })
            {
                var path = PathTo(graph, Block(block));
                var draft = graph.StartDraft(path[^1]) with { ComponentBindings = new([]) };
                graph = graph.SaveDraft(graph.SelectedRoot, path, draft, Guid.NewGuid(), Guid.NewGuid(), [.. path.Skip(1).Select(_ => Guid.NewGuid())],
                    new(RequirementRevisionActor.User, "fixture", DateTimeOffset.UtcNow, "Leave its component unowned", [], [])).Graph;
            }
            await File.WriteAllTextAsync(blocksPath, RecursiveBlockGraphXml.Write(graph, RecursiveBlockGraphXml.SchemaVersion), timeout.Token);
            byte[] unowned = await File.ReadAllBytesAsync(blocksPath, timeout.Token);

            await using var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(), Path.Combine(root, "state"),
                Path.Combine(root, "host.log"), timeout.Token);
            var names = new List<string>(); string? cursor = null;
            do
            {
                var page = await host.ListTools(cursor);
                names.AddRange(page.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!));
                cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            } while (cursor is not null);
            CollectionAssert.Contains(names, "kicad_design_block_owners_plan");
            CollectionAssert.Contains(names, "kicad_design_block_owners_apply");

            object Owners(string? sha = null, string? designId = null, string? blocks = null) => sha is null
                ? new { instanceId = saved.State.InstanceId.ToString("D"), recoveryPath, expectedRevisionToken = saved.RevisionToken,
                    blockGraphPath = blocks ?? blocksPath, designId = designId ?? PsuCpuIds.Id(0x01, 2).ToString("D") }
                : new { instanceId = saved.State.InstanceId.ToString("D"), recoveryPath, expectedRevisionToken = saved.RevisionToken,
                    blockGraphPath = blocks ?? blocksPath, designId = designId ?? PsuCpuIds.Id(0x01, 2).ToString("D"), expectedBlockGraphSha256 = sha };
            static JsonElement Success(JsonElement result)
            {
                Assert.IsFalse(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
                return result.GetProperty("structuredContent");
            }
            static string? Code(JsonElement result) => result.GetProperty("structuredContent").GetProperty("errorCode").GetString();

            var plan = Success(await host.Tool("kicad_design_block_owners_plan", Owners()));
            var assignment = plan.GetProperty("assignments").EnumerateArray().Single();
            Assert.AreEqual(PsuCpuIds.Id(0x07, 8), assignment.GetProperty("componentId").GetGuid());
            Assert.AreEqual(Block(8), assignment.GetProperty("blockId").GetGuid(), "U6: everything else on the CPU sheet belongs to Processor.");
            var request = plan.GetProperty("resolutionRequests").EnumerateArray().Single();
            Assert.AreEqual(BlockOwnershipSynchronization.OwnerUnresolved, request.GetProperty("code").GetString());
            Assert.AreEqual(PsuCpuIds.Id(0x07, 1), request.GetProperty("componentId").GetGuid());
            CollectionAssert.AreEqual(new[] { Block(1), Block(2), Block(4), Block(5), Block(6), Block(7) },
                request.GetProperty("candidateBlockIds").EnumerateArray().Select(e => e.GetGuid()).ToArray(),
                "J1: four blocks own the PSU sheet's other parts, so the person chooses among them and their ancestors.");
            Assert.IsTrue(plan.GetProperty("resolutionRequired").GetBoolean());
            string sha = plan.GetProperty("blockGraphSha256").GetString()!;

            // Refusals write nothing: a changed file, an invalid design identity, a relative path.
            Assert.AreEqual("recursive_block_file_changed", Code(await host.Tool("kicad_design_block_owners_apply", Owners(new string('0', 64)))));
            Assert.AreEqual("invalid_block_design", Code(await host.Tool("kicad_design_block_owners_plan", Owners(designId: "not-a-design"))));
            Assert.AreEqual("invalid_block_graph_path", Code(await host.Tool("kicad_design_block_owners_plan", Owners(blocks: "system.blocks.xml"))));
            CollectionAssert.AreEqual(unowned, await File.ReadAllBytesAsync(blocksPath, timeout.Token), "Refusals write nothing.");

            // Apply binds what is decided and leaves the request for the person.
            var applied = Success(await host.Tool("kicad_design_block_owners_apply", Owners(sha)));
            Assert.IsTrue(applied.GetProperty("blockGraphWritten").GetBoolean());
            Assert.AreEqual(Block(8), applied.GetProperty("savedBlocks").EnumerateArray().Single().GetProperty("blockId").GetGuid());
            Assert.AreEqual(PsuCpuIds.Id(0x07, 1), applied.GetProperty("resolutionRequests").EnumerateArray().Single().GetProperty("componentId").GetGuid());
            var owned = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(blocksPath, timeout.Token));
            Guid[] Bound(int block) => [.. owned.Inspect(owned.Walk(owned.SelectedRoot).Single(s => s.BlockId == Block(block)))
                .EffectiveComponentBindings.Targets.Select(t => t.ComponentId)];
            CollectionAssert.AreEqual(new[] { PsuCpuIds.Id(0x07, 7), PsuCpuIds.Id(0x07, 8) }.Order().ToArray(), Bound(8).Order().ToArray());
            Assert.IsFalse(owned.Walk(owned.SelectedRoot).Any(s => owned.Inspect(s).EffectiveComponentBindings.Targets.Any(t => t.ComponentId == PsuCpuIds.Id(0x07, 1))),
                "J1 stays unowned until the person binds it.");
            string ownedSha = applied.GetProperty("blockGraphSha256").GetString()!;
            byte[] ownedBytes = await File.ReadAllBytesAsync(blocksPath, timeout.Token);
            var repeat = Success(await host.Tool("kicad_design_block_owners_apply", Owners(ownedSha)));
            Assert.IsFalse(repeat.GetProperty("blockGraphWritten").GetBoolean(), "A repeat is a no-op.");
            Assert.AreEqual(ownedSha, repeat.GetProperty("blockGraphSha256").GetString());
            CollectionAssert.AreEqual(ownedBytes, await File.ReadAllBytesAsync(blocksPath, timeout.Token));

            // The worker keeps block ownership only with both the block graph and the design identity.
            var half = await host.Tool("kicad_design_automatic_sync_start", new { instanceId = Guid.NewGuid().ToString("D"), recoveryPath,
                designPath = Path.Combine(root, "design.xml"), expectedRecoveryRevision = "token", blockGraphPath = blocksPath });
            Assert.AreEqual("invalid_automatic_sync_target", Code(half), "Block ownership needs both the block graph and the design identity.");
        }
        finally { Directory.Delete(root, true); }

        static ImmutableArray<BlockSelection> PathTo(RecursiveBlockGraph graph, Guid block)
        {
            var path = new List<BlockSelection>();
            bool Visit(BlockSelection selection)
            {
                path.Add(selection);
                if (selection.BlockId == block || graph.Inspect(selection).Children.Any(Visit)) return true;
                path.RemoveAt(path.Count - 1);
                return false;
            }
            Assert.IsTrue(Visit(graph.SelectedRoot));
            return [.. path];
        }
    }
}
