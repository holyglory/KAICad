using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyPcbPageOwner(NativeClient client, DocumentSpecifier board,
        DocumentSpecifier schematic, int processId, string display, string evidence, CancellationToken token)
    {
        Task<PageSettings> Read(DocumentSpecifier target) => client.InvokeAsync<GetPageSettings, PageSettings>(
            new() { Document = target }, token);
        var original = await Read(board);
        var other = await Read(schematic);
        string layout = Path.Combine(board.Project.Path, "pcb-owner-fixture.kicad_wks");
        await File.WriteAllTextAsync(layout,
            "(kicad_wks (version 20220228) (generator pl_editor) " +
            "(setup (textsize 1.5 1.5) (linewidth 0.15) (textlinewidth 0.15) " +
            "(left_margin 10) (right_margin 10) (top_margin 10) (bottom_margin 10)) " +
            "(rect (name \"Frame\") (start 0 0 ltcorner) (end 0 0)) " +
            "(tbtext \"PCB OWNER FIXTURE\" (name \"Label\") (pos 10 10 ltcorner)))", token);
        var desired = original.Clone(); desired.DrawingSheet = layout;
        var changed = await client.InvokeAsync<SetPageSettings, PageSettings>(new()
            { Document = board, PageSettings = desired }, token);
        Assert.AreEqual(desired, changed);
        Assert.AreEqual(other, await Read(schematic), "PCB API layout changes must not change the schematic layout owner.");
        await SaveCheckedThroughMcp(client, board, evidence, token);
        await client.InvokeAsync<RevertDocument, Google.Protobuf.WellKnownTypes.Empty>(new() { Document = board }, token);
        Assert.AreEqual(desired, await Read(board));
        Assert.AreEqual(other, await Read(schematic));
        var beforeDialog = await ObserveLifecycleState(client, board, token);
        NativeKeyboard.SchematicShortcut(display, processId, "click", "PCB Editor", false, false,
            clickFromLeft: 100, clickFromTop: 44);
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            int delay = 25;
            while (!NativeKeyboard.HasWindow(display, processId, "Page Settings"))
            { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
            await NativeSetupUi.StableGeometry(display, processId, limit.Token, "Page Settings");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-pcb-page-owner.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "Escape", "Page Settings", false, false);
            while (NativeKeyboard.HasWindow(display, processId, "Page Settings"))
            { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
        }
        Assert.AreEqual(beforeDialog, await ObserveLifecycleState(client, board, token),
            "Page dialog Cancel must preserve the exact PCB state and owner filename.");
        Assert.AreEqual(other, await Read(schematic));
        await client.InvokeAsync<SetPageSettings, PageSettings>(new() { Document = board, PageSettings = original }, token);
        await SaveCheckedThroughMcp(client, board, evidence, token);
        Assert.AreEqual(original, await Read(board));
        Assert.AreEqual(other, await Read(schematic));
    }
}
