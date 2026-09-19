using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DiagramRequirementMergeTests
{
    private static readonly DiagramRequirementScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static DiagramRequirementSnapshot Snapshot(DiagramRequirements text) => new(Scope, Guid.NewGuid(), text);

    [TestMethod]
    public void IndependentEditsComposeAndIdenticalChangesDoNotConflict()
    {
        var baseline = Snapshot(new("Power the CPU", "Separate telemetry", "Keep sensing quiet"));
        var mine = baseline.Requirements with { General = "Power the CPU and memory" };
        var saved = Snapshot(baseline.Requirements with { Routing = "Place converters at the connector" });
        var result = DiagramRequirementMerge.Prepare(baseline, mine, saved).Inspect();
        Assert.IsTrue(result.CanSave);
        Assert.AreEqual(new DiagramRequirements(mine.General, baseline.Requirements.Schematic, saved.Requirements.Routing), result.Candidate);
        var both = Snapshot(mine);
        Assert.AreEqual(mine, DiagramRequirementMerge.Prepare(baseline, mine, both).Inspect().Candidate);
        Assert.AreEqual(baseline.Requirements, DiagramRequirementMerge.Prepare(baseline, baseline.Requirements, baseline).Inspect().Candidate);
    }

    [TestMethod]
    public void ConflictsPreserveEveryInputAndNeverExposeAPartialCandidate()
    {
        var baseline = Snapshot(new("Original intent", "Original drawing", "Original routing"));
        var mine = new DiagramRequirements("User intent", "User drawing", "Top edge");
        var saved = Snapshot(new("Agent intent", "Agent drawing", "Bottom edge"));
        var merge = DiagramRequirementMerge.Prepare(baseline, mine, saved);
        var result = merge.Inspect();
        Assert.IsFalse(result.CanSave); Assert.IsNull(result.Candidate); Assert.HasCount(3, result.Conflicts);
        Assert.AreEqual(new DiagramRequirementConflict(DiagramRequirementField.Routing, "Original routing", "Top edge", "Bottom edge"), result.Conflicts[2]);
        var routing = merge.Choose(DiagramRequirementField.Routing, "Top edge, subject to thermal review");
        Assert.IsNull(merge.Inspect([routing]).Candidate);
        var resolved = merge.Inspect([routing, merge.Choose(DiagramRequirementField.General, mine.General),
            merge.Choose(DiagramRequirementField.Schematic, saved.Requirements.Schematic)]);
        Assert.IsTrue(resolved.CanSave);
        Assert.AreEqual(new DiagramRequirements("User intent", "Agent drawing", "Top edge, subject to thermal review"), resolved.Candidate);
        Assert.AreEqual("Original routing", baseline.Requirements.Routing);
        Assert.AreEqual("Bottom edge", saved.Requirements.Routing);
    }

    [TestMethod]
    public void ResolutionCannotSurviveAnotherSavedOrDraftChangeEvenIfTheConflictingTextIsUnchanged()
    {
        var baseline = Snapshot(new("Intent", "Drawing", "Base"));
        var mine = baseline.Requirements with { Routing = "Mine" };
        var saved = Snapshot(baseline.Requirements with { Routing = "Theirs" });
        var merge = DiagramRequirementMerge.Prepare(baseline, mine, saved);
        var choice = merge.Choose(DiagramRequirementField.Routing, "Resolved");
        foreach (var changed in new[]
        {
            DiagramRequirementMerge.Prepare(baseline, mine, saved with { RevisionId = Guid.NewGuid() }),
            DiagramRequirementMerge.Prepare(baseline, mine with { General = "New draft" }, saved),
            DiagramRequirementMerge.Prepare(baseline, mine, saved with { Requirements = saved.Requirements with { Schematic = "New saved drawing" } })
        }) Assert.ThrowsExactly<AutomationException>(() => changed.Inspect([choice]));
        Assert.IsTrue(merge.Inspect([choice]).CanSave, "The original comparison remains usable and unchanged.");
    }

    [TestMethod]
    public void WrongScopeMutatedRevisionsAndSpuriousResolutionsAreRejected()
    {
        var baseline = Snapshot(new("General", "Schematic", "Routing"));
        foreach (var wrong in new[] { Scope with { DocumentId = Guid.NewGuid() }, Scope with { OwnerId = Guid.NewGuid() }, Scope with { DesignStateId = Guid.NewGuid() } })
            Assert.ThrowsExactly<AutomationException>(() => DiagramRequirementMerge.Prepare(baseline, baseline.Requirements, baseline with { Scope = wrong }));
        Assert.ThrowsExactly<AutomationException>(() => DiagramRequirementMerge.Prepare(baseline, baseline.Requirements,
            baseline with { Requirements = baseline.Requirements with { General = "Changed under same immutable id" } }));
        var mine = baseline.Requirements with { Routing = "Mine" };
        var saved = Snapshot(baseline.Requirements with { Routing = "Saved" });
        var merge = DiagramRequirementMerge.Prepare(baseline, mine, saved);
        var choice = merge.Choose(DiagramRequirementField.Routing, "Chosen");
        Assert.ThrowsExactly<AutomationException>(() => merge.Choose(DiagramRequirementField.General, "Override unrelated field"));
        Assert.ThrowsExactly<AutomationException>(() => merge.Inspect([choice, choice]));
        Assert.ThrowsExactly<AutomationException>(() => merge.Inspect([choice with { Field = DiagramRequirementField.General }]));
        Assert.ThrowsExactly<AutomationException>(() => merge.Inspect([choice with { Scope = Scope with { OwnerId = Guid.NewGuid() } }]));
    }

    [TestMethod]
    public void EmptyTextIsAnExplicitEditAndWhitespaceUnicodeAndLineEndingsAreNotNormalized()
    {
        var text = new DiagramRequirements("  User text\r\n第二行 Ω  ", "", "Unspecified is not zero");
        text.Validate();
        var baseline = Snapshot(text);
        var mine = text.With(DiagramRequirementField.Routing, "");
        Assert.AreEqual(mine, DiagramRequirementMerge.Prepare(baseline, mine, baseline).Inspect().Candidate);
        Assert.AreEqual(text.General, DiagramRequirementMerge.Prepare(baseline, mine, baseline).Inspect().Candidate!.General);
        var saved = Snapshot(text.With(DiagramRequirementField.Routing, "A new constraint"));
        Assert.IsFalse(DiagramRequirementMerge.Prepare(baseline, mine, saved).Inspect().CanSave);
        Assert.ThrowsExactly<AutomationException>(() => (text with { General = null! }).Validate());
        Assert.ThrowsExactly<AutomationException>(() => text.With(DiagramRequirementField.General, "invalid\0XML"));
        Assert.ThrowsExactly<AutomationException>(() => text.With((DiagramRequirementField)99, "unknown field"));
    }
}
