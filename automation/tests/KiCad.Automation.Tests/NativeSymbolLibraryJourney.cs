using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;
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
        var artworkRead = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = "GeneratedArtworkProbe" });
        RequireToolSuccess(artworkRead);
        Assert.IsTrue(artworkRead.GetProperty("structuredContent").GetProperty("definition").GetProperty("items")
            .EnumerateArray().Any(item => item.GetProperty("item").TryGetProperty("@type", out var type)
                && type.GetString() == "type.googleapis.com/kiapi.schematic.types.SchematicField"
                && item.GetProperty("item").TryGetProperty("customProperties", out var properties)
                && properties.EnumerateArray().Any(property => property.GetProperty("key").GetString() == "kicad.ai.symbol_artwork.source_sha256")), artworkRead.GetRawText());
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
}
