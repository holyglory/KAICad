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

        // The native repeated-screen journey covers publication and history. Extend
        // this exact ownership fixture for ambiguous answers and contradictory parts.
        var (repeated, placed, _) = PlacedResistor(s => SchematicRebuildTests.WithRepeatedSheet(WithSense(s)));
        var observed = repeated.Observed.Clone();
        var firstScreen = observed.Instances.Single(s => s.Items.Any(i => i.Is(SchematicSymbolInstance.Descriptor)
            && i.Unpack<SchematicSymbolInstance>().Id.Equals(placed.Id)));
        var sibling = observed.Instances.Single(s => s.Metadata.ScreenId.Equals(firstScreen.Metadata.ScreenId)
            && !s.Metadata.Document.Equals(firstScreen.Metadata.Document));
        var paths = new[] { firstScreen, sibling }.Select(s => s.Metadata.Document.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray()).ToArray();
        foreach (var record in placed.InstanceRecords.Records)
            record.Reference = record.Path.SequenceEqual(sibling.Metadata.Document.SheetPath.Path) ? "R202" : "R2";
        for (int i = 0; i < firstScreen.Items.Count; ++i)
            if (firstScreen.Items[i].Is(SchematicSymbolInstance.Descriptor)
                && firstScreen.Items[i].Unpack<SchematicSymbolInstance>().Id.Equals(placed.Id)) firstScreen.Items[i] = Any.Pack(placed);
        var alias = placed.Clone(); alias.Path = sibling.Metadata.Document.SheetPath.Clone(); alias.ReferenceField.Text.Text_ = "R202";
        sibling.Items.Add(Any.Pack(alias));
        repeated = Checkpointed(repeated.Baseline, observed);
        Guid placedId = Guid.Parse(placed.Id.Value);
        var ambiguous = SchematicNativeAdditionProjection.Project(repeated, [], CancellationToken.None);
        Assert.AreEqual(SchematicNativeAdditionProjection.ResolutionRequired, ambiguous.ErrorCode);
        Assert.HasCount(2, ambiguous.Requests);
        var noPath = SchematicNativeAdditionProjection.Answer(repeated, [], [new(placedId, PartId: SensePart)], CancellationToken.None);
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid, noPath.ErrorCode, "Repeated native UUIDs require a path.");
        SchematicOwnershipAnswer Choice(int index, Guid partId) => new(placedId, PartId: partId) { NativePath = paths[index] };
        var partial = SchematicNativeAdditionProjection.Answer(repeated, [], [Choice(0, SensePart)], CancellationToken.None);
        Assert.IsNull(partial.Answered); Assert.HasCount(1, partial.Requests);
        var conflicting = SchematicNativeAdditionProjection.Answer(repeated, [],
            [Choice(0, SensePart), Choice(1, PsuCpuIds.Id(0x03, 3))], CancellationToken.None);
        Assert.IsNull(conflicting.Answered); Assert.AreEqual(SchematicNativeAdditionProjection.AnswerMismatch, conflicting.ErrorCode);
        var agreed = SchematicNativeAdditionProjection.Answer(repeated, [], [Choice(0, SensePart), Choice(1, SensePart)], CancellationToken.None);
        Assert.IsNotNull(agreed.Answered, agreed.ErrorCode + ": " + agreed.ErrorMessage);
        agreed.Answered.Engineering.Validate([]);
        var newComponents = agreed.Answered.Engineering.Circuit.Components
            .Where(c => !repeated.Baseline.Engineering.Circuit.Components.Any(b => b.Id == c.Id)).ToArray();
        Assert.HasCount(2, newComponents); Assert.HasCount(1, newComponents.Select(c => c.DefinitionId).Distinct());
        CollectionAssert.AreEquivalent(new[] { "R2", "R202" }, newComponents.Select(c => c.Reference).ToArray());
        var declaredState = repeated with { DesiredFileBytes = Xml(agreed.Answered) };
        var declaredResult = SchematicNativeAdditionProjection.Project(declaredState, [], agreed.Answered, null, CancellationToken.None);
        Assert.IsNotNull(declaredResult.Adoption, declaredResult.ErrorCode + ": " + declaredResult.ErrorMessage);
        CollectionAssert.AreEquivalent(newComponents, declaredResult.Adoption.BindingCandidate.Engineering.Circuit.Components
            .Where(c => newComponents.Any(n => n.Id == c.Id)).ToArray());

        // Inserting an entire known physical sheet reuses its existing component
        // definitions. The binding identity decides ownership, not the new references.
        var knownSheet = SchematicRebuildTests.Placed();
        var repeatedSheet = SchematicRebuildTests.WithRepeatedSheet(knownSheet);
        var insertedState = Checkpointed(knownSheet, repeatedSheet.Schematic);
        var inserted = SchematicNativeAdditionProjection.Project(insertedState, [], CancellationToken.None);
        Assert.IsNotNull(inserted.Adoption, inserted.ErrorCode + ": " + inserted.ErrorMessage);
        var adoptedSheet = inserted.Adoption.BindingCandidate;
        Guid newSheet = inserted.Adoption.AddedSheetInstances.Single();
        var templateComponents = knownSheet.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == PsuCpuIds.Id(0x05, 2)).ToArray();
        var insertedComponents = adoptedSheet.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == newSheet).ToArray();
        CollectionAssert.AreEquivalent(templateComponents.Select(c => c.DefinitionId).ToArray(), insertedComponents.Select(c => c.DefinitionId).ToArray());
        Assert.AreEqual(knownSheet.Engineering.Circuit.Sheets.Count, adoptedSheet.Engineering.Circuit.Sheets.Count);
        Assert.AreEqual(templateComponents.Length, inserted.Adoption.AddedComponents.Count);
        Assert.IsTrue(SchematicDesignBindings.Inspect(adoptedSheet, []).IdentitiesResolved);
        var secondPlan = SchematicNativeAdditionProjection.Project(insertedState, [], CancellationToken.None);
        CollectionAssert.AreEqual(inserted.Adoption.AddedComponents.ToArray(), secondPlan.Adoption!.AddedComponents.ToArray());
        CollectionAssert.AreEqual(inserted.Adoption.AddedOccurrences.ToArray(), secondPlan.Adoption.AddedOccurrences.ToArray());

        // A model component deliberately left undrawn has no native reference for
        // the new instance. Preserve that unresolved choice instead of guessing it.
        var undrawnComponent = templateComponents[0];
        var undrawnIds = knownSheet.Engineering.Circuit.Symbols.Where(s => s.ComponentId == undrawnComponent.Id).Select(s => s.Id).ToHashSet();
        var nativeUndrawn = knownSheet.SymbolBindings.Where(b => undrawnIds.Contains(b.SymbolOccurrenceId)).Select(b => b.NativeObjectId.ToString("D")).ToHashSet();
        var undrawn = knownSheet with { Schematic = knownSheet.Schematic.Clone(), Engineering = knownSheet.Engineering with
            { Circuit = knownSheet.Engineering.Circuit with { Symbols = knownSheet.Engineering.Circuit.Symbols.Where(s => !undrawnIds.Contains(s.Id)).ToArray() } },
            SymbolBindings = knownSheet.SymbolBindings.Where(b => !undrawnIds.Contains(b.SymbolOccurrenceId)).ToArray() };
        foreach (var sheet in undrawn.Schematic.Instances)
            for (int i = sheet.Items.Count - 1; i >= 0; --i)
                if (sheet.Items[i].Is(SchematicSymbolInstance.Descriptor) && nativeUndrawn.Contains(sheet.Items[i].Unpack<SchematicSymbolInstance>().Id.Value))
                    sheet.Items.RemoveAt(i);
        var undrawnRepeat = SchematicRebuildTests.WithRepeatedSheet(undrawn);
        var undrawnState = Checkpointed(undrawn, undrawnRepeat.Schematic);
        var unresolved = SchematicNativeAdditionProjection.Project(undrawnState, [], CancellationToken.None);
        Assert.IsNull(unresolved.Adoption);
        Assert.AreEqual(SchematicNativeAdditionProjection.ResolutionRequired, unresolved.ErrorCode);
        var referenceRequest = unresolved.SheetComponentRequests.Single();
        Assert.AreEqual(undrawnComponent.DefinitionId, referenceRequest.ComponentDefinitionId);
        var explicitReference = new SchematicSheetComponentReference(referenceRequest.SheetInstanceId,
            referenceRequest.ComponentDefinitionId, "UNPLACED301");
        var chosen = undrawnState with { RepeatedSheetResolution = new(SchematicRepeatedSheetChoices.SnapshotToken(undrawnState),
            [explicitReference], []) };
        var completeRepeated = SchematicNativeAdditionProjection.Project(chosen, [], CancellationToken.None);
        Assert.IsNotNull(completeRepeated.Adoption, completeRepeated.ErrorCode + ": " + completeRepeated.ErrorMessage);
        var referenceOwner = completeRepeated.Adoption.BindingCandidate.Engineering.Circuit.Components.Single(c => c.Id == referenceRequest.ProposedComponentId);
        Assert.AreEqual(explicitReference.Reference, referenceOwner.Reference);
        Assert.IsFalse(completeRepeated.Adoption.BindingCandidate.Engineering.Circuit.Symbols.Any(s => s.ComponentId == referenceOwner.Id));
        var changedReference = chosen with { NativeRevision = chosen.NativeRevision with { Sequence = chosen.NativeRevision.Sequence + 1 } };
        Assert.IsNull(SchematicRepeatedSheetChoices.Current(changedReference), "Choices bind the complete observed revision.");
        var duplicateReference = chosen with { RepeatedSheetResolution = chosen.RepeatedSheetResolution! with
            { ComponentReferences = [explicitReference with { Reference = undrawnComponent.Reference }] } };
        Assert.IsNull(SchematicNativeAdditionProjection.Project(duplicateReference, [], CancellationToken.None).Adoption,
            "A repeated component cannot copy the earlier instance's reference.");
        var unknownReference = chosen with { RepeatedSheetResolution = chosen.RepeatedSheetResolution! with
            { ComponentReferences = [explicitReference with { ComponentDefinitionId = Guid.NewGuid() }] } };
        Assert.AreEqual(SchematicRepeatedSheetChoices.Invalid,
            SchematicNativeAdditionProjection.Project(unknownReference, [], CancellationToken.None).ErrorCode);
        // A partial answer is useful, but it cannot retain a conflicting assigned
        // reference just because another undrawn component is still unanswered.
        var ownerDefinition = undrawn.Engineering.Circuit.SheetInstances.Single(s => s.Id == undrawnComponent.SheetInstanceId).DefinitionId;
        var otherDefinition = undrawn.Engineering.Circuit.Sheets.SelectMany(s => s.Components)
            .Single(c => c.Id == undrawnComponent.DefinitionId) with { Id = Guid.NewGuid() };
        var twoUndrawn = undrawn with { Engineering = undrawn.Engineering with { Circuit = undrawn.Engineering.Circuit with
        { Sheets = undrawn.Engineering.Circuit.Sheets.Select(s => s.Id == ownerDefinition
            ? s with { Components = [.. s.Components, otherDefinition] } : s).ToArray(),
            Components = [.. undrawn.Engineering.Circuit.Components,
                new(Guid.NewGuid(), otherDefinition.Id, undrawnComponent.SheetInstanceId, "UNPLACED101")] } } };
        var partialState = Checkpointed(twoUndrawn, undrawnRepeat.Schematic);
        var partialChoices = new DesignRepeatedSheetResolution(SchematicRepeatedSheetChoices.SnapshotToken(partialState), [explicitReference], []);
        var partialPlan = SchematicNativeAdditionProjection.Project(partialState with { RepeatedSheetResolution = partialChoices }, [], CancellationToken.None);
        Assert.AreEqual(SchematicNativeAdditionProjection.ResolutionRequired, partialPlan.ErrorCode);
        Assert.HasCount(1, partialPlan.SheetComponentRequests);
        Assert.AreEqual(otherDefinition.Id, partialPlan.SheetComponentRequests.Single().ComponentDefinitionId);
        Assert.AreEqual("native_addition_conflict", SchematicNativeAdditionProjection.Project(partialState with
            { RepeatedSheetResolution = partialChoices with { ComponentReferences = [explicitReference with { Reference = "UNPLACED101" }] } },
            [], CancellationToken.None).ErrorCode);
        var unannotatedChoices = partialChoices with { ComponentReferences = [explicitReference with { Reference = "R?" },
            new(explicitReference.SheetInstanceId, otherDefinition.Id, "R?")] };
        var unannotated = SchematicNativeAdditionProjection.Project(partialState with { RepeatedSheetResolution = unannotatedChoices }, [], CancellationToken.None);
        Assert.IsNotNull(unannotated.Adoption, "Deliberately unannotated references remain repeatable: " + unannotated.ErrorMessage);
    }

    [TestMethod]
    public void AnUndecidablePartIsARequestAndANewLibrarySymbolIsANewPart()
    {
        // Two parts drawn with the same library symbol and pins: the new symbol could be either.
        var (ambiguous, added, _) = PlacedResistor(WithSense);
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

    // A second part drawn with the fixture's library resistor and exactly its pins.
    private static readonly Guid SensePart = Guid.Parse("5e115e00-0000-4000-8000-00000000000a");

    private static SchematicDesign WithSense(SchematicDesign baseline)
    {
        var circuit = baseline.Engineering.Circuit;
        var r = circuit.Parts.Single(p => p.Id == PsuCpuIds.Id(0x03, 3));
        var declaration = baseline.PartSymbols!.Single(p => p.PartId == r.Id) with { PartId = SensePart };
        return baseline with { Engineering = baseline.Engineering with { Circuit = circuit with { Parts = [.. circuit.Parts, r with { Id = SensePart, Name = "R_sense" }] } },
            PartSymbols = [.. baseline.PartSymbols!, declaration] };
    }

    // A part with exactly the fixture resistor's units and pins, declared with another library symbol: a capacitor's.
    private static readonly Guid CapacitorPart = Guid.Parse("c0000000-0000-4000-8000-00000000000c");

    private static SchematicDesign WithCapacitor(SchematicDesign baseline)
    {
        var circuit = baseline.Engineering.Circuit;
        var r = circuit.Parts.Single(p => p.Id == PsuCpuIds.Id(0x03, 3));
        var declaration = baseline.PartSymbols!.Single(p => p.PartId == r.Id) with
            { PartId = CapacitorPart, LibraryId = new() { LibraryNickname = "Device", EntryName = "C" } };
        return baseline with { Engineering = baseline.Engineering with { Circuit = circuit with { Parts = [.. circuit.Parts, r with { Id = CapacitorPart, Name = "C" }] } },
            PartSymbols = [.. baseline.PartSymbols!, declaration] };
    }

    // The XML a person writes to answer with a part of their own that the XML adds: the resistor's units and pins, declared
    // with the library symbol they name (none: no declaration).
    private static SchematicDesign WithOwnPart(SchematicDesign design, Guid part, string? library)
    {
        var circuit = design.Engineering.Circuit;
        var r = circuit.Parts.Single(p => p.Id == PsuCpuIds.Id(0x03, 3));
        var added = design with { Engineering = design.Engineering with { Circuit = circuit with { Parts = [.. circuit.Parts, r with { Id = part, Name = "R_own" }] } } };
        if (library is null) return added;
        var declaration = design.PartSymbols!.Single(p => p.PartId == r.Id) with
            { PartId = part, LibraryId = new() { LibraryNickname = library.Split(':')[0], EntryName = library.Split(':')[1] } };
        return added with { PartSymbols = [.. design.PartSymbols!, declaration] };
    }

    private static byte[] Xml(SchematicDesign design) => Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, []));

    // The XML a person writes to answer that the symbol placed in KiCad is a new component on the PSU sheet: identities of
    // their own choosing, the part they choose, and no placement unless given.
    private static (SchematicDesign Design, Guid Component, Guid Occurrence) HandAnswer(DesignRecoveryState state, Guid native, Guid part,
        string value, string reference = "R2", SymbolPlacement? placement = null)
    {
        var design = DesignRecoveryStore.ReadDesired(state); var circuit = design.Engineering.Circuit;
        Guid sheet = PsuCpuIds.Id(0x05, 2), sheetDefinition = circuit.SheetInstances.Single(i => i.Id == sheet).DefinitionId;
        Guid definition = Guid.NewGuid(), component = Guid.NewGuid(), occurrence = Guid.NewGuid();
        return (design with
        {
            Engineering = design.Engineering with { Circuit = circuit with
            {
                Sheets = [.. circuit.Sheets.Select(s => s.Id == sheetDefinition ? s with { Components = [.. s.Components, new(definition, part, value)] } : s)],
                Components = [.. circuit.Components, new(component, definition, sheet, reference)],
                Symbols = [.. circuit.Symbols, new(occurrence, component, 1, placement)]
            } },
            SymbolBindings = [.. design.SymbolBindings, new(occurrence, native)]
        }, component, occurrence);
    }

    // A person's answers to resolution requests (ledger p35cfdc0345e056a5), and planning a design never synchronized (ledger
    // p95b6c94e732b880a). The PSU/CPU ownership journey answers an undecidable part with kicad_design_ownership_answer while
    // the automatic worker waits, and adopts a symbol placed before a first synchronization, against KiCad. These offline
    // cases pin what the journey cannot reach cheaply: an answer written by hand with the person's own identities, every
    // refusal, and the planner seams the answer passes through. No existing test could be extended: before this item a
    // request could only stay paused, and a never-synchronized design could not plan an added symbol.
    [TestMethod]
    public async Task AnAnswerInTheXmlOrFromTheToolAdoptsAnUndecidableSymbolAsAnswered()
    {
        var token = CancellationToken.None;
        var (state, added, path) = PlacedResistor(WithSense);
        Assert.IsNull(state.LastSynchronization, "The fixture record has never synchronized.");
        Guid native = Guid.Parse(added.Id.Value), circuit = state.Baseline.Engineering.Circuit.Id;
        Guid proposed = SchematicNativeAdditionProjection.AdoptedIdentity("component", circuit, path, native);
        Guid occurrence = SchematicNativeAdditionProjection.AdoptedIdentity("occurrence", circuit, path, native);
        string value = added.ValueField.Text.Text_;
        var requested = SchematicNetReconciliation.Plan(state, [], token);
        Assert.AreEqual(SchematicNativeAdditionProjection.ResolutionRequired, requested.ErrorCode);
        StringAssert.Contains(requested.ErrorMessage, SchematicNativeAdditionProjection.AnswerTool, "The refusal says how to answer.");
        Assert.AreEqual(proposed, requested.ResolutionRequests!.Single().ProposedComponentId, "The request names the component R2 would be.");

        // The tool's answer declares R2 as an R_sense in the saved XML, and changes nothing else.
        var answer = SchematicNativeAdditionProjection.Answer(state, [], [new(native, PartId: SensePart)], token);
        Assert.IsNotNull(answer.Answered, answer.ErrorCode + ": " + answer.ErrorMessage);
        CollectionAssert.AreEqual(new[] { native }, answer.DeclaredSymbols.ToArray());
        var saved = DesignRecoveryStore.ReadDesired(state);
        var declared = answer.Answered.Engineering.Circuit;
        CollectionAssert.AreEqual(saved.Engineering.Circuit.Parts.Select(p => p.Id).ToArray(), declared.Parts.Select(p => p.Id).ToArray(),
            "An existing part was chosen: no part is added.");
        CollectionAssert.AreEqual(new[] { proposed }, declared.Components.Select(c => c.Id).Except(saved.Engineering.Circuit.Components.Select(c => c.Id)).ToArray());
        CollectionAssert.AreEqual(new[] { new SchematicSymbolBinding(occurrence, native) }, answer.Answered.SymbolBindings.Except(saved.SymbolBindings).ToArray());
        Assert.AreEqual(EngineeringDesignXml.Write(saved.Engineering with { Circuit = declared }, []), EngineeringDesignXml.Write(answer.Answered.Engineering, []),
            "Only the circuit gains the answer.");
        var answeredState = state with { DesiredFileBytes = Xml(answer.Answered) };

        // Planned from the saved record, as apply and the worker plan it. The record has never synchronized, so planning
        // continues with an empty history (seam 2); binding a new occurrence to a symbol KiCad already shows is no XML
        // creation although the XML adds a component (seam 4); sheets keep their bindings (seam 3).
        Assert.IsTrue(SchematicNativeCreationProjection.IsSupportedAddition(state.Baseline, answer.Answered.Engineering));
        string directory = Directory.CreateTempSubdirectory("kicad-ownership-answer-").FullName;
        try
        {
            var store = new DesignRecoveryStore(Path.Combine(directory, "recovery.json"));
            var record = store.Save(answeredState, null);
            var plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(store, record);
            Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
            Assert.IsEmpty(plan.NativeOperations, "KiCad already shows R2; nothing is sent to it.");
            Assert.IsFalse(plan.NativeRebuildRequired); Assert.IsFalse(plan.NativeConnectionRealizationRequired);
            var candidate = plan.Candidate!.Engineering.Circuit;
            var component = candidate.Components.Single(c => c.Id == proposed);
            Assert.AreEqual("R2", component.Reference);
            Assert.AreEqual(SensePart, candidate.Sheets.SelectMany(s => s.Components).Single(d => d.Id == component.DefinitionId).PartId);
            Assert.AreEqual(SchematicModelProjection.Placement(added), candidate.Symbols.Single(s => s.Id == occurrence).Placement);
            CollectionAssert.AreEqual(new[] { occurrence }, plan.Electrical!.AddedSymbolOccurrences!.ToArray());
            CollectionAssert.AreEqual(state.Baseline.SheetBindings.Select(b => (b.SheetInstanceId, SchematicDesignBindings.PathKey(b.NativePath))).ToArray(),
                plan.Candidate.SheetBindings.Select(b => (b.SheetInstanceId, SchematicDesignBindings.PathKey(b.NativePath))).ToArray());
            Assert.AreEqual(plan.CandidateXml, (await SchematicSynchronizationPlanner.PlanForExecutionWithHistoryAsync(store, record)).CandidateXml,
                "Apply and the worker plan the same design.");
            // Once published, the answered design is settled: planning it again changes nothing.
            var settled = SchematicSynchronizationPlanner.Plan(answeredState with { Baseline = plan.Candidate, DesiredFileBytes = Encoding.UTF8.GetBytes(plan.CandidateXml!),
                BaselineElectrical = answeredState.ObservedElectrical!.Clone() });
            Assert.IsTrue(settled.CanPrepare, settled.ErrorCode + ": " + settled.ErrorMessage);
            Assert.IsEmpty(settled.NativeOperations); Assert.IsNull(settled.Electrical!.AddedComponents);
            Assert.AreEqual(plan.CandidateXml, settled.CandidateXml);
            // A design synchronized without content-verified retained XML still refuses the placement (fails closed).
            var unverified = store.Save(record.State with { LastSynchronization = new(2, Guid.NewGuid(), record.State.InstanceId,
                Path.Combine(directory, "design.xml"), new string('a', 64), new string('b', 64), Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"), record.State.NativeRevision.Sequence, false, true, null, null, null) }, record.RevisionToken);
            Assert.AreEqual("unverified_native_ownership_history", (await SchematicSynchronizationPlanner.PlanWithHistoryAsync(store, unverified)).ErrorCode);
            // A record created again at the path of one that synchronized has no synchronization of its own, but the receipts
            // archived beside it show this instance synchronized there: it fails closed too. Another instance's receipts do not.
            var again = new DesignRecoveryStore(Path.Combine(directory, "again.json"));
            var archive = new DesignSynchronizationReceipts(again.StatePath);
            var receipt = unverified.State.LastSynchronization!;
            archive.Archive(receipt with { OperationId = Guid.NewGuid(), InstanceId = Guid.NewGuid() });
            var recreated = again.Save(answeredState, null);
            var otherInstance = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(again, recreated);
            Assert.IsTrue(otherInstance.CanPrepare, otherInstance.ErrorCode + ": " + otherInstance.ErrorMessage);
            archive.Archive(receipt);
            Assert.AreEqual("unverified_native_ownership_history", (await SchematicSynchronizationPlanner.PlanWithHistoryAsync(again, recreated)).ErrorCode);
        }
        finally { Directory.Delete(directory, true); }

        // An answer written by hand, with the person's own identities and no placement, is taken exactly as declared.
        var (own, ownComponent, ownOccurrence) = HandAnswer(state, native, SensePart, value);
        var hand = SchematicNetReconciliation.Plan(state with { DesiredFileBytes = Xml(own) }, [], token);
        Assert.IsNotNull(hand.Candidate, hand.ErrorCode + ": " + hand.ErrorMessage);
        Assert.AreEqual("R2", hand.Candidate.Circuit.Components.Single(c => c.Id == ownComponent).Reference);
        Assert.AreEqual(SchematicModelProjection.Placement(added), hand.Candidate.Circuit.Symbols.Single(s => s.Id == ownOccurrence).Placement,
            "The placement KiCad shows fills the one the answer leaves out.");
        CollectionAssert.AreEqual(new[] { ownOccurrence }, hand.Restoration!.AnsweredOccurrences.ToArray());
        Assert.IsFalse(hand.Candidate.Circuit.Components.Any(c => c.Id == proposed), "No derived identity is invented beside the person's.");

        // Refusals, before KiCad or the XML changes: an answer that disagrees with KiCad, an XML that changes something else
        // too, and a binding to a symbol KiCad does not show as new.
        string? Planned(SchematicDesign xml) => SchematicNetReconciliation.Plan(state with { DesiredFileBytes = Xml(xml) }, [], token).ErrorCode;
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerMismatch, Planned(HandAnswer(state, native, SensePart, value, reference: "R9").Design));
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerMismatch, Planned(HandAnswer(state, native, SensePart, "10k").Design));
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerMismatch, Planned(HandAnswer(state, native, SensePart, value,
            placement: new SymbolPlacement(1.27m, 1.27m, 0, false, false, false)).Design));
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerMismatch, Planned(HandAnswer(state, native, PsuCpuIds.Id(0x03, 1), value).Design),
            "The connector part does not have the resistor's pins.");
        var renamed = own with { Engineering = own.Engineering with { Circuit = own.Engineering.Circuit with { Components = [.. own.Engineering.Circuit.Components
            .Select(c => c.Id == PsuCpuIds.Id(0x07, 2) ? c with { Reference = "U10" } : c)] } } };
        Assert.AreEqual("ownership_change_with_xml_edits", Planned(renamed));
        Guid r1 = state.Baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == PsuCpuIds.Id(0x09, 3)).NativeObjectId;
        Assert.AreEqual("electrical_ownership_changed", Planned(HandAnswer(state, r1, SensePart, value).Design),
            "R1's symbol is no symbol placed since the last synchronization.");

        // Tool answers that are not one of the request's choices are refused; a missing part leaves the request open.
        var invalid = new SchematicOwnershipAnswer[][]
        {
            [], [new(native, PartId: PsuCpuIds.Id(0x03, 2))], [new(Guid.NewGuid(), PartId: SensePart)],
            [new(native, PartId: SensePart, ComponentId: PsuCpuIds.Id(0x07, 3))], [new(native, PartId: SensePart), new(native, PartId: SensePart)]
        };
        foreach (var answers in invalid)
        {
            var refused = SchematicNativeAdditionProjection.Answer(state, [], answers, token);
            Assert.IsNull(refused.Answered); Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid, refused.ErrorCode, refused.ErrorMessage);
        }
        var open = SchematicNativeAdditionProjection.Answer(state, [], [new(native)], token);
        Assert.AreEqual(SchematicNativeAdditionProjection.ResolutionRequired, open.ErrorCode);
        Assert.AreEqual(native, open.Requests.Single().NativeObjectId);
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid,
            SchematicNativeAdditionProjection.Answer(state with { DesiredFileBytes = Xml(answer.Answered) }, [], [new(native, PartId: SensePart)], token).ErrorCode,
            "A symbol the XML already answers is not answered twice.");

        // A part with exactly the resistor's pins but drawn with another library symbol (a capacitor) is none of R2's
        // choices, through the tool or in the XML; nor may an answer override the part exact identities decide.
        var (withCapacitor, placed, _) = PlacedResistor(d => WithCapacitor(WithSense(d)));
        Guid placedNative = Guid.Parse(placed.Id.Value);
        string placedValue = placed.ValueField.Text.Text_;
        var offered = SchematicNetReconciliation.Plan(withCapacitor, [], token).ResolutionRequests!.Single();
        CollectionAssert.AreEquivalent(new[] { PsuCpuIds.Id(0x03, 3), SensePart }, offered.CandidatePartIds.ToArray(), "The capacitor is not offered.");
        var capacitorAnswer = SchematicNativeAdditionProjection.Answer(withCapacitor, [], [new(placedNative, PartId: CapacitorPart)], token);
        Assert.IsNull(capacitorAnswer.Answered);
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid, capacitorAnswer.ErrorCode, capacitorAnswer.ErrorMessage);
        string? PlannedWith(DesignRecoveryState source, SchematicDesign xml) => SchematicNetReconciliation.Plan(source with { DesiredFileBytes = Xml(xml) }, [], token).ErrorCode;
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerMismatch, PlannedWith(withCapacitor, HandAnswer(withCapacitor, placedNative, CapacitorPart, placedValue).Design),
            "The XML cannot make the resistor KiCad draws a capacitor.");
        Assert.IsNull(PlannedWith(withCapacitor, HandAnswer(withCapacitor, placedNative, SensePart, placedValue).Design), "One of the choices is taken.");
        var (exact, exactPlaced, _) = PlacedResistor(WithCapacitor);
        var decided = SchematicNetReconciliation.Plan(exact, [], token);
        Assert.IsNotNull(decided.Candidate, "Without R_sense, R2 is the fixture's R by exact identity: " + decided.ErrorCode);
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid, SchematicNativeAdditionProjection.Answer(exact, [],
            [new(Guid.Parse(exactPlaced.Id.Value), PartId: CapacitorPart)], token).ErrorCode, "An answer cannot override the part exact identities decide.");
        // A part the XML adds itself is an answer only when the XML declares it with the library symbol KiCad draws.
        Guid ownPart = Guid.Parse("0e0e0e0e-0000-4000-8000-00000000000e");
        SchematicDesign OwnPartAnswer(string? library) => HandAnswer(state with { DesiredFileBytes = Xml(WithOwnPart(DesignRecoveryStore.ReadDesired(state), ownPart, library)) },
            native, ownPart, value).Design;
        Assert.IsNull(PlannedWith(state, OwnPartAnswer("Device:R")), "A new part declared with the resistor's library symbol is taken as declared.");
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerMismatch, PlannedWith(state, OwnPartAnswer("Device:C")));
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerMismatch, PlannedWith(state, OwnPartAnswer(null)), "A new part without a library symbol says nothing.");
    }

    // kicad_design_ownership_answer over the production MCP STDIO server, as an agent calls it (ledger p35cfdc0345e056a5). The
    // PSU/CPU ownership journey drives it against KiCad while the automatic worker waits; this process-level case pins its
    // arguments, refusals and file effects without KiCad. Lane-owned home, so the parent's McpProcessTests seam is untouched.
    [TestMethod]
    public async Task OwnershipAnswersOverStdioDeclareExactlyWhatThePersonChose()
    {
        string root = Directory.CreateTempSubdirectory("kicad-ownership-answer-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var (state, added, path) = PlacedResistor(d => WithCapacitor(WithSense(d)));
            string recoveryPath = Path.Combine(root, "recovery.json"), designPath = Path.Combine(root, "design.xml");
            await File.WriteAllBytesAsync(designPath, state.DesiredFileBytes, timeout.Token);
            var store = new DesignRecoveryStore(recoveryPath);
            var saved = store.Save(state, null);
            Guid native = Guid.Parse(added.Id.Value);
            Guid proposed = SchematicNativeAdditionProjection.AdoptedIdentity("component", state.Baseline.Engineering.Circuit.Id, path, native);

            await using var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(), Path.Combine(root, "state"),
                Path.Combine(root, "host.log"), timeout.Token);
            var names = new List<string>(); string? cursor = null;
            do
            {
                var page = await host.ListTools(cursor);
                names.AddRange(page.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!));
                cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            } while (cursor is not null);
            CollectionAssert.Contains(names, SchematicNativeAdditionProjection.AnswerTool);

            object Args(object[] answers, string? token = null, string? design = null) => new { instanceId = state.InstanceId.ToString("D"),
                recoveryPath, expectedRevisionToken = token ?? store.Read()!.RevisionToken, designPath = design ?? designPath, answers };
            static JsonElement Success(JsonElement result)
            {
                Assert.IsFalse(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
                return result.GetProperty("structuredContent");
            }
            static string? Code(JsonElement result) => result.GetProperty("structuredContent").GetProperty("errorCode").GetString();
            byte[] before = await File.ReadAllBytesAsync(designPath, timeout.Token);

            // Refusals write nothing: not one of the choices (the regulator's pins, the capacitor's library symbol), no answer, a
            // stale record, a relative path, a missing part.
            Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid, Code(await host.Tool("kicad_design_ownership_answer",
                Args([new { nativeObjectId = native, partId = PsuCpuIds.Id(0x03, 2) }]))));
            Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid, Code(await host.Tool("kicad_design_ownership_answer",
                Args([new { nativeObjectId = native, partId = CapacitorPart }]))));
            Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid, Code(await host.Tool("kicad_design_ownership_answer", Args([]))));
            Assert.AreEqual("design_recovery_changed", Code(await host.Tool("kicad_design_ownership_answer",
                Args([new { nativeObjectId = native, partId = SensePart }], token: new string('0', 64)))));
            Assert.AreEqual("invalid_design_path", Code(await host.Tool("kicad_design_ownership_answer",
                Args([new { nativeObjectId = native, partId = SensePart }], design: "design.xml"))));
            var open = await host.Tool("kicad_design_ownership_answer", Args([new { nativeObjectId = native }]));
            Assert.AreEqual(SchematicNativeAdditionProjection.ResolutionRequired, Code(open));
            Assert.AreEqual(native, open.GetProperty("structuredContent").GetProperty("ownershipResolutionRequests").EnumerateArray().Single()
                .GetProperty("nativeObjectId").GetGuid(), "The request stays open and is named again.");
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(designPath, timeout.Token), "Refusals write nothing.");
            Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken, "Refusals leave the record as it was.");

            // The answer: R2 is an R_sense. The XML declares it and the record takes that XML in; KiCad is not contacted.
            var answered = Success(await host.Tool("kicad_design_ownership_answer", Args([new { nativeObjectId = native, partId = SensePart }])));
            Assert.IsTrue(answered.GetProperty("designFileWritten").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, answered.GetProperty("synchronizationConflict").ValueKind,
                "The next synchronization's plan, made before writing, finds nothing else to stop at.");
            Assert.IsFalse(answered.GetProperty("nativeMutationAuthorized").GetBoolean());
            var symbol = answered.GetProperty("answeredSymbols").EnumerateArray().Single();
            Assert.AreEqual(native, symbol.GetProperty("nativeObjectId").GetGuid());
            Assert.AreEqual(proposed, symbol.GetProperty("componentId").GetGuid());
            Assert.AreEqual(SensePart, symbol.GetProperty("partId").GetGuid());
            byte[] written = await File.ReadAllBytesAsync(designPath, timeout.Token);
            Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(written)), answered.GetProperty("designSha256").GetString());
            var record = store.Read()!;
            CollectionAssert.AreEqual(written, record.State.DesiredFileBytes);
            Assert.AreEqual(answered.GetProperty("recoveryRevisionToken").GetString(), record.RevisionToken);
            Assert.AreEqual(native, SchematicDesignXml.Read(Encoding.UTF8.GetString(written), []).SymbolBindings
                .Single(b => b.SymbolOccurrenceId == symbol.GetProperty("occurrenceId").GetGuid()).NativeObjectId);
            var plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(store, record);
            Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
            var component = plan.Candidate!.Engineering.Circuit.Components.Single(c => c.Id == proposed);
            Assert.AreEqual(SensePart, plan.Candidate.Engineering.Circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == component.DefinitionId).PartId);
            // The XML now answers R2; answering it again is refused and writes nothing.
            Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid, Code(await host.Tool("kicad_design_ownership_answer",
                Args([new { nativeObjectId = native, partId = PsuCpuIds.Id(0x03, 3) }]))));
            CollectionAssert.AreEqual(written, await File.ReadAllBytesAsync(designPath, timeout.Token));

            // A record that cannot take the answered XML in after the check (here its folder turned read-only; a concurrent
            // record change fails the same way): the XML keeps the answer and the result says so, so the person resumes the
            // synchronization, which reads the XML, instead of answering again.
            if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            {
                Assert.Inconclusive("A read-only POSIX folder makes the record's save fail; Windows and privileged processes cannot express it.");
                return;
            }
            string lockedFolder = Path.Combine(root, "locked"), lockedDesign = Path.Combine(root, "locked-design.xml");
            var lockedStore = new DesignRecoveryStore(Path.Combine(lockedFolder, "recovery.json"));
            await File.WriteAllBytesAsync(lockedDesign, state.DesiredFileBytes, timeout.Token);
            var locked = lockedStore.Save(state, null);
            File.SetUnixFileMode(lockedFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                var partial = await host.Tool("kicad_design_ownership_answer", new { instanceId = state.InstanceId.ToString("D"),
                    recoveryPath = lockedStore.StatePath, expectedRevisionToken = locked.RevisionToken, designPath = lockedDesign,
                    answers = new object[] { new { nativeObjectId = native, partId = SensePart } } });
                Assert.IsTrue(partial.GetProperty("isError").GetBoolean(), partial.GetRawText());
                var content = partial.GetProperty("structuredContent");
                Assert.AreEqual("design_recovery_io", content.GetProperty("errorCode").GetString());
                Assert.IsTrue(content.GetProperty("designFileWritten").GetBoolean());
                Assert.IsFalse(content.GetProperty("recoveryDesiredUpdated").GetBoolean());
                StringAssert.Contains(content.GetProperty("errorMessage").GetString(), "Do not answer again");
                Assert.AreEqual(proposed, content.GetProperty("answeredSymbols").EnumerateArray().Single().GetProperty("componentId").GetGuid());
                byte[] lockedWritten = await File.ReadAllBytesAsync(lockedDesign, timeout.Token);
                Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(lockedWritten)), content.GetProperty("designSha256").GetString());
                CollectionAssert.AreEqual(written, lockedWritten, "The XML holds the same answer.");
                Assert.AreEqual(locked.RevisionToken, lockedStore.Read()!.RevisionToken, "The record is as it was.");
            }
            finally { File.SetUnixFileMode(lockedFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        finally { Directory.Delete(root, true); }
    }

    // The native symbol an occurrence is bound to, and the snapshot without it.
    private static (SchematicHierarchyData Without, SchematicSymbolInstance Symbol) WithoutSymbol(SchematicDesign design, Guid occurrence)
    {
        var symbol = SchematicModelProjection.NativeSymbols(design, design.Schematic)[occurrence].Clone();
        var without = design.Schematic.Clone();
        foreach (var screen in without.Instances)
            for (int i = screen.Items.Count - 1; i >= 0; --i)
                if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor) && screen.Items[i].Unpack<SchematicSymbolInstance>().Id.Value == symbol.Id.Value)
                    screen.Items.RemoveAt(i);
        return (without, symbol);
    }

    private static SchematicScreenData ScreenOf(SchematicHierarchyData data, SchematicDesign design, Guid sheetInstance) => data.Instances.Single(s =>
        string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value))
            == SchematicDesignBindings.PathKey(design.SheetBindings.Single(b => b.SheetInstanceId == sheetInstance).NativePath));

    private static DesignRecoveryState Checkpointed(SchematicDesign baseline, SchematicHierarchyData observed)
    {
        var state = SchematicRebuildTests.State(baseline, baseline, observed);
        return state with { BaselineElectrical = Isolated(baseline.Schematic, state.BaselineElectrical!.Hierarchy.Revision),
            ObservedElectrical = Isolated(observed, state.ObservedElectrical!.Hierarchy.Revision) };
    }

    // Answers to unit requests (ledger p35cfdc0345e056a5), offline only: the PSU/CPU ownership journey answers a part request.
    // KiCad joins the units of a multi-unit part into one component by their reference, a name, so the person decides which
    // component a new unit belongs to.
    [TestMethod]
    public void AnswersToUnitRequestsMakeTheNewUnitsPartOfTheComponentThePersonChose()
    {
        var token = CancellationToken.None;
        var placed = SchematicRebuildTests.Placed();
        Guid unitFour = PsuCpuIds.Id(0x09, 10), processor = PsuCpuIds.Id(0x07, 7), cpuPower = PsuCpuIds.Id(0x05, 4), cpu = PsuCpuIds.Id(0x05, 3);
        // An earlier synchronization removed the processor's power unit; now the person places that unit of U5 again.
        var (without, removed) = WithoutSymbol(placed, unitFour);
        var baseline = SchematicRebuildTests.WithoutOccurrences(placed, unitFour) with { Schematic = without };
        var observed = without.Clone();
        var again = PlacedCopy(removed, "U5", 0);
        ScreenOf(observed, baseline, cpuPower).Items.Add(Any.Pack(again));
        var state = Checkpointed(baseline, observed);
        Guid native = Guid.Parse(again.Id.Value);
        var request = SchematicNetReconciliation.Plan(state, [], token).ResolutionRequests!.Single();
        Assert.AreEqual(SchematicNativeAdditionProjection.UnitOwnerAmbiguous, request.Code);
        CollectionAssert.AreEqual(new[] { processor }, request.CandidateComponentIds.ToArray());
        // A component of its own would be a second U5.
        Assert.AreEqual("native_addition_conflict", SchematicNativeAdditionProjection.Answer(state, [],
            [new(native, ComponentId: request.ProposedComponentId)], token).ErrorCode);
        // The person answers that it is U5's power unit.
        var answer = SchematicNativeAdditionProjection.Answer(state, [], [new(native, ComponentId: processor)], token);
        Assert.IsNotNull(answer.Answered, answer.ErrorCode + ": " + answer.ErrorMessage);
        Assert.AreEqual(baseline.Engineering.Circuit.Components.Count, answer.Answered.Engineering.Circuit.Components.Count, "No component is added.");
        var joined = SchematicNetReconciliation.Plan(state with { DesiredFileBytes = Xml(answer.Answered) }, [], token);
        Assert.IsNotNull(joined.Candidate, joined.ErrorCode + ": " + joined.ErrorMessage);
        var units = joined.Candidate.Circuit.Symbols.Where(s => s.ComponentId == processor).ToArray();
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4 }, units.Select(u => u.Unit).ToArray());
        Assert.AreEqual(cpuPower, units.Single(u => u.Unit == 4).SheetInstanceId, "The unit sits on CPU_POWER, apart from U5's own sheet.");
        Assert.IsEmpty(joined.AddedComponents!);
        Assert.IsFalse(joined.Candidate.Circuit.Nets.Any(n => n.Pins.Any(p => p.ComponentId == processor)), "Its new pins are unconnected, so they make no net.");
        // Joining a component whose reference KiCad does not show is refused: set the reference in KiCad first.
        var renamed = PlacedCopy(removed, "U7", 0);
        var elsewhere = without.Clone(); ScreenOf(elsewhere, baseline, cpuPower).Items.Add(Any.Pack(renamed));
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid, SchematicNativeAdditionProjection.Answer(Checkpointed(baseline, elsewhere), [],
            [new(Guid.Parse(renamed.Id.Value), ComponentId: processor)], token).ErrorCode);

        // Two new units of the processor's part, named U9: the person answers that they are one component.
        var screen = ScreenOf(placed.Schematic, placed, cpu);
        SchematicSymbolInstance Unit(int unit) => SchematicModelProjection.NativeSymbols(placed, placed.Schematic)[placed.Engineering.Circuit.Symbols
            .Single(s => s.ComponentId == processor && s.Unit == unit).Id];
        var first = PlacedCopy(Unit(1), "U9", 0, 101_600_000); var second = PlacedCopy(Unit(2), "U9", 0, 101_600_000);
        var two = placed.Schematic.Clone(); ScreenOf(two, placed, cpu).Items.Add(Any.Pack(first)); ScreenOf(two, placed, cpu).Items.Add(Any.Pack(second));
        var pair = Checkpointed(placed, two);
        Guid firstNative = Guid.Parse(first.Id.Value), secondNative = Guid.Parse(second.Id.Value);
        var requests = SchematicNetReconciliation.Plan(pair, [], token).ResolutionRequests!;
        Assert.HasCount(2, requests);
        Assert.IsTrue(requests.All(r => r.Code == SchematicNativeAdditionProjection.UnitGroupingAmbiguous));
        var firstRequest = requests.Single(r => r.NativeObjectId == firstNative);
        var secondRequest = requests.Single(r => r.NativeObjectId == secondNative);
        CollectionAssert.AreEqual(new[] { secondRequest.ProposedComponentId }, firstRequest.CandidateComponentIds.ToArray());
        var grouped = SchematicNativeAdditionProjection.Answer(pair, [], [new(secondNative, ComponentId: firstRequest.ProposedComponentId)], token);
        Assert.IsNotNull(grouped.Answered, grouped.ErrorCode + ": " + grouped.ErrorMessage);
        CollectionAssert.AreEquivalent(new[] { firstNative, secondNative }, grouped.DeclaredSymbols.ToArray(), "The component the answer joins is declared with it.");
        var one = SchematicNetReconciliation.Plan(pair with { DesiredFileBytes = Xml(grouped.Answered) }, [], token);
        Assert.IsNotNull(one.Candidate, one.ErrorCode + ": " + one.ErrorMessage);
        CollectionAssert.AreEqual(new[] { firstRequest.ProposedComponentId }, one.AddedComponents!.ToArray());
        CollectionAssert.AreEquivalent(new[] { 1, 2 }, one.Candidate.Circuit.Symbols.Where(s => s.ComponentId == firstRequest.ProposedComponentId).Select(s => s.Unit).ToArray());
        Assert.AreEqual("U9", one.Candidate.Circuit.Components.Single(c => c.Id == firstRequest.ProposedComponentId).Reference);
        // Each unit a component of its own would be two components named U9.
        Assert.AreEqual("native_addition_conflict", SchematicNativeAdditionProjection.Answer(pair, [],
            [new(firstNative, ComponentId: firstRequest.ProposedComponentId), new(secondNative, ComponentId: secondRequest.ProposedComponentId)], token).ErrorCode);
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
