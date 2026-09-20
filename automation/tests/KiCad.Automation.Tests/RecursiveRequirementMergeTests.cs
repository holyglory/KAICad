using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveRequirementMergeTests
{
    private static RecursiveBlockGraph Save(RecursiveBlockGraph graph, RecursiveBlockDraft draft) =>
        graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], draft, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;

    [TestMethod]
    public void IndependentFieldsComposeWithoutRevertingNewerDiagramProperties()
    {
        var first = LinkedDiagramFixture.Create().Graph;
        var local = first.StartDraft(first.SelectedRoot);
        local = local with { Requirements = local.Requirements.Edit(DiagramRequirementField.Routing, "Top edge") };
        var remote = first.StartDraft(first.SelectedRoot);
        remote = remote with { Name = "New system name", Requirements = remote.Requirements.Edit(DiagramRequirementField.Schematic, "Group telemetry") };
        var latest = Save(first, remote);
        var result = RecursiveRequirementMerge.Prepare(latest, local).Inspect();
        Assert.IsTrue(result.CanSave); Assert.IsNotNull(result.Candidate);
        Assert.AreEqual("New system name", result.Candidate.Name);
        Assert.AreEqual("Top edge", result.Candidate.Requirements.Requirements.Routing);
        Assert.AreEqual("Group telemetry", result.Candidate.Requirements.Requirements.Schematic);
        Assert.AreEqual(latest.SelectedRoot, result.ExpectedRoot); Assert.AreEqual(latest.SelectedRoot, result.Candidate.Baseline);
        var saved = Save(latest, result.Candidate);
        Assert.AreEqual("Top edge", saved.Requirements(saved.SelectedRoot).Requirements.Routing);
        Assert.AreEqual("", saved.Requirements(first.SelectedRoot).Requirements.Routing);
        Assert.AreEqual(first.ConnectionArchives.Length, saved.ConnectionArchives.Length);
    }

    [TestMethod]
    public void ConflictsRetainAllThreeVersionsAndRequireExactRevisionBoundChoices()
    {
        var first = RecursiveBlockFixture.Create().Graph;
        var local = first.StartDraft(first.SelectedRoot);
        local = local with { Requirements = local.Requirements.Edit(DiagramRequirementField.Routing, "Top edge") };
        var remote = first.StartDraft(first.SelectedRoot);
        remote = remote with { Requirements = remote.Requirements.Edit(DiagramRequirementField.Routing, "Bottom edge") };
        var latest = Save(first, remote);
        var merge = RecursiveRequirementMerge.Prepare(latest, local);
        var blocked = merge.Inspect(); Assert.IsFalse(blocked.CanSave); Assert.IsNull(blocked.Candidate);
        Assert.AreEqual(new DiagramRequirementConflict(DiagramRequirementField.Routing, "", "Top edge", "Bottom edge"), blocked.Conflicts.Single());
        var choice = merge.Requirements.Choose(DiagramRequirementField.Routing, "Top edge beside the heat sink");
        var ready = merge.Inspect([choice]); Assert.IsNotNull(ready.Candidate);
        var saved = Save(latest, ready.Candidate);
        Assert.AreEqual("Top edge beside the heat sink", saved.Requirements(saved.SelectedRoot).Requirements.Routing);
        Assert.AreEqual("Bottom edge", latest.Requirements(latest.SelectedRoot).Requirements.Routing);
        Assert.AreEqual("Top edge", local.Requirements.Requirements.Routing);
        var newerDraft = latest.StartDraft(latest.SelectedRoot);
        newerDraft = newerDraft with { Requirements = newerDraft.Requirements.Edit(DiagramRequirementField.General, "More saved context") };
        var newer = Save(latest, newerDraft);
        Assert.ThrowsExactly<AutomationException>(() => RecursiveRequirementMerge.Prepare(newer, local).Inspect([choice]));
    }

    [TestMethod]
    public void ChangedImplementationRemovedBlockAndLocalStructuralEditsAreNotGuessed()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph;
        var draft = graph.StartDraft(f.Selected["CPU"]);
        var selected = graph.Select(graph.SelectedRoot, [graph.SelectedRoot, f.Selected["CPU"]], f.Alternatives["CPU"], [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        Assert.ThrowsExactly<AutomationException>(() => RecursiveRequirementMerge.Prepare(selected, draft));
        var rootDraft = graph.StartDraft(graph.SelectedRoot) with { Children = [f.Selected["PSU"]] };
        var removed = Save(graph, rootDraft);
        Assert.ThrowsExactly<AutomationException>(() => RecursiveRequirementMerge.Prepare(removed, draft));
        Assert.ThrowsExactly<AutomationException>(() => RecursiveRequirementMerge.Prepare(graph,
            graph.StartDraft(graph.SelectedRoot) with { Name = "Local structure change" }));
        var current = graph.Inspect(graph.SelectedRoot);
        var candidate = current with { Selection = current.Selection with { RevisionId = Guid.NewGuid() }, ParentRevisionId = current.Selection.RevisionId, Name = "Unselected candidate" };
        var appended = graph.AppendRevision(current.Selection.RevisionId, candidate);
        Assert.ThrowsExactly<AutomationException>(() => RecursiveRequirementMerge.Prepare(appended, graph.StartDraft(graph.SelectedRoot)));
    }

    [TestMethod]
    public void UnchangedRebaseProducesNoNewRevisionAndKeepsIndependentDraftState()
    {
        var graph = RecursiveBlockFixture.Create().Graph;
        var result = RecursiveRequirementMerge.Prepare(graph, graph.StartDraft(graph.SelectedRoot)).Inspect();
        Assert.IsNotNull(result.Candidate);
        var saved = Save(graph, result.Candidate);
        Assert.AreSame(graph, saved);
    }
}
