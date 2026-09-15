using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicSynchronizationPlanTests
{
    internal static DesignRecoveryState Fixture()
    {
        var state = SchematicNetReconciliationTests.Fixture();
        var schematic = state.Baseline.Schematic.Clone();
        Edit(schematic, symbol =>
        {
            symbol.Position = new() { XNm = symbol.Unit.Unit * 10000000, YNm = 20000000 };
            symbol.Transform = new() { Orientation = (SchematicSymbolOrientation)1 };
            symbol.Locked = LockedState.LsUnlocked;
        });
        foreach (var screen in schematic.Instances) screen.Metadata.TitleBlock = new();
        CompleteSharedRecords(schematic);
        var engineering = state.Baseline.Engineering with { Circuit = state.Baseline.Engineering.Circuit with
        { Symbols = state.Baseline.Engineering.Circuit.Symbols.Select(x => x with
            { Placement = new SymbolPlacement(x.Unit * 10, 20, 0, false, false, false) }).ToArray() } };
        var baseline = state.Baseline with { Engineering = engineering, Schematic = schematic };
        var electrical = state.BaselineElectrical!.Clone(); electrical.Hierarchy.Data = schematic.Clone();
        return state with { Baseline = baseline, Observed = schematic.Clone(), BaselineElectrical = electrical,
            ObservedElectrical = electrical.Clone(), DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, state.KnowledgeLibraries)) };
    }

    [TestMethod]
    public void NoChangePreservesTheWholeDesignAndDoesNotInventLiveAuthorization()
    {
        var state = Fixture();
        var result = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(result.CanPrepare, result.ErrorMessage);
        Assert.AreEqual(SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries), result.CandidateXml);
        Assert.IsEmpty(result.NativeOperations);
        Assert.IsFalse(result.NativeConnectivityValidationRequired);
        Assert.IsTrue(result.ObservedConnectivity!.ConnectivityEquivalent);
        Assert.IsNotEmpty(result.CoverageGaps, "The synthetic fixture's known serializer gaps must not disappear.");
        Assert.IsFalse(state.TrackingComplete);
    }

    [TestMethod]
    public void NoOpRetainsDesiredEnumerationEvenWhenNativeReportsAnotherOrder()
    {
        var state = Fixture();
        var wanted = state.Baseline.Schematic.Clone();
        var reversed = wanted.Instances.Reverse().ToArray(); wanted.Instances.Clear(); wanted.Instances.Add(reversed);
        foreach (var screen in wanted.Instances)
        {
            var items = screen.Items.Reverse().ToArray(); screen.Items.Clear(); screen.Items.Add(items);
        }
        var desired = state.Baseline with { Schematic = wanted };
        string expected = SchematicDesignXml.Write(desired, state.KnowledgeLibraries);
        var result = SchematicSynchronizationPlanner.Plan(state with { DesiredFileBytes = Encoding.UTF8.GetBytes(expected) });
        Assert.IsTrue(result.CanPrepare, result.ErrorMessage);
        Assert.IsTrue(expected == result.CandidateXml, "An unchanged plan must preserve desired XML enumeration without file churn.");
        Assert.IsEmpty(result.NativeOperations);
        Assert.IsFalse(result.NativeConnectivityValidationRequired);
    }

    [TestMethod]
    public void NativeMoveAndRewiringKeepConcurrentInstructionsAndUnresolvedRequirements()
    {
        var state = Fixture(); var originalNet = state.Baseline.Engineering.Circuit.Nets.Single();
        var engineering = state.Baseline.Engineering with { Structure = state.Baseline.Engineering.Structure with
        { Statements = state.Baseline.Engineering.Structure.Statements.Select(x => x with { Text = x.Text + "\nKeep near the thermal region." }).ToArray() } };
        state = SchematicNetReconciliationTests.Desired(state, engineering);
        state = SchematicNetReconciliationTests.NativeGroups(state, [originalNet.Pins[0]], [originalNet.Pins[1]]);
        var observed = state.Observed.Clone(); Edit(observed, s => { if (s.Unit.Unit == 1) s.Position.XNm += 1000000; });
        state = Observe(state, observed);
        byte[] desiredBytes = state.DesiredFileBytes.ToArray(), electricalBefore = state.ObservedElectrical!.ToByteArray();
        var result = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(result.CanPrepare, result.ErrorMessage);
        Assert.AreEqual(observed, result.Candidate!.Schematic);
        Assert.AreEqual(11m, result.Candidate.Engineering.Circuit.Symbols.First(x => x.Unit == 1).Placement!.XMillimeters);
        Assert.AreEqual(2, result.Candidate.Engineering.Circuit.Nets.Count);
        Assert.IsNotEmpty(result.Candidate.Engineering.Structure.UnresolvedNetBindings!);
        foreach (var expected in engineering.Structure.Statements)
        {
            var actual = result.Candidate.Engineering.Structure.Statements.Single(x => x.Id == expected.Id);
            Assert.AreEqual(expected.TargetId, actual.TargetId);
            Assert.AreEqual(expected.Role, actual.Role); Assert.AreEqual(expected.Strength, actual.Strength);
            Assert.AreEqual(expected.Text, actual.Text); Assert.AreEqual(expected.Connection, actual.Connection);
            CollectionAssert.AreEqual(expected.DerivedFrom.ToArray(), actual.DerivedFrom.ToArray());
            CollectionAssert.AreEqual(expected.Sources.ToArray(), actual.Sources.ToArray());
        }
        Assert.IsTrue(result.Candidate.Engineering.Structure.UnresolvedNetBindings!.All(x => x.FormerNetId == originalNet.Id));
        Assert.IsFalse(result.Candidate.Engineering.Structure.Connections.Any(x => x.NetIds.Contains(originalNet.Id)),
            "A retired active net binding must be retained as unresolved, not guessed onto a split net.");
        Assert.IsTrue(result.ObservedConnectivity!.ConnectivityEquivalent);
        Assert.IsEmpty(result.NativeOperations);
        CollectionAssert.AreEqual(desiredBytes, state.DesiredFileBytes);
        CollectionAssert.AreEqual(electricalBefore, state.ObservedElectrical.ToByteArray());
        Assert.AreEqual(result.CandidateXml, SchematicSynchronizationPlanner.Plan(state).CandidateXml);
    }

    [TestMethod]
    public void NativeReferenceAndValueEditsAreProjectedIntoTheSameCandidate()
    {
        var state = Fixture(); var observed = state.Observed.Clone();
        foreach (var screen in observed.Instances)
            EditScreen(screen, s =>
            {
                s.ValueField.Text.Text_ = "changed native value";
                if (s.Path.Path.Last().Value == observed.Instances[1].Metadata.Document.SheetPath.Path.Last().Value)
                    s.ReferenceField.Text.Text_ = "U99";
            });
        var result = SchematicSynchronizationPlanner.Plan(Observe(state, observed));
        Assert.IsTrue(result.CanPrepare, result.ErrorMessage);
        Assert.IsTrue(result.Candidate!.Engineering.Circuit.Components.Any(x => x.Reference == "U99"));
        Assert.IsTrue(result.Candidate.Engineering.Circuit.Sheets.SelectMany(x => x.Components).All(x => x.Value == "changed native value"));
        Assert.IsEmpty(result.ProjectionDifferences);
    }

    [TestMethod]
    public void DesiredModelPropertiesCannotSilentlyDivergeFromItsNativeRepresentation()
    {
        foreach (bool placement in new[] { false, true })
        {
            var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit;
            circuit = placement ? circuit with { Symbols = circuit.Symbols.Select(x => x with
                { Placement = x.Placement! with { XMillimeters = 90 } }).ToArray() }
                : circuit with { Components = circuit.Components.Select((x, i) => x with { Reference = "U" + (90 + i) }).ToArray() };
            state = SchematicNetReconciliationTests.Desired(state, state.Baseline.Engineering with { Circuit = circuit });
            var result = SchematicSynchronizationPlanner.Plan(state);
            Assert.IsFalse(result.CanPrepare);
            Assert.AreEqual("native_projection_required", result.ErrorCode);
            Assert.IsNotEmpty(result.ProjectionDifferences);
            Assert.IsNull(result.CandidateXml); Assert.IsEmpty(result.NativeOperations);
        }
    }

    [TestMethod]
    public void PendingNativeOperationAndMissingCheckpointsCannotReturnPartialCandidates()
    {
        var state = Fixture();
        foreach (var input in new[] { state with { PendingMutation = new() }, state with { BaselineElectrical = null }, state with { ObservedElectrical = null } })
        {
            var result = SchematicSynchronizationPlanner.Plan(input);
            Assert.IsFalse(result.CanPrepare); Assert.IsNull(result.CandidateXml); Assert.IsEmpty(result.NativeOperations);
            Assert.IsNotNull(result.ErrorCode); Assert.IsTrue(result.NativeConnectivityValidationRequired);
        }
    }

    [TestMethod]
    public void ElectricalConflictAndChangedOwnershipCannotReturnHierarchyOnlySuccess()
    {
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit; var old = circuit.Nets.Single();
        state = SchematicNetReconciliationTests.Desired(state, state.Baseline.Engineering with { Circuit = circuit with
        { Nets = [old with { Pins = [.. old.Pins, new(circuit.Components[0].Id, "1")] }] } });
        state = SchematicNetReconciliationTests.NativeGroups(state, [old.Pins[0]], [old.Pins[1]]);
        var conflict = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsFalse(conflict.CanPrepare); Assert.IsNull(conflict.CandidateXml); Assert.IsEmpty(conflict.NativeOperations);
        Assert.IsNotEmpty(conflict.Electrical!.Conflicts);
        var changed = Fixture();
        changed = SchematicNetReconciliationTests.Desired(changed, changed.Baseline.Engineering with { Circuit = changed.Baseline.Engineering.Circuit with
        { Symbols = changed.Baseline.Engineering.Circuit.Symbols.Skip(1).ToArray() } });
        var ownership = SchematicSynchronizationPlanner.Plan(changed);
        Assert.IsFalse(ownership.CanPrepare); Assert.AreEqual("electrical_ownership_changed", ownership.ErrorCode);
    }

    [TestMethod]
    public void RequestedNetChangesRemainExplicitUntilNativeConnectivityIsAppliedAndChecked()
    {
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit; var old = circuit.Nets.Single();
        var desired = state.Baseline.Engineering with { Circuit = circuit with
        { Nets = [old with { Pins = [.. old.Pins, new(circuit.Components[0].Id, "1")] }] } };
        var result = SchematicSynchronizationPlanner.Plan(SchematicNetReconciliationTests.Desired(state, desired));
        Assert.IsTrue(result.CanPrepare, result.ErrorMessage);
        Assert.IsTrue(result.NativeConnectivityValidationRequired);
        Assert.IsFalse(result.ObservedConnectivity!.ConnectivityEquivalent);
        Assert.IsNotEmpty(result.ObservedConnectivity.Differences);
    }

    [TestMethod]
    public void MalformedXmlAndCancellationLeaveAllInputsUntouched()
    {
        var state = Fixture() with { DesiredFileBytes = Encoding.UTF8.GetBytes("<invalid>") };
        var before = state.DesiredFileBytes.ToArray();
        var result = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsFalse(result.CanPrepare); Assert.IsNull(result.CandidateXml);
        CollectionAssert.AreEqual(before, state.DesiredFileBytes);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicSynchronizationPlanner.Plan(Fixture(), cancellation.Token));
    }

    [TestMethod]
    public void ConflictingHierarchyReturnsNoElectricalOnlySuccess()
    {
        var state = Fixture(); var desired = state.Baseline.Schematic.Clone(); desired.Instances[0].Metadata.TitleBlock.Title = "XML title";
        var observed = state.Observed.Clone(); observed.Instances[0].Metadata.TitleBlock.Title = "Native title";
        state = Observe(state, observed) with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(
            state.Baseline with { Schematic = desired }, state.KnowledgeLibraries)) };
        var result = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsFalse(result.CanPrepare); Assert.IsNull(result.CandidateXml); Assert.IsEmpty(result.NativeOperations);
        Assert.IsNotEmpty(result.Hierarchy!.Conflicts);
        Assert.IsNotNull(result.Electrical!.Candidate, "Retain useful electrical diagnostics without presenting them as a complete design.");
    }

    [TestMethod]
    public void DesiredNativeMetadataRetainsOperationsAndRequiresPostApplyConnectivity()
    {
        var state = Fixture(); var desired = state.Baseline.Schematic.Clone(); desired.Instances[0].Metadata.TitleBlock.Title = "Desired page title";
        state = state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(state.Baseline with { Schematic = desired }, state.KnowledgeLibraries)) };
        var result = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(result.CanPrepare, result.ErrorMessage);
        Assert.AreEqual("Desired page title", result.Candidate!.Schematic.Instances[0].Metadata.TitleBlock.Title);
        Assert.IsNotEmpty(result.NativeOperations); Assert.IsTrue(result.NativeConnectivityValidationRequired);
    }

    private static DesignRecoveryState Observe(DesignRecoveryState state, SchematicHierarchyData observed)
    {
        CompleteSharedRecords(observed);
        var electrical = state.ObservedElectrical!.Clone(); electrical.Hierarchy.Data = observed.Clone(); electrical.Hierarchy.Revision.Sequence++;
        return state with { Observed = observed, ObservedElectrical = electrical,
            NativeRevision = new(electrical.Hierarchy.Revision.Epoch, electrical.Hierarchy.Revision.Sequence) };
    }
    private static void CompleteSharedRecords(SchematicHierarchyData hierarchy)
    {
        var records = hierarchy.Instances.SelectMany(screen => screen.Items
            .Where(item => item.Is(SchematicSymbolInstance.Descriptor)).Select(item => item.Unpack<SchematicSymbolInstance>()))
            .GroupBy(symbol => symbol.Id.Value, StringComparer.Ordinal).ToDictionary(group => group.Key, group =>
            {
                var placements = new SymbolSheetRecords();
                foreach (var symbol in group.OrderBy(x => string.Join('/', x.Path.Path.Select(id => id.Value)), StringComparer.Ordinal))
                {
                    var record = new SymbolSheetRecord { ProjectName = "fixture", Reference = symbol.ReferenceField.Text.Text_,
                        Unit = symbol.Unit.Unit, Variants = symbol.Variants?.Clone() ?? new() };
                    record.Path.Add(symbol.Path.Path.Select(x => x.Clone()));
                    placements.Records.Add(record);
                }
                return placements;
            }, StringComparer.Ordinal);
        Edit(hierarchy, symbol => symbol.InstanceRecords = records[symbol.Id.Value].Clone());
    }
    private static void Edit(SchematicHierarchyData data, Action<SchematicSymbolInstance> edit)
    { foreach (var screen in data.Instances) EditScreen(screen, edit); }
    private static void EditScreen(SchematicScreenData screen, Action<SchematicSymbolInstance> edit)
    {
        for (int i = 0; i < screen.Items.Count; i++)
            if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor))
            { var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>(); edit(symbol); screen.Items[i] = Any.Pack(symbol); }
    }
}
