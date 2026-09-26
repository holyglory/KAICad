using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using NativeRevision = KiCad.Automation.Model.DocumentRevision;

namespace KiCad.Automation.Native;

/// <summary>How a person or agent leaves a synchronization whose native change KiCad committed but whose follow-up check
/// refused it (ledgers p0aa59a1dfc8701ea and p6728215278183167).</summary>
public enum PendingOperationChoice
{
    /// <summary>Remove exactly the operation's native change from KiCad and clear the pending operation.</summary>
    Undo,
    /// <summary>Keep what KiCad shows as the operation's result and publish the XML re-planned from it.</summary>
    KeepAndReplan,
    /// <summary>Clear a pending operation whose result KiCad is verified not to show.</summary>
    Discard
}

/// <summary>The receipt of one resolved pending operation, kept next to the recovery record in "&lt;record&gt;.resolved": which
/// operation, at which record revision, what was chosen and done, and the whole pending operation as it was, so a later
/// look at KiCad's history or at the XML can tell the operation's result from a user edit. Outcome is "undone" (KiCad shows
/// the design as it was before the operation), "discarded" (KiCad already showed it) or "keep-pending" (KiCad keeps the
/// operation's result and the continuation publishes it).</summary>
public sealed record DesignPendingResolution(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] Guid OperationId,
    string? NativeOperationId,
    [property: JsonRequired] Guid InstanceId,
    [property: JsonRequired] string Choice,
    [property: JsonRequired] string Outcome,
    [property: JsonRequired] string ResolvedFromRevisionToken,
    [property: JsonRequired] string PendingKind,
    string? Lane,
    [property: JsonRequired] string NativeStatus,
    [property: JsonRequired] string ProcessEpoch,
    [property: JsonRequired] NativeRevision RevisionBefore,
    [property: JsonRequired] NativeRevision RevisionAfter,
    string? UndoOperationId,
    [property: JsonRequired] int UndoOperations,
    Guid? ContinuationOperationId,
    [property: JsonRequired] bool NativeChangedSinceOperation,
    [property: JsonRequired] string BaselineSha256,
    [property: JsonRequired] string DesiredSha256,
    byte[]? PendingMutation,
    byte[]? PendingNativeState,
    DesignPublicationIntent? PendingPublication,
    DesignLayoutIntent? PendingLayout,
    IReadOnlyList<NetIdentityChange>? NetChanges,
    [property: JsonRequired] DateTimeOffset ResolvedAt)
{
    public const int CurrentSchemaVersion = 1;
    public const string Undone = "undone", Discarded = "discarded", KeepPending = "keep-pending";

    public static string ChoiceName(PendingOperationChoice choice) => choice switch
    {
        PendingOperationChoice.Undo => "undo", PendingOperationChoice.KeepAndReplan => "keep-and-replan",
        PendingOperationChoice.Discard => "discard", _ => throw new ArgumentOutOfRangeException(nameof(choice))
    };
}

/// <summary>Resolution receipts kept next to a design recovery record, in "&lt;record&gt;.resolved": one immutable file per
/// resolved operation.</summary>
public static class DesignPendingResolutions
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Directory(string recoveryPath) => Path.GetFullPath(recoveryPath) + ".resolved";

    /// <summary>Writes the receipt unless one exists for the same operation: the first resolution keeps what it saw.</summary>
    internal static string Write(string recoveryPath, DesignPendingResolution receipt)
    {
        string folder = System.IO.Directory.CreateDirectory(Directory(recoveryPath)).FullName;
        string destination = Path.Combine(folder, receipt.OperationId.ToString("N") + ".json");
        if (File.Exists(destination)) return destination;
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, receipt, Json);
                output.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, destination, false); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return destination;
    }

    /// <summary>The receipt of the operation's resolution, or null when it was never resolved.</summary>
    public static (DesignPendingResolution Receipt, string Path)? Find(string recoveryPath, Guid operationId, Guid instanceId)
    {
        string file = Path.Combine(Directory(recoveryPath), operationId.ToString("N") + ".json");
        if (!File.Exists(file)) return null;
        var receipt = Read(file);
        if (receipt.OperationId != operationId || receipt.InstanceId != instanceId)
            throw new AutomationException("invalid_resolution_receipt", $"The resolution receipt {file} does not belong to this operation and instance.");
        return (receipt, file);
    }

    public static DesignPendingResolution Read(string path)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<DesignPendingResolution>(File.ReadAllBytes(path), Json);
            if (receipt is null || receipt.SchemaVersion != DesignPendingResolution.CurrentSchemaVersion
                || receipt.Outcome is not (DesignPendingResolution.Undone or DesignPendingResolution.Discarded or DesignPendingResolution.KeepPending))
                throw new AutomationException("invalid_resolution_receipt", $"The resolution receipt {path} is incomplete.");
            return receipt;
        }
        catch (JsonException error)
        { throw new AutomationException("invalid_resolution_receipt", $"The resolution receipt {path} cannot be read: {error.Message}"); }
    }
}

/// <summary>What resolving a pending operation did. ResolvedNow is false for a repeated call that found the operation
/// already resolved at the named revision; nothing was changed then.</summary>
public sealed record PendingOperationResolution(StoredDesignRecovery Recovery, DesignPendingResolution Receipt, string ReceiptPath,
    bool ResolvedNow, string NextStep);

/// <summary>The guarded way out of a synchronization that KiCad applied but whose follow-up check refused it, for example
/// a created component whose connections KiCad draws otherwise than the XML says (native_sync_connectivity_mismatch), or a
/// connection realization whose committed drawing differs from its plan (realization_resolution_mismatch). Retrying such
/// an operation only repeats the refusal, and no other tool clears it. This one is bound to the exact pending operation
/// and recovery revision, never runs while an automatic worker or another synchronization owns the record, reads KiCad's
/// retained receipt of the operation's native edit without ever executing it, and changes nothing unless its check of
/// KiCad passes:
/// <list type="bullet">
/// <item>undo: KiCad must show exactly the state the operation's edit left (nothing changed since); one checked native
/// edit, attributed to the design, restores the state the operation started from, and KiCad is verified to show it.</item>
/// <item>keep-and-replan: KiCad keeps what it shows; the operation's planned design is re-planned from KiCad's objects and
/// connections (KiCad's connections win; each net that changes is reported for its requirements), and one prepared
/// publication of that design is journaled for the running KiCad, completed like any pending synchronization.</item>
/// <item>discard: KiCad must already show the state the operation started from (its edit never ran, was refused, was
/// undone in KiCad, or KiCad reloaded the saved sheets); nothing is sent to KiCad.</item>
/// </list>
/// The XML is never written here: undo and discard leave it as it is, so the next synchronization applies it again, and
/// keep-and-replan writes it only when its continuation publishes a result KiCad was checked to show. The baseline never
/// moves here. The record's observation moves to the KiCad state that was verified.</summary>
public static class DesignRecoveryPendingResolution
{
    public static async Task<PendingOperationResolution> ResolveAsync(DesignRecoveryStore store, InstanceRegistry? registry,
        string instanceId, string expectedRevisionToken, Guid operationId, PendingOperationChoice choice, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        token.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(instanceId, "D", out var instance) || instance == Guid.Empty)
            throw Error("invalid_instance_id", "Specify the saved instance ID.");
        if (operationId == Guid.Empty) throw Error("invalid_operation_id", "Specify the pending operation's UUID.");
        if (!Enum.IsDefined(choice)) throw Error("invalid_resolution_choice", "Choose undo, keep-and-replan or discard.");
        var saved = store.Read() ?? throw Error("missing_design_recovery", "No saved recovery record exists.");
        if (saved.State.InstanceId != instance)
            throw Error("recovery_instance_mismatch", "The record belongs to a different instance.");
        string choiceName = DesignPendingResolution.ChoiceName(choice);

        // A repeated call after the operation was resolved reports that resolution and changes nothing.
        if (DesignPendingResolutions.Find(store.StatePath, operationId, instance) is { } done
            && (saved.RevisionToken != expectedRevisionToken || !Holds(saved.State, operationId)))
        {
            if (done.Receipt.ResolvedFromRevisionToken != expectedRevisionToken || done.Receipt.Choice != choiceName)
                throw Error("pending_operation_resolved", $"Operation {operationId:D} was already resolved with '{done.Receipt.Choice}' "
                    + $"(outcome {done.Receipt.Outcome}) at recovery revision {done.Receipt.ResolvedFromRevisionToken}; nothing was changed. "
                    + "Read the record again with kicad_design_recovery_plan.");
            return new(saved, done.Receipt, done.Path, false, NextStep(saved, done.Receipt));
        }
        if (saved.RevisionToken != expectedRevisionToken)
            throw Error("design_recovery_changed", "Recovery changed; read the current record with kicad_design_recovery_plan before resolving its operation.");
        var state = saved.State;
        if (!state.HasPendingWork)
            throw Error("no_pending_operation", "The recovery record holds no pending operation; nothing was changed.");
        Guid? pending = state.PendingPublication?.OperationId ?? state.PendingLayout?.OperationId;
        if (pending is null)
            throw Error("pending_operation_unidentified", "The pending native edit has no synchronization operation ID (a record from an earlier "
                + "preview). Inspect it with kicad_design_recovery_observe; nothing was changed.");
        if (pending != operationId)
            throw Error("pending_operation_mismatch", $"The recovery record holds operation {pending:D}, not {operationId:D}; nothing was changed.");
        if (state.PendingPublication is { Phase: not DesignPublicationPhase.Prepared } started)
            throw Error("publication_started", $"Operation {operationId:D} was already publishing its XML (phase {started.Phase}), so KiCad saved "
                + "its result and the XML may be replaced. Complete it with kicad_design_sync_apply (the same operationId and its requested "
                + "recovery revision token); nothing was changed.");
        var guard = state.PendingNativeState ?? throw Error("operation_process_unknown",
            "The pending operation does not record which KiCad process holds it; inspect it with kicad_design_recovery_plan. Nothing was changed.");
        if (state.PendingNativeSave is not null && choice != PendingOperationChoice.KeepAndReplan)
            throw Error("pending_native_save_started", $"Operation {operationId:D} had already asked KiCad to save its result, so KiCad's files "
                + "may hold it. Keep it (keep-and-replan), or complete it with kicad_design_sync_apply; nothing was changed.");

        var client = Client(registry, instanceId, guard.ProcessEpoch, operationId);
        if (client.Epoch != guard.ProcessEpoch)
            throw Error("operation_process_ended", $"Operation {operationId:D} belongs to KiCad process epoch {guard.ProcessEpoch}, but this "
                + $"instance now runs process epoch {client.Epoch}: the KiCad that holds it ended. Release it with "
                + "kicad_design_recovery_release_exited; nothing was changed.");
        var session = await client.HandshakeAsync(token);
        if (session.InstanceId != instanceId)
            throw Error("recovery_instance_mismatch", "The attached KiCad is not the instance recorded by this design.");

        using var automatic = AutomaticOwnership(store);
        using var writer = Writer(store);
        saved = store.Read() ?? throw Error("missing_design_recovery", "No saved recovery record exists.");
        if (saved.RevisionToken != expectedRevisionToken)
            throw Error("design_recovery_changed", "Recovery changed while taking ownership of the record; read it again.");
        state = saved.State;

        var live = await Capture(client, state, token);
        var native = await NativeStatusAsync(client, state, live, token);
        var before = state.Observed;
        bool showsStart = Same(live.Electrical.Hierarchy.Data, before, token) && SameConnections(state.ObservedElectrical, live.Electrical);
        bool changedSince = native.Receipt?.ObservedAfter is { } after && !live.State.Equals(after);
        var lane = state.PendingLayout?.Lane ?? (state.PendingLayout is not null ? "connected-move" : "ordinary");
        string kind = state.PendingLayout is not null ? "layout" : "publication";
        DesignRecoveryState next;
        string outcome;
        string? undoId = null;
        int undoOperations = 0;
        Guid? continuation = null;
        IReadOnlyList<NetIdentityChange>? netChanges = null;
        var observed = live;

        switch (choice)
        {
            case PendingOperationChoice.Discard:
                if (!showsStart)
                    throw Error("operation_result_in_kicad", $"KiCad does not show the design as it was before operation {operationId:D}"
                        + (native.Status == "completed" ? $": KiCad committed its native edit {state.PendingMutation!.OperationId}" : "")
                        + ". Discarding would leave that result in KiCad as if it were a user edit. Remove it with undo, or keep it with "
                        + "keep-and-replan; nothing was changed.");
                next = Attached(state, live);
                outcome = DesignPendingResolution.Discarded;
                break;

            case PendingOperationChoice.Undo:
                if (native.Status == "indeterminate")
                    throw Indeterminate(operationId, state);
                if (native.Status != "completed")
                    throw Error("native_operation_not_committed", $"KiCad holds no committed native edit of operation {operationId:D} "
                        + $"(its receipt is {native.Status}), so there is nothing to undo. "
                        + (showsStart ? "KiCad shows the design as it was before the operation: choose discard."
                            : "KiCad does not show the design as it was before the operation either; inspect it with kicad_schematic_checked_state.")
                        + " Nothing was changed.");
                // Already undone (for example by an earlier call whose reply was lost): nothing is sent again.
                if (!showsStart)
                {
                    if (changedSince)
                        throw Error("native_changed_since_operation", $"KiCad changed after operation {operationId:D} committed its native "
                            + $"edit (revision {native.Receipt!.ObservedAfter.Revision.Sequence}, now {live.State.Revision.Sequence}), so "
                            + "undoing it would also remove those later edits. Undo them in KiCad first, or keep KiCad's result with "
                            + "keep-and-replan; nothing was changed.");
                    IReadOnlyList<SchematicItemOperation> restore;
                    try { restore = SchematicHierarchyDelta.Plan(live.Electrical.Hierarchy.Data, before, token); }
                    catch (AutomationException error)
                    {
                        throw Error("undo_unsupported", $"The native edit of operation {operationId:D} cannot be undone as one checked edit "
                            + $"({error.Code}: {error.Message}). Undo it in KiCad (Edit > Undo) and then choose discard, or keep it with "
                            + "keep-and-replan; nothing was changed.");
                    }
                    var request = new CheckedSchematicBatch { Batch = Batch(live.State, state.OriginId, UndoOperation(operationId, native.Receipt!.OperationId),
                        $"Undo synchronization {operationId:D} (native edit {native.Receipt.OperationId})", restore), ExpectedState = live.State.Clone() };
                    CheckedSchematicContract.ValidateRequest(request, client.Epoch);
                    var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
                    CheckedSchematicContract.ValidateResult(request, receipt, inspect: false);
                    if (receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted)
                        throw Error("undo_not_committed", $"KiCad did not undo operation {operationId:D} ({receipt.Status}: {receipt.ErrorCode} "
                            + $"{receipt.ErrorMessage}). The pending operation is kept; nothing was changed in the recovery record.");
                    undoId = request.Batch.OperationId; undoOperations = restore.Count;
                    observed = await Capture(client, state, token);
                    if (!observed.State.Equals(receipt.ObservedAfter)
                        || !Same(observed.Electrical.Hierarchy.Data, before, token) || !SameConnections(state.ObservedElectrical, observed.Electrical))
                        throw Error("undo_incomplete", $"KiCad committed the undo of operation {operationId:D} (native edit {undoId}), but "
                            + "does not show exactly the design the operation started from. The pending operation is kept: keep KiCad's "
                            + "result with keep-and-replan, or undo the rest in KiCad and choose discard.");
                }
                next = Attached(state, observed);
                outcome = DesignPendingResolution.Undone;
                break;

            case PendingOperationChoice.KeepAndReplan:
                if (native.Status == "indeterminate") throw Indeterminate(operationId, state);
                if (native.Status is "not-found" or "rejected" or "session-ended")
                    throw Error("native_operation_not_committed", $"KiCad holds no committed native edit of operation {operationId:D} "
                        + $"(its receipt is {native.Status}), so there is no result to keep. Choose discard, or resume the operation with "
                        + "kicad_design_sync_apply; nothing was changed.");
                string designPath = state.PendingPublication?.DesignPath ?? state.PendingLayout!.DesignPath;
                byte[] input = state.PendingPublication?.ExpectedFileBytes ?? state.PendingLayout!.ExpectedFileBytes;
                byte[] xml;
                try { xml = await File.ReadAllBytesAsync(designPath, token); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                { throw Error("design_file_changed", $"The XML file {designPath} no longer exists; restore it before keeping KiCad's result. Nothing was changed."); }
                if (!xml.AsSpan().SequenceEqual(state.DesiredFileBytes) || !input.AsSpan().SequenceEqual(state.DesiredFileBytes))
                    throw Error("design_file_changed", $"The XML file {designPath} changed after operation {operationId:D} started, so KiCad's "
                        + "result would be published over that change. Undo the operation (or put that XML version back) instead; nothing was changed.");
                byte[] planned = state.PendingPublication?.CandidateFileBytes ?? state.PendingLayout!.PlannedDesignFileBytes;
                var kept = SchematicDesignXml.Read(new UTF8Encoding(false, true).GetString(planned), state.KnowledgeLibraries)
                    with { Schematic = live.Electrical.Hierarchy.Data.Clone() };
                var bindings = SchematicDesignBindings.Inspect(kept, state.KnowledgeLibraries, token);
                if (!bindings.IdentitiesResolved)
                    throw Error("kept_result_unbound", $"KiCad's objects no longer resolve to the components, sheets and pins of operation "
                        + $"{operationId:D}: {Describe(bindings.Issues.Select(i => i.Code + (i.NativePath is null ? "" : " at " + i.NativePath)))}. "
                        + "Undo it instead; nothing was changed.");
                if (bindings.Differences.Count != 0)
                    throw Error("kept_result_differs", $"KiCad shows other symbol properties than operation {operationId:D} planned: "
                        + $"{Describe(bindings.Differences.Select(d => $"{d.Field} of occurrence {d.SymbolOccurrenceId:D} is '{d.NativeValue}' in KiCad, '{d.ModelValue}' planned"))}. "
                        + "Change them in KiCad as planned and choose keep-and-replan again, or undo the operation; nothing was changed.");
                var nets = SchematicNetReconciliation.PlanKept(kept, live.Electrical, state.OriginId, state.KnowledgeLibraries, token);
                if (nets.Candidate is null)
                    throw Error("kept_result_conflict", $"KiCad's connections cannot be kept for operation {operationId:D} ({nets.ErrorCode}: "
                        + $"{nets.ErrorMessage}). Undo it instead; nothing was changed.");
                kept = kept with { Engineering = nets.Candidate };
                var check = SchematicElectricalComparison.Compare(kept, live.Electrical, state.KnowledgeLibraries, token);
                string keptXml = SchematicDesignXml.Write(kept, state.KnowledgeLibraries);
                if (!check.PinBindingsComplete || !check.ConnectivityEquivalent
                    || SchematicDesignXml.Write(SchematicDesignXml.Read(keptXml, state.KnowledgeLibraries), state.KnowledgeLibraries) != keptXml)
                    throw Error("kept_result_differs", $"The design re-planned from KiCad's result of operation {operationId:D} still does not "
                        + "describe what KiCad shows. Undo it instead; nothing was changed.");
                continuation = KeepOperation(operationId, live.State.ProcessEpoch);
                netChanges = nets.NetChanges;
                next = Attached(state, live) with
                {
                    PendingNativeState = live.State.Clone(),
                    PendingPublication = DesignPublicationIntent.Create(designPath, state.DesiredFileBytes, Encoding.UTF8.GetBytes(keptXml),
                        continuation, saved.RevisionToken)
                };
                outcome = DesignPendingResolution.KeepPending;
                break;

            default: throw Error("invalid_resolution_choice", "Choose undo, keep-and-replan or discard.");
        }

        var resolution = new DesignPendingResolution(DesignPendingResolution.CurrentSchemaVersion, operationId, state.PendingMutation?.OperationId,
            state.InstanceId, choiceName, outcome, saved.RevisionToken, kind, lane, native.Status, guard.ProcessEpoch, state.NativeRevision,
            new(observed.State.Revision.Epoch, observed.State.Revision.Sequence), undoId, undoOperations, continuation, changedSince,
            DesignReleasedOperation.BaselineDigest(state), DesignReleasedOperation.Sha(state.DesiredFileBytes), state.PendingMutation?.ToByteArray(),
            guard.ToByteArray(), state.PendingPublication, state.PendingLayout, netChanges, DateTimeOffset.UtcNow);
        var resolved = store.ResolvePendingOperation(saved, next, resolution);
        var stored = DesignPendingResolutions.Find(store.StatePath, operationId, instance)
            ?? throw Error("invalid_resolution_receipt", "The resolution receipt was not kept.");
        return new(resolved, stored.Receipt, stored.Path, true, NextStep(resolved, stored.Receipt));
    }

    /// <summary>What to do next after a resolution, from the record as it is now.</summary>
    internal static string NextStep(StoredDesignRecovery current, DesignPendingResolution receipt)
    {
        if (receipt.Outcome == DesignPendingResolution.KeepPending)
        {
            var id = receipt.ContinuationOperationId!.Value;
            if (current.State.PendingPublication is { } kept && kept.OperationId == id)
                return $"KiCad keeps the result of operation {receipt.OperationId:D}. Operation {id:D} publishes it: complete it with "
                    + $"kicad_design_sync_apply (operationId {id:D}, expectedRevisionToken {kept.RequestedRecoveryRevisionToken}), or start "
                    + "automatic synchronization, which completes pending work first. It saves KiCad's sheets and writes the XML only if "
                    + "KiCad still shows that result."
                    + (receipt.NetChanges is { Count: > 0 } changes ? $" Keeping KiCad's connections changes {changes.Count} net(s) of the XML; "
                        + "the requirements bound to them are kept as unresolved net bindings to resolve." : "");
            return current.State.LastSynchronization?.OperationId == id
                ? $"The kept result of operation {receipt.OperationId:D} was published by operation {id:D}; nothing is pending."
                : $"Operation {id:D}, which published the kept result, is no longer pending; read the record with kicad_design_recovery_plan.";
        }
        return $"KiCad shows the design as it was before operation {receipt.OperationId:D} and nothing is pending. The XML still asks for "
            + "that change, so the next synchronization (kicad_design_sync_plan then kicad_design_sync_apply, or automatic "
            + "synchronization) applies it again; change the XML first if KiCad would refuse it again.";
    }

    private static bool Holds(DesignRecoveryState state, Guid operationId) =>
        (state.PendingPublication?.OperationId ?? state.PendingLayout?.OperationId) == operationId;

    // The record attached to the verified KiCad state, with no pending operation. Whole-sheet choices were made against the
    // observation this replaces, so they are chosen again; nothing else changes.
    private static DesignRecoveryState Attached(DesignRecoveryState state, CheckedSchematicState live) => state with
    {
        Observed = live.Electrical.Hierarchy.Data.Clone(), ObservedElectrical = live.Electrical.Clone(),
        NativeRevision = new(live.State.Revision.Epoch, live.State.Revision.Sequence), TrackingComplete = live.Electrical.Hierarchy.TrackingComplete,
        HierarchyResolution = null, PendingMutation = null, PendingNativeState = null, PendingNativeSave = null,
        PendingCandidateFileBytes = null, PendingPublication = null, PendingLayout = null
    };

    private sealed record NativeStatus(string Status, CheckedSchematicBatchReceipt? Receipt);

    // KiCad's retained receipt of the pending native edit, read without executing it: completed (committed), not-found
    // (never reached this document session), rejected (refused with nothing changed) or indeterminate. When KiCad has since
    // reloaded the sheets (a new document session), the edit can never run and its session's receipts are gone: session-ended.
    private static async Task<NativeStatus> NativeStatusAsync(NativeClient client, DesignRecoveryState state, CheckedSchematicState live,
        CancellationToken token)
    {
        if (state.PendingMutation is not { } batch) return new("none", null);
        if (live.State.Revision.Epoch != batch.DocumentEpoch) return new("session-ended", null);
        var expected = new CheckedSchematicBatch { Batch = batch.Clone(), ExpectedState = state.PendingNativeState!.Clone() };
        CheckedSchematicContract.ValidateRequest(expected, client.Epoch);
        var receipt = await client.InvokeAsync<ReadCheckedSchematicBatchReceipt, CheckedSchematicBatchReceipt>(new()
        {
            Document = batch.Document.Clone(), ProcessEpoch = client.Epoch, OperationId = batch.OperationId, ExpectedRequest = expected
        }, token);
        CheckedSchematicContract.ValidateResult(expected, receipt, inspect: true);
        return receipt.Status switch
        {
            CheckedSchematicBatchStatus.CsbsCompleted => new("completed", receipt),
            CheckedSchematicBatchStatus.CsbsNotFound => new("not-found", null),
            CheckedSchematicBatchStatus.CsbsRejected => new("rejected", receipt),
            _ => new("indeterminate", receipt)
        };
    }

    private static AutomationException Indeterminate(Guid operationId, DesignRecoveryState state) =>
        Error("native_operation_indeterminate", $"KiCad reports the native edit {state.PendingMutation!.OperationId} of operation "
            + $"{operationId:D} as indeterminate: it may have run. Inspect KiCad with kicad_schematic_checked_state; if it shows the design "
            + "as it was before the operation, choose discard. Nothing was changed.");

    private static NativeClient Client(InstanceRegistry? registry, string instanceId, string epoch, Guid operationId)
    {
        if (registry is null) throw Error("instance_registry_unavailable", "Resolving a pending operation requires the service instance registry.");
        try { return registry.Client(instanceId); }
        catch (AutomationException error) when (error.Code == "unknown_instance")
        {
            throw Error("instance_not_attached", $"KiCad instance {instanceId} is not attached to this server. Attach the KiCad that holds "
                + $"operation {operationId:D} (process epoch {epoch}) with kicad_instance_attach, then call again; nothing was changed.");
        }
        catch (AutomationException error) when (error.Code == "instance_exited")
        {
            throw Error("operation_process_ended", $"{error.Message} Operation {operationId:D} belongs to that KiCad: release it with "
                + "kicad_design_recovery_release_exited. Nothing was changed.");
        }
    }

    private static async Task<CheckedSchematicState> Capture(NativeClient client, DesignRecoveryState state, CancellationToken token)
    {
        var root = state.Baseline.Schematic.Document;
        var result = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        CheckedSchematicContract.ValidateObservation(result, root, client.Epoch);
        return result;
    }

    // The same objects in both directions: nothing to create, change or remove.
    private static bool Same(Kiapi.Schematic.Types.SchematicHierarchyData a, Kiapi.Schematic.Types.SchematicHierarchyData b, CancellationToken token)
    {
        try { return SchematicHierarchyDelta.Plan(a, b, token).Count == 0 && SchematicHierarchyDelta.Plan(b, a, token).Count == 0; }
        catch (AutomationException) { return false; }
    }

    // The same groups of connected items on every sheet (net names are KiCad's and may be renumbered).
    private static bool SameConnections(SchematicElectricalState? expected, SchematicElectricalState actual)
    {
        if (expected is null) return true;
        static string[] Groups(SchematicElectricalState state) => state.Nets
            .Select(net => string.Join('\n', net.Sheets.SelectMany(sheet => sheet.Items.Select(item =>
                string.Join('/', sheet.Path.Path.Select(id => id.Value)) + "#" + item.Value)).Order(StringComparer.Ordinal)))
            .Where(group => group.Length != 0).Order(StringComparer.Ordinal).ToArray();
        return Groups(expected).SequenceEqual(Groups(actual));
    }

    private static ApplySchematicItemBatch Batch(DocumentLifecycleState checkpoint, Guid originId, Guid operationId, string description,
        IEnumerable<SchematicItemOperation> operations)
    {
        var batch = new ApplySchematicItemBatch { Document = checkpoint.Document.Clone(), DocumentEpoch = checkpoint.Revision.Epoch,
            ExpectedRevision = checkpoint.Revision.Clone(), OperationId = operationId.ToString("D"),
            OriginId = originId.ToString("D"), Description = description };
        batch.Operations.Add(operations.Select(operation => operation.Clone()));
        return batch;
    }

    // Name-based identities (RFC 9562 version 8, SHA-256), so a repeated resolution names the same undo and continuation.
    internal static Guid UndoOperation(Guid operationId, string nativeOperationId) => Named("kicad-undo:" + operationId.ToString("D") + ":" + nativeOperationId);
    internal static Guid KeepOperation(Guid operationId, string processEpoch) => Named("kicad-keep:" + operationId.ToString("D") + ":" + processEpoch);

    private static Guid Named(string name)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    private static string Describe(IEnumerable<string> items)
    {
        var list = items.Distinct(StringComparer.Ordinal).ToArray();
        return string.Join("; ", list.Take(6)) + (list.Length > 6 ? $"; and {list.Length - 6} more" : "");
    }

    private static FileStream AutomaticOwnership(DesignRecoveryStore store)
    {
        string path = store.StatePath + ".automatic.lock";
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Error("invalid_automatic_sync_lock", "Automatic ownership lock paths cannot be redirected.");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            throw Error("automatic_sync_ownership_conflict", "An automatic synchronization owns this recovery record; stop it "
                + "(kicad_design_automatic_sync_stop), resolve the operation, then start it again. Nothing was changed.");
        }
    }

    private static IDisposable Writer(DesignRecoveryStore store)
    {
        try { return new DesignSynchronizationReceipts(store.StatePath).Acquire(); }
        catch (AutomationException error) when (error.Code == "synchronization_busy")
        {
            throw Error("synchronization_busy", "Another synchronization is running on this recovery record; wait until it ends, then "
                + "read the record again. Nothing was changed.");
        }
    }

    private static AutomationException Error(string code, string message) => new(code, message);
}
