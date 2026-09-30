using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicSymbolProjectSettingsTests
{
    internal static SchematicFieldTemplates Templates() => new()
    {
        Fields = { new SchematicFieldTemplate { Name = "電源 & Vendor", Visible = true },
            new SchematicFieldTemplate { Name = "Documentation", Url = true } }
    };

    private static SchematicHierarchyData Fixture()
    {
        var value = SchematicHierarchyTopologyTests.Fixture();
        foreach (var screen in value.Instances)
        {
            screen.Metadata.FieldTemplates = Templates();
            screen.Metadata.SymbolComparison = new() { MissingFields = true, PinAltFunctions = true };
        }
        return value;
    }

    [TestMethod]
    public void TypedSettingsRoundTripWithOneProjectOperationPerOwner()
    {
        var before = Fixture(); var after = before.Clone();
        foreach (var screen in after.Instances)
        {
            screen.Metadata.FieldTemplates.Fields.Add(new SchematicFieldTemplate { Name = "Instruction", Visible = true });
            foreach (var field in SchematicSymbolComparisonSettings.Descriptor.Fields.InFieldNumberOrder())
                field.Accessor.SetValue(screen.Metadata.SymbolComparison, !(bool)field.Accessor.GetValue(screen.Metadata.SymbolComparison));
        }
        var xml = SchematicDataXml.Write(after);
        Assert.AreEqual(after, SchematicDataXml.Read(xml));
        var operations = SchematicHierarchyDelta.Plan(before, after);
        Assert.HasCount(2, operations);
        Assert.AreEqual(after.Instances[0].Metadata.FieldTemplates, operations.Single(o => o.SetFieldTemplates is not null).SetFieldTemplates);
        Assert.AreEqual(after.Instances[0].Metadata.SymbolComparison, operations.Single(o => o.SetSymbolComparison is not null).SetSymbolComparison);
        Assert.HasCount(0, SchematicHierarchyDelta.Plan(after, after.Clone()));
        after.Instances[^1].Metadata.SymbolComparison.MissingFields = !after.Instances[^1].Metadata.SymbolComparison.MissingFields;
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void EmptyTemplatesAreDifferentFromMissingCoverageAndOrderIsPreserved()
    {
        var before = Fixture().Instances[0]; var after = before.Clone();
        after.Metadata.FieldTemplates.Fields.Clear();
        Assert.HasCount(0, SchematicItemDelta.Plan(before, after).Single().SetFieldTemplates.Fields);
        var reversed = before.Clone();
        reversed.Metadata.FieldTemplates.Fields.Clear();
        reversed.Metadata.FieldTemplates.Fields.Add(before.Metadata.FieldTemplates.Fields.Reverse().Select(f => f.Clone()));
        Assert.AreEqual(reversed.Metadata.FieldTemplates, SchematicItemDelta.Plan(before, reversed).Single().SetFieldTemplates);
        Assert.AreEqual(reversed, SchematicDataXml.Read(SchematicDataXml.Write(reversed)));
        foreach (bool template in new[] { true, false })
        {
            var missing = before.Clone();
            if (template) missing.Metadata.FieldTemplates = null; else missing.Metadata.SymbolComparison = null;
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, missing));
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(missing, before));
            Assert.HasCount(0, SchematicItemDelta.Plan(missing, missing.Clone()));
        }
    }

    [TestMethod]
    public void InvalidNamesAndUnknownFieldsAreRejectedWithoutDroppingData()
    {
        var before = Fixture().Instances[0];
        foreach (string name in new[] { "", "bad\0name", "rEfErEnCe", "value", "Footprint", "DATASHEET", "Description", "Documentation" })
        {
            var invalid = before.Clone();
            invalid.Metadata.FieldTemplates.Fields.Add(new SchematicFieldTemplate { Name = name });
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, invalid));
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(invalid, invalid));
        }
        var caseSensitive = before.Clone();
        caseSensitive.Metadata.FieldTemplates.Fields.Add(new SchematicFieldTemplate { Name = "documentation" });
        Assert.HasCount(1, SchematicItemDelta.Plan(before, caseSensitive));
        foreach (bool template in new[] { true, false })
        {
            var unknown = before.Clone();
            byte[] tail = [0xa0, 0x06, 1];
            if (template) unknown.Metadata.FieldTemplates.Fields[0] = SchematicFieldTemplate.Parser.ParseFrom(
                unknown.Metadata.FieldTemplates.Fields[0].ToByteArray().Concat(tail).ToArray());
            else unknown.Metadata.SymbolComparison = SchematicSymbolComparisonSettings.Parser.ParseFrom(
                unknown.Metadata.SymbolComparison.ToByteArray().Concat(tail).ToArray());
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemMerge.Plan(before, before.Clone(), unknown));
        }
    }

    [TestMethod]
    public void IndependentFlagsMergeButCompetingTemplateOrdersRemainConflicts()
    {
        var before = Fixture().Instances[0]; var xml = before.Clone(); var native = before.Clone();
        xml.Metadata.SymbolComparison.FieldTexts = true;
        native.Metadata.SymbolComparison.ExtraFields = true;
        var merged = SchematicItemMerge.Plan(before, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.IsTrue(merged.Merged!.Metadata.SymbolComparison.FieldTexts);
        Assert.IsTrue(merged.Merged.Metadata.SymbolComparison.ExtraFields);
        Assert.HasCount(1, merged.NativeOperations);
        xml.Metadata.FieldTemplates.Fields.Add(new SchematicFieldTemplate { Name = "Desired" });
        native.Metadata.FieldTemplates.Fields.Add(new SchematicFieldTemplate { Name = "Manual" });
        var originalXml = xml.Clone(); var originalNative = native.Clone();
        Assert.IsFalse(SchematicItemMerge.Plan(before, xml, native).CanApply);
        Assert.AreEqual(originalXml, xml); Assert.AreEqual(originalNative, native);
        var same = SchematicItemMerge.Plan(before, xml, xml.Clone());
        Assert.IsTrue(same.CanApply); Assert.HasCount(0, same.NativeOperations);
    }
}
