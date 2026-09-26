using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicItemDeltaTests
{
    [TestMethod]
    public void UnsavableFieldModeCannotEnterAPlannedDelta()
    {
        var before = Fixture();
        var label = new GlobalLabel
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            Text = new() { Text_ = "SIGNAL", Attributes = new() { Multiline = false } },
            IntersheetRefsField = new() { Text = new() { Text_ = "1", Attributes = new() { Multiline = true } } }
        };
        before.Items.Add(Any.Pack(label));
        Assert.AreEqual(0, SchematicItemDelta.Plan(before, before).Count);
        var desired = before.Clone();
        label.IntersheetRefsField.Text.Attributes.Multiline = false;
        desired.Items[^1] = Any.Pack(label);
        var xml = SchematicDataXml.Write(desired);
        var failure = Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, desired));
        StringAssert.Contains(failure.Message, "multiline");
        Assert.AreEqual(xml, SchematicDataXml.Write(desired));
    }

    [TestMethod]
    public void OwnedPinNameOffsetsRejectUnrepresentableValuesWithoutChangingXml()
    {
        var before = Fixture();
        var symbol = new SchematicSymbolInstance
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            DefinitionPinNameOffset = new() { ValueNm = 0 }
        };
        before.Items.Add(Any.Pack(symbol));
        Assert.AreEqual(0, SchematicItemDelta.Plan(before, before).Count);
        foreach (long value in new[] { 1L, -1L, ((long)int.MaxValue + 1) * 100,
            ((long)int.MinValue - 1) * 100 })
        {
            var invalid = before.Clone();
            var bad = symbol.Clone(); bad.DefinitionPinNameOffset.ValueNm = value;
            invalid.Items[^1] = Any.Pack(bad);
            string xml = SchematicDataXml.Write(invalid);
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, invalid));
            Assert.AreEqual(xml, SchematicDataXml.Write(invalid));
        }
    }

    [TestMethod]
    public void CacheChangesHaveExplicitScreenOwnershipAndUnchangedRecordsAreNoOps()
    {
        var current = Fixture();
        current.CachedSymbols.Add(new SchematicCachedSymbol { CacheKey = "Library:Unused", Definition = new() });
        current.CachedSymbols.Add(new SchematicCachedSymbol { CacheKey = "Library:Other", Definition = new() });
        Assert.AreEqual(0, SchematicItemDelta.Plan(current, current.Clone()).Count);
        var reordered = current.Clone();
        reordered.CachedSymbols.Clear();
        reordered.CachedSymbols.Add(current.CachedSymbols.Reverse().Select(entry => entry.Clone()));
        Assert.AreEqual(0, SchematicItemDelta.Plan(current, reordered).Count);
        var changed = current.Clone(); changed.CachedSymbols[0].ShowPinNames = true;
        var operation = SchematicItemDelta.Plan(current, changed).Single();
        Assert.AreEqual(current.Metadata.ScreenId, operation.ReplaceLibraryCache.ScreenId);
        CollectionAssert.AreEqual(changed.CachedSymbols.OrderBy(c => c.CacheKey, StringComparer.Ordinal).ToArray(),
            operation.ReplaceLibraryCache.Definitions.ToArray());
        Assert.AreEqual(0, SchematicItemDelta.Plan(changed, changed.Clone()).Count);
        changed = current.Clone(); changed.CachedSymbols.RemoveAt(0);
        Assert.AreEqual(1, SchematicItemDelta.Plan(current, changed).Single().ReplaceLibraryCache.Definitions.Count);
        changed = current.Clone(); changed.CachedSymbols.Clear();
        Assert.AreEqual(0, SchematicItemDelta.Plan(current, changed).Single().ReplaceLibraryCache.Definitions.Count);
        changed = current.Clone(); changed.CachedSymbols.Add(current.CachedSymbols[0].Clone()); Reject(current, changed);
        changed = current.Clone(); changed.CachedSymbols[0].CacheKey = ""; Reject(current, changed);
        changed = current.Clone(); changed.CachedSymbols[0].Definition = null; Reject(current, changed);
        Assert.AreEqual(0, SchematicItemDelta.Plan(current, current.Clone()).Count);
    }

    [TestMethod]
    public void DrawingRatiosRoundTripAndProduceExplicitValidatedDelta()
    {
        var before = Fixture();
        before.Metadata.DrawingRatios = new() { DashLengthRatio = 12, GapLengthRatio = 3,
            TextOffsetRatio = 0.15, LabelSizeRatio = 0.4, OverbarHeightRatio = 1.2 };
        var desired = before.Clone(); desired.Metadata.DrawingRatios.DashLengthRatio = 8;
        string xml = SchematicDataXml.Write(desired);
        Assert.AreEqual(desired, SchematicDataXml.Read(xml));
        var operation = SchematicItemDelta.Plan(before, desired).Single();
        Assert.AreEqual(desired.Metadata.DrawingRatios, operation.SetDrawingRatios);
        Assert.AreEqual(0, SchematicItemDelta.Plan(desired, desired).Count);
        Assert.AreEqual(xml, SchematicDataXml.Write(desired));
        foreach (Action<SchematicDrawingRatios> corrupt in new Action<SchematicDrawingRatios>[]
        {
            r => r.DashLengthRatio = double.NaN, r => r.GapLengthRatio = -1,
            r => r.TextOffsetRatio = 3, r => r.LabelSizeRatio = 3,
            r => r.OverbarHeightRatio = double.PositiveInfinity,
            r => { r.DashLengthRatio = 0; r.GapLengthRatio = 0; }
        })
        {
            var invalid = desired.Clone(); corrupt(invalid.Metadata.DrawingRatios);
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, invalid));
        }
        desired.Metadata.DrawingRatios = null;
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, desired));
    }

    [TestMethod]
    public void UnsavableGraphicSegmentUsesTheExplicitNativeLineRepresentation()
    {
        var before = Fixture(); var desired = before.Clone();
        var id = new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") };
        desired.Items.Add(Any.Pack(new SchematicGraphicShape { Id = id, Shape = new()
            { Segment = new() { Start = new(), End = new() { XNm = 1000000 } } } }));
        var error = Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, desired));
        StringAssert.Contains(error.Message, "SLT_GRAPHIC");
        desired.Items.RemoveAt(desired.Items.Count - 1);
        desired.Items.Add(Any.Pack(new SchematicLine { Id = id, Start = new(), End = new() { XNm = 1000000 }, Type = (SchematicLineType)3 }));
        Assert.AreEqual(1, SchematicItemDelta.Plan(before, desired).Count);
    }

    [TestMethod]
    public void NetChainDeclarationsAreEditableButMembershipRemainsComputed()
    {
        var current = Fixture();
        current.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PATH",
            From = new() { Reference = "U1", Pin = "1" }, To = new() { Reference = "U2", Pin = "2" },
            MemberNets = { "/SIGNAL" }, Committed = true });
        Assert.AreEqual(0, SchematicItemDelta.Plan(current, current).Count);
        var desired = current.Clone(); desired.Metadata.NetChains[0].NetClass = "Changed";
        string source = SchematicDataXml.Write(desired);
        var replacement = SchematicItemDelta.Plan(current, desired).Single().ReplaceNetChains;
        Assert.AreEqual("Changed", replacement.Definitions.Single().NetClass);
        Assert.IsFalse(replacement.Definitions.Single().Committed);
        Assert.AreEqual(source, SchematicDataXml.Write(desired));
        desired.Metadata.NetChains[0].Committed = false;
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(current, desired));
        desired.Metadata.NetChains.Clear();
        Assert.AreEqual(0, SchematicItemDelta.Plan(current, desired).Single().ReplaceNetChains.Definitions.Count);
        desired.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PENDING" });
        Assert.AreEqual("PENDING", SchematicItemDelta.Plan(current, desired).Single().ReplaceNetChains.Definitions[0].Name);
    }

    [TestMethod]
    public void NetChainDeclarationsRejectLossyOrInvalidFields()
    {
        var before = Fixture();
        foreach (Action<SchematicNetChainDefinition> corrupt in new Action<SchematicNetChainDefinition>[]
        {
            c => c.Name = "bad name", c => c.Name = "", c => c.NetClass = "N\0UL",
            c => c.From = new() { Pin = "1\0" }, c => c.Committed = true,
            c => c.Color = new() { R = double.NaN }, c => c.Color = new() { A = 2 },
            c => c.MemberNets.Add("__SG_runtime"), c => c.MemberNets.Add(""),
            c => { c.MemberNets.Add("/A"); c.MemberNets.Add("/A"); }
        })
        {
            var desired = before.Clone(); var chain = new SchematicNetChainDefinition { Name = "VALID" };
            corrupt(chain); desired.Metadata.NetChains.Add(chain);
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, desired));
        }
        var duplicate = before.Clone();
        duplicate.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "DUPLICATE" });
        duplicate.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "DUPLICATE" });
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, duplicate));
    }

    [TestMethod]
    public void TextVariablesRetainUnicodeMultilineAndEmptyValuesAndSupportRemoval()
    {
        var before = Fixture(); var after = before.Clone();
        after.Metadata.TextVariables.Add("NOTE", "電源 & timing\nKeep near MCU");
        after.Metadata.TextVariables.Add("EMPTY", "");
        string original = SchematicDataXml.Write(after);
        Assert.AreEqual(after.Metadata.TextVariables,
            SchematicItemDelta.Plan(before, after).Single().ReplaceTextVariables.Variables);
        Assert.AreEqual(0, SchematicItemDelta.Plan(after, before).Single().ReplaceTextVariables.Variables.Count);
        Assert.AreEqual(0, SchematicItemDelta.Plan(after, after).Count);
        Assert.AreEqual(original, SchematicDataXml.Write(after));
        foreach (var pair in new[] { ("", "value"), ("N\0AME", "value"), ("NAME", "va\0lue") })
        {
            var invalid = before.Clone(); invalid.Metadata.TextVariables.Add(pair.Item1, pair.Item2);
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, invalid));
        }
    }

    [TestMethod]
    public void BusAliasReplacementPreservesDefinitionsAndRejectsLossyInputs()
    {
        var before = Fixture(); var after = before.Clone();
        var alias = new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } };
        after.Metadata.BusAliases.Add(alias);
        string original = SchematicDataXml.Write(after);
        Assert.AreEqual(alias, SchematicItemDelta.Plan(before, after).Single().ReplaceBusAliases.Aliases.Single());
        Assert.AreEqual(0, SchematicItemDelta.Plan(after, after).Count);
        Assert.AreEqual(0, SchematicItemDelta.Plan(after, before).Single().ReplaceBusAliases.Aliases.Count);
        Assert.AreEqual(original, SchematicDataXml.Write(after));
        foreach (string problem in new[] { "empty", "space", "nul", "member", "duplicate" })
        {
            var invalid = after.Clone();
            switch (problem)
            {
                case "empty": invalid.Metadata.BusAliases[0].Name = ""; break;
                case "space": invalid.Metadata.BusAliases[0].Name = " DATA"; break;
                case "nul": invalid.Metadata.BusAliases[0].Name = "DA\0TA"; break;
                case "member": invalid.Metadata.BusAliases[0].Members.Add(" "); break;
                case "duplicate": invalid.Metadata.BusAliases.Add(alias.Clone()); break;
            }
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, invalid), problem);
        }
        // Native memory permits conflicting names, but the project writer loses one.
        after.Metadata.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D2" } });
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, after));
    }

    [TestMethod]
    public void LabelMultilineFlagsRejectBeforePlanningButMultilineNotesRemainSupported()
    {
        var current = Fixture();
        var text = new Kiapi.Common.Types.Text { Text_ = "SIGNAL", Attributes = new() { Multiline = true } };
        var id = new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") };
        foreach (var item in new[]
        {
            Any.Pack(new LocalLabel { Id = id, Text = text }),
            Any.Pack(new GlobalLabel { Id = id, Text = text }),
            Any.Pack(new HierarchicalLabel { Id = id, Text = text }),
            Any.Pack(new DirectiveLabel { Id = id, Text = text })
        })
        {
            var desired = current.Clone(); desired.Items.Add(item);
            string xml = SchematicDataXml.Write(desired);
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(current, desired));
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(desired, desired));
            Assert.AreEqual(xml, SchematicDataXml.Write(desired));
        }
        var valid = current.Clone(); valid.Items.Add(Any.Pack(new SchematicText { Id = id, Text = text }));
        Assert.AreEqual(1, SchematicItemDelta.Plan(current, valid).Count);
        text = text.Clone(); text.Attributes.Multiline = false;
        valid = current.Clone(); valid.Items.Add(Any.Pack(new HierarchicalLabel { Id = id, Text = text }));
        Assert.AreEqual(1, SchematicItemDelta.Plan(current, valid).Count);
    }

    [TestMethod]
    public void VariantRegistryEditsAreExplicitAndPreserveDescriptions()
    {
        var current = Fixture();
        current.Metadata.VariantDescriptions.Add("Assembly", "Keep accessible");
        Assert.AreEqual(0, SchematicItemDelta.Plan(current, current.Clone()).Count);
        var desired = current.Clone();
        desired.Metadata.VariantDescriptions["Assembly"] = "Move toward heatsink";
        var operation = SchematicItemDelta.Plan(current, desired).Single();
        Assert.AreEqual(desired.Metadata.VariantDescriptions, operation.ReplaceVariantRegistry.Descriptions);
        Assert.AreEqual("Keep accessible", current.Metadata.VariantDescriptions["Assembly"]);
        Assert.AreEqual("Move toward heatsink", desired.Metadata.VariantDescriptions["Assembly"]);
        foreach (var (name, description) in new[] { ("", "value"), (" spaced", ""), ("a\0b", ""), ("valid", "a\0b") })
        {
            var invalid = current.Clone(); invalid.Metadata.VariantDescriptions.Add(name, description);
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(current, invalid));
        }
        desired.Metadata.VariantDescriptions.Clear();
        Assert.AreEqual(0, SchematicItemDelta.Plan(current, desired).Single().ReplaceVariantRegistry.Descriptions.Count);
    }

    // Ledger p91fda8ca22a68141: builds up to preview 23 listed library_cache among the state their snapshots could not hold,
    // although they held the cache exactly; this build no longer lists it. A record those builds saved needs no native edit
    // against this build's snapshot of the same sheet, in either direction; before, applying it failed with "Unsupported
    // settings or document identity changed". Only that retired marker is exempt: any other coverage difference still stops
    // planning (MissingCoverageMetadataChangesAndIdentityAmbiguityStopPlanning), and so does every metadata change KiCad
    // cannot make. The NativeXmlRebuild journey applies such a record through kicad_design_sync_apply against a live KiCad;
    // this pins the delta rule on its own, which no existing case covers.
    [TestMethod]
    public void TheRetiredLibraryCacheMarkerIsNotANativeEdit()
    {
        var upgraded = Fixture();
        upgraded.Metadata.UnrepresentedState.Clear();
        upgraded.Metadata.UnrepresentedState.Add(new[] { "complete_project_settings", "shared_screen_root_ownership", "net_chains" });
        var preview23 = upgraded.Clone();
        preview23.Metadata.UnrepresentedState.Insert(2, "library_cache");
        Assert.AreEqual(0, SchematicItemDelta.Plan(upgraded, preview23).Count);
        Assert.AreEqual(0, SchematicItemDelta.Plan(preview23, upgraded).Count);
        Assert.IsTrue(SchematicItemDelta.SameCoverage(upgraded.Metadata.UnrepresentedState, preview23.Metadata.UnrepresentedState));
        // Must-catch: any other coverage difference, with or without the retired marker, and an identity KiCad cannot change.
        foreach (var (what, edit) in new (string, Action<SchematicMetadata>)[]
        {
            ("another marker", m => m.UnrepresentedState.Add("future_settings")),
            ("a marker dropped", m => m.UnrepresentedState.Remove("net_chains")),
            ("another screen identity", m => m.ScreenId = new() { Value = Guid.NewGuid().ToString("D") }),
        })
        {
            var desired = preview23.Clone();
            edit(desired.Metadata);
            var refused = Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(upgraded, desired), what);
            Assert.AreEqual("unsupported_schematic_delta", refused.Code, what);
            Assert.AreEqual("Unsupported settings or document identity changed; those changes cannot be discarded.", refused.Message, what);
        }
    }

    private static SchematicScreenData Fixture()
    {
        string root = Guid.NewGuid().ToString("D");
        var result = new SchematicScreenData { Metadata = new()
        {
            ScreenId = new() { Value = root }, Document = new() { SheetPath = new() }
        } };
        result.Metadata.Document.SheetPath.Path.Add(new Kiapi.Common.Types.KIID { Value = root });
        result.Metadata.UnrepresentedState.Add("library_cache");
        for (int i = 0; i < 3; ++i)
            result.Items.Add(Any.Pack(new SchematicText { Id = new() { Value = Guid.NewGuid().ToString("D") },
                Text = new() { Text_ = "Note " + i, Position = new() { XNm = i * 1000000 } } }));
        return result;
    }

    [TestMethod]
    public void SheetPlansPreserveExplicitChildIdentityAndDoNotInventMissingContents()
    {
        var current = Fixture();
        var sheet = new SheetSymbol { Id = new() { Value = Guid.NewGuid().ToString("D") },
            ChildScreenId = new() { Value = Guid.NewGuid().ToString("D") },
            Path = current.Metadata.Document.SheetPath.Clone(), Position = new(),
            FilenameField = new() { Text = new() { Text_ = "child.kicad_sch", Attributes = new() { Multiline = true } } } };
        current.Items.Add(Any.Pack(sheet));
        var desired = current.Clone(); var moved = sheet.Clone(); moved.Position.XNm = 5000000;
        desired.Items[^1] = Any.Pack(moved);
        Assert.AreEqual(moved, SchematicItemDelta.Plan(current, desired).Single().Update.Unpack<SheetSymbol>());
        var repeated = sheet.Clone(); repeated.Id.Value = Guid.NewGuid().ToString("D");
        desired = current.Clone(); desired.Items.Add(Any.Pack(repeated));
        Assert.AreEqual(repeated, SchematicItemDelta.Plan(current, desired).Single().Create.Unpack<SheetSymbol>());
        desired = current.Clone(); desired.Items.RemoveAt(desired.Items.Count - 1);
        Assert.AreEqual(sheet.Id, SchematicItemDelta.Plan(current, desired).Single().Remove);
        foreach (string problem in new[] { "child", "file", "parent", "missing" })
        {
            var invalid = sheet.Clone();
            switch (problem)
            {
                case "child": invalid.ChildScreenId.Value = Guid.NewGuid().ToString("D"); break;
                case "file": invalid.FilenameField.Text.Text_ = "other.kicad_sch"; break;
                case "parent": invalid.Path.Path[0].Value = Guid.NewGuid().ToString("D"); break;
                case "missing": invalid.ChildScreenId = null; break;
            }
            desired = current.Clone(); desired.Items[^1] = Any.Pack(invalid); Reject(current, desired);
        }
        var newChild = repeated.Clone(); newChild.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        desired = current.Clone(); desired.Items.Add(Any.Pack(newChild)); Reject(current, desired);
        Assert.AreEqual(0, SchematicItemDelta.Plan(current, current).Count);
    }

    [TestMethod]
    public void NewerOrContradictoryFormatsRejectPlanningWithoutLosingXml()
    {
        var current = Fixture();
        foreach (bool changeLoaded in new[] { false, true })
        {
            var newer = current.Clone();
            if (changeLoaded) newer.Metadata.LoadedNativeFormatVersion = SchematicItemDelta.SupportedWriterFormatVersion + 1;
            else newer.Metadata.WriterNativeFormatVersion = SchematicItemDelta.SupportedWriterFormatVersion + 1;
            string xml = SchematicDataXml.Write(newer);
            Assert.AreEqual(newer, SchematicDataXml.Read(xml));
            foreach (var (before, after) in new[] { (current, newer), (newer, current), (newer, newer) })
                Assert.AreEqual("unsupported_native_format", Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, after)).Code);
            Assert.AreEqual("unsupported_native_format", Assert.ThrowsExactly<AutomationException>(() => SchematicItemMerge.Plan(current, current, newer)).Code);
            Assert.AreEqual(xml, SchematicDataXml.Write(newer));
        }
        var contradictory = current.Clone(); contradictory.Metadata.LoadedNativeFormatVersion = 20250114;
        contradictory.Metadata.WriterNativeFormatVersion = 20240101;
        Assert.AreEqual("invalid_native_format", Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(contradictory, contradictory)).Code);
        contradictory.Metadata.WriterNativeFormatVersion = SchematicItemDelta.SupportedWriterFormatVersion;
        Assert.AreEqual(0, SchematicItemDelta.Plan(contradictory, contradictory).Count);
    }

    [TestMethod]
    public void PlansExplicitAssetAndRootRecordsTogetherWithItems()
    {
        var current = Fixture(); current.Metadata.EmbeddedFiles = new();
        current.Metadata.RootInstance = new() { PageNumber = "1" };
        var desired = current.Clone(); desired.Metadata.EmbeddedFonts = true;
        desired.Metadata.RootInstance.PageNumber = "10.A";
        var note = desired.Items[0].Unpack<SchematicText>(); note.Text.Text_ = "changed";
        desired.Items[0] = Any.Pack(note);
        var plan = SchematicItemDelta.Plan(current, desired);
        Assert.AreEqual(3, plan.Count);
        Assert.IsTrue(plan.Single(o => o.ReplaceEmbeddedFiles is not null).ReplaceEmbeddedFiles.EmbeddedFonts);
        Assert.AreEqual("10.A", plan.Single(o => o.SetRootInstance is not null).SetRootInstance.PageNumber);
        Assert.AreEqual(0, SchematicItemDelta.Plan(desired, desired).Count);
        desired.Metadata.RootInstance = new();
        Assert.IsFalse(SchematicItemDelta.Plan(current, desired).Single(o => o.SetRootInstance is not null).SetRootInstance.HasPageNumber);
        desired.Metadata.RootInstance = null; Reject(current, desired);
        desired = current.Clone(); desired.Metadata.EmbeddedFiles = null; Reject(current, desired);
    }

    [TestMethod]
    public void ReorderedUnchangedObjectsProduceNoEdits()
    {
        var current = Fixture(); var desired = current.Clone();
        desired.Items.Clear(); desired.Items.Add(current.Items.Reverse());
        Assert.AreEqual(0, SchematicItemDelta.Plan(current, desired).Count);
    }

    [TestMethod]
    public void PlansOnlyChangedIdentitiesAndDoesNotMutateInputs()
    {
        var current = Fixture(); var desired = current.Clone();
        var moved = desired.Items[0].Unpack<SchematicText>(); moved.Text.Position.XNm += 5000000;
        desired.Items[0] = Any.Pack(moved);
        desired.Items.RemoveAt(1);
        var added = new SchematicText { Id = new() { Value = Guid.NewGuid().ToString("D") }, Text = new() { Text_ = "New instruction" } };
        desired.Items.Add(Any.Pack(added));
        string original = SchematicDataXml.Write(current), edited = SchematicDataXml.Write(desired);
        var plan = SchematicItemDelta.Plan(current, desired);
        Assert.AreEqual(3, plan.Count);
        Assert.AreEqual(moved, plan.Single(o => o.OperationCase == SchematicItemOperation.OperationOneofCase.Update).Update.Unpack<SchematicText>());
        Assert.AreEqual(added, plan.Single(o => o.OperationCase == SchematicItemOperation.OperationOneofCase.Create).Create.Unpack<SchematicText>());
        Assert.AreEqual(current.Items[1].Unpack<SchematicText>().Id, plan.Single(o => o.OperationCase == SchematicItemOperation.OperationOneofCase.Remove).Remove);
        Assert.AreEqual(original, SchematicDataXml.Write(current));
        Assert.AreEqual(edited, SchematicDataXml.Write(desired));
    }

    [TestMethod]
    public void MissingCoverageMetadataChangesAndIdentityAmbiguityStopPlanning()
    {
        var current = Fixture(); var desired = current.Clone();
        desired.Metadata.UnrepresentedState.Add("future_settings"); Reject(current, desired);
        desired = current.Clone(); desired.Items.Add(desired.Items[0]); Reject(current, desired);
        desired = current.Clone(); desired.Metadata.ScreenId.Value = Guid.NewGuid().ToString("D"); Reject(current, desired);
        desired = current.Clone(); desired.Items[0] = Any.Pack(new Junction { Id = current.Items[0].Unpack<SchematicText>().Id.Clone() }); Reject(current, desired);
        desired = current.Clone(); desired.Items.Add(Any.Pack(new SheetSymbol { Id = new() { Value = Guid.NewGuid().ToString("D") } })); Reject(current, desired);
        desired = current.Clone(); desired.UnrepresentedItems.Add(new SchematicUnrepresentedItem { Reason = "future object" }); Reject(current, desired);
    }

    private static void Reject(SchematicScreenData current, SchematicScreenData desired) =>
        Assert.AreEqual("unsupported_schematic_delta", Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(current, desired)).Code);
}
