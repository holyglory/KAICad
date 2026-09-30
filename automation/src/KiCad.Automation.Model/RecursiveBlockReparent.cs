using System.Collections.Immutable;

namespace KiCad.Automation.Model;

/// <summary>Moving block B from parent P (end of SourceParentPath) to parent Q (end of
/// TargetParentPath) in the selected design (contract rbg-v2 section 4.9). NewRevisionIds maps
/// every block on either path to the fresh identity of its one successor revision.</summary>
public sealed record ReparentRequest(BlockSelection ExpectedRoot, ImmutableArray<BlockSelection> SourceParentPath,
    ImmutableArray<BlockSelection> TargetParentPath, Guid BlockId, ImmutableHashSet<Guid> DetachConnectionIds,
    DiagramBlockPlacement? TargetPlacement, ImmutableDictionary<Guid, Guid> NewRevisionIds, RequirementRevisionOrigin? Origin);

/// <summary>What a move would do: the root connections of P attached to B that must be detached,
/// every effect on both levels (the removal cascade in P; in Q, stale layout entries of B that are
/// dropped and notes on B that become resolved again), and the blocks that receive one successor
/// revision each (root first, then the source branch, then the rest of the target branch).</summary>
public sealed record ReparentPreview(ImmutableHashSet<Guid> RequiredDetachConnectionIds, ImmutableArray<LevelEditEffect> Effects,
    ImmutableArray<Guid> SuccessorBlockIds);

public sealed partial class RecursiveBlockGraph
{
    /// <summary>Pure inspection of a move; NewRevisionIds and the detach set may be empty.</summary>
    public ReparentPreview PrepareReparent(ReparentRequest request)
    {
        var plan = PlanReparent(request, commit: false);
        return new(plan.Required, RecursiveLevelCascade.Ordered(plan.Cascade.Effects.Concat(plan.Arrival.Effects)),
            [.. plan.Union.Select(s => s.BlockId)]);
    }

    /// <summary>Immediately saves the move as one successor revision of every block on both paths,
    /// built deepest first; B keeps its identity, interior, interfaces, archive, bindings and
    /// allocation, P's detached connections stay in P's archive history, and every historic root still
    /// reproduces the old hierarchy. PrunedPresentationEntries counts B's dormant layout entries dropped
    /// from Q. Any failure leaves this graph unchanged.</summary>
    public RecursiveBlockSelectionResult Reparent(ReparentRequest request)
    {
        var plan = PlanReparent(request, commit: true);
        var moved = plan.Parent.Children.Single(c => c.BlockId == request.BlockId);
        var successors = new Dictionary<Guid, BlockSelection>();
        var revisions = Revisions.ToBuilder(); var states = States.ToBuilder();
        foreach (var node in plan.Union.OrderByDescending(Depth))
        {
            var revision = Inspect(node);
            var children = node.BlockId == plan.Parent.Selection.BlockId ? plan.Cascade.Children : revision.Children;
            children = [.. children.Select(c => successors.TryGetValue(c.BlockId, out var next) ? next : c)];
            var diagram = node.BlockId == plan.Parent.Selection.BlockId ? plan.Cascade.Diagram : revision.Diagram;
            if (node.BlockId == plan.Target.Selection.BlockId)
            {
                children = children.Add(moved);
                diagram = plan.Arrival.Diagram;
            }
            var selection = new BlockSelection(node.BlockId, node.StateId, request.NewRevisionIds[node.BlockId]);
            revisions.Add(revision with { Selection = selection, ParentRevisionId = revision.Selection.RevisionId, Children = children,
                Diagram = diagram, Origin = request.Origin!, RestoredFrom = null });
            var state = _states[node.StateId];
            states[states.IndexOf(state)] = state with { HeadRevisionId = selection.RevisionId };
            successors[node.BlockId] = selection;
        }
        var graph = new RecursiveBlockGraph(DocumentId, successors[SelectedRoot.BlockId], states, revisions, RequirementHistories,
            ConnectionArchives, ImplementationChanges, RefinementInputs, Proposals);
        // The two changed parents are the created revisions; every other successor is a containing snapshot.
        return new(graph, [.. plan.Union.Where(s => s.BlockId != plan.Parent.Selection.BlockId && s.BlockId != plan.Target.Selection.BlockId)
            .Select(s => successors[s.BlockId])], true, plan.Arrival.Pruned);

        int Depth(BlockSelection node)
        {
            int source = request.SourceParentPath.IndexOf(node);
            return source >= 0 ? source : request.TargetParentPath.IndexOf(node);
        }
    }

    private sealed record ReparentPlan(RecursiveBlockRevision Parent, RecursiveBlockRevision Target, ImmutableHashSet<Guid> Required,
        RecursiveLevelCascade.Result Cascade, ArrivalResult Arrival, ImmutableArray<BlockSelection> Union);

    /// <summary>Q's local diagram after B arrives, what changed there, and how many of B's dormant layout
    /// entries were dropped.</summary>
    private sealed record ArrivalResult(BlockLocalDiagram? Diagram, ImmutableArray<LevelEditEffect> Effects, int Pruned);

    private ReparentPlan PlanReparent(ReparentRequest request, bool commit)
    {
        if (request is null || request.ExpectedRoot is null || request.SourceParentPath.IsDefaultOrEmpty || request.TargetParentPath.IsDefaultOrEmpty
            || request.SourceParentPath.Any(s => s is null) || request.TargetParentPath.Any(s => s is null) || request.BlockId == Guid.Empty
            || commit && (request.DetachConnectionIds is null || request.NewRevisionIds is null || request.Origin is null))
            throw Reparent("invalid_reparent_request", "A move needs the expected root, both exact parent paths, the block, the detach set, fresh revision identities and an origin.");
        if (request.ExpectedRoot != SelectedRoot)
            throw new AutomationException("stale_root_revision", "The selected design changed; reload it before moving a block.");
        foreach (var path in new[] { request.SourceParentPath, request.TargetParentPath })
        {
            bool exact = path[0] == SelectedRoot;
            for (int i = 0; exact && i + 1 < path.Length; ++i)
                exact = _revisions.TryGetValue(path[i].RevisionId, out var parent) && parent.Selection == path[i] && parent.Children.Contains(path[i + 1]);
            if (!exact) throw Reparent("reparent_not_in_selected_design", "Both parent paths must run from the selected root through exact pinned children.");
        }
        Guid block = request.BlockId;
        var parentRevision = Inspect(request.SourceParentPath[^1]); var targetRevision = Inspect(request.TargetParentPath[^1]);
        if (block == SelectedRoot.BlockId) throw Reparent("reparent_root", "The root of the design cannot be moved under another block.");
        if (!parentRevision.Children.Any(c => c.BlockId == block))
            throw Reparent("reparent_block_not_child", "The block to move must be a direct child of the source parent.");
        if (request.TargetParentPath.Any(s => s.BlockId == block))
            throw Reparent("reparent_cycle", "A block cannot be moved into itself or one of its own descendants.");
        if (parentRevision.Selection.BlockId == targetRevision.Selection.BlockId)
            throw Reparent("reparent_same_parent", "The block already belongs to this parent.");
        var union = request.SourceParentPath.Concat(request.TargetParentPath.Where(s => !request.SourceParentPath.Contains(s))).ToImmutableArray();
        if (union.Any(s => _states[s.StateId].HeadRevisionId != s.RevisionId))
            throw new AutomationException("stale_parent_revision", "A block on the move's paths has a newer saved revision; compare it before moving.");
        var required = RecursiveLevelCascade.AttachedRoots(this, parentRevision, block);
        if (commit)
        {
            if (!required.IsSubsetOf(request.DetachConnectionIds))
                throw Reparent("reparent_connected_block", "Connections at the source level still attach to this block; confirm detaching them to move it.");
            if (!request.DetachConnectionIds.IsSubsetOf(required))
                throw Reparent("reparent_disposition_mismatch", "Detach exactly the source-level connections attached to the moved block.");
            var keys = union.Select(s => s.BlockId).ToHashSet();
            if (request.NewRevisionIds.Count != keys.Count || !request.NewRevisionIds.Keys.All(keys.Contains))
                throw Reparent("invalid_reparent_request", "Supply one new revision identity for every block on the source and target paths.");
            var used = RetainedIdentities();
            if (request.NewRevisionIds.Values.Any(id => id == Guid.Empty || !used.Add(id)))
                throw new AutomationException("identity_reused", "New revision identities must be fresh and distinct.");
            request.Origin!.Validate();
        }
        if (request.TargetPlacement is { } placement)
        {
            try
            {
                if (placement.BlockId != block) throw DiagramPresentationView.Invalid("The target placement must place the moved block.");
                new DiagramPresentationView([placement], [], []).Validate();
            }
            catch (AutomationException error) when (error.Code != "invalid_presentation_view")
            { throw DiagramPresentationView.Invalid("The target placement needs valid diagram-unit geometry: " + error.Message); }
        }
        var cascade = RecursiveLevelCascade.RemoveChild(this, parentRevision, block, required, request.Origin ?? parentRevision.Origin);
        var arrival = Arrive(targetRevision, block, request.TargetPlacement, request.Origin ?? targetRevision.Origin);
        return new(parentRevision, targetRevision, required, cascade, arrival, union);
    }

    /// <summary>B becomes a child of Q (contract rbg-v2 section 4.9: Q' adds TargetPlacement if given,
    /// otherwise B is unplaced in Q). Dormant layout entries Q still keeps for B (its placement and
    /// ports, for example copied verbatim from an older revision) would come back into use, so they are
    /// dropped and reported as PresentationEntryRemoved. A note whose exact target is B would otherwise
    /// claim a present target is unresolved, so it becomes resolved again and is reported as
    /// AnnotationResolved. Nothing else in Q changes.</summary>
    private static ArrivalResult Arrive(RecursiveBlockRevision target, Guid block, DiagramBlockPlacement? placement,
        RequirementRevisionOrigin origin)
    {
        Guid level = target.Selection.BlockId;
        var diagram = target.Diagram;
        if (diagram is null)
            return new(placement is null ? null : new BlockLocalDiagram([], [], [], new DiagramPresentationView([placement], [], []), default), [], 0);
        var effects = ImmutableArray.CreateBuilder<LevelEditEffect>();
        var view = diagram.Layout;
        var staleBlocks = view.BlockPlacements.Where(b => b.BlockId == block).ToArray();
        var stalePorts = view.PortPlacements.Where(p => p.BlockId == block).ToArray();
        foreach (var entry in staleBlocks)
            effects.Add(new(LevelEditEffectKind.PresentationEntryRemoved, entry.BlockId, level, DiagramPresentationView.Key(entry)));
        foreach (var entry in stalePorts)
            effects.Add(new(LevelEditEffectKind.PresentationEntryRemoved, entry.InterfaceId, level, DiagramPresentationView.Key(entry)));
        var blocks = view.BlockPlacements.Where(b => b.BlockId != block).ToList();
        if (placement is not null) blocks.Add(placement);
        view = view with { Blocks = [.. blocks], Ports = [.. view.PortPlacements.Where(p => p.BlockId != block)] };
        var notes = diagram.Notes.Select(note =>
        {
            if (note.Target.Kind != DiagramAnnotationTargetKind.Block || note.Target.TargetId != block || note.Target.UnresolvedReason is null)
                return note;
            effects.Add(new(LevelEditEffectKind.AnnotationResolved, note.Id, level, RecursiveLevelCascade.TargetReturned));
            return note with { Target = note.Target with { UnresolvedReason = null }, Origin = origin };
        }).ToImmutableArray();
        return new(diagram with { Presentation = view.IsEmpty ? null : view,
                Annotations = diagram.Annotations.IsDefault && notes.IsEmpty ? diagram.Annotations : notes },
            RecursiveLevelCascade.Ordered(effects), staleBlocks.Length + stalePorts.Length);
    }

    private static AutomationException Reparent(string code, string message) => new(code, message);
}
