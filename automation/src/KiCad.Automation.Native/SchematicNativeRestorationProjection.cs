using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal sealed record SchematicNativeRestorationResult(SchematicDesign BindingCandidate, SchematicOwnershipHistory History,
    IReadOnlyList<Guid> RestoredOccurrences, IReadOnlyList<Guid> RestoredComponents);

internal sealed record SchematicOwnershipInspection(string SnapshotToken, IReadOnlyList<SchematicNativeRestorationResult> Candidates);

/// <summary>Recover identity declarations from verified deletion predecessors.
/// Current requirements and live objects are never replaced with historical text.</summary>
internal static class SchematicNativeRestorationProjection
{
    internal static SchematicNativeRestorationResult Project(DesignRecoveryState state,
        IReadOnlyList<SchematicOwnershipHistory> history, CancellationToken token)
    {
        var inspection = Inspect(state, history, token);
        if (state.OwnershipResolution is { } selected)
        {
            var candidate = inspection.Candidates.SingleOrDefault(c => c.History.Receipt.OperationId == selected.HistoryOperationId);
            if (selected.SnapshotToken != inspection.SnapshotToken || candidate is null
                || candidate.History.Receipt.PreviousXmlSha256 != selected.HistoryXmlSha256)
                throw Error("native_owner_resolution_stale", "The selected history no longer matches this snapshot; inspect and choose again.");
            return candidate;
        }
        if (inspection.Candidates.Select(Key).Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw Error("ambiguous_native_ownership_history", "Verified histories disagree about restored identities or unresolved requirement bindings; select an explicit resolution.");
        return inspection.Candidates.OrderBy(c => c.History.Receipt.OperationId).First();
    }

    internal static SchematicOwnershipInspection Inspect(DesignRecoveryState state,
        IReadOnlyList<SchematicOwnershipHistory> history, CancellationToken token)
    {
        string observedOwners = SchematicNetReconciliation.NativeOwners(state.Observed);
        string currentTopology = SchematicNetReconciliation.Topology(state.Baseline.Engineering.Circuit);
        string currentBindings = SchematicNetReconciliation.Bindings(state.Baseline);
        var candidates = new List<SchematicNativeRestorationResult>();
        foreach (var entry in history)
        {
            token.ThrowIfCancellationRequested();
            if (SchematicNetReconciliation.NativeOwners(entry.Design.Schematic) != observedOwners) continue;
            var report = SchematicDesignBindings.Inspect(entry.Design, state.KnowledgeLibraries, token);
            if (!report.IdentitiesResolved || report.Differences.Any(d => d.Field == "unit")) continue;
            var reduced = SchematicNativeRemovalProjection.Project(entry.Design, state.Baseline.Schematic, state.KnowledgeLibraries, token);
            if (reduced.BindingCandidate is null || reduced.RemovedOccurrences.Count == 0
                || SchematicNetReconciliation.Topology(reduced.BindingCandidate.Engineering.Circuit) != currentTopology
                || SchematicNetReconciliation.Bindings(reduced.BindingCandidate) != currentBindings) continue;
            candidates.Add(Build(state, entry, token));
        }
        if (candidates.Count == 0)
            throw Error("native_ownership_history_not_matched", "No verified deletion predecessor matches the exact restored native owners.");
        // Native revision order is meaningful only within one document epoch.
        // Prefer the current receipt; never order different epochs by UUID/time.
        if (candidates.Any(c => c.History.Latest)) candidates = candidates.Where(c => c.History.Latest).ToList();
        else if (candidates.Any(c => c.History.Receipt.NativeDocumentEpoch == state.NativeRevision.Epoch))
        {
            candidates = candidates.Where(c => c.History.Receipt.NativeDocumentEpoch == state.NativeRevision.Epoch).ToList();
            ulong newest = candidates.Max(c => c.History.Receipt.NativeRevisionSequence);
            candidates = candidates.Where(c => c.History.Receipt.NativeRevisionSequence == newest).ToList();
        }
        return new(SnapshotToken(state, history, token), candidates.OrderBy(c => c.History.Receipt.OperationId).ToArray());
    }

    private static string SnapshotToken(DesignRecoveryState state, IReadOnlyList<SchematicOwnershipHistory> history, CancellationToken token)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Bytes(byte[] bytes)
        {
            token.ThrowIfCancellationRequested();
            Span<byte> length = stackalloc byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(length, bytes.LongLength);
            digest.AppendData(length); digest.AppendData(bytes);
        }
        void Text(string value) => Bytes(Encoding.UTF8.GetBytes(value));
        Text("kicad-ownership-resolution-v1");
        Text(JsonSerializer.Serialize(new { state.OriginId, state.InstanceId, state.NativeRevision, state.TrackingComplete, state.HierarchyResolution }));
        Text(SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries)); Bytes(state.DesiredFileBytes);
        Text(SchematicDataXml.Write(state.Observed));
        Bytes(state.BaselineElectrical?.ToByteArray() ?? []); Bytes(state.ObservedElectrical?.ToByteArray() ?? []);
        foreach (var library in state.KnowledgeLibraries) Text(ComponentKnowledgeXml.WriteLibrary(library));
        foreach (var entry in history.OrderBy(h => h.Receipt.OperationId)) Text(JsonSerializer.Serialize(new { entry.Receipt, entry.Latest }));
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static SchematicNativeRestorationResult Build(DesignRecoveryState state, SchematicOwnershipHistory history, CancellationToken token)
    {
        var baseline = state.Baseline; var current = baseline.Engineering.Circuit; var old = history.Design.Engineering.Circuit;
        var componentIds = current.Components.Select(c => c.Id).ToHashSet();
        var symbolIds = current.Symbols.Select(s => s.Id).ToHashSet();
        var definitions = current.Sheets.SelectMany(s => s.Components).Select(c => c.Id).ToHashSet();
        var restoredComponents = old.Components.Where(c => !componentIds.Contains(c.Id)).OrderBy(c => c.Id).ToArray();
        var restoredSymbols = old.Symbols.Where(s => !symbolIds.Contains(s.Id)).OrderBy(s => s.Id).ToArray();
        var next = current with
        {
            Components = [.. current.Components, .. restoredComponents],
            Symbols = [.. current.Symbols, .. restoredSymbols],
            Sheets = current.Sheets.Select(s => s with { Components = [.. s.Components,
                .. old.Sheets.Single(o => o.Id == s.Id).Components.Where(c => !definitions.Contains(c.Id)).OrderBy(c => c.Id)] }).ToArray()
        };
        var addedSymbols = restoredSymbols.Select(s => s.Id).ToHashSet();
        var design = baseline with { Engineering = baseline.Engineering with { Circuit = next }, Schematic = state.Observed.Clone(),
            SymbolBindings = [.. baseline.SymbolBindings, .. history.Design.SymbolBindings.Where(b => addedSymbols.Contains(b.SymbolOccurrenceId))
                .OrderBy(b => b.SymbolOccurrenceId)] };
        var native = SchematicModelProjection.NativeSymbols(design, state.Observed);
        string Field(IEnumerable<SymbolOccurrence> symbols, bool reference)
        {
            var values = symbols.Select(s => reference ? native[s.Id].ReferenceField?.Text?.Text_ : native[s.Id].ValueField?.Text?.Text_)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length != 1 || values[0] is null)
                throw Error("inconsistent_restored_properties", "Restored units must agree about their component reference and shared value.");
            return values[0]!;
        }
        var restoredIds = restoredComponents.Select(c => c.Id).ToHashSet();
        var owners = next.Components.ToDictionary(c => c.Id);
        next = next with
        {
            Components = next.Components.Select(c => restoredIds.Contains(c.Id)
                ? c with { Reference = Field(next.Symbols.Where(s => s.ComponentId == c.Id), true) } : c).ToArray(),
            Sheets = next.Sheets.Select(s => s with { Components = s.Components.Select(c => definitions.Contains(c.Id) ? c
                : c with { Value = Field(next.Symbols.Where(s => owners[s.ComponentId].DefinitionId == c.Id), false) }).ToArray() }).ToArray(),
            Symbols = next.Symbols.Select(s => addedSymbols.Contains(s.Id) ? s with
                { Placement = SchematicModelProjection.Placement(native[s.Id]),
                    SheetInstanceId = s.EffectiveSheetInstanceId(owners[s.ComponentId]) == owners[s.ComponentId].SheetInstanceId
                        ? null : s.EffectiveSheetInstanceId(owners[s.ComponentId]) } : s).ToArray()
        };
        token.ThrowIfCancellationRequested(); next.Validate();
        // These provisional nets are only for binding comparison. The real
        // electrical checkpoint supplies the final pin partition later.
        var engineering = ComponentReferenceRetention.Retain(baseline.Engineering, next, [], state.KnowledgeLibraries);
        design = design with { Engineering = engineering };
        var report = SchematicDesignBindings.Inspect(design, state.KnowledgeLibraries, token);
        if (!report.IdentitiesResolved)
            throw Error("unresolved_restored_bindings", "The restored declarations do not resolve every exact native object.");
        return new(design, history, addedSymbols.Order().ToArray(), restoredIds.Order().ToArray());
    }

    internal static EngineeringDesign ResolveRetained(EngineeringDesign design, SchematicNativeRestorationResult restoration,
        IReadOnlyCollection<Guid> restoredNets, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries)
    {
        var historical = restoration.History.Design.Engineering;
        foreach (var pending in (design.Structure.UnresolvedComponentReferences ?? []).ToArray())
            if (restoration.RestoredComponents.Contains(pending.FormerTarget.ComponentId)
                && !(historical.Structure.UnresolvedComponentReferences ?? []).Any(r => r.OwnerId == pending.OwnerId
                    && r.Slot == pending.Slot && r.FormerTarget == pending.FormerTarget))
                design = ComponentReferenceRetention.Resolve(design, pending.OwnerId, pending.Slot, pending.FormerTarget, pending.FormerTarget, libraries);
        foreach (var pending in (design.UnresolvedGuidanceBindings ?? []).ToArray())
            if (restoration.RestoredComponents.Contains(pending.ComponentInstanceId)
                && !(historical.UnresolvedGuidanceBindings ?? []).Any(r => r.ComponentInstanceId == pending.ComponentInstanceId))
                design = ComponentReferenceRetention.ResolveGuidance(design, pending.ComponentInstanceId, pending.ComponentInstanceId, libraries);
        foreach (var pending in (design.Structure.UnresolvedNetBindings ?? []).ToArray())
            if (restoredNets.Contains(pending.FormerNetId)
                && !(historical.Structure.UnresolvedNetBindings ?? []).Any(r => r.OwnerId == pending.OwnerId && r.FormerNetId == pending.FormerNetId))
                design = design with { Structure = design.Structure.ResolveUnresolvedNet(design.Circuit, pending.OwnerId, pending.FormerNetId, pending.FormerNetId) };
        design.Validate(libraries); return design;
    }

    private static string Key(SchematicNativeRestorationResult candidate)
    {
        var circuit = candidate.BindingCandidate.Engineering.Circuit;
        var old = candidate.History.Design.Engineering;
        return JsonSerializer.Serialize(new
        {
            topology = SchematicNetReconciliation.Topology(circuit), bindings = SchematicNetReconciliation.Bindings(candidate.BindingCandidate),
            unresolvedComponents = (old.Structure.UnresolvedComponentReferences ?? []).OrderBy(r => r.OwnerId).ThenBy(r => r.Slot)
                .ThenBy(r => r.FormerTarget.ComponentId).ThenBy(r => r.FormerTarget.PinNumber, StringComparer.Ordinal),
            unresolvedGuidance = (old.UnresolvedGuidanceBindings ?? []).OrderBy(r => r.ComponentInstanceId),
            unresolvedNets = (old.Structure.UnresolvedNetBindings ?? []).OrderBy(r => r.OwnerId).ThenBy(r => r.FormerNetId),
            nets = old.Circuit.Nets.OrderBy(n => n.Id).Select(n => new { n.Id, n.Name, pins = SchematicNetReconciliation.Key(n.Pins) })
        });
    }

    private static AutomationException Error(string code, string message) => new(code, message);
}
