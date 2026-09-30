using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record SchematicSheetComponentReference(Guid SheetInstanceId, Guid ComponentDefinitionId, string Reference);

public sealed record SchematicSheetComponentResolutionRequest(string Code, Guid SheetInstanceId,
    Guid ComponentDefinitionId, Guid ProposedComponentId, IReadOnlyList<Guid> NativePath, Guid PartId, string Reason);

public sealed record DesignRepeatedSheetResolution(string SnapshotToken,
    IReadOnlyList<SchematicSheetComponentReference> ComponentReferences, IReadOnlyList<SchematicOwnershipAnswer> SymbolOwners);

internal static class SchematicRepeatedSheetChoices
{
    internal const string ReferenceRequired = "native_sheet_component_reference_required";
    internal const string Invalid = "native_repeated_sheet_answer_invalid";

    internal static async Task<(StoredDesignRecovery Recovery, SchematicNativeAdditionResult Projection)> RetainAsync(
        DesignRecoveryStore store, StoredDesignRecovery saved, IReadOnlyList<SchematicSheetComponentReference> references,
        IReadOnlyList<SchematicOwnershipAnswer> symbolOwners, CancellationToken token, bool allowNewFiles = false)
    {
        if (saved.State.HasPendingWork)
            throw new AutomationException("pending_recovery_requires_reconciliation", "Finish or reconcile the pending synchronization before changing repeated-sheet choices.");
        if (references.Count == 0 && symbolOwners.Count == 0)
            throw new AutomationException(Invalid, "Give at least one reference or symbol-owner choice from the current repeated-sheet questions.");
        string snapshot = SnapshotToken(saved.State);
        Validate(new(snapshot, references, symbolOwners));
        var changes = SchematicNativeSheetChanges.Compare(saved.State.Baseline.Schematic, saved.State.Observed);
        var oldScreens = saved.State.Baseline.Schematic.Instances.Select(s => s.Metadata.ScreenId.Value).ToHashSet(StringComparer.Ordinal);
        var inserted = changes.Inserted.ToHashSet(StringComparer.Ordinal);
        if (changes.ErrorCode is not null || !saved.State.Observed.Instances.Any(s => inserted.Contains(SchematicNativeSheetChanges.Key(s))
            && (allowNewFiles || oldScreens.Contains(s.Metadata.ScreenId.Value))))
            throw new AutomationException(Invalid, "The observed native change must insert another instance of an existing sheet file.");
        var current = saved.State.RepeatedSheetResolution is { } previous && previous.SnapshotToken == snapshot ? previous : null;
        var mergedReferences = (current?.ComponentReferences ?? []).ToDictionary(r => (r.SheetInstanceId, r.ComponentDefinitionId));
        foreach (var reference in references) mergedReferences[(reference.SheetInstanceId, reference.ComponentDefinitionId)] = reference;
        static string Key(SchematicOwnershipAnswer answer) => SchematicDesignBindings.PathKey(answer.NativePath!) + "#" + answer.NativeObjectId.ToString("D");
        var mergedSymbols = (current?.SymbolOwners ?? []).ToDictionary(Key, StringComparer.Ordinal);
        foreach (var answer in symbolOwners) mergedSymbols[Key(answer)] = answer;
        var resolution = new DesignRepeatedSheetResolution(snapshot,
            [.. mergedReferences.Values.OrderBy(r => r.SheetInstanceId).ThenBy(r => r.ComponentDefinitionId)],
            [.. mergedSymbols.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value)]);
        var state = saved.State with { RepeatedSheetResolution = resolution };
        var history = await SchematicOwnershipHistoryReader.ReadForAdditionsAsync(store, state, token);
        var projected = SchematicNativeAdditionProjection.Project(state, history, DesignRecoveryStore.ReadDesired(state), null, token);
        if (projected.Adoption is null && projected.ErrorCode != SchematicNativeAdditionProjection.ResolutionRequired)
            throw new AutomationException(projected.ErrorCode ?? Invalid, projected.ErrorMessage ?? "The choices do not produce a valid repeated sheet.");
        if (projected.SheetComponentRequests.Count != 0 && resolution.SymbolOwners.Count != 0)
            throw new AutomationException(Invalid, "Give every missing component reference before answering the remaining symbol-owner questions.");
        token.ThrowIfCancellationRequested();
        return (store.Save(state, saved.RevisionToken), projected);
    }

    internal static string SnapshotToken(DesignRecoveryState state)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(byte[] bytes)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"));
            hash.AppendData(bytes);
        }
        Add(Encoding.UTF8.GetBytes(state.InstanceId.ToString("D") + "/" + state.NativeRevision.Epoch + "/"
            + state.NativeRevision.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + state.TrackingComplete));
        Add(Encoding.UTF8.GetBytes(SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries)));
        Add(state.DesiredFileBytes);
        Add(Encoding.UTF8.GetBytes(SchematicDataXml.Write(state.Observed)));
        // Bind each checkpoint's revision, memberships and limitations. Its
        // owning hierarchy is already bound above, so it need not be serialized twice.
        void Electrical(KiCad.Automation.Protocol.SchematicElectricalState? electrical) => Add(electrical is null ? []
            : JsonSerializer.SerializeToUtf8Bytes(new { electrical.Hierarchy.Revision.Epoch, electrical.Hierarchy.Revision.Sequence,
                electrical.Hierarchy.TrackingComplete, Nets = electrical.Nets.Select(SchematicDataXml.Write).ToArray(),
                Limitations = electrical.Limitations.ToArray() }));
        Electrical(state.BaselineElectrical); Electrical(state.ObservedElectrical);
        Add(state.NativeFileLocations?.ToByteArray() ?? []);
        foreach (var library in state.KnowledgeLibraries) Add(Encoding.UTF8.GetBytes(ComponentKnowledgeXml.WriteLibrary(library)));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static DesignRepeatedSheetResolution? Current(DesignRecoveryState state) =>
        state.RepeatedSheetResolution is { } value && value.SnapshotToken == SnapshotToken(state) ? value : null;

    internal static bool Same(DesignRepeatedSheetResolution? first, DesignRepeatedSheetResolution? second) =>
        JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);

    internal static void Validate(DesignRepeatedSheetResolution resolution)
    {
        if (resolution.SnapshotToken is not { Length: 64 } || !resolution.SnapshotToken.All(char.IsAsciiHexDigitLower)
            || resolution.ComponentReferences is null || resolution.SymbolOwners is null
            || resolution.ComponentReferences.Any(r => r is null || r.SheetInstanceId == Guid.Empty || r.ComponentDefinitionId == Guid.Empty
                || string.IsNullOrWhiteSpace(r.Reference) || r.Reference != r.Reference.Trim() || r.Reference.Any(char.IsControl))
            || resolution.ComponentReferences.Select(r => (r.SheetInstanceId, r.ComponentDefinitionId)).Distinct().Count() != resolution.ComponentReferences.Count
            || resolution.SymbolOwners.Any(a => a is null || a.NativeObjectId == Guid.Empty || a.NativePath is not { Count: > 0 }
                || a.NativePath.Any(p => p == Guid.Empty)))
            throw new AutomationException(Invalid, "Give exact sheet and component-definition identities, nonempty references and complete paths for symbol-owner choices.");
        var keys = resolution.SymbolOwners.Select(a => SchematicDesignBindings.PathKey(a.NativePath!) + "#" + a.NativeObjectId.ToString("D"));
        if (keys.Distinct(StringComparer.Ordinal).Count() != resolution.SymbolOwners.Count)
            throw new AutomationException(Invalid, "Answer each symbol on a sheet path once.");
    }
}
