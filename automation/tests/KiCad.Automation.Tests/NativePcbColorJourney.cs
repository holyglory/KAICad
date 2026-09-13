using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyPcbNetColor(NativeClient client, DocumentSpecifier board,
        DocumentSpecifier schematic, int processId, string display, string evidence, CancellationToken token)
    {
        async Task<Kiapi.Schematic.Types.SchematicNetSettings> Colors() =>
            (await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = schematic }, token)).Data.Metadata.NetSettings.Clone();
        void Key(string key, string window = "PCB Editor", bool control = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, window, control, false);
        async Task Dialog(bool visible)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            int delay = 25;
            while (NativeKeyboard.HasWindow(display, processId, "Color Picker") != visible)
            { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
            if (visible) await NativeSetupUi.StableGeometry(display, processId, limit.Token, "Color Picker");
        }
        async Task Menu(bool clear)
        {
            // The fixture's first sorted named net is OTHER; color editing uses
            // its real context menu, not the grid table's setter.
            NativeKeyboard.SchematicShortcut(display, processId, "right-click", "PCB Editor", false, false,
                clickFromRight: 140, clickFromTop: 201);
            await NativeSetupUi.WaitForPopup(display, processId, true, token, "PCB Editor");
            Key("Home"); if (clear) Key("Down"); Key("Return");
            if (!clear) await Dialog(true);
        }

        var original = await Colors();
        Assert.IsFalse(original.NetColors.ContainsKey("OTHER"), "The isolated fixture starts with no OTHER override.");
        string project = Path.Combine(board.Project.Path, board.Project.Name + ".kicad_pro");
        NativeKeyboard.SchematicShortcut(display, processId, "click", "PCB Editor", false, false,
            clickFromRight: 45, clickFromTop: 140);
        await NativeSetupUi.StableGeometry(display, processId, token, "PCB Editor");
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-net-color-grid.png"), token);
        var before = await ObserveLifecycleState(client, board, token);
        byte[] diskBefore = await File.ReadAllBytesAsync(project, token);
        await Menu(false);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-net-color-cancel.png"), token);
        Key("Escape", "Color Picker"); await Dialog(false);
        Assert.AreEqual(before, await ObserveLifecycleState(client, board, token));
        Assert.AreEqual(original, await Colors());
        CollectionAssert.AreEqual(diskBefore, await File.ReadAllBytesAsync(project, token));

        await Menu(false);
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Color Picker", false, false,
            clickFromLeft: 285, clickFromBottom: 25);
        Key("a", "Color Picker", true);
        foreach (char character in "#127fa3") Key(character.ToString(), "Color Picker");
        Key("Tab", "Color Picker");
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-net-color-edited.png"), token);
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Color Picker", false, false,
            clickFromRight: 60, clickFromBottom: 25);
        await Dialog(false);
        var changed = await Colors();
        Assert.IsTrue(changed.NetColors.ContainsKey("OTHER"));
        var actual = changed.NetColors["OTHER"];
        Assert.AreEqual(18d / 255, actual.R, 1e-12);
        Assert.AreEqual(127d / 255, actual.G, 1e-12);
        Assert.AreEqual(163d / 255, actual.B, 1e-12);
        Assert.AreEqual(1d, actual.A, 1e-12);
        var expected = original.Clone(); expected.NetColors.Add("OTHER", actual.Clone());
        Assert.AreEqual(expected, changed, "Changing one visible net must preserve every other declared binding.");
        Assert.AreNotEqual(before.StateSha256, (await ObserveLifecycleState(client, board, token)).StateSha256);
        CollectionAssert.AreEqual(diskBefore, await File.ReadAllBytesAsync(project, token));
        var rejected = await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(new()
            { Document = board, ExpectedState = before, OperationId = Guid.NewGuid().ToString("D") }, token);
        Assert.AreEqual(LifecycleOperationStatus.LosRejected, rejected.Status);
        await SaveCheckedThroughMcp(client, board, evidence, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
        Assert.AreEqual(expected, await Colors());
        await Menu(true);
        await NativeSetupUi.WaitForPopup(display, processId, false, token, "PCB Editor");
        Assert.AreEqual(original, await Colors());
        await SaveCheckedThroughMcp(client, board, evidence, token);
        NativeKeyboard.SchematicShortcut(display, processId, "click", "PCB Editor", false, false,
            clickFromRight: 195, clickFromTop: 140); // Return to Layers for subsequent journeys.
    }
}
