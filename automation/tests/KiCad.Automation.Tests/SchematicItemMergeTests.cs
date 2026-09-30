using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicItemMergeTests
{
    private static SchematicScreenData OrderedFixture()
    {
        var baseline = Fixture(); baseline.Items.Clear();
        foreach (string id in new[] { "ffffffff-ffff-4fff-8fff-ffffffffffff", "11111111-1111-4111-8111-111111111111" })
            baseline.Items.Add(Any.Pack(new SchematicText { Id = new() { Value = id },
                Text = new() { Text_ = id } }));
        foreach (string key in new[] { "Lib:Z", "Lib:A" })
            baseline.CachedSymbols.Add(new SchematicCachedSymbol { CacheKey = key, Definition = new() });
        return baseline;
    }

    [TestMethod]
    public void UnchangedCollectionsKeepNativeRepresentationWithoutFileChurn()
    {
        var baseline = OrderedFixture();
        var native = baseline.Clone(); native.Metadata.TextVariables.Add("NOTE", "Native edit");
        var xml = baseline.Clone();
        var merged = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.AreEqual(native, merged.Merged);
        Assert.AreEqual(SchematicDataXml.Write(native), SchematicDataXml.Write(merged.Merged!));
        Assert.IsEmpty(merged.NativeOperations);
        Assert.AreEqual(OrderedFixture().Items, baseline.Items);
        Assert.AreEqual(baseline, xml);
        var converged = SchematicItemMerge.Plan(baseline, native, native);
        Assert.AreEqual(native, converged.Merged);
        Assert.IsEmpty(converged.NativeOperations);
    }

    [TestMethod]
    public void CombinedEditsPreserveSurvivorOrderAppendXmlAdditionsAndBecomeIdempotent()
    {
        var baseline = OrderedFixture(); var native = baseline.Clone(); var xml = baseline.Clone();
        native.Metadata.TextVariables.Add("NOTE", "Native edit");
        xml.Items.RemoveAt(1); xml.CachedSymbols.RemoveAt(1);
        foreach (string id in new[] { "33333333-3333-4333-8333-333333333333", "22222222-2222-4222-8222-222222222222" })
            xml.Items.Add(Any.Pack(new SchematicText { Id = new() { Value = id }, Text = new() { Text_ = "New" } }));
        foreach (string key in new[] { "Lib:D", "Lib:C" })
            xml.CachedSymbols.Add(new SchematicCachedSymbol { CacheKey = key, Definition = new() });
        var expected = xml.Clone(); expected.Metadata = native.Metadata.Clone();
        var merged = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.AreEqual(expected, merged.Merged);
        Assert.IsNotEmpty(merged.NativeOperations);
        var again = SchematicItemMerge.Plan(merged.Merged!, merged.Merged!.Clone(), merged.Merged!.Clone());
        Assert.AreEqual(merged.Merged, again.Merged);
        Assert.AreEqual(SchematicDataXml.Write(expected), SchematicDataXml.Write(again.Merged!));
        Assert.IsEmpty(again.NativeOperations);
    }

    [TestMethod]
    public void CacheEditsMergeByExactKeyAndRetainAllConflictingVersions()
    {
        var baseline = Fixture();
        baseline.CachedSymbols.Add(new SchematicCachedSymbol { CacheKey = "Lib:One", Definition = new() });
        baseline.CachedSymbols.Add(new SchematicCachedSymbol { CacheKey = "Lib:Two", Definition = new() });
        var xml = baseline.Clone(); xml.CachedSymbols[0].ShowPinNames = true;
        var native = baseline.Clone(); native.CachedSymbols[1].ShowPinNumbers = true;
        var independent = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(independent.CanApply);
        Assert.IsTrue(independent.Merged!.CachedSymbols[0].ShowPinNames);
        Assert.IsTrue(independent.Merged.CachedSymbols[1].ShowPinNumbers);
        Assert.IsNotNull(independent.NativeOperations.Single().ReplaceLibraryCache);
        Assert.AreEqual(0, SchematicItemMerge.Plan(baseline, native, native).NativeOperations.Count);
        native.CachedSymbols[0].ShowPinNumbers = true;
        var conflict = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsFalse(conflict.CanApply); Assert.IsNull(conflict.Merged);
        var record = conflict.Conflicts.Single();
        Assert.AreEqual("Lib:One", record.CacheKey);
        Assert.AreEqual(baseline.CachedSymbols[0], record.Baseline!.Unpack<SchematicCachedSymbol>());
        Assert.AreEqual(xml.CachedSymbols[0], record.Xml!.Unpack<SchematicCachedSymbol>());
        Assert.AreEqual(native.CachedSymbols[0], record.Native!.Unpack<SchematicCachedSymbol>());
        var tools = new SchematicXmlTools();
        var toolConflict = tools.Plan(SchematicDataXml.Write(baseline), SchematicDataXml.Write(xml),
            SchematicDataXml.Write(native), CancellationToken.None);
        Assert.AreEqual("Lib:One", toolConflict.StructuredContent!.Value.GetProperty("conflicts")[0].GetProperty("cacheKey").GetString());
        var toolResolved = tools.Plan(SchematicDataXml.Write(baseline), SchematicDataXml.Write(xml),
            SchematicDataXml.Write(native), CancellationToken.None,
            cacheChoices: new Dictionary<string, string> { ["Lib:One"] = "xml" });
        Assert.IsTrue(toolResolved.StructuredContent!.Value.GetProperty("canPlan").GetBoolean());
        Assert.IsFalse(toolResolved.StructuredContent.Value.GetProperty("liveMutationAuthorized").GetBoolean());
        foreach (var choice in System.Enum.GetValues<SchematicConflictChoice>())
        {
            var chosen = SchematicItemMerge.Resolve(baseline, xml, native, new Dictionary<Guid, SchematicConflictChoice>(),
                cacheChoices: new Dictionary<string, SchematicConflictChoice> { ["Lib:One"] = choice });
            Assert.IsTrue(chosen.CanApply);
            var expected = choice == SchematicConflictChoice.Xml ? xml : choice == SchematicConflictChoice.Native ? native : baseline;
            Assert.AreEqual(expected.CachedSymbols[0], chosen.Merged!.CachedSymbols[0]);
            Assert.IsTrue(chosen.Merged.CachedSymbols[1].ShowPinNumbers);
        }
        xml.CachedSymbols.RemoveAt(0);
        Assert.IsNull(SchematicItemMerge.Plan(baseline, xml, native).Conflicts.Single().Xml);
        var deleted = SchematicItemMerge.Resolve(baseline, xml, native, new Dictionary<Guid, SchematicConflictChoice>(),
            cacheChoices: new Dictionary<string, SchematicConflictChoice> { ["Lib:One"] = SchematicConflictChoice.Xml });
        Assert.IsTrue(deleted.CanApply); Assert.AreEqual("Lib:Two", deleted.Merged!.CachedSymbols.Single().CacheKey);
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemMerge.Resolve(baseline, xml, native,
            new Dictionary<Guid, SchematicConflictChoice>(), cacheChoices: new Dictionary<string, SchematicConflictChoice>
            { ["lib:one"] = SchematicConflictChoice.Xml }));
    }

    [TestMethod]
    public void VariantChoicesResolveOnlyExplicitNamedConflictsIncludingDeletion()
    {
        var baseline = Fixture();
        baseline.Metadata.VariantDescriptions.Add("A", "original A");
        baseline.Metadata.VariantDescriptions.Add("B", "original B");
        var xml = baseline.Clone(); var native = baseline.Clone();
        foreach (var name in new[] { "A", "B" })
        {
            xml.Metadata.VariantDescriptions[name] = "XML " + name;
            native.Metadata.VariantDescriptions[name] = "native " + name;
        }
        var choices = new Dictionary<string, SchematicConflictChoice> { ["A"] = SchematicConflictChoice.Xml };
        Assert.IsFalse(SchematicItemMerge.Resolve(baseline, xml, native, new Dictionary<Guid, SchematicConflictChoice>(), variantChoices: choices).CanApply);
        choices["B"] = SchematicConflictChoice.Native;
        var result = SchematicItemMerge.Resolve(baseline, xml, native, new Dictionary<Guid, SchematicConflictChoice>(), variantChoices: choices);
        Assert.IsTrue(result.CanApply);
        Assert.AreEqual("XML A", result.Merged!.Metadata.VariantDescriptions["A"]);
        Assert.AreEqual("native B", result.Merged.Metadata.VariantDescriptions["B"]);
        choices["A"] = SchematicConflictChoice.Baseline;
        Assert.AreEqual("original A", SchematicItemMerge.Resolve(baseline, xml, native,
            new Dictionary<Guid, SchematicConflictChoice>(), variantChoices: choices).Merged!.Metadata.VariantDescriptions["A"]);
        xml.Metadata.VariantDescriptions.Remove("A"); choices["A"] = SchematicConflictChoice.Xml;
        Assert.IsFalse(SchematicItemMerge.Resolve(baseline, xml, native,
            new Dictionary<Guid, SchematicConflictChoice>(), variantChoices: choices).Merged!.Metadata.VariantDescriptions.ContainsKey("A"));
        choices["missing"] = SchematicConflictChoice.Native;
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => SchematicItemMerge.Resolve(baseline, xml, native,
            new Dictionary<Guid, SchematicConflictChoice>(), variantChoices: choices));
        Assert.AreEqual("native A", native.Metadata.VariantDescriptions["A"]);
        Assert.AreEqual("original B", baseline.Metadata.VariantDescriptions["B"]);
    }

    [TestMethod]
    public void RegistryDescriptionAndSymbolMoveMergeWithoutAFalseObjectConflict()
    {
        var baseline = Fixture(); baseline.Metadata.VariantDescriptions.Add("Assembly", "original");
        var symbol = new SchematicSymbolInstance { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = new() { XNm = 1000000 }, Variants = new() };
        symbol.Variants.Variants.Add(new SchematicSymbolVariant { Name = "Assembly", Description = "original" });
        baseline.Items.Add(Any.Pack(symbol));
        var xml = baseline.Clone();
        var moved = symbol.Clone(); moved.Position.XNm += 1000000; xml.Items[^1] = Any.Pack(moved);
        var native = baseline.Clone(); native.Metadata.VariantDescriptions["Assembly"] = "Near heatsink";
        var projected = symbol.Clone(); projected.Variants.Variants[0].Description = "Near heatsink";
        native.Items[^1] = Any.Pack(projected);
        var merged = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(merged.CanApply);
        var actual = merged.Merged!.Items.Single(i => i.Is(SchematicSymbolInstance.Descriptor)).Unpack<SchematicSymbolInstance>();
        Assert.AreEqual(moved.Position, actual.Position);
        Assert.AreEqual("Near heatsink", actual.Variants.Variants[0].Description);
        Assert.IsFalse(merged.NativeOperations.Single(o => o.Update is not null).Update.Unpack<SchematicSymbolInstance>().Variants.Variants[0].HasDescription);
        Assert.IsNotNull(merged.NativeOperations.Single(o => o.ReplaceLibraryCache is not null).ReplaceLibraryCache);

        var combined = xml.Clone(); combined.Metadata.VariantDescriptions["Assembly"] = "XML instruction";
        var operations = SchematicItemDelta.Plan(baseline, combined);
        Assert.AreEqual(3, operations.Count);
        Assert.IsNotNull(operations[0].ReplaceVariantRegistry);
        Assert.IsFalse(operations[1].Update.Unpack<SchematicSymbolInstance>().Variants.Variants[0].HasDescription);
        Assert.AreEqual("original", combined.Items[^1].Unpack<SchematicSymbolInstance>().Variants.Variants[0].Description);
        var resolved = SchematicItemMerge.Resolve(baseline, combined, native, new Dictionary<Guid, SchematicConflictChoice>(),
            variantChoices: new Dictionary<string, SchematicConflictChoice> { ["Assembly"] = SchematicConflictChoice.Xml });
        Assert.IsTrue(resolved.CanApply);
        Assert.AreEqual("XML instruction", resolved.Merged!.Items.Single(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Unpack<SchematicSymbolInstance>().Variants.Variants[0].Description);
        Assert.AreEqual(3, resolved.NativeOperations.Count);
        projected.Position.XNm += 3000000; native.Items[^1] = Any.Pack(projected);
        var objectChoice = new Dictionary<Guid, SchematicConflictChoice> { [Guid.Parse(symbol.Id.Value)] = SchematicConflictChoice.Native };
        Assert.IsFalse(SchematicItemMerge.Resolve(baseline, combined, native, objectChoice).CanApply);
        var bothChoices = SchematicItemMerge.Resolve(baseline, combined, native, objectChoice,
            variantChoices: new Dictionary<string, SchematicConflictChoice> { ["Assembly"] = SchematicConflictChoice.Xml });
        Assert.IsTrue(bothChoices.CanApply);
        var chosenSymbol = bothChoices.Merged!.Items.Single(i => i.Is(SchematicSymbolInstance.Descriptor)).Unpack<SchematicSymbolInstance>();
        Assert.AreEqual(projected.Position, chosenSymbol.Position);
        Assert.AreEqual("XML instruction", chosenSymbol.Variants.Variants[0].Description);
        moved.Variants.Variants[0].Description = "unbound override"; combined.Items[^1] = Any.Pack(moved);
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => SchematicItemDelta.Plan(baseline, combined));
    }

    [TestMethod]
    public void VariantDescriptionsMergeByExactNameAndPreserveConflictingVersions()
    {
        var baseline = Fixture(); baseline.Metadata.VariantDescriptions.Add("Assembly", "original");
        var xml = baseline.Clone(); xml.Metadata.VariantDescriptions["Assembly"] = "Near heatsink";
        var native = baseline.Clone(); native.Metadata.VariantDescriptions.Add("Optional", "");
        var merged = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.AreEqual("Near heatsink", merged.Merged!.Metadata.VariantDescriptions["Assembly"]);
        Assert.AreEqual("", merged.Merged.Metadata.VariantDescriptions["Optional"]);
        Assert.IsNotNull(merged.NativeOperations.Single().ReplaceVariantRegistry);
        native.Metadata.VariantDescriptions["Assembly"] = "Near connector";
        Assert.AreEqual("metadata_changed", SchematicItemMerge.Plan(baseline, xml, native).Conflicts.Single().Reason);
        xml.Metadata.VariantDescriptions.Remove("Assembly");
        Assert.IsFalse(SchematicItemMerge.Plan(baseline, xml, native).CanApply);
        Assert.AreEqual("Near connector", native.Metadata.VariantDescriptions["Assembly"]);
        Assert.AreEqual("original", baseline.Metadata.VariantDescriptions["Assembly"]);
    }

    private static SchematicScreenData Fixture()
    {
        var root = new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") };
        var result = new SchematicScreenData { Metadata = new() { ScreenId = root, Document = new() { SheetPath = new() } } };
        result.Metadata.Document.SheetPath.Path.Add(root.Clone());
        for (int i = 0; i < 2; ++i)
            result.Items.Add(Any.Pack(new SchematicText { Id = new() { Value = Guid.NewGuid().ToString("D") }, Text = new() { Text_ = "original " + i } }));
        return result;
    }
    private static void Edit(SchematicScreenData state, int index, string value)
    {
        var item = state.Items[index].Unpack<SchematicText>(); item.Text.Text_ = value;
        state.Items[index] = Any.Pack(item);
    }

    [TestMethod]
    public void PageChangesMergeIndependentlyAndFollowTheirAssetReplacement()
    {
        var baseline = Fixture(); baseline.Metadata.Page = new() { PageSize = (Kiapi.Common.Types.PageSize)4,
            Orientation = (Kiapi.Common.Types.PageOrientation)1 };
        baseline.Metadata.EmbeddedFiles = new(); baseline.Metadata.TitleBlock = new();
        var xml = baseline.Clone(); xml.Metadata.Page.Orientation = (Kiapi.Common.Types.PageOrientation)2;
        xml.Metadata.EmbeddedFonts = true;
        var native = baseline.Clone(); native.Metadata.TitleBlock.Title = "Native title";
        var merged = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.IsNotNull(merged.NativeOperations[0].ReplaceEmbeddedFiles);
        Assert.AreEqual(xml.Metadata.Page, merged.NativeOperations[1].SetPageSettings);
        Assert.AreEqual(native.Metadata.TitleBlock, merged.Merged!.Metadata.TitleBlock);
        native.Metadata.Page.DrawingSheet = "alternative.kicad_wks";
        Assert.AreEqual("metadata_changed", SchematicItemMerge.Plan(baseline, xml, native).Conflicts.Single().Reason);
        xml.Metadata.Page = null;
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => SchematicItemDelta.Plan(baseline, xml));
    }

    [TestMethod]
    public void TitleBlockMergesWithRootChangesAndRequiresExplicitState()
    {
        var baseline = Fixture(); baseline.Metadata.TitleBlock = new() { Title = "Board" };
        baseline.Metadata.RootInstance = new() { PageNumber = "1" };
        var xml = baseline.Clone(); xml.Metadata.TitleBlock.Title = "Controller";
        var native = baseline.Clone(); native.Metadata.RootInstance.PageNumber = "2";
        var result = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(result.CanApply);
        Assert.AreEqual(xml.Metadata.TitleBlock, result.NativeOperations.Single().SetTitleBlock);
        Assert.AreEqual("2", result.Merged!.Metadata.RootInstance.PageNumber);
        native.Metadata.TitleBlock.Revision = "B";
        Assert.AreEqual("metadata_changed", SchematicItemMerge.Plan(baseline, xml, native).Conflicts.Single().Reason);
        xml.Metadata.TitleBlock = new();
        Assert.AreEqual(new Kiapi.Common.Types.TitleBlockInfo(), SchematicItemDelta.Plan(baseline, xml).Single().SetTitleBlock);
        xml.Metadata.TitleBlock = null;
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => SchematicItemDelta.Plan(baseline, xml));
    }

    [TestMethod]
    public void NoteWordingAndPlacementMergeInEitherDirectionWithoutMutatingInputs()
    {
        var baseline = Fixture();
        var wording = baseline.Clone(); Edit(wording, 0, "Keep close to the connector");
        var placement = baseline.Clone();
        var moved = placement.Items[0].Unpack<SchematicText>();
        moved.Text.Position = new() { XNm = 2540000, YNm = 5080000 };
        moved.Text.Attributes = new() { Bold = true };
        placement.Items[0] = Any.Pack(moved);
        foreach (var (xml, native) in new[] { (wording, placement), (placement, wording) })
        {
            var originalXml = SchematicDataXml.Write(xml); var originalNative = SchematicDataXml.Write(native);
            var result = SchematicItemMerge.Plan(baseline, xml, native);
            Assert.IsTrue(result.CanApply);
            var note = result.NativeOperations.Single().Update.Unpack<SchematicText>();
            Assert.AreEqual("Keep close to the connector", note.Text.Text_);
            Assert.AreEqual(moved.Text.Position, note.Text.Position);
            Assert.AreEqual(moved.Text.Attributes, note.Text.Attributes);
            Assert.AreEqual(originalXml, SchematicDataXml.Write(xml));
            Assert.AreEqual(originalNative, SchematicDataXml.Write(native));
            Assert.AreEqual(0, SchematicItemMerge.Plan(result.Merged!, result.Merged!, result.Merged!).NativeOperations.Count);
        }
    }

    [TestMethod]
    public void NotePresentationAndContentRemainAtomicWhenTheirFieldsCompete()
    {
        var baseline = Fixture();
        foreach (bool competingContent in new[] { false, true })
        {
            var xml = baseline.Clone(); var native = baseline.Clone();
            var left = xml.Items[0].Unpack<SchematicText>(); var right = native.Items[0].Unpack<SchematicText>();
            if (competingContent)
            {
                left.Text.Text_ = "new wording"; right.Text.Hyperlink = "https://example.invalid/source";
            }
            else
            {
                left.Text.Position = new() { XNm = 100 }; right.Text.Attributes = new() { Bold = true };
            }
            xml.Items[0] = Any.Pack(left); native.Items[0] = Any.Pack(right);
            var result = SchematicItemMerge.Plan(baseline, xml, native);
            Assert.IsFalse(result.CanApply); Assert.IsNull(result.Merged);
            Assert.AreEqual(0, result.NativeOperations.Count);
            Assert.AreEqual("competing_edit", result.Conflicts.Single().Reason);
            Assert.AreEqual(xml.Items[0], result.Conflicts.Single().Xml);
            Assert.AreEqual(native.Items[0], result.Conflicts.Single().Native);
        }
    }

    [TestMethod]
    public void DifferentTargetsNeverBecomeNativeOnlyOrConvergentMetadataEdits()
    {
        var baseline = Fixture();
        foreach (bool changeScreen in new[] { false, true })
        {
            var other = baseline.Clone();
            if (changeScreen) other.Metadata.ScreenId.Value = Guid.NewGuid().ToString("D");
            else other.Metadata.Document.SheetPath.Path.Add(new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") });
            foreach (var (xml, native) in new[] { (baseline, other), (other, baseline), (other, other) })
            {
                var result = SchematicItemMerge.Plan(baseline, xml, native);
                Assert.IsFalse(result.CanApply); Assert.IsNull(result.Merged);
                Assert.AreEqual(0, result.NativeOperations.Count);
                Assert.AreEqual("target_changed", result.Conflicts.Single().Reason);
                Assert.AreEqual(native.Metadata, result.Conflicts.Single().Native!.Unpack<SchematicMetadata>());
                Assert.IsFalse(SchematicItemMerge.Resolve(baseline, xml, native,
                    new Dictionary<Guid, SchematicConflictChoice>()).CanApply);
            }
        }
    }

    [TestMethod]
    public void IndependentAssetAndRootChangesMergeButCompetingAssetsRemainTogether()
    {
        var baseline = Fixture(); baseline.Metadata.RootInstance = new() { PageNumber = "1" };
        baseline.Metadata.EmbeddedFiles = new();
        var xml = baseline.Clone(); xml.Metadata.EmbeddedFonts = true;
        var native = baseline.Clone(); native.Metadata.RootInstance.PageNumber = "B.4";
        var combined = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(combined.CanApply);
        Assert.AreEqual("B.4", combined.Merged!.Metadata.RootInstance.PageNumber);
        Assert.IsTrue(combined.Merged.Metadata.EmbeddedFonts);
        Assert.IsTrue(combined.NativeOperations.Single().ReplaceEmbeddedFiles.EmbeddedFonts);
        native.Metadata.EmbeddedFiles.Files.Add(new Kiapi.Common.Types.EmbeddedFile { Name = "native.png" });
        var competing = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsFalse(competing.CanApply); Assert.AreEqual(0, competing.NativeOperations.Count);
        Assert.AreEqual("metadata_changed", competing.Conflicts.Single().Reason);
        Assert.AreEqual(native.Metadata, competing.Conflicts.Single().Native!.Unpack<SchematicMetadata>());
    }

    [TestMethod]
    public void XmlRootPageChangeMergesWithIndependentNativeObjectEdit()
    {
        var baseline = Fixture(); baseline.Metadata.RootInstance = new() { PageNumber = "1" };
        var xml = baseline.Clone(); xml.Metadata.RootInstance.PageNumber = "A.2";
        var native = baseline.Clone(); Edit(native, 0, "keep native note");
        var merged = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.AreEqual("A.2", merged.Merged!.Metadata.RootInstance.PageNumber);
        Assert.AreEqual(native.Items[0], merged.Merged.Items.Single(i => i.Unpack<SchematicText>().Text.Text_ == "keep native note"));
        Assert.AreEqual("A.2", merged.NativeOperations.Single().SetRootInstance.PageNumber);
        Assert.AreEqual(0, SchematicItemMerge.Plan(baseline, baseline, xml).NativeOperations.Count);
        native.Metadata.RootInstance.PageNumber = "competing";
        Assert.AreEqual("metadata_changed", SchematicItemMerge.Plan(baseline, xml, native).Conflicts.Single().Reason);
    }

    [TestMethod]
    public void ExplicitChoicesPreserveUnrelatedEditsAndOriginalConflictVersions()
    {
        var baseline = Fixture(); var xml = baseline.Clone(); var native = baseline.Clone();
        var id = Guid.Parse(baseline.Items[0].Unpack<SchematicText>().Id.Value);
        Edit(xml, 0, "XML choice"); Edit(native, 0, "native choice");
        Edit(native, 1, "independent native note");
        foreach (var choice in System.Enum.GetValues<SchematicConflictChoice>())
        {
            var result = SchematicItemMerge.Resolve(baseline, xml, native, new Dictionary<Guid, SchematicConflictChoice> { [id] = choice });
            Assert.IsTrue(result.CanApply);
            var texts = result.Merged!.Items.Select(i => i.Unpack<SchematicText>()).ToArray();
            Assert.AreEqual(choice switch { SchematicConflictChoice.Xml => "XML choice", SchematicConflictChoice.Native => "native choice", _ => "original 0" }, texts.Single(t => t.Id.Value == id.ToString("D")).Text.Text_);
            Assert.IsTrue(texts.Any(t => t.Text.Text_ == "independent native note"));
            Assert.AreEqual(choice == SchematicConflictChoice.Native ? 0 : 1, result.NativeOperations.Count);
        }
        Assert.AreEqual(1, SchematicItemMerge.Plan(baseline, xml, native).Conflicts.Count);
    }

    [TestMethod]
    public void PartialResolutionDoesNotApplyAndDeletionIsAnExplicitChoice()
    {
        var baseline = Fixture(); var xml = baseline.Clone(); var native = baseline.Clone();
        var first = Guid.Parse(baseline.Items[0].Unpack<SchematicText>().Id.Value);
        var second = Guid.Parse(baseline.Items[1].Unpack<SchematicText>().Id.Value);
        Edit(xml, 0, "xml"); Edit(native, 0, "native");
        Edit(native, 1, "modified"); xml.Items.RemoveAt(1);
        var choices = new Dictionary<Guid, SchematicConflictChoice> { [first] = SchematicConflictChoice.Native };
        var partial = SchematicItemMerge.Resolve(baseline, xml, native, choices);
        Assert.IsFalse(partial.CanApply); Assert.AreEqual(0, partial.NativeOperations.Count);
        Assert.AreEqual(second, partial.Conflicts.Single().ObjectId);
        choices[second] = SchematicConflictChoice.Xml;
        var complete = SchematicItemMerge.Resolve(baseline, xml, native, choices);
        Assert.IsTrue(complete.CanApply); Assert.AreEqual(1, complete.Merged!.Items.Count);
        Assert.AreEqual(1, complete.NativeOperations.Count);
        choices[Guid.NewGuid()] = SchematicConflictChoice.Native;
        var failure = Assert.Throws<KiCad.Automation.Model.AutomationException>(() => SchematicItemMerge.Resolve(baseline, xml, native, choices));
        Assert.AreEqual("invalid_sync_resolution", failure.Code);
    }

    [TestMethod]
    public void DisjointEditsCombineAndOnlyXmlChangeNeedsNativeOperation()
    {
        var baseline = Fixture(); var xml = baseline.Clone(); var native = baseline.Clone();
        Edit(xml, 0, "XML instruction"); Edit(native, 1, "Native instruction");
        string original = SchematicDataXml.Write(baseline);
        var result = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(result.CanApply); Assert.IsNotNull(result.Merged);
        Assert.AreEqual(1, result.NativeOperations.Count);
        Assert.IsTrue(result.Merged.Items.Contains(xml.Items[0]));
        Assert.IsTrue(result.Merged.Items.Contains(native.Items[1]));
        Assert.AreEqual(original, SchematicDataXml.Write(baseline));
        var converged = SchematicItemMerge.Plan(result.Merged, result.Merged, result.Merged);
        Assert.IsTrue(converged.CanApply); Assert.AreEqual(0, converged.NativeOperations.Count);
    }

    [TestMethod]
    public void NativeOnlyAndIdenticalEditsDoNotCauseFeedbackEdits()
    {
        var baseline = Fixture(); var native = baseline.Clone(); Edit(native, 0, "manual note");
        foreach (var xml in new[] { baseline, native })
        {
            var result = SchematicItemMerge.Plan(baseline, xml, native);
            Assert.IsTrue(result.CanApply); Assert.AreEqual(0, result.NativeOperations.Count);
            Assert.IsTrue(result.Merged!.Items.Contains(native.Items[0]));
        }
    }

    [TestMethod]
    public void CompetingEditsRetainAllVersionsWithoutPartialApplication()
    {
        var baseline = Fixture(); var xml = baseline.Clone(); var native = baseline.Clone();
        Edit(xml, 0, "XML alternative"); Edit(native, 0, "native alternative"); Edit(xml, 1, "independent edit");
        var result = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsFalse(result.CanApply); Assert.IsNull(result.Merged); Assert.AreEqual(0, result.NativeOperations.Count);
        var conflict = result.Conflicts.Single();
        Assert.AreEqual("competing_edit", conflict.Reason);
        Assert.AreEqual(baseline.Items[0], conflict.Baseline); Assert.AreEqual(xml.Items[0], conflict.Xml); Assert.AreEqual(native.Items[0], conflict.Native);
        Assert.AreEqual(Guid.Parse(baseline.Items[0].Unpack<SchematicText>().Id.Value), conflict.ObjectId);
    }

    [TestMethod]
    public void DeleteModifyAndCompetingCreationAreConflictsButSharedDeletionIsNot()
    {
        var baseline = Fixture(); var xml = baseline.Clone(); var native = baseline.Clone();
        xml.Items.RemoveAt(0); Edit(native, 0, "keep edited");
        var conflict = SchematicItemMerge.Plan(baseline, xml, native).Conflicts.Single();
        Assert.AreEqual("delete_modify", conflict.Reason); Assert.IsNull(conflict.Xml);
        native.Items.RemoveAt(0);
        Assert.IsTrue(SchematicItemMerge.Plan(baseline, xml, native).CanApply);
        xml = baseline.Clone(); native = baseline.Clone();
        var added = baseline.Items[0].Unpack<SchematicText>(); added.Id.Value = Guid.NewGuid().ToString("D");
        xml.Items.Add(Any.Pack(added)); added.Text.Text_ = "different new content"; native.Items.Add(Any.Pack(added));
        Assert.AreEqual("competing_creation", SchematicItemMerge.Plan(baseline, xml, native).Conflicts.Single().Reason);
    }

    [TestMethod]
    public void NativeOnlyTypeReplacementCannotRebindAnExistingIdentity()
    {
        var baseline = Fixture(); var native = baseline.Clone();
        native.Items[0] = Any.Pack(new Junction { Id = baseline.Items[0].Unpack<SchematicText>().Id.Clone() });
        foreach (var xml in new[] { baseline, native })
        {
            var result = SchematicItemMerge.Plan(baseline, xml, native);
            Assert.IsFalse(result.CanApply); Assert.IsNull(result.Merged);
            Assert.AreEqual("object_type_changed", result.Conflicts.Single().Reason);
        }
    }

    [TestMethod]
    public void UnsupportedMetadataEditPreservesVersionsAndPauses()
    {
        var baseline = Fixture(); var xml = baseline.Clone();
        xml.Metadata.UnrepresentedState.Add("future_settings");
        var result = SchematicItemMerge.Plan(baseline, xml, baseline);
        Assert.IsFalse(result.CanApply); Assert.IsNull(result.Merged);
        Assert.AreEqual(xml.Metadata, result.Conflicts.Single().Xml!.Unpack<SchematicMetadata>());
    }

    [TestMethod]
    public void TextVariableUpdatesMergeWithoutDroppingCompetingVersions()
    {
        var baseline = Fixture(); var xml = baseline.Clone();
        xml.Metadata.TextVariables.Add("NOTE", "Keep near MCU");
        var result = SchematicItemMerge.Plan(baseline, xml, baseline);
        Assert.IsTrue(result.CanApply); Assert.AreEqual(xml.Metadata, result.Merged!.Metadata);
        var native = baseline.Clone(); native.Metadata.TextVariables.Add("NOTE", "Keep near connector");
        var conflict = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsFalse(conflict.CanApply); Assert.IsNull(conflict.Merged);
        Assert.AreEqual(xml.Metadata, conflict.Conflicts.Single().Xml!.Unpack<SchematicMetadata>());
    }

    [TestMethod]
    public void IndependentVariablesAndBusAliasesMergeButDeleteVersusEmptyConflicts()
    {
        var baseline = Fixture(); baseline.Metadata.TextVariables.Add("OLD", "old value");
        var xml = baseline.Clone(); var native = baseline.Clone();
        xml.Metadata.TextVariables.Remove("OLD"); xml.Metadata.TextVariables.Add("LAYOUT", "Near CPU");
        native.Metadata.TextVariables.Add("NOTE", "Keep cool");
        xml.Metadata.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } });
        string xmlBefore = SchematicDataXml.Write(xml), nativeBefore = SchematicDataXml.Write(native);
        var merged = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.IsFalse(merged.Merged!.Metadata.TextVariables.ContainsKey("OLD"));
        Assert.AreEqual("Near CPU", merged.Merged.Metadata.TextVariables["LAYOUT"]);
        Assert.AreEqual("Keep cool", merged.Merged.Metadata.TextVariables["NOTE"]);
        Assert.AreEqual(xml.Metadata.BusAliases, merged.Merged.Metadata.BusAliases);
        Assert.AreEqual(1, merged.NativeOperations.Count(o => o.ReplaceTextVariables is not null));
        Assert.AreEqual(1, merged.NativeOperations.Count(o => o.ReplaceBusAliases is not null));
        Assert.AreEqual(xmlBefore, SchematicDataXml.Write(xml)); Assert.AreEqual(nativeBefore, SchematicDataXml.Write(native));
        native.Metadata.TextVariables["OLD"] = "";
        var conflict = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsFalse(conflict.CanApply); Assert.IsNull(conflict.Merged);
        Assert.AreEqual(xml.Metadata, conflict.Conflicts.Single().Xml!.Unpack<SchematicMetadata>());
        Assert.AreEqual(native.Metadata, conflict.Conflicts.Single().Native!.Unpack<SchematicMetadata>());
    }

    [TestMethod]
    public void IndependentChainEditsMergeAndNativeMembershipDoesNotCauseFalseConflict()
    {
        var baseline = Fixture();
        baseline.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "A", NetClass = "Default" });
        baseline.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "B", NetClass = "Default" });
        var xml = baseline.Clone(); var native = baseline.Clone();
        xml.Metadata.NetChains[0].NetClass = "Sensitive";
        native.Metadata.NetChains[0].Committed = true;
        native.Metadata.NetChains[1].NetClass = "Power";
        string originalXml = SchematicDataXml.Write(xml), originalNative = SchematicDataXml.Write(native);
        var result = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(result.CanApply);
        Assert.AreEqual("Sensitive", result.Merged!.Metadata.NetChains[0].NetClass);
        Assert.IsTrue(result.Merged.Metadata.NetChains[0].Committed);
        Assert.AreEqual("Power", result.Merged.Metadata.NetChains[1].NetClass);
        Assert.AreEqual(1, result.NativeOperations.Count);
        Assert.IsTrue(result.NativeOperations.Single().ReplaceNetChains.Definitions.All(c => !c.Committed));
        Assert.AreEqual(originalXml, SchematicDataXml.Write(xml));
        Assert.AreEqual(originalNative, SchematicDataXml.Write(native));
    }

    [TestMethod]
    public void ChainDeleteModifyAndCompetingCreationRetainBothVersions()
    {
        var baseline = Fixture();
        baseline.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "A", NetClass = "Default" });
        var xml = baseline.Clone(); var native = baseline.Clone();
        xml.Metadata.NetChains.Clear(); native.Metadata.NetChains[0].NetClass = "Changed";
        var conflict = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsFalse(conflict.CanApply);
        Assert.AreEqual(xml.Metadata, conflict.Conflicts.Single().Xml!.Unpack<SchematicMetadata>());
        Assert.AreEqual(native.Metadata, conflict.Conflicts.Single().Native!.Unpack<SchematicMetadata>());
        baseline.Metadata.NetChains.Clear();
        xml.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "A", NetClass = "Other" });
        Assert.IsFalse(SchematicItemMerge.Plan(baseline, xml, native).CanApply);
        xml.Metadata.NetChains[0].NetClass = "Changed";
        Assert.IsTrue(SchematicItemMerge.Plan(baseline, xml, native).CanApply);
        xml.Metadata.NetChains[0].Committed = true;
        Assert.IsFalse(SchematicItemMerge.Plan(baseline, xml, native).CanApply);
    }

    [TestMethod]
    public void ExplicitChainChoicesPreserveOtherEditsAndRejectUnrelatedChoices()
    {
        var baseline = Fixture();
        baseline.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "A", NetClass = "Original" });
        var xml = baseline.Clone(); var native = baseline.Clone();
        xml.Metadata.NetChains[0].NetClass = "Xml";
        native.Metadata.NetChains[0].NetClass = "Native";
        native.Metadata.TextVariables["UNRELATED"] = "Preserve";
        string originalXml = SchematicDataXml.Write(xml), originalNative = SchematicDataXml.Write(native);
        foreach (var choice in System.Enum.GetValues<SchematicConflictChoice>())
        {
            var result = SchematicItemMerge.Resolve(baseline, xml, native,
                new Dictionary<Guid, SchematicConflictChoice>(), new Dictionary<string, SchematicConflictChoice> { ["A"] = choice });
            Assert.IsTrue(result.CanApply);
            Assert.AreEqual(choice switch { SchematicConflictChoice.Xml => "Xml", SchematicConflictChoice.Native => "Native", _ => "Original" },
                result.Merged!.Metadata.NetChains.Single().NetClass);
            Assert.AreEqual("Preserve", result.Merged.Metadata.TextVariables["UNRELATED"]);
        }
        Assert.AreEqual(originalXml, SchematicDataXml.Write(xml)); Assert.AreEqual(originalNative, SchematicDataXml.Write(native));
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => SchematicItemMerge.Resolve(baseline, xml, native,
            new Dictionary<Guid, SchematicConflictChoice>(), new Dictionary<string, SchematicConflictChoice> { ["UNKNOWN"] = SchematicConflictChoice.Xml }));
        xml.Metadata.NetChains.Clear();
        var deletion = SchematicItemMerge.Resolve(baseline, xml, native, new Dictionary<Guid, SchematicConflictChoice>(),
            new Dictionary<string, SchematicConflictChoice> { ["A"] = SchematicConflictChoice.Xml });
        Assert.IsTrue(deletion.CanApply); Assert.AreEqual(0, deletion.Merged!.Metadata.NetChains.Count);
    }

    [TestMethod]
    public void ObjectAndChainConflictsCanBeChosenInEitherOrder()
    {
        var baseline = Fixture();
        baseline.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PATH", NetClass = "Original" });
        var xml = baseline.Clone(); var native = baseline.Clone();
        xml.Metadata.NetChains[0].NetClass = "Xml"; native.Metadata.NetChains[0].NetClass = "Native";
        Edit(xml, 0, "Xml note"); Edit(native, 0, "Native note");
        var id = Guid.Parse(baseline.Items[0].Unpack<SchematicText>().Id.Value);
        var objects = new Dictionary<Guid, SchematicConflictChoice> { [id] = SchematicConflictChoice.Xml };
        var chains = new Dictionary<string, SchematicConflictChoice> { ["PATH"] = SchematicConflictChoice.Native };
        var objectOnly = SchematicItemMerge.Resolve(baseline, xml, native, objects);
        Assert.IsFalse(objectOnly.CanApply); Assert.AreEqual("metadata_changed", objectOnly.Conflicts.Single().Reason);
        var chainOnly = SchematicItemMerge.Resolve(baseline, xml, native, new Dictionary<Guid, SchematicConflictChoice>(), chains);
        Assert.IsFalse(chainOnly.CanApply); Assert.AreEqual(id, chainOnly.Conflicts.Single().ObjectId);
        var both = SchematicItemMerge.Resolve(baseline, xml, native, objects, chains);
        Assert.IsTrue(both.CanApply);
        Assert.AreEqual("Native", both.Merged!.Metadata.NetChains.Single().NetClass);
        Assert.AreEqual("Xml note", both.Merged.Items.Select(i => i.Unpack<SchematicText>()).Single(i => i.Id.Value == id.ToString("D")).Text.Text_);
    }
}
