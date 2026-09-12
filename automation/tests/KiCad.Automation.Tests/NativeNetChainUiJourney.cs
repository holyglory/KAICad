using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNetChainNameDialog(NativeClient client, DocumentSpecifier root,
        string rootFile, string pinId, int processId, string display, string evidence, CancellationToken token)
    {
        const string title = "Name Net Chain", oldName = "AUTOMATION_PATH", newName = "renamedpath";
        var header = new ItemHeader { Document = root };
        bool capturedMenuStack = false;
        void Key(string key, string window = "Schematic Editor", bool control = false, bool alt = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, window, control, focusCanvas: false, altKey: alt);
        async Task Window(string window, bool visible)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (NativeKeyboard.HasWindow(display, processId, window) != visible)
                    await Task.Delay(50, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-dialog-failed.png"), CancellationToken.None);
                throw;
            }
        }
        async Task<SchematicHierarchyDataSnapshot> Read(CancellationToken cancellation)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { return await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, limit.Token); }
                catch (NativeApiException error) when (error.Status == 7) { await Task.Delay(50, limit.Token); }
            }
        }
        async Task Same(IMessage expected, IMessage actual, string name)
        {
            bool same = expected.Equals(actual);
            if (!same)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, name + "-expected.json"), SchematicJson.Formatter.Format(expected), token);
                await File.WriteAllTextAsync(Path.Combine(evidence, name + "-actual.json"), SchematicJson.Formatter.Format(actual), token);
            }
            Assert.IsTrue(same, "Net-chain state differs; see retained " + name + " snapshots.");
        }
        async Task OpenChainMenu()
        {
            await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
            var select = new AddToSelection { Header = header }; select.Items.Add(new KIID { Value = pinId });
            var selection = await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
            Assert.IsTrue(selection.Items.Any(i => i.Is(SchematicPin.Descriptor) && i.Unpack<SchematicPin>().Id.Value == pinId),
                "The real native context menu requires the exact placed pin selection.");
            // Open the actual canvas context menu without a left click that
            // would clear the explicit pin selection. The keyboard Menu key
            // is not handled by this GAL canvas on GTK.
            NativeKeyboard.SchematicShortcut(display, processId, "right-click", controlKey: false,
                focusCanvas: true, clickFromLeft: 640, clickFromTop: 450);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-context-menu.png"), token);
            // The rendered menu ends with Select All, Unselect All, Zoom and
            // Grid. Net Chain precedes those four entries; Name precedes Create.
            Key("End"); for (int i = 0; i < 4; i++) Key("Up"); Key("Right");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-actions-menu.png"), token);
            var windows = new List<string>();
            NativeKeyboard.SchematicShortcut(display, processId, "", describe: windows.Add);
            await File.WriteAllLinesAsync(Path.Combine(evidence, "chain-menu-windows.txt"), windows, token);
            bool assertion = windows.Any(w => w.Contains("map=2", StringComparison.Ordinal)
                && (w.Contains("assert", StringComparison.OrdinalIgnoreCase) || w.Contains("Debug Alert", StringComparison.OrdinalIgnoreCase)));
            if (assertion && !capturedMenuStack)
            {
                // Read-only diagnosis of this fixture-owned native process.
                // Keep the stack cold; do not continue through a native assertion.
                var start = new ProcessStartInfo("gdb") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "--batch", "--nx", "--quiet", "-p", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-ex", "set pagination off", "-ex", "thread apply all bt 40", "-ex", "detach" }) start.ArgumentList.Add(arg);
                using var debugger = Process.Start(start)!;
                Task stdout = Capture(debugger.StandardOutput, Path.Combine(evidence, "chain-menu-stack.stdout.log"));
                Task stderr = Capture(debugger.StandardError, Path.Combine(evidence, "chain-menu-stack.stderr.log"));
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(20));
                try { await debugger.WaitForExitAsync(limit.Token); }
                finally { if (!debugger.HasExited) { debugger.Kill(); await debugger.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); }
                capturedMenuStack = true;
            }
            if (assertion)
                throw new InvalidOperationException("Native assertion while opening the net-chain menu; see retained window/stack evidence.");
        }
        async Task Open()
        {
            await OpenChainMenu();
            Key("End"); Key("Up"); Key("Return");
            await Window(title, true);
        }
        void Text(string value)
        {
            Key("a", title, control: true); Key("BackSpace", title);
            foreach (char c in value) Key(c.ToString(), title);
        }
        async Task Saved(bool renamed)
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            using var project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token));
            var classes = project.RootElement.GetProperty("net_settings").GetProperty("net_chain_classes");
            Assert.AreEqual("fastbus", classes.GetProperty(renamed ? newName : oldName).GetString());
            Assert.IsFalse(classes.TryGetProperty(renamed ? oldName : newName, out _));
            Assert.AreEqual("preserved", classes.GetProperty("UNAFFECTED_CHAIN").GetString());
            string contents = await File.ReadAllTextAsync(rootFile, token);
            StringAssert.Contains(contents, "(net_chain \"" + (renamed ? newName : oldName) + "\"");
        }
        async Task<SchematicHierarchyDataSnapshot> History(string key, SchematicHierarchyDataSnapshot prior)
        {
            Key(key, control: true);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var value = await Read(limit.Token);
                if (!value.Revision.Equals(prior.Revision)) return value;
                await Task.Delay(50, limit.Token);
            }
        }

        await Saved(false); var before = await Read(token);
        await Open(); Key("Escape", title); await Window(title, false); await Same(before, await Read(token), "chain-cancel");
        await Open(); Key("Return", title); await Window(title, false); await Same(before, await Read(token), "chain-noop");
        foreach (string invalid in new[] { "", "UNRESOLVED_PATH" })
        {
            await Open(); Text(invalid); Key("Return", title); await Window(title, false);
            if (invalid.Length != 0) { await Window("Error", true); Key("Return", "Error"); await Window("Error", false); }
            await Same(before, await Read(token), "chain-rejected"); await Saved(false);
        }
        await Open(); Text(newName);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-name-edited.png"), token);
        Key("Return", title); await Window(title, false);
        var changed = await Read(token); Assert.IsTrue(changed.Revision.Sequence > before.Revision.Sequence);
        foreach (var sheet in changed.Data.Instances)
        {
            Assert.IsFalse(sheet.Metadata.NetChains.Any(c => c.Name == oldName));
            Assert.IsTrue(sheet.Metadata.NetChains.Single(c => c.Name == newName).Committed);
        }
        var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            { Document = root, DocumentEpoch = before.Revision.Epoch, AfterSequence = before.Revision.Sequence }, token);
        Assert.AreEqual(1, journal.Changes.Count); Assert.AreEqual("Name Net Chain", journal.Changes.Single().Description);
        await Saved(true);
        var stale = new ApplySchematicItemBatch { Document = root, DocumentEpoch = before.Revision.Epoch,
            ExpectedRevision = before.Revision, OperationId = Guid.NewGuid().ToString("D") };
        var definitions = new SchematicNetChainState();
        foreach (var chain in before.Data.Instances[0].Metadata.NetChains)
        { var copy = chain.Clone(); copy.Committed = false; definitions.Definitions.Add(copy); }
        stale.Operations.Add(new SchematicItemOperation { ReplaceNetChains = definitions });
        var error = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
        StringAssert.Contains(error.Message.ToLowerInvariant(), "revision");
        await Same(changed, await Read(token), "chain-stale");
        var undone = await History("z", changed); await Same(before.Data, undone.Data, "chain-undo"); await Saved(false);
        var redone = await History("y", undone); await Same(changed.Data, redone.Data, "chain-redo"); await Saved(true);
        undone = await History("z", redone); await Same(before.Data, undone.Data, "chain-restored"); await Saved(false);

        // Replace terminal B with the exact currently selected pin A. The
        // native menu explicitly offers B here; A is the disabled no-op entry.
        var terminalBefore = await Read(token);
        var electricalBefore = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = root }, token);
        await OpenChainMenu(); Key("End"); Key("Up"); Key("Up"); Key("Right");
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-terminal-menu.png"), token);
        Key("Escape"); Key("Escape"); Key("Escape");
        await Same(terminalBefore, await Read(token), "chain-terminal-cancel");
        await OpenChainMenu(); Key("End"); Key("Up"); Key("Up"); Key("Right"); Key("End"); Key("Return");
        var terminalAfter = await Read(token);
        Assert.IsTrue(terminalAfter.Revision.Sequence > terminalBefore.Revision.Sequence);
        foreach (var screen in terminalAfter.Data.Instances)
        {
            var chain = screen.Metadata.NetChains.Single(c => c.Name == oldName);
            Assert.AreEqual(chain.From.Reference, chain.To.Reference);
            Assert.AreEqual(chain.From.Pin, chain.To.Pin);
        }
        var electricalAfter = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = root }, token);
        string[] Partition(SchematicElectricalState state) => state.Nets.Select(n =>
            string.Join("|", n.Sheets.SelectMany(s => s.Items.Select(id => string.Join('/', s.Path.Path.Select(p => p.Value)) + ":" + id.Value)).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(Partition(electricalBefore), Partition(electricalAfter), "Changing a chain endpoint must not rewire the circuit.");
        journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            { Document = root, DocumentEpoch = terminalBefore.Revision.Epoch, AfterSequence = terminalBefore.Revision.Sequence }, token);
        Assert.AreEqual(1, journal.Changes.Count);
        Assert.AreEqual("Replace terminal pin", journal.Changes.Single().Description);
        await Saved(false);
        var terminalUndo = await History("z", terminalAfter); await Same(terminalBefore.Data, terminalUndo.Data, "chain-terminal-undo");
        var terminalRedo = await History("y", terminalUndo); await Same(terminalAfter.Data, terminalRedo.Data, "chain-terminal-redo");
        terminalUndo = await History("z", terminalRedo); await Same(terminalBefore.Data, terminalUndo.Data, "chain-terminal-restore");
        await Saved(false);
        await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);

        // Schematic Setup remains a real native dialog. Exercise the lazy
        // net-chain page without manufacturing edits on Cancel or unchanged OK.
        var setupBaseline = await Read(token);
        async Task StableSetupGeometry(string window = "Schematic Setup")
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            (int X, int Y, int Width, int Height)? previous = null;
            var quiet = Stopwatch.StartNew();
            while (true)
            {
                (int X, int Y, int Width, int Height) current = default;
                NativeKeyboard.SchematicShortcut(display, processId, "", window,
                    observeGeometry: value => current = value);
                if (previous != current) { previous = current; quiet.Restart(); }
                else if (quiet.Elapsed >= TimeSpan.FromMilliseconds(200)) return;
                await Task.Delay(50, limit.Token);
            }
        }
        async Task OpenSetupPage()
        {
            Key("f", alt: true); Key("End");
            for (int i = 0; i < 4; i++) Key("Up");
            Key("Return"); await Window("Schematic Setup", true);
            await StableSetupGeometry();
            // Measured native tree row. Lazy page resolution can resize the
            // dialog; wait for stable geometry before targeting its buttons.
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                focusCanvas: true, clickFromLeft: 80, clickFromTop: 264);
            await StableSetupGeometry();
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-setup-page.png"), token);
        }
        async Task FinishSetup(bool accept)
        {
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                focusCanvas: true, clickFromRight: accept ? 60 : 150, clickFromBottom: 25);
            await Window("Schematic Setup", false);
        }
        foreach (bool accept in new[] { false, true, true })
        {
            await OpenSetupPage(); await FinishSetup(accept);
            await Same(setupBaseline, await Read(token), accept ? "chain-setup-noop" : "chain-setup-cancel");
            await Saved(false);
        }
        // A class imported while this page already exists must become a real
        // dropdown choice without losing the pending chain name. Typing an
        // arbitrary override would not prove that the shared choices refreshed.
        const string draftClass = "zzzzDraftForSetup", draftChain = "draftpreserved";
        string projectPath = Path.ChangeExtension(rootFile, ".kicad_pro");
        var projectBeforeDraft = JsonNode.Parse(await File.ReadAllTextAsync(projectPath, token))!;
        var importedProject = projectBeforeDraft.DeepClone();
        var classList = importedProject["net_settings"]!["classes"]!.AsArray();
        Assert.IsTrue(classList.All(c => string.CompareOrdinal(c!["name"]!.GetValue<string>(), draftClass) < 0));
        var importedClass = classList.Single(c => c!["name"]!.GetValue<string>() == "Default")!.DeepClone();
        importedClass["name"] = draftClass;
        classList.Add(importedClass);
        string importDirectory = Directory.CreateTempSubdirectory("chain-draft-import-").FullName;
        string importProjectPath = Path.Combine(importDirectory, "draft.kicad_pro");
        await File.WriteAllTextAsync(importProjectPath, importedProject.ToJsonString(), token);
        try
        {
            foreach (bool accept in new[] { false, true })
            {
                var draftBefore = await Read(token);
                await OpenSetupPage();
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                    clickFromLeft: 350, clickFromTop: 107);
                Key("F2", "Schematic Setup"); Key("a", "Schematic Setup", control: true);
                foreach (char c in draftChain) Key(c.ToString(), "Schematic Setup");
                Key("Tab", "Schematic Setup");
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                    clickFromLeft: 400, clickFromBottom: 25);
                await Window("Import Settings", true);
                await NativeSetupUi.StableGeometry(display, processId, token, "Import Settings");
                NativeKeyboard.SchematicShortcut(display, processId, "a", "Import Settings", true,
                    clickFromLeft: 150, clickFromTop: 23);
                foreach (char c in importProjectPath) Key(c.ToString(), "Import Settings");
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Import Settings", false,
                    clickFromLeft: 65, clickFromBottom: 25);
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Import Settings", false,
                    clickFromRight: 65, clickFromBottom: 25);
                await Window("Import Settings", false);
                await NativeSetupUi.SelectPage(display, processId, 34, token);
                await NativeSetupUi.SelectPage(display, processId, 264, token);
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                    clickFromLeft: 850, clickFromTop: 107);
                Key("F2", "Schematic Setup"); Key("Down", "Schematic Setup", alt: true);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"chain-draft-choices-{accept}.png"), token);
                Key("End", "Schematic Setup"); Key("Return", "Schematic Setup"); Key("Tab", "Schematic Setup");
                await FinishSetup(accept);
                var actual = await Read(token);
                if (!accept)
                {
                    await Same(draftBefore, actual, "chain-draft-cancel");
                    await Saved(false);
                    continue;
                }
                var expected = draftBefore.Data.Clone();
                foreach (var screen in expected.Instances)
                {
                    var chain = screen.Metadata.NetChains.Single(c => c.Name == oldName);
                    chain.Name = draftChain; chain.NetClass = draftClass;
                    // The native snapshot emits the name-keyed chain map in
                    // name order. Renaming moves this entry within that map.
                    var orderedChains = screen.Metadata.NetChains.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
                    screen.Metadata.NetChains.Clear();
                    screen.Metadata.NetChains.Add(orderedChains);
                    if (screen.Metadata.NetChainClasses.Assignments.TryGetValue(oldName, out var assignment))
                    {
                        screen.Metadata.NetChainClasses.Assignments.Remove(oldName);
                        screen.Metadata.NetChainClasses.Assignments[draftChain] = assignment;
                    }
                }
                await Same(expected, actual.Data, "chain-draft-applied");
                Assert.AreEqual(draftBefore.Revision.Sequence + 1, actual.Revision.Sequence);
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
                var savedProject = JsonNode.Parse(await File.ReadAllTextAsync(projectPath, token))!;
                Assert.IsTrue(savedProject["net_settings"]!["classes"]!.AsArray().Any(c => c!["name"]!.GetValue<string>() == draftClass));
                var draftUndone = await History("z", actual);
                await Same(draftBefore.Data, draftUndone.Data, "chain-draft-undo");
                var draftRedone = await History("y", draftUndone);
                await Same(actual.Data, draftRedone.Data, "chain-draft-redo");
                await Same(draftBefore.Data, (await History("z", draftRedone)).Data, "chain-draft-restored");
                await Saved(false);
                var restoredProject = JsonNode.Parse(await File.ReadAllTextAsync(projectPath, token))!;
                Assert.IsTrue(JsonNode.DeepEquals(projectBeforeDraft["net_settings"], restoredProject["net_settings"]),
                    "One native Undo must restore the class registry and chain assignments together.");
            }
        }
        finally { Directory.Delete(importDirectory, true); }
        setupBaseline = await Read(token);
        foreach (bool accept in new[] { false, true })
        {
            await OpenSetupPage();
            if (accept)
            {
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                    focusCanvas: true, clickFromLeft: 350, clickFromTop: 107);
                Key("F2", "Schematic Setup"); Key("a", "Schematic Setup", control: true);
                foreach (char c in "UNRESOLVED_PATH") Key(c.ToString(), "Schematic Setup");
                Key("Tab", "Schematic Setup");
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                    focusCanvas: true, clickFromRight: 60, clickFromBottom: 25);
                await Window("Net Chains", true); Key("Return", "Net Chains"); await Window("Net Chains", false);
                Assert.IsTrue(NativeKeyboard.HasWindow(display, processId, "Schematic Setup"),
                    "A reserved name must leave the editable form open without overwriting the unresolved declaration.");
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-setup-rejected-name.png"), token);
            }
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                focusCanvas: true, clickFromLeft: 350, clickFromTop: 107);
            Key("F2", "Schematic Setup"); Key("a", "Schematic Setup", control: true);
            foreach (char c in "setupchain") Key(c.ToString(), "Schematic Setup");
            Key("Tab", "Schematic Setup");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-setup-renamed.png"), token);
            await FinishSetup(accept);
            var actual = await Read(token);
            if (!accept) { await Same(setupBaseline, actual, "chain-setup-cancel-edit"); continue; }
            Assert.IsTrue(actual.Revision.Sequence > setupBaseline.Revision.Sequence);
            foreach (var screen in actual.Data.Instances)
            {
                Assert.IsTrue(screen.Metadata.NetChains.Any(c => c.Name == "setupchain"));
                Assert.IsFalse(screen.Metadata.NetChains.Any(c => c.Name == oldName));
            }
            journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = root, DocumentEpoch = setupBaseline.Revision.Epoch, AfterSequence = setupBaseline.Revision.Sequence }, token);
            Assert.AreEqual(1, journal.Changes.Count);
            Assert.AreEqual("Edit Schematic Setup", journal.Changes.Single().Description);
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            using (var project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token)))
            {
                var classes = project.RootElement.GetProperty("net_settings").GetProperty("net_chain_classes");
                Assert.AreEqual("fastbus", classes.GetProperty("setupchain").GetString());
                Assert.IsFalse(classes.TryGetProperty(oldName, out _));
                Assert.AreEqual("preserved", classes.GetProperty("UNAFFECTED_CHAIN").GetString());
            }
            var reverted = await History("z", actual); await Same(setupBaseline.Data, reverted.Data, "chain-setup-undo");
            var reapplied = await History("y", reverted); await Same(actual.Data, reapplied.Data, "chain-setup-redo");
            reverted = await History("z", reapplied); await Same(setupBaseline.Data, reverted.Data, "chain-setup-restored");
            await Saved(false);
        }
        foreach (string operation in new[] { "cancel", "noop", "edit", "clear" })
        foreach (bool accept in new[] { false, true })
        {
            const string picker = "Color Picker";
            var previous = await Read(token);
            await OpenSetupPage();
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                focusCanvas: true, clickFromLeft: 950, clickFromTop: 107);
            Key("F2", "Schematic Setup"); await Window(picker, true); await StableSetupGeometry(picker);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-color-picker.png"), token);
            if (operation is "edit" or "cancel")
            {
                NativeKeyboard.SchematicShortcut(display, processId, "click", picker, controlKey: false,
                    focusCanvas: true, clickFromLeft: 300, clickFromBottom: 25);
                Key("a", picker, control: true);
                foreach (char c in "#CC884480") Key(c.ToString(), picker);
            }
            else if (operation == "clear")
                NativeKeyboard.SchematicShortcut(display, processId, "click", picker, controlKey: false,
                    focusCanvas: true, clickFromLeft: 450, clickFromBottom: 25);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-color-" + operation + "-edited.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", picker, controlKey: false,
                focusCanvas: true, clickFromRight: operation == "cancel" ? 150 : 60, clickFromBottom: 25);
            await Window(picker, false);
            await FinishSetup(accept);
            var actual = await Read(token);
            if (!accept || operation is "cancel" or "noop")
            {
                await Same(previous, actual, "chain-color-" + operation + "-unchanged");
                await Saved(false); continue;
            }
            var expected = previous.Data.Clone();
            foreach (var screen in expected.Instances)
                screen.Metadata.NetChains.Single(c => c.Name == oldName).Color = operation == "clear" ? null
                    : new() { R = 204.0 / 255, G = 136.0 / 255, B = 68.0 / 255, A = 128.0 / 255 };
            await Same(expected, actual.Data, "chain-color-" + operation);
            journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = root, DocumentEpoch = previous.Revision.Epoch, AfterSequence = previous.Revision.Sequence }, token);
            Assert.AreEqual(1, journal.Changes.Count);
            Assert.AreEqual("Edit Schematic Setup", journal.Changes.Single().Description);
            await Saved(false);
            var undoneColor = await History("z", actual); await Same(previous.Data, undoneColor.Data, "chain-color-undo");
            var redoneColor = await History("y", undoneColor); await Same(expected, redoneColor.Data, "chain-color-redo");
            await Saved(false);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
            var reopenedColor = await Read(token); await Same(expected, reopenedColor.Data, "chain-color-reloaded");
            var restoreColor = new ApplySchematicItemBatch { Document = root, ExpectedRevision = reopenedColor.Revision,
                DocumentEpoch = reopenedColor.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
            var originalChains = new SchematicNetChainState();
            foreach (var chain in previous.Data.Instances[0].Metadata.NetChains)
            {
                var definition = chain.Clone(); definition.Committed = false; originalChains.Definitions.Add(definition);
            }
            restoreColor.Operations.Add(new SchematicItemOperation { ReplaceNetChains = originalChains });
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(restoreColor, token);
            await Same(previous.Data, (await Read(token)).Data, "chain-color-restored");
            await Saved(false);
        }
        foreach (string operation in new[] { "class", "netclass", "delete" })
        foreach (bool accept in new[] { false, true })
        {
            var previous = await Read(token);
            await OpenSetupPage();
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                focusCanvas: true, clickFromLeft: operation == "class" ? 720 : operation == "netclass" ? 850 : 350, clickFromTop: 107);
            if (operation is "class" or "netclass")
            {
                Key("F2", "Schematic Setup"); Key("a", "Schematic Setup", control: true);
                foreach (char c in operation == "class" ? "changedclass" : "RoutingOverride") Key(c.ToString(), "Schematic Setup");
                Key("Tab", "Schematic Setup");
            }
            else
            {
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                    focusCanvas: true, clickFromLeft: 294, clickFromBottom: 66);
                await Window("Delete Net Chain", true);
                Key("Return", "Delete Net Chain"); await Window("Delete Net Chain", false);
            }
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-setup-" + operation + ".png"), token);
            await FinishSetup(accept);
            var actual = await Read(token);
            if (!accept) { await Same(previous, actual, "chain-setup-cancel-" + operation); await Saved(false); continue; }
            Assert.IsTrue(actual.Revision.Sequence > previous.Revision.Sequence);
            journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = root, DocumentEpoch = previous.Revision.Epoch, AfterSequence = previous.Revision.Sequence }, token);
            Assert.AreEqual(1, journal.Changes.Count);
            Assert.AreEqual("Edit Schematic Setup", journal.Changes.Single().Description);
            foreach (var screen in actual.Data.Instances)
            {
                Assert.AreEqual(operation != "delete", screen.Metadata.NetChains.Any(c => c.Name == oldName));
                if (operation == "netclass")
                    Assert.AreEqual("RoutingOverride", screen.Metadata.NetChains.Single(c => c.Name == oldName).NetClass);
                Assert.AreEqual("UnknownClass", screen.Metadata.NetChains.Single(c => c.Name == "UNRESOLVED_PATH").NetClass);
            }
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            using (var project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token)))
            {
                var classes = project.RootElement.GetProperty("net_settings").GetProperty("net_chain_classes");
                Assert.AreEqual("preserved", classes.GetProperty("UNAFFECTED_CHAIN").GetString());
                if (operation == "class") Assert.AreEqual("changedclass", classes.GetProperty(oldName).GetString());
                else if (operation == "netclass") Assert.AreEqual("fastbus", classes.GetProperty(oldName).GetString());
                else Assert.IsFalse(classes.TryGetProperty(oldName, out _));
            }
            if (operation == "delete")
            {
                await VerifyCreation(actual);
                actual = await Read(token); // creation and its Undo advance the journal
            }
            var reverted = await History("z", actual); await Same(previous.Data, reverted.Data, "chain-setup-undo-" + operation); await Saved(false);
            var reapplied = await History("y", reverted); await Same(actual.Data, reapplied.Data, "chain-setup-redo-" + operation);
            reverted = await History("z", reapplied); await Same(previous.Data, reverted.Data, "chain-setup-restore-" + operation); await Saved(false);
        }

        await VerifyClassRegistry();

        async Task VerifyClassRegistry()
        {
            await VerifySnapshotSchemaVersions(client, root, token);
            var registryBaseline = await Read(token);
            foreach (var sheet in registryBaseline.Data.Instances)
            {
                Assert.IsNotNull(sheet.Metadata.NetChainClasses);
                CollectionAssert.Contains(sheet.Metadata.NetChainClasses.Definitions.ToArray(), "emptygroup");
                Assert.IsFalse(sheet.Metadata.NetChainClasses.Assignments.Values.Contains("emptygroup"));
            }
            foreach (string invalid in new[] { "", "fastbus" })
            {
                await OpenSetupPage();
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                    focusCanvas: true, clickFromLeft: 440, clickFromTop: 24);
                await StableSetupGeometry();
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                    focusCanvas: true, clickFromLeft: 300, clickFromBottom: 65);
                await Window("Add Class", true);
                nuint entry = 0;
                NativeKeyboard.SchematicShortcut(display, processId, "", "Add Class", observeWindow: value => entry = value);
                Key("a", "Add Class", control: true); Key("BackSpace", "Add Class");
                foreach (char c in invalid) Key(c.ToString(), "Add Class");
                Key("Return", "Add Class");
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(5));
                    while (!NativeKeyboard.HasWindow(display, processId, "Add Class", excludeWindow: entry))
                        await Task.Delay(50, limit.Token);
                    NativeKeyboard.SchematicShortcut(display, processId, "Return", "Add Class", controlKey: false,
                        focusCanvas: false, excludeWindow: entry);
                }
                await Window("Add Class", false); await FinishSetup(true);
                await Same(registryBaseline, await Read(token), "chain-class-rejected");
            }
            foreach (var (action, assigned) in new[]
                { ("add", false), ("rename", false), ("delete", false), ("rename", true), ("delete", true) })
            foreach (bool accept in new[] { false, true })
            {
                string targetClass = assigned ? "fastbus" : "emptygroup";
                string evidenceKey = "chain-class-" + action + (assigned ? "-assigned" : "-unused");
                var previous = await Read(token);
                string dialog = action == "add" ? "Add Class" : action == "rename" ? "Rename Class" : "Delete Class";
                async Task OpenClassEditor()
                {
                    await OpenSetupPage();
                    NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                        focusCanvas: true, clickFromLeft: 440, clickFromTop: 24);
                    await StableSetupGeometry();
                    if (action != "add")
                    {
                        NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                            focusCanvas: true, clickFromLeft: 400, clickFromTop: assigned ? 142 : 112);
                    }
                    NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                        focusCanvas: true, clickFromLeft: action == "add" ? 300 : action == "rename" ? 330 : 365,
                        clickFromBottom: 65);
                    await Window(dialog, true);
                }
                await OpenClassEditor();
                if (!accept)
                {
                    Key("Escape", dialog); await Window(dialog, false); await FinishSetup(true);
                    await Same(previous, await Read(token), evidenceKey + "-dialog-cancel");
                    await OpenClassEditor();
                }
                if (action != "delete")
                {
                    Key("a", dialog, control: true);
                    foreach (char c in action == "add" ? "newgroup" : "renamedgroup") Key(c.ToString(), dialog);
                }
                Key("Return", dialog); await Window(dialog, false);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, evidenceKey + "-edited.png"), token);
                await FinishSetup(accept);
                var changed = await Read(token);
                if (!accept) { await Same(previous, changed, "chain-class-cancel-" + action); continue; }
                var expected = previous.Data.Clone();
                foreach (var sheet in expected.Instances)
                {
                    var state = sheet.Metadata.NetChainClasses;
                    if (action != "add") state.Definitions.Remove(targetClass);
                    if (action != "delete") state.Definitions.Add(action == "add" ? "newgroup" : "renamedgroup");
                    if (assigned)
                    {
                        foreach (string owner in state.Assignments.Where(pair => pair.Value == targetClass).Select(pair => pair.Key).ToArray())
                        {
                            if (action == "delete") state.Assignments.Remove(owner);
                            else state.Assignments[owner] = "renamedgroup";
                        }
                    }
                    var sorted = state.Definitions.Order(StringComparer.Ordinal).ToArray();
                    state.Definitions.Clear(); state.Definitions.Add(sorted);
                }
                await Same(expected, changed.Data, evidenceKey);
                var events = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                    { Document = root, DocumentEpoch = previous.Revision.Epoch, AfterSequence = previous.Revision.Sequence }, token);
                Assert.AreEqual(1, events.Changes.Count); Assert.AreEqual("Edit Schematic Setup", events.Changes.Single().Description);
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
                using (var project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token)))
                {
                    CollectionAssert.AreEquivalent(expected.Instances[0].Metadata.NetChainClasses.Definitions.ToArray(),
                        project.RootElement.GetProperty("net_settings").GetProperty("net_chain_class_definitions")
                            .EnumerateArray().Select(v => v.GetString()).ToArray());
                    var persisted = project.RootElement.GetProperty("net_settings").GetProperty("net_chain_classes");
                    CollectionAssert.AreEquivalent(expected.Instances[0].Metadata.NetChainClasses.Assignments.Select(p => p.Key + ":" + p.Value).ToArray(),
                        persisted.EnumerateObject().Select(p => p.Name + ":" + p.Value.GetString()).ToArray());
                }
                var undoneClass = await History("z", changed); await Same(previous.Data, undoneClass.Data, "chain-class-undo");
                var redoneClass = await History("y", undoneClass); await Same(expected, redoneClass.Data, "chain-class-redo");
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
                var reopened = await Read(token); await Same(expected, reopened.Data, "chain-class-reopened");
                // Opening setup after reload must show the still-unused class;
                // unchanged OK may neither lose it nor create another revision.
                await OpenSetupPage(); await FinishSetup(true);
                await Same(reopened, await Read(token), "chain-class-reopen-noop");
                var restore = new ApplySchematicItemBatch { Document = root, DocumentEpoch = reopened.Revision.Epoch,
                    ExpectedRevision = reopened.Revision, OperationId = Guid.NewGuid().ToString("D") };
                restore.Operations.Add(new SchematicItemOperation { ReplaceNetChainClasses = previous.Data.Instances[0].Metadata.NetChainClasses.Clone() });
                await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(restore, token);
                await Same(previous.Data, (await Read(token)).Data, "chain-class-restored");
                await Saved(false);
            }
            var batchBaseline = await Read(token);
            var originalState = batchBaseline.Data.Instances[0].Metadata.NetChainClasses;
            ApplySchematicItemBatch ReplaceClasses(SchematicNetChainClassState state)
            {
                var batch = new ApplySchematicItemBatch { Document = root, DocumentEpoch = batchBaseline.Revision.Epoch,
                    ExpectedRevision = batchBaseline.Revision, OperationId = Guid.NewGuid().ToString("D") };
                batch.Operations.Add(new SchematicItemOperation { ReplaceNetChainClasses = state });
                return batch;
            }
            foreach (Action<SchematicNetChainClassState> corrupt in new Action<SchematicNetChainClassState>[]
            {
                s => s.Definitions.Add(s.Definitions[0]), s => s.Definitions.Add(""), s => s.Definitions.Add("bad\0class"),
                s => s.Assignments["CHAIN"] = "Missing", s => s.Assignments[""] = s.Definitions[0],
                s => s.Assignments["bad\0chain"] = s.Definitions[0]
            })
            {
                var invalid = originalState.Clone(); corrupt(invalid);
                await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(ReplaceClasses(invalid), token));
                await Same(batchBaseline, await Read(token), "chain-class-malformed");
            }
            var future = SchematicNetChainClassState.Parser.ParseFrom(originalState.ToByteArray()
                .Concat(new byte[] { 0xf8, 0x07, 0x01 }).ToArray());
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(ReplaceClasses(future), token));
            await Same(batchBaseline, await Read(token), "chain-class-future");
            var desired = originalState.Clone(); desired.Definitions.Add("newclass"); desired.Assignments.Add("UnresolvedChain", "newclass");
            var edit = ReplaceClasses(desired);
            var rollback = edit.Clone(); rollback.OperationId = Guid.NewGuid().ToString("D"); rollback.Operations.Add(new SchematicItemOperation());
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rollback, token));
            await Same(batchBaseline, await Read(token), "chain-class-rollback");
            var result = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(edit, token);
            Assert.IsTrue(result.NetChainClassesChanged);
            var actualEdit = await Read(token);
            foreach (var sheet in actualEdit.Data.Instances)
                Assert.AreEqual("newclass", sheet.Metadata.NetChainClasses.Assignments["UnresolvedChain"]);
            Assert.AreEqual(result, await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(edit, token));
            await Same(actualEdit, await Read(token), "chain-class-retry");
            var noop = edit.Clone(); noop.OperationId = Guid.NewGuid().ToString("D"); noop.ExpectedRevision = actualEdit.Revision;
            Assert.IsFalse((await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(noop, token)).NetChainClassesChanged);
            await Same(actualEdit, await Read(token), "chain-class-noop");
            var stale = ReplaceClasses(originalState.Clone());
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
            await Same(actualEdit, await Read(token), "chain-class-stale");
            var apiUndo = await History("z", actualEdit); await Same(batchBaseline.Data, apiUndo.Data, "chain-class-api-undo");
            var apiRedo = await History("y", apiUndo); await Same(actualEdit.Data, apiRedo.Data, "chain-class-api-redo");
            apiUndo = await History("z", apiRedo); await Same(batchBaseline.Data, apiUndo.Data, "chain-class-api-restored");
            await Saved(false);
        }

        async Task VerifyCreation(SchematicHierarchyDataSnapshot empty)
        {
            const string create = "Create Net Chain";
            async Task<nuint> OpenCreate()
            {
                await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
                var select = new AddToSelection { Header = header };
                var symbols = empty.Data.Instances.Single(s => s.Metadata.Document.Equals(root)).Items
                    .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                    .OrderBy(s => s.ReferenceField.Text.Text_, StringComparer.Ordinal).ToArray();
                Assert.AreEqual(2, symbols.Length);
                select.Items.Add(symbols.Select(s => s.Id.Clone()));
                await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
                Key("t", alt: true); Key("End"); Key("Up"); Key("Up"); Key("Return");
                // The isolated two-probe fixture has no passthrough component;
                // accept the native dialog's explicit manual-link choice.
                await Window("Find Path", true);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-create-manual-choice.png"), token);
                Key("Return", "Find Path"); await Window("Find Path", false);
                await Window(create, true); await StableSetupGeometry(create);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-create-dialog.png"), token);
                nuint owner = 0;
                NativeKeyboard.SchematicShortcut(display, processId, "", create, observeWindow: id => owner = id);
                return owner;
            }
            void Name(string value)
            {
                NativeKeyboard.SchematicShortcut(display, processId, "click", create, controlKey: false,
                    focusCanvas: true, clickFromLeft: 150, clickFromBottom: 70);
                Key("a", create, control: true); Key("BackSpace", create);
                foreach (char c in value) Key(c.ToString(), create);
            }
            void Create() => NativeKeyboard.SchematicShortcut(display, processId, "click", create, controlKey: false,
                focusCanvas: true, clickFromRight: 60, clickFromBottom: 25);
            async Task Close()
            {
                NativeKeyboard.SchematicShortcut(display, processId, "click", create, controlKey: false,
                    focusCanvas: true, clickFromRight: 150, clickFromBottom: 25);
                await Window(create, false);
            }
            await OpenCreate(); Name("cancelledchain"); await Close();
            await Same(empty, await Read(token), "chain-create-cancel");
            nuint parent = await OpenCreate(); Name("UNRESOLVED_PATH"); Create();
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                while (!NativeKeyboard.HasWindow(display, processId, create, excludeWindow: parent))
                    await Task.Delay(50, limit.Token);
                NativeKeyboard.SchematicShortcut(display, processId, "Return", create, controlKey: false,
                    focusCanvas: false, excludeWindow: parent);
                while (NativeKeyboard.HasWindow(display, processId, create, excludeWindow: parent))
                    await Task.Delay(50, limit.Token);
            }
            await Close(); await Same(empty, await Read(token), "chain-create-collision");
            await OpenCreate(); Name("createdpath"); Create(); await Close();
            var created = await Read(token);
            Assert.IsTrue(created.Revision.Sequence > empty.Revision.Sequence);
            foreach (var screen in created.Data.Instances)
                Assert.IsTrue(screen.Metadata.NetChains.Single(c => c.Name == "createdpath").Committed);
            var events = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = root, DocumentEpoch = empty.Revision.Epoch, AfterSequence = empty.Revision.Sequence }, token);
            Assert.AreEqual(1, events.Changes.Count);
            Assert.AreEqual("Create Net Chain", events.Changes.Single().Description);
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            StringAssert.Contains(await File.ReadAllTextAsync(rootFile, token), "(net_chain \"createdpath\"");
            // A manual one-net chain has no bridging resistor to disable.
            // The actual Remove control must still persist the explicit
            // removal and retain its original now-unresolved endpoint intent.
            await OpenChainMenu(); Key("End"); Key("Up"); Key("Up"); Key("Up"); Key("Return");
            var manuallyRemoved = await Read(token);
            Assert.IsTrue(manuallyRemoved.Revision.Sequence > created.Revision.Sequence);
            foreach (var screen in manuallyRemoved.Data.Instances)
            {
                var removed = screen.Metadata.NetChains.Single(c => c.Name == "createdpath");
                var original = created.Data.Instances[0].Metadata.NetChains.Single(c => c.Name == "createdpath");
                Assert.IsFalse(removed.Committed);
                Assert.AreEqual(0, removed.MemberNets.Count);
                CollectionAssert.AreEquivalent(original.MemberNets.ToArray(), removed.Exclusions.NetNames.ToArray());
                Assert.AreEqual(2, removed.Exclusions.Pins.Count);
                Assert.AreEqual(original.From, removed.From); Assert.AreEqual(original.To, removed.To);
            }
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            StringAssert.Contains(await File.ReadAllTextAsync(rootFile, token), "(excluded_pin");
            var manualUndo = await History("z", manuallyRemoved); await Same(created.Data, manualUndo.Data, "chain-manual-remove-undo");
            var manualRedo = await History("y", manualUndo); await Same(manuallyRemoved.Data, manualRedo.Data, "chain-manual-remove-redo");
            manualUndo = await History("z", manualRedo); await Same(created.Data, manualUndo.Data, "chain-manual-remove-restored");
            created = await Read(token);
            var undoneCreation = await History("z", created); await Same(empty.Data, undoneCreation.Data, "chain-create-undo");
            var redoneCreation = await History("y", undoneCreation); await Same(created.Data, redoneCreation.Data, "chain-create-redo");
            undoneCreation = await History("z", redoneCreation); await Same(empty.Data, undoneCreation.Data, "chain-create-restored");
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
        }
    }
}
