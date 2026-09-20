using System.Collections.Immutable;

namespace KiCad.Automation.Model;

public enum DiagramConnectionKind { Abstract, Interface, SignalGroup, DifferentialPair, Signal }
public sealed record ConnectionSelection(Guid ConnectionId, Guid StateId, Guid RevisionId);
public sealed record ConnectionDesignState(Guid Id, Guid ConnectionId, string Name, Guid HeadRevisionId);
public sealed record DiagramConnectionRevision(ConnectionSelection Selection, Guid? ParentRevisionId,
    string Name, DiagramConnectionKind Kind, ImmutableArray<DiagramEndpointBinding> Endpoints,
    Guid RequirementRevisionId, ImmutableArray<ConnectionSelection> Members, RequirementRevisionOrigin Origin);

public sealed record DiagramConnectionSelectionResult(DiagramConnectionArchive Archive,
    ImmutableArray<ConnectionSelection> Roots, ImmutableArray<ConnectionSelection> CreatedAncestors, bool Changed);
public sealed record DiagramConnectionDraft(ConnectionSelection Baseline, string Name, DiagramConnectionKind Kind,
    ImmutableArray<DiagramEndpointBinding> Endpoints, ImmutableArray<ConnectionSelection> Members, DiagramRequirementDraft Requirements);
public sealed record DiagramConnectionCommit(DiagramConnectionArchive Archive, DiagramConnectionRevision Revision, bool Changed);

/// <summary>Immutable connection implementations belonging to one block's local diagram.
/// The containing block revision pins its connection roots. Publishing a new connection
/// head never changes those pins, native nets, or an unrelated block.</summary>
public sealed class DiagramConnectionArchive
{
    public Guid DocumentId { get; }
    public Guid OwnerBlockId { get; }
    public ImmutableArray<ConnectionDesignState> States { get; }
    public ImmutableArray<DiagramConnectionRevision> Revisions { get; }
    public ImmutableArray<DiagramRequirementHistory> RequirementHistories { get; }
    private readonly ImmutableDictionary<Guid, ConnectionDesignState> _states;
    private readonly ImmutableDictionary<Guid, DiagramConnectionRevision> _revisions;
    private readonly ImmutableDictionary<Guid, DiagramRequirementHistory> _requirements;

    public DiagramConnectionArchive(Guid documentId, Guid ownerBlockId, IEnumerable<ConnectionDesignState> states,
        IEnumerable<DiagramConnectionRevision> revisions, IEnumerable<DiagramRequirementHistory> requirementHistories)
    {
        if (documentId == Guid.Empty || ownerBlockId == Guid.Empty || states is null || revisions is null || requirementHistories is null)
            throw Invalid("A local connection archive needs an exact document and owning block.");
        DocumentId = documentId; OwnerBlockId = ownerBlockId;
        States = states.ToImmutableArray(); Revisions = revisions.ToImmutableArray(); RequirementHistories = requirementHistories.ToImmutableArray();
        var statesById = ImmutableDictionary.CreateBuilder<Guid, ConnectionDesignState>();
        foreach (var state in States)
        {
            if (state is null || state.Id == Guid.Empty || state.ConnectionId == Guid.Empty || state.HeadRevisionId == Guid.Empty
                || !statesById.TryAdd(state.Id, state)) throw Invalid("Connection implementations require distinct identities and exact saved heads.");
            Name(state.Name);
        }
        _states = statesById.ToImmutable();
        var requirements = ImmutableDictionary.CreateBuilder<Guid, DiagramRequirementHistory>();
        foreach (var history in RequirementHistories)
            if (history is null || history.Scope.DocumentId != DocumentId
                || !_states.TryGetValue(history.Scope.DesignStateId, out var state)
                || history.Scope.OwnerId != state.ConnectionId || !requirements.TryAdd(state.Id, history))
                throw Invalid("Requirement history belongs to one exact connection implementation in this document.");
        _requirements = requirements.ToImmutable();
        var revisionIndex = ImmutableDictionary.CreateBuilder<Guid, DiagramConnectionRevision>();
        foreach (var revision in Revisions)
        {
            if (revision?.Selection is not { } selection || selection.RevisionId == Guid.Empty
                || !_states.TryGetValue(selection.StateId, out var state) || state.ConnectionId != selection.ConnectionId
                || !revisionIndex.TryAdd(selection.RevisionId, revision) || !Enum.IsDefined(revision.Kind)
                || revision.Endpoints.IsDefaultOrEmpty || revision.Endpoints.Length < 2 || revision.Members.IsDefault
                || revision.Origin is null) throw Invalid("Connections need exact identities, a supported kind, at least two endpoints and an explicit member list.");
            Name(revision.Name); revision.Origin.Validate();
            foreach (var endpoint in revision.Endpoints)
            {
                if (endpoint is null) throw Invalid("A connection endpoint cannot be null.");
                endpoint.Validate();
            }
            if (!_requirements.TryGetValue(state.Id, out var history)) throw Invalid("Every connection implementation needs its three requirement fields and history.");
            _ = history.Inspect(revision.RequirementRevisionId);
        }
        _revisions = revisionIndex.ToImmutable();
        var identities = States.Select(s => s.ConnectionId).Distinct().ToHashSet();
        if (!identities.Add(DocumentId) || !identities.Add(OwnerBlockId) || States.Any(s => !identities.Add(s.Id))
            || Revisions.Any(r => !identities.Add(r.Selection.RevisionId)))
            throw Invalid("Connection, implementation, revision, document and block identities cannot alias.");
        foreach (var state in States)
        {
            var seen = new HashSet<Guid>(); Guid? current = state.HeadRevisionId;
            while (current is { } id)
            {
                if (!seen.Add(id) || !_revisions.TryGetValue(id, out var revision)
                    || revision.Selection.StateId != state.Id || revision.Selection.ConnectionId != state.ConnectionId)
                    throw Invalid("Each connection implementation requires its complete acyclic saved history.");
                current = revision.ParentRevisionId;
            }
            if (seen.Count != Revisions.Count(r => r.Selection.StateId == state.Id))
                throw Invalid("No connection revision can be orphaned outside its implementation history.");
        }
        foreach (var revision in Revisions)
        {
            var members = revision.Members.Select(Inspect).ToArray();
            if (revision.Kind == DiagramConnectionKind.Signal && members.Length != 0)
                throw Invalid("An individual signal cannot hide another member hierarchy.");
            if (revision.Kind == DiagramConnectionKind.DifferentialPair
                && (members.Length != 2 || members.Any(m => m.Kind != DiagramConnectionKind.Signal)))
                throw Invalid("A differential pair needs exactly two explicitly identified signal members; other groups are not pairs.");
            _ = Walk([revision.Selection]);
        }
    }

    public DiagramConnectionRevision Inspect(ConnectionSelection selection)
    {
        if (selection is null || !_revisions.TryGetValue(selection.RevisionId, out var revision) || revision.Selection != selection)
            throw Invalid("Identify an existing exact connection, implementation and revision.");
        return revision;
    }

    public DiagramRequirementSnapshot Requirements(ConnectionSelection selection)
    {
        var revision = Inspect(selection);
        return _requirements[selection.StateId].Inspect(revision.RequirementRevisionId);
    }

    public DiagramConnectionDraft StartDraft(ConnectionSelection selection)
    {
        var revision = Inspect(selection); var requirements = Requirements(selection);
        return new(selection, revision.Name, revision.Kind, revision.Endpoints, revision.Members,
            new(requirements, requirements.Requirements, ImmutableDictionary<DiagramRequirementField, Guid>.Empty));
    }

    public DiagramConnectionCommit SaveDraft(DiagramConnectionDraft draft, Guid revisionId, Guid requirementRevisionId,
        RequirementRevisionOrigin origin, IEnumerable<DiagramRequirementResolution>? resolutions = null)
    {
        if (draft?.Requirements is null || draft.Endpoints.IsDefault || draft.Members.IsDefault)
            throw Invalid("A connection draft needs its exact baseline, endpoint/member data and requirement fields.");
        var baseline = Inspect(draft.Baseline);
        if (draft.Requirements.Baseline != Requirements(draft.Baseline)) throw Invalid("The connection draft does not match its saved requirement baseline.");
        if (_states[draft.Baseline.StateId].HeadRevisionId != draft.Baseline.RevisionId)
            throw new AutomationException("stale_connection_revision", "This connection has a newer saved revision; retain the current draft for comparison.");
        var history = _requirements[draft.Baseline.StateId];
        var requirements = history.Commit(history.Current.Id, draft.Requirements, requirementRevisionId, origin, resolutions);
        if (baseline.Name == draft.Name && baseline.Kind == draft.Kind && baseline.Members.SequenceEqual(draft.Members)
            && baseline.Endpoints.Length == draft.Endpoints.Length && baseline.Endpoints.Zip(draft.Endpoints).All(p => p.First.SameDefinition(p.Second))
            && requirements.Revision.Requirements == Requirements(draft.Baseline).Requirements)
            return new(this, baseline, false);
        var revision = new DiagramConnectionRevision(draft.Baseline with { RevisionId = revisionId }, baseline.Selection.RevisionId,
            draft.Name, draft.Kind, draft.Endpoints, requirements.Revision.Id, draft.Members, origin);
        var archive = AppendRevision(baseline.Selection.RevisionId, revision, requirements.History);
        return new(archive, revision, true);
    }

    public ImmutableArray<ConnectionSelection> Walk(ImmutableArray<ConnectionSelection> roots)
    {
        if (roots.IsDefault) throw Invalid("Provide the local diagram's explicit connection roots.");
        var found = new HashSet<Guid>(); var result = ImmutableArray.CreateBuilder<ConnectionSelection>();
        var pending = new Stack<ConnectionSelection>(roots.Reverse());
        while (pending.TryPop(out var selection))
        {
            var revision = Inspect(selection);
            if (!found.Add(selection.ConnectionId)) throw Invalid("Connection members cannot cycle or share one occurrence through different group paths.");
            result.Add(selection);
            for (int i = revision.Members.Length - 1; i >= 0; --i) pending.Push(revision.Members[i]);
        }
        return result.ToImmutable();
    }

    public ImmutableArray<DiagramConnectionRevision> History(Guid stateId)
    {
        if (!_states.TryGetValue(stateId, out var state)) throw Invalid("This connection implementation does not exist.");
        var result = ImmutableArray.CreateBuilder<DiagramConnectionRevision>(); Guid? current = state.HeadRevisionId;
        while (current is { } id)
        { var revision = _revisions[id]; result.Add(revision); current = revision.ParentRevisionId; }
        return result.ToImmutable();
    }

    public bool Retains(DiagramConnectionArchive saved) => saved is not null && DocumentId == saved.DocumentId
        && OwnerBlockId == saved.OwnerBlockId
        && saved.States.All(s => _states.TryGetValue(s.Id, out var current) && s.ConnectionId == current.ConnectionId && s.Name == current.Name)
        && saved.Revisions.All(r => _revisions.TryGetValue(r.Selection.RevisionId, out var current) && Same(r, current))
        && saved.RequirementHistories.All(h => _requirements.TryGetValue(h.Scope.DesignStateId, out var current) && current.Retains(h));

    private static bool Same(DiagramConnectionRevision a, DiagramConnectionRevision b) =>
        a.Selection == b.Selection && a.ParentRevisionId == b.ParentRevisionId && a.Name == b.Name && a.Kind == b.Kind
        && a.RequirementRevisionId == b.RequirementRevisionId && a.Members.SequenceEqual(b.Members)
        && DiagramRequirementHistory.SameOrigin(a.Origin, b.Origin) && a.Endpoints.Length == b.Endpoints.Length
        && a.Endpoints.Zip(b.Endpoints).All(p => p.First.SameDefinition(p.Second));

    public DiagramConnectionArchive AppendRevision(Guid expectedHead, DiagramConnectionRevision revision,
        DiagramRequirementHistory? requirementHistory = null)
    {
        if (revision?.Selection is not { } selection || !_states.TryGetValue(selection.StateId, out var state)
            || state.ConnectionId != selection.ConnectionId || state.HeadRevisionId != expectedHead || revision.ParentRevisionId != expectedHead)
            throw new AutomationException("stale_connection_revision", "The connection implementation changed; retain the candidate and compare the saved revision.");
        if (_revisions.ContainsKey(selection.RevisionId)) throw Invalid("A saved connection revision identity cannot be reused.");
        var histories = RequirementHistories;
        if (requirementHistory is not null)
        {
            var saved = _requirements[state.Id];
            if (!requirementHistory.Retains(saved)) throw Invalid("Connection requirement history cannot rewrite saved values or provenance.");
            histories = histories.SetItem(histories.IndexOf(saved), requirementHistory);
        }
        return new(DocumentId, OwnerBlockId, States.Select(s => s.Id == state.Id ? s with { HeadRevisionId = selection.RevisionId } : s),
            Revisions.Add(revision), histories);
    }

    /// <summary>Refine one connection/member and rebuild only its containing groups.
    /// The caller must then save the returned roots in a new guarded block revision.</summary>
    public DiagramConnectionSelectionResult Select(ImmutableArray<ConnectionSelection> roots,
        ImmutableArray<ConnectionSelection> path, ConnectionSelection replacement, ImmutableArray<Guid> ancestorRevisionIds,
        RequirementRevisionOrigin origin)
    {
        _ = Walk(roots); _ = Inspect(replacement);
        if (path.IsDefaultOrEmpty || !roots.Contains(path[0]) || ancestorRevisionIds.IsDefault
            || ancestorRevisionIds.Length != path.Length - 1 || origin is null)
            throw Invalid("Supply the exact root-to-member path and fresh revision identities for its containing groups.");
        origin.Validate();
        for (int i = 0; i < path.Length; ++i)
            if (i + 1 < path.Length && !Inspect(path[i]).Members.Contains(path[i + 1]))
                throw Invalid("The requested member path is not part of this local diagram snapshot.");
        if (path[^1].ConnectionId != replacement.ConnectionId) throw Invalid("Selection cannot substitute a different connection occurrence.");
        if (path[^1] == replacement) return new(this, roots, [], false);
        if (_states[replacement.StateId].HeadRevisionId != replacement.RevisionId)
            throw new AutomationException("historical_connection_requires_draft", "Restore an earlier connection as a new revision before selecting it.");
        var ids = _revisions.Keys.Concat(_states.Keys).Concat(States.Select(s => s.ConnectionId)).Append(DocumentId).Append(OwnerBlockId).ToHashSet();
        if (ancestorRevisionIds.Any(id => id == Guid.Empty || !ids.Add(id))) throw Invalid("Containing connection revisions require fresh distinct identities.");
        var states = States.ToBuilder(); var revisions = Revisions.ToBuilder();
        var ancestors = new ConnectionSelection[path.Length - 1]; var member = replacement;
        for (int i = path.Length - 2; i >= 0; --i)
        {
            var parent = Inspect(path[i]); var state = _states[parent.Selection.StateId];
            if (state.HeadRevisionId != parent.Selection.RevisionId)
                throw new AutomationException("stale_connection_parent", "A containing interface has a newer saved revision; compare it before selecting this member.");
            var next = parent.Selection with { RevisionId = ancestorRevisionIds[i] };
            revisions.Add(parent with { Selection = next, ParentRevisionId = parent.Selection.RevisionId,
                Members = parent.Members.Select(m => m == path[i + 1] ? member : m).ToImmutableArray(), Origin = origin });
            states[states.IndexOf(state)] = state with { HeadRevisionId = next.RevisionId };
            ancestors[i] = next; member = next;
        }
        var result = new DiagramConnectionArchive(DocumentId, OwnerBlockId, states, revisions, RequirementHistories);
        var selectedRoots = roots.Select(r => r == path[0] ? member : r).ToImmutableArray();
        _ = result.Walk(selectedRoots);
        return new(result, selectedRoots, [.. ancestors], true);
    }

    private static void Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw Invalid("A connection or implementation needs a name.");
        DiagramEndpointBinding.Text(name);
    }
    private static AutomationException Invalid(string message) => new("invalid_diagram_connection_archive", message);
}
