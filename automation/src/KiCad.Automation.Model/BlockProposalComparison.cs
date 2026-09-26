using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Model;

/// <summary>One element that differs between two revisions of the same block. LevelPath names, by block identity, the
/// level whose own fields or local diagram hold the element: the compared block first, then each changed child below it.
/// ConnectionPath is empty for an element of the level itself; for a part of a connection (its name, fields, kind,
/// domain, direction, ends, realization or members) it names that connection and the members leading to it. Aspect names
/// the part of the element that changed where the category alone does not (a definition facet, a connection's ends,
/// kind, domain or direction, or the order of its members). A changed, added or removed child block or connection names
/// its exact revision before and after.</summary>
public sealed record DiagramElementChange(ImmutableArray<Guid> LevelPath, ImmutableArray<Guid> ConnectionPath,
    DiagramHistoryChangeCategory Category, DiagramHistoryChangeKind Kind, Guid ObjectId, string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagramRequirementField? Field = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Aspect = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? BeforeRevisionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? AfterRevisionId = null)
{
    /// <summary>The element and part this change is about, independent of what either side did to it.</summary>
    [JsonIgnore] public string Key => string.Join('/', LevelPath) + "|" + string.Join('/', ConnectionPath) + "|" + Category + "|"
        + ObjectId.ToString("D") + "|" + Field + "|" + Aspect;
}

/// <summary>An element both the current design and the proposal changed since the proposal's base revision.</summary>
public sealed record DiagramElementOverlap(ImmutableArray<Guid> LevelPath, ImmutableArray<Guid> ConnectionPath,
    DiagramHistoryChangeCategory Category, Guid ObjectId, string Name, DiagramHistoryChangeKind CurrentKind,
    DiagramHistoryChangeKind ProposalKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagramRequirementField? Field = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Aspect = null);

/// <summary>A proposal compared with the design it would change (ledger pa48933d0fe0a5c2f). BaseRevision is the target
/// revision the proposal was built on (its original input's), CurrentPath the target's root-to-block path in today's
/// selected design (empty when the target is no longer part of it). ProposalChanges are what the proposal changes from
/// the base. CandidateAdopted: the proposal has been chosen, so today's target is its candidate or descends from it: a
/// later revision of the candidate's implementation, or an implementation made from one (CandidateSelected: exactly the
/// candidate). Then CurrentChanges are only what changed after the candidate, and nothing is changed on both sides,
/// because the proposal's own changes are part of today's design. Otherwise CurrentChanges are what changed from the base to today's revision and ChangedOnBothSides the
/// elements both touched. Stale: the proposal is not adopted and today's target is no longer the base revision, so
/// choosing it is refused rather than applied over the newer work. Choosing an adopted proposal again is refused too.</summary>
public sealed record BlockProposalComparison(Guid DocumentId, Guid ProposalId, Guid InputId, bool Published,
    ImmutableArray<BlockSelection> BasePath, BlockSelection BaseRevision, BlockSelection Candidate,
    ImmutableArray<BlockSelection> CurrentPath, bool Stale, bool CandidateSelected, bool CandidateAdopted,
    ImmutableArray<DiagramElementChange> CurrentChanges, ImmutableArray<DiagramElementChange> ProposalChanges,
    ImmutableArray<DiagramElementOverlap> ChangedOnBothSides)
{
    /// <summary>The same comparison as the refusal details every tool error carries: one entry per change on each side and
    /// per element changed on both, naming the level and the element.</summary>
    public ImmutableArray<AutomationErrorDetail> Details()
    {
        static string Describe(DiagramHistoryChangeCategory category, string kind, string name, DiagramRequirementField? field, string? aspect)
            => $"{category}{(field is { } f ? " " + f : "")}{(aspect is null ? "" : " " + aspect)} {kind}{(string.IsNullOrEmpty(name) ? "" : ": " + name)}";
        return [.. CurrentChanges.Select(c => new AutomationErrorDetail("current_change", c.LevelPath[^1], c.ObjectId,
                Describe(c.Category, c.Kind.ToString(), c.Name, c.Field, c.Aspect))),
            .. ProposalChanges.Select(c => new AutomationErrorDetail("proposal_change", c.LevelPath[^1], c.ObjectId,
                Describe(c.Category, c.Kind.ToString(), c.Name, c.Field, c.Aspect))),
            .. ChangedOnBothSides.Select(c => new AutomationErrorDetail("changed_on_both_sides", c.LevelPath[^1], c.ObjectId,
                Describe(c.Category, c.CurrentKind + " today, " + c.ProposalKind + " in the proposal", c.Name, c.Field, c.Aspect)))];
    }
}

public static class BlockProposalComparer
{
    /// <summary>Compare a published proposal with today's selected design. Reads only.</summary>
    public static BlockProposalComparison Published(RecursiveBlockGraph graph, Guid proposalId)
    {
        var record = graph.Proposal(proposalId);
        return Build(graph, record.Id, record.InputId, true, record.BasePath, record.Candidate);
    }

    /// <summary>Compare a proposal that is not (or not yet) part of the diagram, for example one an agent built on an older
    /// file, by preparing it against today's graph exactly as publication would. Nothing is written; an invalid proposal
    /// is refused with the preparation's own code.</summary>
    public static BlockProposalComparison Request(RecursiveBlockGraph graph, BlockProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (graph.Proposals.Any(p => p.Id == proposal.Id)) return Published(graph, proposal.Id);
        var prepared = BlockProposalCompiler.Prepare(graph, proposal);
        return Build(prepared.Graph, proposal.Id, proposal.InputId, false, proposal.BasePath, proposal.Candidate);
    }

    private static BlockProposalComparison Build(RecursiveBlockGraph graph, Guid proposalId, Guid inputId, bool published,
        ImmutableArray<BlockSelection> basePath, BlockSelection candidate)
    {
        var target = basePath[^1];
        var currentPath = BlockProposalCompiler.FindPath(graph, target.BlockId);
        var proposal = Revisions(graph, target, candidate);
        if (currentPath.IsEmpty)
        {
            // The target left today's design: its removal is reported at the deepest block of its base path that today's design
            // still has, by today's root-to-block path to that block. The root always remains, so such a block exists.
            ImmutableArray<Guid> level = [];
            for (int i = basePath.Length - 2; i >= 0 && level.IsEmpty; --i)
                level = [.. BlockProposalCompiler.FindPath(graph, basePath[i].BlockId).Select(s => s.BlockId)];
            ImmutableArray<DiagramElementChange> removed = [new(level.IsEmpty ? [target.BlockId] : level, [], DiagramHistoryChangeCategory.Block,
                DiagramHistoryChangeKind.Removed, target.BlockId, graph.Inspect(target).Name, BeforeRevisionId: target.RevisionId)];
            return new(graph.DocumentId, proposalId, inputId, published, basePath, target, candidate, currentPath, true, false, false,
                removed, proposal, []);
        }
        var today = currentPath[^1];
        if (Adopted(graph, today, candidate))
            // The proposal was chosen: its own changes are in today's design, so only what changed after the candidate is today's
            // side, and nothing is changed on both sides.
            return new(graph.DocumentId, proposalId, inputId, published, basePath, target, candidate, currentPath, false, today == candidate, true,
                Revisions(graph, candidate, today), proposal, []);
        var current = Revisions(graph, target, today);
        var byKey = current.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
        var both = proposal.Where(p => byKey.ContainsKey(p.Key)).Select(p =>
        {
            var changed = byKey[p.Key];
            return new DiagramElementOverlap(p.LevelPath, p.ConnectionPath, p.Category, p.ObjectId, p.Name, changed.Kind, p.Kind, p.Field, p.Aspect);
        }).ToImmutableArray();
        return new(graph.DocumentId, proposalId, inputId, published, basePath, target, candidate, currentPath, today != target, false, false,
            current, proposal, both);
    }

    /// <summary>Whether <paramref name="today"/> descends from the candidate: the candidate itself, a later saved revision of the
    /// candidate's implementation, or an implementation made from one of those (a duplicate, or a later proposal that refined it),
    /// and so on. Then the proposal has been chosen and its changes are part of today's design. Only the proposal's own candidate
    /// implementation starts at the candidate, so descending from it means the proposal was adopted.</summary>
    private static bool Adopted(RecursiveBlockGraph graph, BlockSelection today, BlockSelection candidate)
    {
        if (today.BlockId != candidate.BlockId) return false;
        var states = graph.States.ToDictionary(s => s.Id); var seen = new HashSet<Guid>();
        for (BlockSelection? revision = today; revision is not null && seen.Add(revision.RevisionId);)
        {
            if (revision == candidate) return true;
            revision = graph.Inspect(revision).ParentRevisionId is { } parent ? revision with { RevisionId = parent }
                : states[revision.StateId].ForkedFrom;
        }
        return false;
    }

    /// <summary>Every element that differs from <paramref name="before"/> to <paramref name="after"/>, two revisions of the same
    /// block in any of its implementations: the block's own level, then, for each child whose pinned revision differs, that
    /// child's level, and for each connection or member whose pinned revision differs, its own parts. An added or removed
    /// child or connection is one entry; its contents are read from its named revision.</summary>
    public static ImmutableArray<DiagramElementChange> Revisions(RecursiveBlockGraph graph, BlockSelection before, BlockSelection after)
    {
        if (before is null || after is null || before.BlockId != after.BlockId)
            throw new AutomationException("wrong_diagram_history_scope", "Compare two revisions of the same block.");
        var result = ImmutableArray.CreateBuilder<DiagramElementChange>();
        Level([before.BlockId], before, after);
        return result.ToImmutable();

        void Level(ImmutableArray<Guid> level, BlockSelection a, BlockSelection b)
        {
            if (a == b) return;
            var old = graph.Inspect(a); var @new = graph.Inspect(b);
            var oldChildren = old.Children.ToDictionary(c => c.BlockId); var newChildren = @new.Children.ToDictionary(c => c.BlockId);
            var oldLinks = old.LocalDiagram.Connections.ToDictionary(c => c.ConnectionId); var newLinks = @new.LocalDiagram.Connections.ToDictionary(c => c.ConnectionId);
            var realizationOnly = new HashSet<Guid>();
            foreach (var change in DiagramHistoryQuery.Changes(graph, a, b))
            {
                Guid? beforeRevision = null, afterRevision = null; string name = change.Name; string? aspect = null;
                switch (change.Category)
                {
                    case DiagramHistoryChangeCategory.Block when change.Kind != DiagramHistoryChangeKind.Reordered:
                        beforeRevision = oldChildren.GetValueOrDefault(change.ObjectId)?.RevisionId; afterRevision = newChildren.GetValueOrDefault(change.ObjectId)?.RevisionId;
                        break;
                    case DiagramHistoryChangeCategory.Connection or DiagramHistoryChangeCategory.InterconnectRealization when change.Kind != DiagramHistoryChangeKind.Reordered:
                        beforeRevision = oldLinks.GetValueOrDefault(change.ObjectId)?.RevisionId; afterRevision = newLinks.GetValueOrDefault(change.ObjectId)?.RevisionId;
                        if (change.Category == DiagramHistoryChangeCategory.InterconnectRealization) realizationOnly.Add(change.ObjectId);
                        break;
                    case DiagramHistoryChangeCategory.Requirement or DiagramHistoryChangeCategory.PhysicalAllocation: name = @new.Name; break;
                    case DiagramHistoryChangeCategory.Definition: name = @new.Name; aspect = change.Name; break;
                    case DiagramHistoryChangeCategory.Layout: name = ""; break;
                }
                if (change.Kind == DiagramHistoryChangeKind.Reordered)
                    aspect = change.Category == DiagramHistoryChangeCategory.Block ? "Children" : change.Category.ToString() + "s";
                result.Add(new(level, [], change.Category, change.Kind, change.ObjectId, name, change.Field, aspect, beforeRevision, afterRevision));
            }
            foreach (var child in @new.Children)
                if (oldChildren.TryGetValue(child.BlockId, out var was) && was != child) Level(level.Add(child.BlockId), was, child);
            foreach (var link in @new.LocalDiagram.Connections)
                if (oldLinks.TryGetValue(link.ConnectionId, out var was) && was != link)
                    Connection(level, [link.ConnectionId], graph.Connections(b.BlockId), was, link, realizationOnly.Contains(link.ConnectionId));
        }

        void Connection(ImmutableArray<Guid> level, ImmutableArray<Guid> path, DiagramConnectionArchive archive, ConnectionSelection a,
            ConnectionSelection b, bool realizationReported)
        {
            var old = archive.Inspect(a); var @new = archive.Inspect(b); Guid id = b.ConnectionId;
            void Add(DiagramHistoryChangeCategory category, DiagramHistoryChangeKind kind, Guid objectId, string name,
                DiagramRequirementField? field = null, string? aspect = null, Guid? beforeRevision = null, Guid? afterRevision = null)
                => result.Add(new(level, path, category, kind, objectId, name, field, aspect, beforeRevision, afterRevision));
            if (old.Name != @new.Name) Add(DiagramHistoryChangeCategory.Name, DiagramHistoryChangeKind.Changed, id, @new.Name);
            var oldFields = archive.Requirements(a).Requirements; var newFields = archive.Requirements(b).Requirements;
            foreach (var field in Enum.GetValues<DiagramRequirementField>())
                if (oldFields.Get(field) != newFields.Get(field))
                    Add(DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, id, @new.Name, field);
            if (old.Kind != @new.Kind) Add(DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Changed, id, @new.Name, aspect: "Kind");
            if (old.Domain != @new.Domain) Add(DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Changed, id, @new.Name, aspect: "Domain");
            if (old.Direction != @new.Direction) Add(DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Changed, id, @new.Name, aspect: "Direction");
            if (old.Endpoints.Length != @new.Endpoints.Length || !old.Endpoints.Zip(@new.Endpoints).All(p => p.First.SameDefinition(p.Second)))
                Add(DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Changed, id, @new.Name, aspect: "Endpoints");
            if (!realizationReported && !InterconnectRealization.Same(old.Realization, @new.Realization))
                Add(DiagramHistoryChangeCategory.InterconnectRealization, old.Realization is null ? DiagramHistoryChangeKind.Added
                    : @new.Realization is null ? DiagramHistoryChangeKind.Removed : DiagramHistoryChangeKind.Changed, id, @new.Name);
            var oldMembers = old.Members.ToDictionary(m => m.ConnectionId); var newMembers = @new.Members.ToDictionary(m => m.ConnectionId);
            foreach (var member in @new.Members)
                if (!oldMembers.TryGetValue(member.ConnectionId, out var was))
                    Add(DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Added, member.ConnectionId, archive.Inspect(member).Name, afterRevision: member.RevisionId);
                else if (was != member)
                    Add(DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Changed, member.ConnectionId, archive.Inspect(member).Name,
                        beforeRevision: was.RevisionId, afterRevision: member.RevisionId);
            foreach (var member in old.Members.Where(m => !newMembers.ContainsKey(m.ConnectionId)))
                Add(DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Removed, member.ConnectionId, archive.Inspect(member).Name, beforeRevision: member.RevisionId);
            if (oldMembers.Count == newMembers.Count && oldMembers.Keys.All(newMembers.ContainsKey)
                && !old.Members.Select(m => m.ConnectionId).SequenceEqual(@new.Members.Select(m => m.ConnectionId)))
                Add(DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Reordered, id, "", aspect: "Members");
            foreach (var member in @new.Members)
                if (oldMembers.TryGetValue(member.ConnectionId, out var was) && was != member)
                    Connection(level, path.Add(member.ConnectionId), archive, was, member, false);
        }
    }
}
