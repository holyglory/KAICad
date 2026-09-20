using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveEditorHistoryQueryTests
{
    [TestMethod]
    public void ComparisonUsesExactObjectsAndRestorationRetainsWholeHistoricalContents()
    {
        var initial = LinkedDiagramFixture.Create().Graph;
        Guid boundary = Guid.NewGuid(), note = Guid.NewGuid();
        var before = initial.Inspect(initial.SelectedRoot);
        var draft = initial.StartDraft(initial.SelectedRoot) with
        {
            Name = "New system concept", Children = [before.Children[1]],
            Requirements = initial.StartDraft(initial.SelectedRoot).Requirements.Edit(DiagramRequirementField.General, "A revised requirement."),
            Diagram = new([new(boundary, "External interface", "Unresolved external connection.")], [],
                [new(note, DiagramAnnotationRole.Comment, "Retain the first solution as an alternative.",
                    new(DiagramAnnotationTargetKind.Canvas, null), null, [], RecursiveBlockFixture.Origin())])
        };
        var changed = initial.SaveDraft(initial.SelectedRoot, [initial.SelectedRoot], draft,
            Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
        var comparison = DiagramHistoryQuery.Compare(changed, changed.SelectedRoot, initial.SelectedRoot);
        Assert.HasCount(7, comparison.Changes);
        Assert.IsTrue(comparison.Changes.Any(c => c.Category == DiagramHistoryChangeCategory.Name));
        Assert.IsTrue(comparison.Changes.Any(c => c.Field == DiagramRequirementField.General));
        Assert.IsTrue(comparison.Changes.Any(c => c.Category == DiagramHistoryChangeCategory.Block && c.ObjectId == before.Children[0].BlockId && c.Kind == DiagramHistoryChangeKind.Removed));
        Assert.AreEqual(2, comparison.Changes.Count(c => c.Category == DiagramHistoryChangeCategory.Connection && c.Kind == DiagramHistoryChangeKind.Removed));
        Assert.IsTrue(comparison.Changes.Any(c => c.ObjectId == boundary && c.Kind == DiagramHistoryChangeKind.Added));
        Assert.IsTrue(comparison.Changes.Any(c => c.ObjectId == note && c.Kind == DiagramHistoryChangeKind.Added));
        var parsed = RecursiveBlockGraphXml.Read(RecursiveBlockGraphXml.Write(changed));
        CollectionAssert.AreEqual(comparison.Changes.ToArray(), DiagramHistoryQuery.Compare(parsed, changed.SelectedRoot, initial.SelectedRoot).Changes.ToArray());
        var restored = parsed.RestoreAsDraft(parsed.StartDraft(parsed.SelectedRoot), initial.SelectedRoot);
        Assert.AreEqual(parsed.SelectedRoot, restored.Baseline); Assert.AreEqual(initial.SelectedRoot, restored.RestoredFrom);
        CollectionAssert.AreEqual(before.Children.ToArray(), restored.Children.ToArray());
        Assert.IsTrue(before.LocalDiagram.SameContents(restored.LocalDiagram));
        var saved = parsed.SaveDraft(parsed.SelectedRoot, [parsed.SelectedRoot], restored,
            Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
        Assert.IsEmpty(DiagramHistoryQuery.Compare(saved, saved.SelectedRoot, initial.SelectedRoot).Changes);
        Assert.AreEqual("New system concept", saved.Inspect(changed.SelectedRoot).Name);
    }

    [TestMethod]
    public void ReorderingIsReportedWithoutInventingAddedOrRemovedObjects()
    {
        var graph = LinkedDiagramFixture.Create().Graph; var original = graph.SelectedRoot;
        var draft = graph.StartDraft(original);
        draft = draft with { Children = [.. draft.Children.Reverse()], Diagram = draft.LocalDiagram with { Connections = [.. draft.LocalDiagram.Connections.Reverse()] } };
        var saved = graph.SaveDraft(original, [original], draft, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
        var difference = DiagramHistoryQuery.Compare(saved, saved.SelectedRoot, original);
        Assert.HasCount(2, difference.Changes);
        Assert.IsTrue(difference.Changes.All(c => c.Kind == DiagramHistoryChangeKind.Reordered));
        Assert.IsEmpty(DiagramHistoryQuery.Compare(saved, saved.SelectedRoot, saved.SelectedRoot).Changes);
    }

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
