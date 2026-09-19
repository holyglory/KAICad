using System.Collections.Immutable;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

/// <summary>Shared deterministic system/PSU/CPU fixture. All descriptions are test data,
/// not component recommendations or verified electrical characteristics.</summary>
internal sealed record RecursiveBlockFixture(RecursiveBlockGraph Graph,
    ImmutableDictionary<string, BlockSelection> Selected, ImmutableDictionary<string, BlockSelection> Alternatives)
{
    public static RequirementRevisionOrigin Origin(string actor = "Fixture user") => new(
        actor == "Fixture user" ? RequirementRevisionActor.User : RequirementRevisionActor.Agent,
        actor, new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero), "Test-only system refinement", [], []);

    public static RecursiveBlockFixture Create()
    {
        int next = 1;
        Guid Id() => Guid.Parse("fda30afe-8703-45dd-bdc0-" + (next++).ToString("x12"));
        Guid document = Id();
        var states = new List<BlockDesignState>(); var revisions = new List<RecursiveBlockRevision>();
        var histories = new List<DiagramRequirementHistory>();
        var selected = ImmutableDictionary.CreateBuilder<string, BlockSelection>();
        var alternatives = ImmutableDictionary.CreateBuilder<string, BlockSelection>();
        BlockSelection Node(string name, params BlockSelection[] children)
        {
            Guid block = Id();
            for (int i = 0; i < 2; ++i)
            {
                Guid state = Id(), revision = Id(), requirement = Id();
                var selection = new BlockSelection(block, state, revision);
                var fields = new DiagramRequirements($"{name}: implementation not yet chosen.", "Keep this diagram readable.", "");
                histories.Add(new(new(document, block, state), [new(requirement, null, fields, Origin(), [])]));
                states.Add(new(state, block, i == 0 ? "Initial approach" : "Alternative approach", revision));
                revisions.Add(new(selection, null, name, requirement, [.. children], Origin()));
                (i == 0 ? selected : alternatives).Add(name, selection);
            }
            return selected[name];
        }
        var regulator = Node("Regulator"); var power = Node("Power stage", regulator);
        var telemetry = Node("Telemetry"); var psu = Node("PSU", power, telemetry);
        var processor = Node("Processor"); var memory = Node("Memory"); var cpu = Node("CPU", processor, memory);
        var root = Node("System", psu, cpu);
        return new(new(document, root, states, revisions, histories), selected.ToImmutable(), alternatives.ToImmutable());
    }
}

[TestClass]
public sealed class RecursiveBlockGraphTests
{
    private static RequirementRevisionOrigin Origin(string actor = "Fixture user") => RecursiveBlockFixture.Origin(actor);

    [TestMethod]
    public void EveryLevelHasItsOwnChildrenAndExactAlternativeSelection()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph;
        CollectionAssert.AreEqual(new[] { f.Selected["PSU"], f.Selected["CPU"] }, graph.Inspect(graph.SelectedRoot).Children.ToArray());
        CollectionAssert.AreEqual(new[] { f.Selected["Power stage"], f.Selected["Telemetry"] }, graph.Inspect(f.Selected["PSU"]).Children.ToArray());
        CollectionAssert.AreEqual(new[] { f.Selected["Processor"], f.Selected["Memory"] }, graph.Inspect(f.Selected["CPU"]).Children.ToArray());
        Assert.HasCount(8, graph.Walk(graph.SelectedRoot));
        Assert.HasCount(16, graph.States); // Two independently selectable implementations for every occurrence.
        var changed = graph.Select(graph.SelectedRoot, [graph.SelectedRoot, f.Selected["CPU"]],
            f.Alternatives["CPU"], [Guid.NewGuid()], Origin());
        Assert.IsTrue(changed.Changed);
        Assert.AreEqual(f.Alternatives["CPU"], changed.Graph.Inspect(changed.Graph.SelectedRoot).Children[1]);
        Assert.AreEqual(f.Selected["PSU"], changed.Graph.Inspect(changed.Graph.SelectedRoot).Children[0]);
        Assert.AreEqual(f.Selected["CPU"], changed.Graph.Inspect(graph.SelectedRoot).Children[1]);
        Assert.AreEqual(graph.SelectedRoot, graph.Inspect(graph.SelectedRoot).Selection);
    }

    [TestMethod]
    public void SavingADeepChildCreatesContainingSnapshotsAndPreservesOldRootAndSiblings()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph;
        ImmutableArray<BlockSelection> path = [graph.SelectedRoot, f.Selected["PSU"], f.Selected["Power stage"], f.Selected["Regulator"]];
        var draft = graph.StartDraft(path[^1]);
        draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.Routing, "Place near the cooling edge.") };
        var result = graph.SaveDraft(graph.SelectedRoot, path, draft, Guid.NewGuid(), Guid.NewGuid(),
            [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()], Origin("Any compatible agent"));
        Assert.IsTrue(result.Changed); Assert.HasCount(3, result.CreatedAncestors);
        Assert.AreEqual(graph.Revisions.Length + 4, result.Graph.Revisions.Length);
        Assert.AreEqual(graph.SelectedRoot.RevisionId, result.Graph.Inspect(result.Graph.SelectedRoot).ParentRevisionId);
        Assert.AreSame(graph.Inspect(f.Selected["CPU"]), result.Graph.Inspect(f.Selected["CPU"]));
        var selected = result.Graph.Walk(result.Graph.SelectedRoot);
        var regulator = selected.Single(s => s.BlockId == f.Selected["Regulator"].BlockId);
        Assert.AreEqual("Place near the cooling edge.", result.Graph.Requirements(regulator).Requirements.Routing);
        Assert.AreEqual("", result.Graph.Requirements(f.Selected["Regulator"]).Requirements.Routing);
        CollectionAssert.AreEqual(graph.Walk(graph.SelectedRoot).ToArray(), result.Graph.Walk(graph.SelectedRoot).ToArray());
        Assert.AreEqual("Any compatible agent", result.Graph.Inspect(regulator).Origin.Actor);
        Assert.AreEqual(f.Alternatives["Regulator"].RevisionId,
            result.Graph.States.Single(s => s.Id == f.Alternatives["Regulator"].StateId).HeadRevisionId);
    }

    [TestMethod]
    public void HistoryInspectionAndRestorationOnlyChangeDraftUntilSavedAsSuccessor()
    {
        var f = RecursiveBlockFixture.Create(); var before = f.Graph;
        var firstDraft = before.StartDraft(before.SelectedRoot);
        firstDraft = firstDraft with { Name = "Revised system", Requirements = firstDraft.Requirements.Edit(DiagramRequirementField.General, "New requirement") };
        var second = before.SaveDraft(before.SelectedRoot, [before.SelectedRoot], firstDraft, Guid.NewGuid(), Guid.NewGuid(), [], Origin()).Graph;
        Assert.AreEqual("System", second.Inspect(before.SelectedRoot).Name);
        Assert.AreEqual("Revised system", second.Inspect(second.SelectedRoot).Name);
        var restored = second.RestoreAsDraft(second.StartDraft(second.SelectedRoot), before.SelectedRoot);
        Assert.AreEqual("System", restored.Name);
        Assert.AreEqual(second.SelectedRoot, restored.Baseline);
        Assert.AreEqual("Revised system", second.Inspect(second.SelectedRoot).Name);
        var third = second.SaveDraft(second.SelectedRoot, [second.SelectedRoot], restored, Guid.NewGuid(), Guid.NewGuid(), [], Origin()).Graph;
        Assert.AreEqual("System", third.Inspect(third.SelectedRoot).Name);
        Assert.AreEqual(second.SelectedRoot.RevisionId, third.Inspect(third.SelectedRoot).ParentRevisionId);
        Assert.HasCount(3, third.History(third.SelectedRoot.StateId));
        Assert.AreEqual(before.Requirements(before.SelectedRoot).Requirements, third.Requirements(third.SelectedRoot).Requirements);
        Assert.AreEqual(before.SelectedRoot, third.Inspect(third.SelectedRoot).RestoredFrom);
        var reloaded = RecursiveBlockGraphXml.Read(RecursiveBlockGraphXml.Write(third));
        Assert.AreEqual(before.SelectedRoot, reloaded.Inspect(reloaded.SelectedRoot).RestoredFrom);
        Assert.ThrowsExactly<AutomationException>(() => third.Select(third.SelectedRoot, [third.SelectedRoot], before.SelectedRoot, [], Origin()));
        // Declining is discarding this immutable draft, not publishing an old snapshot.
        Assert.AreEqual("Revised system", second.Inspect(second.SelectedRoot).Name);
    }

    [TestMethod]
    public void WholeDiagramRestoreCannotDiscardExistingDraftOrInventItsSource()
    {
        var graph = RecursiveBlockFixture.Create().Graph;
        var draft = graph.StartDraft(graph.SelectedRoot);
        var dirty = draft with { Name = "Unrelated unsaved name" };
        Assert.ThrowsExactly<AutomationException>(() => graph.RestoreAsDraft(dirty, graph.SelectedRoot));
        Assert.AreEqual("Unrelated unsaved name", dirty.Name);
        var textDraft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "Unsaved text") };
        Assert.ThrowsExactly<AutomationException>(() => graph.RestoreAsDraft(textDraft, graph.SelectedRoot));
        var source = graph.States.First(s => s.BlockId != graph.SelectedRoot.BlockId);
        var forged = draft with { Name = "New name", RestoredFrom = new(source.BlockId, source.Id, source.HeadRevisionId) };
        Assert.ThrowsExactly<AutomationException>(() => graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], forged,
            Guid.NewGuid(), Guid.NewGuid(), [], Origin()));
        Assert.AreEqual("System", graph.Inspect(graph.SelectedRoot).Name);
    }

    [TestMethod]
    public void NoOpSaveAndSelectionCreateNoHistoryOrNewGraph()
    {
        var graph = RecursiveBlockFixture.Create().Graph;
        var save = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], graph.StartDraft(graph.SelectedRoot),
            Guid.NewGuid(), Guid.NewGuid(), [], Origin());
        Assert.IsFalse(save.Changed); Assert.AreSame(graph, save.Graph);
        var select = graph.Select(graph.SelectedRoot, [graph.SelectedRoot], graph.SelectedRoot, [], Origin());
        Assert.IsFalse(select.Changed); Assert.AreSame(graph, select.Graph);
    }

    [TestMethod]
    public void PublishingCandidateDoesNotLeakIntoActiveOrHistoricTreeAndStaleRequestsAreRejected()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph; var psu = graph.Inspect(f.Selected["PSU"]);
        var candidate = psu with { Selection = psu.Selection with { RevisionId = Guid.NewGuid() }, ParentRevisionId = psu.Selection.RevisionId, Name = "Candidate PSU" };
        var appended = graph.AppendRevision(psu.Selection.RevisionId, candidate);
        Assert.AreEqual(graph.SelectedRoot, appended.SelectedRoot);
        Assert.AreEqual("PSU", appended.Inspect(appended.Inspect(appended.SelectedRoot).Children[0]).Name);
        Assert.ThrowsExactly<AutomationException>(() => appended.AppendRevision(psu.Selection.RevisionId, candidate with { Selection = candidate.Selection with { RevisionId = Guid.NewGuid() } }));
        Assert.ThrowsExactly<AutomationException>(() => appended.Select(graph.SelectedRoot,
            [graph.SelectedRoot, psu.Selection, f.Selected["Power stage"]], f.Alternatives["Power stage"], [Guid.NewGuid(), Guid.NewGuid()], Origin()));
        var selected = appended.Select(graph.SelectedRoot, [graph.SelectedRoot, psu.Selection], candidate.Selection, [Guid.NewGuid()], Origin()).Graph;
        Assert.ThrowsExactly<AutomationException>(() => selected.Select(graph.SelectedRoot, [graph.SelectedRoot, f.Selected["CPU"]], f.Alternatives["CPU"], [Guid.NewGuid()], Origin()));
        Assert.AreEqual("Candidate PSU", selected.Inspect(selected.Inspect(selected.SelectedRoot).Children[0]).Name);
        Assert.AreEqual("PSU", selected.Inspect(psu.Selection).Name);
    }

    [TestMethod]
    public void WrongPathsTargetsDraftBaselinesAndRequirementHistoryRewritesCannotPublish()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph;
        Assert.ThrowsExactly<AutomationException>(() => graph.Select(graph.SelectedRoot,
            [graph.SelectedRoot, f.Selected["Regulator"]], f.Alternatives["Regulator"], [Guid.NewGuid()], Origin()));
        Assert.ThrowsExactly<AutomationException>(() => graph.Select(graph.SelectedRoot,
            [graph.SelectedRoot, f.Selected["PSU"]], f.Alternatives["CPU"], [Guid.NewGuid()], Origin()));
        Assert.ThrowsExactly<AutomationException>(() => graph.Select(graph.SelectedRoot,
            [graph.SelectedRoot, f.Selected["PSU"]], f.Alternatives["PSU"], [graph.SelectedRoot.RevisionId], Origin()));
        var draft = graph.StartDraft(graph.SelectedRoot) with { Requirements = graph.StartDraft(f.Selected["PSU"]).Requirements };
        Assert.ThrowsExactly<AutomationException>(() => graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], draft,
            Guid.NewGuid(), Guid.NewGuid(), [], Origin()));
        var root = graph.Inspect(graph.SelectedRoot);
        var newRevision = root with { Selection = root.Selection with { RevisionId = Guid.NewGuid() }, ParentRevisionId = root.Selection.RevisionId };
        var saved = graph.RequirementHistories.Single(h => h.Scope.DesignStateId == root.Selection.StateId);
        var rewritten = new DiagramRequirementHistory(saved.Scope, [saved.Current with { Origin = Origin("Forged actor") }]);
        Assert.ThrowsExactly<AutomationException>(() => graph.AppendRevision(root.Selection.RevisionId, newRevision, rewritten));
        // A separately deserialized but byte-equivalent saved prefix is legitimate.
        var reloaded = DiagramRequirementHistoryXml.Read(DiagramRequirementHistoryXml.Write(saved));
        Assert.AreEqual(graph.SelectedRoot, graph.AppendRevision(root.Selection.RevisionId, newRevision, reloaded).SelectedRoot);
    }

    [TestMethod]
    public void InvalidInactiveStatesMissingChildrenCyclesAndSharedOccurrencesAreRejected()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph;
        RecursiveBlockGraph Replace(BlockSelection selection, Func<RecursiveBlockRevision, RecursiveBlockRevision> change) =>
            new(graph.DocumentId, graph.SelectedRoot, graph.States,
                graph.Revisions.Select(r => r.Selection == selection ? change(r) : r), graph.RequirementHistories);
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Alternatives["CPU"], r => r with { Children = [f.Selected["CPU"]] }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["PSU"], r => r with { Children = [f.Selected["PSU"]] }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["PSU"], r => r with { Children = [f.Selected["CPU"]] }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["PSU"], r => r with { Children = [f.Selected["Power stage"], f.Selected["Power stage"]] }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["PSU"], r => r with { Children = [f.Selected["Power stage"] with { RevisionId = Guid.NewGuid() }] }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["PSU"], r => r with { ParentRevisionId = r.Selection.RevisionId }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["PSU"], r => r with { RequirementRevisionId = graph.Inspect(f.Selected["CPU"]).RequirementRevisionId }));
        Assert.AreEqual(f.Selected["System"], graph.SelectedRoot);
    }
}
