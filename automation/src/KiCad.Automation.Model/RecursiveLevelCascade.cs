using System.Collections.Immutable;

namespace KiCad.Automation.Model;

public enum LevelEditEffectKind { ChildRemoved, ConnectionRemoved, InterfaceRemoved, AnnotationUnresolved, RealizationTargetRemoved,
    RealizationStateChanged, RealizationRemoved, PresentationEntryRemoved }

/// <summary>One consequence of removing something from a diagram level, reported to the user
/// before it is saved (contract rbg-v2 section 4.7).</summary>
public sealed record LevelEditEffect(LevelEditEffectKind Kind, Guid ObjectId, Guid ScopeBlockId, string Detail);

/// <summary>The single implementation of the removal cascade (contract rbg-v2 section 4.7, steps
/// 1-5): removing a child also removes the root connections attached to it, marks notes on removed
/// targets unresolved, removes realization targets on them (downgrading the record's state) and
/// removes their layout entries. Nothing is inferred or retargeted.</summary>
internal static class RecursiveLevelCascade
{
    internal const string TargetRemoved = "Target removed from this diagram level.";
    internal const string RealizingElementRemoved = "Realizing element was removed.";

    internal sealed record Result(ImmutableArray<BlockSelection> Children, BlockLocalDiagram? Diagram, ImmutableArray<LevelEditEffect> Effects);

    /// <summary>Root connections of the level whose connection tree has an endpoint on the block.</summary>
    internal static ImmutableHashSet<Guid> AttachedRoots(RecursiveBlockGraph graph, RecursiveBlockRevision scope, Guid blockId)
    {
        if (scope.LocalDiagram.Connections.IsEmpty) return [];
        var archive = graph.Connections(scope.Selection.BlockId);
        return [.. scope.LocalDiagram.Connections.Where(root => archive.Walk([root])
            .Any(c => archive.Inspect(c).Endpoints.Any(e => e.BlockId == blockId))).Select(root => root.ConnectionId)];
    }

    internal static Result RemoveChild(RecursiveBlockGraph graph, RecursiveBlockRevision scope, Guid blockId,
        ImmutableHashSet<Guid> removedRoots, RequirementRevisionOrigin origin)
    {
        Guid level = scope.Selection.BlockId;
        var effects = ImmutableArray.CreateBuilder<LevelEditEffect>();
        var child = scope.Children.Single(c => c.BlockId == blockId);
        effects.Add(new(LevelEditEffectKind.ChildRemoved, blockId, level, graph.Inspect(child).Name));
        var local = scope.LocalDiagram;
        var removedConnections = new HashSet<Guid>();
        if (!local.Connections.IsEmpty)
        {
            var archive = graph.Connections(level);
            foreach (var root in local.Connections.Where(r => removedRoots.Contains(r.ConnectionId)))
            {
                effects.Add(new(LevelEditEffectKind.ConnectionRemoved, root.ConnectionId, level, archive.Inspect(root).Name));
                removedConnections.UnionWith(archive.Walk([root]).Select(c => c.ConnectionId));
            }
        }
        var notes = local.Notes.Select(note =>
        {
            bool removed = note.Target.UnresolvedReason is null && note.Target.TargetId is { } target
                && (note.Target.Kind == DiagramAnnotationTargetKind.Block && target == blockId
                    || note.Target.Kind == DiagramAnnotationTargetKind.Connection && removedConnections.Contains(target));
            if (!removed) return note;
            effects.Add(new(LevelEditEffectKind.AnnotationUnresolved, note.Id, level, TargetRemoved));
            return note with { Target = note.Target with { UnresolvedReason = TargetRemoved }, Origin = origin };
        }).ToImmutableArray();
        var realizations = local.Realizations.Select(record =>
        {
            bool Removed(InterfaceRealizationTarget t) => t.Kind == InterfaceRealizationTargetKind.ChildInterface && t.BlockId == blockId
                || t.Kind == InterfaceRealizationTargetKind.LocalConnection && removedConnections.Contains(t.ConnectionId!.Value);
            var kept = record.TargetList.Where(t => !Removed(t)).ToImmutableArray();
            if (kept.Length == record.TargetList.Length) return record;
            foreach (var target in record.TargetList.Where(Removed))
                effects.Add(new(LevelEditEffectKind.RealizationTargetRemoved, record.InterfaceId, level, Describe(target)));
            var next = kept.IsEmpty
                ? record with { State = DiagramRealizationState.Unknown, Targets = [], UnresolvedReason = RealizingElementRemoved }
                : record.State == DiagramRealizationState.Resolved
                    ? record with { State = DiagramRealizationState.Partial, Targets = kept, UnresolvedReason = RealizingElementRemoved }
                    : record with { Targets = kept };
            if (next.State != record.State)
                effects.Add(new(LevelEditEffectKind.RealizationStateChanged, record.InterfaceId, level, record.State + " -> " + next.State));
            return next;
        }).ToImmutableArray();
        DiagramPresentationView? presentation = local.Presentation;
        if (presentation is not null)
        {
            var view = presentation;
            foreach (var block in view.BlockPlacements.Where(b => b.BlockId == blockId))
                effects.Add(new(LevelEditEffectKind.PresentationEntryRemoved, block.BlockId, level, DiagramPresentationView.Key(block)));
            foreach (var port in view.PortPlacements.Where(p => p.BlockId == blockId))
                effects.Add(new(LevelEditEffectKind.PresentationEntryRemoved, port.InterfaceId, level, DiagramPresentationView.Key(port)));
            foreach (var route in view.ConnectionRoutes.Where(r => removedConnections.Contains(r.ConnectionId)))
                effects.Add(new(LevelEditEffectKind.PresentationEntryRemoved, route.ConnectionId, level, DiagramPresentationView.Key(route)));
            presentation = view with
            {
                Blocks = [.. view.BlockPlacements.Where(b => b.BlockId != blockId)],
                Ports = [.. view.PortPlacements.Where(p => p.BlockId != blockId)],
                Routes = [.. view.ConnectionRoutes.Where(r => !removedConnections.Contains(r.ConnectionId))]
            };
            if (presentation.IsEmpty) presentation = null;
        }
        BlockLocalDiagram? diagram = scope.Diagram is null ? null : local with
        {
            Connections = [.. local.Connections.Where(c => !removedRoots.Contains(c.ConnectionId))],
            Annotations = notes, Presentation = presentation,
            InterfaceRealizations = realizations.IsEmpty ? local.InterfaceRealizations : realizations
        };
        return new([.. scope.Children.Where(c => c.BlockId != blockId)], diagram, Ordered(effects));
    }

    /// <summary>Effects in (kind, object identity, detail) order.</summary>
    internal static ImmutableArray<LevelEditEffect> Ordered(IEnumerable<LevelEditEffect> effects) => [.. effects.OrderBy(e => e.Kind)
        .ThenBy(e => e.ObjectId.ToString("D"), StringComparer.Ordinal).ThenBy(e => e.Detail, StringComparer.Ordinal)];

    private static string Describe(InterfaceRealizationTarget target) => target.Kind switch
    {
        InterfaceRealizationTargetKind.ChildInterface => $"ChildInterface {target.BlockId:D}/{target.InterfaceId:D}",
        InterfaceRealizationTargetKind.LocalConnection => $"LocalConnection {target.ConnectionId:D}",
        _ => "Pin " + InterfaceRealizationTarget.PinKey(target.Pin)
    };
}
