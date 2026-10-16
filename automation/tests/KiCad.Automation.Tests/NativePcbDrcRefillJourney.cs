using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // A focused live boundary for refillZones.  The ordinary DRC journey proves
    // the complete-input and freshness contract, but previously never sent a
    // refill request through the MCP server.  Keep this fixture small so it can
    // run against the existing native executable without a native rebuild.
    private static async Task VerifyNativePcbDrcRefill(NativeClient client,
        DocumentSpecifier schematic, string evidence, string instanceId, CancellationToken token)
    {
        string boardPath = Path.Combine(schematic.Project.Path, schematic.Project.Name + ".kicad_pcb");
        Assert.IsFalse(File.Exists(boardPath), "Only this focused DRC fixture may create the board.");
        const string zoneId = "77777777-7777-4777-8777-777777777777";
        const string trackId = "88888888-8888-4888-8888-888888888888";
        const string board = """
            (kicad_pcb (version 20260206) (generator "pcbnew")
              (general (thickness 1.6))
              (paper "A4")
              (layers (0 "F.Cu" signal) (31 "B.Cu" signal) (25 "Edge.Cuts" user))
              (setup (pad_to_mask_clearance 0))
              (segment (start 20 20) (end 40 20) (width 0.25) (layer "F.Cu")
                (net 0) (uuid "88888888-8888-4888-8888-888888888888"))
              (zone (net 0) (net_name "") (layer "F.Cu") (uuid "77777777-7777-4777-8777-777777777777")
                (hatch edge 0.5)
                (connect_pads (clearance 0))
                (min_thickness 0.25)
                (fill yes (thermal_gap 0.5) (thermal_bridge_width 0.5))
                (polygon (pts (xy 10 10) (xy 50 10) (xy 50 40) (xy 10 40))))
            )
            """;
        await File.WriteAllTextAsync(boardPath, board, token);

        try
        {
            var opened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
                toolName: "kicad_pcb_open");
            var boardDocument = opened.Document;
            var before = await ObserveLifecycleState(client, boardDocument, token);
            var zone = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            {
                Header = new() { Document = boardDocument },
                Items = { new KIID { Value = zoneId } }
            }, token);
            Assert.HasCount(1, zone.Items, "The native loader must expose the seeded zone before the DRC job.");

            await using var agent = await PcbDrcAgent.StartAsync(client,
                Path.Combine(evidence, "pcb-drc-refill-mcp.stderr.log"), token);
            string operationId = Guid.NewGuid().ToString("D");
            var reply = await agent.Mcp.Tool("kicad_pcb_drc_start", new
            {
                instanceId = agent.InstanceId,
                documentJson = SchematicJson.Formatter.Format(boardDocument),
                operationId,
                refillZones = true,
                reportAllTrackErrors = true,
                testFootprints = false,
                expectedRevisionJson = SchematicJson.Formatter.Format(before.Revision),
                processEpoch = client.Epoch
            });
            Assert.IsFalse(PcbDrcAgent.Failed(reply), reply.GetRawText());
            var state = SchematicJson.Parser.Parse<PcbDrcJobState>(PcbDrcAgent.Text(reply));
            Assert.AreEqual(operationId, state.OperationId);
            Assert.AreEqual(client.Epoch, state.ProcessEpoch);
            Assert.IsFalse(state.WorkerFinished, "The MCP start reply must expose a running detached worker.");
            Assert.IsFalse(state.SnapshotComplete);
            Assert.IsFalse(state.ResultsFresh);

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromMinutes(2));
            int delay = 25;
            while (!state.WorkerFinished)
            {
                await Task.Delay(delay, wait.Token);
                delay = Math.Min(delay * 2, 500);
                state = await agent.Read(boardDocument, state, wait.Token);
            }

            Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, state.Status,
                state.ErrorCode + ": " + state.ErrorMessage);
            Assert.IsTrue(state.SnapshotComplete,
                "A completed refill job must capture every input used by its worker.");
            Assert.IsTrue(state.ResultsFresh,
                "A completed refill job must expose fresh results before a live input changes.");
            Assert.IsFalse(state.InputWarnings.Any(warning => warning.StartsWith("snapshot_incomplete: ", StringComparison.Ordinal)));
            await File.WriteAllTextAsync(Path.Combine(evidence, "pcb-drc-job-refill.json"),
                SchematicJson.Formatter.Format(state), token);

            var afterWorker = await ObserveLifecycleState(client, boardDocument, token);
            Assert.AreEqual(before.Revision, afterWorker.Revision,
                "Detached zone refill must not advance the live board revision.");
            Assert.AreEqual(before.StateSha256, afterWorker.StateSha256,
                "Detached zone refill must not mutate live board state.");
            Assert.IsFalse(afterWorker.NativeContentDirty,
                "Detached zone refill must not dirty the live editor.");

            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = boardDocument }, token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = boardDocument }, token);
            var persisted = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            {
                Header = new() { Document = boardDocument },
                Items = { new KIID { Value = zoneId }, new KIID { Value = trackId } }
            }, token);
            Assert.HasCount(2, persisted.Items);
            Assert.IsTrue(persisted.Items.Any(item => item.Is(Zone.Descriptor)
                && item.Unpack<Zone>().Id.Value == zoneId));
            Assert.IsTrue(persisted.Items.Any(item => item.Is(Track.Descriptor)
                && item.Unpack<Track>().Id.Value == trackId));
            var afterReload = await ObserveLifecycleState(client, boardDocument, token);
            Assert.IsFalse(afterReload.NativeContentDirty,
                "The refill fixture must remain clean after save and reload.");

            var metadata = new
            {
                boardPath,
                zoneId,
                trackId,
                operationId,
                jobId = state.JobId,
                processEpoch = client.Epoch,
                status = state.Status.ToString(),
                snapshotComplete = state.SnapshotComplete,
                resultsFresh = state.ResultsFresh,
                liveRevisionUnchanged = before.Revision.Equals(afterWorker.Revision),
                liveStateUnchanged = before.StateSha256 == afterWorker.StateSha256,
                zonePersistedAfterReload = persisted.Items.Any(item => item.Is(Zone.Descriptor)
                    && item.Unpack<Zone>().Id.Value == zoneId),
                trackPersistedAfterReload = persisted.Items.Any(item => item.Is(Track.Descriptor)
                    && item.Unpack<Track>().Id.Value == trackId),
                boardSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(boardPath, token))).ToLowerInvariant()
            };
            await File.WriteAllTextAsync(Path.Combine(evidence, "pcb-drc-job-refill-receipt.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), token);
        }
        finally
        {
            // The board lives below the disposable native-session directory; do
            // not remove it while KiCad still owns the open document. The outer
            // journey terminates only its fixture processes and then removes the
            // directory after all evidence is written.
        }
    }
}
