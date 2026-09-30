using System.Text.Json;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyPcbPreferences(NativeClient client, DocumentSpecifier board,
        int processId, string display, string evidence, CancellationToken token)
    {
        string project = Path.Combine(board.Project.Path, board.Project.Name + ".kicad_pro");
        async Task WaitDialog(string title, bool visible)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            int delay = 25;
            while (NativeKeyboard.HasWindow(display, processId, title) != visible)
            { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
            if (visible) await NativeSetupUi.StableGeometry(display, processId, limit.Token, title);
        }
        void Key(string key, string title = "PCB Editor", bool control = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, title, control, false);
        async Task OpenSaveChoice(bool preset)
        {
            // Native Appearance controls are anchored above the selection filter.
            // This uses the actual rendered choice, not its event handler.
            await NativeSetupUi.StableGeometry(display, processId, token, "PCB Editor");
            NativeKeyboard.SchematicShortcut(display, processId, "click", "PCB Editor", false, false,
                clickFromRight: 30, clickFromBottom: preset ? 306 : 251);
            await NativeSetupUi.WaitForPopup(display, processId, true, token, "PCB Editor");
            Key("End"); Key("Up"); Key("Return");
            await WaitDialog(preset ? "Save Layer Preset" : "Save Viewport", true);
        }

        foreach (bool preset in new[] { true, false })
        {
            string title = preset ? "Save Layer Preset" : "Save Viewport";
            string name = preset ? "automation preset" : "automation viewport";
            var before = await ObserveLifecycleState(client, board, token);
            byte[] beforeFile = await File.ReadAllBytesAsync(project, token);
            await OpenSaveChoice(preset);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-pcb-preference-cancel-" + preset + ".png"), token);
            Key("Escape", title);
            await WaitDialog(title, false);
            Assert.AreEqual(before, await ObserveLifecycleState(client, board, token), "Cancelling a named preference must not change native state.");
            CollectionAssert.AreEqual(beforeFile, await File.ReadAllBytesAsync(project, token));

            await OpenSaveChoice(preset);
            Key("a", title, true);
            foreach (char character in name) Key(character.ToString(), title);
            Key("Return", title);
            await WaitDialog(title, false);
            var changed = await ObserveLifecycleState(client, board, token);
            Assert.AreNotEqual(before.StateSha256, changed.StateSha256, "The live named preference must enter the exact state before saving.");
            CollectionAssert.AreEqual(beforeFile, await File.ReadAllBytesAsync(project, token), "A state observation must not persist pending UI values.");
            var stale = await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(new()
                { Document = board, ExpectedState = before, OperationId = Guid.NewGuid().ToString("D") }, token);
            Assert.AreEqual(LifecycleOperationStatus.LosRejected, stale.Status);
            Assert.AreEqual(changed, await ObserveLifecycleState(client, board, token));
            await SaveCheckedThroughMcp(client, board, evidence, token);
            using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(project, token));
            var entries = saved.RootElement.GetProperty("board").GetProperty(preset ? "layer_presets" : "viewports");
            Assert.IsTrue(entries.EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == name));
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-pcb-preference-saved-" + preset + ".png"), token);
        }
        var savedState = await ObserveLifecycleState(client, board, token);
        await client.InvokeAsync<RevertDocument, Google.Protobuf.WellKnownTypes.Empty>(new() { Document = board }, token);
        var reloaded = await ObserveLifecycleState(client, board, token);
        // The state digest is native-writer data, not the regenerated runtime BOARD UUID.
        Assert.AreEqual(savedState.StateSha256, reloaded.StateSha256,
            "Native preferences and design state must survive a real reload.");
        using var persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(project, token));
        Assert.IsTrue(persisted.RootElement.GetProperty("board").GetProperty("layer_presets").EnumerateArray()
            .Any(entry => entry.GetProperty("name").GetString() == "automation preset"));
        Assert.IsTrue(persisted.RootElement.GetProperty("board").GetProperty("viewports").EnumerateArray()
            .Any(entry => entry.GetProperty("name").GetString() == "automation viewport"));
        Assert.IsFalse(reloaded.NativeContentDirty);
    }
}
