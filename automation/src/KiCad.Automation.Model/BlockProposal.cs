using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Model;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProposedBlock(BlockSelection Selection, BlockSelection? BasedOn, string ImplementationName,
    string Name, Guid RequirementRevisionId, DiagramRequirements Requirements, ImmutableArray<BlockSelection> Children,
    BlockLocalDiagram Diagram, BlockDefinition? Definition = null, BlockComponentBindings? ComponentBindings = null,
    Guid? ForkRevisionId = null, Guid? ForkRequirementRevisionId = null,
    BlockPhysicalAllocation? PhysicalAllocation = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProposedConnection(Guid OwnerBlockId, ConnectionSelection Selection, ConnectionSelection? BasedOn,
    string ImplementationName, string Name, DiagramConnectionKind Kind, Guid RequirementRevisionId,
    DiagramRequirements Requirements, ImmutableArray<DiagramEndpointBinding> Endpoints, ImmutableArray<ConnectionSelection> Members,
    Guid? ForkRevisionId = null, Guid? ForkRequirementRevisionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DiagramDomain Domain = DiagramDomain.Unspecified,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DiagramConnectionDirection Direction = DiagramConnectionDirection.Unspecified,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InterconnectRealization? Realization = null);
public enum BlockProposalIssueKind { Unresolved, Conflicting, Unsupported }
public sealed record BlockProposalIssue(Guid Id, BlockProposalIssueKind Kind, string Message, Guid? TargetId,
    ImmutableArray<SourceReference> Sources);

/// <summary>A complete proposed closure, not a replacement of existing history.
/// Changed occurrences get independent implementations; active heads remain editable.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BlockProposal(Guid Id, Guid InputId, ImmutableArray<BlockSelection> BasePath,
    BlockSelection Candidate, ImmutableArray<ProposedBlock> Blocks, ImmutableArray<ProposedConnection> Connections,
    ImmutableArray<BlockProposalIssue> Issues, RequirementRevisionOrigin Origin);
public sealed record PreparedBlockProposal(BlockProposal Proposal, RecursiveBlockGraph Graph, bool ContextStillSelected);

public static class BlockProposalCompiler
{
    public static PreparedBlockProposal Prepare(RecursiveBlockGraph graph, BlockProposal proposal)
    {
        if (proposal is null || proposal.Id == Guid.Empty || proposal.InputId == Guid.Empty || proposal.BasePath.IsDefaultOrEmpty
            || proposal.Candidate is null || proposal.Blocks.IsDefaultOrEmpty || proposal.Connections.IsDefault || proposal.Issues.IsDefault
            || proposal.Origin is null || proposal.Origin.ActorKind != RequirementRevisionActor.Agent)
            throw Invalid("Supply the full typed proposal, original input, exact context and agent origin.");
        proposal.Origin.Validate();
        var input = graph.RefinementInput(proposal.InputId);
        if (!proposal.BasePath.SequenceEqual(input.BlockPath)) throw Invalid("The proposal must retain the original input's exact block path.");
        input.ValidateAgainst(graph);
        if (proposal.Candidate.BlockId != proposal.BasePath[^1].BlockId)
            throw Invalid("The proposed implementation belongs to the input's target block, not a replacement occurrence.");
        var originalScope = graph.Walk(proposal.BasePath[^1]).Select(s => s.BlockId).ToHashSet();
        var knownBlocks = graph.States.Select(s => s.BlockId).ToHashSet();
        var used = graph.RetainedIdentities();
        Fresh(proposal.Id); var declaredBlocks = new HashSet<Guid>();
        var states = graph.States.ToBuilder(); var revisions = graph.Revisions.ToBuilder(); var histories = graph.RequirementHistories.ToBuilder();
        var origin = proposal.Origin with { InputIds = proposal.Origin.InputIds.Append(input.Id).Append(proposal.Id).Distinct().ToImmutableArray() };
        var names = graph.States.Select(s => (s.BlockId, s.Name)).ToList();
        foreach (var block in proposal.Blocks)
        {
            if (block?.Selection is not { } selection || block.Requirements is null || block.Diagram is null || block.Children.IsDefault
                || !declaredBlocks.Add(selection.BlockId)) throw Invalid("Declare each proposed block exactly once, with its complete local diagram and requirements.");
            Text(block.ImplementationName); Text(block.Name); block.Requirements.Validate(); block.Diagram.Validate(selection.BlockId);
            block.PhysicalAllocation?.Validate();
            Fresh(selection.StateId); Fresh(selection.RevisionId); Fresh(block.RequirementRevisionId);
            if (names.Any(n => n.BlockId == selection.BlockId && string.Equals(n.Name, block.ImplementationName, StringComparison.OrdinalIgnoreCase)))
                throw Invalid("Use a distinct implementation name for this block.");
            names.Add((selection.BlockId, block.ImplementationName));
            var scope = new DiagramRequirementScope(graph.DocumentId, selection.BlockId, selection.StateId);
            ImmutableArray<DiagramRequirementRevision> requirementHistory = [];
            if (block.BasedOn is { } basedOn)
            {
                if (basedOn.BlockId != selection.BlockId || !originalScope.Contains(selection.BlockId))
                    throw Invalid("A changed existing block must belong to the original input's subtree.");
                var before = graph.Inspect(basedOn);
                if (selection == proposal.Candidate && basedOn != proposal.BasePath[^1])
                    throw Invalid("The proposed target must derive from the input's exact baseline revision.");
                Fresh(block.ForkRevisionId ?? Guid.Empty); Fresh(block.ForkRequirementRevisionId ?? Guid.Empty);
                var fork = new BlockSelection(selection.BlockId, selection.StateId, block.ForkRevisionId!.Value);
                revisions.Add(before with { Selection = fork, ParentRevisionId = null, RequirementRevisionId = block.ForkRequirementRevisionId!.Value,
                    Origin = origin, RestoredFrom = null });
                // The proposed implementation continues the baseline's field history: earlier texts keep their
                // authors, sources and linked inputs, and the proposal's rewrite is the next revision.
                var baselineText = graph.Requirements(basedOn);
                requirementHistory = [new(block.ForkRequirementRevisionId.Value, baselineText.RevisionId, baselineText.Requirements, origin, [])];
            }
            else
            {
                if (knownBlocks.Contains(selection.BlockId) || block.ForkRevisionId is not null || block.ForkRequirementRevisionId is not null)
                    throw Invalid("Existing occurrences need explicit baselines; new blocks cannot fabricate a fork history.");
                Fresh(selection.BlockId);
            }
            requirementHistory = requirementHistory.Add(new(block.RequirementRevisionId, block.ForkRequirementRevisionId, block.Requirements, origin, []));
            histories.Add(new(scope, requirementHistory));
            states.Add(new(selection.StateId, selection.BlockId, block.ImplementationName, selection.RevisionId, block.BasedOn));
            revisions.Add(new(selection, block.ForkRevisionId, block.Name, block.RequirementRevisionId, block.Children, origin,
                Diagram: block.Diagram, Definition: block.Definition, ComponentBindings: block.ComponentBindings,
                PhysicalAllocation: block.PhysicalAllocation));
        }
        if (!proposal.Blocks.Any(b => b.Selection == proposal.Candidate && b.BasedOn == proposal.BasePath[^1]))
            throw Invalid("Include the new implementation of the original target in the proposed closure.");
        var archives = graph.ConnectionArchives.ToBuilder();
        foreach (var group in proposal.Connections.GroupBy(c => c?.OwnerBlockId))
        {
            if (group.Key is not { } owner || !declaredBlocks.Contains(owner))
                throw Invalid("Proposed connections belong to a proposed block's local diagram.");
            var existing = graph.ConnectionArchives.SingleOrDefault(a => a.OwnerBlockId == owner);
            var connectionStates = existing?.States.ToBuilder() ?? ImmutableArray.CreateBuilder<ConnectionDesignState>();
            var connectionRevisions = existing?.Revisions.ToBuilder() ?? ImmutableArray.CreateBuilder<DiagramConnectionRevision>();
            var connectionHistories = existing?.RequirementHistories.ToBuilder() ?? ImmutableArray.CreateBuilder<DiagramRequirementHistory>();
            var occurrenceIds = new HashSet<Guid>();
            foreach (var item in group)
            {
                if (item?.Selection is not { } selection || item.Requirements is null || item.Endpoints.IsDefault || item.Members.IsDefault
                    || !occurrenceIds.Add(selection.ConnectionId)) throw Invalid("Declare each proposed connection once with typed endpoints, members and requirements.");
                Text(item.ImplementationName); Text(item.Name); item.Requirements.Validate();
                Fresh(selection.StateId); Fresh(selection.RevisionId); Fresh(item.RequirementRevisionId);
                if (connectionStates.Any(s => s.ConnectionId == selection.ConnectionId && string.Equals(s.Name, item.ImplementationName, StringComparison.OrdinalIgnoreCase)))
                    throw Invalid("Use a distinct implementation name for this connection.");
                ImmutableArray<DiagramRequirementRevision> requirementHistory = [];
                if (item.BasedOn is { } basedOn)
                {
                    if (existing is null || basedOn.ConnectionId != selection.ConnectionId)
                        throw Invalid("A refined connection needs its exact original owner and baseline.");
                    var before = existing.Inspect(basedOn);
                    Fresh(item.ForkRevisionId ?? Guid.Empty); Fresh(item.ForkRequirementRevisionId ?? Guid.Empty);
                    connectionRevisions.Add(before with { Selection = new(selection.ConnectionId, selection.StateId, item.ForkRevisionId!.Value),
                        ParentRevisionId = null, RequirementRevisionId = item.ForkRequirementRevisionId!.Value, Origin = origin });
                    var baselineText = existing.Requirements(basedOn);
                    requirementHistory = [new(item.ForkRequirementRevisionId.Value, baselineText.RevisionId, baselineText.Requirements, origin, [])];
                }
                else
                {
                    Fresh(selection.ConnectionId);
                    if (item.ForkRevisionId is not null || item.ForkRequirementRevisionId is not null) throw Invalid("A new connection cannot claim fork history.");
                }
                connectionStates.Add(new(selection.StateId, selection.ConnectionId, item.ImplementationName, selection.RevisionId));
                connectionRevisions.Add(new(selection, item.ForkRevisionId, item.Name, item.Kind, item.Endpoints, item.RequirementRevisionId, item.Members, origin,
                    item.Domain, item.Direction, item.Realization));
                requirementHistory = requirementHistory.Add(new(item.RequirementRevisionId, item.ForkRequirementRevisionId, item.Requirements, origin, []));
                connectionHistories.Add(new(new(graph.DocumentId, selection.ConnectionId, selection.StateId), requirementHistory));
            }
            var archive = new DiagramConnectionArchive(graph.DocumentId, owner, connectionStates, connectionRevisions, connectionHistories);
            if (existing is null) archives.Add(archive); else archives[archives.IndexOf(existing)] = archive;
        }
        // Construct and validate only after every declaration is available. No
        // mutable input graph or partially published block/connection escapes.
        var result = new RecursiveBlockGraph(graph.DocumentId, graph.SelectedRoot, states, revisions, histories, archives,
            graph.ImplementationChanges, graph.RefinementInputs, graph.Proposals);
        var closure = result.Walk(proposal.Candidate);
        if (declaredBlocks.Any(id => !closure.Any(s => s.BlockId == id))
            || closure.Any(s => !declaredBlocks.Contains(s.BlockId) && !originalScope.Contains(s.BlockId))
            || proposal.Blocks.Any(b => !closure.Contains(b.Selection)))
            throw Invalid("Every proposed block must be reachable, and no unrelated occurrence can be pulled into the proposal.");
        foreach (var connection in proposal.Connections)
        {
            var block = result.Inspect(closure.Single(s => s.BlockId == connection.OwnerBlockId));
            if (!result.Connections(connection.OwnerBlockId).Walk(block.LocalDiagram.Connections).Contains(connection.Selection))
                throw Invalid("Every proposed connection must be selected inside its exact owner's candidate diagram.");
        }
        var targets = closure.Select(s => s.BlockId).Concat(proposal.Connections.Select(c => c.Selection.ConnectionId)).ToHashSet();
        foreach (var issue in proposal.Issues)
        {
            if (issue is null || !Enum.IsDefined(issue.Kind) || issue.Sources.IsDefault || issue.TargetId == Guid.Empty
                || (issue.TargetId is { } target && !targets.Contains(target))) throw Invalid("Issues must name a supported status and an exact object in the proposed closure.");
            Fresh(issue.Id); Text(issue.Message);
            (origin with { Sources = issue.Sources }).Validate();
        }
        bool stillSelected = proposal.BasePath.SequenceEqual(FindPath(graph, proposal.BasePath[^1].BlockId));
        return new(proposal, result, stillSelected);

        void Fresh(Guid id) { if (id == Guid.Empty || !used.Add(id)) throw Invalid("Proposal identities must be fresh and distinct from all retained history."); }
    }

    public static ImmutableArray<BlockSelection> FindPath(RecursiveBlockGraph graph, Guid target)
    {
        var pending = new Stack<ImmutableArray<BlockSelection>>(); pending.Push([graph.SelectedRoot]);
        while (pending.TryPop(out var path))
        {
            if (path[^1].BlockId == target) return path;
            foreach (var child in graph.Inspect(path[^1]).Children) pending.Push(path.Add(child));
        }
        return [];
    }

    public static RecursiveBlockSelectionResult Select(RecursiveBlockGraph graph, Guid proposalId, BlockSelection expectedRoot,
        ImmutableArray<BlockSelection> currentPath, ImmutableArray<Guid> ancestorIds, RequirementRevisionOrigin origin)
    {
        var proposal = graph.Proposal(proposalId);
        var target = proposal.BasePath[^1]; var today = FindPath(graph, target.BlockId);
        if (currentPath.IsDefaultOrEmpty || currentPath[^1] != target || today.IsEmpty || today[^1] != target)
            throw new AutomationException("proposal_target_changed", "The target no longer matches this proposal's original revision; retain both versions for comparison.");
        // The target is unchanged, but the path to it runs through older saved revisions of the blocks on today's path (a root
        // or containing revision the selected design no longer pins): an outdated path is refused as stale (like a connection
        // edit's), so its refusal can report today's path to the target. A path that is not a saved path of those blocks at
        // all (a block skipped or added, a start at another block, a revision the diagram does not have, or a revision that
        // never contained the next block) is not outdated but invalid, and the selection below refuses it.
        if (expectedRoot == graph.SelectedRoot && !currentPath.SequenceEqual(today) && OlderPathOf(graph, currentPath, today))
            throw currentPath[0] != graph.SelectedRoot
                ? new AutomationException("stale_root_revision", "The path to the target starts at another root revision than the selected design; read the target's current path again. Nothing was changed.")
                : new AutomationException("stale_block_revision", "The path to the target names a block revision the selected design no longer pins; read the target's current path again. Nothing was changed.");
        var linked = origin with { InputIds = origin.InputIds.Append(proposal.InputId).Append(proposal.Id).Distinct().ToImmutableArray() };
        return graph.Select(expectedRoot, currentPath, proposal.Candidate, ancestorIds, linked);
    }
    /// <summary>Whether <paramref name="path"/> is a saved path of exactly the blocks on <paramref name="today"/>'s path: the
    /// same blocks in the same order, each an existing revision that contains a revision of the next block.</summary>
    private static bool OlderPathOf(RecursiveBlockGraph graph, ImmutableArray<BlockSelection> path, ImmutableArray<BlockSelection> today)
    {
        if (path.Length != today.Length) return false;
        var saved = graph.Revisions.Select(r => r.Selection).ToHashSet();
        for (int i = 0; i < path.Length; ++i)
        {
            if (path[i] is null || path[i].BlockId != today[i].BlockId || !saved.Contains(path[i])) return false;
            if (i > 0 && !graph.Inspect(path[i - 1]).Children.Any(c => c.BlockId == path[i].BlockId)) return false;
        }
        return true;
    }
    private static void Text(string? value)
    { if (string.IsNullOrWhiteSpace(value)) throw Invalid("Supply meaningful names and issue text."); DiagramEndpointBinding.Text(value); }
    internal static AutomationException Invalid(string message) => new("invalid_block_proposal", message);
}
