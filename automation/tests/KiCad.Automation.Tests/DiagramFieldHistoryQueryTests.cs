using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DiagramFieldHistoryQueryTests
{
    private static RecursiveBlockGraph Edit(RecursiveBlockGraph graph, DiagramRequirementField field, string text)
    {
        var draft = graph.StartDraft(graph.SelectedRoot);
        draft = draft with { Requirements = draft.Requirements.Edit(field, text) };
        return graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], draft, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
    }

    [TestMethod]
    public void FieldHistoryOmitsUnrelatedEditsAndNeverLeaksLaterCandidatesIntoTheSavedView()
    {
        var first = RecursiveBlockFixture.Create().Graph;
        var second = Edit(first, DiagramRequirementField.Routing, "Keep power paths short.");
        var third = Edit(second, DiagramRequirementField.General, "A newer general requirement.");
        var fourth = Edit(third, DiagramRequirementField.Routing, "Keep power paths away from sensing.");
        var page = DiagramFieldHistoryQuery.Block(fourth, third.SelectedRoot, DiagramRequirementField.Routing);
        Assert.AreEqual(3, page.ContextVersion); Assert.AreEqual(third.SelectedRoot.RevisionId, page.ContextRevisionId);
        Assert.AreEqual("Keep power paths short.", page.SavedText);
        Assert.HasCount(2, page.Entries); Assert.AreEqual(2, page.Entries[0].ContextVersion);
        Assert.AreEqual(second.SelectedRoot.RevisionId, page.Entries[0].ContextRevisionId);
        Assert.IsTrue(page.Entries[0].IsSavedText);
        Assert.IsFalse(page.Entries.Any(e => e.ContextRevisionId == fourth.SelectedRoot.RevisionId));
        Assert.IsFalse(page.Entries.Any(e => e.ContextRevisionId == third.SelectedRoot.RevisionId));
        Assert.AreEqual(fourth.SelectedRoot, fourth.Inspect(fourth.SelectedRoot).Selection);
    }

    [TestMethod]
    public void PaginationPreservesExactRestoreTargetsAndCurrentMarkerWithoutActivatingAnything()
    {
        var first = RecursiveBlockFixture.Create().Graph;
        var second = Edit(first, DiagramRequirementField.Routing, "Top edge");
        var third = Edit(second, DiagramRequirementField.Routing, "Bottom edge");
        var firstPage = DiagramFieldHistoryQuery.Block(third, third.SelectedRoot, DiagramRequirementField.Routing, 0, 1);
        var secondPage = DiagramFieldHistoryQuery.Block(third, third.SelectedRoot, DiagramRequirementField.Routing, 1, 1);
        var lastPage = DiagramFieldHistoryQuery.Block(third, third.SelectedRoot, DiagramRequirementField.Routing, 2, 1);
        Assert.AreEqual(3, firstPage.Total); Assert.IsTrue(firstPage.HasMore); Assert.IsTrue(secondPage.HasMore); Assert.IsFalse(lastPage.HasMore);
        Assert.IsTrue(firstPage.Entries[0].IsSavedText); Assert.IsFalse(secondPage.Entries[0].IsSavedText);
        Assert.AreEqual(second.Requirements(second.SelectedRoot).RevisionId, secondPage.Entries[0].RequirementRevisionId);
        var history = third.RequirementHistories.Single(h => h.Scope.DesignStateId == third.SelectedRoot.StateId);
        var restored = history.RestoreField(third.StartDraft(third.SelectedRoot).Requirements,
            secondPage.Entries[0].RequirementRevisionId, DiagramRequirementField.Routing);
        Assert.AreEqual("Top edge", restored.Requirements.Routing);
        Assert.AreEqual("Bottom edge", third.Requirements(third.SelectedRoot).Requirements.Routing);
        var beyond = DiagramFieldHistoryQuery.Block(third, third.SelectedRoot, DiagramRequirementField.Routing, int.MaxValue, 1);
        Assert.IsEmpty(beyond.Entries); Assert.IsFalse(beyond.HasMore);
    }

    [TestMethod]
    public void ConnectionHistoryUsesItsOwnVersionAndRequirementScope()
    {
        var f = DiagramConnectionFixture.Create(); var archive = f.Archive; var selection = f.Selected["Data+"];
        var history = archive.RequirementHistories.Single(h => h.Scope.DesignStateId == selection.StateId);
        var changed = history.Commit(history.Current.Id, history.StartDraft().Edit(DiagramRequirementField.Schematic, "Label this member clearly."),
            Guid.NewGuid(), RecursiveBlockFixture.Origin("Agent client")).History;
        var previous = archive.Inspect(selection);
        var candidate = previous with { Selection = selection with { RevisionId = Guid.NewGuid() }, ParentRevisionId = selection.RevisionId, RequirementRevisionId = changed.Current.Id };
        var appended = archive.AppendRevision(selection.RevisionId, candidate, changed);
        var saved = DiagramFieldHistoryQuery.Connection(appended, selection, DiagramRequirementField.Schematic);
        Assert.AreEqual("", saved.SavedText); Assert.AreEqual(1, saved.Total);
        var page = DiagramFieldHistoryQuery.Connection(appended, candidate.Selection, DiagramRequirementField.Schematic);
        Assert.AreEqual(selection.ConnectionId, page.Scope.OwnerId); Assert.AreEqual(2, page.ContextVersion);
        Assert.AreEqual("Agent client", page.Entries[0].Origin.Actor);
        Assert.AreEqual("Label this member clearly.", page.SavedText);
    }

    [TestMethod]
    public void QueryRejectsWrongOwnerInvalidFieldAndUnboundedPageSizes()
    {
        var graph = RecursiveBlockFixture.Create().Graph;
        Assert.ThrowsExactly<AutomationException>(() => DiagramFieldHistoryQuery.Block(graph,
            graph.SelectedRoot with { BlockId = Guid.NewGuid() }, DiagramRequirementField.General));
        Assert.ThrowsExactly<AutomationException>(() => DiagramFieldHistoryQuery.Block(graph, graph.SelectedRoot, (DiagramRequirementField)999));
        Assert.ThrowsExactly<AutomationException>(() => DiagramFieldHistoryQuery.Block(graph, graph.SelectedRoot, DiagramRequirementField.General, -1));
        Assert.ThrowsExactly<AutomationException>(() => DiagramFieldHistoryQuery.Block(graph, graph.SelectedRoot, DiagramRequirementField.General, 0, 0));
        Assert.ThrowsExactly<AutomationException>(() => DiagramFieldHistoryQuery.Block(graph, graph.SelectedRoot, DiagramRequirementField.General, 0, 201));
    }
}
