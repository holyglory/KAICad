using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicHierarchyDeltaTests
{
    [TestMethod]
    public void VariantRegistryHasOneProjectOwnerAcrossRepeatedSheets()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        foreach (var screen in after.Instances) screen.Metadata.VariantDescriptions.Add("Assembly", "Near heatsink");
        var operations = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, operations.Count);
        Assert.AreEqual("Near heatsink", operations.Single().ReplaceVariantRegistry.Descriptions["Assembly"]);
        after.Instances[^1].Metadata.VariantDescriptions["Assembly"] = "Contradiction";
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void TextVariablesAreProjectWideAndEmittedOnlyOnce()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        foreach (var screen in after.Instances) screen.Metadata.TextVariables.Add("NOTE", "Place near connector");
        var operations = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, operations.Count);
        Assert.AreEqual("Place near connector", operations.Single().ReplaceTextVariables.Variables["NOTE"]);
        after.Instances[^1].Metadata.TextVariables["NOTE"] = "Contradiction";
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void ProjectBusAliasesAreEmittedOnceAndConflictingSheetCopiesReject()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        foreach (var screen in after.Instances)
            screen.Metadata.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } });
        var plan = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, plan.Count);
        Assert.AreEqual("DATA", plan.Single().ReplaceBusAliases.Aliases.Single().Name);
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(after, after).Count);
        after.Instances[^1].Metadata.BusAliases[0].Members.Add("D2");
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void AnotherInstanceUsesExistingContentsAndRejectsConflictingCopies()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        var repeated = after.Instances[0].Items[0].Unpack<SheetSymbol>();
        repeated.Id.Value = Guid.NewGuid().ToString("D");
        after.Instances[0].Items.Add(Any.Pack(repeated));
        var instance = after.Instances[1].Clone();
        instance.Metadata.Document.SheetPath.Path[^1] = repeated.Id.Clone();
        after.Instances.Add(instance);
        var plan = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, plan.Count);
        Assert.AreEqual(repeated, plan[0].Create.Unpack<SheetSymbol>());
        var bad = after.Clone(); var note = bad.Instances[^1].Items[0].Unpack<SchematicText>();
        note.Text.Text_ = "Conflicting shared contents"; bad.Instances[^1].Items[0] = Any.Pack(note);
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, bad));
        bad = after.Clone(); bad.Instances[^1].Metadata.TitleBlock = new() { Title = "Conflicting metadata" };
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, bad));
        VerifyExplicitRelocationChoices();

        // Extend this existing repeated-screen fixture for the intentionally
        // isolated ownership comparison. Native execution is verified separately.
        static void VerifyExplicitRelocationChoices()
        {
            var original = SchematicHierarchyTopologyTests.Fixture();
            var root = original.Instances[0];
            var parents = original.Instances.Skip(1).ToArray();
            var template = root.Items[0].Unpack<SheetSymbol>();
            static void BindReferences(SchematicHierarchyData data)
            {
                foreach (var group in data.Instances.GroupBy(s => s.Metadata.ScreenId.Value))
                {
                    var instances = group.ToArray();
                    foreach (var id in instances[0].Items.Where(i => i.Is(SheetSymbol.Descriptor))
                        .Select(i => i.Unpack<SheetSymbol>().Id.Value).ToArray())
                    {
                        var records = instances.Select(screen =>
                        {
                            var sheet = screen.Items.Where(i => i.Is(SheetSymbol.Descriptor))
                                .Select(i => i.Unpack<SheetSymbol>()).Single(s => s.Id.Value == id);
                            var record = new SheetPlacementRecord { ProjectName = data.Document.Project?.Name ?? "",
                                PageNumber = sheet.PageNumber, Variants = sheet.Variants?.Clone() ?? new() };
                            foreach (var variant in record.Variants.Variants) variant.ClearDescription();
                            record.Path.Add(screen.Metadata.Document.SheetPath.Path.Select(p => p.Clone())); return record;
                        }).ToArray();
                        foreach (var screen in instances)
                        {
                            int index = screen.Items.Select((item, i) => (item, i)).Single(x => x.item.Is(SheetSymbol.Descriptor)
                                && x.item.Unpack<SheetSymbol>().Id.Value == id).i;
                            var sheet = screen.Items[index].Unpack<SheetSymbol>(); sheet.InstanceRecords = new();
                            sheet.InstanceRecords.Records.Add(records.Select(r => r.Clone())); screen.Items[index] = Any.Pack(sheet);
                        }
                    }
                }
            }
            SchematicScreenData[] AddChildren(SchematicScreenData[] owners)
            {
                var id = new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") };
                var screenId = new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") };
                return owners.Select(owner =>
                {
                    var sheet = template.Clone(); sheet.Id = id.Clone(); sheet.ChildScreenId = screenId.Clone();
                    sheet.Path = owner.Metadata.Document.SheetPath.Clone(); sheet.FilenameField.Text.Text_ = id.Value + ".kicad_sch";
                    owner.Items.Add(Any.Pack(sheet));
                    var child = new SchematicScreenData { Metadata = owner.Metadata.Clone() };
                    child.Metadata.ScreenId = screenId.Clone(); child.Metadata.Document.SheetPath.Path.Add(id.Clone());
                    original.Instances.Add(child); return child;
                }).ToArray();
            }
            var children = AddChildren(parents); var descendants = AddChildren(children);
            BindReferences(original);
            string first = SchematicNativeSheetChanges.Key(children[0]), second = SchematicNativeSheetChanges.Key(children[1]);
            var relocated = original.Clone();
            foreach (var parent in relocated.Instances.Take(3).Skip(1))
            {
                var item = parent.Items.Single(i => i.Is(SheetSymbol.Descriptor));
                parent.Items.Remove(item);
            }
            var reference = parents[1].Items.Single(i => i.Is(SheetSymbol.Descriptor)).Unpack<SheetSymbol>();
            reference.Path = root.Metadata.Document.SheetPath.Clone(); relocated.Instances[0].Items.Add(Any.Pack(reference));
            string target = SchematicNativeSheetChanges.Key(root) + "/" + reference.Id.Value;
            for (int i = relocated.Instances.Count - 1; i >= 0; --i)
            {
                var screen = relocated.Instances[i]; string path = SchematicNativeSheetChanges.Key(screen);
                if (path == first || path.StartsWith(first + "/", StringComparison.Ordinal)) { relocated.Instances.RemoveAt(i); continue; }
                if (path != second && !path.StartsWith(second + "/", StringComparison.Ordinal)) continue;
                screen.Metadata.Document.SheetPath.Path.Clear();
                screen.Metadata.Document.SheetPath.Path.Add((target + path[second.Length..]).Split('/')
                    .Select(value => new Kiapi.Common.Types.KIID { Value = value }));
                for (int n = 0; n < screen.Items.Count; ++n)
                    if (screen.Items[n].Is(SheetSymbol.Descriptor))
                    { var sheet = screen.Items[n].Unpack<SheetSymbol>(); sheet.Path = screen.Metadata.Document.SheetPath.Clone(); screen.Items[n] = Any.Pack(sheet); }
            }
            BindReferences(relocated);
            Assert.IsTrue(SchematicHierarchyTopology.Inspect(original).IsValid);
            Assert.IsTrue(SchematicHierarchyTopology.Inspect(relocated).IsValid);
            Assert.AreEqual(SchematicNativeSheetChanges.MoveAmbiguous, SchematicNativeSheetChanges.Compare(original, relocated).ErrorCode);
            var choice = new SchematicSheetInstanceChoices([new(second, target)], [first], []);
            var collapse = SchematicNativeSheetChanges.Compare(original, relocated, choice);
            Assert.IsNull(collapse.ErrorCode); Assert.HasCount(2, collapse.Moved); Assert.HasCount(2, collapse.Removed);
            Assert.AreEqual(target, collapse.Now(second)); Assert.IsNull(collapse.Now(first));
            var collapsePlan = SchematicHierarchyDelta.PlanWithSheetChoices(original, relocated, choice);
            var collapseMapping = collapsePlan.Single(o => o.SetSheetInstancePaths is not null).SetSheetInstancePaths;
            Assert.AreEqual(reference.Id, collapseMapping.SourceSheetId);
            Assert.AreEqual(reference.Id, collapseMapping.DestinationSheetId);
            Assert.AreEqual(root.Metadata.Document, collapseMapping.DestinationDocument);
            Assert.AreEqual(second, string.Join('/', collapseMapping.Moves.Single().Before.Path.Select(p => p.Value)));
            Assert.AreEqual(target, string.Join('/', collapseMapping.Moves.Single().After.Path.Select(p => p.Value)));
            Assert.AreEqual(first, string.Join('/', collapseMapping.Retired.Single().Path.Select(p => p.Value)));
            Assert.IsEmpty(collapseMapping.Added);
            Assert.AreEqual(collapseMapping.SourceDocument, collapsePlan.Single(o => o.Remove is not null).TargetDocument,
                "The mapping names the exact deduplicated physical removal target, independently of which instance survives.");
            Assert.AreEqual(target + "/" + descendants[1].Metadata.Document.SheetPath.Path[^1].Value,
                collapse.Now(SchematicNativeSheetChanges.Key(descendants[1])));
            var expansion = SchematicNativeSheetChanges.Compare(relocated, original,
                new([new(target, second)], [], [first]));
            Assert.IsNull(expansion.ErrorCode); Assert.HasCount(2, expansion.Moved); Assert.HasCount(2, expansion.Inserted);
            var expansionPlan = SchematicHierarchyDelta.PlanWithSheetChoices(relocated, original,
                new([new(target, second)], [], [first]));
            var expansionMapping = expansionPlan.Single(o => o.SetSheetInstancePaths is not null).SetSheetInstancePaths;
            Assert.AreEqual(first, string.Join('/', expansionMapping.Added.Single().Path.Select(p => p.Value)));
            Assert.IsEmpty(expansionMapping.Retired);
            var wrapped = relocated.Clone();
            var wrapperReference = template.Clone(); wrapperReference.Id.Value = Guid.NewGuid().ToString("D");
            wrapperReference.ChildScreenId.Value = Guid.NewGuid().ToString("D");
            wrapperReference.NameField.Text.Text_ = "Wrapper";
            wrapperReference.FilenameField.Text.Text_ = wrapperReference.Id.Value + ".kicad_sch";
            // The root fixture has a known empty cache; the existing shared child
            // deliberately carries an unknown-cache marker for coverage tests.
            var wrapper = new SchematicScreenData { Metadata = root.Metadata.Clone() };
            wrapper.Metadata.LoadedNativeFormatVersion = 0; wrapper.Metadata.RootInstance = new();
            wrapper.Metadata.ScreenId = wrapperReference.ChildScreenId.Clone(); wrapper.Metadata.Document = root.Metadata.Document.Clone();
            wrapper.Metadata.Document.SheetPath.Path.Add(wrapperReference.Id.Clone());
            string wrapperPath = SchematicNativeSheetChanges.Key(wrapper);
            var moving = wrapped.Instances[0].Items.Single(i => i.Is(SheetSymbol.Descriptor)
                && i.Unpack<SheetSymbol>().Id.Equals(reference.Id));
            wrapped.Instances[0].Items.Remove(moving);
            var wrappedReference = moving.Unpack<SheetSymbol>(); wrappedReference.Path = wrapper.Metadata.Document.SheetPath.Clone();
            wrapper.Items.Add(Any.Pack(wrappedReference)); wrapped.Instances[0].Items.Add(Any.Pack(wrapperReference));
            string wrappedTarget = wrapperPath + "/" + reference.Id.Value;
            foreach (var screen in wrapped.Instances)
            {
                string path = SchematicNativeSheetChanges.Key(screen);
                if (path != target && !path.StartsWith(target + "/", StringComparison.Ordinal)) continue;
                screen.Metadata.Document.SheetPath.Path.Clear();
                screen.Metadata.Document.SheetPath.Path.Add((wrappedTarget + path[target.Length..]).Split('/')
                    .Select(value => new Kiapi.Common.Types.KIID { Value = value }));
                for (int n = 0; n < screen.Items.Count; ++n)
                    if (screen.Items[n].Is(SheetSymbol.Descriptor))
                    { var sheet = screen.Items[n].Unpack<SheetSymbol>(); sheet.Path = screen.Metadata.Document.SheetPath.Clone(); screen.Items[n] = Any.Pack(sheet); }
            }
            wrapped.Instances.Add(wrapper); BindReferences(wrapped);
            Assert.IsTrue(SchematicHierarchyTopology.Inspect(wrapped).IsValid);
            var throughNewParent = SchematicNativeSheetChanges.Compare(original, wrapped,
                new([new(second, wrappedTarget)], [first], [wrapperPath]));
            Assert.IsNull(throughNewParent.ErrorCode); Assert.HasCount(2, throughNewParent.Moved);
            CollectionAssert.AreEqual(new[] { wrapperPath }, throughNewParent.Inserted.ToArray(),
                "An added parent does not turn the moved subtree into newly owned instances.");
            var wrappedPlan = SchematicHierarchyDelta.PlanWithSheetChoices(original, wrapped,
                new([new(second, wrappedTarget)], [first], [wrapperPath]));
            var wrappedMapping = wrappedPlan.Single(o => o.SetSheetInstancePaths is not null).SetSheetInstancePaths;
            Assert.AreEqual(wrapper.Metadata.Document, wrappedMapping.DestinationDocument);
            Assert.IsEmpty(wrappedMapping.Added, "The new unique parent is created separately from the existing child reference's instance mapping.");
            foreach (var invalid in new[]
            {
                choice with { Retired = [] },
                choice with { Moves = [new(second, target), new(first, target)], Retired = [] },
                choice with { Retired = [first, second] },
                choice with { Added = [target] },
                choice with { Moves = [new(second, SchematicNativeSheetChanges.Key(root))] },
                choice with { Retired = [first, first] }
            }) Assert.AreEqual(SchematicNativeSheetChanges.MoveAnswerInvalid,
                SchematicNativeSheetChanges.Compare(original, relocated, invalid).ErrorCode);
            Assert.AreEqual(SchematicNativeSheetChanges.MoveAmbiguous, SchematicNativeSheetChanges.Compare(original, relocated).ErrorCode,
                "A prior answer cannot silently change a later comparison.");
        }
    }

    [TestMethod]
    public void NewNestedScreensAreCreatedBeforeTheirContentsWithoutDiscardingUnknownState()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        var template = before.Instances[0].Items[0].Unpack<SheetSymbol>();
        SchematicScreenData Add(SchematicScreenData parent)
        {
            var sheet = template.Clone(); sheet.Id.Value = Guid.NewGuid().ToString("D");
            sheet.ChildScreenId.Value = Guid.NewGuid().ToString("D"); sheet.Path = parent.Metadata.Document.SheetPath.Clone();
            sheet.FilenameField.Text.Text_ = sheet.Id.Value + ".kicad_sch";
            parent.Items.Add(Any.Pack(sheet));
            var screen = new SchematicScreenData { Metadata = new() { ScreenId = sheet.ChildScreenId.Clone(),
                Document = parent.Metadata.Document.Clone(), TitleBlock = new() { Title = "Generated sheet" } } };
            screen.Metadata.Document.SheetPath.Path.Add(sheet.Id.Clone());
            screen.Items.Add(Any.Pack(new SchematicText { Id = new() { Value = Guid.NewGuid().ToString("D") }, Text = new() { Text_ = "New contents" } }));
            after.Instances.Add(screen); return screen;
        }
        var child = Add(after.Instances[0]); var nested = Add(child);
        var plan = SchematicHierarchyDelta.Plan(before, after).ToList();
        var creates = plan.Where(o => o.Create?.Is(SheetSymbol.Descriptor) == true).ToArray();
        Assert.AreEqual(2, creates.Length);
        foreach (var operation in creates)
        {
            var sheet = operation.Create.Unpack<SheetSymbol>();
            var target = operation.TargetDocument.Clone(); target.SheetPath.Path.Add(sheet.Id.Clone());
            Assert.IsTrue(plan.FindIndex(o => o.TargetDocument.Equals(target)) > plan.IndexOf(operation));
        }
        Assert.AreEqual(2, plan.Count(o => o.SetTitleBlock is not null));
        var removal = SchematicHierarchyDelta.Plan(after, before);
        Assert.AreEqual(1, removal.Count, "A removed subtree needs one parent-reference removal, not deletion of its contents.");
        Assert.AreEqual(child.Metadata.Document.SheetPath.Path[^1], removal[0].Remove);
        Assert.AreEqual(before.Document, removal[0].TargetDocument);
        var reparented = after.Clone();
        var oldParent = reparented.Instances.Single(s => s.Metadata.Document.Equals(child.Metadata.Document));
        var moved = oldParent.Items.Single(i => i.Is(SheetSymbol.Descriptor)).Unpack<SheetSymbol>();
        oldParent.Items.Remove(Any.Pack(moved)); moved.Path = before.Document.SheetPath.Clone();
        reparented.Instances[0].Items.Add(Any.Pack(moved));
        var movedScreen = reparented.Instances.Single(s => s.Metadata.ScreenId.Equals(nested.Metadata.ScreenId));
        movedScreen.Metadata.Document = before.Document.Clone(); movedScreen.Metadata.Document.SheetPath.Path.Add(moved.Id.Clone());
        var movePlan = SchematicHierarchyDelta.Plan(after, reparented);
        Assert.AreEqual(2, movePlan.Count);
        Assert.AreEqual(moved.Id, movePlan.Single(o => o.Remove is not null).Remove);
        Assert.AreEqual(moved, movePlan.Single(o => o.Create is not null).Create.Unpack<SheetSymbol>());
        foreach (string unsupported in new[] { "unknown", "alias", "provenance", "root" })
        {
            var bad = after.Clone(); var metadata = bad.Instances[^1].Metadata;
            switch (unsupported)
            {
                case "unknown": metadata.UnrepresentedState.Add("library_cache"); break;
                case "alias": metadata.BusAliases.Add(new SchematicBusAlias { Name = "uninitialized" }); break;
                case "provenance": metadata.LoadedNativeFormatVersion = 20250114; break;
                case "root": metadata.RootInstance = new() { PageNumber = "7" }; break;
            }
            Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, bad), unsupported);
        }
        foreach (var screen in after.Instances)
            screen.Metadata.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } });
        var withAliases = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, withAliases.Count(o => o.ReplaceBusAliases is not null));
        Assert.AreEqual(2, withAliases.Count(o => o.Create?.Is(SheetSymbol.Descriptor) == true));
        foreach (var screen in before.Instances)
            screen.Metadata.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } });
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(before, after).Count(o => o.ReplaceBusAliases is not null));
        foreach (var screen in after.Instances) screen.Metadata.TextVariables.Add("NOTE", "New hierarchy");
        Assert.AreEqual(1, SchematicHierarchyDelta.Plan(before, after).Count(o => o.ReplaceTextVariables is not null));
    }

    [TestMethod]
    public void SharedSymbolMovesRetainDifferentReferencesAndUnitsInCompleteRecords()
    {
        var before = SchematicHierarchyTopologyTests.Fixture();
        var records = new SymbolSheetRecords();
        for (int i = 1; i < before.Instances.Count; ++i)
        {
            var record = new SymbolSheetRecord { Reference = "U" + i, Unit = i, ProjectName = "fixture", Variants = new() };
            record.Variants.Variants.Add(new SchematicSymbolVariant { Name = "assembly", Attributes = new() { DoNotPopulate = i == 2 } });
            record.Path.Add(before.Instances[i].Metadata.Document.SheetPath.Path);
            records.Records.Add(record);
        }
        string identity = Guid.NewGuid().ToString("D");
        for (int i = 1; i < before.Instances.Count; ++i)
        {
            before.Instances[i].Items.Add(Any.Pack(new SchematicSymbolInstance
            {
                Id = new() { Value = identity }, Path = before.Instances[i].Metadata.Document.SheetPath.Clone(),
                Position = new(), ReferenceField = new() { Text = new() { Text_ = "U" + i,
                    Attributes = new() { Multiline = true } } },
                Unit = new() { Unit = i }, InstanceRecords = records.Clone(), Variants = records.Records[i - 1].Variants.Clone()
            }));
        }
        var after = before.Clone();
        foreach (var screen in after.Instances.Skip(1))
        {
            var symbol = screen.Items[^1].Unpack<SchematicSymbolInstance>(); symbol.Position.XNm = 1000000;
            screen.Items[^1] = Any.Pack(symbol);
        }
        string desired = SchematicDataXml.Write(after);
        var plan = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(2, plan.Count);
        Assert.AreEqual(before.Instances[1].Metadata.ScreenId, plan[1].ReplaceLibraryCache.ScreenId);
        Assert.AreEqual(0, plan[1].ReplaceLibraryCache.Definitions.Count);
        Assert.AreEqual(records, plan[0].Update.Unpack<SchematicSymbolInstance>().InstanceRecords);
        Assert.AreEqual(desired, SchematicDataXml.Write(after));
        foreach (string invalid in new[] { "reference", "unit", "path", "records", "geometry", "variant", "description" })
        {
            var bad = after.Clone(); var symbol = bad.Instances[2].Items[^1].Unpack<SchematicSymbolInstance>();
            switch (invalid)
            {
                case "reference": symbol.ReferenceField.Text.Text_ = "U99"; break;
                case "unit": symbol.Unit.Unit = 9; break;
                case "path": symbol.Path = before.Document.SheetPath.Clone(); break;
                case "records": symbol.InstanceRecords = null; break;
                case "geometry": symbol.Position.XNm++; break;
                case "variant": symbol.Variants.Variants[0].Attributes.DoNotPopulate = false; break;
                case "description": symbol.Variants.Variants[0].Description = "Changed project description"; break;
            }
            bad.Instances[2].Items[^1] = Any.Pack(symbol);
            Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, bad), invalid);
        }
    }

    [TestMethod]
    public void SharedScreenEditsAreTargetedOnceAndOrderIndependent()
    {
        var before = SchematicHierarchyTopologyTests.Fixture();
        var after = before.Clone();
        foreach (var screen in after.Instances.Skip(1))
        {
            var note = screen.Items[0].Unpack<SchematicText>();
            note.Text.Text_ = "Updated shared note"; screen.Items[0] = Any.Pack(note);
        }
        string original = SchematicDataXml.Write(before), desired = SchematicDataXml.Write(after);
        var plan = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, plan.Count);
        Assert.AreEqual("Updated shared note", plan[0].Update.Unpack<SchematicText>().Text.Text_);
        Assert.AreEqual(2, plan[0].TargetDocument.SheetPath.Path.Count);
        var reversed = after.Clone(); reversed.Instances.Clear(); reversed.Instances.Add(after.Instances.Reverse());
        CollectionAssert.AreEqual(plan.ToArray(), SchematicHierarchyDelta.Plan(before, reversed).ToArray());
        Assert.AreEqual(original, SchematicDataXml.Write(before)); Assert.AreEqual(desired, SchematicDataXml.Write(after));
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(before, before).Count);
    }

    [TestMethod]
    public void ConflictingSharedEditsAndMissingInstancesRejectWithoutPartialPlan()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        var note = after.Instances[1].Items[0].Unpack<SchematicText>();
        note.Text.Text_ = "Only one instance changed"; after.Instances[1].Items[0] = Any.Pack(note);
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
        after = before.Clone(); after.Instances.RemoveAt(1);
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
        after = before.Clone(); after.Instances[1].Metadata.EmbeddedFonts = true;
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicHierarchyDelta.Plan(before, before, cancelled.Token));
    }
}
