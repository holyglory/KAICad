using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public sealed record SchematicPinRebindAnswer(string FormerPin, string? ReplacementPin);
public sealed record DesignComponentRebindResolution(string SnapshotToken, IReadOnlyList<SchematicOwnershipAnswer> Answers);

/// <summary>Explicit changes to already bound native symbols. The saved native counterpart,
/// not an optional library declaration, establishes whether its ownership changed.</summary>
internal static class SchematicNativeRebinding
{
    internal const string Required = "native_component_rebind_required";
    private sealed record Change(SymbolOccurrence Occurrence, ComponentInstance Component, PartDefinition Former,
        SchematicSymbolInstance Before, SchematicSymbolInstance After, string Path);

    internal static string Signature(SchematicSymbolInstance symbol) => JsonSerializer.Serialize(new
    {
        library = SchematicNativeAdditionProjection.LibraryKey(symbol), units = symbol.Definition?.UnitCount,
        unit = symbol.Unit?.Unit, style = symbol.BodyStyle?.Style,
        pins = symbol.Definition?.Items.Where(c => c.Item?.Is(SchematicPin.Descriptor) == true)
            .Select(c => (Child: c, Pin: c.Item.Unpack<SchematicPin>()))
            .OrderBy(p => p.Pin.LibraryPinId?.Value ?? p.Pin.Id?.Value, StringComparer.Ordinal)
            .ThenBy(p => p.Pin.Number, StringComparer.Ordinal).Select(p => new
            {
                id = p.Pin.LibraryPinId?.Value ?? p.Pin.Id?.Value, p.Pin.Number, p.Pin.Name,
                unit = p.Child.Unit?.Unit ?? 0, style = p.Child.BodyStyle?.Style ?? 0,
                p.Pin.ActiveAlternate, p.Pin.ElectricalType
            })
    });

    private static IReadOnlyList<Change> Changes(DesignRecoveryState state, CancellationToken token)
    {
        var baseline = state.Baseline;
        if (!SchematicDesignBindings.Inspect(baseline, state.KnowledgeLibraries, token).IdentitiesResolved
            || !SchematicHierarchyTopology.Inspect(state.Observed, token).IsValid) return [];
        var old = SchematicModelProjection.NativeSymbols(baseline, baseline.Schematic);
        var observed = state.Observed.Instances.SelectMany(s => s.Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => (Path: SchematicNativeSheetChanges.Key(s), Symbol: i.Unpack<SchematicSymbolInstance>())))
            .ToDictionary(x => x.Path + "#" + x.Symbol.Id.Value, x => x.Symbol, StringComparer.Ordinal);
        var owners = baseline.Engineering.Circuit.Components.ToDictionary(c => c.Id);
        var definitions = baseline.Engineering.Circuit.Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);
        var parts = baseline.Engineering.Circuit.Parts.ToDictionary(p => p.Id);
        var bindings = baseline.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId);
        var paths = baseline.SheetBindings.ToDictionary(s => s.SheetInstanceId, s => SchematicDesignBindings.PathKey(s.NativePath));
        var changes = new List<Change>();
        foreach (var occurrence in baseline.Engineering.Circuit.Symbols)
        {
            token.ThrowIfCancellationRequested();
            var owner = owners[occurrence.ComponentId]; string path = paths[occurrence.EffectiveSheetInstanceId(owner)];
            if (observed.TryGetValue(path + "#" + bindings[occurrence.Id].NativeObjectId.ToString("D"), out var current)
                && Signature(old[occurrence.Id]) != Signature(current))
                changes.Add(new(occurrence, owner, parts[definitions[owner.DefinitionId].PartId], old[occurrence.Id], current, path));
        }
        return changes;
    }

    internal static bool HasChanges(DesignRecoveryState state, CancellationToken token) => Changes(state, token).Count != 0;

    private static PartDefinition Replacement(Change change, Guid circuitId)
    {
        var symbol = change.After;
        if (symbol.Definition is null || symbol.Definition.UnitCount < 1 || symbol.Unit?.Unit is not > 0)
            throw Invalid("The replacement needs a complete native definition and unit before it can be selected.");
        int style = symbol.BodyStyle?.Style is > 0 ? symbol.BodyStyle.Style : 1;
        var pins = symbol.Definition.Items.Where(c => c.Item?.Is(SchematicPin.Descriptor) == true
                && ((c.BodyStyle?.Style ?? 0) == 0 || c.BodyStyle!.Style == style))
            .Select(c => (Child: c, Pin: c.Item.Unpack<SchematicPin>()))
            .Select(p => new PartPin(p.Pin.Number, p.Pin.Name, p.Child.Unit?.Unit ?? 0)).Distinct()
            .OrderBy(p => p.Number, StringComparer.Ordinal).ThenBy(p => p.Unit).ToArray();
        string library = SchematicNativeAdditionProjection.LibraryKey(symbol);
        return new(SchematicNativeAdditionProjection.AdoptedPartIdentity(circuitId, library,
            (int)symbol.Definition.UnitCount, pins), library.Length == 0 ? "Native replacement" : library,
            (int)symbol.Definition.UnitCount, pins);
    }

    private static bool SamePart(PartDefinition a, PartDefinition b) => a.Units == b.Units
        && a.Pins.ToHashSet().SetEquals(b.Pins);

    private static IReadOnlyList<Guid> Candidates(DesignRecoveryState state, Change change, PartDefinition replacement,
        IReadOnlyList<SchematicOwnershipHistory>? history)
    {
        string library = SchematicNativeAdditionProjection.LibraryKey(change.After);
        var declared = (state.Baseline.PartSymbols ?? []).Where(p =>
            (p.LibraryId.LibraryNickname.Length == 0 ? "" : p.LibraryId.LibraryNickname + ":") + p.LibraryId.EntryName == library)
            .Select(p => p.PartId).ToHashSet();
        if (SchematicNativeAdditionProjection.LibraryKey(change.Before) == library && SamePart(change.Former, replacement))
            declared.Add(change.Former.Id);
        foreach (var entry in history ?? [])
        {
            if (SchematicNetReconciliation.NativeOwners(entry.Design.Schematic) != SchematicNetReconciliation.NativeOwners(state.Observed)) continue;
            var previousOwner = entry.Design.Engineering.Circuit.Components.SingleOrDefault(c => c.Id == change.Component.Id);
            if (previousOwner is not null)
                declared.Add(entry.Design.Engineering.Circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == previousOwner.DefinitionId).PartId);
        }
        var candidates = state.Baseline.Engineering.Circuit.Parts.Where(p => declared.Contains(p.Id) && SamePart(p, replacement))
            .Select(p => p.Id).ToArray();
        return candidates.Length == 0 ? [replacement.Id] : candidates;
    }

    internal static SchematicNetReconciliationResult? Plan(DesignRecoveryState state, SchematicDesign desired, CancellationToken token,
        IReadOnlyList<SchematicOwnershipHistory>? history = null)
    {
        var changes = Changes(state, token);
        if (changes.Count == 0) return null;
        var requests = changes.Select(c =>
        {
            var replacement = Replacement(c, desired.Engineering.Circuit.Id);
            return new SchematicOwnershipResolutionRequest(Required, Guid.Parse(c.After.Id.Value), c.Path,
                c.Occurrence.EffectiveSheetInstanceId(c.Component), c.After.ReferenceField?.Text?.Text_ ?? c.Component.Reference,
                SchematicNativeAdditionProjection.LibraryKey(c.After), c.After.Unit.Unit, Candidates(state, c, replacement, history),
                [c.Component.Id], "Select the replacement part and map every former pin, or retain its instructions as unresolved.", c.Component.Id)
            { FormerComponentId = c.Component.Id, FormerPartId = c.Former.Id, FormerPins = c.Former.Pins, ReplacementPins = replacement.Pins };
        }).ToArray();
        var retained = state.ComponentRebindResolution;
        SchematicOwnershipHistory? source = null;
        if (retained is null || retained.SnapshotToken != SchematicRepeatedSheetChoices.SnapshotToken(state))
        {
            var proven = HistoricalAnswers(state, changes, history ?? [], token);
            if (proven is { } selected) { retained = selected.Resolution; source = selected.History; }
        }
        if (retained is null || retained.SnapshotToken != SchematicRepeatedSheetChoices.SnapshotToken(state))
            return new(null, [], [], [], [], SchematicNativeAdditionProjection.ResolutionRequired,
                "Answer the changed component and pin identities with kicad_design_ownership_answer; nothing was published.")
                { ResolutionRequests = requests };
        return Apply(state, desired, changes, requests, retained.Answers, token, source) with { ResolvedComponentRebind = retained };
    }

    private static (DesignComponentRebindResolution Resolution, SchematicOwnershipHistory History)? HistoricalAnswers(
        DesignRecoveryState state, IReadOnlyList<Change> changes, IReadOnlyList<SchematicOwnershipHistory> history, CancellationToken token)
    {
        var candidates = new Dictionary<string, (DesignComponentRebindResolution, SchematicOwnershipHistory)>(StringComparer.Ordinal);
        string observed = SchematicNetReconciliation.NativeOwners(state.Observed);
        foreach (var entry in history)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Receipt.CompletedComponentRebind is not { } applied
                || SchematicNetReconciliation.NativeOwners(entry.Design.Schematic) != observed) continue;
            var answers = new List<SchematicOwnershipAnswer>();
            foreach (var change in changes)
            {
                var past = entry.Design.Engineering.Circuit;
                var owner = past.Components.SingleOrDefault(c => c.Id == change.Component.Id);
                var sourceAnswers = applied.Answers.Where(a => a.ComponentId == change.Component.Id).ToArray();
                if (owner is null || sourceAnswers.Length == 0) break;
                var maps = sourceAnswers.Select(a => JsonSerializer.Serialize(a.PinMappings)).Distinct().ToArray();
                if (maps.Length != 1) break;
                var previousPart = past.Sheets.SelectMany(s => s.Components).Single(d => d.Id == owner.DefinitionId).PartId;
                var forward = sourceAnswers[0].PinMappings!;
                answers.Add(new(Guid.Parse(change.After.Id.Value), previousPart, owner.Id)
                { NativePath = change.Path.Split('/').Select(Guid.Parse).ToArray(), PinMappings = change.Former.Pins.Select(p =>
                    new SchematicPinRebindAnswer(p.Number, forward.SingleOrDefault(m => m.ReplacementPin == p.Number)?.FormerPin)).ToArray() });
            }
            if (answers.Count != changes.Count) continue;
            var resolution = new DesignComponentRebindResolution(SchematicRepeatedSheetChoices.SnapshotToken(state), answers);
            // Include historical requirements/nets: equal native content alone cannot select different model meanings.
            string key = JsonSerializer.Serialize(new { answers, xml = SchematicDesignXml.Write(entry.Design, state.KnowledgeLibraries) });
            candidates.TryAdd(key, (resolution, entry));
        }
        return candidates.Count == 1 ? candidates.Values.Single() : null;
    }

    internal static StoredDesignRecovery Retain(DesignRecoveryStore store, StoredDesignRecovery saved,
        IReadOnlyList<SchematicOwnershipAnswer> answers, CancellationToken token)
    {
        if (saved.State.HasPendingWork) throw Invalid("Finish the pending synchronization before replacing component choices.");
        var resolution = new DesignComponentRebindResolution(SchematicRepeatedSheetChoices.SnapshotToken(saved.State), answers);
        Validate(resolution);
        var state = saved.State with { ComponentRebindResolution = resolution };
        var result = Plan(state, DesignRecoveryStore.ReadDesired(state), token);
        if (result?.Candidate is null) throw Invalid(result?.ErrorMessage ?? "There is no current native component replacement to answer.");
        return store.Save(state, saved.RevisionToken);
    }

    internal static void Validate(DesignComponentRebindResolution value)
    {
        if (value.SnapshotToken is not { Length: 64 } || !value.SnapshotToken.All(char.IsAsciiHexDigitLower)
            || value.Answers is not { Count: > 0 } || value.Answers.Any(a => a is null || a.NativeObjectId == Guid.Empty
                || a.PartId is null || a.PartId == Guid.Empty || a.ComponentId is null || a.ComponentId == Guid.Empty
                || a.NativePath is not { Count: > 0 } || a.NativePath.Any(p => p == Guid.Empty) || a.PinMappings is null
                || a.PinMappings.Any(p => p is null || string.IsNullOrWhiteSpace(p.FormerPin)
                    || p.ReplacementPin is not null && string.IsNullOrWhiteSpace(p.ReplacementPin))))
            throw Invalid("Supply complete part/component identities, native paths and explicit former-pin dispositions.");
        if (value.Answers.Select(a => SchematicDesignBindings.PathKey(a.NativePath!) + "#" + a.NativeObjectId).Distinct().Count() != value.Answers.Count)
            throw Invalid("Answer each observed symbol once.");
    }

    internal static bool Same(DesignComponentRebindResolution? a, DesignComponentRebindResolution? b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    private static SchematicNetReconciliationResult Apply(DesignRecoveryState state, SchematicDesign desired,
        IReadOnlyList<Change> changes, IReadOnlyList<SchematicOwnershipResolutionRequest> requests,
        IReadOnlyList<SchematicOwnershipAnswer> answers, CancellationToken token, SchematicOwnershipHistory? history)
    {
        var original = state.Baseline.Engineering.Circuit;
        if (CircuitXml.Write(original) != CircuitXml.Write(desired.Engineering.Circuit)
            || SchematicNetReconciliation.Bindings(state.Baseline) != SchematicNetReconciliation.Bindings(desired))
            throw Invalid("Keep the current circuit unchanged while answering the native replacement; newer instructions are retained.");
        if (answers.Count != requests.Count) throw Invalid("Answer every changed symbol in this snapshot together.");
        var byKey = answers.ToDictionary(a => SchematicDesignBindings.PathKey(a.NativePath!) + "#" + a.NativeObjectId.ToString("D"));
        var parts = original.Parts.ToDictionary(p => p.Id);
        var definitions = original.Sheets.SelectMany(s => s.Components).ToDictionary(d => d.Id);
        var symbols = original.Symbols.ToDictionary(s => s.Id);
        var native = SchematicModelProjection.NativeSymbols(state.Baseline, state.Observed);
        var mapping = new Dictionary<PinEndpoint, PinEndpoint?>();
        var targets = new Dictionary<Guid, Guid>();
        var replacedComponents = changes.Select(c => c.Component.Id).ToHashSet();
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            string key = change.Path + "#" + change.After.Id.Value;
            if (!byKey.TryGetValue(key, out var answer) || answer.ComponentId != change.Component.Id
                || !requests.Single(r => r.NativePath == change.Path && r.NativeObjectId.ToString("D") == change.After.Id.Value)
                    .CandidatePartIds.Contains(answer.PartId!.Value))
                throw Invalid("Choose the offered part for this exact existing component and symbol.");
            var replacement = Replacement(change, original.Id) with { Id = answer.PartId!.Value };
            if (parts.TryGetValue(replacement.Id, out var known))
            { if (!SamePart(known, replacement)) throw Invalid("The chosen part has different native pins or units."); }
            else parts.Add(replacement.Id, replacement);
            if (targets.TryGetValue(change.Component.DefinitionId, out var target) && target != replacement.Id)
                throw Invalid("All instances of one component definition must select the same replacement part.");
            targets[change.Component.DefinitionId] = replacement.Id;
            var chosen = answer.PinMappings!;
            if (chosen.Select(p => p.FormerPin).Distinct().Count() != chosen.Count
                || !chosen.Select(p => p.FormerPin).ToHashSet().SetEquals(change.Former.Pins.Select(p => p.Number))
                || chosen.Any(p => p.ReplacementPin is not null && !replacement.Pins.Any(n => n.Number == p.ReplacementPin))
                || chosen.Where(p => p.ReplacementPin is not null).Select(p => p.ReplacementPin).Distinct().Count()
                    != chosen.Count(p => p.ReplacementPin is not null))
                throw Invalid("Map each former pin once to a distinct replacement pin, or explicitly leave it unresolved.");
            foreach (var pin in chosen)
            {
                var old = new PinEndpoint(change.Component.Id, pin.FormerPin);
                PinEndpoint? next = pin.ReplacementPin is null ? null : new(change.Component.Id, pin.ReplacementPin);
                if (mapping.TryGetValue(old, out var existing) && existing != next)
                    throw Invalid("Units of the same component must agree about pin identities.");
                mapping[old] = next;
            }
            symbols[change.Occurrence.Id] = change.Occurrence with { Unit = change.After.Unit.Unit };
        }
        // A shared definition cannot quietly change another instance, and every unit
        // of one physical component must describe the selected replacement.
        foreach (var owner in original.Components.Where(c => targets.ContainsKey(c.DefinitionId)))
        {
            if (!replacedComponents.Contains(owner.Id)) throw Invalid("Include every instance of the changed component definition.");
            var replacement = parts[targets[owner.DefinitionId]];
            var chosenSymbol = changes.First(c => c.Component.Id == owner.Id);
            foreach (var occurrence in original.Symbols.Where(s => s.ComponentId == owner.Id))
            {
                var actual = native[occurrence.Id];
                var template = changes.First(c => c.Component.Id == owner.Id) with { After = actual };
                if (SchematicNativeAdditionProjection.LibraryKey(actual) != SchematicNativeAdditionProjection.LibraryKey(chosenSymbol.After)
                    || !SamePart(Replacement(template, original.Id), replacement))
                    throw Invalid("Update all units of the component to one consistent native definition before answering.");
            }
        }
        PinEndpoint? Mapped(PinEndpoint pin) => mapping.TryGetValue(pin, out var mapped) ? mapped : pin;
        var mappedNets = original.Nets.Select(n => n with { Pins = n.Pins.Select(Mapped).Where(p => p is not null)
            .Select(p => p!).ToArray() }).Where(n => n.Pins.Count != 0 || original.Nets.Single(o => o.Id == n.Id).Pins.Count == 0).ToArray();
        var nextCircuit = original with { Parts = parts.Values.ToArray(),
            Sheets = original.Sheets.Select(s => s with { Components = s.Components.Select(d => targets.TryGetValue(d.Id, out var target)
                ? d with { PartId = target } : d).ToArray() }).ToArray(), Symbols = original.Symbols.Select(s => symbols[s.Id]).ToArray(), Nets = mappedNets };
        nextCircuit.Validate();
        var pinChanges = mapping.Select(p => new ComponentReferenceChange(new(p.Key.ComponentId, p.Key.Pin),
            ComponentReferenceChangeKind.Reidentified, "The native symbol changed; retain the explicit former pin identity.",
            p.Value is { } target ? [new(target.ComponentId, target.Pin)] : [])).ToArray();
        var absentNets = original.Nets.Where(n => !mappedNets.Any(m => m.Id == n.Id)).Select(n => new NetIdentityChange(n.Id,
            NetBindingChangeKind.Removed, "The replacement has no mapped pins for this net.", [])).ToArray();
        var engineering = ComponentReferenceRetention.Retain(desired.Engineering, nextCircuit, pinChanges, state.KnowledgeLibraries, absentNets);
        foreach (var pending in (engineering.Structure.UnresolvedComponentReferences ?? []).ToArray())
            if (pending.FormerTarget.PinNumber is { } number
                && !(desired.Engineering.Structure.UnresolvedComponentReferences ?? []).Any(p => p.OwnerId == pending.OwnerId
                    && p.Slot == pending.Slot && p.FormerTarget == pending.FormerTarget)
                && mapping.TryGetValue(new(pending.FormerTarget.ComponentId, number), out var mapped) && mapped is { } target)
                engineering = ComponentReferenceRetention.Resolve(engineering, pending.OwnerId, pending.Slot,
                    pending.FormerTarget, new(target.ComponentId, target.Pin), state.KnowledgeLibraries);
        var candidate = desired with { Engineering = engineering, Schematic = state.Observed.Clone() };
        // Old declarations remain valid for the old part. A new part uses the actual
        // captured cache when available, so it can later be rebuilt without guessing.
        var declarations = (desired.PartSymbols ?? []).ToList();
        foreach (var change in changes)
        {
            Guid target = targets[change.Component.DefinitionId];
            if (declarations.Any(d => d.PartId == target)) continue;
            var screen = state.Observed.Instances.Single(s => SchematicNativeSheetChanges.Key(s) == change.Path);
            string cacheKey = string.IsNullOrEmpty(change.After.LibName) ? SchematicNativeAdditionProjection.LibraryKey(change.After) : change.After.LibName;
            var cache = screen.CachedSymbols.SingleOrDefault(c => c.CacheKey == cacheKey);
            if (cache is not null) declarations.Add(new(target, change.After.LibraryId.Clone(), cache.Clone(), change.After.BodyStyle?.Style ?? 1));
        }
        candidate = candidate with { PartSymbols = declarations.Count == 0 ? null : declarations };
        var before = SchematicElectricalComparison.Compare(state.Baseline, state.BaselineElectrical!, state.KnowledgeLibraries, token);
        if (!before.PinBindingsComplete || !before.ConnectivityEquivalent) throw Invalid("The saved electrical baseline is not aligned.");
        var current = SchematicElectricalComparison.Compare(candidate, state.ObservedElectrical!, state.KnowledgeLibraries, token);
        if (!current.PinBindingsComplete)
            return new(null, [], [], current.Issues, current.CoverageGaps, "unresolved_electrical_bindings");
        var baselinePartition = SchematicNetReconciliation.Partition(nextCircuit, current.PinPartitions!.SelectMany(g => g.Pins).ToArray(), current.StackedPins ?? []);
        var historicalNets = history is null ? null : SchematicNetReconciliation.Partition(history.Design.Engineering.Circuit,
            current.PinPartitions!.SelectMany(g => g.Pins).ToArray(), current.StackedPins ?? []).Nets;
        var finalNets = new List<CircuitNet>();
        foreach (var group in current.PinPartitions!)
        {
            string key = SchematicNetReconciliation.Key(group.Pins);
            if (baselinePartition.Nets.TryGetValue(key, out var exact)) finalNets.Add(exact);
            else if (historicalNets is not null && historicalNets.TryGetValue(key, out var previous)
                && !mappedNets.Any(n => n.Id == previous.Id)) finalNets.Add(previous);
            else if (group.Pins.Count > 1)
                finalNets.Add(new(SchematicNetReconciliation.GeneratedIdentity(state.OriginId, original.Id, group.Pins),
                    string.IsNullOrWhiteSpace(group.NativeName) ? "Native connection" : group.NativeName, group.Pins));
        }
        finalNets.AddRange(mappedNets.Where(n => n.Pins.Count == 0));
        var netChanges = original.Nets.Where(n => !finalNets.Any(f => f.Id == n.Id)).Select(n => new NetIdentityChange(n.Id,
            NetBindingChangeKind.Reidentified, "Native connectivity after replacement no longer matches the former net.",
            finalNets.Where(f => n.Pins.Select(Mapped).Any(p => p is not null && f.Pins.Contains(p))).Select(f => f.Id).ToArray())).ToArray();
        engineering = ComponentReferenceRetention.Retain(engineering, nextCircuit with { Nets = finalNets }, [], state.KnowledgeLibraries, netChanges, original);
        if (history is not null)
            engineering = SchematicNativeRestorationProjection.ResolveRetained(engineering,
                new(candidate, history, [], replacedComponents.ToArray()), finalNets.Select(n => n.Id).ToArray(), state.KnowledgeLibraries);
        candidate = candidate with { Engineering = engineering };
        var result = new SchematicNativeRestorationResult(candidate, null, [], [])
        { ReboundOccurrences = changes.Select(c => c.Occurrence.Id).ToArray() };
        return new(engineering, [], netChanges, [], before.CoverageGaps.Concat(current.CoverageGaps).Distinct().ToArray()) { Restoration = result };
    }

    private static AutomationException Invalid(string message) => new(SchematicNativeAdditionProjection.AnswerInvalid, message);
}
