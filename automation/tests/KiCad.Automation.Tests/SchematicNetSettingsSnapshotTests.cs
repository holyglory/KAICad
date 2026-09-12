using Kiapi.Common.Project;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicNetSettingsSnapshotTests
{
    [TestMethod]
    public void TypedSchemaRetainsDeclaredValuesAndObservedAssignmentProjection()
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
}
