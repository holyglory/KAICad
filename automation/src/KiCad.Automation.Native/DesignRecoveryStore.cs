using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using NativeRevision = KiCad.Automation.Model.DocumentRevision;

namespace KiCad.Automation.Native;

public sealed record DesignRecoveryState(Guid OriginId, Guid InstanceId, NativeRevision NativeRevision,
    bool TrackingComplete, SchematicDesign Baseline, byte[] DesiredFileBytes,
    SchematicHierarchyData Observed, IReadOnlyList<ComponentKnowledgeLibrary> KnowledgeLibraries,
    ApplySchematicItemBatch? PendingMutation = null, DesignHierarchyResolution? HierarchyResolution = null,
    SchematicElectricalState? BaselineElectrical = null, SchematicElectricalState? ObservedElectrical = null,
    DocumentLifecycleState? PendingNativeState = null, CheckedSaveDocument? PendingNativeSave = null,
    byte[]? PendingCandidateFileBytes = null, DesignPublicationIntent? PendingPublication = null,
    DesignSynchronizationReceipt? LastSynchronization = null, DesignLayoutIntent? PendingLayout = null,
    DesignOwnershipResolution? OwnershipResolution = null)
{
    public bool HasPendingWork => PendingMutation is not null || PendingPublication is not null || PendingLayout is not null;
}

public sealed record DesignHierarchyResolution(string SnapshotToken,
    IReadOnlyDictionary<string, SchematicConflictChoice> Choices, string NativeEpoch, ulong NativeSequence);

public sealed record StoredDesignRecovery(string RevisionToken, DesignRecoveryState State);

public sealed record DesignOwnershipResolution(string SnapshotToken, Guid HistoryOperationId, string HistoryXmlSha256);

/// <summary>Durable full-design recovery, not a completion ledger or proof of synchronization.
/// Keeps the last baseline, exact desired file bytes (including invalid XML), current native
/// hierarchy and an optional unconfirmed mutation. Persist before dispatch; after interruption
/// inspect the same native operation receipt and epoch, never invent a replacement operation ID.
/// Saving does not edit either design, authorize a mutation or advance a baseline automatically.</summary>
public sealed class DesignRecoveryStore(string statePath)
{
    private readonly string path = Path.GetFullPath(statePath);
    internal string StatePath => path;
    private static readonly JsonSerializerOptions Json = new()
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Envelope([property: JsonRequired] int Version,
        [property: JsonRequired] Guid OriginId, [property: JsonRequired] Guid InstanceId,
        [property: JsonRequired] string NativeEpoch, [property: JsonRequired] ulong NativeSequence,
        [property: JsonRequired] bool TrackingComplete, [property: JsonRequired] string BaselineXml,
        [property: JsonRequired] byte[] DesiredFileBytes, [property: JsonRequired] string ObservedXml,
        [property: JsonRequired] string[] KnowledgeLibraryXml,
        [property: JsonRequired] byte[]? PendingMutation,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DesignHierarchyResolution? HierarchyResolution = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElectricalEnvelope? BaselineElectrical = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElectricalEnvelope? ObservedElectrical = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? PendingNativeState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? PendingNativeSave = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? PendingCandidateFileBytes = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DesignPublicationIntent? PendingPublication = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DesignSynchronizationReceipt? LastSynchronization = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DesignLayoutIntent? PendingLayout = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DesignOwnershipResolution? OwnershipResolution = null);
    private sealed record ElectricalEnvelope([property: JsonRequired] string Epoch,
        [property: JsonRequired] ulong Sequence, [property: JsonRequired] bool TrackingComplete,
        [property: JsonRequired] string[] NetsXml, [property: JsonRequired] string[] Limitations);

    public StoredDesignRecovery ResolveHierarchy(string expectedRevisionToken, string expectedSnapshotToken,
        IReadOnlyDictionary<string, SchematicConflictChoice> choices)
    {
        var current = Read();
        if (current is null || current.RevisionToken != expectedRevisionToken)
            throw Failure("design_recovery_changed", "Recovery state changed; reload conflicts before choosing.");
        var selected = current.State.HierarchyResolution?.Choices.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? [];
        foreach (var (path, choice) in choices) selected[path] = choice;
        var resolution = new DesignHierarchyResolution(expectedSnapshotToken, selected,
            current.State.NativeRevision.Epoch, current.State.NativeRevision.Sequence);
        // Validate the exact choice set before acquiring the save lock; Save performs
        // a second CAS check under that lock and revalidates the serialized result.
        return Save(current.State with { HierarchyResolution = resolution }, expectedRevisionToken);
    }

    public static SchematicHierarchyMergeResult PlanHierarchy(DesignRecoveryState state)
    {
        var desired = ReadDesired(state);
        return state.HierarchyResolution is { } resolution
            ? SchematicHierarchyMerge.Resolve(state.Baseline.Schematic, desired.Schematic, state.Observed,
                resolution.SnapshotToken, resolution.Choices)
            : SchematicHierarchyMerge.Plan(state.Baseline.Schematic, desired.Schematic, state.Observed);
    }

    public static string HierarchySnapshotToken(DesignRecoveryState state) =>
        SchematicHierarchyMerge.SnapshotToken(state.Baseline.Schematic, ReadDesired(state).Schematic, state.Observed);

    internal static SchematicDesign ReadDesired(DesignRecoveryState state)
    {
        try { return SchematicDesignXml.Read(new System.Text.UTF8Encoding(false, true).GetString(state.DesiredFileBytes), state.KnowledgeLibraries); }
        catch (System.Text.DecoderFallbackException error) { throw Failure("invalid_desired_design", error.Message); }
    }

    public StoredDesignRecovery? Read()
    {
        try { return ReadCore(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Failure("design_recovery_io", error.Message); }
    }

    public StoredDesignRecovery Save(DesignRecoveryState state, string? expectedRevisionToken) =>
        SaveCore(state, expectedRevisionToken, abandoningRealization: false);

    private StoredDesignRecovery SaveCore(DesignRecoveryState state, string? expectedRevisionToken, bool abandoningRealization,
        bool releasingExited = false, Action? beforeReplace = null, DesignReleasedOperation? continuing = null)
    {
        Validate(state);
        var fileIntent = state.PendingPublication ?? (state.PendingLayout is { } layout
            ? DesignPublicationIntent.Create(layout.DesignPath, layout.ExpectedFileBytes, layout.PlannedDesignFileBytes,
                layout.OperationId, layout.RequestedRecoveryRevisionToken) : null);
        if (fileIntent is { } publication)
        {
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (new[] { publication.DesignPath, publication.StagedPath, publication.PreviousPath, publication.DesignPath + ".sync.lock" }
                .Any(p => string.Equals(p, path, comparison) || string.Equals(p, path + ".lock", comparison)))
                throw Failure("invalid_design_publication", "Design publication and recovery files must remain separate.");
        }
        var envelope = new Envelope(state.PendingLayout?.Lane is not null ? 10 : state.OwnershipResolution is not null ? 9 : state.PendingLayout is not null ? 8
            : state.LastSynchronization is not null || state.PendingPublication?.RequestedRecoveryRevisionToken is not null ? 7
            : state.PendingPublication is not null ? 6
            : state.PendingNativeSave is not null || state.PendingCandidateFileBytes is not null ? 5
            : state.PendingNativeState is not null ? 4
            : state.BaselineElectrical is not null || state.ObservedElectrical is not null ? 3
            : state.HierarchyResolution is null ? 1 : 2, state.OriginId, state.InstanceId, state.NativeRevision.Epoch,
            state.NativeRevision.Sequence, state.TrackingComplete,
            SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries), state.DesiredFileBytes,
            SchematicDataXml.Write(state.Observed), state.KnowledgeLibraries.Select(ComponentKnowledgeXml.WriteLibrary).ToArray(),
            state.PendingMutation?.ToByteArray(), state.HierarchyResolution,
            EncodeElectrical(state.BaselineElectrical, state.Baseline.Schematic), EncodeElectrical(state.ObservedElectrical, state.Observed),
            state.PendingNativeState?.ToByteArray(), state.PendingNativeSave?.ToByteArray(), state.PendingCandidateFileBytes,
            state.PendingPublication, state.LastSynchronization, state.PendingLayout, state.OwnershipResolution);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        // Verify complete recoverability before touching the previous recovery file.
        var next = Decode(bytes);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Same trusted local coordination as SchematicSyncStore. Keep the inode:
            // unlinking the lock would permit competing locks on different files.
            using var ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = ReadCore();
            if (current?.RevisionToken != expectedRevisionToken)
                throw Failure("design_recovery_changed", "Recovery state changed; reload it before saving.");
            ValidateReleasedContinuation(current?.State, state, continuing);
            ValidateTransition(current?.State, state, abandoningRealization, releasingExited);
            if (current?.RevisionToken == next.RevisionToken) return current;
            beforeReplace?.Invoke();
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return next;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Failure("design_recovery_io", error.Message); }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private StoredDesignRecovery? ReadCore()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        return Decode(bytes);
    }

    private static StoredDesignRecovery Decode(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || json.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                    != json.RootElement.EnumerateObject().Count())
                throw Failure("invalid_design_recovery", "Invalid or duplicate recovery fields.");
            var objects = new Stack<JsonElement>(); objects.Push(json.RootElement);
            while (objects.TryPop(out var element))
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        if (!names.Add(property.Name)) throw Failure("invalid_design_recovery", "Duplicate nested recovery field.");
                        objects.Push(property.Value);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array)
                    foreach (var item in element.EnumerateArray()) objects.Push(item);
            }
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Json)
                ?? throw Failure("invalid_design_recovery", "Missing recovery state.");
            bool electrical = envelope.BaselineElectrical is not null || envelope.ObservedElectrical is not null;
            int requiredVersion = envelope.PendingLayout?.Lane is not null ? 10 : envelope.OwnershipResolution is not null ? 9 : envelope.PendingLayout is not null ? 8
                : envelope.LastSynchronization is not null || envelope.PendingPublication?.RequestedRecoveryRevisionToken is not null ? 7
                : envelope.PendingPublication is not null ? 6
                : envelope.PendingNativeSave is not null || envelope.PendingCandidateFileBytes is not null ? 5
                : envelope.PendingNativeState is not null ? 4 : electrical ? 3
                : envelope.HierarchyResolution is not null ? 2 : 1;
            if (envelope.Version != requiredVersion || envelope.KnowledgeLibraryXml is null)
                throw Failure("invalid_design_recovery", "Unsupported or incomplete recovery state.");
            var libraries = envelope.KnowledgeLibraryXml.Select(ComponentKnowledgeXml.ReadLibrary).ToArray();
            var observed = SchematicDataXml.Read(envelope.ObservedXml) as SchematicHierarchyData
                ?? throw Failure("invalid_design_recovery", "Recovery requires a typed native hierarchy.");
            var baseline = SchematicDesignXml.Read(envelope.BaselineXml, libraries);
            var state = new DesignRecoveryState(envelope.OriginId, envelope.InstanceId,
                new(envelope.NativeEpoch, envelope.NativeSequence), envelope.TrackingComplete,
                baseline, envelope.DesiredFileBytes, observed, libraries,
                envelope.PendingMutation is null ? null : ApplySchematicItemBatch.Parser.ParseFrom(envelope.PendingMutation), envelope.HierarchyResolution,
                DecodeElectrical(envelope.BaselineElectrical, baseline.Schematic), DecodeElectrical(envelope.ObservedElectrical, observed),
                envelope.PendingNativeState is null ? null : DocumentLifecycleState.Parser.ParseFrom(envelope.PendingNativeState),
                envelope.PendingNativeSave is null ? null : CheckedSaveDocument.Parser.ParseFrom(envelope.PendingNativeSave),
                envelope.PendingCandidateFileBytes, envelope.PendingPublication, envelope.LastSynchronization, envelope.PendingLayout, envelope.OwnershipResolution);
            Validate(state);
            return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), state);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidProtocolBufferException)
        { throw Failure("invalid_design_recovery", error.Message); }
    }

    // An ordinary pending layout is a connected move, transform or lock request. A lane
    // realization (CN-1 §9.3) contains only creations, updates and library-cache
    // replacements, followed by one whole-batch connectivity assertion (field 27), which
    // a connection realization always carries.
    private static bool LayoutMutationMatchesLane(ApplySchematicItemBatch batch, string? lane)
    {
        if (lane is null)
            return batch.Operations.Any(o => o.MoveConnectedSymbols is not null
                || o.TransformConnectedSymbols is not null || o.SetSymbolLocks is not null);
        if (lane is not DesignLayoutIntent.ConnectionRealizationLane and not DesignLayoutIntent.RebuildLane
            || batch.Operations.Count == 0)
            return false;
        const SchematicItemOperation.OperationOneofCase assertion = (SchematicItemOperation.OperationOneofCase)27;
        int assertions = batch.Operations.Count(o => o.OperationCase == assertion);
        if (assertions > 1 || (assertions == 1 && batch.Operations[^1].OperationCase != assertion)
            || (lane == DesignLayoutIntent.ConnectionRealizationLane && assertions == 0))
            return false;
        return batch.Operations.All(o => o.OperationCase is assertion
            or SchematicItemOperation.OperationOneofCase.Create
            or SchematicItemOperation.OperationOneofCase.Update
            or SchematicItemOperation.OperationOneofCase.ReplaceLibraryCache);
    }

    /// <summary>Clear a lane realization whose native assertion rejected the batch before any
    /// mutation (CN-1 §9.4). Verified against the exact journaled request: the receipt must be a
    /// rejection for the pending operation whose observations equal the journaled native state,
    /// so nothing native changed. The XML, baseline and publication state stay untouched.</summary>
    public StoredDesignRecovery AbandonRejectedRealization(StoredDesignRecovery saved, CheckedSchematicBatchReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(receipt);
        var state = saved.State;
        if (state.PendingLayout?.Lane is null || state.PendingMutation is null || state.PendingNativeState is null)
            throw Failure("invalid_layout_intent", "Only a pending lane realization can be abandoned.");
        if (receipt.Status != CheckedSchematicBatchStatus.CsbsRejected
            || receipt.OperationId != state.PendingMutation.OperationId
            || receipt.ObservedBefore is null || receipt.ObservedAfter is null
            || !receipt.ObservedBefore.Equals(state.PendingNativeState) || !receipt.ObservedAfter.Equals(state.PendingNativeState))
            throw Failure("invalid_layout_intent", "Abandon only a rejected realization whose native state is unchanged.");
        return SaveCore(state with { PendingMutation = null, PendingNativeState = null, PendingLayout = null }, saved.RevisionToken,
            abandoningRealization: true);
    }

    /// <summary>Release the pending operation of a KiCad process proven to have ended (decision
    /// nd2e75380e7f8aa7f). The proof must be the exit of exactly the process epoch that holds the
    /// operation: the epoch of its checked native state and, when one was journaled, of its save. Only the
    /// instance registry issues a ProvenInstanceExit, and it proves an exit only by this server's observer of
    /// that process or by a saved registration of the same machine, boot and process ID namespace. Under the
    /// record's lock, after the revision check, the whole pending operation is first kept in a durable release
    /// receipt with the native files its save had already replaced, so later reconciliation treats a partial
    /// result as that operation's and never as a user edit. Only the pending operation is then cleared:
    /// baseline, desired XML, observation, choices and completed receipts stay exactly as they were. Until the
    /// released operation is continued (ContinueReleasedOperation, used by ExitedOperationRelease), no save may
    /// attach the record to another native document session: the KiCad started again may hold part of the
    /// operation's result, which an ordinary reattachment would present as a user edit.</summary>
    public StoredDesignRecovery ReleaseExitedOperation(StoredDesignRecovery saved, ProvenInstanceExit proven)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(proven);
        var proof = proven.Exit;
        var state = saved.State;
        if (!state.HasPendingWork)
            throw Failure("no_pending_operation", "The recovery record holds no pending operation to release.");
        if (state.PendingNativeState is not { } native)
            throw Failure("operation_process_unknown", "The pending operation does not record which KiCad process holds it.");
        if (proof.InstanceId != state.InstanceId.ToString("D") || proof.Epoch != native.ProcessEpoch
            || (state.PendingNativeSave is { } save && save.ExpectedState?.ProcessEpoch != proof.Epoch)
            || proof.ProcessId <= 0 || proof.Evidence is not (InstanceExit.ExitStatusEvidence or InstanceExit.ProcessAbsentEvidence))
            throw Failure("operation_exit_unproven", "Release requires the proven exit of exactly the KiCad process that holds the pending operation.");
        var receipt = DesignReleasedOperation.Create(saved, proof);
        return SaveCore(state with
        {
            PendingMutation = null, PendingNativeState = null, PendingNativeSave = null, PendingCandidateFileBytes = null,
            PendingPublication = null, PendingLayout = null
        }, saved.RevisionToken, abandoningRealization: false, releasingExited: true,
            beforeReplace: () => DesignReleasedOperations.Write(path, receipt));
    }

    /// <summary>Continue an operation released by ReleaseExitedOperation on the KiCad started again: the one save
    /// that attaches the record to that KiCad's document session, together with the continuation journaled for it
    /// (or, when nothing of the operation survived, with no pending work). The record must still sit on the
    /// released document session with exactly <paramref name="receipt"/> open; every ordinary rule of the next
    /// state still applies. Moving to the new session closes the receipt, so this happens once.</summary>
    internal StoredDesignRecovery ContinueReleasedOperation(DesignRecoveryState state, string expectedRevisionToken,
        DesignReleasedOperation receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return SaveCore(state, expectedRevisionToken, abandoningRealization: false, continuing: receipt);
    }

    // A released operation stays open while the record sits on the document session it was released from. Only its
    // continuation may then attach the record to another session (ContinueReleasedOperation); an ordinary save that
    // does, such as kicad_design_recovery_reattach, is refused, because the KiCad started again may hold part of the
    // operation's result and the next synchronization would take it for a user edit. Records never released are not
    // affected: without a receipt folder nothing is read.
    private void ValidateReleasedContinuation(DesignRecoveryState? current, DesignRecoveryState next, DesignReleasedOperation? continuing)
    {
        if (current is null)
        {
            if (continuing is not null) throw Failure("released_operation_not_open", "No recovery record holds the released operation.");
            return;
        }
        if (continuing is null && current.NativeRevision.Epoch == next.NativeRevision.Epoch) return;
        var open = DesignReleasedOperations.OpenOn(path, current);
        if (continuing is null)
        {
            if (open.Count == 0) return;
            var first = open[0];
            throw Failure("released_operation_requires_continuation", $"Operation {DesignReleasedOperations.Describe(first)} was released when "
                + $"KiCad process epoch {first.ProcessEpoch} ended, and the KiCad started again may hold part of its result. Continue it with "
                + "kicad_design_recovery_release_exited (continuation 'resume' or 'roll-back') before this record is attached to another KiCad "
                + "document session; nothing was changed.");
        }
        if (current.HasPendingWork || !open.Any(receipt => receipt.ProcessEpoch == continuing.ProcessEpoch
                && receipt.OperationId == continuing.OperationId && receipt.NativeOperationId == continuing.NativeOperationId))
            throw Failure("released_operation_not_open", $"Operation {DesignReleasedOperations.Describe(continuing)} is not waiting to be continued "
                + "on this record: it was continued already, or the record changed.");
    }

    private static void Validate(DesignRecoveryState state)
    {
        if (state.OriginId == Guid.Empty || state.InstanceId == Guid.Empty || state.NativeRevision is null
            || string.IsNullOrWhiteSpace(state.NativeRevision.Epoch) || state.Baseline is null
            || state.DesiredFileBytes is null || state.Observed is null || state.KnowledgeLibraries is null)
            throw Failure("invalid_design_recovery", "An explicit instance, origin, revision and all design versions are required.");
        var document = state.Baseline.Schematic.Document;
        if (document?.SheetPath is null || document.SheetPath.Path.Count != 1
            || !Guid.TryParseExact(document.SheetPath.Path[0].Value, "D", out var rootId) || rootId == Guid.Empty
            || !document.Equals(state.Observed.Document))
            throw Failure("invalid_design_recovery", "Native and baseline versions must identify the same hierarchy root.");
        _ = EncodeElectrical(state.BaselineElectrical, state.Baseline.Schematic);
        _ = EncodeElectrical(state.ObservedElectrical, state.Observed);
        if (state.LastSynchronization is { } completed)
        {
            completed.Validate();
            if (completed.InstanceId != state.InstanceId)
                throw Failure("invalid_sync_receipt", "The completed operation belongs to another instance.");
        }
        if (state.OwnershipResolution is { } ownership
            && (ownership.HistoryOperationId == Guid.Empty
                || ownership.SnapshotToken is not { Length: 64 } || !ownership.SnapshotToken.All(char.IsAsciiHexDigitLower)
                || ownership.HistoryXmlSha256 is not { Length: 64 } || !ownership.HistoryXmlSha256.All(char.IsAsciiHexDigitLower)))
            throw Failure("invalid_ownership_resolution", "A history choice requires its exact operation, content digest and snapshot token.");
        if (state.ObservedElectrical is { } current
            && (current.Hierarchy.Revision.Epoch != state.NativeRevision.Epoch
                || current.Hierarchy.Revision.Sequence != state.NativeRevision.Sequence
                || current.Hierarchy.TrackingComplete != state.TrackingComplete))
            throw Failure("invalid_electrical_recovery", "Current connectivity must match the saved native observation revision and coverage.");
        if (state.BaselineElectrical is { } baseline
            && baseline.Hierarchy.Revision.Epoch == state.NativeRevision.Epoch
            && baseline.Hierarchy.Revision.Sequence > state.NativeRevision.Sequence)
            throw Failure("invalid_electrical_recovery", "The electrical baseline cannot be newer than the current native observation.");
        if (state.HierarchyResolution is { } resolution)
        {
            if (string.IsNullOrWhiteSpace(resolution.SnapshotToken) || resolution.Choices is null
                || resolution.NativeEpoch != state.NativeRevision.Epoch || resolution.NativeSequence != state.NativeRevision.Sequence)
                throw Failure("invalid_design_resolution", "Saved hierarchy choices require their exact snapshot token.");
            _ = PlanHierarchy(state);
        }
        if (state.PendingLayout is { } layout)
        {
            if (state.PendingPublication is not null || state.PendingNativeSave is not null || state.PendingCandidateFileBytes is not null
                || state.PendingMutation is null || !LayoutMutationMatchesLane(state.PendingMutation, layout.Lane)
                || layout.ExpectedFileBytes is null || layout.PlannedDesignFileBytes is null
                || string.IsNullOrEmpty(layout.RequestedRecoveryRevisionToken))
                throw Failure("invalid_layout_intent", "Unresolved layout requires its exact connected-move request, not a save or final publication.");
            ValidatePublication(state, new(layout.OperationId, layout.DesignPath,
                layout.DesignPath + ".sync-" + layout.OperationId.ToString("N"),
                layout.DesignPath + ".sync-" + layout.OperationId.ToString("N"),
                layout.ExpectedFileBytes, layout.PlannedDesignFileBytes, DesignPublicationPhase.Prepared,
                layout.RequestedRecoveryRevisionToken));
        }
        if (state.PendingPublication is { } publication)
            ValidatePublication(state, publication);
        if (state.PendingNativeSave is { } save && state.PendingPublication is not null)
            ValidatePublicationSave(state, save);
        if (state.PendingMutation is not { } pending)
        {
            if (state.PendingPublication is null && (state.PendingNativeState is not null || state.PendingNativeSave is not null
                || state.PendingCandidateFileBytes is not null))
                throw Failure("invalid_design_recovery", "A pending native precondition requires its exact saved mutation.");
            return;
        }
        if (state.PendingNativeState is { } nativeState)
            CheckedSchematicContract.ValidateRequest(new() { Batch = pending, ExpectedState = nativeState }, nativeState.ProcessEpoch);
        if (state.PendingNativeSave is { } nativeSave && state.PendingPublication is null)
        {
            if (state.PendingCandidateFileBytes is null || nativeSave.ExpectedState is null
                || !nativeSave.Document.Equals(pending.Document) || string.IsNullOrWhiteSpace(nativeSave.OperationId)
                || !Guid.TryParseExact(nativeSave.OperationId, "D", out _)
                || nativeSave.ExpectedState.ProcessEpoch != state.PendingNativeState?.ProcessEpoch
                || nativeSave.ExpectedState.Revision?.Epoch != pending.DocumentEpoch)
                throw Failure("invalid_design_recovery", "A pending native save requires its exact synchronized candidate and document state.");
            _ = SchematicDesignXml.Read(new System.Text.UTF8Encoding(false, true).GetString(state.PendingCandidateFileBytes), state.KnowledgeLibraries);
        }
        else if (state.PendingCandidateFileBytes is not null)
            _ = SchematicDesignXml.Read(new System.Text.UTF8Encoding(false, true).GetString(state.PendingCandidateFileBytes), state.KnowledgeLibraries);
        if (string.IsNullOrWhiteSpace(pending.OperationId) || System.Text.Encoding.UTF8.GetByteCount(pending.OperationId) > 128
            || pending.OperationId.Contains('\0') || pending.DocumentEpoch != state.NativeRevision.Epoch
            || pending.ExpectedRevision?.Epoch != state.NativeRevision.Epoch
            || pending.ExpectedRevision.Sequence != state.NativeRevision.Sequence
            || pending.OriginId != state.OriginId.ToString("D") || pending.Operations.Count == 0)
            throw Failure("invalid_design_recovery", "A pending mutation requires its exact retry identity, origin and observed revision.");
        bool SameOwner(Kiapi.Common.Types.DocumentSpecifier? target)
        {
            if (target?.SheetPath is null || target.SheetPath.Path.Count == 0
                || target.SheetPath.Path.Any(id => !Guid.TryParseExact(id.Value, "D", out var value) || value == Guid.Empty)
                || target.SheetPath.Path[0].Value != document.SheetPath.Path[0].Value) return false;
            var rootTarget = target.Clone(); rootTarget.SheetPath = document.SheetPath.Clone();
            return rootTarget.Equals(document);
        }
        if (!SameOwner(pending.Document) || pending.Operations.Any(o => o.TargetDocument is not null && !SameOwner(o.TargetDocument)))
            throw Failure("invalid_design_recovery", "Pending mutation targets must belong to the recorded native design.");
    }

    private static void ValidatePublication(DesignRecoveryState state, DesignPublicationIntent publication)
    {
        if (publication.OperationId == Guid.Empty || !Enum.IsDefined(publication.Phase)
            || (publication.RequestedRecoveryRevisionToken is { } requestToken
                && (requestToken.Length != 64 || !requestToken.All(char.IsAsciiHexDigitLower)))
            || string.IsNullOrWhiteSpace(publication.DesignPath) || !Path.IsPathFullyQualified(publication.DesignPath)
            || Path.GetFullPath(publication.DesignPath) != publication.DesignPath
            || !string.Equals(Path.GetExtension(publication.DesignPath), ".xml", StringComparison.OrdinalIgnoreCase)
            || publication.StagedPath != publication.DesignPath + ".sync-" + publication.OperationId.ToString("N")
            || (publication.PreviousPath != publication.StagedPath && publication.PreviousPath != publication.StagedPath + ".previous")
            || publication.ExpectedFileBytes is null || publication.CandidateFileBytes is null
            || state.PendingCandidateFileBytes is not null || state.PendingNativeState is null || state.ObservedElectrical is null)
            throw Failure("invalid_design_publication", "Publication requires exact paths, versions, phase and its original native checkpoint.");
        CheckedSchematicContract.ValidateObservation(new() { State = state.PendingNativeState, Electrical = state.ObservedElectrical },
            state.Baseline.Schematic.Document, state.PendingNativeState.ProcessEpoch);
        foreach (byte[] bytes in new[] { publication.ExpectedFileBytes, publication.CandidateFileBytes })
        {
            var design = ReadDesired(state with { DesiredFileBytes = bytes });
            if (!design.Schematic.Document.Equals(state.Baseline.Schematic.Document))
                throw Failure("invalid_design_publication", "Both XML versions must belong to the saved native design.");
        }
        if (publication.Phase != DesignPublicationPhase.Prepared && state.PendingNativeSave is null)
            throw Failure("invalid_design_publication", "Native-save identity must be retained before publishing XML.");
    }

    private static void ValidateTransition(DesignRecoveryState? current, DesignRecoveryState next, bool abandoningRealization = false,
        bool releasingExited = false)
    {
        if (current?.HasPendingWork == true && !Equals(current.OwnershipResolution, next.OwnershipResolution)
            && !(next.OwnershipResolution is null && !next.HasPendingWork
                && next.LastSynchronization is { } completed
                && completed.OperationId == (current.PendingPublication?.OperationId ?? current.PendingLayout?.OperationId)))
            throw Failure("ownership_resolution_pending", "Keep the selected ownership mapping unchanged until the pending synchronization is completed.");
        if (current?.PendingLayout is { } abandoned && abandoningRealization)
        {
            // Only AbandonRejectedRealization reaches here, after verifying the rejected
            // receipt: clear exactly the pending lane request and nothing else.
            if (abandoned.Lane is null || next.PendingLayout is not null || next.PendingMutation is not null
                || next.PendingNativeState is not null || next.PendingNativeSave is not null || next.PendingPublication is not null
                || next.PendingCandidateFileBytes is not null || current.OriginId != next.OriginId || current.InstanceId != next.InstanceId
                || current.NativeRevision != next.NativeRevision || !Equals(current.Observed, next.Observed)
                || !Equals(current.ObservedElectrical, next.ObservedElectrical) || !Equals(current.BaselineElectrical, next.BaselineElectrical)
                || !current.DesiredFileBytes.AsSpan().SequenceEqual(next.DesiredFileBytes)
                || !current.KnowledgeLibraries.Select(ComponentKnowledgeXml.WriteLibrary)
                    .SequenceEqual(next.KnowledgeLibraries.Select(ComponentKnowledgeXml.WriteLibrary))
                || SchematicDesignXml.Write(current.Baseline, current.KnowledgeLibraries) != SchematicDesignXml.Write(next.Baseline, next.KnowledgeLibraries))
                throw Failure("layout_intent_changed", "Abandoning a rejected realization clears only its pending request.");
            return;
        }
        if (releasingExited)
        {
            // Only ReleaseExitedOperation reaches here, after matching a proven process exit to the
            // operation: clear exactly the pending operation, whatever its phase, and nothing else.
            // Ordinary saves cannot clear a publication or layout without its completion.
            static byte[] Serialized<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (current is null || !current.HasPendingWork || next.HasPendingWork || next.PendingNativeState is not null
                || next.PendingNativeSave is not null || next.PendingCandidateFileBytes is not null
                || current.OriginId != next.OriginId || current.InstanceId != next.InstanceId
                || current.NativeRevision != next.NativeRevision || current.TrackingComplete != next.TrackingComplete
                || !Equals(current.Observed, next.Observed) || !Equals(current.ObservedElectrical, next.ObservedElectrical)
                || !Equals(current.BaselineElectrical, next.BaselineElectrical)
                || !current.DesiredFileBytes.AsSpan().SequenceEqual(next.DesiredFileBytes)
                || !Serialized(current.HierarchyResolution).AsSpan().SequenceEqual(Serialized(next.HierarchyResolution))
                || !Equals(current.OwnershipResolution, next.OwnershipResolution)
                || !Serialized(current.LastSynchronization).AsSpan().SequenceEqual(Serialized(next.LastSynchronization))
                || !current.KnowledgeLibraries.Select(ComponentKnowledgeXml.WriteLibrary)
                    .SequenceEqual(next.KnowledgeLibraries.Select(ComponentKnowledgeXml.WriteLibrary))
                || SchematicDesignXml.Write(current.Baseline, current.KnowledgeLibraries) != SchematicDesignXml.Write(next.Baseline, next.KnowledgeLibraries))
                throw Failure("released_operation_changed", "Releasing an exited operation clears only that operation.");
            return;
        }
        if (current?.PendingLayout is { } layout)
        {
            if (!Equals(current.PendingMutation, next.PendingMutation) || !Equals(current.PendingNativeState, next.PendingNativeState)
                || next.PendingNativeSave is not null || current.OriginId != next.OriginId || current.InstanceId != next.InstanceId
                || current.NativeRevision != next.NativeRevision || !Equals(current.Observed, next.Observed)
                || !Equals(current.ObservedElectrical, next.ObservedElectrical)
                || !Equals(current.BaselineElectrical, next.BaselineElectrical)
                || !current.KnowledgeLibraries.Select(ComponentKnowledgeXml.WriteLibrary)
                    .SequenceEqual(next.KnowledgeLibraries.Select(ComponentKnowledgeXml.WriteLibrary))
                || SchematicDesignXml.Write(current.Baseline, current.KnowledgeLibraries) != SchematicDesignXml.Write(next.Baseline, next.KnowledgeLibraries))
                throw Failure("layout_intent_changed", "Retain the original layout request, native guard and synchronized baseline until its geometry is resolved.");
            if (next.PendingLayout is { } retained)
            {
                if (!JsonSerializer.SerializeToUtf8Bytes(layout, Json).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(retained, Json)))
                    throw Failure("layout_intent_changed", "The pending layout belongs to one exact original request.");
            }
            else
            {
                var publication = next.PendingPublication;
                if (publication is null || publication.Phase != DesignPublicationPhase.Prepared
                    || publication.OperationId != layout.OperationId || publication.DesignPath != layout.DesignPath
                    || publication.RequestedRecoveryRevisionToken != layout.RequestedRecoveryRevisionToken
                    || !publication.ExpectedFileBytes.AsSpan().SequenceEqual(layout.ExpectedFileBytes))
                    throw Failure("missing_layout_result", "Resolve layout into a final publication with the same original operation and XML input.");
                var planned = ReadDesired(current with { DesiredFileBytes = layout.PlannedDesignFileBytes });
                var resolved = ReadDesired(next with { DesiredFileBytes = publication.CandidateFileBytes });
                // Wire geometry is native-resolved; electrical intent and user
                // instructions must not be rewritten as part of that resolution.
                if (SchematicDesignXml.Write(planned with { Schematic = resolved.Schematic }, current.KnowledgeLibraries)
                    != SchematicDesignXml.Write(resolved, next.KnowledgeLibraries))
                    throw Failure("layout_intent_changed", "Resolving wire geometry cannot change the requested engineering design.");
            }
        }
        if (current?.PendingPublication is not { RequestedRecoveryRevisionToken: not null } before) return;
        if (next.PendingPublication is { } after)
        {
            byte[] a = JsonSerializer.SerializeToUtf8Bytes(before with { Phase = DesignPublicationPhase.Prepared }, Json);
            byte[] b = JsonSerializer.SerializeToUtf8Bytes(after with { Phase = DesignPublicationPhase.Prepared }, Json);
            if (!a.AsSpan().SequenceEqual(b) || !Equals(current.PendingMutation, next.PendingMutation)
                || !Equals(current.PendingNativeState, next.PendingNativeState)
                || (current.PendingNativeSave is not null && !Equals(current.PendingNativeSave, next.PendingNativeSave)))
                throw Failure("sync_intent_changed", "The exact request and pending native identities cannot change during synchronization.");
            bool validPhase = before.Phase == after.Phase || ((before.Phase, after.Phase) switch
            {
                (DesignPublicationPhase.Prepared, DesignPublicationPhase.Staged or DesignPublicationPhase.Converged) => true,
                (DesignPublicationPhase.Staged, DesignPublicationPhase.Attempting or DesignPublicationPhase.Converged) => true,
                (DesignPublicationPhase.Attempting, DesignPublicationPhase.Published) => true,
                _ => false
            });
            if (!validPhase) throw Failure("sync_phase_changed", "Publication phases cannot be rewound or skipped.");
        }
        else if (before.Phase is not (DesignPublicationPhase.Published or DesignPublicationPhase.Converged)
            || next.LastSynchronization is not { } result || result.OperationId != before.OperationId
            || result.RequestedRecoveryRevisionToken != before.RequestedRecoveryRevisionToken
            || result.DesignPath != before.DesignPath
            || result.DesignFileSha256 != Convert.ToHexStringLower(SHA256.HashData(before.CandidateFileBytes))
            || (result.Version == 2 && result.PreviousXmlPath is not null
                && (result.PreviousXmlSha256 != Convert.ToHexStringLower(SHA256.HashData(before.ExpectedFileBytes))
                    || (result.PreviousXmlPath != before.StagedPath && result.PreviousXmlPath != before.PreviousPath))))
            throw Failure("missing_sync_completion_receipt", "Clear the pending publication only together with its matching completed result.");
    }

    private static void ValidatePublicationSave(DesignRecoveryState state, CheckedSaveDocument save)
    {
        var native = state.PendingNativeState!;
        if (!Guid.TryParseExact(save.OperationId, "D", out var operation) || operation == Guid.Empty
            || !Equals(save.Document, native.Document) || !Equals(save.ExpectedState?.Document, native.Document)
            || save.ExpectedState!.ProcessEpoch != native.ProcessEpoch || save.ExpectedState.NativeIdentity != native.NativeIdentity
            || save.ExpectedState.Revision?.Epoch != native.Revision.Epoch
            || save.ExpectedState.Revision.Sequence < native.Revision.Sequence)
            throw Failure("invalid_design_publication", "The pending save must identify the same native document and process.");
    }

    private static ElectricalEnvelope? EncodeElectrical(SchematicElectricalState? state, SchematicHierarchyData hierarchy)
    {
        if (state is null) return null;
        if (state.Hierarchy?.Revision is not { } revision || string.IsNullOrWhiteSpace(revision.Epoch)
            || !Equals(state.Hierarchy.Data, hierarchy))
            throw Failure("invalid_electrical_recovery", "Electrical checkpoints require their exact owning hierarchy and revision.");
        var envelope = new ElectricalEnvelope(revision.Epoch, revision.Sequence, state.Hierarchy.TrackingComplete,
            state.Nets.Select(SchematicDataXml.Write).ToArray(), state.Limitations.ToArray());
        // Round-trip the complete message, not just its known fields. Future
        // transport fields cannot silently disappear from a recovery record.
        if (!state.Equals(DecodeElectrical(envelope, hierarchy)))
            throw Failure("invalid_electrical_recovery", "The electrical checkpoint contains unsupported fields.");
        return envelope;
    }

    private static SchematicElectricalState? DecodeElectrical(ElectricalEnvelope? envelope, SchematicHierarchyData hierarchy)
    {
        if (envelope is null) return null;
        if (string.IsNullOrWhiteSpace(envelope.Epoch) || envelope.NetsXml is null || envelope.Limitations is null)
            throw Failure("invalid_electrical_recovery", "Electrical checkpoint fields are incomplete.");
        var result = new SchematicElectricalState { Hierarchy = new()
        { Data = hierarchy.Clone(), Revision = new() { Epoch = envelope.Epoch, Sequence = envelope.Sequence }, TrackingComplete = envelope.TrackingComplete } };
        foreach (string xml in envelope.NetsXml)
            result.Nets.Add(SchematicDataXml.Read(xml) as SchematicNet
                ?? throw Failure("invalid_electrical_recovery", "A checkpoint membership must be a typed native net."));
        result.Limitations.Add(envelope.Limitations);
        return result;
    }

    private static AutomationException Failure(string code, string message) => new(code, message);
}
