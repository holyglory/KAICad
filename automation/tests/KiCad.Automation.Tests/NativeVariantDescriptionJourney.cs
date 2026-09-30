using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyVariantDescriptionDialog(NativeClient client, DocumentSpecifier root,
        string rootFile, int processId, string display, string evidence, CancellationToken token)
    {
        const string chooser = "Edit Variant Description", editor = "Edit Description for";
        bool captured = false;
        void Key(string key, string title = "Schematic Editor", bool control = false, bool alt = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, title, control,
                focusCanvas: false, altKey: alt);
        async Task Window(string title, bool visible)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (NativeKeyboard.HasWindow(display, processId, title) != visible)
                await Task.Delay(100, timeout.Token);
        }
        async Task OpenChooser()
        {
            // Existing Tools > Variants submenu: Reload Plugins is the last
            // Tools entry; Edit Description is the third variant action.
            Key("t", alt: true); Key("End"); Key("Up"); Key("Right");
            Key("Down"); Key("Down"); Key("Return");
            await Window(chooser, true);
        }
        async Task OpenEditor()
        {
            await OpenChooser(); Key("Return", chooser); await Window(editor, true);
            NativeKeyboard.SchematicShortcut(display, processId, "click", editor, controlKey: false,
                focusCanvas: true, clickFromLeft: 40, clickFromTop: 50);
            if (!captured)
            {
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "variant-description-dialog.png"), token);
                captured = true;
            }
        }
        void Text(string value)
        {
            Key("a", editor, control: true); Key("BackSpace", editor);
            foreach (char c in value) Key(c.ToString(), editor);
        }
        async Task Accept()
        {
            // GTK orders Cancel before OK here; tabbing once from the field
            // selects Cancel. Use the rendered bottom-right OK button.
            NativeKeyboard.SchematicShortcut(display, processId, "click", editor, controlKey: false,
                focusCanvas: true, clickFromRight: 60, clickFromBottom: 26);
            await Window(editor, false);
        }
        Task<SchematicMetadataSnapshot> Read() => client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(
            new() { Document = root }, token);
        async Task Saved(string description)
        {
            var snapshot = await Read();
            Assert.AreEqual(description, snapshot.Metadata.VariantDescriptions["Assembly"]);
            Assert.AreEqual(snapshot.Metadata, SchematicDataXml.Read(SchematicDataXml.Write(snapshot.Metadata)));
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            using var project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token));
            var variant = project.RootElement.GetProperty("schematic").GetProperty("variants").EnumerateArray()
                .Single(v => v.GetProperty("name").GetString() == "Assembly");
            Assert.AreEqual(description, variant.TryGetProperty("description", out var value) ? value.GetString() : "",
                "The registered variant and its description must persist through native saving.");
        }
        async Task ChangedSince(SchematicMetadataSnapshot before)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new() { Document = root }, timeout.Token))
                .Revision.Equals(before.Revision)) await Task.Delay(100, timeout.Token);
        }
        await Saved("original"); var baseline = await Read();
        var beforeRejected = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = root }, token);
        var invalidSymbol = beforeRejected.Data.Items.First(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Unpack<SchematicSymbolInstance>();
        invalidSymbol.Variants ??= new();
        invalidSymbol.Variants.Variants.Add(new SchematicSymbolVariant
            { Name = "Assembly", Description = "unrequested global change" });
        var rejected = new ApplySchematicItemBatch { Document = root, ExpectedRevision = baseline.Revision,
            DocumentEpoch = baseline.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        rejected.Operations.Add(new SchematicItemOperation { Update = Any.Pack(invalidSymbol) });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token));
        Assert.AreEqual(beforeRejected, await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = root }, token));
        await Saved("original");
        await OpenChooser(); Key("Escape", chooser); await Window(chooser, false);
        Assert.AreEqual(baseline, await Read());
        await OpenEditor(); Text("discard"); Key("Escape", editor); await Window(editor, false);
        Assert.AreEqual(baseline, await Read()); await Saved("original");
        await OpenEditor(); await Accept(); Assert.AreEqual(baseline, await Read());
        await OpenEditor(); Text("updated");
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "variant-description-edited.png"), token);
        await Accept(); await ChangedSince(baseline); await Saved("updated");
        var edited = await Read();
        var changes = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            { Document = root, DocumentEpoch = baseline.Revision.Epoch, AfterSequence = baseline.Revision.Sequence }, token);
        Assert.AreEqual(1, changes.Changes.Count);
        Assert.AreEqual("Edit Variant Description", changes.Changes.Single().Description);
        NativeKeyboard.SchematicShortcut(display, processId, "z"); await ChangedSince(edited); await Saved("original");
        async Task VerifyAdd()
        {
        const string add = "New Design Variant", newVariant = "brandnew";
        async Task OpenAdd()
        {
            Key("t", alt: true); Key("End"); Key("Up"); Key("Right"); Key("Return");
            await Window(add, true);
            NativeKeyboard.SchematicShortcut(display, processId, "click", add, controlKey: false,
                focusCanvas: true, clickFromLeft: 40, clickFromTop: 45);
        }
        async Task HasAdded(bool expected)
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            using var project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token));
            Assert.AreEqual(expected, project.RootElement.GetProperty("schematic").GetProperty("variants").EnumerateArray()
                .Any(v => v.GetProperty("name").GetString() == newVariant));
        }
        var baseline = await Read();
        await OpenAdd(); Key("Escape", add); await Window(add, false);
        Assert.AreEqual(baseline, await Read());
        foreach (string invalidName in new[] { "", "assembly" })
        {
            await OpenAdd();
            foreach (char c in invalidName) Key(c.ToString(), add);
            NativeKeyboard.SchematicShortcut(display, processId, "click", add, controlKey: false,
                focusCanvas: true, clickFromRight: 60, clickFromBottom: 26);
            await Window(add, false);
            Assert.AreEqual(baseline, await Read(), "Empty and case-insensitive duplicate names must not commit an edit.");
            await HasAdded(false);
        }
        await OpenAdd();
        foreach (char c in newVariant) Key(c.ToString(), add);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "variant-add-dialog.png"), token);
        NativeKeyboard.SchematicShortcut(display, processId, "click", add, controlKey: false,
            focusCanvas: true, clickFromRight: 60, clickFromBottom: 26);
        await Window(add, false); await ChangedSince(baseline); await HasAdded(true);
        var changes = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            { Document = root, DocumentEpoch = baseline.Revision.Epoch, AfterSequence = baseline.Revision.Sequence }, token);
        Assert.AreEqual(1, changes.Changes.Count);
        Assert.AreEqual("Add Design Variant", changes.Changes.Single().Description);
        var edited = await Read();
        NativeKeyboard.SchematicShortcut(display, processId, "z"); await ChangedSince(edited); await HasAdded(false);
        var undone = await Read();
        NativeKeyboard.SchematicShortcut(display, processId, "y"); await ChangedSince(undone); await HasAdded(true);
        edited = await Read();
        NativeKeyboard.SchematicShortcut(display, processId, "z"); await ChangedSince(edited); await HasAdded(false);
        }
        var undone = await Read();
        NativeKeyboard.SchematicShortcut(display, processId, "y"); await ChangedSince(undone); await Saved("updated");
        await OpenEditor(); Text(""); await Accept(); await Saved("");
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
        await Saved("");
        await OpenEditor(); Text("original"); await Accept(); await Saved("original");

        // Assembly has no per-symbol overrides. Removing it must still be a
        // real, undoable project edit, not an empty native commit.
        const string remove = "Remove Design Variant";
        baseline = await Read();
        Key("t", alt: true); Key("End"); Key("Up"); Key("Right");
        Key("Down"); Key("Return"); await Window(remove, true);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "variant-remove-dialog.png"), token);
        Key("Escape", remove); await Window(remove, false);
        Assert.AreEqual(baseline, await Read());
        Key("t", alt: true); Key("End"); Key("Up"); Key("Right");
        Key("Down"); Key("Return"); await Window(remove, true);
        Key("Return", remove); await Window(remove, false); await ChangedSince(baseline);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
        using (var project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token)))
            Assert.IsFalse(project.RootElement.GetProperty("schematic").GetProperty("variants").EnumerateArray()
                .Any(v => v.GetProperty("name").GetString() == "Assembly"));
        changes = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            { Document = root, DocumentEpoch = baseline.Revision.Epoch, AfterSequence = baseline.Revision.Sequence }, token);
        Assert.AreEqual(1, changes.Changes.Count);
        Assert.AreEqual("Delete variant 'Assembly'", changes.Changes.Single().Description);
        edited = await Read();
        NativeKeyboard.SchematicShortcut(display, processId, "z"); await ChangedSince(edited); await Saved("original");
        undone = await Read();
        NativeKeyboard.SchematicShortcut(display, processId, "y"); await ChangedSince(undone);
        edited = await Read();
        NativeKeyboard.SchematicShortcut(display, processId, "z"); await ChangedSince(edited); await Saved("original");
        await VerifyAdd();
        foreach (var (menuIndex, title, destination) in new[]
        {
            (4, "Copy Design Variant", "Assembly_copy"),
            (3, "Rename Design Variant", "renamed")
        })
        {
            async Task OpenName()
            {
                Key("t", alt: true); Key("End"); Key("Up"); Key("Right");
                for (int i = 0; i < menuIndex; i++) Key("Down");
                Key("Return"); await Window(title, true);
                nuint chooserId = 0;
                NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false,
                    observeWindow: id => chooserId = id);
                Key("Return", title);
                // These dialogs share a title, but are distinct native windows.
                // Do not query document state while the editor is modal.
                using var transition = CancellationTokenSource.CreateLinkedTokenSource(token);
                transition.CancelAfter(TimeSpan.FromSeconds(5));
                nuint nameId = chooserId;
                while (nameId == chooserId || nameId == 0)
                {
                    nameId = 0;
                    try
                    {
                        NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false,
                            observeWindow: id => nameId = id);
                    }
                    catch (InvalidOperationException error) when (error.Message.StartsWith($"Expected one '{title}'", StringComparison.Ordinal)) { }
                    if (nameId == chooserId || nameId == 0) await Task.Delay(100, transition.Token);
                }
            }
            async Task SavedOperation(bool applied)
            {
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
                using var project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token));
                var variants = project.RootElement.GetProperty("schematic").GetProperty("variants").EnumerateArray().ToArray();
                Assert.AreEqual(applied, variants.Any(v => v.GetProperty("name").GetString() == destination));
                Assert.AreEqual(menuIndex == 4 || !applied, variants.Any(v => v.GetProperty("name").GetString() == "Assembly"));
                if (applied)
                    Assert.AreEqual("original", variants.Single(v => v.GetProperty("name").GetString() == destination)
                        .GetProperty("description").GetString());
            }
            baseline = await Read();
            await OpenName(); Key("Escape", title); await Window(title, false);
            Assert.AreEqual(baseline, await Read());
            await OpenName();
            if (menuIndex == 3)
            {
                Key("a", title, control: true); Key("BackSpace", title);
                foreach (char c in destination) Key(c.ToString(), title);
            }
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"variant-{menuIndex}-name.png"), token);
            Key("Return", title); await Window(title, false); await ChangedSince(baseline); await SavedOperation(true);
            changes = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = root, DocumentEpoch = baseline.Revision.Epoch, AfterSequence = baseline.Revision.Sequence }, token);
            Assert.AreEqual(1, changes.Changes.Count);
            Assert.AreEqual($"{(menuIndex == 4 ? "Copy" : "Rename")} variant 'Assembly' to '{destination}'",
                changes.Changes.Single().Description);
            edited = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, "z"); await ChangedSince(edited); await SavedOperation(false);
            undone = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, "y"); await ChangedSince(undone); await SavedOperation(true);
            edited = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, "z"); await ChangedSince(edited); await SavedOperation(false);
        }
        await VerifyTableVariantEdits(client, root, rootFile, processId, display, evidence, token);
    }
}
