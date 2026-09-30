using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyInferredNetChainCreation(NativeClient client, DocumentSpecifier root,
        string rootFile, int processId, string display, string evidence, CancellationToken token)
    {
        // Reuse the real native connectivity fixture, with its embedded library.
        // The caller owns this disposable file and restores it in its finally.
        string native = await File.ReadAllTextAsync(Path.Combine(FindRoot(),
            "qa/data/eeschema/net_chains_four_nets.kicad_sch"), token);
        const string oldDeclaration = "\t(net_chain \"Signal1\"\n\t\t(uuid \"bff9f6c6-e9c4-49c9-8788-6c2b070b8b14\")\n\t\t(uuid \"bff9f6c6-e9c4-49c9-8788-6c2b070b8b15\")\n\t)\n";
        native = native.Replace("\r\n", "\n", StringComparison.Ordinal);
        StringAssert.Contains(native, oldDeclaration);
        native = native.Replace(oldDeclaration, "", StringComparison.Ordinal)
            .Replace("(path \"/899d2596-f6f5-4ac0-b36c-4a6df0ed51e2\"",
                "(path \"/" + root.SheetPath.Path[0].Value + "\"", StringComparison.Ordinal)
            .Replace("(project \"signals_four_nets\"", "(project \"" + Path.GetFileNameWithoutExtension(rootFile) + "\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(rootFile, native, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
        // Normalize the older QA file through the current native writer before
        // taking the baseline, including its reported loaded format version.
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
        var header = new ItemHeader { Document = root };
        int contextOpenCount = 0;
        int ContextX() => 640 + 24 * (contextOpenCount++ % 2);
        void Key(string key, string window = "Schematic Editor", bool control = false, bool alt = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, window, control, focusCanvas: false, altKey: alt);
        async Task Window(string title, bool visible)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (NativeKeyboard.HasWindow(display, processId, title) != visible)
                await Task.Delay(50, limit.Token);
        }
        async Task<int> Menus(int expected, bool allowFewer = false)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                int count = -1;
                NativeKeyboard.SchematicShortcut(display, processId, "", observePopupCount: value => count = value);
                if (count == expected || (allowFewer && count >= 0 && count < expected)) return count;
                await Task.Delay(50, limit.Token);
            }
        }
        async Task<SchematicHierarchyDataSnapshot> Read()
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { return await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, limit.Token); }
                catch (NativeApiException error) when (error.Status == 7) { await Task.Delay(50, limit.Token); }
            }
        }
        async Task Same(IMessage expected, IMessage actual, string stage)
        {
            if (expected.Equals(actual)) return;
            await File.WriteAllTextAsync(Path.Combine(evidence, stage + "-expected.json"), SchematicJson.Formatter.Format(expected), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, stage + "-actual.json"), SchematicJson.Formatter.Format(actual), token);
            Assert.Fail("Inferred-chain state differs; see retained " + stage + " snapshots.");
        }
        async Task<SchematicHierarchyDataSnapshot> History(string key, SchematicHierarchyDataSnapshot previous)
        {
            Key(key, control: true);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                limit.Token.ThrowIfCancellationRequested();
                var current = await Read();
                if (!current.Revision.Equals(previous.Revision)) return current;
                await Task.Delay(50, limit.Token);
            }
        }
        async Task<SchematicElectricalState> Electrical() =>
            await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = root }, token);
        string[] Partition(SchematicElectricalState state) => state.Nets.Select(n =>
            string.Join("|", n.Sheets.SelectMany(s => s.Items.Select(id => string.Join('/', s.Path.Path.Select(p => p.Value)) + ":" + id.Value)).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal).ToArray();
        var loaded = await Read();
        Assert.AreEqual(1, loaded.Data.Instances.Count);
        Assert.AreEqual(0, loaded.Data.Instances[0].Metadata.NetChains.Count);
        var symbols = loaded.Data.Instances[0].Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).ToArray();
        CollectionAssert.AreEquivalent(new[] { "TP1", "TP2", "R1", "R2", "R3" }, symbols.Select(s => s.ReferenceField.Text.Text_).ToArray());
        string[] pins = symbols.Where(s => s.ReferenceField.Text.Text_.StartsWith("TP", StringComparison.Ordinal))
            .SelectMany(s => s.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor))
                .Select(i => i.Item.Unpack<SchematicPin>().Id.Value)).ToArray();
        Assert.AreEqual(2, pins.Length);
        var electrical = await Electrical();
        Assert.AreEqual(4, electrical.Nets.Count, "The inferred path must cross three resistors and four distinct electrical nets.");
        // Reproduce the original complete-symbol mutation surface. Changing
        // only propagation and restoring all five typed instances must retain
        // the exact shared library definitions, cache keys and placed pin IDs.
        var symbolBaseline = await Read();
        async Task<SchematicHierarchyDataSnapshot> UpdateAllSymbols(bool block)
        {
            var current = await Read();
            var batch = new ApplySchematicItemBatch { Document = root, DocumentEpoch = current.Revision.Epoch,
                ExpectedRevision = current.Revision, OperationId = Guid.NewGuid().ToString("D") };
            foreach (var original in symbols)
            {
                var update = original.Clone();
                if (block && update.ReferenceField.Text.Text_ == "R2") update.Passthrough = (SchematicPassthroughMode)2;
                batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(update) });
            }
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
            return await Read();
        }
        var blockedSymbol = await UpdateAllSymbols(true);
        var expectedBlocked = symbolBaseline.Data.Clone();
        var rootItems = expectedBlocked.Instances[0].Items;
        for (int index = 0; index < rootItems.Count; ++index)
        {
            if (!rootItems[index].Is(SchematicSymbolInstance.Descriptor)) continue;
            var symbol = rootItems[index].Unpack<SchematicSymbolInstance>();
            if (symbol.ReferenceField.Text.Text_ != "R2") continue;
            symbol.Passthrough = (SchematicPassthroughMode)2; rootItems[index] = Any.Pack(symbol);
        }
        await Same(expectedBlocked, blockedSymbol.Data, "symbol-complete-update");
        CollectionAssert.AreEqual(Partition(electrical), Partition(await Electrical()));
        var restoredSymbols = await UpdateAllSymbols(false);
        await Same(symbolBaseline.Data, restoredSymbols.Data, "symbol-complete-restoration");
        NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false,
            focusCanvas: true, clickFromLeft: 1210, clickFromTop: 550);
        var undoSymbols = await History("z", restoredSymbols); await Same(expectedBlocked, undoSymbols.Data, "symbol-complete-undo");
        // Use the actual toolbar Redo here after full symbol replacement:
        // rebuilt property widgets may still own the local Ctrl+Y accelerator.
        NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false,
            focusCanvas: true, clickFromLeft: 280, clickFromTop: 43);
        SchematicHierarchyDataSnapshot redoSymbols;
        using (var redoLimit = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            redoLimit.CancelAfter(TimeSpan.FromSeconds(5));
            do
            {
                redoLimit.Token.ThrowIfCancellationRequested();
                redoSymbols = await Read();
                if (redoSymbols.Revision.Equals(undoSymbols.Revision)) await Task.Delay(50, redoLimit.Token);
            } while (redoSymbols.Revision.Equals(undoSymbols.Revision));
        }
        await Same(symbolBaseline.Data, redoSymbols.Data, "symbol-complete-redo");
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
        await Same(symbolBaseline.Data, (await Read()).Data, "symbol-complete-reloaded");
        const string title = "Create Net Chain";
        foreach (bool shortcut in new[] { false, true })
        {
            string name = shortcut ? "betweenpins" : "inferredpath";
            async Task Open()
            {
                await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
                if (shortcut)
                {
                    var select = new AddToSelection { Header = header };
                    select.Items.Add(pins.Select(p => new KIID { Value = p }));
                    var selected = await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
                    CollectionAssert.AreEquivalent(pins, selected.Items.Where(i => i.Is(SchematicPin.Descriptor))
                        .Select(i => i.Unpack<SchematicPin>().Id.Value).ToArray());
                    NativeKeyboard.SchematicShortcut(display, processId, "right-click", controlKey: false,
                        focusCanvas: true, clickFromLeft: ContextX(), clickFromTop: 450);
                    await Menus(1);
                    Key("End"); for (int i = 0; i < 4; i++) Key("Up"); Key("Right");
                    await Menus(2);
                    await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-two-pin-menu.png"), token);
                    Key("End"); Key("Return");
                }
                else { Key("t", alt: true); Key("End"); Key("Up"); Key("Up"); Key("Return"); }
                await Window(title, true);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, name + "-dialog.png"), token);
            }
            void Name()
            {
                if (!shortcut)
                    NativeKeyboard.SchematicShortcut(display, processId, "click", title, controlKey: false,
                        focusCanvas: true, clickFromLeft: 150, clickFromBottom: 70);
                Key("a", title, control: true);
                foreach (char c in name) Key(c.ToString(), title);
            }
            async Task Close(bool accept)
            {
                if (shortcut) Key(accept ? "Return" : "Escape", title);
                else
                {
                    if (accept)
                        NativeKeyboard.SchematicShortcut(display, processId, "click", title, controlKey: false,
                            focusCanvas: true, clickFromRight: 60, clickFromBottom: 25);
                    NativeKeyboard.SchematicShortcut(display, processId, "click", title, controlKey: false,
                        focusCanvas: true, clickFromRight: 150, clickFromBottom: 25);
                }
                await Window(title, false);
            }
            var before = await Read();
            await Open(); Name(); await Close(false);
            await Same(before, await Read(), name + "-cancel");
            await Open(); Name(); await Close(true);
            var created = await Read();
            var chain = created.Data.Instances.Single().Metadata.NetChains.Single(c => c.Name == name);
            Assert.IsTrue(chain.Committed);
            Assert.AreEqual(4, chain.MemberNets.Count);
            CollectionAssert.AreEquivalent(new[] { "TP1", "TP2" }, new[] { chain.From.Reference, chain.To.Reference });
            Assert.AreEqual("1", chain.From.Pin); Assert.AreEqual("1", chain.To.Pin);
            CollectionAssert.AreEqual(Partition(electrical), Partition(await Electrical()), "A net chain must not merge or rewire its member nets.");
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = root, DocumentEpoch = before.Revision.Epoch, AfterSequence = before.Revision.Sequence }, token);
            Assert.AreEqual(1, journal.Changes.Count);
            Assert.AreEqual("Create Net Chain", journal.Changes.Single().Description);
            var stale = new ApplySchematicItemBatch { Document = root, DocumentEpoch = before.Revision.Epoch,
                ExpectedRevision = before.Revision, OperationId = Guid.NewGuid().ToString("D") };
            stale.Operations.Add(new SchematicItemOperation { ReplaceNetChains = new() });
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
            await Same(created, await Read(), name + "-stale");
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            StringAssert.Contains(await File.ReadAllTextAsync(rootFile, token), "(net_chain \"" + name + "\"");
            var undone = await History("z", created); await Same(before.Data, undone.Data, name + "-undo");
            var redone = await History("y", undone); await Same(created.Data, redone.Data, name + "-redo");
            if (!shortcut) await VerifyRemoval();
            // Save/reload verifies the committed declaration rebuilds the real
            // chain; then remove it through the existing revision-safe delta.
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
            var reopened = await Read(); await Same(created.Data, reopened.Data, name + "-reload");
            var remove = new ApplySchematicItemBatch { Document = root, DocumentEpoch = reopened.Revision.Epoch,
                ExpectedRevision = reopened.Revision, OperationId = Guid.NewGuid().ToString("D") };
            remove.Operations.Add(new SchematicItemOperation { ReplaceNetChains = new() });
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(remove, token);
            await Same(before.Data, (await Read()).Data, name + "-restored");
        }

        async Task VerifyRemoval()
        {
            var middle = symbols.Single(s => s.ReferenceField.Text.Text_ == "R2").Definition.Items
                .Where(i => i.Item.Is(SchematicPin.Descriptor)).Select(i => i.Item.Unpack<SchematicPin>()).Single(p => p.Number == "1");
            foreach (var (caseName, selectedPins, bridgeCount) in new[]
                { ("one-endpoint", pins.Take(1).ToArray(), 1), ("two-endpoints", pins, 2), ("interior", new[] { middle.Id.Value }, 2) })
            {
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
                byte[] originalBytes = await File.ReadAllBytesAsync(rootFile, token);
                var before = await Read();
                string stage = "chain-remove-" + caseName;
                async Task OpenRemoval(bool accept)
                {
                    await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
                    var select = new AddToSelection { Header = header };
                    select.Items.Add(selectedPins.Select(p => new KIID { Value = p }));
                    var selected = await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
                    CollectionAssert.AreEquivalent(selectedPins, selected.Items
                        .Where(i => i.Is(SchematicPin.Descriptor)).Select(i => i.Unpack<SchematicPin>().Id.Value).ToArray());
                    NativeKeyboard.SchematicShortcut(display, processId, "right-click", controlKey: false,
                        focusCanvas: true, clickFromLeft: ContextX(), clickFromTop: 450);
                    await Menus(1);
                    Key("End"); for (int i = 0; i < 4; i++) Key("Up"); Key("Right");
                    await Menus(2);
                    await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, stage + "-menu.png"), token);
                    if (accept) { Key("Home"); Key("Down"); Key("Return"); await Menus(0); }
                    else
                    {
                        // GTK may dismiss either the submenu or the whole
                        // popup stack. Both are valid Cancel behavior; wait
                        // for the observed transition before sending more input.
                        Key("Escape");
                        if (await Menus(1, allowFewer: true) == 1)
                        { Key("Escape"); await Menus(0); }
                    }
                }
                await OpenRemoval(false); await Same(before, await Read(), stage + "-cancel");
                await OpenRemoval(true);
                var removed = await Read();
                Assert.IsTrue(removed.Revision.Sequence > before.Revision.Sequence);
                var originalChain = before.Data.Instances.Single().Metadata.NetChains.Single(c => c.Committed);
                var removedChain = removed.Data.Instances.Single().Metadata.NetChains.Single(c => c.Name == originalChain.Name);
                var selectedNets = electrical.Nets.Where(n => n.Sheets.SelectMany(s => s.Items)
                    .Any(id => selectedPins.Contains(id.Value))).Select(n => n.Name).ToArray();
                Assert.IsFalse(removedChain.Committed, "Removing an endpoint or breaking its path retains unresolved intent without guessing new endpoints.");
                CollectionAssert.AreEquivalent(selectedNets, removedChain.Exclusions.NetNames.ToArray());
                var allPins = symbols.SelectMany(s => s.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor))
                    .Select(i => i.Item.Unpack<SchematicPin>().Id.Value)).ToHashSet(StringComparer.Ordinal);
                var expectedAnchors = electrical.Nets.Where(n => selectedNets.Contains(n.Name))
                    .SelectMany(n => n.Sheets.SelectMany(s => s.Items.Where(pin => allPins.Contains(pin.Value))
                        .Select(pin => string.Join('/', s.Path.Path.Select(id => id.Value).Append(pin.Value)))))
                    .Order(StringComparer.Ordinal).ToArray();
                var actualAnchors = removedChain.Exclusions.Pins.Select(p => string.Join('/', p.Path.Path.Select(id => id.Value).Append(p.Pin.Value)))
                    .Order(StringComparer.Ordinal).ToArray();
                CollectionAssert.AreEqual(expectedAnchors, actualAnchors, "Each removed electrical pin retains its exact sheet ownership.");
                CollectionAssert.AreEquivalent(originalChain.MemberNets.Except(selectedNets).ToArray(), removedChain.MemberNets.ToArray());
                Assert.AreEqual(originalChain.From, removedChain.From); Assert.AreEqual(originalChain.To, removedChain.To);
                var originalSymbols = before.Data.Instances.Single().Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                    .Select(i => i.Unpack<SchematicSymbolInstance>()).ToDictionary(s => s.Id.Value);
                int changed = 0;
                foreach (var item in removed.Data.Instances.Single().Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)))
                {
                    var symbol = item.Unpack<SchematicSymbolInstance>();
                    var original = originalSymbols[symbol.Id.Value];
                    if (symbol.Passthrough != original.Passthrough)
                    {
                        Assert.AreEqual((SchematicPassthroughMode)2, symbol.Passthrough);
                        StringAssert.StartsWith(symbol.ReferenceField.Text.Text_, "R");
                        changed++;
                        symbol.Passthrough = original.Passthrough;
                    }
                    await Same(original, symbol, stage + "-unaffected-symbol");
                }
                Assert.AreEqual(bridgeCount, changed, "Only bridges adjacent to the explicitly selected electrical owner may change.");
                CollectionAssert.AreEqual(Partition(electrical), Partition(await Electrical()));
                var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                    { Document = root, DocumentEpoch = before.Revision.Epoch, AfterSequence = before.Revision.Sequence }, token);
                Assert.AreEqual(1, journal.Changes.Count);
                Assert.AreEqual("Remove from Net Chain", journal.Changes.Single().Description);
                var retained = new SchematicNetChainState();
                foreach (var declaration in removed.Data.Instances.Single().Metadata.NetChains)
                { var copy = declaration.Clone(); copy.Committed = false; retained.Definitions.Add(copy); }
                var repeat = new ApplySchematicItemBatch { Document = root, DocumentEpoch = removed.Revision.Epoch,
                    ExpectedRevision = removed.Revision, OperationId = Guid.NewGuid().ToString("D") };
                repeat.Operations.Add(new SchematicItemOperation { ReplaceNetChains = retained });
                Assert.IsFalse((await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(repeat, token)).NetChainsChanged);
                await Same(removed, await Read(), stage + "-noop");
                var legacy = repeat.Clone(); legacy.OperationId = Guid.NewGuid().ToString("D");
                foreach (var declaration in legacy.Operations.Single().ReplaceNetChains.Definitions) declaration.Exclusions = null;
                await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(legacy, token));
                await Same(removed, await Read(), stage + "-legacy-rejected");
                var stale = new ApplySchematicItemBatch { Document = root, DocumentEpoch = before.Revision.Epoch,
                    ExpectedRevision = before.Revision, OperationId = Guid.NewGuid().ToString("D") };
                stale.Operations.Add(new SchematicItemOperation { ReplaceNetChains = new() });
                await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
                await Same(removed, await Read(), stage + "-stale");
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
                Assert.AreEqual(bridgeCount, (await File.ReadAllTextAsync(rootFile, token)).Split("(passthrough block)", StringSplitOptions.None).Length - 1);
                var undone = await History("z", removed); await Same(before.Data, undone.Data, stage + "-undo");
                var redone = await History("y", undone); await Same(removed.Data, redone.Data, stage + "-redo");
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
                StringAssert.Contains(await File.ReadAllTextAsync(rootFile, token), "(excluded_nets");
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
                var reloaded = await Read(); await Same(removed.Data, reloaded.Data, stage + "-reload");
                Assert.AreEqual(removed.Data, SchematicDataXml.Read(SchematicDataXml.Write(reloaded.Data)));
                // Reload resets native Undo. Fixture restoration is distinct
                // from the verified Undo/Redo and removal behavior above: use
                // this fixture's own saved bytes, not unrelated symbol updates.
                await File.WriteAllBytesAsync(rootFile, originalBytes, token);
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
                await Same(before.Data, (await Read()).Data, stage + "-restored");
            }
        }
    }
}
