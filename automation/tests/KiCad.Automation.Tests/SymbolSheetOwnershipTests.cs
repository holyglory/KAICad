using System.Text;
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
}
