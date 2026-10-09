using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    [TestMethod, TestCategory("NativePcbItems")]
    public Task NativeDifferentialPairApplyConnectsBothPadPairsThroughMcp()
        => RunNativeSessions(NativeJourney.PcbDifferentialPair);

    private static async Task<DocumentSpecifier> VerifyNativePcbDifferentialPairApply(NativeClient client,
        DocumentSpecifier currentBoard, string evidence, string instanceId, int processId,
        string display, CancellationToken token)
    {
        // Keep the shove/split fixture intact. Its deliberately incomplete
        // POWER_RAIL copper is not an acceptance board for a clean pair apply.
        string originalPath = Path.GetFullPath(Path.Combine(currentBoard.Project.Path, currentBoard.BoardFilename));
        await SaveCheckedThroughMcp(client, currentBoard, evidence, token, verifyReconnect: false);
        string fixtureDirectory = Directory.CreateDirectory(Path.Combine(currentBoard.Project.Path, "pair-apply")).FullName;
        string boardPath = Path.Combine(fixtureDirectory, "pair-apply.kicad_pcb");
        await File.WriteAllTextAsync(boardPath, DifferentialPairRouteFixture.BoardText(), token);
        await File.WriteAllTextAsync(Path.ChangeExtension(boardPath, ".kicad_pro"),
            DifferentialPairRouteFixture.ProjectText, token);
        string retained = Directory.CreateDirectory(Path.Combine(evidence, instanceId + "-differential-pair")).FullName;
        string stateDirectory = Directory.CreateTempSubdirectory("kicad-pcb-pair-apply-mcp-").FullName;
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                stateDirectory, Path.Combine(retained, "mcp.stderr.log"), token);
            RequireToolSuccess(await mcp.Tool("kicad_instance_attach",
                new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            await CloseBoard(currentBoard);
            var opened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
                toolName: "kicad_pcb_open");
            DocumentSpecifier board = opened.Document;
            var before = await ObserveLifecycleState(client, board, token);
            var padReply = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbPad } }, token);
            Assert.HasCount(4, padReply.Items, "Only the two source/destination pad pairs belong to this acceptance board.");
            var pads = padReply.Items.Select(item => item.Unpack<Pad>()).ToDictionary(pad => pad.Id.Value, StringComparer.Ordinal);
            Pad positiveStart = pads[DifferentialPairRouteFixture.PositiveStartPadId];
            Pad positiveEnd = pads[DifferentialPairRouteFixture.PositiveEndPadId];
            Pad negativeStart = pads[DifferentialPairRouteFixture.NegativeStartPadId];
            Pad negativeEnd = pads[DifferentialPairRouteFixture.NegativeEndPadId];
            Assert.AreEqual(DifferentialPairRouteFixture.PositiveNet, positiveStart.Net.Name);
            Assert.AreEqual(positiveStart.Net.Name, positiveEnd.Net.Name);
            Assert.AreEqual(DifferentialPairRouteFixture.NegativeNet, negativeStart.Net.Name);
            Assert.AreEqual(negativeStart.Net.Name, negativeEnd.Net.Name);
            Assert.IsEmpty((await ReadRoutes()).Items, "Pair copper must come entirely from the real planner.");
            CollectionAssert.AreEqual(new[] { positiveStart.Id.Value }, await ConnectedIds(positiveStart, before));
            CollectionAssert.AreEqual(new[] { negativeStart.Id.Value }, await ConnectedIds(negativeStart, before));

            var netClasses = await client.InvokeAsync<GetNetClassForNets, NetClassForNetsResponse>(new()
            { Net = { positiveStart.Net.Clone(), negativeStart.Net.Clone() } }, token);
            foreach (string net in new[] { positiveStart.Net.Name, negativeStart.Net.Name })
            {
                Assert.IsTrue(netClasses.Classes.TryGetValue(net, out var netClass));
                var settings = netClass!.Board;
                Assert.IsNotNull(settings);
                Assert.IsNotNull(settings.DiffPairTrackWidth);
                Assert.IsNotNull(settings.DiffPairGap);
                Assert.AreEqual(DifferentialPairRouteFixture.WidthNm, settings.DiffPairTrackWidth.ValueNm);
                Assert.AreEqual(DifferentialPairRouteFixture.GapNm, settings.DiffPairGap.ValueNm);
            }

            // Source pads are wider apart than the configured pair. Native
            // PNS therefore supplies the real fan-in and parallel route as a
            // multi-segment candidate. The cursor denotes the pair centerline,
            // not either destination pad. No returned segment is selected out.
            var previewRequest = new StartPcbRoutePreview
            {
                Document = board, ProcessEpoch = client.Epoch, ExpectedRevision = before.Revision.Clone(),
                OperationId = Guid.NewGuid().ToString("D"), StartItemId = positiveStart.Id.Value,
                Start = positiveStart.Position.Clone(), Layer = BoardLayer.BlFCu,
                DifferentialPair = true, ForceFinish = true, MaximumCompleteDeltaVersion = 2
            };
            previewRequest.Waypoints.Add(new Vector2
            { XNm = DifferentialPairRouteFixture.EndXNm, YNm = DifferentialPairRouteFixture.CenterYNm });
            await File.WriteAllTextAsync(Path.Combine(retained, "input.json"), JsonSerializer.Serialize(new
            {
                fixture = "isolated-two-net-native-pns", processEpoch = client.Epoch,
                boardPath, boardText = DifferentialPairRouteFixture.BoardText(), projectText = DifferentialPairRouteFixture.ProjectText,
                beforeState = SchematicJson.Formatter.Format(before),
                requestJson = SchematicJson.Formatter.Format(previewRequest),
                padsJson = BoardJson.Formatter.Format(padReply), netClassesJson = BoardJson.Formatter.Format(netClasses)
            }), token);
            var previewReply = await mcp.Tool("kicad_pcb_route_preview", new
            {
                instanceId, requestJson = SchematicJson.Formatter.Format(previewRequest),
                expectedStateJson = SchematicJson.Formatter.Format(before)
            });
            await File.WriteAllTextAsync(Path.Combine(retained, "preview.json"), previewReply.GetRawText(), token);
            RequireToolSuccess(previewReply);
            var previewData = previewReply.GetProperty("structuredContent");
            Assert.IsTrue(previewData.GetProperty("nativeRouter").GetBoolean());
            Assert.IsFalse(previewData.GetProperty("nativeCommit").GetBoolean());
            Assert.IsFalse(previewData.GetProperty("drcValidated").GetBoolean());
            var nativePreview = SchematicJson.Parser.Parse<PcbRoutePreviewState>(previewData.GetProperty("preview").GetRawText());
            Assert.AreEqual(2u, nativePreview.CompleteDeltaVersion);
            Assert.IsEmpty(nativePreview.RemovedItemIds);
            Assert.IsTrue(nativePreview.ReplacementUpdates is null || nativePreview.ReplacementUpdates.Items.Count == 0);
            Assert.IsEmpty(nativePreview.GroupChanges);
            string candidateJson = previewData.GetProperty("candidateRequestJson").GetString()!;
            var candidate = BoardJson.Parser.Parse<CreateItems>(candidateJson);
            Assert.IsTrue(candidate.Items.Count > 2, "Native fan-in must exercise the complete multi-segment apply path.");
            Assert.IsTrue(candidate.Items.All(item => item.Is(Track.Descriptor)));
            var candidateTracks = candidate.Items.Select(item => item.Unpack<Track>()).ToArray();
            CollectionAssert.AreEquivalent(new[] { positiveStart.Net.Name, negativeStart.Net.Name },
                candidateTracks.Select(track => track.Net.Name).Distinct(StringComparer.Ordinal).ToArray());
            Assert.AreEqual(candidate.Items.Count, candidateTracks.Select(track => track.Id.Value).Distinct(StringComparer.Ordinal).Count());
            Assert.IsTrue(candidateTracks.All(track => track.Layer == BoardLayer.BlFCu && track.Width.ValueNm == DifferentialPairRouteFixture.WidthNm));
            Assert.AreEqual(candidateJson, previewData.GetProperty("drcCandidateRequestJson").GetString(),
                "Detached DRC must receive every unchanged native preview segment.");
            Assert.AreEqual(before, await ObserveLifecycleState(client, board, token));

            var drcReply = await mcp.Tool("kicad_pcb_drc_start", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board), operationId = Guid.NewGuid().ToString("D"),
                expectedRevisionJson = SchematicJson.Formatter.Format(before.Revision), processEpoch = client.Epoch,
                candidateRequestJson = candidateJson, refillZones = false, reportAllTrackErrors = true, testFootprints = false
            });
            RequireToolSuccess(drcReply);
            var drc = SchematicJson.Parser.Parse<PcbDrcJobState>(drcReply.GetProperty("content")[0].GetProperty("text").GetString()!);
            using (var drcDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                drcDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                int delay = 25;
                // Always read the exact-payload receipt, even if start was
                // already terminal. Completion flags alone are insufficient.
                do
                {
                    if (!drc.WorkerFinished) await Task.Delay(delay, drcDeadline.Token);
                    drcReply = await mcp.Tool("kicad_pcb_drc_job", new
                    {
                        instanceId, documentJson = SchematicJson.Formatter.Format(board), jobId = drc.JobId,
                        processEpoch = client.Epoch, candidateRequestJson = candidateJson
                    });
                    RequireToolSuccess(drcReply);
                    drc = SchematicJson.Parser.Parse<PcbDrcJobState>(drcReply.GetProperty("content")[0].GetProperty("text").GetString()!);
                    delay = Math.Min(delay * 2, 250);
                    await File.WriteAllTextAsync(Path.Combine(retained, "drc.json"), SchematicJson.Formatter.Format(drc), token);
                } while (!drc.WorkerFinished);
            }
            Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, drc.Status, drc.ErrorMessage);
            Assert.IsTrue(drc.CandidateDryRun && drc.CandidatePayloadVerified && drc.SnapshotComplete && drc.ResultsFresh);
            Assert.IsEmpty(drc.Findings, "Every native DRC finding blocks pair apply: " + SchematicJson.Formatter.Format(drc));
            CollectionAssert.AreEqual(candidate.Items.Select(RouteIdentity).ToArray(), drc.CandidateItemIds.ToArray());
            Assert.AreEqual(before, await ObserveLifecycleState(client, board, token));

            var deletes = new DeleteItems { Header = new() { Document = board } };
            string operationId = Guid.NewGuid().ToString("D");
            var arguments = new
            {
                instanceId, deleteRequestJson = BoardJson.Formatter.Format(deletes), createRequestJson = candidateJson,
                expectedStateJson = SchematicJson.Formatter.Format(before), operationId,
                requestJson = JsonSerializer.Serialize(new { positiveNet = positiveStart.Net.Name, negativeNet = negativeStart.Net.Name }),
                drcJobStateJson = SchematicJson.Formatter.Format(drc)
            };
            var applyReply = await mcp.Tool("kicad_pcb_diff_pair_apply", arguments);
            await File.WriteAllTextAsync(Path.Combine(retained, "apply.json"), applyReply.GetRawText(), token);
            RequireToolSuccess(applyReply);
            var applyData = applyReply.GetProperty("structuredContent");
            Assert.IsTrue(applyData.GetProperty("mutationConfirmed").GetBoolean());
            Assert.IsTrue(applyData.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.IsTrue(applyData.GetProperty("drcValidated").GetBoolean());
            Assert.AreEqual("native-process", applyData.GetProperty("receiptScope").GetString());
            Assert.IsFalse(applyData.GetProperty("replayed").GetBoolean());
            var receipt = SchematicJson.Parser.Parse<CheckedPcbRouteReceipt>(applyData.GetProperty("receipt").GetRawText());
            var checkedRequest = new CheckedPcbRouteCommit
            {
                Candidate = candidate, ExpectedState = before, DrcJobId = drc.JobId, OperationId = operationId,
                MaximumResultBytes = 4 * 1024 * 1024
            };
            CheckedPcbRouteContract.ValidateResult(checkedRequest, receipt, inspect: false);
            Assert.AreEqual(CheckedPcbRouteReceipt.Types.State.Completed, receipt.State);
            Assert.AreEqual(candidate.Items.Count, receipt.Created.CreatedItems.Count);
            var ids = candidate.Items.Select(RouteIdentity).Order(StringComparer.Ordinal).ToArray();
            var nativeReadback = await ReadRoutes();
            CollectionAssert.AreEqual(ids, nativeReadback.Items.Select(RouteIdentity).Order(StringComparer.Ordinal).ToArray());
            var snapshots = nativeReadback.Items.ToDictionary(RouteIdentity, item => item.Clone(), StringComparer.Ordinal);
            foreach (var created in receipt.Created.CreatedItems)
                Assert.AreEqual(created.Item, snapshots[RouteIdentity(created.Item)], "Native receipt geometry must match readback.");
            var after = await ObserveLifecycleState(client, board, token);
            Assert.AreEqual(applyData.GetProperty("afterState").GetString(), SchematicJson.Formatter.Format(after));
            var positiveConnected = ExpectedConnections(positiveStart, positiveEnd);
            var negativeConnected = ExpectedConnections(negativeStart, negativeEnd);
            CollectionAssert.AreEqual(positiveConnected, await ConnectedIds(positiveStart, after), "Every positive segment must connect both positive pads.");
            CollectionAssert.AreEqual(negativeConnected, await ConnectedIds(negativeStart, after), "Every negative segment must connect both negative pads.");

            var replayReply = await mcp.Tool("kicad_pcb_diff_pair_apply", arguments);
            RequireToolSuccess(replayReply);
            var replayData = replayReply.GetProperty("structuredContent");
            Assert.IsTrue(replayData.GetProperty("replayed").GetBoolean());
            Assert.AreEqual(operationId, replayData.GetProperty("operationId").GetString());
            Assert.AreEqual(applyData.GetProperty("beforeState").GetString(), replayData.GetProperty("beforeState").GetString());
            Assert.AreEqual(applyData.GetProperty("afterState").GetString(), replayData.GetProperty("afterState").GetString());
            Assert.AreEqual(after, await ObserveLifecycleState(client, board, token));
            var receiptReply = await mcp.Tool("kicad_pcb_diff_pair_receipt", new
            { instanceId, expectedProcessEpoch = client.Epoch, operationId });
            RequireToolSuccess(receiptReply);
            var receiptData = receiptReply.GetProperty("structuredContent");
            Assert.IsTrue(receiptData.GetProperty("found").GetBoolean());
            Assert.AreEqual("mcp-process", receiptData.GetProperty("receiptScope").GetString(),
                "Lookup reports the MCP receipt cache containing the authoritative native receipt.");
            Assert.IsTrue(receiptData.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.AreEqual(replayData.GetRawText(), receiptData.GetProperty("receipt").GetRawText());
            await File.WriteAllTextAsync(Path.Combine(retained, "replay.json"), replayReply.GetRawText(), token);
            await File.WriteAllTextAsync(Path.Combine(retained, "receipt-lookup.json"), receiptReply.GetRawText(), token);

            NativeKeyboard.SchematicShortcut(display, processId, "z", "PCB Editor", true, true,
                clickFromLeft: 500, clickFromTop: 500);
            await WaitForRoutes(present: false);
            var undone = await ObserveLifecycleState(client, board, token);
            CollectionAssert.AreEqual(new[] { positiveStart.Id.Value }, await ConnectedIds(positiveStart, undone));
            CollectionAssert.AreEqual(new[] { negativeStart.Id.Value }, await ConnectedIds(negativeStart, undone));
            NativeKeyboard.SchematicShortcut(display, processId, "y", "PCB Editor", true, true,
                clickFromLeft: 500, clickFromTop: 500);
            await WaitForRoutes(present: true);
            var redone = await ObserveLifecycleState(client, board, token);
            CollectionAssert.AreEqual(positiveConnected, await ConnectedIds(positiveStart, redone));
            CollectionAssert.AreEqual(negativeConnected, await ConnectedIds(negativeStart, redone));

            var saveReply = await mcp.Tool("kicad_document_save", new
            { instanceId, expectedStateJson = SchematicJson.Formatter.Format(redone), operationId = Guid.NewGuid().ToString("D") });
            RequireToolSuccess(saveReply);
            await CloseBoard(board);
            board = (await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
                toolName: "kicad_pcb_open")).Document;
            var persisted = await ReadRoutes();
            CollectionAssert.AreEqual(ids, persisted.Items.Select(RouteIdentity).Order(StringComparer.Ordinal).ToArray());
            foreach (var item in persisted.Items) AssertPersistedRouteState(snapshots[RouteIdentity(item)], item);
            var persistedState = await ObserveLifecycleState(client, board, token);
            CollectionAssert.AreEqual(positiveConnected, await ConnectedIds(positiveStart, persistedState));
            CollectionAssert.AreEqual(negativeConnected, await ConnectedIds(negativeStart, persistedState));
            await File.WriteAllTextAsync(Path.Combine(retained, "verified.json"), JsonSerializer.Serialize(new
            {
                planner = "native-pns-differential-pair", completeCandidateUnchanged = true,
                candidateRequestJson = candidateJson, trackCount = ids.Length,
                drc = SchematicJson.Formatter.Format(drc), receipt = SchematicJson.Formatter.Format(receipt),
                nativeReadback = BoardJson.Formatter.Format(nativeReadback), persistedReadback = BoardJson.Formatter.Format(persisted),
                positiveEndpointPadIds = new[] { positiveStart.Id.Value, positiveEnd.Id.Value }, positiveConnected,
                negativeEndpointPadIds = new[] { negativeStart.Id.Value, negativeEnd.Id.Value }, negativeConnected,
                replayVerified = true, receiptLookupScope = "mcp-process", undoRedoVerified = true, saveReopenVerified = true
            }), token);
            await CloseBoard(board);
            return (await CreateRootThroughMcp(client.Endpoint, instanceId, originalPath, evidence, token,
                toolName: "kicad_pcb_open")).Document;

            async Task<GetItemsResponse> ReadRoutes() => await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia } }, token);

            string[] ExpectedConnections(Pad source, Pad destination) => candidateTracks
                .Where(track => track.Net.Name == source.Net.Name).Select(track => track.Id.Value)
                .Concat(new[] { source.Id.Value, destination.Id.Value }).Order(StringComparer.Ordinal).ToArray();

            async Task<string[]> ConnectedIds(Pad source, DocumentLifecycleState checkpoint)
            {
                var reply = await mcp.Tool("kicad_pcb_connected_items", new
                {
                    instanceId, expectedStateJson = SchematicJson.Formatter.Format(checkpoint),
                    requestJson = BoardJson.Formatter.Format(new GetConnectedItems
                    { Header = new() { Document = board }, Items = { source.Id.Clone() } })
                });
                RequireToolSuccess(reply);
                var data = reply.GetProperty("structuredContent");
                Assert.IsTrue(data.GetProperty("nativeQuery").GetBoolean());
                Assert.IsFalse(data.GetProperty("boardMutation").GetBoolean());
                Assert.AreEqual(SchematicJson.Formatter.Format(checkpoint), data.GetProperty("beforeState").GetString());
                Assert.AreEqual(data.GetProperty("beforeState").GetString(), data.GetProperty("afterState").GetString());
                var items = BoardJson.Parser.Parse<GetItemsResponse>(data.GetProperty("response").GetRawText());
                Assert.AreEqual(ItemRequestStatus.IrsOk, items.Status);
                Assert.IsTrue(items.Items.All(item => item.Is(Pad.Descriptor) || item.Is(Track.Descriptor)));
                Assert.IsTrue(items.Items.All(item => (item.Is(Pad.Descriptor)
                    ? item.Unpack<Pad>().Net.Name : item.Unpack<Track>().Net.Name) == source.Net.Name));
                return items.Items.Select(item => item.Is(Pad.Descriptor) ? item.Unpack<Pad>().Id.Value : RouteIdentity(item))
                    .Order(StringComparer.Ordinal).ToArray();
            }

            async Task WaitForRoutes(bool present)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                int delay = 25;
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var routes = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
                    {
                        Header = new() { Document = board },
                        Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia }
                    }, deadline.Token);
                    if (!present && routes.Items.Count == 0) return;
                    if (present && routes.Items.Count == ids.Length)
                    {
                        CollectionAssert.AreEqual(ids, routes.Items.Select(RouteIdentity).Order(StringComparer.Ordinal).ToArray());
                        foreach (var item in routes.Items) Assert.AreEqual(snapshots[RouteIdentity(item)], item);
                        return;
                    }
                    await Task.Delay(delay, deadline.Token);
                    delay = Math.Min(delay * 2, 250);
                }
            }

            async Task CloseBoard(DocumentSpecifier target)
            {
                RequireToolSuccess(await mcp.Tool("kicad_document_close", new
                {
                    instanceId, expectedStateJson = SchematicJson.Formatter.Format(await ObserveLifecycleState(client, target, token)),
                    operationId = Guid.NewGuid().ToString("D")
                }));
            }
        }
        finally { Directory.Delete(stateDirectory, true); }

        static void RequireToolSuccess(JsonElement reply) => Assert.IsFalse(
            reply.TryGetProperty("isError", out var error) && error.GetBoolean(), reply.GetRawText());
    }
}

internal static class DifferentialPairRouteFixture
{
    internal const string PositiveNet = "USB_D+", NegativeNet = "USB_D-";
    internal const long WidthNm = 250_000, GapNm = 250_000, EndXNm = 20_000_000, CenterYNm = 10_400_000;
    internal const string PositiveStartPadId = "7e57f1c5-1000-4000-8000-000000000001";
    internal const string NegativeStartPadId = "7e57f1c5-1000-4000-8000-000000000002";
    internal const string PositiveEndPadId = "7e57f1c5-1000-4000-8000-000000000003";
    internal const string NegativeEndPadId = "7e57f1c5-1000-4000-8000-000000000004";

    internal static string BoardText() => $"""
        (kicad_pcb (version 20260206) (generator "pcbnew")
          (general (thickness 1.6)) (paper "A4")
          (layers (0 "F.Cu" signal) (2 "B.Cu" signal) (25 "Edge.Cuts" user))
          (setup (pad_to_mask_clearance 0))
          (net 0 "") (net 1 "USB_D+") (net 2 "USB_D-")
          (gr_rect (start 0 0) (end 40 30) (stroke (width 0.05) (type default)) (fill none)
            (layer "Edge.Cuts") (uuid "7e57f1c5-1000-4000-8000-000000000020"))
          (footprint "DP_P_SOURCE" (layer "F.Cu") (at 10 10) (uuid "7e57f1c5-1000-4000-8000-000000000011")
            (attr smd)
            (pad "1" smd circle (at 0 0) (size 0.3 0.3) (layers "F.Cu" "F.Paste" "F.Mask")
              (net 1 "USB_D+") (uuid "{PositiveStartPadId}")))
          (footprint "DP_N_SOURCE" (layer "F.Cu") (at 10 10.8) (uuid "7e57f1c5-1000-4000-8000-000000000012")
            (attr smd)
            (pad "1" smd circle (at 0 0) (size 0.3 0.3) (layers "F.Cu" "F.Paste" "F.Mask")
              (net 2 "USB_D-") (uuid "{NegativeStartPadId}")))
          (footprint "DP_P_DESTINATION" (layer "F.Cu") (at 20 10.15) (uuid "7e57f1c5-1000-4000-8000-000000000013")
            (attr smd)
            (pad "1" smd circle (at 0 0) (size 0.3 0.3) (layers "F.Cu" "F.Paste" "F.Mask")
              (net 1 "USB_D+") (uuid "{PositiveEndPadId}")))
          (footprint "DP_N_DESTINATION" (layer "F.Cu") (at 20 10.65) (uuid "7e57f1c5-1000-4000-8000-000000000014")
            (attr smd)
            (pad "1" smd circle (at 0 0) (size 0.3 0.3) (layers "F.Cu" "F.Paste" "F.Mask")
              (net 2 "USB_D-") (uuid "{NegativeEndPadId}")))
        )
        """;

    internal const string ProjectText = """
        {
          "meta": { "filename": "pair-apply.kicad_pro", "version": 3 },
          "board": { "design_settings": {
            "rules": { "min_clearance": 0.2, "min_track_width": 0.2, "min_copper_edge_clearance": 0.5 },
            "diff_pair_dimensions": []
          } },
          "net_settings": {
            "meta": { "version": 5 },
            "classes": [{ "name": "Default", "clearance": 0.2, "track_width": 0.25,
              "diff_pair_width": 0.25, "diff_pair_gap": 0.25, "diff_pair_via_gap": 0.25,
              "via_diameter": 0.6, "via_drill": 0.3, "microvia_diameter": 0.3, "microvia_drill": 0.1,
              "bus_width": 12, "wire_width": 6, "line_style": 0, "priority": 2147483647 }],
            "netclass_patterns": [], "netclass_assignments": null
          }
        }
        """;
}
