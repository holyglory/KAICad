using System.Collections.Immutable;

namespace KiCad.Automation.Model;

public sealed record DiagramHistoryEntry(BlockSelection Selection, int Version, string Name,
    RequirementRevisionOrigin Origin, int ChildCount, int ConnectionCount, int AnnotationCount, bool IsContext);

public sealed record DiagramHistoryPage(Guid DocumentId, BlockSelection Context, int ContextVersion,
    int Offset, int Total, ImmutableArray<DiagramHistoryEntry> Entries)
{
    public bool HasMore => Offset + Entries.Length < Total;
}

public enum DiagramHistoryChangeKind { Added, Removed, Changed, Reordered }
public enum DiagramHistoryChangeCategory { Name, Requirement, Block, Connection, Interface, Comment, Definition, PhysicalAllocation }
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
            r.LocalDiagram.Notes.Length, r.Selection == context)).ToImmutableArray();
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
        var before = Inspect(graph, context, inspected); var after = graph.Inspect(context);
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
        CompareItems(before.LocalDiagram.Connections, after.LocalDiagram.Connections, DiagramHistoryChangeCategory.Connection,
            c => c.ConnectionId, c => graph.Connections(context.BlockId).Inspect(c).Name, (a, b) => a == b);
        CompareItems(before.LocalDiagram.Interfaces, after.LocalDiagram.Interfaces, DiagramHistoryChangeCategory.Interface,
            i => i.Id, i => i.Name, (a, b) => a == b);
        CompareItems(before.LocalDiagram.Notes, after.LocalDiagram.Notes, DiagramHistoryChangeCategory.Comment,
            n => n.Id, n => n.Text, (a, b) => a.SameContents(b));
        int version = Read(graph, context, 0, 1).ContextVersion;
        int inspectedVersion = Read(graph, inspected, 0, 1).ContextVersion;
        return new(graph.DocumentId, context, inspected, version, inspectedVersion, before.Origin, changes.ToImmutable());

        void CompareItems<T>(ImmutableArray<T> oldItems, ImmutableArray<T> newItems, DiagramHistoryChangeCategory category,
            Func<T, Guid> id, Func<T, string> name, Func<T, T, bool> same)
        {
            var oldById = oldItems.ToDictionary(id); var newById = newItems.ToDictionary(id);
            foreach (var item in newItems)
                if (!oldById.TryGetValue(id(item), out var old)) changes.Add(new(category, DiagramHistoryChangeKind.Added, id(item), name(item)));
                else if (!same(old, item)) changes.Add(new(category, DiagramHistoryChangeKind.Changed, id(item), name(item)));
            foreach (var item in oldItems)
                if (!newById.ContainsKey(id(item))) changes.Add(new(category, DiagramHistoryChangeKind.Removed, id(item), name(item)));
            if (oldById.Count == newById.Count && oldById.Keys.All(newById.ContainsKey)
                && !oldItems.Select(id).SequenceEqual(newItems.Select(id)))
                changes.Add(new(category, DiagramHistoryChangeKind.Reordered, context.BlockId, ""));
        }
    }

    private static bool SamePhysical(BlockPhysicalAllocation? left, BlockPhysicalAllocation? right) => left is null
        ? right is null : right is not null && left.SameContents(right);
}
