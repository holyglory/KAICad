using System.Collections.Immutable;

namespace KiCad.Automation.Model;

/// <summary>LayoutOnly: the revision differs from its parent revision only in its per-level layout.</summary>
public sealed record DiagramHistoryEntry(BlockSelection Selection, int Version, string Name,
    RequirementRevisionOrigin Origin, int ChildCount, int ConnectionCount, int AnnotationCount, bool IsContext, bool LayoutOnly = false);

public sealed record DiagramHistoryPage(Guid DocumentId, BlockSelection Context, int ContextVersion,
    int Offset, int Total, ImmutableArray<DiagramHistoryEntry> Entries)
{
    public bool HasMore => Offset + Entries.Length < Total;
}

public enum DiagramHistoryChangeKind { Added, Removed, Changed, Reordered }
public enum DiagramHistoryChangeCategory { Name, Requirement, Block, Connection, Interface, Comment, Definition, PhysicalAllocation,
    Layout, InterfaceRealization, InterconnectRealization }
public sealed record DiagramHistoryChange(DiagramHistoryChangeCategory Category, DiagramHistoryChangeKind Kind,
    Guid ObjectId, string Name, DiagramRequirementField? Field = null);
public sealed record DiagramHistoryComparison(Guid DocumentId, BlockSelection Context, BlockSelection Inspected,
    int ContextVersion, int InspectedVersion, RequirementRevisionOrigin InspectedOrigin,
    ImmutableArray<DiagramHistoryChange> Changes);

/// <summary>Read-only whole-diagram history pinned to an exact persisted context.
/// A later implementation head is not part of this context and cannot silently
/// enter an older page. Preview and restore remain separate caller actions.</summary>
public static class DiagramHistoryQuery
{
    public static DiagramHistoryPage Read(RecursiveBlockGraph graph, BlockSelection context,
        int offset = 0, int limit = 50)
    {
        _ = graph.Inspect(context);
        if (offset < 0 || limit is < 1 or > 200)
            throw new AutomationException("invalid_diagram_history_page", "Choose a history page of 1 to 200 entries and a non-negative offset.");
        var history = graph.History(context.StateId).SkipWhile(r => r.Selection != context).ToImmutableArray();
        // Graph construction proves the complete linear implementation history;
        // still require the exact requested owner/context before returning a page.
        if (history.IsEmpty)
            throw new AutomationException("missing_diagram_history_context", "The requested diagram revision is not part of this implementation history.");
        var rows = history.Skip(offset).Take(limit).Select((r, i) => new DiagramHistoryEntry(r.Selection,
            history.Length - offset - i, r.Name, r.Origin, r.Children.Length, r.LocalDiagram.Connections.Length,
            r.LocalDiagram.Notes.Length, r.Selection == context, LayoutOnly(graph, r))).ToImmutableArray();
        return new(graph.DocumentId, context, history.Length, offset, history.Length, rows);
    }

    public static RecursiveBlockRevision Inspect(RecursiveBlockGraph graph, BlockSelection context, BlockSelection inspected)
    {
        _ = graph.Inspect(context); var revision = graph.Inspect(inspected);
        if (context.BlockId != inspected.BlockId || context.StateId != inspected.StateId
            || !graph.History(context.StateId).SkipWhile(r => r.Selection != context).Any(r => r.Selection == inspected))
            throw new AutomationException("wrong_diagram_history_scope", "Inspect a revision belonging to this exact diagram implementation and saved context.");
        return revision;
    }

    /// <summary>Content changes from the inspected earlier diagram to the saved
    /// context. Stable identities, not similar names or positions, match objects.</summary>
    public static DiagramHistoryComparison Compare(RecursiveBlockGraph graph, BlockSelection context, BlockSelection inspected)
    {
        var before = Inspect(graph, context, inspected);
        var changes = Changes(graph, inspected, context);
        int version = Read(graph, context, 0, 1).ContextVersion;
        int inspectedVersion = Read(graph, inspected, 0, 1).ContextVersion;
        return new(graph.DocumentId, context, inspected, version, inspectedVersion, before.Origin, changes);
    }

    /// <summary>Content changes from one saved revision of a block to another saved revision of the same block, in the
    /// same or another of its implementations (a proposal's candidate, or today's head against an older revision). The
    /// changes are those of this one level: its name, fields, definition, children, connections, ports, comments,
    /// realizations and layout. Stable identities, not similar names or positions, match objects.</summary>
    public static ImmutableArray<DiagramHistoryChange> Changes(RecursiveBlockGraph graph, BlockSelection inspected, BlockSelection context)
    {
        var before = graph.Inspect(inspected); var after = graph.Inspect(context);
        if (inspected.BlockId != context.BlockId)
            throw new AutomationException("wrong_diagram_history_scope", "Compare two revisions of the same block.");
        var changes = ImmutableArray.CreateBuilder<DiagramHistoryChange>();
        if (before.Name != after.Name)
            changes.Add(new(DiagramHistoryChangeCategory.Name, DiagramHistoryChangeKind.Changed, context.BlockId, after.Name));
        var oldFields = graph.Requirements(inspected).Requirements; var newFields = graph.Requirements(context).Requirements;
        foreach (var field in Enum.GetValues<DiagramRequirementField>())
            if (oldFields.Get(field) != newFields.Get(field))
                changes.Add(new(DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, context.BlockId, "", field));
        foreach (var facet in Enum.GetValues<BlockDefinitionFacet>())
            if (!before.EffectiveDefinition.Get(facet).SameContents(after.EffectiveDefinition.Get(facet)))
                changes.Add(new(DiagramHistoryChangeCategory.Definition, DiagramHistoryChangeKind.Changed, context.BlockId,
                    facet == BlockDefinitionFacet.OrderablePart ? "Orderable part" : facet.ToString()));
        if (!(before.EffectiveDefinition.KnowledgeClass ?? DefinitionChoice<KnowledgeClassReference>.Unspecified)
            .SameContents(after.EffectiveDefinition.KnowledgeClass ?? DefinitionChoice<KnowledgeClassReference>.Unspecified))
            changes.Add(new(DiagramHistoryChangeCategory.Definition, DiagramHistoryChangeKind.Changed, context.BlockId, "Knowledge class"));
        if (!before.EffectiveComponentBindings.SameContents(after.EffectiveComponentBindings))
            changes.Add(new(DiagramHistoryChangeCategory.Definition, DiagramHistoryChangeKind.Changed, context.BlockId, "Components"));
        if (!SamePhysical(before.PhysicalAllocation, after.PhysicalAllocation))
            changes.Add(new(DiagramHistoryChangeCategory.PhysicalAllocation, DiagramHistoryChangeKind.Changed, context.BlockId, "Physical allocation"));
        CompareItems(before.Children, after.Children, DiagramHistoryChangeCategory.Block,
            c => c.BlockId, c => graph.Inspect(c).Name, (a, b) => a == b);
        // A pinned connection whose new revision changed only its interconnect realization is
        // reported as a realization change, not as a changed connection.
        var connectionChanges = ImmutableArray.CreateBuilder<DiagramHistoryChange>();
        CompareItems(before.LocalDiagram.Connections, after.LocalDiagram.Connections, DiagramHistoryChangeCategory.Connection,
            c => c.ConnectionId, c => graph.Connections(context.BlockId).Inspect(c).Name, (a, b) => a == b, connectionChanges);
        foreach (var change in connectionChanges)
        {
            if (change.Kind == DiagramHistoryChangeKind.Changed)
            {
                var archive = graph.Connections(context.BlockId);
                var oldRevision = archive.Inspect(before.LocalDiagram.Connections.Single(c => c.ConnectionId == change.ObjectId));
                var newRevision = archive.Inspect(after.LocalDiagram.Connections.Single(c => c.ConnectionId == change.ObjectId));
                if (!InterconnectRealization.Same(oldRevision.Realization, newRevision.Realization)
                    && DiagramConnectionArchive.SameDefinition(oldRevision with { Realization = null }, newRevision with { Realization = null })
                    && archive.Requirements(oldRevision.Selection).Requirements == archive.Requirements(newRevision.Selection).Requirements)
                {
                    changes.Add(change with { Category = DiagramHistoryChangeCategory.InterconnectRealization });
                    continue;
                }
            }
            changes.Add(change);
        }
        CompareItems(before.LocalDiagram.Interfaces, after.LocalDiagram.Interfaces, DiagramHistoryChangeCategory.Interface,
            i => i.Id, i => i.Name, (a, b) => a == b);
        CompareItems(before.LocalDiagram.Notes, after.LocalDiagram.Notes, DiagramHistoryChangeCategory.Comment,
            n => n.Id, n => n.Text, (a, b) => a.SameContents(b));
        var oldRealizations = InterfaceRealization.CanonicalList(before.LocalDiagram.Realizations).ToDictionary(r => r.InterfaceId);
        var newRealizations = InterfaceRealization.CanonicalList(after.LocalDiagram.Realizations).ToDictionary(r => r.InterfaceId);
        string InterfaceName(Guid id) => after.LocalDiagram.Interfaces.Concat(before.LocalDiagram.Interfaces).FirstOrDefault(i => i.Id == id)?.Name ?? "";
        foreach (var (id, record) in newRealizations)
            if (!oldRealizations.TryGetValue(id, out var old))
                changes.Add(new(DiagramHistoryChangeCategory.InterfaceRealization, DiagramHistoryChangeKind.Added, id, InterfaceName(id)));
            else if (!old.SameContents(record))
                changes.Add(new(DiagramHistoryChangeCategory.InterfaceRealization, DiagramHistoryChangeKind.Changed, id, InterfaceName(id)));
        foreach (var id in oldRealizations.Keys.Where(id => !newRealizations.ContainsKey(id)))
            changes.Add(new(DiagramHistoryChangeCategory.InterfaceRealization, DiagramHistoryChangeKind.Removed, id, InterfaceName(id)));
        CompareLayout(graph.ActivePresentation(inspected), graph.ActivePresentation(context));
        return changes.ToImmutable();

        void CompareLayout(DiagramPresentationView oldView, DiagramPresentationView newView)
        {
            // Object: the placed block, the port's interface, the routed connection, or the level for its frame and z-order.
            var oldEntries = LayoutEntries(oldView); var newEntries = LayoutEntries(newView);
            foreach (var (key, entry) in newEntries)
                if (!oldEntries.TryGetValue(key, out var old)) changes.Add(new(DiagramHistoryChangeCategory.Layout, DiagramHistoryChangeKind.Added, entry.Object, "Layout"));
                else if (!old.Same(entry)) changes.Add(new(DiagramHistoryChangeCategory.Layout, DiagramHistoryChangeKind.Changed, entry.Object, "Layout"));
            foreach (var (_, entry) in oldEntries.Where(e => !newEntries.ContainsKey(e.Key)))
                changes.Add(new(DiagramHistoryChangeCategory.Layout, DiagramHistoryChangeKind.Removed, entry.Object, "Layout"));
            var common = newView.BlockPlacements.Select(b => b.BlockId).Where(id => oldView.BlockPlacements.Any(o => o.BlockId == id)).ToHashSet();
            if (!oldView.BlockPlacements.Select(b => b.BlockId).Where(common.Contains).SequenceEqual(newView.BlockPlacements.Select(b => b.BlockId).Where(common.Contains))
                && !changes.Any(c => c.Category == DiagramHistoryChangeCategory.Layout && c.ObjectId == context.BlockId && c.Kind == DiagramHistoryChangeKind.Changed))
                changes.Add(new(DiagramHistoryChangeCategory.Layout, DiagramHistoryChangeKind.Changed, context.BlockId, "Layout"));
        }

        Dictionary<string, LayoutEntry> LayoutEntries(DiagramPresentationView view)
        {
            var result = new Dictionary<string, LayoutEntry>(StringComparer.Ordinal);
            if (view.Frame is { } frame) result["frame"] = new(context.BlockId, frame);
            foreach (var block in view.BlockPlacements) result[DiagramPresentationView.Key(block)] = new(block.BlockId, block);
            foreach (var port in view.PortPlacements) result[DiagramPresentationView.Key(port)] = new(port.InterfaceId, port);
            foreach (var route in view.ConnectionRoutes) result[DiagramPresentationView.Key(route)] = new(route.ConnectionId, route);
            return result;
        }

        void CompareItems<T>(ImmutableArray<T> oldItems, ImmutableArray<T> newItems, DiagramHistoryChangeCategory category,
            Func<T, Guid> id, Func<T, string> name, Func<T, T, bool> same, ImmutableArray<DiagramHistoryChange>.Builder? into = null)
        {
            var output = into ?? changes;
            var oldById = oldItems.ToDictionary(id); var newById = newItems.ToDictionary(id);
            foreach (var item in newItems)
                if (!oldById.TryGetValue(id(item), out var old)) output.Add(new(category, DiagramHistoryChangeKind.Added, id(item), name(item)));
                else if (!same(old, item)) output.Add(new(category, DiagramHistoryChangeKind.Changed, id(item), name(item)));
            foreach (var item in oldItems)
                if (!newById.ContainsKey(id(item))) output.Add(new(category, DiagramHistoryChangeKind.Removed, id(item), name(item)));
            if (oldById.Count == newById.Count && oldById.Keys.All(newById.ContainsKey)
                && !oldItems.Select(id).SequenceEqual(newItems.Select(id)))
                output.Add(new(category, DiagramHistoryChangeKind.Reordered, context.BlockId, ""));
        }
    }

    private static bool SamePhysical(BlockPhysicalAllocation? left, BlockPhysicalAllocation? right) => left is null
        ? right is null : right is not null && left.SameContents(right);

    private sealed record LayoutEntry(Guid Object, object Value)
    {
        public bool Same(LayoutEntry other) => Value is DiagramConnectionRoute route
            ? route.SameContents(other.Value as DiagramConnectionRoute) : Equals(Value, other.Value);
    }

    /// <summary>True when the revision differs from its parent revision only in its layout.</summary>
    public static bool LayoutOnly(RecursiveBlockGraph graph, RecursiveBlockRevision revision)
    {
        if (revision.ParentRevisionId is not { } parentId || revision.RestoredFrom is not null) return false;
        var parent = graph.History(revision.Selection.StateId).Single(r => r.Selection.RevisionId == parentId);
        return revision.Name == parent.Name && revision.Children.SequenceEqual(parent.Children)
            && graph.Requirements(revision.Selection).Requirements == graph.Requirements(parent.Selection).Requirements
            && revision.EffectiveDefinition.SameContents(parent.EffectiveDefinition)
            && revision.EffectiveComponentBindings.SameContents(parent.EffectiveComponentBindings)
            && SamePhysical(revision.PhysicalAllocation, parent.PhysicalAllocation)
            && revision.LocalDiagram.SameStructure(parent.LocalDiagram)
            && !DiagramPresentationView.Same(revision.LocalDiagram.Presentation, parent.LocalDiagram.Presentation);
    }
}
