using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DocumentLifecycleToolTests
{
    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content.Single()).Text;

    [TestMethod]
    public async Task InvalidInputAndCancellationDoNotDispatchSave()
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-input-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            var tool = new DocumentLifecycleTools(registry);
            foreach (string json in new[] { "{", "{}", "null" })
                Assert.IsTrue((await tool.Save("missing", json, Guid.NewGuid().ToString("D"), default)).IsError);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => tool.Save("missing", "{}", "bad", cancelled.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() => tool.Close("missing", "{}", "bad", cancelled.Token));
            Assert.IsTrue((await tool.Close("missing", "{}", Guid.NewGuid().ToString("D"), default)).IsError);
            Assert.AreEqual(0, transport.LifecycleCalls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task NativeResultsRetainIdentityFailureStatusAndRecoveryQueries()
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-results-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new DocumentLifecycleTools(registry);
            var state = new DocumentLifecycleState
            {
                Document = new() { Type = (DocumentType)3, BoardFilename = "fixture.kicad_pcb",
                    Project = new() { Name = "fixture", Path = directory } },
                ProcessEpoch = transport.Session.Epoch, NativeIdentity = Guid.NewGuid().ToString("D"),
                Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 5 }, StateSha256 = new string('a', 64)
            };
            foreach (var status in new[] { LifecycleOperationStatus.LosSaved, LifecycleOperationStatus.LosRejected,
                         LifecycleOperationStatus.LosFailed, LifecycleOperationStatus.LosIndeterminate })
            {
                string id = Guid.NewGuid().ToString("D");
                transport.Status = status;
                var response = await tool.Save(transport.Session.InstanceId, SchematicJson.Formatter.Format(state), id, default);
                Assert.AreEqual(status != LifecycleOperationStatus.LosSaved, response.IsError ?? false);
                var result = SchematicJson.Parser.Parse<LifecycleOperationResult>(Text(response));
                Assert.AreEqual(status, result.Status);
                Assert.AreEqual(id, result.OperationId);
                Assert.IsNotNull(response.StructuredContent);
                var observed = await tool.Operation(transport.Session.InstanceId, SchematicJson.Formatter.Format(state.Document),
                    id, state.ProcessEpoch, default);
                Assert.IsFalse(observed.IsError ?? false); // Reading a failed receipt itself succeeds.
                Assert.AreEqual(result, SchematicJson.Parser.Parse<LifecycleOperationResult>(Text(observed)));
            }
            int calls = transport.LifecycleCalls;
            var wrongEpoch = state.Clone(); wrongEpoch.ProcessEpoch = Guid.NewGuid().ToString("D");
            Assert.IsTrue((await tool.Save(transport.Session.InstanceId, SchematicJson.Formatter.Format(wrongEpoch),
                Guid.NewGuid().ToString("D"), default)).IsError);
            Assert.AreEqual(calls, transport.LifecycleCalls);
            transport.WrongTarget = true;
            Assert.IsTrue((await tool.Save(transport.Session.InstanceId, SchematicJson.Formatter.Format(state),
                Guid.NewGuid().ToString("D"), default)).IsError);
            transport.WrongTarget = false;
            transport.Status = LifecycleOperationStatus.LosClosed;
            var closed = await tool.Close(transport.Session.InstanceId, SchematicJson.Formatter.Format(state),
                Guid.NewGuid().ToString("D"), default);
            Assert.IsFalse(closed.IsError ?? false);
            Assert.AreEqual(LifecycleOperationStatus.LosClosed, SchematicJson.Parser.Parse<LifecycleOperationResult>(Text(closed)).Status);
            transport.Status = LifecycleOperationStatus.LosSaved;
            Assert.IsTrue((await tool.Close(transport.Session.InstanceId, SchematicJson.Formatter.Format(state),
                Guid.NewGuid().ToString("D"), default)).IsError, "A save result cannot be reported as a successful close.");
            transport.Status = LifecycleOperationStatus.LosReverted;
            foreach (bool close in new[] { false, true })
            {
                var wrongKind = close
                    ? await tool.Close(transport.Session.InstanceId, SchematicJson.Formatter.Format(state), Guid.NewGuid().ToString("D"), default)
                    : await tool.Save(transport.Session.InstanceId, SchematicJson.Formatter.Format(state), Guid.NewGuid().ToString("D"), default);
                Assert.IsTrue(wrongKind.IsError, "A revert receipt cannot become save or close success.");
                Assert.AreEqual("invalid_lifecycle_result", wrongKind.StructuredContent!.Value.GetProperty("code").GetString());
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    // Timeouts need KiCad to stay silent for the full 15 s native deadline per case, so the
    // mapping from the transport's delivery evidence to the tool error is checked here in
    // isolation; the native journey proves the cancelled, never-received case end to end.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransportFailuresSayWhetherKiCadReceivedTheRequest(bool delivered)
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-transport-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new DocumentLifecycleTools(registry);
            var state = State(directory, transport.Session.Epoch);
            transport.Failure = new NngException(5, "Timed out", requestDelivered: delivered);
            foreach (bool close in new[] { false, true })
            {
                string id = Guid.NewGuid().ToString("D");
                var response = close
                    ? await tool.Close(transport.Session.InstanceId, SchematicJson.Formatter.Format(state), id, default)
                    : await tool.Save(transport.Session.InstanceId, SchematicJson.Formatter.Format(state), id, default);
                Assert.IsTrue(response.IsError, "A transport failure is never a lifecycle success.");
                var error = response.StructuredContent!.Value;
                string message = error.GetProperty("message").GetString()!;
                Assert.AreEqual(delivered ? "operation_outcome_unknown" : "native_not_reached", error.GetProperty("code").GetString(), message);
                StringAssert.Contains(message, id, "The error names the operation to query or repeat.");
                StringAssert.Contains(message, delivered ? "kicad_document_operation" : "never received it");
                StringAssert.Contains(message, "same operation ID");
                if (!delivered && !close) StringAssert.Contains(message, "nothing was saved");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task UnknownOperationExplainsThatKiCadNeverStartedIt()
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-unknown-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new DocumentLifecycleTools(registry);
            var state = State(directory, transport.Session.Epoch);
            string id = Guid.NewGuid().ToString("D");
            // The native refusal starts with a fixed marker; the explanation after it may change freely.
            transport.ReceiptError = DocumentLifecycleTools.NativeUnknownOperation + ": any explanation";
            var unknown = await tool.Operation(transport.Session.InstanceId, SchematicJson.Formatter.Format(state.Document), id, state.ProcessEpoch, default);
            Assert.IsTrue(unknown.IsError);
            Assert.AreEqual("operation_not_started", unknown.StructuredContent!.Value.GetProperty("code").GetString());
            string message = unknown.StructuredContent!.Value.GetProperty("message").GetString()!;
            StringAssert.Contains(message, id);
            StringAssert.Contains(message, "has no record of starting");
            StringAssert.Contains(message, "saved or closed nothing");
            // Any other native refusal keeps its own status, including prose that merely resembles the marker.
            foreach (string other in new[] { "Lifecycle operation belongs to another document",
                         "Lifecycle operation is not known in this process", DocumentLifecycleTools.NativeUnknownOperation + "_later" })
            {
                transport.ReceiptError = other;
                var refused = await tool.Operation(transport.Session.InstanceId, SchematicJson.Formatter.Format(state.Document), id, state.ProcessEpoch, default);
                Assert.AreEqual("native_status_3", refused.StructuredContent!.Value.GetProperty("code").GetString(), other);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RevertRequiresCompleteObservationAndVerifiesCleanState()
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-revert-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.Epoch = Guid.NewGuid().ToString("D");
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new DocumentLifecycleTools(registry);
            var incomplete = State(directory, transport.Session.Epoch);
            incomplete.NativeContentDirty = true;
            string operationId = Guid.NewGuid().ToString("D");
            var refused = await tool.Revert(transport.Session.InstanceId, SchematicJson.Formatter.Format(incomplete), operationId, default);
            Assert.IsTrue(refused.IsError);
            Assert.AreEqual("incomplete_revert_observation", refused.StructuredContent!.Value.GetProperty("code").GetString());
            Assert.AreEqual(0, transport.RevertCalls, "Incomplete observations must be refused before the legacy native revert command.");

            var complete = CompleteState(directory, transport.Session.Epoch, dirty: true);
            transport.LifecycleState = complete.Clone();
            var reverted = await tool.Revert(transport.Session.InstanceId, SchematicJson.Formatter.Format(complete), operationId, default);
            Assert.IsFalse(reverted.IsError ?? false, reverted.StructuredContent?.GetRawText());
            Assert.IsTrue(reverted.StructuredContent!.Value.GetProperty("committed").GetBoolean());
            Assert.IsTrue(reverted.StructuredContent.Value.GetProperty("postStateVerified").GetBoolean());
            Assert.IsTrue(reverted.StructuredContent.Value.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.AreEqual(1, transport.RevertCalls);

            var replay = await tool.Revert(transport.Session.InstanceId, SchematicJson.Formatter.Format(complete), operationId, default);
            Assert.IsFalse(replay.IsError ?? false, replay.StructuredContent?.GetRawText());
            Assert.IsTrue(replay.StructuredContent!.Value.GetProperty("replayed").GetBoolean());
            Assert.AreEqual(1, transport.RevertCalls, "Exact replay must use the process-local receipt instead of dispatching a second destructive reload.");

            var receipt = await tool.RevertReceipt(transport.Session.InstanceId, transport.Session.Epoch, operationId, default);
            Assert.IsFalse(receipt.IsError ?? false, receipt.StructuredContent?.GetRawText());
            Assert.IsTrue(receipt.StructuredContent!.Value.GetProperty("found").GetBoolean());
            Assert.AreEqual("completed", receipt.StructuredContent.Value.GetProperty("outcome").GetString());
            Assert.IsTrue(receipt.StructuredContent.Value.GetProperty("nativeReceiptAvailable").GetBoolean());

            var changed = complete.Clone(); changed.StateSha256 = new string('b', 64);
            var reused = await tool.Revert(transport.Session.InstanceId, SchematicJson.Formatter.Format(changed), operationId, default);
            Assert.IsTrue(reused.IsError);
            Assert.AreEqual("operation_reused", reused.StructuredContent!.Value.GetProperty("code").GetString());

            // A new MCP tool/registry instance must recover the receipt from the state directory,
            // rather than depending on the original process's static replay cache.
            var restarted = new DocumentLifecycleTools(new InstanceRegistry(new LifecycleTransport(), directory));
            var recovered = await restarted.RevertReceipt(transport.Session.InstanceId, transport.Session.Epoch, operationId, default);
            Assert.IsFalse(recovered.IsError ?? false, recovered.StructuredContent?.GetRawText());
            Assert.IsTrue(recovered.StructuredContent!.Value.GetProperty("found").GetBoolean());
            Assert.AreEqual("completed", recovered.StructuredContent.Value.GetProperty("outcome").GetString());
            Assert.IsTrue(recovered.StructuredContent.Value.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.IsTrue(recovered.StructuredContent.Value.GetProperty("receipt").GetProperty("postStateVerified").GetBoolean());

            // A file entry whose embedded identity does not match its lookup key is rejected
            // fail-closed instead of being treated as an absent receipt.
            string corruptOperation = Guid.NewGuid().ToString("D");
            string receiptPath = Path.Combine(directory, "document-revert-receipts.json");
            await File.WriteAllTextAsync(receiptPath,
                $"{{\"{corruptOperation}\":{{\"operationId\":\"{operationId}\"}}}}");
            var corrupt = await restarted.RevertReceipt(transport.Session.InstanceId, transport.Session.Epoch, corruptOperation, default);
            Assert.IsTrue(corrupt.IsError, corrupt.StructuredContent?.GetRawText());
            Assert.AreEqual("revert_receipt_corrupt", corrupt.StructuredContent!.Value.GetProperty("code").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DeliveredRevertCancellationPersistsUnknownAndBlocksReplay()
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-revert-cancel-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.Epoch = Guid.NewGuid().ToString("D");
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new DocumentLifecycleTools(registry);
            var complete = CompleteState(directory, transport.Session.Epoch, dirty: true);
            transport.LifecycleState = complete.Clone();
            string operationId = Guid.NewGuid().ToString("D");
            transport.RevertFailure = new NngOperationCanceledException(8, "cancelled after send", true, false);

            var cancelled = await tool.Revert(transport.Session.InstanceId, SchematicJson.Formatter.Format(complete), operationId, default);
            Assert.IsTrue(cancelled.IsError, cancelled.StructuredContent?.GetRawText());
            Assert.AreEqual("operation_outcome_unknown", cancelled.StructuredContent!.Value.GetProperty("code").GetString());
            Assert.AreEqual(0, transport.RevertCalls, "The fixture transport failed after delivery evidence before its handler reply.");

            var receipt = await tool.RevertReceipt(transport.Session.InstanceId, transport.Session.Epoch, operationId, default);
            Assert.IsFalse(receipt.IsError ?? false, receipt.StructuredContent?.GetRawText());
            Assert.AreEqual("indeterminate", receipt.StructuredContent!.Value.GetProperty("outcome").GetString());
            transport.RevertFailure = null;
            var replay = await tool.Revert(transport.Session.InstanceId, SchematicJson.Formatter.Format(complete), operationId, default);
            Assert.IsTrue(replay.IsError, replay.StructuredContent?.GetRawText());
            Assert.AreEqual("operation_outcome_unknown", replay.StructuredContent!.Value.GetProperty("code").GetString());
            Assert.AreEqual(0, transport.RevertCalls, "An indeterminate operation must never be dispatched again automatically.");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RevertRetryReconcilesTerminalNativeReceiptWithoutRedispatch()
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-revert-reconcile-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.Epoch = Guid.NewGuid().ToString("D");
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new DocumentLifecycleTools(registry);
            var complete = CompleteState(directory, transport.Session.Epoch, dirty: true);
            transport.LifecycleState = complete.Clone();
            string operationId = Guid.NewGuid().ToString("D");
            transport.RevertFailure = new NngOperationCanceledException(8, "reply lost", true, false);

            var cancelled = await tool.Revert(transport.Session.InstanceId, SchematicJson.Formatter.Format(complete), operationId, default);
            Assert.IsTrue(cancelled.IsError, cancelled.StructuredContent?.GetRawText());
            Assert.AreEqual("operation_outcome_unknown", cancelled.StructuredContent!.Value.GetProperty("code").GetString());

            // Simulate the native process completing the admitted reload after the MCP reply was
            // lost. Reconciliation must read this receipt and the clean state, never dispatching
            // NativeCapabilityCheckedRevertDocument again.
            transport.RevertFailure = null;
            transport.LifecycleState.NativeContentDirty = false;
            transport.LifecycleState.StateSha256 = transport.LifecycleState.CleanCheckpointSha256;
            transport.RevertReceiptResult = new LifecycleOperationResult
            {
                Document = complete.Document.Clone(), OperationId = operationId,
                ProcessEpoch = transport.Session.Epoch, Status = LifecycleOperationStatus.LosReverted,
                RevertCompleted = true
            };
            transport.RevertReceiptExpectedState = complete.Clone();

            var reconciled = await tool.Revert(transport.Session.InstanceId,
                SchematicJson.Formatter.Format(complete), operationId, default);
            Assert.IsFalse(reconciled.IsError ?? false, reconciled.StructuredContent?.GetRawText());
            Assert.IsTrue(reconciled.StructuredContent!.Value.GetProperty("replayed").GetBoolean());
            Assert.IsTrue(reconciled.StructuredContent.Value.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.AreEqual(complete, transport.LastReceiptQuery?.ExpectedState,
                "Receipt reconciliation must name the original observed state.");
            Assert.AreEqual(0, transport.RevertCalls, "Receipt reconciliation must never dispatch a second reload.");

            var receipt = await tool.RevertReceipt(transport.Session.InstanceId, transport.Session.Epoch, operationId, default);
            Assert.IsFalse(receipt.IsError ?? false, receipt.StructuredContent?.GetRawText());
            Assert.AreEqual("completed", receipt.StructuredContent!.Value.GetProperty("outcome").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task AdmittedRevertWithoutNativeDispatchStaysInProgressWithoutDispatch()
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-revert-admitted-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.Epoch = Guid.NewGuid().ToString("D");
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new DocumentLifecycleTools(registry);
            var complete = CompleteState(directory, transport.Session.Epoch, dirty: true);
            string operationId = Guid.NewGuid().ToString("D");
            string fingerprint = DocumentRevertReceiptStore.Fingerprint(transport.Session.InstanceId, operationId,
                SchematicJson.Formatter.Format(complete));
            new DocumentRevertReceiptStore(directory).Reserve(transport.Session.InstanceId, operationId,
                transport.Session.Epoch, fingerprint);

            var refused = await tool.Revert(transport.Session.InstanceId,
                SchematicJson.Formatter.Format(complete), operationId, default);
            Assert.IsTrue(refused.IsError, refused.StructuredContent?.GetRawText());
            Assert.AreEqual("operation_in_progress", refused.StructuredContent!.Value.GetProperty("code").GetString());
            Assert.AreEqual(0, transport.RevertCalls);
            Assert.IsNull(transport.LastReceiptQuery, "An admitted reservation is not proof that native dispatch began.");

            var receipt = await tool.RevertReceipt(transport.Session.InstanceId, transport.Session.Epoch, operationId, default);
            Assert.IsFalse(receipt.IsError ?? false, receipt.StructuredContent?.GetRawText());
            Assert.AreEqual("admitted", receipt.StructuredContent!.Value.GetProperty("outcome").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RevertRetryRefusesNativeReceiptFromAnotherObservedState()
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-revert-state-binding-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.Epoch = Guid.NewGuid().ToString("D");
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new DocumentLifecycleTools(registry);
            var expected = CompleteState(directory, transport.Session.Epoch, dirty: true);
            transport.LifecycleState = expected.Clone();
            string operationId = Guid.NewGuid().ToString("D");
            transport.RevertFailure = new NngOperationCanceledException(8, "reply lost", true, false);
            var interrupted = await tool.Revert(transport.Session.InstanceId,
                SchematicJson.Formatter.Format(expected), operationId, default);
            Assert.IsTrue(interrupted.IsError);

            transport.RevertFailure = null;
            transport.LifecycleState.NativeContentDirty = false;
            transport.RevertReceiptResult = new LifecycleOperationResult
            {
                Document = expected.Document.Clone(), OperationId = operationId,
                ProcessEpoch = transport.Session.Epoch, Status = LifecycleOperationStatus.LosReverted,
                RevertCompleted = true
            };
            transport.RevertReceiptExpectedState = expected.Clone();
            transport.RevertReceiptExpectedState!.StateSha256 = new string('b', 64);

            var refused = await tool.Revert(transport.Session.InstanceId,
                SchematicJson.Formatter.Format(expected), operationId, default);
            Assert.IsTrue(refused.IsError, refused.StructuredContent?.GetRawText());
            Assert.AreEqual("revert_receipt_conflict", refused.StructuredContent!.Value.GetProperty("code").GetString());
            Assert.AreEqual(expected, transport.LastReceiptQuery?.ExpectedState);
            Assert.AreEqual(0, transport.RevertCalls, "Receipt mismatch must not dispatch a reload.");
            var receipt = await tool.RevertReceipt(transport.Session.InstanceId, transport.Session.Epoch, operationId, default);
            Assert.AreEqual("indeterminate", receipt.StructuredContent!.Value.GetProperty("outcome").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static DocumentLifecycleState State(string directory, string epoch) => new()
    {
        Document = new() { Type = (DocumentType)3, BoardFilename = "fixture.kicad_pcb",
            Project = new() { Name = "fixture", Path = directory } },
        ProcessEpoch = epoch, NativeIdentity = Guid.NewGuid().ToString("D"),
        Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 5 }, StateSha256 = new string('a', 64)
    };

    private static DocumentLifecycleState CompleteState(string directory, string epoch, bool dirty) => new()
    {
        Document = new() { Type = (DocumentType)3, BoardFilename = "fixture.kicad_pcb",
            Project = new() { Name = "fixture", Path = directory } },
        ProcessEpoch = epoch, NativeIdentity = Guid.NewGuid().ToString("D"),
        Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 5 }, StateSha256 = new string('a', 64),
        Scope = DocumentLifecycleScope.DlsPcb, NativeContentDirty = dirty,
        ProjectSettingsIncluded = true,
        CleanCheckpointSha256 = new string('a', 64),
        CompleteChangeTracking = true, DiskBaselineChecked = true,
        NativeFiles = { Path.Combine(directory, "fixture.kicad_pcb") },
        FileBaselines = { new NativeFileBaselineState
        {
            Path = Path.Combine(directory, "fixture.kicad_pcb"),
            BaselinePath = Path.Combine(directory, "fixture.kicad_pcb"),
            BaselineKnown = true, BaselineExists = true, BaselineSha256 = new string('c', 64), BaselineBytes = 10,
            CurrentKnown = true, CurrentExists = true, CurrentSha256 = new string('c', 64), CurrentBytes = 10,
            Status = NativeFileBaselineStatus.NfbsUnchanged
        } }
    };

    private sealed class LifecycleTransport : INativeTransport
    {
        public NativeClientTests.FixtureTransport Session { get; } = new();
        public int LifecycleCalls { get; private set; }
        public LifecycleOperationStatus Status { get; set; } = LifecycleOperationStatus.LosSaved;
        public bool WrongTarget { get; set; }
        public NngException? Failure { get; set; }
        public Exception? RevertFailure { get; set; }
        public LifecycleOperationResult? RevertReceiptResult { get; set; }
        public DocumentLifecycleState? RevertReceiptExpectedState { get; set; }
        public ReadLifecycleOperation? LastReceiptQuery { get; private set; }
        public string? ReceiptError { get; set; }
        public int RevertCalls { get; private set; }
        public DocumentLifecycleState LifecycleState { get; set; } = new();
        private LifecycleOperationResult? result;
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var envelope = ApiRequest.Parser.ParseFrom(request);
            if (Failure is not null && (envelope.Message.Is(CheckedSaveDocument.Descriptor) || envelope.Message.Is(CheckedCloseDocument.Descriptor)))
                return Task.FromException<byte[]>(Failure);
            if (RevertFailure is not null && envelope.Message.Is(NativeCapabilityCheckedRevertDocument.Descriptor))
                return Task.FromException<byte[]>(RevertFailure);
            if (ReceiptError is not null && envelope.Message.Is(ReadLifecycleOperation.Descriptor))
                return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                    Status = new() { Status = (ApiStatusCode)3, ErrorMessage = ReceiptError } }.ToByteArray());
            if (envelope.Message.Is(CheckedSaveDocument.Descriptor))
            {
                ++LifecycleCalls;
                var save = envelope.Message.Unpack<CheckedSaveDocument>();
                result = new() { Document = save.Document.Clone(), OperationId = save.OperationId,
                    ProcessEpoch = Session.Epoch, Status = Status, ErrorCode = Status == LifecycleOperationStatus.LosSaved ? "" : "fixture_failure" };
                if (WrongTarget) result.Document.BoardFilename = "other.kicad_pcb";
            }
            else if (envelope.Message.Is(ReadLifecycleOperation.Descriptor))
            {
                ++LifecycleCalls;
                LastReceiptQuery = envelope.Message.Unpack<ReadLifecycleOperation>();
                if (RevertReceiptExpectedState is not null && !RevertReceiptExpectedState.Equals(LastReceiptQuery.ExpectedState))
                    return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                        Status = new() { Status = (ApiStatusCode)3,
                            ErrorMessage = "Lifecycle operation belongs to another observed state" } }.ToByteArray());
                if (RevertReceiptResult is not null)
                    return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                        Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(RevertReceiptResult.Clone()) }.ToByteArray());

                if (result is not null)
                    return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                        Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(result) }.ToByteArray());

                return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                    Status = new() { Status = (ApiStatusCode)3,
                        ErrorMessage = DocumentLifecycleTools.NativeUnknownOperation + ": fixture has no receipt" } }.ToByteArray());
            }
            else if (envelope.Message.Is(CheckedCloseDocument.Descriptor))
            {
                ++LifecycleCalls;
                var close = envelope.Message.Unpack<CheckedCloseDocument>();
                result = new() { Document = close.Document.Clone(), OperationId = close.OperationId,
                    ProcessEpoch = Session.Epoch, Status = Status };
                if (WrongTarget) result.Document.BoardFilename = "other.kicad_pcb";
            }
            else if (envelope.Message.Is(ReadDocumentLifecycleState.Descriptor))
            {
                return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                    Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(LifecycleState.Clone()) }.ToByteArray());
            }
            else if (envelope.Message.Is(NativeCapabilityCheckedRevertDocument.Descriptor))
            {
                ++RevertCalls;
                var revert = envelope.Message.Unpack<NativeCapabilityCheckedRevertDocument>();
                LifecycleState.NativeContentDirty = false;
                var native = new LifecycleOperationResult
                {
                    Document = revert.Document.Clone(), OperationId = revert.OperationId,
                    ProcessEpoch = Session.Epoch, Status = LifecycleOperationStatus.LosReverted,
                    RevertCompleted = true,
                    ObservedState = LifecycleState.Clone()
                };
                return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                    Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(native) }.ToByteArray());
            }
            else return Session.ExchangeAsync(endpoint, request, timeout, cancellationToken);
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(result!) }.ToByteArray());
        }
    }
}
