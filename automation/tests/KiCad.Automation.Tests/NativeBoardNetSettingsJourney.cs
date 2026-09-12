using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Commands;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyBoardNetSettings(NativeClient client, DocumentSpecifier schematic,
        string project, int processId, string display, string evidence, CancellationToken token)
    {
        string boardPath = Path.ChangeExtension(project, ".kicad_pcb");
        Assert.IsFalse(File.Exists(boardPath), "Only the private fixture board may be created or replaced.");
        var open = new OpenDocument { Type = (DocumentType)3, Path = boardPath };
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<OpenDocument, OpenDocumentResponse>(open, token));
        Assert.IsFalse(File.Exists(boardPath));
        var foreign = open.Clone(); foreign.Path = Path.Combine(Path.GetDirectoryName(project)!, "foreign.kicad_pcb");
        foreign.CreateIfMissing = true;
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<OpenDocument, OpenDocumentResponse>(foreign, token));
        Assert.IsFalse(File.Exists(foreign.Path));
        open.CreateIfMissing = true;
        var opened = await client.InvokeAsync<OpenDocument, OpenDocumentResponse>(open, token);
        var board = opened.Document;
        Assert.AreEqual((DocumentType)3, board.Type);
        Assert.AreEqual(Path.GetFileName(boardPath), board.BoardFilename);
        Assert.AreEqual(schematic.Project, board.Project);
        Assert.IsFalse(File.Exists(boardPath), "Explicit native creation remains unsaved.");
        Assert.AreEqual(opened, await client.InvokeAsync<OpenDocument, OpenDocumentResponse>(open, token));
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = board }, token);
        Assert.IsTrue(File.Exists(boardPath));

        // Test data only: reload a small, explicitly owned routed board through
        // the native loader, retaining both locked and unlocked geometry.
        await File.WriteAllTextAsync(boardPath, """
            (kicad_pcb (version 20260206) (generator "pcbnew")
              (general (thickness 1.6)) (paper "A4")
              (layers (0 "F.Cu" signal) (2 "B.Cu" signal) (25 "Edge.Cuts" user))
              (setup (pad_to_mask_clearance 0))
              (segment (start 20 20) (end 40 20) (width 0.25) (layer "F.Cu")
                (net "POWER_RAIL") (uuid "11111111-1111-4111-8111-111111111111") (locked yes))
              (via (at 40 20) (size 0.6) (drill 0.3) (layers "F.Cu" "B.Cu")
                (net "POWER_RAIL") (uuid "22222222-2222-4222-8222-222222222222"))
              (segment (start 20 25) (end 40 25) (width 0.35) (layer "B.Cu")
                (net "OTHER") (uuid "33333333-3333-4333-8333-333333333333")))
            """, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
        Task<GetItemsResponse> Items() => client.InvokeAsync<GetItemsByNet, GetItemsResponse>(
            new() { Header = new() { Document = board } }, token);
        Task<NetsResponse> Nets(string netclass) => client.InvokeAsync<GetNets, NetsResponse>(
            new() { Board = board, NetclassFilter = { netclass } }, token);
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = schematic }, token);
        var originalItems = await Items(); Assert.HasCount(3, originalItems.Items);
        static string[] Encoded(GetItemsResponse items) => items.Items
            .Select(item => Convert.ToBase64String(item.ToByteArray())).Order(StringComparer.Ordinal).ToArray();
        var original = await Read();
        var desired = original.Data.Metadata.NetSettings.Clone();
        var power = desired.DefaultClass.Clone(); power.Name = "BoardPower"; power.Priority = 1;
        power.Board.TrackWidth = new() { ValueNm = 600000 };
        desired.Classes.Add(power);
        desired.Patterns.Add(new SchematicNetClassPattern { Pattern = "POWER_RAIL", NetClass = power.Name });
        async Task Apply(SchematicNetSettings settings)
        {
            var before = await Read(); var value = settings.Clone(); value.LabelAssignments.Clear();
            var batch = new ApplySchematicItemBatch { Document = schematic, DocumentEpoch = before.Revision.Epoch,
                ExpectedRevision = before.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"),
                Description = "Cross-editor net settings" };
            batch.Operations.Add(new SchematicItemOperation { SetNetSettings = value });
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        }
        Assert.HasCount(0, (await Nets(power.Name)).Nets);
        await Apply(desired);
        async Task Verify(bool assigned, string phase)
        {
            var nets = await Nets(power.Name);
            CollectionAssert.AreEqual(assigned ? new[] { "POWER_RAIL" } : Array.Empty<string>(),
                nets.Nets.Select(net => net.Name).ToArray());
            CollectionAssert.AreEqual(Encoded(originalItems), Encoded(await Items()),
                "A project net-class change must preserve all track/via geometry, locks and identities.");
            NativeKeyboard.SchematicShortcut(display, processId, "", "PCB Editor", false, false);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-board-net-settings-" + phase + ".png"), token);
        }
        await Verify(true, "applied");
        foreach (var (key, assigned) in new[] { ("z", false), ("y", true) })
        {
            var before = await Read();
            await FocusedSchematicShortcut(client, schematic, processId, display, key, token);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            int delay = 25;
            while ((await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = schematic }, wait.Token)).Revision.Equals(before.Revision))
            { await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 200); }
            await Verify(assigned, key);
        }
        await Apply(original.Data.Metadata.NetSettings);
        await Verify(false, "restored");
        Assert.AreEqual(original.Data, (await Read()).Data);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = board }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
        await Verify(false, "reopened");
    }
}
