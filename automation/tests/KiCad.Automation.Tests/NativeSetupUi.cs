using System.Diagnostics;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

// Real native input shared by Setup journeys. Window mapping alone does not
// establish that its modal loop or deferred lazy-page layout is ready.
internal static class NativeSetupUi
{
    internal static async Task Open(NativeClient client, DocumentSpecifier document,
        string display, int processId, CancellationToken token)
    {
        NativeKeyboard.SchematicShortcut(display, processId, "f", controlKey: false, altKey: true);
        NativeKeyboard.SchematicShortcut(display, processId, "End", controlKey: false, focusCanvas: false);
        for (int i = 0; i < 4; ++i)
            NativeKeyboard.SchematicShortcut(display, processId, "Up", controlKey: false, focusCanvas: false);
        NativeKeyboard.SchematicShortcut(display, processId, "Return", controlKey: false, focusCanvas: false);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(10));
        int delay = 25;
        while (true)
        {
            wait.Token.ThrowIfCancellationRequested();
            if (NativeKeyboard.HasWindow(display, processId, "Schematic Setup"))
            {
                try { await client.InvokeAsync<GetPageSettings, PageSettings>(new() { Document = document }, wait.Token); }
                catch (NativeApiException busy) when (busy.Status == 7) { break; }
            }
            await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 500);
        }
        await StableGeometry(display, processId, token);
    }

    internal static async Task SelectPage(string display, int processId, int treeRowY, CancellationToken token)
    {
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
            clickFromLeft: 120, clickFromTop: treeRowY);
        await StableGeometry(display, processId, token);
    }

    // failureCapture, when given, receives a screenshot of the display if the popup never reaches the wanted state,
    // so an intermittent failure can be diagnosed from the retained evidence.
    internal static async Task WaitForPopup(string display, int processId, bool visible, CancellationToken token,
        string window = "Schematic Setup", string? failureCapture = null)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        int delay = 25;
        try
        {
            while (true)
            {
                wait.Token.ThrowIfCancellationRequested();
                int count = 0;
                NativeKeyboard.SchematicShortcut(display, processId, "", window,
                    observePopupCount: value => count = value);
                if ((count != 0) == visible) return;
                await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 200);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (failureCapture is not null)
                await NativeKeyboard.CaptureAsync(display, failureCapture, CancellationToken.None);
            throw new AssertFailedException($"A popup of '{window}' did not {(visible ? "open" : "close")} within 5 s"
                + (failureCapture is null ? "." : $"; the display is saved as {Path.GetFileName(failureCapture)}."));
        }
    }

    internal static async Task StableGeometry(string display, int processId, CancellationToken token,
        string window = "Schematic Setup")
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(8));
        (int X, int Y, int Width, int Height)? previous = null;
        var quiet = Stopwatch.StartNew();
        int delay = 25;
        while (true)
        {
            wait.Token.ThrowIfCancellationRequested();
            (int X, int Y, int Width, int Height) current = default;
            NativeKeyboard.SchematicShortcut(display, processId, "", window,
                observeGeometry: value => current = value);
            if (previous != current) { previous = current; quiet.Restart(); }
            else if (quiet.Elapsed >= TimeSpan.FromMilliseconds(200)) return;
            await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 100);
        }
    }
}
