using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveImplementationManagementTests
{
    [TestMethod]
    public void RemovalRetainsHistoricRootAndForkSourcesAndCanBeRestored()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph; var source = f.Selected["CPU"];
        var alternative = f.Alternatives["CPU"];
        var selected = graph.Select(graph.SelectedRoot, [graph.SelectedRoot, source], alternative, [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        var archived = selected.SetImplementationArchived(source.StateId, true, Guid.NewGuid(), RecursiveBlockFixture.Origin());
        Assert.IsTrue(archived.States.Single(s => s.Id == source.StateId).Archived);
        CollectionAssert.AreEqual(graph.Walk(graph.SelectedRoot).ToArray(), archived.Walk(graph.SelectedRoot).ToArray());
        Assert.AreEqual(graph.Requirements(source), archived.Requirements(source));
        Assert.ThrowsExactly<AutomationException>(() => archived.Select(archived.SelectedRoot, [archived.SelectedRoot, alternative], source,
            [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
        Assert.ThrowsExactly<AutomationException>(() => archived.SetImplementationArchived(alternative.StateId, true, Guid.NewGuid(), RecursiveBlockFixture.Origin()));
        var forked = archived.ForkImplementation(source, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Recovered exploration", RecursiveBlockFixture.Origin());
        Assert.AreEqual(archived.SelectedRoot, forked.SelectedRoot); // Copying archived history does not activate it.
        var restored = forked.SetImplementationArchived(source.StateId, false, Guid.NewGuid(), RecursiveBlockFixture.Origin());
        Assert.IsFalse(restored.States.Single(s => s.Id == source.StateId).Archived);
        Assert.HasCount(2, restored.ImplementationChanges);
        Assert.AreEqual(ImplementationChangeKind.Archive, restored.ImplementationChanges[0].Kind);
        Assert.AreEqual(ImplementationChangeKind.Restore, restored.ImplementationChanges[1].Kind);
        var loaded = RecursiveBlockGraphXml.Read(RecursiveBlockGraphXml.Write(restored));
        Assert.AreEqual(RecursiveBlockGraphXml.Write(restored), RecursiveBlockGraphXml.Write(loaded));
        Assert.AreEqual(RecursiveBlockGraphXml.Write(restored), RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(restored))));
        var returned = loaded.Select(loaded.SelectedRoot, [loaded.SelectedRoot, alternative], source, [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        Assert.AreEqual(source, returned.Inspect(returned.SelectedRoot).Children[1]);
        Assert.AreEqual(alternative, returned.Inspect(loaded.SelectedRoot).Children[1]);
    }

    [TestMethod]
    public void RenamePreservesExactPriorNamesAndAllManagementHistoryAcrossFurtherEdits()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var source = f.Blocks["CPU"];
        var renamed = graph.RenameImplementation(source.StateId, "Production direction", Guid.NewGuid(), RecursiveBlockFixture.Origin());
        Assert.AreEqual(graph.SelectedRoot, renamed.SelectedRoot);
        Assert.AreEqual("Initial approach", renamed.ImplementationChanges.Single().BeforeName);
        Assert.AreEqual("Production direction", renamed.ImplementationChanges.Single().AfterName);
        Assert.AreSame(renamed, renamed.RenameImplementation(source.StateId, "Production direction", Guid.NewGuid(), RecursiveBlockFixture.Origin()));
        var draft = renamed.StartDraft(source);
        draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "New current wording") };
        var edited = renamed.SaveDraft(renamed.SelectedRoot, [renamed.SelectedRoot, source], draft, Guid.NewGuid(), Guid.NewGuid(),
            [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        Assert.HasCount(1, edited.ImplementationChanges);
        Assert.AreEqual("Production direction", edited.States.Single(s => s.Id == source.StateId).Name);
        Assert.ThrowsExactly<AutomationException>(() => edited.RenameImplementation(source.StateId, "Alternative approach", Guid.NewGuid(), RecursiveBlockFixture.Origin()));
        Assert.ThrowsExactly<AutomationException>(() => edited.RenameImplementation(source.StateId, " ", Guid.NewGuid(), RecursiveBlockFixture.Origin()));
    }

    [TestMethod]
    public void NewInteriorRetainsRequirementsAndBoundaryContractAndMarksDetachedNotesUnresolved()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var cpu = f.Blocks["CPU"];
        var note = new DiagramAnnotation(Guid.NewGuid(), DiagramAnnotationRole.Instruction, "Prefer this memory near the processor.",
            new(DiagramAnnotationTargetKind.Block, f.Blocks["Memory"].BlockId), null, [], RecursiveBlockFixture.Origin());
        var draft = graph.StartDraft(cpu);
        draft = draft with { Diagram = draft.LocalDiagram with { Annotations = [note] } };
        graph = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, cpu], draft, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        cpu = graph.Inspect(graph.SelectedRoot).Children[1];
        Guid state = Guid.NewGuid(), revision = Guid.NewGuid();
        var created = graph.ForkImplementation(cpu, state, revision, Guid.NewGuid(), "New topology", RecursiveBlockFixture.Origin(), emptyInterior: true);
        var initial = created.Inspect(new(cpu.BlockId, state, revision));
        Assert.IsEmpty(initial.Children); Assert.IsEmpty(initial.LocalDiagram.Connections);
        CollectionAssert.AreEqual(graph.Inspect(cpu).LocalDiagram.Interfaces.ToArray(), initial.LocalDiagram.Interfaces.ToArray());
        Assert.AreEqual(graph.Requirements(cpu).Requirements, created.Requirements(initial.Selection).Requirements);
        Assert.AreEqual(note.Text, initial.LocalDiagram.Notes.Single().Text);
        Assert.AreEqual(note.Target.TargetId, initial.LocalDiagram.Notes.Single().Target.TargetId);
        Assert.IsNotNull(initial.LocalDiagram.Notes.Single().Target.UnresolvedReason);
        var chosen = created.Select(created.SelectedRoot, [created.SelectedRoot, cpu], initial.Selection, [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        Assert.AreEqual(initial.Selection, chosen.Inspect(chosen.SelectedRoot).Children[1]);
    }

    [TestMethod]
    public void ForgedRemovalOrBrokenManagementChainsAreRejectedWithoutLosingValidHistory()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph; var state = f.Alternatives["CPU"].StateId;
        Assert.ThrowsExactly<AutomationException>(() => new RecursiveBlockGraph(graph.DocumentId, graph.SelectedRoot,
            graph.States.Select(s => s.Id == state ? s with { Archived = true } : s), graph.Revisions, graph.RequirementHistories));
        var removed = graph.SetImplementationArchived(state, true, Guid.NewGuid(), RecursiveBlockFixture.Origin());
        var bad = removed.ImplementationChanges[0] with { BeforeArchived = true };
        Assert.ThrowsExactly<AutomationException>(() => new RecursiveBlockGraph(removed.DocumentId, removed.SelectedRoot, removed.States,
            removed.Revisions, removed.RequirementHistories, removed.ConnectionArchives, [bad]));
        Assert.ThrowsExactly<AutomationException>(() => removed.RenameImplementation(state, "Renamed", removed.ImplementationChanges[0].Id, RecursiveBlockFixture.Origin()));
        Assert.AreSame(removed, removed.SetImplementationArchived(state, true, Guid.NewGuid(), RecursiveBlockFixture.Origin()));
        Assert.ThrowsExactly<AutomationException>(() => graph.SetImplementationArchived(graph.SelectedRoot.StateId, true, Guid.NewGuid(), RecursiveBlockFixture.Origin()));
    }
}
