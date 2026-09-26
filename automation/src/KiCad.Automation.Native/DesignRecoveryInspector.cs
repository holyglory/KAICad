using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using SchematicHierarchyData = Kiapi.Schematic.Types.SchematicHierarchyData;

namespace KiCad.Automation.Native;

public enum DesignRecoveryDisposition
{
    NoPendingOperation,
    NotFound,
    CompletedNeedsReconciliation,
    Rejected,
    Indeterminate,
    PublicationNeedsReconciliation
}

public sealed record DesignRecoveryInspection(string RevisionToken, DesignRecoveryDisposition Disposition,
    SchematicOperationReceipt? Receipt, CheckedSchematicBatchReceipt? CheckedReceipt = null,
    LifecycleOperationResult? SaveReceipt = null);

public sealed record DesignRecoveryObservation(DesignRecoveryInspection Inspection,
    SchematicHierarchyDataSnapshot Snapshot, SchematicElectricalState? Electrical = null);

/// <summary>Inspect the saved operation only. Never submit, clear a pending edit,
/// replace the desired file, or advance the synchronized baseline.</summary>
public static class DesignRecoveryInspector
{
    /// <summary>Persist a fresh observation only, leaving baseline and desired bytes intact.
    /// An unconfirmed mutation must first be reconciled through its exact receipt; refreshing
    /// cannot silently drop it or overwrite the revision required for an identical retry.</summary>
    public static async Task<StoredDesignRecovery> RefreshAsync(DesignRecoveryStore store,
        NativeClient client, string expectedRevisionToken, CancellationToken cancellationToken = default,
        KiCad.Automation.Model.DocumentRevision? minimumRevision = null, bool includeElectrical = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var saved = store.Read();
        if (saved is null || saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Reload the recovery record before refreshing its native observation.");
        if (saved.State.HasPendingWork)
            throw new AutomationException("pending_recovery_requires_reconciliation",
                "Inspect and reconcile the saved pending operation before replacing its observed revision.");
        var observed = await ObserveAsync(store, client, cancellationToken, includeElectrical);
        if (observed.Inspection.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery state changed during native refresh.");
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = observed.Snapshot;
        if (minimumRevision is not null && (snapshot.Revision.Epoch != minimumRevision.Epoch
            || snapshot.Revision.Sequence < minimumRevision.Sequence))
            throw new AutomationException("invalid_recovery_revision", "The snapshot predates the committed native event.");
        bool changed = !snapshot.Data.Equals(saved.State.Observed)
            || snapshot.Revision.Epoch != saved.State.NativeRevision.Epoch
            || snapshot.Revision.Sequence != saved.State.NativeRevision.Sequence
            || snapshot.TrackingComplete != saved.State.TrackingComplete;
        bool electricalChanged = observed.Electrical is not null && saved.State.ObservedElectrical is not null
            && !Equals(observed.Electrical, saved.State.ObservedElectrical);
        // The store performs the final compare-and-swap under its file lock. Even an
        // unchanged observation must pass that check rather than return a stale success.
        return store.Save(saved.State with
        {
            Observed = snapshot.Data,
            NativeRevision = new(snapshot.Revision.Epoch, snapshot.Revision.Sequence),
            TrackingComplete = snapshot.TrackingComplete,
            ObservedElectrical = observed.Electrical ?? (changed ? null : saved.State.ObservedElectrical),
            HierarchyResolution = changed || electricalChanged ? null : saved.State.HierarchyResolution
        }, expectedRevisionToken);
    }

    /// <summary>Read the current hierarchy after receipt inspection. A later revision may
    /// include undo or unrelated edits; never treat it as the operation's original result.
    /// Return both observations for reconciliation without changing the recovery record.</summary>
    public static async Task<DesignRecoveryObservation> ObserveAsync(DesignRecoveryStore store,
        NativeClient client, CancellationToken cancellationToken = default, bool includeElectrical = false)
    {
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "No saved design recovery state exists.");
        var inspection = await InspectAsync(store, client, cancellationToken);
        if (inspection.RevisionToken != saved.RevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery state changed before observation; reload it before continuing.");
        // InspectAsync intentionally makes no IPC call when there is no pending operation.
        // Observation always verifies the saved instance, including that case.
        var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != saved.State.InstanceId.ToString("D"))
            throw new AutomationException("recovery_instance_mismatch", "Reattach the exact instance recorded by this design before recovery.");
        var target = (saved.State.PendingMutation?.Document ?? saved.State.Baseline.Schematic.Document).Clone();
        var electrical = includeElectrical ? await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(
            new() { Document = target }, cancellationToken) : null;
        var snapshot = includeElectrical
            ? electrical?.Hierarchy ?? throw new AutomationException("invalid_recovery_snapshot", "The electrical response has no hierarchy snapshot.")
            : await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = target }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (store.Read()?.RevisionToken != saved.RevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery state changed during observation; reload it before continuing.");
        if (snapshot.Data?.Document is null || !snapshot.Data.Document.Equals(saved.State.Baseline.Schematic.Document))
            throw new AutomationException("invalid_recovery_snapshot", "The native snapshot identifies a different design.");
        ulong minimumSequence = Math.Max(saved.State.NativeRevision.Sequence,
            Math.Max(inspection.Receipt?.Result?.Revision?.Sequence ?? 0,
                inspection.CheckedReceipt?.Result?.Revision?.Sequence ?? 0));
        if (snapshot.Revision is not { } revision || revision.Epoch != saved.State.NativeRevision.Epoch
            || revision.Sequence < minimumSequence)
            throw new AutomationException("invalid_recovery_revision", "The native snapshot predates recovery or belongs to a different document session.");
        // Validate supported typed serialization before handing data to reconciliation.
        // Explicit coverage gaps and incomplete tracking remain in the returned snapshot.
        _ = SchematicDataXml.Read(SchematicDataXml.Write(snapshot.Data));
        return new(inspection, snapshot, electrical);
    }

    /// <summary>Bootstrap only a missing electrical baseline after proving that
    /// the native hierarchy and mapped circuit pins still match the saved baseline.</summary>
    public static async Task<StoredDesignRecovery> InitializeElectricalBaselineAsync(DesignRecoveryStore store,
        NativeClient client, string expectedRevisionToken, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var saved = store.Read();
        if (saved is null || saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Reload recovery before initializing its electrical baseline.");
        if (saved.State.HasPendingWork)
            throw new AutomationException("pending_recovery_requires_reconciliation", "Reconcile the pending edit before initializing a baseline.");
        if (saved.State.BaselineElectrical is not null)
            throw new AutomationException("electrical_baseline_exists", "An established electrical baseline cannot be replaced by initialization.");
        var observed = await ObserveAsync(store, client, token, includeElectrical: true);
        if (observed.Inspection.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery changed during electrical baseline observation.");
        if (!SameSchematicContent(saved.State.Baseline.Schematic, observed.Snapshot.Data))
            throw new AutomationException("electrical_baseline_mismatch", "Reconcile native edits before establishing the missing electrical baseline.");
        var comparison = SchematicElectricalComparison.Compare(saved.State.Baseline, observed.Electrical!, saved.State.KnowledgeLibraries, token);
        if (!comparison.PinBindingsComplete || !comparison.ConnectivityEquivalent)
            throw new AutomationException("electrical_baseline_mismatch", "The saved circuit and exact native pin connections do not agree.");
        token.ThrowIfCancellationRequested();
        // The baseline checkpoint belongs to the saved baseline hierarchy, which may still carry an earlier build's retired
        // coverage marker; its pin connections are the ones KiCad shows for that same content.
        var baselineElectrical = observed.Electrical!.Clone();
        baselineElectrical.Hierarchy.Data = saved.State.Baseline.Schematic.Clone();
        return store.Save(saved.State with
        {
            Observed = observed.Snapshot.Data, NativeRevision = new(observed.Snapshot.Revision.Epoch, observed.Snapshot.Revision.Sequence),
            TrackingComplete = observed.Snapshot.TrackingComplete, BaselineElectrical = baselineElectrical,
            ObservedElectrical = observed.Electrical,
            HierarchyResolution = observed.Snapshot.Data.Equals(saved.State.Observed)
                && observed.Snapshot.Revision.Epoch == saved.State.NativeRevision.Epoch
                && observed.Snapshot.Revision.Sequence == saved.State.NativeRevision.Sequence
                && observed.Snapshot.TrackingComplete == saved.State.TrackingComplete ? saved.State.HierarchyResolution : null
        }, expectedRevisionToken);
    }

    /// <summary>Whether two native hierarchy snapshots hold the same schematic: every screen, object, library cache entry,
    /// setting and coverage entry exactly, apart from retired coverage markers (SchematicItemDelta.RetiredCoverageMarkers).
    /// A record an earlier preview saved still lists library_cache as state its snapshot could not hold, although that
    /// build held the cache exactly; this build no longer lists it, so the two describe the same schematic. Every other
    /// difference, in the schematic or in what the snapshot says it cannot hold, still counts.</summary>
    internal static bool SameSchematicContent(SchematicHierarchyData saved, SchematicHierarchyData observed)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(observed);
        static SchematicHierarchyData WithoutRetiredCoverage(SchematicHierarchyData data)
        {
            var result = data.Clone();
            foreach (var screen in result.Instances.Where(s => s.Metadata is not null))
            {
                var kept = screen.Metadata.UnrepresentedState.Where(m => !SchematicItemDelta.RetiredCoverageMarkers.Contains(m)).ToArray();
                screen.Metadata.UnrepresentedState.Clear(); screen.Metadata.UnrepresentedState.Add(kept);
            }
            return result;
        }
        return WithoutRetiredCoverage(saved).Equals(WithoutRetiredCoverage(observed));
    }

    public static async Task<DesignRecoveryInspection> InspectAsync(DesignRecoveryStore store,
        NativeClient client, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "No saved design recovery state exists.");
        var pending = saved.State.PendingMutation;
        if (pending is null && saved.State.PendingPublication is null)
            return new(saved.RevisionToken, DesignRecoveryDisposition.NoPendingOperation, null);
        var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != saved.State.InstanceId.ToString("D"))
            throw new AutomationException("recovery_instance_mismatch", "Reattach the exact instance recorded by this design before recovery.");
        if (pending is null)
        {
            if (saved.State.PendingNativeState?.ProcessEpoch != session.Epoch)
                throw new AutomationException("instance_changed", "The pending publication belongs to another native process.");
            var saveResult = saved.State.PendingNativeSave is { } requestSave
                ? await ReadSaveReceiptAsync(client, requestSave, cancellationToken) : null;
            cancellationToken.ThrowIfCancellationRequested();
            if (store.Read()?.RevisionToken != saved.RevisionToken)
                throw new AutomationException("design_recovery_changed", "Recovery changed during publication inspection.");
            return new(saved.RevisionToken, DesignRecoveryDisposition.PublicationNeedsReconciliation, null, null, saveResult);
        }
        if (saved.State.PendingNativeState is { } nativeState)
        {
            var expected = new CheckedSchematicBatch { Batch = pending.Clone(), ExpectedState = nativeState.Clone() };
            CheckedSchematicContract.ValidateRequest(expected, session.Epoch);
            var checkedReceipt = await client.InvokeAsync<ReadCheckedSchematicBatchReceipt, CheckedSchematicBatchReceipt>(new()
            {
                Document = pending.Document.Clone(), ProcessEpoch = nativeState.ProcessEpoch,
                OperationId = pending.OperationId, ExpectedRequest = expected
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (store.Read()?.RevisionToken != saved.RevisionToken)
                throw new AutomationException("design_recovery_changed", "Recovery changed during checked receipt inspection.");
            CheckedSchematicContract.ValidateResult(expected, checkedReceipt, inspect: true);
            var checkedDisposition = checkedReceipt.Status switch
            {
                CheckedSchematicBatchStatus.CsbsNotFound => DesignRecoveryDisposition.NotFound,
                CheckedSchematicBatchStatus.CsbsCompleted => DesignRecoveryDisposition.CompletedNeedsReconciliation,
                CheckedSchematicBatchStatus.CsbsRejected => DesignRecoveryDisposition.Rejected,
                _ => DesignRecoveryDisposition.Indeterminate
            };
            LifecycleOperationResult? saveReceipt = null;
            if (saved.State.PendingNativeSave is { } saveRequest)
            {
                saveReceipt = await ReadSaveReceiptAsync(client, saveRequest, cancellationToken);
            }
            // A failed save is not a rejected (or missing) schematic mutation.
            // Keep both outcomes available so observation can inspect recovery
            // without retrying, erasing, or misreporting the committed edit.
            cancellationToken.ThrowIfCancellationRequested();
            if (store.Read()?.RevisionToken != saved.RevisionToken)
                throw new AutomationException("design_recovery_changed", "Recovery changed during native save receipt inspection.");
            return new(saved.RevisionToken, checkedDisposition, null, checkedReceipt, saveReceipt);
        }
        var request = new InspectSchematicOperation
        {
            Document = pending.Document.Clone(), DocumentEpoch = pending.DocumentEpoch,
            OperationId = pending.OperationId, ExpectedRequest = pending.Clone()
        };
        var receipt = await client.InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (store.Read()?.RevisionToken != saved.RevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery state changed during inspection; reload it before continuing.");
        if (!Equals(receipt.Document, request.Document) || receipt.DocumentEpoch != request.DocumentEpoch
            || receipt.OperationId != request.OperationId)
            throw new AutomationException("invalid_recovery_receipt", "The native reply identifies a different pending edit.");
        if (receipt.State != SchematicOperationReceipt.Types.State.NotFound && !receipt.ExpectedRequestVerified)
            throw new AutomationException("unverified_recovery_receipt", "The native peer did not verify the exact saved request; retain it for reconciliation.");
        if (receipt.State == SchematicOperationReceipt.Types.State.Completed
            && (receipt.Result?.Revision is not { } revision || revision.Epoch != pending.DocumentEpoch
                || revision.Sequence < pending.ExpectedRevision.Sequence))
            throw new AutomationException("invalid_recovery_revision", "The completed edit has no matching non-regressing result revision; reobserve before reconciliation.");
        var disposition = receipt.State switch
        {
            SchematicOperationReceipt.Types.State.NotFound when receipt.Result is null && !receipt.ExpectedRequestVerified
                && receipt.NativeStatusCode == 0 => DesignRecoveryDisposition.NotFound,
            SchematicOperationReceipt.Types.State.Completed when receipt.Result is not null && receipt.NativeStatusCode == 0
                => DesignRecoveryDisposition.CompletedNeedsReconciliation,
            SchematicOperationReceipt.Types.State.Rejected when receipt.Result is null && receipt.NativeStatusCode > 1
                => DesignRecoveryDisposition.Rejected,
            SchematicOperationReceipt.Types.State.Indeterminate when receipt.Result is null && receipt.NativeStatusCode == 0
                => DesignRecoveryDisposition.Indeterminate,
            _ => throw new AutomationException("invalid_recovery_receipt", "The native receipt has an unknown or inconsistent outcome.")
        };
        return new(saved.RevisionToken, disposition, receipt);
    }

    private static async Task<LifecycleOperationResult> ReadSaveReceiptAsync(NativeClient client,
        CheckedSaveDocument request, CancellationToken token)
    {
        var receipt = await client.InvokeAsync<ReadLifecycleOperation, LifecycleOperationResult>(new()
        {
            Document = request.Document.Clone(), OperationId = request.OperationId,
            ProcessEpoch = request.ExpectedState.ProcessEpoch
        }, token);
        if (!Equals(receipt.Document, request.Document) || receipt.OperationId != request.OperationId
            || receipt.ProcessEpoch != request.ExpectedState.ProcessEpoch
            || receipt.Status is not (LifecycleOperationStatus.LosSaved or LifecycleOperationStatus.LosRejected
                or LifecycleOperationStatus.LosFailed or LifecycleOperationStatus.LosIndeterminate))
            throw new AutomationException("invalid_recovery_save_receipt", "The native save receipt identifies a different operation.");
        return receipt;
    }
}
