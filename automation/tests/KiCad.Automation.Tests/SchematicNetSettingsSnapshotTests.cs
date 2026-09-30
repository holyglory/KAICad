using Kiapi.Common.Project;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicNetSettingsSnapshotTests
{
    internal static SchematicNetSettings Settings()
    {
        var settings = new SchematicNetSettings
        {
            DefaultClass = new NetClass { Name = "Default", Priority = int.MaxValue,
                Type = NetClassType.NctExplicit, Board = new() { Clearance = new() { ValueNm = 200000 } },
                Schematic = new() { WireWidth = new() { ValueNm = 25400 } } }
        };
        settings.Classes.Add(new NetClass { Name = "電源", Priority = 1, Type = NetClassType.NctExplicit,
            Board = new() { TrackWidth = new() { ValueNm = 500000 }, TuningProfile = "power" }, Schematic = new() });
        settings.LabelAssignments.Add("/VCC", new SchematicNetClassNames { Names = { "電源" } });
        settings.Patterns.Add(new SchematicNetClassPattern { Pattern = "USB*", NetClass = "Default" });
        settings.Patterns.Add(new SchematicNetClassPattern { Pattern = "V*", NetClass = "電源" });
        settings.NetColors.Add("/VCC", new() { R = 1, A = 1 });
        settings.ChainNetclasses.Add("power-rail", "電源");
        return settings;
    }

    private static SchematicHierarchyData Fixture()
    {
        var snapshot = SchematicHierarchyTopologyTests.Fixture();
        foreach (var screen in snapshot.Instances) screen.Metadata.NetSettings = Settings();
        return snapshot;
    }

    [TestMethod]
    public void TypedSchemaRetainsDeclaredValuesAndObservedAssignmentProjection()
    {
        var settings = Settings();
        var snapshot = SchematicHierarchyTopologyTests.Fixture();
        foreach (var screen in snapshot.Instances) screen.Metadata.NetSettings = settings.Clone();
        var decoded = (SchematicHierarchyData)SchematicDataXml.Read(SchematicDataXml.Write(snapshot));
        Assert.AreEqual(snapshot, decoded);
        foreach (var screen in decoded.Instances)
        {
            Assert.IsNull(screen.Metadata.NetSettings.Classes[0].Board.Clearance,
                "An inherited parameter must not become an explicit effective value.");
            Assert.AreEqual("USB*", screen.Metadata.NetSettings.Patterns[0].Pattern);
            Assert.AreEqual("V*", screen.Metadata.NetSettings.Patterns[1].Pattern);
            Assert.AreEqual("電源", screen.Metadata.NetSettings.LabelAssignments["/VCC"].Names[0]);
        }
    }

    [TestMethod]
    public void ProjectDeltaIsSingleAndNeverWritesTheObservedLabelCache()
    {
        var before = Fixture(); var desired = before.Clone();
        foreach (var screen in desired.Instances)
        {
            screen.Metadata.NetSettings.DefaultClass.Board.TrackWidth = new() { ValueNm = 320000 };
            screen.Metadata.NetSettings.LabelAssignments.Clear();
        }
        var operations = SchematicHierarchyDelta.Plan(before, desired);
        Assert.HasCount(1, operations);
        Assert.IsNotNull(operations[0].SetNetSettings);
        Assert.HasCount(0, operations[0].SetNetSettings.LabelAssignments);
        Assert.HasCount(0, SchematicHierarchyDelta.Plan(desired, desired.Clone()));
        var projectionOnly = before.Clone();
        foreach (var screen in projectionOnly.Instances) screen.Metadata.NetSettings.LabelAssignments.Clear();
        Assert.HasCount(0, SchematicHierarchyDelta.Plan(before, projectionOnly));
        desired.Instances[^1].Metadata.NetSettings.DefaultClass.Priority = 42;
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, desired));
    }

    [TestMethod]
    public void IndependentDeclaredPropertiesMergeButConflictingPatternsDoNot()
    {
        var before = Fixture(); var xml = before.Clone(); var native = before.Clone();
        foreach (var screen in xml.Instances)
        {
            screen.Metadata.NetSettings.DefaultClass.Board.Clearance = new() { ValueNm = 300000 };
            screen.Metadata.NetSettings.Classes[0].Board.TrackWidth = new() { ValueNm = 600000 };
        }
        foreach (var screen in native.Instances)
        {
            screen.Metadata.NetSettings.DefaultClass.Schematic.WireWidth = new() { ValueNm = 50800 };
            screen.Metadata.NetSettings.Classes[0].Priority = 2;
        }
        var merged = SchematicHierarchyMerge.Plan(before, xml, native);
        Assert.IsTrue(merged.CanApply);
        var result = merged.Merged!.Instances[0].Metadata.NetSettings;
        Assert.AreEqual(300000L, result.DefaultClass.Board.Clearance.ValueNm);
        Assert.AreEqual(50800L, result.DefaultClass.Schematic.WireWidth.ValueNm);
        Assert.AreEqual(600000L, result.Classes[0].Board.TrackWidth.ValueNm);
        Assert.AreEqual(2, result.Classes[0].Priority);
        foreach (var screen in xml.Instances) screen.Metadata.NetSettings.Patterns[0].Pattern = "XML*";
        foreach (var screen in native.Instances) screen.Metadata.NetSettings.Patterns[0].Pattern = "NATIVE*";
        var originalXml = xml.Clone(); var originalNative = native.Clone();
        Assert.IsFalse(SchematicHierarchyMerge.Plan(before, xml, native).CanApply);
        Assert.AreEqual(originalXml, xml); Assert.AreEqual(originalNative, native);
    }

    [TestMethod]
    public void MissingCoverageAndInvalidDeclaredIdentityCannotBecomeDefaultValues()
    {
        var before = Fixture().Instances[0];
        var absent = before.Clone(); absent.Metadata.NetSettings = null;
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, absent));
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(absent, before));
        foreach (int kind in Enumerable.Range(0, 3))
        {
            var invalid = before.Clone();
            if (kind == 0) invalid.Metadata.NetSettings.DefaultClass = null;
            if (kind == 1) invalid.Metadata.NetSettings.Classes[0].Type = NetClassType.NctImplicit;
            if (kind == 2) invalid.Metadata.NetSettings.Classes.Add(invalid.Metadata.NetSettings.Classes[0].Clone());
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, invalid));
        }
    }
}
