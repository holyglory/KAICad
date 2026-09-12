using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicHierarchyMergeTests
{
    private static void Note(SchematicScreenData screen, string text)
    {
        screen.Items.Add(Any.Pack(new SchematicText
            { Id = new() { Value = Guid.NewGuid().ToString("D") }, Text = new() { Text_ = text } }));
    }

    private static void MoveSharedNote(SchematicHierarchyData hierarchy)
    {
        foreach (var child in hierarchy.Instances.Skip(1))
        {
            var note = child.Items[0].Unpack<SchematicText>(); note.Text.Position = new() { XNm = 1000000 };
            child.Items[0] = Any.Pack(note);
        }
    }

    private static SchematicScreenData AddBranch(SchematicHierarchyData hierarchy, SchematicScreenData? parent = null)
    {
        var root = hierarchy.Instances[0]; var reference = root.Items[0].Unpack<SheetSymbol>();
        parent ??= root;
        reference.Id.Value = Guid.NewGuid().ToString("D"); reference.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        reference.Path = parent.Metadata.Document.SheetPath.Clone();
        reference.FilenameField.Text.Text_ = reference.Id.Value + ".kicad_sch";
        parent.Items.Add(Any.Pack(reference));
        var screen = new SchematicScreenData { Metadata = parent.Metadata.Clone() };
        screen.Metadata.ScreenId = reference.ChildScreenId.Clone();
        screen.Metadata.Document.SheetPath.Path.Add(reference.Id.Clone());
        Note(screen, "New branch contents"); hierarchy.Instances.Add(screen); return screen;
    }

    private static void RemoveFirstBranch(SchematicHierarchyData hierarchy)
    {
        hierarchy.Instances[0].Items.RemoveAt(0); hierarchy.Instances.RemoveAt(1);
    }

    [TestMethod]
    public void IndependentRootAndSharedSheetEditsMergeWithoutDuplicatePhysicalMoves()
    {
        var b = SchematicHierarchyTopologyTests.Fixture(); var x = b.Clone(); var n = b.Clone();
        Note(x.Instances[0], "XML instruction"); MoveSharedNote(n);
        string bXml = SchematicDataXml.Write(b), xXml = SchematicDataXml.Write(x), nXml = SchematicDataXml.Write(n);
        var result = SchematicHierarchyMerge.Plan(b, x, n);
        Assert.IsTrue(result.CanApply, result.ErrorMessage);
        Assert.AreEqual(1, result.NativeOperations.Count);
        Assert.AreEqual("XML instruction", result.NativeOperations[0].Create.Unpack<SchematicText>().Text.Text_);
        Assert.AreEqual(1000000L, result.Merged!.Instances[1].Items[0].Unpack<SchematicText>().Text.Position.XNm);
        Assert.HasCount(2, result.CoverageGaps);
        Assert.AreEqual(bXml, SchematicDataXml.Write(b)); Assert.AreEqual(xXml, SchematicDataXml.Write(x));
        Assert.AreEqual(nXml, SchematicDataXml.Write(n));
    }

    [TestMethod]
    public void NewBranchAndIndependentNativeRootEditFormOneCompleteDelta()
    {
        var b = SchematicHierarchyTopologyTests.Fixture(); var x = b.Clone(); var n = b.Clone();
        AddBranch(x); Note(n.Instances[0], "Native review note");
        var result = SchematicHierarchyMerge.Plan(b, x, n);
        Assert.IsTrue(result.CanApply, result.ErrorMessage);
        Assert.HasCount(4, result.Merged!.Instances);
        Assert.AreEqual(1, result.NativeOperations.Count(o => o.Create?.Is(SheetSymbol.Descriptor) == true));
        Assert.IsTrue(result.Merged.Instances[0].Items.Any(i => i.Is(SchematicText.Descriptor)
            && i.Unpack<SchematicText>().Text.Text_ == "Native review note"));
        var reverse = SchematicHierarchyMerge.Plan(b, n, x);
        Assert.IsTrue(reverse.CanApply, reverse.ErrorMessage);
        Assert.HasCount(1, reverse.NativeOperations);
        Assert.IsTrue(reverse.NativeOperations[0].Create.Is(SchematicText.Descriptor));
    }

    [TestMethod]
    public void RemovedBranchDoesNotDeleteSharedContentsAndDeleteModifyConflictsAreAtomic()
    {
        var b = SchematicHierarchyTopologyTests.Fixture(); var x = b.Clone(); RemoveFirstBranch(x);
        var removal = SchematicHierarchyMerge.Plan(b, x, b);
        Assert.IsTrue(removal.CanApply, removal.ErrorMessage);
        Assert.HasCount(2, removal.Merged!.Instances); Assert.HasCount(1, removal.NativeOperations);
        var n = b.Clone(); MoveSharedNote(n); Note(x.Instances[0], "Independent addition must not partially apply");
        var conflict = SchematicHierarchyMerge.Plan(b, x, n);
        Assert.IsFalse(conflict.CanApply); Assert.IsNull(conflict.Merged); Assert.IsEmpty(conflict.NativeOperations);
        Assert.IsTrue(conflict.Conflicts.Any(c => c.Reason == "sheet_delete_modify" && c.Xml is null && c.Native is not null));
    }

    [TestMethod]
    public void CompetingNewSheetContentsRetainBothVersions()
    {
        var b = SchematicHierarchyTopologyTests.Fixture(); var x = b.Clone(); AddBranch(x); var n = x.Clone();
        var note = n.Instances[^1].Items[0].Unpack<SchematicText>(); note.Text.Text_ = "Native alternative";
        n.Instances[^1].Items[0] = Any.Pack(note);
        var result = SchematicHierarchyMerge.Plan(b, x, n);
        Assert.IsFalse(result.CanApply); Assert.IsEmpty(result.NativeOperations);
        var conflict = result.Conflicts.Single(); Assert.AreEqual("competing_sheet_addition", conflict.Reason);
        Assert.IsNull(conflict.Baseline); Assert.AreEqual(x.Instances[^1], conflict.Xml); Assert.AreEqual(n.Instances[^1], conflict.Native);
    }

    [TestMethod]
    public void ConvergentChangesAndUnchangedSynchronizationHaveNoFurtherOperations()
    {
        var b = SchematicHierarchyTopologyTests.Fixture(); var x = b.Clone(); AddBranch(x);
        var result = SchematicHierarchyMerge.Plan(b, x, x);
        Assert.IsTrue(result.CanApply, result.ErrorMessage); Assert.IsEmpty(result.NativeOperations);
        var again = SchematicHierarchyMerge.Plan(result.Merged!, result.Merged!, result.Merged!);
        Assert.IsTrue(again.CanApply); Assert.IsEmpty(again.NativeOperations);
        Assert.AreEqual(SchematicDataXml.Write(result.Merged!), SchematicDataXml.Write(again.Merged!));
    }

    [TestMethod]
    public void DifferentDocumentsInvalidInputsAndCancellationNeverReturnPartialEdits()
    {
        var b = SchematicHierarchyTopologyTests.Fixture(); var x = b.Clone(); x.Instances.RemoveAt(1);
        Assert.AreEqual("invalid_merge_hierarchy", SchematicHierarchyMerge.Plan(b, x, b).ErrorCode);
        var other = SchematicHierarchyTopologyTests.Fixture();
        Assert.AreEqual("merge_document_changed", SchematicHierarchyMerge.Plan(b, other, b).ErrorCode);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicHierarchyMerge.Plan(b, b, b, cancel.Token));
    }

    [TestMethod]
    public void AnnotationPolicyKeepsOneOwnerAcrossBranchInsertionAndRemoval()
    {
        foreach (bool remove in new[] { false, true })
        {
            var baseline = SchematicHierarchyTopologyTests.Fixture();
            foreach (var screen in baseline.Instances) screen.Metadata.Annotation = SchematicAnnotationTests.Policy();
            var xml = baseline.Clone(); var native = baseline.Clone();
            if (remove) RemoveFirstBranch(xml); else AddBranch(xml);
            foreach (var screen in native.Instances) screen.Metadata.Annotation.StartAfter = 741;
            var merged = SchematicHierarchyMerge.Plan(baseline, xml, native);
            Assert.IsTrue(merged.CanApply, merged.ErrorMessage);
            Assert.IsTrue(merged.Merged!.Instances.All(s => s.Metadata.Annotation.StartAfter == 741));
            Assert.AreEqual(0, merged.NativeOperations.Count(o => o.SetAnnotation is not null));
            var reverse = SchematicHierarchyMerge.Plan(baseline, native, xml);
            Assert.IsTrue(reverse.CanApply, reverse.ErrorMessage);
            Assert.AreEqual(1, reverse.NativeOperations.Count(o => o.SetAnnotation is not null));
        }
    }

    [TestMethod]
    public void ProjectChangesHaveOneOwnerEvenAcrossConcurrentBranchInsertionAndRemoval()
    {
        foreach (bool remove in new[] { false, true })
        {
            var b = SchematicHierarchyTopologyTests.Fixture(); var x = b.Clone(); var n = b.Clone();
            if (remove) RemoveFirstBranch(x); else AddBranch(x);
            foreach (var screen in n.Instances) screen.Metadata.TextVariables.Add("NOTE", "Native project instruction");
            var result = SchematicHierarchyMerge.Plan(b, x, n);
            Assert.IsTrue(result.CanApply, result.ErrorMessage);
            Assert.IsTrue(result.Merged!.Instances.All(s => s.Metadata.TextVariables["NOTE"] == "Native project instruction"));
            Assert.AreEqual(0, result.NativeOperations.Count(o => o.ReplaceTextVariables is not null));
            var reverse = SchematicHierarchyMerge.Plan(b, n, x);
            Assert.IsTrue(reverse.CanApply, reverse.ErrorMessage);
            Assert.AreEqual(1, reverse.NativeOperations.Count(o => o.ReplaceTextVariables is not null));
        }
    }

    [TestMethod]
    public void InconsistentSharedInputCannotBecomeAnUnchangedSuccessfulMerge()
    {
        foreach (string field in new[] { "text", "metadata", "missing_item" })
        {
            var bad = SchematicHierarchyTopologyTests.Fixture();
            if (field == "text")
            {
                var note = bad.Instances[1].Items[0].Unpack<SchematicText>(); note.Text.Text_ = "Only one shared copy changed";
                bad.Instances[1].Items[0] = Any.Pack(note);
            }
            else if (field == "metadata") bad.Instances[1].Metadata.TitleBlock = new() { Title = "Contradictory shared page" };
            else bad.Instances[1].Items.Clear();
            var result = SchematicHierarchyMerge.Plan(bad, bad, bad);
            Assert.IsFalse(result.CanApply, field);
            Assert.IsNull(result.Merged); Assert.IsEmpty(result.NativeOperations);
        }
    }

    [TestMethod]
    public void WholeSheetChoicesRequireExactSnapshotAndConsistentSharedVersions()
    {
        var b = SchematicHierarchyTopologyTests.Fixture(); var x = b.Clone(); var n = b.Clone();
        foreach (var screen in x.Instances.Skip(1))
        {
            var note = screen.Items[0].Unpack<SchematicText>(); note.Text.Text_ = "XML wording"; screen.Items[0] = Any.Pack(note);
        }
        foreach (var screen in n.Instances.Skip(1))
        {
            var note = screen.Items[0].Unpack<SchematicText>(); note.Text.Text_ = "Native wording"; screen.Items[0] = Any.Pack(note);
        }
        var conflict = SchematicHierarchyMerge.Plan(b, x, n);
        Assert.HasCount(2, conflict.Conflicts);
        string token = SchematicHierarchyMerge.SnapshotToken(b, x, n);
        var choices = conflict.Conflicts.ToDictionary(c => c.InstancePath, _ => SchematicConflictChoice.Xml);
        foreach (var choice in System.Enum.GetValues<SchematicConflictChoice>())
        {
            var selected = choices.ToDictionary(p => p.Key, _ => choice);
            var result = SchematicHierarchyMerge.Resolve(b, x, n, token, selected);
            Assert.IsTrue(result.CanApply, result.ErrorMessage);
            string expected = choice == SchematicConflictChoice.Xml ? "XML wording"
                : choice == SchematicConflictChoice.Native ? "Native wording" : "Shared child contents";
            Assert.IsTrue(result.Merged!.Instances.Skip(1).All(s => s.Items[0].Unpack<SchematicText>().Text.Text_ == expected));
            Assert.AreEqual(choice == SchematicConflictChoice.Native ? 0 : 1, result.NativeOperations.Count);
        }
        choices[choices.Keys.First()] = SchematicConflictChoice.Native;
        var inconsistent = SchematicHierarchyMerge.Resolve(b, x, n, token, choices);
        Assert.IsFalse(inconsistent.CanApply); Assert.IsEmpty(inconsistent.NativeOperations);
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => SchematicHierarchyMerge.Resolve(b, x, n, "stale", choices));
        var altered = x.Clone(); Note(altered.Instances[0], "Later change");
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => SchematicHierarchyMerge.Resolve(b, altered, n, token, choices));
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => SchematicHierarchyMerge.Resolve(b, x, n, token,
            new Dictionary<string, SchematicConflictChoice> { ["unknown"] = SchematicConflictChoice.Xml }));
        var partial = SchematicHierarchyMerge.Resolve(b, x, n, token,
            new Dictionary<string, SchematicConflictChoice> { [choices.Keys.First()] = SchematicConflictChoice.Xml });
        Assert.IsFalse(partial.CanApply); Assert.HasCount(1, partial.Conflicts); Assert.IsEmpty(partial.NativeOperations);
        Assert.AreEqual(token, SchematicHierarchyMerge.SnapshotToken(b, x, n));
    }

    [TestMethod]
    public void ReparentingPreservesNativeChildIdentityAndIndependentRootNotes()
    {
        var b = SchematicHierarchyTopologyTests.Fixture(); var parent = AddBranch(b); var nested = AddBranch(b, parent);
        var x = b.Clone(); var n = b.Clone(); Note(n.Instances[0], "Native root annotation");
        var oldParent = x.Instances.Single(s => s.Metadata.Document.Equals(parent.Metadata.Document));
        var moved = oldParent.Items.Single(i => i.Is(SheetSymbol.Descriptor)).Unpack<SheetSymbol>();
        oldParent.Items.Remove(Any.Pack(moved)); moved.Path = x.Document.SheetPath.Clone(); x.Instances[0].Items.Add(Any.Pack(moved));
        var child = x.Instances.Single(s => s.Metadata.ScreenId.Equals(nested.Metadata.ScreenId));
        child.Metadata.Document = x.Document.Clone(); child.Metadata.Document.SheetPath.Path.Add(moved.Id.Clone());
        var result = SchematicHierarchyMerge.Plan(b, x, n);
        Assert.IsTrue(result.CanApply, result.ErrorMessage);
        Assert.HasCount(2, result.NativeOperations);
        Assert.AreEqual(moved.Id, result.NativeOperations.Single(o => o.Remove is not null).Remove);
        Assert.AreEqual(moved, result.NativeOperations.Single(o => o.Create is not null).Create.Unpack<SheetSymbol>());
        Assert.AreEqual(nested.Metadata.ScreenId, result.Merged!.Instances.Single(s => s.Metadata.Document.Equals(child.Metadata.Document)).Metadata.ScreenId);
    }
}
