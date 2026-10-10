using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
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
     Description("Revert an explicitly observed schematic hierarchy or PCB to the files currently on disk. The native checked lifecycle controller admits the exact document digest and unchanged file baselines on KiCad's GUI thread, reserves the operation UUID, performs the reload, and retains a process-owned terminal receipt. The MCP also retains a per-instance durable receipt. Reload failure has no automatic rollback; cancellation or transport loss after native dispatch is operation_outcome_unknown and must be resolved by reading kicad_document_state before another action. An admitted receipt is fail-closed: if the caller exits before native dispatch, it remains operation_in_progress and is never dispatched a second time."),
     KiCadCapability("document-lifecycle", "compiled-mcp plus native checked lifecycle plus durable receipt", "exact lifecycle observation digest/file baselines, process epoch, operation UUID and per-instance operation receipt"),
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
            var store = new DocumentRevertReceiptStore(registry.StateDirectory);
            var admission = store.Reserve(instanceId, id, client.Epoch, fingerprint);
            if (!admission.Created)
            {
                if (admission.Receipt.Outcome == DocumentRevertReceiptOutcome.Completed)
                    return ReadCompletedReceipt(admission.Receipt, instanceId, id, client.Epoch, fingerprint) with { Replayed = true };

                // Only an Unknown receipt can be reconciled after attempted native dispatch. An
                // Admitted receipt may belong to a caller that crashed before dispatch; querying
                // native in that window could report operation_not_started and race the original
                // caller into a second dispatch. Keep the admission fail-closed until a future
                // dispatch-phase lease records that boundary explicitly.
                if (admission.Receipt.Outcome == DocumentRevertReceiptOutcome.Unknown)
                {
                    var reconciled = await ReconcileNativeReceipt(store, admission.Receipt, instanceId, id,
                        fingerprint, expected, client, cancellationToken);
                    if (reconciled is not null)
                        return reconciled;
                }

                // The original caller may have published a terminal result meanwhile. Re-read
                // before choosing the caller-facing state; the immutable reservation snapshot
                // above cannot reflect that transition.
                var currentAdmission = store.Read(instanceId, id) ?? admission.Receipt;
                if (currentAdmission.Outcome == DocumentRevertReceiptOutcome.Unknown)
                    throw new AutomationException("operation_outcome_unknown", currentAdmission.Message
                        ?? "The revert operation has an indeterminate native outcome; inspect kicad_document_state before another action.");
                if (currentAdmission.Outcome == DocumentRevertReceiptOutcome.Completed)
                    return ReadCompletedReceipt(currentAdmission, instanceId, id, client.Epoch, fingerprint) with { Replayed = true };
                throw new AutomationException("operation_in_progress",
                    $"Revert operation {id} is already admitted by another MCP process. Inspect kicad_document_revert_receipt or kicad_document_state before retrying; no second native dispatch was made.");
            }

            // The native legacy command cannot consume lifecycle state atomically. The durable
            // admission prevents a second MCP process from entering this boundary concurrently;
            // the read immediately before dispatch still rejects an intervening native change.
            DocumentLifecycleState before;
            try
            {
                before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                    new() { Document = expected.Document.Clone() }, cancellationToken);
                if (!before.Equals(expected))
                {
                    store.RemoveAdmission(instanceId, id, client.Epoch, fingerprint);
                    throw new AutomationException("stale_document_state", "The document changed before revert; obtain a fresh kicad_document_state observation and retry with a new operation UUID.");
                }
                ValidateRevertObservation(before, id, client.Epoch);
            }
            catch
            {
                // A failed pre-dispatch observation cannot have discarded in-memory changes.
                // Leave no stale admission that would unnecessarily block a safe retry.
                try { store.RemoveAdmission(instanceId, id, client.Epoch, fingerprint); }
                catch { /* preserve the original observation error; the durable guard remains fail-closed */ }
                throw;
            }

            LifecycleOperationResult nativeResult;
            try
            {
                nativeResult = await client.InvokeAsync<NativeCapabilityCheckedRevertDocument, LifecycleOperationResult>(new()
                {
                    Document = expected.Document.Clone(), OperationId = id, ExpectedState = expected.Clone()
                }, cancellationToken);
                ValidateResult(nativeResult, expected.Document, id, client.Epoch, LifecycleOperationStatus.LosReverted);
                if (nativeResult.Status == LifecycleOperationStatus.LosRejected)
                {
                    store.RemoveAdmission(instanceId, id, client.Epoch, fingerprint);
                    throw new AutomationException("native_revert_refused", nativeResult.ErrorMessage);
                }
                if (nativeResult.Status != LifecycleOperationStatus.LosReverted || !nativeResult.RevertCompleted)
                {
                    string message = $"KiCad admitted revert operation {id}, but its native receipt is {nativeResult.Status}: {nativeResult.ErrorMessage}. Inspect kicad_document_state before another action.";
                    RememberRevertUnknown(store, id, fingerprint, instanceId, client.Epoch, message);
                    throw new AutomationException("operation_outcome_unknown", message);
                }
            }
            catch (AutomationException)
            {
                // A terminal native refusal/indeterminate receipt was already retained above.
                // Do not try to replace it with a second terminal result while unwinding.
                throw;
            }
            catch (NngOperationCanceledException error) when (error.RequestDelivered)
            {
                string message = $"KiCad received checked revert operation {id} but cancellation stopped the reply ({error.Message}). Inspect kicad_document_state before another action; the native receipt may be terminal.";
                RememberRevertUnknown(store, id, fingerprint, instanceId, client.Epoch, message);
                throw new AutomationException("operation_outcome_unknown", message);
            }
            catch (NngException error) when (error.RequestDelivered)
            {
                string message = $"KiCad received checked revert operation {id} but did not answer ({error.Message}). Inspect kicad_document_state before another action; the native receipt may be terminal.";
                RememberRevertUnknown(store, id, fingerprint, instanceId, client.Epoch, message);
                throw new AutomationException("operation_outcome_unknown", message);
            }
            catch (NngException error)
            {
                store.RemoveAdmission(instanceId, id, client.Epoch, fingerprint);
                throw new AutomationException("native_not_reached",
                    $"KiCad did not receive the revert request for operation {id} ({error.Message}); no in-memory changes were intentionally discarded. Retry with a fresh observation and operation UUID.");
            }
            catch (NativeApiException)
            {
                // A typed native refusal is a terminal no-dispatch result; release the
                // admission so the caller can correct the request and retry safely.
                store.RemoveAdmission(instanceId, id, client.Epoch, fingerprint);
                throw;
            }
            catch (OperationCanceledException)
            {
                // Cancellation before NNG accepted the request has no delivered marker and is
                // safe to retry. Delivered cancellation is handled above by the typed exception.
                store.RemoveAdmission(instanceId, id, client.Epoch, fingerprint);
                throw;
            }
            catch (Exception error)
            {
                string message = $"The native checked revert operation {id} returned an unreadable result ({error.Message}). Inspect kicad_document_state before another action; the outcome is indeterminate.";
                RememberRevertUnknown(store, id, fingerprint, instanceId, client.Epoch, message);
                throw new AutomationException("operation_outcome_unknown", message);
            }

            DocumentLifecycleState after;
            try
            {
                after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                    new() { Document = expected.Document.Clone() }, cancellationToken);
            }
            catch (Exception error)
            {
                string message = $"KiCad accepted revert operation {id}, but the post-revert lifecycle state could not be read ({error.Message}). Inspect kicad_document_state before another action; the outcome is indeterminate.";
                RememberRevertUnknown(store, id, fingerprint, instanceId, client.Epoch, message);
                throw new AutomationException("revert_state_not_confirmed", message);
            }

            if (!RevertReceiptStatesMatch(expected, nativeResult, after))
            {
                string message = $"KiCad completed revert operation {id}, but its retained native state and independent post-revert observation do not match the expected clean checkpoint. Inspect kicad_document_state before another action.";
                RememberRevertUnknown(store, id, fingerprint, instanceId, client.Epoch, message);
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
                NativeReceiptAvailable = true,
                DiscardedUnsavedChanges = true,
                BeforeState = before,
                AfterState = after,
                PostStateVerified = true,
                Verification = "native checked lifecycle receipt is terminal, editor state is clean and every observed file baseline is unchanged",
                TransportRetrySafe = false
            };
            RememberRevertCompleted(store, id, fingerprint, result);
            return result;
        });

    [McpServerTool(Name = "kicad_document_revert_receipt", ReadOnly = true),
     Description("Read the durable per-instance MCP receipt for kicad_document_revert by its exact operation UUID and process epoch. This never reverts or retries a document. An admitted receipt means another MCP process owns the dispatch boundary; a completed receipt is paired with the native process receipt; an unknown receipt requires a fresh kicad_document_state observation because reload failure cannot be rolled back automatically."),
     KiCadCapability("document-lifecycle", "compiled-mcp plus durable state receipt", "operation UUID, process epoch and state-directory receipt"),
     KiCadVerification(KiCadVerificationLevel.InProcess, "DocumentLifecycleToolTests.RevertRequiresCompleteObservationAndVerifiesCleanState")]
    public Task<CallToolResult> RevertReceipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken) => InstanceToolBoundary.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEpoch(expectedProcessEpoch);
            string id = OperationId(operationId);
            var store = new DocumentRevertReceiptStore(registry.StateDirectory);
            var receipt = store.Read(instanceId, id);
            if (receipt is null)
                return Task.FromResult<object>(new
                {
                    instanceId, processEpoch = expectedProcessEpoch, operationId = id,
                    found = false, nativeReceiptAvailable = false, retryDispatched = false
                });
            if (receipt.ProcessEpoch != expectedProcessEpoch)
                throw new AutomationException("operation_identity_mismatch", "The durable revert receipt belongs to another native process epoch; inspect the current instance before retrying.");
            if (receipt.Outcome == DocumentRevertReceiptOutcome.Unknown)
                return Task.FromResult<object>(new
                {
                    instanceId, processEpoch = expectedProcessEpoch, operationId = id,
                    found = true, outcome = "indeterminate", nativeReceiptAvailable = false,
                    retryDispatched = false, errorCode = "operation_outcome_unknown", message = receipt.Message
                });
            if (receipt.Outcome == DocumentRevertReceiptOutcome.Admitted)
                return Task.FromResult<object>(new
                {
                    instanceId, processEpoch = expectedProcessEpoch, operationId = id,
                    found = true, outcome = "admitted", nativeReceiptAvailable = false,
                    retryDispatched = false, message = "The operation is durably admitted and cannot be dispatched a second time."
                });
            var result = ReadCompletedReceipt(receipt, instanceId, id, expectedProcessEpoch, receipt.Fingerprint);
            return Task.FromResult<object>(new
            {
                instanceId, processEpoch = expectedProcessEpoch, operationId = id,
                found = true, outcome = "completed", nativeReceiptAvailable = true,
                retryDispatched = false, receipt = result
            });
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
        if (!state.ProjectSettingsIncluded || state.NativeFiles.Count == 0
            || !DocumentRevertReceiptRecord.Sha256(state.CleanCheckpointSha256)
            || state.FileBaselines.Count != state.NativeFiles.Count
            || state.FileBaselines.Any(file => file is null || file.Status != NativeFileBaselineStatus.NfbsUnchanged
                || !file.BaselineKnown || !file.CurrentKnown || !file.BaselineExists.Equals(file.CurrentExists)
                || file.BaselineSha256 != file.CurrentSha256 || file.BaselineBytes != file.CurrentBytes))
            throw new AutomationException("incomplete_revert_observation", "Revert requires complete unchanged native file baselines so unsaved changes can be identified before they are discarded.");
    }

    private static bool RevertStateIsClean(DocumentLifecycleState state) =>
        state.NativeContentDirty == false && state.ProjectSettingsIncluded
        && state.NativeFiles.Count > 0 && state.FileBaselines.Count == state.NativeFiles.Count
        && state.FileBaselines.All(file => file is not null && file.Status == NativeFileBaselineStatus.NfbsUnchanged
            && file.BaselineKnown && file.CurrentKnown && file.BaselineExists.Equals(file.CurrentExists)
            && file.BaselineSha256 == file.CurrentSha256 && file.BaselineBytes == file.CurrentBytes);

    private static bool RevertReceiptStatesMatch(DocumentLifecycleState expected,
        LifecycleOperationResult native, DocumentLifecycleState after)
    {
        if (!native.ExpectedStateVerified || native.ObservedState is not { } observed
            || !DocumentRevertReceiptRecord.Sha256(expected.CleanCheckpointSha256)
            || !observed.Document.Equals(expected.Document) || observed.ProcessEpoch != expected.ProcessEpoch
            || observed.Scope != expected.Scope || observed.StateSha256 != expected.CleanCheckpointSha256
            || observed.CleanCheckpointSha256 != expected.CleanCheckpointSha256
            || !RevertIdentityMatches(expected, observed)
            || !observed.NativeFiles.SequenceEqual(expected.NativeFiles)
            || observed.FileBaselines.Count != expected.FileBaselines.Count
            || !observed.FileBaselines.Zip(expected.FileBaselines).All(pair => pair.First.Equals(pair.Second))
            || !RevertStateIsClean(observed) || !RevertStateEquivalent(observed, after)
            || !RevertStateIsClean(after))
            return false;

        return true;
    }

    private static bool RevertStateEquivalent(DocumentLifecycleState first, DocumentLifecycleState second) =>
        first.Document.Equals(second.Document) && first.ProcessEpoch == second.ProcessEpoch
        && first.Scope == second.Scope && first.StateSha256 == second.StateSha256
        && first.CleanCheckpointSha256 == second.CleanCheckpointSha256
        && first.NativeIdentity == second.NativeIdentity
        && first.Revision?.Epoch == second.Revision?.Epoch
        && first.NativeContentDirty == second.NativeContentDirty
        && first.ProjectSettingsIncluded == second.ProjectSettingsIncluded
        && first.CompleteChangeTracking == second.CompleteChangeTracking
        && first.DiskBaselineChecked == second.DiskBaselineChecked
        && first.NativeFiles.SequenceEqual(second.NativeFiles)
        && first.FileBaselines.Count == second.FileBaselines.Count
        && first.FileBaselines.Zip(second.FileBaselines).All(pair => pair.First.Equals(pair.Second));

    private static bool RevertIdentityMatches(DocumentLifecycleState expected, DocumentLifecycleState state)
    {
        if (state.Revision is null || !Guid.TryParseExact(state.Revision.Epoch, "D", out var revisionEpoch)
            || revisionEpoch == Guid.Empty)
            return false;

        if (expected.Scope == DocumentLifecycleScope.DlsPcb)
            return Guid.TryParseExact(state.NativeIdentity, "D", out var identity) && identity != Guid.Empty
                && state.Revision.Epoch == state.NativeIdentity;

        return expected.Scope == DocumentLifecycleScope.DlsSchematicHierarchy
            && state.NativeIdentity == expected.NativeIdentity
            && expected.Revision is not null && state.Revision.Epoch == expected.Revision.Epoch;
    }

    private static async Task<DocumentRevertResult?> ReconcileNativeReceipt(
        DocumentRevertReceiptStore store, DocumentRevertReceiptRecord persisted, string instanceId,
        string operationId, string fingerprint, DocumentLifecycleState expected, NativeClient client,
        CancellationToken cancellationToken)
    {
        if (persisted.Outcome != DocumentRevertReceiptOutcome.Unknown)
            return null;

        LifecycleOperationResult native;
        try
        {
            native = await client.InvokeAsync<ReadLifecycleOperation, LifecycleOperationResult>(new()
            {
                Document = expected.Document.Clone(), OperationId = operationId, ProcessEpoch = client.Epoch,
                ExpectedState = expected.Clone()
            }, cancellationToken);
        }
        catch (NativeApiException error) when (error.Status == 3
            && error.Message.StartsWith(NativeUnknownOperation + ":", StringComparison.Ordinal))
        {
            // Native explicitly says this operation never entered its receipt table. The durable
            // record is already Unknown, so this does not establish permission to redispatch.
            return null;
        }
        catch (NativeApiException error) when (error.Status == 3)
        {
            throw new AutomationException("revert_receipt_conflict",
                $"The native receipt for revert operation {operationId} does not match its original request ({error.Message}).");
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (NngException)
        {
            return null;
        }

        try
        {
            ValidateResult(native, expected.Document, operationId, client.Epoch);
        }
        catch (AutomationException error)
        {
            throw new AutomationException("revert_receipt_conflict",
                $"The native receipt for revert operation {operationId} is not an exact lifecycle result ({error.Message}).");
        }

        if (native.Status is LifecycleOperationStatus.LosSaved or LifecycleOperationStatus.LosClosed)
            throw new AutomationException("revert_receipt_conflict",
                $"The native receipt for revert operation {operationId} belongs to a different lifecycle operation ({native.Status}).");

        if (native.Status != LifecycleOperationStatus.LosReverted || !native.RevertCompleted)
        {
            // LOS_INDETERMINATE/operation_in_progress means another caller is still inside the
            // controller. A terminal refusal or failure remains unknown and never authorizes a
            // redispatch.
            return null;
        }

        DocumentLifecycleState after;
        try
        {
            after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new()
            {
                Document = expected.Document.Clone()
            }, cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }

        if (!RevertReceiptStatesMatch(expected, native, after))
        {
            return null;
        }

        var result = new DocumentRevertResult
        {
            InstanceId = instanceId,
            ProcessEpoch = client.Epoch,
            OperationId = operationId,
            Document = expected.Document.Clone(),
            OperationOutcome = "completed",
            Committed = true,
            Replayed = true,
            NativeReceiptAvailable = true,
            DiscardedUnsavedChanges = true,
            BeforeState = expected.Clone(),
            AfterState = after,
            PostStateVerified = true,
            Verification = "native terminal revert receipt reconciled after MCP transport loss; editor state matches the clean checkpoint",
            TransportRetrySafe = false
        };

        try
        {
            RememberRevertCompleted(store, operationId, fingerprint, result);
        }
        catch (AutomationException error) when (error.Code == "revert_receipt_conflict")
        {
            // A concurrent original caller may have written the completed receipt between the
            // read and this promotion. Re-read that terminal record instead of surfacing a
            // false conflict or dispatching again.
            var current = store.Read(instanceId, operationId);
            if (current?.Outcome == DocumentRevertReceiptOutcome.Completed)
                return ReadCompletedReceipt(current, instanceId, operationId, client.Epoch, fingerprint) with { Replayed = true };
            throw;
        }

        return result;
    }

    private static string RevertFingerprint(string instanceId, DocumentLifecycleState state, string operationId) =>
        DocumentRevertReceiptStore.Fingerprint(instanceId, operationId, SchematicJson.Formatter.Format(state));

    private static readonly JsonSerializerOptions RevertResultJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    private static void RememberRevertUnknown(DocumentRevertReceiptStore store, string operationId,
        string fingerprint, string instanceId, string processEpoch, string message) =>
        store.WriteTerminal(new DocumentRevertReceiptRecord(DocumentRevertReceiptRecord.CurrentVersion,
            instanceId, operationId, processEpoch, fingerprint, DocumentRevertReceiptOutcome.Unknown, null, message));

    private static void RememberRevertCompleted(DocumentRevertReceiptStore store, string operationId,
        string fingerprint, DocumentRevertResult result)
    {
        var persisted = new PersistedRevertResult(result.InstanceId, result.ProcessEpoch, result.OperationId,
            SchematicJson.Formatter.Format(result.Document), result.OperationOutcome, result.Committed,
            result.NativeReceiptAvailable, result.DiscardedUnsavedChanges,
            Convert.ToBase64String(result.BeforeState?.ToByteArray() ?? []),
            Convert.ToBase64String(result.AfterState?.ToByteArray() ?? []), result.PostStateVerified,
            result.Verification, result.TransportRetrySafe);
        JsonElement payload = JsonSerializer.SerializeToElement(persisted, RevertResultJson);
        store.WriteTerminal(new DocumentRevertReceiptRecord(DocumentRevertReceiptRecord.CurrentVersion,
            result.InstanceId, operationId, result.ProcessEpoch, fingerprint,
            DocumentRevertReceiptOutcome.Completed, payload, null));
    }

    private static DocumentRevertResult ReadCompletedReceipt(DocumentRevertReceiptRecord receipt,
        string instanceId, string operationId, string processEpoch, string fingerprint)
    {
        if (receipt.Outcome != DocumentRevertReceiptOutcome.Completed || receipt.Result is null
            || receipt.InstanceId != instanceId || receipt.OperationId != operationId
            || receipt.ProcessEpoch != processEpoch || receipt.Fingerprint != fingerprint)
            throw new AutomationException("revert_receipt_corrupt", "The completed document-revert receipt identity does not match its request.");

        try
        {
            var persisted = receipt.Result.Value.Deserialize<PersistedRevertResult>(RevertResultJson)
                ?? throw new InvalidDataException("The completed document-revert result is empty.");
            var result = new DocumentRevertResult
            {
                InstanceId = persisted.InstanceId,
                ProcessEpoch = persisted.ProcessEpoch,
                OperationId = persisted.OperationId,
                Document = SchematicJson.Parser.Parse<DocumentSpecifier>(persisted.DocumentJson),
                OperationOutcome = persisted.OperationOutcome,
                Committed = persisted.Committed,
                Replayed = false,
                NativeReceiptAvailable = persisted.NativeReceiptAvailable,
                DiscardedUnsavedChanges = persisted.DiscardedUnsavedChanges,
                BeforeState = ParseState(persisted.BeforeStateBase64),
                AfterState = ParseState(persisted.AfterStateBase64),
                PostStateVerified = persisted.PostStateVerified,
                Verification = persisted.Verification,
                TransportRetrySafe = persisted.TransportRetrySafe
            };
            if (result.InstanceId != instanceId || result.ProcessEpoch != processEpoch
                || result.OperationId != operationId || !result.Committed
                || result.OperationOutcome != "completed" || !result.NativeReceiptAvailable
                || !result.DiscardedUnsavedChanges || !result.PostStateVerified
                || result.BeforeState is null || result.AfterState is null
                || !result.BeforeState.Document.Equals(result.Document)
                || !result.AfterState.Document.Equals(result.Document)
                || result.BeforeState.ProcessEpoch != processEpoch
                || result.AfterState.ProcessEpoch != processEpoch
                || !RevertStateIsClean(result.AfterState)
                || !DocumentRevertReceiptRecord.Sha256(result.BeforeState.CleanCheckpointSha256)
                || result.AfterState.StateSha256 != result.BeforeState.CleanCheckpointSha256
                || result.AfterState.CleanCheckpointSha256 != result.BeforeState.CleanCheckpointSha256
                || !RevertIdentityMatches(result.BeforeState, result.AfterState)
                || !result.AfterState.NativeFiles.SequenceEqual(result.BeforeState.NativeFiles)
                || result.AfterState.FileBaselines.Count != result.BeforeState.FileBaselines.Count
                || !result.AfterState.FileBaselines.Zip(result.BeforeState.FileBaselines)
                    .All(pair => pair.First.Equals(pair.Second))
                || RevertFingerprint(instanceId, result.BeforeState, operationId) != fingerprint)
                throw new InvalidDataException("The completed document-revert result does not cross-check against its receipt identity and state.");
            DocumentStateTools.ValidateTarget(result.Document);
            return result;
        }
        catch (Exception error)
        {
            throw new AutomationException("revert_receipt_corrupt", "The persisted document-revert result is malformed or has inconsistent identity: " + error.Message);
        }
    }

    private static DocumentLifecycleState? ParseState(string value) => string.IsNullOrEmpty(value)
        ? null : DocumentLifecycleState.Parser.ParseFrom(Convert.FromBase64String(value));

    private sealed record PersistedRevertResult(string InstanceId, string ProcessEpoch, string OperationId,
        string DocumentJson, string OperationOutcome, bool Committed, bool NativeReceiptAvailable,
        bool DiscardedUnsavedChanges, string BeforeStateBase64, string AfterStateBase64, bool PostStateVerified,
        string Verification, bool TransportRetrySafe);

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
                or LifecycleOperationStatus.LosFailed or LifecycleOperationStatus.LosIndeterminate
                or LifecycleOperationStatus.LosClosed or LifecycleOperationStatus.LosReverted)
            || (expectedSuccess is not null && result.Status is (LifecycleOperationStatus.LosSaved
                or LifecycleOperationStatus.LosClosed or LifecycleOperationStatus.LosReverted)
                && result.Status != expectedSuccess))
            throw new AutomationException("invalid_lifecycle_result", "Native operation result did not match the requested target and identity.");
    }
}
