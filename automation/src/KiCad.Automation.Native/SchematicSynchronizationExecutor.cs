using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using NativeRevision = KiCad.Automation.Model.DocumentRevision;

namespace KiCad.Automation.Native;

public sealed record SchematicSynchronizationExecution(string RecoveryRevisionToken, string DesignFileSha256,
    NativeRevision NativeRevision, bool NativeMutationCommitted, bool NativeFilesSaved,
    bool SynchronizationCommitted, CheckedSchematicBatchReceipt? NativeReceipt,
    Guid? PublicationId = null, string? PreviousXmlPath = null, bool Replayed = false, RetainedXmlLocation? RetainedXml = null);

/// <summary>Journaled execution of a supported design candidate. Still internal
/// until the complete executor and retained-file lifecycle are qualified.</summary>
internal static class SchematicSynchronizationExecutor
{
    public static async Task<SchematicSynchronizationExecution> ApplyAsync(DesignRecoveryStore store,
        NativeClient client, string designPath, string expectedRevisionToken, Guid operationId,
        CancellationToken cancellationToken = default, Func<string, CancellationToken, Task>? executionCheckpoint = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operationId == Guid.Empty) throw Error("invalid_operation_id", "A caller-stable synchronization operation ID is required.");
        if (!Path.IsPathFullyQualified(designPath)) throw Error("invalid_design_path", "An absolute design XML path is required.");
        designPath = Path.GetFullPath(designPath);
        var saved = store.Read() ?? throw Error("missing_design_recovery", "No design recovery record exists.");
        var receipts = new DesignSynchronizationReceipts(store.StatePath);
        if (Completed(saved, receipts, operationId) is { } historical)
        {
            historical.RequireRequest(saved.State.InstanceId, designPath, expectedRevisionToken);
            return historical.Result(saved.RevisionToken, replayed: true);
        }
        RequireRequest(saved, operationId, designPath, expectedRevisionToken);
        var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != saved.State.InstanceId.ToString("D"))
            throw Error("recovery_instance_mismatch", "The native connection belongs to another saved instance.");
        using var ownership = receipts.Acquire();
        saved = store.Read() ?? throw Error("missing_design_recovery", "No design recovery record exists.");
        // The latest result and baseline were committed together. Never replace
        // that only receipt until its immutable archive is safely available.
        if (saved.State.LastSynchronization is { } previous)
        {
            receipts.Archive(previous);
            var retained = RetainedXmlHistory.Archive(previous);
            if (retained.ErrorCode is not null)
                throw Error("sync_history_requires_attention", retained.ErrorMessage ?? "Inspect the retained XML history before starting another operation.");
        }
        if (Completed(saved, receipts, operationId) is { } completed)
        {
            completed.RequireRequest(saved.State.InstanceId, designPath, expectedRevisionToken);
            return completed.Result(saved.RevisionToken, replayed: true);
        }
        RequireRequest(saved, operationId, designPath, expectedRevisionToken);
        if (saved.State.HasPendingWork)
        {
            if (saved.State.PendingLayout is not null)
                saved = await ResolveLayoutAsync(store, client, saved, cancellationToken, executionCheckpoint);
            if (saved.State.PendingPublication is null)
                throw Error("pending_recovery_requires_reconciliation", "Legacy pending operations have no recorded publication destination; inspect and reconcile them first.");
            if (saved.State.PendingPublication.DesignPath != designPath)
                throw Error("publication_target_mismatch", "Resume only the exact recorded XML destination.");
            return await ResumeAsync(store, receipts, client, saved, cancellationToken, executionCheckpoint);
        }

        byte[] original = await File.ReadAllBytesAsync(designPath, cancellationToken);
        if (!original.AsSpan().SequenceEqual(saved.State.DesiredFileBytes))
            throw Error("design_file_changed", "Capture the latest saved XML before planning synchronization.");
        var plan = await SchematicSynchronizationPlanner.PlanForExecutionWithHistoryAsync(store, saved, cancellationToken);
        if (!plan.CanPrepare || plan.Candidate is null || plan.CandidateXml is null)
            throw Error(plan.ErrorCode ?? "design_sync_conflict", plan.ErrorMessage ?? "Resolve the design conflicts before applying changes.");
        var checkpoint = await Capture(client, saved.State, cancellationToken);
        if (checkpoint.State.Revision.Epoch != saved.State.NativeRevision.Epoch
            || checkpoint.State.Revision.Sequence != saved.State.NativeRevision.Sequence
            || !checkpoint.Electrical.Hierarchy.Data.Equals(saved.State.Observed)
            || !checkpoint.Electrical.Equals(saved.State.ObservedElectrical))
            throw Error("native_checkpoint_stale", "Refresh the native observation before applying this plan.");
        if (!CheckedSchematicContract.FileCoverage(checkpoint.State))
            throw Error("native_file_conflict", "Native file baselines must be known and unchanged before synchronization.");
        var desired = DesignRecoveryStore.ReadDesired(saved.State);
        bool unchangedXml = Equivalent(desired, plan.Candidate, saved.State, cancellationToken);
        byte[] candidateBytes = unchangedXml ? original : Encoding.UTF8.GetBytes(plan.CandidateXml);
        if (unchangedXml && plan.NativeOperations.Count == 0 && !checkpoint.State.NativeContentDirty
            && checkpoint.State.CleanCheckpointSha256 == checkpoint.State.StateSha256
            && Equivalent(saved.State.Baseline, plan.Candidate, saved.State, cancellationToken))
        {
            Read(store, saved.RevisionToken);
            var noOp = new DesignSynchronizationReceipt(2, operationId, saved.State.InstanceId, designPath,
                expectedRevisionToken, Hash(original), client.Epoch, saved.State.NativeRevision.Epoch,
                saved.State.NativeRevision.Sequence, false, false, null, null);
            receipts.Archive(noOp);
            return noOp.Result(saved.RevisionToken, replayed: false);
        }

        ApplySchematicItemBatch? batch = null;
        if (plan.NativeOperations.Count != 0)
        {
            batch = new() { Document = checkpoint.State.Document.Clone(), DocumentEpoch = checkpoint.State.Revision.Epoch,
                ExpectedRevision = checkpoint.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"),
                OriginId = saved.State.OriginId.ToString("D"), Description = "Apply XML synchronization candidate" };
            batch.Operations.Add(plan.NativeOperations.Select(operation => operation.Clone()));
        }
        if (plan.NativeLayoutResolutionRequired)
        {
            saved = store.Save(saved.State with { PendingMutation = batch, PendingNativeState = checkpoint.State.Clone(),
                PendingLayout = DesignLayoutIntent.Create(designPath, original, candidateBytes, operationId, expectedRevisionToken) }, saved.RevisionToken);
            if (executionCheckpoint is not null) await executionCheckpoint("layout-prepared", cancellationToken);
            saved = await ResolveLayoutAsync(store, client, saved, cancellationToken, executionCheckpoint);
            return await ResumeAsync(store, receipts, client, saved, cancellationToken, executionCheckpoint);
        }
        // This includes native-only changes and engineering-only XML changes.
        // An interrupted publication must have a journal even without a batch.
        saved = store.Save(saved.State with { PendingMutation = batch, PendingNativeState = checkpoint.State.Clone(),
            PendingNativeSave = null, PendingCandidateFileBytes = null,
            PendingPublication = DesignPublicationIntent.Create(designPath, original, candidateBytes, operationId, expectedRevisionToken) }, saved.RevisionToken);
        return await ResumeAsync(store, receipts, client, saved, cancellationToken, executionCheckpoint);
    }

    private static async Task<SchematicSynchronizationExecution> ResumeAsync(DesignRecoveryStore store, DesignSynchronizationReceipts receipts,
        NativeClient client, StoredDesignRecovery saved, CancellationToken token, Func<string, CancellationToken, Task>? checkpoint)
    {
        var intent = saved.State.PendingPublication!;
        var initialState = saved.State.PendingNativeState ?? throw Error("missing_native_precondition", "The publication lacks its original native checkpoint.");
        if (initialState.ProcessEpoch != client.Epoch)
            throw Error("instance_changed", "The pending synchronization belongs to another native process.");
        if (!saved.State.DesiredFileBytes.AsSpan().SequenceEqual(intent.ExpectedFileBytes)
            && !saved.State.DesiredFileBytes.AsSpan().SequenceEqual(intent.CandidateFileBytes))
            throw Error("publication_desired_changed", "Reconcile the newer XML with the pending candidate before continuing.");
        // File intake may not yet have observed a just-saved XML change. Check
        // the actual file BEFORE replaying any native edit or save request.
        try
        {
            byte[] liveXml = await File.ReadAllBytesAsync(intent.DesignPath, token);
            if (!liveXml.AsSpan().SequenceEqual(intent.ExpectedFileBytes) && !liveXml.AsSpan().SequenceEqual(intent.CandidateFileBytes))
                throw Error("publication_target_changed", "The live XML changed; no pending native action was replayed.");
        }
        catch (FileNotFoundException) when (OperatingSystem.IsWindows() && intent.Phase == DesignPublicationPhase.Attempting
            && File.Exists(intent.PreviousPath) && File.Exists(intent.StagedPath))
        {
            // A recorded ReplaceFileW partial move is reconciled by the file
            // committer; it will never overwrite a reappearing destination.
        }
        var candidate = SchematicDesignXml.Read(new UTF8Encoding(false, true).GetString(intent.CandidateFileBytes), saved.State.KnowledgeLibraries);
        CheckedSchematicBatchReceipt? receipt = null;

        if (saved.State.PendingMutation is { } batch)
        {
            Read(store, saved.RevisionToken);
            var request = new CheckedSchematicBatch { Batch = batch.Clone(), ExpectedState = initialState.Clone() };
            // The native controller either executes the not-yet-seen request
            // under its exact guard or returns its original retained receipt.
            // It never executes an indeterminate/completed operation again.
            receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
            CheckedSchematicContract.ValidateResult(request, receipt, inspect: false);
            if (receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted)
                throw Error("native_sync_not_committed", receipt.ErrorMessage.Length == 0
                    ? "Inspect the retained native operation before continuing synchronization." : receipt.ErrorMessage);
        }

        if (saved.State.PendingNativeSave is null)
        {
            var observed = await Capture(client, saved.State, token);
            if (!observed.State.Equals(receipt?.ObservedAfter ?? initialState))
                throw Error("native_changed_during_sync", "Native state changed after this operation; reconcile it before saving.");
            RequireCandidate(candidate, observed.Electrical, saved.State, token);
            saved = store.Save(saved.State with { PendingNativeSave = new()
            {
                Document = observed.State.Document.Clone(), ExpectedState = observed.State.Clone(),
                OperationId = Guid.NewGuid().ToString("D")
            } }, saved.RevisionToken);
        }

        var saveRequest = saved.State.PendingNativeSave!;
        Read(store, saved.RevisionToken);
        // Replay the exact request, not an unverified operation-id lookup. This
        // safely handles a lost first save reply or a save not yet dispatched.
        var save = await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(saveRequest.Clone(), token);
        if (save.Status != LifecycleOperationStatus.LosSaved || save.OperationId != saveRequest.OperationId
            || save.ProcessEpoch != client.Epoch || !Equals(save.Document, saveRequest.Document) || save.ObservedState is null)
            throw Error("native_sync_save_failed", "The exact native save was not confirmed; retain the pending request for inspection.");
        var afterSave = await Capture(client, saved.State, token);
        if (!afterSave.State.Equals(save.ObservedState))
            throw Error("native_changed_during_sync", "The native document changed after saving; preserve the pending candidate for reconciliation.");
        RequireCandidate(candidate, afterSave.Electrical, saved.State, token);
        var publication = await DesignPublicationCommitter.CommitAsync(store, saved.RevisionToken, save, token, checkpoint: checkpoint);
        saved = publication.Recovery;

        var final = await Capture(client, saved.State, token);
        if (!final.State.Equals(afterSave.State))
            throw Error("native_changed_during_sync", "Native state changed during XML publication; do not advance the baseline.");
        if (!(await File.ReadAllBytesAsync(intent.DesignPath, token)).AsSpan().SequenceEqual(intent.CandidateFileBytes))
            throw Error("publication_target_changed", "XML changed after publication; retain the pending versions for reconciliation.");
        // Preserve native enumeration in its own electrical checkpoint. XML
        // enumeration is not an object-property change and need not be rewritten.
        var baseline = candidate with { Schematic = final.Electrical.Hierarchy.Data.Clone() };
        var resultReceipt = new DesignSynchronizationReceipt(2, intent.OperationId, saved.State.InstanceId,
            intent.DesignPath, intent.RequestedRecoveryRevisionToken!, publication.FileSha256, client.Epoch,
            final.State.Revision.Epoch, final.State.Revision.Sequence, receipt is not null, true,
            receipt?.ToByteArray(), publication.PreviousPath,
            publication.PreviousPath is null ? null : Hash(intent.ExpectedFileBytes));
        var complete = store.Save(saved.State with
        {
            Baseline = baseline, DesiredFileBytes = intent.CandidateFileBytes,
            Observed = final.Electrical.Hierarchy.Data.Clone(), NativeRevision = new(final.State.Revision.Epoch, final.State.Revision.Sequence),
            TrackingComplete = final.Electrical.Hierarchy.TrackingComplete, BaselineElectrical = final.Electrical.Clone(),
            ObservedElectrical = final.Electrical.Clone(), PendingMutation = null, PendingNativeState = null,
            PendingNativeSave = null, PendingCandidateFileBytes = null, PendingPublication = null, HierarchyResolution = null,
            LastSynchronization = resultReceipt, OwnershipResolution = null
        }, saved.RevisionToken);
        if (checkpoint is not null) await checkpoint("baseline-committed", CancellationToken.None);
        // A failure here leaves the complete result in the atomic recovery
        // record. Retry returns it and the next operation must archive it first.
        receipts.Archive(resultReceipt);
        if (checkpoint is not null) await checkpoint("receipt-archived", CancellationToken.None);
        var history = RetainedXmlHistory.Archive(resultReceipt);
        if (checkpoint is not null && history.Status == "archived") await checkpoint("retained-archived", CancellationToken.None);
        return resultReceipt.Result(complete.RevisionToken, replayed: false) with { RetainedXml = history };
    }

    private static DesignSynchronizationReceipt? Completed(StoredDesignRecovery saved,
        DesignSynchronizationReceipts receipts, Guid operationId) => saved.State.LastSynchronization?.OperationId == operationId
            ? saved.State.LastSynchronization : receipts.Read(operationId);

    private static void RequireRequest(StoredDesignRecovery saved, Guid operationId, string path, string token)
    {
        if (saved.State.PendingLayout is { } layout)
        {
            if (layout.DesignPath != path) throw Error("publication_target_mismatch", "Resume only the exact recorded XML destination.");
            if (layout.OperationId != operationId || layout.RequestedRecoveryRevisionToken != token)
                throw Error("sync_operation_id_conflict", "The pending layout belongs to another exact request; inspect it before continuing.");
        }
        else if (saved.State.PendingPublication is { } pending)
        {
            if (pending.DesignPath != path) throw Error("publication_target_mismatch", "Resume only the exact recorded XML destination.");
            if (pending.OperationId != operationId || pending.RequestedRecoveryRevisionToken != token)
                throw Error("sync_operation_id_conflict", "The pending publication belongs to another exact request; inspect it before continuing.");
        }
        else if (saved.RevisionToken != token)
            throw Error("design_recovery_changed", "Recovery changed; reload the current record.");
    }

    private static async Task<StoredDesignRecovery> ResolveLayoutAsync(DesignRecoveryStore store, NativeClient client,
        StoredDesignRecovery saved, CancellationToken token, Func<string, CancellationToken, Task>? checkpoint)
    {
        var intent = saved.State.PendingLayout!;
        var initial = saved.State.PendingNativeState!;
        if (initial.ProcessEpoch != client.Epoch)
            throw Error("instance_changed", "The pending connected move belongs to another native process.");
        if (!saved.State.DesiredFileBytes.AsSpan().SequenceEqual(intent.ExpectedFileBytes)
            || !(await File.ReadAllBytesAsync(intent.DesignPath, token)).AsSpan().SequenceEqual(intent.ExpectedFileBytes))
            throw Error("publication_target_changed", "XML changed before the pending move was resolved; no native request was replayed.");
        Read(store, saved.RevisionToken);
        var request = new CheckedSchematicBatch { Batch = saved.State.PendingMutation!.Clone(), ExpectedState = initial.Clone() };
        var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
        CheckedSchematicContract.ValidateResult(request, receipt, inspect: false);
        if (receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted)
            throw Error("native_sync_not_committed", receipt.ErrorMessage.Length == 0
                ? "Inspect the retained connected-move operation before continuing." : receipt.ErrorMessage);
        var observed = await Capture(client, saved.State, token);
        if (!observed.State.Equals(receipt.ObservedAfter))
            throw Error("native_changed_during_sync", "Native state changed after the move; preserve the pending versions for reconciliation.");
        var planned = SchematicDesignXml.Read(new UTF8Encoding(false, true).GetString(intent.PlannedDesignFileBytes), saved.State.KnowledgeLibraries);
        var resolved = SchematicLayoutResolution.Resolve(planned, observed.Electrical, request.Batch, saved.State.KnowledgeLibraries, token);
        byte[] candidate = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(resolved, saved.State.KnowledgeLibraries));
        if (!(await File.ReadAllBytesAsync(intent.DesignPath, token)).AsSpan().SequenceEqual(intent.ExpectedFileBytes))
            throw Error("publication_target_changed", "XML changed while resolving the move; retain the pending native result.");
        saved = store.Save(saved.State with { PendingLayout = null, PendingPublication = DesignPublicationIntent.Create(
            intent.DesignPath, intent.ExpectedFileBytes, candidate, intent.OperationId, intent.RequestedRecoveryRevisionToken) }, saved.RevisionToken);
        if (checkpoint is not null) await checkpoint("layout-resolved", token);
        return saved;
    }

    private static async Task<CheckedSchematicState> Capture(NativeClient client, DesignRecoveryState state, CancellationToken token)
    {
        var root = state.Baseline.Schematic.Document;
        var result = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        CheckedSchematicContract.ValidateObservation(result, root, client.Epoch);
        return result;
    }

    private static void RequireCandidate(SchematicDesign candidate, SchematicElectricalState native,
        DesignRecoveryState state, CancellationToken token)
    {
        if (SchematicHierarchyDelta.Plan(native.Hierarchy.Data, candidate.Schematic, token).Count != 0)
            throw Error("native_sync_projection_mismatch", "The native objects do not match the candidate; no XML is published.");
        var comparison = SchematicElectricalComparison.Compare(candidate, native, state.KnowledgeLibraries, token);
        if (!comparison.PinBindingsComplete || !comparison.ConnectivityEquivalent)
            throw Error("native_sync_connectivity_mismatch", "The native pin connections do not match the candidate; no XML is published.");
    }

    internal static bool Equivalent(SchematicDesign a, SchematicDesign b, DesignRecoveryState state, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Reloading a file saved by the pinned writer changes loaded-format
        // provenance. It needs XML publication, not a native metadata mutation.
        // The planner has already validated both formats. Do not swallow other
        // unsupported deltas or compare only the engineering half of a design.
        if (a.Schematic.Instances.Any(before => b.Schematic.Instances.FirstOrDefault(after =>
                Equals(before.Metadata.Document, after.Metadata.Document)) is { } after
            && before.Metadata.LoadedNativeFormatVersion != after.Metadata.LoadedNativeFormatVersion))
            return false;
        return SchematicHierarchyDelta.Plan(a.Schematic, b.Schematic, token).Count == 0
            && SchematicDesignXml.Write(a with { Schematic = b.Schematic }, state.KnowledgeLibraries)
                == SchematicDesignXml.Write(b, state.KnowledgeLibraries);
    }

    private static StoredDesignRecovery Read(DesignRecoveryStore store, string expected)
    {
        var saved = store.Read() ?? throw Error("missing_design_recovery", "No design recovery record exists.");
        return saved.RevisionToken == expected ? saved : throw Error("design_recovery_changed", "Recovery changed; reload the current record.");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static AutomationException Error(string code, string message) => new(code, message);
}
