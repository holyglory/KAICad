using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicReferenceInventoryTests
{
    [TestMethod]
    public void InventoryUsesExplicitEntriesAndOneOperationAcrossRepeatedSheets()
    {
        var before = SchematicHierarchyTopologyTests.Fixture();
        foreach (var screen in before.Instances)
        {
            screen.Metadata.ReferenceInventory = new();
            screen.Metadata.ReferenceInventory.Allocated.Add(new[] { "R1", "R2", "PREFIX", "#PWR1", "X,1" });
        }
        var after = before.Clone();
        foreach (var screen in after.Instances) screen.Metadata.ReferenceInventory.Allocated.Add("U123");
        Assert.AreEqual(after, SchematicDataXml.Read(SchematicDataXml.Write(after)));
        var operation = SchematicHierarchyDelta.Plan(before, after).Single();
        CollectionAssert.AreEquivalent(after.Instances[0].Metadata.ReferenceInventory.Allocated.ToArray(), operation.SetReferenceInventory.Allocated.ToArray());
        var unchanged = after.Clone();
        foreach (var screen in after.Instances)
        {
            var entries = screen.Metadata.ReferenceInventory.Allocated.Reverse().ToArray();
            screen.Metadata.ReferenceInventory.Allocated.Clear(); screen.Metadata.ReferenceInventory.Allocated.Add(entries);
        }
        Assert.HasCount(0, SchematicHierarchyDelta.Plan(unchanged, after));
        after.Instances[^1].Metadata.ReferenceInventory.Allocated.Add("C999");
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void EmptyInventoryIsExplicitAndMissingDataDoesNotClearIt()
    {
        var before = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        before.Metadata.ReferenceInventory = new(); before.Metadata.ReferenceInventory.Allocated.Add("R1");
        var empty = before.Clone(); empty.Metadata.ReferenceInventory.Allocated.Clear();
        Assert.IsNotNull(SchematicItemDelta.Plan(before, empty).Single().SetReferenceInventory);
        var missing = before.Clone(); missing.Metadata.ReferenceInventory = null;
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, missing));
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(missing, before));
        Assert.HasCount(0, SchematicItemDelta.Plan(missing, missing.Clone()));
        foreach (var entries in new[] { new[] { "R1", "R1" }, new[] { "" }, new[] { "R\0X" } })
        {
            var invalid = empty.Clone(); invalid.Metadata.ReferenceInventory.Allocated.Add(entries);
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, invalid));
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(invalid, invalid));
        }
    }

    [TestMethod]
    public void ConcurrentAllocationHistoryChangesConflictWithoutGuessing()
    {
        var baseline = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        baseline.Metadata.ReferenceInventory = new(); baseline.Metadata.ReferenceInventory.Allocated.Add("R1");
        var xml = baseline.Clone(); xml.Metadata.ReferenceInventory.Allocated.Add("R2");
        var native = baseline.Clone(); native.Metadata.Annotation = SchematicAnnotationTests.Policy();
        baseline.Metadata.Annotation = native.Metadata.Annotation.Clone(); xml.Metadata.Annotation = native.Metadata.Annotation.Clone();
        native.Metadata.Annotation.StartAfter = 500;
        var merged = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.AreEqual(500, merged.Merged!.Metadata.Annotation.StartAfter);
        Assert.IsNotNull(merged.NativeOperations.Single().SetReferenceInventory);
        native.Metadata.ReferenceInventory.Allocated.Add("R3");
        var originalXml = xml.Clone(); var originalNative = native.Clone();
        Assert.IsFalse(SchematicItemMerge.Plan(baseline, xml, native).CanApply);
        Assert.AreEqual(originalXml, xml); Assert.AreEqual(originalNative, native);
        var same = SchematicItemMerge.Plan(baseline, xml, xml.Clone());
        Assert.IsTrue(same.CanApply); Assert.HasCount(0, same.NativeOperations);
    }

    [TestMethod]
    public void UnknownInventoryFieldsNeverDisappear()
    {
        var baseline = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        baseline.Metadata.ReferenceInventory = new(); baseline.Metadata.ReferenceInventory.Allocated.Add("R1");
        var native = baseline.Clone();
        native.Metadata.ReferenceInventory = SchematicReferenceInventory.Parser.ParseFrom(
            native.Metadata.ReferenceInventory.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 1 }).ToArray());
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemMerge.Plan(baseline, baseline.Clone(), native));
    }
}
