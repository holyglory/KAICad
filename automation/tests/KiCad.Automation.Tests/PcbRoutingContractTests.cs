using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbRoutingContractTests
{
    [TestMethod]
    public void ConcaveContoursAndNestedHolesArePreservedWithoutInventingAllowedSpace()
    {
        BoardGraphicShape[] edges =
        [
            Edge(0, 0, 10_000, 0), Edge(10_000, 0, 10_000, 10_000),
            Edge(10_000, 10_000, 6_000, 10_000), Edge(6_000, 10_000, 6_000, 4_000),
            Edge(6_000, 4_000, 0, 4_000), Edge(0, 4_000, 0, 0),
            Edge(2_000, 1_000, 4_000, 1_000), Edge(4_000, 1_000, 4_000, 3_000),
            Edge(4_000, 3_000, 2_000, 3_000), Edge(2_000, 3_000, 2_000, 1_000)
        ];
        PcbRouteGeometry.Region region = PcbRouteGeometry.FromEdgeCuts(edges, 0)!;

        Assert.IsFalse(region.Blocks(1_000, 1_000));
        Assert.IsFalse(region.Blocks(8_000, 8_000));
        Assert.IsTrue(region.Blocks(2_000, 8_000), "A concave outside region must remain unavailable.");
        Assert.IsTrue(region.Blocks(3_000, 2_000), "A nested contour must remain a hole.");

        static BoardGraphicShape Edge(long x1, long y1, long x2, long y2) => new()
        {
            Layer = BoardLayer.BlEdgeCuts,
            Shape = new GraphicShape { Segment = new() { Start = new() { XNm = x1, YNm = y1 }, End = new() { XNm = x2, YNm = y2 } } }
        };
    }

    [TestMethod]
    public void ThinGeometryBetweenGridNodesBlocksTheWholeRouteSegment()
    {
        PolySet barrier = RectanglePolySet(4_900, 4_500, 5_100, 5_500);
        PcbRouteGeometry.Region region = PcbRouteGeometry.FromPolySet(barrier, 0)!;
        Assert.IsTrue(region.BlocksSegment(0, 5_000, 10_000, 5_000));
        Assert.IsFalse(region.BlocksSegment(0, 1_000, 10_000, 1_000));
    }

    [TestMethod]
    public void MalformedContoursAndUnsupportedCurvesFailClosed()
    {
        BoardGraphicShape[] open = [new()
        {
            Layer = BoardLayer.BlEdgeCuts,
            Shape = new GraphicShape { Segment = new() { Start = new() { XNm = 0, YNm = 0 }, End = new() { XNm = 10, YNm = 0 } } }
        }];
        AutomationException openError = Assert.ThrowsExactly<AutomationException>(() =>
            PcbRouteGeometry.FromEdgeCuts(open, 0));
        Assert.AreEqual("invalid_route_edge_cuts", openError.Code);

        GraphicShape curved = new() { Circle = new() { Center = new(), RadiusPoint = new() { XNm = 10, YNm = 0 } } };
        AutomationException curveError = Assert.ThrowsExactly<AutomationException>(() =>
            PcbRouteGeometry.FromGraphic(curved, 0));
        Assert.AreEqual("unsupported_route_curve", curveError.Code);
    }

    [TestMethod]
    public void WiderViaClearanceIsAppliedToGeometryBoundaries()
    {
        PcbRouteGeometry.Region region = PcbRouteGeometry.FromPolySet(RectanglePolySet(0, 0, 10_000, 10_000), 0)!;
        Assert.IsFalse(region.Blocks(5_000, 5_000));
        Assert.IsTrue(region.Blocks(5_000, 5_000, 6_000), "A via radius that reaches the boundary must be refused.");
    }

    private static PolySet RectanglePolySet(long minX, long minY, long maxX, long maxY) => new()
    {
        Polygons =
        {
            new PolygonWithHoles
            {
                Outline = new PolyLine { Closed = true, Nodes =
                {
                    new PolyLineNode { Point = new() { XNm = minX, YNm = minY } },
                    new PolyLineNode { Point = new() { XNm = maxX, YNm = minY } },
                    new PolyLineNode { Point = new() { XNm = maxX, YNm = maxY } },
                    new PolyLineNode { Point = new() { XNm = minX, YNm = maxY } }
                } }
            }
        }
    };

    [TestMethod]
    public void GeometryAwareZonesRespectHolesAndClearance()
    {
        var zone = new PolySet
        {
            Polygons =
            {
                new PolygonWithHoles
                {
                    Outline = new PolyLine { Closed = true, Nodes =
                    {
                        new PolyLineNode { Point = new() { XNm = 0, YNm = 0 } },
                        new PolyLineNode { Point = new() { XNm = 10_000, YNm = 0 } },
                        new PolyLineNode { Point = new() { XNm = 10_000, YNm = 10_000 } },
                        new PolyLineNode { Point = new() { XNm = 0, YNm = 10_000 } }
                    } },
                    Holes =
                    {
                        new PolyLine { Closed = true, Nodes =
                        {
                            new PolyLineNode { Point = new() { XNm = 4_000, YNm = 4_000 } },
                            new PolyLineNode { Point = new() { XNm = 6_000, YNm = 4_000 } },
                            new PolyLineNode { Point = new() { XNm = 6_000, YNm = 6_000 } },
                            new PolyLineNode { Point = new() { XNm = 4_000, YNm = 6_000 } }
                        } }
                    }
                }
            }
        };

        PolySet outlineOnly = zone.Clone();
        outlineOnly.Polygons[0].Holes.Clear();
        PcbRouteGeometry.Region region = PcbRouteGeometry.FromPolySet(zone, 500)!,
            boundary = PcbRouteGeometry.FromEdgeCuts(
                [new BoardGraphicShape
                {
                    Layer = BoardLayer.BlEdgeCuts,
                    Shape = new GraphicShape { Polygon = outlineOnly }
                }], 500)!;

        Assert.IsTrue(region.Blocks(2_000, 2_000));
        Assert.IsFalse(region.Blocks(5_000, 5_000), "A route through a true zone hole must remain available.");
        Assert.IsTrue(region.Blocks(4_200, 5_000), "Clearance must shrink the usable hole boundary.");
        Assert.IsFalse(boundary.Blocks(5_000, 5_000));
        Assert.IsTrue(boundary.Blocks(-500, 5_000), "The edge-cut allow-region must block cells outside the board.");
    }

    [TestMethod]
    public void GeometryAwareEdgeCutFallbackIsDeterministicForSegmentLoops()
    {
        BoardGraphicShape[] edges =
        [
            Edge(0, 0, 10_000, 0), Edge(10_000, 0, 10_000, 10_000),
            Edge(10_000, 10_000, 0, 10_000), Edge(0, 10_000, 0, 0)
        ];
        PcbRouteGeometry.Region first = PcbRouteGeometry.FromEdgeCuts(edges, 0)!,
            second = PcbRouteGeometry.FromEdgeCuts(edges.Reverse(), 0)!;
        Assert.AreEqual(first.Bounds, second.Bounds);
        Assert.IsFalse(first.Blocks(5_000, 5_000));
        Assert.IsTrue(first.Blocks(11_000, 5_000));
        Assert.IsTrue(first.KeepInside);

        static BoardGraphicShape Edge(long x1, long y1, long x2, long y2) => new()
        {
            Layer = BoardLayer.BlEdgeCuts,
            Shape = new GraphicShape { Segment = new() { Start = new() { XNm = x1, YNm = y1 }, End = new() { XNm = x2, YNm = y2 } } }
        };
    }

    [TestMethod]
    public void TypedHeadlessRouteRequestRoundTripsAllPlannerFieldsAndRejectsUnknownMembers()
    {
        var request = new PcbRoutePlanRequestInput
        {
            NetName = "USB_D+",
            NetClassName = "HighSpeed",
            Start = new() { XNm = 1_000, YNm = 2_000 },
            End = new() { XNm = 9_000, YNm = 2_000 },
            Layer = "F.Cu",
            WidthNm = 180,
            ClearanceNm = 120,
            GridNm = 1_000,
            Bounds = new() { MinXNm = 0, MinYNm = 0, MaxXNm = 10_000, MaxYNm = 10_000 },
            MaxNodes = 40_000,
            MaxObstacleReport = 37,
            IncludeFootprintEnvelopes = false,
            IncludeRipUpCandidate = true
        };

        string json = request.ToRequestJson();
        JsonElement parsed = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual("USB_D+", parsed.GetProperty("netName").GetString());
        Assert.AreEqual("HighSpeed", parsed.GetProperty("netClassName").GetString());
        Assert.AreEqual(180, parsed.GetProperty("widthNm").GetInt64());
        Assert.AreEqual(120, parsed.GetProperty("clearanceNm").GetInt64());
        Assert.AreEqual(1_000, parsed.GetProperty("gridNm").GetInt64());
        Assert.AreEqual(40_000, parsed.GetProperty("maxNodes").GetInt32());
        Assert.AreEqual(37, parsed.GetProperty("maxObstacleReport").GetInt32());
        Assert.IsFalse(parsed.GetProperty("includeFootprintEnvelopes").GetBoolean());
        Assert.IsTrue(parsed.GetProperty("includeRipUpCandidate").GetBoolean());
        Assert.AreEqual(1_000, parsed.GetProperty("start").GetProperty("xNm").GetInt64());
        Assert.AreEqual(10_000, parsed.GetProperty("bounds").GetProperty("maxXNm").GetInt64());

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbRoutePlanRequestInput>(
            "{\"netName\":\"A\",\"unexpected\":true}", options));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbRoutePlanRequestInput>(
            "{\"netName\":\"A\",\"netName\":\"B\"}", options));
    }

    [TestMethod]
    public async Task CompiledStdioExposesTypedHeadlessRoutePlanAndRefusesMixedForms()
    {
        string root = Directory.CreateTempSubdirectory("kicad-route-plan-input-contract-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        static bool IsObjectSchema(JsonElement schema)
        {
            JsonElement type = schema.GetProperty("type");
            return type.ValueKind == JsonValueKind.String
                ? type.GetString() == "object"
                : type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(item => item.GetString() == "object");
        }
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(), Path.Combine(root, "state"),
                Path.Combine(root, "stderr.log"), deadline.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tool = listed.GetProperty("tools").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "kicad_pcb_route_plan");
            JsonElement properties = tool.GetProperty("inputSchema").GetProperty("properties");
            foreach (string property in new[] { "documentJson", "document", "expectedStateJson", "expectedState", "requestJson", "request" })
                Assert.IsTrue(properties.TryGetProperty(property, out _), property);
            Assert.IsTrue(properties.GetProperty("document").GetProperty("type").EnumerateArray()
                .Any(item => item.GetString() == "object"));
            Assert.IsTrue(properties.GetProperty("expectedState").GetProperty("type").EnumerateArray()
                .Any(item => item.GetString() == "object"));
            Assert.IsTrue(properties.GetProperty("request").GetProperty("type").EnumerateArray()
                .Any(item => item.GetString() == "object"));
            JsonElement requestProperties = properties.GetProperty("request").GetProperty("properties");
            foreach (string property in new[] { "netName", "netClassName", "start", "end", "layer", "widthNm", "clearanceNm", "gridNm", "bounds", "maxNodes", "includeRipUpCandidate" })
                Assert.IsTrue(requestProperties.TryGetProperty(property, out _), property);

            JsonElement typedTool = listed.GetProperty("tools").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "kicad_pcb_route_plan_typed");
            JsonElement typedProperties = typedTool.GetProperty("inputSchema").GetProperty("properties");
            Assert.IsTrue(IsObjectSchema(typedProperties.GetProperty("input")), typedTool.GetRawText());
            JsonElement typedInputProperties = typedProperties.GetProperty("input").GetProperty("properties");
            foreach (string property in new[] { "document", "expectedState", "operationId", "request" })
                Assert.IsTrue(typedInputProperties.TryGetProperty(property, out _), property);
            Assert.IsTrue(IsObjectSchema(typedInputProperties.GetProperty("document")));
            Assert.IsTrue(IsObjectSchema(typedInputProperties.GetProperty("expectedState")));
            Assert.IsTrue(IsObjectSchema(typedInputProperties.GetProperty("request")));

            JsonElement invalidTyped = await mcp.Tool("kicad_pcb_route_plan_typed", new
            {
                instanceId = Guid.NewGuid().ToString("D"), input = new { }
            });
            Assert.IsTrue(invalidTyped.GetProperty("isError").GetBoolean(), invalidTyped.GetRawText());
            Assert.AreEqual("invalid_route_plan_typed_input", invalidTyped.GetProperty("structuredContent")
                .GetProperty("errorCode").GetString(), invalidTyped.GetRawText());

            JsonElement conflict = await mcp.Tool("kicad_pcb_route_plan", new
            {
                instanceId = Guid.NewGuid().ToString("D"),
                documentJson = "{}",
                document = new { },
                expectedStateJson = "{}",
                requestJson = "{}"
            });
            Assert.IsTrue(conflict.GetProperty("isError").GetBoolean(), conflict.GetRawText());
            Assert.AreEqual("invalid_route_plan_input", conflict.GetProperty("structuredContent")
                .GetProperty("errorCode").GetString(), conflict.GetRawText());

            JsonElement empty = await mcp.Tool("kicad_pcb_route_plan", new
            {
                instanceId = Guid.NewGuid().ToString("D"),
                documentJson = "{}",
                expectedStateJson = "{}",
                requestJson = ""
            });
            Assert.IsTrue(empty.GetProperty("isError").GetBoolean(), empty.GetRawText());
            Assert.AreEqual("invalid_route_plan_input", empty.GetProperty("structuredContent")
                .GetProperty("errorCode").GetString(), empty.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void RoutePreviewEndpointIdentityRoundTripsAndStaleCheckpointIsRejected()
    {
        var document = new DocumentSpecifier { Type = DocumentType.DoctypePcb, BoardFilename = "route.kicad_pcb",
            Project = new() { Name = "route", Path = "/tmp/kicad-route-contract" } };
        string epoch = Guid.NewGuid().ToString("D");
        var state = new DocumentLifecycleState
        {
            Document = document, Scope = DocumentLifecycleScope.DlsPcb, ProcessEpoch = epoch,
            Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 7 }
        };
        var request = new StartPcbRoutePreview
        {
            Document = document, ProcessEpoch = epoch, ExpectedRevision = state.Revision.Clone(),
            OperationId = Guid.NewGuid().ToString("D"),
            StartItemId = Guid.NewGuid().ToString("D"), EndItemId = Guid.NewGuid().ToString("D"),
            Start = new() { XNm = 1_000, YNm = 2_000 }, Layer = BoardLayer.BlFCu,
            ForceFinish = true, DifferentialPair = true, MaximumCompleteDeltaVersion = 2
        };
        request.Waypoints.Add(new Vector2 { XNm = 10_000, YNm = 2_000 });

        var parsed = SchematicJson.Parser.Parse<StartPcbRoutePreview>(SchematicJson.Formatter.Format(request));
        Assert.AreEqual(request, parsed);
        PcbRoutingTools.ValidatePreviewCheckpoint(parsed, state);

        var stale = state.Clone();
        stale.Revision.Sequence++;
        var error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbRoutingTools.ValidatePreviewCheckpoint(parsed, stale));
        Assert.AreEqual("invalid_route_preview", error.Code);
    }

    [TestMethod]
    public void CheckedRouteRequestRoundTripsAnyPackedGeometry()
    {
        string project = "/tmp/kicad-route-contract";
        var document = new DocumentSpecifier { Type = DocumentType.DoctypePcb, BoardFilename = "route.kicad_pcb",
            Project = new() { Name = "route", Path = project } };
        var track = new Track
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            Start = new() { XNm = 1000, YNm = 2000 }, End = new() { XNm = 3000, YNm = 2000 },
            Width = new() { ValueNm = 200 }, Layer = BoardLayer.BlFCu, Net = new() { Name = "N1" }
        };
        var candidate = new CreateItems { Header = new() { Document = document } };
        candidate.Items.Add(Any.Pack(track));
        var state = new DocumentLifecycleState { Document = document, ProcessEpoch = Guid.NewGuid().ToString("D"),
            NativeIdentity = Guid.NewGuid().ToString("D"), Scope = DocumentLifecycleScope.DlsPcb,
            ProjectSettingsIncluded = true, StateSha256 = new string('a', 64),
            Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 4 } };
        var request = new CheckedPcbRouteCommit { Candidate = candidate, ExpectedState = state,
            DrcJobId = Guid.NewGuid().ToString("D"), OperationId = Guid.NewGuid().ToString("D"), MaximumResultBytes = 4096 };
        var parsed = SchematicJson.Parser.Parse<CheckedPcbRouteCommit>(SchematicJson.Formatter.Format(request));
        Assert.AreEqual(request, parsed);
        Assert.AreEqual(track, parsed.Candidate.Items.Single().Unpack<Track>());
        var receipt = new CheckedPcbRouteReceipt { Document = document, ProcessEpoch = state.ProcessEpoch,
            OperationId = request.OperationId, State = CheckedPcbRouteReceipt.Types.State.Completed,
            RequestVerified = true, Before = state, After = state.Clone() };
        Assert.AreEqual(receipt, SchematicJson.Parser.Parse<CheckedPcbRouteReceipt>(SchematicJson.Formatter.Format(receipt)));
    }

    [TestMethod]
    public void FootprintEnvelopeObservationRoundTrips()
    {
        var footprint = new FootprintInstance
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = new() { XNm = 100_000, YNm = 200_000 },
            Definition = new() { Id = new() { LibraryNickname = "Generated", EntryName = "QFN-4" } }
        };
        var response = new GetItemsResponse();
        response.Items.Add(Any.Pack(footprint));
        var parsed = SchematicJson.Parser.Parse<GetItemsResponse>(SchematicJson.Formatter.Format(response));
        Assert.AreEqual(footprint, parsed.Items.Single().Unpack<FootprintInstance>());

        var bounds = new GetBoundingBoxResponse();
        bounds.Items.Add(footprint.Id.Clone());
        bounds.Boxes.Add(new Kiapi.Common.Types.Box2 { Position = new() { XNm = 90_000, YNm = 190_000 }, Size = new() { XNm = 20_000, YNm = 20_000 } });
        var boundsParsed = SchematicJson.Parser.Parse<GetBoundingBoxResponse>(SchematicJson.Formatter.Format(bounds));
        Assert.AreEqual(bounds, boundsParsed);
    }

    [TestMethod]
    public void CheckedRouteContractAcceptsCleanCompletedReceiptAndExactGeometry()
    {
        var (request, track, state) = CleanRoute();
        track.CustomProperties.Add(new CustomProperty { Key = "z", Value = "1" });
        track.CustomProperties.Add(new CustomProperty { Key = "a", Value = "2" });
        request.Candidate.Items[0] = Any.Pack(track);
        CheckedPcbRouteContract.ValidateRequest(request, state.ProcessEpoch);

        var after = state.Clone();
        after.Revision.Sequence++;
        after.StateSha256 = new string('b', 64);
        var created = new CreateItemsResponse
        {
            Header = new() { Document = request.Candidate.Header.Document },
            Status = ItemRequestStatus.IrsOk
        };
        created.CreatedItems.Add(new ItemCreationResult
        {
            Status = new() { Code = ItemStatusCode.IscOk }, Item = Any.Pack(track)
        });
        var receipt = new CheckedPcbRouteReceipt
        {
            Document = request.Candidate.Header.Document,
            ProcessEpoch = state.ProcessEpoch,
            OperationId = request.OperationId,
            State = CheckedPcbRouteReceipt.Types.State.Completed,
            RequestVerified = true,
            Before = state,
            After = after,
            Created = created
        };

        CheckedPcbRouteContract.ValidateResult(request, receipt, inspect: false);

        var normalized = receipt.Clone();
        var nativeTrack = track.Clone();
        var sortedProperties = nativeTrack.CustomProperties.OrderBy(property => property.Key, StringComparer.Ordinal).ToArray();
        nativeTrack.CustomProperties.Clear(); nativeTrack.CustomProperties.Add(sortedProperties);
        nativeTrack.Parent = new() { Value = Guid.NewGuid().ToString("D") };
        nativeTrack.Locked = LockedState.LsUnlocked;
        nativeTrack.Net.Code = new NetCode { Value = 17 };
        normalized.Created.CreatedItems[0].Item = Any.Pack(nativeTrack);
        CheckedPcbRouteContract.ValidateResult(request, normalized, inspect: false);

        var via = new Via
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = new() { XNm = 0, YNm = 20_000 },
            Type = ViaType.VtThrough, Net = new() { Name = "N1" }, IsFree = false,
            PadStack = new()
            {
                Type = PadStackType.PstNormal, Layers = { BoardLayer.BlFCu, BoardLayer.BlBCu },
                Drill = new() { Diameter = new() { XNm = 300, YNm = 300 }, StartLayer = BoardLayer.BlFCu,
                    EndLayer = BoardLayer.BlBCu, Shape = DrillShape.DsCircle },
                CopperLayers = { new PadStackLayer { Layer = BoardLayer.BlFCu, Shape = PadStackShape.PssCircle,
                    Size = new() { XNm = 600, YNm = 600 } } }
            }
        };
        var viaRequest = request.Clone(); viaRequest.Candidate.Items[0] = Any.Pack(via);
        var viaReceipt = receipt.Clone(); viaReceipt.Created.CreatedItems[0].Item = Any.Pack(via);
        CheckedPcbRouteContract.ValidateResult(viaRequest, viaReceipt, inspect: false);
        var freeVia = via.Clone(); freeVia.IsFree = true;
        viaReceipt.Created.CreatedItems[0].Item = Any.Pack(freeVia);
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidateResult(viaRequest, viaReceipt, inspect: false));

    }

    [TestMethod]
    public void CheckedRouteContractCanonicalizesViaLayersButRejectsInactiveGeometry()
    {
        var (request, _, state) = CleanRoute();
        var document = request.Candidate.Header.Document;
        var via = new Via
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = new() { XNm = 20_000, YNm = 10_000 },
            Type = ViaType.VtBlind,
            Net = new() { Name = "VIA_MAIN" },
            PadStack = new()
            {
                Type = PadStackType.PstCustom,
                Layers = { BoardLayer.BlIn1Cu, BoardLayer.BlBCu },
                Drill = new()
                {
                    Diameter = new() { XNm = 300, YNm = 300 },
                    StartLayer = BoardLayer.BlIn1Cu,
                    EndLayer = BoardLayer.BlBCu,
                    Shape = DrillShape.DsCircle
                },
                CopperLayers =
                {
                    new PadStackLayer
                    {
                        Layer = BoardLayer.BlIn1Cu, Shape = PadStackShape.PssCircle,
                        CustomAnchorShape = PadStackShape.PssRectangle,
                        Size = new() { XNm = 1_600, YNm = 1_600 }
                    },
                    new PadStackLayer
                    {
                        Layer = BoardLayer.BlBCu, Shape = PadStackShape.PssCircle,
                        CustomAnchorShape = PadStackShape.PssRectangle,
                        Size = new() { XNm = 1_600, YNm = 1_600 }
                    }
                }
            }
        };

        var native = via.Clone();
        native.PadStack.Layers.Clear();
        native.PadStack.Layers.Add(BoardLayer.BlBCu);
        native.PadStack.Layers.Add(BoardLayer.BlIn1Cu);
        native.PadStack.CopperLayers.Clear();
        native.PadStack.CopperLayers.Add(new PadStackLayer
        {
            Layer = BoardLayer.BlBCu, Shape = PadStackShape.PssCircle,
            CustomAnchorShape = PadStackShape.PssRectangle, Size = new() { XNm = 1_600, YNm = 1_600 }
        });
        native.PadStack.CopperLayers.Add(new PadStackLayer
        {
            Layer = BoardLayer.BlIn1Cu, Shape = PadStackShape.PssCircle,
            CustomAnchorShape = PadStackShape.PssRectangle, Size = new() { XNm = 1_600, YNm = 1_600 }
        });
        // PCB_VIA retains the default front profile when a custom padstack
        // starts on an inner layer.  This zero-size profile is serialization
        // residue; a non-empty inactive profile must remain a mismatch.
        native.PadStack.CopperLayers.Add(new PadStackLayer
        {
            Layer = BoardLayer.BlFCu, Shape = PadStackShape.PssCircle,
            CustomAnchorShape = PadStackShape.PssRectangle
        });

        var requestVia = request.Clone();
        requestVia.Candidate.Items.Clear();
        requestVia.Candidate.Items.Add(Any.Pack(via));
        var after = state.Clone();
        after.Revision.Sequence++;
        after.StateSha256 = new string('b', 64);
        var created = new CreateItemsResponse { Status = ItemRequestStatus.IrsOk };
        created.CreatedItems.Add(new ItemCreationResult
        {
            Status = new() { Code = ItemStatusCode.IscOk }, Item = Any.Pack(native)
        });
        var receipt = new CheckedPcbRouteReceipt
        {
            Document = document,
            ProcessEpoch = state.ProcessEpoch,
            OperationId = requestVia.OperationId,
            State = CheckedPcbRouteReceipt.Types.State.Completed,
            RequestVerified = true,
            Before = state,
            After = after,
            Created = created
        };

        CheckedPcbRouteContract.ValidateResult(requestVia, receipt, inspect: false);

        var invalid = native.Clone();
        invalid.PadStack.CopperLayers.Single(layer => layer.Layer == BoardLayer.BlFCu).Size =
            new() { XNm = 1, YNm = 1 };
        receipt.Created.CreatedItems[0].Item = Any.Pack(invalid);
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateResult(requestVia, receipt, inspect: false));
    }

    [TestMethod]
    public void CheckedRouteContractAcceptsIdentityPreservingReplacementAndRejectsUuidOverlap()
    {
        var (request, track, state) = CleanRoute();
        var replacement = track.Clone();
        replacement.Id = new() { Value = Guid.NewGuid().ToString("D") };
        replacement.Start.XNm += 1000;
        request.ReplacementUpdates = new UpdateItems { Header = request.Candidate.Header.Clone() };
        request.ReplacementUpdates.Items.Add(Any.Pack(replacement));

        var removed = new KIID { Value = Guid.NewGuid().ToString("D") };
        request.Removals = new DeleteItems { Header = request.Candidate.Header.Clone(), ItemIds = { removed } };
        CheckedPcbRouteContract.ValidateRequest(request, state.ProcessEpoch);

        var after = state.Clone();
        after.Revision.Sequence++;
        after.StateSha256 = new string('b', 64);
        var created = new CreateItemsResponse { Status = ItemRequestStatus.IrsOk };
        created.CreatedItems.Add(new ItemCreationResult
        {
            Status = new() { Code = ItemStatusCode.IscOk }, Item = Any.Pack(track)
        });
        var receipt = new CheckedPcbRouteReceipt
        {
            Document = request.Candidate.Header.Document,
            ProcessEpoch = state.ProcessEpoch,
            OperationId = request.OperationId,
            State = CheckedPcbRouteReceipt.Types.State.Completed,
            RequestVerified = true,
            Before = state,
            After = after,
            Created = created,
            Updated = request.ReplacementUpdates.Clone()
        };
        receipt.UpdatedResult = new UpdateItemsResponse { Status = ItemRequestStatus.IrsOk };
        receipt.UpdatedResult.UpdatedItems.Add(new ItemUpdateResult
        {
            Status = new() { Code = ItemStatusCode.IscOk }, Item = Any.Pack(replacement)
        });
        receipt.Removals = request.Removals.Clone();
        receipt.RemovedResult = new DeleteItemsResponse
        {
            Status = ItemRequestStatus.IrsOk,
            DeletedItems = { new ItemDeletionResult { Id = removed.Clone(), Status = ItemDeletionStatus.IdsOk } }
        };
        CheckedPcbRouteContract.ValidateResult(request, receipt, inspect: false);

        var replay = receipt.Clone(); replay.Replayed = true;
        CheckedPcbRouteContract.ValidateResult(request, replay, inspect: false);
        var forgedRemoval = receipt.Clone();
        forgedRemoval.RemovedResult.DeletedItems[0].Id = new() { Value = Guid.NewGuid().ToString("D") };
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidateResult(request, forgedRemoval, inspect: false));

        var deleteOnly = request.Clone(); deleteOnly.Candidate.Items.Clear(); deleteOnly.ReplacementUpdates = null;
        CheckedPcbRouteContract.ValidateRequest(deleteOnly, state.ProcessEpoch);
        var deleteReceipt = receipt.Clone(); deleteReceipt.Created.CreatedItems.Clear();
        deleteReceipt.Updated = null; deleteReceipt.UpdatedResult = null;
        CheckedPcbRouteContract.ValidateResult(deleteOnly, deleteReceipt, inspect: false);
        deleteOnly.Removals.ItemIds.Clear();
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidateRequest(deleteOnly, state.ProcessEpoch));
        deleteOnly.Removals.ItemIds.Add(new KIID { Value = Guid.Empty.ToString("D") });
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidateRequest(deleteOnly, state.ProcessEpoch));
        var overlapRemoval = request.Clone(); overlapRemoval.Removals.ItemIds[0] = replacement.Id.Clone();
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidateRequest(overlapRemoval, state.ProcessEpoch));

        var forged = receipt.Clone();
        forged.UpdatedResult.UpdatedItems[0].Item = Any.Pack(track);
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateResult(request, forged, inspect: false));

        request.ReplacementUpdates.Items.Clear();
        request.ReplacementUpdates.Items.Add(Any.Pack(track.Clone()));
        var error = Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateRequest(request, state.ProcessEpoch));
        StringAssert.Contains(error.Message, "unique UUIDs distinct");
    }

    [TestMethod]
    public void CheckedRouteContractRejectsForgedCompletedReceipt()
    {
        var (request, track, state) = CleanRoute();
        track.Start.XNm = 0;
        track.CustomProperties.Add(new CustomProperty { Key = "route-note", Value = "preserve-me" });
        track.SolderMask = new() { ExposeCopper = true, SolderMaskMargin = new() { ValueNm = 0 } };
        request.Candidate.Items[0] = Any.Pack(track);
        var after = state.Clone();
        after.Revision.Sequence++;
        after.StateSha256 = new string('b', 64);
        var created = new CreateItemsResponse { Status = ItemRequestStatus.IrsOk };
        created.CreatedItems.Add(new ItemCreationResult
        {
            Status = new() { Code = ItemStatusCode.IscOk }, Item = Any.Pack(track)
        });
        var receipt = new CheckedPcbRouteReceipt
        {
            Document = request.Candidate.Header.Document,
            ProcessEpoch = state.ProcessEpoch,
            OperationId = request.OperationId,
            State = CheckedPcbRouteReceipt.Types.State.Completed,
            RequestVerified = true,
            Before = state,
            After = after,
            Created = created
        };

        var staleAfter = receipt.Clone();
        staleAfter.After.Revision.Sequence = state.Revision.Sequence;
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateResult(request, staleAfter, inspect: false));

        var changedGeometry = receipt.Clone();
        var changedTrack = track.Clone();
        changedTrack.End.XNm++;
        changedGeometry.Created.CreatedItems[0].Item = Any.Pack(changedTrack);
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateResult(request, changedGeometry, inspect: false));

        void RejectChange(Action<Track> mutate)
        {
            var changed = track.Clone(); mutate(changed);
            var forged = receipt.Clone(); forged.Created.CreatedItems[0].Item = Any.Pack(changed);
            Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidateResult(request, forged, inspect: false));
        }
        RejectChange(value => value.Start.XNm = 1);
        RejectChange(value => value.Net.Name = "N2");
        RejectChange(value => value.CustomProperties[0].Value = "changed");
        RejectChange(value => value.CustomProperties.Clear());
        RejectChange(value => value.CustomProperties.Add(value.CustomProperties[0].Clone()));
        var duplicateRequest = request.Clone(); var duplicateTrack = track.Clone();
        duplicateTrack.CustomProperties.Add(duplicateTrack.CustomProperties[0].Clone());
        duplicateRequest.Candidate.Items[0] = Any.Pack(duplicateTrack);
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidateRequest(duplicateRequest, state.ProcessEpoch));

        RejectChange(value => value.SolderMask.ExposeCopper = false);
        RejectChange(value => value.SolderMask.SolderMaskMargin = null);

        var empty = track.Clone(); empty.CustomProperties.Clear();
        var emptyRequest = request.Clone(); emptyRequest.Candidate.Items[0] = Any.Pack(empty);
        var extra = empty.Clone(); extra.CustomProperties.Add(new CustomProperty { Key = "unrequested", Value = "extra" });
        var extraReceipt = receipt.Clone(); extraReceipt.Created.CreatedItems[0].Item = Any.Pack(extra);
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidateResult(emptyRequest, extraReceipt, inspect: false));
    }

    [TestMethod]
    public void CheckedRouteContractTreatsMissingAsReadOnlyAndPreservesIndeterminateEvidence()
    {
        var (request, _, state) = CleanRoute();
        var missing = new CheckedPcbRouteReceipt
        {
            Document = request.Candidate.Header.Document,
            ProcessEpoch = state.ProcessEpoch,
            OperationId = request.OperationId,
            State = CheckedPcbRouteReceipt.Types.State.NotFound
        };
        CheckedPcbRouteContract.ValidateResult(request, missing, inspect: true);
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateResult(request, missing, inspect: false));

        var uncertain = missing.Clone();
        uncertain.State = CheckedPcbRouteReceipt.Types.State.Indeterminate;
        uncertain.RequestVerified = true;
        uncertain.ErrorCode = "commit_result_unknown";
        uncertain.ErrorMessage = "native commit outcome is unknown";
        uncertain.Before = state.Clone();
        CheckedPcbRouteContract.ValidateResult(request, uncertain, inspect: false);

        var missingMessage = uncertain.Clone();
        missingMessage.ErrorMessage = "";
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateResult(request, missingMessage, inspect: false));
    }

    [TestMethod]
    public void CheckedRouteRejectedReceiptRequiresUnchangedAfterState()
    {
        var (request, _, state) = CleanRoute();
        var rejected = new CheckedPcbRouteReceipt
        {
            Document = request.Candidate.Header.Document.Clone(),
            ProcessEpoch = state.ProcessEpoch,
            OperationId = request.OperationId,
            State = CheckedPcbRouteReceipt.Types.State.Rejected,
            RequestVerified = true,
            ErrorCode = "route_items_rejected",
            ErrorMessage = "native route item validation rejected the candidate"
        };

        CheckedPcbRouteContract.ValidateResult(request, rejected, inspect: false);

        var unchangedAfter = rejected.Clone();
        unchangedAfter.After = state.Clone();
        CheckedPcbRouteContract.ValidateResult(request, unchangedAfter, inspect: false);

        var forgedAfter = rejected.Clone();
        forgedAfter.After = state.Clone();
        forgedAfter.After.StateSha256 = new string('b', 64);
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateResult(request, forgedAfter, inspect: false));

        var unanchoredChangedAfter = rejected.Clone();
        unanchoredChangedAfter.After = state.Clone();
        unanchoredChangedAfter.After.Revision.Sequence++;
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateResult(request, unanchoredChangedAfter, inspect: false));
    }

    [TestMethod]
    public void CheckedRouteContractRejectsNonCopperGeometryBeforeNativeAdmission()
    {
        var (request, _, state) = CleanRoute();
        request.Candidate.Items.Clear();
        request.Candidate.Items.Add(Any.Pack(new FootprintInstance
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = new() { XNm = 1000, YNm = 2000 },
            Definition = new() { Id = new() { LibraryNickname = "Generated", EntryName = "QFN-4" } }
        }));

        var error = Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateRequest(request, state.ProcessEpoch));
        StringAssert.Contains(error.Message, "only board-level Track, Arc or Via");
    }

    [TestMethod]
    public void CheckedRouteContractRejectsGeneratorNestedAndDesignBlockGroups()
    {
        var (request, track, state) = CleanRoute();
        var group = new Group
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") }, Name = "route-group",
            Items = { track.Id.Clone() }
        };
        var change = new NativeRouteGroupChange
        {
            ExpectedBefore = new NativeRouteGroupState { Group = group.Clone(), Kind = NativeRouteGroupKind.NrgkOrdinary },
            ExpectedAfter = new NativeRouteGroupState { Group = group.Clone(), Kind = NativeRouteGroupKind.NrgkOrdinary }
        };
        request.GroupChanges.Add(change);
        CheckedPcbRouteContract.ValidateRequest(request, state.ProcessEpoch);

        var generator = request.Clone();
        generator.GroupChanges[0].ExpectedBefore.Kind = NativeRouteGroupKind.NrgkGenerator;
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidateRequest(generator, state.ProcessEpoch));

        var nested = request.Clone();
        nested.GroupChanges[0].ExpectedBefore.ParentGroupId = new() { Value = Guid.NewGuid().ToString("D") };
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidateRequest(nested, state.ProcessEpoch));

        var designBlock = request.Clone();
        designBlock.GroupChanges[0].ExpectedBefore.Group.LibId = new() { LibraryNickname = "blocks", EntryName = "route" };
        designBlock.GroupChanges[0].ExpectedAfter.Group.LibId = designBlock.GroupChanges[0].ExpectedBefore.Group.LibId.Clone();
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidateRequest(designBlock, state.ProcessEpoch));
    }

    [TestMethod]
    public void CheckedRouteGroupMembershipRequiresCanonicalItemIdentities()
    {
        var (request, track, state) = CleanRoute();
        var group = new Group
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") }, Name = "route-group",
            Items = { new KIID { Value = "a2222222-2222-2222-2222-222222222222".ToUpperInvariant() } }
        };
        request.GroupChanges.Add(new NativeRouteGroupChange
        {
            ExpectedBefore = new NativeRouteGroupState
            {
                Group = group.Clone(), Kind = NativeRouteGroupKind.NrgkOrdinary
            },
            ExpectedAfter = new NativeRouteGroupState
            {
                Group = group.Clone(), Kind = NativeRouteGroupKind.NrgkOrdinary
            }
        });
        request.Candidate.Items[0] = Any.Pack(track);

        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateRequest(request, state.ProcessEpoch));
    }

    [TestMethod]
    public void CheckedRouteGroupMembershipChangesRequireExplicitItemOperations()
    {
        var (request, track, state) = CleanRoute();
        var group = new Group
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") }, Name = "route-group",
            Items = { track.Id.Clone() }
        };

        var addedWithoutSource = request.Clone();
        var addedGroup = group.Clone();
        addedGroup.Items.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
        addedWithoutSource.GroupChanges.Add(new NativeRouteGroupChange
        {
            ExpectedBefore = new NativeRouteGroupState
            {
                Group = group.Clone(), Kind = NativeRouteGroupKind.NrgkOrdinary
            },
            ExpectedAfter = new NativeRouteGroupState
            {
                Group = addedGroup, Kind = NativeRouteGroupKind.NrgkOrdinary
            }
        });
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateRequest(addedWithoutSource, state.ProcessEpoch));

        var removedWithoutRequest = request.Clone();
        var removedGroup = group.Clone();
        removedGroup.Items.Clear();
        removedWithoutRequest.GroupChanges.Add(new NativeRouteGroupChange
        {
            ExpectedBefore = new NativeRouteGroupState
            {
                Group = group.Clone(), Kind = NativeRouteGroupKind.NrgkOrdinary
            },
            ExpectedAfter = new NativeRouteGroupState
            {
                Group = removedGroup, Kind = NativeRouteGroupKind.NrgkOrdinary
            }
        });
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidateRequest(removedWithoutRequest, state.ProcessEpoch));
    }

    [TestMethod]
    public void RoutePreviewContractAcceptsOrdinaryGroupDeltaAndRejectsLegacyVersion()
    {
        var (request, source, state) = CleanRoute();
        var created = source.Clone();
        created.Id = new() { Value = Guid.NewGuid().ToString("D") };
        created.Start.XNm += 1000;
        created.End.XNm += 1000;

        var group = new Group
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") }, Name = "route-group",
            Items = { source.Id.Clone() }
        };
        var afterGroup = group.Clone();
        afterGroup.Items.Clear();
        afterGroup.Items.Add(created.Id.Clone());
        var preview = new PcbRoutePreviewState
        {
            Document = request.Candidate.Header.Document,
            OperationId = request.OperationId,
            ProcessEpoch = state.ProcessEpoch,
            Completed = true,
            PreviewOnly = true,
            CompleteDeltaVersion = 2
        };
        preview.RouteItems.Add(Any.Pack(created));
        preview.RemovedItemIds.Add(source.Id.Clone());
        preview.GroupChanges.Add(new NativeRouteGroupChange
        {
            ExpectedBefore = new NativeRouteGroupState
            {
                Group = group,
                Kind = NativeRouteGroupKind.NrgkOrdinary
            },
            ExpectedAfter = new NativeRouteGroupState
            {
                Group = afterGroup,
                Kind = NativeRouteGroupKind.NrgkOrdinary
            },
            CreatedSources =
            {
                new NativeRouteCreatedSource
                {
                    CreatedItemId = created.Id.Clone(),
                    SourceItemId = source.Id.Clone(),
                    SourceGroupId = group.Id.Clone()
                }
            }
        });

        CheckedPcbRouteContract.ValidatePreview(preview);

        var legacy = preview.Clone();
        legacy.CompleteDeltaVersion = 1;
        var legacyError = Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidatePreview(legacy));
        Assert.AreEqual("route_preview_contract_unsupported", legacyError.Code);

        var nested = preview.Clone();
        nested.GroupChanges[0].ExpectedBefore.Kind = NativeRouteGroupKind.NrgkGenerator;
        var nestedError = Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.ValidatePreview(nested));
        Assert.AreEqual("invalid_checked_pcb_route", nestedError.Code);
    }

    [TestMethod]
    public void RoutePreviewReplacementUpdatesRoundTripPreservesDisplacedUuid()
    {
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "route.kicad_pcb",
            Project = new() { Name = "route", Path = Path.Combine(Path.GetTempPath(), "kicad-route-preview") }
        };
        string displacedId = Guid.NewGuid().ToString("D");
        var replacement = new Track
        {
            Id = new() { Value = displacedId },
            Start = new() { XNm = 10_000, YNm = 20_000 },
            End = new() { XNm = 30_000, YNm = 20_000 },
            Width = new() { ValueNm = 200 }, Layer = BoardLayer.BlFCu,
            Net = new() { Name = "N1" }
        };
        var displacedViaId = Guid.NewGuid().ToString("D");
        var replacementVia = new Via
        {
            Id = new() { Value = displacedViaId },
            Position = new() { XNm = 40_000, YNm = 50_000 },
            Type = ViaType.VtThrough,
            IsFree = true,
            Net = new() { Name = "N1" },
            CustomProperties = { new CustomProperty { Key = "via-source", Value = "preserve" } },
            Teardrop = new PadTeardropSettings
            {
                Mode = PadTeardropMode.PtmEnabled,
                AllowMultipleTrackSegments = true,
                PreferZoneConnection = true,
                MaxLength = new() { ValueNm = 900_000 },
                MaxWidth = new() { ValueNm = 1_100_000 },
                BestLengthRatio = 0.6,
                BestWidthRatio = 0.8,
                MaxTrackWidthRatio = 0.7
            },
            PadStack = new PadStack
            {
                Type = PadStackType.PstNormal,
                Layers = { BoardLayer.BlFCu, BoardLayer.BlBCu },
                Drill = new DrillProperties
                {
                    Diameter = new() { XNm = 300, YNm = 300 },
                    StartLayer = BoardLayer.BlFCu,
                    EndLayer = BoardLayer.BlBCu,
                    Shape = DrillShape.DsCircle
                },
                CopperLayers =
                {
                    new PadStackLayer
                    {
                        Layer = BoardLayer.BlFCu,
                        Shape = PadStackShape.PssCircle,
                        Size = new() { XNm = 700, YNm = 700 }
                    }
                }
            }
        };
        var preview = new PcbRoutePreviewState
        {
            Document = document,
            OperationId = Guid.NewGuid().ToString("D"),
            ProcessEpoch = Guid.NewGuid().ToString("D"),
            Completed = true,
            PreviewOnly = true,
            CompleteDeltaVersion = 1,
            ReplacementUpdates = new UpdateItems { Header = new() { Document = document } }
        };
        preview.RemovedItemIds.Add(new KIID { Value = displacedId });
        preview.RemovedItemIds.Add(new KIID { Value = displacedViaId });
        preview.ReplacementUpdates.Items.Add(Any.Pack(replacement));
        preview.ReplacementUpdates.Items.Add(Any.Pack(replacementVia));

        var parsed = SchematicJson.Parser.Parse<PcbRoutePreviewState>(SchematicJson.Formatter.Format(preview));
        Assert.AreEqual(displacedId, parsed.RemovedItemIds.Single(id => id.Value == displacedId).Value);
        Assert.AreEqual(document, parsed.ReplacementUpdates.Header.Document);
        var parsedTrack = parsed.ReplacementUpdates.Items.Single(item => item.Is(Track.Descriptor)).Unpack<Track>();
        Assert.AreEqual(displacedId, parsedTrack.Id.Value);
        var parsedVia = parsed.ReplacementUpdates.Items.Single(item => item.Is(Via.Descriptor)).Unpack<Via>();
        Assert.AreEqual(replacementVia, parsedVia);
        Assert.AreEqual(displacedViaId, parsed.RemovedItemIds.Single(id => id.Value == displacedViaId).Value);

        CheckedPcbRouteContract.ValidatePreview(parsed);
        var legacy = parsed.Clone(); legacy.CompleteDeltaVersion = 0;
        var legacyError = Assert.ThrowsExactly<AutomationException>(() => CheckedPcbRouteContract.ValidatePreview(legacy));
        Assert.AreEqual("route_preview_contract_unsupported", legacyError.Code);
        var cancelled = legacy.Clone(); cancelled.RouteItems.Clear(); cancelled.RemovedItemIds.Clear();
        cancelled.ReplacementUpdates = null; cancelled.Cancelled = true;
        CheckedPcbRouteContract.ValidatePreview(cancelled, inspectStateOnly: true);
        cancelled.Completed = false; cancelled.Cancelled = false; cancelled.ErrorCode = "route_delta_group_membership_unsupported";
        CheckedPcbRouteContract.ValidatePreview(cancelled, inspectStateOnly: true);

        preview.ReplacementUpdates = null;
        preview.ReplacementUpdatesError = "displaced_parent_geometry_unsupported";
        parsed = SchematicJson.Parser.Parse<PcbRoutePreviewState>(SchematicJson.Formatter.Format(preview));
        Assert.IsNull(parsed.ReplacementUpdates);
        Assert.AreEqual("displaced_parent_geometry_unsupported", parsed.ReplacementUpdatesError);
    }

    [TestMethod]
    public void RoutePreviewRemovedIdsRoundTripAsDeleteCandidate()
    {
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "route.kicad_pcb",
            Project = new() { Name = "route", Path = Path.Combine(Path.GetTempPath(), "kicad-route-preview") }
        };
        var displaced = new KIID { Value = Guid.NewGuid().ToString("D") };
        var preview = new PcbRoutePreviewState
        {
            Document = document,
            OperationId = Guid.NewGuid().ToString("D"),
            ProcessEpoch = Guid.NewGuid().ToString("D"),
            Completed = true,
            PreviewOnly = true
        };
        preview.RemovedItemIds.Add(displaced);

        var delete = new DeleteItems { Header = new() { Document = document } };
        delete.ItemIds.Add(preview.RemovedItemIds);
        var parsed = SchematicJson.Parser.Parse<DeleteItems>(BoardJson.Formatter.Format(delete));

        Assert.AreEqual(document, parsed.Header.Document);
        Assert.AreEqual(displaced, parsed.ItemIds.Single());
    }

    [TestMethod]
    public void CandidateFingerprintIsDeterministicAndCoversCompleteRouteDelta()
    {
        var (request, source, _) = CleanRoute();
        string createdId = Guid.NewGuid().ToString("D");
        var created = source.Clone();
        created.Id = new() { Value = createdId };
        created.Start.XNm += 10_000;
        created.End.XNm += 10_000;
        created.CustomProperties.Add(new CustomProperty { Key = "route-origin", Value = "preview" });
        request.Candidate.Items.Add(Any.Pack(created));

        var replacement = source.Clone();
        replacement.End.XNm += 20_000;
        replacement.CustomProperties.Add(new CustomProperty { Key = "replacement", Value = "preserve" });
        request.ReplacementUpdates = new UpdateItems { Header = new() { Document = request.Candidate.Header.Document } };
        request.ReplacementUpdates.Items.Add(Any.Pack(replacement));
        request.Removals = new DeleteItems { Header = new() { Document = request.Candidate.Header.Document } };
        request.Removals.ItemIds.Add(new KIID { Value = Guid.NewGuid().ToString("D") });

        var groupId = Guid.NewGuid().ToString("D");
        request.GroupChanges.Add(new NativeRouteGroupChange
        {
            ExpectedBefore = new NativeRouteGroupState
            {
                Kind = NativeRouteGroupKind.NrgkOrdinary,
                Group = new Group { Id = new() { Value = groupId }, Name = "route" }
            },
            ExpectedAfter = new NativeRouteGroupState
            {
                Kind = NativeRouteGroupKind.NrgkOrdinary,
                Group = new Group { Id = new() { Value = groupId }, Name = "route", Items = { new KIID { Value = createdId } } }
            },
            CreatedSources =
            {
                new NativeRouteCreatedSource
                {
                    CreatedItemId = new KIID { Value = createdId },
                    SourceItemId = source.Id.Clone(),
                    SourceGroupId = new KIID { Value = groupId }
                }
            }
        });

        string fingerprint = CheckedPcbRouteContract.CandidateFingerprint(request);
        Assert.AreEqual(64, fingerprint.Length);
        Assert.IsTrue(fingerprint.All(character => (character >= '0' && character <= '9')
            || (character >= 'a' && character <= 'f')));

        var reordered = request.Clone();
        var candidateItems = reordered.Candidate.Items.Select(item => item.Clone()).Reverse().ToArray();
        reordered.Candidate.Items.Clear();
        reordered.Candidate.Items.Add(candidateItems);
        var updateItems = reordered.ReplacementUpdates.Items.Select(item => item.Clone()).Reverse().ToArray();
        reordered.ReplacementUpdates.Items.Clear();
        reordered.ReplacementUpdates.Items.Add(updateItems);
        var removalIds = reordered.Removals.ItemIds.Select(item => item.Clone()).Reverse().ToArray();
        reordered.Removals.ItemIds.Clear();
        reordered.Removals.ItemIds.Add(removalIds);
        var group = reordered.GroupChanges[0];
        var groupMembers = group.ExpectedAfter.Group.Items.Select(item => item.Clone()).Reverse().ToArray();
        group.ExpectedAfter.Group.Items.Clear();
        group.ExpectedAfter.Group.Items.Add(groupMembers);
        var sources = group.CreatedSources.Select(item => item.Clone()).Reverse().ToArray();
        group.CreatedSources.Clear();
        group.CreatedSources.Add(sources);
        Assert.AreEqual(fingerprint, CheckedPcbRouteContract.CandidateFingerprint(reordered),
            "Equivalent route deltas must have one deterministic fingerprint.");

        var changed = request.Clone();
        var changedTrack = changed.Candidate.Items[0].Unpack<Track>();
        changedTrack.Width.ValueNm++;
        changed.Candidate.Items[0] = Any.Pack(changedTrack);
        Assert.AreNotEqual(fingerprint, CheckedPcbRouteContract.CandidateFingerprint(changed),
            "A changed route property must change the candidate fingerprint.");

        var changedRemoval = request.Clone();
        changedRemoval.Removals.ItemIds[0].Value = Guid.NewGuid().ToString("D");
        Assert.AreNotEqual(fingerprint, CheckedPcbRouteContract.CandidateFingerprint(changedRemoval),
            "A changed deletion set must change the candidate fingerprint.");
    }

    [TestMethod]
    public void CandidateFingerprintRejectsDuplicateCustomPropertyKeys()
    {
        var (request, source, _) = CleanRoute();
        source.CustomProperties.Add(new CustomProperty { Key = "duplicate", Value = "one" });
        source.CustomProperties.Add(new CustomProperty { Key = "duplicate", Value = "two" });
        request.Candidate.Items.Add(Any.Pack(source));

        var error = Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbRouteContract.CandidateFingerprint(request));
        Assert.AreEqual("invalid_checked_pcb_route", error.Code);
    }

    private static (CheckedPcbRouteCommit Request, Track Track, DocumentLifecycleState State) CleanRoute()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "kicad-route-contract");
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "route.kicad_pcb",
            Project = new() { Name = "route", Path = projectPath }
        };
        var track = new Track
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            Start = new() { XNm = 1000, YNm = 2000 },
            End = new() { XNm = 3000, YNm = 2000 },
            Width = new() { ValueNm = 200 },
            Layer = BoardLayer.BlFCu,
            Net = new() { Name = "N1" }
        };
        var candidate = new CreateItems { Header = new() { Document = document } };
        candidate.Items.Add(Any.Pack(track));
        var nativeIdentity = Guid.NewGuid().ToString("D");
        string processEpoch = Guid.NewGuid().ToString("D");
        var state = new DocumentLifecycleState
        {
            Document = document,
            ProcessEpoch = processEpoch,
            NativeIdentity = nativeIdentity,
            Scope = DocumentLifecycleScope.DlsPcb,
            ProjectSettingsIncluded = true,
            CompleteChangeTracking = false,
            DiskBaselineChecked = true,
            StateSha256 = new string('a', 64),
            Revision = new() { Epoch = nativeIdentity, Sequence = 4 }
        };
        string board = Path.Combine(projectPath, document.BoardFilename);
        string project = Path.Combine(projectPath, "route.kicad_pro");
        state.NativeFiles.Add(board);
        state.NativeFiles.Add(project);
        foreach (string path in new[] { board, project })
            state.FileBaselines.Add(new NativeFileBaselineState
            {
                Path = path, BaselinePath = path, BaselineKnown = true, CurrentKnown = true,
                BaselineExists = false, CurrentExists = false, Status = NativeFileBaselineStatus.NfbsUnchanged
            });
        var request = new CheckedPcbRouteCommit
        {
            Candidate = candidate,
            ExpectedState = state,
            DrcJobId = Guid.NewGuid().ToString("D"),
            OperationId = Guid.NewGuid().ToString("D"),
            MaximumResultBytes = 128 * 1024
        };
        return (request, track, state);
    }
}
