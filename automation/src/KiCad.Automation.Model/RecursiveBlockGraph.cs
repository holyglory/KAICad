using System.Collections.Immutable;
using System.Xml;

namespace KiCad.Automation.Model;

/// <summary>An exact occurrence, implementation and revision; never a mutable head lookup.</summary>
public sealed record BlockSelection(Guid BlockId, Guid StateId, Guid RevisionId);

public sealed record BlockDesignState(Guid Id, Guid BlockId, string Name, Guid HeadRevisionId,
    BlockSelection? ForkedFrom = null, bool Archived = false);

/// <summary>The revisioned containment portion of a block's local diagram. The root uses
/// the same contract as every child. Physical allocation is deliberately independent.</summary>
public sealed record RecursiveBlockRevision(BlockSelection Selection, Guid? ParentRevisionId,
    string Name, Guid RequirementRevisionId, ImmutableArray<BlockSelection> Children,
    RequirementRevisionOrigin Origin, BlockSelection? RestoredFrom = null, BlockLocalDiagram? Diagram = null,
    BlockDefinition? Definition = null, BlockComponentBindings? ComponentBindings = null,
    BlockPhysicalAllocation? PhysicalAllocation = null)
{
    public BlockLocalDiagram LocalDiagram => Diagram ?? BlockLocalDiagram.Empty;
    public BlockDefinition EffectiveDefinition => Definition ?? BlockDefinition.Empty;
    public BlockComponentBindings EffectiveComponentBindings => ComponentBindings ?? BlockComponentBindings.Empty;
}

public sealed record RecursiveBlockSelectionResult(RecursiveBlockGraph Graph,
    ImmutableArray<BlockSelection> CreatedAncestors, bool Changed);

public sealed record RecursiveBlockDraft(BlockSelection Baseline, string Name,
    ImmutableArray<BlockSelection> Children, DiagramRequirementDraft Requirements, BlockSelection? RestoredFrom = null,
    BlockLocalDiagram? Diagram = null, BlockDefinition? Definition = null, BlockComponentBindings? ComponentBindings = null,
    BlockPhysicalAllocation? PhysicalAllocation = null)
{
    public BlockLocalDiagram LocalDiagram => Diagram ?? BlockLocalDiagram.Empty;
    public BlockDefinition EffectiveDefinition => Definition ?? BlockDefinition.Empty;
    public BlockComponentBindings EffectiveComponentBindings => ComponentBindings ?? BlockComponentBindings.Empty;
}

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
    public ImmutableArray<DiagramConnectionArchive> ConnectionArchives { get; }
    public ImmutableArray<ImplementationChange> ImplementationChanges { get; }
    public ImmutableArray<DiagramRefinementInput> RefinementInputs { get; }
    public ImmutableArray<BlockProposalRecord> Proposals { get; }
    private readonly ImmutableDictionary<Guid, BlockDesignState> _states;
    private readonly ImmutableDictionary<Guid, RecursiveBlockRevision> _revisions;
    private readonly ImmutableDictionary<Guid, DiagramRequirementHistory> _requirements;
    private readonly ImmutableDictionary<Guid, DiagramConnectionArchive> _connections;

    public RecursiveBlockGraph(Guid documentId, BlockSelection selectedRoot,
        IEnumerable<BlockDesignState> states, IEnumerable<RecursiveBlockRevision> revisions,
        IEnumerable<DiagramRequirementHistory> requirementHistories,
        IEnumerable<DiagramConnectionArchive>? connectionArchives = null,
        IEnumerable<ImplementationChange>? implementationChanges = null,
        IEnumerable<DiagramRefinementInput>? refinementInputs = null, IEnumerable<BlockProposalRecord>? proposals = null)
    {
        if (documentId == Guid.Empty || selectedRoot is null || states is null || revisions is null
            || requirementHistories is null) throw Invalid("Supply a document, selected root and immutable block histories.");
        DocumentId = documentId; SelectedRoot = selectedRoot;
        States = states.ToImmutableArray(); Revisions = revisions.ToImmutableArray();
        RequirementHistories = requirementHistories.ToImmutableArray();
        ConnectionArchives = connectionArchives?.ToImmutableArray() ?? [];
        ImplementationChanges = implementationChanges?.ToImmutableArray() ?? [];
        RefinementInputs = refinementInputs?.ToImmutableArray() ?? [];
        Proposals = proposals?.ToImmutableArray() ?? [];
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
        var connectionIndex = ImmutableDictionary.CreateBuilder<Guid, DiagramConnectionArchive>();
        foreach (var archive in ConnectionArchives)
            if (archive is null || archive.DocumentId != DocumentId || !States.Any(s => s.BlockId == archive.OwnerBlockId)
                || !connectionIndex.TryAdd(archive.OwnerBlockId, archive))
                throw Invalid("Connection archives must belong to distinct exact block occurrences in this document.");
        _connections = connectionIndex.ToImmutable();
        // Occurrences, implementations and revisions are different identity domains.
        var identities = States.Select(s => s.BlockId).Distinct().ToHashSet();
        if (!identities.Add(DocumentId) || States.Any(s => !identities.Add(s.Id))
            || Revisions.Any(r => !identities.Add(r.Selection.RevisionId)))
            throw Invalid("Document, block occurrence, implementation and revision identities cannot alias.");
        foreach (var archive in ConnectionArchives)
            if (archive.States.Select(s => s.ConnectionId).Distinct().Any(id => !identities.Add(id))
                || archive.States.Any(s => !identities.Add(s.Id)) || archive.Revisions.Any(r => !identities.Add(r.Selection.RevisionId)))
                throw Invalid("Connection identities cannot alias block or other connection identities.");
        var latestChanges = new Dictionary<Guid, ImplementationChange>();
        foreach (var change in ImplementationChanges)
        {
            if (change is null || change.Id == Guid.Empty || !identities.Add(change.Id) || !_states.ContainsKey(change.StateId)
                || !Enum.IsDefined(change.Kind) || change.Origin is null) throw Invalid("Implementation changes need distinct identities, an existing state, kind and origin.");
            Text(change.BeforeName, "Retain the implementation's previous name."); Text(change.AfterName, "Retain the implementation's resulting name.");
            change.Origin.Validate();
            bool valid = change.Kind switch
            {
                ImplementationChangeKind.Rename => change.BeforeName != change.AfterName && change.BeforeArchived == change.AfterArchived,
                ImplementationChangeKind.Archive => change.BeforeName == change.AfterName && !change.BeforeArchived && change.AfterArchived,
                ImplementationChangeKind.Restore => change.BeforeName == change.AfterName && change.BeforeArchived && !change.AfterArchived,
                _ => false
            };
            if (!valid || (latestChanges.TryGetValue(change.StateId, out var previous)
                    && (previous.AfterName != change.BeforeName || previous.AfterArchived != change.BeforeArchived))
                || (!latestChanges.ContainsKey(change.StateId) && change.BeforeArchived))
                throw Invalid("Implementation management history must preserve an exact, ordered sequence of names and removal/restoration states.");
            latestChanges[change.StateId] = change;
        }
        foreach (var state in States)
            if (latestChanges.TryGetValue(state.Id, out var change)
                ? state.Name != change.AfterName || state.Archived != change.AfterArchived : state.Archived)
                throw Invalid("Implementation metadata must agree with its retained management history.");
        var interfaceOwners = new Dictionary<Guid, Guid>();
        var annotationOwners = new Dictionary<Guid, Guid>();
        foreach (var revision in Revisions)
        {
            revision.LocalDiagram.Validate();
            revision.EffectiveDefinition.Validate();
            revision.EffectiveComponentBindings.Validate();
            revision.PhysicalAllocation?.Validate();
            foreach (var boundary in revision.LocalDiagram.Interfaces)
            {
                if (identities.Contains(boundary.Id) || (interfaceOwners.TryGetValue(boundary.Id, out var owner) && owner != revision.Selection.BlockId))
                    throw Invalid("An interface identity must remain owned by one exact block occurrence.");
                interfaceOwners[boundary.Id] = revision.Selection.BlockId;
            }
            foreach (var note in revision.LocalDiagram.Notes)
            {
                if (annotationOwners.TryGetValue(note.Id, out var owner) && owner != revision.Selection.BlockId)
                    throw Invalid("An annotation identity must remain owned by its original local diagram.");
                annotationOwners[note.Id] = revision.Selection.BlockId;
            }
        }
        foreach (var input in RefinementInputs)
        {
            if (input is null || !identities.Add(input.Id) || interfaceOwners.ContainsKey(input.Id) || annotationOwners.ContainsKey(input.Id))
                throw Invalid("Refinement inputs need distinct identities that do not alias diagram objects.");
            input.ValidateAgainst(this);
        }
        foreach (var state in States) ValidateHistory(state);
        foreach (var proposal in Proposals)
        {
            if (proposal is null || !identities.Add(proposal.Id) || interfaceOwners.ContainsKey(proposal.Id) || annotationOwners.ContainsKey(proposal.Id))
                throw Invalid("Proposal identities must be distinct from all retained diagram objects.");
            proposal.ValidateAgainst(this);
        }
        foreach (var state in States)
        {
            var seen = new HashSet<Guid> { state.Id }; var current = state;
            while (current.ForkedFrom is { } source)
            {
                _ = Inspect(source);
                if (source.BlockId != current.BlockId || !seen.Add(source.StateId))
                    throw Invalid("An implementation source must be an exact revision of the same block without circular derivation.");
                current = _states[source.StateId];
            }
        }
        foreach (var revision in Revisions)
        {
            ValidateRestoration(revision.Selection, revision.ParentRevisionId, revision.RestoredFrom);
            var children = new HashSet<Guid>();
            foreach (var child in revision.Children)
            {
                _ = Inspect(child);
                if (child.BlockId == revision.Selection.BlockId || !children.Add(child.BlockId))
                    throw Invalid("A local diagram cannot contain itself or the same block occurrence twice.");
            }
            ValidateConnections(revision);
            ValidateAnnotations(revision, identities, interfaceOwners);
        }
        _ = Inspect(SelectedRoot);
        if (Walk(SelectedRoot).Any(s => _states[s.StateId].Archived))
            throw Invalid("A selected design cannot contain a removed implementation; choose a replacement before removing it.");
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
            new(requirements, requirements.Requirements, ImmutableDictionary<DiagramRequirementField, Guid>.Empty),
            Diagram: revision.Diagram, Definition: revision.Definition, ComponentBindings: revision.ComponentBindings,
            PhysicalAllocation: revision.PhysicalAllocation);
    }

    /// <summary>Restore old contents into a draft, not the selected hierarchy or saved heads.</summary>
    public RecursiveBlockDraft RestoreAsDraft(RecursiveBlockDraft draft, BlockSelection source)
    {
        ValidateDraft(draft);
        var baseline = Inspect(draft.Baseline);
        if (draft.Name != baseline.Name || !draft.Children.SequenceEqual(baseline.Children)
            || !draft.LocalDiagram.SameContents(baseline.LocalDiagram)
            || !draft.EffectiveDefinition.SameContents(baseline.EffectiveDefinition)
            || !draft.EffectiveComponentBindings.SameContents(baseline.EffectiveComponentBindings)
            || !SamePhysical(draft.PhysicalAllocation, baseline.PhysicalAllocation)
            || draft.Requirements.Requirements != Requirements(draft.Baseline).Requirements)
            throw new AutomationException("dirty_block_draft", "Save or explicitly decline the existing draft before restoring a whole diagram.");
        var previous = Inspect(source);
        if (source.BlockId != draft.Baseline.BlockId || source.StateId != draft.Baseline.StateId)
            throw Invalid("Restore history from this exact block implementation, not an unrelated alternative.");
        var fields = draft.Requirements;
        foreach (var field in Enum.GetValues<DiagramRequirementField>())
            fields = _requirements[source.StateId].RestoreField(fields, previous.RequirementRevisionId, field);
        return draft with { Name = previous.Name, Children = previous.Children, Requirements = fields,
            RestoredFrom = source, Diagram = previous.Diagram, Definition = previous.Definition, ComponentBindings = previous.ComponentBindings,
            PhysicalAllocation = previous.PhysicalAllocation };
    }

    /// <summary>Atomically produces a new in-memory root and immutable history. A failed
    /// validation or conflict leaves this graph and the supplied draft untouched.</summary>
    public RecursiveBlockSelectionResult SaveDraft(BlockSelection expectedRoot, ImmutableArray<BlockSelection> path,
        RecursiveBlockDraft draft, Guid revisionId, Guid requirementRevisionId, ImmutableArray<Guid> ancestorRevisionIds,
        RequirementRevisionOrigin origin, IReadOnlyCollection<DiagramRequirementResolution>? resolutions = null) =>
        SaveDraftCore(expectedRoot, path, draft, revisionId, requirementRevisionId, ancestorRevisionIds, origin, resolutions, false);

    /// <summary>Commit an inspected implementation draft and select it only after all
    /// containing interface checks succeed. Preview itself requires no mutation.</summary>
    public RecursiveBlockSelectionResult SaveImplementationDraft(BlockSelection expectedRoot, ImmutableArray<BlockSelection> path,
        RecursiveBlockDraft draft, Guid revisionId, Guid requirementRevisionId, ImmutableArray<Guid> ancestorRevisionIds,
        RequirementRevisionOrigin origin) =>
        SaveDraftCore(expectedRoot, path, draft, revisionId, requirementRevisionId, ancestorRevisionIds, origin, null, true);

    private RecursiveBlockSelectionResult SaveDraftCore(BlockSelection expectedRoot, ImmutableArray<BlockSelection> path,
        RecursiveBlockDraft draft, Guid revisionId, Guid requirementRevisionId, ImmutableArray<Guid> ancestorRevisionIds,
        RequirementRevisionOrigin origin, IReadOnlyCollection<DiagramRequirementResolution>? resolutions, bool chooseImplementation)
    {
        ValidateDraft(draft);
        if (expectedRoot != SelectedRoot || path.IsDefaultOrEmpty || path[^1].BlockId != draft.Baseline.BlockId
            || (!chooseImplementation && path[^1] != draft.Baseline)
            || _states[draft.Baseline.StateId].HeadRevisionId != draft.Baseline.RevisionId)
            throw new AutomationException("stale_block_revision", "The saved block or selected hierarchy changed; retain the draft and compare the newer design.");
        // Validate the complete path even for an unchanged save.
        _ = Select(expectedRoot, path, path[^1], ancestorRevisionIds, origin);
        var history = _requirements[draft.Baseline.StateId];
        var requirements = history.Commit(history.Current.Id, draft.Requirements, requirementRevisionId, origin, resolutions);
        var baseline = Inspect(draft.Baseline);
        if (draft.Name == baseline.Name && draft.Children.SequenceEqual(baseline.Children)
            && draft.LocalDiagram.SameContents(baseline.LocalDiagram)
            && draft.EffectiveDefinition.SameContents(baseline.EffectiveDefinition)
            && draft.EffectiveComponentBindings.SameContents(baseline.EffectiveComponentBindings)
            && SamePhysical(draft.PhysicalAllocation, baseline.PhysicalAllocation)
            && requirements.Revision.Requirements == Requirements(draft.Baseline).Requirements)
            return Select(expectedRoot, path, draft.Baseline, ancestorRevisionIds, origin);
        var revision = new RecursiveBlockRevision(new(draft.Baseline.BlockId, draft.Baseline.StateId, revisionId),
            baseline.Selection.RevisionId, draft.Name, requirements.Revision.Id, draft.Children, origin,
            draft.RestoredFrom, draft.Diagram, draft.Definition, draft.ComponentBindings, draft.PhysicalAllocation);
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
            if (!requirementHistory.Retains(saved))
                throw Invalid("Requirement history must preserve every saved revision and its provenance.");
            histories = histories.SetItem(histories.IndexOf(saved), requirementHistory);
        }
        return new(DocumentId, SelectedRoot,
            States.Select(s => s.Id == state.Id ? s with { HeadRevisionId = selection.RevisionId } : s),
            Revisions.Add(revision), histories, ConnectionArchives, ImplementationChanges, RefinementInputs, Proposals);
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
        return new(DocumentId, SelectedRoot, States.Add(state), Revisions.Add(initial), RequirementHistories.Add(requirements), ConnectionArchives, ImplementationChanges, RefinementInputs, Proposals);
    }

    /// <summary>Create an independent implementation from one exact saved revision.
    /// The source stays discoverable and immutable; the new state is not selected.</summary>
    public RecursiveBlockGraph ForkImplementation(BlockSelection source, Guid stateId, Guid revisionId,
        Guid requirementRevisionId, string name, RequirementRevisionOrigin origin, bool emptyInterior = false)
    {
        var original = Inspect(source); Text(name, "A new implementation needs a name.");
        if (States.Any(s => s.BlockId == source.BlockId && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new AutomationException("implementation_name_exists", "Choose a distinct implementation name for this block.");
        var originalRequirements = Requirements(source);
        var state = new BlockDesignState(stateId, source.BlockId, name, revisionId, source);
        var selection = new BlockSelection(source.BlockId, stateId, revisionId);
        var history = new DiagramRequirementHistory(new(DocumentId, source.BlockId, stateId),
            [new(requirementRevisionId, null, originalRequirements.Requirements, origin, [])]);
        BlockLocalDiagram? diagram = original.Diagram;
        if (emptyInterior)
            diagram = new(original.LocalDiagram.Interfaces, [], original.LocalDiagram.Notes.Select(n =>
                n.Target.Kind == DiagramAnnotationTargetKind.Canvas || n.Target.Kind == DiagramAnnotationTargetKind.Block && n.Target.TargetId == source.BlockId
                    ? n : n with { Target = n.Target with { UnresolvedReason = n.Target.UnresolvedReason ?? "The source target is not present in this new implementation." },
                        Origin = n.Target.UnresolvedReason is null ? origin : n.Origin }).ToImmutableArray());
        var initial = new RecursiveBlockRevision(selection, null, original.Name, requirementRevisionId,
            emptyInterior ? [] : original.Children, origin, Diagram: diagram, Definition: original.Definition,
            ComponentBindings: original.ComponentBindings, PhysicalAllocation: original.PhysicalAllocation);
        return AddImplementation(state, initial, history);
    }

    public RecursiveBlockGraph RenameImplementation(Guid stateId, string name, Guid changeId, RequirementRevisionOrigin origin)
    {
        if (!_states.TryGetValue(stateId, out var state)) throw Invalid("The implementation does not exist.");
        Text(name, "An implementation needs a name.");
        if (state.Name == name) return this;
        if (States.Any(s => s.BlockId == state.BlockId && s.Id != stateId && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new AutomationException("implementation_name_exists", "Choose a distinct implementation name for this block.");
        var change = new ImplementationChange(changeId, stateId, ImplementationChangeKind.Rename, state.Name, name, state.Archived, state.Archived, origin);
        return new(DocumentId, SelectedRoot, States.Select(s => s.Id == stateId ? s with { Name = name } : s),
            Revisions, RequirementHistories, ConnectionArchives, ImplementationChanges.Add(change), RefinementInputs, Proposals);
    }

    public RecursiveBlockGraph SetImplementationArchived(Guid stateId, bool archived, Guid changeId, RequirementRevisionOrigin origin)
    {
        if (!_states.TryGetValue(stateId, out var state)) throw Invalid("The implementation does not exist.");
        if (state.Archived == archived) return this;
        if (archived && Walk(SelectedRoot).Any(s => s.StateId == stateId))
            throw new AutomationException("implementation_is_selected", "Choose and save a replacement before removing this implementation from active choices.");
        var change = new ImplementationChange(changeId, stateId, archived ? ImplementationChangeKind.Archive : ImplementationChangeKind.Restore,
            state.Name, state.Name, state.Archived, archived, origin);
        return new(DocumentId, SelectedRoot, States.Select(s => s.Id == stateId ? s with { Archived = archived } : s),
            Revisions, RequirementHistories, ConnectionArchives, ImplementationChanges.Add(change), RefinementInputs, Proposals);
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
        if (_states[replacement.StateId].Archived)
            throw new AutomationException("implementation_removed", "Restore this implementation to active choices before selecting it.");
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
        return new(new(DocumentId, child, states, revisions, RequirementHistories, ConnectionArchives, ImplementationChanges, RefinementInputs, Proposals), [.. created], true);
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
        draft.LocalDiagram.Validate();
        draft.EffectiveDefinition.Validate();
        draft.EffectiveComponentBindings.Validate();
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

    public DiagramConnectionArchive Connections(Guid ownerBlockId) => _connections.TryGetValue(ownerBlockId, out var archive)
        ? archive : throw Invalid("This block has no saved connection archive.");

    /// <summary>Save one relationship/member and its containing local diagram as one
    /// immutable root update. No intermediate archive or partial ancestor update escapes.</summary>
    public RecursiveBlockSelectionResult SaveConnectionDraft(BlockSelection expectedRoot, ImmutableArray<BlockSelection> blockPath,
        ImmutableArray<ConnectionSelection> connectionPath, DiagramConnectionDraft draft, Guid connectionRevisionId,
        Guid requirementRevisionId, ImmutableArray<Guid> connectionAncestorRevisionIds, Guid blockRevisionId,
        Guid blockRequirementRevisionId, ImmutableArray<Guid> blockAncestorRevisionIds, RequirementRevisionOrigin origin)
    {
        if (blockPath.IsDefaultOrEmpty || connectionPath.IsDefaultOrEmpty || draft is null || connectionPath[^1] != draft.Baseline)
            throw Invalid("Connection edits need their exact containing block and member paths.");
        _ = Select(expectedRoot, blockPath, blockPath[^1], blockAncestorRevisionIds, origin);
        var block = Inspect(blockPath[^1]); var archive = Connections(block.Selection.BlockId);
        _ = archive.Select(block.LocalDiagram.Connections, connectionPath, draft.Baseline, connectionAncestorRevisionIds, origin);
        var committed = archive.SaveDraft(draft, connectionRevisionId, requirementRevisionId, origin);
        if (!committed.Changed && draft.DiagramAnnotations.IsDefault) return new(this, [], false);
        var selected = committed.Archive.Select(block.LocalDiagram.Connections, connectionPath, committed.Revision.Selection,
            connectionAncestorRevisionIds, origin);
        var prepared = WithConnections(selected.Archive);
        var blockDraft = prepared.StartDraft(block.Selection);
        blockDraft = blockDraft with { Diagram = block.LocalDiagram with { Connections = selected.Roots,
            Annotations = draft.DiagramAnnotations.IsDefault ? block.LocalDiagram.Annotations : draft.DiagramAnnotations } };
        return prepared.SaveDraft(expectedRoot, blockPath, blockDraft, blockRevisionId, blockRequirementRevisionId, blockAncestorRevisionIds, origin);
    }

    /// <summary>Publish an extended archive without changing any block's pinned local diagram.</summary>
    public RecursiveBlockGraph WithConnections(DiagramConnectionArchive archive)
    {
        if (archive is null || archive.DocumentId != DocumentId || !States.Any(s => s.BlockId == archive.OwnerBlockId))
            throw Invalid("The connection archive belongs to another document or block.");
        var archives = ConnectionArchives;
        if (_connections.TryGetValue(archive.OwnerBlockId, out var saved))
        {
            if (!archive.Retains(saved)) throw Invalid("Publishing connections cannot rewrite any saved connection history.");
            archives = archives.SetItem(archives.IndexOf(saved), archive);
        }
        else archives = archives.Add(archive);
        return new(DocumentId, SelectedRoot, States, Revisions, RequirementHistories, archives, ImplementationChanges, RefinementInputs, Proposals);
    }

    public DiagramRefinementInput RefinementInput(Guid id) => RefinementInputs.SingleOrDefault(i => i.Id == id)
        ?? throw new AutomationException("unknown_refinement_input", "No original input with this exact identity is recorded in the diagram.");

    /// <summary>Append original input without changing any selected revision. Reusing
    /// its identity is safe only when all original content and source references match.</summary>
    public RecursiveBlockGraph WithRefinementInput(DiagramRefinementInput input)
    {
        ArgumentNullException.ThrowIfNull(input); input.ValidateAgainst(this);
        if (RefinementInputs.SingleOrDefault(i => i.Id == input.Id) is { } existing)
            return existing.SameContents(input) ? this : throw new AutomationException("refinement_input_conflict",
                "This input identity already records different content; neither version was replaced.");
        return new(DocumentId, SelectedRoot, States, Revisions, RequirementHistories, ConnectionArchives, ImplementationChanges, RefinementInputs.Add(input), Proposals);
    }

    public BlockProposalRecord Proposal(Guid id) => Proposals.SingleOrDefault(p => p.Id == id)
        ?? throw new AutomationException("unknown_block_proposal", "No candidate with this exact proposal identity exists in the diagram.");

    public RecursiveBlockGraph WithProposal(BlockProposalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record); record.ValidateAgainst(this);
        if (Proposals.SingleOrDefault(p => p.Id == record.Id) is { } previous)
            return previous.SameContents(record) ? this : throw new AutomationException("block_proposal_conflict", "A different proposal already owns this identity.");
        return new(DocumentId, SelectedRoot, States, Revisions, RequirementHistories, ConnectionArchives, ImplementationChanges, RefinementInputs, Proposals.Add(record));
    }

    private void ValidateConnections(RecursiveBlockRevision revision)
    {
        if (revision.LocalDiagram.Connections.IsEmpty) return;
        var archive = Connections(revision.Selection.BlockId);
        var available = revision.Children.Select(Inspect).Append(revision).ToDictionary(r => r.Selection.BlockId);
        foreach (var selection in archive.Walk(revision.LocalDiagram.Connections))
            foreach (var endpoint in archive.Inspect(selection).Endpoints)
                if (!available.TryGetValue(endpoint.BlockId, out var target)
                    || (endpoint.InterfaceId is { } id && !target.LocalDiagram.Interfaces.Any(i => i.Id == id)))
                    throw Invalid("A connection endpoint must target this diagram's boundary or an exact direct child and its pinned interface; do not guess a replacement.");
    }

    private void ValidateAnnotations(RecursiveBlockRevision revision, HashSet<Guid> identities, Dictionary<Guid, Guid> interfaces)
    {
        var blocks = revision.Children.Select(c => c.BlockId).Append(revision.Selection.BlockId).ToHashSet();
        HashSet<Guid> connections = revision.LocalDiagram.Connections.IsEmpty ? [] : Connections(revision.Selection.BlockId)
            .Walk(revision.LocalDiagram.Connections).Select(c => c.ConnectionId).ToHashSet();
        foreach (var note in revision.LocalDiagram.Notes)
        {
            if (identities.Contains(note.Id) || interfaces.ContainsKey(note.Id))
                throw Invalid("Annotation identities must remain separate from engineering objects and interfaces.");
            bool present = note.Target.Kind switch
            {
                DiagramAnnotationTargetKind.Canvas => true,
                DiagramAnnotationTargetKind.Block => blocks.Contains(note.Target.TargetId!.Value),
                DiagramAnnotationTargetKind.Connection => connections.Contains(note.Target.TargetId!.Value),
                _ => false
            };
            if (!present && note.Target.UnresolvedReason is null)
                throw Invalid("A removed annotation target needs its exact former identity and an explicit unresolved reason; do not silently retarget or drop the note.");
            if (present && note.Target.UnresolvedReason is not null)
                throw Invalid("A present annotation target cannot simultaneously be reported as unresolved.");
        }
    }

    private static void Text(string? text, string message)
    {
        if (string.IsNullOrWhiteSpace(text)) throw Invalid(message);
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException) { throw Invalid("Block text cannot be preserved in XML."); }
    }
    private static bool SamePhysical(BlockPhysicalAllocation? left, BlockPhysicalAllocation? right) => left is null
        ? right is null : right is not null && left.SameContents(right);
    private static AutomationException Invalid(string message) => new("invalid_recursive_block_graph", message);
}
