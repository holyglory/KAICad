using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // A bounded live MCP parity lane.  The normal DRC journey starts through
    // the native protobuf API; this fixture deliberately starts the same job
    // through kicad_pcb_drc_start so the public agent contract is exercised.
    private static async Task VerifyNativePcbDrcParity(NativeClient client,
        DocumentSpecifier schematic, string evidence, string instanceId, CancellationToken token)
    {
        string boardPath = Path.Combine(schematic.Project.Path, schematic.Project.Name + ".kicad_pcb");
        Assert.IsFalse(File.Exists(boardPath), "Only this focused parity fixture may create the board.");
        const string footprintA = "77777777-7777-4777-8777-777777777771";
        const string footprintB = "77777777-7777-4777-8777-777777777772";
        const string board = """
            (kicad_pcb (version 20260206) (generator "pcbnew")
              (general (thickness 1.6)) (paper "A4")
              (layers (0 "F.Cu" signal) (31 "B.Cu" signal) (25 "Edge.Cuts" user))
              (setup (pad_to_mask_clearance 0))
              (net 0 "") (net 1 "/SIGNAL")
              (gr_rect (start 50 50) (end 120 90) (stroke (width 0.05) (type default)) (fill none) (layer "Edge.Cuts") (uuid "77777777-7777-4777-8777-777777777790"))
              (segment (start 70 70) (end 100 70) (width 0.25) (layer "F.Cu") (net 1) (uuid "77777777-7777-4777-8777-777777777795"))
              (footprint "DrcJourney:DrcJourneyPart" (layer "F.Cu") (uuid "77777777-7777-4777-8777-777777777771") (at 70 70)
                (property "Reference" "TP1" (at 0 -2 0) (layer "F.SilkS") (uuid "77777777-7777-4777-8777-777777777791") (effects (font (size 1 1) (thickness 0.15))))
                (property "Value" "Probe" (at 0 2 0) (layer "F.Fab") (uuid "77777777-7777-4777-8777-777777777793") (effects (font (size 1 1) (thickness 0.15))))
                (attr smd)
                (pad "1" smd rect (at 0 0) (size 1 1) (layers "F.Cu" "F.Mask" "F.Paste") (net 1 "/SIGNAL") (uuid "77777777-7777-4777-8777-777777777781")))
              (footprint "DrcJourney:DrcJourneyPart" (layer "F.Cu") (uuid "77777777-7777-4777-8777-777777777772") (at 100 70)
                (property "Reference" "TP2" (at 0 -2 0) (layer "F.SilkS") (uuid "77777777-7777-4777-8777-777777777792") (effects (font (size 1 1) (thickness 0.15))))
                (property "Value" "Probe" (at 0 2 0) (layer "F.Fab") (uuid "77777777-7777-4777-8777-777777777794") (effects (font (size 1 1) (thickness 0.15))))
                (attr smd)
                (pad "1" smd rect (at 0 0) (size 1 1) (layers "F.Cu" "F.Mask" "F.Paste") (net 1 "/SIGNAL") (uuid "77777777-7777-4777-8777-777777777782")))
            )
            """;
        await File.WriteAllTextAsync(boardPath, board, token);
        try
        {
            var opened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
                toolName: "kicad_pcb_open");
            var boardDocument = opened.Document;
            var beforeBoard = await ObserveLifecycleState(client, boardDocument, token);
            Assert.IsFalse(beforeBoard.NativeContentDirty, "The parity board must be clean before the detached check.");
            var loaded = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            {
                Header = new() { Document = boardDocument },
                Items = { new KIID { Value = footprintA }, new KIID { Value = footprintB } }
            }, token);
            Assert.HasCount(2, loaded.Items, "The native board must expose both matching footprints.");

            var hierarchy = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
                new() { Document = schematic }, token);
            var symbols = hierarchy.Data.Instances.SelectMany(instance => instance.Items)
                .Where(item => item.Is(SchematicSymbolInstance.Descriptor))
                .Select(item => item.Unpack<SchematicSymbolInstance>()).ToArray();
            Assert.HasCount(2, symbols, "The parity schematic must expose both assigned components.");
            CollectionAssert.AreEquivalent(new[] { "TP1", "TP2" }, symbols.Select(symbol => symbol.ReferenceField.Text.Text_).ToArray());
            foreach (var symbol in symbols)
                Assert.AreEqual("DrcJourney:DrcJourneyPart", symbol.FootprintField.Text.Text_,
                    "Each schematic component must carry the board footprint assignment.");
            var netlist = await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(
                new() { Document = schematic }, token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "schematic-parity-netlist.json"),
                SchematicJson.Formatter.Format(netlist), token);
            var beforeSchematic = await ObserveLifecycleState(client, schematic, token);

            await using var agent = await PcbDrcAgent.StartAsync(client,
                Path.Combine(evidence, "pcb-drc-parity-mcp.stderr.log"), token);
            string operationId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            var request = new
            {
                instanceId = agent.InstanceId,
                documentJson = SchematicJson.Formatter.Format(boardDocument),
                operationId,
                refillZones = false,
                reportAllTrackErrors = true,
                testFootprints = true,
                expectedRevisionJson = SchematicJson.Formatter.Format(beforeBoard.Revision),
                expectedSchematicStateJson = SchematicJson.Formatter.Format(beforeSchematic),
                processEpoch = client.Epoch
            };
            await File.WriteAllTextAsync(Path.Combine(evidence, "pcb-drc-parity-request.json"),
                JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }), token);
            var reply = await agent.Mcp.Tool("kicad_pcb_drc_start", request);
            Assert.IsFalse(PcbDrcAgent.Failed(reply), reply.GetRawText());
            var state = SchematicJson.Parser.Parse<PcbDrcJobState>(PcbDrcAgent.Text(reply));
            Assert.AreEqual(operationId, state.OperationId);
            Assert.AreEqual(client.Epoch, state.ProcessEpoch);
            if (!state.WorkerFinished)
            {
                Assert.IsFalse(state.SnapshotComplete);
                Assert.IsFalse(state.ResultsFresh);
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromMinutes(2));
            int delay = 25;
            while (!state.WorkerFinished)
            {
                await Task.Delay(delay, wait.Token);
                delay = Math.Min(delay * 2, 500);
                state = await agent.Read(boardDocument, state, wait.Token);
            }
            state = await agent.Read(boardDocument, state, token);
            AssertPcbDrcJobCurrent(state, "A matching schematic/PCB parity check must complete with a complete snapshot and fresh results.");
            Assert.AreEqual(beforeSchematic, state.CheckedSchematicState);
            await File.WriteAllTextAsync(Path.Combine(evidence, "pcb-drc-parity.json"),
                SchematicJson.Formatter.Format(state), token);
            Assert.IsFalse(state.Findings.Any(finding => finding.Marker.ErrorType is
                Kiapi.Board.DrcErrorType.DrcetMissingFootprint or Kiapi.Board.DrcErrorType.DrcetExtraFootprint
                or Kiapi.Board.DrcErrorType.DrcetDuplicateFootprint or Kiapi.Board.DrcErrorType.DrcetSchematicParity
                or Kiapi.Board.DrcErrorType.DrcetSchematicFieldsParity or Kiapi.Board.DrcErrorType.DrcetFootprintFilters
                or Kiapi.Board.DrcErrorType.DrcetNetConflict),
                "A matching pair must not produce schematic-parity findings.");
            var afterBoard = await ObserveLifecycleState(client, boardDocument, token);
            var afterSchematic = await ObserveLifecycleState(client, schematic, token);
            Assert.AreEqual(beforeBoard.Revision, afterBoard.Revision, "Detached parity must not advance the PCB revision.");
            Assert.AreEqual(beforeBoard.StateSha256, afterBoard.StateSha256, "Detached parity must not mutate the PCB.");
            Assert.AreEqual(beforeSchematic.Revision, afterSchematic.Revision, "Detached parity must not advance the schematic revision.");
            Assert.AreEqual(beforeSchematic.StateSha256, afterSchematic.StateSha256, "Detached parity must not mutate the schematic.");
            var receipt = new
            {
                operationId,
                jobId = state.JobId,
                processEpoch = client.Epoch,
                testFootprints = true,
                status = state.Status.ToString(),
                snapshotComplete = state.SnapshotComplete,
                resultsFresh = state.ResultsFresh,
                checkedSchematicState = state.CheckedSchematicState,
                boardFootprints = new[] { footprintA, footprintB },
                schematicReferences = symbols.Select(symbol => symbol.ReferenceField.Text.Text_).Order(StringComparer.Ordinal).ToArray(),
                schematicAssignments = symbols.Select(symbol => symbol.FootprintField.Text.Text_).Distinct(StringComparer.Ordinal).ToArray(),
                boardRevisionUnchanged = beforeBoard.Revision.Equals(afterBoard.Revision),
                boardStateUnchanged = beforeBoard.StateSha256 == afterBoard.StateSha256,
                schematicRevisionUnchanged = beforeSchematic.Revision.Equals(afterSchematic.Revision),
                schematicStateUnchanged = beforeSchematic.StateSha256 == afterSchematic.StateSha256,
                boardSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(boardPath, token))).ToLowerInvariant()
            };
            await File.WriteAllTextAsync(Path.Combine(evidence, "pcb-drc-parity-receipt.json"),
                JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }), token);
        }
        finally
        {
            // The board belongs to the disposable native-session directory;
            // the outer journey removes it after all receipts are flushed.
        }
    }
}
