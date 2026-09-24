using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Model;

/// <summary>RemoveConnectionMembers (a lane 2B addition, proto LECK_REMOVE_CONNECTION_MEMBERS = 200) removes signals of
/// one connection of the level (Round A3, owner decision nf53af9d74841b7d3). Its value keeps the contract's rule
/// C# = proto - 1 (rbg-v2 section 2.5) across the gap of the lane band.</summary>
public enum LevelEditCommandKind { RemoveChild, RemoveConnection, RemoveInterface, RemoveConnectionMembers = 199 }

/// <summary>A removal inside one level draft (contract rbg-v2 section 4.7). BlockId names the removed
/// child, or the owner of the removed interface (the level itself or one of its children). MemberIds names
/// the signals RemoveConnectionMembers removes from ConnectionId; every other removal leaves it empty.
/// The member list follows the record rules of rbg-v2 section 2.5: it has no default in the primary
/// constructor, the six-value constructor keeps existing callers, and an omitted list reads as empty.</summary>
[method: JsonConstructor]
public sealed record LevelEditCommand(LevelEditCommandKind Kind, Guid? BlockId, Guid? ConnectionId, Guid? InterfaceId,
    bool DetachConnections, RequirementRevisionOrigin Origin, ImmutableArray<Guid> MemberIds)
{
    public LevelEditCommand(LevelEditCommandKind Kind, Guid? BlockId, Guid? ConnectionId, Guid? InterfaceId, bool DetachConnections,
        RequirementRevisionOrigin Origin) : this(Kind, BlockId, ConnectionId, InterfaceId, DetachConnections, Origin, []) { }

    /// <summary>The signals this removal names; an omitted list is empty.</summary>
    [JsonIgnore] public ImmutableArray<Guid> MemberIdList => MemberIds.IsDefault ? [] : MemberIds;

    /// <summary>Record equality compares the member list by reference; this compares its contents in order.</summary>
    public bool SameContents(LevelEditCommand? other) => other is not null && Kind == other.Kind && BlockId == other.BlockId
        && ConnectionId == other.ConnectionId && InterfaceId == other.InterfaceId && DetachConnections == other.DetachConnections
        && (Origin is null ? other.Origin is null : other.Origin is not null && DiagramRequirementHistory.SameOrigin(Origin, other.Origin))
        && MemberIdList.SequenceEqual(other.MemberIdList);
}

/// <summary>The one implementation of the removal cascade for level drafts (contract rbg-v2 section 4.7).
/// It never writes: it returns the changed draft and every effect so the editor can show them and undo
/// by restoring its previous draft. Nothing is inferred or retargeted: notes on removed targets become
/// unresolved, realization records lose the removed targets, and layout entries of removed objects go.</summary>
public static class RecursiveLevelEdits
{
    public static (RecursiveLevelDraft Draft, ImmutableArray<LevelEditEffect> Effects) Apply(RecursiveBlockGraph graph,
        ImmutableArray<BlockSelection> path, RecursiveLevelDraft draft, LevelEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (draft?.Scope?.Baseline is null || draft.ChildDrafts.IsDefault || draft.ConnectionDrafts.IsDefault || draft.NewChildren.IsDefault
            || draft.NewConnections.IsDefault || command?.Origin is null || !Enum.IsDefined(command.Kind))
            throw RecursiveBlockGraph.Level("Provide the complete level draft and one supported removal with its origin.");
        command.Origin.Validate();
        if (path.IsDefaultOrEmpty || path[0] != graph.SelectedRoot || path[^1].BlockId != draft.Scope.Baseline.BlockId)
            throw RecursiveBlockGraph.Level("A removal names the exact selected path of the level it edits.");
        for (int i = 0; i + 1 < path.Length; ++i)
            if (!graph.Inspect(path[i]).Children.Contains(path[i + 1]))
                throw RecursiveBlockGraph.Level("The level path is not part of the selected design.");
        _ = graph.Inspect(draft.Scope.Baseline);
        if (command.Kind != LevelEditCommandKind.RemoveConnectionMembers && !command.MemberIdList.IsEmpty)
            throw RecursiveBlockGraph.Level("Only a removal of signals names signals.");
        var level = new Level(graph, draft);
        switch (command.Kind)
        {
            case LevelEditCommandKind.RemoveChild:
                if (command.BlockId is not { } block || command.ConnectionId is not null || command.InterfaceId is not null
                    || !draft.Scope.Children.Any(c => c.BlockId == block))
                    throw Missing("The block to remove is not on this diagram level.");
                level.RemoveChild(block, command.Origin);
                break;
            case LevelEditCommandKind.RemoveConnection:
                if (command.ConnectionId is not { } connection || command.BlockId is not null || command.InterfaceId is not null
                    || !draft.Scope.LocalDiagram.Connections.Any(c => c.ConnectionId == connection))
                    throw Missing("The connection to remove is not a connection of this diagram level.");
                level.RemoveConnections([connection], command.Origin);
                break;
            case LevelEditCommandKind.RemoveConnectionMembers:
                if (command.ConnectionId is not { } parent || command.BlockId is not null || command.InterfaceId is not null || command.DetachConnections
                    || command.MemberIdList.IsEmpty || command.MemberIdList.Distinct().Count() != command.MemberIdList.Length
                    || !draft.Scope.LocalDiagram.Connections.Any(c => c.ConnectionId == parent))
                    throw Missing("Name a connection of this diagram level and each of its signals to remove once.");
                level.RemoveMembers(parent, command.MemberIdList, command.Origin);
                break;
            default:
                if (command.BlockId is not { } owner || command.InterfaceId is not { } boundary || command.ConnectionId is not null)
                    throw Missing("Name the block and the interface to remove.");
                level.RemoveInterface(path, owner, boundary, command.DetachConnections, command.Origin);
                break;
        }
        return (level.Result(), RecursiveLevelCascade.Ordered(level.Effects));
    }

    private static AutomationException Missing(string message) => new("level_edit_target_missing", message);

    /// <summary>Mutable working copy of one level draft while a single removal cascades through it.</summary>
    private sealed class Level(RecursiveBlockGraph graph, RecursiveLevelDraft draft)
    {
        private readonly Guid _scope = draft.Scope.Baseline.BlockId;
        private readonly DiagramConnectionArchive? _archive = graph.ConnectionArchives.SingleOrDefault(a => a.OwnerBlockId == draft.Scope.Baseline.BlockId);
        private ImmutableArray<BlockSelection> _children = draft.Scope.Children;
        private BlockLocalDiagram _local = draft.Scope.LocalDiagram;
        private readonly bool _hadDiagram = draft.Scope.Diagram is not null;
        private readonly List<RecursiveBlockDraft> _childDrafts = [.. draft.ChildDrafts];
        private readonly List<DiagramConnectionDraft> _connectionDrafts = [.. draft.ConnectionDrafts];
        private readonly List<NewBlockOccurrence> _newChildren = [.. draft.NewChildren];
        private readonly List<NewConnectionOccurrence> _newConnections = [.. draft.NewConnections];
        public List<LevelEditEffect> Effects { get; } = [];

        public RecursiveLevelDraft Result()
        {
            var presentation = _local.Presentation is { IsEmpty: true } ? null : _local.Presentation;
            BlockLocalDiagram? diagram = _local with { Presentation = presentation };
            if (!_hadDiagram && diagram.Interfaces.IsEmpty && diagram.Connections.IsEmpty && diagram.Notes.IsEmpty && presentation is null
                && diagram.Realizations.IsEmpty) diagram = null;
            return new(draft.Scope with { Children = _children, Diagram = diagram }, [.. _childDrafts], [.. _connectionDrafts],
                [.. _newChildren], [.. _newConnections]);
        }

        /// <summary>The signals drawn for this root in this draft (Round A3).</summary>
        private IEnumerable<NewConnectionOccurrence> Drawn(ConnectionSelection root) => _newConnections.Where(c => c.MemberOf == root.ConnectionId);

        /// <summary>The tree of a root connection as this draft sees it, with each connection's current endpoints: the root (as drawn
        /// or edited in this draft), the saved signals the draft keeps with their own saved members, and the signals drawn for it.
        /// A saved signal the draft dropped is no longer part of it. Both the endpoint check of a removal and the set of removed
        /// connections read this one list, so they always agree.</summary>
        private List<(Guid Connection, ImmutableArray<DiagramEndpointBinding> Endpoints)> Members(ConnectionSelection root)
        {
            var tree = new List<(Guid, ImmutableArray<DiagramEndpointBinding>)>();
            if (_newConnections.FirstOrDefault(c => c.Selection == root) is { } added) tree.Add((root.ConnectionId, added.Endpoints));
            else if (_archive is not null)
            {
                var edited = _connectionDrafts.FirstOrDefault(c => c.Baseline == root);
                tree.Add((root.ConnectionId, edited?.Endpoints ?? _archive.Inspect(root).Endpoints));
                var kept = (edited?.Members ?? _archive.Inspect(root).Members).Where(m => _archive.Revisions.Any(r => r.Selection == m));
                tree.AddRange(_archive.Walk([.. kept]).Select(member => (member.ConnectionId, _archive.Inspect(member).Endpoints)));
            }
            else tree.Add((root.ConnectionId, []));
            tree.AddRange(Drawn(root).Select(c => (c.Selection.ConnectionId, c.Endpoints)));
            return tree;
        }

        /// <summary>The current endpoints of a root connection and all of its members as this draft sees them.</summary>
        private IEnumerable<(Guid Connection, DiagramEndpointBinding Endpoint)> Endpoints(ConnectionSelection root) =>
            Members(root).SelectMany(m => m.Endpoints.Select(e => (m.Connection, e)));

        /// <summary>Every connection identity in the tree of this root (the root, the saved signals it keeps and the signals drawn for it).</summary>
        private IEnumerable<Guid> Tree(ConnectionSelection root) => Members(root).Select(m => m.Connection).Distinct().ToList();

        private string ConnectionName(ConnectionSelection root) =>
            _newConnections.FirstOrDefault(c => c.Selection == root)?.Name ?? _connectionDrafts.FirstOrDefault(c => c.Baseline == root)?.Name
                ?? _archive?.Inspect(root).Name ?? root.ConnectionId.ToString("D");

        private string BlockName(Guid block) =>
            _newChildren.FirstOrDefault(c => c.Selection.BlockId == block)?.Name ?? _childDrafts.FirstOrDefault(c => c.Baseline.BlockId == block)?.Name
                ?? graph.Inspect(_children.Single(c => c.BlockId == block)).Name;

        public void RemoveChild(Guid block, RequirementRevisionOrigin origin)
        {
            Effects.Add(new(LevelEditEffectKind.ChildRemoved, block, _scope, BlockName(block)));
            var attached = _local.Connections.Where(root => Endpoints(root).Any(e => e.Endpoint.BlockId == block)).Select(r => r.ConnectionId).ToList();
            _children = [.. _children.Where(c => c.BlockId != block)];
            _childDrafts.RemoveAll(c => c.Baseline.BlockId == block);
            _newChildren.RemoveAll(c => c.Selection.BlockId == block);
            Cascade(block, attached, origin, t => t.Kind == InterfaceRealizationTargetKind.ChildInterface && t.BlockId == block);
            var view = _local.Layout;
            foreach (var placement in view.BlockPlacements.Where(b => b.BlockId == block))
                Effects.Add(new(LevelEditEffectKind.PresentationEntryRemoved, placement.BlockId, _scope, DiagramPresentationView.Key(placement)));
            foreach (var port in view.PortPlacements.Where(p => p.BlockId == block))
                Effects.Add(new(LevelEditEffectKind.PresentationEntryRemoved, port.InterfaceId, _scope, DiagramPresentationView.Key(port)));
            if (_local.Presentation is not null)
                _local = _local with { Presentation = view with { Blocks = [.. view.BlockPlacements.Where(b => b.BlockId != block)],
                    Ports = [.. view.PortPlacements.Where(p => p.BlockId != block)] } };
        }

        public void RemoveConnections(IReadOnlyCollection<Guid> roots, RequirementRevisionOrigin origin) =>
            Cascade(null, roots, origin, _ => false);

        /// <summary>Removes signals of one root connection (Round A3): a signal drawn in this draft simply goes; a saved signal
        /// leaves the connection's member list, and notes, realization targets and routes on it (and on its own members) are
        /// cascaded as for a removed connection. The connection itself, its other signals and its other details stay.</summary>
        public void RemoveMembers(Guid parent, ImmutableArray<Guid> ids, RequirementRevisionOrigin origin)
        {
            var root = _local.Connections.Single(c => c.ConnectionId == parent);
            var drawn = Drawn(root).ToList();
            bool isNew = _newConnections.Any(c => c.Selection == root);
            int index = _connectionDrafts.FindIndex(c => c.Baseline == root);
            ImmutableArray<ConnectionSelection> members = isNew ? [.. drawn.Select(c => c.Selection)]
                : index >= 0 ? _connectionDrafts[index].Members : _archive?.Inspect(root).Members ?? [];
            var removedConnections = new HashSet<Guid>();
            foreach (var id in ids)
            {
                var member = members.FirstOrDefault(m => m.ConnectionId == id) ?? throw Missing("The signal to remove is not a signal of this connection.");
                var added = drawn.FirstOrDefault(c => c.Selection == member);
                Effects.Add(new(LevelEditEffectKind.ConnectionRemoved, id, _scope, added?.Name ?? _archive!.Inspect(member).Name));
                if (added is not null) { _newConnections.Remove(added); removedConnections.Add(id); }
                else removedConnections.UnionWith(_archive!.Walk([member]).Select(c => c.ConnectionId));
            }
            if (!isNew)
            {
                var link = index >= 0 ? _connectionDrafts[index] : _archive!.StartDraft(root);
                link = link with { Members = [.. link.Members.Where(m => !ids.Contains(m.ConnectionId))] };
                if (index >= 0) _connectionDrafts[index] = link; else _connectionDrafts.Add(link);
            }
            Unresolve(null, removedConnections, origin, _ => false);
        }

        /// <summary>Steps 2-5 of the cascade for the removed block (if any) and root connections.</summary>
        private void Cascade(Guid? block, IReadOnlyCollection<Guid> roots, RequirementRevisionOrigin origin, Func<InterfaceRealizationTarget, bool> blockTarget)
        {
            var removedConnections = new HashSet<Guid>();
            foreach (var root in _local.Connections.Where(c => roots.Contains(c.ConnectionId)).ToList())
            {
                Effects.Add(new(LevelEditEffectKind.ConnectionRemoved, root.ConnectionId, _scope, ConnectionName(root)));
                removedConnections.UnionWith(Tree(root));
                _connectionDrafts.RemoveAll(c => c.Baseline == root);
                _newConnections.RemoveAll(c => c.Selection == root || c.MemberOf == root.ConnectionId);
            }
            _local = _local with { Connections = [.. _local.Connections.Where(c => !roots.Contains(c.ConnectionId))] };
            Unresolve(block, removedConnections, origin, blockTarget);
        }

        /// <summary>Steps 3-5: notes on removed targets become unresolved, realization records lose removed targets, and
        /// routes of removed connections go.</summary>
        private void Unresolve(Guid? block, HashSet<Guid> removedConnections, RequirementRevisionOrigin origin, Func<InterfaceRealizationTarget, bool> blockTarget)
        {
            var notes = _local.Notes.Select(note =>
            {
                bool removed = note.Target.UnresolvedReason is null && note.Target.TargetId is { } target
                    && (note.Target.Kind == DiagramAnnotationTargetKind.Block && target == block
                        || note.Target.Kind == DiagramAnnotationTargetKind.Connection && removedConnections.Contains(target));
                if (!removed) return note;
                Effects.Add(new(LevelEditEffectKind.AnnotationUnresolved, note.Id, _scope, RecursiveLevelCascade.TargetRemoved));
                return note with { Target = note.Target with { UnresolvedReason = RecursiveLevelCascade.TargetRemoved }, Origin = origin };
            }).ToImmutableArray();
            bool Removed(InterfaceRealizationTarget t) => blockTarget(t)
                || t.Kind == InterfaceRealizationTargetKind.LocalConnection && removedConnections.Contains(t.ConnectionId!.Value);
            var realizations = RemoveTargets(Removed);
            var view = _local.Layout;
            foreach (var route in view.ConnectionRoutes.Where(r => removedConnections.Contains(r.ConnectionId)))
                Effects.Add(new(LevelEditEffectKind.PresentationEntryRemoved, route.ConnectionId, _scope, DiagramPresentationView.Key(route)));
            _local = _local with
            {
                Annotations = _local.Annotations.IsDefault ? _local.Annotations : notes,
                InterfaceRealizations = realizations,
                Presentation = _local.Presentation is null ? null : view with { Routes = [.. view.ConnectionRoutes.Where(r => !removedConnections.Contains(r.ConnectionId))] }
            };
        }

        /// <summary>Step 4: removes realization targets and downgrades the records that lose them.</summary>
        private ImmutableArray<InterfaceRealization> RemoveTargets(Func<InterfaceRealizationTarget, bool> removed)
        {
            if (_local.InterfaceRealizations.IsDefault) return _local.InterfaceRealizations;
            return [.. _local.InterfaceRealizations.Select(record =>
            {
                var kept = record.TargetList.Where(t => !removed(t)).ToImmutableArray();
                if (kept.Length == record.TargetList.Length) return record;
                foreach (var target in record.TargetList.Where(removed))
                    Effects.Add(new(LevelEditEffectKind.RealizationTargetRemoved, record.InterfaceId, _scope, Describe(target)));
                var next = kept.IsEmpty
                    ? record with { State = DiagramRealizationState.Unknown, Targets = [], UnresolvedReason = RecursiveLevelCascade.RealizingElementRemoved }
                    : record.State == DiagramRealizationState.Resolved
                        ? record with { State = DiagramRealizationState.Partial, Targets = kept, UnresolvedReason = RecursiveLevelCascade.RealizingElementRemoved }
                        : record with { Targets = kept };
                if (next.State != record.State)
                    Effects.Add(new(LevelEditEffectKind.RealizationStateChanged, record.InterfaceId, _scope, record.State + " -> " + next.State));
                return next;
            })];
        }

        public void RemoveInterface(ImmutableArray<BlockSelection> path, Guid owner, Guid boundary, bool detach, RequirementRevisionOrigin origin)
        {
            var uses = new List<AutomationErrorDetail>();
            string name;
            if (owner == _scope)
            {
                name = _local.Interfaces.FirstOrDefault(i => i.Id == boundary)?.Name
                    ?? throw Missing("The interface to remove is not on this level's boundary.");
                // The parent level still uses this boundary: the user detaches it there first.
                if (path.Length >= 2)
                {
                    var parent = graph.Inspect(path[^2]);
                    if (!parent.LocalDiagram.Connections.IsEmpty)
                    {
                        var archive = graph.Connections(parent.Selection.BlockId);
                        foreach (var link in archive.Walk(parent.LocalDiagram.Connections).Select(archive.Inspect))
                            if (link.Endpoints.Any(e => e.BlockId == owner && e.InterfaceId == boundary))
                                uses.Add(new("connection", parent.Selection.BlockId, link.Selection.ConnectionId,
                                    $"Connection '{link.Name}' of the containing level uses interface '{name}'."));
                    }
                    foreach (var record in parent.LocalDiagram.Realizations)
                        if (record.TargetList.Any(t => t.Kind == InterfaceRealizationTargetKind.ChildInterface && t.BlockId == owner && t.InterfaceId == boundary))
                            uses.Add(new("interface_realization", parent.Selection.BlockId, record.InterfaceId,
                                $"The containing level's realization of interface {record.InterfaceId:D} uses interface '{name}'."));
                    if (uses.Count != 0)
                        throw new AutomationException("boundary_interface_in_use",
                            "The containing level still uses this interface; detach it there first. Nothing was changed.", uses);
                }
            }
            else
            {
                if (!_children.Any(c => c.BlockId == owner)) throw Missing("The interface owner is not on this diagram level.");
                var added = _newChildren.FirstOrDefault(c => c.Selection.BlockId == owner);
                var pinned = _children.Single(c => c.BlockId == owner);
                var edited = _childDrafts.FirstOrDefault(c => c.Baseline.BlockId == owner);
                var interfaces = added?.Interfaces ?? edited?.LocalDiagram.Interfaces ?? graph.Inspect(pinned).LocalDiagram.Interfaces;
                name = interfaces.FirstOrDefault(i => i.Id == boundary)?.Name ?? throw Missing("The interface to remove is not on that block's boundary.");
                if (added is null)
                {
                    // The child's own diagram still uses its boundary: detach inside the child first.
                    var inner = graph.Inspect(pinned);
                    if (!inner.LocalDiagram.Connections.IsEmpty)
                    {
                        var archive = graph.Connections(owner);
                        foreach (var link in archive.Walk(inner.LocalDiagram.Connections).Select(archive.Inspect))
                            if (link.Endpoints.Any(e => e.BlockId == owner && e.InterfaceId == boundary))
                                uses.Add(new("connection", owner, link.Selection.ConnectionId,
                                    $"Connection '{link.Name}' inside '{inner.Name}' uses interface '{name}'."));
                    }
                    if (uses.Count != 0)
                        throw new AutomationException("boundary_interface_in_use",
                            "This block's own diagram still uses the interface; detach it inside the block first. Nothing was changed.", uses);
                }
            }
            var attached = _local.Connections.Where(root => Endpoints(root).Any(e => e.Endpoint.BlockId == owner && e.Endpoint.InterfaceId == boundary)).ToList();
            if (attached.Count != 0 && !detach)
                throw new AutomationException("boundary_interface_in_use", "Connections on this level use the interface; remove them together with it or keep the interface. Nothing was changed.",
                    [.. attached.Select(root => new AutomationErrorDetail("connection", _scope, root.ConnectionId,
                        $"Connection '{ConnectionName(root)}' uses interface '{name}'."))]);
            Cascade(null, [.. attached.Select(r => r.ConnectionId)], origin,
                t => owner != _scope && t.Kind == InterfaceRealizationTargetKind.ChildInterface && t.BlockId == owner && t.InterfaceId == boundary);
            var view = _local.Layout;
            foreach (var port in view.PortPlacements.Where(p => p.BlockId == owner && p.InterfaceId == boundary))
                Effects.Add(new(LevelEditEffectKind.PresentationEntryRemoved, port.InterfaceId, _scope, DiagramPresentationView.Key(port)));
            if (_local.Presentation is not null)
                _local = _local with { Presentation = view with { Ports = [.. view.PortPlacements.Where(p => !(p.BlockId == owner && p.InterfaceId == boundary))] } };
            Effects.Add(new(LevelEditEffectKind.InterfaceRemoved, boundary, _scope, name));
            if (owner == _scope)
            {
                if (_local.Realizations.Any(r => r.InterfaceId == boundary))
                    Effects.Add(new(LevelEditEffectKind.RealizationRemoved, boundary, _scope, name));
                var records = _local.InterfaceRealizations.IsDefault ? _local.InterfaceRealizations
                    : [.. _local.InterfaceRealizations.Where(r => r.InterfaceId != boundary)];
                _local = _local with { Interfaces = [.. _local.Interfaces.Where(i => i.Id != boundary)], InterfaceRealizations = records };
                return;
            }
            int index = _newChildren.FindIndex(c => c.Selection.BlockId == owner);
            if (index >= 0)
            {
                _newChildren[index] = _newChildren[index] with { Interfaces = [.. _newChildren[index].Interfaces.Where(i => i.Id != boundary)] };
                return;
            }
            int edit = _childDrafts.FindIndex(c => c.Baseline.BlockId == owner);
            var child = edit >= 0 ? _childDrafts[edit] : graph.StartDraft(_children.Single(c => c.BlockId == owner));
            var local = child.LocalDiagram;
            if (local.Realizations.Any(r => r.InterfaceId == boundary))
                Effects.Add(new(LevelEditEffectKind.RealizationRemoved, boundary, owner, name));
            child = child with { Diagram = local with { Interfaces = [.. local.Interfaces.Where(i => i.Id != boundary)],
                InterfaceRealizations = local.InterfaceRealizations.IsDefault ? local.InterfaceRealizations
                    : [.. local.InterfaceRealizations.Where(r => r.InterfaceId != boundary)] } };
            if (edit >= 0) _childDrafts[edit] = child; else _childDrafts.Add(child);
        }

        private static string Describe(InterfaceRealizationTarget target) => target.Kind switch
        {
            InterfaceRealizationTargetKind.ChildInterface => $"ChildInterface {target.BlockId:D}/{target.InterfaceId:D}",
            InterfaceRealizationTargetKind.LocalConnection => $"LocalConnection {target.ConnectionId:D}",
            _ => "Pin " + InterfaceRealizationTarget.PinKey(target.Pin)
        };
    }
}
