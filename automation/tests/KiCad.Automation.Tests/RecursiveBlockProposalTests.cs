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

    // Isolated rules of the stale-proposal comparison (ledger pa48933d0fe0a5c2f). The native journey compares a root proposal with the
    // saved design through the production MCP server, but its fixture changes no nested level on both sides, no member of a refined
    // connection and never takes the target out of the design; those cases are only reachable here.
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
        // Chosen over a refreshed path, today's supply is the candidate.
        var chosen = published.Select(published.SelectedRoot, [published.SelectedRoot, psuNow], f.Proposal.Candidate, [Guid.NewGuid()], f.Proposal.Origin).Graph;
        var afterChoice = BlockProposalComparer.Published(chosen, f.Proposal.Id);
        Assert.IsTrue(afterChoice.CandidateSelected); Assert.IsTrue(afterChoice.Stale);
        CollectionAssert.AreEqual(afterChoice.ProposalChanges.Select(Key).ToArray(), afterChoice.CurrentChanges.Select(Key).ToArray());

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
