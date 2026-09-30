using Google.Protobuf;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicNativeRemovalProjectionTests
{
    private static SchematicHierarchyData Remove(SchematicDesign design, IEnumerable<Guid> occurrences)
    {
        var ids = occurrences.ToHashSet(); var circuit = design.Engineering.Circuit;
        var components = circuit.Components.ToDictionary(c => c.Id);
        var paths = design.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var bindings = design.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId.ToString("D"));
        var targets = circuit.Symbols.Where(s => ids.Contains(s.Id))
            .Select(s => (Path: paths[s.EffectiveSheetInstanceId(components[s.ComponentId])], Id: bindings[s.Id])).ToHashSet();
        var native = design.Schematic.Clone();
        foreach (var screen in native.Instances)
        {
            string path = string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(p => p.Value));
            for (int i = screen.Items.Count - 1; i >= 0; --i)
                if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor)
                    && targets.Contains((path, screen.Items[i].Unpack<SchematicSymbolInstance>().Id.Value))) screen.Items.RemoveAt(i);
        }
        return native;
    }

    [TestMethod]
    public void UnchangedProjectionHasNoFileChurnAndNeverMutatesTheCapturedState()
    {
        var (design, library, state) = SchematicElectricalComparisonTests.Fixture();
        string xml = SchematicDesignXml.Write(design, [library]); byte[] native = state.ToByteArray();
        var result = SchematicNativeRemovalProjection.Project(design, state.Hierarchy.Data, [library]);
        Assert.IsNotNull(result.BindingCandidate, result.ErrorMessage);
        Assert.AreEqual(xml, SchematicDesignXml.Write(result.BindingCandidate, [library]));
        Assert.IsEmpty(result.ComponentChanges); Assert.IsEmpty(result.RemovedOccurrences); Assert.IsEmpty(result.RetiredNets);
        CollectionAssert.AreEqual(native, state.ToByteArray()); Assert.AreEqual(xml, SchematicDesignXml.Write(design, [library]));
        result.BindingCandidate.Schematic.Instances[0].Items.Clear();
        CollectionAssert.AreEqual(native, state.ToByteArray()); Assert.AreEqual(xml, SchematicDesignXml.Write(design, [library]));
    }

    [TestMethod]
    public void RemovingOneUnitKeepsBothRepeatedComponentsAndAllPhysicalPins()
    {
        var (design, library, _) = SchematicElectricalComparisonTests.Fixture();
        var removed = design.Engineering.Circuit.Symbols.Where(s => s.Unit == 1).Select(s => s.Id).ToArray();
        var result = SchematicNativeRemovalProjection.Project(design, Remove(design, removed), [library]);
        Assert.IsNotNull(result.BindingCandidate, result.ErrorMessage);
        Assert.HasCount(2, result.BindingCandidate.Engineering.Circuit.Components);
        Assert.HasCount(2, result.BindingCandidate.Engineering.Circuit.Symbols);
        CollectionAssert.AreEquivalent(removed, result.RemovedOccurrences.ToArray());
        CollectionAssert.AreEqual(design.Engineering.Circuit.Parts.ToArray(), result.BindingCandidate.Engineering.Circuit.Parts.ToArray());
        Assert.IsEmpty(result.ComponentChanges); Assert.IsEmpty(result.RetiredNets);
        Assert.IsTrue(SchematicDesignBindings.Inspect(result.BindingCandidate, [library]).IdentitiesResolved);
    }

    [TestMethod]
    public void LastDrawingsRetireComponentsAndKeepDetachedInstructionsAndLibraryGuidance()
    {
        var (design, library, _) = SchematicElectricalComparisonTests.Fixture();
        var removed = design.Engineering.Circuit.Symbols.Select(s => s.Id).ToArray();
        var result = SchematicNativeRemovalProjection.Project(design, Remove(design, removed), [library]);
        Assert.IsNotNull(result.BindingCandidate, result.ErrorMessage);
        var engineering = result.BindingCandidate.Engineering;
        Assert.IsEmpty(engineering.Circuit.Components); Assert.IsEmpty(engineering.Circuit.Symbols);
        Assert.IsEmpty(result.BindingCandidate.SymbolBindings); Assert.IsEmpty(engineering.Circuit.Nets);
        Assert.HasCount(2, result.ComponentChanges); Assert.HasCount(1, result.RetiredNets);
        Assert.IsTrue(engineering.HasUnresolvedComponentReferences);
        Assert.IsNotEmpty(engineering.UnresolvedGuidanceBindings!);
        CollectionAssert.AreEqual(design.Engineering.Structure.Statements.ToArray(), engineering.Structure.Statements.ToArray());
        Assert.IsEmpty(engineering.Validate([library]), "Retired guidance cannot be returned as a live component assignment.");
        string xml = SchematicDesignXml.Write(result.BindingCandidate, [library]);
        Assert.AreEqual(xml, SchematicDesignXml.Write(SchematicDesignXml.Read(xml, [library]), [library]));
        Assert.IsTrue(SchematicDesignBindings.Inspect(result.BindingCandidate, [library]).IdentitiesResolved);
    }

    [TestMethod]
    public void CrossSheetUnitRemovalUsesItsActualLocationAndPreservesTheComponent()
    {
        var state = SymbolSheetOwnershipTests.Fixture(); var design = state.Baseline;
        var target = design.Engineering.Circuit.Symbols.First(s => s.Unit == 2);
        var result = SchematicNativeRemovalProjection.Project(design, Remove(design, [target.Id]), state.KnowledgeLibraries);
        Assert.IsNotNull(result.BindingCandidate, result.ErrorMessage);
        Assert.HasCount(1, result.RemovedOccurrences); Assert.AreEqual(target.Id, result.RemovedOccurrences[0]);
        Assert.HasCount(2, result.BindingCandidate.Engineering.Circuit.Components);
        Assert.HasCount(3, result.BindingCandidate.Engineering.Circuit.Symbols);
        Assert.IsEmpty(result.ComponentChanges);
    }

    [TestMethod]
    public void RetiringOnlyOneInstanceOfASharedComponentDefinitionRequiresExplicitResolution()
    {
        var state = SymbolSheetOwnershipTests.Fixture(); var design = state.Baseline;
        Guid component = design.Engineering.Circuit.Components[1].Id;
        var removed = design.Engineering.Circuit.Symbols.Where(s => s.Unit == 1 || s.ComponentId == component).Select(s => s.Id);
        var result = SchematicNativeRemovalProjection.Project(design, Remove(design, removed), state.KnowledgeLibraries);
        Assert.IsNull(result.BindingCandidate); Assert.AreEqual("component_ownership_resolution_required", result.ErrorCode);
        Assert.IsEmpty(result.ComponentChanges); Assert.IsEmpty(result.RemovedOccurrences);
    }

    [TestMethod]
    public void SameNamedReplacementChangedPinsAndChangedSheetsCannotMasqueradeAsDeletion()
    {
        var (design, library, _) = SchematicElectricalComparisonTests.Fixture();
        foreach (int change in Enumerable.Range(0, 3))
        {
            var observed = design.Schematic.Clone(); string newId = Guid.NewGuid().ToString("D");
            foreach (var screen in observed.Instances.Skip(1))
            for (int i = 0; i < screen.Items.Count; ++i)
            {
                if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
                var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
                if (symbol.Unit.Unit != 1) continue;
                if (change == 0) symbol.Id.Value = newId; // Same name, position and reference are not an identity.
                if (change == 1)
                {
                    var child = symbol.Definition.Items.First(c => c.Item.Is(SchematicPin.Descriptor));
                    var pin = child.Item.Unpack<SchematicPin>(); pin.Number = "new-pin"; child.Item = Any.Pack(pin);
                }
                screen.Items[i] = Any.Pack(symbol);
            }
            if (change == 2) observed.Instances[0].Metadata.ScreenId.Value = newId;
            var result = SchematicNativeRemovalProjection.Project(design, observed, [library]);
            Assert.IsNull(result.BindingCandidate); Assert.IsNotNull(result.ErrorCode);
        }
    }

    [TestMethod]
    public void UnresolvedBaselineAndCancellationCannotProduceACandidate()
    {
        var (design, library, state) = SchematicElectricalComparisonTests.Fixture();
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicNativeRemovalProjection.Project(design, state.Hierarchy.Data, [library], new(true)));
        var result = SchematicNativeRemovalProjection.Project(design with { SymbolBindings = [] }, state.Hierarchy.Data, [library]);
        Assert.IsNull(result.BindingCandidate); Assert.AreEqual("unresolved_design_bindings", result.ErrorCode);
    }

    private static DesignRecoveryState RemoveAll(DesignRecoveryState state)
    {
        var observed = state.ObservedElectrical!.Clone();
        observed.Hierarchy.Data = Remove(state.Baseline, state.Baseline.Engineering.Circuit.Symbols.Select(s => s.Id));
        observed.Nets.Clear(); observed.Hierarchy.Revision.Sequence++;
        return state with { Observed = observed.Hierarchy.Data.Clone(), ObservedElectrical = observed,
            NativeRevision = new(observed.Hierarchy.Revision.Epoch, observed.Hierarchy.Revision.Sequence) };
    }

    [TestMethod]
    public void NativeRemovalProducesAConsistentReverseXmlPlanWithRetainedRequirements()
    {
        var state = RemoveAll(SchematicSynchronizationPlanTests.Fixture());
        byte[] before = state.BaselineElectrical!.ToByteArray(), current = state.ObservedElectrical!.ToByteArray();
        byte[] desired = state.DesiredFileBytes.ToArray();
        var plan = SchematicSynchronizationPlanner.PlanForExecution(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        Assert.IsNotNull(plan.Candidate); Assert.IsNotNull(plan.CandidateXml);
        Assert.IsEmpty(plan.Candidate.Engineering.Circuit.Components);
        Assert.IsEmpty(plan.Candidate.SymbolBindings); Assert.IsEmpty(plan.NativeOperations);
        Assert.IsTrue(plan.Candidate.Engineering.HasUnresolvedComponentReferences);
        Assert.IsTrue(plan.ObservedConnectivity!.ConnectivityEquivalent);
        Assert.IsFalse(plan.NativeConnectivityValidationRequired);
        Assert.HasCount(4, plan.Electrical!.RemovedSymbolOccurrences!);
        Assert.HasCount(2, plan.Electrical.ComponentChanges!);
        Assert.AreEqual(plan.CandidateXml, SchematicDesignXml.Write(SchematicDesignXml.Read(plan.CandidateXml, state.KnowledgeLibraries), state.KnowledgeLibraries));
        CollectionAssert.AreEqual(before, state.BaselineElectrical.ToByteArray());
        CollectionAssert.AreEqual(current, state.ObservedElectrical.ToByteArray());
        CollectionAssert.AreEqual(desired, state.DesiredFileBytes);
    }

    [TestMethod]
    public void ConcurrentXmlEngineeringChangesPauseNativeRemovalWithoutDiscardingEitherVersion()
    {
        var state = RemoveAll(SchematicSynchronizationPlanTests.Fixture());
        var desired = state.Baseline.Engineering with { Circuit = state.Baseline.Engineering.Circuit with
        { Components = state.Baseline.Engineering.Circuit.Components.Select((c, i) => i == 0 ? c with { Reference = "U900" } : c).ToArray() } };
        state = SchematicNetReconciliationTests.Desired(state, desired);
        byte[] input = state.DesiredFileBytes.ToArray(), native = state.ObservedElectrical!.ToByteArray();
        var plan = SchematicSynchronizationPlanner.PlanForExecution(state);
        Assert.IsFalse(plan.CanPrepare); Assert.IsNull(plan.Candidate); Assert.IsEmpty(plan.NativeOperations);
        Assert.AreEqual("ownership_change_with_xml_edits", plan.ErrorCode);
        CollectionAssert.AreEqual(input, state.DesiredFileBytes); CollectionAssert.AreEqual(native, state.ObservedElectrical.ToByteArray());
    }

    [TestMethod]
    public void ASettledRemovalProducesNoFurtherEditsOrXmlChurn()
    {
        var state = RemoveAll(SchematicSynchronizationPlanTests.Fixture());
        var first = SchematicSynchronizationPlanner.PlanForExecution(state);
        Assert.IsTrue(first.CanPrepare, first.ErrorCode);
        var settled = state with { Baseline = first.Candidate!, DesiredFileBytes = Encoding.UTF8.GetBytes(first.CandidateXml!),
            BaselineElectrical = state.ObservedElectrical!.Clone() };
        var again = SchematicSynchronizationPlanner.PlanForExecution(settled);
        Assert.IsTrue(again.CanPrepare, again.ErrorCode); Assert.AreEqual(first.CandidateXml, again.CandidateXml);
        Assert.IsEmpty(again.NativeOperations); Assert.IsEmpty(again.Electrical!.NetChanges);
        Assert.IsNull(again.Electrical.RemovedSymbolOccurrences);
    }
}
