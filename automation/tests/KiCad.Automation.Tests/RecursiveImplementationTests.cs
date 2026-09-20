using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveImplementationTests
{
    [TestMethod]
    public void ANewImplementationPinsItsExactSourceAndStartsIndependentHistoryWithoutSelection()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var source = f.Blocks["CPU"];
        Guid stateId = Guid.NewGuid(), revisionId = Guid.NewGuid(), requirementId = Guid.NewGuid();
        var forked = graph.ForkImplementation(source, stateId, revisionId, requirementId, "Low-power exploration", RecursiveBlockFixture.Origin());
        Assert.AreEqual(graph.SelectedRoot, forked.SelectedRoot);
        var state = forked.States.Single(s => s.Id == stateId); Assert.AreEqual(source, state.ForkedFrom);
        var choice = new BlockSelection(source.BlockId, stateId, revisionId);
        Assert.AreEqual(graph.Requirements(source).Requirements, forked.Requirements(choice).Requirements);
        Assert.IsTrue(graph.Inspect(source).LocalDiagram.SameContents(forked.Inspect(choice).LocalDiagram));
        CollectionAssert.AreEqual(graph.Inspect(source).Children.ToArray(), forked.Inspect(choice).Children.ToArray());
        var draft = forked.StartDraft(choice);
        draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "Use less power.") };
        var edited = forked.SaveImplementationDraft(forked.SelectedRoot, [forked.SelectedRoot, source], draft,
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        Assert.AreEqual(graph.Requirements(source).Requirements, edited.Requirements(source).Requirements);
        Assert.AreEqual(source, edited.States.Single(s => s.Id == stateId).ForkedFrom);
        var loaded = RecursiveBlockGraphXml.Read(RecursiveBlockGraphXml.Write(edited));
        Assert.AreEqual(source, loaded.States.Single(s => s.Id == stateId).ForkedFrom);
        var fromMessage = KiCad.Automation.Native.RecursiveBlockCodec.Decode(KiCad.Automation.Native.RecursiveBlockCodec.Encode(edited));
        Assert.AreEqual(RecursiveBlockGraphXml.Write(loaded), RecursiveBlockGraphXml.Write(fromMessage));
        Assert.HasCount(2, loaded.History(stateId));
        Assert.HasCount(1, loaded.History(source.StateId));
    }

    [TestMethod]
    public void DuplicateNamesWrongSourceAndCircularImplementationDerivationAreRejected()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph; var source = f.Selected["CPU"];
        Assert.ThrowsExactly<AutomationException>(() => graph.ForkImplementation(source, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "INITIAL APPROACH", RecursiveBlockFixture.Origin()));
        Assert.ThrowsExactly<AutomationException>(() => graph.ForkImplementation(source, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            " ", RecursiveBlockFixture.Origin()));
        Assert.ThrowsExactly<AutomationException>(() => graph.ForkImplementation(source with { BlockId = Guid.NewGuid() }, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "New", RecursiveBlockFixture.Origin()));
        var self = graph.States.Select(s => s.Id == source.StateId ? s with { ForkedFrom = source } : s);
        Assert.ThrowsExactly<AutomationException>(() => new RecursiveBlockGraph(graph.DocumentId, graph.SelectedRoot, self, graph.Revisions, graph.RequirementHistories));
        var alternative = f.Alternatives["CPU"];
        var cyclic = graph.States.Select(s => s.Id == source.StateId ? s with { ForkedFrom = alternative }
            : s.Id == alternative.StateId ? s with { ForkedFrom = source } : s);
        Assert.ThrowsExactly<AutomationException>(() => new RecursiveBlockGraph(graph.DocumentId, graph.SelectedRoot, cyclic, graph.Revisions, graph.RequirementHistories));
        var wrongOwner = graph.States.Select(s => s.Id == source.StateId ? s with { ForkedFrom = f.Selected["PSU"] } : s);
        Assert.ThrowsExactly<AutomationException>(() => new RecursiveBlockGraph(graph.DocumentId, graph.SelectedRoot, wrongOwner, graph.Revisions, graph.RequirementHistories));
    }

    [TestMethod]
    public void PreviewDraftDoesNotSelectAndSavePublishesItsEditsOnlyInThatImplementation()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph; var original = f.Selected["CPU"]; var alternative = f.Alternatives["CPU"];
        var draft = graph.StartDraft(alternative);
        draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "An alternative CPU approach.") };
        Assert.AreEqual(original, graph.Inspect(graph.SelectedRoot).Children[1]);
        var selected = graph.SaveImplementationDraft(graph.SelectedRoot, [graph.SelectedRoot, original], draft,
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin());
        Assert.IsTrue(selected.Changed);
        var next = selected.Graph.Inspect(selected.Graph.SelectedRoot).Children[1];
        Assert.AreEqual(alternative.StateId, next.StateId); Assert.AreNotEqual(alternative.RevisionId, next.RevisionId);
        Assert.AreEqual("An alternative CPU approach.", selected.Graph.Requirements(next).Requirements.General);
        Assert.AreEqual(graph.Requirements(original).Requirements, selected.Graph.Requirements(original).Requirements);
        Assert.AreEqual(graph.Requirements(alternative).Requirements, selected.Graph.Requirements(alternative).Requirements);
        Assert.AreEqual(original, selected.Graph.Inspect(graph.SelectedRoot).Children[1]);
        Assert.AreEqual(f.Selected["PSU"], selected.Graph.Inspect(selected.Graph.SelectedRoot).Children[0]);
        Assert.ThrowsExactly<AutomationException>(() => graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, original], draft,
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
    }

    [TestMethod]
    public void UnchangedImplementationSelectionCreatesOnlyNecessaryContainingRevision()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph;
        var selected = graph.SaveImplementationDraft(graph.SelectedRoot, [graph.SelectedRoot, f.Selected["PSU"]], graph.StartDraft(f.Alternatives["PSU"]),
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        Assert.AreEqual(graph.Revisions.Length + 1, selected.Revisions.Length);
        Assert.AreEqual(f.Alternatives["PSU"], selected.Inspect(selected.SelectedRoot).Children[0]);
        var rootChoice = graph.SaveImplementationDraft(graph.SelectedRoot, [graph.SelectedRoot], graph.StartDraft(f.Alternatives["System"]),
            Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
        Assert.AreEqual(f.Alternatives["System"], rootChoice.SelectedRoot);
        Assert.AreEqual(graph.Revisions.Length, rootChoice.Revisions.Length);
        Assert.AreSame(graph, graph.SaveImplementationDraft(graph.SelectedRoot, [graph.SelectedRoot], graph.StartDraft(graph.SelectedRoot),
            Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph);
    }

    [TestMethod]
    public void StaleOrIncompatibleSelectionsCannotPartlySaveTheCandidate()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var cpu = f.Blocks["CPU"];
        var state = graph.States.Single(s => s.BlockId == cpu.BlockId && s.Id != cpu.StateId);
        var alternative = new BlockSelection(cpu.BlockId, state.Id, state.HeadRevisionId);
        var draft = graph.StartDraft(alternative);
        draft = draft with { Diagram = BlockLocalDiagram.Empty, Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "Incompatible alternative draft") };
        string before = RecursiveBlockGraphXml.Write(graph);
        Assert.ThrowsExactly<AutomationException>(() => graph.SaveImplementationDraft(graph.SelectedRoot, [graph.SelectedRoot, cpu], draft,
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
        Assert.AreEqual(before, RecursiveBlockGraphXml.Write(graph));
        var changed = graph.SaveImplementationDraft(graph.SelectedRoot, [graph.SelectedRoot, cpu], graph.StartDraft(alternative),
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        Assert.ThrowsExactly<AutomationException>(() => changed.SaveImplementationDraft(graph.SelectedRoot, [graph.SelectedRoot, cpu], graph.StartDraft(alternative),
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
        Assert.ThrowsExactly<AutomationException>(() => graph.SaveImplementationDraft(graph.SelectedRoot, [graph.SelectedRoot, cpu], graph.StartDraft(f.Blocks["PSU"]),
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
    }
}
