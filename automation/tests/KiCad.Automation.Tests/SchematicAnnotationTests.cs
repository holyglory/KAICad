using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicAnnotationTests
{
    internal static SchematicAnnotationSettings Policy() => new()
    {
        StartAfter = 0, Order = SchematicAnnotationOrder.SaoXPosition,
        Method = SchematicAnnotationMethod.SamIncremental, ReuseDesignators = true
    };

    [TestMethod]
    public void AnnotationRoundTripsAndIsAppliedOnceAcrossRepeatedSheets()
    {
        var before = SchematicHierarchyTopologyTests.Fixture();
        foreach (var screen in before.Instances) screen.Metadata.Annotation = Policy();
        var after = before.Clone();
        foreach (var screen in after.Instances)
        {
            screen.Metadata.Annotation.StartAfter = 301;
            screen.Metadata.Annotation.Order = SchematicAnnotationOrder.SaoYPosition;
            screen.Metadata.Annotation.Method = SchematicAnnotationMethod.SamSheetTimes1000;
            screen.Metadata.Annotation.ReuseDesignators = false;
        }
        Assert.AreEqual(after, SchematicDataXml.Read(SchematicDataXml.Write(after)));
        Assert.AreEqual(after.Instances[0].Metadata.Annotation, SchematicHierarchyDelta.Plan(before, after).Single().SetAnnotation);
        Assert.HasCount(0, SchematicHierarchyDelta.Plan(after, after.Clone()));
        after.Instances[^1].Metadata.Annotation.StartAfter++;
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void UnsupportedOrUnavailablePoliciesCannotBecomeDefaults()
    {
        var before = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        before.Metadata.Annotation = Policy();
        foreach (var value in new[] { 0, -1, 9 })
        {
            var after = before.Clone(); after.Metadata.Annotation.Order = (SchematicAnnotationOrder)value;
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, after));
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(after, after));
            after = before.Clone(); after.Metadata.Annotation.Method = (SchematicAnnotationMethod)value;
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, after));
        }
        var missing = before.Clone(); missing.Metadata.Annotation = null;
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, missing));
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(missing, before));
        Assert.HasCount(0, SchematicItemDelta.Plan(missing, missing.Clone()));
    }

    [TestMethod]
    public void IndependentPolicyFieldsMergeWithoutLosingNativeChanges()
    {
        var baseline = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        baseline.Metadata.Annotation = Policy();
        var xml = baseline.Clone(); xml.Metadata.Annotation.StartAfter = 108;
        var native = baseline.Clone(); native.Metadata.Annotation.ReuseDesignators = false;
        var merged = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.AreEqual(108, merged.Merged!.Metadata.Annotation.StartAfter);
        Assert.IsFalse(merged.Merged.Metadata.Annotation.ReuseDesignators);
        Assert.AreEqual(merged.Merged.Metadata.Annotation, merged.NativeOperations.Single().SetAnnotation);
        var same = SchematicItemMerge.Plan(baseline, merged.Merged, merged.Merged.Clone());
        Assert.IsTrue(same.CanApply); Assert.HasCount(0, same.NativeOperations);
        native.Metadata.Annotation.StartAfter = 109;
        var originalXml = xml.Clone(); var originalNative = native.Clone();
        Assert.IsFalse(SchematicItemMerge.Plan(baseline, xml, native).CanApply);
        Assert.AreEqual(originalXml, xml); Assert.AreEqual(originalNative, native);
    }

    [TestMethod]
    public void UnknownPolicyFieldsAreNotDiscardedByXmlOrMerge()
    {
        var baseline = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        baseline.Metadata.Annotation = Policy();
        var native = baseline.Clone();
        native.Metadata.Annotation = SchematicAnnotationSettings.Parser.ParseFrom(
            native.Metadata.Annotation.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 1 }).ToArray());
        var original = native.Clone();
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemMerge.Plan(baseline, baseline.Clone(), native));
        Assert.ThrowsExactly<AutomationException>(() => SchematicDataXml.Write(native));
        Assert.AreEqual(original, native);
    }

    [TestMethod]
    public void ExactPersistedSignedStartNumbersRoundTrip()
    {
        var before = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        before.Metadata.Annotation = Policy();
        foreach (int value in new[] { int.MinValue, -1, 0, 17, int.MaxValue })
        {
            var after = before.Clone(); after.Metadata.Annotation.StartAfter = value;
            Assert.AreEqual(after, SchematicDataXml.Read(SchematicDataXml.Write(after)));
            Assert.HasCount(value == 0 ? 0 : 1, SchematicItemDelta.Plan(before, after));
        }
    }
}
