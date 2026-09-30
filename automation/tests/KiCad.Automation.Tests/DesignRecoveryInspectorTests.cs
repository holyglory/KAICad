using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignRecoveryInspectorTests
{
    private static async Task Isolated(Func<DesignRecoveryStore, StoredDesignRecovery, ReceiptTransport, Task> test)
    {
        string directory = Directory.CreateTempSubdirectory("kicad-recovery-inspect-").FullName;
        try
        {
            var state = DesignRecoveryStoreTests.Fixture();
            state = state with { PendingMutation = DesignRecoveryStoreTests.Mutation(state) };
            var store = new DesignRecoveryStore(Path.Combine(directory, "recovery.json"));
            var saved = store.Save(state, null);
            await test(store, saved, new ReceiptTransport(state));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public Task EveryNativeOutcomeRetainsPendingCommandAndAllDesignVersions() => Isolated(async (store, saved, transport) =>
    {
        foreach (var (native, disposition) in new[]
        {
            (SchematicOperationReceipt.Types.State.NotFound, DesignRecoveryDisposition.NotFound),
            (SchematicOperationReceipt.Types.State.Completed, DesignRecoveryDisposition.CompletedNeedsReconciliation),
            (SchematicOperationReceipt.Types.State.Rejected, DesignRecoveryDisposition.Rejected),
            (SchematicOperationReceipt.Types.State.Indeterminate, DesignRecoveryDisposition.Indeterminate)
        })
        {
            transport.State = native;
            var result = await DesignRecoveryInspector.InspectAsync(store, transport.Client());
            Assert.AreEqual(disposition, result.Disposition);
            Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
            var actualRequest = transport.LastInspection!.ExpectedRequest;
            Assert.AreEqual(saved.State.PendingMutation, actualRequest);
            Assert.AreEqual(saved.State.PendingMutation, store.Read()!.State.PendingMutation);
        }
        Assert.AreEqual(4, transport.Inspections);
    });

    [TestMethod]
    public Task WrongInstanceAndChangedProcessNeverInspectAnotherEditor() => Isolated(async (store, saved, transport) =>
    {
        transport.InstanceId = Guid.NewGuid().ToString("D");
        Assert.AreEqual("recovery_instance_mismatch", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InspectAsync(store, transport.Client()))).Code);
        Assert.AreEqual(0, transport.Inspections);
        transport.InstanceId = saved.State.InstanceId.ToString("D");
        transport.ChangeProcessAfterHandshake = true;
        Assert.AreEqual("instance_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InspectAsync(store, transport.Client()))).Code);
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public Task MissingVerificationAndInconsistentReceiptsCannotConfirmRecovery() => Isolated(async (store, saved, transport) =>
    {
        foreach (Action<SchematicOperationReceipt> change in new Action<SchematicOperationReceipt>[]
        {
            r => r.ExpectedRequestVerified = false, r => r.DocumentEpoch = "other-document",
            r => r.OperationId = "other-operation", r => r.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D"),
            r => r.Result = null, r => r.NativeStatusCode = 3, r => r.State = (SchematicOperationReceipt.Types.State)99,
            r => r.Result.Revision = null, r => r.Result.Revision.Epoch = "other-document",
            r => r.Result.Revision.Sequence = saved.State.NativeRevision.Sequence - 1,
            r => { r.State = SchematicOperationReceipt.Types.State.NotFound; r.Result = null; },
            r => { r.State = SchematicOperationReceipt.Types.State.Rejected; r.Result = null; r.NativeStatusCode = 1; }
        })
        {
            transport.ChangeReceipt = change;
            await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignRecoveryInspector.InspectAsync(store, transport.Client()));
            Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        }
        transport.ChangeReceipt = null;
        Assert.AreEqual(DesignRecoveryDisposition.CompletedNeedsReconciliation,
            (await DesignRecoveryInspector.InspectAsync(store, transport.Client())).Disposition);
    });

    [TestMethod]
    public Task ConcurrentRecoveryWriteAndCancellationRetainTheNewerState() => Isolated(async (store, saved, transport) =>
    {
        StoredDesignRecovery? newer = null;
        transport.BeforeReply = () => newer = store.Save(saved.State with { DesiredFileBytes = [0xff, 0x3c] }, saved.RevisionToken);
        Assert.AreEqual("design_recovery_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InspectAsync(store, transport.Client()))).Code);
        Assert.AreEqual(newer!.RevisionToken, store.Read()!.RevisionToken);
        using var cancellation = new CancellationTokenSource();
        transport.BeforeReply = cancellation.Cancel;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            DesignRecoveryInspector.InspectAsync(store, transport.Client(), cancellation.Token));
        Assert.AreEqual(newer.RevisionToken, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public Task MissingPendingOperationDoesNotContactNativePeer() => Isolated(async (store, saved, transport) =>
    {
        var cleared = store.Save(saved.State with { PendingMutation = null }, saved.RevisionToken);
        var result = await DesignRecoveryInspector.InspectAsync(store, transport.Client());
        Assert.AreEqual(DesignRecoveryDisposition.NoPendingOperation, result.Disposition);
        Assert.AreEqual(0, transport.Calls);
        Assert.AreEqual(cleared.RevisionToken, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public Task FreshObservationPreservesLaterEditsAndPendingRecovery() => Isolated(async (store, saved, transport) =>
    {
        transport.ChangeSnapshot = snapshot => snapshot.Revision.Sequence += 8;
        var result = await DesignRecoveryInspector.ObserveAsync(store, transport.Client());
        Assert.AreEqual(DesignRecoveryDisposition.CompletedNeedsReconciliation, result.Inspection.Disposition);
        Assert.AreEqual(saved.State.NativeRevision.Sequence + 9, result.Snapshot.Revision.Sequence);
        Assert.AreEqual(saved.State.NativeRevision.Sequence + 1, result.Inspection.Receipt!.Result.Revision.Sequence);
        Assert.IsFalse(result.Snapshot.TrackingComplete);
        Assert.AreEqual(saved.State.Observed, result.Snapshot.Data);
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        Assert.AreEqual(1, transport.Snapshots);
        transport.FileLocationStatus = 3;
        transport.AdvanceOnFileRefusal = true;
        Assert.AreEqual(NativeSheetFileLocations.ObservationChanged, (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.ObserveAsync(store, transport.Client()))).Code);
        Assert.AreEqual(3, transport.Snapshots, "A refused read is classified from one fresh hierarchy, without an internal retry loop.");
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        Assert.AreEqual(saved.State.PendingMutation, store.Read()!.State.PendingMutation);
        transport.AdvanceOnFileRefusal = false;
        transport.FileRefusals = 0;
        Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            DesignRecoveryInspector.ObserveAsync(store, transport.Client()))).Status,
            "A refusal with no actual revision advance is still a real error.");
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        transport.AdvanceOnFileRefusal = true;
        transport.ChangeSnapshot = snapshot =>
        {
            snapshot.Revision.Sequence += 8;
            if (transport.FileRefusals > 0) snapshot.Revision.Epoch = "different-document-session";
        };
        Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            DesignRecoveryInspector.ObserveAsync(store, transport.Client()))).Status,
            "A different document session is never classified as a retryable revision advance.");
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public Task WrongOrOlderSnapshotsCannotEnterReconciliation() => Isolated(async (store, saved, transport) =>
    {
        foreach (Action<SchematicHierarchyDataSnapshot> change in new Action<SchematicHierarchyDataSnapshot>[]
        {
            s => s.Data = null, s => s.Data.Document = null,
            s => s.Data.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D"),
            s => s.Revision = null, s => s.Revision.Epoch = "replacement-document",
            s => s.Revision.Sequence = saved.State.NativeRevision.Sequence
        })
        {
            transport.ChangeSnapshot = change;
            await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignRecoveryInspector.ObserveAsync(store, transport.Client()));
            Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        }
        transport.ChangeSnapshot = null;
        Assert.IsNotNull((await DesignRecoveryInspector.ObserveAsync(store, transport.Client())).Snapshot);
    });

    [TestMethod]
    public Task ObservationCancellationAndConcurrentWritesPreserveRecovery() => Isolated(async (store, saved, transport) =>
    {
        StoredDesignRecovery? newer = null;
        transport.BeforeSnapshotReply = () => newer = store.Save(saved.State with { DesiredFileBytes = [0xff, 0x3c] }, saved.RevisionToken);
        Assert.AreEqual("design_recovery_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.ObserveAsync(store, transport.Client()))).Code);
        Assert.AreEqual(newer!.RevisionToken, store.Read()!.RevisionToken);
        using var cancellation = new CancellationTokenSource();
        transport.BeforeSnapshotReply = cancellation.Cancel;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            DesignRecoveryInspector.ObserveAsync(store, transport.Client(), cancellation.Token));
        Assert.AreEqual(newer.RevisionToken, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public Task ObservationWithoutPendingEditStillVerifiesInstance() => Isolated(async (store, saved, transport) =>
    {
        var cleared = store.Save(saved.State with { PendingMutation = null }, saved.RevisionToken);
        transport.InstanceId = Guid.NewGuid().ToString("D");
        Assert.AreEqual("recovery_instance_mismatch", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.ObserveAsync(store, transport.Client()))).Code);
        Assert.AreEqual(0, transport.Snapshots);
        transport.InstanceId = saved.State.InstanceId.ToString("D");
        var result = await DesignRecoveryInspector.ObserveAsync(store, transport.Client());
        Assert.AreEqual(DesignRecoveryDisposition.NoPendingOperation, result.Inspection.Disposition);
        Assert.AreEqual(0, transport.Inspections);
        Assert.AreEqual(cleared.RevisionToken, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public Task RefreshPreservesDesiredBytesAndBaselineAndIsIdempotent() => Isolated(async (store, saved, transport) =>
    {
        var ready = store.Save(saved.State with { PendingMutation = null, DesiredFileBytes = [0xff, 0x3c] }, saved.RevisionToken);
        transport.ChangeSnapshot = s => s.Data.Instances[0].Metadata.TextVariables.Add("native_refresh", "preserved");
        var refreshed = await DesignRecoveryInspector.RefreshAsync(store, transport.Client(), ready.RevisionToken);
        Assert.AreEqual(ready.State.NativeRevision.Sequence + 1, refreshed.State.NativeRevision.Sequence);
        Assert.AreEqual("preserved", refreshed.State.Observed.Instances[0].Metadata.TextVariables["native_refresh"]);
        CollectionAssert.AreEqual(ready.State.DesiredFileBytes, refreshed.State.DesiredFileBytes);
        Assert.AreEqual(SchematicDesignXml.Write(ready.State.Baseline, ready.State.KnowledgeLibraries),
            SchematicDesignXml.Write(refreshed.State.Baseline, refreshed.State.KnowledgeLibraries));
        Assert.IsFalse(refreshed.State.TrackingComplete);
        Assert.IsNull(refreshed.State.PendingMutation);
        var repeated = await DesignRecoveryInspector.RefreshAsync(store, transport.Client(), refreshed.RevisionToken);
        Assert.AreEqual(refreshed.RevisionToken, repeated.RevisionToken);
        Assert.AreEqual(0, transport.Inspections);
    });

    [TestMethod]
    public Task RefreshNeverErasesPendingOperationOrConcurrentXmlSave() => Isolated(async (store, saved, transport) =>
    {
        Assert.AreEqual("pending_recovery_requires_reconciliation", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.RefreshAsync(store, transport.Client(), saved.RevisionToken))).Code);
        Assert.AreEqual(0, transport.Calls);
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        var ready = store.Save(saved.State with { PendingMutation = null }, saved.RevisionToken);
        await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.RefreshAsync(store, transport.Client(), saved.RevisionToken));
        Assert.AreEqual(0, transport.Calls);
        StoredDesignRecovery? concurrent = null;
        transport.BeforeSnapshotReply = () => concurrent = store.Save(ready.State with { DesiredFileBytes = [0xff] }, ready.RevisionToken);
        await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.RefreshAsync(store, transport.Client(), ready.RevisionToken));
        Assert.AreEqual(concurrent!.RevisionToken, store.Read()!.RevisionToken);
        CollectionAssert.AreEqual(new byte[] { 0xff }, store.Read()!.State.DesiredFileBytes);
        Assert.AreEqual(ready.State.NativeRevision, store.Read()!.State.NativeRevision);
    });

    [TestMethod]
    public Task RefreshCancellationAndRestartedDocumentLeaveAllVersionsIntact() => Isolated(async (store, saved, transport) =>
    {
        var ready = store.Save(saved.State with { PendingMutation = null }, saved.RevisionToken);
        using var cancellation = new CancellationTokenSource();
        transport.BeforeSnapshotReply = cancellation.Cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DesignRecoveryInspector.RefreshAsync(store, transport.Client(), ready.RevisionToken, cancellation.Token));
        Assert.AreEqual(ready.RevisionToken, store.Read()!.RevisionToken);
        transport.BeforeSnapshotReply = null;
        transport.ChangeSnapshot = s => s.Revision.Epoch = "restarted-document";
        await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.RefreshAsync(store, transport.Client(), ready.RevisionToken));
        Assert.AreEqual(ready.RevisionToken, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public Task RefreshRetainsUnchangedChoicesButInvalidatesUntrackedContentChange() => Isolated(async (store, saved, unused) =>
    {
        var b = SchematicHierarchyTopologyTests.Fixture(); var x = b.Clone(); var n = b.Clone();
        x.Instances[0].Metadata.TitleBlock = new() { Title = "Desired" };
        n.Instances[0].Metadata.TitleBlock = new() { Title = "Native" };
        var baseline = saved.State.Baseline with { Schematic = b, SheetBindings = [], SymbolBindings = [] };
        var state = saved.State with { Baseline = baseline, Observed = n, PendingMutation = null,
            DesiredFileBytes = System.Text.Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline with { Schematic = x }, saved.State.KnowledgeLibraries)) };
        var ready = store.Save(state, saved.RevisionToken);
        var choices = DesignRecoveryStore.PlanHierarchy(state).Conflicts.ToDictionary(c => c.InstancePath, _ => SchematicConflictChoice.Xml);
        Assert.IsNotEmpty(choices);
        var resolved = store.ResolveHierarchy(ready.RevisionToken, DesignRecoveryStore.HierarchySnapshotToken(state), choices);
        var transport = new ReceiptTransport(state);
        transport.ChangeSnapshot = s => s.Revision.Sequence = state.NativeRevision.Sequence;
        var unchanged = await DesignRecoveryInspector.RefreshAsync(store, transport.Client(), resolved.RevisionToken);
        Assert.AreEqual(resolved.RevisionToken, unchanged.RevisionToken);
        Assert.IsNotNull(unchanged.State.HierarchyResolution);
        transport.ChangeSnapshot = s =>
        {
            // Incomplete native revision coverage cannot prove content unchanged.
            s.Revision.Sequence = state.NativeRevision.Sequence;
            s.Data.Instances[0].Metadata.TitleBlock.Title = "Later native title";
        };
        var refreshed = await DesignRecoveryInspector.RefreshAsync(store, transport.Client(), unchanged.RevisionToken);
        Assert.IsNull(refreshed.State.HierarchyResolution);
        Assert.AreEqual(unchanged.State.NativeRevision, refreshed.State.NativeRevision);
        Assert.AreNotEqual(unchanged.RevisionToken, refreshed.RevisionToken);
        CollectionAssert.AreEqual(unchanged.State.DesiredFileBytes, refreshed.State.DesiredFileBytes);
    });

    [TestMethod]
    public async Task LockedRecoveryRejectsRefreshAndCanRecoverWithoutLosingVersions()
    {
        string root = Directory.CreateTempSubdirectory("kicad-refresh-lock-").FullName;
        try
        {
            string path = Path.Combine(root, "recovery.json");
            var state = DesignRecoveryStoreTests.Fixture();
            var store = new DesignRecoveryStore(path); var saved = store.Save(state, null);
            var transport = new ReceiptTransport(state);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    DesignRecoveryInspector.RefreshAsync(store, transport.Client(), saved.RevisionToken, cancelled.Token));
                Assert.AreEqual(0, transport.Calls);
            }
            using (var ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.AreEqual("design_recovery_io", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                    DesignRecoveryInspector.RefreshAsync(store, transport.Client(), saved.RevisionToken))).Code);
                Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
            }
            var refreshed = await DesignRecoveryInspector.RefreshAsync(store, transport.Client(), saved.RevisionToken);
            Assert.AreEqual(state.NativeRevision.Sequence + 1, refreshed.State.NativeRevision.Sequence);
            CollectionAssert.AreEqual(state.DesiredFileBytes, refreshed.State.DesiredFileBytes);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ReceiptTransport(DesignRecoveryState saved) : INativeTransport
    {
        public string InstanceId = saved.InstanceId.ToString("D");
        public bool ChangeProcessAfterHandshake;
        public SchematicOperationReceipt.Types.State State = SchematicOperationReceipt.Types.State.Completed;
        public Action<SchematicOperationReceipt>? ChangeReceipt;
        public Action? BeforeReply;
        public Action<SchematicHierarchyDataSnapshot>? ChangeSnapshot;
        public Action? BeforeSnapshotReply;
        public int Snapshots;
        public int FileLocationStatus, FileRefusals;
        public bool AdvanceOnFileRefusal;
        public int Calls, Inspections;
        public InspectSchematicOperation? LastInspection;
        public NativeClient Client() => new(this, "ipc:///fixture-recovery.sock", "process-epoch");

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var message = ApiRequest.Parser.ParseFrom(request).Message;
            IMessage reply;
            bool inspection = message.Is(InspectSchematicOperation.Descriptor);
            if (message.Is(GetAutomationSession.Descriptor))
            {
                var session = new AutomationSession { ProtocolVersion = 1, InstanceId = InstanceId, Epoch = "process-epoch" };
                if (FileLocationStatus != 0) session.HandledRequests.Add(ReadSchematicFileLocations.Descriptor.FullName);
                reply = session;
            }
            else if (message.Is(ReadSchematicFileLocations.Descriptor))
            {
                Assert.AreEqual(3, FileLocationStatus);
                if (AdvanceOnFileRefusal) FileRefusals++;
                return Task.FromResult(new ApiResponse { Header = new() { KicadToken = "process-epoch" },
                    Status = new() { Status = (ApiStatusCode)FileLocationStatus, ErrorMessage = "file observation refused" } }.ToByteArray());
            }
            else if (inspection)
            {
                Inspections++;
                LastInspection = message.Unpack<InspectSchematicOperation>();
                var receipt = new SchematicOperationReceipt
                {
                    Document = LastInspection.Document.Clone(), DocumentEpoch = LastInspection.DocumentEpoch,
                    OperationId = LastInspection.OperationId, State = State,
                    ExpectedRequestVerified = State != SchematicOperationReceipt.Types.State.NotFound,
                    Result = State == SchematicOperationReceipt.Types.State.Completed ? new()
                    {
                        Revision = new() { Epoch = saved.NativeRevision.Epoch, Sequence = saved.NativeRevision.Sequence + 1 }
                    } : null,
                    NativeStatusCode = State == SchematicOperationReceipt.Types.State.Rejected ? 3 : 0
                };
                ChangeReceipt?.Invoke(receipt);
                BeforeReply?.Invoke();
                reply = receipt;
            }
            else if (message.Is(ReadSchematicHierarchyData.Descriptor))
            {
                Snapshots++;
                var requestDocument = message.Unpack<ReadSchematicHierarchyData>().Document;
                Assert.AreEqual(saved.PendingMutation?.Document ?? saved.Baseline.Schematic.Document, requestDocument);
                var snapshot = new SchematicHierarchyDataSnapshot
                {
                    Data = saved.Observed.Clone(),
                    Revision = new() { Epoch = saved.NativeRevision.Epoch, Sequence = saved.NativeRevision.Sequence + 1 + (ulong)FileRefusals },
                    TrackingComplete = false
                };
                ChangeSnapshot?.Invoke(snapshot);
                BeforeSnapshotReply?.Invoke();
                reply = snapshot;
            }
            else throw new AssertFailedException("Recovery must never dispatch a native mutation.");
            return Task.FromResult(new ApiResponse
            {
                Header = new() { KicadToken = inspection && ChangeProcessAfterHandshake ? "replacement-process" : "process-epoch" },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(reply)
            }.ToByteArray());
        }
    }
}
