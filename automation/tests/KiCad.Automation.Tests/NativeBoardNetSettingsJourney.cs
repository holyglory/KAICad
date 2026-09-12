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
        var untouched = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = schematic }, token);
        async Task Reject(string phase)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var failure = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<OpenDocument, OpenDocumentResponse>(open, limit.Token));
                Assert.AreEqual(3, failure.Status, phase);
                Assert.AreEqual(untouched, await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                    new() { Document = schematic }, limit.Token), "Rejected board opening must preserve the schematic.");
            }
            catch
            {
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence,
                    processId + "-board-open-" + phase + "-failure.png"), CancellationToken.None);
                throw;
            }
        }
        foreach (var (phase, source) in new[]
        {
            ("future-format", "(kicad_pcb (version 99999999) (generator pcbnew))"),
            ("malformed", "(kicad_pcb (version 20260206) (unexpected_automation_fixture_node 1))")
        })
        {
            await File.WriteAllTextAsync(boardPath, source, token);
            await Reject(phase);
            Assert.AreEqual(source, await File.ReadAllTextAsync(boardPath, token));
            File.Delete(boardPath); // Only the deliberately invalid private fixture just written above.
        }
        string lockPath = Path.Combine(Path.GetDirectoryName(boardPath)!, "~" + Path.GetFileName(boardPath) + ".lck");
        const string foreignLock = """{"username":"fixture-other-user","hostname":"fixture-other-host"}""";
        await File.WriteAllTextAsync(lockPath, foreignLock, token);
        await Reject("locked");
        Assert.AreEqual(foreignLock, await File.ReadAllTextAsync(lockPath, token));
        Assert.IsFalse(File.Exists(boardPath));
        File.Delete(lockPath); // Release this test's simulated foreign lock, never a real editor lock.
        string autosave = Path.Combine(Path.GetDirectoryName(boardPath)!, "_autosave-" + Path.GetFileName(boardPath));
        const string recovered = "(kicad_pcb (version 20260206) (generator pcbnew))";
        await File.WriteAllTextAsync(autosave, recovered, token);
        await Reject("recovery-needed");
        Assert.AreEqual(recovered, await File.ReadAllTextAsync(autosave, token));
        Assert.IsFalse(File.Exists(boardPath));
        File.Delete(autosave); // The isolated recovery input is no longer needed.
        string instanceId = (await client.HandshakeAsync(token)).InstanceId;
        var opened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
            toolName: "kicad_pcb_create");
        var board = opened.Document;
        Assert.AreEqual((DocumentType)3, board.Type);
        Assert.AreEqual(Path.GetFileName(boardPath), board.BoardFilename);
        Assert.AreEqual(schematic.Project, board.Project);
        Assert.IsFalse(File.Exists(boardPath), "Explicit native creation remains unsaved.");
        Assert.AreEqual(opened, await client.InvokeAsync<OpenDocument, OpenDocumentResponse>(open, token));
        foreach (int kind in Enumerable.Range(0, 3))
        {
            var wrong = board.Clone();
            switch (kind)
            {
                case 0: wrong.Project = null; break;
                case 1: wrong.Project.Name = "other-project"; break;
                case 2: wrong.Project.Path += "other-directory/"; break;
            }
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<GetNets, NetsResponse>(new() { Board = wrong }, token))).Status);
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = board }, token);
        Assert.IsTrue(File.Exists(boardPath));
        Assert.AreEqual(opened, await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
            toolName: "kicad_pcb_open"));

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
            new() { Header = new() { Document = board },
                Nets = { new Kiapi.Board.Types.Net { Name = "POWER_RAIL" },
                    new Kiapi.Board.Types.Net { Name = "OTHER" } } }, token);
        Task<NetsResponse> Nets(string netclass) => client.InvokeAsync<GetNets, NetsResponse>(
            new() { Board = board, NetclassFilter = { netclass } }, token);
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = schematic }, token);
        var loadedNets = await client.InvokeAsync<GetNets, NetsResponse>(new() { Board = board }, token);
        CollectionAssert.AreEqual(new[] { "OTHER", "POWER_RAIL" },
            loadedNets.Nets.Select(net => net.Name).Where(name => name.Length != 0).Order(StringComparer.Ordinal).ToArray());
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
            var currentItems = await Items();
            if (!Encoded(originalItems).SequenceEqual(Encoded(currentItems)))
            {
                await File.WriteAllBytesAsync(Path.Combine(evidence, processId + "-board-" + phase + "-expected.pb"), originalItems.ToByteArray(), token);
                await File.WriteAllBytesAsync(Path.Combine(evidence, processId + "-board-" + phase + "-actual.pb"), currentItems.ToByteArray(), token);
            }
            var expectedItems = phase == "reopened"
                ? NativeBoardReloadComparison.RebindRuntimeContainer(originalItems, currentItems) : originalItems;
            CollectionAssert.AreEqual(Encoded(expectedItems), Encoded(currentItems),
                "A project net-class change must preserve all track/via geometry, locks and identities.");
            // A presence query does not raise the window after schematic Undo.
            // Motion raises this exact owned PCB window without clicking/editing.
            NativeKeyboard.SchematicShortcut(display, processId, "motion", "PCB Editor", false, false);
            await NativeSetupUi.StableGeometry(display, processId, token, "PCB Editor");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-board-net-settings-" + phase + ".png"), token);
            CollectionAssert.AreEqual(Encoded(currentItems), Encoded(await Items()),
                "Bringing the PCB into view must not change any native design field.");
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
        Assert.AreEqual(board, (await client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
            new() { Type = (DocumentType)3 }, token)).Documents.Single());
    }
}
