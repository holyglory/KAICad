using System.Collections.Immutable;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockPhysicalAllocationTests
{
    private static BlockPhysicalAllocation ResolvedFixture()
    {
        Guid assembly = Guid.NewGuid(), stack = Guid.NewGuid(), board = Guid.NewGuid(), component = Guid.NewGuid();
        return new(PhysicalAllocationState.Resolved,
            [new(assembly, PhysicalAllocationKind.Assembly, "Vehicle controller", "assembly:controller"),
             new(stack, PhysicalAllocationKind.BoardStack, "Controller stack", "stack:controller", ParentId: assembly),
             new(board, PhysicalAllocationKind.Board, "Main controller PCB", "board:main", "boards/main.kicad_pcb", stack),
             new(component, PhysicalAllocationKind.Component, "Thermal processor", "component:processor", ParentId: board)]);
    }

    private static RecursiveBlockGraph Save(RecursiveBlockGraph graph, BlockPhysicalAllocation allocation) => graph.SaveDraft(
        graph.SelectedRoot, [graph.SelectedRoot], graph.StartDraft(graph.SelectedRoot) with { PhysicalAllocation = allocation },
        Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;

    [TestMethod]
    public void RootAndChildPhysicalTargetsRoundTripWithoutChangingFunctionalHierarchy()
    {
        var original = LinkedDiagramFixture.Create().Graph;
        var saved = Save(original, ResolvedFixture());
        var xml = RecursiveBlockGraphXml.Write(saved);
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(xml)));
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(saved))));
        var allocation = saved.Inspect(saved.SelectedRoot).PhysicalAllocation!;
        allocation.Validate(); Assert.AreEqual(PhysicalAllocationState.Resolved, allocation.State);
        Assert.AreEqual(4, allocation.Targets.Length);
        Assert.AreEqual(original.Inspect(original.SelectedRoot).Children, saved.Inspect(saved.SelectedRoot).Children);
        Assert.AreEqual(DiagramHistoryChangeCategory.PhysicalAllocation,
            DiagramHistoryQuery.Compare(saved, saved.SelectedRoot, original.SelectedRoot).Changes.Single().Category);
    }

    [TestMethod]
    public void UnknownAndPartialStatesKeepUnresolvedPhysicalFactsExplicit()
    {
        var unknown = BlockPhysicalAllocation.Unknown("The enclosure and board partition have not been decided.");
        unknown.Validate();
        var partial = new BlockPhysicalAllocation(PhysicalAllocationState.Partial,
            [new(Guid.NewGuid(), PhysicalAllocationKind.Board, "Power board")],
            "Telemetry controller allocation is still unresolved.");
        partial.Validate();
        var graph = Save(LinkedDiagramFixture.Create().Graph, partial);
        Assert.AreEqual(partial.UnknownReason, graph.Inspect(graph.SelectedRoot).PhysicalAllocation!.UnknownReason);
        foreach (var invalid in new BlockPhysicalAllocation[]
        {
            new(PhysicalAllocationState.Unknown, [new(Guid.NewGuid(), PhysicalAllocationKind.Board, "Guessed board")], "not unknown"),
            new(PhysicalAllocationState.Partial, [], "missing target"),
            new(PhysicalAllocationState.Resolved, [new(Guid.NewGuid(), PhysicalAllocationKind.Board, "Board")], "still open"),
        }) Assert.ThrowsExactly<AutomationException>(invalid.Validate);
    }

    [TestMethod]
    public void CyclicAndUnsafePhysicalTargetsAreRejectedWithoutChangingTheGraph()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var cyclic = new BlockPhysicalAllocation(PhysicalAllocationState.Resolved,
            [new(first, PhysicalAllocationKind.Board, "One", ParentId: second), new(second, PhysicalAllocationKind.BoardStack, "Two", ParentId: first)]);
        Assert.ThrowsExactly<AutomationException>(cyclic.Validate);
        var unsafePath = new BlockPhysicalAllocation(PhysicalAllocationState.Resolved,
            [new(Guid.NewGuid(), PhysicalAllocationKind.Board, "Board", RepositoryPath: "/outside/board.kicad_pcb")]);
        Assert.ThrowsExactly<AutomationException>(unsafePath.Validate);
    }

    [TestMethod]
    public void RequirementOnlyRebaseDoesNotSilentlyRebasePhysicalAllocation()
    {
        var original = LinkedDiagramFixture.Create().Graph; var saved = Save(original, ResolvedFixture());
        var draft = original.StartDraft(original.SelectedRoot) with
        { Requirements = original.StartDraft(original.SelectedRoot).Requirements.Edit(DiagramRequirementField.General, "Keep the enclosure serviceable.") };
        Assert.ThrowsExactly<AutomationException>(() => RecursiveRequirementMerge.Prepare(saved, draft with
        { PhysicalAllocation = BlockPhysicalAllocation.Unknown("User has not decided the board split.") }));
    }
}
