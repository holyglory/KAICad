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
        // A member implementation made from another one continues that one's field history: the earlier text keeps its
        // author and its own implementation's version, and the member's own rewrite follows it.
        var alternative = f.Alternatives["Data+"];
        var altHistory = appended.RequirementHistories.Single(h => h.Scope.DesignStateId == alternative.StateId);
        var continuing = new DiagramRequirementHistory(altHistory.Scope, [altHistory.Revisions[0] with { ParentId = changed.Current.Id,
            Requirements = changed.Current.Requirements }]);
        var rewrite = continuing.Commit(continuing.Current.Id, continuing.StartDraft().Edit(DiagramRequirementField.Schematic, "Label the positive leg."),
            Guid.NewGuid(), RecursiveBlockFixture.Origin("Member agent")).History;
        var altRevision = appended.Inspect(alternative);
        var altNext = altRevision with { Selection = alternative with { RevisionId = Guid.NewGuid() }, ParentRevisionId = alternative.RevisionId,
            RequirementRevisionId = rewrite.Current.Id };
        var lineage = new DiagramConnectionArchive(appended.DocumentId, appended.OwnerBlockId, appended.States.Select(s => s.Id == alternative.StateId
                ? s with { HeadRevisionId = altNext.Selection.RevisionId } : s), appended.Revisions.Add(altNext),
            appended.RequirementHistories.Select(h => h.Scope == rewrite.Scope ? new DiagramRequirementHistory(h.Scope, rewrite.Revisions) : h));
        var member = DiagramFieldHistoryQuery.Connection(lineage, altNext.Selection, DiagramRequirementField.Schematic);
        Assert.AreEqual(2, member.ContextVersion); Assert.AreEqual(alternative.StateId, member.Scope.DesignStateId);
        CollectionAssert.AreEqual(new[] { rewrite.Current.Id, changed.Current.Id, history.Current.Id },
            member.Entries.Select(e => e.RequirementRevisionId).ToArray());
        CollectionAssert.AreEqual(new[] { "Member agent", "Agent client", "Fixture user" }, member.Entries.Select(e => e.Origin.Actor).ToArray());
        CollectionAssert.AreEqual(new[] { altNext.Selection.RevisionId, candidate.Selection.RevisionId, selection.RevisionId },
            member.Entries.Select(e => e.ContextRevisionId).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 2, 1 }, member.Entries.Select(e => e.ContextVersion).ToArray());
        // Using the earlier implementation's text in this member's own draft and saving it is a new revision of this member
        // implementation that names the revision the text came from. It survives the archive's XML file and the native codec,
        // and the member's field history then lists it first, followed by everything it continues.
        var memberDraft = lineage.StartDraft(altNext.Selection);
        memberDraft = memberDraft with { Requirements = lineage.RequirementHistories.Single(h => h.Scope == rewrite.Scope)
            .RestoreField(memberDraft.Requirements, changed.Current.Id, DiagramRequirementField.Schematic) };
        Guid restoredRevision = Guid.NewGuid(), restoredText = Guid.NewGuid();
        var restoredMember = lineage.SaveDraft(memberDraft, restoredRevision, restoredText, RecursiveBlockFixture.Origin("Fixture user"));
        Assert.IsTrue(restoredMember.Changed);
        string archiveXml = DiagramConnectionArchiveXml.Write(restoredMember.Archive);
        foreach (var reread in new[] { DiagramConnectionArchiveXml.Read(archiveXml),
            KiCad.Automation.Native.RecursiveBlockCodec.Decode(KiCad.Automation.Native.RecursiveBlockCodec.Encode(restoredMember.Archive)) })
        {
            Assert.AreEqual(archiveXml, DiagramConnectionArchiveXml.Write(reread));
            var restoredHistory = reread.RequirementHistories.Single(h => h.Scope == rewrite.Scope);
            Assert.AreEqual(restoredText, restoredHistory.Current.Id); Assert.AreEqual(rewrite.Current.Id, restoredHistory.Current.ParentId);
            Assert.AreEqual(new RequirementFieldRestoration(DiagramRequirementField.Schematic, changed.Current.Id), restoredHistory.Current.Restorations.Single());
            Assert.AreEqual(changed.Current.Requirements, restoredHistory.Current.Requirements);
            Assert.AreEqual(changed.Current.Id, restoredHistory.DerivedFrom);
            var afterRestore = DiagramFieldHistoryQuery.Connection(reread, altNext.Selection with { RevisionId = restoredRevision }, DiagramRequirementField.Schematic);
            Assert.AreEqual(3, afterRestore.ContextVersion); Assert.AreEqual("Label this member clearly.", afterRestore.SavedText);
            CollectionAssert.AreEqual(new[] { restoredText, rewrite.Current.Id, changed.Current.Id, history.Current.Id },
                afterRestore.Entries.Select(e => e.RequirementRevisionId).ToArray());
            CollectionAssert.AreEqual(new[] { "Fixture user", "Member agent", "Agent client", "Fixture user" }, afterRestore.Entries.Select(e => e.Origin.Actor).ToArray());
        }
        // Later revisions of the earlier implementation stay out of this one's history.
        var laterSource = changed.Commit(changed.Current.Id, changed.StartDraft().Edit(DiagramRequirementField.Schematic, "A later source text."),
            Guid.NewGuid(), RecursiveBlockFixture.Origin("Agent client")).History;
        var laterRevision = candidate with { Selection = candidate.Selection with { RevisionId = Guid.NewGuid() }, ParentRevisionId = candidate.Selection.RevisionId,
            RequirementRevisionId = laterSource.Current.Id };
        var advanced = lineage.AppendRevision(candidate.Selection.RevisionId, laterRevision, laterSource);
        CollectionAssert.AreEqual(member.Entries.Select(e => e.RequirementRevisionId).ToArray(),
            DiagramFieldHistoryQuery.Connection(advanced, altNext.Selection, DiagramRequirementField.Schematic).Entries.Select(e => e.RequirementRevisionId).ToArray());
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
