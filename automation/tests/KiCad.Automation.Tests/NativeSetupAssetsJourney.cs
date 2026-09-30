using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySetupAssets(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        // The composed fixture starts in a supported older file format. Base
        // reconstruction comparisons on an actual current-writer save/reload;
        // never discard loaded-format metadata from the exact state assertion.
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var original = await Read();
        EmbeddedFile asset = original.Data.Metadata.EmbeddedFiles.Files.Single().Clone();
        var withLinks = original.Data.Clone();
        var first = withLinks.Items.First(x => x.Is(SchematicSymbolInstance.Descriptor)).Unpack<SchematicSymbolInstance>();
        string Key(SchematicSymbolInstance symbol) => symbol.LibName.Length != 0 ? symbol.LibName
            : (symbol.LibraryId ?? symbol.Definition.Id).LibraryNickname + ":" + (symbol.LibraryId ?? symbol.Definition.Id).EntryName;
        string cacheKey = Key(first);
        var cache = withLinks.CachedSymbols.Single(x => x.CacheKey == cacheKey);
        cache.Definition.EmbeddedFiles ??= new();
        Assert.IsFalse(cache.Definition.EmbeddedFiles.Files.Any(x => x.Name == asset.Name));
        cache.Definition.EmbeddedFiles.Files.Add(asset.Clone());
        for (int i = 0; i < withLinks.Items.Count; ++i)
        {
            if (!withLinks.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
            var symbol = withLinks.Items[i].Unpack<SchematicSymbolInstance>();
            if (Key(symbol) != cacheKey) continue;
            symbol.Definition.EmbeddedFiles ??= new();
            symbol.Definition.EmbeddedFiles.Files.Add(asset.Clone());
            withLinks.Items[i] = Any.Pack(symbol);
        }
        await Apply(withLinks);
        await Same(withLinks, "prepared-links");
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        var lockedData = withLinks.Clone();
        int lockIndex = lockedData.Items.IndexOf(lockedData.Items.Single(item => item.Is(SchematicSymbolInstance.Descriptor)
            && item.Unpack<SchematicSymbolInstance>().Id.Value == first.Id.Value));
        var lockedSymbol = lockedData.Items[lockIndex].Unpack<SchematicSymbolInstance>();
        lockedSymbol.Locked = (LockedState)2;
        lockedData.Items[lockIndex] = Any.Pack(lockedSymbol);
        await Apply(lockedData);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        var locked = await Read();
        var lockedSave = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token);
        await BeginRemoval("assets-locked");
        for (int attempt = 0; attempt < 2; ++attempt)
        {
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromRight: 60, clickFromBottom: 25);
            await Window("Confirmation", true);
            NativeKeyboard.SchematicShortcut(display, processId, "Return", "Confirmation", false, false);
            await Window("Confirmation", false);
            await Window("Error", true);
            await Capture($"assets-locked-rejected-{attempt}");
            NativeKeyboard.SchematicShortcut(display, processId, "Return", "Error", false, false);
            await Window("Error", false);
            Assert.IsTrue(NativeKeyboard.HasWindow(display, processId, "Schematic Setup"));
            // A second nested-removal confirmation proves the form retained
            // its desired removal after the rejected transaction.
        }
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
            clickFromRight: 150, clickFromBottom: 25);
        await Window("Schematic Setup", false);
        Assert.IsTrue(locked.Equals(await Read()), "Locked rejection must preserve exact data and revision.");
        Assert.AreEqual(lockedSave, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token));
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        await WaitFor(withLinks);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        var baseline = await Read();
        var removed = withLinks.Clone();
        removed.Metadata.DrawingRatios.OverbarHeightRatio = 1.17;
        removed.Metadata.EmbeddedFiles.Files.Clear();
        removed.CachedSymbols.Single(x => x.CacheKey == cacheKey).Definition.EmbeddedFiles.Files.Clear();
        for (int i = 0; i < removed.Items.Count; ++i)
        {
            if (!removed.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
            var symbol = removed.Items[i].Unpack<SchematicSymbolInstance>();
            if (Key(symbol) != cacheKey) continue;
            symbol.Definition.EmbeddedFiles.Files.Clear();
            removed.Items[i] = Any.Pack(symbol);
        }
        foreach (bool accept in new[] { false, true })
        {
            await BeginRemoval($"assets-{accept}");
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromRight: accept ? 60 : 150, clickFromBottom: 25);
            if (accept)
            {
                await Window("Confirmation", true);
                await Capture("assets-nested-confirmation");
                NativeKeyboard.SchematicShortcut(display, processId, "Return", "Confirmation", false, false);
                await Window("Confirmation", false);
            }
            await Window("Schematic Setup", false);
            await Same(accept ? removed : baseline.Data, $"assets-{accept}-result");
            Assert.AreEqual(baseline.Revision.Sequence + (accept ? 1UL : 0UL), (await Read()).Revision.Sequence);
        }
        NativeKeyboard.SchematicShortcut(display, processId, "z"); await WaitFor(baseline.Data);
        NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false,
            clickFromLeft: 280, clickFromTop: 43);
        await WaitFor(removed);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        await Same(removed, "assets-reopened");
        await Apply(original.Data);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await Same(original.Data, "assets-original-restored");

        Task Capture(string stage) => NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-setup-" + stage + ".png"), token);
        async Task BeginRemoval(string stage)
        {
            await NativeSetupUi.Open(client, document, display, processId, token);
            await NativeSetupUi.SelectPage(display, processId, 328, token);
            await Capture(stage + "-selected");
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromLeft: 350, clickFromTop: 35);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromLeft: 330, clickFromBottom: 75);
            await Capture(stage + "-removed");
            await NativeSetupUi.SelectPage(display, processId, 34, token);
            Assert.IsFalse(NativeKeyboard.HasWindow(display, processId, "Confirmation"),
                "Page switching must not remove nested symbol files or ask to commit them.");
            NativeKeyboard.SchematicShortcut(display, processId, "a", "Schematic Setup", true,
                clickFromLeft: 500, clickFromTop: 97);
            foreach (char c in "117")
                NativeKeyboard.SchematicShortcut(display, processId, c.ToString(), "Schematic Setup", false, false);
        }
        async Task Apply(SchematicScreenData desired)
        {
            var current = await Read();
            var batch = new ApplySchematicItemBatch { Document = document, ExpectedRevision = current.Revision,
                DocumentEpoch = current.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Setup embedded-file fixture" };
            batch.Operations.Add(SchematicItemDelta.Plan(current.Data, desired));
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        }
        async Task Same(SchematicScreenData expected, string stage)
        {
            var actual = (await Read()).Data;
            if (!expected.Equals(actual))
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + $"-{stage}-expected.json"), expected.ToString(), token);
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + $"-{stage}-actual.json"), actual.ToString(), token);
            }
            Assert.IsTrue(expected.Equals(actual), stage + ": " + NativeSnapshotDifference.Describe(expected, actual));
        }
        async Task Window(string name, bool present)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(10)); int delay = 25;
            while (NativeKeyboard.HasWindow(display, processId, name) != present)
            { await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 500); }
        }
        async Task WaitFor(SchematicScreenData expected)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(8)); int delay = 25;
            while (true)
            {
                var state = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, wait.Token);
                if (state.Data.Equals(expected)) return;
                await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 500);
            }
        }
    }
}
