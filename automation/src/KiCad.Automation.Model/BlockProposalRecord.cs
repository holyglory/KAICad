using System.Collections.Immutable;

namespace KiCad.Automation.Model;

/// <summary>Durable reference to a published, independent candidate closure. Its
/// revision and original context do not follow subsequently edited state heads.</summary>
public sealed record BlockProposalRecord(Guid Id, Guid InputId, string RequestSha256,
    ImmutableArray<BlockSelection> BasePath, BlockSelection Candidate, ImmutableArray<BlockProposalIssue> Issues,
    RequirementRevisionOrigin Origin)
{
    public void ValidateAgainst(RecursiveBlockGraph graph)
    {
        if (Id == Guid.Empty || InputId == Guid.Empty || BasePath.IsDefaultOrEmpty || Candidate is null || Issues.IsDefault
            || Origin is null || Origin.ActorKind != RequirementRevisionActor.Agent)
            throw BlockProposalCompiler.Invalid("Retain the candidate's exact identity, context, input and agent origin.");
        DiagramRefinementInput.Hash(RequestSha256); Origin.Validate();
        if (!graph.RefinementInput(InputId).BlockPath.SequenceEqual(BasePath)
            || Candidate.BlockId != BasePath[^1].BlockId)
            throw BlockProposalCompiler.Invalid("The candidate record must retain its original input's exact target context.");
        var candidate = graph.Inspect(Candidate);
        var state = graph.States.Single(s => s.Id == Candidate.StateId);
        if (state.ForkedFrom != BasePath[^1] || !candidate.Origin.InputIds.Contains(InputId) || !candidate.Origin.InputIds.Contains(Id)
            || !DiagramRequirementHistory.SameOrigin(Origin, candidate.Origin))
            throw BlockProposalCompiler.Invalid("The candidate must retain its original baseline and input/proposal provenance.");
        var closure = graph.Walk(Candidate); var targets = closure.Select(s => s.BlockId).ToHashSet();
        foreach (var block in closure.Select(graph.Inspect))
            if (!block.LocalDiagram.Connections.IsEmpty)
                foreach (var connection in graph.Connections(block.Selection.BlockId).Walk(block.LocalDiagram.Connections)) targets.Add(connection.ConnectionId);
        var ids = new HashSet<Guid> { Id };
        foreach (var issue in Issues)
        {
            if (issue is null || issue.Id == Guid.Empty || !ids.Add(issue.Id) || !Enum.IsDefined(issue.Kind)
                || string.IsNullOrWhiteSpace(issue.Message) || issue.Sources.IsDefault || issue.TargetId == Guid.Empty
                || (issue.TargetId is { } target && !targets.Contains(target)))
                throw BlockProposalCompiler.Invalid("Retain distinct proposal issues with exact candidate targets and source references.");
            DiagramEndpointBinding.Text(issue.Message); (Origin with { Sources = issue.Sources }).Validate();
        }
    }

    public bool SameContents(BlockProposalRecord other) => other is not null && Id == other.Id && InputId == other.InputId
        && RequestSha256 == other.RequestSha256 && BasePath.SequenceEqual(other.BasePath) && Candidate == other.Candidate
        && DiagramRequirementHistory.SameOrigin(Origin, other.Origin) && Issues.Length == other.Issues.Length
        && Issues.Zip(other.Issues).All(p => p.First.Id == p.Second.Id && p.First.Kind == p.Second.Kind && p.First.Message == p.Second.Message
            && p.First.TargetId == p.Second.TargetId && p.First.Sources.SequenceEqual(p.Second.Sources));
}
