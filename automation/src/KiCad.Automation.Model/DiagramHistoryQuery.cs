using System.Collections.Immutable;

namespace KiCad.Automation.Model;

public sealed record DiagramHistoryEntry(BlockSelection Selection, int Version, string Name,
    RequirementRevisionOrigin Origin, int ChildCount, int ConnectionCount, int AnnotationCount, bool IsContext);

public sealed record DiagramHistoryPage(Guid DocumentId, BlockSelection Context, int ContextVersion,
    int Offset, int Total, ImmutableArray<DiagramHistoryEntry> Entries)
{
    public bool HasMore => Offset + Entries.Length < Total;
}

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
}
