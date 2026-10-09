using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class DocumentLifecycleTools(InstanceRegistry registry)
{
    private static readonly object RevertReceiptsGate = new();
    private static readonly Dictionary<string, RevertReceiptEntry> RevertReceipts = new(StringComparer.Ordinal);

    [McpServerTool(Name = "kicad_document_save", ReadOnly = false),
     Description("Save an explicit schematic hierarchy or PCB only if the supplied kicad_document_state observation still matches the native process, document, revision, content and loaded file versions. Supply a new operation UUID, and reuse that exact request after a timeout. Returns a retained operation result; failed or uncertain saves require inspection. A failed save is never reported as saved and the editor keeps the unsaved changes. Its errorMessage names every cause and the next step. Failed-save errorCodes: file_not_writable, the file system would not let KiCad write the files in blockedFiles (a read-only file or folder, a full disk); native_save_refused, KiCad refused for a reason that making the document's files writable does not fix, for example a project KiCad opened read-only or a project lock KiCad could not take (errorMessage says what to change instead, such as making the lock file writable or deleting it, then reopening the project); native_save_failed, writing failed and KiCad found no cause (check the disk and save again); partial_save, saving stopped after replacing the files in writtenFiles, so the disk mixes old and new files until a save succeeds; file_changed_during_save, another program changed a file during the save and KiCad kept that version; file_unreadable_during_save, KiCad could not read a file to confirm nobody changed it; save_outside_document, the save tried to write a design file that is not part of the observed document. If this call is cancelled or times out, call kicad_document_operation with the same operation ID to learn whether KiCad started it. Does not close editors or promise atomic multi-file persistence."),
     KiCadCapability("document-lifecycle", "native-api", "document state observation, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits", "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp",
         "NativeSessionTests.CheckedBatchesRejectChangedStateAndPreserveNativeUndo")]
    public async Task<CallToolResult> Save(string instanceId, string expectedStateJson, string operationId,
        CancellationToken cancellationToken)
    {
        LifecycleOperationResult? result = null;
        var response = await InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = Parse<DocumentLifecycleState>(expectedStateJson);
            DocumentStateTools.ValidateTarget(expected.Document);
            string id = OperationId(operationId);
            var client = registry.Client(instanceId);
            if (expected.ProcessEpoch != client.Epoch || !Guid.TryParseExact(expected.NativeIdentity, "D", out _)
                || expected.Revision is null || !Guid.TryParseExact(expected.Revision.Epoch, "D", out _)
                || expected.StateSha256.Length != 64 || !expected.StateSha256.All(char.IsAsciiHexDigitLower))
                throw new AutomationException("invalid_save_observation", "Use a complete observation from this native process; it cannot be replaced by a filename or guessed revision.");
            result = await Dispatch("save", id, () => client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(new()
                { Document = expected.Document.Clone(), ExpectedState = expected, OperationId = id }, cancellationToken));
            ValidateResult(result, expected.Document, id, client.Epoch, LifecycleOperationStatus.LosSaved);
            return SchematicJson.Formatter.Format(result);
        });
        if (result is not null && !(response.IsError ?? false))
        {
            using var json = JsonDocument.Parse(SchematicJson.Formatter.Format(result));
            response.StructuredContent = json.RootElement.Clone();
            response.IsError = result.Status != LifecycleOperationStatus.LosSaved;
        }
        return response;
    }

    [McpServerTool(Name = "kicad_document_close", ReadOnly = false),
     Description("Close an exact schematic or PCB only when its supplied native observation still matches a clean loaded/saved checkpoint. Dirty changes, pending dialogs and associated editors/viewers are refused. This never implicitly saves, discards, forces closure or stops the native process. Reuse the same operation UUID and request after a timeout; the operation receipt survives editor closure. If this call is cancelled or times out, call kicad_document_operation with the same operation ID to learn whether KiCad started it."),
     KiCadCapability("document-lifecycle", "native-api", "clean document state observation, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits",
         "NativeSessionTests.CheckedBatchesRejectChangedStateAndPreserveNativeUndo")]
    public async Task<CallToolResult> Close(string instanceId, string expectedStateJson, string operationId,
        CancellationToken cancellationToken)
    {
        LifecycleOperationResult? result = null;
        var response = await InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = Parse<DocumentLifecycleState>(expectedStateJson);
            DocumentStateTools.ValidateTarget(expected.Document);
            string id = OperationId(operationId);
            var client = registry.Client(instanceId);
            if (expected.ProcessEpoch != client.Epoch)
                throw new AutomationException("stale_process_epoch", "Use an observation from this native process epoch.");
            result = await Dispatch("close", id, () => client.InvokeAsync<CheckedCloseDocument, LifecycleOperationResult>(new()
                { Document = expected.Document.Clone(), ExpectedState = expected, OperationId = id }, cancellationToken));
            ValidateResult(result, expected.Document, id, client.Epoch, LifecycleOperationStatus.LosClosed);
            return SchematicJson.Formatter.Format(result);
        });
        if (result is not null && !(response.IsError ?? false))
        {
            using var json = JsonDocument.Parse(SchematicJson.Formatter.Format(result));
            response.StructuredContent = json.RootElement.Clone();
            response.IsError = result.Status != LifecycleOperationStatus.LosClosed;
        }
        return response;
    }

    [McpServerTool(Name = "kicad_document_revert", ReadOnly = false),
     Description("Revert an explicitly observed schematic hierarchy or PCB to the files currently on disk. The request requires the complete native lifecycle observation, the matching process epoch and a caller-owned operation UUID; KiCad refuses no-op or stale targets before the legacy revert command is dispatched. The MCP verifies that the editor is clean and its file baselines are unchanged after the reload, then stores an atomic state-directory receipt for exact replay after an MCP restart. Revert discards unsaved in-memory edits. The native legacy command has no process-owned receipt, so a delivered transport loss is reported as operation_outcome_unknown and must be resolved by reading kicad_document_state before another action."),
     KiCadCapability("document-lifecycle", "compiled-mcp plus native-api plus durable receipt", "complete lifecycle observation, process epoch, operation UUID and state-directory receipt"),
     KiCadVerification(KiCadVerificationLevel.InProcess, "DocumentLifecycleToolTests.RevertRequiresCompleteObservationAndVerifiesCleanState")]
    public Task<CallToolResult> Revert(string instanceId, string expectedStateJson, string operationId,
        CancellationToken cancellationToken) => InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = Parse<DocumentLifecycleState>(expectedStateJson);
            DocumentStateTools.ValidateTarget(expected.Document);
            string id = OperationId(operationId);
            var client = registry.Client(instanceId);
            ValidateRevertObservation(expected, id, client.Epoch);

            string fingerprint = RevertFingerprint(instanceId, expected, id);
            if (TryReadRevertReceipt(id, fingerprint, out RevertReceiptEntry? replay))
            {
                if (replay!.Outcome == RevertOutcome.Unknown)
                    throw new AutomationException("operation_outcome_unknown", replay.Message);
                return replay.Result! with { Replayed = true };
            }

            // Read immediately before dispatch.  The native legacy command cannot consume the
            // lifecycle state atomically, so this read is an explicit admission boundary and the
            // post-read below refuses to claim success if the editor changed during the reload.
            var before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = expected.Document.Clone() }, cancellationToken);
            if (!before.Equals(expected))
                throw new AutomationException("stale_document_state", "The document changed before revert; obtain a fresh kicad_document_state observation and retry with a new operation UUID.");
            ValidateRevertObservation(before, id, client.Epoch);

            try
            {
                await client.InvokeAsync<RevertDocument, Empty>(new()
                {
                    Document = expected.Document.Clone(), OperationId = id
                }, cancellationToken);
            }
            catch (NngException error) when (error.RequestDelivered)
            {
                string message = $"KiCad received the revert request for operation {id} but did not answer ({error.Message}). Inspect kicad_document_state before another action; the legacy native revert command has no process-owned receipt.";
                RememberRevertUnknown(id, fingerprint, instanceId, client.Epoch, message);
                throw new AutomationException("operation_outcome_unknown", message);
            }
            catch (NngException error)
            {
                throw new AutomationException("native_not_reached",
                    $"KiCad did not receive the revert request for operation {id} ({error.Message}); no in-memory changes were intentionally discarded. Retry with a fresh observation and operation UUID.");
            }

            DocumentLifecycleState after;
            try
            {
                after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                    new() { Document = expected.Document.Clone() }, cancellationToken);
            }
            catch (Exception error) when (error is NngException or NativeApiException)
            {
                string message = $"KiCad accepted revert operation {id}, but the post-revert lifecycle state could not be read ({error.Message}). Inspect kicad_document_state before another action; the outcome is indeterminate.";
                RememberRevertUnknown(id, fingerprint, instanceId, client.Epoch, message);
                throw new AutomationException("revert_state_not_confirmed", message);
            }

            if (!after.Document.Equals(expected.Document) || after.ProcessEpoch != client.Epoch)
            {
                string message = $"KiCad returned a post-revert state for a different document or process on operation {id}; inspect kicad_document_state before another action.";
                RememberRevertUnknown(id, fingerprint, instanceId, client.Epoch, message);
                throw new AutomationException("revert_state_not_confirmed", message);
            }

            if (!RevertStateIsClean(after))
            {
                string message = $"KiCad completed revert operation {id}, but the editor or its file baselines are not clean in the post-revert observation. Inspect kicad_document_state before another action.";
                RememberRevertUnknown(id, fingerprint, instanceId, client.Epoch, message);
                throw new AutomationException("revert_state_not_confirmed", message);
            }

            var result = new DocumentRevertResult
            {
                InstanceId = instanceId,
                ProcessEpoch = client.Epoch,
                OperationId = id,
                Document = expected.Document.Clone(),
                OperationOutcome = "completed",
                Committed = true,
                Replayed = false,
                NativeReceiptAvailable = false,
                DiscardedUnsavedChanges = true,
                BeforeState = before,
                AfterState = after,
                PostStateVerified = true,
                Verification = "native lifecycle state is clean and every observed file baseline is unchanged",
                TransportRetrySafe = false
            };
            RememberRevertCompleted(id, fingerprint, result);
            return result;
        });

    [McpServerTool(Name = "kicad_document_revert_receipt", ReadOnly = true),
     Description("Read the durable MCP receipt for kicad_document_revert by its exact operation UUID and process epoch. The receipt is recovered from the instance state directory after an MCP restart while the native process epoch remains explicit. This never reverts or retries a document. A completed receipt can be replayed safely; an unknown receipt requires a fresh kicad_document_state observation because the legacy native command does not retain a process-owned receipt."),
     KiCadCapability("document-lifecycle", "compiled-mcp plus durable state receipt", "operation UUID, process epoch and state-directory receipt"),
     KiCadVerification(KiCadVerificationLevel.InProcess, "DocumentLifecycleToolTests.RevertRequiresCompleteObservationAndVerifiesCleanState")]
    public Task<CallToolResult> RevertReceipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken) => InstanceToolBoundary.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEpoch(expectedProcessEpoch);
            string id = OperationId(operationId);
            lock (RevertReceiptsGate)
            {
                // Prefer the atomic state-directory copy so a fresh MCP process and the current
                // process observe the same receipt. Fall back to memory only when persistence was
                // unavailable for this operation.
                RevertReceiptEntry? receipt = ReadPersistedRevertReceipt(id)
                    ?? (RevertReceipts.TryGetValue(id, out var inMemory) ? inMemory : null);
                if (receipt is null || receipt.InstanceId != instanceId || receipt.ProcessEpoch != expectedProcessEpoch)
                    return Task.FromResult<object>(new
                    {
                        instanceId, processEpoch = expectedProcessEpoch, operationId = id,
                        found = false, nativeReceiptAvailable = false, retryDispatched = false
                    });
                RevertReceipts[id] = receipt;
                if (receipt.Outcome == RevertOutcome.Unknown)
                    return Task.FromResult<object>(new
                    {
                        instanceId, processEpoch = expectedProcessEpoch, operationId = id,
                        found = true, outcome = "indeterminate", nativeReceiptAvailable = false,
                        retryDispatched = false, errorCode = "operation_outcome_unknown", message = receipt.Message
                    });
                return Task.FromResult<object>(new
                {
                    instanceId, processEpoch = expectedProcessEpoch, operationId = id,
                    found = true, outcome = "completed", nativeReceiptAvailable = false,
                    retryDispatched = false, receipt = receipt.Result
                });
            }
        });

    [McpServerTool(Name = "kicad_document_operation", ReadOnly = true),
     Description("Read the retained result of an exact document lifecycle operation in its original native process. This never retries a save, launches an editor or adopts a restarted process. Receipts survive editor closure and MCP reconnect while that native process remains alive. The error operation_not_started means KiCad has no record of starting that operation ID (for example the call was cancelled before KiCad received it), so it saved or closed nothing for it."),
     KiCadCapability("document-lifecycle", "native-api", "document, operation UUID, process epoch"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits",
         "NativeSessionTests.CheckedBatchesRejectChangedStateAndPreserveNativeUndo")]
    public Task<CallToolResult> Operation(string instanceId, string documentJson, string operationId,
        string processEpoch, CancellationToken cancellationToken) => InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = Parse<DocumentSpecifier>(documentJson);
            DocumentStateTools.ValidateTarget(document);
            string id = OperationId(operationId);
            var client = registry.Client(instanceId);
            if (processEpoch != client.Epoch)
                throw new AutomationException("stale_process_epoch", "The operation belongs to another native process epoch.");
            LifecycleOperationResult result;
            try
            {
                result = await client.InvokeAsync<ReadLifecycleOperation, LifecycleOperationResult>(new()
                    { Document = document, OperationId = id, ProcessEpoch = processEpoch }, cancellationToken);
            }
            catch (NativeApiException error) when (error.Status == 3
                && error.Message.StartsWith(NativeUnknownOperation + ":", StringComparison.Ordinal))
            {
                throw new AutomationException("operation_not_started",
                    $"KiCad (process epoch {processEpoch}) has no record of starting a save or close with operation ID {id}, "
                    + "so it saved or closed nothing for it. Usually the call was cancelled or timed out before KiCad received "
                    + "it; KiCad also keeps no record of a request it refused before starting (for example a malformed "
                    + "request). Read kicad_document_state: the editor keeps any unsaved changes. Then repeat the original "
                    + "request with the same operation ID, or save or close again with a fresh observation and a new "
                    + "operation ID.");
            }
            ValidateResult(result, document, id, processEpoch);
            return SchematicJson.Formatter.Format(result);
        });

    // Fixed marker (DOCUMENT_LIFECYCLE_CONTROLLER::UNKNOWN_OPERATION_MARKER) that starts, followed by ':',
    // the native refusal to read an operation ID the process never started. Only the marker is matched.
    internal const string NativeUnknownOperation = "lifecycle_operation_not_started";

    // A transport failure is not a lifecycle result. Say whether KiCad received the request, so the
    // caller knows whether it may still complete, and how to find out without saving twice.
    private static async Task<LifecycleOperationResult> Dispatch(string action, string id,
        Func<Task<LifecycleOperationResult>> send)
    {
        try { return await send(); }
        catch (NngException error) when (error.RequestDelivered)
        {
            throw new AutomationException("operation_outcome_unknown",
                $"KiCad received the {action} request for operation {id} but did not answer ({error.Message}). It may still "
                + $"finish. Do not assume it {(action == "save" ? "saved" : "closed")} or failed: call kicad_document_operation "
                + "with this operation ID, or repeat this exact request with the same operation ID; KiCad runs it at most once.");
        }
        catch (NngException error)
        {
            throw new AutomationException("native_not_reached",
                $"KiCad did not accept the {action} request for operation {id} ({error.Message}), so it never received it and "
                + $"{(action == "save" ? "nothing was saved; the editor keeps any unsaved changes" : "the editor stays open")}. "
                + "Check that the KiCad instance is running and responsive, then repeat this exact request with the same "
                + "operation ID.");
        }
    }

    private static T Parse<T>(string json) where T : IMessage<T>, new()
    {
        try { return SchematicJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_lifecycle_request", "Provide a valid native lifecycle observation or document descriptor: " + error.Message); }
    }

    private static string OperationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "Provide a nonempty operation UUID.");
        return id.ToString("D");
    }

    private static void ValidateEpoch(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var epoch) || epoch == Guid.Empty)
            throw new AutomationException("invalid_process_epoch", "Provide a nonempty native process epoch UUID.");
    }

    private static void ValidateRevertObservation(DocumentLifecycleState state, string operationId, string processEpoch)
    {
        if (state.ProcessEpoch != processEpoch)
            throw new AutomationException("stale_process_epoch", "The lifecycle observation belongs to another native process epoch.");
        if (!Guid.TryParseExact(state.NativeIdentity, "D", out var identity) || identity == Guid.Empty
            || state.Revision is null || !Guid.TryParseExact(state.Revision.Epoch, "D", out var revisionEpoch)
            || revisionEpoch == Guid.Empty || state.StateSha256.Length != 64
            || !state.StateSha256.All(char.IsAsciiHexDigitLower))
            throw new AutomationException("invalid_revert_observation", "Use a complete lifecycle observation with native identity, revision and state digest; it cannot be replaced by a filename or guessed revision.");
        if (!state.CompleteChangeTracking || !state.DiskBaselineChecked || state.NativeFiles.Count == 0
            || state.FileBaselines.Count != state.NativeFiles.Count
            || state.FileBaselines.Any(file => file is null || file.Status != NativeFileBaselineStatus.NfbsUnchanged
                || !file.BaselineKnown || !file.CurrentKnown || !file.BaselineExists.Equals(file.CurrentExists)
                || file.BaselineSha256 != file.CurrentSha256 || file.BaselineBytes != file.CurrentBytes))
            throw new AutomationException("incomplete_revert_observation", "Revert requires complete unchanged native file baselines so unsaved changes can be identified before they are discarded.");
    }

    private static bool RevertStateIsClean(DocumentLifecycleState state) =>
        state.NativeContentDirty == false && state.DiskBaselineChecked && state.CompleteChangeTracking
        && state.NativeFiles.Count > 0 && state.FileBaselines.Count == state.NativeFiles.Count
        && state.FileBaselines.All(file => file is not null && file.Status == NativeFileBaselineStatus.NfbsUnchanged
            && file.BaselineKnown && file.CurrentKnown && file.BaselineExists.Equals(file.CurrentExists)
            && file.BaselineSha256 == file.CurrentSha256 && file.BaselineBytes == file.CurrentBytes);

    private static string RevertFingerprint(string instanceId, DocumentLifecycleState state, string operationId) =>
        instanceId + "\n" + operationId + "\n" + SchematicJson.Formatter.Format(state);

    private bool TryReadRevertReceipt(string operationId, string fingerprint, out RevertReceiptEntry? receipt)
    {
        lock (RevertReceiptsGate)
        {
            if (!RevertReceipts.TryGetValue(operationId, out receipt))
            {
                receipt = ReadPersistedRevertReceipt(operationId);
                if (receipt is null) return false;
                RevertReceipts[operationId] = receipt;
            }
            if (receipt.Fingerprint != fingerprint)
                throw new AutomationException("operation_reused", "The revert operation UUID was already used for a different document observation; choose a new operation UUID.");
            return true;
        }
    }

    private void RememberRevertUnknown(string operationId, string fingerprint, string instanceId,
        string processEpoch, string message)
    {
        lock (RevertReceiptsGate)
        {
            var receipt = new RevertReceiptEntry(instanceId, processEpoch, fingerprint, RevertOutcome.Unknown, null, message);
            RevertReceipts[operationId] = receipt;
            PersistRevertReceipt(operationId, receipt);
        }
    }

    private void RememberRevertCompleted(string operationId, string fingerprint, DocumentRevertResult result)
    {
        lock (RevertReceiptsGate)
        {
            var receipt = new RevertReceiptEntry(result.InstanceId, result.ProcessEpoch, fingerprint,
                RevertOutcome.Completed, result, "");
            RevertReceipts[operationId] = receipt;
            PersistRevertReceipt(operationId, receipt);
        }
    }

    // Revert is backed by a legacy native command that has no process-owned receipt. Keep the
    // MCP receipt in the same state directory as the instance registration so a new MCP process
    // can replay a completed result or discover an indeterminate outcome without dispatching a
    // second destructive reload. The native process epoch remains part of every record; this is
    // recovery evidence, never permission to adopt a replacement KiCad process.
    private const string RevertReceiptFileName = "document-revert-receipts.json";

    private RevertReceiptEntry? ReadPersistedRevertReceipt(string operationId)
    {
        string path = Path.Combine(registry.StateDirectory, RevertReceiptFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, PersistedRevertReceipt>>(File.ReadAllText(path));
            if (all is null || !all.TryGetValue(operationId, out PersistedRevertReceipt? persisted)) return null;
            if (persisted is null || !string.Equals(persisted.OperationId, operationId, StringComparison.Ordinal))
                throw new AutomationException("revert_receipt_corrupt", "The persisted revert receipt identity does not match its operation UUID.");
            return FromPersisted(persisted);
        }
        catch (JsonException error)
        {
            throw new AutomationException("revert_receipt_corrupt", "The persisted document-revert receipt is not valid JSON: " + error.Message);
        }
    }

    private void PersistRevertReceipt(string operationId, RevertReceiptEntry receipt)
    {
        try
        {
            Directory.CreateDirectory(registry.StateDirectory);
            string path = Path.Combine(registry.StateDirectory, RevertReceiptFileName);
            var all = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, PersistedRevertReceipt>>(File.ReadAllText(path))
                    ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
            all[operationId] = ToPersisted(operationId, receipt);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(all));
            File.Move(temporary, path, true);
        }
        catch (JsonException error)
        {
            throw new AutomationException("revert_receipt_corrupt", "The persisted document-revert receipt cannot be read: " + error.Message);
        }
        catch (IOException error)
        {
            // The native reload already completed. Keep the in-memory receipt and report the
            // persistence failure to the caller instead of claiming durable recovery evidence.
            throw new AutomationException("revert_receipt_persistence_failed", "The document was reverted, but its MCP receipt could not be persisted: " + error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new AutomationException("revert_receipt_persistence_failed", "The document was reverted, but the MCP state directory is not writable: " + error.Message);
        }
    }

    private static PersistedRevertReceipt ToPersisted(string operationId, RevertReceiptEntry receipt) =>
        new(operationId, receipt.InstanceId, receipt.ProcessEpoch, receipt.Fingerprint, receipt.Outcome,
            receipt.Result is null ? null : new PersistedRevertResult(
                receipt.Result.InstanceId, receipt.Result.ProcessEpoch, receipt.Result.OperationId,
                SchematicJson.Formatter.Format(receipt.Result.Document), receipt.Result.OperationOutcome,
                receipt.Result.Committed, receipt.Result.NativeReceiptAvailable,
                receipt.Result.DiscardedUnsavedChanges, Convert.ToBase64String(receipt.Result.BeforeState?.ToByteArray() ?? []),
                Convert.ToBase64String(receipt.Result.AfterState?.ToByteArray() ?? []), receipt.Result.PostStateVerified,
                receipt.Result.Verification, receipt.Result.TransportRetrySafe), receipt.Message);

    private static RevertReceiptEntry FromPersisted(PersistedRevertReceipt receipt)
    {
        DocumentRevertResult? result = null;
        if (receipt.Result is not null)
        {
            try
            {
                result = new DocumentRevertResult
                {
                    InstanceId = receipt.Result.InstanceId,
                    ProcessEpoch = receipt.Result.ProcessEpoch,
                    OperationId = receipt.Result.OperationId,
                    Document = SchematicJson.Parser.Parse<DocumentSpecifier>(receipt.Result.DocumentJson),
                    OperationOutcome = receipt.Result.OperationOutcome,
                    Committed = receipt.Result.Committed,
                    Replayed = false,
                    NativeReceiptAvailable = receipt.Result.NativeReceiptAvailable,
                    DiscardedUnsavedChanges = receipt.Result.DiscardedUnsavedChanges,
                    BeforeState = ParseState(receipt.Result.BeforeStateBase64),
                    AfterState = ParseState(receipt.Result.AfterStateBase64),
                    PostStateVerified = receipt.Result.PostStateVerified,
                    Verification = receipt.Result.Verification,
                    TransportRetrySafe = receipt.Result.TransportRetrySafe
                };
            }
            catch (Exception error) when (error is FormatException or InvalidProtocolBufferException or InvalidJsonException)
            {
                throw new AutomationException("revert_receipt_corrupt", "The persisted document-revert result is malformed: " + error.Message);
            }
        }
        if (!System.Enum.IsDefined(receipt.Outcome) || receipt.Outcome == RevertOutcome.Completed && result is null
            || receipt.Outcome == RevertOutcome.Unknown && string.IsNullOrWhiteSpace(receipt.Message))
            throw new AutomationException("revert_receipt_corrupt", "The persisted document-revert receipt has an invalid outcome.");
        return new(receipt.InstanceId, receipt.ProcessEpoch, receipt.Fingerprint, receipt.Outcome, result, receipt.Message ?? "");
    }

    private static DocumentLifecycleState? ParseState(string value) => string.IsNullOrEmpty(value)
        ? null : DocumentLifecycleState.Parser.ParseFrom(Convert.FromBase64String(value));

    private sealed record PersistedRevertReceipt(string OperationId, string InstanceId, string ProcessEpoch,
        string Fingerprint, RevertOutcome Outcome, PersistedRevertResult? Result, string? Message);

    private sealed record PersistedRevertResult(string InstanceId, string ProcessEpoch, string OperationId,
        string DocumentJson, string OperationOutcome, bool Committed, bool NativeReceiptAvailable,
        bool DiscardedUnsavedChanges, string BeforeStateBase64, string AfterStateBase64, bool PostStateVerified,
        string Verification, bool TransportRetrySafe);

    private enum RevertOutcome { Completed, Unknown }

    private sealed record RevertReceiptEntry(string InstanceId, string ProcessEpoch, string Fingerprint,
        RevertOutcome Outcome, DocumentRevertResult? Result, string Message);

    private sealed record DocumentRevertResult
    {
        public string InstanceId { get; init; } = "";
        public string ProcessEpoch { get; init; } = "";
        public string OperationId { get; init; } = "";
        public DocumentSpecifier Document { get; init; } = new();
        public string OperationOutcome { get; init; } = "";
        public bool Committed { get; init; }
        public bool Replayed { get; init; }
        public bool NativeReceiptAvailable { get; init; }
        public bool DiscardedUnsavedChanges { get; init; }
        public DocumentLifecycleState? BeforeState { get; init; }
        public DocumentLifecycleState? AfterState { get; init; }
        public bool PostStateVerified { get; init; }
        public string Verification { get; init; } = "";
        public bool TransportRetrySafe { get; init; }
    }

    private static void ValidateResult(LifecycleOperationResult result, DocumentSpecifier document, string id, string epoch,
        LifecycleOperationStatus? expectedSuccess = null)
    {
        if (!document.Equals(result.Document) || result.OperationId != id || result.ProcessEpoch != epoch
            || result.Status is not (LifecycleOperationStatus.LosSaved or LifecycleOperationStatus.LosRejected
                or LifecycleOperationStatus.LosFailed or LifecycleOperationStatus.LosIndeterminate or LifecycleOperationStatus.LosClosed)
            || (expectedSuccess is not null && result.Status is (LifecycleOperationStatus.LosSaved or LifecycleOperationStatus.LosClosed)
                && result.Status != expectedSuccess))
            throw new AutomationException("invalid_lifecycle_result", "Native operation result did not match the requested target and identity.");
    }
}
