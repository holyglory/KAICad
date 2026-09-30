using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyPageSettings(NativeClient client, DocumentSpecifier document,
        int processId, string display, string directory, string evidence, string instanceId, CancellationToken token)
    {
        var beforeFocus = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        // A newly opened editor need not have canvas focus yet. Later Undo/Redo
        // does establish it; compare page methods from the same observed input state.
        await FocusedSchematicShortcut(client, document, processId, display, "", token);
        Assert.AreEqual(beforeFocus, await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token), "Establishing focus must not alter native design state.");
        var query = new GetPageSettings { Document = document };
        var original = await client.InvokeAsync<GetPageSettings, PageSettings>(query, token);
        var originalTitle = await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token);
        var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
            new() { Document = document }, token);
        var cursor = new ReadSchematicChangeJournal
        {
            Document = document, DocumentEpoch = journal.DocumentEpoch, AfterSequence = journal.Sequence
        };
        string layoutPath = Path.Combine(directory, "automation-layout.kicad_wks");
        await File.WriteAllTextAsync(layoutPath,
            "(kicad_wks (version 20220228) (generator pl_editor) " +
            "(setup (textsize 1.5 1.5) (linewidth 0.15) (textlinewidth 0.15) " +
            "(left_margin 10) (right_margin 10) (top_margin 10) (bottom_margin 10)) " +
            "(rect (name \"Frame\") (start 0 0 ltcorner) (end 0 0)) " +
            "(tbtext \"AUTOMATION PAGE FIXTURE\" (name \"Label\") (pos 10 10 ltcorner)))", token);
        var custom = new PageSettings
        {
            PageSize = (PageSize)16, Orientation = (PageOrientation)2,
            UserPageSize = new Vector2 { XNm = 200000000, YNm = 300000000 },
            DrawingSheet = layoutPath
        };
        var tooSmall = custom.Clone(); tooSmall.UserPageSize.XNm = 1;
        var tooLarge = custom.Clone(); tooLarge.UserPageSize.YNm = long.MaxValue;
        var contradictory = custom.Clone(); contradictory.Orientation = (PageOrientation)1;
        var unknown = custom.Clone(); unknown.PageSize = (PageSize)999;
        var missingDimensions = custom.Clone(); missingDimensions.UserPageSize = null;
        var missingLayout = custom.Clone(); missingLayout.DrawingSheet = Path.Combine(directory, "absent.kicad_wks");
        string malformedPath = Path.Combine(directory, "malformed.kicad_wks");
        await File.WriteAllTextAsync(malformedPath, "(not-a-drawing-sheet", token);
        var malformedLayout = custom.Clone(); malformedLayout.DrawingSheet = malformedPath;
        string truncatedPath = Path.Combine(directory, "truncated.kicad_wks");
        await File.WriteAllTextAsync(truncatedPath, "(kicad_wks (version 20220228)", token);
        var truncatedLayout = custom.Clone(); truncatedLayout.DrawingSheet = truncatedPath;
        string futurePath = Path.Combine(directory, "future.kicad_wks");
        await File.WriteAllTextAsync(futurePath, "(kicad_wks (version 99999999))", token);
        var futureLayout = custom.Clone(); futureLayout.DrawingSheet = futurePath;
        string trailingPath = Path.Combine(directory, "trailing.kicad_wks");
        string validLayout = await File.ReadAllTextAsync(layoutPath, token);
        await File.WriteAllTextAsync(trailingPath, validLayout + " (page_layout)", token);
        var trailingLayout = custom.Clone(); trailingLayout.DrawingSheet = trailingPath;
        foreach (var invalid in new[] { tooSmall, tooLarge, contradictory, unknown, missingDimensions, missingLayout,
                                        malformedLayout, truncatedLayout, futureLayout, trailingLayout })
        {
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<SetPageSettings, PageSettings>(new() { Document = document, PageSettings = invalid }, token),
                $"Invalid page settings must reject: {invalid}")).Status);
            Assert.AreEqual(original, await client.InvokeAsync<GetPageSettings, PageSettings>(query, token));
            Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes.Count);
            var rejectedBatch = new ApplySchematicItemBatch { Document = document };
            rejectedBatch.Operations.Add(new SchematicItemOperation { SetTitleBlock = new() { Title = "must roll back" } });
            rejectedBatch.Operations.Add(new SchematicItemOperation { SetPageSettings = invalid });
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejectedBatch, token));
            Assert.AreEqual(original, await client.InvokeAsync<GetPageSettings, PageSettings>(query, token));
            Assert.AreEqual(originalTitle, await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token));
            Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes.Count);
        }

        string legacyPath = Path.Combine(directory, "legacy.kicad_wks");
        await File.WriteAllTextAsync(legacyPath, validLayout.Replace(
            "(kicad_wks (version 20220228) (generator pl_editor)", "(page_layout", StringComparison.Ordinal), token);
        var legacy = custom.Clone(); legacy.DrawingSheet = Path.GetFileName(legacyPath);

        // Standard portrait, custom dimensions, and a supported unversioned
        // asset referenced relative to the engineering project.
        var standaloneImages = new Dictionary<string, SchematicPreview>();
        int scenario = 0;
        foreach (bool useBatch in new[] { false, true })
        foreach (var requested in new[] { new PageSettings { PageSize = (PageSize)4, Orientation = (PageOrientation)2 }, custom, legacy })
        {
            var before = await client.InvokeAsync<GetPageSettings, PageSettings>(query, token);
            var set = new SetPageSettings { Document = document, PageSettings = requested };
            async Task Set()
            {
                if (useBatch)
                {
                    var batch = new ApplySchematicItemBatch { Document = document };
                    batch.Operations.Add(new SchematicItemOperation { SetPageSettings = requested });
                    await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
                }
                else await client.InvokeAsync<SetPageSettings, PageSettings>(set, token);
                Assert.AreEqual(requested, await client.InvokeAsync<GetPageSettings, PageSettings>(query, token));
            }
            await Set();
            var rendered = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(
                new() { Document = document }, token);
            await File.WriteAllBytesAsync(Path.Combine(evidence, $"{instanceId}-page-method-{scenario++}.png"), rendered.Png.ToByteArray(), token);
            string renderKey = requested.ToString();
            if (!useBatch) standaloneImages.Add(renderKey, rendered);
            else
            {
                Assert.AreEqual(standaloneImages[renderKey].Viewport, rendered.Viewport);
                Assert.IsTrue(standaloneImages[renderKey].Png.Equals(rendered.Png),
                    "Atomic page edits must repaint the same pixels as the standalone page command.");
            }
            var changed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token);
            Assert.AreEqual(1, changed.Changes.Count);
            cursor.AfterSequence = changed.Sequence;
            await Set();
            Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes.Count);
            foreach (var (key, kind) in new[] { ("z", SchematicChange.Types.Kind.Undo), ("y", SchematicChange.Types.Kind.Redo) })
            {
                NativeKeyboard.SchematicShortcut(display, processId, key);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(TimeSpan.FromSeconds(5));
                do
                {
                    changed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, wait.Token);
                    if (changed.Changes.Count == 0) await Task.Delay(100, wait.Token);
                } while (changed.Changes.Count == 0);
                Assert.AreEqual(kind, changed.Changes.Last().Kind);
                cursor.AfterSequence = changed.Sequence;
                Assert.AreEqual(kind == SchematicChange.Types.Kind.Undo ? before : requested,
                    await client.InvokeAsync<GetPageSettings, PageSettings>(query, token));
            }
        }
        var image = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, $"{instanceId}-page-redo.png"), image.Png.ToByteArray(), token);
    }
}
