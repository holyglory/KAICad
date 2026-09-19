using System.Collections.Immutable;

namespace KiCad.Automation.Model;

public sealed record DiagramFieldHistoryEntry(Guid RequirementRevisionId, Guid ContextRevisionId,
    int ContextVersion, string OwnerName, string Text, RequirementRevisionOrigin Origin, bool IsSavedText);

public sealed record DiagramFieldHistoryPage(DiagramRequirementScope Scope, DiagramRequirementField Field,
    Guid ContextRevisionId, int ContextVersion, Guid RequirementRevisionId, string SavedText,
    int Offset, int Total, ImmutableArray<DiagramFieldHistoryEntry> Entries)
{
    public bool HasMore => Offset + Entries.Length < Total;
}

/// <summary>Field history bounded to an exact inspected block/connection revision. Later
/// unselected candidates cannot appear as saved edits, and unrelated changes are omitted.</summary>
public static class DiagramFieldHistoryQuery
{
    public static DiagramFieldHistoryPage Block(RecursiveBlockGraph graph, BlockSelection selection,
        DiagramRequirementField field, int offset = 0, int limit = 50)
    {
        _ = graph.Inspect(selection);
        var history = graph.RequirementHistories.Single(h => h.Scope.DesignStateId == selection.StateId);
        return Read(history, graph.History(selection.StateId).Reverse().Select(r =>
            (r.Selection.RevisionId, r.RequirementRevisionId, r.Name)), selection.RevisionId, field, offset, limit);
    }

    public static DiagramFieldHistoryPage Connection(DiagramConnectionArchive archive, ConnectionSelection selection,
        DiagramRequirementField field, int offset = 0, int limit = 50)
    {
        _ = archive.Inspect(selection);
        var history = archive.RequirementHistories.Single(h => h.Scope.DesignStateId == selection.StateId);
        return Read(history, archive.History(selection.StateId).Reverse().Select(r =>
            (r.Selection.RevisionId, r.RequirementRevisionId, r.Name)), selection.RevisionId, field, offset, limit);
    }

    private static DiagramFieldHistoryPage Read(DiagramRequirementHistory history,
        IEnumerable<(Guid RevisionId, Guid RequirementId, string Name)> contexts,
        Guid targetRevision, DiagramRequirementField field, int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 200 || !Enum.IsDefined(field))
            throw new AutomationException("invalid_field_history_query", "Choose a supported field and a bounded history page of 1 to 200 entries.");
        var byId = history.Revisions.ToDictionary(r => r.Id);
        var entries = new List<DiagramFieldHistoryEntry>();
        string? previousText = null; int version = 0;
        foreach (var context in contexts)
        {
            ++version;
            var revision = byId[context.RequirementId];
            string text = revision.Requirements.Get(field);
            if (previousText != text)
                entries.Add(new(revision.Id, context.RevisionId, version, context.Name, text, revision.Origin, false));
            previousText = text;
            if (context.RevisionId != targetRevision) continue;
            // The current field value can predate the current diagram revision. Keep
            // its real change context instead of fabricating a field edit for a rename.
            entries[^1] = entries[^1] with { IsSavedText = true };
            return new(history.Scope, field, context.RevisionId, version, revision.Id, text, offset, entries.Count,
                entries.AsEnumerable().Reverse().Skip(offset).Take(limit).ToImmutableArray());
        }
        throw new AutomationException("missing_field_history_context", "The requested saved context is not part of this exact implementation history.");
    }
}
