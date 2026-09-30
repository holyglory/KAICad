using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicNetChainClassesTests
{
    private static SchematicNetChainClassState State() => new()
    {
        Definitions = { "Used", "Unused" }, Assignments = { ["Chain"] = "Used" }
    };

    [TestMethod]
    public void TypedXmlPreservesUnusedClassesAndOneProjectWideDelta()
    {
        var before = SchematicHierarchyTopologyTests.Fixture();
        foreach (var screen in before.Instances) screen.Metadata.NetChainClasses = State();
        var after = before.Clone();
        foreach (var screen in after.Instances) screen.Metadata.NetChainClasses.Definitions.Add("Empty & reusable");
        string xml = SchematicDataXml.Write(after);
        Assert.AreEqual(after, SchematicDataXml.Read(xml));
        var operations = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, operations.Count);
        CollectionAssert.AreEquivalent(new[] { "Used", "Unused", "Empty & reusable" }, operations.Single().ReplaceNetChainClasses.Definitions.ToArray());
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(after, after).Count);
        Assert.AreEqual(xml, SchematicDataXml.Write(after));
        after.Instances[^1].Metadata.NetChainClasses.Definitions.Add("Different owner");
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void MalformedAndUnavailableStateCannotClearOrInventClasses()
    {
        foreach (Action<SchematicNetChainClassState> corrupt in new Action<SchematicNetChainClassState>[]
        {
            s => s.Definitions.Add("Used"), s => s.Definitions.Add(""), s => s.Definitions.Add("bad\0name"),
            s => s.Assignments["Chain"] = "Missing", s => s.Assignments[""] = "Used",
            s => s.Assignments["bad\0chain"] = "Used"
        })
        {
            var value = State(); corrupt(value);
            Assert.ThrowsExactly<AutomationException>(() => SchematicNetChainClasses.Normalize(value));
        }
        var future = SchematicNetChainClassState.Parser.ParseFrom(State().ToByteArray().Concat(new byte[] { 0xf8, 0x07, 0x01 }).ToArray());
        Assert.ThrowsExactly<AutomationException>(() => SchematicNetChainClasses.Normalize(future));
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        foreach (var screen in after.Instances) screen.Metadata.NetChainClasses = State();
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(after, before));
        Assert.IsNotNull(SchematicNetChainClasses.Normalize(new())); // explicit empty is valid
    }

    [TestMethod]
    public void IndependentClassesAndAssignmentsMergeButConflictingOwnersDoNot()
    {
        var before = State(); var xml = before.Clone(); var native = before.Clone();
        xml.Definitions.Add("Xml"); xml.Assignments.Add("XmlChain", "Xml");
        native.Definitions.Add("Native"); native.Assignments.Add("NativeChain", "Native");
        Assert.IsTrue(SchematicNetChainClasses.Merge(before, xml, native, out var merged));
        Assert.AreEqual("Xml", merged!.Assignments["XmlChain"]);
        Assert.AreEqual("Native", merged.Assignments["NativeChain"]);
        CollectionAssert.Contains(merged.Definitions.ToArray(), "Unused");
        xml.Assignments["Chain"] = "Xml"; native.Assignments["Chain"] = "Native";
        Assert.IsFalse(SchematicNetChainClasses.Merge(before, xml, native, out merged)); Assert.IsNull(merged);
        xml = before.Clone(); native = before.Clone();
        xml.Definitions.Remove("Unused"); native.Assignments.Add("NewOwner", "Unused");
        Assert.IsFalse(SchematicNetChainClasses.Merge(before, xml, native, out merged)); Assert.IsNull(merged);
        var same = before.Clone(); same.Definitions.Clear(); same.Definitions.Add(before.Definitions.Reverse());
        Assert.IsTrue(SchematicNetChainClasses.Merge(before, same, native, out merged));
        Assert.AreEqual(native, merged, "Unchanged semantic state preserves the chosen representation.");
    }

    [TestMethod]
    public void MetadataMergeCombinesNativeNotesWithReusableClasses()
    {
        var before = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone(); before.Metadata.NetChainClasses = State();
        var xml = before.Clone(); xml.Metadata.NetChainClasses.Definitions.Add("Future group");
        var native = before.Clone(); native.Metadata.TextVariables["NOTE"] = "Keep the connector accessible";
        var result = SchematicItemMerge.Plan(before, xml, native);
        Assert.IsTrue(result.CanApply);
        Assert.AreEqual(native.Metadata.TextVariables["NOTE"], result.Merged!.Metadata.TextVariables["NOTE"]);
        Assert.IsNotNull(result.NativeOperations.Single().ReplaceNetChainClasses);
    }

    [TestMethod]
    public void ClassNamesRemainLiteralAcrossUnicodeXmlEscapingAndOrdering()
    {
        var value = new SchematicNetChainClassState
        {
            Definitions = { "電源", "RF_µwave", "Keep <A> & 'B'", "🛠", "  literal spacing  " },
            Assignments = { ["階層/Chain:1"] = "電源", ["Quoted \"chain\""] = "Keep <A> & 'B'" }
        };
        string xml = SchematicDataXml.Write(value);
        Assert.AreEqual(value, SchematicDataXml.Read(xml));
        var before = SchematicHierarchyTopologyTests.Fixture();
        foreach (var sheet in before.Instances) sheet.Metadata.NetChainClasses = value.Clone();
        var reordered = before.Clone();
        foreach (var sheet in reordered.Instances)
        {
            sheet.Metadata.NetChainClasses.Definitions.Clear();
            sheet.Metadata.NetChainClasses.Definitions.Add(value.Definitions.Reverse());
        }
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(before, reordered).Count,
            "A different presentation order cannot create a native edit.");
        Assert.AreEqual(xml, SchematicDataXml.Write(value));
    }
}
