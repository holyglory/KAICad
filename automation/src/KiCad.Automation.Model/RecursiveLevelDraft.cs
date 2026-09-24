using System.Collections.Immutable;

namespace KiCad.Automation.Model;

/// <summary>A block drawn into a diagram level before its first save. It starts as a caption
/// (owner decision n98a3f3c41084f0ed): requirement text, interfaces and a definition are optional.</summary>
public sealed record NewBlockOccurrence(BlockSelection Selection, Guid RequirementRevisionId, string ImplementationName, string Name,
    DiagramRequirements Requirements, ImmutableArray<DiagramBoundaryInterface> Interfaces, BlockDefinition? Definition);

/// <summary>A connection drawn into a diagram level before its first save; it starts as a caption.</summary>
public sealed record NewConnectionOccurrence(ConnectionSelection Selection, Guid RequirementRevisionId, string ImplementationName,
    string Name, DiagramConnectionKind Kind, DiagramDomain Domain, DiagramConnectionDirection Direction,
    ImmutableArray<DiagramEndpointBinding> Endpoints, DiagramRequirements Requirements, InterconnectRealization? Realization);

/// <summary>One Save/Decline scope per diagram level (contract rbg-v2 section 4.6): the level itself,
/// edits of its direct children and root connections, and the blocks and connections drawn into it.</summary>
public sealed record RecursiveLevelDraft(RecursiveBlockDraft Scope, ImmutableArray<RecursiveBlockDraft> ChildDrafts,
    ImmutableArray<DiagramConnectionDraft> ConnectionDrafts, ImmutableArray<NewBlockOccurrence> NewChildren,
    ImmutableArray<NewConnectionOccurrence> NewConnections);

public sealed record LevelRevisionId(Guid RevisionId, Guid RequirementRevisionId);

/// <summary>Caller-chosen identities for the revisions a level save may create. Children and
/// Connections are keyed by the edited child block and root connection identities.</summary>
public sealed record LevelRevisionIds(Guid ScopeRevisionId, Guid ScopeRequirementRevisionId, ImmutableArray<Guid> AncestorRevisionIds,
    ImmutableDictionary<Guid, LevelRevisionId> Children, ImmutableDictionary<Guid, LevelRevisionId> Connections);

public sealed record RecursiveLevelSaveResult(RecursiveBlockGraph Graph, bool Changed, ImmutableArray<BlockSelection> CreatedBlockRevisions,
    ImmutableArray<ConnectionSelection> CreatedConnectionRevisions, int PrunedPresentationEntries, ImmutableArray<BlockSelection> CreatedAncestors);

public sealed partial class RecursiveBlockGraph
{
    public RecursiveLevelDraft StartLevelDraft(BlockSelection scope) => new(StartDraft(scope), [], [], [], []);

    /// <summary>Saves one level draft atomically (contract rbg-v2 section 4.6, L1-L5 and steps 1-7):
    /// new blocks and connections get one implementation, one revision and one requirement revision
    /// each; changed child and connection drafts get one new revision each (never selected on their
    /// own); the level gets one revision pinning all of them; and the containing levels get one
    /// snapshot each. Nothing is written when nothing changed. Any failure leaves this graph unchanged.</summary>
    public RecursiveLevelSaveResult SaveLevelDraft(BlockSelection expectedRoot, ImmutableArray<BlockSelection> path, RecursiveLevelDraft draft,
        LevelRevisionIds ids, RequirementRevisionOrigin origin, IReadOnlyCollection<DiagramRequirementResolution>? resolutions = null,
        bool selectImplementation = false)
    {
        if (draft?.Scope?.Baseline is null || draft.ChildDrafts.IsDefault || draft.ConnectionDrafts.IsDefault || draft.NewChildren.IsDefault
            || draft.NewConnections.IsDefault || ids is null || ids.AncestorRevisionIds.IsDefault || ids.Children is null
            || ids.Connections is null || origin is null)
            throw Level("Provide the complete level draft, the identities of the revisions it may create and the change origin.");
        origin.Validate();
        var scope = draft.Scope;
        var baseline = Inspect(scope.Baseline);
        // L1 and the stale checks of an ordinary block save.
        if (expectedRoot != SelectedRoot || path.IsDefaultOrEmpty || path[^1].BlockId != scope.Baseline.BlockId
            || (!selectImplementation && path[^1] != scope.Baseline) || _states[scope.Baseline.StateId].HeadRevisionId != scope.Baseline.RevisionId)
            throw new AutomationException("stale_block_revision", "The saved level or selected hierarchy changed; retain the draft and compare the newer design.");
        _ = Select(expectedRoot, path, path[^1], ids.AncestorRevisionIds, origin);
        if (scope.Children.IsDefault || scope.Children.Distinct().Count() != scope.Children.Length)
            throw Level("A level lists each child block once.");
        Guid level = scope.Baseline.BlockId;
        var used = RetainedIdentities();
        void Fresh(Guid id)
        {
            if (id == Guid.Empty || !used.Add(id))
                throw new AutomationException("identity_reused", "Every new block, connection, interface and revision needs a fresh identity that is not used anywhere in this diagram.");
        }
        // L4: blocks and connections drawn into this level.
        var added = new HashSet<Guid>();
        foreach (var child in draft.NewChildren)
        {
            if (child?.Selection is not { } selection || child.Requirements is null || child.Interfaces.IsDefault || !added.Add(selection.BlockId))
                throw Level("Declare each new block once with its caption, requirement fields and interface list.");
            LevelText(child.Name, "A new block needs a caption."); LevelText(child.ImplementationName, "A new block needs an implementation name.");
            child.Requirements.Validate(); child.Definition?.Validate();
            Fresh(selection.BlockId); Fresh(selection.StateId); Fresh(selection.RevisionId); Fresh(child.RequirementRevisionId);
            foreach (var boundary in child.Interfaces) Fresh(boundary?.Id ?? Guid.Empty);
            new BlockLocalDiagram(child.Interfaces, []).Validate(selection.BlockId);
            if (scope.Children.Count(c => c == selection) != 1)
                throw Level("Each new block appears exactly once among the level's children.");
        }
        var addedConnections = new HashSet<Guid>();
        foreach (var link in draft.NewConnections)
        {
            if (link?.Selection is not { } selection || link.Requirements is null || link.Endpoints.IsDefault || !addedConnections.Add(selection.ConnectionId))
                throw Level("Declare each new connection once with its caption, endpoints and requirement fields.");
            LevelText(link.Name, "A new connection needs a caption."); LevelText(link.ImplementationName, "A new connection needs an implementation name.");
            link.Requirements.Validate();
            if (!Enum.IsDefined(link.Kind) || !Enum.IsDefined(link.Domain) || !Enum.IsDefined(link.Direction))
                throw Level("A new connection uses a supported kind, domain and direction.");
            Fresh(selection.ConnectionId); Fresh(selection.StateId); Fresh(selection.RevisionId); Fresh(link.RequirementRevisionId);
            foreach (var segment in link.Realization?.SegmentList ?? []) Fresh(segment.Id);
            if (scope.LocalDiagram.Connections.Count(c => c == selection) != 1)
                throw Level("Each new connection appears exactly once among the level's connections.");
        }
        // L2: edits of direct children stay on their boundary; their interiors belong to their own level.
        var childDrafts = new Dictionary<Guid, RecursiveBlockDraft>();
        foreach (var child in draft.ChildDrafts)
        {
            if (child?.Baseline is null || !baseline.Children.Contains(child.Baseline) || !scope.Children.Contains(child.Baseline)
                || !childDrafts.TryAdd(child.Baseline.BlockId, child))
                throw Level("A child draft edits a distinct child that the saved level pins and the draft keeps.");
            ValidateDraft(child);
            if (_states[child.Baseline.StateId].HeadRevisionId != child.Baseline.RevisionId)
                throw new AutomationException("stale_child_revision", "This child block has a newer saved revision; retain the draft and compare it.");
            var saved = Inspect(child.Baseline);
            if (!ChildBoundaryOnly(child, saved))
                throw new AutomationException("child_interior_edit_not_allowed",
                    "Edit a child's own diagram in its level; from here only its caption, requirements, interfaces, definition, components and physical allocation can change.");
        }
        // L3: edits of root connections keep their member hierarchy.
        var connectionDrafts = new Dictionary<Guid, DiagramConnectionDraft>();
        foreach (var link in draft.ConnectionDrafts)
        {
            if (link?.Baseline is null || !baseline.LocalDiagram.Connections.Contains(link.Baseline) || !scope.LocalDiagram.Connections.Contains(link.Baseline)
                || !connectionDrafts.TryAdd(link.Baseline.ConnectionId, link))
                throw Level("A connection draft edits a distinct root connection that the saved level pins and the draft keeps.");
            if (!link.DiagramAnnotations.IsDefault)
                throw Level("Notes belong to the level draft, not to a connection draft.");
            var saved = Connections(level).Inspect(link.Baseline);
            if (link.Members.IsDefault || !link.Members.SequenceEqual(saved.Members))
                throw new AutomationException("connection_member_edit_requires_member_path", "Edit a connection's members through their own member path.");
        }
        // L5: exactly one identity pair per child and connection draft.
        if (!ids.Children.Keys.ToHashSet().SetEquals(childDrafts.Keys) || !ids.Connections.Keys.ToHashSet().SetEquals(connectionDrafts.Keys))
            throw Level("Assign one new revision identity pair to each child draft and each connection draft, and no others.");
        Fresh(ids.ScopeRevisionId); Fresh(ids.ScopeRequirementRevisionId);
        foreach (var id in ids.AncestorRevisionIds) Fresh(id);
        foreach (var pair in ids.Children.Values.Concat(ids.Connections.Values))
        {
            if (pair is null) throw Level("A revision identity pair cannot be missing.");
            Fresh(pair.RevisionId); Fresh(pair.RequirementRevisionId);
        }

        // Step 2: create each new block with one implementation, one revision and one requirement revision.
        var graph = this;
        var createdBlocks = ImmutableArray.CreateBuilder<BlockSelection>();
        if (!draft.NewChildren.IsEmpty)
        {
            var states = States.ToBuilder(); var revisions = Revisions.ToBuilder(); var histories = RequirementHistories.ToBuilder();
            foreach (var child in draft.NewChildren)
            {
                var selection = child.Selection;
                states.Add(new(selection.StateId, selection.BlockId, child.ImplementationName, selection.RevisionId));
                histories.Add(new(new(DocumentId, selection.BlockId, selection.StateId),
                    [new(child.RequirementRevisionId, null, child.Requirements, origin, [])]));
                revisions.Add(new(selection, null, child.Name, child.RequirementRevisionId, [], origin,
                    Diagram: child.Interfaces.IsEmpty ? null : new BlockLocalDiagram(child.Interfaces, []), Definition: child.Definition));
                createdBlocks.Add(selection);
            }
            graph = new(DocumentId, SelectedRoot, states, revisions, histories, ConnectionArchives, ImplementationChanges, RefinementInputs, Proposals);
        }
        // Step 3: commit changed child drafts through AppendRevision, never Select.
        var blockSubstitutions = new Dictionary<BlockSelection, BlockSelection>();
        foreach (var child in draft.ChildDrafts)
        {
            var saved = graph.Inspect(child.Baseline);
            var history = graph._requirements[child.Baseline.StateId];
            var pair = ids.Children[child.Baseline.BlockId];
            var requirements = history.Commit(history.Current.Id, child.Requirements, pair.RequirementRevisionId, origin, For(resolutions, history.Scope));
            if (child.Name == saved.Name && child.LocalDiagram.Interfaces.SequenceEqual(saved.LocalDiagram.Interfaces)
                && child.EffectiveDefinition.SameContents(saved.EffectiveDefinition)
                && child.EffectiveComponentBindings.SameContents(saved.EffectiveComponentBindings)
                && SamePhysical(child.PhysicalAllocation, saved.PhysicalAllocation)
                && requirements.Revision.Requirements == graph.Requirements(child.Baseline).Requirements)
                continue;
            BlockLocalDiagram? diagram = saved.Diagram is null && child.LocalDiagram.Interfaces.IsEmpty ? null
                : saved.LocalDiagram with { Interfaces = child.LocalDiagram.Interfaces, InterfaceRealizations = child.LocalDiagram.InterfaceRealizations };
            var revision = new RecursiveBlockRevision(child.Baseline with { RevisionId = pair.RevisionId }, saved.Selection.RevisionId, child.Name,
                requirements.Revision.Id, saved.Children, origin, null, diagram, child.Definition, child.ComponentBindings, child.PhysicalAllocation);
            graph = graph.AppendRevision(saved.Selection.RevisionId, revision, requirements.History);
            blockSubstitutions[child.Baseline] = revision.Selection;
            createdBlocks.Add(revision.Selection);
        }
        // Step 4: create new connections in the level's archive (creating the archive if absent) and commit connection drafts.
        var createdConnections = ImmutableArray.CreateBuilder<ConnectionSelection>();
        var connectionSubstitutions = new Dictionary<ConnectionSelection, ConnectionSelection>();
        if (!draft.NewConnections.IsEmpty || !draft.ConnectionDrafts.IsEmpty)
        {
            var existing = graph._connections.GetValueOrDefault(level);
            var archive = existing;
            if (!draft.NewConnections.IsEmpty)
            {
                var states = existing?.States.ToBuilder() ?? ImmutableArray.CreateBuilder<ConnectionDesignState>();
                var revisions = existing?.Revisions.ToBuilder() ?? ImmutableArray.CreateBuilder<DiagramConnectionRevision>();
                var histories = existing?.RequirementHistories.ToBuilder() ?? ImmutableArray.CreateBuilder<DiagramRequirementHistory>();
                foreach (var link in draft.NewConnections)
                {
                    var selection = link.Selection;
                    states.Add(new(selection.StateId, selection.ConnectionId, link.ImplementationName, selection.RevisionId));
                    revisions.Add(new(selection, null, link.Name, link.Kind, link.Endpoints, link.RequirementRevisionId, [], origin,
                        link.Domain, link.Direction, link.Realization));
                    histories.Add(new(new(DocumentId, selection.ConnectionId, selection.StateId), [new(link.RequirementRevisionId, null, link.Requirements, origin, [])]));
                    createdConnections.Add(selection);
                }
                archive = new DiagramConnectionArchive(DocumentId, level, states, revisions, histories);
            }
            foreach (var link in draft.ConnectionDrafts)
            {
                var pair = ids.Connections[link.Baseline.ConnectionId];
                var scopeOf = new DiagramRequirementScope(DocumentId, link.Baseline.ConnectionId, link.Baseline.StateId);
                var committed = archive!.SaveDraft(link, pair.RevisionId, pair.RequirementRevisionId, origin, For(resolutions, scopeOf));
                if (!committed.Changed) continue;
                archive = committed.Archive; connectionSubstitutions[link.Baseline] = committed.Revision.Selection;
                createdConnections.Add(committed.Revision.Selection);
            }
            if (archive is not null && !ReferenceEquals(archive, existing)) graph = graph.WithConnections(archive);
        }
        // Step 5: the level revision pins every new and changed child and connection.
        var children = scope.Children.Select(c => blockSubstitutions.TryGetValue(c, out var next) ? next : c).ToImmutableArray();
        BlockLocalDiagram? local = scope.Diagram is null ? null : scope.Diagram with
        {
            Connections = [.. scope.Diagram.Connections.Select(c => connectionSubstitutions.TryGetValue(c, out var next) ? next : c)]
        };
        // A level that still defines nothing keeps "no diagram" rather than an empty one.
        if (baseline.Diagram is null && local is not null && local.Interfaces.IsEmpty && local.Connections.IsEmpty && local.Notes.IsEmpty
            && local.Layout.IsEmpty && local.Realizations.IsEmpty) local = null;
        var scopeDraft = scope with { Children = children, Diagram = local };
        var scopeHistory = graph._requirements[scope.Baseline.StateId];
        var result = graph.SaveDraftCore(expectedRoot, path, scopeDraft, ids.ScopeRevisionId, ids.ScopeRequirementRevisionId, ids.AncestorRevisionIds,
            origin, For(resolutions, scopeHistory.Scope), selectImplementation);
        // Step 6: nothing changed anywhere, so the caller writes nothing.
        if (!result.Changed) return new(this, false, [], [], 0, []);
        if (result.Graph._revisions.TryGetValue(ids.ScopeRevisionId, out var levelRevision)) createdBlocks.Add(levelRevision.Selection);
        return new(result.Graph, true, createdBlocks.ToImmutable(), createdConnections.ToImmutable(), result.PrunedPresentationEntries, result.CreatedAncestors);
    }

    /// <summary>A child edited from its parent's level may change its caption, requirements, interfaces
    /// (with domain and direction), definition, component bindings and physical allocation, and may drop
    /// only the realization records of interfaces it removes (contract rbg-v2 L2).</summary>
    private static bool ChildBoundaryOnly(RecursiveBlockDraft child, RecursiveBlockRevision saved)
    {
        var kept = child.LocalDiagram.Interfaces.Select(i => i.Id).ToHashSet();
        var expected = saved.LocalDiagram.Realizations.Where(r => kept.Contains(r.InterfaceId)).ToImmutableArray();
        return child.RestoredFrom is null && child.Children.SequenceEqual(saved.Children)
            && child.LocalDiagram.Connections.SequenceEqual(saved.LocalDiagram.Connections)
            && child.LocalDiagram.Notes.Length == saved.LocalDiagram.Notes.Length
            && child.LocalDiagram.Notes.Zip(saved.LocalDiagram.Notes).All(n => n.First.SameContents(n.Second))
            && DiagramPresentationView.Same(child.LocalDiagram.Presentation, saved.LocalDiagram.Presentation)
            && InterfaceRealization.Same(child.LocalDiagram.Realizations, expected);
    }

    private static DiagramRequirementResolution[]? For(IReadOnlyCollection<DiagramRequirementResolution>? resolutions,
        DiagramRequirementScope scope) => resolutions?.Where(r => r?.Scope == scope).ToArray();

    private static void LevelText(string? text, string message)
    {
        if (string.IsNullOrWhiteSpace(text)) throw Level(message);
        try { System.Xml.XmlConvert.VerifyXmlChars(text); }
        catch (System.Xml.XmlException) { throw Level("Diagram text cannot contain characters that XML cannot preserve."); }
    }

    internal static AutomationException Level(string message) => new("invalid_level_draft", message);
}
