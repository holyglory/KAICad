using System.Collections.Immutable;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DiagramRequirementHistoryTests
{
    private static RequirementRevisionOrigin Origin(string actor = "User") => new(
        actor == "User" ? RequirementRevisionActor.User : RequirementRevisionActor.Agent,
        actor, new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero), "Fixture edit",
        [new SourceReference("requirements", "rev1", 2, null, null)], [Guid.NewGuid()]);

    private static DiagramRequirementHistory Initial() => new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
        [new(Guid.NewGuid(), null, new("Power the CPU", "Group power paths", "Keep power paths short"), Origin(), [])]);

    [TestMethod]
    public void InspectAndFieldHistoryDoNotMutateOrActivateAnything()
    {
        var first = Initial();
        var second = first.Commit(first.Current.Id, first.StartDraft().Edit(DiagramRequirementField.Routing, "Keep sensing quiet"),
            Guid.NewGuid(), Origin("Agent A")).History;
        var third = second.Commit(second.Current.Id, second.StartDraft().Edit(DiagramRequirementField.General, "Power CPU and memory"),
            Guid.NewGuid(), Origin()).History;
        Assert.AreEqual(first.Current.Requirements, third.Inspect(first.Current.Id).Requirements);
        Assert.HasCount(1, first.Revisions); Assert.HasCount(2, second.Revisions); Assert.HasCount(3, third.Revisions);
        CollectionAssert.AreEqual(new[] { second.Current.Id, first.Current.Id },
            third.FieldHistory(DiagramRequirementField.Routing).Select(r => r.Id).ToArray());
        Assert.AreEqual(third.Revisions[2].Id, third.Current.Id);
        Assert.AreEqual("Agent A", third.FieldHistory(DiagramRequirementField.Routing)[0].Origin.Actor);
    }

    [TestMethod]
    public void RestoreOnlyOneFieldIntoAnExistingDraftThenSaveAsNew()
    {
        var first = Initial();
        var current = first.Commit(first.Current.Id, first.StartDraft().Edit(DiagramRequirementField.Routing, "Keep sensing quiet"),
            Guid.NewGuid(), Origin("Agent A")).History;
        var draft = current.StartDraft().Edit(DiagramRequirementField.General, "My unsaved intent");
        var restored = current.RestoreField(draft, first.Current.Id, DiagramRequirementField.Routing);
        Assert.AreEqual("My unsaved intent", restored.Requirements.General);
        Assert.AreEqual("Keep power paths short", restored.Requirements.Routing);
        Assert.AreEqual("Keep sensing quiet", current.Current.Requirements.Routing);
        var result = current.Commit(current.Current.Id, restored, Guid.NewGuid(), Origin());
        Assert.IsTrue(result.Changed); Assert.HasCount(3, result.History.Revisions);
        Assert.AreEqual(current.Current.Id, result.Revision.ParentId);
        Assert.AreEqual(new RequirementFieldRestoration(DiagramRequirementField.Routing, first.Current.Id), result.Revision.Restorations.Single());
        Assert.AreEqual(current.Current.Requirements, result.History.Inspect(current.Current.Id).Requirements);
        Assert.AreEqual("rev1", result.Revision.Origin.Sources.Single().Revision);
        Assert.AreEqual(0, restored.Edit(DiagramRequirementField.Routing, "Further edit").RestoredFields.Count);
    }

    [TestMethod]
    public void ConcurrentIndependentChangesComposeButCompetingChangesRequireBoundResolution()
    {
        var first = Initial();
        var local = first.StartDraft().Edit(DiagramRequirementField.Routing, "Top edge");
        var remote = first.Commit(first.Current.Id, first.StartDraft().Edit(DiagramRequirementField.Schematic, "Group telemetry"),
            Guid.NewGuid(), Origin("Agent A")).History;
        var composed = remote.Commit(remote.Current.Id, local, Guid.NewGuid(), Origin());
        Assert.AreEqual("Group telemetry", composed.Revision.Requirements.Schematic);
        Assert.AreEqual("Top edge", composed.Revision.Requirements.Routing);
        remote = remote.Commit(remote.Current.Id, remote.StartDraft().Edit(DiagramRequirementField.Routing, "Bottom edge"),
            Guid.NewGuid(), Origin("Agent B")).History;
        var comparison = remote.PrepareMerge(local);
        Assert.IsFalse(comparison.Inspect().CanSave);
        Assert.ThrowsExactly<AutomationException>(() => remote.Commit(remote.Current.Id, local, Guid.NewGuid(), Origin()));
        var selected = comparison.Choose(DiagramRequirementField.Routing, "Top edge");
        var resolved = remote.Commit(remote.Current.Id, local, Guid.NewGuid(), Origin(), [selected]);
        Assert.AreEqual("Top edge", resolved.Revision.Requirements.Routing);
        Assert.AreEqual("Bottom edge", remote.Current.Requirements.Routing);
        Assert.AreEqual("Group telemetry", resolved.Revision.Requirements.Schematic);
        Assert.ThrowsExactly<AutomationException>(() => resolved.History.Commit(remote.Current.Id, local, Guid.NewGuid(), Origin(), [selected]));
    }

    [TestMethod]
    public void UnchangedSaveAndUnchangedRestoreDoNotCreateHistoryChurn()
    {
        var history = Initial(); var draft = history.StartDraft();
        Assert.AreSame(draft, history.RestoreField(draft, history.Current.Id, DiagramRequirementField.General));
        var result = history.Commit(history.Current.Id, draft, Guid.NewGuid(), Origin());
        Assert.IsFalse(result.Changed); Assert.AreSame(history, result.History);
        Assert.AreSame(history.Current, result.Revision);
    }

    [TestMethod]
    public void UnavailableWrongOwnerAndModifiedBaselineCannotRestoreOrCommit()
    {
        var history = Initial(); var draft = history.StartDraft();
        Assert.ThrowsExactly<AutomationException>(() => history.Inspect(Guid.NewGuid()));
        Assert.ThrowsExactly<AutomationException>(() => history.RestoreField(draft, Guid.NewGuid(), DiagramRequirementField.General));
        Assert.ThrowsExactly<AutomationException>(() => history.PrepareMerge(draft with
        { Baseline = draft.Baseline with { Scope = draft.Baseline.Scope with { OwnerId = Guid.NewGuid() } } }));
        Assert.ThrowsExactly<AutomationException>(() => history.PrepareMerge(draft with
        { Baseline = draft.Baseline with { Requirements = draft.Requirements with { General = "Invented past" } } }));
        Assert.ThrowsExactly<AutomationException>(() => history.PrepareMerge(draft with
        { RestoredFields = ImmutableDictionary<DiagramRequirementField, Guid>.Empty.Add(DiagramRequirementField.Routing, Guid.NewGuid()) }));
    }

    [TestMethod]
    public void HistoryRejectsCyclesDuplicateIdsFalseRestorationAndMissingProvenance()
    {
        var first = Initial(); var root = first.Current;
        Assert.ThrowsExactly<AutomationException>(() => new DiagramRequirementHistory(first.Scope, []));
        Assert.ThrowsExactly<AutomationException>(() => new DiagramRequirementHistory(first.Scope, [root, root]));
        Assert.ThrowsExactly<AutomationException>(() => new DiagramRequirementHistory(first.Scope, [root with { ParentId = root.Id }]));
        Assert.ThrowsExactly<AutomationException>(() => new DiagramRequirementHistory(first.Scope,
            [root, root with { Id = Guid.NewGuid(), ParentId = root.Id,
                Requirements = root.Requirements with { Routing = "Not the restored value" },
                Restorations = [new(DiagramRequirementField.Routing, root.Id)] }]));
        Assert.ThrowsExactly<AutomationException>(() => first.Commit(root.Id, first.StartDraft(), Guid.NewGuid(), Origin() with { Actor = "" }));
        Assert.ThrowsExactly<AutomationException>(() => first.Commit(root.Id, first.StartDraft(), Guid.NewGuid(), Origin() with
        { Sources = [new SourceReference("requirements", "", null, null, null)] }));
        Assert.HasCount(1, first.Revisions);
    }
}
