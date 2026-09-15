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
    byte[]? PendingCandidateFileBytes = null);

public sealed record DesignHierarchyResolution(string SnapshotToken,
    IReadOnlyDictionary<string, SchematicConflictChoice> Choices, string NativeEpoch, ulong NativeSequence);

public sealed record StoredDesignRecovery(string RevisionToken, DesignRecoveryState State);

/// <summary>Durable full-design recovery, not a completion ledger or proof of synchronization.
/// Keeps the last baseline, exact desired file bytes (including invalid XML), current native
/// hierarchy and an optional unconfirmed mutation. Persist before dispatch; after interruption
/// inspect the same native operation receipt and epoch, never invent a replacement operation ID.
/// Saving does not edit either design, authorize a mutation or advance a baseline automatically.</summary>
public sealed class DesignRecoveryStore(string statePath)
{
    private readonly string path = Path.GetFullPath(statePath);
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
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? PendingCandidateFileBytes = null);
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

    public StoredDesignRecovery Save(DesignRecoveryState state, string? expectedRevisionToken)
    {
        Validate(state);
        var envelope = new Envelope(state.PendingNativeSave is not null || state.PendingCandidateFileBytes is not null ? 5
            : state.PendingNativeState is not null ? 4
            : state.BaselineElectrical is not null || state.ObservedElectrical is not null ? 3
            : state.HierarchyResolution is null ? 1 : 2, state.OriginId, state.InstanceId, state.NativeRevision.Epoch,
            state.NativeRevision.Sequence, state.TrackingComplete,
            SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries), state.DesiredFileBytes,
            SchematicDataXml.Write(state.Observed), state.KnowledgeLibraries.Select(ComponentKnowledgeXml.WriteLibrary).ToArray(),
            state.PendingMutation?.ToByteArray(), state.HierarchyResolution,
            EncodeElectrical(state.BaselineElectrical, state.Baseline.Schematic), EncodeElectrical(state.ObservedElectrical, state.Observed),
            state.PendingNativeState?.ToByteArray(), state.PendingNativeSave?.ToByteArray(), state.PendingCandidateFileBytes);
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
            if (current?.RevisionToken == next.RevisionToken) return current;
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
            if (envelope.Version is not (1 or 2 or 3 or 4 or 5) || envelope.KnowledgeLibraryXml is null
                || (envelope.Version == 1 && envelope.HierarchyResolution is not null)
                || (envelope.Version == 2 && envelope.HierarchyResolution is null)
                || (envelope.Version < 4 && (envelope.Version == 3) != electrical)
                || (envelope.Version == 4) != (envelope.PendingNativeState is not null
                    && envelope.PendingNativeSave is null && envelope.PendingCandidateFileBytes is null)
                || (envelope.Version == 5) != (envelope.PendingNativeSave is not null
                    || envelope.PendingCandidateFileBytes is not null)
                || (envelope.Version < 5 && (envelope.PendingNativeSave is not null
                    || envelope.PendingCandidateFileBytes is not null)))
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
                envelope.PendingCandidateFileBytes);
            Validate(state);
            return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), state);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidProtocolBufferException)
        { throw Failure("invalid_design_recovery", error.Message); }
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
        if (state.PendingMutation is not { } pending)
        {
            if (state.PendingNativeState is not null || state.PendingNativeSave is not null
                || state.PendingCandidateFileBytes is not null)
                throw Failure("invalid_design_recovery", "A pending native precondition requires its exact saved mutation.");
            return;
        }
        if (state.PendingNativeState is { } nativeState)
            CheckedSchematicContract.ValidateRequest(new() { Batch = pending, ExpectedState = nativeState }, nativeState.ProcessEpoch);
        if (state.PendingNativeSave is { } nativeSave)
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
