using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>Native owners that appeared in KiCad since the last synchronization, projected onto the design: owners a
/// verified history restores (<see cref="History"/> is the history used), or symbols placed in KiCad that become new
/// components (<see cref="AddedOccurrences"/>; <see cref="History"/> is then null). Symbols removed in the same KiCad
/// change are already removed from <see cref="BindingCandidate"/>, with <see cref="ComponentChanges"/> for the
/// components they retired.</summary>
internal sealed record SchematicNativeRestorationResult(SchematicDesign BindingCandidate, SchematicOwnershipHistory? History,
    IReadOnlyList<Guid> RestoredOccurrences, IReadOnlyList<Guid> RestoredComponents)
{
    public IReadOnlyList<Guid> AddedOccurrences { get; init; } = [];
    public IReadOnlyList<Guid> AddedComponents { get; init; } = [];
    public IReadOnlyList<Guid> AddedParts { get; init; } = [];
    public IReadOnlyList<Guid> RemovedOccurrences { get; init; } = [];
    public IReadOnlyList<ComponentReferenceChange> ComponentChanges { get; init; } = [];

    /// <summary>The verified history a restoration used; only restorations have one.</summary>
    public SchematicOwnershipHistory Source => History
        ?? throw new AutomationException("native_ownership_history_missing", "Only a restoration from verified history has a source.");
}

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
            var candidate = inspection.Candidates.SingleOrDefault(c => c.Source.Receipt.OperationId == selected.HistoryOperationId);
            if (selected.SnapshotToken != inspection.SnapshotToken || candidate is null
                || candidate.Source.Receipt.PreviousXmlSha256 != selected.HistoryXmlSha256)
                throw Error("native_owner_resolution_stale", "The selected history no longer matches this snapshot; inspect and choose again.");
            return candidate;
        }
        if (inspection.Candidates.Select(Key).Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw Error("ambiguous_native_ownership_history", "Verified histories disagree about restored identities or unresolved requirement bindings; select an explicit resolution.");
        return inspection.Candidates.OrderBy(c => c.Source.Receipt.OperationId).First();
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
        if (candidates.Any(c => c.Source.Latest)) candidates = candidates.Where(c => c.Source.Latest).ToList();
        else if (candidates.Any(c => c.Source.Receipt.NativeDocumentEpoch == state.NativeRevision.Epoch))
        {
            candidates = candidates.Where(c => c.Source.Receipt.NativeDocumentEpoch == state.NativeRevision.Epoch).ToList();
            ulong newest = candidates.Max(c => c.Source.Receipt.NativeRevisionSequence);
            candidates = candidates.Where(c => c.Source.Receipt.NativeRevisionSequence == newest).ToList();
        }
        return new(SnapshotToken(state, history, token), candidates.OrderBy(c => c.Source.Receipt.OperationId).ToArray());
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
        var historical = restoration.Source.Design.Engineering;
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
        var old = candidate.Source.Design.Engineering;
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

/// <summary>A decision the synchronization cannot take from exact identities alone. It names the native symbol KiCad
/// shows, the sheet it sits on and the exact candidates; nothing is published until the question is answered.</summary>
public sealed record SchematicOwnershipResolutionRequest(string Code, Guid NativeObjectId, string NativePath,
    Guid SheetInstanceId, string Reference, string LibraryId, int Unit, IReadOnlyList<Guid> CandidatePartIds,
    IReadOnlyList<Guid> CandidateComponentIds, string Reason);

internal sealed record SchematicNativeAdditionResult(SchematicNativeRestorationResult? Adoption,
    IReadOnlyList<SchematicOwnershipResolutionRequest> Requests, IReadOnlyList<SchematicBindingIssue> Issues,
    IReadOnlyList<HierarchyCoverageGap> CoverageGaps, string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>Symbols placed in KiCad since the last synchronization become design components (ledger p74ee7c1da24272d9).
/// Each new symbol becomes one new component instance on the sheet instance KiCad shows it on, with identities derived
/// from the circuit, the native sheet path and the symbol's own UUID, so a repeated plan, a replay and a redo give the
/// same identities. Its part is decided only by exact identity: an existing part whose symbols KiCad draws, or whose
/// declared symbol is, the same library symbol with exactly the same units and pins (numbers, names and units); a new
/// part with that library symbol's pins when there is none. Anything else is a resolution request, never a guess: several
/// such parts, a new unit of a multi-unit part that an existing component may be missing, or several new units of one
/// multi-unit part. Symbols removed in the same KiCad change are removed as the removal projection removes them, with
/// their instructions retained. Sheets that appear, disappear or move are not adopted here (sheet_ownership_changed).</summary>
public static class SchematicNativeAdditionProjection
{
    public const string ResolutionRequired = "native_ownership_resolution_required";
    public const string PartAmbiguous = "native_part_ambiguous";
    public const string UnitOwnerAmbiguous = "native_unit_owner_ambiguous";
    public const string UnitGroupingAmbiguous = "native_unit_grouping_ambiguous";

    /// <summary>The stable identity a symbol placed in KiCad gives the design object of <paramref name="kind"/>
    /// ("component", "definition" or "occurrence").</summary>
    public static Guid AdoptedIdentity(string kind, Guid circuitId, IReadOnlyList<Guid> nativePath, Guid nativeObjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(nativePath);
        return Stable("kicad-native-adoption-v1\n" + kind + "\n" + circuitId.ToString("D") + "\n"
            + string.Join('/', nativePath.Select(p => p.ToString("D"))) + "\n" + nativeObjectId.ToString("D"));
    }

    /// <summary>The stable identity of the new part a library symbol with exactly these units and pins gives the circuit.</summary>
    public static Guid AdoptedPartIdentity(Guid circuitId, string libraryId, int units, IEnumerable<PartPin> pins)
    {
        ArgumentNullException.ThrowIfNull(libraryId);
        ArgumentNullException.ThrowIfNull(pins);
        return Stable("kicad-native-adoption-v1\npart\n" + circuitId.ToString("D") + "\n" + libraryId + "\n" + Signature(units, pins));
    }

    internal static SchematicNativeAdditionResult Project(DesignRecoveryState state, IReadOnlyList<SchematicOwnershipHistory>? history,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var gaps = new List<HierarchyCoverageGap>();
        SchematicNativeAdditionResult Failure(string code, string message, IReadOnlyList<SchematicBindingIssue>? issues = null) =>
            new(null, [], issues ?? [], gaps.Distinct().ToArray(), code, message);
        try
        {
            var baseline = state.Baseline; var observed = state.Observed; var libraries = state.KnowledgeLibraries;
            var original = SchematicDesignBindings.Inspect(baseline, libraries, token);
            gaps.AddRange(original.CoverageGaps);
            if (!original.IdentitiesResolved) return Failure("unresolved_design_bindings", "Resolve the saved design's bindings first.", original.Issues);
            var topology = SchematicHierarchyTopology.Inspect(observed, token);
            gaps.AddRange(topology.CoverageGaps);
            if (!topology.IsValid) return Failure("invalid_native_hierarchy", "Resolve the reported native hierarchy before adopting new symbols.");
            static string Key(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(p => p.Value));
            var screens = observed.Instances.ToDictionary(Key, StringComparer.Ordinal);
            var before = baseline.Schematic.Instances.ToDictionary(Key, StringComparer.Ordinal);
            if (!screens.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(before.Keys)
                || screens.Any(pair => pair.Value.Metadata.ScreenId.Value != before[pair.Key].Metadata.ScreenId.Value))
                return Failure("sheet_ownership_changed", "Sheet insertion, removal or reparenting requires explicit ownership reconciliation.");

            var circuit = baseline.Engineering.Circuit;
            var components = circuit.Components.ToDictionary(c => c.Id);
            var sheetPaths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
            var bound = baseline.SymbolBindings.Select(b =>
            {
                var occurrence = circuit.Symbols.Single(s => s.Id == b.SymbolOccurrenceId);
                return sheetPaths[occurrence.EffectiveSheetInstanceId(components[occurrence.ComponentId])] + "#" + b.NativeObjectId.ToString("D");
            }).ToHashSet(StringComparer.Ordinal);
            var added = screens.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(pair => pair.Value.Items
                    .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => (Path: pair.Key, Symbol: i.Unpack<SchematicSymbolInstance>())))
                .Where(x => !bound.Contains(x.Path + "#" + x.Symbol.Id.Value))
                .OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Symbol.Id.Value, StringComparer.Ordinal).ToArray();
            if (added.Length == 0)
                return Failure("electrical_ownership_changed", "Unit changes or changed library pin identities require explicit ownership reconciliation.");

            // A symbol KiCad shows again after an undo belongs to the verified history that knew it, not to a new
            // component. Restoring it together with new symbols is two decisions; take them one at a time.
            var historical = (history ?? []).SelectMany(h => h.Design.SymbolBindings.Select(b => b.NativeObjectId)).ToHashSet();
            if (added.Any(x => Guid.TryParse(x.Symbol.Id.Value, out var id) && historical.Contains(id)))
                return Failure("native_restoration_with_additions",
                    "KiCad shows symbols an earlier synchronized design had together with newly placed ones. Synchronize them separately: "
                    + "undo the new placement in KiCad, synchronize the restored symbols, then redo it.");

            // Removals first, exactly as a removal-only change is projected.
            var withoutAdded = observed.Clone();
            foreach (var screen in withoutAdded.Instances)
            {
                string path = Key(screen);
                for (int i = screen.Items.Count - 1; i >= 0; --i)
                    if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor)
                        && !bound.Contains(path + "#" + screen.Items[i].Unpack<SchematicSymbolInstance>().Id.Value))
                        screen.Items.RemoveAt(i);
            }
            var removal = SchematicNativeRemovalProjection.Project(baseline, withoutAdded, libraries, token);
            gaps.AddRange(removal.CoverageGaps);
            if (removal.BindingCandidate is null)
                return Failure(removal.ErrorCode ?? "electrical_ownership_changed", removal.ErrorMessage
                    ?? "Unit changes or changed library pin identities require explicit ownership reconciliation.", removal.Issues);
            var kept = removal.BindingCandidate;
            var keptCircuit = kept.Engineering.Circuit;
            var keptComponents = keptCircuit.Components.ToDictionary(c => c.Id);
            var definitions = keptCircuit.Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);

            // Library evidence for each existing part: the library symbols KiCad draws its units with, and its declared symbol.
            var evidence = new Dictionary<Guid, HashSet<string>>();
            void Evidence(Guid part, string library) { if (!evidence.TryGetValue(part, out var set)) evidence.Add(part, set = new(StringComparer.Ordinal)); set.Add(library); }
            var keptPaths = kept.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
            foreach (var binding in kept.SymbolBindings)
            {
                var occurrence = keptCircuit.Symbols.Single(s => s.Id == binding.SymbolOccurrenceId);
                var owner = keptComponents[occurrence.ComponentId];
                var symbol = screens[keptPaths[occurrence.EffectiveSheetInstanceId(owner)]].Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                    .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == binding.NativeObjectId.ToString("D"));
                Evidence(definitions[owner.DefinitionId].PartId, LibraryKey(symbol));
            }
            foreach (var declared in baseline.PartSymbols ?? [])
                if (declared.LibraryId is { } library) Evidence(declared.PartId, LibraryKey(library));

            var requests = new List<SchematicOwnershipResolutionRequest>();
            var decided = new List<(string Path, SchematicSymbolInstance Symbol, Guid Sheet, Guid Part, PartDefinition? NewPart)>();
            var newParts = new Dictionary<Guid, PartDefinition>();
            foreach (var (path, symbol) in added)
            {
                token.ThrowIfCancellationRequested();
                var sheet = kept.SheetBindings.Single(b => SchematicDesignBindings.PathKey(b.NativePath) == path);
                string screenId = screens[path].Metadata.ScreenId.Value;
                if (screens.Values.Count(s => s.Metadata.ScreenId.Value == screenId) != 1)
                    return Failure("native_addition_repeated_sheet_unsupported",
                        "A symbol placed on a sheet shown several times cannot be adopted yet; synchronize it from the XML instead.");
                if (!Guid.TryParseExact(symbol.Id?.Value, "D", out Guid nativeId) || nativeId == Guid.Empty
                    || symbol.Definition is null || symbol.Unit is null || symbol.Unit.Unit < 1 || symbol.Unit.Unit > (int)symbol.Definition.UnitCount
                    || string.IsNullOrWhiteSpace(symbol.ReferenceField?.Text?.Text_))
                    return Failure("native_addition_incomplete", "A symbol placed in KiCad needs its identity, definition, unit and reference to be adopted.");
                string library = LibraryKey(symbol);
                var pins = Pins(symbol);
                int units = checked((int)symbol.Definition.UnitCount);
                string signature = Signature(units, pins);
                var candidates = keptCircuit.Parts.Where(p => evidence.TryGetValue(p.Id, out var libraries) && libraries.Contains(library)
                    && Signature(p.Units, p.Pins) == signature).Select(p => p.Id).ToList();
                Guid derived = AdoptedPartIdentity(keptCircuit.Id, library, units, pins);
                if (keptCircuit.Parts.SingleOrDefault(p => p.Id == derived) is { } earlier && Signature(earlier.Units, earlier.Pins) == signature
                    && !candidates.Contains(derived))
                    candidates.Add(derived);
                if (candidates.Count > 1)
                {
                    requests.Add(Request(PartAmbiguous, candidates.Order().ToArray(), [],
                        "Several parts are drawn with this library symbol and have exactly its pins; choose the part this symbol is."));
                    continue;
                }
                PartDefinition? created = null;
                if (candidates.Count == 0)
                {
                    created = newParts.TryGetValue(derived, out var shared) ? shared : new PartDefinition(derived,
                        (symbol.LibraryId ?? symbol.Definition.Id)?.EntryName is { Length: > 0 } entry ? entry : library, units, pins);
                    newParts[derived] = created;
                }
                decided.Add((path, symbol, sheet.SheetInstanceId, created?.Id ?? candidates[0], created));

                SchematicOwnershipResolutionRequest Request(string code, IReadOnlyList<Guid> parts, IReadOnlyList<Guid> owners, string reason) =>
                    new(code, nativeId, path, sheet.SheetInstanceId, symbol.ReferenceField.Text.Text_, library, symbol.Unit.Unit, parts, owners, reason);
            }
            // Units of a multi-unit part: KiCad joins units into one component by their reference designator, which is a
            // name. Adopt one only when no component could own it: every existing component of the part already draws that
            // unit, and it is the only new unit of that part.
            foreach (var group in decided.Where(d => (d.NewPart?.Units ?? keptCircuit.Parts.Single(p => p.Id == d.Part).Units) > 1).GroupBy(d => d.Part))
            {
                var owners = keptCircuit.Components.Where(c => definitions[c.DefinitionId].PartId == group.Key).ToArray();
                foreach (var entry in group)
                {
                    var missing = owners.Where(c => !keptCircuit.Symbols.Any(s => s.ComponentId == c.Id && s.Unit == entry.Symbol.Unit.Unit))
                        .Select(c => c.Id).Order().ToArray();
                    if (missing.Length != 0 || group.Count() > 1)
                        requests.Add(new(missing.Length != 0 ? UnitOwnerAmbiguous : UnitGroupingAmbiguous, Guid.Parse(entry.Symbol.Id.Value),
                            entry.Path, entry.Sheet, entry.Symbol.ReferenceField.Text.Text_, LibraryKey(entry.Symbol), entry.Symbol.Unit.Unit, [group.Key],
                            missing.Length != 0 ? missing : group.Where(g => g != entry).Select(g => AdoptedIdentity("component", keptCircuit.Id,
                                PathOf(g.Path), Guid.Parse(g.Symbol.Id.Value))).Order().ToArray(),
                            missing.Length != 0 ? "An existing component of this part does not draw this unit; choose whether the new unit is one of its units."
                                : "Several new units of this multi-unit part were placed; choose which of them are one component."));
                }
            }
            if (requests.Count != 0)
                return new(null, requests.OrderBy(r => r.NativePath, StringComparer.Ordinal).ThenBy(r => r.NativeObjectId).ToArray(), [],
                    gaps.Distinct().ToArray(), ResolutionRequired,
                    "KiCad shows new symbols whose design owner cannot be decided from exact identities; answer the resolution requests.");

            // Every decision is exact: create the parts, components and occurrences.
            var sheets = keptCircuit.SheetInstances.ToDictionary(s => s.Id);
            var addedDefinitions = new Dictionary<Guid, List<ComponentDefinition>>();
            var addedComponents = new List<ComponentInstance>(); var addedOccurrences = new List<SymbolOccurrence>();
            var addedBindings = new List<SchematicSymbolBinding>();
            foreach (var (path, symbol, sheet, part, _) in decided)
            {
                var nativeId = Guid.Parse(symbol.Id.Value); var nativePath = PathOf(path);
                Guid definition = AdoptedIdentity("definition", keptCircuit.Id, nativePath, nativeId);
                Guid component = AdoptedIdentity("component", keptCircuit.Id, nativePath, nativeId);
                Guid occurrence = AdoptedIdentity("occurrence", keptCircuit.Id, nativePath, nativeId);
                Guid owner = sheets[sheet].DefinitionId;
                if (!addedDefinitions.TryGetValue(owner, out var list)) addedDefinitions.Add(owner, list = []);
                list.Add(new(definition, part, symbol.ValueField?.Text?.Text_ ?? ""));
                addedComponents.Add(new(component, definition, sheet, symbol.ReferenceField.Text.Text_));
                addedOccurrences.Add(new(occurrence, component, symbol.Unit.Unit, SchematicModelProjection.Placement(symbol)));
                addedBindings.Add(new(occurrence, nativeId));
            }
            var next = keptCircuit with
            {
                Parts = [.. keptCircuit.Parts, .. newParts.Values.OrderBy(p => p.Id)],
                Sheets = [.. keptCircuit.Sheets.Select(s => addedDefinitions.TryGetValue(s.Id, out var extra) ? s with { Components = [.. s.Components, .. extra] } : s)],
                Components = [.. keptCircuit.Components, .. addedComponents],
                Symbols = [.. keptCircuit.Symbols, .. addedOccurrences]
            };
            try { next.Validate(); }
            catch (AutomationException error)
            {
                return Failure("native_addition_conflict", "The symbols placed in KiCad cannot join the design as they are: " + error.Message);
            }
            var design = kept with { Engineering = kept.Engineering with { Circuit = next }, Schematic = observed.Clone(),
                SymbolBindings = [.. kept.SymbolBindings, .. addedBindings] };
            design.Engineering.Validate(libraries);
            var report = SchematicDesignBindings.Inspect(design, libraries, token);
            gaps.AddRange(report.CoverageGaps);
            if (!report.IdentitiesResolved)
                return Failure("unresolved_added_bindings", "The adopted symbols do not resolve every exact native object.", report.Issues);
            return new(new SchematicNativeRestorationResult(design, null, [], [])
            {
                AddedOccurrences = [.. addedOccurrences.Select(o => o.Id).Order()],
                AddedComponents = [.. addedComponents.Select(c => c.Id).Order()],
                AddedParts = [.. newParts.Keys.Order()],
                RemovedOccurrences = removal.RemovedOccurrences, ComponentChanges = removal.ComponentChanges
            }, [], [], gaps.Distinct().ToArray());
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
    }

    private static IReadOnlyList<Guid> PathOf(string path) => [.. path.Split('/').Select(Guid.Parse)];

    internal static string LibraryKey(SchematicSymbolInstance symbol) =>
        LibraryKey(symbol.LibraryId ?? symbol.Definition?.Id ?? new Kiapi.Common.Types.LibraryIdentifier());

    private static string LibraryKey(Kiapi.Common.Types.LibraryIdentifier library) =>
        (library.LibraryNickname.Length == 0 ? "" : library.LibraryNickname + ":") + library.EntryName;

    // The electrical pins of the placed body: every unit's pins of the symbol's body style and the common ones.
    private static IReadOnlyList<PartPin> Pins(SchematicSymbolInstance symbol)
    {
        int style = symbol.BodyStyle?.Style is > 0 ? symbol.BodyStyle.Style : 1;
        return [.. symbol.Definition.Items.Where(c => c.Item?.Is(SchematicPin.Descriptor) == true
                && (c.BodyStyle?.Style ?? 0) is var body && (body == 0 || body == style))
            .Select(c => (Pin: c.Item.Unpack<SchematicPin>(), Unit: c.Unit?.Unit ?? 0))
            .Select(x => new PartPin(x.Pin.Number, x.Pin.Name, x.Unit)).Distinct()
            .OrderBy(p => p.Number, StringComparer.Ordinal).ThenBy(p => p.Unit)];
    }

    private static string Signature(int units, IEnumerable<PartPin> pins) => JsonSerializer.Serialize(new
    {
        units, pins = pins.OrderBy(p => p.Number, StringComparer.Ordinal).ThenBy(p => p.Unit).Select(p => new { p.Number, p.Name, p.Unit })
    });

    private static Guid Stable(string text)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x50); digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }
}
