using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicLayoutResolutionTests
{
    [TestMethod]
    public void CorrectPositionsCannotHideDisconnectedPinsAndDisplayNamesDoNotDefineConnectivity()
    {
        var f = Fixture(); var changed = f.Native.Clone();
        changed.Nets.Clear();
        Assert.AreEqual("native_sync_connectivity_mismatch", Assert.ThrowsExactly<AutomationException>(() =>
            SchematicLayoutResolution.Resolve(f.Plan.Candidate!, changed, f.Batch, f.State.KnowledgeLibraries)).Code);
        var renamed = f.Native.Clone(); renamed.Nets[0].Name = "Different native display name";
        Assert.IsNotNull(SchematicLayoutResolution.Resolve(f.Plan.Candidate!, renamed, f.Batch, f.State.KnowledgeLibraries));
    }

    [TestMethod]
    public void ConnectedLabelMovementPreservesTextAndLockedWireGeometry()
    {
        var f = Fixture(); var planned = f.Plan.Candidate! with { Schematic = f.Plan.Candidate!.Schematic.Clone() };
        var observed = f.Native.Clone();
        var document = f.Plan.NativeOperations.Single().TargetDocument;
        string physical = planned.Schematic.Instances.Single(s => s.Metadata.Document.Equals(document)).Metadata.ScreenId.Value;
        var label = new LocalLabel { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = new() { XNm = 10000000, YNm = 20000000 },
            Text = new() { Position = new() { XNm = 10000000, YNm = 20000000 }, Text_ = "Preserved signal" },
            Locked = LockedState.LsUnlocked };
        var wire = new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Start = new() { XNm = 30000000, YNm = 20000000 }, End = new() { XNm = 40000000, YNm = 20000000 },
            Type = SchematicLineType.SltWire, Locked = LockedState.LsLocked };
        foreach (var screen in planned.Schematic.Instances.Where(s => s.Metadata.ScreenId.Value == physical))
        { screen.Items.Add(Any.Pack(label)); screen.Items.Add(Any.Pack(wire)); }
        var movedLabel = label.Clone(); movedLabel.Position.XNm += 2540000; movedLabel.Text.Position.XNm += 2540000;
        foreach (var screen in observed.Hierarchy.Data.Instances.Where(s => s.Metadata.ScreenId.Value == physical))
        { screen.Items.Add(Any.Pack(movedLabel)); screen.Items.Add(Any.Pack(wire)); }
        Assert.IsNotNull(SchematicLayoutResolution.Resolve(planned, observed, f.Batch, f.State.KnowledgeLibraries));
        foreach (bool changeWire in new[] { false, true })
        {
            var changed = observed.Clone();
            foreach (var screen in changed.Hierarchy.Data.Instances.Where(s => s.Metadata.ScreenId.Value == physical))
            {
                if (changeWire) { var altered = wire.Clone(); altered.End.XNm++; screen.Items[^1] = Any.Pack(altered); }
                else { var altered = movedLabel.Clone(); altered.Text.Text_ = "Different signal"; screen.Items[^2] = Any.Pack(altered); }
            }
            Assert.ThrowsExactly<AutomationException>(() => SchematicLayoutResolution.Resolve(planned, changed, f.Batch, f.State.KnowledgeLibraries));
        }
    }

    [TestMethod]
    public void ExecutionPlanKeepsUnresolvedGeometrySeparateFromPublicPreparation()
    {
        var f = Fixture();
        Assert.IsFalse(SchematicSynchronizationPlanner.Plan(f.State).CanPrepare);
        Assert.IsTrue(f.Plan.CanPrepare, f.Plan.ErrorMessage);
        Assert.IsTrue(f.Plan.NativeLayoutResolutionRequired);
        Assert.AreEqual(1, f.Plan.NativeOperations.Count(o => o.MoveConnectedSymbols is not null));
        Assert.AreEqual(1, f.Plan.NativeOperations.Single(o => o.MoveConnectedSymbols is not null).MoveConnectedSymbols.Symbols.Count);
        Assert.IsEmpty(f.Plan.NativeOperations.Where(o => o.Update is not null));
        var result = SchematicLayoutResolution.Resolve(f.Plan.Candidate!, f.Native, f.Batch, f.State.KnowledgeLibraries);
        Assert.AreEqual(f.Native.Hierarchy.Data, result.Schematic);
        Assert.AreEqual(SchematicDesignXml.Write(f.Plan.Candidate! with { Schematic = result.Schematic }, f.State.KnowledgeLibraries),
            SchematicDesignXml.Write(result, f.State.KnowledgeLibraries));
        var native = f.Native.Clone(); native.Hierarchy.Revision.Sequence++;
        var converged = f.State with { Baseline = result, DesiredFileBytes = System.Text.Encoding.UTF8.GetBytes(SchematicDesignXml.Write(result, f.State.KnowledgeLibraries)),
            Observed = result.Schematic.Clone(), BaselineElectrical = native.Clone(), ObservedElectrical = native.Clone(),
            NativeRevision = new(native.Hierarchy.Revision.Epoch, native.Hierarchy.Revision.Sequence) };
        var noOp = SchematicSynchronizationPlanner.PlanForExecution(converged);
        Assert.IsTrue(noOp.CanPrepare, noOp.ErrorMessage);
        Assert.IsEmpty(noOp.NativeOperations); Assert.IsFalse(noOp.NativeLayoutResolutionRequired);
    }

    [TestMethod]
    public void ActualWireGeometryIsCapturedButMetadataAndUnmovedObjectsCannotDrift()
    {
        var f = Fixture(); var accepted = f.Native.Clone();
        var target = f.Plan.NativeOperations.Single().TargetDocument;
        var screen = accepted.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(target));
        screen.Items.Add(Any.Pack(new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Start = new() { XNm = 10000000, YNm = 20000000 }, End = new() { XNm = 12540000, YNm = 20000000 },
            Type = SchematicLineType.SltWire, Locked = LockedState.LsUnlocked }));
        // Apply the same physical screen object to all repeated instances.
        foreach (var copy in accepted.Hierarchy.Data.Instances.Where(s => s.Metadata.ScreenId.Equals(screen.Metadata.ScreenId) && !ReferenceEquals(s, screen)))
            copy.Items.Add(screen.Items[^1].Clone());
        Assert.AreEqual(accepted.Hierarchy.Data, SchematicLayoutResolution.Resolve(f.Plan.Candidate!, accepted, f.Batch, f.State.KnowledgeLibraries).Schematic);
        foreach (int corruption in new[] { 0, 1, 2, 3 })
        {
            var changed = f.Native.Clone();
            if (corruption == 0) changed.Hierarchy.Data.Instances[0].Metadata.TitleBlock.Title = "Unrequested";
            else Edit(changed.Hierarchy.Data, symbol =>
            {
                if (corruption == 1 && symbol.Unit.Unit == 2) symbol.Position.XNm++;
                if (corruption == 2) symbol.ValueField.Text.Text_ = "Unrequested";
                if (corruption == 3 && symbol.Unit.Unit == 1) symbol.Position.XNm++;
            });
            Assert.ThrowsExactly<AutomationException>(() => SchematicLayoutResolution.Resolve(f.Plan.Candidate!, changed, f.Batch, f.State.KnowledgeLibraries));
        }
    }

    [TestMethod]
    public void RotationMirroringAndLocksAreNotPassedOffAsTranslation()
    {
        foreach (int change in new[] { 0, 1, 2 })
        {
            var state = SchematicSynchronizationPlanTests.Fixture();
            var wanted = state.Baseline.Engineering with { Circuit = state.Baseline.Engineering.Circuit with
            { Symbols = state.Baseline.Engineering.Circuit.Symbols.Select(s => s with { Placement = change switch
            {
                0 => s.Placement! with { RotationDegrees = 90 },
                1 => s.Placement! with { MirrorX = true },
                _ => s.Placement! with { Locked = true }
            } }).ToArray() } };
            var plan = SchematicSynchronizationPlanner.PlanForExecution(SchematicNetReconciliationTests.Desired(state, wanted));
            Assert.IsFalse(plan.CanPrepare); Assert.IsEmpty(plan.NativeOperations); Assert.IsNull(plan.CandidateXml);
            Assert.AreEqual("nontranslation_placement_change", plan.ErrorCode);
        }
    }

    [TestMethod]
    public void MixedPropertyAndPositionEditsOrderPropertiesBeforeConnectedMovement()
    {
        var f = Fixture();
        var wanted = DesignRecoveryStore.ReadDesired(f.State).Engineering;
        wanted = wanted with { Circuit = wanted.Circuit with
            { Components = wanted.Circuit.Components.Select((c, i) => c with { Reference = "U" + (900 + i) }).ToArray() } };
        var plan = SchematicSynchronizationPlanner.PlanForExecution(SchematicNetReconciliationTests.Desired(f.State, wanted));
        Assert.IsTrue(plan.CanPrepare, plan.ErrorMessage); Assert.IsTrue(plan.NativeLayoutResolutionRequired);
        Assert.IsTrue(plan.NativeOperations.TakeWhile(o => o.MoveConnectedSymbols is null).Any(o => o.Update is not null));
        Assert.IsNotNull(plan.NativeOperations[^1].MoveConnectedSymbols);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicLayoutResolution.Resolve(f.Plan.Candidate!, f.Native, f.Batch,
            f.State.KnowledgeLibraries, cancelled.Token));
    }

    private static (DesignRecoveryState State, SchematicSynchronizationPlan Plan, SchematicElectricalState Native, ApplySchematicItemBatch Batch) Fixture()
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        var wanted = state.Baseline.Engineering with { Circuit = state.Baseline.Engineering.Circuit with
        { Symbols = state.Baseline.Engineering.Circuit.Symbols.Select(s => s.Unit == 1 ? s with
            { Placement = s.Placement! with { XMillimeters = s.Placement.XMillimeters + 2.54m, YMillimeters = s.Placement.YMillimeters + 2.54m } } : s).ToArray() } };
        state = SchematicNetReconciliationTests.Desired(state, wanted);
        var plan = SchematicSynchronizationPlanner.PlanForExecution(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorMessage);
        var native = state.ObservedElectrical!.Clone();
        Edit(native.Hierarchy.Data, symbol => { if (symbol.Unit.Unit == 1) { symbol.Position.XNm += 2540000; symbol.Position.YNm += 2540000; } });
        var batch = new ApplySchematicItemBatch { Document = state.Observed.Document.Clone() };
        batch.Operations.Add(plan.NativeOperations.Select(o => o.Clone()));
        return (state, plan, native, batch);
    }
    private static void Edit(SchematicHierarchyData data, Action<SchematicSymbolInstance> edit)
    {
        foreach (var screen in data.Instances)
            for (int i = 0; i < screen.Items.Count; i++)
                if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor))
                { var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>(); edit(symbol); screen.Items[i] = Any.Pack(symbol); }
    }
}
