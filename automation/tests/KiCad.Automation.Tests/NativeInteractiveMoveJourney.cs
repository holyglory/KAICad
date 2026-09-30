using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyInteractiveMoveAdmission(NativeClient client, DocumentSpecifier document,
        ElectricalFixture fixture, int processId, string display, string evidence, string instanceId,
        CancellationToken token)
    {
        var header = new ItemHeader { Document = document };
        async Task<SchematicScreenDataSnapshot> Snapshot(CancellationToken local) =>
            await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document }, local);
        var before = await Snapshot(token);
        var saveState = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token);
        var symbol = before.Data.Items.Single(i => i.Is(SchematicSymbolInstance.Descriptor)
            && i.Unpack<SchematicSymbolInstance>().Id.Value == fixture.Symbol).Unpack<SchematicSymbolInstance>();

        // Give the canvas real keyboard focus, then select explicitly. Selection
        // alone must not prevent a matched observation or count as a design edit.
        NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false);
        await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
        var select = new AddToSelection { Header = header };
        select.Items.Add(symbol.Id.Clone());
        var selection = await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
        Assert.AreEqual(1, selection.Items.Count);
        Assert.AreEqual(before, await Snapshot(token));

        NativeKeyboard.SchematicShortcut(display, processId, "m", controlKey: false, focusCanvas: false);
        try
        {
            using var start = CancellationTokenSource.CreateLinkedTokenSource(token);
            start.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { await Snapshot(start.Token); }
                catch (NativeApiException error) when (error.Status == 7)
                {
                    StringAssert.Contains(error.Message, "current schematic edit");
                    break;
                }
                await Task.Delay(100, start.Token);
            }
            var previewError = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token));
            Assert.AreEqual(7, previewError.Status);
            Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(new() { Document = document }, token))).Status);
            Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = document }, token))).Status);
            var mutation = new ApplySchematicItemBatch
            {
                Document = document, DocumentEpoch = before.Revision.Epoch,
                ExpectedRevision = before.Revision, OperationId = Guid.NewGuid().ToString("D")
            };
            var moved = symbol.Clone(); moved.Position.XNm += 2540000;
            mutation.Operations.Add(new SchematicItemOperation { Update = Any.Pack(moved) });
            Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(mutation, token))).Status);
            await NativeKeyboard.CaptureAsync(display,
                Path.Combine(evidence, instanceId + "-interactive-move.png"), token);
        }
        catch
        {
            try
            {
                await NativeKeyboard.CaptureAsync(display,
                    Path.Combine(evidence, instanceId + "-interactive-move-failed.png"), CancellationToken.None);
            }
            catch (Exception captureError)
            {
                Console.Error.WriteLine("Interactive-move failure capture unavailable: " + captureError.Message);
            }
            throw;
        }
        finally
        {
            NativeKeyboard.SchematicShortcut(display, processId, "Escape", controlKey: false, focusCanvas: false);
        }

        using var restore = CancellationTokenSource.CreateLinkedTokenSource(token);
        restore.CancelAfter(TimeSpan.FromSeconds(5));
        SchematicScreenDataSnapshot after;
        try
        {
            while (true)
            {
                try { after = await Snapshot(restore.Token); break; }
                catch (NativeApiException error) when (error.Status == 7) { await Task.Delay(100, restore.Token); }
            }
        }
        catch
        {
            try
            {
                await NativeKeyboard.CaptureAsync(display,
                    Path.Combine(evidence, instanceId + "-interactive-cancel-failed.png"), CancellationToken.None);
            }
            catch (Exception captureError)
            {
                Console.Error.WriteLine("Interactive-cancel failure capture unavailable: " + captureError.Message);
            }
            throw;
        }
        Assert.AreEqual(before, after, "Cancelling the user's move must restore content without inventing a committed revision.");
        Assert.AreEqual(saveState, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token));
        var image = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-interactive-move-cancelled.png"), image.Png.ToByteArray(), token);
        await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
        await VerifyManualMoveRoundTrip(client, document, fixture, processId, display, evidence, instanceId, token);
    }
}
