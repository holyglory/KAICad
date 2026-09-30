using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyManualSetup(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        Task<SchematicChangeJournal> Journal() => client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
            new() { Document = document }, token);
        var before = await Journal();
        var baseline = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        var originalRatio = baseline.Data.Metadata.DrawingRatios.OverbarHeightRatio;
        var initialSaveState = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token);
        Assert.AreNotEqual(1.2, originalRatio, "The edit fixture must actually change the native value.");
        foreach (var (accept, edit, stage) in new[]
                 { (false, false, "cancel"), (true, false, "unchanged"), (true, false, "unchanged-again"),
                   (false, true, "cancel-edit"), (true, true, "changed") })
        {
            await NativeSetupUi.Open(client, document, display, processId, token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            if (edit)
            {
                // Select the real Formatting page and measured Overbar entry;
                // GTK tab order may visit dialog-wide buttons before the page.
                await NativeSetupUi.SelectPage(display, processId, 34, token);
                NativeKeyboard.SchematicShortcut(display, processId, "a", "Schematic Setup", true,
                    clickFromLeft: 500, clickFromTop: 97);
                foreach (var key in new[] { "1", "2", "0" })
                    NativeKeyboard.SchematicShortcut(display, processId, key, "Schematic Setup", false, false);
                // Leaving Formatting must transfer into the shared draft, not
                // into the live project. Return to it before Cancel or OK.
                await NativeSetupUi.SelectPage(display, processId, 55, token);
                await NativeKeyboard.CaptureAsync(display,
                    Path.Combine(evidence, instanceId + "-setup-" + stage + "-other-page.png"), token);
                await NativeSetupUi.SelectPage(display, processId, 34, token);
            }
            await NativeKeyboard.CaptureAsync(display,
                Path.Combine(evidence, instanceId + "-setup-" + stage + ".png"), token);
            if (accept)
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                    clickFromRight: 60, clickFromBottom: 25);
            else
                // Escape can be consumed by the focused text-entry editor.
                // Exercise the visible dialog Cancel button instead.
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                    clickFromRight: 150, clickFromBottom: 25);
            while (NativeKeyboard.HasWindow(display, processId, "Schematic Setup"))
                await Task.Delay(100, timeout.Token);
            var after = await Journal();
            Assert.AreEqual(before.DocumentEpoch, after.DocumentEpoch);
            Assert.AreEqual(before.Sequence + (accept && edit ? 1UL : 0UL), after.Sequence,
                edit && accept ? "Accepted settings change must advance the journal once."
                    : accept ? "Unchanged Schematic Setup must not manufacture an edit."
                    : "Cancelled setup must not record an edit.");
            if (edit && accept)
            {
                var changes = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                    new() { Document = document, DocumentEpoch = before.DocumentEpoch, AfterSequence = before.Sequence }, token);
                Assert.IsFalse(changes.ResetRequired);
                Assert.AreEqual("Edit Schematic Setup", changes.Changes.Single().Description);
            }
            var state = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document }, token);
            Assert.AreEqual(accept && edit ? 1.2 : originalRatio, state.Data.Metadata.DrawingRatios.OverbarHeightRatio);
            var expected = baseline.Data.Clone();
            if (accept && edit) expected.Metadata.DrawingRatios.OverbarHeightRatio = 1.2;
            Assert.AreEqual(expected, state.Data, "Setup must preserve unrelated supported schematic state.");
            if (!(accept && edit))
                Assert.AreEqual(initialSaveState,
                    await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token),
                    "Cancelled or unchanged setup must preserve dirty-sheet state.");
        }
        var acceptedState = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        await WaitForState(baseline.Data);
        NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false,
            clickFromLeft: 280, clickFromTop: 43, focusCanvas: false);
        await WaitForState(acceptedState.Data);
        var stale = new ApplySchematicItemBatch
        {
            Document = document, DocumentEpoch = baseline.Revision.Epoch, ExpectedRevision = baseline.Revision,
            OperationId = Guid.NewGuid().ToString("D")
        };
        stale.Operations.Add(new SchematicItemOperation { SetDrawingRatios = baseline.Data.Metadata.DrawingRatios.Clone() });
        var error = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
        StringAssert.Contains(error.Message, "Stale document revision");
        Assert.AreEqual(1.2, (await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token)).Data.Metadata.DrawingRatios.OverbarHeightRatio,
            "Rejecting the stale request must preserve the user's accepted setting.");
        var changed = await Journal();
        stale.ExpectedRevision.Sequence = changed.Sequence;
        stale.OperationId = Guid.NewGuid().ToString("D");
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token);
        Assert.AreEqual(originalRatio,
            (await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document }, token)).Data.Metadata.DrawingRatios.OverbarHeightRatio);
        await client.InvokeAsync<SaveDocument, Google.Protobuf.WellKnownTypes.Empty>(new() { Document = document }, token);

        async Task WaitForState(Kiapi.Schematic.Types.SchematicScreenData expected)
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
