using System.Collections.Immutable;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockProposalTests
{
    internal static (RecursiveBlockGraph Graph, BlockProposal Proposal, BlockSelection Sibling) Fixture()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var baseline = f.Blocks["PSU"];
        var input = RecursiveBlockRefinementInputTests.Input(graph) with { BlockPath = [graph.SelectedRoot, baseline], Attachments = [] };
        graph = graph.WithRefinementInput(input);
        ProposedBlock Node(string name) => new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), null, "Proposed", name,
            Guid.NewGuid(), new("Test-only " + name, "Keep grouping readable.", "Select details after evaluating parts."), [], BlockLocalDiagram.Empty);
        var converter = Node("Converters"); var telemetry = Node("Telemetry controller"); var auxiliary = Node("Auxiliary supply");
        converter = converter with { Children = [auxiliary.Selection] };
        Guid owner = baseline.BlockId;
        var member = new ProposedConnection(owner, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), null,
            "Proposed", "Telemetry data", DiagramConnectionKind.Signal, Guid.NewGuid(), new("Report power status.", "Label this signal.", "Keep away from switching paths."),
            [DiagramEndpointBinding.Unknown(converter.Selection.BlockId, "Source assignment remains unknown."),
             new(DiagramEndpointKind.Compatible, telemetry.Selection.BlockId, null, "Any compatible input.",
                 new("Telemetry input", "", [], []), [], null)], []);
        var link = new ProposedConnection(owner, new(f.Links["PSU/Telemetry"].ConnectionId, Guid.NewGuid(), Guid.NewGuid()), f.Links["PSU/Telemetry"],
            "Proposed bundle", "Telemetry", DiagramConnectionKind.SignalGroup, Guid.NewGuid(), new("Status and control bundle.", "Group member labels.", "Resolve routing after component choice."),
            [DiagramEndpointBinding.Unknown(converter.Selection.BlockId), DiagramEndpointBinding.Unknown(telemetry.Selection.BlockId)],
            [member.Selection], Guid.NewGuid(), Guid.NewGuid());
        var original = graph.Inspect(baseline);
        var target = new ProposedBlock(new(owner, Guid.NewGuid(), Guid.NewGuid()), baseline, "Detailed supply proposal", "PSU",
            Guid.NewGuid(), new("Provide the required power and telemetry.", "Separate conversion and telemetry groups.", "Preserve the thermal area."),
            [converter.Selection, telemetry.Selection], new(original.LocalDiagram.Interfaces, [link.Selection]),
            Definition: RecursiveBlockDefinitionTests.Partial(), ForkRevisionId: Guid.NewGuid(), ForkRequirementRevisionId: Guid.NewGuid());
        var proposal = new BlockProposal(Guid.NewGuid(), input.Id, input.BlockPath, target.Selection,
            [target, converter, telemetry, auxiliary], [link, member],
            [new(Guid.NewGuid(), BlockProposalIssueKind.Unresolved, "Part selection and voltage rails remain open.", target.Selection.BlockId, [])],
            RecursiveBlockFixture.Origin("Compatible agent fixture"));
        return (graph, proposal, f.Blocks["CPU"]);
    }

    /// <summary>A whole-block proposal that rewrites the target's three fields. With <paramref name="refine"/>, it also
    /// refines that existing connection of the target's level (a new implementation of it with rewritten fields).</summary>
    internal static BlockProposal CreateFor(RecursiveBlockGraph graph, DiagramRefinementInput input, ConnectionSelection? refine = null)
    {
        var baseline = input.BlockPath[^1]; var original = graph.Inspect(baseline);
        ProposedBlock Node(string name) => new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), null, "Agent proposal", name,
            Guid.NewGuid(), new("Add " + name + " for the requested design.", "Keep this group readable.", "Preserve thermal and signal separation."), [], BlockLocalDiagram.Empty);
        var converter = Node("Converter"); var telemetry = Node("Telemetry controller");
        var signal = new ProposedConnection(baseline.BlockId, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), null,
            "Agent proposal", "Telemetry bundle", DiagramConnectionKind.SignalGroup, Guid.NewGuid(),
            new("Expose supply status.", "Show the bundle as a grouped signal.", "Keep it away from switching paths."),
            [DiagramEndpointBinding.Unknown(converter.Selection.BlockId), DiagramEndpointBinding.Unknown(telemetry.Selection.BlockId)], []);
        var connections = original.LocalDiagram.Connections.Add(signal.Selection);
        ImmutableArray<ProposedConnection> proposed = [signal];
        if (refine is { } basis)
        {
            var before = graph.Connections(baseline.BlockId).Inspect(basis);
            var refined = new ProposedConnection(baseline.BlockId, new(basis.ConnectionId, Guid.NewGuid(), Guid.NewGuid()), basis,
                "Agent proposal", before.Name, before.Kind, Guid.NewGuid(),
                new("Carry the converted supply to the boundary port.", "Label the rail at both ends.", "Keep the rail short and wide."),
                before.Endpoints, before.Members, Guid.NewGuid(), Guid.NewGuid(), before.Domain, before.Direction, before.Realization);
            connections = connections.Replace(basis, refined.Selection); proposed = proposed.Add(refined);
        }
        var target = new ProposedBlock(new(baseline.BlockId, Guid.NewGuid(), Guid.NewGuid()), baseline, "Agent proposal", original.Name,
            Guid.NewGuid(), new("Refined implementation from the original request.", "Separate conversion and telemetry.", "Preserve the existing thermal region."),
            [.. original.Children, converter.Selection, telemetry.Selection],
            original.LocalDiagram with { Connections = connections },
            Definition: RecursiveBlockDefinitionTests.Partial(), ForkRevisionId: Guid.NewGuid(), ForkRequirementRevisionId: Guid.NewGuid());
        return new BlockProposal(Guid.NewGuid(), input.Id, input.BlockPath, target.Selection,
            [target, converter, telemetry], proposed,
            [new(Guid.NewGuid(), BlockProposalIssueKind.Unresolved, "Exact models and rails remain open.", target.Selection.BlockId, [])],
            RecursiveBlockFixture.Origin("Compatible agent fixture"));
    }

    [TestMethod]
    public void CompleteDecompositionAndPartialBundlePrepareWithoutMovingActiveHeads()
    {
        var f = Fixture(); string before = RecursiveBlockGraphXml.Write(f.Graph);
        var prepared = BlockProposalCompiler.Prepare(f.Graph, f.Proposal); var graph = prepared.Graph;
        Assert.IsTrue(prepared.ContextStillSelected); Assert.AreEqual(f.Graph.SelectedRoot, graph.SelectedRoot);
        Assert.IsTrue(f.Graph.States.All(s => graph.States.Contains(s)), "No active or inactive implementation head may advance during preparation.");
        Assert.HasCount(4, graph.Walk(f.Proposal.Candidate));
        var blockHistory = graph.History(f.Proposal.Candidate.StateId);
        Assert.HasCount(2, blockHistory);
        Assert.AreEqual(f.Graph.Requirements(f.Proposal.BasePath[^1]).Requirements, graph.Requirements(blockHistory[1].Selection).Requirements);
        Assert.AreEqual(f.Proposal.Blocks[0].Requirements, graph.Requirements(f.Proposal.Candidate).Requirements);
        Assert.IsTrue(blockHistory[0].Origin.InputIds.Contains(f.Proposal.InputId));
        Assert.IsTrue(blockHistory[0].Origin.InputIds.Contains(f.Proposal.Id));
        var connection = graph.Connections(f.Proposal.Candidate.BlockId).Inspect(f.Proposal.Connections[0].Selection);
        Assert.AreEqual(DiagramConnectionKind.SignalGroup, connection.Kind); Assert.HasCount(1, connection.Members);
        var signal = graph.Connections(f.Proposal.Candidate.BlockId).Inspect(connection.Members[0]);
        Assert.AreEqual(DiagramEndpointKind.Unresolved, signal.Endpoints[0].Kind);
        Assert.AreEqual(DiagramEndpointKind.Compatible, signal.Endpoints[1].Kind);
        Assert.IsTrue(signal.Endpoints.All(e => e.Pin is null));
        // The proposed implementation continues the baseline's field history (ledger p390b40bed99e0ab2): its copy of the
        // baseline names the exact revision it was derived from, earlier texts keep their author, and the rewrite follows.
        var baselineText = f.Graph.Requirements(f.Proposal.BasePath[^1]);
        var candidateHistory = graph.RequirementHistories.Single(h => h.Scope.DesignStateId == f.Proposal.Candidate.StateId);
        Assert.AreEqual(baselineText.RevisionId, candidateHistory.DerivedFrom);
        Assert.AreEqual(baselineText.RevisionId, candidateHistory.Lineage[^1].Id);
        var general = DiagramFieldHistoryQuery.Block(graph, f.Proposal.Candidate, DiagramRequirementField.General);
        Assert.AreEqual(2, general.ContextVersion);
        CollectionAssert.AreEqual(new[] { f.Proposal.Blocks[0].RequirementRevisionId, baselineText.RevisionId },
            general.Entries.Select(e => e.RequirementRevisionId).ToArray());
        Assert.AreEqual(f.Proposal.Origin.Actor, general.Entries[0].Origin.Actor);
        CollectionAssert.IsSubsetOf(new[] { f.Proposal.InputId, f.Proposal.Id }, general.Entries[0].Origin.InputIds.ToArray());
        Assert.AreEqual(f.Graph.Requirements(f.Proposal.BasePath[^1]).Requirements.General, general.Entries[1].Text);
        Assert.AreEqual("Fixture user", general.Entries[1].Origin.Actor);
        Assert.AreEqual(f.Proposal.BasePath[^1].RevisionId, general.Entries[1].ContextRevisionId); Assert.AreEqual(1, general.Entries[1].ContextVersion);
        var refined = f.Proposal.Connections.Single(c => c.BasedOn is not null);
        var linkText = f.Graph.Connections(refined.OwnerBlockId).Requirements(refined.BasedOn!);
        var links = graph.Connections(refined.OwnerBlockId);
        Assert.AreEqual(linkText.RevisionId, links.RequirementHistories.Single(h => h.Scope.DesignStateId == refined.Selection.StateId).DerivedFrom);
        var linkPage = DiagramFieldHistoryQuery.Connection(links, refined.Selection, DiagramRequirementField.General);
        CollectionAssert.AreEqual(new[] { refined.RequirementRevisionId, linkText.RevisionId }, linkPage.Entries.Select(e => e.RequirementRevisionId).ToArray());
        Assert.AreEqual(refined.BasedOn!.RevisionId, linkPage.Entries[1].ContextRevisionId);
        var selected = graph.Select(graph.SelectedRoot, f.Proposal.BasePath, f.Proposal.Candidate, [Guid.NewGuid()], f.Proposal.Origin).Graph;
        Assert.AreEqual(f.Sibling, selected.Inspect(selected.SelectedRoot).Children[1]);
        CollectionAssert.AreEqual(general.Entries.Select(e => e.RequirementRevisionId).ToArray(), DiagramFieldHistoryQuery.Block(selected,
            selected.Inspect(selected.SelectedRoot).Children[0], DiagramRequirementField.General).Entries.Select(e => e.RequirementRevisionId).ToArray(),
            "Choosing the proposal keeps the field history it continues.");
        Assert.AreEqual(f.Proposal.Candidate, selected.Inspect(selected.SelectedRoot).Children[0]);
        Assert.AreEqual(before, RecursiveBlockGraphXml.Write(f.Graph));
        string xml = RecursiveBlockGraphXml.Write(graph);
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(xml)));
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(graph))));
    }

    [TestMethod]
    public void StaleContextCanBePreparedForReviewWithoutOverwritingNewerWork()
    {
        var f = Fixture(); var changed = RecursiveBlockFixture.RefineRoot(f.Graph, DiagramRequirementField.General, 1);
        string newer = RecursiveBlockGraphXml.Write(changed);
        var proposal = BlockProposalCompiler.Prepare(changed, f.Proposal);
        Assert.IsFalse(proposal.ContextStillSelected); Assert.AreEqual(changed.SelectedRoot, proposal.Graph.SelectedRoot);
        Assert.AreEqual(changed.Requirements(changed.SelectedRoot).Requirements, proposal.Graph.Requirements(proposal.Graph.SelectedRoot).Requirements);
        Assert.ThrowsExactly<AutomationException>(() => proposal.Graph.Select(f.Proposal.BasePath[0], f.Proposal.BasePath,
            f.Proposal.Candidate, [Guid.NewGuid()], f.Proposal.Origin));
        Assert.AreEqual(newer, RecursiveBlockGraphXml.Write(changed));
        // Unrelated ancestor text can be retained through an explicitly refreshed
        // path; the target's original revision has not been guessed or rewritten.
        var chosen = proposal.Graph.Select(changed.SelectedRoot, [changed.SelectedRoot, f.Proposal.BasePath[^1]],
            f.Proposal.Candidate, [Guid.NewGuid()], f.Proposal.Origin).Graph;
        Assert.AreEqual(changed.Requirements(changed.SelectedRoot).Requirements, chosen.Requirements(chosen.SelectedRoot).Requirements);
    }

    [TestMethod]
    public void InvalidMembersUnreachableObjectsAndIdentityReuseRejectTheWholeProposal()
    {
        var f = Fixture(); string xml = RecursiveBlockGraphXml.Write(f.Graph); var p = f.Proposal;
        foreach (var invalid in new[]
        {
            p with { InputId = Guid.NewGuid() }, p with { Candidate = f.Sibling },
            p with { Blocks = p.Blocks.SetItem(0, p.Blocks[0] with { Selection = p.BasePath[^1] }) },
            p with { Blocks = p.Blocks.SetItem(0, p.Blocks[0] with { Children = [f.Sibling, .. p.Blocks[0].Children] }) },
            p with { Blocks = p.Blocks.SetItem(0, p.Blocks[0] with { Children = [] }) },
            p with { Connections = p.Connections.SetItem(1, p.Connections[1] with { OwnerBlockId = f.Sibling.BlockId }) },
            p with { Connections = p.Connections.SetItem(0, p.Connections[0] with { Members = [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())] }) },
            p with { Connections = p.Connections.SetItem(1, p.Connections[1] with { Endpoints = [DiagramEndpointBinding.Unknown(Guid.NewGuid()), p.Connections[1].Endpoints[1]] }) },
            p with { Issues = [p.Issues[0] with { TargetId = f.Sibling.BlockId }] },
            p with { Blocks = p.Blocks.SetItem(0, p.Blocks[0] with { ForkRequirementRevisionId = p.Blocks[0].RequirementRevisionId }) }
        })
        {
            Assert.ThrowsExactly<AutomationException>(() => BlockProposalCompiler.Prepare(f.Graph, invalid));
            Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(f.Graph));
        }
    }
}
