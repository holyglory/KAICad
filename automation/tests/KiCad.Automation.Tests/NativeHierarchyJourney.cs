using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private sealed record HierarchyFixture(string Contents, string First, string Second, string TextId, string Directory);

    private static async Task<HierarchyFixture> MakeHierarchyFixture(string rootId, string directory, CancellationToken token)
    {
        string first = Guid.NewGuid().ToString("D"), second = Guid.NewGuid().ToString("D");
        string text = Guid.NewGuid().ToString("D");
        string innerGroup = Guid.NewGuid().ToString("D"), outerGroup = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(Path.Combine(directory, "shared-child.kicad_sch"), $$"""
            (kicad_sch (version 20250114) (generator eeschema) (uuid {{Guid.NewGuid():D}})
              (paper "A4") (lib_symbols)
              (text "Shared child contents" (at 50 50 0) (effects (font (size 1.27 1.27))) (uuid {{text}}))
              (group "Linked supply" (uuid {{innerGroup}}) (lib_id "FixtureBlocks:Supply") (members "{{text}}"))
              (group "Outer assembly" (uuid {{outerGroup}}) (members "{{innerGroup}}")))
            """, token);
        string Sheet(string id, string name, string page, int y) => $$"""
            (sheet (at 160 {{y}}) (size 20 15) (stroke (width 0) (type default))
              (fill (color 0 0 0 0)) (uuid {{id}})
              (property "Sheetname" "{{name}}" (at 160 {{y}} 0) (effects (font (size 1.27 1.27))))
              (property "Sheetfile" "shared-child.kicad_sch" (at 160 {{y + 15}} 0) (effects (font (size 1.27 1.27))))
              (instances (project "fixture" (path "/{{rootId}}" (page "{{page}}")))))
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, "unloaded.kicad_sch"),
            await File.ReadAllTextAsync(Path.Combine(directory, "shared-child.kicad_sch"), token), token);
        return new(Sheet(first, "Channel A", "2", 40) + Sheet(second, "Channel B", "3", 70), first, second, text, directory);
    }

    private static async Task VerifyHierarchyBatch(NativeClient client, DocumentSpecifier document,
        HierarchyFixture fixture, int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        var hierarchyQuery = new GetSchematicHierarchy { Document = document };
        var baseline = await client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token);
        Assert.AreEqual(2, baseline.TopLevelSheets.Single().Children.Count);
        var header = new ItemHeader { Document = document };
        var query = new GetItemsById { Header = header };
        query.Items.Add(new KIID { Value = fixture.First });
        var original = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token)).Items.Single().Unpack<SheetSymbol>();
        Assert.IsNotNull(original.ChildScreenId);
        var siblingQuery = query.Clone(); siblingQuery.Items[0].Value = fixture.Second;
        var sibling = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(siblingQuery, token)).Items.Single().Unpack<SheetSymbol>();
        Assert.AreEqual(original.ChildScreenId, sibling.ChildScreenId, "Repeated sheets must reference the same native screen identity.");
        var screenBefore = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token);
        foreach (string problem in new[] { "different", "malformed", "renamed_file" })
        {
            var wrongChild = original.Clone();
            if (problem == "different") wrongChild.ChildScreenId.Value = Guid.NewGuid().ToString("D");
            else if (problem == "malformed") wrongChild.ChildScreenId.Value = "not-a-screen-id";
            else wrongChild.FilenameField.Text.Text_ = "renamed-without-content.kicad_sch";
            var badReference = new ApplySchematicItemBatch { Document = document };
            badReference.Operations.Add(new SchematicItemOperation { Update = Any.Pack(wrongChild) });
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(badReference, token));
            Assert.AreEqual(screenBefore, await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token));
        }
        var firstContents = await ReadChild(fixture.First);
        Assert.AreEqual(firstContents, await ReadChild(fixture.Second));
        var moved = original.Clone(); moved.Position.XNm -= 5000000;
        moved.NameField.Text.Text_ = "Channel A revised";
        moved.PageNumber = "A-2";
        foreach (var placement in moved.InstanceRecords.Records.Where(r => r.Path.SequenceEqual(document.SheetPath.Path)))
            placement.PageNumber = moved.PageNumber;
        var screenDesired = screenBefore.Data.Clone();
        screenDesired.Items[screenDesired.Items.IndexOf(Any.Pack(original))] = Any.Pack(moved);
        var update = new ApplySchematicItemBatch { Document = document, Description = "Arrange repeated sheet" };
        update.Operations.Add(SchematicItemDelta.Plan(screenBefore.Data, screenDesired));
        var rejected = update.Clone(); rejected.Operations.Add(new SchematicItemOperation());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token));
        Assert.AreEqual(baseline, await client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token));
        Assert.AreEqual(firstContents, await ReadChild(fixture.First));
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(update, token);
        Assert.AreEqual(0, SchematicItemDelta.Plan(
            (await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token)).Data,
            screenDesired).Count, "The model-driven sheet edit must preserve all unrelated screen state.");
        Assert.AreEqual(firstContents, await ReadChild(fixture.First));
        Assert.AreEqual(firstContents, await ReadChild(fixture.Second));
        var changedHierarchy = await client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token);
        var changedSheet = changedHierarchy.TopLevelSheets.Single().Children.Single(s => s.Path.Path.Last().Value == fixture.First);
        Assert.AreEqual("Channel A revised", changedSheet.Name);
        Assert.AreEqual("A-2", changedSheet.PageNumber);
        await Undo();
        Assert.AreEqual(baseline, await client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token));
        Assert.AreEqual(original, (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token)).Items.Single().Unpack<SheetSymbol>());

        var deletion = new ApplySchematicItemBatch { Document = document, Description = "Remove one repeated sheet" };
        var withoutFirst = screenBefore.Data.Clone(); withoutFirst.Items.Remove(Any.Pack(original));
        deletion.Operations.Add(SchematicItemDelta.Plan(screenBefore.Data, withoutFirst));
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(deletion, token);
        Assert.AreEqual(1, (await client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token))
            .TopLevelSheets.Single().Children.Count);
        Assert.AreEqual(firstContents, await ReadChild(fixture.Second));
        await Undo();
        Assert.AreEqual(baseline, await client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token));
        Assert.AreEqual(firstContents, await ReadChild(fixture.First));

        var repeated = original.Clone(); repeated.Id.Value = Guid.NewGuid().ToString("D");
        repeated.NameField.Text.Text_ = "Channel C"; repeated.PageNumber = "4";
        foreach (var placement in repeated.InstanceRecords.Records.Where(r => r.Path.SequenceEqual(document.SheetPath.Path)))
            placement.PageNumber = repeated.PageNumber;
        repeated.Position.YNm += 50000000;
        var creation = new ApplySchematicItemBatch { Document = document, Description = "Add repeated sheet" };
        var withRepeated = screenBefore.Data.Clone(); withRepeated.Items.Add(Any.Pack(repeated));
        creation.Operations.Add(SchematicItemDelta.Plan(screenBefore.Data, withRepeated));
        var failedCreation = creation.Clone(); failedCreation.Operations.Add(new SchematicItemOperation());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(failedCreation, token));
        Assert.AreEqual(baseline, await client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token));
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(creation, token);
        Assert.AreEqual(3, (await client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token))
            .TopLevelSheets.Single().Children.Count);
        Assert.AreEqual(firstContents, await ReadChild(repeated.Id.Value), "A repeated sheet must reuse the loaded content.");
        await Undo();
        Assert.AreEqual(baseline, await client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token));
        Assert.AreEqual(firstContents, await ReadChild(fixture.Second));

        var staging = await client.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = header }, token);
        Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token))).Status);
        await client.InvokeAsync<EndCommit, EndCommitResponse>(new() { Header = header, Id = staging.Id, Action = (CommitAction)2 }, token);

        foreach (string invalidFile in new[] { "", "fixture.kicad_sch", "unloaded.kicad_sch" })
        {
            var invalid = repeated.Clone(); invalid.FilenameField.Text.Text_ = invalidFile;
            var rejectedSheet = new ApplySchematicItemBatch { Document = document };
            rejectedSheet.Operations.Add(new SchematicItemOperation { Create = Any.Pack(invalid) });
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejectedSheet, token))).Status);
            Assert.AreEqual(baseline, await client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token));
        }

        var stagedFirst = repeated.Clone(); stagedFirst.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        stagedFirst.FilenameField.Text.Text_ = "batch-child.kicad_sch";
        var stagedSecond = stagedFirst.Clone(); stagedSecond.Id.Value = Guid.NewGuid().ToString("D");
        stagedSecond.FilenameField.Text.Text_ = "batch-other.kicad_sch";
        var sharedCreation = new ApplySchematicItemBatch { Document = document };
        sharedCreation.Operations.Add(new SchematicItemOperation { Create = Any.Pack(stagedFirst) });
        sharedCreation.Operations.Add(new SchematicItemOperation { Create = Any.Pack(stagedSecond) });
        var beforeShared = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token);
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(sharedCreation, token));
        Assert.AreEqual(beforeShared, await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token),
            "A duplicate child identity for different staged files must roll back the first creation.");
        stagedSecond.FilenameField.Text.Text_ = stagedFirst.FilenameField.Text.Text_;
        sharedCreation.Operations[1].Create = Any.Pack(stagedSecond);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(sharedCreation, token);
        var stagedQuery = new GetItemsById { Header = header }; stagedQuery.Items.Add(stagedFirst.Id); stagedQuery.Items.Add(stagedSecond.Id);
        var stagedSheets = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(stagedQuery, token)).Items;
        Assert.AreEqual(2, stagedSheets.Count);
        Assert.IsTrue(stagedSheets.All(i => i.Unpack<SheetSymbol>().ChildScreenId.Equals(stagedFirst.ChildScreenId)),
            "Same-file staged instances must share exactly one declared screen identity.");
        await Undo();
        Assert.AreEqual(baseline, await client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, token));

        var blank = repeated.Clone(); blank.FilenameField.Text.Text_ = "new-child.kicad_sch";
        blank.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        var blankBatch = new ApplySchematicItemBatch { Document = document, Description = "Create empty child sheet" };
        blankBatch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(blank) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(blankBatch, token);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        string childPath = Path.Combine(fixture.Directory, "new-child.kicad_sch");
        Assert.IsTrue(File.Exists(childPath), "Saving a new child must write its named native schematic.");
        StringAssert.Contains(await File.ReadAllTextAsync(childPath, token), "kicad_sch");
        StringAssert.Contains(await File.ReadAllTextAsync(childPath, token), blank.ChildScreenId.Value,
            "A newly created child must persist its declared screen identity.");
        await Undo();
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "fixture.kicad_sch"), token))
            .Contains(blank.Id.Value, StringComparison.Ordinal), "Saved parent must reflect the undone sheet creation.");
        Assert.AreEqual(firstContents, await ReadChild(fixture.Second));

        // Target the second repeated instance while the root remains displayed.
        var childTarget = document.Clone(); childTarget.SheetPath.Path.Add(new KIID { Value = fixture.Second });
        var childOriginal = await ReadChild(fixture.Second);
        var childMoved = childOriginal.Clone(); childMoved.Text.Position.XNm += 5000000;
        var childBatch = new ApplySchematicItemBatch { Document = childTarget, Description = "Edit shared child contents" };
        childBatch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(childMoved) });
        var rootImage = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        var rejectedChild = childBatch.Clone(); rejectedChild.Operations.Add(new SchematicItemOperation());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejectedChild, token));
        Assert.AreEqual(childOriginal, await ReadChild(fixture.First));
        Assert.AreEqual(rootImage.Png, (await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token)).Png,
            "Rollback in another sheet must not insert its items into the displayed root canvas.");
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(childBatch, token);
        Assert.AreEqual(childMoved, await ReadChild(fixture.First));
        Assert.AreEqual(childMoved, await ReadChild(fixture.Second));
        Assert.AreEqual(rootImage.Png, (await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token)).Png);
        await Undo();
        Assert.AreEqual(childOriginal, await ReadChild(fixture.First));
        Assert.AreEqual(rootImage.Png, (await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token)).Png);

        var childDelete = new ApplySchematicItemBatch { Document = childTarget, Description = "Delete shared child object" };
        childDelete.Operations.Add(new SchematicItemOperation { Remove = new KIID { Value = fixture.TextId } });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(childDelete, token);
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => ReadChild(fixture.First));
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => ReadChild(fixture.Second));
        await Undo();
        Assert.AreEqual(childOriginal, await ReadChild(fixture.First));
        Assert.AreEqual(childOriginal, await ReadChild(fixture.Second));

        foreach (var invalidTarget in new[] { document.Clone(), childTarget.Clone() })
        {
            var invalidPath = invalidTarget.SheetPath ?? throw new InvalidOperationException("Fixture sheet path missing");
            if (invalidPath.Path.Count == 1) invalidTarget.SheetPath = null;
            else invalidPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
            var invalidBatch = childBatch.Clone(); invalidBatch.Document = invalidTarget;
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(invalidBatch, token))).Status);
            Assert.AreEqual(childOriginal, await ReadChild(fixture.Second));
        }

        var navigationJournal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document }, token);
        var pendingNavigation = await client.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = header }, token);
        Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = childTarget }, token))).Status);
        await client.InvokeAsync<EndCommit, EndCommitResponse>(new() { Header = header, Id = pendingNavigation.Id, Action = (CommitAction)2 }, token);
        foreach (string sheetId in new[] { fixture.First, fixture.Second })
        {
            var viewTarget = document.Clone(); viewTarget.SheetPath.Path.Add(new KIID { Value = sheetId });
            Assert.AreEqual(viewTarget, await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = viewTarget }, token));
            Assert.AreEqual(viewTarget, (await client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(new() { Type = (DocumentType)1 }, token)).Documents.Single());
            var electricalRoot = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = document }, token);
            var electricalChild = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = viewTarget }, token);
            Assert.AreEqual(document, electricalRoot.Hierarchy.Data.Document);
            Assert.AreEqual(electricalRoot, electricalChild, "Loaded-sheet targeting must observe the same whole hierarchy without changing the visible sheet.");
            Assert.AreEqual(viewTarget, (await client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(new() { Type = (DocumentType)1 }, token)).Documents.Single());
            var childImage = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = viewTarget }, token);
            Assert.AreEqual(viewTarget, childImage.Document);
            Assert.AreNotEqual(rootImage.Png, childImage.Png, "Navigation must render child contents rather than reuse the root image.");
            await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-child-" + sheetId + ".png"), childImage.Png.ToByteArray(), token);
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token))).Status);
            var absent = viewTarget.Clone(); absent.SheetPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = absent }, token))).Status);
            Assert.AreEqual(viewTarget, await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = viewTarget }, token));
            Assert.AreEqual(childOriginal, await ReadChild(sheetId));
        }
        Assert.AreEqual(document, await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document }, token));
        Assert.AreEqual(navigationJournal.Sequence,
            (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document }, token)).Sequence,
            "Navigation must not create a design edit or consume undo history.");
        var returnedRoot = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-root-before-navigation.png"), rootImage.Png.ToByteArray(), token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-root-after-navigation.png"), returnedRoot.Png.ToByteArray(), token);
        Assert.IsTrue(rootImage.Png.Equals(returnedRoot.Png), "Returning to the root must preserve its view.");
        Assert.AreEqual(rootImage.Viewport, returnedRoot.Viewport);
        Assert.AreEqual(navigationJournal.Sequence, returnedRoot.Revision.Sequence);

        async Task<SchematicText> ReadChild(string sheetId)
        {
            var childDocument = document.Clone(); childDocument.SheetPath.Path.Add(new KIID { Value = sheetId });
            var childQuery = new GetItemsById { Header = new ItemHeader { Document = childDocument } };
            childQuery.Items.Add(new KIID { Value = fixture.TextId });
            return (await client.InvokeAsync<GetItemsById, GetItemsResponse>(childQuery, token)).Items.Single().Unpack<SchematicText>();
        }

        async Task Undo()
        {
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document }, token);
            var cursor = new ReadSchematicChangeJournal { Document = document, DocumentEpoch = journal.DocumentEpoch, AfterSequence = journal.Sequence };
            NativeKeyboard.SchematicShortcut(display, processId, "z");
            using var inputDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            inputDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicChangeJournal changed;
            do
            {
                changed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, inputDeadline.Token);
                if (changed.Changes.Count == 0) await Task.Delay(100, inputDeadline.Token);
            } while (changed.Changes.Count == 0);
        }
    }
}
