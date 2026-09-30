using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySetupPinMap(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Snapshot() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        var baseline = await Snapshot();
        var initialSave = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token);
        var changed = baseline.Data.Clone();
        var inputRule = changed.Metadata.ErcSettings.PinMap.Single(x => (int)x.First == 1 && (int)x.Second == 1);
        Assert.IsTrue((int)inputRule.Conflict is >= 1 and <= 3);
        inputRule.Conflict = (SchematicErcPinConflict)((int)inputRule.Conflict % 3 + 1);
        foreach (var (accept, validationFailure) in new[] { (false, false), (false, true), (true, true) })
        {
            string phase = $"{accept}-{validationFailure}";
            await NativeSetupUi.Open(client, document, display, processId, token);
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(token);
            ready.CancelAfter(TimeSpan.FromSeconds(10));
            int delay = 25;
            // Click the visible Pin Conflicts Map row in this fixed Linux
            // fixture. Category redirection does not remove category rows from
            // keyboard navigation, and lazy page construction must finish before
            // traversal into its controls. Retain the rendered checkpoint.
            await NativeSetupUi.SelectPage(display, processId, 180, token);
            await NativeKeyboard.CaptureAsync(display,
                Path.Combine(evidence, instanceId + $"-setup-pinmap-{phase}-selected.png"), token);
            // The bitmap matrix is not the first keyboard traversal target.
            // Use its measured input/input cell on the rendered fixed-display
            // fixture (native window-relative coordinates).
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromLeft: 414, clickFromTop: 59);
            await NativeKeyboard.CaptureAsync(display,
                Path.Combine(evidence, instanceId + $"-setup-pinmap-{phase}-edited.png"), token);
            Assert.IsFalse(NativeKeyboard.HasWindow(display, processId, "Import Settings"),
                "The matrix probe must edit a matrix button, not activate the global Import action.");
            await NativeSetupUi.SelectPage(display, processId, 34, token);
            await NativeKeyboard.CaptureAsync(display,
                Path.Combine(evidence, instanceId + $"-setup-pinmap-{phase}-page-switched.png"), token);
            if (validationFailure)
            {
                // An invalid value on a later page must not commit the earlier
                // matrix change. After acknowledging the error, correcting the
                // value and accepting must retain that earlier pending change.
                NativeKeyboard.SchematicShortcut(display, processId, "a", "Schematic Setup", true,
                    clickFromLeft: 500, clickFromTop: 493);
                NativeKeyboard.SchematicShortcut(display, processId, "0", "Schematic Setup", false, false);
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                    clickFromRight: 60, clickFromBottom: 25);
                using var invalid = CancellationTokenSource.CreateLinkedTokenSource(token);
                invalid.CancelAfter(TimeSpan.FromSeconds(8));
                int errorDelay = 25;
                while (!NativeKeyboard.HasWindow(display, processId, "Error"))
                { await Task.Delay(errorDelay, invalid.Token); errorDelay = Math.Min(errorDelay * 2, 500); }
                await NativeKeyboard.CaptureAsync(display,
                    Path.Combine(evidence, instanceId + $"-setup-pinmap-{phase}-validation-error.png"), token);
                NativeKeyboard.SchematicShortcut(display, processId, "Return", "Error", false, false);
                while (NativeKeyboard.HasWindow(display, processId, "Error"))
                    await Task.Delay(50, invalid.Token);
                Assert.IsTrue(NativeKeyboard.HasWindow(display, processId, "Schematic Setup"));
                NativeKeyboard.SchematicShortcut(display, processId, "a", "Schematic Setup", true,
                    clickFromLeft: 500, clickFromTop: 493);
                string originalGridMils = (baseline.Data.Metadata.Formatting.ConnectionGridNm / 25400m)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                foreach (char c in originalGridMils)
                    NativeKeyboard.SchematicShortcut(display, processId, c.ToString(), "Schematic Setup", false, false);
            }
            ready.CancelAfter(TimeSpan.FromSeconds(10));
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromRight: accept ? 60 : 150, clickFromBottom: 25);
            delay = 25;
            try
            {
                while (NativeKeyboard.HasWindow(display, processId, "Schematic Setup"))
                { await Task.Delay(delay, ready.Token); delay = Math.Min(delay * 2, 500); }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                var windows = new List<string>();
                NativeKeyboard.HasWindow(display, processId, "Schematic Setup", describe: windows.Add);
                await File.WriteAllLinesAsync(Path.Combine(evidence, instanceId + "-setup-pinmap-windows.txt"), windows, token);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-setup-pinmap-close-failed.png"), token);
                throw;
            }
            var result = await Snapshot();
            var expected = accept ? changed : baseline.Data;
            if (!expected.Equals(result.Data))
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + $"-pinmap-{accept}-expected.json"), expected.ToString(), token);
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + $"-pinmap-{accept}-actual.json"), result.Data.ToString(), token);
            }
            Assert.IsTrue(expected.Equals(result.Data),
                $"Pin matrix {(accept ? "accept" : "cancel")} must preserve every unrelated field; exact states are retained in evidence.");
            Assert.AreEqual(baseline.Revision.Sequence + (accept ? 1UL : 0UL), result.Revision.Sequence);
            if (!accept)
                Assert.AreEqual(initialSave, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                    new() { Document = document }, token));
        }
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        await WaitFor(baseline.Data);
        NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false, focusCanvas: false,
            clickFromLeft: 280, clickFromTop: 43);
        await WaitFor(changed);
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        await WaitFor(baseline.Data);
        await client.InvokeAsync<SaveDocument, Google.Protobuf.WellKnownTypes.Empty>(new() { Document = document }, token);

        async Task WaitFor(SchematicScreenData expected)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(8));
            int delay = 25;
            while (true)
            {
                var state = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                    new() { Document = document }, wait.Token);
                if (state.Data.Equals(expected)) return;
                await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 500);
            }
        }
    }
}
