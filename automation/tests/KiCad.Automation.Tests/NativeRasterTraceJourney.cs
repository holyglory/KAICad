using System.Security.Cryptography;
using System.Text.Json;
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
                persistedShapeCount = persisted.Items.Count
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), token);
    }

    private static string Property(BoardGraphicShape shape, string key)
        => shape.CustomProperties.Single(property => property.Key == key).Value;
}
