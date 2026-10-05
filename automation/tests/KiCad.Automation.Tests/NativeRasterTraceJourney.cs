using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativeRasterTrace(NativeClient client, string evidence,
        string instanceId, CancellationToken token)
    {
        string source = Path.Combine(RepositoryRoot(), "automation", "tests", "fixtures", "raster_trace",
            "outer-hole-diagonal-64.png");
        byte[] bytes = await File.ReadAllBytesAsync(source, token);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.AreEqual("0837b533060887d38b4685c505aa34622ca175cfb28741d5643729e42506fe10", hash);

        string stateDirectory = Path.Combine(evidence, instanceId + "-raster-trace-mcp");
        await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
            stateDirectory, Path.Combine(evidence, instanceId + "-raster-trace-mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new
            { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        var input = new
        {
            sourcePath = source,
            expectedSourceSha256 = hash,
            physicalWidthNm = 64_000_000L,
            threshold = 128,
            invert = false,
            turdSize = 0,
            curveToleranceNm = 1_000L,
            maxContours = 4096,
            maxVertices = 1_000_000,
            sourceOrigin = "deterministic-fixture",
            hostReceipt = "fixture-manifest"
        };
        var first = await mcp.Tool("kicad_artwork_raster_trace", new { instanceId, input });
        RequireToolSuccess(first);
        var data = first.GetProperty("structuredContent");
        var trace = data.GetProperty("trace");
        Assert.IsTrue(trace.GetProperty("accepted").GetBoolean(), first.GetRawText());
        Assert.AreEqual(64, trace.GetProperty("decodedWidth").GetInt32());
        Assert.AreEqual(64, trace.GetProperty("decodedHeight").GetInt32());
        Assert.AreEqual(hash, trace.GetProperty("sourceSha256").GetString());
        Assert.IsGreaterThan(0, trace.GetProperty("contourCount").GetInt32());
        Assert.IsGreaterThan(0, trace.GetProperty("filledPieceCount").GetInt32());
        var contours = trace.GetProperty("contours").EnumerateArray().ToArray();
        Assert.AreEqual(4, contours.Length, first.GetRawText());
        Assert.AreEqual(2, contours.Count(contour => !contour.GetProperty("hole").GetBoolean()
            && string.IsNullOrEmpty(contour.GetProperty("parentId").GetString())), first.GetRawText());
        Assert.AreEqual(1, contours.Count(contour => contour.GetProperty("hole").GetBoolean()), first.GetRawText());
        Assert.AreEqual(1, contours.Count(contour => !contour.GetProperty("hole").GetBoolean()
            && contour.GetProperty("parentId").GetString() is { Length: > 0 }), first.GetRawText());
        Assert.IsFalse(data.GetProperty("nativeDocumentMutated").GetBoolean());
        Assert.IsFalse(data.GetProperty("cadProjectionCommitted").GetBoolean());
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-raster-trace.json"), first.GetRawText(), token);

        var replay = await mcp.Tool("kicad_artwork_raster_trace", new { instanceId, input });
        RequireToolSuccess(replay);
        Assert.AreEqual(first.GetProperty("structuredContent").GetRawText(), replay.GetProperty("structuredContent").GetRawText());

        var mismatch = await mcp.Tool("kicad_artwork_raster_trace", new
        {
            instanceId,
            input = new { sourcePath = source, expectedSourceSha256 = new string('0', 64), physicalWidthNm = 64_000_000L,
                threshold = 128, invert = false, turdSize = 0, curveToleranceNm = 1_000L, maxContours = 4096, maxVertices = 1_000_000 }
        });
        Assert.IsTrue(mismatch.GetProperty("isError").GetBoolean(), mismatch.GetRawText());
        Assert.AreEqual("source_hash_mismatch", mismatch.GetProperty("structuredContent").GetProperty("errorCode").GetString());

        var tightBudget = await mcp.Tool("kicad_artwork_raster_trace", new
        {
            instanceId,
            input = new { sourcePath = source, expectedSourceSha256 = hash, physicalWidthNm = 64_000_000L,
                threshold = 128, invert = false, turdSize = 0, curveToleranceNm = 1_000L, maxContours = 4096, maxVertices = 3 }
        });
        Assert.IsFalse(tightBudget.GetProperty("isError").GetBoolean(), tightBudget.GetRawText());
        Assert.IsFalse(tightBudget.GetProperty("structuredContent").GetProperty("trace").GetProperty("accepted").GetBoolean(), tightBudget.GetRawText());
        Assert.AreEqual("trace_complexity", tightBudget.GetProperty("structuredContent").GetProperty("trace").GetProperty("rejectionCode").GetString());

        var tightTolerance = await mcp.Tool("kicad_artwork_raster_trace", new
        {
            instanceId,
            input = new { sourcePath = source, expectedSourceSha256 = hash, physicalWidthNm = 64_000_000L,
                threshold = 128, invert = false, turdSize = 0, curveToleranceNm = 1L, maxContours = 4096, maxVertices = 1_000_000 }
        });
        Assert.IsFalse(tightTolerance.GetProperty("isError").GetBoolean(), tightTolerance.GetRawText());
        Assert.IsFalse(tightTolerance.GetProperty("structuredContent").GetProperty("trace").GetProperty("accepted").GetBoolean(), tightTolerance.GetRawText());
        Assert.AreEqual("curve_tolerance_too_small", tightTolerance.GetProperty("structuredContent").GetProperty("trace").GetProperty("rejectionCode").GetString());

        var hugeCoordinates = await mcp.Tool("kicad_artwork_raster_trace", new
        {
            instanceId,
            input = new { sourcePath = source, expectedSourceSha256 = hash, physicalWidthNm = 3_000_000_000L,
                threshold = 128, invert = false, turdSize = 0, curveToleranceNm = 1_000L, maxContours = 4096, maxVertices = 1_000_000 }
        });
        Assert.IsFalse(hugeCoordinates.GetProperty("isError").GetBoolean(), hugeCoordinates.GetRawText());
        Assert.IsFalse(hugeCoordinates.GetProperty("structuredContent").GetProperty("trace").GetProperty("accepted").GetBoolean(), hugeCoordinates.GetRawText());
        Assert.AreEqual("coordinate_range", hugeCoordinates.GetProperty("structuredContent").GetProperty("trace").GetProperty("rejectionCode").GetString());

        await VerifyNativeRasterProjectionPersistence(client, mcp, source, hash, trace,
            evidence, instanceId, token);
    }

    private static async Task VerifyNativeRasterProjectionPersistence(NativeClient client, StdioMcpFixture mcp,
        string source, string sourceSha256, JsonElement trace, string evidence, string instanceId,
        CancellationToken token)
    {
        // Board projection is the smallest real target: it writes the filled
        // polygons through the native PCB item transaction and keeps the
        // source bytes in the explicit repository archive.  The projection
        // used to be deleted before any save, so this closes the native
        // save/close/reopen proof gap without changing the projection API.
        var session = await client.HandshakeAsync(token);
        string projectPath = session.ProjectPath;
        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        string boardPath = Path.ChangeExtension(projectPath, ".kicad_pcb");
        Assert.IsFalse(File.Exists(boardPath), "The raster persistence fixture must start with a new board.");

        string boardOpenEvidence = Path.Combine(evidence, instanceId + "-raster-projection-board-open");
        Directory.CreateDirectory(boardOpenEvidence);
        var opened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, boardOpenEvidence, token,
            toolName: "kicad_pcb_create");
        DocumentSpecifier board = opened.Document;
        Assert.AreEqual(Path.GetFileName(boardPath), board.BoardFilename);

        DocumentLifecycleState before = await ObserveLifecycleState(client, board, token);
        JsonElement documentJson = JsonDocument.Parse(SchematicJson.Formatter.Format(board)).RootElement.Clone();
        JsonElement expectedStateJson = JsonDocument.Parse(SchematicJson.Formatter.Format(before)).RootElement.Clone();

        // Seed a real source footprint through the same native placement
        // adapter used by the projection handler.  The source deliberately
        // carries pads, attributes and custom metadata so the clone path can
        // prove that generated artwork does not replace the existing design
        // content.
        string sourceFootprintId = Guid.NewGuid().ToString("D");
        FootprintInstance sourceFootprintCandidate = RasterSourceFootprint(sourceFootprintId);
        var sourcePlan = await mcp.Tool("kicad_pcb_footprint_place_plan", new
        {
            documentJson,
            expectedStateJson,
            footprintJson = BoardJson.Formatter.Format(sourceFootprintCandidate)
        });
        RequireToolSuccess(sourcePlan);
        JsonElement sourcePlanData = sourcePlan.GetProperty("structuredContent");
        var sourceApply = await mcp.Tool("kicad_pcb_footprint_place_apply", new
        {
            instanceId,
            candidateRequestJson = sourcePlanData.GetProperty("candidateRequestJson").GetString(),
            expectedStateJson = SchematicJson.Formatter.Format(before),
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(sourceApply);
        DocumentLifecycleState sourcePlacedState = SchematicJson.Parser.Parse<DocumentLifecycleState>(
            sourceApply.GetProperty("structuredContent").GetProperty("afterState").GetString()!);
        FootprintInstance sourceFootprint = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
        {
            Header = new() { Document = board },
            Items = { new KIID { Value = sourceFootprintId } }
        }, token)).Items.Single().Unpack<FootprintInstance>();
        AssertRasterFootprintMetadata(sourceFootprintCandidate, sourceFootprint,
            "the native source footprint must retain its seeded metadata");
        documentJson = JsonDocument.Parse(SchematicJson.Formatter.Format(board)).RootElement.Clone();
        expectedStateJson = JsonDocument.Parse(SchematicJson.Formatter.Format(sourcePlacedState)).RootElement.Clone();
        const string archivePath = "raster-trace/projection.png";
        var projection = await mcp.Tool("kicad_artwork_raster_project", new
        {
            instanceId,
            input = new
            {
                operationId = Guid.NewGuid().ToString("D"),
                targetKind = "BOARD",
                expectedProcessEpoch = client.Epoch,
                sourcePath = source,
                sourceKind = "generated",
                sourceOrigin = "deterministic-fixture",
                proposal = trace,
                board = new
                {
                    document = documentJson,
                    expectedState = expectedStateJson,
                    layer = "F.SilkS",
                    originXNm = 30_000_000L,
                    originYNm = 30_000_000L,
                    repositoryRoot = projectDirectory,
                    sourceArchivePath = archivePath
                }
            }
        });
        RequireToolSuccess(projection);
        JsonElement projectionData = projection.GetProperty("structuredContent");
        Assert.IsTrue(projectionData.GetProperty("accepted").GetBoolean(), projection.GetRawText());
        Assert.IsFalse(projectionData.GetProperty("provenanceAtomic").GetBoolean(), projection.GetRawText());
        Assert.AreEqual(sourceSha256, projectionData.GetProperty("sourceSha256").GetString(), projection.GetRawText());
        Assert.AreEqual(trace.GetProperty("geometrySha256").GetString(), projectionData.GetProperty("geometrySha256").GetString(),
            projection.GetRawText());
        string archive = projectionData.GetProperty("sourceArchive").GetString()!;
        Assert.AreEqual(archivePath, archive, projection.GetRawText());
        string archivedSource = Path.Combine(projectDirectory, archive.Replace('/', Path.DirectorySeparatorChar));
        Assert.IsTrue(File.Exists(archivedSource), projection.GetRawText());
        Assert.AreEqual(sourceSha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archivedSource, token))));

        string[] createdIds = projectionData.GetProperty("createdIds").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        Assert.AreEqual(trace.GetProperty("filledPieceCount").GetInt32(), createdIds.Length, projection.GetRawText());
        var live = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
        {
            Header = new() { Document = board },
            Items = { createdIds.Select(id => new KIID { Value = id }) }
        }, token);
        Assert.AreEqual(createdIds.Length, live.Items.Count, projection.GetRawText());
        var expectedGeometry = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var item in live.Items)
        {
            Assert.IsTrue(item.Is(BoardGraphicShape.Descriptor), projection.GetRawText());
            BoardGraphicShape shape = item.Unpack<BoardGraphicShape>();
            Assert.AreEqual(BoardLayer.BlFSilkS, shape.Layer, projection.GetRawText());
            Assert.AreEqual(GraphicShape.GeometryOneofCase.Polygon, shape.Shape.GeometryCase, projection.GetRawText());
            Assert.IsTrue(shape.Shape.Polygon.Polygons.Single().Outline.Closed, projection.GetRawText());
            expectedGeometry[shape.Id.Value] = shape.Shape.Polygon.Polygons.Single().Outline.Nodes
                .Select(node => $"{node.Point.XNm}:{node.Point.YNm}").ToArray();
            Assert.AreEqual(trace.GetProperty("geometrySha256").GetString(), Property(shape, "kicad.ai.raster_trace.geometry_sha256"), projection.GetRawText());
            Assert.AreEqual(sourceSha256, Property(shape, "kicad.ai.raster_trace.source_sha256"), projection.GetRawText());
            Assert.AreEqual("filled-vector-projection", Property(shape, "kicad.ai.raster_trace.role"), projection.GetRawText());
            Assert.AreEqual(archivePath, Property(shape, "kicad.ai.raster_trace.source_archive"), projection.GetRawText());
        }

        DocumentLifecycleState projected = await ObserveLifecycleState(client, board, token);
        var saved = await mcp.Tool("kicad_document_save", new
        {
            instanceId,
            expectedStateJson = SchematicJson.Formatter.Format(projected),
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(saved);
        JsonElement savedData = saved.GetProperty("structuredContent");
        DocumentLifecycleState savedState = SchematicJson.Parser.Parse<DocumentLifecycleState>(
            savedData.GetProperty("observedState").GetRawText());
        Assert.IsFalse(savedState.NativeContentDirty, saved.GetRawText());
        var closed = await mcp.Tool("kicad_document_close", new
        {
            instanceId,
            expectedStateJson = SchematicJson.Formatter.Format(savedState),
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(closed);
        var reopened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, boardOpenEvidence, token,
            toolName: "kicad_pcb_open");
        DocumentSpecifier reloadedBoard = reopened.Document;
        DocumentLifecycleState reloaded = await ObserveLifecycleState(client, reloadedBoard, token);
        Assert.IsFalse(reloaded.NativeContentDirty, "A board reopened from the saved projection must be clean.");
        var persisted = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
        {
            Header = new() { Document = reloadedBoard },
            Items = { createdIds.Select(id => new KIID { Value = id }) }
        }, token);
        Assert.AreEqual(createdIds.Length, persisted.Items.Count,
            "Every projected filled piece must survive a native save, close and reopen.");
        foreach (var item in persisted.Items)
        {
            Assert.IsTrue(item.Is(BoardGraphicShape.Descriptor), item.ToString());
            BoardGraphicShape shape = item.Unpack<BoardGraphicShape>();
            Assert.IsTrue(expectedGeometry.TryGetValue(shape.Id.Value, out string[]? geometry), shape.Id.Value);
            CollectionAssert.AreEqual(geometry, shape.Shape.Polygon.Polygons.Single().Outline.Nodes
                .Select(node => $"{node.Point.XNm}:{node.Point.YNm}").ToArray(), shape.Id.Value);
            Assert.AreEqual(trace.GetProperty("geometrySha256").GetString(), Property(shape, "kicad.ai.raster_trace.geometry_sha256"), shape.Id.Value);
            Assert.AreEqual(sourceSha256, Property(shape, "kicad.ai.raster_trace.source_sha256"), shape.Id.Value);
            Assert.AreEqual("filled-vector-projection", Property(shape, "kicad.ai.raster_trace.role"), shape.Id.Value);
            Assert.AreEqual(archivePath, Property(shape, "kicad.ai.raster_trace.source_archive"), shape.Id.Value);
        }

        // Reuse the saved board as the source for the footprint target.  This
        // exercises the real clone transaction after the board itself has
        // already survived a save/close/reopen cycle.
        DocumentLifecycleState footprintBefore = await ObserveLifecycleState(client, reloadedBoard, token);
        JsonElement footprintDocumentJson = JsonDocument.Parse(SchematicJson.Formatter.Format(reloadedBoard)).RootElement.Clone();
        JsonElement footprintExpectedStateJson = JsonDocument.Parse(SchematicJson.Formatter.Format(footprintBefore)).RootElement.Clone();
        string projectedFootprintId = Guid.NewGuid().ToString("D");
        var footprintProjection = await mcp.Tool("kicad_artwork_raster_project", new
        {
            instanceId,
            input = new
            {
                operationId = Guid.NewGuid().ToString("D"),
                targetKind = "FOOTPRINT",
                expectedProcessEpoch = client.Epoch,
                sourcePath = source,
                sourceKind = "generated",
                sourceOrigin = "deterministic-fixture",
                proposal = trace,
                footprint = new
                {
                    document = footprintDocumentJson,
                    expectedState = footprintExpectedStateJson,
                    sourceFootprintId,
                    targetFootprintId = projectedFootprintId,
                    libraryNickname = "Generated",
                    footprintName = "RasterProjected",
                    originXNm = 70_000_000L,
                    originYNm = 70_000_000L,
                    orientationDegrees = 0.0,
                    layer = "F.Cu",
                    artworkLayer = "F.SilkS"
                }
            }
        });
        RequireToolSuccess(footprintProjection);
        JsonElement footprintProjectionData = footprintProjection.GetProperty("structuredContent");
        Assert.IsTrue(footprintProjectionData.GetProperty("accepted").GetBoolean(), footprintProjection.GetRawText());
        Assert.IsTrue(footprintProjectionData.GetProperty("embeddedSourceVerified").GetBoolean(), footprintProjection.GetRawText());
        Assert.IsTrue(footprintProjectionData.GetProperty("provenanceAtomic").GetBoolean(), footprintProjection.GetRawText());
        Assert.AreEqual(sourceSha256, footprintProjectionData.GetProperty("sourceSha256").GetString(), footprintProjection.GetRawText());
        Assert.AreEqual(trace.GetProperty("geometrySha256").GetString(),
            footprintProjectionData.GetProperty("geometrySha256").GetString(), footprintProjection.GetRawText());

        FootprintInstance projectedFootprint = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
        {
            Header = new() { Document = reloadedBoard },
            Items = { new KIID { Value = projectedFootprintId } }
        }, token)).Items.Single().Unpack<FootprintInstance>();
        AssertRasterFootprintMetadata(sourceFootprint, projectedFootprint,
            "the live footprint projection must preserve source pads and metadata");
        Dictionary<string, string[]> expectedFootprintGeometry = AssertRasterFootprintArtwork(
            projectedFootprint, trace, sourceSha256, "live footprint projection");
        string embeddedName = "raster_trace_" + sourceSha256 + ".png";
        EmbeddedFile embedded = projectedFootprint.EmbeddedFiles.Files.Single(file => file.Name == embeddedName);
        Assert.AreEqual(sourceSha256, embedded.DataHash, footprintProjection.GetRawText());

        DocumentLifecycleState projectedFootprintState = await ObserveLifecycleState(client, reloadedBoard, token);
        var footprintSaved = await mcp.Tool("kicad_document_save", new
        {
            instanceId,
            expectedStateJson = SchematicJson.Formatter.Format(projectedFootprintState),
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(footprintSaved);
        DocumentLifecycleState footprintSavedState = SchematicJson.Parser.Parse<DocumentLifecycleState>(
            footprintSaved.GetProperty("structuredContent").GetProperty("observedState").GetRawText());
        Assert.IsFalse(footprintSavedState.NativeContentDirty, footprintSaved.GetRawText());
        var footprintClosed = await mcp.Tool("kicad_document_close", new
        {
            instanceId,
            expectedStateJson = SchematicJson.Formatter.Format(footprintSavedState),
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(footprintClosed);
        var footprintReopened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, boardOpenEvidence,
            token, toolName: "kicad_pcb_open");
        DocumentSpecifier reloadedFootprintBoard = footprintReopened.Document;
        DocumentLifecycleState footprintReloadedState = await ObserveLifecycleState(client, reloadedFootprintBoard, token);
        Assert.IsFalse(footprintReloadedState.NativeContentDirty,
            "A board reopened from the saved footprint projection must be clean.");

        FootprintInstance persistedSourceFootprint = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
        {
            Header = new() { Document = reloadedFootprintBoard },
            Items = { new KIID { Value = sourceFootprintId } }
        }, token)).Items.Single().Unpack<FootprintInstance>();
        AssertRasterFootprintMetadata(sourceFootprint, persistedSourceFootprint,
            "the original source footprint must survive the projection save/close/reopen");
        FootprintInstance persistedFootprint = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
        {
            Header = new() { Document = reloadedFootprintBoard },
            Items = { new KIID { Value = projectedFootprintId } }
        }, token)).Items.Single().Unpack<FootprintInstance>();
        AssertRasterFootprintMetadata(sourceFootprint, persistedFootprint,
            "the reopened footprint projection must preserve source pads and metadata");
        AssertRasterFootprintArtwork(persistedFootprint, trace, sourceSha256,
            "reopened footprint projection", expectedFootprintGeometry);
        EmbeddedFile persistedEmbedded = persistedFootprint.EmbeddedFiles.Files.Single(file => file.Name == embeddedName);
        Assert.AreEqual(sourceSha256, persistedEmbedded.DataHash, persistedFootprint.ToString());

        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-raster-projection-persistence.json"),
            JsonSerializer.Serialize(new
            {
                board = boardPath,
                sourceSha256,
                geometrySha256 = trace.GetProperty("geometrySha256").GetString(),
                archive = archivedSource,
                createdIds,
                saved = true,
                closed = true,
                reopened = true,
                persistedShapeCount = persisted.Items.Count,
                sourceFootprintId,
                projectedFootprintId,
                persistedFootprintPadCount = persistedFootprint.Definition.Items.Count(item => item.Is(Pad.Descriptor)),
                persistedFootprintArtworkCount = persistedFootprint.Definition.Items.Count(item => item.Is(BoardGraphicShape.Descriptor)),
                footprintSaved = true,
                footprintClosed = true,
                footprintReopened = true
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), token);
    }

    private static FootprintInstance RasterSourceFootprint(string id)
    {
        var footprint = new FootprintInstance
        {
            Id = new() { Value = id },
            Position = new() { XNm = 10_000_000, YNm = 10_000_000 },
            Orientation = new() { ValueDegrees = 15.0 },
            Layer = BoardLayer.BlFCu,
            Definition = new Footprint
            {
                Id = new() { LibraryNickname = "Generated", EntryName = "RasterSource" },
                Attributes = new FootprintAttributes
                {
                    Description = "Raster source fixture",
                    Keywords = "raster,source",
                    MountingStyle = FootprintMountingStyle.FmsSmd
                }
            },
            CustomProperties =
            {
                new CustomProperty { Key = "fixture-owner", Value = "raster-trace" },
                new CustomProperty { Key = "fixture-revision", Value = "source-v1" }
            }
        };
        footprint.Definition.Items.Add(Any.Pack(RasterSourcePad(
            "10000000-0000-4000-8000-000000000001", "1", 0, 0, "pad-one")));
        footprint.Definition.Items.Add(Any.Pack(RasterSourcePad(
            "10000000-0000-4000-8000-000000000002", "2", 2_000_000, 0, "pad-two")));
        return footprint;
    }

    private static Pad RasterSourcePad(string id, string number, long xNm, long yNm, string role)
        => new()
        {
            Id = new() { Value = id },
            Number = number,
            Type = PadType.PtSmd,
            Position = new() { XNm = xNm, YNm = yNm },
            PadStack = new PadStack
            {
                Type = PadStackType.PstNormal,
                Layers = { BoardLayer.BlFCu },
                CopperLayers =
                {
                    new PadStackLayer
                    {
                        Layer = BoardLayer.BlFCu,
                        Shape = PadStackShape.PssRoundrect,
                        Size = new() { XNm = 1_000_000, YNm = 800_000 },
                        CornerRoundingRatio = 0.2
                    }
                },
                FrontOuterLayers = new() { SolderMaskMode = SolderMaskMode.SmmMasked }
            },
            CustomProperties = { new CustomProperty { Key = "fixture-pad-role", Value = role } }
        };

    private static void AssertRasterFootprintMetadata(FootprintInstance expectedSource,
        FootprintInstance actual, string context)
    {
        Assert.AreEqual(expectedSource.Definition.Attributes, actual.Definition.Attributes, context);
        foreach (CustomProperty expected in expectedSource.CustomProperties)
        {
            Assert.IsTrue(actual.CustomProperties.Any(property => property.Key == expected.Key
                && property.Value == expected.Value), context + ": " + expected.Key);
        }
        Pad[] expectedPads = expectedSource.Definition.Items.Where(item => item.Is(Pad.Descriptor))
            .Select(item => item.Unpack<Pad>()).ToArray();
        Pad[] actualPads = actual.Definition.Items.Where(item => item.Is(Pad.Descriptor))
            .Select(item => item.Unpack<Pad>()).ToArray();
        Assert.AreEqual(expectedPads.Length, actualPads.Length, context);
        foreach (Pad expectedPad in expectedPads)
        {
            Pad actualPad = actualPads.Single(pad => pad.Id.Value == expectedPad.Id.Value);
            Pad expectedNormalized = expectedPad.Clone();
            expectedNormalized.Parent = new();
            Pad actualNormalized = actualPad.Clone();
            actualNormalized.Parent = new();
            Assert.AreEqual(expectedNormalized, actualNormalized, context + ": pad " + expectedPad.Number);
        }
    }

    private static Dictionary<string, string[]> AssertRasterFootprintArtwork(FootprintInstance footprint,
        JsonElement trace, string sourceSha256, string context,
        Dictionary<string, string[]>? expectedGeometry = null)
    {
        string geometrySha256 = trace.GetProperty("geometrySha256").GetString()!;
        string contributorSemantics = trace.GetProperty("contributorSemantics").GetString()!;
        var actualGeometry = new Dictionary<string, string[]>(StringComparer.Ordinal);
        BoardGraphicShape[] artwork = footprint.Definition.Items.Where(item => item.Is(BoardGraphicShape.Descriptor))
            .Select(item => item.Unpack<BoardGraphicShape>()).ToArray();
        Assert.AreEqual(trace.GetProperty("filledPieceCount").GetInt32(), artwork.Length, context);
        foreach (BoardGraphicShape shape in artwork)
        {
            Assert.AreEqual(BoardLayer.BlFSilkS, shape.Layer, context + ": layer");
            Assert.AreEqual(GraphicShape.GeometryOneofCase.Polygon, shape.Shape.GeometryCase, context + ": geometry");
            Assert.IsTrue(shape.Shape.Polygon.Polygons.Single().Outline.Closed, context + ": closed polygon");
            string[] geometry = shape.Shape.Polygon.Polygons.Single().Outline.Nodes
                .Select(node => $"{node.Point.XNm}:{node.Point.YNm}").ToArray();
            actualGeometry[shape.Id.Value] = geometry;
            Assert.AreEqual(geometrySha256, Property(shape, "kicad.ai.raster_trace.geometry_sha256"), context + ": geometry hash");
            Assert.AreEqual(sourceSha256, Property(shape, "kicad.ai.raster_trace.source_sha256"), context + ": source hash");
            Assert.AreEqual("generated", Property(shape, "kicad.ai.raster_trace.source_kind"), context + ": source kind");
            Assert.AreEqual("deterministic-fixture", Property(shape, "kicad.ai.raster_trace.source_origin"), context + ": source origin");
            Assert.AreEqual("filled-vector-projection", Property(shape, "kicad.ai.raster_trace.role"), context + ": role");
            Assert.AreEqual(contributorSemantics, Property(shape, "kicad.ai.raster_trace.contributor_semantics"), context + ": contributor semantics");
            if (expectedGeometry is not null)
            {
                Assert.IsTrue(expectedGeometry.TryGetValue(shape.Id.Value, out string[]? expected), context + ": shape " + shape.Id.Value);
                CollectionAssert.AreEqual(expected, geometry, context + ": shape " + shape.Id.Value);
            }
        }
        return actualGeometry;
    }

    private static string Property(BoardGraphicShape shape, string key)
        => shape.CustomProperties.Single(property => property.Key == key).Value;
}
