using System.Text;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using NativeRevision = KiCad.Automation.Model.DocumentRevision;

namespace KiCad.Automation.Native;

public sealed record SchematicSynchronizationExecution(string RecoveryRevisionToken, string DesignFileSha256,
    NativeRevision NativeRevision, bool NativeMutationCommitted, bool NativeFilesSaved,
    bool SynchronizationCommitted, CheckedSchematicBatchReceipt? NativeReceipt);

/// <summary>Executes one complete, recoverable XML-to-native synchronization.
/// Planning remains pure; this type owns the guarded commit and its recovery journal.</summary>
public static class SchematicSynchronizationExecutor
{
    public static async Task<SchematicSynchronizationExecution> ApplyAsync(DesignRecoveryStore store,
        NativeClient client, string designPath, string expectedRevisionToken,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(designPath))
            throw new AutomationException("invalid_design_path", "The design XML path must be absolute.");
        designPath = Path.GetFullPath(designPath);
        cancellationToken.ThrowIfCancellationRequested();
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "No saved design recovery state exists.");
        if (saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery changed; inspect it before applying synchronization.");
        if (saved.State.PendingMutation is not null)
        {
            if (saved.State.PendingCandidateFileBytes is null)
                throw new AutomationException("pending_recovery_requires_reconciliation", "Inspect the saved native operation before starting another synchronization.");
            return await ResumePendingAsync(store, client, designPath, saved, cancellationToken);
        }

        byte[] originalXml;
        try { originalXml = await File.ReadAllBytesAsync(designPath, cancellationToken); }
        catch (FileNotFoundException) { throw new AutomationException("design_file_missing", "The design XML file no longer exists."); }
        catch (DirectoryNotFoundException) { throw new AutomationException("design_file_missing", "The design XML directory no longer exists."); }
        if (!originalXml.AsSpan().SequenceEqual(saved.State.DesiredFileBytes))
            throw new AutomationException("design_file_changed", "The design XML is newer than the saved recovery observation.");

        var plan = SchematicSynchronizationPlanner.Plan(saved.State, cancellationToken);
        if (!plan.CanPrepare || plan.Candidate is null || plan.CandidateXml is null)
            throw new AutomationException(plan.ErrorCode ?? "design_sync_conflict",
                plan.ErrorMessage ?? "The design has unresolved synchronization conflicts.");
        byte[] candidateXml = Encoding.UTF8.GetBytes(plan.CandidateXml);
        var root = saved.State.Baseline.Schematic.Document?.Clone()
            ?? throw new AutomationException("invalid_recovery_state", "The saved design has no native root document.");
        var checkpoint = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = root, ProcessEpoch = client.Epoch }, cancellationToken);
        CheckedSchematicContract.ValidateObservation(checkpoint, root, client.Epoch);
        RequireSavedCheckpoint(saved.State, checkpoint);

        CheckedSchematicBatchReceipt? receipt = null;
        StoredDesignRecovery pending = saved;
        if (plan.NativeOperations.Count != 0)
        {
            var batch = new ApplySchematicItemBatch
            {
                Document = root.Clone(), DocumentEpoch = checkpoint.State.Revision.Epoch,
                ExpectedRevision = checkpoint.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"),
                OriginId = saved.State.OriginId.ToString("D"), Description = "Apply XML synchronization candidate"
            };
            batch.Operations.Add(plan.NativeOperations.Select(x => x.Clone()));
            var request = new CheckedSchematicBatch { Batch = batch, ExpectedState = checkpoint.State.Clone() };
            pending = store.Save(saved.State with
            {
                PendingMutation = batch, PendingNativeState = checkpoint.State.Clone(),
                PendingCandidateFileBytes = candidateXml
            }, saved.RevisionToken);
            receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, cancellationToken);
            CheckedSchematicContract.ValidateResult(request, receipt, inspect: false);
            if (receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted)
                throw new AutomationException("native_sync_not_committed", receipt.ErrorMessage.Length == 0
                    ? "The native synchronization was not committed; inspect the saved recovery operation." : receipt.ErrorMessage);
            var saveRequest = new CheckedSaveDocument
            {
                Document = receipt.Document.Clone(), OperationId = Guid.NewGuid().ToString("D"),
                ExpectedState = receipt.ObservedAfter!.Clone()
            };
            pending = store.Save(pending.State with { PendingNativeSave = saveRequest }, pending.RevisionToken);
            await SaveNativeAsync(client, saveRequest, receipt.ProcessEpoch, cancellationToken);
        }

        var observation = plan.NativeOperations.Count == 0
            ? await DesignRecoveryInspector.ObserveAsync(store, client, cancellationToken, includeElectrical: true)
            : await DesignRecoveryInspector.ObserveAsync(store, client, cancellationToken, includeElectrical: true);
        if (observation.Inspection.Disposition != DesignRecoveryDisposition.CompletedNeedsReconciliation
            && pending.State.PendingMutation is not null)
            throw new AutomationException("native_sync_receipt_missing", "The native commit cannot be reconciled from its exact receipt.");
        if (!observation.Snapshot.Data.Equals(plan.Candidate.Schematic))
            throw new AutomationException("native_sync_projection_mismatch", "The native schematic does not match the synchronized XML candidate.");
        var comparison = SchematicElectricalComparison.Compare(plan.Candidate, observation.Electrical!,
            saved.State.KnowledgeLibraries, cancellationToken);
        if (!comparison.PinBindingsComplete || !comparison.ConnectivityEquivalent)
            throw new AutomationException("native_sync_connectivity_mismatch", "The committed native schematic does not match the XML connectivity candidate.");

        string designHash = await PublishCandidateAsync(designPath, originalXml, candidateXml, cancellationToken);
        var current = store.Read() ?? throw new AutomationException("design_recovery_changed", "Recovery disappeared during synchronization.");
        var finalized = store.Save(current.State with
        {
            Baseline = plan.Candidate, DesiredFileBytes = candidateXml, Observed = observation.Snapshot.Data,
            NativeRevision = new(observation.Snapshot.Revision.Epoch, observation.Snapshot.Revision.Sequence),
            TrackingComplete = observation.Snapshot.TrackingComplete, BaselineElectrical = observation.Electrical,
            ObservedElectrical = observation.Electrical, PendingMutation = null, PendingNativeState = null,
            PendingNativeSave = null, PendingCandidateFileBytes = null, HierarchyResolution = null
        }, current.RevisionToken);
        return new(finalized.RevisionToken, designHash, finalized.State.NativeRevision,
            receipt is not null, receipt is not null, true, receipt);
    }

    private static async Task<SchematicSynchronizationExecution> ResumePendingAsync(DesignRecoveryStore store,
        NativeClient client, string designPath, StoredDesignRecovery saved, CancellationToken token)
    {
        var inspection = await DesignRecoveryInspector.InspectAsync(store, client, token);
        if (inspection.CheckedReceipt?.Status != CheckedSchematicBatchStatus.CsbsCompleted)
            throw new AutomationException("pending_recovery_requires_reconciliation", "The saved native batch is not confirmed; inspect its exact receipt before retrying.");
        var pendingState = saved.State;
        if (pendingState.PendingNativeSave is null)
        {
            if (inspection.CheckedReceipt.ObservedAfter is null)
                throw new AutomationException("native_sync_receipt_missing", "The completed native batch has no save precondition.");
            var saveRequest = new CheckedSaveDocument
            {
                Document = inspection.CheckedReceipt.Document.Clone(), OperationId = Guid.NewGuid().ToString("D"),
                ExpectedState = inspection.CheckedReceipt.ObservedAfter.Clone()
            };
            var updated = store.Save(pendingState with { PendingNativeSave = saveRequest }, saved.RevisionToken);
            pendingState = updated.State;
            await SaveNativeAsync(client, saveRequest, inspection.CheckedReceipt.ProcessEpoch, token);
            saved = updated;
        }
        else if (inspection.SaveReceipt?.Status != LifecycleOperationStatus.LosSaved)
            throw new AutomationException("native_save_requires_recovery", "The exact native save is not confirmed; inspect or retry its saved operation.");

        var observation = await DesignRecoveryInspector.ObserveAsync(store, client, token, includeElectrical: true);
        var candidate = SchematicDesignXml.Read(new UTF8Encoding(false, true).GetString(
            pendingState.PendingCandidateFileBytes!), pendingState.KnowledgeLibraries);
        if (!observation.Snapshot.Data.Equals(candidate.Schematic))
            throw new AutomationException("native_sync_projection_mismatch", "The recovered native schematic does not match its saved synchronization candidate.");
        var comparison = SchematicElectricalComparison.Compare(candidate, observation.Electrical!,
            pendingState.KnowledgeLibraries, token);
        if (!comparison.PinBindingsComplete || !comparison.ConnectivityEquivalent)
            throw new AutomationException("native_sync_connectivity_mismatch", "The recovered native schematic does not match its saved XML connectivity candidate.");
        byte[] originalXml = await File.ReadAllBytesAsync(designPath, token);
        if (!originalXml.AsSpan().SequenceEqual(pendingState.DesiredFileBytes))
            throw new AutomationException("design_file_changed_during_sync", "The design XML changed before the pending synchronization could be finalized.");
        byte[] candidateBytes = pendingState.PendingCandidateFileBytes!;
        string designHash = await PublishCandidateAsync(designPath, originalXml, candidateBytes, token);
        var current = store.Read() ?? throw new AutomationException("design_recovery_changed", "Recovery disappeared during synchronization recovery.");
        var finalized = store.Save(current.State with
        {
            Baseline = candidate, DesiredFileBytes = candidateBytes,
            Observed = observation.Snapshot.Data,
            NativeRevision = new(observation.Snapshot.Revision.Epoch, observation.Snapshot.Revision.Sequence),
            TrackingComplete = observation.Snapshot.TrackingComplete, BaselineElectrical = observation.Electrical,
            ObservedElectrical = observation.Electrical, PendingMutation = null, PendingNativeState = null,
            PendingNativeSave = null, PendingCandidateFileBytes = null, HierarchyResolution = null
        }, current.RevisionToken);
        return new(finalized.RevisionToken, designHash, finalized.State.NativeRevision, true, true, true,
            inspection.CheckedReceipt);
    }

    private static async Task SaveNativeAsync(NativeClient client, CheckedSaveDocument request,
        string processEpoch, CancellationToken token)
    {
        var result = await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(request, token);
        if (!Equals(result.Document, request.Document) || result.Status != LifecycleOperationStatus.LosSaved
            || result.ProcessEpoch != processEpoch || result.OperationId != request.OperationId)
            throw new AutomationException("native_sync_save_failed", "The native editor did not confirm a saved synchronized document.");
    }

    private static async Task<string> PublishCandidateAsync(string path, byte[] original, byte[] candidate,
        CancellationToken token)
    {
        try
        {
            byte[] current = await File.ReadAllBytesAsync(path, token);
            if (current.AsSpan().SequenceEqual(candidate))
                return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(candidate));
            if (!current.AsSpan().SequenceEqual(original))
                throw new AutomationException("design_file_changed_during_sync", "The design XML changed while native synchronization was running.");
        }
        catch (FileNotFoundException) { throw new AutomationException("design_file_missing", "The design XML file no longer exists."); }
        return await DesignFilePublisher.WriteIfUnchangedAsync(path, original, candidate, token);
    }

    private static void RequireSavedCheckpoint(DesignRecoveryState saved, CheckedSchematicState checkpoint)
    {
        if (checkpoint.State.Revision is null
            || checkpoint.State.Revision.Epoch != saved.NativeRevision.Epoch
            || checkpoint.State.Revision.Sequence != saved.NativeRevision.Sequence
            || !checkpoint.Electrical.Hierarchy.Revision.Equals(checkpoint.State.Revision)
            || !checkpoint.Electrical.Hierarchy.Data.Equals(saved.Observed)
            || (saved.ObservedElectrical is not null && !checkpoint.Electrical.Equals(saved.ObservedElectrical)))
            throw new AutomationException("native_checkpoint_stale", "The live native document changed after the saved recovery observation.");
    }
}
