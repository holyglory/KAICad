using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;
using System.Text.Json;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativeSymbolLibrary(NativeClient client, DocumentSpecifier document,
        string evidence, string instanceId, CancellationToken token)
    {
        const string libraryNickname = "RebindFixture";
        const string symbolName = "ReplacedProbe";

        string stateDirectory = Path.Combine(evidence, instanceId + "-symbol-library-mcp");
        await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
            stateDirectory, Path.Combine(evidence, instanceId + "-symbol-library-mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new
            { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        var list = await mcp.Tool("kicad_symbol_library_list", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname
        });
        RequireToolSuccess(list);
        string initialModifyHash = list.GetProperty("structuredContent").GetProperty("modifyHash").GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsTrue(list.GetProperty("structuredContent").GetProperty("symbolNames")
            .EnumerateArray().Any(item => item.GetString() == symbolName), list.GetRawText());

        var read = await mcp.Tool("kicad_symbol_library_read", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            libraryNickname, symbolName
        });
        RequireToolSuccess(read);
        var definition = read.GetProperty("structuredContent").GetProperty("definition");
        Assert.AreEqual(libraryNickname, definition.GetProperty("id").GetProperty("libraryNickname").GetString());
        Assert.AreEqual(symbolName, definition.GetProperty("id").GetProperty("entryName").GetString());
        Assert.IsFalse(read.GetProperty("structuredContent").GetProperty("revisionTracked").GetBoolean());
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-symbol-library-read.json"), read.GetRawText(), token);

        string definitionJson = definition.GetRawText();
        const string artworkSvg = "<svg viewBox=\"0 0 10 10\"><line x1=\"1\" y1=\"2\" x2=\"8\" y2=\"2\"/></svg>";
        string artworkHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(artworkSvg)));
        var artworkPlan = await mcp.Tool("kicad_symbol_svg_plan", new
        {
            libraryNickname, symbolName = "GeneratedArtworkProbe", svg = artworkSvg, sourceSha256 = artworkHash,
            sourceKind = "generated", assetId = Guid.NewGuid().ToString("D"), originXNm = "0", originYNm = "0",
            nanometersPerSvgUnit = "100000", strokeWidthNm = 100000
        });
        RequireToolSuccess(artworkPlan);
        var artworkData = artworkPlan.GetProperty("structuredContent");
        Assert.IsTrue(artworkData.GetProperty("proposalOnly").GetBoolean());
        string artworkDefinitionJson = artworkData.GetProperty("definitionJson").GetString()!;
        var artworkValidation = await mcp.Tool("kicad_symbol_library_validate", new
        { libraryNickname, definitionJson = artworkDefinitionJson });
        RequireToolSuccess(artworkValidation);
        Assert.IsFalse(artworkValidation.GetProperty("structuredContent").GetProperty("valid").GetBoolean(),
            "SVG-only symbols must remain rejected until pins and complete fields are supplied.");
        var completeArtworkDefinition = SchematicJson.Parser.Parse<SchematicSymbol>(definitionJson);
        var artworkGraphics = SchematicJson.Parser.Parse<SchematicSymbol>(artworkDefinitionJson);
        completeArtworkDefinition.Id.EntryName = "GeneratedArtworkProbe";
        completeArtworkDefinition.Items.Add(artworkGraphics.Items);
        string completeArtworkJson = SchematicJson.Formatter.Format(completeArtworkDefinition);
        var completeArtworkValidation = await mcp.Tool("kicad_symbol_library_validate", new
        { libraryNickname, definitionJson = completeArtworkJson });
        RequireToolSuccess(completeArtworkValidation);
        Assert.IsTrue(completeArtworkValidation.GetProperty("structuredContent").GetProperty("valid").GetBoolean(), completeArtworkValidation.GetRawText());
        string artworkWriteId = Guid.NewGuid().ToString("D");
        var artworkWrite = await mcp.Tool("kicad_symbol_library_write", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname,
            definitionJson = completeArtworkJson, operationId = artworkWriteId, overwrite = true,
            expectedModifyHash = initialModifyHash
        });
        RequireToolSuccess(artworkWrite);
        Assert.IsTrue(artworkWrite.GetProperty("structuredContent").GetProperty("persistenceVerified").GetBoolean(), artworkWrite.GetRawText());
        Assert.AreEqual("native-symbol-library-readback", artworkWrite.GetProperty("structuredContent").GetProperty("persistenceSource").GetString());
        Assert.AreEqual(64, artworkWrite.GetProperty("structuredContent").GetProperty("persistedContentSha256").GetString()!.Length);
        Assert.AreEqual(artworkWrite.GetProperty("structuredContent").GetProperty("afterModifyHash").GetUInt64(),
            artworkWrite.GetProperty("structuredContent").GetProperty("persistedModifyHash").GetUInt64());
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
                hostReceipt = "symbol-library-projection"
            }
        });
        RequireToolSuccess(rasterTrace);
        var rasterProjection = await mcp.Tool("kicad_artwork_raster_project", new
        {
            instanceId,
            input = new
            {
                operationId = Guid.NewGuid().ToString("D"), targetKind = "SYMBOL", expectedProcessEpoch = client.Epoch,
                sourcePath = rasterSource, sourceKind = "generated", sourceOrigin = "deterministic-fixture",
                proposal = rasterTrace.GetProperty("structuredContent").GetProperty("trace"),
                symbol = new
                {
                    libraryNickname, symbolName = "GeneratedArtworkProbe",
                    expectedModifyHash = artworkWrite.GetProperty("structuredContent").GetProperty("afterModifyHash").GetUInt64().ToString()
                }
            }
        });
        RequireToolSuccess(rasterProjection);
        Assert.IsTrue(rasterProjection.GetProperty("structuredContent").GetProperty("accepted").GetBoolean(), rasterProjection.GetRawText());
        Assert.IsTrue(rasterProjection.GetProperty("structuredContent").GetProperty("embeddedSourceVerified").GetBoolean(), rasterProjection.GetRawText());
        string artworkAfterModifyHash = rasterProjection.GetProperty("structuredContent").GetProperty("nativeResult")
            .GetProperty("afterModifyHash").GetUInt64().ToString();
        SchematicSymbol expectedArtwork = SchematicJson.Parser.Parse<SchematicSymbol>(
            artworkWrite.GetProperty("structuredContent").GetProperty("definition").GetRawText());
        string[] createdRasterIds = rasterProjection.GetProperty("structuredContent").GetProperty("createdIds")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        JsonElement trace = rasterTrace.GetProperty("structuredContent").GetProperty("trace");
        Assert.AreEqual(trace.GetProperty("filledPieceCount").GetInt32(), createdRasterIds.Length,
            rasterProjection.GetRawText());
        var artworkRead = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = "GeneratedArtworkProbe" });
        RequireToolSuccess(artworkRead);
        Assert.AreEqual(artworkAfterModifyHash,
            artworkRead.GetProperty("structuredContent").GetProperty("modifyHash").GetUInt64().ToString(),
            "the projection readback must retain the native library modify identity");
        SchematicSymbol projectedArtwork = SchematicJson.Parser.Parse<SchematicSymbol>(
            artworkRead.GetProperty("structuredContent").GetProperty("definition").GetRawText());
        AssertSymbolContent(projectedArtwork, expectedArtwork,
            "the native projection readback must retain the source symbol's pins and fields");
        AssertRasterSymbolPieces(projectedArtwork, trace, createdRasterIds, rasterSha,
            sourceKind: "generated", sourceOrigin: "deterministic-fixture");
        AssertRasterSymbolEmbeddedSource(projectedArtwork, rasterSha, rasterBytes.Length);
        string projectedCanonical = SchematicJson.Formatter.Format(projectedArtwork);

        // Symbol-library writes are project-scoped, so close and reopen the
        // clean schematic through the native lifecycle before reading the
        // library again. This forces the adapter to reload the persisted
        // .kicad_sym file while keeping the test's disposable project intact.
        DocumentLifecycleState beforeClose = await ObserveLifecycleState(client, document, token);
        Assert.IsFalse(beforeClose.NativeContentDirty, "the symbol projection must not dirty the schematic document");
        string schematicPath = beforeClose.NativeFiles.Single(path =>
            string.Equals(Path.GetFileName(path), document.Project.Name + ".kicad_sch", StringComparison.OrdinalIgnoreCase));
        string closeOperationId = Guid.NewGuid().ToString("D");
        var close = await mcp.Tool("kicad_document_close", new
        {
            instanceId, expectedStateJson = SchematicJson.Formatter.Format(beforeClose), operationId = closeOperationId
        });
        RequireToolSuccess(close);
        var reopened = await CreateRootThroughMcp(client.Endpoint, instanceId, schematicPath, evidence, token,
            toolName: "kicad_schematic_open");
        Assert.AreEqual(document, reopened.Document, "closing and reopening the project schematic must preserve its native identity");
        var artworkReloaded = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = "GeneratedArtworkProbe" });
        RequireToolSuccess(artworkReloaded);
        Assert.AreEqual(artworkAfterModifyHash,
            artworkReloaded.GetProperty("structuredContent").GetProperty("modifyHash").GetUInt64().ToString(),
            "the native modify identity must survive close and reopen");
        SchematicSymbol reloadedArtwork = SchematicJson.Parser.Parse<SchematicSymbol>(
            artworkReloaded.GetProperty("structuredContent").GetProperty("definition").GetRawText());
        Assert.AreEqual(projectedCanonical, SchematicJson.Formatter.Format(reloadedArtwork),
            "the projected symbol must be byte-stable across native close and reopen");
        AssertSymbolContent(reloadedArtwork, expectedArtwork,
            "close and reopen must retain the source symbol's pins and fields");
        AssertRasterSymbolPieces(reloadedArtwork, trace, createdRasterIds, rasterSha,
            sourceKind: "generated", sourceOrigin: "deterministic-fixture");
        AssertRasterSymbolEmbeddedSource(reloadedArtwork, rasterSha, rasterBytes.Length);
        var artworkDelete = await mcp.Tool("kicad_symbol_library_delete", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = "GeneratedArtworkProbe",
            operationId = Guid.NewGuid().ToString("D"), expectedModifyHash = artworkAfterModifyHash
        });
        RequireToolSuccess(artworkDelete);
        Assert.IsTrue(artworkDelete.GetProperty("structuredContent").GetProperty("persistenceVerified").GetBoolean(), artworkDelete.GetRawText());
        Assert.AreEqual("native-symbol-library-readback", artworkDelete.GetProperty("structuredContent").GetProperty("persistenceSource").GetString());
        Assert.AreEqual(artworkDelete.GetProperty("structuredContent").GetProperty("afterModifyHash").GetUInt64(),
            artworkDelete.GetProperty("structuredContent").GetProperty("persistedModifyHash").GetUInt64());
        var postArtworkList = await mcp.Tool("kicad_symbol_library_list", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname });
        RequireToolSuccess(postArtworkList);
        string postArtworkModifyHash = postArtworkList.GetProperty("structuredContent").GetProperty("modifyHash").GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        string writeOperationId = Guid.NewGuid().ToString("D");
        var write = await mcp.Tool("kicad_symbol_library_write", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname,
            definitionJson, operationId = writeOperationId, overwrite = true, expectedModifyHash = postArtworkModifyHash
        });
        RequireToolSuccess(write);
        Assert.IsTrue(write.GetProperty("structuredContent").GetProperty("nativeCommandSucceeded").GetBoolean());
        Assert.AreEqual("modify-hash-matched", write.GetProperty("structuredContent").GetProperty("revisionAdmission").GetString());
        Assert.IsTrue(write.GetProperty("structuredContent").GetProperty("persistenceVerified").GetBoolean(), write.GetRawText());
        Assert.AreEqual("native-symbol-library-readback", write.GetProperty("structuredContent").GetProperty("persistenceSource").GetString());
        Assert.AreEqual(write.GetProperty("structuredContent").GetProperty("afterModifyHash").GetUInt64(),
            write.GetProperty("structuredContent").GetProperty("persistedModifyHash").GetUInt64());
        var writeReplay = await mcp.Tool("kicad_symbol_library_write", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname,
            definitionJson, operationId = writeOperationId, overwrite = true, expectedModifyHash = postArtworkModifyHash
        });
        RequireToolSuccess(writeReplay);
        Assert.AreEqual(write.GetProperty("structuredContent").GetRawText(), writeReplay.GetProperty("structuredContent").GetRawText());
        var staleWrite = await mcp.Tool("kicad_symbol_library_write", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname,
            definitionJson, operationId = Guid.NewGuid().ToString("D"), overwrite = true,
            expectedModifyHash = initialModifyHash
        });
        Assert.IsTrue(staleWrite.TryGetProperty("isError", out var staleError) && staleError.GetBoolean(), staleWrite.GetRawText());
        Assert.AreEqual("symbol_library_changed", staleWrite.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        var writeReceipt = await mcp.Tool("kicad_symbol_library_receipt", new
        { instanceId, expectedProcessEpoch = client.Epoch, operationId = writeOperationId });
        RequireToolSuccess(writeReceipt);
        Assert.IsTrue(writeReceipt.GetProperty("structuredContent").GetProperty("found").GetBoolean(), writeReceipt.GetRawText());
        string deleteOperationId = Guid.NewGuid().ToString("D");
        var delete = await mcp.Tool("kicad_symbol_library_delete", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName,
            operationId = deleteOperationId,
            expectedModifyHash = write.GetProperty("structuredContent").GetProperty("afterModifyHash").GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        RequireToolSuccess(delete);
        Assert.IsTrue(delete.GetProperty("structuredContent").GetProperty("nativeCommandSucceeded").GetBoolean());
        Assert.IsTrue(delete.GetProperty("structuredContent").GetProperty("persistenceVerified").GetBoolean(), delete.GetRawText());
        Assert.AreEqual("native-symbol-library-readback", delete.GetProperty("structuredContent").GetProperty("persistenceSource").GetString());
        Assert.AreEqual(delete.GetProperty("structuredContent").GetProperty("afterModifyHash").GetUInt64(),
            delete.GetProperty("structuredContent").GetProperty("persistedModifyHash").GetUInt64());
        var deleteReplay = await mcp.Tool("kicad_symbol_library_delete", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName,
            operationId = deleteOperationId,
            expectedModifyHash = write.GetProperty("structuredContent").GetProperty("afterModifyHash").GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        RequireToolSuccess(deleteReplay);
        Assert.AreEqual(delete.GetProperty("structuredContent").GetRawText(), deleteReplay.GetProperty("structuredContent").GetRawText());
        var deleteReceipt = await mcp.Tool("kicad_symbol_library_receipt", new
        { instanceId, expectedProcessEpoch = client.Epoch, operationId = deleteOperationId });
        RequireToolSuccess(deleteReceipt);
        Assert.IsTrue(deleteReceipt.GetProperty("structuredContent").GetProperty("found").GetBoolean(), deleteReceipt.GetRawText());
        var restore = await mcp.Tool("kicad_symbol_library_write", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname,
            definitionJson, operationId = Guid.NewGuid().ToString("D"), overwrite = true,
            expectedModifyHash = delete.GetProperty("structuredContent").GetProperty("afterModifyHash").GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        RequireToolSuccess(restore);
        Assert.IsTrue(restore.GetProperty("structuredContent").GetProperty("persistenceVerified").GetBoolean(), restore.GetRawText());
        var restoredList = await mcp.Tool("kicad_symbol_library_list", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname });
        RequireToolSuccess(restoredList);
        Assert.IsTrue(restoredList.GetProperty("structuredContent").GetProperty("symbolNames")
            .EnumerateArray().Any(item => item.GetString() == symbolName), restoredList.GetRawText());
    }

    private static void AssertSymbolContent(SchematicSymbol actual, SchematicSymbol expected, string message)
    {
        CollectionAssert.IsSubsetOf(SymbolPins(expected), SymbolPins(actual), message + ": pins");
        CollectionAssert.IsSubsetOf(SymbolFields(expected), SymbolFields(actual), message + ": fields");
    }

    private static string[] SymbolPins(SchematicSymbol symbol) => symbol.Items
        .Where(child => child.Item?.Is(SchematicPin.Descriptor) == true)
        .Select(child => SchematicJson.Formatter.Format(child.Item!.Unpack<SchematicPin>()))
        .Order(StringComparer.Ordinal).ToArray();

    private static string[] SymbolFields(SchematicSymbol symbol)
    {
        IEnumerable<SchematicField> fields = new[]
        {
            symbol.ReferenceField, symbol.ValueField, symbol.FootprintField,
            symbol.DatasheetField, symbol.DescriptionField
        }.Where(field => field is not null)!;
        fields = fields.Concat(symbol.Items.Where(child => child.Item?.Is(SchematicField.Descriptor) == true)
            .Select(child => child.Item!.Unpack<SchematicField>()));
        return fields.Select(field => SchematicJson.Formatter.Format(field))
            .Order(StringComparer.Ordinal).ToArray();
    }

    private static void AssertRasterSymbolPieces(SchematicSymbol symbol, JsonElement trace,
        string[] createdIds, string sourceSha256, string sourceKind, string sourceOrigin)
    {
        JsonElement[] pieces = trace.GetProperty("filledPieces").EnumerateArray().ToArray();
        Assert.AreEqual(pieces.Length, createdIds.Length, "the native symbol contains every projected filled raster piece");
        foreach ((string id, int index) in createdIds.Select((id, index) => (id, index)))
        {
            SchematicGraphicShape shape = symbol.Items
                .Where(child => child.Item?.Is(SchematicGraphicShape.Descriptor) == true)
                .Select(child => child.Item!.Unpack<SchematicGraphicShape>())
                .Single(candidate => candidate.Id.Value == id);
            Assert.AreEqual(GraphicShape.GeometryOneofCase.Polygon, shape.Shape.GeometryCase, id);
            PolygonWithHoles polygon = shape.Shape.Polygon.Polygons.Single();
            Assert.IsTrue(polygon.Outline.Closed, id);
            string[] expectedPoints = pieces[index].GetProperty("points").EnumerateArray()
                .Select(point => $"{Long(point.GetProperty("xNm"))}:{Long(point.GetProperty("yNm"))}")
                .ToArray();
            List<string> actualPointList = polygon.Outline.Nodes
                .Select(point => $"{point.Point.XNm}:{point.Point.YNm}").ToList();
            if (actualPointList.Count > 1 && actualPointList[0] == actualPointList[^1])
                actualPointList.RemoveAt(actualPointList.Count - 1);
            string[] actualPoints = actualPointList.ToArray();
            CollectionAssert.AreEqual(expectedPoints, actualPoints, id);
            Assert.AreEqual(pieces[index].GetProperty("id").GetString(), Property(shape, "kicad.ai.raster_trace.piece_id"), id);
            Assert.AreEqual(sourceSha256, Property(shape, "kicad.ai.raster_trace.source_sha256"), id);
            Assert.AreEqual(trace.GetProperty("geometrySha256").GetString(), Property(shape, "kicad.ai.raster_trace.geometry_sha256"), id);
            Assert.AreEqual(sourceKind, Property(shape, "kicad.ai.raster_trace.source_kind"), id);
            Assert.AreEqual(sourceOrigin, Property(shape, "kicad.ai.raster_trace.source_origin"), id);
            Assert.AreEqual("filled-vector-projection", Property(shape, "kicad.ai.raster_trace.role"), id);
            Assert.AreEqual("true", Property(shape, "kicad.ai.raster_trace.filled"), id);
        }

        SchematicField manifest = symbol.Items
            .Where(child => child.Item?.Is(SchematicField.Descriptor) == true)
            .Select(child => child.Item!.Unpack<SchematicField>())
            .Single(field => field.Name == "kicad.ai.raster_trace_manifest");
        Assert.AreEqual(sourceSha256, Property(manifest, "kicad.ai.raster_trace.source_sha256"));
        Assert.AreEqual(trace.GetProperty("geometrySha256").GetString(), Property(manifest, "kicad.ai.raster_trace.geometry_sha256"));
        Assert.AreEqual(sourceKind, Property(manifest, "kicad.ai.raster_trace.source_kind"));
        Assert.AreEqual(sourceOrigin, Property(manifest, "kicad.ai.raster_trace.source_origin"));
        StringAssert.Contains(manifest.Text.Text_, "kicad.ai.raster_trace.v1");
    }

    private static void AssertRasterSymbolEmbeddedSource(SchematicSymbol symbol, string sourceSha256, int sourceBytes)
    {
        Assert.IsNotNull(symbol.EmbeddedFiles, "the projected symbol must carry embedded source files");
        EmbeddedFile source = symbol.EmbeddedFiles!.Files.Single(file => file.Name == "raster_trace_" + sourceSha256 + ".png");
        Assert.AreEqual(sourceSha256, source.DataHash);
        Assert.IsFalse(source.Data.IsEmpty, "the projected symbol must retain the compressed source payload");
        Assert.IsGreaterThan(0, sourceBytes);
    }

    private static string Property(SchematicGraphicShape shape, string key) => shape.CustomProperties
        .Single(property => property.Key == key).Value;

    private static string Property(SchematicField field, string key) => field.CustomProperties
        .Single(property => property.Key == key).Value;

    private static long Long(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
        : value.GetInt64();
}
