using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Group = Kiapi.Schematic.Types.Group;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativeRasterSchematicProjection(NativeClient client,
        DocumentSpecifier document, string evidence, string instanceId, CancellationToken token)
    {
        // The existing diagonal fixture intentionally exercises sub-100 nm
        // Potrace coordinates and is rejected by the production schematic
        // quantum guard. Use a separate deterministic axis-aligned square so
        // this positive persistence journey proves the supported target path.
        const string sourceBase64 = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAAAAABWESUoAAAAHElEQVR42mP4TwAwDCEFDFjAqIJRBQOpYIjnLABIPm2vpQ0y7QAAAABJRU5ErkJggg==";
        string source = Path.Combine(evidence, instanceId + "-schematic-square.png");
        byte[] sourceBytes = Convert.FromBase64String(sourceBase64);
        await File.WriteAllBytesAsync(source, sourceBytes, token);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        Assert.AreEqual("561edf02355c988e4ff7d5393f561d0a39c0778e652a73dde082e115b6049c28", sourceSha256);

        string stateDirectory = Path.Combine(evidence, instanceId + "-raster-trace-schematic-mcp");
        await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
            stateDirectory, Path.Combine(evidence, instanceId + "-raster-trace-schematic-mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new
            { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        var traceReply = await mcp.Tool("kicad_artwork_raster_trace", new
        {
            instanceId,
            input = new
            {
                sourcePath = source,
                expectedSourceSha256 = sourceSha256,
                physicalWidthNm = 32_000_000L,
                threshold = 128,
                invert = false,
                turdSize = 0,
                curveToleranceNm = 1_000L,
                maxContours = 4096,
                maxVertices = 1_000_000,
                sourceOrigin = "deterministic-fixture",
                hostReceipt = "fixture-manifest"
            }
        });
        RequireToolSuccess(traceReply);
        JsonElement trace = traceReply.GetProperty("structuredContent").GetProperty("trace").Clone();
        Assert.IsTrue(trace.GetProperty("accepted").GetBoolean(), traceReply.GetRawText());
        Assert.AreEqual(sourceSha256, trace.GetProperty("sourceSha256").GetString());
        Assert.IsGreaterThan(0, trace.GetProperty("filledPieceCount").GetInt32());

        string documentJson = SchematicJson.Formatter.Format(document);
        var checkedReply = await mcp.Tool("kicad_schematic_checked_state", new { instanceId, documentJson });
        RequireToolSuccess(checkedReply);
        CheckedSchematicState before = SchematicJson.Parser.Parse<CheckedSchematicState>(
            checkedReply.GetProperty("structuredContent").GetRawText());
        CheckedSchematicContract.ValidateObservation(before, document, client.Epoch);
        SchematicScreenData root = before.Electrical.Hierarchy.Data.Instances.Single(screen =>
            screen.Metadata?.Document?.Equals(document) == true);
        SchematicText existingText = root.Items.Where(item => item.Is(SchematicText.Descriptor))
            .Select(item => item.Unpack<SchematicText>())
            .Single(item => item.Text.Text_ == "Native automation fixture");
        SchematicText existingTextBefore = existingText.Clone();
        int existingItemCount = root.Items.Count;

        string groupId = Guid.NewGuid().ToString("D");
        JsonElement expectedStateJson = JsonDocument.Parse(SchematicJson.Formatter.Format(before.State)).RootElement.Clone();
        JsonElement documentJsonElement = JsonDocument.Parse(documentJson).RootElement.Clone();
        var projection = await mcp.Tool("kicad_artwork_raster_project", new
        {
            instanceId,
            input = new
            {
                operationId = Guid.NewGuid().ToString("D"),
                targetKind = "SCHEMATIC",
                expectedProcessEpoch = client.Epoch,
                sourcePath = source,
                sourceKind = "generated",
                sourceOrigin = "deterministic-fixture",
                proposal = trace,
                schematic = new
                {
                    document = documentJsonElement,
                    expectedState = expectedStateJson,
                    originXNm = 30_000_000L,
                    originYNm = 30_000_000L,
                    manifestGroupId = groupId
                }
            }
        });
        RequireToolSuccess(projection);
        JsonElement projectionData = projection.GetProperty("structuredContent");
        Assert.IsTrue(projectionData.GetProperty("accepted").GetBoolean(), projection.GetRawText());
        Assert.IsTrue(projectionData.GetProperty("embeddedSourceVerified").GetBoolean(), projection.GetRawText());
        Assert.IsTrue(projectionData.GetProperty("provenanceAtomic").GetBoolean(), projection.GetRawText());
        Assert.IsFalse(projectionData.GetProperty("designFilesSaved").GetBoolean(), projection.GetRawText());
        Assert.AreEqual(sourceSha256, projectionData.GetProperty("sourceSha256").GetString(), projection.GetRawText());
        Assert.AreEqual(trace.GetProperty("geometrySha256").GetString(),
            projectionData.GetProperty("geometrySha256").GetString(), projection.GetRawText());

        string[] createdIds = projectionData.GetProperty("createdIds").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        Assert.AreEqual(trace.GetProperty("filledPieceCount").GetInt32(), createdIds.Length, projection.GetRawText());
        JsonElement receiptJson = projectionData.GetProperty("receipt");
        CheckedSchematicBatchReceipt receipt = SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(receiptJson.GetRawText());
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, projection.GetRawText());
        Assert.AreEqual(before.State, receipt.ObservedBefore, projection.GetRawText());

        SchematicScreenDataSnapshot projectedScreen = await client.InvokeAsync<ReadSchematicScreenData,
            SchematicScreenDataSnapshot>(new() { Document = document }, token);
        Assert.AreEqual(existingItemCount + createdIds.Length + 1, projectedScreen.Data.Items.Count,
            "The projection adds one group and one graphic per filled piece while retaining existing schematic objects.");
        AssertSchematicRasterArtwork(projectedScreen.Data, trace, createdIds, sourceSha256,
            "live schematic projection", 30_000_000L, 30_000_000L);
        AssertSchematicRasterGroup(projectedScreen.Data, groupId, createdIds, trace, sourceSha256,
            "live schematic projection group");
        SchematicText projectedExistingText = projectedScreen.Data.Items.Where(item => item.Is(SchematicText.Descriptor))
            .Select(item => item.Unpack<SchematicText>()).Single(item => item.Id.Equals(existingTextBefore.Id));
        Assert.AreEqual(existingTextBefore.Text.Text_, projectedExistingText.Text.Text_);
        Assert.AreEqual(existingTextBefore.Text.Position, projectedExistingText.Text.Position);
        string projectedEmbeddedHash = await AssertSchematicRasterEmbeddedSource(client, document, sourceSha256, sourceBytes.Length, token);

        DocumentLifecycleState projectedState = await ObserveLifecycleState(client, document, token);
        var saved = await mcp.Tool("kicad_document_save", new
        {
            instanceId,
            expectedStateJson = SchematicJson.Formatter.Format(projectedState),
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(saved);
        DocumentLifecycleState savedState = SchematicJson.Parser.Parse<DocumentLifecycleState>(
            saved.GetProperty("structuredContent").GetProperty("observedState").GetRawText());
        Assert.IsFalse(savedState.NativeContentDirty, saved.GetRawText());
        string schematicPath = savedState.NativeFiles.Single(path =>
            string.Equals(Path.GetFileName(path), document.Project.Name + ".kicad_sch", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(File.Exists(schematicPath), schematicPath);

        var closed = await mcp.Tool("kicad_document_close", new
        {
            instanceId,
            expectedStateJson = SchematicJson.Formatter.Format(savedState),
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(closed);
        var reopened = await CreateRootThroughMcp(client.Endpoint, instanceId, schematicPath, evidence, token,
            toolName: "kicad_schematic_open");
        Assert.AreEqual(document, reopened.Document,
            "Closing and reopening the project schematic must preserve its native document identity.");

        SchematicScreenDataSnapshot reloadedScreen = await client.InvokeAsync<ReadSchematicScreenData,
            SchematicScreenDataSnapshot>(new() { Document = reopened.Document }, token);
        Assert.AreEqual(existingItemCount + createdIds.Length + 1, reloadedScreen.Data.Items.Count,
            "The saved schematic keeps its original objects alongside the raster group and filled pieces.");
        AssertSchematicRasterArtwork(reloadedScreen.Data, trace, createdIds, sourceSha256,
            "reloaded schematic projection", 30_000_000L, 30_000_000L, requireExplicitClosure: false);
        AssertSchematicRasterGroup(reloadedScreen.Data, groupId, createdIds, trace, sourceSha256,
            "reloaded schematic projection group");
        SchematicText reloadedExistingText = reloadedScreen.Data.Items.Where(item => item.Is(SchematicText.Descriptor))
            .Select(item => item.Unpack<SchematicText>()).Single(item => item.Id.Equals(existingTextBefore.Id));
        Assert.AreEqual(existingTextBefore.Text.Text_, reloadedExistingText.Text.Text_);
        Assert.AreEqual(existingTextBefore.Text.Position, reloadedExistingText.Text.Position);
        string reloadedEmbeddedHash = await AssertSchematicRasterEmbeddedSource(client, reopened.Document, sourceSha256,
            sourceBytes.Length, token);
        Assert.AreEqual(projectedEmbeddedHash, reloadedEmbeddedHash,
            "The embedded source checksum must survive native save, close and reopen.");

        CheckedSchematicState after = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = reopened.Document, ProcessEpoch = client.Epoch }, token);
        CheckedSchematicContract.ValidateObservation(after, reopened.Document, client.Epoch);
        Assert.IsFalse(after.State.NativeContentDirty, "The reopened projection must be clean after the save.");
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-raster-schematic-projection.json"),
            JsonSerializer.Serialize(new
            {
                schematic = schematicPath,
                sourceSha256,
                geometrySha256 = trace.GetProperty("geometrySha256").GetString(),
                groupId,
                createdIds,
                filledPieceCount = createdIds.Length,
                existingItemCount,
                saved = true,
                closed = true,
                reopened = true,
                embeddedDataHash = reloadedEmbeddedHash
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), token);
    }

    private static void AssertSchematicRasterArtwork(SchematicScreenData data, JsonElement trace,
        string[] createdIds, string sourceSha256, string context, long originXNm, long originYNm,
        bool requireExplicitClosure = true)
    {
        JsonElement[] pieces = trace.GetProperty("filledPieces").EnumerateArray().ToArray();
        Assert.AreEqual(pieces.Length, createdIds.Length, context);
        var projectedPolygons = new List<RasterTraceProjectionAssertions.Polygon>();
        foreach ((string id, int index) in createdIds.Select((id, index) => (id, index)))
        {
            SchematicGraphicShape shape = data.Items.Where(item => item.Is(SchematicGraphicShape.Descriptor))
                .Select(item => item.Unpack<SchematicGraphicShape>()).Single(candidate => candidate.Id.Value == id);
            Assert.AreEqual(GraphicShape.GeometryOneofCase.Polygon, shape.Shape.GeometryCase, context + ": geometry");
            Assert.IsTrue(shape.Shape.Attributes.Fill.FillType is GraphicFillType.GftFilled
                or GraphicFillType.GftFilledWithColor, context + ": fill");
            PolygonWithHoles polygon = shape.Shape.Polygon.Polygons.Single();
            bool repeatedClosure = polygon.Outline.Nodes.Count > 1
                && polygon.Outline.Nodes[0].Point.Equals(polygon.Outline.Nodes[^1].Point);
            // KiCad's schematic reload normalizes filled PolygonWithHoles
            // outlines to an implicitly closed polyline: the explicit Closed
            // flag and repeated endpoint may both disappear while the fill and
            // ordered boundary points remain intact. The live checked batch
            // must still expose an explicit/repeated closure before save.
            if (requireExplicitClosure)
                Assert.IsTrue(polygon.Outline.Closed || repeatedClosure, context + ": closed polygon");
            (long X, long Y)[] expectedPoints = pieces[index].GetProperty("points").EnumerateArray()
                .Select(point => (RasterLong(point.GetProperty("xNm")) + originXNm, RasterLong(point.GetProperty("yNm")) + originYNm))
                .ToArray();
            List<(long X, long Y)> actualPoints = polygon.Outline.Nodes
                .Select(node => (node.Point.XNm, node.Point.YNm)).ToList();
            if (actualPoints.Count > 1 && actualPoints[0] == actualPoints[^1]) actualPoints.RemoveAt(actualPoints.Count - 1);
            Assert.AreEqual(expectedPoints.Length, actualPoints.Count, context + ": point count");
            for (int pointIndex = 0; pointIndex < expectedPoints.Length; pointIndex++)
            {
                Assert.IsTrue(Math.Abs(expectedPoints[pointIndex].X - actualPoints[pointIndex].X) <= 100
                    && Math.Abs(expectedPoints[pointIndex].Y - actualPoints[pointIndex].Y) <= 100,
                    $"{context}: {id} point {pointIndex} expected {expectedPoints[pointIndex]} actual {actualPoints[pointIndex]}");
            }
            // KiCad's schematic serializer currently drops custom properties
            // attached directly to graphic shapes. Check the per-piece
            // provenance at the live checked-batch boundary; after reload the
            // retained group manifest and embedded source are the durable
            // provenance records.
            if (requireExplicitClosure)
            {
                projectedPolygons.Add(new(id, SchematicProperty(shape, "kicad.ai.raster_trace.piece_id"),
                    polygon.Outline.Nodes.Select(node => new RasterTraceProjectionAssertions.Point(
                        node.Point.XNm, node.Point.YNm)).ToArray()));
                Assert.AreEqual(pieces[index].GetProperty("id").GetString(), SchematicProperty(shape, "kicad.ai.raster_trace.piece_id"), id);
                Assert.AreEqual(sourceSha256, SchematicProperty(shape, "kicad.ai.raster_trace.source_sha256"), id);
                Assert.AreEqual(trace.GetProperty("geometrySha256").GetString(), SchematicProperty(shape, "kicad.ai.raster_trace.geometry_sha256"), id);
                Assert.AreEqual("generated", SchematicProperty(shape, "kicad.ai.raster_trace.source_kind"), id);
                Assert.AreEqual("deterministic-fixture", SchematicProperty(shape, "kicad.ai.raster_trace.source_origin"), id);
                Assert.AreEqual("filled-vector-projection", SchematicProperty(shape, "kicad.ai.raster_trace.role"), id);
                Assert.AreEqual("true", SchematicProperty(shape, "kicad.ai.raster_trace.filled"), id);
            }
        }
        if (requireExplicitClosure)
            RasterTraceProjectionAssertions.AssertAgainstProposal(trace, createdIds, projectedPolygons,
                originXNm, originYNm, context);
    }

    private static void AssertSchematicRasterGroup(SchematicScreenData data, string groupId,
        string[] createdIds, JsonElement trace, string sourceSha256, string context)
    {
        Group group = data.Items.Where(item => item.Is(Group.Descriptor)).Select(item => item.Unpack<Group>())
            .Single(candidate => candidate.Id.Value == groupId);
        CollectionAssert.AreEquivalent(createdIds, group.Items.Select(item => item.Value).ToArray(), context + ": members");
        Assert.AreEqual("Raster trace artwork", group.Name, context + ": name");
        Assert.AreEqual(sourceSha256, SchematicProperty(group.CustomProperties, "kicad.ai.raster_trace.source_sha256"), context);
        Assert.AreEqual(trace.GetProperty("geometrySha256").GetString(),
            SchematicProperty(group.CustomProperties, "kicad.ai.raster_trace.geometry_sha256"), context);
        Assert.AreEqual("generated", SchematicProperty(group.CustomProperties, "kicad.ai.raster_trace.source_kind"), context);
        Assert.AreEqual("deterministic-fixture", SchematicProperty(group.CustomProperties, "kicad.ai.raster_trace.source_origin"), context);
        Assert.AreEqual("filled-vector-projection", SchematicProperty(group.CustomProperties, "kicad.ai.raster_trace.role"), context);

        // Schematic serializers currently drop custom properties attached to
        // each graphic shape.  The group is the durable provenance owner, so
        // its bounded manifest must survive save/reload and retain the exact
        // trace topology identity used by the projected vectors.
        JsonElement manifest = JsonDocument.Parse(
            SchematicProperty(group.CustomProperties, "kicad.ai.raster_trace.manifest")).RootElement;
        Assert.AreEqual("kicad.ai.raster_trace.v1", manifest.GetProperty("schema").GetString(), context);
        Assert.AreEqual(sourceSha256, manifest.GetProperty("sourceSha256").GetString(), context);
        Assert.AreEqual(trace.GetProperty("geometrySha256").GetString(),
            manifest.GetProperty("geometrySha256").GetString(), context);
        Assert.AreEqual("generated", manifest.GetProperty("sourceKind").GetString(), context);
        Assert.AreEqual("deterministic-fixture", manifest.GetProperty("sourceOrigin").GetString(), context);
        Assert.AreEqual(trace.GetProperty("contributorSemantics").GetString(),
            manifest.GetProperty("contributorSemantics").GetString(), context);
        Assert.AreEqual(trace.GetProperty("coordinateOrientation").GetString(),
            manifest.GetProperty("coordinateOrientation").GetString(), context);
        Assert.AreEqual(trace.GetProperty("rounding").GetString(),
            manifest.GetProperty("rounding").GetString(), context);

        JsonElement[] traceContours = trace.GetProperty("contours").EnumerateArray().ToArray();
        JsonElement[] manifestContours = manifest.GetProperty("contours").EnumerateArray().ToArray();
        Assert.AreEqual(traceContours.Length, manifestContours.Length, context + ": manifest contours");
        CollectionAssert.AreEquivalent(
            traceContours.Select(contour => contour.GetProperty("id").GetString()).ToArray(),
            manifestContours.Select(contour => contour.GetProperty("id").GetString()).ToArray(),
            context + ": manifest contour IDs");

        JsonElement[] tracePieces = trace.GetProperty("filledPieces").EnumerateArray().ToArray();
        JsonElement[] manifestPieces = manifest.GetProperty("pieces").EnumerateArray().ToArray();
        Assert.AreEqual(tracePieces.Length, manifestPieces.Length, context + ": manifest pieces");
        CollectionAssert.AreEquivalent(
            tracePieces.Select(piece => piece.GetProperty("id").GetString()).ToArray(),
            manifestPieces.Select(piece => piece.GetProperty("id").GetString()).ToArray(),
            context + ": manifest piece IDs");
        foreach (JsonElement tracePiece in tracePieces)
        {
            string pieceId = tracePiece.GetProperty("id").GetString()!;
            JsonElement manifestPiece = manifestPieces.Single(piece => piece.GetProperty("id").GetString() == pieceId);
            CollectionAssert.AreEqual(
                tracePiece.GetProperty("sourceContourIds").EnumerateArray().Select(id => id.GetString()).ToArray(),
                manifestPiece.GetProperty("sourceContourIds").EnumerateArray().Select(id => id.GetString()).ToArray(),
                context + ": manifest contour links for " + pieceId);
            Assert.AreEqual(tracePiece.GetProperty("sourceContoursAreWholeTrace").GetBoolean(),
                manifestPiece.GetProperty("sourceContoursAreWholeTrace").GetBoolean(),
                context + ": manifest trace completeness for " + pieceId);
        }
    }

    private static async Task<string> AssertSchematicRasterEmbeddedSource(NativeClient client,
        DocumentSpecifier document, string sourceSha256, int sourceBytes, CancellationToken token)
    {
        SchematicMetadataSnapshot metadata = await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(
            new() { Document = document }, token);
        Assert.IsNotNull(metadata.Metadata.EmbeddedFiles);
        EmbeddedFile source = metadata.Metadata.EmbeddedFiles.Files.Single(file =>
            file.Name == "raster_trace_" + sourceSha256 + ".png");
        // KiCad may normalize the embedded payload's legacy checksum to its
        // native 128-bit form on the first read. The filename and artwork
        // properties retain the caller's exact SHA-256; persistence requires
        // this native checksum and compressed payload to remain stable.
        Assert.IsFalse(string.IsNullOrWhiteSpace(source.DataHash));
        Assert.IsFalse(source.Data.IsEmpty);
        Assert.IsGreaterThan(0, sourceBytes);
        return source.DataHash;
    }

    private static string SchematicProperty(IEnumerable<CustomProperty> properties, string key) =>
        properties.Single(property => property.Key == key).Value;

    private static string SchematicProperty(SchematicGraphicShape shape, string key) =>
        SchematicProperty(shape.CustomProperties, key);

    private static long RasterLong(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
        : value.GetInt64();
}
