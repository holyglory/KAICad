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
        var sourceDisplay = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryPinDisplay>(
            read.GetProperty("structuredContent").GetProperty("pinDisplay").GetRawText());
        Assert.IsTrue(sourceDisplay.HasShowPinNames && sourceDisplay.HasShowPinNumbers && sourceDisplay.HasPinNameOffsetNm);
        Assert.IsFalse(sourceDisplay.ShowPinNames, "The native fixture explicitly hides pin names.");
        Assert.IsTrue(sourceDisplay.ShowPinNumbers);
        Assert.AreEqual(0L, sourceDisplay.PinNameOffsetNm, "Zero spacing is an explicit native fixture value.");
        var sourcePreview = await RenderLibraryPinDisplay(mcp, client, document,
            SchematicJson.Parser.Parse<SchematicSymbol>(definitionJson), sourceDisplay, evidence, instanceId, "source", token);
        // Backward-compatible creation without an envelope uses actual native
        // LIB_SYMBOL defaults (visible names/numbers and the 20 mil offset).
        var defaultDefinition = SchematicJson.Parser.Parse<SchematicSymbol>(definitionJson);
        defaultDefinition.Id.EntryName = "PinDisplayDefaultsProbe";
        var defaultWrite = await mcp.Tool("kicad_symbol_library_write", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname,
            definitionJson = SchematicJson.Formatter.Format(defaultDefinition), operationId = Guid.NewGuid().ToString("D"), overwrite = false
        });
        RequireToolSuccess(defaultWrite);
        Assert.AreEqual(new NativeCapabilitySymbolLibraryPinDisplay
        { ShowPinNames = true, ShowPinNumbers = true, PinNameOffsetNm = 508_000 },
            SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryPinDisplay>(
                defaultWrite.GetProperty("structuredContent").GetProperty("pinDisplay").GetRawText()));
        RequireToolSuccess(await mcp.Tool("kicad_symbol_library_delete", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = defaultDefinition.Id.EntryName,
            operationId = Guid.NewGuid().ToString("D") }));
        const string displayCloneName = "PinDisplayCloneProbe";
        string displayCloneOperation = Guid.NewGuid().ToString("D");
        var displayCloneArguments = new
        {
            instanceId, expectedProcessEpoch = client.Epoch, definitionJson,
            targetLibraryNickname = libraryNickname, targetSymbolName = displayCloneName,
            operationId = displayCloneOperation, pinDisplayJson = SchematicJson.Formatter.Format(sourceDisplay)
        };
        var displayClone = await mcp.Tool("kicad_symbol_library_clone", displayCloneArguments);
        RequireToolSuccess(displayClone);
        Assert.IsTrue(displayClone.GetProperty("structuredContent").GetProperty("persistenceVerified").GetBoolean());
        Assert.IsTrue(displayClone.GetProperty("structuredContent").GetProperty("pinDisplayInputVerified").GetBoolean());
        var displayCloneReplay = await mcp.Tool("kicad_symbol_library_clone", displayCloneArguments);
        RequireToolSuccess(displayCloneReplay);
        Assert.AreEqual(displayClone.GetProperty("structuredContent").GetRawText(), displayCloneReplay.GetProperty("structuredContent").GetRawText());

        var displayCloneRead = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = displayCloneName });
        RequireToolSuccess(displayCloneRead);
        var cloneDisplay = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryPinDisplay>(
            displayCloneRead.GetProperty("structuredContent").GetProperty("pinDisplay").GetRawText());
        Assert.AreEqual(sourceDisplay, cloneDisplay, "Compare the captured source display with the saved clone, not a response with itself.");
        var displayCloneDefinition = SchematicJson.Parser.Parse<SchematicSymbol>(
            displayCloneRead.GetProperty("structuredContent").GetProperty("definition").GetRawText());
        var clonePreview = await RenderLibraryPinDisplay(mcp, client, document,
            displayCloneDefinition, cloneDisplay, evidence, instanceId, "clone", token);
        CollectionAssert.AreEqual(sourcePreview, clonePreview, "The cloned library pin display must render like its source.");

        // A legacy body-only update must preserve the current library display.
        // Changing body metadata ensures this exercises a real edit.
        displayCloneDefinition.Keywords = "native pin display preservation";
        var omittedDisplayWrite = await mcp.Tool("kicad_symbol_library_write", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname,
            definitionJson = SchematicJson.Formatter.Format(displayCloneDefinition), operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(omittedDisplayWrite);
        Assert.AreEqual(sourceDisplay, SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryPinDisplay>(
            omittedDisplayWrite.GetProperty("structuredContent").GetProperty("pinDisplay").GetRawText()));
        var editedDisplay = new NativeCapabilitySymbolLibraryPinDisplay
        { ShowPinNames = true, ShowPinNumbers = false, PinNameOffsetNm = -1_270_000 };
        string displayEditOperation = Guid.NewGuid().ToString("D");
        var displayEditArguments = new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname,
            definitionJson = SchematicJson.Formatter.Format(displayCloneDefinition), operationId = displayEditOperation,
            pinDisplayJson = SchematicJson.Formatter.Format(editedDisplay)
        };
        var displayEdit = await mcp.Tool("kicad_symbol_library_write", displayEditArguments);
        RequireToolSuccess(displayEdit);
        Assert.IsTrue(displayEdit.GetProperty("structuredContent").GetProperty("pinDisplayInputVerified").GetBoolean());
        var displayEditReplay = await mcp.Tool("kicad_symbol_library_write", displayEditArguments);
        RequireToolSuccess(displayEditReplay);
        Assert.AreEqual(displayEdit.GetProperty("structuredContent").GetRawText(), displayEditReplay.GetProperty("structuredContent").GetRawText());
        var displayEditReceipt = await mcp.Tool("kicad_symbol_library_receipt", new
        { instanceId, expectedProcessEpoch = client.Epoch, operationId = displayEditOperation });
        RequireToolSuccess(displayEditReceipt);
        var retainedDisplayWrite = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryWriteSymbolResponse>(
            displayEditReceipt.GetProperty("structuredContent").GetProperty("writeResponse").GetRawText());
        Assert.AreEqual(editedDisplay, retainedDisplayWrite.PinDisplay);

        // The native owner enforces completeness and grid validity even when a
        // caller bypasses managed admission. Rejected writes preserve content.
        foreach (var invalidDisplay in new[]
        {
            new NativeCapabilitySymbolLibraryPinDisplay { ShowPinNames = false },
            new NativeCapabilitySymbolLibraryPinDisplay { ShowPinNames = false, ShowPinNumbers = false, PinNameOffsetNm = 1 }
        })
        {
            try
            {
                await client.InvokeAsync<NativeCapabilitySymbolLibraryWriteSymbol, NativeCapabilitySymbolLibraryWriteSymbolResponse>(
                    new() { LibraryNickname = libraryNickname, Definition = displayCloneDefinition.Clone(), Overwrite = true,
                        OperationId = Guid.NewGuid().ToString("D"), PinDisplay = invalidDisplay }, token);
                Assert.Fail("The native owner accepted an incomplete or unrepresentable display.");
            }
            catch (NativeApiException) { }
        }
        var editedDisplayRead = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = displayCloneName });
        RequireToolSuccess(editedDisplayRead);
        Assert.AreEqual(editedDisplay, SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryPinDisplay>(
            editedDisplayRead.GetProperty("structuredContent").GetProperty("pinDisplay").GetRawText()));
        var editedPreview = await RenderLibraryPinDisplay(mcp, client, document,
            displayCloneDefinition, editedDisplay, evidence, instanceId, "edited", token);
        Assert.IsFalse(sourcePreview.SequenceEqual(editedPreview), "Changing pin-name/number visibility must change the native drawing.");

        initialModifyHash = editedDisplayRead.GetProperty("structuredContent").GetProperty("modifyHash").GetUInt64()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
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
        string embeddedDataHash = AssertRasterSymbolEmbeddedSource(projectedArtwork, rasterSha, rasterBytes.Length);
        string projectedCanonical = SchematicJson.Formatter.Format(projectedArtwork);

        // Symbol-library writes are project-scoped, so close and reopen the
        // clean schematic through the native lifecycle before reading the
        // library again. This forces the adapter to reload the persisted
        // .kicad_sym file while keeping the test's disposable project intact.
        DocumentLifecycleState projectedState = await ObserveLifecycleState(client, document, token);
        var saved = await mcp.Tool("kicad_document_save", new
        {
            instanceId,
            expectedStateJson = SchematicJson.Formatter.Format(projectedState),
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(saved);
        DocumentLifecycleState beforeClose = SchematicJson.Parser.Parse<DocumentLifecycleState>(
            saved.GetProperty("structuredContent").GetProperty("observedState").GetRawText());
        Assert.IsFalse(beforeClose.NativeContentDirty, saved.GetRawText());
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
        AssertRasterSymbolEmbeddedSource(reloadedArtwork, rasterSha, rasterBytes.Length, embeddedDataHash);
        var displayCloneReloaded = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = displayCloneName });
        RequireToolSuccess(displayCloneReloaded);
        var reloadedDisplay = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryPinDisplay>(
            displayCloneReloaded.GetProperty("structuredContent").GetProperty("pinDisplay").GetRawText());
        Assert.AreEqual(editedDisplay, reloadedDisplay, "Native reopen must retain false visibility and negative spacing exactly.");
        var reloadedPreview = await RenderLibraryPinDisplay(mcp, client, document,
            SchematicJson.Parser.Parse<SchematicSymbol>(displayCloneReloaded.GetProperty("structuredContent").GetProperty("definition").GetRawText()),
            reloadedDisplay, evidence, instanceId, "reloaded", token);
        CollectionAssert.AreEqual(editedPreview, reloadedPreview, "Reopening must retain the actual native pin drawing.");
        var unchangedSource = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName });
        RequireToolSuccess(unchangedSource);
        Assert.AreEqual(sourceDisplay, SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryPinDisplay>(
            unchangedSource.GetProperty("structuredContent").GetProperty("pinDisplay").GetRawText()));
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-symbol-pin-display.json"), JsonSerializer.Serialize(new
        {
            sourceRead = read, cloneRead = displayCloneRead, editRead = editedDisplayRead, reopenedRead = displayCloneReloaded,
            cloneOperation = displayCloneOperation, editOperation = displayEditOperation,
            cloneReplayVerified = true, editReplayVerified = true, omittedUpdatePreserved = true,
            sourcePreviewSha256 = Convert.ToHexStringLower(SHA256.HashData(sourcePreview)),
            clonePreviewSha256 = Convert.ToHexStringLower(SHA256.HashData(clonePreview)),
            editedPreviewSha256 = Convert.ToHexStringLower(SHA256.HashData(editedPreview)),
            reopenedPreviewSha256 = Convert.ToHexStringLower(SHA256.HashData(reloadedPreview))
        }), token);
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
        var displayCloneDelete = await mcp.Tool("kicad_symbol_library_delete", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = displayCloneName,
            operationId = Guid.NewGuid().ToString("D") });
        RequireToolSuccess(displayCloneDelete);
    }

    private static async Task<byte[]> RenderLibraryPinDisplay(StdioMcpFixture mcp, NativeClient client,
        DocumentSpecifier document, SchematicSymbol definition, NativeCapabilitySymbolLibraryPinDisplay display,
        string evidence, string instanceId, string phase, CancellationToken token)
    {
        // Use the existing checked placement and native offscreen renderer.
        // The library definition remains library-owned; temporary placed pins
        // receive distinct identities and explicit links to their source pins.
        var checkedReply = await mcp.Tool("kicad_schematic_checked_state", new
        { instanceId, documentJson = SchematicJson.Formatter.Format(document) });
        RequireToolSuccess(checkedReply);
        var before = SchematicJson.Parser.Parse<CheckedSchematicState>(checkedReply.GetProperty("structuredContent").GetRawText());
        var template = before.Electrical.Hierarchy.Data.Instances.Single(screen => Equals(screen.Metadata.Document, document))
            .Items.Where(item => item.Is(SchematicSymbolInstance.Descriptor)).Select(item => item.Unpack<SchematicSymbolInstance>()).First();
        var sample = template.Clone();
        sample.Id = new() { Value = Guid.NewGuid().ToString("D") };
        sample.Position = new() { XNm = 100_000_000, YNm = 80_000_000 };
        sample.Transform = new() { Orientation = SchematicSymbolOrientation.Sso0 };
        sample.Unit = new() { Unit = 1 }; sample.BodyStyle = new() { Style = 1 };
        sample.Definition = definition.Clone(); sample.LibraryId = definition.Id.Clone(); sample.LibName = "";
        sample.SeparatePinIdentities = true; sample.Definition.PinsUseLocalCoordinates = true;
        sample.ShowPinNames = display.ShowPinNames; sample.ShowPinNumbers = display.ShowPinNumbers;
        sample.PinNameOffset = new() { ValueNm = display.PinNameOffsetNm };
        sample.DefinitionPinNameOffset = sample.PinNameOffset.Clone();
        sample.ReferenceField = definition.ReferenceField.Clone(); sample.ValueField = definition.ValueField.Clone();
        sample.FootprintField = definition.FootprintField.Clone(); sample.DatasheetField = definition.DatasheetField.Clone();
        sample.DescriptionField = definition.DescriptionField.Clone(); sample.UserFields.Clear();
        sample.ReferenceField.Text.Text_ = "PINVIEW"; sample.ValueField.Text.Text_ = "Pin display";
        foreach (var field in new[] { sample.ReferenceField, sample.ValueField, sample.FootprintField, sample.DatasheetField, sample.DescriptionField })
        {
            field.Text.Position.XNm += sample.Position.XNm;
            field.Text.Position.YNm += sample.Position.YNm;
        }
        for (int index = 0; index < sample.Definition.Items.Count; index++)
        {
            var child = sample.Definition.Items[index];
            if (!child.Item.Is(SchematicPin.Descriptor)) continue;
            var pin = child.Item.Unpack<SchematicPin>();
            pin.LibraryPinId = pin.Id.Clone(); pin.Id = new() { Value = Guid.NewGuid().ToString("D") };
            child.Item = Any.Pack(pin);
        }
        sample.Path = document.SheetPath.Clone();
        sample.InstanceRecords.Records.Clear();
        var instance = template.InstanceRecords.Records.First().Clone();
        instance.Path.Clear(); instance.Path.Add(document.SheetPath.Path.Select(id => id.Clone()));
        instance.Reference = "PINVIEW"; instance.Unit = 1;
        sample.InstanceRecords.Records.Add(instance);
        var create = await mcp.Tool("kicad_schematic_create_symbol_instance", new
        {
            instanceId, documentJson = SchematicJson.Formatter.Format(document), expectedStateJson = SchematicJson.Formatter.Format(before),
            symbolJson = SchematicJson.Formatter.Format(sample), operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(create);
        var receipt = SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(create.GetProperty("structuredContent").GetProperty("receipt").GetRawText());
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status);
        var placed = receipt.Result.Items.Single().Unpack<SchematicSymbolInstance>();
        Assert.AreEqual(display.ShowPinNames, placed.ShowPinNames);
        Assert.AreEqual(display.ShowPinNumbers, placed.ShowPinNumbers);
        Assert.AreEqual(display.PinNameOffsetNm, placed.PinNameOffset.ValueNm);

        var renderRequest = new RenderSchematicViews { Document = document };
        renderRequest.Views.Add(new SchematicRenderView
        {
            Key = "library-pin-display", WidthPixels = 800, HeightPixels = 800,
            Region = new() { Position = new() { XNm = 90_000_000, YNm = 70_000_000 }, Size = new() { XNm = 20_000_000, YNm = 20_000_000 } }
        });
        var render = await mcp.Tool("kicad_schematic_render_views", new
        { instanceId, requestJson = SchematicJson.Formatter.Format(renderRequest) });
        RequireToolSuccess(render);
        var rendered = SchematicJson.Parser.Parse<SchematicViewSet>(render.GetProperty("structuredContent").GetProperty("viewSet").GetRawText());
        var renderedSample = rendered.Snapshot.Data.Items.Where(item => item.Is(SchematicSymbolInstance.Descriptor))
            .Select(item => item.Unpack<SchematicSymbolInstance>()).Single(symbol => symbol.Id.Equals(sample.Id));
        Assert.AreEqual(display.ShowPinNames, renderedSample.ShowPinNames);
        Assert.AreEqual(display.ShowPinNumbers, renderedSample.ShowPinNumbers);
        Assert.AreEqual(display.PinNameOffsetNm, renderedSample.PinNameOffset.ValueNm);
        byte[] png = Convert.FromBase64String(render.GetProperty("content").EnumerateArray()
            .Single(block => block.GetProperty("type").GetString() == "image").GetProperty("data").GetString()!);
        Assert.IsGreaterThan(100, png.Length);
        Assert.AreEqual(800U, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)));
        Assert.AreEqual(800U, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-symbol-pin-display-" + phase + ".png"), png, token);
        var afterReply = await mcp.Tool("kicad_schematic_checked_state", new
        { instanceId, documentJson = SchematicJson.Formatter.Format(document) });
        RequireToolSuccess(afterReply);
        var delete = await mcp.Tool("kicad_schematic_delete_symbol_instance", new
        {
            instanceId, documentJson = SchematicJson.Formatter.Format(document), itemId = sample.Id.Value,
            expectedStateJson = afterReply.GetProperty("structuredContent").GetRawText(), operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(delete);
        return png;
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
            (long X, long Y)[] expectedPoints = pieces[index].GetProperty("points").EnumerateArray()
                .Select(point => (Long(point.GetProperty("xNm")), Long(point.GetProperty("yNm"))))
                .ToArray();
            List<(long X, long Y)> actualPointList = polygon.Outline.Nodes
                .Select(point => (point.Point.XNm, point.Point.YNm)).ToList();
            if (actualPointList.Count > 1 && actualPointList[0] == actualPointList[^1])
                actualPointList.RemoveAt(actualPointList.Count - 1);
            Assert.AreEqual(expectedPoints.Length, actualPointList.Count, id);
            for (int pointIndex = 0; pointIndex < expectedPoints.Length; pointIndex++)
            {
                (long expectedX, long expectedY) = expectedPoints[pointIndex];
                (long actualX, long actualY) = actualPointList[pointIndex];
                Assert.IsTrue(Math.Abs(expectedX - actualX) <= 100 && Math.Abs(expectedY - actualY) <= 100,
                    $"{id} point {pointIndex}: native symbol serialization may normalize each coordinate by at most 100 nm; expected {expectedX}:{expectedY}, actual {actualX}:{actualY}.");
            }
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

    private static string AssertRasterSymbolEmbeddedSource(SchematicSymbol symbol, string sourceSha256, int sourceBytes,
        string? expectedDataHash = null)
    {
        Assert.IsNotNull(symbol.EmbeddedFiles, "the projected symbol must carry embedded source files");
        EmbeddedFile source = symbol.EmbeddedFiles!.Files.Single(file => file.Name == "raster_trace_" + sourceSha256 + ".png");
        Assert.IsFalse(string.IsNullOrWhiteSpace(source.DataHash), "the projected symbol must retain a native embedded-file hash");
        if (expectedDataHash is not null)
            Assert.AreEqual(expectedDataHash, source.DataHash, "the embedded-file hash must survive native close and reopen");
        Assert.IsFalse(source.Data.IsEmpty, "the projected symbol must retain the compressed source payload");
        Assert.IsGreaterThan(0, sourceBytes);
        return source.DataHash;
    }

    private static string Property(SchematicGraphicShape shape, string key) => shape.CustomProperties
        .Single(property => property.Key == key).Value;

    private static string Property(SchematicField field, string key) => field.CustomProperties
        .Single(property => property.Key == key).Value;

    private static long Long(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
        : value.GetInt64();
}
