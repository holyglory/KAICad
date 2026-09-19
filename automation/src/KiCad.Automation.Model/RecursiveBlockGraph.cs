using System.Collections.Immutable;
using System.Xml;

namespace KiCad.Automation.Model;

/// <summary>An exact occurrence, implementation and revision; never a mutable head lookup.</summary>
public sealed record BlockSelection(Guid BlockId, Guid StateId, Guid RevisionId);

public sealed record BlockDesignState(Guid Id, Guid BlockId, string Name, Guid HeadRevisionId);

/// <summary>The revisioned containment portion of a block's local diagram. The root uses
/// the same contract as every child. Physical allocation is deliberately independent.</summary>
public sealed record RecursiveBlockRevision(BlockSelection Selection, Guid? ParentRevisionId,
    string Name, Guid RequirementRevisionId, ImmutableArray<BlockSelection> Children,
    RequirementRevisionOrigin Origin, BlockSelection? RestoredFrom = null);

public sealed record RecursiveBlockSelectionResult(RecursiveBlockGraph Graph,
    ImmutableArray<BlockSelection> CreatedAncestors, bool Changed);

public sealed record RecursiveBlockDraft(BlockSelection Baseline, string Name,
    ImmutableArray<BlockSelection> Children, DiagramRequirementDraft Requirements, BlockSelection? RestoredFrom = null);

/// <summary>Immutable block occurrence/revision graph. Publishing an unselected revision
/// and selecting it are separate operations. Persistence and native activation belong
/// to the owning document transaction, not this domain object.</summary>
public sealed class RecursiveBlockGraph
{
    public Guid DocumentId { get; }
    public BlockSelection SelectedRoot { get; }
    public ImmutableArray<BlockDesignState> States { get; }
    public ImmutableArray<RecursiveBlockRevision> Revisions { get; }
    public ImmutableArray<DiagramRequirementHistory> RequirementHistories { get; }
    private readonly ImmutableDictionary<Guid, BlockDesignState> _states;
    private readonly ImmutableDictionary<Guid, RecursiveBlockRevision> _revisions;
    private readonly ImmutableDictionary<Guid, DiagramRequirementHistory> _requirements;

    public RecursiveBlockGraph(Guid documentId, BlockSelection selectedRoot,
        IEnumerable<BlockDesignState> states, IEnumerable<RecursiveBlockRevision> revisions,
        IEnumerable<DiagramRequirementHistory> requirementHistories)
    {
        if (documentId == Guid.Empty || selectedRoot is null || states is null || revisions is null
            || requirementHistories is null) throw Invalid("Supply a document, selected root and immutable block histories.");
        DocumentId = documentId; SelectedRoot = selectedRoot;
        States = states.ToImmutableArray(); Revisions = revisions.ToImmutableArray();
        RequirementHistories = requirementHistories.ToImmutableArray();
        var stateIndex = ImmutableDictionary.CreateBuilder<Guid, BlockDesignState>();
        foreach (var state in States)
        {
            if (state is null || state.Id == Guid.Empty || state.BlockId == Guid.Empty
                || state.HeadRevisionId == Guid.Empty || !stateIndex.TryAdd(state.Id, state))
                throw Invalid("Block implementations require distinct identities, an occurrence and an exact saved head.");
            Text(state.Name, "An implementation needs a name.");
        }
        _states = stateIndex.ToImmutable();
        var requirements = ImmutableDictionary.CreateBuilder<Guid, DiagramRequirementHistory>();
        foreach (var history in RequirementHistories)
            if (history is null || history.Scope.DocumentId != DocumentId
                || !_states.TryGetValue(history.Scope.DesignStateId, out var state)
                || history.Scope.OwnerId != state.BlockId || !requirements.TryAdd(state.Id, history))
                throw Invalid("Requirement history must belong to one exact block implementation in this document.");
        _requirements = requirements.ToImmutable();
        var revisionIndex = ImmutableDictionary.CreateBuilder<Guid, RecursiveBlockRevision>();
        foreach (var revision in Revisions)
        {
            if (revision is null || revision.Selection is null || revision.Selection.RevisionId == Guid.Empty
                || !_states.TryGetValue(revision.Selection.StateId, out var state)
                || state.BlockId != revision.Selection.BlockId || revision.Children.IsDefault
                || revision.Origin is null || !revisionIndex.TryAdd(revision.Selection.RevisionId, revision))
                throw Invalid("Each revision needs its exact block/state identity, diagram children and origin.");
            Text(revision.Name, "A block revision needs a name."); revision.Origin.Validate();
            if (!_requirements.TryGetValue(state.Id, out var history))
                throw Invalid("Every block implementation needs its three-field requirement history.");
            _ = history.Inspect(revision.RequirementRevisionId);
        }
        _revisions = revisionIndex.ToImmutable();
        // Occurrences, implementations and revisions are different identity domains.
        var identities = States.Select(s => s.BlockId).Distinct().ToHashSet();
        if (!identities.Add(DocumentId) || States.Any(s => !identities.Add(s.Id))
            || Revisions.Any(r => !identities.Add(r.Selection.RevisionId)))
            throw Invalid("Document, block occurrence, implementation and revision identities cannot alias.");
        foreach (var state in States) ValidateHistory(state);
        foreach (var revision in Revisions)
        {
            ValidateRestoration(revision.Selection, revision.ParentRevisionId, revision.RestoredFrom);
            var children = new HashSet<Guid>();
            foreach (var child in revision.Children)
            {
                _ = Inspect(child);
                if (!children.Add(child.BlockId)) throw Invalid("A local diagram cannot contain the same block occurrence twice.");
            }
        }
        _ = Inspect(SelectedRoot);
        // Validate inactive and historical states too. A later selection must not expose
        // latent cycles or reuse one occurrence under different parents.
        foreach (var revision in Revisions) _ = Walk(revision.Selection);
    }

    public RecursiveBlockRevision Inspect(BlockSelection selection)
    {
        if (selection is null || !_revisions.TryGetValue(selection.RevisionId, out var revision)
            || revision.Selection != selection)
            throw Invalid("A block selection must identify an existing exact occurrence, implementation and revision.");
        return revision;
    }

    public DiagramRequirementSnapshot Requirements(BlockSelection selection)
    {
        var revision = Inspect(selection);
        return _requirements[selection.StateId].Inspect(revision.RequirementRevisionId);
    }

    /// <summary>Preorder of this exact snapshot. It never resolves a child through today's head.</summary>
    public ImmutableArray<BlockSelection> Walk(BlockSelection root)
    {
        var result = ImmutableArray.CreateBuilder<BlockSelection>();
        var seen = new HashSet<Guid>();
        var pending = new Stack<BlockSelection>(); pending.Push(root);
        while (pending.TryPop(out var selection))
        {
            var revision = Inspect(selection);
            if (!seen.Add(selection.BlockId))
                throw Invalid("A selected hierarchy cannot cycle or share a block occurrence; reuse a definition through distinct occurrences.");
            result.Add(selection);
            for (int i = revision.Children.Length - 1; i >= 0; --i) pending.Push(revision.Children[i]);
        }
        return result.ToImmutable();
    }

    public ImmutableArray<RecursiveBlockRevision> History(Guid stateId)
    {
        if (!_states.TryGetValue(stateId, out var state)) throw Invalid("The requested block implementation does not exist.");
        var result = ImmutableArray.CreateBuilder<RecursiveBlockRevision>();
        Guid? current = state.HeadRevisionId;
        while (current is { } id)
        {
            var revision = _revisions[id]; result.Add(revision); current = revision.ParentRevisionId;
        }
        return result.ToImmutable();
    }

    public RecursiveBlockDraft StartDraft(BlockSelection selection)
    {
        var revision = Inspect(selection);
        var requirements = Requirements(selection);
        return new(selection, revision.Name, revision.Children,
            new(requirements, requirements.Requirements, ImmutableDictionary<DiagramRequirementField, Guid>.Empty));
    }

    /// <summary>Restore old contents into a draft, not the selected hierarchy or saved heads.</summary>
    public RecursiveBlockDraft RestoreAsDraft(RecursiveBlockDraft draft, BlockSelection source)
    {
        ValidateDraft(draft);
        var baseline = Inspect(draft.Baseline);
        if (draft.Name != baseline.Name || !draft.Children.SequenceEqual(baseline.Children)
            || draft.Requirements.Requirements != Requirements(draft.Baseline).Requirements)
            throw new AutomationException("dirty_block_draft", "Save or explicitly decline the existing draft before restoring a whole diagram.");
        var previous = Inspect(source);
        if (source.BlockId != draft.Baseline.BlockId || source.StateId != draft.Baseline.StateId)
            throw Invalid("Restore history from this exact block implementation, not an unrelated alternative.");
        var fields = draft.Requirements;
        foreach (var field in Enum.GetValues<DiagramRequirementField>())
            fields = _requirements[source.StateId].RestoreField(fields, previous.RequirementRevisionId, field);
        return draft with { Name = previous.Name, Children = previous.Children, Requirements = fields, RestoredFrom = source };
    }

    /// <summary>Atomically produces a new in-memory root and immutable history. A failed
    /// validation or conflict leaves this graph and the supplied draft untouched.</summary>
    public RecursiveBlockSelectionResult SaveDraft(BlockSelection expectedRoot, ImmutableArray<BlockSelection> path,
        RecursiveBlockDraft draft, Guid revisionId, Guid requirementRevisionId, ImmutableArray<Guid> ancestorRevisionIds,
        RequirementRevisionOrigin origin, IReadOnlyCollection<DiagramRequirementResolution>? resolutions = null)
    {
        ValidateDraft(draft);
        if (expectedRoot != SelectedRoot || path.IsDefaultOrEmpty || path[^1] != draft.Baseline
            || _states[draft.Baseline.StateId].HeadRevisionId != draft.Baseline.RevisionId)
            throw new AutomationException("stale_block_revision", "The saved block or selected hierarchy changed; retain the draft and compare the newer design.");
        // Validate the complete path even for an unchanged save.
        _ = Select(expectedRoot, path, draft.Baseline, ancestorRevisionIds, origin);
        var history = _requirements[draft.Baseline.StateId];
        var requirements = history.Commit(history.Current.Id, draft.Requirements, requirementRevisionId, origin, resolutions);
        var baseline = Inspect(draft.Baseline);
        if (draft.Name == baseline.Name && draft.Children.SequenceEqual(baseline.Children)
            && requirements.Revision.Requirements == Requirements(draft.Baseline).Requirements)
            return new(this, [], false);
        var revision = new RecursiveBlockRevision(new(draft.Baseline.BlockId, draft.Baseline.StateId, revisionId),
            baseline.Selection.RevisionId, draft.Name, requirements.Revision.Id, draft.Children, origin, draft.RestoredFrom);
        var appended = AppendRevision(baseline.Selection.RevisionId, revision, requirements.History);
        return appended.Select(expectedRoot, path, revision.Selection, ancestorRevisionIds, origin);
    }

    /// <summary>Publish a candidate without changing the root's chosen design. A changed
    /// requirement history must extend the exact saved prefix without rewriting it.</summary>
    public RecursiveBlockGraph AppendRevision(Guid expectedHead, RecursiveBlockRevision revision,
        DiagramRequirementHistory? requirementHistory = null)
    {
        if (revision?.Selection is not { } selection || !_states.TryGetValue(selection.StateId, out var state)
            || state.BlockId != selection.BlockId || state.HeadRevisionId != expectedHead
            || revision.ParentRevisionId != expectedHead)
            throw new AutomationException("stale_block_revision", "The block implementation changed; retain the candidate and reload its exact saved head.");
        if (_revisions.ContainsKey(selection.RevisionId)) throw Invalid("A saved revision identity cannot be reused.");
        var histories = RequirementHistories;
        if (requirementHistory is not null)
        {
            var saved = _requirements[state.Id];
            if (requirementHistory.Scope != saved.Scope
                || requirementHistory.Revisions.Length < saved.Revisions.Length
                || !saved.Revisions.Select((r, i) => SameRequirementRevision(r, requirementHistory.Revisions[i])).All(x => x))
                throw Invalid("Requirement history must preserve every saved revision and its provenance.");
            histories = histories.SetItem(histories.IndexOf(saved), requirementHistory);
        }
        return new(DocumentId, SelectedRoot,
            States.Select(s => s.Id == state.Id ? s with { HeadRevisionId = selection.RevisionId } : s),
            Revisions.Add(revision), histories);
    }

    /// <summary>Create an alternative for an existing occurrence; it remains unselected.
    /// Its initial history is explicit rather than fabricated from a mutable head.</summary>
    public RecursiveBlockGraph AddImplementation(BlockDesignState state, RecursiveBlockRevision initial,
        DiagramRequirementHistory requirements)
    {
        if (state is null || initial is null || _states.ContainsKey(state.Id)
            || !States.Any(s => s.BlockId == state.BlockId) || initial.ParentRevisionId is not null
            || initial.Selection != new BlockSelection(state.BlockId, state.Id, state.HeadRevisionId))
            throw Invalid("An alternative needs a fresh implementation of an existing occurrence and its initial revision.");
        return new(DocumentId, SelectedRoot, States.Add(state), Revisions.Add(initial), RequirementHistories.Add(requirements));
    }

    /// <summary>Select a revision of an existing occurrence at an exact root-to-block path.
    /// Creates ancestors bottom-up. Unchanged siblings and all historic roots stay intact.
    /// ancestorRevisionIds are in the same root-to-parent order as path, excluding its leaf.</summary>
    public RecursiveBlockSelectionResult Select(BlockSelection expectedRoot, ImmutableArray<BlockSelection> path,
        BlockSelection replacement, ImmutableArray<Guid> ancestorRevisionIds, RequirementRevisionOrigin origin)
    {
        if (expectedRoot != SelectedRoot)
            throw new AutomationException("stale_root_revision", "The selected design changed; reload it before selecting a block revision.");
        if (path.IsDefaultOrEmpty || path[0] != SelectedRoot || ancestorRevisionIds.IsDefault
            || ancestorRevisionIds.Length != path.Length - 1 || origin is null)
            throw Invalid("Selection needs the exact root-to-block path and fresh identities for its containing snapshots.");
        origin.Validate(); _ = Inspect(replacement);
        for (int i = 0; i < path.Length; ++i)
        {
            var current = Inspect(path[i]);
            if (i + 1 < path.Length && !current.Children.Contains(path[i + 1]))
                throw Invalid("The selection path is not part of the selected root snapshot.");
        }
        if (path[^1].BlockId != replacement.BlockId)
            throw Invalid("Selecting an implementation cannot silently replace a different block occurrence.");
        if (path[^1] == replacement) return new(this, [], false);
        if (_states[replacement.StateId].HeadRevisionId != replacement.RevisionId)
            throw new AutomationException("historical_revision_requires_draft", "Restore an earlier revision into a new draft before saving; browsing history does not activate it.");
        var ids = new HashSet<Guid>(_revisions.Keys.Concat(_states.Keys).Concat(States.Select(s => s.BlockId)).Append(DocumentId));
        if (ancestorRevisionIds.Any(id => id == Guid.Empty || !ids.Add(id)))
            throw Invalid("Containing revisions require fresh, distinct identities.");
        var revisions = Revisions.ToBuilder(); var states = States.ToBuilder();
        var created = new BlockSelection[path.Length - 1];
        var child = replacement;
        for (int i = path.Length - 2; i >= 0; --i)
        {
            var parent = Inspect(path[i]); var state = _states[parent.Selection.StateId];
            // A candidate for the parent may have been published since this active
            // snapshot. Do not silently discard those unsaved-to-root changes.
            if (state.HeadRevisionId != parent.Selection.RevisionId)
                throw new AutomationException("stale_parent_revision", "A containing implementation has a newer saved revision; compare it before selecting this child.");
            var next = new BlockSelection(parent.Selection.BlockId, parent.Selection.StateId, ancestorRevisionIds[i]);
            revisions.Add(parent with { Selection = next, ParentRevisionId = parent.Selection.RevisionId,
                Children = parent.Children.Select(c => c == path[i + 1] ? child : c).ToImmutableArray(), Origin = origin, RestoredFrom = null });
            states[states.IndexOf(state)] = state with { HeadRevisionId = next.RevisionId };
            created[i] = next; child = next;
        }
        return new(new(DocumentId, child, states, revisions, RequirementHistories), [.. created], true);
    }

    private void ValidateHistory(BlockDesignState state)
    {
        var seen = new HashSet<Guid>(); Guid? id = state.HeadRevisionId;
        while (id is { } current)
        {
            if (!seen.Add(current) || !_revisions.TryGetValue(current, out var revision)
                || revision.Selection.StateId != state.Id || revision.Selection.BlockId != state.BlockId)
                throw Invalid("Implementation history must be a complete, acyclic chain of that exact block and state.");
            id = revision.ParentRevisionId;
        }
        if (seen.Count != Revisions.Count(r => r.Selection.StateId == state.Id))
            throw Invalid("Every saved revision belongs to its implementation's history; alternatives use separate states.");
    }

    private void ValidateDraft(RecursiveBlockDraft draft)
    {
        if (draft is null || draft.Requirements is null || draft.Children.IsDefault)
            throw Invalid("Provide the saved baseline, current fields and diagram children for this draft.");
        _ = Inspect(draft.Baseline); Text(draft.Name, "A block draft needs a name.");
        if (draft.Requirements.Baseline != Requirements(draft.Baseline))
            throw Invalid("The draft's requirement baseline must match its exact saved block revision.");
        _ = _requirements[draft.Baseline.StateId].PrepareMerge(draft.Requirements);
        ValidateRestoration(draft.Baseline, draft.Baseline.RevisionId, draft.RestoredFrom);
    }

    private void ValidateRestoration(BlockSelection owner, Guid? parentId, BlockSelection? source)
    {
        if (source is null) return;
        _ = Inspect(source);
        if (source.BlockId != owner.BlockId || source.StateId != owner.StateId)
            throw Invalid("A restored diagram must identify a saved source in the same block implementation.");
        for (Guid? id = parentId; id is { } current; id = _revisions[current].ParentRevisionId)
            if (current == source.RevisionId) return;
        throw Invalid("The restoration source must be in the saved baseline history, not a future or unrelated revision.");
    }

    private static bool SameRequirementRevision(DiagramRequirementRevision a, DiagramRequirementRevision b) =>
        a.Id == b.Id && a.ParentId == b.ParentId && a.Requirements == b.Requirements
        && a.Origin.ActorKind == b.Origin.ActorKind && a.Origin.Actor == b.Origin.Actor
        && a.Origin.RecordedAt == b.Origin.RecordedAt && a.Origin.Summary == b.Origin.Summary
        && a.Origin.Sources.SequenceEqual(b.Origin.Sources) && a.Origin.InputIds.SequenceEqual(b.Origin.InputIds)
        && a.Restorations.SequenceEqual(b.Restorations);

    private static void Text(string? text, string message)
    {
        if (string.IsNullOrWhiteSpace(text)) throw Invalid(message);
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException) { throw Invalid("Block text cannot be preserved in XML."); }
    }
    private static AutomationException Invalid(string message) => new("invalid_recursive_block_graph", message);
}
