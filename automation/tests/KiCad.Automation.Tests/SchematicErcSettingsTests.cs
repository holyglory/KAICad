using Google.Protobuf;
using Kiapi.Common.Types;
using Kiapi.Schematic;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicErcSettingsTests
{
    private static readonly HashSet<ErcErrorType> Rules = [(ErcErrorType)3, (ErcErrorType)54, (ErcErrorType)55];

    [TestMethod]
    public void TypedXmlRetainsPolicyOrderedPinPairsAndExclusionReferences()
    {
        var settings = Fixture();
        string xml = SchematicDataXml.Write(settings);
        StringAssert.Contains(xml, "rule_severities");
        StringAssert.Contains(xml, "pin_map");
        StringAssert.Contains(xml, "exclusions");
        var restored = (SchematicErcSettings)SchematicDataXml.Read(xml);
        Assert.AreEqual(settings, restored);
        SchematicErcSettingsValidation.Validate(restored, Rules);
        Assert.AreEqual("電源 & timing\r\nuser intent", restored.Exclusions[0].Comment);
        Assert.AreNotEqual(restored.PinMap[1].Conflict, restored.PinMap[12].Conflict,
            "Ordered matrix entries must not be silently symmetrized.");
    }

    [TestMethod]
    public void MetadataDistinguishesMissingErcStateFromExplicitEmptyExclusions()
    {
        var missing = new SchematicMetadata();
        var explicitState = Fixture(); explicitState.Exclusions.Clear();
        var present = new SchematicMetadata { ErcSettings = explicitState };
        Assert.IsNull(((SchematicMetadata)SchematicDataXml.Read(SchematicDataXml.Write(missing))).ErcSettings);
        var restored = (SchematicMetadata)SchematicDataXml.Read(SchematicDataXml.Write(present));
        Assert.IsNotNull(restored.ErcSettings);
        Assert.IsEmpty(restored.ErcSettings.Exclusions);
        SchematicErcSettingsValidation.Validate(restored.ErcSettings, Rules);
    }

    [TestMethod]
    public void IncompleteUnknownDuplicateAndUnrepresentableValuesAreRejected()
    {
        foreach (Action<SchematicErcSettings> change in new Action<SchematicErcSettings>[]
        {
            value => value.RuleSeverities.RemoveAt(0),
            value => value.RuleSeverities.Add(value.RuleSeverities[0].Clone()),
            value => value.RuleSeverities[0].RuleType = (ErcErrorType)999,
            value => value.RuleSeverities[0].Severity = RuleSeverity.RsExclusion,
            value => value.PinMap.RemoveAt(0),
            value => value.PinMap[0] = value.PinMap[1].Clone(),
            value => value.PinMap[0].First = (ElectricalPinType)0,
            value => value.PinMap[0].Conflict = (SchematicErcPinConflict)999,
            value => value.Exclusions.Add(value.Exclusions[0].Clone()),
            value => value.Exclusions[0].Comment = "invalid\0comment",
            value => value.Exclusions[0].Marker.Items[0].Value = Guid.Empty.ToString("D"),
            value => value.Exclusions[0].Marker.Position.XNm = 1,
            value => value.Exclusions[0].Marker.Position.YNm = long.MaxValue,
            value => value.Exclusions[0].Marker.SheetSpecificPath = new SheetPath(),
        })
        {
            var settings = Fixture(); change(settings);
            Assert.ThrowsExactly<AutomationException>(() => SchematicErcSettingsValidation.Validate(settings, Rules));
        }
        Assert.ThrowsExactly<AutomationException>(() => SchematicErcSettingsValidation.Validate(Fixture(), new HashSet<ErcErrorType>()));
    }

    [TestMethod]
    public void ProjectErcChangesProduceOneOperationAcrossRepeatedSheets()
    {
        var before = SchematicHierarchyTopologyTests.Fixture();
        foreach (var screen in before.Instances) screen.Metadata.ErcSettings = Fixture();
        var desired = before.Clone();
        foreach (var screen in desired.Instances)
        {
            screen.Metadata.ErcSettings.RuleSeverities[0].Severity = RuleSeverity.RsError;
            screen.Metadata.ErcSettings.Exclusions[0].Comment = "Place near heat path";
        }
        var operations = SchematicHierarchyDelta.Plan(before, desired);
        Assert.AreEqual(1, operations.Count);
        Assert.IsNotNull(operations[0].SetErcSettings);
        Assert.IsTrue(SchematicErcSettingsValidation.Same(desired.Instances[0].Metadata.ErcSettings, operations[0].SetErcSettings));
        desired.Instances[^1].Metadata.ErcSettings.Exclusions[0].Comment = "conflicting shared policy";
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, desired));
    }

    [TestMethod]
    public void ReorderedPoliciesAreNoOpsAndMissingCoverageCannotInventDefaults()
    {
        var before = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        before.Metadata.ErcSettings = Fixture();
        var after = before.Clone();
        var rules = after.Metadata.ErcSettings.RuleSeverities.Reverse().ToArray();
        var pins = after.Metadata.ErcSettings.PinMap.Reverse().ToArray();
        after.Metadata.ErcSettings.RuleSeverities.Clear(); after.Metadata.ErcSettings.RuleSeverities.Add(rules);
        after.Metadata.ErcSettings.PinMap.Clear(); after.Metadata.ErcSettings.PinMap.Add(pins);
        Assert.IsEmpty(SchematicItemDelta.Plan(before, after));
        var missing = before.Clone(); missing.Metadata.ErcSettings = null;
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, missing));
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(missing, before));
        after.Metadata.ErcSettings.PinMap.RemoveAt(0);
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, after));
    }

    [TestMethod]
    public void IndependentErcEditsMergeWhileCompetingCommentsRemainConflicts()
    {
        var before = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        before.Metadata.ErcSettings = Fixture();
        var xml = before.Clone(); xml.Metadata.ErcSettings.RuleSeverities[0].Severity = RuleSeverity.RsError;
        var native = before.Clone(); native.Metadata.ErcSettings.PinMap[0].Conflict = (SchematicErcPinConflict)3;
        var merged = SchematicItemMerge.Plan(before, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.AreEqual(RuleSeverity.RsError, merged.Merged!.Metadata.ErcSettings.RuleSeverities[0].Severity);
        Assert.AreEqual((SchematicErcPinConflict)3, merged.Merged.Metadata.ErcSettings.PinMap[0].Conflict);
        Assert.IsNotNull(merged.NativeOperations.Single().SetErcSettings);
        xml.Metadata.ErcSettings.Exclusions[0].Comment = "XML instruction";
        native.Metadata.ErcSettings.Exclusions[0].Comment = "Native instruction";
        var unchangedXml = xml.Clone(); var unchangedNative = native.Clone();
        var conflict = SchematicItemMerge.Plan(before, xml, native);
        Assert.IsFalse(conflict.CanApply);
        Assert.AreEqual(unchangedXml, xml); Assert.AreEqual(unchangedNative, native);
    }

    [TestMethod]
    public void UnchangedErcRepresentationSurvivesReverseMergeWithoutFileChurn()
    {
        var before = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        before.Metadata.ErcSettings = Fixture();
        var rules = before.Metadata.ErcSettings.RuleSeverities.Reverse().ToArray();
        var pins = before.Metadata.ErcSettings.PinMap.Reverse().ToArray();
        before.Metadata.ErcSettings.RuleSeverities.Clear(); before.Metadata.ErcSettings.RuleSeverities.Add(rules);
        before.Metadata.ErcSettings.PinMap.Clear(); before.Metadata.ErcSettings.PinMap.Add(pins);
        var native = before.Clone(); native.Metadata.TextVariables.Add("NATIVE_NOTE", "Keep user layout");
        var merged = SchematicItemMerge.Plan(before, before.Clone(), native);
        Assert.IsTrue(merged.CanApply);
        Assert.AreEqual(native, merged.Merged);
        Assert.IsEmpty(merged.NativeOperations);
        Assert.AreEqual(SchematicDataXml.Write(native), SchematicDataXml.Write(merged.Merged!));
    }

    internal static SchematicErcSettings Fixture()
    {
        var result = new SchematicErcSettings();
        foreach (var type in Rules.OrderBy(value => (int)value))
            result.RuleSeverities.Add(new ErcSeveritySetting { RuleType = type, Severity = RuleSeverity.RsWarning });
        var types = Enum.GetValues<ElectricalPinType>().Where(type => (int)type != 0).ToArray();
        foreach (var first in types)
        foreach (var second in types)
            result.PinMap.Add(new SchematicErcPinRule
            { First = first, Second = second, Conflict = (SchematicErcPinConflict)(first == second ? 1 : 2) });
        result.PinMap[1].Conflict = (SchematicErcPinConflict)3;
        var root = new KIID { Value = "11111111-1111-4111-8111-111111111111" };
        var path = new SheetPath(); path.Path.Add(root);
        var marker = new ErcMarker
        {
            ErrorType = (ErcErrorType)3, Position = new Vector2 { XNm = 100, YNm = -200 },
            SheetSpecificPath = path.Clone(), MainItemSheetPath = path.Clone(),
            Child = new ErcSymbolChildReference { TextValue = "Power field" }
        };
        marker.Items.Add(new KIID { Value = "22222222-2222-4222-8222-222222222222" });
        result.Exclusions.Add(new ErcExclusion { Marker = marker, Comment = "電源 & timing\r\nuser intent" });
        return result;
    }
}
