using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySynchronizationLocks(NativeClient client, DocumentSpecifier document,
        DesignRecoveryStore store, string designPath, int processId, string display, string evidence,
        string instanceId, CancellationToken token)
    {
        var first = store.Read()!.State.Baseline.Engineering.Circuit.Symbols[0];
        var nativeId = store.Read()!.State.Baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == first.Id).NativeObjectId;
        var cases = new List<object>();
        var original = await Placement();
        var beforeGroup = await Capture();
        var group = new Group { Id = new() { Value = Guid.NewGuid().ToString("D") }, Name = "Locked placement fixture", Locked = LockedState.LsLocked };
        group.Items.Add(new KIID { Value = nativeId.ToString("D") });
        var grouping = new ApplySchematicItemBatch { Document = document.Clone(), DocumentEpoch = beforeGroup.State.Revision.Epoch,
            ExpectedRevision = beforeGroup.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D") };
        grouping.Operations.Add(new SchematicItemOperation { Create = Any.Pack(group) });
        var createdGroup = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(new()
            { Batch = grouping, ExpectedState = beforeGroup.State.Clone() }, token);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, createdGroup.Status, createdGroup.ErrorMessage);
        var grouped = await Capture(); Assert.IsTrue((await Placement()).Locked);
        var memberUnlock = new SchematicSymbolLocks { Locked = LockedState.LsUnlocked };
        memberUnlock.Symbols.Add(new KIID { Value = nativeId.ToString("D") });
        var protectedBatch = new ApplySchematicItemBatch { Document = document.Clone(), DocumentEpoch = grouped.State.Revision.Epoch,
            ExpectedRevision = grouped.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D") };
        protectedBatch.Operations.Add(new SchematicItemOperation { SetSymbolLocks = memberUnlock });
        var protectedResult = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(new()
            { Batch = protectedBatch, ExpectedState = grouped.State.Clone() }, token);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, protectedResult.Status);
        StringAssert.Contains(protectedResult.ErrorMessage, "containing group");
        Assert.AreEqual(grouped, await Capture());
        await FocusedSchematicShortcut(client, document, processId, display, "z", token);
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                { Document = document.Clone(), ProcessEpoch = client.Epoch }, limit.Token)).State.Revision.Equals(grouped.State.Revision))
                await Task.Delay(50, limit.Token);
        }
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(beforeGroup.Electrical.Hierarchy.Data, (await Capture()).Electrical.Hierarchy.Data, token));
        var restoredGroup = await DesignRecoveryInspector.RefreshAsync(store, client, store.Read()!.RevisionToken, token, includeElectrical: true);
        Assert.IsFalse((await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, restoredGroup.RevisionToken,
            Guid.NewGuid(), token)).NativeMutationCommitted);
        await Apply(original with { Locked = true }, "lock-only");
        var locked = await Capture();

        // A lock that remains requested forbids motion, without leaving a
        // pending native operation or changing the editor.
        var saved = store.Read()!;
        byte[] originalXml = await File.ReadAllBytesAsync(designPath, token);
        var pinnedMove = original with { Locked = true, XMillimeters = original.XMillimeters + 2.54m };
        byte[] invalid = Desired(saved.State.Baseline, pinnedMove);
        await File.WriteAllBytesAsync(designPath, invalid, token);
        var rejectedInput = store.Save(saved.State with { DesiredFileBytes = invalid }, saved.RevisionToken);
        var refusal = await Assert.ThrowsExactlyAsync<AutomationException>(() => SchematicSynchronizationExecutor.ApplyAsync(
            store, client, designPath, rejectedInput.RevisionToken, Guid.NewGuid(), token));
        Assert.AreEqual("locked_symbol", refusal.Code); Assert.AreEqual(locked, await Capture());
        Assert.IsFalse(store.Read()!.State.HasPendingWork);
        await File.WriteAllBytesAsync(designPath, originalXml, token);
        store.Save(store.Read()!.State with { DesiredFileBytes = originalXml }, store.Read()!.RevisionToken);

        // Reject after actually unlocking and moving inside a native batch;
        // both geometry and the original lock must roll back together.
        var unlock = new SchematicSymbolLocks { Locked = LockedState.LsUnlocked };
        unlock.Symbols.Add(new KIID { Value = nativeId.ToString("D") });
        var move = new SchematicConnectedSymbolMove { Delta = new() { XNm = 2540000 } };
        move.Symbols.Add(new KIID { Value = nativeId.ToString("D") });
        var failedBatch = new ApplySchematicItemBatch { Document = document.Clone(), DocumentEpoch = locked.State.Revision.Epoch,
            ExpectedRevision = locked.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D") };
        failedBatch.Operations.Add(new SchematicItemOperation { SetSymbolLocks = unlock });
        failedBatch.Operations.Add(new SchematicItemOperation { MoveConnectedSymbols = move });
        failedBatch.Operations.Add(new SchematicItemOperation());
        var failed = new CheckedSchematicBatch { Batch = failedBatch, ExpectedState = locked.State.Clone() };
        var rejection = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(failed, token);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, rejection.Status, rejection.ErrorMessage);
        Assert.AreEqual(locked, await Capture());
        Assert.AreEqual(rejection, await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(failed, token));

        await Apply(original with { Locked = false }, "unlock-only");
        await Apply(original with { Locked = true }, "lock-before-explicit-unlock");
        var moved = original with { Locked = false, XMillimeters = original.XMillimeters + 2.54m };
        await Apply(moved, "unlock-and-move");
        var placedAndLocked = moved with { Locked = true, YMillimeters = moved.YMillimeters + 2.54m };
        await Apply(placedAndLocked, "move-and-lock");
        foreach (var (key, expected) in new[] { ("z", moved), ("y", placedAndLocked) })
        {
            var before = await Capture();
            await FocusedSchematicShortcut(client, document, processId, display, key, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                { Document = document.Clone(), ProcessEpoch = client.Epoch }, limit.Token)).State.Revision.Equals(before.State.Revision))
                await Task.Delay(50, limit.Token);
            Assert.IsTrue(SchematicOrientation.Equivalent(expected, await Placement()));
            saved = await DesignRecoveryInspector.RefreshAsync(store, client, store.Read()!.RevisionToken, token, includeElectrical: true);
            var reverse = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token);
            Assert.IsTrue(reverse.SynchronizationCommitted); Assert.IsFalse(reverse.NativeMutationCommitted);
            await Agreement(expected);
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-sync-locks.json"), JsonSerializer.Serialize(new
        { instanceId, cases, parentGroupUnlockRejected = true, pinnedMoveRejected = true, unlockMoveFailureRolledBack = true, nativeUndoRedoToXml = true,
            crossPlatformReady = false }), token);

        async Task Apply(SymbolPlacement wanted, string name)
        {
            Console.WriteLine($"Native synchronization {instanceId}: {name}");
            var before = await Capture(); var state = store.Read()!;
            byte[] bytes = Desired(state.State.Baseline, wanted);
            await File.WriteAllBytesAsync(designPath, bytes, token);
            state = store.Save(state.State with { DesiredFileBytes = bytes }, state.RevisionToken);
            Guid operation = Guid.NewGuid();
            var result = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, state.RevisionToken, operation, token);
            Assert.IsTrue(result.SynchronizationCommitted); Assert.IsTrue(result.NativeMutationCommitted);
            await Agreement(wanted);
            var after = await Capture();
            CollectionAssert.AreEqual(PinPartitions(before.Electrical), PinPartitions(after.Electrical));
            var replay = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, state.RevisionToken, operation, token);
            Assert.IsTrue(replay.Replayed); Assert.AreEqual(after, await Capture());
            var unchanged = store.Read()!;
            var noOp = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, unchanged.RevisionToken, Guid.NewGuid(), token);
            Assert.IsFalse(noOp.NativeMutationCommitted); Assert.IsFalse(noOp.NativeFilesSaved);
            Assert.AreEqual(unchanged.RevisionToken, noOp.RecoveryRevisionToken);
            cases.Add(new { name, exactLockAndPlacement = true, xmlNativeMatched = true, replayedOnce = true, unchangedDidNotEdit = true });
        }
        byte[] Desired(SchematicDesign baseline, SymbolPlacement placement) => Encoding.UTF8.GetBytes(SchematicDesignXml.Write(
            baseline with { Engineering = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with
                { Symbols = baseline.Engineering.Circuit.Symbols.Select(s => s.Id == first.Id ? s with { Placement = placement } : s).ToArray() } } }, []));
        async Task Agreement(SymbolPlacement wanted)
        {
            var current = await Capture();
            var xml = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            Assert.IsTrue(SchematicOrientation.Equivalent(wanted, xml.Engineering.Circuit.Symbols.Single(s => s.Id == first.Id).Placement));
            Assert.IsTrue(SchematicOrientation.Equivalent(wanted, await Placement()));
            Assert.IsEmpty(SchematicHierarchyDelta.Plan(current.Electrical.Hierarchy.Data, xml.Schematic, token));
            Assert.IsTrue(SchematicElectricalComparison.Compare(xml, current.Electrical, [], token).ConnectivityEquivalent);
        }
        async Task<SymbolPlacement> Placement() => SchematicModelProjection.Placement(
            SchematicModelProjection.NativeSymbols(store.Read()!.State.Baseline, (await Capture()).Electrical.Hierarchy.Data)[first.Id]);
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
    }
}
