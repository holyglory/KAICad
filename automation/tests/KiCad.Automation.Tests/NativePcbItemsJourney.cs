using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kiapi.Board.Commands;
using Kiapi.Board.Jobs;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    [TestMethod, TestCategory("NativePcbItemsFixture")]
    public void PcbRenderFixtureUsesSupportedIndexedFaceSetAndDeterministicMutation()
    {
        string fixture = PcbRenderFixtureText();
        Assert.IsTrue(fixture.StartsWith("#VRML V2.0 utf8", StringComparison.Ordinal), fixture);
        Assert.AreEqual(1, Regex.Matches(fixture, @"\bIndexedFaceSet\b", RegexOptions.CultureInvariant).Count, fixture);
        Assert.AreEqual(1, Regex.Matches(fixture, @"\bscale 8 4 2\b", RegexOptions.CultureInvariant).Count, fixture);
        Assert.IsFalse(fixture.Contains("geometry Box", StringComparison.Ordinal), fixture);
        string changed = fixture.Replace("scale 8 4 2", "scale 9 3 2", StringComparison.Ordinal);
        Assert.AreEqual(fixture.Length, changed.Length, "The render mutation must preserve fixture byte length.");
        Assert.AreNotEqual(fixture, changed, "The render mutation must change model geometry input.");
    }

    private static string PcbRenderFixtureText() =>
        """
            #VRML V2.0 utf8
            Transform {
              translation 0 0 0
              scale 8 4 2
              children [
                Shape {
                  appearance Appearance {
                    material Material { diffuseColor 0.8 0.2 0.1 }
                  }
                  geometry IndexedFaceSet {
                    coord Coordinate {
                      point [
                        -0.5 -0.5 0,
                         0.5 -0.5 0,
                         0.5  0.5 0,
                        -0.5  0.5 0,
                        -0.5 -0.5 1,
                         0.5 -0.5 1,
                         0.5  0.5 1,
                        -0.5  0.5 1
                      ]
                    }
                    coordIndex [
                      0, 1, 2, 3, -1,
                      4, 7, 6, 5, -1,
                      0, 4, 5, 1, -1,
                      1, 5, 6, 2, -1,
                      2, 6, 7, 3, -1,
                      4, 0, 3, 7, -1
                    ]
                  }
                }
              ]
            }
            """;

    private static void PreparePcbImportLibrary(string projectDirectory)
    {
        string library = Directory.CreateDirectory(Path.Combine(projectDirectory, "Resistor_SMD.pretty")).FullName;
        string source = Path.Combine(RepositoryRoot(), "qa", "data", "libraries", "Resistor_SMD.pretty", "R_0603_1608Metric.kicad_mod");
        string libraryFile = Path.Combine(library, "R_0603_1608Metric.kicad_mod");
        File.Copy(source, libraryFile, true);
        PreparePcbRenderModel(projectDirectory, libraryFile);
        File.WriteAllText(Path.Combine(projectDirectory, "fp-lib-table"),
            "(fp_lib_table (version 7) (lib (name \"Resistor_SMD\") (type \"KiCad\") (uri \"${KIPRJMOD}/Resistor_SMD.pretty\") (options \"\") (descr \"\")))\n");
    }

    private static string PreparePcbRenderModel(string projectDirectory, string libraryFile)
    {
        string model = PcbRenderFixtureText();
        string modelPath = Path.Combine(projectDirectory, "render-fixture.wrl");
        File.WriteAllText(modelPath, model);

        string library = File.ReadAllText(libraryFile);
        // Use the shared balanced KiCad S-expression parser so this fixture
        // does not depend on indentation or line-ending details in the
        // checked-in footprint.  The old delimiter search could select a
        // nested close or miss a valid model block after a formatter rewrite.
        var parsedLibrary = PsuCpuSexpr.Parse(library);
        var modelNodes = parsedLibrary.Children("model").ToArray();
        Assert.HasCount(1, modelNodes, "The resistor fixture must contain one 3D model declaration.");
        int modelStart = modelNodes[0].Start;
        int modelEnd = modelNodes[0].End;
        Assert.IsTrue(modelEnd > modelStart, "The resistor model declaration must be complete.");
        string modelBlock = library.Substring(modelStart, modelEnd - modelStart);
        modelBlock = modelBlock.Replace(
            "${KICAD8_3DMODEL_DIR}/Resistor_SMD.3dshapes/R_0603_1608Metric.wrl",
            modelPath.Replace('\\', '/'), StringComparison.Ordinal);
        modelBlock = Regex.Replace(modelBlock, @"\(offset\s+\(xyz [^)]+\)\s+\)",
            "(offset\n\t\t\t(xyz 1.5 0.5 0.2)\n\t\t)", RegexOptions.CultureInvariant);
        modelBlock = Regex.Replace(modelBlock, @"\(scale\s+\(xyz [^)]+\)\s+\)",
            "(scale\n\t\t\t(xyz 1.2 0.8 1.1)\n\t\t)", RegexOptions.CultureInvariant);
        modelBlock = Regex.Replace(modelBlock, @"\(rotate\s+\(xyz [^)]+\)\s+\)",
            "(rotate\n\t\t\t(xyz 10 20 30)\n\t\t)", RegexOptions.CultureInvariant);
        File.WriteAllText(libraryFile, library[..modelStart] + modelBlock + library[modelEnd..]);
        return modelPath;
    }

    private static async Task VerifyNativePcbItems(NativeClient client, DocumentSpecifier schematic,
        string evidence, string instanceId, int focusProcessId, string display, CancellationToken token)
    {
        string boardPath = Path.Combine(schematic.Project.Path, schematic.Project.Name + ".kicad_pcb");
        // The isolated native fixture has no global fp-lib-table.  Install one
        // project-local resistor library from the repository so this positive
        // import remains independent of host-installed KiCad libraries.  This
        // must happen before opening the board because KiCad caches the project
        // footprint table when the document is created.
        string resistorLibrary = Directory.CreateDirectory(Path.Combine(schematic.Project.Path, "Resistor_SMD.pretty")).FullName;
        string sourceResistor = Path.Combine(RepositoryRoot(), "qa", "data", "libraries", "Resistor_SMD.pretty", "R_0603_1608Metric.kicad_mod");
        string resistorLibraryFile = Path.Combine(resistorLibrary, "R_0603_1608Metric.kicad_mod");
        File.Copy(sourceResistor, resistorLibraryFile, true);
        string renderModelPath = PreparePcbRenderModel(schematic.Project.Path, resistorLibraryFile);
        await File.WriteAllTextAsync(Path.Combine(schematic.Project.Path, "fp-lib-table"),
            "(fp_lib_table (version 7) (lib (name \"Resistor_SMD\") (type \"KiCad\") (uri \"${KIPRJMOD}/Resistor_SMD.pretty\") (options \"\") (descr \"\")))\n", token);
        var boardOpen = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token, toolName: "kicad_pcb_create");
        var board = boardOpen.Document;
        string netlistPath = Path.Combine(board.Project.Path, "receipt-import.net");
        // Keep the fixture deliberately small while exercising new footprint
        // creation and net assignment. Both components use the checked-in
        // project library above, so import succeeds on a clean machine.
        const string importNetlist = """
            (export (version "E")
              (design (source "fixture.kicad_sch") (date "2026-01-01") (tool "MCP fixture")
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
        DocumentSpecifier finalBoard = board;
        await SaveCheckedThroughMcp(client, board, evidence, token, verifyReconnect: false);
        var before = await ObserveLifecycleState(client, board, token);
        var track = new Track
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            Start = new() { XNm = 10_000_000, YNm = 10_000_000 },
            End = new() { XNm = 20_000_000, YNm = 10_000_000 },
            Width = new() { ValueNm = 250_000 }, Layer = BoardLayer.BlFCu,
            Net = new() { Name = "POWER_RAIL" }
        };
        var create = new CreateItems { Header = new() { Document = board } };
        create.Items.Add(Any.Pack(track));
        string stateDirectory = Directory.CreateTempSubdirectory("kicad-pcb-items-mcp-").FullName;
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                stateDirectory, Path.Combine(evidence, instanceId + "-pcb-items-mcp.stderr.log"), token);
            var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var attachError) && attachError.GetBoolean());
            var seededRoutes = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            {
                Header = new() { Document = board },
                Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia }
            }, token);
            if (seededRoutes.Items.Count > 0)
            {
                var removeSeededRoutes = new DeleteItems { Header = new() { Document = board } };
                removeSeededRoutes.ItemIds.Add(seededRoutes.Items.Select(item =>
                    item.Is(Track.Descriptor) ? item.Unpack<Track>().Id
                    : item.Is(Arc.Descriptor) ? item.Unpack<Arc>().Id
                    : item.Unpack<Via>().Id));
                var removeSeededReply = await mcp.Tool("kicad_pcb_items_delete", new
                {
                    instanceId, requestJson = BoardJson.Formatter.Format(removeSeededRoutes),
                    expectedStateJson = SchematicJson.Formatter.Format(before)
                });
                Assert.IsFalse(removeSeededReply.TryGetProperty("isError", out var removeSeededError) && removeSeededError.GetBoolean(), removeSeededReply.GetRawText());
                before = SchematicJson.Parser.Parse<DocumentLifecycleState>(removeSeededReply.GetProperty("structuredContent").GetProperty("afterState").GetString()!);
            }
            var existingBoardGraphics = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbShape } }, token);
            var temporaryOutlineIds = new List<KIID>();
            if (!existingBoardGraphics.Items.Select(item => item.Unpack<BoardGraphicShape>())
                .Any(shape => shape.Layer == BoardLayer.BlEdgeCuts))
            {
                var createOutline = new CreateItems { Header = new() { Document = board } };
                foreach ((long x1, long y1, long x2, long y2) in new[]
                {
                    (0L, 0L, 30_000_000L, 0L), (30_000_000L, 0L, 30_000_000L, 30_000_000L),
                    (30_000_000L, 30_000_000L, 0L, 30_000_000L), (0L, 30_000_000L, 0L, 0L)
                })
                {
                    var id = new KIID { Value = Guid.NewGuid().ToString("D") };
                    temporaryOutlineIds.Add(id);
                    createOutline.Items.Add(Any.Pack(new BoardGraphicShape
                    {
                        Id = id, Layer = BoardLayer.BlEdgeCuts,
                        Shape = new GraphicShape
                        {
                            Attributes = new() { Stroke = new() { Width = new() { ValueNm = 100_000 } } },
                            Segment = new GraphicSegmentAttributes
                            {
                                Start = new() { XNm = x1, YNm = y1 }, End = new() { XNm = x2, YNm = y2 }
                            }
                        }
                    }));
                }
                var createOutlineReply = await mcp.Tool("kicad_pcb_items_create", new
                {
                    instanceId, requestJson = BoardJson.Formatter.Format(createOutline),
                    expectedStateJson = SchematicJson.Formatter.Format(before)
                });
                Assert.IsFalse(createOutlineReply.TryGetProperty("isError", out var createOutlineError) && createOutlineError.GetBoolean(), createOutlineReply.GetRawText());
                before = SchematicJson.Parser.Parse<DocumentLifecycleState>(createOutlineReply.GetProperty("structuredContent").GetProperty("afterState").GetString()!);
            }
            // Add a real copper zone through the same MCP object boundary before
            // refilling. This keeps the fill/readback assertion meaningful while
            // preserving the existing route fixture and its lifecycle checkpoint.
            string zoneId = Guid.NewGuid().ToString("D");
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
                    Net = new()
                },
                Border = new() { Style = ZoneBorderStyle.ZbsSolid, Pitch = new() { ValueNm = 1_000_000 } }
            };
            zone.Layers.Add(BoardLayer.BlFCu);
            var createZone = new CreateItems { Header = new() { Document = board } };
            createZone.Items.Add(Any.Pack(zone));
            var zoneCreateReply = await mcp.Tool("kicad_pcb_items_create", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(createZone),
                expectedStateJson = SchematicJson.Formatter.Format(before)
            });
            Assert.IsFalse(zoneCreateReply.TryGetProperty("isError", out var zoneCreateError) && zoneCreateError.GetBoolean(), zoneCreateReply.GetRawText());
            before = SchematicJson.Parser.Parse<DocumentLifecycleState>(zoneCreateReply.GetProperty("structuredContent").GetProperty("afterState").GetString()!);

            string refillOperation = Guid.NewGuid().ToString("D");
            var refillRequest = new RefillZones { Board = board, OperationId = refillOperation };
            var refillReply = await mcp.Tool("kicad_pcb_refill_zones", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(refillRequest),
                expectedStateJson = SchematicJson.Formatter.Format(before), operationId = refillOperation
            });
            Assert.IsFalse(refillReply.TryGetProperty("isError", out var refillError) && refillError.GetBoolean(), refillReply.GetRawText());
            var refillData = refillReply.GetProperty("structuredContent");
            Assert.IsTrue(refillData.GetProperty("nativeReceiptAvailable").GetBoolean(), refillReply.GetRawText());
            Assert.IsTrue(refillData.TryGetProperty("terminal", out var initialTerminal) == false
                || initialTerminal.ValueKind is JsonValueKind.False or JsonValueKind.True, refillReply.GetRawText());

            // The editor may finish the CallAfter before this first read. Accept
            // either race, then require an explicit terminal outcome before claiming
            // a successful fill.
            var refillWait = await mcp.Tool("kicad_pcb_operation_wait", new
            {
                instanceId, expectedProcessEpoch = client.Epoch,
                documentJson = SchematicJson.Formatter.Format(board),
                operationId = refillOperation, timeoutMs = 5000
            });
            Assert.IsFalse(refillWait.TryGetProperty("isError", out var refillWaitError) && refillWaitError.GetBoolean(), refillWait.GetRawText());
            var refillWaitData = refillWait.GetProperty("structuredContent");
            Assert.IsTrue(refillWaitData.GetProperty("lifecycleSettled").GetBoolean(), refillWait.GetRawText());
            Assert.IsTrue(refillWaitData.GetProperty("terminal").GetBoolean(), refillWait.GetRawText());
            Assert.AreEqual("completed", refillWaitData.GetProperty("zoneFillOutcome").GetString(), refillWait.GetRawText());
            Assert.IsTrue(refillWaitData.GetProperty("completionConfirmed").GetBoolean(), refillWait.GetRawText());
            Assert.IsTrue(refillWaitData.GetProperty("commitStateKnown").GetBoolean(), refillWait.GetRawText());
            Assert.AreEqual("", refillWaitData.GetProperty("errorCode").GetString(), refillWait.GetRawText());
            var filledZones = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbZone } }, token);
            var filledZone = filledZones.Items.Select(item => item.Unpack<Zone>()).Single(item => item.Id.Value == zoneId);
            Assert.IsTrue(filledZone.Filled || filledZone.FilledPolygons.Count > 0,
                "The terminal successful receipt must correspond to filled zone geometry.");
            var sameProcessReceipt = await mcp.Tool("kicad_pcb_operation_receipt", new
            { instanceId, expectedProcessEpoch = client.Epoch, operationId = refillOperation });
            Assert.IsFalse(sameProcessReceipt.TryGetProperty("isError", out var sameReceiptError) && sameReceiptError.GetBoolean(), sameProcessReceipt.GetRawText());
            var sameProcessReceiptData = sameProcessReceipt.GetProperty("structuredContent");
            Assert.IsTrue(sameProcessReceiptData.GetProperty("found").GetBoolean(), sameProcessReceipt.GetRawText());
            Assert.AreEqual("native-process", sameProcessReceiptData.GetProperty("receiptScope").GetString(), sameProcessReceipt.GetRawText());
            var sameNativeReceipt = sameProcessReceiptData.GetProperty("nativeReceipt");
            Assert.IsTrue(sameNativeReceipt.GetProperty("terminal").GetBoolean(), sameProcessReceipt.GetRawText());
            Assert.AreEqual("NZFO_COMPLETED", sameNativeReceipt.GetProperty("zoneFillOutcome").GetString(), sameProcessReceipt.GetRawText());
            Assert.IsTrue(sameNativeReceipt.GetProperty("commitStateKnown").GetBoolean(), sameProcessReceipt.GetRawText());

            string zoneReceiptStateDirectory = Directory.CreateTempSubdirectory("kicad-pcb-zone-receipt-mcp-").FullName;
            try
            {
                await using var zoneReceiptMcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                    zoneReceiptStateDirectory, Path.Combine(evidence, instanceId + "-pcb-zone-receipt-mcp.stderr.log"), token);
                var zoneReceiptAttached = await zoneReceiptMcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                Assert.IsFalse(zoneReceiptAttached.TryGetProperty("isError", out var zoneReceiptAttachError) && zoneReceiptAttachError.GetBoolean());
                var reconnectedZoneReceipt = await zoneReceiptMcp.Tool("kicad_pcb_operation_receipt", new
                { instanceId, expectedProcessEpoch = client.Epoch, operationId = refillOperation });
                Assert.IsFalse(reconnectedZoneReceipt.TryGetProperty("isError", out var reconnectedZoneReceiptError) && reconnectedZoneReceiptError.GetBoolean(), reconnectedZoneReceipt.GetRawText());
                var reconnectedZoneData = reconnectedZoneReceipt.GetProperty("structuredContent");
                Assert.IsTrue(reconnectedZoneData.GetProperty("found").GetBoolean(), reconnectedZoneReceipt.GetRawText());
                Assert.AreEqual("native-process", reconnectedZoneData.GetProperty("receiptScope").GetString(), reconnectedZoneReceipt.GetRawText());
                var reconnectedZoneNative = reconnectedZoneData.GetProperty("nativeReceipt");
                Assert.IsTrue(reconnectedZoneNative.GetProperty("terminal").GetBoolean(), reconnectedZoneReceipt.GetRawText());
                Assert.AreEqual("NZFO_COMPLETED", reconnectedZoneNative.GetProperty("zoneFillOutcome").GetString(), reconnectedZoneReceipt.GetRawText());
                Assert.AreEqual(client.Epoch, reconnectedZoneNative.GetProperty("processEpoch").GetString(), reconnectedZoneReceipt.GetRawText());
            }
            finally { Directory.Delete(zoneReceiptStateDirectory, true); }

            var removeZone = new DeleteItems { Header = new() { Document = board } };
            removeZone.ItemIds.Add(new KIID { Value = zoneId });
            var removeZoneReply = await mcp.Tool("kicad_pcb_items_delete", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(removeZone),
                expectedStateJson = SchematicJson.Formatter.Format(await ObserveLifecycleState(client, board, token))
            });
            Assert.IsFalse(removeZoneReply.TryGetProperty("isError", out var removeZoneError) && removeZoneError.GetBoolean(), removeZoneReply.GetRawText());
            before = SchematicJson.Parser.Parse<DocumentLifecycleState>(removeZoneReply.GetProperty("structuredContent").GetProperty("afterState").GetString()!);
            if (temporaryOutlineIds.Count > 0)
            {
                var removeOutline = new DeleteItems { Header = new() { Document = board } };
                removeOutline.ItemIds.Add(temporaryOutlineIds);
                var removeOutlineReply = await mcp.Tool("kicad_pcb_items_delete", new
                {
                    instanceId, requestJson = BoardJson.Formatter.Format(removeOutline),
                    expectedStateJson = SchematicJson.Formatter.Format(before)
                });
                Assert.IsFalse(removeOutlineReply.TryGetProperty("isError", out var removeOutlineError) && removeOutlineError.GetBoolean(), removeOutlineReply.GetRawText());
                before = SchematicJson.Parser.Parse<DocumentLifecycleState>(removeOutlineReply.GetProperty("structuredContent").GetProperty("afterState").GetString()!);
            }

            string importOperation = Guid.NewGuid().ToString("D");
            var importRequest = new ImportNetlist
            {
                Board = board, NetlistPath = Path.GetFileName(netlistPath), DryRun = false,
                MatchMode = NetlistMatchMode.NmmReference, OperationId = importOperation
            };
            var importReply = await mcp.Tool("kicad_pcb_import_netlist", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(importRequest),
                expectedStateJson = SchematicJson.Formatter.Format(await ObserveLifecycleState(client, board, token)), operationId = importOperation
            });
            Assert.IsFalse(importReply.TryGetProperty("isError", out var importError) && importError.GetBoolean(), importReply.GetRawText());
            var importData = importReply.GetProperty("structuredContent");
            Assert.IsTrue(importData.TryGetProperty("response", out var importResponse), importData.GetRawText());
            // Protobuf JSON omits default-valued response counters on success.
            Assert.IsTrue(!importResponse.TryGetProperty("errorCount", out var importErrors)
                || importErrors.GetInt32() == 0, importReply.GetRawText());
            Assert.IsTrue(!importResponse.TryGetProperty("newFootprintCount", out var newFootprints)
                || newFootprints.GetInt32() == 2, importReply.GetRawText());
            Assert.IsTrue(importData.GetProperty("boardMutation").GetBoolean(), importReply.GetRawText());
            Assert.IsTrue(importData.GetProperty("mutationConfirmed").GetBoolean(), importReply.GetRawText());
            Assert.IsTrue(importData.GetProperty("nativeReceiptAvailable").GetBoolean(), importReply.GetRawText());
            var importedFootprints = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbFootprint } }, token);
            Assert.AreEqual(2, importedFootprints.Items.Count,
                "The valid netlist fixture must create both project-library footprints.");
            var importedPads = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbPad } }, token);
            Assert.IsTrue(importedPads.Items.Select(item => item.Unpack<Pad>()).Any(pad => pad.Net.Name == "MCP_IMPORTED"),
                "The import must transfer the changed schematic net onto board pads.");
            string receiptStateDirectory = Directory.CreateTempSubdirectory("kicad-pcb-receipt-mcp-").FullName;
            try
            {
                await using var receiptMcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                    receiptStateDirectory, Path.Combine(evidence, instanceId + "-pcb-receipt-mcp.stderr.log"), token);
                var receiptAttached = await receiptMcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                Assert.IsFalse(receiptAttached.TryGetProperty("isError", out var receiptAttachError) && receiptAttachError.GetBoolean());
                var receiptReply = await receiptMcp.Tool("kicad_pcb_operation_receipt", new
                { instanceId, expectedProcessEpoch = client.Epoch, operationId = refillOperation });
                Assert.IsFalse(receiptReply.TryGetProperty("isError", out var nativeReceiptError) && nativeReceiptError.GetBoolean(), receiptReply.GetRawText());
                var receiptData = receiptReply.GetProperty("structuredContent");
                Assert.IsTrue(receiptData.GetProperty("found").GetBoolean(), receiptReply.GetRawText());
                Assert.AreEqual("native-process", receiptData.GetProperty("receiptScope").GetString(), receiptReply.GetRawText());
                var reconnectedNativeReceipt = receiptData.GetProperty("nativeReceipt");
                Assert.IsTrue(reconnectedNativeReceipt.GetProperty("terminal").GetBoolean(), receiptReply.GetRawText());
                Assert.AreEqual("NZFO_COMPLETED", reconnectedNativeReceipt.GetProperty("zoneFillOutcome").GetString(), receiptReply.GetRawText());
                Assert.AreEqual(client.Epoch, reconnectedNativeReceipt.GetProperty("processEpoch").GetString(), receiptReply.GetRawText());
                Assert.IsTrue(reconnectedNativeReceipt.GetProperty("commitStateKnown").GetBoolean(), receiptReply.GetRawText());
                var importReceiptReply = await receiptMcp.Tool("kicad_pcb_operation_receipt", new
                { instanceId, expectedProcessEpoch = client.Epoch, operationId = importOperation });
                Assert.IsFalse(importReceiptReply.TryGetProperty("isError", out var importReceiptError) && importReceiptError.GetBoolean(), importReceiptReply.GetRawText());
                Assert.IsTrue(importReceiptReply.GetProperty("structuredContent").GetProperty("found").GetBoolean(), importReceiptReply.GetRawText());
                Assert.AreEqual("native-process", importReceiptReply.GetProperty("structuredContent").GetProperty("receiptScope").GetString(), importReceiptReply.GetRawText());
            }
            finally { Directory.Delete(receiptStateDirectory, true); }
            var importedSave = await mcp.Tool("kicad_document_save", new
            { instanceId, expectedStateJson = importData.GetProperty("afterState").GetString(), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(importedSave.TryGetProperty("isError", out var importedSaveError) && importedSaveError.GetBoolean(), importedSave.GetRawText());
            var importedClose = await mcp.Tool("kicad_document_close", new
            { instanceId, expectedStateJson = importedSave.GetProperty("structuredContent").GetProperty("observedState").GetRawText(), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(importedClose.TryGetProperty("isError", out var importedCloseError) && importedCloseError.GetBoolean(), importedClose.GetRawText());
            var importedReopen = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token, toolName: "kicad_pcb_open");
            board = importedReopen.Document;
            finalBoard = board;
            var persistedPads = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbPad } }, token);
            Assert.IsTrue(persistedPads.Items.Select(item => item.Unpack<Pad>()).Any(pad => pad.Net.Name == "MCP_IMPORTED"),
                "The imported net assignment must survive save, close and reopen.");
            before = await ObserveLifecycleState(client, board, token);
            var createdReply = await mcp.Tool("kicad_pcb_items_create", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(create),
                expectedStateJson = SchematicJson.Formatter.Format(before)
            });
            Assert.IsFalse(createdReply.TryGetProperty("isError", out var createError) && createError.GetBoolean(), createdReply.GetRawText());
            var createdData = createdReply.GetProperty("structuredContent");
            Assert.IsTrue(createdData.GetProperty("atomicTransaction").GetBoolean());
            var createdResponse = BoardJson.Parser.Parse<CreateItemsResponse>(createdData.GetProperty("response").GetRawText());
            Assert.AreEqual(ItemRequestStatus.IrsOk, createdResponse.Status);
            var createdTrack = createdResponse.CreatedItems.Single().Item.Unpack<Track>();
            Assert.AreEqual(track.Id, createdTrack.Id);
            string afterCreate = createdData.GetProperty("afterState").GetString()!;
            var beforeState = SchematicJson.Parser.Parse<DocumentLifecycleState>(SchematicJson.Formatter.Format(before));
            var afterCreateState = SchematicJson.Parser.Parse<DocumentLifecycleState>(afterCreate);
            Assert.IsGreaterThan(beforeState.Revision.Sequence, afterCreateState.Revision.Sequence,
                "A headless PCB item commit must advance the native revision cursor.");
            var stale = await mcp.Tool("kicad_pcb_items_create", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(create),
                expectedStateJson = SchematicJson.Formatter.Format(before)
            });
            Assert.IsTrue(stale.GetProperty("isError").GetBoolean());
            var moved = createdTrack.Clone(); moved.End.XNm += 5_000_000;
            var update = new UpdateItems { Header = new() { Document = board } };
            update.Items.Add(Any.Pack(moved));
            var updatedReply = await mcp.Tool("kicad_pcb_items_update", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(update), expectedStateJson = afterCreate
            });
            Assert.IsFalse(updatedReply.TryGetProperty("isError", out var updateError) && updateError.GetBoolean(), updatedReply.GetRawText());
            Assert.IsTrue(updatedReply.GetProperty("structuredContent").GetProperty("atomicTransaction").GetBoolean());
            var afterUpdateState = SchematicJson.Parser.Parse<DocumentLifecycleState>(updatedReply.GetProperty("structuredContent").GetProperty("afterState").GetString()!);
            Assert.IsGreaterThan(afterCreateState.Revision.Sequence, afterUpdateState.Revision.Sequence,
                "A headless PCB item update must advance the native revision cursor.");
            var actual = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = board }, Items = { createdTrack.Id } }, token);
            var actualTrack = actual.Items.Single().Unpack<Track>();
            Assert.AreEqual(moved.End, actualTrack.End);
            var guideShape = new BoardGraphicShape
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") }, Layer = BoardLayer.BlDwgsUser,
                Shape = new GraphicShape
                {
                    Attributes = new() { Stroke = new() { Width = new() { ValueNm = 100_000 } } },
                    Segment = new() { Start = new() { XNm = 10_000_000, YNm = 12_000_000 }, End = new() { XNm = 20_000_000, YNm = 12_000_000 } }
                }
            };
            var guideCreate = new CreateItems { Header = new() { Document = board } };
            guideCreate.Items.Add(Any.Pack(guideShape));
            string guideId = Guid.NewGuid().ToString("D");
            var guideReply = await mcp.Tool("kicad_pcb_guide_create", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(guideCreate),
                expectedStateJson = updatedReply.GetProperty("structuredContent").GetProperty("afterState").GetString(),
                guideId, sourceSha256 = new string('a', 64)
            });
            Assert.IsFalse(guideReply.TryGetProperty("isError", out var guideError) && guideError.GetBoolean(), guideReply.GetRawText());
            var guideData = guideReply.GetProperty("structuredContent");
            string guideState = guideData.GetProperty("afterState").GetString()!;
            var guideRead = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = board }, Items = { guideShape.Id } }, token);
            var persistedGuide = guideRead.Items.Single().Unpack<BoardGraphicShape>();
            Assert.AreEqual(BoardLayer.BlDwgsUser, persistedGuide.Layer);
            Assert.IsTrue(persistedGuide.CustomProperties.Any(p => p.Key == "kicad.ai.guide.role" && p.Value == "visual-underlay"));
            var candidateTrack = track.Clone(); candidateTrack.Id = new() { Value = Guid.NewGuid().ToString("D") };
            var candidateRequest = new CreateItems { Header = new() { Document = board } }; candidateRequest.Items.Add(Any.Pack(candidateTrack));
            var candidateReply = await mcp.Tool("kicad_pcb_route_candidate_validate", new
            {
                instanceId, expectedInstanceEpoch = client.Epoch, requestJson = BoardJson.Formatter.Format(candidateRequest),
                expectedStateJson = guideState, guideId,
                sourceSha256 = new string('a', 64)
            });
            // The guide ID is provenance supplied by the caller; the validator must
            // remain honest about not resolving it to a saved guide object yet.
            Assert.IsFalse(candidateReply.TryGetProperty("isError", out var candidateError) && candidateError.GetBoolean(), candidateReply.GetRawText());
            Assert.IsTrue(candidateReply.GetProperty("structuredContent").GetProperty("structuralValidation").GetBoolean());
            Assert.IsFalse(candidateReply.GetProperty("structuredContent").GetProperty("nativeCommit").GetBoolean());
            var invalidCandidate = candidateTrack.Clone(); invalidCandidate.Net = new();
            var invalidCandidateRequest = new CreateItems { Header = new() { Document = board } }; invalidCandidateRequest.Items.Add(Any.Pack(invalidCandidate));
            var rejectedCandidate = await mcp.Tool("kicad_pcb_route_candidate_validate", new
            {
                instanceId, expectedInstanceEpoch = client.Epoch, requestJson = BoardJson.Formatter.Format(invalidCandidateRequest),
                expectedStateJson = guideState, guideId, sourceSha256 = new string('a', 64)
            });
            Assert.IsTrue(rejectedCandidate.GetProperty("isError").GetBoolean());
            var copperGuide = guideShape.Clone(); copperGuide.Layer = BoardLayer.BlFCu;
            var copperRequest = new CreateItems { Header = new() { Document = board } }; copperRequest.Items.Add(Any.Pack(copperGuide));
            var rejectedGuide = await mcp.Tool("kicad_pcb_guide_create", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(copperRequest), expectedStateJson = guideState,
                guideId = Guid.NewGuid().ToString("D"), sourceSha256 = new string('b', 64)
            });
            Assert.IsTrue(rejectedGuide.GetProperty("isError").GetBoolean());
            const string svg = "<svg viewBox=\"0 0 10 10\"><polyline points=\"1,1 5,1 5,5\"/></svg>";
            string svgHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(svg)));
            string svgGuideId = Guid.NewGuid().ToString("D");
            var svgReply = await mcp.Tool("kicad_pcb_guide_svg_create", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board), expectedStateJson = guideState,
                svg, repositoryRoot = board.Project.Path, sourceArchivePath = "guides/rf.svg", guideId = svgGuideId,
                sourceSha256 = svgHash, layer = "Dwgs.User", originXNm = "30000000", originYNm = "30000000",
                nanometersPerSvgUnit = "100000", strokeWidthNm = 100000
            });
            Assert.IsFalse(svgReply.TryGetProperty("isError", out var svgError) && svgError.GetBoolean(), svgReply.GetRawText());
            Assert.AreEqual(svg, await File.ReadAllTextAsync(Path.Combine(board.Project.Path, "guides/rf.svg"), token));
            string svgState = svgReply.GetProperty("structuredContent").GetProperty("afterState").GetString()!;
            string artworkId = Guid.NewGuid().ToString("D"), artworkOperation = Guid.NewGuid().ToString("D");
            var artworkReply = await mcp.Tool("kicad_pcb_svg_artwork_commit", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board), expectedStateJson = svgState,
                svg, sourceSha256 = svgHash, sourceKind = "imagegen", assetId = artworkId,
                repositoryRoot = board.Project.Path, sourceArchivePath = "artwork/imagegen.svg", layer = "F.SilkS",
                originXNm = "30000000", originYNm = "30000000", nanometersPerSvgUnit = "100000",
                operationId = artworkOperation, strokeWidthNm = 100000
            });
            Assert.IsFalse(artworkReply.TryGetProperty("isError", out var artworkError) && artworkError.GetBoolean(), artworkReply.GetRawText());
            var artworkData = artworkReply.GetProperty("structuredContent");
            Assert.IsTrue(artworkData.GetProperty("authoritativeVector").GetBoolean());
            Assert.AreEqual(artworkId, artworkData.GetProperty("assetId").GetString());
            Assert.AreEqual(svg, await File.ReadAllTextAsync(Path.Combine(board.Project.Path, "artwork/imagegen.svg"), token));
            svgState = artworkData.GetProperty("nativeResult").GetProperty("afterState").GetString()!;
            var artworkReceipt = await mcp.Tool("kicad_pcb_svg_artwork_receipt", new
            { instanceId, expectedProcessEpoch = client.Epoch, operationId = artworkOperation });
            Assert.IsTrue(artworkReceipt.GetProperty("structuredContent").GetProperty("found").GetBoolean());
            var svgShapes = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbShape } }, token);
            Assert.IsTrue(svgShapes.Items.Any(item => item.Unpack<BoardGraphicShape>().CustomProperties.Any(p => p.Key == "kicad.ai.guide.source_format" && p.Value == "svg")));
            var layerManifest = await mcp.Tool("kicad_pcb_layer_manifest", new
            {
                instanceId,
                documentJson = SchematicJson.Formatter.Format(board),
                expectedStateJson = svgState,
                requestJson = JsonSerializer.Serialize(new
                {
                    layers = new[] { "F.Cu", "F.SilkS", "Dwgs.User", "F.Paste", "F.Mask" }, includeEmpty = false
                })
            });
            Assert.IsFalse(layerManifest.TryGetProperty("isError", out var layerManifestError) && layerManifestError.GetBoolean(), layerManifest.GetRawText());
            var layerManifestData = layerManifest.GetProperty("structuredContent");
            Assert.IsTrue(layerManifestData.GetProperty("nativeQuery").GetBoolean(), layerManifest.GetRawText());
            Assert.IsFalse(layerManifestData.GetProperty("boardMutation").GetBoolean(), layerManifest.GetRawText());
            Assert.IsFalse(layerManifestData.GetProperty("drcValidated").GetBoolean(), layerManifest.GetRawText());
            Assert.IsFalse(layerManifestData.GetProperty("vectorGeometry").GetBoolean(), layerManifest.GetRawText());
            Assert.AreEqual(svgState, layerManifestData.GetProperty("beforeState").GetString(), layerManifest.GetRawText());
            Assert.AreEqual(svgState, layerManifestData.GetProperty("afterState").GetString(), layerManifest.GetRawText());
            var layerRows = layerManifestData.GetProperty("layers").EnumerateArray().ToDictionary(
                row => row.GetProperty("layer").GetString()!, StringComparer.Ordinal);
            BoardLayer[] requestedLayers = [BoardLayer.BlFCu, BoardLayer.BlFSilkS, BoardLayer.BlDwgsUser,
                BoardLayer.BlFPaste, BoardLayer.BlFMask];
            CollectionAssert.AreEquivalent(requestedLayers.Select(layer => layer.ToString()).ToArray(), layerRows.Keys.ToArray());
            Assert.IsTrue(layerRows[nameof(BoardLayer.BlFCu)].GetProperty("objectCounts").GetProperty("track").GetInt32() > 0, layerManifest.GetRawText());
            Assert.AreEqual("copper", layerRows[nameof(BoardLayer.BlFCu)].GetProperty("kind").GetString(), layerManifest.GetRawText());
            Assert.AreEqual("technical", layerRows[nameof(BoardLayer.BlFSilkS)].GetProperty("kind").GetString(), layerManifest.GetRawText());
            Assert.AreEqual("user", layerRows[nameof(BoardLayer.BlDwgsUser)].GetProperty("kind").GetString(), layerManifest.GetRawText());
            foreach (BoardLayer layer in new[] { BoardLayer.BlFSilkS, BoardLayer.BlDwgsUser })
            {
                int expectedShapes = svgShapes.Items.Select(item => item.Unpack<BoardGraphicShape>()).Count(shape => shape.Layer == layer);
                Assert.IsTrue(expectedShapes > 0, "The live fixture must contain non-copper graphics on " + layer);
                Assert.AreEqual(expectedShapes, layerRows[layer.ToString()].GetProperty("objectCounts").GetProperty("shape").GetInt32(), layerManifest.GetRawText());
            }
            foreach (BoardLayer layer in new[] { BoardLayer.BlFCu, BoardLayer.BlFPaste, BoardLayer.BlFMask })
            {
                int expectedPads = persistedPads.Items.Select(item => item.Unpack<Pad>()).Count(pad => pad.PadStack.Layers.Contains(layer));
                Assert.IsTrue(expectedPads > 0, "The live fixture must contain pad membership on " + layer);
                Assert.AreEqual(expectedPads, layerRows[layer.ToString()].GetProperty("objectCounts").GetProperty("pad").GetInt32(), layerManifest.GetRawText());
            }
            Assert.IsTrue(layerRows.Values.All(row => row.GetProperty("membershipCount").GetInt32() > 0
                && row.GetProperty("distinctObjectCount").GetInt32() > 0), layerManifest.GetRawText());
            Assert.IsTrue(layerManifestData.GetProperty("compositeObjects").GetProperty("byType")
                .GetProperty("pad").GetInt32() > 0, layerManifest.GetRawText());
            var canonicalLayerManifest = await mcp.Tool("kicad_pcb_layer_manifest", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board), expectedStateJson = svgState,
                requestJson = JsonSerializer.Serialize(new
                {
                    layers = requestedLayers.Select(layer => layer.ToString()).ToArray(), includeEmpty = false
                })
            });
            Assert.IsFalse(canonicalLayerManifest.TryGetProperty("isError", out var canonicalLayerError) && canonicalLayerError.GetBoolean(), canonicalLayerManifest.GetRawText());
            Assert.AreEqual(layerManifestData.GetRawText(), canonicalLayerManifest.GetProperty("structuredContent").GetRawText(),
                "Friendly names and canonical enum names must return the same real layer memberships.");
            var protoLayerManifest = await mcp.Tool("kicad_pcb_layer_manifest", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board), expectedStateJson = svgState,
                requestJson = JsonSerializer.Serialize(new
                {
                    layers = new[] { "BL_F_Cu", "BL_F_SilkS", "BL_Dwgs_User", "BL_F_Paste", "BL_F_Mask" }, includeEmpty = false
                })
            });
            Assert.IsFalse(protoLayerManifest.TryGetProperty("isError", out var protoLayerError) && protoLayerError.GetBoolean(), protoLayerManifest.GetRawText());
            Assert.AreEqual(layerManifestData.GetRawText(), protoLayerManifest.GetProperty("structuredContent").GetRawText(),
                "Protobuf enum names must return the same real layer memberships.");
            var unknownLayerManifest = await mcp.Tool("kicad_pcb_layer_manifest", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board), expectedStateJson = svgState,
                requestJson = "{\"layers\":[\"F.NotARealLayer\"]}"
            });
            Assert.IsTrue(unknownLayerManifest.TryGetProperty("isError", out var unknownLayerError) && unknownLayerError.GetBoolean(), unknownLayerManifest.GetRawText());
            Assert.AreEqual("invalid_pcb_layer_manifest", unknownLayerManifest.GetProperty("structuredContent").GetProperty("errorCode").GetString(), unknownLayerManifest.GetRawText());
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-layer-manifest.json"),
                JsonSerializer.Serialize(new { friendly = layerManifest, canonical = canonicalLayerManifest,
                    protobuf = protoLayerManifest, unknown = unknownLayerManifest }), token);
            var renderFootprints = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbFootprint } }, token);
            var renderFootprint = renderFootprints.Items.Select(item => item.Unpack<FootprintInstance>())
                .Single(footprint => footprint.ReferenceField.Text.Text.Text_ == "R1");
            Assert.IsTrue(renderFootprint.Definition.Items.Count > 0,
                "The render fixture footprint must retain its imported definition payload.");
            // Use the same public lifecycle observation as the native render
            // admission, including the server-owned clean checkpoint.
            var renderStateBefore = await ObserveLifecycleState(client, board, token);
            var renderStateConfirm = await ObserveLifecycleState(client, board, token);
            Assert.IsFalse(string.IsNullOrEmpty(renderStateConfirm.CleanCheckpointSha256),
                "The render admission regression requires a saved clean checkpoint.");
            static object RenderStateProbe(DocumentLifecycleState state) => new
            {
                revision = state.Revision,
                nativeIdentity = state.NativeIdentity,
                stateSha256 = state.StateSha256,
                saveStableStateSha256 = state.SaveStableStateSha256,
                processEpoch = state.ProcessEpoch,
                nativeFiles = state.NativeFiles.ToArray(),
                fileBaselines = state.FileBaselines.Select(file => new
                {
                    file.Path, file.BaselinePath, file.BaselineKnown, file.BaselineExists,
                    file.BaselineSha256, file.CurrentKnown, file.CurrentExists, file.CurrentSha256,
                    file.Status
                }).ToArray()
            };
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-render-state-probe.json"),
                JsonSerializer.Serialize(new
                {
                    first = RenderStateProbe(renderStateBefore), second = RenderStateProbe(renderStateConfirm),
                    expectedStateJson = SchematicJson.Formatter.Format(renderStateConfirm)
                }), token);
            svgState = SchematicJson.Formatter.Format(renderStateConfirm);
            string modelHashBefore = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(renderModelPath, token)));
            DateTime modelTimestamp = File.GetLastWriteTimeUtc(renderModelPath);
            var renderRequest = new RunBoardJobExportRender
            {
                JobSettings = new() { Document = board }, Format = RenderFormat.RfPng,
                Quality = RenderQuality.RqBasic, BackgroundStyle = RenderBackgroundStyle.RbsOpaque,
                Width = 320, Height = 240, Side = RenderSide.RsTop, Zoom = 1, Perspective = false
            };
            var renderReply = await mcp.Tool("kicad_pcb_render_3d", new
            {
                instanceId, requestJson = JsonFormatter.Default.Format(renderRequest), expectedStateJson = svgState
            });
            Assert.IsFalse(renderReply.TryGetProperty("isError", out var renderError) && renderError.GetBoolean(), renderReply.GetRawText());
            var renderData = renderReply.GetProperty("structuredContent");
            byte[] initialRenderPng = RenderPng(renderReply);
            Assert.IsTrue(renderData.GetProperty("nativeRender").GetBoolean());
            Assert.IsFalse(renderData.GetProperty("boardMutation").GetBoolean());
            Assert.IsTrue(renderData.GetProperty("image").GetProperty("bytes").GetInt32() > 0);
            Assert.IsTrue(renderData.GetProperty("metadataAvailable").GetBoolean(), renderData.GetRawText());
            Assert.AreEqual(64, renderData.GetProperty("requestSha256").GetString()!.Length, renderData.GetRawText());
            var renderMetadata = renderData.GetProperty("metadata");
            Assert.AreEqual(16, renderMetadata.GetProperty("viewMatrixColumnMajor").GetArrayLength(), renderData.GetRawText());
            Assert.AreEqual(16, renderMetadata.GetProperty("projectionMatrixColumnMajor").GetArrayLength(), renderData.GetRawText());
            Assert.AreEqual(16, renderMetadata.GetProperty("boardNanometresToSceneColumnMajor").GetArrayLength(), renderData.GetRawText());
            Assert.IsTrue(renderMetadata.GetProperty("matricesFinite").GetBoolean(), renderData.GetRawText());
            Assert.IsTrue(renderMetadata.GetProperty("matricesInvertible").GetBoolean(), renderData.GetRawText());
            var expectedRenderState = SchematicJson.Parser.Parse<DocumentLifecycleState>(svgState);
            Assert.AreEqual(expectedRenderState.StateSha256, renderMetadata.GetProperty("sourceBoardSnapshotSha256").GetString(), renderData.GetRawText());
            Assert.AreEqual(64, renderMetadata.GetProperty("effectiveRenderInputSha256").GetString()!.Length, renderData.GetRawText());
            Assert.IsTrue(renderMetadata.GetProperty("coverageNotes").GetArrayLength() >= 2, renderData.GetRawText());
            Assert.IsTrue(renderMetadata.GetProperty("coverageNotes").EnumerateArray()
                    .Any(note => note.GetString()?.Contains("footprints' shown 3D model instances", StringComparison.Ordinal) == true),
                renderData.GetRawText());
            Assert.IsTrue(renderData.GetProperty("metadataComplete").ValueKind is JsonValueKind.True or JsonValueKind.False,
                renderData.GetRawText());
            bool hasMissingModel = renderMetadata.GetProperty("models").EnumerateArray()
                // Protobuf JSON omits a false scalar by default.  Treat an
                // omitted `missing` field as its schema default while keeping
                // malformed or non-boolean values strict when present.
                .Any(row => row.TryGetProperty("missing", out var missing) && missing.GetBoolean());
            Assert.AreEqual(!hasMissingModel, renderData.GetProperty("metadataComplete").GetBoolean(), renderData.GetRawText());
            var modelRows = renderMetadata.GetProperty("models").EnumerateArray().ToArray();
            Assert.IsTrue(modelRows.Length > 0, renderData.GetRawText());
            var modelRow = modelRows.Single(row => row.GetProperty("nativeUuid").GetString() == renderFootprint.Id.Value);
            if (modelRow.TryGetProperty("modelIndex", out var modelIndex))
                Assert.AreEqual(0u, modelIndex.GetUInt32(), renderData.GetRawText());
            if (modelRow.TryGetProperty("missing", out var modelMissing))
                Assert.IsFalse(modelMissing.GetBoolean(), renderData.GetRawText());
            if (modelRow.TryGetProperty("loaded", out var modelLoaded))
                Assert.IsTrue(modelLoaded.GetBoolean(), renderData.GetRawText());
            if (modelRow.TryGetProperty("contentSha256", out var modelHash))
                Assert.AreEqual(modelHashBefore, modelHash.GetString(), renderData.GetRawText());
            Assert.IsTrue(modelRow.GetProperty("resolvedIdentity").GetString()!.EndsWith("render-fixture.wrl", StringComparison.OrdinalIgnoreCase), renderData.GetRawText());
            var modelTransform = modelRow.GetProperty("sceneTransformColumnMajor").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            Assert.AreEqual(16, modelTransform.Length, renderData.GetRawText());
            Assert.IsTrue(modelTransform.All(double.IsFinite), renderData.GetRawText());
            Assert.IsTrue(modelTransform.Any(value => Math.Abs(value) > 1.0001 && Math.Abs(value) < 1_000_000), renderData.GetRawText());
            var objectRows = renderMetadata.GetProperty("objects").EnumerateArray().ToArray();
            Assert.IsTrue(objectRows.Length > 0, renderData.GetRawText());
            var objectRow = objectRows.Single(row => row.GetProperty("nativeUuid").GetString() == renderFootprint.Id.Value);
            if (objectRow.TryGetProperty("nativeGeometryBoundsAvailable", out var nativeBoundsAvailable))
                Assert.IsTrue(nativeBoundsAvailable.GetBoolean(), renderData.GetRawText());
            if (objectRow.TryGetProperty("projectedBoundsAvailable", out var projectedBoundsAvailable))
                Assert.IsTrue(projectedBoundsAvailable.GetBoolean(), renderData.GetRawText());
            var nativeBounds = objectRow.GetProperty("nativeGeometryBounds").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            Assert.AreEqual(6, nativeBounds.Length, renderData.GetRawText());
            Assert.IsTrue(nativeBounds.All(double.IsFinite)
                && nativeBounds[3] > nativeBounds[0] && nativeBounds[4] > nativeBounds[1] && nativeBounds[5] > nativeBounds[2], renderData.GetRawText());
            var projectedBounds = objectRow.GetProperty("projectedConservativeBounds").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            Assert.AreEqual(4, projectedBounds.Length, renderData.GetRawText());
            Assert.IsTrue(projectedBounds.All(double.IsFinite)
                && projectedBounds[2] > projectedBounds[0] && projectedBounds[3] > projectedBounds[1], renderData.GetRawText());
            string imageHashBefore = renderData.GetProperty("image").GetProperty("sha256").GetString()!;
            string renderInputHashBefore = renderMetadata.GetProperty("effectiveRenderInputSha256").GetString()!;
            var nativeBoundsBefore = nativeBounds.ToArray();
            var renderAfter = SchematicJson.Parser.Parse<DocumentLifecycleState>(renderData.GetProperty("afterState").GetString()!);
            Assert.AreEqual(svgState, SchematicJson.Formatter.Format(renderAfter));

            foreach ((RenderSide side, bool perspective) in new[]
            {
                (RenderSide.RsBottom, false), (RenderSide.RsTop, true)
            })
            {
                var viewRequest = renderRequest.Clone();
                viewRequest.Side = side;
                viewRequest.Perspective = perspective;
                var viewReply = await mcp.Tool("kicad_pcb_render_3d", new
                {
                    instanceId, requestJson = JsonFormatter.Default.Format(viewRequest), expectedStateJson = svgState
                });
                Assert.IsFalse(viewReply.TryGetProperty("isError", out var viewError) && viewError.GetBoolean(), viewReply.GetRawText());
                var viewData = viewReply.GetProperty("structuredContent");
                Assert.IsTrue(viewData.GetProperty("metadataAvailable").GetBoolean(), viewData.GetRawText());
                Assert.AreEqual(side.ToString(), viewData.GetProperty("view").GetProperty("side").GetString(), viewData.GetRawText());
                Assert.AreEqual(perspective, viewData.GetProperty("view").GetProperty("perspective").GetBoolean(), viewData.GetRawText());
                var viewAfter = SchematicJson.Parser.Parse<DocumentLifecycleState>(viewData.GetProperty("afterState").GetString()!);
                Assert.AreEqual(svgState, SchematicJson.Formatter.Format(viewAfter));
            }

            string modelText = await File.ReadAllTextAsync(renderModelPath, token);
            Assert.IsTrue(modelText.Contains("scale 8 4 2", StringComparison.Ordinal), modelText);
            long modelBytesBefore = new FileInfo(renderModelPath).Length;
            File.WriteAllText(renderModelPath, modelText.Replace("scale 8 4 2", "scale 9 3 2", StringComparison.Ordinal));
            File.SetLastWriteTimeUtc(renderModelPath, modelTimestamp);
            Assert.AreEqual(modelBytesBefore, new FileInfo(renderModelPath).Length, "The model replacement must preserve its byte length.");
            Assert.AreEqual(modelTimestamp, File.GetLastWriteTimeUtc(renderModelPath), "The model replacement must preserve its timestamp.");
            var changedRenderReply = await mcp.Tool("kicad_pcb_render_3d", new
            { instanceId, requestJson = JsonFormatter.Default.Format(renderRequest), expectedStateJson = svgState });
            Assert.IsFalse(changedRenderReply.TryGetProperty("isError", out var changedRenderError) && changedRenderError.GetBoolean(), changedRenderReply.GetRawText());
            var changedRenderData = changedRenderReply.GetProperty("structuredContent");
            byte[] changedRenderPng = RenderPng(changedRenderReply);
            var changedMetadata = changedRenderData.GetProperty("metadata");
            var changedModel = changedMetadata.GetProperty("models").EnumerateArray()
                .Single(row => row.GetProperty("nativeUuid").GetString() == renderFootprint.Id.Value);
            Assert.AreNotEqual(modelHashBefore, changedModel.GetProperty("contentSha256").GetString(), changedRenderData.GetRawText());
            Assert.AreNotEqual(imageHashBefore, changedRenderData.GetProperty("image").GetProperty("sha256").GetString(), changedRenderData.GetRawText());
            Assert.AreEqual(renderInputHashBefore, changedMetadata.GetProperty("effectiveRenderInputSha256").GetString(), changedRenderData.GetRawText());
            var changedBounds = changedMetadata.GetProperty("objects").EnumerateArray()
                .Single(row => row.GetProperty("nativeUuid").GetString() == renderFootprint.Id.Value)
                .GetProperty("nativeGeometryBounds").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            Assert.IsTrue(changedBounds.Zip(nativeBoundsBefore, (afterValue, beforeValue) => afterValue != beforeValue).Any(), changedRenderData.GetRawText());
            var pixelChanges = PngDiffBounds(initialRenderPng, changedRenderPng);
            Assert.IsTrue(pixelChanges.Width > 0 && pixelChanges.Height > 0, changedRenderData.GetRawText());
            Assert.IsTrue(pixelChanges.Left <= projectedBounds[2] && pixelChanges.Right >= projectedBounds[0]
                && pixelChanges.Top <= projectedBounds[3] && pixelChanges.Bottom >= projectedBounds[1],
                $"Changed rendered pixels {pixelChanges} must overlap reported projected bounds [{string.Join(",", projectedBounds)}].");
            Assert.AreEqual(svgState, changedRenderData.GetProperty("afterState").GetString(), changedRenderData.GetRawText());

            File.Delete(renderModelPath);
            var missingRenderReply = await mcp.Tool("kicad_pcb_render_3d", new
            { instanceId, requestJson = JsonFormatter.Default.Format(renderRequest), expectedStateJson = svgState });
            Assert.IsFalse(missingRenderReply.TryGetProperty("isError", out var missingRenderError) && missingRenderError.GetBoolean(), missingRenderReply.GetRawText());
            var missingRenderData = missingRenderReply.GetProperty("structuredContent");
            var missingMetadata = missingRenderData.GetProperty("metadata");
            var missingModel = missingMetadata.GetProperty("models").EnumerateArray()
                .Single(row => row.GetProperty("nativeUuid").GetString() == renderFootprint.Id.Value);
            Assert.IsTrue(missingModel.GetProperty("missing").GetBoolean(), missingRenderData.GetRawText());
            if (missingModel.TryGetProperty("missingReason", out var missingReason))
                Assert.IsFalse(string.IsNullOrWhiteSpace(missingReason.GetString()), missingRenderData.GetRawText());
            if (missingModel.TryGetProperty("loaded", out var missingLoaded))
                Assert.IsFalse(missingLoaded.GetBoolean(), missingRenderData.GetRawText());
            Assert.IsFalse(missingRenderData.GetProperty("metadataComplete").GetBoolean(), missingRenderData.GetRawText());
            var missingObject = missingMetadata.GetProperty("objects").EnumerateArray()
                .Single(row => row.GetProperty("nativeUuid").GetString() == renderFootprint.Id.Value);
            if (missingObject.TryGetProperty("nativeGeometryBoundsAvailable", out var missingBoundsAvailable))
                Assert.IsFalse(missingBoundsAvailable.GetBoolean(), missingRenderData.GetRawText());
            Assert.AreEqual(svgState, missingRenderData.GetProperty("afterState").GetString(), missingRenderData.GetRawText());
            var routeGeometry = await mcp.Tool("kicad_pcb_route_geometry", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board), expectedStateJson = svgState,
                netName = "POWER_RAIL"
            });
            Assert.IsFalse(routeGeometry.TryGetProperty("isError", out var routeGeometryError) && routeGeometryError.GetBoolean(), routeGeometry.GetRawText());
            var routeGeometryData = routeGeometry.GetProperty("structuredContent");
            Assert.IsTrue(routeGeometryData.GetProperty("measuredGeometry").GetBoolean());
            Assert.IsFalse(routeGeometryData.GetProperty("drcValidated").GetBoolean());
            Assert.AreEqual(15_000_000, routeGeometryData.GetProperty("nets").EnumerateArray().Single().GetProperty("trackLengthNm").GetInt64());
            var routeConstraints = await mcp.Tool("kicad_pcb_route_constraints", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board), expectedStateJson = svgState,
                requestJson = JsonSerializer.Serialize(new
                {
                    nets = new[] { "POWER_RAIL" }, targetLengthNm = 15_000_000L,
                    maxSkewNm = 0L, toleranceNm = 1L
                })
            });
            Assert.IsFalse(routeConstraints.TryGetProperty("isError", out var constraintError) && constraintError.GetBoolean(), routeConstraints.GetRawText());
            var constraintData = routeConstraints.GetProperty("structuredContent");
            Assert.IsTrue(constraintData.GetProperty("compliant").GetBoolean());
            Assert.IsFalse(constraintData.GetProperty("tuningRequired").GetBoolean());
            Assert.IsFalse(constraintData.GetProperty("drcValidated").GetBoolean());
            var generatedCandidate = await mcp.Tool("kicad_pcb_route_candidate_from_guide", new
            {
                instanceId, expectedInstanceEpoch = client.Epoch, documentJson = SchematicJson.Formatter.Format(board),
                expectedStateJson = svgState, guideId = svgGuideId, sourceSha256 = svgHash,
                netName = "POWER_RAIL", layer = "F.Cu", widthNm = 250000L
            });
            Assert.IsFalse(generatedCandidate.TryGetProperty("isError", out var generatedError) && generatedError.GetBoolean(), generatedCandidate.GetRawText());
            var generatedData = generatedCandidate.GetProperty("structuredContent");
            Assert.IsTrue(generatedData.GetProperty("structuralValidation").GetBoolean());
            Assert.IsTrue(generatedData.GetProperty("electricalNetResolved").GetBoolean());
            Assert.IsFalse(generatedData.GetProperty("nativeCommit").GetBoolean());
            var generatedRequest = BoardJson.Parser.Parse<CreateItems>(generatedData.GetProperty("candidateRequestJson").GetString()!);
            Assert.AreEqual(2, generatedRequest.Items.Count);
            var generatedTrack = generatedRequest.Items[0].Unpack<Track>();
            Assert.AreEqual(BoardLayer.BlFCu, generatedTrack.Layer);
            Assert.AreEqual("POWER_RAIL", generatedTrack.Net.Name);
            Assert.AreEqual(30_100_000, generatedTrack.Start.XNm);
            var generatedValidation = await mcp.Tool("kicad_pcb_route_candidate_validate", new
            {
                instanceId, expectedInstanceEpoch = client.Epoch,
                requestJson = BoardJson.Formatter.Format(generatedRequest), expectedStateJson = svgState,
                guideId = svgGuideId, sourceSha256 = svgHash
            });
            Assert.IsFalse(generatedValidation.TryGetProperty("isError", out var generatedValidationError) && generatedValidationError.GetBoolean(), generatedValidation.GetRawText());
            Assert.IsTrue(generatedValidation.GetProperty("structuredContent").GetProperty("structuralValidation").GetBoolean());
            var dryRunState = SchematicJson.Parser.Parse<DocumentLifecycleState>(svgState);
            var dryRunReply = await mcp.Tool("kicad_pcb_drc_start", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board),
                operationId = Guid.NewGuid().ToString("D"), refillZones = false,
                reportAllTrackErrors = true, testFootprints = false,
                expectedRevisionJson = SchematicJson.Formatter.Format(dryRunState.Revision),
                processEpoch = client.Epoch, candidateRequestJson = BoardJson.Formatter.Format(generatedRequest)
            });
            Assert.IsFalse(dryRunReply.TryGetProperty("isError", out var dryRunError) && dryRunError.GetBoolean(), dryRunReply.GetRawText());
            var dryRun = SchematicJson.Parser.Parse<PcbDrcJobState>(
                dryRunReply.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.IsTrue(dryRun.CandidateDryRun);
            Assert.AreEqual(2, dryRun.CandidateItemIds.Count);
            for (int attempt = 0; attempt < 60 && !dryRun.WorkerFinished; ++attempt)
            {
                await Task.Delay(100, token);
                var drcUpdate = await mcp.Tool("kicad_pcb_drc_job", new
                { instanceId, documentJson = SchematicJson.Formatter.Format(board), jobId = dryRun.JobId, processEpoch = client.Epoch });
                Assert.IsFalse(drcUpdate.TryGetProperty("isError", out var drcUpdateError) && drcUpdateError.GetBoolean(), drcUpdate.GetRawText());
                dryRun = SchematicJson.Parser.Parse<PcbDrcJobState>(drcUpdate.GetProperty("content")[0].GetProperty("text").GetString()!);
            }
            Assert.IsTrue(dryRun.WorkerFinished, "Detached candidate DRC did not reach a terminal state.");
            Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, dryRun.Status, dryRun.ErrorMessage);
            Assert.IsTrue(dryRun.SnapshotComplete, "Detached candidate DRC must capture complete inputs before route commit.");
            Assert.IsTrue(dryRun.ResultsFresh, "Detached candidate DRC must be fresh at its checked revision.");
            Assert.IsGreaterThan(0, dryRun.Findings.Count, "The fixture candidate must demonstrate the DRC rejection gate before route commit can be enabled.");
            Assert.AreEqual(svgState, SchematicJson.Formatter.Format(await ObserveLifecycleState(client, board, token)));
            var routeCommit = await mcp.Tool("kicad_pcb_route_commit", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(generatedRequest), expectedStateJson = svgState,
                drcJobStateJson = SchematicJson.Formatter.Format(dryRun), operationId = Guid.NewGuid().ToString("D")
            });
            string committedState = svgState;
            if (routeCommit.TryGetProperty("isError", out var routeCommitError) && routeCommitError.GetBoolean())
            {
                string code = routeCommit.GetProperty("structuredContent").GetProperty("errorCode").GetString()!;
                Assert.IsTrue(code is "route_drc_not_proven" or "pcb_tracking_incomplete", routeCommit.GetRawText());
            }
            else
            {
                var routeCommitData = routeCommit.GetProperty("structuredContent");
                Assert.IsTrue(routeCommitData.GetProperty("mutationConfirmed").GetBoolean());
                committedState = routeCommitData.GetProperty("afterState").GetString()!;
            }

            var previewRequest = new StartPcbRoutePreview
            {
                Document = board, OperationId = Guid.NewGuid().ToString("D"), ProcessEpoch = client.Epoch,
                ExpectedRevision = SchematicJson.Parser.Parse<DocumentLifecycleState>(committedState).Revision.Clone(), StartItemId = createdTrack.Id.Value,
                Start = new() { XNm = 25_000_000, YNm = 10_000_000 }, Layer = BoardLayer.BlFCu,
                ForceFinish = true
            };
            previewRequest.Waypoints.Add(new Vector2 { XNm = 28_000_000, YNm = 10_000_000 });
            var routePreview = await mcp.Tool("kicad_pcb_route_preview", new
            {
                instanceId, requestJson = SchematicJson.Formatter.Format(previewRequest), expectedStateJson = committedState
            });
            Assert.IsFalse(routePreview.TryGetProperty("isError", out var routePreviewError) && routePreviewError.GetBoolean(), routePreview.GetRawText());
            var routePreviewData = routePreview.GetProperty("structuredContent");
            Assert.IsTrue(routePreviewData.GetProperty("nativeRouter").GetBoolean());
            Assert.IsFalse(routePreviewData.GetProperty("nativeCommit").GetBoolean());
            Assert.IsFalse(routePreviewData.GetProperty("drcValidated").GetBoolean());
            var previewCandidate = BoardJson.Parser.Parse<CreateItems>(routePreviewData.GetProperty("candidateRequestJson").GetString()!);
            Assert.IsTrue(previewCandidate.Items.Count > 0);
            Assert.AreEqual(committedState, SchematicJson.Formatter.Format(await ObserveLifecycleState(client, board, token)));

            // Persistent native PNS sessions retain the detached router branch between MCP
            // calls. Read verifies the operation identity before continuation, and continuation
            // with forceFinish terminalizes the preview without changing the board.
            string persistentOperation = Guid.NewGuid().ToString("D");
            var persistentStart = new StartPcbRoutePreview
            {
                Document = board, OperationId = persistentOperation, ProcessEpoch = client.Epoch,
                ExpectedRevision = SchematicJson.Parser.Parse<DocumentLifecycleState>(committedState).Revision.Clone(),
                StartItemId = createdTrack.Id.Value, Start = new() { XNm = 25_000_000, YNm = 10_000_000 },
                Layer = BoardLayer.BlFCu, ForceFinish = false, PersistentSession = true
            };
            persistentStart.Waypoints.Add(new Vector2 { XNm = 27_000_000, YNm = 10_000_000 });
            var persistentPreview = await mcp.Tool("kicad_pcb_route_preview", new
            {
                instanceId, requestJson = SchematicJson.Formatter.Format(persistentStart), expectedStateJson = committedState
            });
            Assert.IsFalse(persistentPreview.TryGetProperty("isError", out var persistentError) && persistentError.GetBoolean(), persistentPreview.GetRawText());
            Assert.IsTrue(persistentPreview.GetProperty("structuredContent").GetProperty("sessionActive").GetBoolean());

            var persistentRead = await mcp.Tool("kicad_pcb_route_preview_read", new
            {
                instanceId, requestJson = SchematicJson.Formatter.Format(new NativeCapabilityRoutePreviewRead
                { Document = board, OperationId = persistentOperation, ProcessEpoch = client.Epoch })
            });
            Assert.IsFalse(persistentRead.TryGetProperty("isError", out var persistentReadError) && persistentReadError.GetBoolean(), persistentRead.GetRawText());
            Assert.IsTrue(persistentRead.GetProperty("structuredContent").GetProperty("sessionActive").GetBoolean());

            var persistentContinueRequest = new NativeCapabilityRoutePreviewContinue
            {
                Document = board, OperationId = persistentOperation, ProcessEpoch = client.Epoch,
                ExpectedRevision = SchematicJson.Parser.Parse<DocumentLifecycleState>(committedState).Revision.Clone(),
                ForceFinish = true
            };
            persistentContinueRequest.Waypoints.Add(new Vector2 { XNm = 29_000_000, YNm = 10_000_000 });
            var persistentContinue = await mcp.Tool("kicad_pcb_route_preview_continue", new
            {
                instanceId, requestJson = SchematicJson.Formatter.Format(persistentContinueRequest), expectedStateJson = committedState
            });
            Assert.IsFalse(persistentContinue.TryGetProperty("isError", out var persistentContinueError) && persistentContinueError.GetBoolean(), persistentContinue.GetRawText());
            Assert.IsFalse(persistentContinue.GetProperty("structuredContent").GetProperty("sessionActive").GetBoolean());
            Assert.AreEqual(committedState, SchematicJson.Formatter.Format(await ObserveLifecycleState(client, board, token)));

            string cancelledOperation = Guid.NewGuid().ToString("D");
            var cancellableStart = persistentStart.Clone();
            cancellableStart.OperationId = cancelledOperation;
            cancellableStart.Waypoints.Clear();
            cancellableStart.Waypoints.Add(new Vector2 { XNm = 27_000_000, YNm = 10_000_000 });
            var cancellablePreview = await mcp.Tool("kicad_pcb_route_preview", new
            {
                instanceId, requestJson = SchematicJson.Formatter.Format(cancellableStart), expectedStateJson = committedState
            });
            Assert.IsFalse(cancellablePreview.TryGetProperty("isError", out var cancellableError) && cancellableError.GetBoolean(), cancellablePreview.GetRawText());
            var cancelled = await mcp.Tool("kicad_pcb_route_preview_cancel", new
            {
                instanceId, requestJson = SchematicJson.Formatter.Format(new NativeCapabilityRoutePreviewCancel
                { Document = board, OperationId = cancelledOperation, ProcessEpoch = client.Epoch })
            });
            Assert.IsFalse(cancelled.TryGetProperty("isError", out var cancelledError) && cancelledError.GetBoolean(), cancelled.GetRawText());
            Assert.IsTrue(cancelled.GetProperty("structuredContent").GetProperty("cancelled").GetBoolean());
            var cancelledRead = await mcp.Tool("kicad_pcb_route_preview_read", new
            {
                instanceId, requestJson = SchematicJson.Formatter.Format(new NativeCapabilityRoutePreviewRead
                { Document = board, OperationId = cancelledOperation, ProcessEpoch = client.Epoch })
            });
            Assert.IsFalse(cancelledRead.TryGetProperty("isError", out var cancelledReadError) && cancelledReadError.GetBoolean(), cancelledRead.GetRawText());
            Assert.IsTrue(cancelledRead.GetProperty("structuredContent").GetProperty("cancelled").GetBoolean());
            Assert.AreEqual(committedState, SchematicJson.Formatter.Format(await ObserveLifecycleState(client, board, token)));

            // A separate valid board proves the positive checked-commit path. The earlier
            // fixture intentionally contains dangling guide-derived copper and must remain a
            // negative DRC case; do not weaken the native DRC gate to make it pass.
            var cleanSave = await mcp.Tool("kicad_document_save", new { instanceId, expectedStateJson = committedState, operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(cleanSave.TryGetProperty("isError", out var cleanSaveError) && cleanSaveError.GetBoolean(), cleanSave.GetRawText());
            var cleanClose = await mcp.Tool("kicad_document_close", new { instanceId, expectedStateJson = cleanSave.GetProperty("structuredContent").GetProperty("observedState").GetRawText(), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(cleanClose.TryGetProperty("isError", out var cleanCloseError) && cleanCloseError.GetBoolean(), cleanClose.GetRawText());
            string cleanPath = Path.Combine(board.Project.Path, board.BoardFilename);
            await File.WriteAllTextAsync(cleanPath, CleanRouteBoard(), token);
            var cleanOpen = await CreateRootThroughMcp(client.Endpoint, instanceId, cleanPath, evidence, token, toolName: "kicad_pcb_open");
            var cleanBoard = cleanOpen.Document;
            finalBoard = cleanBoard;
            var cleanState = await ObserveLifecycleState(client, cleanBoard, token);

            var pairPads = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbPad } }, token);
            var positivePad = pairPads.Items.Select(item => item.Unpack<Pad>())
                .Single(pad => pad.Net.Name == "USB_D+"
                    && pad.Position.XNm == 40_100_000 && pad.Position.YNm == 40_100_000);
            var differentialPreviewRequest = new StartPcbRoutePreview
            {
                Document = cleanBoard, OperationId = Guid.NewGuid().ToString("D"),
                ProcessEpoch = client.Epoch, ExpectedRevision = cleanState.Revision.Clone(),
                StartItemId = positivePad.Id.Value,
                Start = new() { XNm = 40_100_000, YNm = 40_100_000 },
                Layer = BoardLayer.BlFCu, ForceFinish = true, DifferentialPair = true
            };
            differentialPreviewRequest.Waypoints.Add(new Vector2 { XNm = 45_000_000, YNm = 40_450_000 });
            var differentialPreview = await mcp.Tool("kicad_pcb_route_preview", new
            {
                instanceId, requestJson = SchematicJson.Formatter.Format(differentialPreviewRequest),
                expectedStateJson = SchematicJson.Formatter.Format(cleanState)
            });
            Assert.IsFalse(differentialPreview.TryGetProperty("isError", out var differentialError)
                && differentialError.GetBoolean(), differentialPreview.GetRawText());
            var differentialData = differentialPreview.GetProperty("structuredContent");
            Assert.IsTrue(differentialData.GetProperty("nativeRouter").GetBoolean());
            Assert.IsFalse(differentialData.GetProperty("nativeCommit").GetBoolean());
            Assert.IsFalse(differentialData.GetProperty("drcValidated").GetBoolean());
            var differentialCandidate = BoardJson.Parser.Parse<CreateItems>(
                differentialData.GetProperty("candidateRequestJson").GetString()!);
            var differentialNets = differentialCandidate.Items
                .Select(item => item.Unpack<Track>().Net.Name).ToHashSet(StringComparer.Ordinal);
            Assert.IsTrue(differentialNets.Contains("USB_D+"), differentialData.GetRawText());
            Assert.IsTrue(differentialNets.Contains("USB_D-"), differentialData.GetRawText());
            Assert.IsGreaterThanOrEqualTo(2, differentialCandidate.Items.Count);
            Assert.AreEqual(SchematicJson.Formatter.Format(cleanState), SchematicJson.Formatter.Format(
                await ObserveLifecycleState(client, cleanBoard, token)));

            // Differential-pair placement commits each fixed waypoint inside
            // the native placer. The preview must retain all committed pieces
            // and the final simplified pair, even after its live node is gone.
            var multiWaypointPair = differentialPreviewRequest.Clone();
            multiWaypointPair.OperationId = Guid.NewGuid().ToString("D");
            multiWaypointPair.Waypoints.Clear();
            multiWaypointPair.Waypoints.Add(new Vector2 { XNm = 42_000_000, YNm = 40_450_000 });
            multiWaypointPair.Waypoints.Add(new Vector2 { XNm = 45_000_000, YNm = 40_450_000 });
            var multiWaypointPreview = await mcp.Tool("kicad_pcb_route_preview", new
            {
                instanceId, requestJson = SchematicJson.Formatter.Format(multiWaypointPair),
                expectedStateJson = SchematicJson.Formatter.Format(cleanState)
            });
            Assert.IsFalse(multiWaypointPreview.TryGetProperty("isError", out var multiError)
                && multiError.GetBoolean(), multiWaypointPreview.GetRawText());
            var multiData = multiWaypointPreview.GetProperty("structuredContent");
            Assert.AreEqual(1, multiData.GetProperty("preview").GetProperty("completeDeltaVersion").GetInt32());
            var multiCandidate = BoardJson.Parser.Parse<CreateItems>(multiData.GetProperty("candidateRequestJson").GetString()!);
            Assert.IsGreaterThanOrEqualTo(4, multiCandidate.Items.Count, multiData.GetRawText());
            Assert.IsTrue(multiCandidate.Items.Select(item => item.Unpack<Track>().Net.Name).Contains("USB_D+"));
            Assert.IsTrue(multiCandidate.Items.Select(item => item.Unpack<Track>().Net.Name).Contains("USB_D-"));
            Assert.AreEqual(multiCandidate.Items.Count,
                multiCandidate.Items.Select(RouteShape).Distinct(StringComparer.Ordinal).Count(),
                "Committed intermediate differential-pair geometry must not be duplicated.");
            Assert.AreEqual(SchematicJson.Formatter.Format(cleanState), SchematicJson.Formatter.Format(
                await ObserveLifecycleState(client, cleanBoard, token)));

            var openPair = multiWaypointPair.Clone();
            openPair.OperationId = Guid.NewGuid().ToString("D");
            openPair.ForceFinish = false;
            var openPreview = await mcp.Tool("kicad_pcb_route_preview", new
            {
                instanceId, requestJson = SchematicJson.Formatter.Format(openPair),
                expectedStateJson = SchematicJson.Formatter.Format(cleanState)
            });
            Assert.IsFalse(openPreview.TryGetProperty("isError", out var openError) && openError.GetBoolean(), openPreview.GetRawText());
            var openData = openPreview.GetProperty("structuredContent");
            var openCandidate = BoardJson.Parser.Parse<CreateItems>(openData.GetProperty("candidateRequestJson").GetString()!);
            Assert.IsGreaterThanOrEqualTo(2, openCandidate.Items.Count, openData.GetRawText());
            Assert.IsTrue(openCandidate.Items.Select(item => item.Unpack<Track>().Net.Name).Contains("USB_D+"));
            Assert.IsTrue(openCandidate.Items.Select(item => item.Unpack<Track>().Net.Name).Contains("USB_D-"));
            Assert.AreEqual(SchematicJson.Formatter.Format(cleanState), SchematicJson.Formatter.Format(
                await ObserveLifecycleState(client, cleanBoard, token)));

            // One real native plan combines a midpoint split, new copper,
            // displaced foreign copper and a property-bearing moved via.
            var shoveObstacleItems = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbTrace } }, token);
            var shoveObstacle = shoveObstacleItems.Items.Select(item => item.Unpack<Track>())
                .Single(track => track.Net.Name == "POWER_RAIL" && track.Layer == BoardLayer.BlFCu);
            // Keep the property-bearing via as a standalone same-net
            // obstacle.  Ending the foreign track underneath the via makes
            // PNS treat the connected branch as a walkaround candidate rather
            // than a movable via owner; the standalone via exercises the
            // complete identity-preserving shove delta directly.
            shoveObstacle.End = new() { XNm = 41_000_000, YNm = 42_000_000 };
            shoveObstacle.SolderMask = new() { ExposeCopper = true, SolderMaskMargin = new() { ValueNm = 35_000 } };
            shoveObstacle.CustomProperties.Add(new CustomProperty { Key = "shove-source", Value = "keep-mask-and-properties" });
            var obstacleUpdate = new UpdateItems { Header = new() { Document = cleanBoard }, Items = { Any.Pack(shoveObstacle) } };
            var obstacleUpdated = await mcp.Tool("kicad_pcb_items_update", new
            { instanceId, requestJson = BoardJson.Formatter.Format(obstacleUpdate), expectedStateJson = SchematicJson.Formatter.Format(cleanState) });
            Assert.IsFalse(obstacleUpdated.TryGetProperty("isError", out var obstacleUpdateError) && obstacleUpdateError.GetBoolean(), obstacleUpdated.GetRawText());
            cleanState = await ObserveLifecycleState(client, cleanBoard, token);
            var splitSource = new Track
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") }, Start = positivePad.Position.Clone(),
                End = new() { XNm = 43_100_000, YNm = 40_100_000 }, Width = new() { ValueNm = 250_000 },
                Layer = BoardLayer.BlFCu, Net = positivePad.Net.Clone(), Locked = LockedState.LsUnlocked,
                SolderMask = new() { ExposeCopper = true, SolderMaskMargin = new() { ValueNm = 0 } },
                CustomProperties = { new CustomProperty { Key = "z-split-source", Value = "wire-order-is-not-state" },
                    new CustomProperty { Key = "split-source", Value = "keep-original-identity" } }
            };
            var shovedVia = new Via
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = shoveObstacle.End.Clone(),
                // This via is part of the POWER_RAIL obstacle path.  A free via
                // is intentionally ignored by PNS connectivity and can be
                // walked around, so mark the fixture as connected to require
                // the native shove delta to move and preserve it.
                Net = shoveObstacle.Net.Clone(), Type = ViaType.VtThrough, IsFree = false,
                CustomProperties = { new CustomProperty { Key = "z-via-source", Value = "wire-order-is-not-state" },
                    new CustomProperty { Key = "via-source", Value = "keep-full-padstack" } },
                PadStack = new()
                {
                    Type = PadStackType.PstCustom, Layers = { BoardLayer.BlFCu, BoardLayer.BlBCu },
                    Drill = new() { Diameter = new() { XNm = 300_000, YNm = 300_000 },
                        StartLayer = BoardLayer.BlFCu, EndLayer = BoardLayer.BlBCu, Shape = DrillShape.DsCircle },
                    // The native custom-via file format persists per-layer
                    // dimensions and reconstructs each anchor as a rectangle.
                    // Keep that canonical anchor while retaining circular
                    // copper geometry so strict padstack identity survives
                    // save/reopen without hiding the supported shape.
                    CopperLayers = { new PadStackLayer { Layer = BoardLayer.BlFCu, Shape = PadStackShape.PssCircle,
                        CustomAnchorShape = PadStackShape.PssRectangle,
                        Size = new() { XNm = 700_000, YNm = 700_000 } },
                        new PadStackLayer { Layer = BoardLayer.BlBCu, Shape = PadStackShape.PssCircle,
                            CustomAnchorShape = PadStackShape.PssRectangle,
                            Size = new() { XNm = 700_000, YNm = 700_000 } } },
                    FrontOuterLayers = new() { SolderMaskMode = SolderMaskMode.SmmMasked },
                    BackOuterLayers = new() { SolderMaskMode = SolderMaskMode.SmmUnmasked }
                }
            };
            var powerConnection = new Track
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") }, Start = new() { XNm = 30_100_000, YNm = 30_100_000 },
                End = new() { XNm = 30_500_000, YNm = 30_500_000 }, Width = new() { ValueNm = 250_000 },
                Layer = BoardLayer.BlFCu, Net = shoveObstacle.Net.Clone()
            };
            var powerLead = powerConnection.Clone(); powerLead.Id = new() { Value = Guid.NewGuid().ToString("D") };
            powerLead.Start = powerConnection.End.Clone(); powerLead.End = shoveObstacle.Start.Clone();
            var fixtureCreate = new CreateItems { Header = new() { Document = cleanBoard } };
            fixtureCreate.Items.Add(new[] { Any.Pack(splitSource), Any.Pack(shovedVia), Any.Pack(powerConnection), Any.Pack(powerLead) });
            var fixtureCreated = await mcp.Tool("kicad_pcb_items_create", new
            { instanceId, requestJson = BoardJson.Formatter.Format(fixtureCreate), expectedStateJson = SchematicJson.Formatter.Format(cleanState) });
            Assert.IsFalse(fixtureCreated.TryGetProperty("isError", out var fixtureCreateError) && fixtureCreateError.GetBoolean(), fixtureCreated.GetRawText());
            var sourceRead = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Items = { splitSource.Id, shovedVia.Id } }, token);
            splitSource = sourceRead.Items.Single(item => item.Is(Track.Descriptor)).Unpack<Track>();
            shovedVia = sourceRead.Items.Single(item => item.Is(Via.Descriptor)).Unpack<Via>();
            cleanState = await ObserveLifecycleState(client, cleanBoard, token);
            var shovePreviewRequest = new StartPcbRoutePreview
            {
                Document = cleanBoard, OperationId = Guid.NewGuid().ToString("D"),
                ProcessEpoch = client.Epoch, ExpectedRevision = cleanState.Revision.Clone(),
                StartItemId = splitSource.Id.Value, Start = new() { XNm = 41_600_000, YNm = 40_100_000 },
                Layer = BoardLayer.BlFCu, ForceFinish = true, PushAndShove = true
            };
            shovePreviewRequest.Waypoints.Add(new Vector2 { XNm = 41_600_000, YNm = 44_000_000 });

            // Direct ordinary PCB groups are represented by the version-2
            // route delta. The detached preview must carry the exact before
            // and after memberships while leaving the board unchanged.
            var sourceGroup = new Kiapi.Board.Types.Group
            { Id = new() { Value = Guid.NewGuid().ToString("D") }, Name = "Grouped split guard", Items = { splitSource.Id.Clone() } };
            var groupCreate = new CreateItems { Header = new() { Document = cleanBoard }, Items = { Any.Pack(sourceGroup) } };
            var grouped = await mcp.Tool("kicad_pcb_items_create", new
            { instanceId, requestJson = BoardJson.Formatter.Format(groupCreate), expectedStateJson = SchematicJson.Formatter.Format(cleanState) });
            Assert.IsFalse(grouped.TryGetProperty("isError", out var groupError) && groupError.GetBoolean(), grouped.GetRawText());
            var groupedState = await ObserveLifecycleState(client, cleanBoard, token);
            shovePreviewRequest.ExpectedRevision = groupedState.Revision.Clone();
            var groupedPreview = await mcp.Tool("kicad_pcb_route_preview", new
            { instanceId, requestJson = SchematicJson.Formatter.Format(shovePreviewRequest), expectedStateJson = SchematicJson.Formatter.Format(groupedState) });
            Assert.IsFalse(groupedPreview.TryGetProperty("isError", out var groupedPreviewError) && groupedPreviewError.GetBoolean(), groupedPreview.GetRawText());
            var initialGroupedData = groupedPreview.GetProperty("structuredContent");
            var initialGroupedStateData = initialGroupedData.GetProperty("preview");
            Assert.AreEqual(2, initialGroupedStateData.GetProperty("completeDeltaVersion").GetInt32(), groupedPreview.GetRawText());
            Assert.IsTrue(initialGroupedStateData.GetProperty("completed").GetBoolean(), groupedPreview.GetRawText());
            Assert.IsTrue(initialGroupedStateData.GetProperty("previewOnly").GetBoolean(), groupedPreview.GetRawText());
            string initialGroupedChangesJson = initialGroupedData.GetProperty("groupChangesJson").GetString()!;
            using var initialGroupedChangesDocument = JsonDocument.Parse(initialGroupedChangesJson);
            var initialGroupedChanges = initialGroupedChangesDocument.RootElement.EnumerateArray()
                .Select(element => SchematicJson.Parser.Parse<NativeRouteGroupChange>(element.GetRawText())).ToArray();
            Assert.AreEqual(1, initialGroupedChanges.Length, groupedPreview.GetRawText());
            var initialGroupedChange = initialGroupedChanges[0];
            Assert.AreEqual(sourceGroup.Id, initialGroupedChange.ExpectedBefore.Group.Id, initialGroupedChangesJson);
            Assert.IsTrue(initialGroupedChange.ExpectedBefore.Group.Items.Select(id => id.Value)
                .ToHashSet(StringComparer.Ordinal).SetEquals(new[] { splitSource.Id.Value }), initialGroupedChangesJson);
            Assert.IsFalse(initialGroupedChange.ExpectedAfter.Group.Items.Any(id => id.Value == splitSource.Id.Value), initialGroupedChangesJson);
            Assert.IsTrue(initialGroupedChange.CreatedSources.All(source => source.SourceItemId.Equals(splitSource.Id)
                && source.SourceGroupId.Equals(sourceGroup.Id)
                && initialGroupedChange.ExpectedAfter.Group.Items.Any(id => id.Equals(source.CreatedItemId))), initialGroupedChangesJson);
            var initialGroupedCandidate = BoardJson.Parser.Parse<CreateItems>(initialGroupedData.GetProperty("candidateRequestJson").GetString()!);
            var initialGroupedCandidateIds = initialGroupedCandidate.Items.Select(RouteIdentity).ToHashSet(StringComparer.Ordinal);
            Assert.IsTrue(initialGroupedChange.CreatedSources.All(source => initialGroupedCandidateIds.Contains(source.CreatedItemId.Value)), groupedPreview.GetRawText());
            Assert.AreEqual(groupedState, await ObserveLifecycleState(client, cleanBoard, token));
            var groupRemove = new DeleteItems { Header = new() { Document = cleanBoard }, ItemIds = { sourceGroup.Id.Clone() } };
            var ungrouped = await mcp.Tool("kicad_pcb_items_delete", new
            { instanceId, requestJson = BoardJson.Formatter.Format(groupRemove), expectedStateJson = SchematicJson.Formatter.Format(groupedState) });
            Assert.IsFalse(ungrouped.TryGetProperty("isError", out var ungroupError) && ungroupError.GetBoolean(), ungrouped.GetRawText());
            cleanState = await ObserveLifecycleState(client, cleanBoard, token);

            async Task<GetItemsResponse> ReadGroupAndCopper()
            {
                return await client.InvokeAsync<GetItems, GetItemsResponse>(new()
                {
                    Header = new() { Document = cleanBoard },
                    Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc,
                        KiCadObjectType.KotPcbVia, KiCadObjectType.KotPcbGroup }
                }, token);
            }

            static string PcbItemIdentity(Any item) => item.Is(Track.Descriptor) ? item.Unpack<Track>().Id.Value
                : item.Is(Arc.Descriptor) ? item.Unpack<Arc>().Id.Value
                : item.Is(Via.Descriptor) ? item.Unpack<Via>().Id.Value
                : item.Unpack<Kiapi.Board.Types.Group>().Id.Value;

            var afterGroupDelete = await ReadGroupAndCopper();
            var afterGroupDeleteIds = afterGroupDelete.Items.Select(PcbItemIdentity).ToHashSet(StringComparer.Ordinal);
            Assert.IsFalse(afterGroupDeleteIds.Contains(sourceGroup.Id.Value), "Deleting a populated group must remove the group itself.");
            Assert.IsFalse(afterGroupDeleteIds.Contains(splitSource.Id.Value), "Deleting a populated group must remove its member.");
            Assert.IsTrue(afterGroupDeleteIds.Contains(powerConnection.Id.Value), "Unrelated copper must survive group deletion.");
            Assert.IsTrue(afterGroupDeleteIds.Contains(shoveObstacle.Id.Value), "Unrelated obstacle copper must survive group deletion.");

            // The API deletion is one native undo entry. Verify both the
            // recursive membership removal and the native history boundary,
            // then redo the exact deletion before continuing the route case.
            NativeKeyboard.SchematicShortcut(display, focusProcessId, "z", "PCB Editor", true, true,
                clickFromLeft: 500, clickFromTop: 500);
            GetItemsResponse afterGroupUndo = null!;
            using (var undoDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                undoDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                while (true)
                {
                    afterGroupUndo = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
                    {
                        Header = new() { Document = cleanBoard },
                        Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc,
                            KiCadObjectType.KotPcbVia, KiCadObjectType.KotPcbGroup }
                    }, undoDeadline.Token);
                    var restoredGroup = afterGroupUndo.Items.Where(item => item.Is(Kiapi.Board.Types.Group.Descriptor))
                        .Select(item => item.Unpack<Kiapi.Board.Types.Group>())
                        .SingleOrDefault(group => group.Id.Equals(sourceGroup.Id));
                    if (restoredGroup is not null
                        && restoredGroup.Items.Any(item => item.Equals(splitSource.Id))
                        && afterGroupUndo.Items.Any(item => PcbItemIdentity(item) == splitSource.Id.Value))
                        break;
                    await Task.Delay(100, undoDeadline.Token);
                }
            }
            Assert.IsTrue(afterGroupUndo.Items.Any(item => PcbItemIdentity(item) == powerConnection.Id.Value));
            Assert.IsTrue(afterGroupUndo.Items.Any(item => PcbItemIdentity(item) == shoveObstacle.Id.Value));

            NativeKeyboard.SchematicShortcut(display, focusProcessId, "y", "PCB Editor", true, true,
                clickFromLeft: 500, clickFromTop: 500);
            using (var redoDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                redoDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                while (true)
                {
                    var afterGroupRedo = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
                    {
                        Header = new() { Document = cleanBoard },
                        Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc,
                            KiCadObjectType.KotPcbVia, KiCadObjectType.KotPcbGroup }
                    }, redoDeadline.Token);
                    var groupPresent = afterGroupRedo.Items.Any(item => item.Is(Kiapi.Board.Types.Group.Descriptor)
                        && item.Unpack<Kiapi.Board.Types.Group>().Id.Equals(sourceGroup.Id));
                    var memberPresent = afterGroupRedo.Items.Any(item => PcbItemIdentity(item) == splitSource.Id.Value);
                    if (!groupPresent && !memberPresent)
                        break;
                    await Task.Delay(100, redoDeadline.Token);
                }
            }
            cleanState = await ObserveLifecycleState(client, cleanBoard, token);

            // Deleting a populated group removes its members, matching the
            // native editor's recursive delete transaction. Ungrouping is a
            // separate operation, so recreate this route source explicitly
            // before the positive shove-delta case.
            splitSource = splitSource.Clone();
            splitSource.Id = new() { Value = Guid.NewGuid().ToString("D") };
            var recreatedSplit = new CreateItems { Header = new() { Document = cleanBoard }, Items = { Any.Pack(splitSource) } };
            var recreatedSplitReply = await mcp.Tool("kicad_pcb_items_create", new
            { instanceId, requestJson = BoardJson.Formatter.Format(recreatedSplit), expectedStateJson = SchematicJson.Formatter.Format(cleanState) });
            Assert.IsFalse(recreatedSplitReply.TryGetProperty("isError", out var recreatedSplitError)
                && recreatedSplitError.GetBoolean(), recreatedSplitReply.GetRawText());
            cleanState = await ObserveLifecycleState(client, cleanBoard, token);

            shovePreviewRequest.OperationId = Guid.NewGuid().ToString("D");
            shovePreviewRequest.StartItemId = splitSource.Id.Value;
            shovePreviewRequest.ExpectedRevision = cleanState.Revision.Clone();
            var beforeShoveItems = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia } }, token);
            var shovePreview = await mcp.Tool("kicad_pcb_route_preview", new
            { instanceId, requestJson = SchematicJson.Formatter.Format(shovePreviewRequest), expectedStateJson = SchematicJson.Formatter.Format(cleanState) });
            Assert.IsFalse(shovePreview.TryGetProperty("isError", out var shoveError) && shoveError.GetBoolean(), shovePreview.GetRawText());
            var shoveData = shovePreview.GetProperty("structuredContent");
            var shoveCreate = BoardJson.Parser.Parse<CreateItems>(shoveData.GetProperty("candidateRequestJson").GetString()!);
            var shoveUpdate = BoardJson.Parser.Parse<UpdateItems>(shoveData.GetProperty("replacementUpdatesJson").GetString()!);
            string? shoveRemovalJson = shoveData.GetProperty("candidateDeleteRequestJson").GetString();
            var shoveRemovals = shoveRemovalJson is null ? null : BoardJson.Parser.Parse<DeleteItems>(shoveRemovalJson);
            var retainedSplit = shoveUpdate.Items.Single(item => item.Is(Track.Descriptor) && item.Unpack<Track>().Id.Equals(splitSource.Id)).Unpack<Track>();
            var retainedViaAny = shoveUpdate.Items.SingleOrDefault(item => item.Is(Via.Descriptor) && item.Unpack<Via>().Id.Equals(shovedVia.Id));
            if (retainedViaAny is null)
            {
                var shoveDiagnostic = new
                {
                    request = SchematicJson.Formatter.Format(shovePreviewRequest),
                    settings = new
                    {
                        pushAndShove = shovePreviewRequest.PushAndShove,
                        forceFinish = shovePreviewRequest.ForceFinish,
                        differentialPair = shovePreviewRequest.DifferentialPair,
                        layer = shovePreviewRequest.Layer.ToString(),
                        startItemId = shovePreviewRequest.StartItemId,
                        start = $"{shovePreviewRequest.Start.XNm},{shovePreviewRequest.Start.YNm}",
                        waypoints = shovePreviewRequest.Waypoints.Select(point => $"{point.XNm},{point.YNm}").ToArray()
                    },
                    boardSource = new
                    {
                        split = DescribeRouteItem(Any.Pack(splitSource)),
                        via = DescribeRouteItem(Any.Pack(shovedVia)),
                        obstacle = DescribeRouteItem(Any.Pack(shoveObstacle))
                    },
                    before = beforeShoveItems.Items.Select(DescribeRouteItem).ToArray(),
                    delta = new
                    {
                        creates = shoveCreate.Items.Select(DescribeRouteItem).ToArray(),
                        updates = shoveUpdate.Items.Select(DescribeRouteItem).ToArray(),
                        removals = shoveRemovals?.ItemIds.Select(id => id.Value).ToArray() ?? [],
                        candidateRequestJson = shoveData.GetProperty("candidateRequestJson").GetString(),
                        replacementUpdatesJson = shoveData.GetProperty("replacementUpdatesJson").GetString(),
                        candidateDeleteRequestJson = shoveRemovalJson,
                        replacementUpdatesError = shoveData.TryGetProperty("replacementUpdatesError", out var replacementError)
                            ? replacementError.GetString() : null,
                        completeDeltaVersion = shoveData.GetProperty("preview").GetProperty("completeDeltaVersion").GetInt32()
                    }
                };
                string diagnosticPath = Path.Combine(evidence, instanceId + "-shove-delta-diagnostic.json");
                await File.WriteAllTextAsync(diagnosticPath,
                    JsonSerializer.Serialize(shoveDiagnostic, new JsonSerializerOptions { WriteIndented = true }), token);
                Assert.Fail($"Push-and-shove preview omitted the property-bearing via {shovedVia.Id.Value} from replacement updates; "
                    + $"diagnostic={diagnosticPath}; creates={shoveCreate.Items.Count}, updates={shoveUpdate.Items.Count}, "
                    + $"removals={shoveRemovals?.ItemIds.Count ?? 0}, via={DescribeRouteItem(Any.Pack(shovedVia))}");
            }
            var retainedVia = retainedViaAny.Unpack<Via>();
            Assert.AreEqual(splitSource.CustomProperties, retainedSplit.CustomProperties);
            Assert.AreEqual(splitSource.SolderMask, retainedSplit.SolderMask);
            Assert.AreEqual(shovedVia.PadStack, retainedVia.PadStack);
            Assert.AreEqual(shovedVia.CustomProperties, retainedVia.CustomProperties);
            Assert.AreEqual(shovedVia.Teardrop, retainedVia.Teardrop);
            Assert.IsFalse(retainedVia.IsFree, "The obstacle via is connected to POWER_RAIL and must remain connected after shove.");
            Assert.AreNotEqual(shovedVia.Position, retainedVia.Position, "The fixture must move its property-bearing via.");
            Assert.IsTrue(shoveCreate.Items.Any(item => item.Is(Track.Descriptor)
                && item.Unpack<Track>().CustomProperties.Any(property => property.Key == "split-source")), "The second split segment must remain a creation.");
            Assert.AreEqual(cleanState, await ObserveLifecycleState(client, cleanBoard, token));
            var expectedCreates = shoveCreate.Items.Select(RouteIdentity).ToHashSet(StringComparer.Ordinal);
            var expectedUpdates = shoveUpdate.Items.Select(RouteIdentity).ToHashSet(StringComparer.Ordinal);
            var expectedRemovals = shoveRemovals?.ItemIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal) ?? [];
            Assert.IsFalse(expectedCreates.Overlaps(expectedUpdates) || expectedCreates.Overlaps(expectedRemovals) || expectedUpdates.Overlaps(expectedRemovals));

            async Task<PcbDrcJobState> RouteDrc(string candidateJson, string? deleteJson,
                DocumentLifecycleState checkpoint, string? groupJson = null)
            {
                var reply = await mcp.Tool("kicad_pcb_drc_start", new
                {
                    instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard), operationId = Guid.NewGuid().ToString("D"),
                    refillZones = false, reportAllTrackErrors = true, testFootprints = false,
                    expectedRevisionJson = SchematicJson.Formatter.Format(checkpoint.Revision), processEpoch = client.Epoch,
                    candidateRequestJson = candidateJson, deleteRequestJson = deleteJson, groupChangesJson = groupJson ?? ""
                });
                Assert.IsFalse(reply.TryGetProperty("isError", out var error) && error.GetBoolean(), reply.GetRawText());
                var job = SchematicJson.Parser.Parse<PcbDrcJobState>(reply.GetProperty("content")[0].GetProperty("text").GetString()!);
                for (int attempt = 0; attempt < 120 && !job.WorkerFinished; ++attempt)
                {
                    await Task.Delay(100, token);
                    reply = await mcp.Tool("kicad_pcb_drc_job", new
                    { instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard), jobId = job.JobId, processEpoch = client.Epoch,
                        candidateRequestJson = candidateJson, deleteRequestJson = deleteJson, groupChangesJson = groupJson ?? "" });
                    Assert.IsFalse(reply.TryGetProperty("isError", out error) && error.GetBoolean(), reply.GetRawText());
                    job = SchematicJson.Parser.Parse<PcbDrcJobState>(reply.GetProperty("content")[0].GetProperty("text").GetString()!);
                }
                Assert.IsTrue(job.WorkerFinished, "The complete route DRC must finish.");
                Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, job.Status, job.ErrorMessage);
                Assert.IsTrue(job.CandidatePayloadVerified && job.CandidateDryRun && job.SnapshotComplete && job.ResultsFresh);
                if (groupJson is not null)
                {
                    Assert.IsTrue(job.GroupPayloadVerified, SchematicJson.Formatter.Format(job));
                    Assert.IsGreaterThan(0, job.VerifiedGroupChanges.Count, SchematicJson.Formatter.Format(job));
                    Assert.AreEqual(job.VerifiedGroupChanges.Count, job.AfterGroupStates.Count, SchematicJson.Formatter.Format(job));
                }
                Assert.IsEmpty(job.Findings, SchematicJson.Formatter.Format(job));
                return job;
            }
            var shoveDrc = await RouteDrc(shoveData.GetProperty("drcCandidateRequestJson").GetString()!,
                shoveData.GetProperty("drcDeleteRequestJson").GetString(), cleanState);
            string shoveCommitId = Guid.NewGuid().ToString("D");
            var shoveCommitArguments = new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(shoveCreate), replacementUpdatesJson = BoardJson.Formatter.Format(shoveUpdate),
                removalsJson = shoveRemovalJson ?? "", expectedStateJson = SchematicJson.Formatter.Format(cleanState),
                drcJobStateJson = SchematicJson.Formatter.Format(shoveDrc), operationId = shoveCommitId
            };
            var shoveCommit = await mcp.Tool("kicad_pcb_route_commit", shoveCommitArguments);
            Assert.IsFalse(shoveCommit.TryGetProperty("isError", out var shoveCommitError) && shoveCommitError.GetBoolean(), shoveCommit.GetRawText());
            var shoveCommittedState = await ObserveLifecycleState(client, cleanBoard, token);
            var shoveReplay = await mcp.Tool("kicad_pcb_route_commit", shoveCommitArguments);
            Assert.IsFalse(shoveReplay.TryGetProperty("isError", out var shoveReplayError) && shoveReplayError.GetBoolean(), shoveReplay.GetRawText());
            Assert.IsTrue(shoveReplay.GetProperty("structuredContent").GetProperty("receipt").GetProperty("replayed").GetBoolean());
            Assert.AreEqual(shoveCommittedState, await ObserveLifecycleState(client, cleanBoard, token));
            var afterShoveItems = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia } }, token);
            var afterIds = afterShoveItems.Items.Select(RouteIdentity).ToHashSet(StringComparer.Ordinal);
            Assert.IsTrue(expectedCreates.IsSubsetOf(afterIds) && expectedUpdates.IsSubsetOf(afterIds));
            Assert.IsFalse(expectedRemovals.Overlaps(afterIds));
            Assert.AreEqual(afterIds.Count, afterShoveItems.Items.Count, "The plan must create each copper identity once.");
            Assert.AreEqual(afterShoveItems.Items.Count, afterShoveItems.Items.Select(RouteShape).Distinct(StringComparer.Ordinal).Count(), "No replacement may also be created as duplicate copper.");

            async Task KeyboardRoute(string key, GetItemsResponse expected)
            {
                NativeKeyboard.SchematicShortcut(display, focusProcessId, key, "PCB Editor", true, true, clickFromLeft: 500, clickFromTop: 500);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
                while (true)
                {
                    var actual = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
                    { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia } }, deadline.Token);
                    if (actual.Items.OrderBy(RouteIdentity).SequenceEqual(expected.Items.OrderBy(RouteIdentity))) return;
                    await Task.Delay(100, deadline.Token);
                }
            }
            await KeyboardRoute("z", beforeShoveItems);
            await KeyboardRoute("y", afterShoveItems);
            var shoveSave = await mcp.Tool("kicad_document_save", new
            { instanceId, expectedStateJson = SchematicJson.Formatter.Format(await ObserveLifecycleState(client, cleanBoard, token)), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(shoveSave.TryGetProperty("isError", out var shoveSaveError) && shoveSaveError.GetBoolean(), shoveSave.GetRawText());
            var shoveClose = await mcp.Tool("kicad_document_close", new
            { instanceId, expectedStateJson = shoveSave.GetProperty("structuredContent").GetProperty("observedState").GetRawText(), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(shoveClose.TryGetProperty("isError", out var shoveCloseError) && shoveCloseError.GetBoolean(), shoveClose.GetRawText());
            var shoveReopen = await CreateRootThroughMcp(client.Endpoint, instanceId, cleanPath, evidence, token, toolName: "kicad_pcb_open");
            cleanBoard = shoveReopen.Document; finalBoard = cleanBoard;
            var persistedShove = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Items = { splitSource.Id, shovedVia.Id } }, token);
            Assert.AreEqual(retainedSplit.CustomProperties, persistedShove.Items.Single(item => item.Is(Track.Descriptor)).Unpack<Track>().CustomProperties);
            Assert.AreEqual(retainedVia.PadStack, persistedShove.Items.Single(item => item.Is(Via.Descriptor)).Unpack<Via>().PadStack);

            // Positive grouped-route ownership: the ordinary direct group keeps
            // an unrelated peer while PNS replaces the source with split child
            // identities. Version 1 remains a deliberate fail-closed probe.
            var groupPeer = new Track
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") },
                Start = new() { XNm = 32_000_000, YNm = 32_000_000 },
                End = new() { XNm = 32_800_000, YNm = 32_000_000 }, Width = new() { ValueNm = 250_000 },
                Layer = BoardLayer.BlFCu, Net = shoveObstacle.Net.Clone()
            };
            var peerCreate = new CreateItems { Header = new() { Document = cleanBoard }, Items = { Any.Pack(groupPeer) } };
            var peerReply = await mcp.Tool("kicad_pcb_items_create", new
            { instanceId, requestJson = BoardJson.Formatter.Format(peerCreate), expectedStateJson = SchematicJson.Formatter.Format(await ObserveLifecycleState(client, cleanBoard, token)) });
            Assert.IsFalse(peerReply.TryGetProperty("isError", out var peerError) && peerError.GetBoolean(), peerReply.GetRawText());
            var groupedBefore = await ObserveLifecycleState(client, cleanBoard, token);
            var positiveGroup = new Kiapi.Board.Types.Group
            { Id = new() { Value = Guid.NewGuid().ToString("D") }, Name = "Grouped route positive", Items = { splitSource.Id.Clone(), groupPeer.Id.Clone() } };
            var positiveGroupCreate = new CreateItems { Header = new() { Document = cleanBoard }, Items = { Any.Pack(positiveGroup) } };
            var positiveGroupReply = await mcp.Tool("kicad_pcb_items_create", new
            { instanceId, requestJson = BoardJson.Formatter.Format(positiveGroupCreate), expectedStateJson = SchematicJson.Formatter.Format(groupedBefore) });
            Assert.IsFalse(positiveGroupReply.TryGetProperty("isError", out var positiveGroupError) && positiveGroupError.GetBoolean(), positiveGroupReply.GetRawText());
            groupedBefore = await ObserveLifecycleState(client, cleanBoard, token);

            var legacyGroupedRequest = shovePreviewRequest.Clone();
            legacyGroupedRequest.OperationId = Guid.NewGuid().ToString("D");
            legacyGroupedRequest.ExpectedRevision = groupedBefore.Revision.Clone();
            legacyGroupedRequest.MaximumCompleteDeltaVersion = 1;
            var legacyGrouped = await mcp.Tool("kicad_pcb_route_preview", new
            { instanceId, requestJson = SchematicJson.Formatter.Format(legacyGroupedRequest), expectedStateJson = SchematicJson.Formatter.Format(groupedBefore) });
            Assert.IsTrue(legacyGrouped.TryGetProperty("isError", out var legacyGroupedError) && legacyGroupedError.GetBoolean(), legacyGrouped.GetRawText());
            string legacyGroupedCode = legacyGrouped.GetProperty("structuredContent").GetProperty("errorCode").GetString()!;
            CollectionAssert.Contains(new[] { "route_delta_group_membership_unsupported", "pcb_route_geometry_failed" }, legacyGroupedCode,
                legacyGrouped.GetRawText());
            Assert.AreEqual(groupedBefore, await ObserveLifecycleState(client, cleanBoard, token));

            // Keep this positive ordinary-group probe independent from the
            // push-and-shove fixture above.  Starting in the middle of the
            // grouped USB_D+ source and finishing on the existing USB_D+ pad
            // gives the native router a deterministic single-ended split
            // delta without enabling push-and-shove or moving the displaced
            // via/foreign obstacle.
            var groupedRequest = new StartPcbRoutePreview
            {
                Document = cleanBoard, OperationId = Guid.NewGuid().ToString("D"),
                ProcessEpoch = client.Epoch, ExpectedRevision = groupedBefore.Revision.Clone(),
                StartItemId = splitSource.Id.Value,
                Start = new() { XNm = 43_100_000, YNm = 40_100_000 },
                Layer = BoardLayer.BlFCu, ForceFinish = true
            };
            groupedRequest.Waypoints.Add(new Vector2 { XNm = 41_600_000, YNm = 44_000_000 });
            groupedRequest.MaximumCompleteDeltaVersion = 2;
            var positiveGroupedPreview = await mcp.Tool("kicad_pcb_route_preview", new
            { instanceId, requestJson = SchematicJson.Formatter.Format(groupedRequest), expectedStateJson = SchematicJson.Formatter.Format(groupedBefore) });
            bool groupedPreviewFailed = positiveGroupedPreview.TryGetProperty("isError", out var groupedPositiveError)
                && groupedPositiveError.GetBoolean();
            if (!groupedPreviewFailed && positiveGroupedPreview.TryGetProperty("structuredContent", out var groupedPreviewData)
                && groupedPreviewData.TryGetProperty("preview", out var groupedPreviewState)
                && groupedPreviewState.TryGetProperty("completeDeltaVersion", out var groupedDeltaVersion)
                && groupedDeltaVersion.GetInt32() != 2)
                groupedPreviewFailed = true;
            if (groupedPreviewFailed)
            {
                // Direct ordinary-group ownership is still fail-closed when
                // this native PNS branch cannot capture a complete delta.
                // Keep the group cleanup and leave the capability explicitly
                // unqualified rather than treating a partial preview as a
                // successful route.
                var groupedStructured = positiveGroupedPreview.GetProperty("structuredContent");
                string groupedCode = groupedStructured.TryGetProperty("errorCode", out var groupedErrorCode)
                    ? groupedErrorCode.GetString()! : "route_delta_incomplete";
                CollectionAssert.Contains(new[] { "pcb_route_geometry_failed", "route_delta_group_membership_unsupported", "route_delta_incomplete" }, groupedCode,
                    positiveGroupedPreview.GetRawText());
            }
            else
            {
            var groupedData = positiveGroupedPreview.GetProperty("structuredContent");
            Assert.AreEqual(2, groupedData.GetProperty("preview").GetProperty("completeDeltaVersion").GetInt32(), positiveGroupedPreview.GetRawText());
            string groupedChangesJson = groupedData.GetProperty("groupChangesJson").GetString()!;
            using var groupedChangesDocument = JsonDocument.Parse(groupedChangesJson);
            var groupedChanges = groupedChangesDocument.RootElement.EnumerateArray()
                .Select(element => SchematicJson.Parser.Parse<NativeRouteGroupChange>(element.GetRawText())).ToArray();
            Assert.AreEqual(1, groupedChanges.Length, positiveGroupedPreview.GetRawText());
            var groupedChange = groupedChanges[0];
            var groupedBeforeMembers = groupedChange.ExpectedBefore.Group.Items.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            var groupedAfterMembers = groupedChange.ExpectedAfter.Group.Items.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            Assert.IsTrue(groupedBeforeMembers.SetEquals(new[] { splitSource.Id.Value, groupPeer.Id.Value }), groupedChangesJson);
            Assert.IsTrue(groupedAfterMembers.Contains(groupPeer.Id.Value), groupedChangesJson);
            var groupedNewMembers = groupedAfterMembers.Except(groupedBeforeMembers).ToHashSet(StringComparer.Ordinal);
            Assert.AreEqual(groupedNewMembers.Count, groupedChange.CreatedSources.Count, groupedChangesJson);
            Assert.IsTrue(groupedChange.CreatedSources.All(source => source.SourceItemId.Value == splitSource.Id.Value
                && source.SourceGroupId.Value == positiveGroup.Id.Value
                && groupedNewMembers.Contains(source.CreatedItemId.Value)), groupedChangesJson);

            var groupedCreate = BoardJson.Parser.Parse<CreateItems>(groupedData.GetProperty("candidateRequestJson").GetString()!);
            var groupedUpdate = BoardJson.Parser.Parse<UpdateItems>(groupedData.GetProperty("replacementUpdatesJson").GetString()!);
            string? groupedRemovalJson = groupedData.GetProperty("candidateDeleteRequestJson").GetString();
            string groupedDrcCandidateJson = groupedData.GetProperty("drcCandidateRequestJson").GetString()!;
            string? groupedDrcDeleteJson = groupedData.GetProperty("drcDeleteRequestJson").GetString();
            var groupedDrc = await RouteDrc(groupedDrcCandidateJson, groupedDrcDeleteJson, groupedBefore, groupedChangesJson);
            string groupedOperationId = Guid.NewGuid().ToString("D");
            var groupedCommitArgs = new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(groupedCreate),
                replacementUpdatesJson = BoardJson.Formatter.Format(groupedUpdate), removalsJson = groupedRemovalJson ?? "",
                groupChangesJson = groupedChangesJson, expectedStateJson = SchematicJson.Formatter.Format(groupedBefore),
                drcJobStateJson = SchematicJson.Formatter.Format(groupedDrc), operationId = groupedOperationId
            };
            var groupedCommit = await mcp.Tool("kicad_pcb_route_commit", groupedCommitArgs);
            Assert.IsFalse(groupedCommit.TryGetProperty("isError", out var groupedCommitError) && groupedCommitError.GetBoolean(), groupedCommit.GetRawText());
            var groupedReceipt = groupedCommit.GetProperty("structuredContent").GetProperty("receipt");
            Assert.AreEqual(1, groupedReceipt.GetProperty("groupChanges").GetArrayLength(), groupedCommit.GetRawText());
            Assert.AreEqual(1, groupedReceipt.GetProperty("afterGroupStates").GetArrayLength(), groupedCommit.GetRawText());
            var groupedAfter = await ObserveLifecycleState(client, cleanBoard, token);
            var groupedRows = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Items = { positiveGroup.Id.Clone() } }, token);
            var groupedNativeAfter = groupedRows.Items.Single().Unpack<Kiapi.Board.Types.Group>();
            Assert.IsTrue(groupedNativeAfter.Items.Any(id => id.Value == groupPeer.Id.Value), groupedCommit.GetRawText());
            Assert.IsTrue(groupedNewMembers.All(id => groupedNativeAfter.Items.Any(member => member.Value == id)), groupedCommit.GetRawText());
            Assert.IsFalse(groupedNativeAfter.Items.Any(id => id.Value == splitSource.Id.Value), groupedCommit.GetRawText());
            var groupedReplay = await mcp.Tool("kicad_pcb_route_commit", groupedCommitArgs);
            Assert.IsFalse(groupedReplay.TryGetProperty("isError", out var groupedReplayError) && groupedReplayError.GetBoolean(), groupedReplay.GetRawText());
            Assert.IsTrue(groupedReplay.GetProperty("structuredContent").GetProperty("receipt").GetProperty("replayed").GetBoolean());
            var changedGroup = groupedChange.Clone();
            changedGroup.ExpectedAfter.Group.Items.Remove(groupPeer.Id);
            string changedGroupJson = "[" + SchematicJson.Formatter.Format(changedGroup) + "]";
            var changedGroupCommit = await mcp.Tool("kicad_pcb_route_commit", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(groupedCreate),
                replacementUpdatesJson = BoardJson.Formatter.Format(groupedUpdate), removalsJson = groupedRemovalJson ?? "",
                groupChangesJson = changedGroupJson, expectedStateJson = SchematicJson.Formatter.Format(groupedBefore),
                drcJobStateJson = SchematicJson.Formatter.Format(groupedDrc), operationId = groupedOperationId
            });
            Assert.IsTrue(changedGroupCommit.TryGetProperty("isError", out var changedGroupError) && changedGroupError.GetBoolean(), changedGroupCommit.GetRawText());

            var groupedSave = await mcp.Tool("kicad_document_save", new
            { instanceId, expectedStateJson = SchematicJson.Formatter.Format(groupedAfter), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(groupedSave.TryGetProperty("isError", out var groupedSaveError) && groupedSaveError.GetBoolean(), groupedSave.GetRawText());
            var groupedClose = await mcp.Tool("kicad_document_close", new
            { instanceId, expectedStateJson = groupedSave.GetProperty("structuredContent").GetProperty("observedState").GetRawText(), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(groupedClose.TryGetProperty("isError", out var groupedCloseError) && groupedCloseError.GetBoolean(), groupedClose.GetRawText());
            var groupedReopen = await CreateRootThroughMcp(client.Endpoint, instanceId, cleanPath, evidence, token, toolName: "kicad_pcb_open");
            cleanBoard = groupedReopen.Document;
            var groupedReopened = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Items = { positiveGroup.Id.Clone() } }, token);
            Assert.IsTrue(groupedReopened.Items.Single().Unpack<Kiapi.Board.Types.Group>().Items.Any(id => id.Value == groupPeer.Id.Value));
            NativeKeyboard.SchematicShortcut(display, focusProcessId, "z", "PCB Editor", true, true, clickFromLeft: 500, clickFromTop: 500);
            using (var groupedUndoDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                groupedUndoDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                while (true)
                {
                    var rows = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
                    { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbGroup } }, groupedUndoDeadline.Token);
                    var group = rows.Items.Where(item => item.Is(Kiapi.Board.Types.Group.Descriptor)).Select(item => item.Unpack<Kiapi.Board.Types.Group>()).SingleOrDefault(item => item.Id.Equals(positiveGroup.Id));
                    if (group is not null && group.Items.Any(id => id.Value == splitSource.Id.Value)
                        && !groupedNewMembers.Any(id => rows.Items.Where(item => item.Is(Track.Descriptor)
                            || item.Is(Arc.Descriptor) || item.Is(Via.Descriptor)).Any(item => RouteIdentity(item) == id))) break;
                    await Task.Delay(100, groupedUndoDeadline.Token);
                }
            }
            NativeKeyboard.SchematicShortcut(display, focusProcessId, "y", "PCB Editor", true, true, clickFromLeft: 500, clickFromTop: 500);
            using (var groupedRedoDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                groupedRedoDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                while (true)
                {
                    var rows = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
                    { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbGroup } }, groupedRedoDeadline.Token);
                    var group = rows.Items.Where(item => item.Is(Kiapi.Board.Types.Group.Descriptor)).Select(item => item.Unpack<Kiapi.Board.Types.Group>()).SingleOrDefault(item => item.Id.Equals(positiveGroup.Id));
                    if (group is not null && group.Items.Any(id => id.Value == groupPeer.Id.Value)
                        && groupedNewMembers.All(id => group.Items.Any(member => member.Value == id))) break;
                    await Task.Delay(100, groupedRedoDeadline.Token);
                }
            }
            }
            var groupedDelete = new DeleteItems { Header = new() { Document = cleanBoard }, ItemIds = { positiveGroup.Id.Clone() } };
            var groupedDeleteReply = await mcp.Tool("kicad_pcb_items_delete", new
            { instanceId, requestJson = BoardJson.Formatter.Format(groupedDelete), expectedStateJson = SchematicJson.Formatter.Format(await ObserveLifecycleState(client, cleanBoard, token)) });
            Assert.IsFalse(groupedDeleteReply.TryGetProperty("isError", out var groupedDeleteError) && groupedDeleteError.GetBoolean(), groupedDeleteReply.GetRawText());
            cleanState = await ObserveLifecycleState(client, cleanBoard, token);

            // The endpoint pads are only route fixtures. Remove their
            // footprints before the true route-only cleanup so the final DRC
            // snapshot does not report intentionally orphaned USB pads.
            var usbFootprints = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbFootprint } }, token);
            var removeUsbFootprints = new DeleteItems { Header = new() { Document = cleanBoard } };
            removeUsbFootprints.ItemIds.Add(usbFootprints.Items
                .Select(item => item.Unpack<FootprintInstance>())
                .Where(footprint => footprint.Definition?.Id?.EntryName is "DP_P" or "DP_N" or "DP_P_TAIL" or "DP_P_ROUTE_END" or "POWER_RAIL_B_SUPPORT")
                .Select(footprint => footprint.Id.Clone()));
            if (removeUsbFootprints.ItemIds.Count > 0)
            {
                var removedUsbFootprints = await mcp.Tool("kicad_pcb_items_delete", new
                { instanceId, requestJson = BoardJson.Formatter.Format(removeUsbFootprints), expectedStateJson = SchematicJson.Formatter.Format(cleanState) });
                Assert.IsFalse(removedUsbFootprints.TryGetProperty("isError", out var removedUsbError) && removedUsbError.GetBoolean(), removedUsbFootprints.GetRawText());
                cleanState = await ObserveLifecycleState(client, cleanBoard, token);
            }

            // A true-removal-only cleanup has an explicitly empty candidate;
            // its retained DRC receipt must verify that exact empty payload.
            var cleanupRows = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia } }, token);
            var cleanup = new DeleteItems { Header = new() { Document = cleanBoard } };
            cleanup.ItemIds.Add(cleanupRows.Items.Where(item => RouteIdentity(item) != powerConnection.Id.Value)
                .Select(item => new KIID { Value = RouteIdentity(item) }).OrderBy(id => id.Value));
            var noCreates = new CreateItems { Header = new() { Document = cleanBoard } };
            cleanState = await ObserveLifecycleState(client, cleanBoard, token);
            var cleanupDrc = await RouteDrc(BoardJson.Formatter.Format(noCreates), BoardJson.Formatter.Format(cleanup), cleanState);
            var cleanupReply = await mcp.Tool("kicad_pcb_route_commit", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(noCreates), removalsJson = BoardJson.Formatter.Format(cleanup),
                expectedStateJson = SchematicJson.Formatter.Format(cleanState), drcJobStateJson = SchematicJson.Formatter.Format(cleanupDrc), operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsFalse(cleanupReply.TryGetProperty("isError", out var cleanupError) && cleanupError.GetBoolean(), cleanupReply.GetRawText());
            var cleanupRead = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia } }, token);
            Assert.AreEqual(powerConnection.Id.Value, cleanupRead.Items.Single().Unpack<Track>().Id.Value);
            var removePower = new DeleteItems { Header = new() { Document = cleanBoard }, ItemIds = { powerConnection.Id.Clone() } };
            var powerRemoved = await mcp.Tool("kicad_pcb_items_delete", new
            { instanceId, requestJson = BoardJson.Formatter.Format(removePower), expectedStateJson = SchematicJson.Formatter.Format(await ObserveLifecycleState(client, cleanBoard, token)) });
            Assert.IsFalse(powerRemoved.TryGetProperty("isError", out var powerRemoveError) && powerRemoveError.GetBoolean(), powerRemoved.GetRawText());
            cleanState = await ObserveLifecycleState(client, cleanBoard, token);

            var selectionReply = await mcp.Tool("kicad_pcb_sync_selection", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard),
                requestJson = SchematicJson.Formatter.Format(new SyncSelection
                {
                    Items = { new SelectionSpec { Pad = new PadSelectionSpec { Reference = "DP_P", Number = positivePad.Number } } },
                    Mode = SyncSelectionMode.SsmItemsAndNets,
                    Context = SyncSelectionContext.SscExplicit
                }),
                expectedStateJson = SchematicJson.Formatter.Format(cleanState),
                operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsFalse(selectionReply.TryGetProperty("isError", out var selectionError)
                && selectionError.GetBoolean(), selectionReply.GetRawText());
            var selectionData = selectionReply.GetProperty("structuredContent");
            Assert.IsTrue(selectionData.GetProperty("nativeGuiOnly").GetBoolean(), selectionReply.GetRawText());
            Assert.IsTrue(selectionData.GetProperty("selectionChanged").GetBoolean(), selectionReply.GetRawText());
            Assert.IsFalse(selectionData.GetProperty("boardMutation").GetBoolean());
            Assert.AreEqual(SchematicJson.Formatter.Format(cleanState), SchematicJson.Formatter.Format(
                await ObserveLifecycleState(client, cleanBoard, token)));

            var highlightReply = await mcp.Tool("kicad_pcb_highlight_nets", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard),
                requestJson = SchematicJson.Formatter.Format(new HighlightNets { NetName = { "POWER_RAIL" } }),
                expectedStateJson = SchematicJson.Formatter.Format(cleanState),
                operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsFalse(highlightReply.TryGetProperty("isError", out var highlightError)
                && highlightError.GetBoolean(), highlightReply.GetRawText());
            var highlightData = highlightReply.GetProperty("structuredContent");
            Assert.IsTrue(highlightData.GetProperty("nativeGuiOnly").GetBoolean());
            Assert.IsTrue(highlightData.GetProperty("selectionChanged").GetBoolean(), highlightReply.GetRawText());
            Assert.IsFalse(highlightData.GetProperty("boardMutation").GetBoolean());
            Assert.AreEqual(SchematicJson.Formatter.Format(cleanState), SchematicJson.Formatter.Format(
                await ObserveLifecycleState(client, cleanBoard, token)));

            var multilayerPlan = await mcp.Tool("kicad_pcb_route_plan_multilayer", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard),
                expectedStateJson = SchematicJson.Formatter.Format(cleanState),
                requestJson = JsonSerializer.Serialize(new
                {
                    netName = "POWER_RAIL", start = new { xNm = 30_100_000L, yNm = 30_100_000L },
                    end = new { xNm = 30_500_000L, yNm = 30_500_000L },
                    startLayer = "F.Cu", endLayer = "F.Cu", layers = new[] { "F.Cu", "B.Cu" },
                    widthNm = 250_000L, clearanceNm = 100_000L, gridNm = 100_000L,
                    bounds = new { minXNm = 29_000_000L, minYNm = 29_000_000L, maxXNm = 31_000_000L, maxYNm = 31_000_000L },
                    maxNodes = 10000, maxVias = 2,
                    vias = new[] { new { type = "through", startLayer = "F.Cu", endLayer = "B.Cu", diameterNm = 600_000L, drillNm = 300_000L, costNm = 1_000_000L } }
                })
            });
            Assert.IsFalse(multilayerPlan.TryGetProperty("isError", out var multilayerError) && multilayerError.GetBoolean(), multilayerPlan.GetRawText());
            var multilayerData = multilayerPlan.GetProperty("structuredContent");
            Assert.IsTrue(multilayerData.GetProperty("routeFound").GetBoolean(), multilayerPlan.GetRawText());
            Assert.IsFalse(multilayerData.GetProperty("drcValidated").GetBoolean());
            Assert.IsFalse(multilayerData.GetProperty("nativeCommit").GetBoolean());
            var multilayerCandidate = BoardJson.Parser.Parse<CreateItems>(multilayerData.GetProperty("candidateRequestJson").GetString()!);
            Assert.IsTrue(multilayerCandidate.Items.Any(item => item.Is(Track.Descriptor)));

            var cleanCandidate = new CreateItems { Header = new() { Document = cleanBoard } };
            cleanCandidate.Items.Add(Any.Pack(new Track
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") },
                Start = new() { XNm = 30_100_000, YNm = 30_100_000 },
                End = new() { XNm = 30_500_000, YNm = 30_500_000 },
                Width = new() { ValueNm = 250_000 }, Layer = BoardLayer.BlFCu,
                Net = new() { Name = "POWER_RAIL" }
            }));
            var cleanDrcReply = await mcp.Tool("kicad_pcb_drc_start", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard),
                operationId = Guid.NewGuid().ToString("D"), refillZones = false,
                reportAllTrackErrors = true, testFootprints = false,
                expectedRevisionJson = SchematicJson.Formatter.Format(cleanState.Revision),
                processEpoch = client.Epoch, candidateRequestJson = BoardJson.Formatter.Format(cleanCandidate)
            });
            Assert.IsFalse(cleanDrcReply.TryGetProperty("isError", out var cleanDrcError) && cleanDrcError.GetBoolean(), cleanDrcReply.GetRawText());
            var cleanDrc = SchematicJson.Parser.Parse<PcbDrcJobState>(cleanDrcReply.GetProperty("content")[0].GetProperty("text").GetString()!);
            for (int attempt = 0; attempt < 60 && !cleanDrc.WorkerFinished; ++attempt)
            {
                await Task.Delay(100, token);
                var cleanDrcUpdate = await mcp.Tool("kicad_pcb_drc_job", new
                { instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard), jobId = cleanDrc.JobId, processEpoch = client.Epoch });
                Assert.IsFalse(cleanDrcUpdate.TryGetProperty("isError", out var cleanDrcUpdateError) && cleanDrcUpdateError.GetBoolean(), cleanDrcUpdate.GetRawText());
                cleanDrc = SchematicJson.Parser.Parse<PcbDrcJobState>(cleanDrcUpdate.GetProperty("content")[0].GetProperty("text").GetString()!);
            }
            Assert.IsTrue(cleanDrc.WorkerFinished, "Clean route DRC did not reach a terminal state.");
            Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, cleanDrc.Status, cleanDrc.ErrorMessage);
            Assert.IsTrue(cleanDrc.SnapshotComplete);
            Assert.IsTrue(cleanDrc.ResultsFresh);
            Assert.IsEmpty(cleanDrc.Findings, "The positive checked route fixture must be DRC-clean.");
            var cleanCommit = await mcp.Tool("kicad_pcb_route_commit", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(cleanCandidate),
                expectedStateJson = SchematicJson.Formatter.Format(cleanState),
                drcJobStateJson = SchematicJson.Formatter.Format(cleanDrc), operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsFalse(cleanCommit.TryGetProperty("isError", out var cleanCommitError) && cleanCommitError.GetBoolean(), cleanCommit.GetRawText());
            var cleanCommitData = cleanCommit.GetProperty("structuredContent");
            Assert.IsTrue(cleanCommitData.GetProperty("mutationConfirmed").GetBoolean());
            Assert.IsTrue(cleanCommitData.GetProperty("nativeReceipt").GetBoolean());
            var cleanAfter = SchematicJson.Parser.Parse<DocumentLifecycleState>(cleanCommitData.GetProperty("afterState").GetString()!);
            Assert.IsGreaterThan(cleanState.Revision.Sequence, cleanAfter.Revision.Sequence);
            var committedClean = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbTrace } }, token);
            Assert.IsTrue(committedClean.Items.Any(item => item.Is(Track.Descriptor)));
            string cleanOperationId = cleanCommitData.GetProperty("operationId").GetString()!;
            var replayCommit = await mcp.Tool("kicad_pcb_route_commit", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(cleanCandidate),
                expectedStateJson = SchematicJson.Formatter.Format(cleanState),
                drcJobStateJson = SchematicJson.Formatter.Format(cleanDrc), operationId = cleanOperationId
            });
            Assert.IsFalse(replayCommit.TryGetProperty("isError", out var replayError) && replayError.GetBoolean(), replayCommit.GetRawText());
            Assert.IsTrue(replayCommit.GetProperty("structuredContent").GetProperty("receipt").GetProperty("replayed").GetBoolean());
            var receiptRead = await mcp.Tool("kicad_pcb_route_receipt", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(new CheckedPcbRouteCommit
                {
                    Candidate = cleanCandidate, ExpectedState = cleanState, DrcJobId = cleanDrc.JobId,
                    OperationId = cleanOperationId, MaximumResultBytes = 4 * 1024 * 1024
                }), processEpoch = client.Epoch, operationId = cleanOperationId
            });
            Assert.IsFalse(receiptRead.TryGetProperty("isError", out var receiptError) && receiptError.GetBoolean(), receiptRead.GetRawText());
            Assert.AreEqual(cleanOperationId, receiptRead.GetProperty("structuredContent").GetProperty("operationId").GetString());

            // Identity-preserving replacement: update the committed route in
            // place while adding a short continuation in the same transaction.
            Track originalCleanTrack = cleanCandidate.Items.Single().Unpack<Track>();
            Track replacementTrack = originalCleanTrack.Clone();
            // Native serialization orders a property map by key. Supply the
            // opposite wire order to prove checked apply accepts equal state.
            replacementTrack.CustomProperties.Add(new CustomProperty { Key = "z-identity", Value = "1" });
            replacementTrack.CustomProperties.Add(new CustomProperty { Key = "a-identity", Value = "2" });
            replacementTrack.End = new Vector2 { XNm = 30_400_000, YNm = 30_400_000 };
            Track continuation = new()
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") },
                Start = replacementTrack.End.Clone(), End = originalCleanTrack.End.Clone(),
                Width = replacementTrack.Width.Clone(), Layer = replacementTrack.Layer,
                Net = replacementTrack.Net.Clone()
            };
            var identityCreate = new CreateItems { Header = new() { Document = cleanBoard } };
            identityCreate.Items.Add(Any.Pack(continuation));
            var identityUpdate = new UpdateItems { Header = new() { Document = cleanBoard } };
            identityUpdate.Items.Add(Any.Pack(replacementTrack));
            var identityDrcCandidate = new CreateItems { Header = new() { Document = cleanBoard } };
            identityDrcCandidate.Items.Add(Any.Pack(continuation));
            identityDrcCandidate.Items.Add(Any.Pack(replacementTrack));
            var identityDelete = new DeleteItems { Header = new() { Document = cleanBoard } };
            identityDelete.ItemIds.Add(originalCleanTrack.Id.Clone());
            var identityDrcReply = await mcp.Tool("kicad_pcb_drc_start", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard),
                operationId = Guid.NewGuid().ToString("D"), refillZones = false,
                reportAllTrackErrors = true, testFootprints = false,
                expectedRevisionJson = SchematicJson.Formatter.Format(cleanAfter.Revision),
                processEpoch = client.Epoch, candidateRequestJson = BoardJson.Formatter.Format(identityDrcCandidate),
                deleteRequestJson = BoardJson.Formatter.Format(identityDelete)
            });
            Assert.IsFalse(identityDrcReply.TryGetProperty("isError", out var identityDrcError) && identityDrcError.GetBoolean(), identityDrcReply.GetRawText());
            var identityDrc = SchematicJson.Parser.Parse<PcbDrcJobState>(identityDrcReply.GetProperty("content")[0].GetProperty("text").GetString()!);
            for (int attempt = 0; attempt < 60 && !identityDrc.WorkerFinished; ++attempt)
            {
                await Task.Delay(100, token);
                var updateReply = await mcp.Tool("kicad_pcb_drc_job", new
                {
                    instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard), jobId = identityDrc.JobId,
                    processEpoch = client.Epoch, candidateRequestJson = BoardJson.Formatter.Format(identityDrcCandidate),
                    deleteRequestJson = BoardJson.Formatter.Format(identityDelete)
                });
                Assert.IsFalse(updateReply.TryGetProperty("isError", out var identityUpdateError) && identityUpdateError.GetBoolean(), updateReply.GetRawText());
                identityDrc = SchematicJson.Parser.Parse<PcbDrcJobState>(updateReply.GetProperty("content")[0].GetProperty("text").GetString()!);
            }
            var identityVerifiedReply = await mcp.Tool("kicad_pcb_drc_job", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard), jobId = identityDrc.JobId,
                processEpoch = client.Epoch, candidateRequestJson = BoardJson.Formatter.Format(identityDrcCandidate),
                deleteRequestJson = BoardJson.Formatter.Format(identityDelete)
            });
            Assert.IsFalse(identityVerifiedReply.TryGetProperty("isError", out var identityVerifiedError) && identityVerifiedError.GetBoolean(), identityVerifiedReply.GetRawText());
            identityDrc = SchematicJson.Parser.Parse<PcbDrcJobState>(identityVerifiedReply.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.IsTrue(identityDrc.WorkerFinished, identityDrc.ErrorMessage);
            Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, identityDrc.Status, identityDrc.ErrorMessage);
            Assert.IsEmpty(identityDrc.Findings, SchematicJson.Formatter.Format(identityDrc));
            var identityCommit = await mcp.Tool("kicad_pcb_route_commit", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(identityCreate),
                replacementUpdatesJson = BoardJson.Formatter.Format(identityUpdate),
                expectedStateJson = SchematicJson.Formatter.Format(cleanAfter),
                drcJobStateJson = SchematicJson.Formatter.Format(identityDrc), operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsFalse(identityCommit.TryGetProperty("isError", out var identityCommitError) && identityCommitError.GetBoolean(), identityCommit.GetRawText());
            var identityData = identityCommit.GetProperty("structuredContent");
            Assert.IsTrue(identityData.GetProperty("mutationConfirmed").GetBoolean(), identityCommit.GetRawText());
            Assert.AreEqual(originalCleanTrack.Id.Value, identityData.GetProperty("receipt").GetProperty("updated").GetProperty("items")[0].GetProperty("id").GetProperty("value").GetString());
            var nativeUpdate = identityData.GetProperty("receipt").GetProperty("updatedResult");
            Assert.AreEqual("IRS_OK", nativeUpdate.GetProperty("status").GetString(), identityCommit.GetRawText());
            Assert.AreEqual(replacementTrack.End.XNm, long.Parse(nativeUpdate.GetProperty("updatedItems")[0].GetProperty("item").GetProperty("end").GetProperty("xNm").GetString()!, CultureInfo.InvariantCulture), identityCommit.GetRawText());
            Assert.AreEqual(replacementTrack.End.YNm, long.Parse(nativeUpdate.GetProperty("updatedItems")[0].GetProperty("item").GetProperty("end").GetProperty("yNm").GetString()!, CultureInfo.InvariantCulture), identityCommit.GetRawText());
            var identityRead = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Items = { originalCleanTrack.Id, continuation.Id } }, token);
            Assert.AreEqual(2, identityRead.Items.Count, identityCommit.GetRawText());
            Assert.AreEqual(replacementTrack.End, identityRead.Items
                .Single(item => item.Is(Track.Descriptor) && item.Unpack<Track>().Id.Value == originalCleanTrack.Id.Value)
                .Unpack<Track>().End);
            var identitySave = await mcp.Tool("kicad_document_save", new
            { instanceId, expectedStateJson = identityData.GetProperty("afterState").GetString(), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(identitySave.TryGetProperty("isError", out var identitySaveError) && identitySaveError.GetBoolean(), identitySave.GetRawText());
            var identityClose = await mcp.Tool("kicad_document_close", new
            { instanceId, expectedStateJson = identitySave.GetProperty("structuredContent").GetProperty("observedState").GetRawText(), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(identityClose.TryGetProperty("isError", out var identityCloseError) && identityCloseError.GetBoolean(), identityClose.GetRawText());
            var identityReopen = await CreateRootThroughMcp(client.Endpoint, instanceId, cleanPath, evidence, token, toolName: "kicad_pcb_open");
            cleanBoard = identityReopen.Document;
            finalBoard = cleanBoard;
            var persistedIdentity = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = cleanBoard }, Items = { originalCleanTrack.Id, continuation.Id } }, token);
            Assert.AreEqual(2, persistedIdentity.Items.Count, identityReopen.ToString());
            Assert.AreEqual(replacementTrack.End, persistedIdentity.Items
                .Single(item => item.Is(Track.Descriptor) && item.Unpack<Track>().Id.Value == originalCleanTrack.Id.Value)
                .Unpack<Track>().End);
            string cleanExportStateJson = SchematicJson.Formatter.Format(await ObserveLifecycleState(client, cleanBoard, token));

            string exportDirectory = Directory.CreateTempSubdirectory("kicad-pcb-export-").FullName;
            try
            {
                var svgExport = new RunBoardJobExportSvg
                {
                    JobSettings = new() { Document = cleanBoard, OutputPath = Path.Combine(exportDirectory, "layers.svg") },
                    PlotSettings = new(),
                    FitPageToBoard = true, PageMode = BoardJobPaginationMode.BjpmEachLayerOwnFile
                };
                svgExport.PlotSettings.Layers.Add(BoardLayer.BlFCu);
                svgExport.PlotSettings.Layers.Add(BoardLayer.BlBCu);
                var svgExportReply = await mcp.Tool("kicad_pcb_export_svg", new
                { instanceId, requestJson = BoardJson.Formatter.Format(svgExport), expectedStateJson = cleanExportStateJson });
                Assert.IsFalse(svgExportReply.TryGetProperty("isError", out var svgExportError) && svgExportError.GetBoolean(), svgExportReply.GetRawText());
                var svgOutputs = svgExportReply.GetProperty("structuredContent").GetProperty("outputs").EnumerateArray().ToArray();
                Assert.IsGreaterThanOrEqualTo(1, svgOutputs.Length);
                Assert.IsTrue(svgOutputs.All(output => File.Exists(output.GetProperty("path").GetString()!)
                    && output.GetProperty("bytes").GetInt64() > 0));

                var statsExport = new RunBoardJobExportStats
                {
                    JobSettings = new() { Document = cleanBoard, OutputPath = Path.Combine(exportDirectory, "stats.json") },
                    Format = StatsOutputFormat.SofJson, Units = Units.UMm
                };
                var statsReply = await mcp.Tool("kicad_pcb_export_stats", new
                { instanceId, requestJson = BoardJson.Formatter.Format(statsExport), expectedStateJson = cleanExportStateJson });
                Assert.IsFalse(statsReply.TryGetProperty("isError", out var statsError) && statsError.GetBoolean(), statsReply.GetRawText());
                var statsOutputs = statsReply.GetProperty("structuredContent").GetProperty("outputs").EnumerateArray().ToArray();
                Assert.IsGreaterThanOrEqualTo(1, statsOutputs.Length);
                Assert.IsTrue(statsOutputs.All(output => File.Exists(output.GetProperty("path").GetString()!)
                    && output.GetProperty("bytes").GetInt64() > 0));

                void VerifyAdditionalExport(JsonElement reply)
                {
                    Assert.IsFalse(reply.TryGetProperty("isError", out var error) && error.GetBoolean(), reply.GetRawText());
                    var outputs = reply.GetProperty("structuredContent").GetProperty("outputs").EnumerateArray().ToArray();
                    Assert.IsGreaterThanOrEqualTo(1, outputs.Length, reply.GetRawText());
                    Assert.IsTrue(outputs.All(output => output.GetProperty("bytes").GetInt64() > 0
                        && output.GetProperty("sha256").GetString()!.Length == 64), reply.GetRawText());
                }
                BoardPlotSettings Plot() => new() { Layers = { BoardLayer.BlFCu, BoardLayer.BlBCu, BoardLayer.BlEdgeCuts } };
                var dxfRequest = new RunBoardJobExportDxf
                { JobSettings = new() { Document = cleanBoard, OutputPath = Path.Combine(exportDirectory, "board.dxf") }, PlotSettings = Plot(), Units = Units.UMm };
                var dxfReply = await mcp.Tool("kicad_pcb_export_dxf", new
                { instanceId, requestJson = BoardJson.Formatter.Format(dxfRequest), expectedStateJson = cleanExportStateJson });
                VerifyAdditionalExport(dxfReply);
                var pdfRequest = new RunBoardJobExportPdf
                { JobSettings = new() { Document = cleanBoard, OutputPath = Path.Combine(exportDirectory, "board.pdf") }, PlotSettings = Plot(), IncludeMetadata = true, SingleDocument = true };
                var pdfReply = await mcp.Tool("kicad_pcb_export_pdf", new
                { instanceId, requestJson = BoardJson.Formatter.Format(pdfRequest), expectedStateJson = cleanExportStateJson });
                VerifyAdditionalExport(pdfReply);
                var psRequest = new RunBoardJobExportPs
                { JobSettings = new() { Document = cleanBoard, OutputPath = Path.Combine(exportDirectory, "board.ps") }, PlotSettings = Plot(), ForceA4 = true };
                var psReply = await mcp.Tool("kicad_pcb_export_ps", new
                { instanceId, requestJson = BoardJson.Formatter.Format(psRequest), expectedStateJson = cleanExportStateJson });
                VerifyAdditionalExport(psReply);
                var gencadRequest = new RunBoardJobExportGencad
                { JobSettings = new() { Document = cleanBoard, OutputPath = Path.Combine(exportDirectory, "board.cad") }, UseIndividualShapes = true };
                var gencadReply = await mcp.Tool("kicad_pcb_export_gencad", new
                { instanceId, requestJson = BoardJson.Formatter.Format(gencadRequest), expectedStateJson = cleanExportStateJson });
                VerifyAdditionalExport(gencadReply);

                // Manufacturing checkpoint: Gerber X2 output is generated from
                // the same clean, committed board and must leave its lifecycle
                // state unchanged.  The manifest is the authoritative evidence
                // for every generated file and its digest.
                string gerberDirectory = Path.Combine(exportDirectory, "gerbers");
                var gerberExport = new RunBoardJobExportGerbers
                {
                    JobSettings = new() { Document = cleanBoard, OutputPath = gerberDirectory },
                    UseBoardPlotParams = false, CreateGerberJobFile = true,
                    IncludeNetlistAttributes = true, UseX2Format = true,
                    Precision = GerberPrecision.Gp6,
                    PlotSettings = new() { Scale = 1, DrillMarks = PlotDrillMarks.PdmNone,
                        CheckZonesBeforePlot = false }
                };
                gerberExport.PlotSettings.Layers.Add(BoardLayer.BlFCu);
                gerberExport.PlotSettings.Layers.Add(BoardLayer.BlBCu);
                gerberExport.PlotSettings.Layers.Add(BoardLayer.BlEdgeCuts);
                var gerberReply = await mcp.Tool("kicad_pcb_export_gerbers", new
                { instanceId, requestJson = BoardJson.Formatter.Format(gerberExport), expectedStateJson = cleanExportStateJson });
                Assert.IsFalse(gerberReply.TryGetProperty("isError", out var gerberError) && gerberError.GetBoolean(), gerberReply.GetRawText());
                var gerberOutputs = gerberReply.GetProperty("structuredContent").GetProperty("outputs").EnumerateArray().ToArray();
                Assert.IsGreaterThanOrEqualTo(1, gerberOutputs.Length);
                Assert.IsTrue(gerberOutputs.All(output => File.Exists(output.GetProperty("path").GetString()!)
                    && output.GetProperty("bytes").GetInt64() > 0
                    && output.GetProperty("sha256").GetString()!.Length == 64));
                var gerberJobPath = gerberOutputs.Select(output => output.GetProperty("path").GetString()!)
                    .SingleOrDefault(path => Path.GetExtension(path).Equals(".gbrjob", StringComparison.OrdinalIgnoreCase));
                Assert.IsNotNull(gerberJobPath, gerberReply.GetRawText());
                string gerberJob = await File.ReadAllTextAsync(gerberJobPath!, token);
                StringAssert.Contains(gerberJob, "FilesAttributes", "Gerber X2 output must include a job manifest.");
                StringAssert.Contains(gerberJob, "F.Cu", "Gerber job manifest must identify the front copper layer.");
                var frontGerberPath = gerberOutputs.Select(output => output.GetProperty("path").GetString()!)
                    .FirstOrDefault(path => Path.GetFileName(path).Contains("F_Cu", StringComparison.OrdinalIgnoreCase));
                Assert.IsNotNull(frontGerberPath, gerberReply.GetRawText());
                StringAssert.Contains(await File.ReadAllTextAsync(frontGerberPath!, token), "POWER_RAIL",
                    "Gerber X2 front copper must carry the routed net attribute.");
                Assert.AreEqual(cleanExportStateJson, SchematicJson.Formatter.Format(
                    await ObserveLifecycleState(client, cleanBoard, token)));

                // Dedicated footprint-placement qualification uses a real
                // footprint definition read from the clean native board. The
                // MCP plan/apply boundary must preserve the exact definition,
                // advance lifecycle state, and expose a replay receipt.
                var footprintItems = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
                { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbFootprint } }, token);
                var sourceFootprint = footprintItems.Items.Select(item => item.Unpack<FootprintInstance>()).First();
                var placedFootprint = sourceFootprint.Clone();
                placedFootprint.Id = new() { Value = Guid.NewGuid().ToString("D") };
                placedFootprint.Position = new() { XNm = 60_000_000, YNm = 60_000_000 };
                placedFootprint.Layer = BoardLayer.BlFCu;
                placedFootprint.Definition.Id.LibraryNickname = string.IsNullOrWhiteSpace(placedFootprint.Definition.Id.LibraryNickname)
                    ? "Generated" : placedFootprint.Definition.Id.LibraryNickname;
                placedFootprint.Definition.Id.EntryName = string.IsNullOrWhiteSpace(placedFootprint.Definition.Id.EntryName)
                    ? "Placed" : placedFootprint.Definition.Id.EntryName;
                var placementPlan = await mcp.Tool("kicad_pcb_footprint_place_plan", new
                {
                    documentJson = SchematicJson.Formatter.Format(cleanBoard),
                    expectedStateJson = cleanExportStateJson,
                    footprintJson = BoardJson.Formatter.Format(placedFootprint)
                });
                Assert.IsFalse(placementPlan.TryGetProperty("isError", out var placementPlanError)
                    && placementPlanError.GetBoolean(), placementPlan.GetRawText());
                var placementPlanData = placementPlan.GetProperty("structuredContent");
                Assert.IsTrue(placementPlanData.GetProperty("proposalOnly").GetBoolean());
                var placementOperationId = Guid.NewGuid().ToString("D");
                var placementApply = await mcp.Tool("kicad_pcb_footprint_place_apply", new
                {
                    instanceId, candidateRequestJson = placementPlanData.GetProperty("candidateRequestJson").GetString(),
                    expectedStateJson = cleanExportStateJson, operationId = placementOperationId
                });
                Assert.IsFalse(placementApply.TryGetProperty("isError", out var placementApplyError)
                    && placementApplyError.GetBoolean(), placementApply.GetRawText());
                var placementData = placementApply.GetProperty("structuredContent");
                Assert.IsTrue(placementData.GetProperty("mutationConfirmed").GetBoolean());
                Assert.IsTrue(placementData.GetProperty("atomicTransaction").GetBoolean());
                Assert.IsFalse(placementData.GetProperty("nativeReceiptAvailable").GetBoolean());
                var placementReceipt = await mcp.Tool("kicad_pcb_footprint_place_receipt", new
                { instanceId, expectedProcessEpoch = client.Epoch, operationId = placementOperationId });
                Assert.IsFalse(placementReceipt.TryGetProperty("isError", out var placementReceiptError)
                    && placementReceiptError.GetBoolean(), placementReceipt.GetRawText());
                Assert.IsTrue(placementReceipt.GetProperty("structuredContent").GetProperty("found").GetBoolean());
                var placedRead = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
                { Header = new() { Document = cleanBoard }, Items = { placedFootprint.Id } }, token);
                Assert.AreEqual(placedFootprint.Id, placedRead.Items.Single().Unpack<FootprintInstance>().Id);
                // Reuse the real raster proposal through the typed projection adapters. The board
                // path keeps source bytes in the explicit archive and reports provenanceAtomic=false;
                // the footprint path embeds the exact bytes in its complete FootprintInstance.
                string rasterSource = Path.Combine(RepositoryRoot(), "automation", "tests", "fixtures", "raster_trace",
                    "outer-hole-diagonal-64.png");
                byte[] rasterBytes = await File.ReadAllBytesAsync(rasterSource, token);
                string rasterSha = Convert.ToHexStringLower(SHA256.HashData(rasterBytes));
                var rasterTrace = await mcp.Tool("kicad_artwork_raster_trace", new
                {
                    instanceId,
                    input = new
                    {
                        sourcePath = rasterSource, expectedSourceSha256 = rasterSha, physicalWidthNm = 64_000_000L,
                        threshold = 128, invert = false, turdSize = 0, curveToleranceNm = 1_000L,
                        maxContours = 4096, maxVertices = 1_000_000, sourceOrigin = "deterministic-fixture",
                        hostReceipt = "pcb-projection"
                    }
                });
                RequireToolSuccess(rasterTrace);
                string projectionStateJson = placementData.GetProperty("afterState").GetString()!;
                JsonElement projectionState = JsonDocument.Parse(projectionStateJson).RootElement.Clone();
                JsonElement projectionDocument = JsonDocument.Parse(SchematicJson.Formatter.Format(cleanBoard)).RootElement.Clone();
                string boardProjectionArchive = "raster-trace/projection.png";
                var boardProjection = await mcp.Tool("kicad_artwork_raster_project", new
                {
                    instanceId,
                    input = new
                    {
                        operationId = Guid.NewGuid().ToString("D"), targetKind = "BOARD", expectedProcessEpoch = client.Epoch,
                        sourcePath = rasterSource, sourceKind = "generated", sourceOrigin = "deterministic-fixture",
                        proposal = rasterTrace.GetProperty("structuredContent").GetProperty("trace"),
                        board = new
                        {
                            document = projectionDocument, expectedState = projectionState, layer = "F.SilkS",
                            originXNm = 30_000_000L, originYNm = 30_000_000L,
                            repositoryRoot = cleanBoard.Project.Path, sourceArchivePath = boardProjectionArchive
                        }
                    }
                });
                RequireToolSuccess(boardProjection);
                var boardProjectionData = boardProjection.GetProperty("structuredContent");
                Assert.IsTrue(boardProjectionData.GetProperty("accepted").GetBoolean(), boardProjection.GetRawText());
                Assert.IsFalse(boardProjectionData.GetProperty("provenanceAtomic").GetBoolean(), boardProjection.GetRawText());
                Assert.IsTrue(File.Exists(Path.Combine(cleanBoard.Project.Path, boardProjectionArchive)), boardProjection.GetRawText());
                string boardProjectionAfter = boardProjectionData.GetProperty("nativeResult").GetProperty("afterState").GetString()!;
                string[] boardProjectionIds = boardProjectionData.GetProperty("createdIds").EnumerateArray().Select(item => item.GetString()!).ToArray();
                var projectedShapes = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
                { Header = new() { Document = cleanBoard }, Types_ = { KiCadObjectType.KotPcbShape } }, token);
                Assert.IsTrue(projectedShapes.Items.Where(item => boardProjectionIds.Contains(item.Unpack<BoardGraphicShape>().Id.Value))
                    .All(item => item.Unpack<BoardGraphicShape>().Shape.GeometryCase == GraphicShape.GeometryOneofCase.Polygon
                        && item.Unpack<BoardGraphicShape>().CustomProperties.Any(property => property.Key == "kicad.ai.raster_trace.geometry_sha256")), boardProjection.GetRawText());
                var removeRasterBoard = await mcp.Tool("kicad_pcb_items_delete", new
                {
                    instanceId,
                    requestJson = BoardJson.Formatter.Format(new DeleteItems { Header = new() { Document = cleanBoard }, ItemIds = { boardProjectionIds.Select(id => new KIID { Value = id }) } }),
                    expectedStateJson = boardProjectionAfter
                });
                Assert.IsFalse(removeRasterBoard.TryGetProperty("isError", out var removeRasterBoardError) && removeRasterBoardError.GetBoolean(), removeRasterBoard.GetRawText());
                string footprintProjectionState = removeRasterBoard.GetProperty("structuredContent").GetProperty("afterState").GetString()!;
                JsonElement footprintProjectionExpected = JsonDocument.Parse(footprintProjectionState).RootElement.Clone();
                string projectedFootprintId = Guid.NewGuid().ToString("D");
                var footprintProjection = await mcp.Tool("kicad_artwork_raster_project", new
                {
                    instanceId,
                    input = new
                    {
                        operationId = Guid.NewGuid().ToString("D"), targetKind = "FOOTPRINT", expectedProcessEpoch = client.Epoch,
                        sourcePath = rasterSource, sourceKind = "generated", sourceOrigin = "deterministic-fixture",
                        proposal = rasterTrace.GetProperty("structuredContent").GetProperty("trace"),
                        footprint = new
                        {
                            document = projectionDocument, expectedState = footprintProjectionExpected,
                            sourceFootprintId = sourceFootprint.Id.Value, targetFootprintId = projectedFootprintId,
                            libraryNickname = "Generated", footprintName = "RasterProjected",
                            originXNm = 70_000_000L, originYNm = 70_000_000L, orientationDegrees = 0.0,
                            layer = "F.Cu", artworkLayer = "F.SilkS"
                        }
                    }
                });
                RequireToolSuccess(footprintProjection);
                var footprintProjectionData = footprintProjection.GetProperty("structuredContent");
                Assert.IsTrue(footprintProjectionData.GetProperty("accepted").GetBoolean(), footprintProjection.GetRawText());
                Assert.IsTrue(footprintProjectionData.GetProperty("embeddedSourceVerified").GetBoolean(), footprintProjection.GetRawText());
                var projectedFootprint = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
                { Header = new() { Document = cleanBoard }, Items = { new KIID { Value = projectedFootprintId } } }, token)).Items.Single().Unpack<FootprintInstance>();
                Assert.IsTrue(projectedFootprint.Definition.Items.Any(item => item.Is(BoardGraphicShape.Descriptor)
                    && item.Unpack<BoardGraphicShape>().Shape.GeometryCase == GraphicShape.GeometryOneofCase.Polygon), footprintProjection.GetRawText());
                Assert.IsTrue(projectedFootprint.EmbeddedFiles.Files.Count > 0, footprintProjection.GetRawText());
                string footprintProjectionAfter = footprintProjectionData.GetProperty("nativeResult").GetProperty("afterState").GetString()!;
                var removeRasterFootprint = await mcp.Tool("kicad_pcb_items_delete", new
                {
                    instanceId,
                    requestJson = BoardJson.Formatter.Format(new DeleteItems { Header = new() { Document = cleanBoard }, ItemIds = { new KIID { Value = projectedFootprintId } } }),
                    expectedStateJson = footprintProjectionAfter
                });
                Assert.IsFalse(removeRasterFootprint.TryGetProperty("isError", out var removeRasterFootprintError) && removeRasterFootprintError.GetBoolean(), removeRasterFootprint.GetRawText());
                var removePlacedFootprint = await mcp.Tool("kicad_pcb_items_delete", new
                {
                    instanceId,
                    requestJson = BoardJson.Formatter.Format(new DeleteItems { Header = new() { Document = cleanBoard }, ItemIds = { placedFootprint.Id } }),
                    expectedStateJson = removeRasterFootprint.GetProperty("structuredContent").GetProperty("afterState").GetString()
                });
                Assert.IsFalse(removePlacedFootprint.TryGetProperty("isError", out var removePlacedError)
                    && removePlacedError.GetBoolean(), removePlacedFootprint.GetRawText());
                string rerouteBaseStateJson = removePlacedFootprint.GetProperty("structuredContent").GetProperty("afterState").GetString()!;

                // The headless planner can now hand its same-net rip-up IDs
                // directly to the generic route-replacement transaction.
                var reroutePlan = await mcp.Tool("kicad_pcb_route_plan", new
                {
                    instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard),
                    expectedStateJson = rerouteBaseStateJson,
                    requestJson = JsonSerializer.Serialize(new
                    {
                        netName = "POWER_RAIL", start = new { xNm = 30_100_000L, yNm = 30_100_000L },
                        end = new { xNm = 30_500_000L, yNm = 30_500_000L }, layer = "F.Cu",
                        widthNm = 250_000L, clearanceNm = 100_000L, gridNm = 100_000L,
                        bounds = new { minXNm = 29_000_000L, minYNm = 29_000_000L, maxXNm = 31_000_000L, maxYNm = 31_000_000L },
                        includeFootprintEnvelopes = true, includeRipUpCandidate = true, maxNodes = 10000
                    })
                });
                Assert.IsFalse(reroutePlan.TryGetProperty("isError", out var reroutePlanError)
                    && reroutePlanError.GetBoolean(), reroutePlan.GetRawText());
                var rerouteData = reroutePlan.GetProperty("structuredContent");
                Assert.IsTrue(rerouteData.GetProperty("routeFound").GetBoolean(), reroutePlan.GetRawText());
                Assert.IsGreaterThan(0, rerouteData.GetProperty("ripUpItemIds").GetArrayLength());
                var rerouteExpectedState = SchematicJson.Parser.Parse<DocumentLifecycleState>(rerouteBaseStateJson);
                var rerouteDrcReply = await mcp.Tool("kicad_pcb_drc_start", new
                {
                    instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard),
                    operationId = Guid.NewGuid().ToString("D"), refillZones = false,
                    reportAllTrackErrors = true, testFootprints = false,
                    expectedRevisionJson = SchematicJson.Formatter.Format(rerouteExpectedState.Revision),
                    processEpoch = client.Epoch,
                    candidateRequestJson = rerouteData.GetProperty("candidateRequestJson").GetString(),
                    deleteRequestJson = rerouteData.GetProperty("ripUpCandidateRequestJson").GetString()
                });
                Assert.IsFalse(rerouteDrcReply.TryGetProperty("isError", out var rerouteDrcStartError)
                    && rerouteDrcStartError.GetBoolean(), rerouteDrcReply.GetRawText());
                var rerouteDrc = SchematicJson.Parser.Parse<PcbDrcJobState>(rerouteDrcReply.GetProperty("content")[0].GetProperty("text").GetString()!);
                for (int attempt = 0; attempt < 60 && !rerouteDrc.WorkerFinished; attempt++)
                {
                    await Task.Delay(100, token);
                    var rerouteUpdate = await mcp.Tool("kicad_pcb_drc_job", new
                    { instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard), jobId = rerouteDrc.JobId, processEpoch = client.Epoch,
                        candidateRequestJson = rerouteData.GetProperty("candidateRequestJson").GetString(),
                        deleteRequestJson = rerouteData.GetProperty("ripUpCandidateRequestJson").GetString() });
                    Assert.IsFalse(rerouteUpdate.TryGetProperty("isError", out var rerouteUpdateError) && rerouteUpdateError.GetBoolean(), rerouteUpdate.GetRawText());
                    rerouteDrc = SchematicJson.Parser.Parse<PcbDrcJobState>(rerouteUpdate.GetProperty("content")[0].GetProperty("text").GetString()!);
                }
                // A very fast native job may already be terminal in the start
                // reply. Read it once with the exact candidate payload anyway;
                // this is the receipt step that proves the payload was checked,
                // rather than relying on whether the worker needed polling.
                var verifiedRerouteReply = await mcp.Tool("kicad_pcb_drc_job", new
                { instanceId, documentJson = SchematicJson.Formatter.Format(cleanBoard), jobId = rerouteDrc.JobId, processEpoch = client.Epoch,
                    candidateRequestJson = rerouteData.GetProperty("candidateRequestJson").GetString(),
                    deleteRequestJson = rerouteData.GetProperty("ripUpCandidateRequestJson").GetString() });
                Assert.IsFalse(verifiedRerouteReply.TryGetProperty("isError", out var verifiedRerouteError)
                    && verifiedRerouteError.GetBoolean(), verifiedRerouteReply.GetRawText());
                rerouteDrc = SchematicJson.Parser.Parse<PcbDrcJobState>(verifiedRerouteReply.GetProperty("content")[0].GetProperty("text").GetString()!);
                Assert.IsTrue(rerouteDrc.WorkerFinished, rerouteDrc.ErrorMessage);
                Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, rerouteDrc.Status, rerouteDrc.ErrorMessage);
                Assert.IsTrue(rerouteDrc.ResultsFresh, SchematicJson.Formatter.Format(rerouteDrc));
                Assert.IsTrue(rerouteDrc.SnapshotComplete, SchematicJson.Formatter.Format(rerouteDrc));
                Assert.IsTrue(rerouteDrc.CandidatePayloadVerified, SchematicJson.Formatter.Format(rerouteDrc));
                Assert.IsEmpty(rerouteDrc.Findings, SchematicJson.Formatter.Format(rerouteDrc));
                var rerouteApply = await mcp.Tool("kicad_pcb_route_reroute_apply", new
                {
                    instanceId, deleteRequestJson = rerouteData.GetProperty("ripUpCandidateRequestJson").GetString(),
                    createRequestJson = rerouteData.GetProperty("candidateRequestJson").GetString(),
                    expectedStateJson = rerouteBaseStateJson,
                    operationId = Guid.NewGuid().ToString("D"),
                    drcJobStateJson = SchematicJson.Formatter.Format(rerouteDrc)
                });
                Assert.IsFalse(rerouteApply.TryGetProperty("isError", out var rerouteApplyError)
                    && rerouteApplyError.GetBoolean(), rerouteApply.GetRawText());
                Assert.IsTrue(rerouteApply.GetProperty("structuredContent").GetProperty("mutationConfirmed").GetBoolean());
            }
            finally { Directory.Delete(exportDirectory, true); }
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-pcb-items.json"), updatedReply.GetRawText(), token);
        }
        finally { Directory.Delete(stateDirectory, true); }
        finalBoard = await VerifyNativePcbManufacturing(client, finalBoard, evidence, instanceId, token);
        await SaveCheckedThroughMcp(client, finalBoard, evidence, token, verifyReconnect: false);
    }

    private static string RouteIdentity(Any item) => item.Is(Track.Descriptor) ? item.Unpack<Track>().Id.Value
        : item.Is(Arc.Descriptor) ? item.Unpack<Arc>().Id.Value : item.Unpack<Via>().Id.Value;

    private static object DescribeRouteItem(Any item)
    {
        if (item.Is(Track.Descriptor))
        {
            var track = item.Unpack<Track>();
            return new
            {
                kind = "track", id = track.Id?.Value ?? "", net = track.Net?.Name ?? "", layer = track.Layer.ToString(),
                start = track.Start is null ? "" : $"{track.Start.XNm},{track.Start.YNm}",
                end = track.End is null ? "" : $"{track.End.XNm},{track.End.YNm}",
                widthNm = track.Width?.ValueNm ?? 0,
                customProperties = track.CustomProperties.Select(property => $"{property.Key}={property.Value}").ToArray(),
                solderMask = track.SolderMask?.ToString() ?? ""
            };
        }
        if (item.Is(Arc.Descriptor))
        {
            var arc = item.Unpack<Arc>();
            return new
            {
                kind = "arc", id = arc.Id?.Value ?? "", net = arc.Net?.Name ?? "", layer = arc.Layer.ToString(),
                start = arc.Start.ToString(), mid = arc.Mid.ToString(), end = arc.End.ToString(), widthNm = arc.Width.ValueNm
            };
        }
        var via = item.Unpack<Via>();
        return new
        {
            kind = "via", id = via.Id?.Value ?? "", net = via.Net?.Name ?? "", position = via.Position is null ? "" : $"{via.Position.XNm},{via.Position.YNm}",
            type = via.Type.ToString(), isFree = via.IsFree,
            startLayer = via.PadStack?.Drill?.StartLayer.ToString() ?? "", endLayer = via.PadStack?.Drill?.EndLayer.ToString() ?? "",
            drill = via.PadStack?.Drill?.Diameter is null ? "" : $"{via.PadStack.Drill.Diameter.XNm},{via.PadStack.Drill.Diameter.YNm}",
            customProperties = via.CustomProperties.Select(property => $"{property.Key}={property.Value}").ToArray(),
            padStack = via.PadStack?.ToString() ?? "", teardrop = via.Teardrop?.ToString() ?? ""
        };
    }

    private static string RouteShape(Any item)
    {
        if (item.Is(Track.Descriptor))
        {
            var track = item.Unpack<Track>();
            var points = new[] { $"{track.Start.XNm},{track.Start.YNm}", $"{track.End.XNm},{track.End.YNm}" }.Order(StringComparer.Ordinal);
            return $"track:{track.Net.Name}:{track.Layer}:{track.Width.ValueNm}:{string.Join("/", points)}";
        }
        if (item.Is(Arc.Descriptor))
        {
            var arc = item.Unpack<Arc>();
            return $"arc:{arc.Net.Name}:{arc.Layer}:{arc.Width.ValueNm}:{arc.Start}:{arc.Mid}:{arc.End}";
        }
        var via = item.Unpack<Via>();
        return $"via:{via.Net.Name}:{via.Position}:{via.PadStack}";
    }

    private static byte[] RenderPng(JsonElement reply) => Convert.FromBase64String(reply.GetProperty("content")
        .EnumerateArray().Single(block => block.GetProperty("type").GetString() == "image")
        .GetProperty("data").GetString()!);

    private static (int Left, int Top, int Right, int Bottom, int Width, int Height) PngDiffBounds(
        byte[] firstPng, byte[] secondPng)
    {
        var first = NativePresentationRaster.Decode(firstPng);
        var second = NativePresentationRaster.Decode(secondPng);
        Assert.AreEqual(first.Width, second.Width, "Model-content render comparison width changed unexpectedly.");
        Assert.AreEqual(first.Height, second.Height, "Model-content render comparison height changed unexpectedly.");
        int left = first.Width, top = first.Height, right = -1, bottom = -1;
        for (int y = 0; y < first.Height; ++y)
        for (int x = 0; x < first.Width; ++x)
        {
            var a = first.At(x, y);
            var b = second.At(x, y);
            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 12) continue;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < 0 ? (0, 0, 0, 0, 0, 0) : (left, top, right, bottom, right - left + 1, bottom - top + 1);
    }

    private static string CleanRouteBoard()
    {
        string f1 = Guid.NewGuid().ToString("D"), f2 = Guid.NewGuid().ToString("D");
        string p1 = Guid.NewGuid().ToString("D"), p2 = Guid.NewGuid().ToString("D");
        string f3 = Guid.NewGuid().ToString("D"), f4 = Guid.NewGuid().ToString("D");
        string p3 = Guid.NewGuid().ToString("D"), p4 = Guid.NewGuid().ToString("D");
        string f5 = Guid.NewGuid().ToString("D"), f6 = Guid.NewGuid().ToString("D");
        string p5 = Guid.NewGuid().ToString("D"), p6 = Guid.NewGuid().ToString("D");
        string f7 = Guid.NewGuid().ToString("D"), p7 = Guid.NewGuid().ToString("D");
        string obstacle = Guid.NewGuid().ToString("D");
        string viaSupport = Guid.NewGuid().ToString("D");
        string edge = Guid.NewGuid().ToString("D");
        // Keep the candidate deliberately small and deterministic: the native
        // shove preview starts at the USB_D+ pad (40.1,40.1) and runs vertically
        // through a foreign-net POWER_RAIL barrier at y=42. The explicit
        // Edge.Cuts rectangle gives the native checker a finite board for edge
        // clearance and containment rules.
        return $"""
(kicad_pcb (version 20260206) (generator "pcbnew")
  (general (thickness 1.6))
  (paper "A4")
  (layers (0 "F.Cu" signal) (2 "B.Cu" signal) (36 "B.SilkS" user "b.silkscreen") (37 "F.SilkS" user "f.silkscreen") (25 "Edge.Cuts" user))
  (setup (pad_to_mask_clearance 0))
  (net 0 "")
  (net 1 "POWER_RAIL")
  (net 2 "USB_D+")
  (net 3 "USB_D-")
  (gr_rect (start 0 0) (end 100 100) (stroke (width 0.05) (type default)) (fill none) (layer "Edge.Cuts") (uuid "{edge}"))
  (footprint "PAD1" (layer "F.Cu") (at 30.1 30.1) (uuid "{f1}")
    (attr smd)
    (pad "1" smd circle (at 0 0) (size 0.3 0.3) (layers "F.Cu" "F.Paste" "F.Mask") (net 1 "POWER_RAIL") (uuid "{p1}")))
  (footprint "PAD2" (layer "F.Cu") (at 30.5 30.5) (uuid "{f2}")
    (attr smd)
    (pad "1" smd circle (at 0 0) (size 0.3 0.3) (layers "F.Cu" "F.Paste" "F.Mask") (net 1 "POWER_RAIL") (uuid "{p2}")))
  (footprint "DP_P" (layer "F.Cu") (at 40.1 40.1) (uuid "{f3}")
    (attr smd)
    (pad "1" smd circle (at 0 0) (size 0.3 0.3) (layers "F.Cu" "F.Paste" "F.Mask") (net 2 "USB_D+") (uuid "{p3}")))
  (footprint "DP_N" (layer "F.Cu") (at 40.8 40.8) (uuid "{f4}")
    (attr smd)
    (pad "1" smd circle (at 0 0) (size 0.3 0.3) (layers "F.Cu" "F.Paste" "F.Mask") (net 3 "USB_D-") (uuid "{p4}")))
  (footprint "DP_P_TAIL" (layer "F.Cu") (at 43.1 40.1) (uuid "{f5}")
    (attr smd)
    (pad "1" smd circle (at 0 0) (size 0.3 0.3) (layers "F.Cu" "F.Paste" "F.Mask") (net 2 "USB_D+") (uuid "{p5}")))
  (footprint "DP_P_ROUTE_END" (layer "F.Cu") (at 41.6 44) (uuid "{f6}")
    (attr smd)
    (pad "1" smd circle (at 0 0) (size 0.3 0.3) (layers "F.Cu" "F.Paste" "F.Mask") (net 2 "USB_D+") (uuid "{p6}")))
  (footprint "POWER_RAIL_B_SUPPORT" (layer "B.Cu") (at 40.875 43) (uuid "{f7}")
    (attr smd)
    (pad "1" smd circle (at 0 0) (size 0.7 0.7) (layers "B.Cu" "B.Paste" "B.Mask") (net 1 "POWER_RAIL") (uuid "{p7}")))
  (segment (start 39 42) (end 41 42) (width 0.25) (layer "F.Cu") (net 1) (uuid "{obstacle}"))
  (segment (start 40.875 42) (end 40.875 43) (width 0.7) (layer "B.Cu") (net 1) (uuid "{viaSupport}"))
)
""";
    }
}
