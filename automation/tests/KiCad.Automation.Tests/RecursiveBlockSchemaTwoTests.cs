using System.Collections.Immutable;
using System.Text.Json;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Contract tests for the schema 2 model invariants (contract rbg-v2 sections 3 and 4: PV1-PV6,
// IR1-IR4, IC1-IC7, G1-G3 and rule U). These rules are pure model logic with many independent
// failure classes, so they are exercised directly; the helper protocol tests in
// RecursiveEditorFileCommandTests and the native recursive-editor journey carry the same behaviour
// through the real process boundaries.
[TestClass]
public sealed class RecursiveBlockSchemaTwoTests
{
    private static readonly RequirementRevisionOrigin Origin = RecursiveBlockFixture.Origin();

    private static string Code(Action action) => Assert.ThrowsExactly<AutomationException>(action).Code;

    [TestMethod]
    public void PresentationDecimalsAreExactCanonicalDiagramUnits()
    {
        Assert.AreEqual("12.5", DiagramCoordinates.Format(12.500m));
        Assert.AreEqual("0", DiagramCoordinates.Format(-0.000m));
        Assert.AreEqual("-0.25", DiagramCoordinates.Format(-0.25m));
        Assert.AreEqual("1000000000", DiagramCoordinates.Format(1_000_000_000m));
        Assert.AreEqual(12.5m, DiagramCoordinates.ParseProtocol("12.500000"), "C++ std::to_string text is canonicalized.");
        Assert.AreEqual(0m, DiagramCoordinates.ParseProtocol("-0.000"));
        Assert.AreEqual(7m, DiagramCoordinates.ParseProtocol("+0007"));
        Assert.AreEqual("12.5", DiagramCoordinates.Format(DiagramCoordinates.ParseProtocol("12.500000")));
        Assert.AreEqual(-101.6m, DiagramCoordinates.ParseCanonical("-101.6"));
        foreach (string text in new[] { "1.2345", "1e3", "1.", ".5", "", "1,5", "0.00000000000000000000000000001", "10000000001", "NaN" })
            Assert.AreEqual("invalid_diagram_coordinate", Code(() => DiagramCoordinates.ParseProtocol(text)), text);
        foreach (string text in new[] { "12.500", "-0", "+1", "01", "1.0", "0.0001", "1e3" })
            Assert.AreEqual("invalid_diagram_coordinate", Code(() => DiagramCoordinates.ParseCanonical(text)), text);
        Assert.AreEqual("invalid_diagram_coordinate", Code(() => DiagramCoordinates.Format(0.0005m)));
        Assert.AreEqual("invalid_diagram_coordinate", Code(() => DiagramCoordinates.Check(1_000_000_000.001m)));
    }

    [TestMethod]
    public void LayoutRulesRejectInvalidGeometryDuplicateKeysAndPortsWithoutARectangle()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), scope = Guid.NewGuid(), port = Guid.NewGuid(), link = Guid.NewGuid();
        var placed = new DiagramBlockPlacement(a, new(0, 0, 100, 50));
        DiagramPresentationView View(ImmutableArray<DiagramBlockPlacement> blocks, ImmutableArray<DiagramPortPlacement> ports = default,
            ImmutableArray<DiagramConnectionRoute> routes = default, DiagramRect? frame = null) =>
            new(blocks, ports.IsDefault ? [] : ports, routes.IsDefault ? [] : routes, frame);
        View([placed], [new(a, port, DiagramPortSide.Left, 50), new(a, b, DiagramPortSide.Top, 100)],
            [new(link, 1, [new(1, 2)], new(3, 4), true)]).Validate(scope);
        Assert.AreEqual("invalid_diagram_coordinate", Code(() => View([placed with { Rect = new(0, 0, 0, 5) }]).Validate()));
        Assert.AreEqual("invalid_diagram_coordinate", Code(() => View([placed with { Rect = new(999_999_999, 0, 2, 5) }]).Validate()));
        Assert.AreEqual("invalid_diagram_coordinate", Code(() => View([placed with { Rect = new(0.0001m, 0, 2, 5) }]).Validate()));
        Assert.AreEqual("invalid_diagram_coordinate", Code(() => View([placed with { FillRgb = 0x1000000 }]).Validate()));
        Assert.AreEqual("invalid_presentation_view", Code(() => View([placed, placed with { Rect = new(1, 1, 1, 1) }]).Validate()));
        Assert.AreEqual("invalid_presentation_view", Code(() => View([], [new(a, port, DiagramPortSide.Left, 0)]).Validate()));
        Assert.AreEqual("invalid_presentation_view", Code(() => View([placed], [new(scope, port, DiagramPortSide.Left, 0)]).Validate(scope)),
            "A port on the level's own boundary sits on the frame.");
        View([placed], [new(scope, port, DiagramPortSide.Left, 0)], frame: new(0, 0, 400, 300)).Validate(scope);
        Assert.AreEqual("invalid_diagram_coordinate", Code(() => View([placed], [new(a, port, DiagramPortSide.Left, 51)]).Validate()),
            "A left port offset runs along the height.");
        Assert.AreEqual("invalid_diagram_coordinate", Code(() => View([placed], [new(a, port, DiagramPortSide.Bottom, -1)]).Validate()));
        Assert.AreEqual("invalid_presentation_view", Code(() => View([placed], [new(a, port, DiagramPortSide.Left, 1), new(a, port, DiagramPortSide.Right, 1)]).Validate()));
        Assert.AreEqual("invalid_presentation_view", Code(() => View([placed], [new(a, port, (DiagramPortSide)9, 1)]).Validate()));
        Assert.AreEqual("invalid_presentation_view", Code(() => View([], routes: [new(link, 0, [], null)]).Validate()));
        Assert.AreEqual("invalid_presentation_view", Code(() => View([], routes: [new(link, 1, [], null), new(link, 1, [new(0, 0)], null)]).Validate()));
        Assert.AreEqual("invalid_presentation_view", Code(() => View([], routes: [new(link, 1, [.. Enumerable.Range(0, 257).Select(i => new DiagramPoint(i, 0))], null)]).Validate()));
        View([], routes: [new(link, 1, [.. Enumerable.Range(0, 256).Select(i => new DiagramPoint(i, 0))], null)]).Validate();
        // Port and route order is not significant; block order is z-order and is.
        var ordered = View([placed, new(b, new(1, 1, 1, 1))], [new(a, port, DiagramPortSide.Left, 1), new(a, b, DiagramPortSide.Left, 2)]);
        var shuffled = View([placed, new(b, new(1, 1, 1, 1))], [new(a, b, DiagramPortSide.Left, 2), new(a, port, DiagramPortSide.Left, 1)]);
        Assert.IsTrue(ordered.SameContents(shuffled));
        Assert.IsFalse(ordered.SameContents(ordered with { Blocks = [.. ordered.Blocks.Reverse()] }));
        Assert.IsTrue(DiagramPresentationView.Same(null, DiagramPresentationView.Empty));
    }

    [TestMethod]
    public void LayoutIsRevisionBoundDormantEntriesAreKeptVerbatimAndPrunedOnlyByAChangedSave()
    {
        var f = SchemaTwoFixture.Create(); var graph = f.Graph; var psu = f.Linked.Blocks["PSU"];
        Assert.AreEqual(1, graph.DormantPresentationCount(psu), "The Regulator placement names a block that is not PSU's child.");
        Assert.HasCount(2, graph.ActivePresentation(psu).Blocks);
        var root = graph.SelectedRoot;
        // A save that only adds dormant entries is unchanged: nothing is written.
        var draft = graph.StartDraft(psu);
        var dormantOnly = draft with { Diagram = draft.LocalDiagram with { Presentation = draft.LocalDiagram.Layout with {
            Blocks = [.. draft.LocalDiagram.Layout.Blocks, new(Guid.NewGuid(), new(1, 1, 1, 1))] } } };
        var unchanged = graph.SaveDraft(root, [root, psu], dormantOnly, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Origin);
        Assert.IsFalse(unchanged.Changed); Assert.AreSame(graph, unchanged.Graph);
        // A changed save prunes dormant entries from the new revision only.
        var moved = draft with { Diagram = draft.LocalDiagram with { Presentation = draft.LocalDiagram.Layout with {
            Blocks = draft.LocalDiagram.Layout.Blocks.SetItem(0, draft.LocalDiagram.Layout.Blocks[0] with { Rect = new(120, 101.6m, 203.2m, 152.4m) }) } } };
        var saved = graph.SaveDraft(root, [root, psu], moved, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Origin);
        Assert.IsTrue(saved.Changed); Assert.AreEqual(1, saved.PrunedPresentationEntries);
        var newPsu = saved.Graph.Inspect(saved.Graph.SelectedRoot).Children[0];
        Assert.AreEqual(0, saved.Graph.DormantPresentationCount(newPsu));
        Assert.AreEqual(120m, saved.Graph.Inspect(newPsu).LocalDiagram.Layout.Blocks[0].Rect.X);
        Assert.AreEqual(1, saved.Graph.DormantPresentationCount(psu), "Old revisions keep their exact layout.");
        // PV5: ancestor snapshots copy their own layout verbatim, dormant entries included.
        Assert.IsTrue(graph.Inspect(root).LocalDiagram.Layout.SameContents(saved.Graph.Inspect(saved.Graph.SelectedRoot).LocalDiagram.Layout));
        var history = DiagramHistoryQuery.Read(saved.Graph, newPsu);
        Assert.IsTrue(history.Entries[0].LayoutOnly); Assert.IsFalse(history.Entries[1].LayoutOnly);
        var compared = DiagramHistoryQuery.Compare(saved.Graph, newPsu, psu);
        Assert.AreEqual(DiagramHistoryChangeCategory.Layout, compared.Changes.Single().Category);
        Assert.AreEqual(f.Linked.Blocks["Power stage"].BlockId, compared.Changes.Single().ObjectId);
        Assert.AreEqual(DiagramHistoryChangeKind.Changed, compared.Changes.Single().Kind);
    }

    [TestMethod]
    public void LevelLayoutCannotPlaceTheLevelItselfAndEmptyInteriorForksKeepOnlyTheBoundary()
    {
        var f = SchemaTwoFixture.Create(); var graph = f.Graph; var psu = f.Linked.Blocks["PSU"];
        var draft = graph.StartDraft(psu);
        var self = draft with { Diagram = draft.LocalDiagram with { Presentation = draft.LocalDiagram.Layout with {
            Blocks = [.. draft.LocalDiagram.Layout.Blocks, new(psu.BlockId, new(0, 0, 10, 10))] } } };
        Assert.AreEqual("invalid_presentation_view", Code(() => graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, psu], self,
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Origin)));
        var fork = graph.ForkImplementation(psu, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Empty interior", Origin, emptyInterior: true);
        var forked = fork.Revisions[^1].LocalDiagram;
        Assert.AreEqual(new DiagramRect(0, 0, 1200, 800), forked.Layout.Frame);
        Assert.IsEmpty(forked.Layout.Blocks); Assert.IsEmpty(forked.Layout.Routes);
        Assert.AreEqual(psu.BlockId, forked.Layout.Ports.Single().BlockId, "Only the boundary port stays.");
        Assert.IsEmpty(forked.Realizations);
        Assert.IsTrue(forked.Interfaces.SequenceEqual(graph.Inspect(psu).LocalDiagram.Interfaces));
        var copy = graph.ForkImplementation(psu, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Full copy", Origin);
        Assert.IsTrue(copy.Revisions[^1].LocalDiagram.SameContents(graph.Inspect(psu).LocalDiagram), "A full fork copies the layout verbatim.");
    }

    [TestMethod]
    public void InterfaceRealizationsStateExactTargetsOfTheSameRevision()
    {
        var f = SchemaTwoFixture.Create(); var graph = f.Graph; var psu = f.Linked.Blocks["PSU"]; var root = graph.SelectedRoot;
        var power = f.Linked.Blocks["Power stage"]; Guid port = f.Linked.Ports["PSU/Power"];
        string Save(InterfaceRealization record, BlockSelection? owner = null)
        {
            var target = owner ?? psu; var draft = graph.StartDraft(target);
            draft = draft with { Diagram = draft.LocalDiagram with { InterfaceRealizations = [record] } };
            ImmutableArray<BlockSelection> path = target == psu ? [root, psu] : [root, psu, target];
            return Code(() => graph.SaveDraft(root, path, draft, Guid.NewGuid(), Guid.NewGuid(), [.. path.Skip(1).Select(_ => Guid.NewGuid())], Origin));
        }
        var child = InterfaceRealizationTarget.ChildInterface(power.BlockId, f.Linked.Ports["Power stage/Output"]);
        Assert.AreEqual("invalid_interface_realization", Save(new(port, DiagramRealizationState.Resolved, [], null, [])));
        Assert.AreEqual("invalid_interface_realization", Save(new(port, DiagramRealizationState.Resolved, [child], "Not resolved after all.", [])));
        Assert.AreEqual("invalid_interface_realization", Save(new(port, DiagramRealizationState.Partial, [child], " ", [])));
        Assert.AreEqual("invalid_interface_realization", Save(new(port, DiagramRealizationState.Unknown, [child], "Unknown", [])));
        Assert.AreEqual("invalid_interface_realization", Save(new(port, DiagramRealizationState.Resolved, [child, child], null, [])));
        Assert.AreEqual("invalid_interface_realization", Save(new(port, DiagramRealizationState.Resolved, [child with { ConnectionId = Guid.NewGuid() }], null, [])));
        Assert.AreEqual("invalid_interface_realization", Save(new(Guid.NewGuid(), DiagramRealizationState.Resolved, [child], null, [])),
            "IR1: only the block's own interfaces are realized.");
        Assert.AreEqual("invalid_interface_realization", Save(new(port, DiagramRealizationState.Resolved,
            [InterfaceRealizationTarget.ChildInterface(f.Linked.Blocks["Regulator"].BlockId, Guid.NewGuid())], null, [])), "IR2: a grandchild is not a direct child.");
        Assert.AreEqual("invalid_interface_realization", Save(new(port, DiagramRealizationState.Resolved,
            [InterfaceRealizationTarget.LocalConnection(f.Linked.Links["CPU/Memory"].ConnectionId)], null, [])), "IR2: a connection of another level.");
        Assert.AreEqual("invalid_interface_realization", Save(new(port, DiagramRealizationState.Resolved,
            [InterfaceRealizationTarget.ExactPin(f.PowerStagePin)], null, [])), "IR2: the pin's component is bound to Power stage, not PSU.");
        Assert.AreEqual("invalid_interface_realization", Save(new(port, DiagramRealizationState.Resolved, [child], null, [new("", "rev", null, null, null)])));
        Assert.AreEqual("invalid_interface_realization", Save(new(f.Linked.Ports["Power stage/Output"], DiagramRealizationState.Resolved,
            [InterfaceRealizationTarget.ExactPin(f.PowerStagePin with { ComponentId = Guid.NewGuid() })], null, []), power));
        // A record that lists targets in another order is the same record.
        var draft = graph.StartDraft(psu);
        var reordered = draft with { Diagram = draft.LocalDiagram with { InterfaceRealizations = [draft.LocalDiagram.Realizations[0] with {
            Targets = [.. draft.LocalDiagram.Realizations[0].Targets.Reverse()] }] } };
        Assert.IsFalse(graph.SaveDraft(root, [root, psu], reordered, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Origin).Changed);
    }

    [TestMethod]
    public void RemovingAnInterfaceStillUsedAtTheParentLevelFailsWithEachUseInTheDetails()
    {
        var f = SchemaTwoFixture.Create(); var graph = f.Graph; var root = graph.SelectedRoot;
        var psu = f.Linked.Blocks["PSU"]; var power = f.Linked.Blocks["Power stage"];
        // Power stage/Output is used by PSU's Supply connection and by PSU's realization of its Power interface.
        var draft = graph.StartDraft(power);
        draft = draft with { Diagram = draft.LocalDiagram with { Interfaces = [], InterfaceRealizations = [] } };
        var error = Assert.ThrowsExactly<AutomationException>(() => graph.SaveDraft(root, [root, psu, power], draft,
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], Origin));
        Assert.AreEqual("boundary_interface_in_use", error.Code);
        var details = error.Details;
        Assert.HasCount(2, details);
        Assert.IsTrue(details.All(d => d.ScopeBlockId == psu.BlockId));
        CollectionAssert.AreEquivalent(new[] { "connection", "interface_realization" }, details.Select(d => d.Kind).ToArray());
        CollectionAssert.AreEquivalent(new Guid?[] { f.Linked.Links["PSU/Supply"].ConnectionId, f.Linked.Ports["PSU/Power"] }, details.Select(d => d.ObjectId).ToArray());
        Assert.AreEqual(graph.SelectedRoot, root, "Nothing changed.");
        // Choosing an implementation without an interface the parent uses fails the same way.
        var cpu = f.Linked.Blocks["CPU"];
        var alternative = graph.States.Single(s => s.BlockId == cpu.BlockId && s.Id != cpu.StateId);
        var choice = new BlockSelection(cpu.BlockId, alternative.Id, alternative.HeadRevisionId);
        var bare = new RecursiveBlockGraph(graph.DocumentId, graph.SelectedRoot, graph.States,
            graph.Revisions.Select(r => r.Selection == choice ? r with { Diagram = BlockLocalDiagram.Empty } : r), graph.RequirementHistories, graph.ConnectionArchives);
        var selection = Assert.ThrowsExactly<AutomationException>(() => bare.Select(root, [root, cpu], choice, [Guid.NewGuid()], Origin));
        Assert.AreEqual("boundary_interface_in_use", selection.Code);
        Assert.IsTrue(selection.Details.Any(d => d.ObjectId == f.Linked.Links["System/Power"].ConnectionId && d.ScopeBlockId == root.BlockId));
    }

    [TestMethod]
    public void InterconnectRealizationsFollowTheirSegmentKindsStatesJoinsAndCircuits()
    {
        Guid design = Guid.NewGuid(), circuit = Guid.NewGuid(), component = Guid.NewGuid();
        InterconnectSegment Segment(InterconnectSegmentKind kind, Guid? designId = null, Guid? circuitId = null, Guid? net = null, Guid? part = null,
            ImmutableArray<DiagramPinTarget> pins = default, Guid? target = null, Guid? hardware = null, string? label = null, string? reference = null,
            string? path = null, string? reason = null, Guid? id = null) =>
            new(id ?? Guid.NewGuid(), kind, label, designId, circuitId, net, part, pins.IsDefault ? [] : pins, target, hardware, reference, path, reason);
        string Check(InterconnectSegment segment, DiagramRealizationState state = DiagramRealizationState.Resolved, string? reason = null) =>
            Code(() => new InterconnectRealization(state, [segment], [], reason, []).Validate());
        var pin = new DiagramPinTarget(design, component, [Guid.NewGuid()], "1");
        new InterconnectRealization(DiagramRealizationState.Resolved, [Segment(InterconnectSegmentKind.Connector, design, circuit, part: component, pins: [pin])], [], null, []).Validate();
        Assert.AreEqual("invalid_interconnect_realization", Check(Segment(InterconnectSegmentKind.BoardNet, design, circuit)), "A board net needs its net.");
        new InterconnectRealization(DiagramRealizationState.Partial, [Segment(InterconnectSegmentKind.BoardNet, design, circuit, reason: "Net not chosen.")],
            [], "The net is open.", []).Validate();
        Assert.AreEqual("invalid_interconnect_realization", Check(Segment(InterconnectSegmentKind.BoardNet, design, circuit, reason: "Net not chosen."),
            DiagramRealizationState.Resolved), "Resolved never hides an incomplete segment.");
        Assert.AreEqual("invalid_interconnect_realization", Check(Segment(InterconnectSegmentKind.Connector, design, circuit, part: component)), "A connector names its pins.");
        Assert.AreEqual("invalid_interconnect_realization", Check(Segment(InterconnectSegmentKind.Connector, design, circuit, part: component,
            pins: [pin with { ComponentId = Guid.NewGuid() }])), "Pins belong to the connector.");
        Assert.AreEqual("invalid_interconnect_realization", Check(Segment(InterconnectSegmentKind.Connector, design, circuit, part: component, pins: [pin, pin])));
        Assert.AreEqual("invalid_interconnect_realization", Check(Segment(InterconnectSegmentKind.Harness, design, target: Guid.NewGuid())), "Unused fields stay empty.");
        Assert.AreEqual("invalid_interconnect_realization", Check(Segment(InterconnectSegmentKind.BoardNet, design, circuit, Guid.NewGuid(), pins: [pin])));
        Assert.AreEqual("invalid_interconnect_realization", Check(Segment(InterconnectSegmentKind.External, label: "Bench")), "External needs a reference or path.");
        Assert.AreEqual("invalid_interconnect_realization", Check(Segment(InterconnectSegmentKind.External, label: "Bench", path: "../outside.md")));
        Assert.AreEqual("invalid_interconnect_realization", Check(Segment(InterconnectSegmentKind.External, label: " ", reference: "R")));
        Assert.AreEqual("invalid_interconnect_realization", Check(Segment((InterconnectSegmentKind)9, reason: "Unknown kind.")));
        Assert.AreEqual("invalid_interconnect_realization", Code(() => new InterconnectRealization(DiagramRealizationState.Unknown,
            [Segment(InterconnectSegmentKind.Harness, target: Guid.NewGuid())], [], "Not known.", []).Validate()), "Unknown has no segments.");
        Assert.AreEqual("invalid_interconnect_realization", Code(() => new InterconnectRealization(DiagramRealizationState.Unknown, [], [], null, []).Validate()));
        Assert.AreEqual("invalid_interconnect_realization", Code(() => new InterconnectRealization(DiagramRealizationState.Partial,
            [Segment(InterconnectSegmentKind.Harness, target: Guid.NewGuid())], [], null, []).Validate()), "Partial says what remains.");
        var first = Segment(InterconnectSegmentKind.Harness, target: Guid.NewGuid()); var second = Segment(InterconnectSegmentKind.HardwareInterface, hardware: Guid.NewGuid());
        foreach (var joins in new ImmutableArray<InterconnectJoin>[] { [new(first.Id, first.Id)], [new(first.Id, Guid.NewGuid())],
            [new(first.Id, second.Id), new(second.Id, first.Id)] })
            Assert.AreEqual("invalid_interconnect_realization", Code(() => new InterconnectRealization(DiagramRealizationState.Resolved, [first, second], joins, null, []).Validate()));
        Assert.AreEqual("invalid_interconnect_realization", Code(() => new InterconnectRealization(DiagramRealizationState.Resolved, [first, first], [], null, []).Validate()));
        Assert.AreEqual("ambiguous_block_circuit", Code(() => new InterconnectRealization(DiagramRealizationState.Resolved,
            [Segment(InterconnectSegmentKind.BoardNet, design, circuit, Guid.NewGuid()), Segment(InterconnectSegmentKind.BoardNet, design, Guid.NewGuid(), Guid.NewGuid())],
            [], null, []).Validate()), "IC5: one circuit per design.");
    }

    [TestMethod]
    public void InterconnectRealizationsAgreeWithTheOwningBlockAndKeepTheirIdentities()
    {
        var f = SchemaTwoFixture.Create(); var graph = f.Graph; var root = graph.SelectedRoot;
        var archive = graph.Connections(root.BlockId); var link = f.Linked.Links["System/Power"]; var revision = archive.Inspect(link);
        RecursiveBlockGraph Publish(InterconnectRealization realization, BlockPhysicalAllocation? allocation = null, ImmutableArray<ComponentRealization> bindings = default)
        {
            var draft = archive.StartDraft(link) with { Realization = realization };
            var saved = graph.SaveConnectionDraft(root, [root], [link], draft, Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [], Origin).Graph;
            if (allocation is null && bindings.IsDefault) return saved;
            var block = saved.StartDraft(saved.SelectedRoot);
            block = block with { PhysicalAllocation = allocation ?? block.PhysicalAllocation,
                ComponentBindings = bindings.IsDefault ? block.ComponentBindings : new(bindings) };
            return saved.SaveDraft(saved.SelectedRoot, [saved.SelectedRoot], block, Guid.NewGuid(), Guid.NewGuid(), [], Origin).Graph;
        }
        var realization = revision.Realization!;
        // IC6: a harness segment names a Harness or Assembly target of the owning block; dropping it later is refused.
        var harness = realization.SegmentList.Single(s => s.Kind == InterconnectSegmentKind.Harness);
        Assert.AreEqual("invalid_interconnect_realization", Code(() => Publish(realization with { Segments =
            realization.Segments.Replace(harness, harness with { PhysicalTargetId = Guid.NewGuid() }) })));
        Assert.AreEqual("physical_target_in_use", Code(() => Publish(realization with { UnresolvedReason = null },
            BlockPhysicalAllocation.Unknown("Harness not yet chosen."))));
        Publish(realization, new(PhysicalAllocationState.Resolved, [new(f.Harness, PhysicalAllocationKind.Assembly, "Supply assembly")]));
        // IC5: the realization agrees with the circuits the owning block binds.
        Assert.AreEqual("ambiguous_block_circuit", Code(() => Publish(realization, bindings: [new(f.Design, Guid.NewGuid(), Guid.NewGuid())])));
        Publish(realization, bindings: [new(f.Design, f.Circuit, Guid.NewGuid())]);
        // G1: a segment identity stays with one connection occurrence and never aliases another identity.
        var telemetry = f.Linked.Links["System/Telemetry"];
        var borrowed = archive.StartDraft(telemetry) with { Realization = new(DiagramRealizationState.Partial, [harness], [], "Only the harness is known.", []) };
        Assert.AreEqual("invalid_interconnect_realization", Code(() => graph.SaveConnectionDraft(root, [root], [telemetry], borrowed,
            Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [], Origin)));
        var aliasing = archive.StartDraft(telemetry) with { Realization = new(DiagramRealizationState.Partial,
            [harness with { Id = root.RevisionId }], [], "Only the harness is known.", []) };
        Assert.AreEqual("invalid_interconnect_realization", Code(() => graph.SaveConnectionDraft(root, [root], [telemetry], aliasing,
            Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [], Origin)));
        // A later revision of the same connection may keep its segment identities.
        var kept = Publish(realization with { UnresolvedReason = "One more check pending.", State = DiagramRealizationState.Partial });
        var latest = kept.Connections(root.BlockId);
        Assert.IsTrue(realization.SegmentList.All(s => latest.SegmentOwners[s.Id] == link.ConnectionId));
        Assert.AreEqual(2, latest.Revisions.Count(r => r.Selection.ConnectionId == link.ConnectionId && r.Realization is not null));
        // Only the realization changed, so history reports an interconnect realization change.
        var newRoot = kept.SelectedRoot;
        var compare = DiagramHistoryQuery.Compare(kept, newRoot, root);
        Assert.AreEqual(DiagramHistoryChangeCategory.InterconnectRealization, compare.Changes.Single().Category);
        Assert.AreEqual(link.ConnectionId, compare.Changes.Single().ObjectId);
    }

    [TestMethod]
    public void ConnectionAndInterfaceDomainsAndDirectionsAreSavedComparedAndRetained()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var root = graph.SelectedRoot;
        var archive = graph.Connections(root.BlockId); var link = f.Links["System/Power"];
        var draft = archive.StartDraft(link) with { Direction = DiagramConnectionDirection.FromFirst };
        var saved = graph.SaveConnectionDraft(root, [root], [link], draft, Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [], Origin);
        Assert.IsTrue(saved.Changed, "Direction alone is a change.");
        var newLink = saved.Graph.Inspect(saved.Graph.SelectedRoot).LocalDiagram.Connections[0];
        Assert.AreEqual(DiagramConnectionDirection.FromFirst, saved.Graph.Connections(root.BlockId).Inspect(newLink).Direction);
        Assert.AreEqual(DiagramConnectionDirection.Unspecified, saved.Graph.Connections(root.BlockId).Inspect(link).Direction, "History is kept.");
        Assert.IsFalse(saved.Graph.SaveConnectionDraft(saved.Graph.SelectedRoot, [saved.Graph.SelectedRoot], [newLink],
            saved.Graph.Connections(root.BlockId).StartDraft(newLink), Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [], Origin).Changed);
        Assert.AreEqual("invalid_diagram_connection_archive", Code(() => graph.SaveConnectionDraft(root, [root], [link],
            draft with { Domain = (DiagramDomain)42 }, Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [], Origin)));
        var rewritten = new DiagramConnectionArchive(archive.DocumentId, archive.OwnerBlockId, archive.States,
            archive.Revisions.Select(r => r.Selection == link ? r with { Domain = DiagramDomain.Power } : r), archive.RequirementHistories);
        Assert.IsFalse(rewritten.Retains(archive), "Changing a saved revision's domain rewrites history.");
        var psu = f.Blocks["PSU"]; var block = graph.StartDraft(psu);
        block = block with { Diagram = block.LocalDiagram with { Interfaces = block.LocalDiagram.Interfaces.SetItem(0,
            block.LocalDiagram.Interfaces[0] with { Domain = DiagramDomain.Power, Direction = DiagramInterfaceDirection.Output }) } };
        var interfaceSaved = graph.SaveDraft(root, [root, psu], block, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Origin).Graph;
        var newPsu = interfaceSaved.Inspect(interfaceSaved.SelectedRoot).Children[0];
        var change = DiagramHistoryQuery.Compare(interfaceSaved, newPsu, psu).Changes.Single();
        Assert.AreEqual(DiagramHistoryChangeCategory.Interface, change.Category); Assert.AreEqual(DiagramHistoryChangeKind.Changed, change.Kind);
        Assert.AreEqual("invalid_block_local_diagram", Code(() => graph.SaveDraft(root, [root, psu], block with { Diagram = block.LocalDiagram with {
            Interfaces = [block.LocalDiagram.Interfaces[0] with { Direction = (DiagramInterfaceDirection)7 }] } },
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Origin)));
    }

    [TestMethod]
    public void InterfaceRealizationHistoryAndRecordsReachableFromAgentJsonStayCompatible()
    {
        var f = SchemaTwoFixture.Create(); var graph = f.Graph; var root = graph.SelectedRoot; var cpu = f.Linked.Blocks["CPU"];
        var draft = graph.StartDraft(cpu);
        draft = draft with { Diagram = draft.LocalDiagram with { InterfaceRealizations = [new(f.Linked.Ports["CPU/Telemetry"], DiagramRealizationState.Partial,
            [InterfaceRealizationTarget.LocalConnection(f.Linked.Links["CPU/Control"].ConnectionId)], "Only control is mapped.", [])] } };
        var saved = graph.SaveDraft(root, [root, cpu], draft, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Origin).Graph;
        var newCpu = saved.Inspect(saved.SelectedRoot).Children[1];
        var change = DiagramHistoryQuery.Compare(saved, newCpu, cpu).Changes.Single();
        Assert.AreEqual(DiagramHistoryChangeCategory.InterfaceRealization, change.Category);
        Assert.AreEqual(f.Linked.Ports["CPU/Telemetry"], change.ObjectId); Assert.AreEqual("Telemetry", change.Name);
        Assert.IsFalse(DiagramHistoryQuery.Read(saved, newCpu).Entries[0].LayoutOnly);
        // Version 1 shaped records keep their exact agent-facing JSON (proposal fingerprints do not move).
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string plain = JsonSerializer.Serialize(new BlockLocalDiagram([new(Guid.Empty, "Power", "")], []), options);
        Assert.IsFalse(plain.Contains("domain", StringComparison.Ordinal) || plain.Contains("presentation", StringComparison.Ordinal)
            || plain.Contains("interfaceRealizations", StringComparison.Ordinal) || plain.Contains("layout", StringComparison.Ordinal), plain);
        var local = graph.Inspect(f.Linked.Blocks["PSU"]).LocalDiagram;
        var roundTrip = JsonSerializer.Deserialize<BlockLocalDiagram>(JsonSerializer.Serialize(local, options), options)!;
        Assert.IsTrue(local.SameContents(roundTrip));
        roundTrip.Validate(f.Linked.Blocks["PSU"].BlockId);
    }
}
