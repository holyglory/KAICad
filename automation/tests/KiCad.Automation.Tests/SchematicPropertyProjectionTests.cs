using System.Text;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicPropertyProjectionTests
{
    [TestMethod]
    public void ReferencesAndValuesProjectOncePerPhysicalSymbolWithoutChangingPinsOrGeometry()
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        var old = SchematicDataXml.Write(state.Observed);
        var desired = state.Baseline.Engineering with { Circuit = state.Baseline.Engineering.Circuit with
        {
            Components = state.Baseline.Engineering.Circuit.Components.Select((c, i) => c with { Reference = "U" + (90 + i) }).ToArray(),
            Sheets = state.Baseline.Engineering.Circuit.Sheets.Select(s => s with
                { Components = s.Components.Select(c => c with { Value = "Changed component value" }).ToArray() }).ToArray()
        } };
        var plan = SchematicSynchronizationPlanner.Plan(SchematicNetReconciliationTests.Desired(state, desired));
        Assert.IsTrue(plan.CanPrepare, plan.ErrorMessage);
        Assert.IsEmpty(SchematicDesignBindings.Inspect(plan.Candidate!, state.KnowledgeLibraries).Differences);
        Assert.AreEqual(state.Baseline.SymbolBindings.Select(b => b.NativeObjectId).Distinct().Count(),
            plan.NativeOperations.Count(o => o.Update?.Is(SchematicSymbolInstance.Descriptor) == true));
        var before = SchematicModelProjection.NativeSymbols(state.Baseline, state.Observed);
        var after = SchematicModelProjection.NativeSymbols(plan.Candidate!, plan.Candidate!.Schematic);
        foreach (var (id, symbol) in after)
        {
            Assert.AreEqual(before[id].Definition, symbol.Definition);
            Assert.AreEqual(before[id].Position, symbol.Position);
            Assert.AreEqual(before[id].Transform, symbol.Transform);
            Assert.AreEqual(before[id].Locked, symbol.Locked);
            Assert.AreEqual(before[id].LibraryId, symbol.LibraryId);
            Assert.AreEqual(before[id].PinMapOverride, symbol.PinMapOverride);
            var component = desired.Circuit.Components.Single(c => c.Id == desired.Circuit.Symbols.Single(s => s.Id == id).ComponentId);
            Assert.AreEqual(component.Reference, symbol.ReferenceField.Text.Text_);
            Assert.AreEqual("Changed component value", symbol.ValueField.Text.Text_);
            Assert.AreEqual(component.Reference, symbol.InstanceRecords.Records.Single(r => r.Path.SequenceEqual(symbol.Path.Path)).Reference);
        }
        Assert.AreEqual(old, SchematicDataXml.Write(state.Observed));
        Assert.IsTrue(plan.NativeConnectivityValidationRequired);
    }

    [TestMethod]
    public void OtherProjectPlacementRecordsAndUserInstructionsArePreserved()
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        var schematic = state.Baseline.Schematic.Clone();
        string otherPath = Guid.NewGuid().ToString("D");
        Edit(schematic, symbol =>
        {
            var record = new SymbolSheetRecord { ProjectName = "another-project", Reference = "U777", Unit = symbol.Unit.Unit };
            record.Path.Add(new Kiapi.Common.Types.KIID { Value = otherPath }); symbol.InstanceRecords.Records.Add(record);
        });
        var baseline = state.Baseline with { Schematic = schematic };
        var electrical = state.BaselineElectrical!.Clone(); electrical.Hierarchy.Data = schematic.Clone();
        state = state with { Baseline = baseline, Observed = schematic.Clone(), BaselineElectrical = electrical.Clone(), ObservedElectrical = electrical.Clone(),
            DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, state.KnowledgeLibraries)) };
        var desired = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with
            { Components = baseline.Engineering.Circuit.Components.Select((c, i) => c with { Reference = "U" + (80 + i) }).ToArray() } };
        var plan = SchematicSynchronizationPlanner.Plan(SchematicNetReconciliationTests.Desired(state, desired));
        Assert.IsTrue(plan.CanPrepare, plan.ErrorMessage);
        foreach (var symbol in SchematicModelProjection.NativeSymbols(plan.Candidate!, plan.Candidate!.Schematic).Values)
            Assert.AreEqual("U777", symbol.InstanceRecords.Records.Single(r => r.ProjectName == "another-project").Reference);
        Assert.AreEqual(StructuralDiagramXml.Write(desired.Structure, desired.Circuit),
            StructuralDiagramXml.Write(plan.Candidate.Engineering.Structure, plan.Candidate.Engineering.Circuit));
    }

    [TestMethod]
    public void ConflictingEngineeringAndXmlSnapshotReferencesProduceNoPartialCandidate()
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        var component = state.Baseline.Engineering.Circuit.Components[0];
        string path = SchematicDesignBindings.PathKey(state.Baseline.SheetBindings.Single(b => b.SheetInstanceId == component.SheetInstanceId).NativePath);
        var native = state.Baseline.Schematic.Clone();
        Edit(native, symbol =>
        {
            foreach (var record in symbol.InstanceRecords.Records.Where(r => PathKey(r.Path) == path)) record.Reference = "U91";
            if (PathKey(symbol.Path.Path) == path) symbol.ReferenceField.Text.Text_ = "U91";
        });
        var desired = state.Baseline with { Schematic = native, Engineering = state.Baseline.Engineering with
        { Circuit = state.Baseline.Engineering.Circuit with { Components = state.Baseline.Engineering.Circuit.Components.Select(c =>
            c.Id == component.Id ? c with { Reference = "U90" } : c).ToArray() } } };
        state = state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, state.KnowledgeLibraries)) };
        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsFalse(plan.CanPrepare); Assert.IsNull(plan.CandidateXml); Assert.IsEmpty(plan.NativeOperations);
        Assert.IsTrue(plan.Properties!.Conflicts.Any(c => c.Field == "reference"));
    }

    [TestMethod]
    public void XmlNativeOnlyValueUpdatesEngineeringAndRetainsTheNativeDelta()
    {
        var state = SchematicSynchronizationPlanTests.Fixture(); var native = state.Baseline.Schematic.Clone();
        Edit(native, symbol => symbol.ValueField.Text.Text_ = "XML-native value");
        var desired = state.Baseline with { Schematic = native };
        var plan = SchematicSynchronizationPlanner.Plan(state with
            { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, state.KnowledgeLibraries)) });
        Assert.IsTrue(plan.CanPrepare, plan.ErrorMessage);
        Assert.IsTrue(plan.Candidate!.Engineering.Circuit.Sheets.SelectMany(s => s.Components).All(c => c.Value == "XML-native value"));
        Assert.IsNotEmpty(plan.NativeOperations);
    }

    [TestMethod]
    public void MissingPlacementRecordsAndUnitChangesAreNotGuessed()
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        var wanted = state.Baseline with { Engineering = state.Baseline.Engineering with { Circuit = state.Baseline.Engineering.Circuit with
            { Components = state.Baseline.Engineering.Circuit.Components.Select((c, i) => c with { Reference = "U" + (70 + i) }).ToArray() } },
            Schematic = state.Baseline.Schematic.Clone() };
        Edit(wanted.Schematic, symbol => symbol.InstanceRecords = null);
        Assert.ThrowsExactly<AutomationException>(() => SchematicPropertyProjection.Project(state.Baseline, wanted, state.KnowledgeLibraries));
        var unitChange = state.Baseline with { Engineering = state.Baseline.Engineering with
        { Circuit = state.Baseline.Engineering.Circuit with { Symbols = state.Baseline.Engineering.Circuit.Symbols.Select(s => s with { Unit = 3 - s.Unit }).ToArray() } } };
        Assert.AreEqual("unit_change_requires_electrical_update", Assert.ThrowsExactly<AutomationException>(() =>
            SchematicPropertyProjection.Project(state.Baseline, unitChange, state.KnowledgeLibraries)).Code);
    }

    private static string PathKey(IEnumerable<Kiapi.Common.Types.KIID> path) => string.Join('/', path.Select(x => x.Value));
    private static void Edit(SchematicHierarchyData data, Action<SchematicSymbolInstance> edit)
    {
        foreach (var screen in data.Instances)
            for (int index = 0; index < screen.Items.Count; index++)
                if (screen.Items[index].Is(SchematicSymbolInstance.Descriptor))
                {
                    var symbol = screen.Items[index].Unpack<SchematicSymbolInstance>();
                    edit(symbol); screen.Items[index] = Any.Pack(symbol);
                }
    }
}
