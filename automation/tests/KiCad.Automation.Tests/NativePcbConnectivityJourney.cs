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
            var afterImport = SchematicJson.Parser.Parse<DocumentLifecycleState>(importData.GetProperty("afterState").GetString()!);

            var importedPads = (await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            {
                Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbPad }
            }, token)).Items.Select(item => item.Unpack<Pad>())
                .Where(pad => pad.Net.Name == connectivityNet).ToArray();
            Assert.HasCount(2, importedPads, "The native connectivity board must have two imported pads on the named net.");

            string trackId = Guid.NewGuid().ToString("D");
            string viaId = Guid.NewGuid().ToString("D");
            string zoneId = Guid.NewGuid().ToString("D");
            var track = new Track
            {
                Id = new() { Value = trackId },
                Start = new() { XNm = 10_000_000, YNm = 10_000_000 },
                End = new() { XNm = 20_000_000, YNm = 10_000_000 },
                Width = new() { ValueNm = 250_000 },
                Layer = BoardLayer.BlFCu,
                Net = new() { Name = connectivityNet }
            };
            var via = new Via
            {
                Id = new() { Value = viaId },
                Position = new() { XNm = 20_000_000, YNm = 10_000_000 },
                Type = ViaType.VtThrough,
                Net = new() { Name = connectivityNet },
                IsFree = false,
                PadStack = new()
                {
                    Type = PadStackType.PstNormal,
                    Layers = { BoardLayer.BlFCu, BoardLayer.BlBCu },
                    Drill = new()
                    {
                        StartLayer = BoardLayer.BlFCu,
                        EndLayer = BoardLayer.BlBCu,
                        Diameter = new() { XNm = 300_000, YNm = 300_000 },
                        Shape = DrillShape.DsCircle
                    },
                    CopperLayers =
                    {
                        new PadStackLayer { Layer = BoardLayer.BlFCu, Shape = PadStackShape.PssCircle, Size = new() { XNm = 600_000, YNm = 600_000 } },
                        new PadStackLayer { Layer = BoardLayer.BlBCu, Shape = PadStackShape.PssCircle, Size = new() { XNm = 600_000, YNm = 600_000 } }
                    }
                }
            };
            var zone = new Zone
            {
                Id = new() { Value = zoneId },
                Type = ZoneType.ZtCopper,
                Outline = new PolySet
                {
                    Polygons =
                    {
                        new PolygonWithHoles
                        {
                            Outline = new PolyLine
                            {
                                Closed = true,
                                Nodes =
                                {
                                    new PolyLineNode { Point = new() { XNm = 5_000_000, YNm = 5_000_000 } },
                                    new PolyLineNode { Point = new() { XNm = 25_000_000, YNm = 5_000_000 } },
                                    new PolyLineNode { Point = new() { XNm = 25_000_000, YNm = 25_000_000 } },
                                    new PolyLineNode { Point = new() { XNm = 5_000_000, YNm = 25_000_000 } }
                                }
                            }
                        }
                    }
                },
                CopperSettings = new CopperZoneSettings
                {
                    Connection = new() { ZoneConnection = ZoneConnectionStyle.ZcsFull },
                    Clearance = new() { ValueNm = 200_000 },
                    MinThickness = new() { ValueNm = 200_000 },
                    IslandMode = IslandRemovalMode.IrmNever,
                    FillMode = ZoneFillMode.ZfmSolid,
                    Net = new() { Name = connectivityNet }
                },
                Border = new() { Style = ZoneBorderStyle.ZbsSolid, Pitch = new() { ValueNm = 1_000_000 } }
            };
            zone.Layers.Add(BoardLayer.BlFCu);

            var create = new CreateItems { Header = new() { Document = board } };
            create.Items.Add(Any.Pack(track));
            create.Items.Add(Any.Pack(via));
            create.Items.Add(Any.Pack(zone));
            var createReply = await mcp.Tool("kicad_pcb_items_create", new
            {
                instanceId,
                requestJson = BoardJson.Formatter.Format(create),
                expectedStateJson = SchematicJson.Formatter.Format(afterImport)
            });
            Assert.IsFalse(createReply.TryGetProperty("isError", out var createError) && createError.GetBoolean(), createReply.GetRawText());
            var createdData = createReply.GetProperty("structuredContent");
            var afterCreate = SchematicJson.Parser.Parse<DocumentLifecycleState>(createdData.GetProperty("afterState").GetString()!);
            Assert.IsGreaterThan(afterImport.Revision.Sequence, afterCreate.Revision.Sequence,
                "Creating named-net copper must advance the native lifecycle revision.");
            var createdResponse = BoardJson.Parser.Parse<CreateItemsResponse>(createdData.GetProperty("response").GetRawText());
            var createdTrack = createdResponse.CreatedItems.Where(item => item.Item.Is(Track.Descriptor))
                .Select(item => item.Item.Unpack<Track>()).SingleOrDefault(item => item.Id.Value == trackId);
            var createdVia = createdResponse.CreatedItems.Where(item => item.Item.Is(Via.Descriptor))
                .Select(item => item.Item.Unpack<Via>()).SingleOrDefault(item => item.Id.Value == viaId);
            var createdZone = createdResponse.CreatedItems.Where(item => item.Item.Is(Zone.Descriptor))
                .Select(item => item.Item.Unpack<Zone>()).SingleOrDefault(item => item.Id.Value == zoneId);
            Assert.IsNotNull(createdTrack, createReply.GetRawText());
            Assert.IsNotNull(createdVia, createReply.GetRawText());
            Assert.IsNotNull(createdZone, createReply.GetRawText());

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
            Assert.HasCount(importedPads.Length + 3, rows, queryReply.GetRawText());
            CollectionAssert.AreEqual(new[] { "pad", "track", "via", "zone" },
                rows.Select(row => row.GetProperty("kind").GetString()!).Distinct().ToArray(), queryReply.GetRawText());
            Assert.IsTrue(rows.Count(row => row.GetProperty("kind").GetString() == "pad") == importedPads.Length, queryReply.GetRawText());
            AssertSummary(rows.Single(row => row.GetProperty("id").GetString() == trackId), "track", trackId,
                null, connectivityNet, "BlFCu", null, null, queryReply.GetRawText());
            AssertSummary(rows.Single(row => row.GetProperty("id").GetString() == viaId), "via", viaId,
                null, connectivityNet, null, null, new[] { "BlBCu", "BlFCu" }, queryReply.GetRawText());
            AssertSummary(rows.Single(row => row.GetProperty("id").GetString() == zoneId), "zone", zoneId,
                null, connectivityNet, null, null, new[] { "BlFCu" }, queryReply.GetRawText());
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
}
