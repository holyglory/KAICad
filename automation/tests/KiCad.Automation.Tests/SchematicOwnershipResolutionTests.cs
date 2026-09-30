using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicOwnershipResolutionTests
{
    [TestMethod]
    public async Task ExactChoiceReopensInVersionNineAndClearRestoresAmbiguityWithoutEditingDesign()
    {
        using var f = new SchematicNativeRestorationTests.Fixture(newerInstructions: true); f.AddAlternativeMapping();
        byte[] design = File.ReadAllBytes(f.Receipt.DesignPath);
        var inspection = await SchematicOwnershipResolutionService.InspectAsync(f.Store, f.Saved);
        Assert.HasCount(2, inspection.Choices);
        var selected = await SchematicOwnershipResolutionService.ResolveAsync(f.Store, f.Saved, inspection.SnapshotToken, f.Receipt.OperationId);
        using var envelope = JsonDocument.Parse(File.ReadAllBytes(f.Store.StatePath));
        Assert.AreEqual(9, envelope.RootElement.GetProperty("Version").GetInt32());
        var reopened = new DesignRecoveryStore(f.Store.StatePath).Read()!;
        Assert.AreEqual(selected.State.OwnershipResolution, reopened.State.OwnershipResolution);
        var plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(f.Store, reopened);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode);
        CollectionAssert.AreEquivalent(f.Original.Baseline.Engineering.Circuit.Components.Select(c => c.Id).ToArray(),
            plan.Candidate!.Engineering.Circuit.Components.Select(c => c.Id).ToArray());
        CollectionAssert.AreEqual(design, File.ReadAllBytes(f.Receipt.DesignPath));
        var clear = SchematicOwnershipResolutionService.Clear(f.Store, reopened);
        Assert.IsNull(clear.State.OwnershipResolution);
        Assert.AreEqual(clear.RevisionToken, SchematicOwnershipResolutionService.Clear(f.Store, clear).RevisionToken);
        plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(f.Store, clear);
        Assert.IsFalse(plan.CanPrepare); Assert.AreEqual("ambiguous_native_ownership_history", plan.ErrorCode);
        CollectionAssert.AreEqual(design, File.ReadAllBytes(f.Receipt.DesignPath));
    }

    [TestMethod]
    public async Task WrongChoicesStaleTokensAndCancellationLeaveRecoveryUntouched()
    {
        using var f = new SchematicNativeRestorationTests.Fixture(newerInstructions: true); f.AddAlternativeMapping();
        var inspection = await SchematicOwnershipResolutionService.InspectAsync(f.Store, f.Saved);
        byte[] before = File.ReadAllBytes(f.Store.StatePath);
        Assert.AreEqual("invalid_history_choice", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            SchematicOwnershipResolutionService.ResolveAsync(f.Store, f.Saved, inspection.SnapshotToken, Guid.NewGuid()))).Code);
        Assert.AreEqual("native_owner_resolution_stale", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            SchematicOwnershipResolutionService.ResolveAsync(f.Store, f.Saved, new string('0', 64), f.Receipt.OperationId))).Code);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => SchematicOwnershipResolutionService.ResolveAsync(
            f.Store, f.Saved, inspection.SnapshotToken, f.Receipt.OperationId, new(true)));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Store.StatePath));
        var selected = await SchematicOwnershipResolutionService.ResolveAsync(f.Store, f.Saved, inspection.SnapshotToken, f.Receipt.OperationId);
        Assert.AreEqual("design_recovery_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            SchematicOwnershipResolutionService.ResolveAsync(f.Store, f.Saved, inspection.SnapshotToken, f.Receipt.OperationId))).Code);
        Assert.AreEqual(selected.RevisionToken, f.Store.Read()!.RevisionToken);
    }

    [TestMethod]
    public async Task NativeOrXmlChangesInvalidateSelectionAndCorruptHistoryCanStillBeCleared()
    {
        foreach (string change in new[] { "native", "xml", "history", "owners" })
        {
            using var f = new SchematicNativeRestorationTests.Fixture(newerInstructions: true); f.AddAlternativeMapping();
            var inspection = await SchematicOwnershipResolutionService.InspectAsync(f.Store, f.Saved);
            var selected = await SchematicOwnershipResolutionService.ResolveAsync(f.Store, f.Saved, inspection.SnapshotToken, f.Receipt.OperationId);
            var next = selected.State;
            if (change == "native")
            {
                var native = next.ObservedElectrical!.Clone(); native.Hierarchy.Revision.Sequence++;
                next = next with { ObservedElectrical = native, NativeRevision = new(native.Hierarchy.Revision.Epoch, native.Hierarchy.Revision.Sequence) };
            }
            if (change == "xml") next = next with { DesiredFileBytes = [.. next.DesiredFileBytes, (byte)'\n'] };
            if (change == "history") File.AppendAllText(f.Receipt.PreviousXmlPath!, "changed");
            if (change == "owners") next = next with { Observed = next.Baseline.Schematic.Clone(),
                ObservedElectrical = next.BaselineElectrical!.Clone(), NativeRevision = new(next.BaselineElectrical.Hierarchy.Revision.Epoch, next.BaselineElectrical.Hierarchy.Revision.Sequence) };
            var changed = f.Store.Save(next, selected.RevisionToken);
            var plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(f.Store, changed);
            Assert.IsFalse(plan.CanPrepare, change); Assert.IsNull(plan.Candidate); Assert.IsEmpty(plan.NativeOperations);
            Assert.AreEqual(change == "history" ? "retained_xml_changed" : "native_owner_resolution_stale", plan.ErrorCode, change);
            Assert.IsNull(SchematicOwnershipResolutionService.Clear(f.Store, changed).State.OwnershipResolution);
        }
    }

    [TestMethod]
    public async Task PendingSynchronizationFreezesTheChoiceAndMalformedRecordsAreRejected()
    {
        using var f = new SchematicNativeRestorationTests.Fixture(newerInstructions: true); f.AddAlternativeMapping();
        var inspection = await SchematicOwnershipResolutionService.InspectAsync(f.Store, f.Saved);
        var selected = await SchematicOwnershipResolutionService.ResolveAsync(f.Store, f.Saved, inspection.SnapshotToken, f.Receipt.OperationId);
        foreach (var bad in new[] { selected.State.OwnershipResolution! with { HistoryOperationId = Guid.Empty },
                     selected.State.OwnershipResolution! with { SnapshotToken = "invalid" },
                     selected.State.OwnershipResolution! with { HistoryXmlSha256 = "invalid" } })
            Assert.AreEqual("invalid_ownership_resolution", Assert.ThrowsExactly<AutomationException>(() =>
                f.Store.Save(selected.State with { OwnershipResolution = bad }, selected.RevisionToken)).Code);
        // The current baseline intentionally has no symbols after deletion;
        // this pending operation addresses a restored object in the observation.
        var mutation = DesignRecoveryStoreTests.Mutation(f.Original);
        mutation.DocumentEpoch = selected.State.NativeRevision.Epoch;
        mutation.ExpectedRevision = new() { Epoch = selected.State.NativeRevision.Epoch, Sequence = selected.State.NativeRevision.Sequence };
        var pending = f.Store.Save(selected.State with { PendingMutation = mutation }, selected.RevisionToken);
        Assert.AreEqual("ownership_resolution_pending", Assert.ThrowsExactly<AutomationException>(() =>
            SchematicOwnershipResolutionService.Clear(f.Store, pending)).Code);
        Assert.AreEqual("ownership_resolution_pending", Assert.ThrowsExactly<AutomationException>(() =>
            f.Store.Save(pending.State with { OwnershipResolution = null }, pending.RevisionToken)).Code);
        Assert.AreEqual(pending.RevisionToken, f.Store.Read()!.RevisionToken);
    }

    [TestMethod]
    public async Task NormalStdioToolsInspectSelectRejectStaleAndClearWithoutNativeMutation()
    {
        using var f = new SchematicNativeRestorationTests.Fixture(newerInstructions: true); f.AddAlternativeMapping();
        string root = Path.GetDirectoryName(f.Store.StatePath)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(), Path.Combine(root, "host"), Path.Combine(root, "host.log"), deadline.Token);
        var args = new { instanceId = f.Saved.State.InstanceId.ToString("D"), recoveryPath = f.Store.StatePath, expectedRevisionToken = f.Saved.RevisionToken };
        var inspected = await host.Tool("kicad_design_owner_history_inspect", args);
        Assert.IsFalse(inspected.TryGetProperty("isError", out var error) && error.GetBoolean());
        var data = inspected.GetProperty("structuredContent"); Assert.AreEqual(2, data.GetProperty("choices").GetArrayLength());
        var selected = await host.Tool("kicad_design_owner_history_resolve", new { args.instanceId, args.recoveryPath, args.expectedRevisionToken,
            expectedSnapshotToken = data.GetProperty("snapshotToken").GetString(), historyOperationId = f.Receipt.OperationId.ToString("D") });
        Assert.IsFalse(selected.TryGetProperty("isError", out error) && error.GetBoolean());
        data = selected.GetProperty("structuredContent"); Assert.IsTrue(data.GetProperty("recoveryChoiceWritten").GetBoolean());
        Assert.IsFalse(data.GetProperty("designFileWritten").GetBoolean()); Assert.IsFalse(data.GetProperty("nativeMutationAuthorized").GetBoolean());
        string revision = data.GetProperty("recoveryRevisionToken").GetString()!;
        var stale = await host.Tool("kicad_design_owner_history_clear", args);
        Assert.AreEqual("design_recovery_changed", stale.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        var wrong = await host.Tool("kicad_design_owner_history_inspect", new { instanceId = Guid.NewGuid().ToString("D"), args.recoveryPath, expectedRevisionToken = revision });
        Assert.AreEqual("recovery_instance_mismatch", wrong.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        var planned = await host.Tool("kicad_design_sync_plan", new { args.instanceId, args.recoveryPath, expectedRevisionToken = revision });
        Assert.IsTrue(planned.GetProperty("structuredContent").GetProperty("canPrepare").GetBoolean());
        var cleared = await host.Tool("kicad_design_owner_history_clear", new { args.instanceId, args.recoveryPath, expectedRevisionToken = revision });
        Assert.AreEqual(JsonValueKind.Null, cleared.GetProperty("structuredContent").GetProperty("selection").ValueKind);
        Assert.IsNull(f.Store.Read()!.State.OwnershipResolution);
    }
}
