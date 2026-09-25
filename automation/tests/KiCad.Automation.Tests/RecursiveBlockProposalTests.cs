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
    /// <summary>A test-only proposal for the input's target: two new blocks joined by a new signal group and, when
    /// <paramref name="refine"/> names one of the target's connections, a refinement of it that rewrites its three fields. When
    /// <paramref name="refineMember"/> names one of that connection's direct members, the proposal refines the member too: it
    /// gets its own proposed implementation, which continues the member's field history, and the refined connection pins it.</summary>
    internal static BlockProposal CreateFor(RecursiveBlockGraph graph, DiagramRefinementInput input, ConnectionSelection? refine = null,
        ConnectionSelection? refineMember = null)
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
            var archive = graph.Connections(baseline.BlockId);
            var before = archive.Inspect(basis); var members = before.Members;
            ProposedConnection? member = null;
            if (refineMember is { } memberBasis)
            {
                Assert.IsTrue(members.Contains(memberBasis), "The refined member is a direct member of the refined connection.");
                var memberBefore = archive.Inspect(memberBasis);
                member = new ProposedConnection(baseline.BlockId, new(memberBasis.ConnectionId, Guid.NewGuid(), Guid.NewGuid()), memberBasis,
                    "Agent proposal", memberBefore.Name, memberBefore.Kind, Guid.NewGuid(),
                    new("Carry both converted rails together.", "Draw the rails as one group.", "Route each rail beside its return."),
                    memberBefore.Endpoints, memberBefore.Members, Guid.NewGuid(), Guid.NewGuid(), memberBefore.Domain, memberBefore.Direction, memberBefore.Realization);
                members = members.Replace(memberBasis, member.Selection);
            }
            var refined = new ProposedConnection(baseline.BlockId, new(basis.ConnectionId, Guid.NewGuid(), Guid.NewGuid()), basis,
                "Agent proposal", before.Name, before.Kind, Guid.NewGuid(),
                new("Carry the converted supply to the boundary port.", "Label the rail at both ends.", "Keep the rail short and wide."),
                before.Endpoints, members, Guid.NewGuid(), Guid.NewGuid(), before.Domain, before.Direction, before.Realization);
            connections = connections.Replace(basis, refined.Selection); proposed = proposed.Add(refined);
            if (member is not null) proposed = proposed.Add(member);
        }
        else Assert.IsNull(refineMember, "A member is refined only with the connection that contains it.");
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

    // Isolated rules of the stale-proposal comparison (ledger pa48933d0fe0a5c2f). The native journey compares a root proposal with the
    // saved design through the production MCP server, and a chosen supply proposal before and after an edit, but its fixture changes no
    // nested level on both sides, no member of a refined connection, chooses no proposal made from a chosen one, has no proposal below a
    // direct child of the root (so no outdated path through a containing block) and never takes the target out of the design; those
    // cases are only reachable here.
    /// <summary>Which code a comparison that cannot be made reports beside a publication or refusal (review of pa48, finding 3).
    /// The journey proves the realistic case over MCP: an outdated request that no longer prepares reports invalid_block_proposal.
    /// A published proposal was validated when it was saved, so no real request makes comparing it fail without a code; that
    /// comparer failure is reached only here, with a failing comparison, which is why this is an isolated test.</summary>
    [TestMethod]
    public void ComparisonThatCannotBeMadeNamesItsCauseWithoutBlamingAPublishedProposal()
    {
        static string? Code(object? unavailable) => unavailable is null ? null
            : System.Text.Json.JsonSerializer.SerializeToElement(unavailable).GetProperty("code").GetString();
        Func<BlockProposalComparison> malformed = () => throw new InvalidOperationException("The request names nothing it can compare.");
        var (request, requestCause) = KiCad.Automation.Mcp.RecursiveEditorTools.Compare(malformed, retainedRequest: true);
        Assert.IsNull(request); Assert.AreEqual("invalid_block_proposal", Code(requestCause), "An unpublished request that cannot be prepared is malformed.");
        var (published, publishedCause) = KiCad.Automation.Mcp.RecursiveEditorTools.Compare(malformed, retainedRequest: false);
        Assert.IsNull(published); Assert.AreEqual("proposal_comparison_failed", Code(publishedCause), "A saved proposal is not blamed for the comparer's failure.");
        foreach (bool retained in new[] { true, false })
            Assert.AreEqual("unknown_block_proposal", Code(KiCad.Automation.Mcp.RecursiveEditorTools.Compare(
                () => throw new AutomationException("unknown_block_proposal", "No such proposal."), retained).Unavailable), "A coded failure keeps its code.");
        var f = Fixture(); var prepared = BlockProposalCompiler.Prepare(f.Graph, f.Proposal);
        var saved = prepared.Graph.WithProposal(new(f.Proposal.Id, f.Proposal.InputId, BlockProposalFiles.Fingerprint(f.Proposal), f.Proposal.BasePath,
            f.Proposal.Candidate, f.Proposal.Issues, prepared.Graph.Inspect(f.Proposal.Candidate).Origin));
        var (comparison, cause) = KiCad.Automation.Mcp.RecursiveEditorTools.Compare(() => BlockProposalComparer.Published(saved, f.Proposal.Id), retainedRequest: false);
        Assert.IsNotNull(comparison); Assert.IsNull(cause, "A comparison that can be made reports no cause.");
    }

    [TestMethod]
    public void StaleProposalComparisonNamesEachChangedElementOnEachSide()
    {
        static string Key(DiagramElementChange c) => string.Join("/", c.LevelPath) + "|" + string.Join("/", c.ConnectionPath) + "|" + c.Category + "|"
            + c.ObjectId + "|" + c.Field + "|" + c.Aspect + "=" + c.Kind;
        static string Expect(Guid[] level, Guid[] links, DiagramHistoryChangeCategory category, DiagramHistoryChangeKind kind, Guid id,
            DiagramRequirementField? field = null, string? aspect = null) => string.Join("/", level) + "|" + string.Join("/", links) + "|" + category + "|"
            + id + "|" + field + "|" + aspect + "=" + kind;
        var f = Fixture(); var graph = f.Graph; var psu = f.Proposal.BasePath[^1];
        Guid power = graph.Inspect(psu).Children[0].BlockId, oldTelemetry = graph.Inspect(psu).Children[1].BlockId;
        Assert.AreEqual("Power stage", graph.Inspect(graph.Inspect(psu).Children[0]).Name);
        // Today's side: the power stage's General text and then the supply's own General text were saved after the input.
        var powerDraft = graph.StartDraft(graph.Inspect(psu).Children[0]);
        graph = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, psu, powerDraft.Baseline], powerDraft with
            { Requirements = powerDraft.Requirements.Edit(DiagramRequirementField.General, "Test-only regulated output.") },
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        var psuNow = graph.Inspect(graph.SelectedRoot).Children.Single(c => c.BlockId == psu.BlockId);
        var psuDraft = graph.StartDraft(psuNow);
        graph = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, psuNow], psuDraft with
            { Requirements = psuDraft.Requirements.Edit(DiagramRequirementField.General, "Test-only supply for both rails.") },
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        psuNow = graph.Inspect(graph.SelectedRoot).Children.Single(c => c.BlockId == psu.BlockId);
        var powerNow = graph.Inspect(psuNow).Children.Single(c => c.BlockId == power);
        string before = RecursiveBlockGraphXml.Write(graph);
        var request = BlockProposalComparer.Request(graph, f.Proposal);
        Assert.AreEqual(before, RecursiveBlockGraphXml.Write(graph), "Comparing writes nothing.");
        Assert.IsTrue(request.Stale); Assert.IsFalse(request.CandidateSelected); Assert.IsFalse(request.Published);
        Assert.AreEqual(psu, request.BaseRevision); Assert.AreEqual(f.Proposal.Candidate, request.Candidate);
        CollectionAssert.AreEqual(new[] { graph.SelectedRoot, psuNow }, request.CurrentPath.ToArray());
        Guid[] level = [psu.BlockId];
        CollectionAssert.AreEqual(new[]
        {
            Expect(level, [], DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, psu.BlockId, DiagramRequirementField.General),
            Expect(level, [], DiagramHistoryChangeCategory.Block, DiagramHistoryChangeKind.Changed, power),
            Expect([psu.BlockId, power], [], DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, power, DiagramRequirementField.General)
        }, request.CurrentChanges.Select(Key).ToArray(), "Today's side, down to the changed child's own level.");
        var changedPower = request.CurrentChanges[1];
        Assert.AreEqual(graph.Inspect(psu).Children[0].RevisionId, changedPower.BeforeRevisionId); Assert.AreEqual(powerNow.RevisionId, changedPower.AfterRevisionId);
        var link = f.Proposal.Connections[0]; var member = f.Proposal.Connections[1];
        var supply = graph.Inspect(psu).LocalDiagram.Connections[0]; var telemetryLink = graph.Inspect(psu).LocalDiagram.Connections[1];
        Assert.AreEqual(link.BasedOn, telemetryLink);
        Guid[] inLink = [telemetryLink.ConnectionId];
        CollectionAssert.AreEquivalent(new[]
        {
            Expect(level, [], DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, psu.BlockId, DiagramRequirementField.General),
            Expect(level, [], DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, psu.BlockId, DiagramRequirementField.Schematic),
            Expect(level, [], DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, psu.BlockId, DiagramRequirementField.Routing),
            Expect(level, [], DiagramHistoryChangeCategory.Definition, DiagramHistoryChangeKind.Changed, psu.BlockId, aspect: "Purpose"),
            Expect(level, [], DiagramHistoryChangeCategory.Definition, DiagramHistoryChangeKind.Changed, psu.BlockId, aspect: "Type"),
            Expect(level, [], DiagramHistoryChangeCategory.Definition, DiagramHistoryChangeKind.Changed, psu.BlockId, aspect: "Model"),
            Expect(level, [], DiagramHistoryChangeCategory.Definition, DiagramHistoryChangeKind.Changed, psu.BlockId, aspect: "Package"),
            Expect(level, [], DiagramHistoryChangeCategory.Definition, DiagramHistoryChangeKind.Changed, psu.BlockId, aspect: "Knowledge class"),
            Expect(level, [], DiagramHistoryChangeCategory.Block, DiagramHistoryChangeKind.Added, f.Proposal.Blocks[1].Selection.BlockId),
            Expect(level, [], DiagramHistoryChangeCategory.Block, DiagramHistoryChangeKind.Added, f.Proposal.Blocks[2].Selection.BlockId),
            Expect(level, [], DiagramHistoryChangeCategory.Block, DiagramHistoryChangeKind.Removed, power),
            Expect(level, [], DiagramHistoryChangeCategory.Block, DiagramHistoryChangeKind.Removed, oldTelemetry),
            Expect(level, [], DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Changed, telemetryLink.ConnectionId),
            Expect(level, [], DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Removed, supply.ConnectionId),
            Expect(level, inLink, DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, telemetryLink.ConnectionId, DiagramRequirementField.General),
            Expect(level, inLink, DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, telemetryLink.ConnectionId, DiagramRequirementField.Schematic),
            Expect(level, inLink, DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, telemetryLink.ConnectionId, DiagramRequirementField.Routing),
            Expect(level, inLink, DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Changed, telemetryLink.ConnectionId, aspect: "Kind"),
            Expect(level, inLink, DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Changed, telemetryLink.ConnectionId, aspect: "Endpoints"),
            Expect(level, inLink, DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Added, member.Selection.ConnectionId)
        }, request.ProposalChanges.Select(Key).ToArray(), "Exactly what the proposal changes, including the parts of its refined connection.");
        var changedLink = request.ProposalChanges.Single(c => c.Category == DiagramHistoryChangeCategory.Connection && c.ObjectId == telemetryLink.ConnectionId
            && c.ConnectionPath.IsEmpty);
        Assert.AreEqual(telemetryLink.RevisionId, changedLink.BeforeRevisionId); Assert.AreEqual(link.Selection.RevisionId, changedLink.AfterRevisionId);
        Assert.AreEqual(member.Selection.RevisionId, request.ProposalChanges.Single(c => c.ObjectId == member.Selection.ConnectionId).AfterRevisionId);
        CollectionAssert.AreEqual(new[] { (DiagramHistoryChangeCategory.Requirement, psu.BlockId, DiagramHistoryChangeKind.Changed, DiagramHistoryChangeKind.Changed),
                (DiagramHistoryChangeCategory.Block, power, DiagramHistoryChangeKind.Changed, DiagramHistoryChangeKind.Removed) },
            request.ChangedOnBothSides.Select(c => (c.Category, c.ObjectId, c.CurrentKind, c.ProposalKind)).ToArray(),
            "The supply's General text and the power stage were changed on both sides.");
        Assert.HasCount(request.CurrentChanges.Length + request.ProposalChanges.Length + request.ChangedOnBothSides.Length, request.Details());
        // Published, the same proposal compares the same way.
        var prepared = BlockProposalCompiler.Prepare(graph, f.Proposal);
        var published = prepared.Graph.WithProposal(new(f.Proposal.Id, f.Proposal.InputId, BlockProposalFiles.Fingerprint(f.Proposal), f.Proposal.BasePath,
            f.Proposal.Candidate, f.Proposal.Issues, prepared.Graph.Inspect(f.Proposal.Candidate).Origin));
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(request with { Published = true }, options),
            System.Text.Json.JsonSerializer.Serialize(BlockProposalComparer.Published(published, f.Proposal.Id), options));
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(BlockProposalComparer.Published(published, f.Proposal.Id), options),
            System.Text.Json.JsonSerializer.Serialize(BlockProposalComparer.Request(published, f.Proposal), options), "A published request compares as published.");
        // Chosen over a refreshed path, today's supply is the candidate: the proposal is adopted, so its own changes are no longer
        // reported as today's changes or as changed on both sides, and it is not stale.
        var chosen = published.Select(published.SelectedRoot, [published.SelectedRoot, psuNow], f.Proposal.Candidate, [Guid.NewGuid()], f.Proposal.Origin).Graph;
        var afterChoice = BlockProposalComparer.Published(chosen, f.Proposal.Id);
        Assert.IsTrue(afterChoice.CandidateAdopted); Assert.IsTrue(afterChoice.CandidateSelected); Assert.IsFalse(afterChoice.Stale);
        Assert.IsEmpty(afterChoice.CurrentChanges); Assert.IsEmpty(afterChoice.ChangedOnBothSides);
        CollectionAssert.AreEqual(request.ProposalChanges.Select(Key).ToArray(), afterChoice.ProposalChanges.Select(Key).ToArray(), "What the proposal changed stays listed.");
        Assert.HasCount(afterChoice.ProposalChanges.Length, afterChoice.Details());
        // Edited after the choice, the proposal is still adopted: today's side is exactly that later edit.
        var candidateDraft = chosen.StartDraft(f.Proposal.Candidate);
        var editedAfterChoice = chosen.SaveDraft(chosen.SelectedRoot, [chosen.SelectedRoot, f.Proposal.Candidate], candidateDraft with
            { Requirements = candidateDraft.Requirements.Edit(DiagramRequirementField.Routing, "Test-only routing note written after the choice.") },
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        var afterEdit = BlockProposalComparer.Published(editedAfterChoice, f.Proposal.Id);
        Assert.IsTrue(afterEdit.CandidateAdopted); Assert.IsFalse(afterEdit.CandidateSelected); Assert.IsFalse(afterEdit.Stale);
        CollectionAssert.AreEqual(new[] { Expect(level, [], DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, psu.BlockId, DiagramRequirementField.Routing) },
            afterEdit.CurrentChanges.Select(Key).ToArray(), "Only the edit made after the choice.");
        Assert.IsEmpty(afterEdit.ChangedOnBothSides);

        // A second proposal refines a member of the chosen supply's grouped connection: the connection, the member and the member's
        // own text are named, each at its exact place.
        var input = RecursiveBlockRefinementInputTests.Input(chosen) with { BlockPath = [chosen.SelectedRoot, f.Proposal.Candidate], Attachments = [] };
        chosen = chosen.WithRefinementInput(input);
        var candidate = chosen.Inspect(f.Proposal.Candidate);
        var refinedMember = new ProposedConnection(psu.BlockId, new(member.Selection.ConnectionId, Guid.NewGuid(), Guid.NewGuid()), member.Selection,
            "Second pass", member.Name, member.Kind, Guid.NewGuid(), member.Requirements with { General = "Report power status and faults." },
            member.Endpoints, [], Guid.NewGuid(), Guid.NewGuid());
        var relink = new ProposedConnection(psu.BlockId, new(link.Selection.ConnectionId, Guid.NewGuid(), Guid.NewGuid()), link.Selection,
            "Second pass", link.Name, link.Kind, Guid.NewGuid(), link.Requirements, link.Endpoints, [refinedMember.Selection], Guid.NewGuid(), Guid.NewGuid());
        var second = new BlockProposal(Guid.NewGuid(), input.Id, input.BlockPath, new(psu.BlockId, Guid.NewGuid(), Guid.NewGuid()),
            [new(new(psu.BlockId, Guid.NewGuid(), Guid.NewGuid()), f.Proposal.Candidate, "Second pass", candidate.Name, Guid.NewGuid(),
                chosen.Requirements(f.Proposal.Candidate).Requirements, candidate.Children, candidate.LocalDiagram with { Connections = [relink.Selection] },
                candidate.Definition, ForkRevisionId: Guid.NewGuid(), ForkRequirementRevisionId: Guid.NewGuid())],
            [relink, refinedMember], [], RecursiveBlockFixture.Origin("Compatible agent fixture"));
        second = second with { Candidate = second.Blocks[0].Selection };
        var memberComparison = BlockProposalComparer.Request(chosen, second);
        Assert.IsFalse(memberComparison.Stale); Assert.IsEmpty(memberComparison.CurrentChanges);
        Guid[] path = [link.Selection.ConnectionId];
        CollectionAssert.AreEqual(new[]
        {
            Expect(level, [], DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Changed, link.Selection.ConnectionId),
            Expect(level, path, DiagramHistoryChangeCategory.Connection, DiagramHistoryChangeKind.Changed, member.Selection.ConnectionId),
            Expect(level, [.. path, member.Selection.ConnectionId], DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed,
                member.Selection.ConnectionId, DiagramRequirementField.General)
        }, memberComparison.ProposalChanges.Select(Key).ToArray(), "The refined member of the grouped connection.");
        Assert.AreEqual(member.Selection.RevisionId, memberComparison.ProposalChanges[1].BeforeRevisionId);
        Assert.AreEqual(refinedMember.Selection.RevisionId, memberComparison.ProposalChanges[1].AfterRevisionId);
        // Once that later proposal, made from the chosen candidate, is chosen too, the first one stays adopted: today's side is exactly
        // what the later proposal changed, and nothing is changed on both sides.
        var secondChosen = Publish(chosen, second);
        secondChosen = secondChosen.Select(secondChosen.SelectedRoot, [secondChosen.SelectedRoot, f.Proposal.Candidate], second.Candidate, [Guid.NewGuid()],
            second.Origin).Graph;
        var firstAfterSecond = BlockProposalComparer.Published(secondChosen, f.Proposal.Id);
        Assert.IsTrue(firstAfterSecond.CandidateAdopted); Assert.IsFalse(firstAfterSecond.CandidateSelected); Assert.IsFalse(firstAfterSecond.Stale);
        CollectionAssert.AreEqual(memberComparison.ProposalChanges.Select(Key).ToArray(), firstAfterSecond.CurrentChanges.Select(Key).ToArray());
        Assert.IsEmpty(firstAfterSecond.ChangedOnBothSides);

        // A target no longer in today's design is reported as removed, and the proposal is stale.
        var plain = RecursiveBlockFixture.Create(); var tree = plain.Graph; var plainPsu = plain.Selected["PSU"];
        var plainInput = RecursiveBlockRefinementInputTests.Input(tree) with { BlockPath = [tree.SelectedRoot, plainPsu], Attachments = [] };
        tree = tree.WithRefinementInput(plainInput);
        var original = tree.Inspect(plainPsu);
        var rewrite = new BlockProposal(Guid.NewGuid(), plainInput.Id, plainInput.BlockPath, new(plainPsu.BlockId, Guid.NewGuid(), Guid.NewGuid()),
            [new(new(plainPsu.BlockId, Guid.NewGuid(), Guid.NewGuid()), plainPsu, "Rewritten", original.Name, Guid.NewGuid(),
                new("Test-only rewritten supply.", "", ""), original.Children, original.LocalDiagram, ForkRevisionId: Guid.NewGuid(), ForkRequirementRevisionId: Guid.NewGuid())],
            [], [], RecursiveBlockFixture.Origin("Compatible agent fixture"));
        rewrite = rewrite with { Candidate = rewrite.Blocks[0].Selection };
        var rootDraft = tree.StartDraft(tree.SelectedRoot);
        var withoutPsu = tree.SaveDraft(tree.SelectedRoot, [tree.SelectedRoot], rootDraft with { Children = [plain.Selected["CPU"]] },
            Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
        var removed = BlockProposalComparer.Request(withoutPsu, rewrite);
        Assert.IsTrue(removed.Stale); Assert.IsEmpty(removed.CurrentPath);
        CollectionAssert.AreEqual(new[] { Expect([tree.SelectedRoot.BlockId], [], DiagramHistoryChangeCategory.Block, DiagramHistoryChangeKind.Removed, plainPsu.BlockId) },
            removed.CurrentChanges.Select(Key).ToArray());
        Assert.AreEqual(plainPsu.RevisionId, removed.CurrentChanges[0].BeforeRevisionId);
        CollectionAssert.AreEqual(new[] { Expect([plainPsu.BlockId], [], DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, plainPsu.BlockId,
            DiagramRequirementField.General), Expect([plainPsu.BlockId], [], DiagramHistoryChangeCategory.Requirement, DiagramHistoryChangeKind.Changed, plainPsu.BlockId,
            DiagramRequirementField.Schematic) }, removed.ProposalChanges.Select(Key).ToArray());

        // A proposal for the power stage inside the supply. Today's supply is edited while the agent works, so the path the agent read
        // (through the older supply revision) is outdated although the stage itself is unchanged: choosing over it is refused as stale
        // and the comparison gives today's path, over which the choice succeeds.
        static RecursiveBlockGraph Publish(RecursiveBlockGraph graph, BlockProposal proposal)
        {
            var prepared = BlockProposalCompiler.Prepare(graph, proposal);
            return prepared.Graph.WithProposal(new(proposal.Id, proposal.InputId, BlockProposalFiles.Fingerprint(proposal), proposal.BasePath,
                proposal.Candidate, proposal.Issues, prepared.Graph.Inspect(proposal.Candidate).Origin));
        }
        var stage = plain.Selected["Power stage"];
        var stageInput = RecursiveBlockRefinementInputTests.Input(tree) with { BlockPath = [tree.SelectedRoot, plainPsu, stage], Attachments = [] };
        tree = tree.WithRefinementInput(stageInput);
        var stageProposal = CreateFor(tree, stageInput);
        var withStageProposal = Publish(tree, stageProposal);
        var supplyDraft = withStageProposal.StartDraft(plainPsu);
        var supplyEdited = withStageProposal.SaveDraft(withStageProposal.SelectedRoot, [withStageProposal.SelectedRoot, plainPsu], supplyDraft with
            { Requirements = supplyDraft.Requirements.Edit(DiagramRequirementField.General, "Test-only supply edited while the agent worked.") },
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        var supplyToday = supplyEdited.Inspect(supplyEdited.SelectedRoot).Children.Single(c => c.BlockId == plainPsu.BlockId);
        Assert.AreEqual(stage, supplyEdited.Inspect(supplyToday).Children[0], "The stage itself is unchanged.");
        var agent = RecursiveBlockFixture.Origin("Compatible agent fixture");
        Assert.AreEqual("stale_block_revision", Assert.ThrowsExactly<AutomationException>(() => BlockProposalCompiler.Select(supplyEdited, stageProposal.Id,
            supplyEdited.SelectedRoot, [supplyEdited.SelectedRoot, plainPsu, stage], [Guid.NewGuid(), Guid.NewGuid()], agent)).Code);
        Assert.AreEqual("stale_root_revision", Assert.ThrowsExactly<AutomationException>(() => BlockProposalCompiler.Select(supplyEdited, stageProposal.Id,
            supplyEdited.SelectedRoot, [withStageProposal.SelectedRoot, plainPsu, stage], [Guid.NewGuid(), Guid.NewGuid()], agent)).Code);
        // A path that is not a saved path of the blocks on today's path is not outdated but invalid: one that skips the supply, one
        // that starts at the supply instead of the root, and one through a supply revision the diagram does not have.
        foreach (var (malformed, what) in new (ImmutableArray<BlockSelection> Path, string What)[]
        {
            ([supplyEdited.SelectedRoot, stage], "skips the supply"), ([supplyToday, stage], "starts at the supply"),
            ([supplyEdited.SelectedRoot, plainPsu with { RevisionId = Guid.NewGuid() }, stage], "names a supply revision the diagram does not have")
        })
            Assert.AreEqual("invalid_recursive_block_graph", Assert.ThrowsExactly<AutomationException>(() => BlockProposalCompiler.Select(supplyEdited,
                stageProposal.Id, supplyEdited.SelectedRoot, malformed, [.. malformed.Skip(1).Select(_ => Guid.NewGuid())], agent)).Code, what);
        var stageToday = BlockProposalComparer.Published(supplyEdited, stageProposal.Id);
        Assert.IsFalse(stageToday.Stale); Assert.IsFalse(stageToday.CandidateAdopted); Assert.IsEmpty(stageToday.CurrentChanges);
        CollectionAssert.AreEqual(new[] { supplyEdited.SelectedRoot, supplyToday, stage }, stageToday.CurrentPath.ToArray());
        var stageChosen = BlockProposalCompiler.Select(supplyEdited, stageProposal.Id, supplyEdited.SelectedRoot, stageToday.CurrentPath,
            [Guid.NewGuid(), Guid.NewGuid()], agent);
        Assert.IsTrue(stageChosen.Changed);
        Assert.IsTrue(BlockProposalComparer.Published(stageChosen.Graph, stageProposal.Id).CandidateSelected);
        // Removed from the supply, the stage is reported removed at the supply's level; with the supply removed too, at the root's.
        var stageDraft = withStageProposal.StartDraft(plainPsu);
        var withoutStage = withStageProposal.SaveDraft(withStageProposal.SelectedRoot, [withStageProposal.SelectedRoot, plainPsu],
            stageDraft with { Children = [plain.Selected["Telemetry"]] }, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        var stageRemoved = BlockProposalComparer.Published(withoutStage, stageProposal.Id);
        Assert.IsTrue(stageRemoved.Stale); Assert.IsEmpty(stageRemoved.CurrentPath);
        CollectionAssert.AreEqual(new[] { Expect([tree.SelectedRoot.BlockId, plainPsu.BlockId], [], DiagramHistoryChangeCategory.Block, DiagramHistoryChangeKind.Removed,
            stage.BlockId) }, stageRemoved.CurrentChanges.Select(Key).ToArray(), "The supply's level, by today's path to it.");
        var systemDraft = withStageProposal.StartDraft(withStageProposal.SelectedRoot);
        var withoutSupply = withStageProposal.SaveDraft(withStageProposal.SelectedRoot, [withStageProposal.SelectedRoot],
            systemDraft with { Children = [plain.Selected["CPU"]] }, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
        CollectionAssert.AreEqual(new[] { Expect([tree.SelectedRoot.BlockId], [], DiagramHistoryChangeCategory.Block, DiagramHistoryChangeKind.Removed, stage.BlockId) },
            BlockProposalComparer.Published(withoutSupply, stageProposal.Id).CurrentChanges.Select(Key).ToArray(), "The root, the deepest level still in the design.");
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
