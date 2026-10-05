using Google.Protobuf;
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
    private static async Task VerifyNativePcbConnectivity(NativeClient client, DocumentSpecifier schematic,
        string evidence, string instanceId, CancellationToken token)
    {
        const string connectivityNet = "MCP_IMPORTED";
        string boardPath = Path.Combine(schematic.Project.Path, schematic.Project.Name + ".kicad_pcb");
        var boardOpen = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
            toolName: "kicad_pcb_create");
        DocumentSpecifier board = boardOpen.Document;
        await SaveCheckedThroughMcp(client, board, evidence, token, verifyReconnect: false);

        string netlistPath = Path.Combine(board.Project.Path, "connectivity.net");
        const string importNetlist = """
            (export (version "E")
              (design (source "fixture.kicad_sch") (date "2026-01-01") (tool "MCP connectivity fixture")
                (sheet (number "1") (name "/") (tstamps "/")))
              (components
                (comp (ref "R1") (value "1k") (footprint "Resistor_SMD:R_0603_1608Metric")
                  (sheetpath (names "/") (tstamps "/")) (tstamps "11111111-1111-4111-8111-111111111111"))
                (comp (ref "R2") (value "2k") (footprint "Resistor_SMD:R_0603_1608Metric")
                  (sheetpath (names "/") (tstamps "/")) (tstamps "22222222-2222-4222-8222-222222222222")))
              (nets
                (net (code "1") (name "MCP_IMPORTED")
                  (node (ref "R1") (pin "1") (pintype "passive"))
                  (node (ref "R2") (pin "1") (pintype "passive")))
                (net (code "2") (name "MCP_RETURN")
                  (node (ref "R1") (pin "2") (pintype "passive"))
                  (node (ref "R2") (pin "2") (pintype "passive")))))
            """;
        await File.WriteAllTextAsync(netlistPath, importNetlist, token);

        string stateDirectory = Directory.CreateTempSubdirectory("kicad-pcb-connectivity-mcp-").FullName;
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                stateDirectory, Path.Combine(evidence, instanceId + "-pcb-connectivity-mcp.stderr.log"), token);
            var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var attachError) && attachError.GetBoolean(), attached.GetRawText());

            var beforeImport = await ObserveLifecycleState(client, board, token);
            string importOperation = Guid.NewGuid().ToString("D");
            var importRequest = new ImportNetlist
            {
                Board = board,
                NetlistPath = Path.GetFileName(netlistPath),
                DryRun = false,
                MatchMode = NetlistMatchMode.NmmReference,
                OperationId = importOperation
            };
            var importReply = await mcp.Tool("kicad_pcb_import_netlist", new
            {
                instanceId,
                requestJson = BoardJson.Formatter.Format(importRequest),
                expectedStateJson = SchematicJson.Formatter.Format(beforeImport),
                operationId = importOperation
            });
            Assert.IsFalse(importReply.TryGetProperty("isError", out var importError) && importError.GetBoolean(), importReply.GetRawText());
            var importData = importReply.GetProperty("structuredContent");
            Assert.IsTrue(importData.GetProperty("boardMutation").GetBoolean(), importReply.GetRawText());
            DocumentLifecycleState afterImport = SchematicJson.Parser.Parse<DocumentLifecycleState>(importData.GetProperty("afterState").GetString()!);

            // Reopen the imported board before creating copper.  This is the
            // native boundary that refreshes the board connectivity index so
            // the query observes both persisted pads and the newly-created
            // route items from one net.
            var importedSave = await mcp.Tool("kicad_document_save", new
            {
                instanceId,
                expectedStateJson = SchematicJson.Formatter.Format(afterImport),
                operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsFalse(importedSave.TryGetProperty("isError", out var importedSaveError) && importedSaveError.GetBoolean(), importedSave.GetRawText());
            var savedStateJson = importedSave.GetProperty("structuredContent").GetProperty("observedState").GetRawText();
            var importedClose = await mcp.Tool("kicad_document_close", new
            {
                instanceId, expectedStateJson = savedStateJson, operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsFalse(importedClose.TryGetProperty("isError", out var importedCloseError) && importedCloseError.GetBoolean(), importedClose.GetRawText());
            var importedReopen = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
                toolName: "kicad_pcb_open");
            board = importedReopen.Document;
            afterImport = await ObserveLifecycleState(client, board, token);

            var importedPads = (await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            {
                Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbPad }
            }, token)).Items.Select(item => item.Unpack<Pad>())
                .Where(pad => pad.Net.Name == connectivityNet).ToArray();
            Assert.HasCount(2, importedPads, "The native connectivity board must have two imported pads on the named net.");

            string trackId = Guid.NewGuid().ToString("D");
            string viaId = Guid.NewGuid().ToString("D");
            Net nativeNet = importedPads[0].Net.Clone();
            var track = new Track
            {
                Id = new() { Value = trackId },
                Start = importedPads[0].Position.Clone(),
                End = new() { XNm = importedPads[0].Position.XNm + 10_000_000, YNm = importedPads[0].Position.YNm },
                Width = new() { ValueNm = 250_000 },
                Layer = BoardLayer.BlFCu,
                Net = nativeNet.Clone()
            };
            var via = new Via
            {
                Id = new() { Value = viaId },
                Position = track.End.Clone(),
                Type = ViaType.VtThrough,
                Net = nativeNet.Clone(),
                IsFree = false,
                PadStack = new()
                {
                    Type = PadStackType.PstCustom, Layers = { BoardLayer.BlFCu, BoardLayer.BlBCu },
                    Drill = new() { Diameter = new() { XNm = 300_000, YNm = 300_000 },
                        StartLayer = BoardLayer.BlFCu, EndLayer = BoardLayer.BlBCu, Shape = DrillShape.DsCircle },
                    CopperLayers =
                    {
                        new PadStackLayer { Layer = BoardLayer.BlFCu, Shape = PadStackShape.PssCircle,
                            CustomAnchorShape = PadStackShape.PssRectangle, Size = new() { XNm = 700_000, YNm = 700_000 } },
                        new PadStackLayer { Layer = BoardLayer.BlBCu, Shape = PadStackShape.PssCircle,
                            CustomAnchorShape = PadStackShape.PssRectangle, Size = new() { XNm = 700_000, YNm = 700_000 } }
                    },
                    FrontOuterLayers = new() { SolderMaskMode = SolderMaskMode.SmmMasked },
                    BackOuterLayers = new() { SolderMaskMode = SolderMaskMode.SmmUnmasked }
                }
            };

            // A mixed track/via/zone create currently rolls back with an
            // indeterminate native result.  Keep the zone summary open until
            // its native admission and refill receipt have a bounded proof.
            var trackCreate = new CreateItems { Header = new() { Document = board }, Items = { Any.Pack(track) } };
            var trackReply = await mcp.Tool("kicad_pcb_items_create", new
            {
                instanceId,
                requestJson = BoardJson.Formatter.Format(trackCreate),
                expectedStateJson = SchematicJson.Formatter.Format(afterImport)
            });
            Assert.IsFalse(trackReply.TryGetProperty("isError", out var trackError) && trackError.GetBoolean(), trackReply.GetRawText());
            var trackData = trackReply.GetProperty("structuredContent");
            var afterTrack = SchematicJson.Parser.Parse<DocumentLifecycleState>(trackData.GetProperty("afterState").GetString()!);
            Assert.IsGreaterThan(afterImport.Revision.Sequence, afterTrack.Revision.Sequence, trackReply.GetRawText());
            var createdTrackResponse = BoardJson.Parser.Parse<CreateItemsResponse>(trackData.GetProperty("response").GetRawText());
            Assert.AreEqual(ItemRequestStatus.IrsOk, createdTrackResponse.Status, trackReply.GetRawText());
            Assert.AreEqual(trackId, createdTrackResponse.CreatedItems.Single().Item.Unpack<Track>().Id.Value, trackReply.GetRawText());

            var viaCreate = new CreateItems { Header = new() { Document = board }, Items = { Any.Pack(via) } };
            var viaReply = await mcp.Tool("kicad_pcb_items_create", new
            {
                instanceId,
                requestJson = BoardJson.Formatter.Format(viaCreate),
                expectedStateJson = SchematicJson.Formatter.Format(afterTrack)
            });
            Assert.IsFalse(viaReply.TryGetProperty("isError", out var viaError) && viaError.GetBoolean(), viaReply.GetRawText());
            var viaData = viaReply.GetProperty("structuredContent");
            var afterCreate = SchematicJson.Parser.Parse<DocumentLifecycleState>(viaData.GetProperty("afterState").GetString()!);
            Assert.IsGreaterThan(afterTrack.Revision.Sequence, afterCreate.Revision.Sequence, viaReply.GetRawText());
            var createdViaResponse = BoardJson.Parser.Parse<CreateItemsResponse>(viaData.GetProperty("response").GetRawText());
            Assert.AreEqual(ItemRequestStatus.IrsOk, createdViaResponse.Status, viaReply.GetRawText());
            Assert.AreEqual(viaId, createdViaResponse.CreatedItems.Single().Item.Unpack<Via>().Id.Value, viaReply.GetRawText());

            var createdRouteItems = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            {
                Header = new() { Document = board }, Items = { track.Id, via.Id }
            }, token);
            var liveTrack = createdRouteItems.Items.Single(item => item.Is(Track.Descriptor)).Unpack<Track>();
            var liveVia = createdRouteItems.Items.Single(item => item.Is(Via.Descriptor)).Unpack<Via>();

            string expectedStateJson = SchematicJson.Formatter.Format(afterCreate);
            string documentJson = SchematicJson.Formatter.Format(board);
            var queryReply = await mcp.Tool("kicad_pcb_connectivity_query", new
            {
                instanceId, documentJson, netName = connectivityNet, expectedStateJson
            });
            Assert.IsFalse(queryReply.TryGetProperty("isError", out var queryError) && queryError.GetBoolean(), queryReply.GetRawText());
            var queryData = queryReply.GetProperty("structuredContent");
            Assert.AreEqual(instanceId, queryData.GetProperty("instanceId").GetString(), queryReply.GetRawText());
            Assert.AreEqual(client.Epoch, queryData.GetProperty("processEpoch").GetString(), queryReply.GetRawText());
            Assert.AreEqual(connectivityNet, queryData.GetProperty("netName").GetString(), queryReply.GetRawText());
            Assert.IsTrue(queryData.GetProperty("nativeQuery").GetBoolean(), queryReply.GetRawText());
            Assert.IsFalse(queryData.GetProperty("boardMutation").GetBoolean(), queryReply.GetRawText());
            Assert.IsFalse(queryData.GetProperty("drcValidated").GetBoolean(), queryReply.GetRawText());
            Assert.AreEqual(expectedStateJson, queryData.GetProperty("beforeState").GetString(), queryReply.GetRawText());
            Assert.AreEqual(expectedStateJson, queryData.GetProperty("afterState").GetString(), queryReply.GetRawText());
            Assert.AreEqual(afterCreate, await ObserveLifecycleState(client, board, token),
                "A successful connectivity read must leave the exact native lifecycle state unchanged.");

            JsonElement[] rows = queryData.GetProperty("items").EnumerateArray().ToArray();
            Assert.AreEqual(rows.Length, queryData.GetProperty("itemCount").GetInt32(), queryReply.GetRawText());
            Assert.HasCount(importedPads.Length + 2, rows, queryReply.GetRawText());
            CollectionAssert.AreEqual(new[] { "pad", "track", "via" },
                rows.Select(row => row.GetProperty("kind").GetString()!).Distinct().ToArray(), queryReply.GetRawText());
            Assert.IsTrue(rows.Count(row => row.GetProperty("kind").GetString() == "pad") == importedPads.Length, queryReply.GetRawText());
            AssertSummary(rows.Single(row => row.GetProperty("id").GetString() == trackId), "track", trackId,
                CanonicalParent(liveTrack.Parent), connectivityNet, liveTrack.Layer.ToString(), null, null, queryReply.GetRawText());
            AssertSummary(rows.Single(row => row.GetProperty("id").GetString() == viaId), "via", viaId,
                CanonicalParent(liveVia.Parent), connectivityNet, null, null,
                liveVia.PadStack.Layers.Select(layer => layer.ToString()).Order(StringComparer.Ordinal).ToArray(), queryReply.GetRawText());
            foreach (Pad pad in importedPads)
            {
                string id = Guid.ParseExact(pad.Id.Value, "D").ToString("D");
                JsonElement row = rows.Single(item => item.GetProperty("id").GetString() == id);
                string? parent = string.IsNullOrWhiteSpace(pad.Parent?.Value) ? null : Guid.ParseExact(pad.Parent.Value, "D").ToString("D");
                AssertSummary(row, "pad", id, parent, connectivityNet, null, pad.Number, null, queryReply.GetRawText());
            }

            var sibling = await SiblingInstance(client, token);
            var siblingAttach = await mcp.Tool("kicad_instance_attach", new
            { endpoint = sibling.Endpoint, expectedInstanceId = sibling.InstanceId });
            Assert.IsFalse(siblingAttach.TryGetProperty("isError", out var siblingAttachError) && siblingAttachError.GetBoolean(), siblingAttach.GetRawText());
            var wrongInstance = await mcp.Tool("kicad_pcb_connectivity_query", new
            {
                instanceId = sibling.InstanceId, documentJson, netName = connectivityNet, expectedStateJson
            });
            Assert.IsTrue(wrongInstance.TryGetProperty("isError", out var wrongInstanceError) && wrongInstanceError.GetBoolean(), wrongInstance.GetRawText());
            Assert.AreEqual("pcb_instance_changed", wrongInstance.GetProperty("structuredContent").GetProperty("errorCode").GetString(), wrongInstance.GetRawText());
            Assert.AreEqual(afterCreate, await ObserveLifecycleState(client, board, token),
                "A wrong-instance refusal must not mutate the original board.");

            var reattach = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(reattach.TryGetProperty("isError", out var reattachError) && reattachError.GetBoolean(), reattach.GetRawText());
            var staleTrack = track.Clone();
            staleTrack.Id = new() { Value = Guid.NewGuid().ToString("D") };
            staleTrack.Start.XNm += 2_000_000;
            staleTrack.End.XNm += 2_000_000;
            var staleCreate = new CreateItems { Header = new() { Document = board } };
            staleCreate.Items.Add(Any.Pack(staleTrack));
            var mutation = await mcp.Tool("kicad_pcb_items_create", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(staleCreate), expectedStateJson
            });
            Assert.IsFalse(mutation.TryGetProperty("isError", out var mutationError) && mutationError.GetBoolean(), mutation.GetRawText());
            var afterMutation = SchematicJson.Parser.Parse<DocumentLifecycleState>(mutation.GetProperty("structuredContent").GetProperty("afterState").GetString()!);
            Assert.IsGreaterThan(afterCreate.Revision.Sequence, afterMutation.Revision.Sequence, mutation.GetRawText());
            var staleQuery = await mcp.Tool("kicad_pcb_connectivity_query", new
            {
                instanceId, documentJson, netName = connectivityNet, expectedStateJson
            });
            Assert.IsTrue(staleQuery.TryGetProperty("isError", out var staleError) && staleError.GetBoolean(), staleQuery.GetRawText());
            Assert.AreEqual("pcb_connectivity_state_changed", staleQuery.GetProperty("structuredContent").GetProperty("errorCode").GetString(), staleQuery.GetRawText());
            Assert.AreEqual(afterMutation, await ObserveLifecycleState(client, board, token),
                "A stale lifecycle refusal must not mutate the board after the independent edit.");
        }
        finally
        {
            Directory.Delete(stateDirectory, true);
        }
    }

    private static void AssertSummary(JsonElement row, string kind, string id, string? parentId, string netName,
        string? layer, string? padNumber, string[]? layers, string context)
    {
        Assert.AreEqual(kind, row.GetProperty("kind").GetString(), context);
        Assert.AreEqual(id, row.GetProperty("id").GetString(), context);
        AssertNullableString(row.GetProperty("parentId"), parentId, context);
        Assert.AreEqual(netName, row.GetProperty("netName").GetString(), context);
        AssertNullableString(row.GetProperty("layer"), layer, context);
        AssertNullableString(row.GetProperty("padNumber"), padNumber, context);
        JsonElement layersValue = row.GetProperty("layers");
        if (layers is null)
            Assert.AreEqual(JsonValueKind.Null, layersValue.ValueKind, context);
        else
            CollectionAssert.AreEqual(layers, layersValue.EnumerateArray().Select(value => value.GetString()!).ToArray(), context);
    }

    private static void AssertNullableString(JsonElement value, string? expected, string context)
    {
        if (expected is null)
            Assert.AreEqual(JsonValueKind.Null, value.ValueKind, context);
        else
            Assert.AreEqual(expected, value.GetString(), context);
    }

    private static string? CanonicalParent(KIID? value) => string.IsNullOrWhiteSpace(value?.Value)
        ? null : Guid.ParseExact(value.Value, "D").ToString("D");
}
