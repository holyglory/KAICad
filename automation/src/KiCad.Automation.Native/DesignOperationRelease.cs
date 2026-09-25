using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using NativeRevision = KiCad.Automation.Model.DocumentRevision;

namespace KiCad.Automation.Native;

/// <summary>One native design file that the save of a released operation named, as it was on disk when
/// the operation was released. Replaced means the interrupted save had already put a new version in place:
/// the file's bytes (or its existence) differ from the version the save started from.</summary>
public sealed record DesignReleasedFile(string Path, bool ExistedBefore, string? Sha256Before, bool Exists, string? Sha256Now)
{
    public bool Replaced => Exists != ExistedBefore || (Exists && Sha256Now != Sha256Before);
}

/// <summary>The whole synchronization operation that a KiCad process left pending when it ended, kept when
/// the operation was released (decision nd2e75380e7f8aa7f): its native edit, the native state it was
/// guarded by, the save KiCad may have been cut off in, the candidate and publication or layout intent, and
/// which native files that save had already replaced. Later reconciliation reads it to recognise a partial
/// result as this operation's, never as a user edit. BaselineSha256 and DesiredSha256 identify the record's
/// baseline design and desired XML at the release, which the release did not change.</summary>
public sealed record DesignReleasedOperation(
    [property: JsonRequired] int SchemaVersion,
    Guid? OperationId,
    string? NativeOperationId,
    [property: JsonRequired] Guid InstanceId,
    [property: JsonRequired] Guid OriginId,
    [property: JsonRequired] string ProcessEpoch,
    [property: JsonRequired] InstanceExit Exit,
    [property: JsonRequired] string ReleasedFromRevisionToken,
    [property: JsonRequired] NativeRevision NativeRevision,
    [property: JsonRequired] string BaselineSha256,
    [property: JsonRequired] string DesiredSha256,
    byte[]? PendingMutation,
    [property: JsonRequired] byte[] PendingNativeState,
    byte[]? PendingNativeSave,
    byte[]? PendingCandidateFileBytes,
    DesignPublicationIntent? PendingPublication,
    DesignLayoutIntent? PendingLayout,
    [property: JsonRequired] IReadOnlyList<DesignReleasedFile> Files,
    [property: JsonRequired] DateTimeOffset ReleasedAt)
{
    public const int CurrentSchemaVersion = 1;

    public bool AnyFileReplaced => Files.Any(file => file.Replaced);

    public ApplySchematicItemBatch? Mutation() => PendingMutation is null ? null : ApplySchematicItemBatch.Parser.ParseFrom(PendingMutation);
    public DocumentLifecycleState NativeState() => DocumentLifecycleState.Parser.ParseFrom(PendingNativeState);
    public CheckedSaveDocument? NativeSave() => PendingNativeSave is null ? null : CheckedSaveDocument.Parser.ParseFrom(PendingNativeSave);

    /// <summary>The receipt of releasing the pending operation of <paramref name="saved"/> on the proven
    /// exit of its process. Reads the native files the operation's save named, to record which the save
    /// had already replaced; nothing is written here.</summary>
    internal static DesignReleasedOperation Create(StoredDesignRecovery saved, InstanceExit exit)
    {
        var state = saved.State;
        var files = new List<DesignReleasedFile>();
        foreach (var baseline in state.PendingNativeSave?.ExpectedState?.FileBaselines ?? [])
        {
            if (string.IsNullOrWhiteSpace(baseline.Path) || !System.IO.Path.IsPathFullyQualified(baseline.Path)) continue;
            bool before = baseline.BaselineKnown && baseline.BaselineExists;
            string? now = null;
            try { now = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(baseline.Path))); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            files.Add(new(baseline.Path, before, before ? baseline.BaselineSha256.ToLowerInvariant() : null, now is not null, now));
        }
        return new(CurrentSchemaVersion, state.PendingPublication?.OperationId ?? state.PendingLayout?.OperationId,
            state.PendingMutation?.OperationId, state.InstanceId, state.OriginId, state.PendingNativeState!.ProcessEpoch, exit,
            saved.RevisionToken, state.NativeRevision, BaselineDigest(state), Sha(state.DesiredFileBytes),
            state.PendingMutation?.ToByteArray(), state.PendingNativeState.ToByteArray(), state.PendingNativeSave?.ToByteArray(),
            state.PendingCandidateFileBytes, state.PendingPublication, state.PendingLayout, files, DateTimeOffset.UtcNow);
    }

    internal static string BaselineDigest(DesignRecoveryState state) =>
        Sha(Encoding.UTF8.GetBytes(SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries)));

    internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>Release receipts kept next to a design recovery record, in the folder
/// "&lt;record&gt;.released": one immutable file per released operation and process epoch.</summary>
public static class DesignReleasedOperations
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Directory(string recoveryPath) => Path.GetFullPath(recoveryPath) + ".released";

    /// <summary>Writes the receipt unless one exists for the same operation and process epoch: the first
    /// release keeps what it saw. Returns the receipt's path.</summary>
    internal static string Write(string recoveryPath, DesignReleasedOperation receipt)
    {
        string folder = System.IO.Directory.CreateDirectory(Directory(recoveryPath)).FullName;
        string destination = Path.Combine(folder, Name(receipt.OperationId, receipt.NativeOperationId, receipt.ProcessEpoch));
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

    /// <summary>The most recent release receipt of one operation of this record, or null when it was never
    /// released. An operation resumed on a new KiCad and released again has one receipt per ended process.</summary>
    public static (DesignReleasedOperation Receipt, string Path)? Latest(string recoveryPath, Guid operationId, Guid instanceId)
    {
        string folder = Directory(recoveryPath);
        if (!System.IO.Directory.Exists(folder)) return null;
        (DesignReleasedOperation Receipt, string Path)? latest = null;
        foreach (string file in System.IO.Directory.EnumerateFiles(folder, operationId.ToString("N") + ".*.json"))
        {
            var receipt = Read(file);
            if (receipt.OperationId != operationId || receipt.InstanceId != instanceId)
                throw new AutomationException("invalid_release_receipt", $"The release receipt {file} does not belong to this operation and instance.");
            if (latest is null || receipt.ReleasedAt > latest.Value.Receipt.ReleasedAt) latest = (receipt, file);
        }
        return latest;
    }

    /// <summary>The receipts still open on <paramref name="state"/>: operations of this instance released while the
    /// record sat on the document session it still sits on. The release does not change that session and only the
    /// continuation moves the record off it (KiCad gives every loaded document a new session), so such an operation
    /// has not been continued yet. An unreadable receipt fails the check rather than being skipped.</summary>
    internal static IReadOnlyList<DesignReleasedOperation> OpenOn(string recoveryPath, DesignRecoveryState state)
    {
        string folder = Directory(recoveryPath);
        if (!System.IO.Directory.Exists(folder)) return [];
        return System.IO.Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal).Select(Read)
            .Where(receipt => receipt.InstanceId == state.InstanceId && receipt.NativeRevision.Epoch == state.NativeRevision.Epoch)
            .ToArray();
    }

    internal static string Describe(DesignReleasedOperation receipt) =>
        receipt.OperationId?.ToString("D") ?? "(native edit " + receipt.NativeOperationId + ")";

    public static DesignReleasedOperation Read(string path)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<DesignReleasedOperation>(File.ReadAllBytes(path), Json);
            if (receipt is null || receipt.SchemaVersion != DesignReleasedOperation.CurrentSchemaVersion || receipt.Files is null
                || receipt.Exit is null || receipt.Exit.Epoch != receipt.ProcessEpoch || receipt.PendingNativeState is null)
                throw new AutomationException("invalid_release_receipt", $"The release receipt {path} is incomplete.");
            _ = receipt.Mutation(); _ = receipt.NativeState(); _ = receipt.NativeSave();
            return receipt;
        }
        catch (Exception error) when (error is JsonException or InvalidProtocolBufferException)
        { throw new AutomationException("invalid_release_receipt", $"The release receipt {path} cannot be read: {error.Message}"); }
    }

    private static string Name(Guid? operationId, string? nativeOperationId, string epoch)
    {
        string operation = operationId?.ToString("N")
            ?? "native-" + DesignReleasedOperation.Sha(Encoding.UTF8.GetBytes(nativeOperationId ?? ""))[..32];
        string process = Guid.TryParseExact(epoch, "D", out var parsed) ? parsed.ToString("N")
            : DesignReleasedOperation.Sha(Encoding.UTF8.GetBytes(epoch))[..32];
        return operation + "." + process + ".json";
    }
}

public enum ExitedOperationContinuation { Resume, RollBack }

/// <summary>What releasing an exited operation did. Outcome: "released" (released, but no KiCad runs for the
/// instance yet, so nothing continues), "resume-pending" (the rest of the released operation is journaled on the
/// running KiCad), "roll-back-pending" (an operation that restores the last synchronized design, in KiCad and in
/// the XML file, is journaled on the running KiCad), "replan" (nothing survived of an operation that had no final
/// candidate; the next synchronization plans the XML again), "completed" (the resumed operation completed) or
/// "rolled-back" (the roll-back completed). A pending continuation completes through the ordinary synchronization
/// executor: kicad_design_sync_apply with ContinuationOperationId and its requested recovery revision token, or
/// automatic synchronization, which completes pending work first. OperationSheets are the sheet paths the operation
/// changes; SheetsWithOperationResult those the running KiCad already holds exactly as the operation left them; and
/// SheetsWithOtherEdits the sheets the operation does not change that the running KiCad holds with other edits (for
/// example a user's note), which the continuation leaves as they are.</summary>
public sealed record ExitedOperationReleaseResult(StoredDesignRecovery Recovery, DesignReleasedOperation Receipt, string ReceiptPath,
    bool ReleasedNow, string Outcome, string? ContinuedEpoch, Guid? ContinuationOperationId, IReadOnlyList<string> SheetsWithOperationResult,
    int NativeOperations, string NextStep, IReadOnlyList<string> OperationSheets, IReadOnlyList<string> SheetsWithOtherEdits);

/// <summary>A continuation refused once the operation was released, by this call (ReleasedNow) or an earlier one. The
/// operation stays released and nothing else changed: KiCad, its files and the XML file are as they were. The message
/// names the way out, the release receipt and the recovery record's current revision token, which the caller passes when
/// it calls again. Refusal is the original error: an AutomationException with its code, or a native or file error.</summary>
public sealed class ExitedOperationRefusal(Exception refusal, string message, bool releasedNow, string receiptPath,
    string? recoveryRevisionToken, string releasedEpoch) : Exception(message, refusal)
{
    public Exception Refusal { get; } = refusal;
    public bool ReleasedNow { get; } = releasedNow;
    public string ReceiptPath { get; } = receiptPath;
    public string? RecoveryRevisionToken { get; } = recoveryRevisionToken;
    public string ReleasedEpoch { get; } = releasedEpoch;
}

/// <summary>Releases a synchronization operation whose KiCad process is proven to have ended, and continues the
/// design in the KiCad started again for the same instance. A partial result is recognised only sheet by sheet
/// against the release receipt. The operation's own sheets (those its result changes, or its native edit targets) must
/// each be either as they were at the last synchronization (the baseline) or exactly as the operation left them (its
/// candidate); anything else there is refused with the way out, so a user edit is never taken for the operation's result,
/// removed or overwritten. Other sheets may hold other edits, such as a user's note made in the KiCad started again: the
/// continuation leaves them exactly as KiCad holds them and publishes them with its XML, as an ordinary synchronization
/// would. It carries only edits that change neither the engineering design nor any connection (notes, text boxes,
/// drawings, images, tables, title block and page settings); anything else on those sheets is refused with the way out,
/// since it has to be reconciled with the XML design first. The sheets' load-format provenance (the file format version
/// KiCad read them in) is not content: a KiCad started again reads files the dead one wrote, so it is taken from the
/// running KiCad. The continuation is journaled in the recovery record as an ordinary pending synchronization of the
/// running process, so it is completed, and survives interruption, exactly like any other: resume completes the
/// released operation itself with only the part KiCad does not hold yet; roll-back removes the partial result and
/// publishes the last synchronized design, keeping the XML it replaces as the previous version. Every refusal after the
/// release names the receipt and the record's current revision token (ExitedOperationRefusal).</summary>
public static class ExitedOperationRelease
{
    public static async Task<ExitedOperationReleaseResult> ReleaseAsync(DesignRecoveryStore store, InstanceRegistry registry,
        string instanceId, string expectedRevisionToken, Guid operationId, ExitedOperationContinuation continuation,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(instanceId, "D", out var instance) || instance == Guid.Empty)
            throw Error("invalid_instance_id", "Specify the saved instance ID.");
        if (operationId == Guid.Empty) throw Error("invalid_operation_id", "Specify the pending operation's UUID.");
        var saved = store.Read() ?? throw Error("missing_design_recovery", "No saved recovery record exists.");
        if (saved.State.InstanceId != instance)
            throw Error("recovery_instance_mismatch", "The record belongs to a different instance.");
        if (saved.RevisionToken != expectedRevisionToken)
            throw Error("design_recovery_changed", "Recovery changed; read the current record before releasing its operation.");

        var latest = DesignReleasedOperations.Latest(store.StatePath, operationId, instance);
        Guid? rollbackId = latest is { } released ? RollbackOperation(released.Receipt) : null;
        ProvenInstanceExit? exit = null;
        if (saved.State.HasPendingWork)
        {
            Guid? pending = saved.State.PendingPublication?.OperationId ?? saved.State.PendingLayout?.OperationId;
            string? epoch = saved.State.PendingNativeState?.ProcessEpoch;
            // A repeated call after the operation was already continued on the running KiCad.
            if (latest is { } earlier && epoch is not null && earlier.Receipt.ProcessEpoch != epoch
                && LiveClient(registry, instanceId)?.Epoch == epoch && (pending == operationId || pending == rollbackId))
                return new(saved, earlier.Receipt, earlier.Path, false, pending == operationId ? "resume-pending" : "roll-back-pending", epoch,
                    pending, [], saved.State.PendingMutation?.Operations.Count ?? 0, CompleteStep(saved.State.PendingPublication!), [], []);
            if (pending != operationId)
                throw Error("released_operation_mismatch", $"The recovery record holds operation {pending?.ToString("D") ?? "(a native edit without a synchronization ID)"}, not {operationId:D}; nothing was released.");
            if (epoch is null)
                throw Error("operation_process_unknown", "The pending operation does not record which KiCad process holds it; inspect it with kicad_design_recovery_plan instead.");
            // Proven before anything changes, and without contacting KiCad (which may be stopped).
            exit = await ProvenInstanceExit.ProveAsync(registry, instanceId, epoch, token)
                ?? throw Error("operation_exit_unproven", $"Operation {operationId:D} belongs to KiCad process epoch {epoch}, and this server cannot prove that process has ended: it may still be running (possibly stopped), or this server cannot observe it, for example because it was attached from another process ID namespace or its registration was written on another computer. Nothing was released. Stop that KiCad, or wait until it has ended, then call again.");
        }
        else if (latest is { } done && (saved.State.LastSynchronization?.OperationId == operationId || saved.State.LastSynchronization?.OperationId == rollbackId))
        {
            bool completed = saved.State.LastSynchronization!.OperationId == operationId;
            return new(saved, done.Receipt, done.Path, false, completed ? "completed" : "rolled-back", null, saved.State.LastSynchronization.OperationId, [], 0,
                completed ? "The released operation completed on the KiCad started again and its XML was published; nothing is left to release."
                    : "The roll-back completed: KiCad and the XML file hold the last synchronized design, and the replaced XML was kept as the previous version.",
                [], []);
        }
        // An automatic worker for this record would plan and apply while this runs; hold its ownership lock.
        using var ownership = AutomaticOwnership(store);
        if (store.Read()?.RevisionToken != saved.RevisionToken)
            throw Error("design_recovery_changed", "Recovery changed while taking ownership of the record; read it again.");
        bool releasedNow = exit is not null;
        if (exit is not null) saved = store.ReleaseExitedOperation(saved, exit);
        var (receipt, receiptPath) = DesignReleasedOperations.Latest(store.StatePath, operationId, instance)
            ?? throw Error("no_released_operation", $"The recovery record holds no pending operation {operationId:D}, and it was never released.");
        // From here on the operation is released: a refusal says so, and names the receipt and the record's current token.
        try { return await ContinueAsync(store, registry, instanceId, saved, receipt, receiptPath, releasedNow, continuation, token); }
        catch (Exception error) when (error is AutomationException or IOException or UnauthorizedAccessException or ArgumentException)
        { throw Refused(store, receipt, receiptPath, releasedNow, error); }
    }

    private static async Task<ExitedOperationReleaseResult> ContinueAsync(DesignRecoveryStore store, InstanceRegistry registry,
        string instanceId, StoredDesignRecovery saved, DesignReleasedOperation receipt, string receiptPath, bool releasedNow,
        ExitedOperationContinuation continuation, CancellationToken token)
    {
        if (!releasedNow && DesignReleasedOperation.BaselineDigest(saved.State) != receipt.BaselineSha256)
            throw Error("released_operation_superseded", "The design was synchronized again after this operation was released; its candidate no longer applies. Plan the XML afresh.");

        // Only the continuation moves the record off the document session the operation was released from. A resumed or
        // rolled-back continuation ends in its own completion (handled above); one that journaled nothing was a re-plan.
        if (!releasedNow && saved.State.NativeRevision.Epoch != receipt.NativeRevision.Epoch)
            return receipt.PendingPublication is null
                ? new ExitedOperationReleaseResult(saved, receipt, receiptPath, false, "replan", null, null, [], 0,
                    "The operation was continued already: nothing of it had reached KiCad's files, so the record was attached to the KiCad started again and the next synchronization plans the XML again from the baseline.", [], [])
                : throw Error("released_operation_superseded", "This operation was continued already, and the record has moved on since (it was synchronized again, or attached to another KiCad document session); nothing is left to release or continue. Read the record with kicad_design_recovery_plan to see where it stands.");

        var client = LiveClient(registry, instanceId);
        if (client is null || client.Epoch == receipt.ProcessEpoch)
            return new(saved, receipt, receiptPath, releasedNow, "released", null, null, [], 0,
                "Start KiCad again for this project (kicad_instance_start, or attach a KiCad started with this instance ID), then call this tool again with the returned recovery revision token to resume or roll back. Until then the record stays on the ended KiCad's session: an ordinary reattachment is refused (released_operation_requires_continuation), because the KiCad started again may hold part of the operation's result.",
                [], []);

        var live = await Capture(client, saved.State, token);
        var liveData = live.Electrical.Hierarchy.Data;
        var state = saved.State;
        var baseline = state.Baseline with { Schematic = WithProvenance(state.Baseline.Schematic, liveData) };
        SchematicDesign? candidate = receipt.PendingPublication is { } publication
            ? SchematicDesignXml.Read(new UTF8Encoding(false, true).GetString(publication.CandidateFileBytes), state.KnowledgeLibraries) : null;
        if (candidate is not null) candidate = candidate with { Schematic = WithProvenance(candidate.Schematic, liveData) };
        string operation = DesignReleasedOperations.Describe(receipt);
        var sheets = ReleasedSheets.Compare(liveData, baseline.Schematic, candidate?.Schematic, receipt, token);
        sheets.RequireOperationSheets(operation, receipt);
        // Resume with a candidate and roll-back publish XML over the other edits KiCad holds; a re-plan leaves them to the
        // next ordinary synchronization, which reconciles any edit.
        if (continuation == ExitedOperationContinuation.RollBack || receipt.PendingPublication is not null)
            sheets.RequireCarried(operation);
        string designPath = receipt.PendingPublication?.DesignPath ?? receipt.PendingLayout?.DesignPath
            ?? throw Error("released_operation_not_resumable", "The released operation names no XML file; inspect the recovery record instead.");
        byte[] xml;
        try { xml = await File.ReadAllBytesAsync(designPath, token); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw Error("design_file_changed", $"The XML file {designPath} no longer exists; restore it (the version the recovery record holds has SHA-256 {receipt.DesiredSha256}) before continuing."); }

        if (continuation == ExitedOperationContinuation.RollBack)
        {
            if (!xml.AsSpan().SequenceEqual(state.DesiredFileBytes))
                throw Error("design_file_changed", RollbackFileMessage(operation, designPath, receipt.DesiredSha256, receipt.PendingPublication, xml));
            // The record attached to the running KiCad; saved together with the continuation in one step, so no record
            // ever sits on the new session without it.
            var attached = await Attached(client, saved, live, token);
            var restoredDesign = baseline with { Schematic = sheets.WithOtherEdits(baseline.Schematic, liveData) };
            var restore = SchematicHierarchyDelta.Plan(liveData, restoredDesign.Schematic, token);
            sheets.RequireWithinOperation(restore);
            byte[] restored = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(restoredDesign, state.KnowledgeLibraries));
            var intent = DesignPublicationIntent.Create(designPath, xml, restored, RollbackOperation(receipt), saved.RevisionToken);
            saved = store.ContinueReleasedOperation(attached with
            {
                PendingMutation = restore.Count == 0 ? null : Batch(live.State, state.OriginId,
                    $"Roll back synchronization {operation} left by KiCad process epoch {receipt.ProcessEpoch}", restore),
                PendingNativeState = live.State.Clone(), PendingNativeSave = null, PendingCandidateFileBytes = null, PendingPublication = intent
            }, saved.RevisionToken, receipt);
            return new(saved, receipt, receiptPath, releasedNow, "roll-back-pending", client.Epoch, intent.OperationId, sheets.WithResult, restore.Count,
                CompleteStep(intent) + " It removes the operation's partial result from KiCad, saves those sheets and publishes the last synchronized design; the XML file it replaces is kept as the previous version."
                + sheets.KeptEdits(), sheets.OperationSheets, sheets.OtherEdits);
        }

        if (receipt.PendingPublication is not { } resumed)
        {
            if (sheets.WithResult.Count != 0)
                throw Error("released_operation_not_resumable", "The operation had no final XML candidate yet (it was still resolving its native layout), so its result cannot be resumed; roll it back instead (continuation 'roll-back').");
            saved = store.ContinueReleasedOperation(await Attached(client, saved, live, token), saved.RevisionToken, receipt);
            return new(saved, receipt, receiptPath, releasedNow, "replan", client.Epoch, null, [], 0,
                "Nothing of the operation reached KiCad's files. The record is attached to the KiCad started again, and the next synchronization plans the XML again from the baseline."
                + (sheets.OtherEdits.Count == 0 ? "" : $" The other edits KiCad holds on {sheets.Names(sheets.OtherEdits)} are ordinary edits for that synchronization."),
                sheets.OperationSheets, sheets.OtherEdits);
        }
        if (resumed.Phase != DesignPublicationPhase.Prepared)
            throw Error("released_publication_started", PublicationStartedMessage(operation, resumed, receipt.DesiredSha256, state.DesiredFileBytes, xml));
        if (!xml.AsSpan().SequenceEqual(resumed.ExpectedFileBytes) || !state.DesiredFileBytes.AsSpan().SequenceEqual(resumed.ExpectedFileBytes))
            throw Error("released_operation_input_changed", InputChangedMessage(operation, resumed, state.DesiredFileBytes, xml));
        var attachedForResume = await Attached(client, saved, live, token);
        // The operation's result, with the sheets it does not change exactly as KiCad holds them. Only what KiCad does not
        // hold yet is applied: sheets that already hold the operation's result, and every other sheet, are left as they are.
        var scoped = candidate! with { Schematic = sheets.WithOtherEdits(candidate.Schematic, liveData) };
        var remainder = SchematicHierarchyDelta.Plan(liveData, scoped.Schematic, token);
        sheets.RequireWithinOperation(remainder);
        var continued = DesignPublicationIntent.Create(resumed.DesignPath, resumed.ExpectedFileBytes,
            Encoding.UTF8.GetBytes(SchematicDesignXml.Write(scoped, state.KnowledgeLibraries)), resumed.OperationId, resumed.RequestedRecoveryRevisionToken);
        saved = store.ContinueReleasedOperation(attachedForResume with
        {
            PendingMutation = remainder.Count == 0 ? null : Batch(live.State, state.OriginId,
                $"Resume synchronization {operation} left by KiCad process epoch {receipt.ProcessEpoch}", remainder),
            PendingNativeState = live.State.Clone(), PendingNativeSave = null, PendingCandidateFileBytes = null, PendingPublication = continued
        }, saved.RevisionToken, receipt);
        return new(saved, receipt, receiptPath, releasedNow, "resume-pending", client.Epoch, continued.OperationId, sheets.WithResult, remainder.Count,
            CompleteStep(continued) + " Only the part KiCad does not hold yet is applied; its XML candidate records the sheet files as this KiCad loaded them."
            + sheets.KeptEdits(), sheets.OperationSheets, sheets.OtherEdits);
    }

    private static string CompleteStep(DesignPublicationIntent publication) =>
        $"Operation {publication.OperationId:D} waits on the running KiCad. Complete it with kicad_design_sync_apply (operationId {publication.OperationId:D}, expectedRevisionToken {publication.RequestedRecoveryRevisionToken}), or start automatic synchronization, which completes it first.";

    // A refusal once the operation is released: the original error, with what the release did and what to call again with.
    private static ExitedOperationRefusal Refused(DesignRecoveryStore store, DesignReleasedOperation receipt, string receiptPath,
        bool releasedNow, Exception error)
    {
        string? current = null;
        try { current = store.Read()?.RevisionToken; }
        catch (AutomationException) { }
        string operation = DesignReleasedOperations.Describe(receipt);
        string token = current is null ? "" : $"; the recovery record's revision token is now {current}";
        string context = releasedNow
            ? $" Operation {operation} was released by this call, since KiCad process epoch {receipt.ProcessEpoch} has ended: its receipt is {receiptPath}{token}. This call changed nothing else: KiCad, its files and the XML file are as they were. Call again with that token once the cause is resolved."
            : $" Operation {operation} stays released (receipt {receiptPath}{token}). This call changed nothing: the recovery record, KiCad, its files and the XML file are as they were.";
        return new(error, error.Message + context, releasedNow, receiptPath, current, receipt.ProcessEpoch);
    }

    // Why a roll-back cannot replace the XML file as it is, and the way out.
    // desiredSha256 identifies the XML the recovery record holds, which the operation started from; publication is the
    // operation's XML publication, if it had one.
    internal static string RollbackFileMessage(string operation, string designPath, string desiredSha256, DesignPublicationIntent? publication,
        byte[] xml)
    {
        string startedFrom = $"the version the recovery record holds, which operation {operation} started from (SHA-256 {desiredSha256})";
        string kept = publication is null ? "" : KeptAt(publication, desiredSha256);
        if (publication is not null && xml.AsSpan().SequenceEqual(publication.CandidateFileBytes))
            return $"The XML file {designPath} already holds operation {operation}'s own result: its publication had put it in place before KiCad ended. A roll-back replaces only {startedFrom}, so that the XML history stays exact. Put that version back as {designPath}{kept}, then call again with continuation 'roll-back'.";
        return $"The XML file {designPath} is not {startedFrom}: it changed after the recovery record last read it, and rolling back would overwrite that change. Put that version of the XML back{kept}, call again to roll back, then make the change again.";
    }

    // Why a started publication cannot be resumed, and the continuation that does work.
    internal static string PublicationStartedMessage(string operation, DesignPublicationIntent resumed, string desiredSha256,
        byte[] desired, byte[] xml)
    {
        string refused = $"Operation {operation}'s XML publication had reached phase {resumed.Phase} when KiCad ended, so it cannot be resumed: its save had completed and the XML file may already have been replaced. ";
        if (xml.AsSpan().SequenceEqual(desired))
            return refused + "Roll it back instead (continuation 'roll-back'): the XML file still holds the version the operation started from, so the roll-back removes the operation's result from KiCad, publishes the last synchronized design and keeps that version as the previous XML. Then make the XML change again.";
        return refused + RollbackFileMessage(operation, resumed.DesignPath, desiredSha256, resumed, xml);
    }

    // Why the XML no longer matches a resumed operation's input, and the continuation that does work.
    internal static string InputChangedMessage(string operation, DesignPublicationIntent resumed, byte[] desired, byte[] xml)
    {
        string changed = $"The XML changed after operation {operation} started: the file is not the version it started from (SHA-256 {DesignReleasedOperation.Sha(resumed.ExpectedFileBytes)}), so its result no longer matches the XML. ";
        return changed + (xml.AsSpan().SequenceEqual(desired)
            ? "Roll it back instead (continuation 'roll-back'): the roll-back publishes the last synchronized design and keeps the current XML as the previous version; then make the XML change again."
            : $"Put that version back as {resumed.DesignPath}, call again to resume or roll back, and make the later change again afterwards.");
    }

    private static string KeptAt(DesignPublicationIntent publication, string sha256)
    {
        foreach (string path in new[] { publication.PreviousPath, publication.StagedPath }.Distinct(StringComparer.Ordinal))
        {
            try { if (DesignReleasedOperation.Sha(File.ReadAllBytes(path)) == sha256) return $" (that version is kept at {path})"; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return "";
    }

    // The roll-back of one released operation, named after that operation and the process it was released from, so a
    // repeated call recognises the roll-back it journaled.
    internal static Guid RollbackOperation(DesignReleasedOperation receipt)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("kicad-roll-back:" + receipt.OperationId?.ToString("D") + ":" + receipt.ProcessEpoch));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80); // RFC 9562 version 8 (name-based, SHA-256)
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    // A copy of target whose sheets carry the load-format provenance of the same sheets in the running KiCad.
    private static SchematicHierarchyData WithProvenance(SchematicHierarchyData target, SchematicHierarchyData live)
    {
        var loaded = live.Instances.ToDictionary(screen => ReleasedSheets.Key(screen.Metadata.Document), screen => screen.Metadata, StringComparer.Ordinal);
        var result = target.Clone();
        foreach (var screen in result.Instances)
            if (loaded.TryGetValue(ReleasedSheets.Key(screen.Metadata.Document), out var metadata))
            {
                screen.Metadata.LoadedNativeFormatVersion = metadata.LoadedNativeFormatVersion;
                screen.Metadata.WriterNativeFormatVersion = metadata.WriterNativeFormatVersion;
            }
        return result;
    }

    // The record as attached to the running KiCad: the same checks as kicad_design_recovery_reattach (the KiCad names
    // the record's instance; the observation is of its root, in its process epoch, and never regresses within a
    // session), without saving it. Pending requests are never carried over and the baseline does not move.
    private static async Task<DesignRecoveryState> Attached(NativeClient client, StoredDesignRecovery saved, CheckedSchematicState live,
        CancellationToken token)
    {
        var session = await client.HandshakeAsync(token);
        if (session.InstanceId != saved.State.InstanceId.ToString("D"))
            throw Error("recovery_instance_mismatch", "The running KiCad is not the instance recorded by this design.");
        var revision = live.State.Revision;
        if (revision.Epoch == saved.State.NativeRevision.Epoch && revision.Sequence < saved.State.NativeRevision.Sequence)
            throw Error("invalid_recovery_revision", "The native document session regressed.");
        _ = SchematicDataXml.Read(SchematicDataXml.Write(live.Electrical.Hierarchy.Data));
        return saved.State with
        {
            Observed = live.Electrical.Hierarchy.Data.Clone(), ObservedElectrical = live.Electrical.Clone(),
            NativeRevision = new(revision.Epoch, revision.Sequence), TrackingComplete = live.Electrical.Hierarchy.TrackingComplete,
            HierarchyResolution = null, OwnershipResolution = null
        };
    }

    private static NativeClient? LiveClient(InstanceRegistry registry, string instanceId)
    {
        try { return registry.Client(instanceId); }
        catch (AutomationException error) when (error.Code is "instance_exited" or "unknown_instance") { return null; }
    }

    private static async Task<CheckedSchematicState> Capture(NativeClient client, DesignRecoveryState state, CancellationToken token)
    {
        var root = state.Baseline.Schematic.Document;
        var result = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        CheckedSchematicContract.ValidateObservation(result, root, client.Epoch);
        return result;
    }

    private static ApplySchematicItemBatch Batch(DocumentLifecycleState checkpoint, Guid originId, string description,
        IEnumerable<SchematicItemOperation> operations)
    {
        var batch = new ApplySchematicItemBatch { Document = checkpoint.Document.Clone(), DocumentEpoch = checkpoint.Revision.Epoch,
            ExpectedRevision = checkpoint.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"),
            OriginId = originId.ToString("D"), Description = description };
        batch.Operations.Add(operations.Select(operation => operation.Clone()));
        return batch;
    }

    private static FileStream AutomaticOwnership(DesignRecoveryStore store)
    {
        string path = store.StatePath + ".automatic.lock";
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Error("invalid_automatic_sync_lock", "Automatic ownership lock paths cannot be redirected.");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        { throw Error("automatic_sync_ownership_conflict", "An automatic synchronization owns this recovery record; stop it (kicad_design_automatic_sync_stop) before releasing its operation."); }
    }

    private static AutomationException Error(string code, string message) => new(code, message);
}

/// <summary>How the sheets a KiCad started again loaded compare with a released operation, by sheet path.
/// OperationSheets: the sheets the operation changes (its candidate differs from the last synchronized design there, or its
/// native edit targets them). WithResult: operation sheets KiCad holds exactly as the operation left them. Edited: operation
/// sheets KiCad holds as neither the synchronized version nor the operation's result. OtherEdits: sheets the operation does
/// not change that differ from the synchronized version. Uncarried: the edits on those sheets that a continuation cannot
/// publish unreconciled, because they could change the engineering design or a connection.</summary>
internal sealed record ReleasedSheets(string Root, IReadOnlyList<string> OperationSheets, IReadOnlyList<string> WithResult, IReadOnlyList<string> Edited,
    IReadOnlyList<string> OtherEdits, IReadOnlyList<string> Uncarried, IReadOnlyDictionary<string, string> SheetNames)
{
    internal static ReleasedSheets Compare(SchematicHierarchyData live, SchematicHierarchyData baseline, SchematicHierarchyData? candidate,
        DesignReleasedOperation receipt, CancellationToken token)
    {
        string root = Key(live.Document);
        var names = SheetNamesOf(candidate is null ? new[] { live, baseline } : new[] { live, baseline, candidate });
        IReadOnlyList<SchematicItemOperation> Delta(SchematicHierarchyData target, string name)
        {
            try { return SchematicHierarchyDelta.Plan(live, target, token); }
            catch (AutomationException error)
            {
                throw new AutomationException("released_operation_diverged", $"The running KiCad's design cannot be compared with the {name}: {error.Message}"
                    + MetadataDifferences(live, target) + " The way out: undo the edits in KiCad that changed these sheets or settings (Edit > Undo), or close the schematic without saving and open it again, then call again; make the edits again once the continuation has completed.");
            }
        }
        var toBaseline = Delta(baseline, "last synchronized design");
        var operationSheets = SheetsOfOperation(baseline, candidate, receipt, root, token);
        var fromBaseline = toBaseline.Select(operation => Target(operation, root)).ToHashSet(StringComparer.Ordinal);
        var fromCandidate = candidate is null ? fromBaseline
            : Delta(candidate, "released operation's result").Select(operation => Target(operation, root)).ToHashSet(StringComparer.Ordinal);
        string[] Sorted(IEnumerable<string> paths) => paths.Order(StringComparer.Ordinal).ToArray();
        var other = Sorted(fromBaseline.Where(path => !operationSheets.Contains(path)));
        var screens = live.Instances.ToDictionary(screen => Key(screen.Metadata.Document), StringComparer.Ordinal);
        var uncarried = new List<string>();
        foreach (var operation in toBaseline)
        {
            string path = Target(operation, root);
            if (operationSheets.Contains(path)) continue;
            string? edit = UncarriedEdit(operation, screens.TryGetValue(path, out var screen) ? SchematicItemDelta.Index(screen.Items) : new Dictionary<Guid, Google.Protobuf.IMessage>());
            if (edit is not null) uncarried.Add($"{edit} on {Name(names, path)}");
        }
        return new(root, Sorted(operationSheets), Sorted(fromBaseline.Where(path => operationSheets.Contains(path) && !fromCandidate.Contains(path))),
            Sorted(fromBaseline.Where(path => operationSheets.Contains(path) && fromCandidate.Contains(path))), other, uncarried, names);
    }

    // Edits on the operation's own sheets cannot be told from its partial result; a result that no save of it put there is
    // not its own either. Both are refused with the way out.
    internal void RequireOperationSheets(string operation, DesignReleasedOperation receipt)
    {
        if (Edited.Count != 0)
            throw new AutomationException("released_operation_sheets_edited", $"Operation {operation} changes {Names(OperationSheets)}. The running KiCad holds other edits on {Names(Edited)}: there they are neither as the last synchronization left them nor exactly as the operation left them, so neither resume nor roll-back can tell those edits from the operation's partial result, and continuing could remove or overwrite them. The way out: undo those edits in KiCad (Edit > Undo, or delete what was added), or, if they are not needed, close the schematic without saving and open it again; then call this tool again. Make the edits again once the continuation has completed: they are then synchronized as ordinary user edits.");
        if (WithResult.Count != 0 && !receipt.AnyFileReplaced)
            throw new AutomationException("released_operation_diverged", $"The running KiCad holds operation {operation}'s result on {Names(WithResult)}, although the interrupted save had replaced no file, so it did not come from that save (it may have been restored from an autosave, or made by hand). The way out: undo it in KiCad, or close the schematic without saving and open it again, then call this tool again.");
    }

    // Other sheets' edits go out with the continuation's XML exactly as KiCad holds them, which is what an ordinary
    // synchronization publishes only for edits that change neither the engineering design nor a connection.
    internal void RequireCarried(string operation)
    {
        if (Uncarried.Count != 0)
            throw new AutomationException("released_operation_edits_not_carried", $"The running KiCad holds edits on sheets that operation {operation} does not change which the continuation cannot carry: {string.Join("; ", Uncarried)}. A continuation keeps edits on such sheets and publishes them with its XML only when they are notes, text boxes, drawings, images, tables, or title-block and page-setting changes; components, wires, buses, labels, junctions, sheets, rule areas, groups, cached library symbols and project settings have to be reconciled with the XML design by an ordinary synchronization first. The way out: undo those edits in KiCad (Edit > Undo, or delete what was added; notes and drawings may stay), or close the schematic without saving and open it again; then call this tool again, and make the edits again once the continuation has completed: they are then synchronized as ordinary user edits.");
    }

    // The continuation's own native change stays on the operation's sheets.
    internal void RequireWithinOperation(IEnumerable<SchematicItemOperation> operations)
    {
        var outside = operations.Select(operation => Target(operation, Root)).Where(path => !OperationSheets.Contains(path)).Distinct().ToArray();
        if (outside.Length != 0)
            throw new AutomationException("released_operation_diverged", $"The continuation would change {Names(outside)}, which the operation does not change; nothing was journaled.");
    }

    // A copy of target whose sheets with other edits are exactly as the running KiCad holds them.
    internal SchematicHierarchyData WithOtherEdits(SchematicHierarchyData target, SchematicHierarchyData live)
    {
        if (OtherEdits.Count == 0) return target;
        var loaded = live.Instances.ToDictionary(screen => Key(screen.Metadata.Document), StringComparer.Ordinal);
        var result = target.Clone();
        for (int index = 0; index < result.Instances.Count; index++)
            if (OtherEdits.Contains(Key(result.Instances[index].Metadata.Document)))
                result.Instances[index] = loaded[Key(result.Instances[index].Metadata.Document)].Clone();
        return result;
    }

    internal string KeptEdits() => OtherEdits.Count == 0 ? ""
        : $" The other edits KiCad holds on {Names(OtherEdits)}, which the operation does not change, stay exactly as they are: the continuation saves them with its sheets and publishes them with its XML, as an ordinary synchronization would.";

    internal string Names(IEnumerable<string> paths) => string.Join(", ", paths.Select(path => Name(SheetNames, path)));

    internal static string Key(Kiapi.Common.Types.DocumentSpecifier document) =>
        string.Join('/', document.SheetPath.Path.Select(id => id.Value));

    private static string Target(SchematicItemOperation operation, string batch) =>
        operation.TargetDocument is { } document ? Key(document) : batch;

    private static string Name(IReadOnlyDictionary<string, string> names, string path) =>
        (names.TryGetValue(path, out var name) ? name : "sheet") + " (sheet path " + path + ")";

    // Sheet names by path: the root, and each sheet named by the sheet symbol that is the last element of its path.
    private static Dictionary<string, string> SheetNamesOf(IEnumerable<SchematicHierarchyData> hierarchies)
    {
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hierarchy in hierarchies)
            foreach (var screen in hierarchy.Instances)
            {
                paths.Add(Key(screen.Metadata.Document));
                foreach (var item in screen.Items.Where(item => item.Is(SheetSymbol.Descriptor)))
                {
                    var sheet = item.Unpack<SheetSymbol>();
                    if (sheet.Id?.Value is { Length: > 0 } id && sheet.NameField?.Text?.Text_ is { Length: > 0 } text) symbols.TryAdd(id, text);
                }
            }
        return paths.ToDictionary(path => path, path => path.Contains('/')
            ? symbols.TryGetValue(path[(path.LastIndexOf('/') + 1)..], out var name) ? "sheet " + name : "sheet"
            : "the root sheet", StringComparer.Ordinal);
    }

    // The sheets the operation changes: those its native edit targets and those where its result differs from the last
    // synchronized design. When the two designs cannot be compared, every sheet counts as the operation's.
    private static HashSet<string> SheetsOfOperation(SchematicHierarchyData baseline, SchematicHierarchyData? candidate,
        DesignReleasedOperation receipt, string root, CancellationToken token)
    {
        var sheets = new HashSet<string>(StringComparer.Ordinal);
        if (receipt.Mutation() is { } mutation)
            foreach (var operation in mutation.Operations) sheets.Add(Target(operation, Key(mutation.Document)));
        if (candidate is null) return sheets;
        try
        {
            foreach (var operation in SchematicHierarchyDelta.Plan(baseline, candidate, token)) sheets.Add(Target(operation, root));
        }
        catch (AutomationException)
        {
            foreach (var screen in baseline.Instances.Concat(candidate.Instances)) sheets.Add(Key(screen.Metadata.Document));
        }
        return sheets;
    }

    // Null when an edit on a sheet the operation does not change can be published as KiCad holds it: it changes neither the
    // engineering design nor any connection. Otherwise what the edit is. The operation turns the running KiCad's sheet back
    // into the synchronized one, so a Remove is an object the edit added, a Create one it removed, an Update one it changed.
    internal static string? UncarriedEdit(SchematicItemOperation operation, IReadOnlyDictionary<Guid, Google.Protobuf.IMessage> live)
    {
        static Google.Protobuf.IMessage Decode(Google.Protobuf.WellKnownTypes.Any item) => SchematicItemDelta.Index([item]).Values.Single();
        Google.Protobuf.IMessage? Live(Google.Protobuf.IMessage item) => Identity(item) is { } id && live.TryGetValue(id, out var now) ? now : null;
        switch (operation.OperationCase)
        {
            case SchematicItemOperation.OperationOneofCase.SetTitleBlock or SchematicItemOperation.OperationOneofCase.SetPageSettings:
                return null;
            case SchematicItemOperation.OperationOneofCase.Create:
                var removed = Decode(operation.Create);
                return Carried(removed) ? null : "removed " + Describe(removed);
            case SchematicItemOperation.OperationOneofCase.Update:
                var before = Decode(operation.Update);
                var after = Live(before);
                return Carried(before) && (after is null || Carried(after)) ? null : "changed " + Describe(after ?? before);
            case SchematicItemOperation.OperationOneofCase.Remove:
                var added = Guid.TryParseExact(operation.Remove?.Value, "D", out var addedId) && live.TryGetValue(addedId, out var item) ? item : null;
                return added is not null && Carried(added) ? null : "added " + (added is null ? "an object " + operation.Remove?.Value : Describe(added));
            default:
                return "changed the sheet's " + operation.OperationCase switch
                {
                    SchematicItemOperation.OperationOneofCase.ReplaceLibraryCache => "cached library symbols",
                    SchematicItemOperation.OperationOneofCase.ReplaceEmbeddedFiles => "embedded files",
                    SchematicItemOperation.OperationOneofCase.SetRootInstance => "root page number",
                    _ => "setting " + operation.OperationCase
                };
        }
    }

    private static bool Carried(Google.Protobuf.IMessage item) => item is SchematicText or SchematicTextBox or SchematicGraphicShape
        or SchematicImage or SchematicTable || item is SchematicLine { Type: SchematicLineType.SltGraphic };

    private static string Describe(Google.Protobuf.IMessage item)
    {
        string kind = item switch
        {
            SchematicLine line => line.Type switch { SchematicLineType.SltWire => "wire", SchematicLineType.SltBus => "bus", _ => "line" },
            Junction => "junction", NoConnectMarker => "no-connect marker", BusEntry => "bus entry",
            LocalLabel or GlobalLabel or HierarchicalLabel or DirectiveLabel => "label", SheetSymbol => "sheet",
            SchematicSymbolInstance => "symbol", Group => "group", SchematicRuleArea => "rule area",
            SchematicText => "note", SchematicTextBox => "text box", SchematicGraphicShape => "drawing", SchematicImage => "image",
            SchematicTable => "table", _ => item.Descriptor.Name
        };
        return Identity(item) is { } id ? $"{kind} {id:D}" : kind;
    }

    private static Guid? Identity(Google.Protobuf.IMessage item) =>
        item.Descriptor.FindFieldByName("id")?.Accessor.GetValue(item) is Kiapi.Common.Types.KIID { Value: { } value }
            && Guid.TryParseExact(value, "D", out var id) ? id : null;

    // Which sheet settings differ, named by field, for a comparison the hierarchy planner refused.
    private static string MetadataDifferences(SchematicHierarchyData live, SchematicHierarchyData target)
    {
        var expected = target.Instances.ToDictionary(screen => Key(screen.Metadata.Document), screen => screen.Metadata, StringComparer.Ordinal);
        var found = new List<string>();
        foreach (var screen in live.Instances)
        {
            string path = Key(screen.Metadata.Document);
            if (!expected.TryGetValue(path, out var other)) { found.Add(path + ": only in the running KiCad"); continue; }
            var fields = SchematicMetadata.Descriptor.Fields.InFieldNumberOrder()
                .Where(field => !Equals(field.Accessor.GetValue(screen.Metadata), field.Accessor.GetValue(other))).Select(field => field.Name).ToArray();
            if (fields.Length != 0) found.Add(path + ": " + string.Join(", ", fields));
        }
        found.AddRange(expected.Keys.Where(path => live.Instances.All(screen => Key(screen.Metadata.Document) != path)).Select(path => path + ": not in the running KiCad"));
        return found.Count == 0 ? "" : " Differing sheet settings: " + string.Join("; ", found) + ".";
    }
}
