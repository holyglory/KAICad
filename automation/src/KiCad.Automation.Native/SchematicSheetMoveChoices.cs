using System.Text.Json;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record SchematicSheetMoveAnswer(Guid SheetInstanceId, IReadOnlyList<Guid> NativePath);
public sealed record DesignSheetMoveResolution(string SnapshotToken, IReadOnlyList<SchematicSheetMoveAnswer> Moves,
    IReadOnlyList<Guid> RetiredSheetInstanceIds, IReadOnlyList<IReadOnlyList<Guid>> AddedNativePaths);

internal static class SchematicSheetMoveChoices
{
    internal const string Tool = "kicad_design_sheet_move_answer";

    internal static StoredDesignRecovery Retain(DesignRecoveryStore store, StoredDesignRecovery saved,
        IReadOnlyList<SchematicSheetMoveAnswer> moves, IReadOnlyList<Guid> retired,
        IReadOnlyList<IReadOnlyList<Guid>> added, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (saved.State.HasPendingWork)
            throw new AutomationException("pending_recovery_requires_reconciliation", "Finish or reconcile the pending synchronization before choosing sheet identities.");
        var original = SchematicNativeSheetChanges.Compare(saved.State.Baseline.Schematic, saved.State.Observed);
        if (original.ErrorCode != SchematicNativeSheetChanges.MoveAmbiguous)
            throw Invalid("There is no unresolved native sheet-instance mapping in this observation.");
        string snapshot = SchematicRepeatedSheetChoices.SnapshotToken(saved.State);
        Validate(new(snapshot, moves, retired, added));
        var answer = new DesignSheetMoveResolution(snapshot,
            moves.Select(m => m with { NativePath = m.NativePath.ToArray() }).ToArray(), retired.ToArray(),
            added.Select(p => (IReadOnlyList<Guid>)p.ToArray()).ToArray());
        Validate(answer);
        var choices = Choices(saved.State, answer);
        var oldPaths = original.Requests.SelectMany(r => r.FormerPaths).Concat(original.Removed).ToHashSet(StringComparer.Ordinal);
        var newPaths = original.Requests.SelectMany(r => r.NewPaths).Concat(original.Inserted).ToHashSet(StringComparer.Ordinal);
        if (choices.Moves.Any(m => !oldPaths.Contains(m.Before) || !newPaths.Contains(m.After))
            || choices.Retired.Any(p => !oldPaths.Contains(p)) || choices.Added.Any(p => !newPaths.Contains(p)))
            throw Invalid("Answer only the current ambiguous sheet paths and their explicitly removed or added descendants.");
        var resolved = SchematicNativeSheetChanges.Compare(saved.State.Baseline.Schematic, saved.State.Observed, choices);
        if (resolved.ErrorCode is not null)
            throw Invalid(resolved.ErrorMessage ?? "Give one complete mapping for the current questions.");
        token.ThrowIfCancellationRequested();
        return store.Save(saved.State with { SheetMoveResolution = answer }, saved.RevisionToken);
    }

    internal static SchematicSheetInstanceChoices? Current(DesignRecoveryState state) =>
        state.SheetMoveResolution is { } answer && answer.SnapshotToken == SchematicRepeatedSheetChoices.SnapshotToken(state)
            ? Choices(state, answer) : null;

    // Undo of an expansion removes owners rather than restoring them. Verified
    // history can identify the survivors, but never supplies current instructions.
    internal static SchematicSheetInstanceChoices? FromRemovalHistory(DesignRecoveryState state,
        IReadOnlyList<SchematicOwnershipHistory> history, CancellationToken token)
    {
        string observedOwners = SchematicNetReconciliation.NativeOwners(state.Observed);
        var candidates = new Dictionary<string, SchematicSheetInstanceChoices>(StringComparer.Ordinal);
        foreach (var entry in history)
        {
            token.ThrowIfCancellationRequested();
            if (SchematicNetReconciliation.NativeOwners(entry.Design.Schematic) != observedOwners) continue;
            var choices = FromDesigns(state.Baseline, entry.Design);
            var reduced = SchematicNativeRemovalProjection.Project(state.Baseline, state.Observed,
                state.KnowledgeLibraries, token, choices);
            if (reduced.BindingCandidate is null
                || reduced.RemovedSheetInstances.Count == 0 && reduced.MovedSheetInstances.Count == 0
                || SchematicNetReconciliation.Topology(reduced.BindingCandidate.Engineering.Circuit)
                    != SchematicNetReconciliation.Topology(entry.Design.Engineering.Circuit)
                || SchematicNetReconciliation.Bindings(reduced.BindingCandidate)
                    != SchematicNetReconciliation.Bindings(entry.Design)) continue;
            candidates.TryAdd(JsonSerializer.Serialize(choices), choices);
        }
        // Equal native content does not make different model identities equivalent.
        return candidates.Count == 1 ? candidates.Values.Single() : null;
    }

    internal static SchematicSheetInstanceChoices FromDesigns(SchematicDesign before, SchematicDesign after)
    {
        var old = before.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var next = after.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        return new(old.Where(p => next.TryGetValue(p.Key, out string? path) && path != p.Value)
                .Select(p => new SchematicSheetPathMove(p.Value, next[p.Key])).ToArray(),
            old.Where(p => !next.ContainsKey(p.Key)).Select(p => p.Value).ToArray(),
            next.Where(p => !old.ContainsKey(p.Key)).Select(p => p.Value).ToArray());
    }

    // A private projection may temporarily omit newly inserted instances. Only
    // restrict a choice after the complete observation has passed comparison.
    internal static SchematicSheetInstanceChoices? Restrict(SchematicSheetInstanceChoices? choices,
        SchematicHierarchyData before, SchematicHierarchyData after)
    {
        if (choices is null) return null;
        var old = before.Instances.Select(SchematicNativeSheetChanges.Key).ToHashSet(StringComparer.Ordinal);
        var current = after.Instances.Select(SchematicNativeSheetChanges.Key).ToHashSet(StringComparer.Ordinal);
        return new(choices.Moves.Where(m => old.Contains(m.Before) && current.Contains(m.After)).ToArray(),
            choices.Retired.Where(p => old.Contains(p) && !current.Contains(p)).ToArray(),
            choices.Added.Where(p => current.Contains(p) && !old.Contains(p)).ToArray());
    }

    private static SchematicSheetInstanceChoices Choices(DesignRecoveryState state, DesignSheetMoveResolution answer)
    {
        Validate(answer);
        var paths = state.Baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        if (answer.Moves.Any(m => !paths.ContainsKey(m.SheetInstanceId)) || answer.RetiredSheetInstanceIds.Any(id => !paths.ContainsKey(id)))
            throw Invalid("Each former instance must be one of the saved design's exact sheet identities.");
        return new(answer.Moves.Select(m => new SchematicSheetPathMove(paths[m.SheetInstanceId], SchematicDesignBindings.PathKey(m.NativePath))).ToArray(),
            answer.RetiredSheetInstanceIds.Select(id => paths[id]).ToArray(), answer.AddedNativePaths.Select(SchematicDesignBindings.PathKey).ToArray());
    }

    internal static bool Same(DesignSheetMoveResolution? a, DesignSheetMoveResolution? b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    internal static void Validate(DesignSheetMoveResolution value)
    {
        static bool Path(IReadOnlyList<Guid>? path) => path is { Count: > 1 } && path.All(id => id != Guid.Empty);
        if (value.SnapshotToken is not { Length: 64 } || !value.SnapshotToken.All(char.IsAsciiHexDigitLower)
            || value.Moves is null || value.RetiredSheetInstanceIds is null || value.AddedNativePaths is null
            || value.Moves.Count + value.RetiredSheetInstanceIds.Count + value.AddedNativePaths.Count == 0
            || value.Moves.Any(m => m is null || m.SheetInstanceId == Guid.Empty || !Path(m.NativePath))
            || value.RetiredSheetInstanceIds.Any(id => id == Guid.Empty) || value.AddedNativePaths.Any(p => !Path(p)))
            throw Invalid("Give exact former sheet identities and complete resulting native paths.");
        var old = value.Moves.Select(m => m.SheetInstanceId).Concat(value.RetiredSheetInstanceIds).ToArray();
        var current = value.Moves.Select(m => SchematicDesignBindings.PathKey(m.NativePath))
            .Concat(value.AddedNativePaths.Select(SchematicDesignBindings.PathKey)).ToArray();
        if (old.Distinct().Count() != old.Length || current.Distinct(StringComparer.Ordinal).Count() != current.Length)
            throw Invalid("Each old instance and each resulting path must be answered once.");
    }

    private static AutomationException Invalid(string message) => new(SchematicNativeSheetChanges.MoveAnswerInvalid, message);
}
