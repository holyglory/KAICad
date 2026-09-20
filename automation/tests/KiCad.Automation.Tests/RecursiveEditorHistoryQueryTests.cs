using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveEditorHistoryQueryTests
{
    [TestMethod]
    public void WholeHistoryIncludesUnrelatedFieldChangesButNeverLaterCandidates()
    {
        var f = LinkedDiagramFixture.Create(); var initial = f.Graph;
        var second = RecursiveBlockFixture.RefineRoot(initial, DiagramRequirementField.General, 1);
        var third = RecursiveBlockFixture.RefineRoot(second, DiagramRequirementField.Routing, 1);
        var page = DiagramHistoryQuery.Read(third, second.SelectedRoot);
        Assert.AreEqual(second.SelectedRoot, page.Context); Assert.AreEqual(2, page.ContextVersion);
        Assert.AreEqual(2, page.Total); Assert.HasCount(2, page.Entries); Assert.IsFalse(page.HasMore);
        Assert.IsTrue(page.Entries[0].IsContext); Assert.IsFalse(page.Entries[1].IsContext);
        Assert.AreEqual(initial.SelectedRoot, page.Entries[1].Selection);
        Assert.AreEqual(2, page.Entries[0].ChildCount); Assert.AreEqual(2, page.Entries[0].ConnectionCount);
        Assert.AreEqual(third.SelectedRoot, third.Inspect(third.SelectedRoot).Selection);
        var inspected = DiagramHistoryQuery.Inspect(third, second.SelectedRoot, initial.SelectedRoot);
        CollectionAssert.AreEqual(initial.Inspect(initial.SelectedRoot).Children.ToArray(), inspected.Children.ToArray());
        CollectionAssert.AreEqual(initial.Inspect(initial.SelectedRoot).LocalDiagram.Connections.ToArray(), inspected.LocalDiagram.Connections.ToArray());
        Assert.ThrowsExactly<AutomationException>(() => DiagramHistoryQuery.Inspect(third, second.SelectedRoot, third.SelectedRoot));
        Assert.ThrowsExactly<AutomationException>(() => DiagramHistoryQuery.Inspect(third, second.SelectedRoot, f.Blocks["CPU"]));
    }

    [TestMethod]
    public void PagesRetainExactVersionsAndRejectAnotherOwnerOrImplementation()
    {
        var f = RecursiveBlockFixture.Create();
        var graph = RecursiveBlockFixture.RefineRoot(f.Graph, DiagramRequirementField.General, 205);
        var first = DiagramHistoryQuery.Read(graph, graph.SelectedRoot, 0, 200);
        var older = DiagramHistoryQuery.Read(graph, graph.SelectedRoot, 200, 200);
        Assert.HasCount(200, first.Entries); Assert.HasCount(6, older.Entries);
        Assert.IsTrue(first.HasMore); Assert.IsFalse(older.HasMore);
        Assert.AreEqual(206, first.Entries[0].Version); Assert.AreEqual(6, older.Entries[0].Version);
        Assert.AreEqual(1, older.Entries[^1].Version); Assert.AreEqual(f.Graph.SelectedRoot, older.Entries[^1].Selection);
        Assert.AreEqual(first.Total, older.Total); Assert.AreEqual(first.Context, older.Context);
        Assert.IsEmpty(DiagramHistoryQuery.Read(graph, graph.SelectedRoot, int.MaxValue).Entries);
        Assert.ThrowsExactly<AutomationException>(() => DiagramHistoryQuery.Read(graph, graph.SelectedRoot, -1));
        Assert.ThrowsExactly<AutomationException>(() => DiagramHistoryQuery.Read(graph, graph.SelectedRoot, 0, 201));
        Assert.ThrowsExactly<AutomationException>(() => DiagramHistoryQuery.Read(graph, graph.SelectedRoot with { BlockId = Guid.NewGuid() }));
        Assert.ThrowsExactly<AutomationException>(() => DiagramHistoryQuery.Inspect(graph, graph.SelectedRoot, f.Alternatives["System"]));
    }
}
