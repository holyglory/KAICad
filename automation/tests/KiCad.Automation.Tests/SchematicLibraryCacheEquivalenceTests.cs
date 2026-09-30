using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicLibraryCacheEquivalenceTests
{
    [TestMethod]
    public void EnumerationIsNotAnEditButPinPropertiesIdsAndMultiplicityRemainExact()
    {
        var (design, _) = SchematicPartSymbolTests.Fixture();
        var original = design.PartSymbols!.Single().Symbol;
        var reordered = original.Clone();
        var children = reordered.Definition.Items.Reverse().ToArray();
        reordered.Definition.Items.Clear(); reordered.Definition.Items.Add(children);
        Assert.IsTrue(SchematicLibraryCacheEquivalence.Equal(original, reordered));
        foreach (string change in new[] { "id", "name", "position", "unit", "style", "duplicate", "delete", "cache", "spacing" })
        {
            var different = reordered.Clone();
            var child = different.Definition.Items[0];
            var pin = child.Item.Unpack<SchematicPin>();
            switch (change)
            {
                case "id": pin.Id.Value = Guid.NewGuid().ToString("D"); break;
                case "name": pin.Name += "_changed"; break;
                case "position": pin.Position.XNm += 100; break;
                case "unit": child.Unit = new() { Unit = 0 }; break;
                case "style": child.BodyStyle = new() { Style = 0 }; break;
                case "duplicate": different.Definition.Items.Add(child.Clone()); break;
                case "delete": different.Definition.Items.RemoveAt(0); break;
                case "cache": different.CacheKey += "_other"; break;
                case "spacing": different.PinNameOffset.ValueNm += 100; break;
            }
            if (change != "delete") child.Item = Any.Pack(pin);
            Assert.IsFalse(SchematicLibraryCacheEquivalence.Equal(original, different), change);
        }
        Assert.IsFalse(SchematicLibraryCacheEquivalence.Equal(original, null));
    }

    [TestMethod]
    public void CacheReorderingDoesNotCauseACommitOrHideCompetingEdits()
    {
        var (design, _) = SchematicPartSymbolTests.Fixture();
        var baseline = design.Schematic.Instances[0].Clone();
        baseline.CachedSymbols.Clear(); baseline.CachedSymbols.Add(design.PartSymbols!.Single().Symbol.Clone());
        var native = baseline.Clone();
        var entries = native.CachedSymbols[0].Definition.Items.Reverse().ToArray();
        native.CachedSymbols[0].Definition.Items.Clear(); native.CachedSymbols[0].Definition.Items.Add(entries);
        Assert.IsEmpty(SchematicItemDelta.Plan(baseline, native));
        var unchanged = SchematicItemMerge.Plan(baseline, baseline.Clone(), native);
        Assert.IsTrue(unchanged.CanApply); Assert.IsEmpty(unchanged.NativeOperations);
        Assert.AreEqual(native, unchanged.Merged, "Keep native enumeration to avoid repeated file churn.");
        var xml = baseline.Clone(); xml.CachedSymbols[0].ShowPinNames = !xml.CachedSymbols[0].ShowPinNames;
        var independent = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(independent.CanApply);
        Assert.IsTrue(independent.NativeOperations.Any(o => o.ReplaceLibraryCache is not null));
        native.CachedSymbols[0].PinNameOffset.ValueNm += 100;
        Assert.IsFalse(SchematicItemMerge.Plan(baseline, xml, native).CanApply,
            "Different edits to the same complete definition still require explicit resolution.");
    }
}
