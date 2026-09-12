using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicBomSettingsTests
{
    internal static SchematicBomSettings Settings()
    {
        var view = new SchematicBomView { Name = "電源 & Grouped", SortField = "Value", SortAscending = false,
            Filter = "R*|C*", FilterScope = SchematicBomFilterScope.SbfsVisible,
            GroupSymbols = true, ExcludeDnp = true, IncludeExcludedFromBom = true,
            Fields = { new SchematicBomField { Name = "Value", Label = "値 & rating", Show = true, GroupBy = true },
                new SchematicBomField { Name = "Reference", Label = "Designators", Show = false, GroupBy = false } } };
        var format = new SchematicBomFormat { Name = "Assembly", FieldDelimiter = "\t;", StringDelimiter = "\"",
            ReferenceDelimiter = ", ", ReferenceRangeDelimiter = "–", KeepTabs = true,
            KeepLineBreaks = true, IncludeByteOrderMark = true };
        return new() { ExportFilename = "${PROJECTNAME}-組立.csv", CurrentView = view, CurrentFormat = format,
            SavedViews = { view.Clone(), new SchematicBomView { Name = "", FilterScope = SchematicBomFilterScope.SbfsAll } },
            SavedFormats = { format.Clone(), new SchematicBomFormat { Name = "" } } };
    }

    private static SchematicHierarchyData Fixture()
    {
        var value = SchematicHierarchyTopologyTests.Fixture();
        foreach (var screen in value.Instances) screen.Metadata.BomSettings = Settings();
        return value;
    }

    [TestMethod]
    public void EveryPersistedFieldHasTypedXmlAndOneProjectDelta()
    {
        var before = Fixture(); var desired = before.Clone();
        foreach (var screen in desired.Instances)
        {
            var bom = screen.Metadata.BomSettings;
            bom.ExportFilename = "fab/${PROJECTNAME}.tsv";
            bom.CurrentView.SortField = "Reference";
            bom.CurrentView.SortAscending = true;
            bom.CurrentView.Filter = "";
            bom.CurrentView.FilterScope = SchematicBomFilterScope.SbfsAll;
            bom.CurrentView.Fields[0].Label = "Power ≤ 1W";
            bom.CurrentFormat.KeepTabs = false;
            bom.SavedViews.Add(bom.CurrentView.Clone());
            bom.SavedFormats.Add(bom.CurrentFormat.Clone());
        }
        var xml = SchematicDataXml.Write(desired);
        Assert.AreEqual(desired, SchematicDataXml.Read(xml));
        var delta = SchematicHierarchyDelta.Plan(before, desired);
        Assert.HasCount(1, delta);
        Assert.AreEqual(desired.Instances[0].Metadata.BomSettings, delta[0].SetBomSettings);
        Assert.HasCount(0, SchematicHierarchyDelta.Plan(desired, desired.Clone()));
        desired.Instances[^1].Metadata.BomSettings.ExportFilename = "conflicting.csv";
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, desired));
    }

    [TestMethod]
    public void EmptyAndOrderedCollectionsDoNotMeanMissingCoverage()
    {
        var before = Fixture().Instances[0]; var empty = before.Clone();
        empty.Metadata.BomSettings.CurrentView.Fields.Clear();
        empty.Metadata.BomSettings.SavedViews.Clear();
        empty.Metadata.BomSettings.SavedFormats.Clear();
        empty.Metadata.BomSettings.ExportFilename = "";
        Assert.HasCount(1, SchematicItemDelta.Plan(before, empty));
        Assert.AreEqual(empty, SchematicDataXml.Read(SchematicDataXml.Write(empty)));
        var reordered = before.Clone();
        reordered.Metadata.BomSettings.CurrentView.Fields.Clear();
        reordered.Metadata.BomSettings.CurrentView.Fields.Add(before.Metadata.BomSettings.CurrentView.Fields.Reverse().Select(f => f.Clone()));
        Assert.HasCount(1, SchematicItemDelta.Plan(before, reordered));
        var missing = before.Clone(); missing.Metadata.BomSettings = null;
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, missing));
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(missing, before));
        Assert.HasCount(0, SchematicItemDelta.Plan(missing, missing.Clone()));
    }

    [TestMethod]
    public void UnknownAndIncompleteSettingsAreRejectedBeforePlanning()
    {
        var before = Fixture().Instances[0];
        foreach (int kind in Enumerable.Range(0, 4))
        {
            var invalid = before.Clone(); var bom = invalid.Metadata.BomSettings;
            if (kind == 0) bom.CurrentView = null;
            else if (kind == 1) bom.CurrentFormat = null;
            else if (kind == 2) bom.SavedViews[0].FilterScope = (SchematicBomFilterScope)99;
            else bom.SavedFormats[0] = SchematicBomFormat.Parser.ParseFrom(
                bom.SavedFormats[0].ToByteArray().Concat(new byte[] { 0xa0, 0x06, 1 }).ToArray());
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, invalid));
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemMerge.Plan(before, before.Clone(), invalid));
        }
    }

    [TestMethod]
    public void IndependentPreferencesMergeAndCompetingColumnOrdersPause()
    {
        var before = Fixture(); var xml = before.Clone(); var native = before.Clone();
        foreach (var screen in xml.Instances) screen.Metadata.BomSettings.ExportFilename = "requested.csv";
        foreach (var screen in native.Instances)
        {
            screen.Metadata.BomSettings.CurrentView.Filter = "U*";
            screen.Metadata.BomSettings.CurrentFormat.StringDelimiter = "'";
        }
        var merged = SchematicHierarchyMerge.Plan(before, xml, native);
        Assert.IsTrue(merged.CanApply);
        foreach (var screen in merged.Merged!.Instances)
        {
            Assert.AreEqual("requested.csv", screen.Metadata.BomSettings.ExportFilename);
            Assert.AreEqual("U*", screen.Metadata.BomSettings.CurrentView.Filter);
            Assert.AreEqual("'", screen.Metadata.BomSettings.CurrentFormat.StringDelimiter);
        }
        foreach (var screen in xml.Instances) screen.Metadata.BomSettings.CurrentView.Fields.RemoveAt(0);
        foreach (var screen in native.Instances) screen.Metadata.BomSettings.CurrentView.Fields[0].Label = "Manual label";
        var savedXml = xml.Clone(); var savedNative = native.Clone();
        Assert.IsFalse(SchematicHierarchyMerge.Plan(before, xml, native).CanApply);
        Assert.AreEqual(savedXml, xml); Assert.AreEqual(savedNative, native);
    }
}
