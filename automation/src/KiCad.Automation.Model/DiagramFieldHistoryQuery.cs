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
/// unselected candidates cannot appear as saved edits, and unrelated changes are omitted.
/// An implementation made from another one (a duplicate, or a proposal that refined the block or
/// connection) continues that implementation's history: its earlier texts come first, up to the revision
/// it was made from, each with its own author, sources, linked inputs and the version it has in its own
/// implementation's diagram history. Paging and the saved context stay bound to the requested implementation.</summary>
public static class DiagramFieldHistoryQuery
{
    public static DiagramFieldHistoryPage Block(RecursiveBlockGraph graph, BlockSelection selection,
        DiagramRequirementField field, int offset = 0, int limit = 50)
    {
        _ = graph.Inspect(selection);
        return Read(graph.RequirementHistories, selection.StateId, state => graph.History(state).Reverse().Select(r =>
            (r.Selection.RevisionId, r.RequirementRevisionId, r.Name)), selection.RevisionId, field, offset, limit);
    }

    public static DiagramFieldHistoryPage Connection(DiagramConnectionArchive archive, ConnectionSelection selection,
        DiagramRequirementField field, int offset = 0, int limit = 50)
    {
        _ = archive.Inspect(selection);
        return Read(archive.RequirementHistories, selection.StateId, state => archive.History(state).Reverse().Select(r =>
            (r.Selection.RevisionId, r.RequirementRevisionId, r.Name)), selection.RevisionId, field, offset, limit);
    }

    private static DiagramFieldHistoryPage Read(ImmutableArray<DiagramRequirementHistory> histories, Guid stateId,
        Func<Guid, IEnumerable<(Guid RevisionId, Guid RequirementId, string Name)>> contexts,
        Guid targetRevision, DiagramRequirementField field, int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 200 || !Enum.IsDefined(field))
            throw new AutomationException("invalid_field_history_query", "Choose a supported field and a bounded history page of 1 to 200 entries.");
        var history = histories.Single(h => h.Scope.DesignStateId == stateId);
        // The implementations this one continues, oldest first, each read up to the revision it was made from.
        // The owning graph or archive has already proved every link names exactly one other implementation.
        var chain = new List<(DiagramRequirementHistory History, Guid? Until)> { (history, null) };
        for (var current = history; current.DerivedFrom is { } parentId && chain.Count <= histories.Length;)
        {
            current = histories.Single(h => h.Scope.OwnerId == current.Scope.OwnerId && h.Revisions.Any(r => r.Id == parentId));
            chain.Add((current, parentId));
        }
        chain.Reverse();
        var entries = new List<DiagramFieldHistoryEntry>();
        string? previousText = null;
        foreach (var (owner, until) in chain)
        {
            int version = 0;
            foreach (var context in contexts(owner.Scope.DesignStateId))
            {
                ++version;
                var revision = owner.Revision(context.RequirementId);
                string text = revision.Requirements.Get(field);
                if (previousText != text)
                    entries.Add(new(revision.Id, context.RevisionId, version, context.Name, text, revision.Origin, false));
                previousText = text;
                if (until is { } derived)
                {
                    // Later revisions of the earlier implementation are not part of this one's history.
                    if (context.RequirementId == derived) break;
                    continue;
                }
                if (context.RevisionId != targetRevision) continue;
                // The current field value can predate the current diagram revision. Keep
                // its real change context instead of fabricating a field edit for a rename.
                entries[^1] = entries[^1] with { IsSavedText = true };
                return new(history.Scope, field, context.RevisionId, version, revision.Id, text, offset, entries.Count,
                    entries.AsEnumerable().Reverse().Skip(offset).Take(limit).ToImmutableArray());
            }
        }
        throw new AutomationException("missing_field_history_context", "The requested saved context is not part of this exact implementation history.");
    }
}
