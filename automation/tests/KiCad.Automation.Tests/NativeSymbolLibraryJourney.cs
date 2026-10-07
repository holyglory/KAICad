using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;
using System.Text;
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

        var symbolDocument = document.Clone();
        symbolDocument.Type = DocumentType.DoctypeSymbol;
        symbolDocument.LibId = new() { LibraryNickname = libraryNickname, EntryName = symbolName };
        var lifecycleSnapshot = await mcp.Tool("kicad_library_snapshot_read", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            documentJson = SchematicJson.Formatter.Format(symbolDocument), libraryNickname, entryName = symbolName,
            forcedRefresh = true
        });
        RequireToolSuccess(lifecycleSnapshot);
        var lifecycleData = lifecycleSnapshot.GetProperty("structuredContent");
        Assert.AreEqual("complete", lifecycleData.GetProperty("status").GetString());
        Assert.AreEqual(instanceId, lifecycleData.GetProperty("instanceId").GetString());
        Assert.AreEqual(client.Epoch, lifecycleData.GetProperty("processEpoch").GetString());
        Assert.AreEqual(libraryNickname, lifecycleData.GetProperty("nickname").GetString());
        Assert.AreEqual(symbolName, lifecycleData.GetProperty("document").GetProperty("libId").GetProperty("entryName").GetString());
        Assert.IsTrue(lifecycleData.GetProperty("forcedRefresh").GetBoolean());
        Assert.IsTrue(lifecycleData.GetProperty("providerObservable").GetBoolean());
        Assert.IsTrue(lifecycleData.GetProperty("contentSnapshotComplete").GetBoolean());
        Assert.IsTrue(lifecycleData.GetProperty("diskBaselineChecked").GetBoolean());
        Assert.IsFalse(lifecycleData.GetProperty("completeChangeTracking").GetBoolean());
        Assert.IsFalse(lifecycleData.GetProperty("postverify").GetBoolean());
        Assert.IsTrue(lifecycleData.GetProperty("detachedDefinitionBytes").GetUInt64() > 0);
        Assert.AreEqual(64, lifecycleData.GetProperty("detachedDefinitionSha256").GetString()!.Length);
        Assert.HasCount(2, lifecycleData.GetProperty("dependencyBaselines").EnumerateArray());
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-symbol-library-snapshot.json"), lifecycleSnapshot.GetRawText(), token);

        string definitionJson = definition.GetRawText();
        var sourceDisplay = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryPinDisplay>(
            read.GetProperty("structuredContent").GetProperty("pinDisplay").GetRawText());
        Assert.IsTrue(sourceDisplay.HasShowPinNames && sourceDisplay.HasShowPinNumbers && sourceDisplay.HasPinNameOffsetNm);
        Assert.IsFalse(sourceDisplay.ShowPinNames, "The native fixture explicitly hides pin names.");
        Assert.IsTrue(sourceDisplay.ShowPinNumbers);
        Assert.AreEqual(0L, sourceDisplay.PinNameOffsetNm, "Zero spacing is an explicit native fixture value.");
        var pinDisplayRegion = new Box2
        {
            Position = new() { XNm = 230_000_000, YNm = 120_000_000 },
            Size = new() { XNm = 20_000_000, YNm = 20_000_000 }
        };
        var sourcePreview = await RenderLibraryPinDisplay(mcp, client, document,
            SchematicJson.Parser.Parse<SchematicSymbol>(definitionJson), sourceDisplay, pinDisplayRegion, evidence, instanceId, "source", token);

        // The checked direct-file lane must admit a complete snapshot, retain the exact
        // request, and verify the saved file through the independent native read path.  Keep an
        // unrelated entry in the same file so the post-write proof covers the complete provider,
        // not only the requested symbol.
        const string checkedUnrelatedName = "CheckedUnrelatedProbe";
        var checkedUnrelatedDefinition = SchematicJson.Parser.Parse<SchematicSymbol>(definitionJson);
        checkedUnrelatedDefinition.Id.EntryName = checkedUnrelatedName;
        var checkedUnrelatedWrite = await mcp.Tool("kicad_symbol_library_write", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname,
            definitionJson = SchematicJson.Formatter.Format(checkedUnrelatedDefinition),
            operationId = Guid.NewGuid().ToString("D"), overwrite = false,
            pinDisplayJson = SchematicJson.Formatter.Format(sourceDisplay)
        });
        RequireToolSuccess(checkedUnrelatedWrite);
        var checkedUnrelatedBefore = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = checkedUnrelatedName });
        RequireToolSuccess(checkedUnrelatedBefore);
        string checkedUnrelatedDefinitionBefore = checkedUnrelatedBefore.GetProperty("structuredContent")
            .GetProperty("definition").GetRawText();
        string checkedUnrelatedDisplayBefore = checkedUnrelatedBefore.GetProperty("structuredContent")
            .GetProperty("pinDisplay").GetRawText();

        // A same-size, same-mtime edit must still be found by the forced provider refresh.  The
        // fixture deliberately contains a target pin-name offset token whose replacement
        // preserves both file length and syntax; restore the exact bytes before continuing.
        var beforeExternalSnapshot = await mcp.Tool("kicad_library_snapshot_read", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            documentJson = SchematicJson.Formatter.Format(symbolDocument), libraryNickname, entryName = symbolName,
            forcedRefresh = true
        });
        RequireToolSuccess(beforeExternalSnapshot);
        var beforeExternalData = beforeExternalSnapshot.GetProperty("structuredContent");
        var beforeExternalState = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryCheckedState>(
            beforeExternalData.GetProperty("checkedState").GetRawText());
        var beforeExternalRead = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName });
        RequireToolSuccess(beforeExternalRead);
        string beforeExternalDefinitionSha = beforeExternalData.GetProperty("detachedDefinitionSha256").GetString()!;
        string beforeExternalPinDisplay = beforeExternalRead.GetProperty("structuredContent")
            .GetProperty("pinDisplay").GetRawText();
        string expandedLibraryPath = lifecycleData.GetProperty("expandedUri").GetString()!;
        byte[] originalLibraryBytes = await File.ReadAllBytesAsync(expandedLibraryPath, token);
        DateTime originalLibraryMtime = File.GetLastWriteTimeUtc(expandedLibraryPath);
        string originalLibraryText = Encoding.UTF8.GetString(originalLibraryBytes);
        int targetSymbolIndex = originalLibraryText.IndexOf("(symbol \"" + symbolName + "\"", StringComparison.Ordinal);
        Assert.IsTrue(targetSymbolIndex >= 0, "The fixture must contain the checked target symbol.");
        const string externalMarker = "(offset 0)";
        int externalMarkerIndex = originalLibraryText.IndexOf(externalMarker, targetSymbolIndex, StringComparison.Ordinal);
        Assert.IsTrue(externalMarkerIndex > targetSymbolIndex, "The checked target must contain a stable pin-name offset token.");
        string externallyEditedText = originalLibraryText[..externalMarkerIndex] + "(offset 1)"
            + originalLibraryText[(externalMarkerIndex + externalMarker.Length)..];
        byte[] externallyEditedBytes = Encoding.UTF8.GetBytes(externallyEditedText);
        Assert.AreEqual(originalLibraryBytes.Length, externallyEditedBytes.Length);
        try
        {
            await File.WriteAllBytesAsync(expandedLibraryPath, externallyEditedBytes, token);
            File.SetLastWriteTimeUtc(expandedLibraryPath, originalLibraryMtime);
            Assert.AreEqual(originalLibraryMtime, File.GetLastWriteTimeUtc(expandedLibraryPath));
            var externalSnapshot = await mcp.Tool("kicad_library_snapshot_read", new
            {
                instanceId, expectedProcessEpoch = client.Epoch,
                documentJson = SchematicJson.Formatter.Format(symbolDocument), libraryNickname, entryName = symbolName,
                forcedRefresh = true
            });
            RequireToolSuccess(externalSnapshot);
            var externalSnapshotData = externalSnapshot.GetProperty("structuredContent");
            var externalState = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryCheckedState>(
                externalSnapshotData.GetProperty("checkedState").GetRawText());
            Assert.AreNotEqual(beforeExternalState.StateSha256, externalState.StateSha256,
                "A same-size, same-mtime external edit must advance the forced snapshot digest.");
            Assert.AreNotEqual(beforeExternalDefinitionSha, externalSnapshotData.GetProperty("detachedDefinitionSha256").GetString(),
                "The forced snapshot must refresh the target definition after an external edit.");
            Assert.IsTrue(externalState.EntryExists);
            var externalRead = await mcp.Tool("kicad_symbol_library_read", new
            { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName });
            RequireToolSuccess(externalRead);
            Assert.AreNotEqual(beforeExternalPinDisplay, externalRead.GetProperty("structuredContent")
                .GetProperty("pinDisplay").GetRawText(),
                "The independent native read must expose the externally changed target pin display.");
        }
        finally
        {
            await File.WriteAllBytesAsync(expandedLibraryPath, originalLibraryBytes, CancellationToken.None);
            File.SetLastWriteTimeUtc(expandedLibraryPath, originalLibraryMtime);
        }
        Assert.AreEqual(originalLibraryMtime, File.GetLastWriteTimeUtc(expandedLibraryPath));

        // Refresh after restoring the external bytes; this is the current complete state from
        // which the missing-entry transaction is admitted.
        var restoredSnapshot = await mcp.Tool("kicad_library_snapshot_read", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            documentJson = SchematicJson.Formatter.Format(symbolDocument), libraryNickname, entryName = symbolName,
            forcedRefresh = true
        });
        RequireToolSuccess(restoredSnapshot);
        var restoredState = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryCheckedState>(
            restoredSnapshot.GetProperty("structuredContent").GetProperty("checkedState").GetRawText());

        const string checkedCreateName = "CheckedCreateProbe";
        var checkedDocument = symbolDocument.Clone();
        checkedDocument.LibId.EntryName = checkedCreateName;
        var checkedDefinition = SchematicJson.Parser.Parse<SchematicSymbol>(definitionJson);
        checkedDefinition.Id.EntryName = checkedCreateName;
        var missingSnapshot = await mcp.Tool("kicad_library_snapshot_read", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            documentJson = SchematicJson.Formatter.Format(checkedDocument), libraryNickname,
            entryName = checkedCreateName, forcedRefresh = true, allowMissingEntry = true
        });
        RequireToolSuccess(missingSnapshot);
        var missingState = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryCheckedState>(
            missingSnapshot.GetProperty("structuredContent").GetProperty("checkedState").GetRawText());
        Assert.IsFalse(missingState.EntryExists);
        Assert.IsTrue(missingState.StateComplete && missingState.DiskBaselineChecked);
        Assert.IsFalse(missingState.CompleteChangeTracking);
        Assert.AreEqual(restoredState.DependencyBaselines.Count, missingState.DependencyBaselines.Count);

        string checkedOperationId = Guid.NewGuid().ToString("D");
        var checkedRequest = new NativeCapabilitySymbolLibraryCheckedWrite
        {
            Document = checkedDocument.Clone(), ProcessEpoch = client.Epoch, OperationId = checkedOperationId,
            ExpectedSnapshotSha256 = missingState.StateSha256, Definition = checkedDefinition.Clone(),
            Overwrite = false, PinDisplay = sourceDisplay.Clone(), ExpectedState = missingState.Clone()
        };
        string checkedRequestJson = SchematicJson.Formatter.Format(checkedRequest);
        var checkedCreate = await mcp.Tool("kicad_symbol_library_write_checked", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            documentJson = SchematicJson.Formatter.Format(checkedDocument),
            definitionJson = SchematicJson.Formatter.Format(checkedDefinition), operationId = checkedOperationId,
            expectedSnapshotSha256 = missingState.StateSha256,
            expectedStateJson = SchematicJson.Formatter.Format(missingState), overwrite = false,
            pinDisplayJson = SchematicJson.Formatter.Format(sourceDisplay)
        });
        RequireToolSuccess(checkedCreate);
        var checkedCreateData = checkedCreate.GetProperty("structuredContent");
        Assert.AreEqual("committed_postverified", checkedCreateData.GetProperty("status").GetString());
        Assert.IsTrue(checkedCreateData.GetProperty("committed").GetBoolean());
        Assert.IsTrue(checkedCreateData.GetProperty("revisionTracked").GetBoolean());
        Assert.IsFalse(checkedCreateData.GetProperty("completeLifecycle").GetBoolean(),
            "The direct MCP writer does not claim lifecycle coverage for GUI/import/rescue writers.");
        Assert.IsTrue(checkedCreateData.GetProperty("targetVerified").GetBoolean());
        Assert.IsTrue(checkedCreateData.GetProperty("unrelatedEntriesVerified").GetBoolean());
        Assert.AreEqual(missingState.StateSha256, checkedCreateData.GetProperty("beforeSnapshotSha256").GetString());
        Assert.AreNotEqual(missingState.StateSha256, checkedCreateData.GetProperty("afterSnapshotSha256").GetString());

        var checkedCreateRead = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = checkedCreateName });
        RequireToolSuccess(checkedCreateRead);
        var checkedCreateReadData = checkedCreateRead.GetProperty("structuredContent");
        Assert.AreEqual(libraryNickname, checkedCreateReadData.GetProperty("definition").GetProperty("id")
            .GetProperty("libraryNickname").GetString());
        Assert.AreEqual(checkedCreateName, checkedCreateReadData.GetProperty("definition").GetProperty("id")
            .GetProperty("entryName").GetString());
        Assert.AreEqual(sourceDisplay, SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryPinDisplay>(
            checkedCreateReadData.GetProperty("pinDisplay").GetRawText()));
        var checkedCreatePreview = await RenderLibraryPinDisplay(mcp, client, document,
            SchematicJson.Parser.Parse<SchematicSymbol>(checkedCreateReadData.GetProperty("definition").GetRawText()),
            sourceDisplay, pinDisplayRegion, evidence, instanceId, "checked-create", token);
        CollectionAssert.AreEqual(sourcePreview, checkedCreatePreview);
        var checkedUnrelatedAfter = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = checkedUnrelatedName });
        RequireToolSuccess(checkedUnrelatedAfter);
        Assert.AreEqual(checkedUnrelatedDefinitionBefore, checkedUnrelatedAfter.GetProperty("structuredContent")
            .GetProperty("definition").GetRawText());
        Assert.AreEqual(checkedUnrelatedDisplayBefore, checkedUnrelatedAfter.GetProperty("structuredContent")
            .GetProperty("pinDisplay").GetRawText());

        // The retained receipt is the complete source of post-write evidence and binds the
        // target identity, pin display, exact request and advancing revision.
        var checkedReceipt = await mcp.Tool("kicad_symbol_library_checked_receipt", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            documentJson = SchematicJson.Formatter.Format(checkedDocument), operationId = checkedOperationId,
            expectedRequestJson = checkedRequestJson
        });
        RequireToolSuccess(checkedReceipt);
        var checkedReceiptData = checkedReceipt.GetProperty("structuredContent");
        Assert.IsTrue(checkedReceiptData.GetProperty("found").GetBoolean());
        Assert.IsFalse(checkedReceiptData.GetProperty("retryDispatched").GetBoolean());
        var checkedReceiptEnvelope = checkedReceiptData.GetProperty("receipt");
        Assert.AreEqual(checkedOperationId, checkedReceiptEnvelope.GetProperty("operationId").GetString());
        Assert.AreEqual("mcp_direct_symbol_file", checkedReceiptEnvelope.GetProperty("scope").GetString());
        Assert.AreEqual("committed_postverified", checkedReceiptEnvelope.GetProperty("status").GetString());
        Assert.IsTrue(checkedReceiptEnvelope.GetProperty("expectedRequestVerified").GetBoolean());
        Assert.IsTrue(checkedReceiptEnvelope.GetProperty("postverified").GetBoolean());
        Assert.IsTrue(checkedReceiptEnvelope.GetProperty("targetVerified").GetBoolean());
        Assert.IsTrue(checkedReceiptEnvelope.GetProperty("unrelatedEntriesVerified").GetBoolean());
        Assert.AreEqual(checkedCreateName, checkedReceiptEnvelope.GetProperty("request").GetProperty("document")
            .GetProperty("libId").GetProperty("entryName").GetString());
        Assert.AreEqual(checkedCreateName, checkedReceiptEnvelope.GetProperty("observedAfter").GetProperty("document")
            .GetProperty("libId").GetProperty("entryName").GetString());
        Assert.AreEqual(sourceDisplay, SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryPinDisplay>(
            checkedReceiptEnvelope.GetProperty("request").GetProperty("pinDisplay").GetRawText()));

        // Advance live state through the legacy writer, then prove the original checked request
        // replays without another write and a changed request with the same UUID is rejected.
        var advancedDefinition = checkedDefinition.Clone();
        advancedDefinition.Keywords = "live state advanced";
        var advancedWrite = await mcp.Tool("kicad_symbol_library_write", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname,
            definitionJson = SchematicJson.Formatter.Format(advancedDefinition),
            operationId = Guid.NewGuid().ToString("D"), overwrite = true,
            pinDisplayJson = SchematicJson.Formatter.Format(sourceDisplay)
        });
        RequireToolSuccess(advancedWrite);
        var checkedStaleWrite = await mcp.Tool("kicad_symbol_library_write_checked", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            documentJson = SchematicJson.Formatter.Format(checkedDocument),
            definitionJson = SchematicJson.Formatter.Format(checkedDefinition),
            operationId = Guid.NewGuid().ToString("D"), expectedSnapshotSha256 = missingState.StateSha256,
            expectedStateJson = SchematicJson.Formatter.Format(missingState), overwrite = true,
            pinDisplayJson = SchematicJson.Formatter.Format(sourceDisplay)
        });
        Assert.IsTrue(checkedStaleWrite.TryGetProperty("isError", out var staleWriteError) && staleWriteError.GetBoolean(), checkedStaleWrite.GetRawText());
        Assert.AreEqual("stale_library_state", checkedStaleWrite.GetProperty("structuredContent").GetProperty("errorCode").GetString());

        var checkedReplay = await mcp.Tool("kicad_symbol_library_write_checked", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            documentJson = SchematicJson.Formatter.Format(checkedDocument),
            definitionJson = SchematicJson.Formatter.Format(checkedDefinition), operationId = checkedOperationId,
            expectedSnapshotSha256 = missingState.StateSha256,
            expectedStateJson = SchematicJson.Formatter.Format(missingState), overwrite = false,
            pinDisplayJson = SchematicJson.Formatter.Format(sourceDisplay)
        });
        RequireToolSuccess(checkedReplay);
        Assert.IsTrue(checkedReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean());
        Assert.AreEqual(checkedCreateData.GetProperty("afterSnapshotSha256").GetString(),
            checkedReplay.GetProperty("structuredContent").GetProperty("afterSnapshotSha256").GetString());

        var conflictingRequest = checkedRequest.Clone();
        conflictingRequest.Definition.Keywords = "operation id conflict";
        var conflictResponse = await mcp.Tool("kicad_symbol_library_write_checked", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            documentJson = SchematicJson.Formatter.Format(checkedDocument),
            definitionJson = SchematicJson.Formatter.Format(conflictingRequest.Definition), operationId = checkedOperationId,
            expectedSnapshotSha256 = missingState.StateSha256,
            expectedStateJson = SchematicJson.Formatter.Format(missingState), overwrite = false,
            pinDisplayJson = SchematicJson.Formatter.Format(sourceDisplay)
        });
        Assert.IsTrue(conflictResponse.TryGetProperty("isError", out var conflictError) && conflictError.GetBoolean(), conflictResponse.GetRawText());
        Assert.AreEqual("operation_id_conflict", conflictResponse.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        Assert.IsFalse(conflictResponse.GetProperty("structuredContent").TryGetProperty("retryDispatched", out var retryDispatched)
            && retryDispatched.GetBoolean(), "An operation-ID conflict must never dispatch a retry.");

        var receiptAfterConflict = await mcp.Tool("kicad_symbol_library_checked_receipt", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            documentJson = SchematicJson.Formatter.Format(checkedDocument), operationId = checkedOperationId,
            expectedRequestJson = checkedRequestJson
        });
        RequireToolSuccess(receiptAfterConflict);
        Assert.IsTrue(receiptAfterConflict.GetProperty("structuredContent").GetProperty("found").GetBoolean());
        Assert.AreEqual(checkedOperationId, receiptAfterConflict.GetProperty("structuredContent").GetProperty("receipt")
            .GetProperty("operationId").GetString());

        // A fresh MCP process can retrieve the retained receipt after reconnect; lookup never
        // dispatches another native write.
        string reconnectState = Path.Combine(evidence, instanceId + "-symbol-library-mcp-reconnect");
        await using (var reconnectMcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
            reconnectState, Path.Combine(evidence, instanceId + "-symbol-library-mcp-reconnect.stderr.log"), token))
        {
            RequireToolSuccess(await reconnectMcp.Tool("kicad_instance_attach", new
                { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            var reconnectReceipt = await reconnectMcp.Tool("kicad_symbol_library_checked_receipt", new
            {
                instanceId, expectedProcessEpoch = client.Epoch,
                documentJson = SchematicJson.Formatter.Format(checkedDocument), operationId = checkedOperationId,
                expectedRequestJson = checkedRequestJson
            });
            RequireToolSuccess(reconnectReceipt);
            Assert.IsTrue(reconnectReceipt.GetProperty("structuredContent").GetProperty("found").GetBoolean());
            Assert.IsFalse(reconnectReceipt.GetProperty("structuredContent").GetProperty("retryDispatched").GetBoolean());
            Assert.AreEqual(checkedOperationId, reconnectReceipt.GetProperty("structuredContent").GetProperty("receipt")
                .GetProperty("operationId").GetString());
        }

        RequireToolSuccess(await mcp.Tool("kicad_symbol_library_delete", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = checkedCreateName,
            operationId = Guid.NewGuid().ToString("D") }));
        RequireToolSuccess(await mcp.Tool("kicad_symbol_library_delete", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = checkedUnrelatedName,
            operationId = Guid.NewGuid().ToString("D") }));

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
            displayCloneDefinition, cloneDisplay, pinDisplayRegion, evidence, instanceId, "clone", token);
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
            displayCloneDefinition, editedDisplay, pinDisplayRegion, evidence, instanceId, "edited", token);
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
            reloadedDisplay, pinDisplayRegion, evidence, instanceId, "reloaded", token);
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
            sourcePixelsSha256 = Convert.ToHexStringLower(SHA256.HashData(sourcePreview)),
            clonePixelsSha256 = Convert.ToHexStringLower(SHA256.HashData(clonePreview)),
            editedPixelsSha256 = Convert.ToHexStringLower(SHA256.HashData(editedPreview)),
            reopenedPixelsSha256 = Convert.ToHexStringLower(SHA256.HashData(reloadedPreview))
        }), token);
        // Number labels can repeat on distinct owned pin objects, including
        // same-unit/style locations and alternate bodies. Exercise the actual
        // write and clone tools, then require every occurrence to survive a
        // native library reopen with its UUID, applicability and functions.
        var multiplicityDefinition = SymbolMultiplicityDefinition(SchematicJson.Parser.Parse<SchematicSymbol>(definitionJson));
        multiplicityDefinition.Id.EntryName = "PinMultiplicityProbe";
        string multiplicityJson = SchematicJson.Formatter.Format(multiplicityDefinition);
        var multiplicityValidation = await mcp.Tool("kicad_symbol_library_validate", new
        { libraryNickname, definitionJson = multiplicityJson });
        RequireToolSuccess(multiplicityValidation);
        Assert.IsTrue(multiplicityValidation.GetProperty("structuredContent").GetProperty("valid").GetBoolean(), multiplicityValidation.GetRawText());
        var multiplicityWrite = await mcp.Tool("kicad_symbol_library_write", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, definitionJson = multiplicityJson,
            operationId = Guid.NewGuid().ToString("D"), overwrite = false, pinDisplayJson = SchematicJson.Formatter.Format(sourceDisplay)
        });
        RequireToolSuccess(multiplicityWrite);
        Assert.IsTrue(multiplicityWrite.GetProperty("structuredContent").GetProperty("persistenceVerified").GetBoolean());
        var multiplicityRead = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = multiplicityDefinition.Id.EntryName });
        RequireToolSuccess(multiplicityRead);
        var multiplicityNative = SchematicJson.Parser.Parse<SchematicSymbol>(multiplicityRead.GetProperty("structuredContent").GetProperty("definition").GetRawText());
        CollectionAssert.AreEqual(SymbolPinOccurrences(multiplicityDefinition), SymbolPinOccurrences(multiplicityNative));
        Assert.AreEqual(2U, multiplicityNative.UnitCount);
        Assert.IsTrue(multiplicityNative.DemorganBodyStyles);
        var multiplicityClone = await mcp.Tool("kicad_symbol_library_clone", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            definitionJson = multiplicityRead.GetProperty("structuredContent").GetProperty("definition").GetRawText(),
            pinDisplayJson = multiplicityRead.GetProperty("structuredContent").GetProperty("pinDisplay").GetRawText(),
            targetLibraryNickname = libraryNickname, targetSymbolName = "PinMultiplicityCloneProbe",
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(multiplicityClone);
        Assert.IsTrue(multiplicityClone.GetProperty("structuredContent").GetProperty("persistenceVerified").GetBoolean());
        CollectionAssert.AreEqual(SymbolPinOccurrences(multiplicityDefinition), SymbolPinOccurrences(
            SchematicJson.Parser.Parse<SchematicSymbol>(multiplicityClone.GetProperty("structuredContent").GetProperty("definition").GetRawText())));
        var multiplicityAfterClone = await mcp.Tool("kicad_symbol_library_read", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = multiplicityDefinition.Id.EntryName });
        RequireToolSuccess(multiplicityAfterClone);
        foreach (string malformed in new[] { "duplicate-id", "duplicate-alternate", "library-link", "active-alternate" })
        {
            var invalid = multiplicityDefinition.Clone();
            var firstPin = invalid.Items.First(child => child.Item.Is(SchematicPin.Descriptor));
            var pin = firstPin.Item.Unpack<SchematicPin>();
            switch (malformed)
            {
                case "duplicate-id": invalid.Items.Add(firstPin.Clone()); break;
                case "duplicate-alternate":
                    pin.Alternates.Add(new SchematicPinAlternate { Name = "MODE", Shape = SchematicPinShape.SpsLine,
                        ElectricalType = Kiapi.Common.Types.ElectricalPinType.EptInput });
                    pin.Alternates.Add(pin.Alternates[0].Clone()); firstPin.Item = Any.Pack(pin); break;
                case "library-link": pin.LibraryPinId = new() { Value = Guid.NewGuid().ToString("D") }; firstPin.Item = Any.Pack(pin); break;
                case "active-alternate": pin.ActiveAlternate = "MODE"; firstPin.Item = Any.Pack(pin); break;
            }
            var refused = await mcp.Tool("kicad_symbol_library_write", new
            {
                instanceId, expectedProcessEpoch = client.Epoch, libraryNickname,
                definitionJson = SchematicJson.Formatter.Format(invalid), operationId = Guid.NewGuid().ToString("D"), overwrite = true
            });
            Assert.IsTrue(refused.TryGetProperty("isError", out var refusedError) && refusedError.GetBoolean(), refused.GetRawText());
            Assert.AreEqual("invalid_symbol_definition", refused.GetProperty("structuredContent").GetProperty("errorCode").GetString());
            var unchangedMultiplicity = await mcp.Tool("kicad_symbol_library_read", new
            { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = multiplicityDefinition.Id.EntryName });
            RequireToolSuccess(unchangedMultiplicity);
            Assert.AreEqual(multiplicityAfterClone.GetProperty("structuredContent").GetRawText(),
                unchangedMultiplicity.GetProperty("structuredContent").GetRawText(), "Rejected raw writes must preserve library content and modify identity.");
        }
        var multiplicityCheckpoint = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, token);
        var multiplicitySave = await mcp.Tool("kicad_document_save", new
        { instanceId, expectedStateJson = SchematicJson.Formatter.Format(multiplicityCheckpoint), operationId = Guid.NewGuid().ToString("D") });
        RequireToolSuccess(multiplicitySave);
        RequireToolSuccess(await mcp.Tool("kicad_document_close", new
        { instanceId, expectedStateJson = multiplicitySave.GetProperty("structuredContent").GetProperty("observedState").GetRawText(),
            operationId = Guid.NewGuid().ToString("D") }));
        var multiplicityReopen = await CreateRootThroughMcp(client.Endpoint, instanceId,
            schematicPath, evidence, token, toolName: "kicad_schematic_open");
        document = multiplicityReopen.Document;
        foreach (string persistedName in new[] { "PinMultiplicityProbe", "PinMultiplicityCloneProbe" })
        {
            var persisted = await mcp.Tool("kicad_symbol_library_read", new
            { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = persistedName });
            RequireToolSuccess(persisted);
            var persistedDefinition = SchematicJson.Parser.Parse<SchematicSymbol>(persisted.GetProperty("structuredContent").GetProperty("definition").GetRawText());
            CollectionAssert.AreEqual(SymbolPinOccurrences(multiplicityDefinition), SymbolPinOccurrences(persistedDefinition));
            Assert.AreEqual(2U, persistedDefinition.UnitCount); Assert.IsTrue(persistedDefinition.DemorganBodyStyles);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-" + persistedName + "-persisted.json"), persisted.GetRawText(), token);
            RequireToolSuccess(await mcp.Tool("kicad_symbol_library_delete", new
            { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = persistedName,
                operationId = Guid.NewGuid().ToString("D") }));
        }
        var artworkBeforeDeleteList = await mcp.Tool("kicad_symbol_library_list", new
        { instanceId, expectedProcessEpoch = client.Epoch, libraryNickname });
        RequireToolSuccess(artworkBeforeDeleteList);
        string artworkDeleteModifyHash = artworkBeforeDeleteList.GetProperty("structuredContent").GetProperty("modifyHash")
            .GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var artworkDelete = await mcp.Tool("kicad_symbol_library_delete", new
        {
            instanceId, expectedProcessEpoch = client.Epoch, libraryNickname, symbolName = "GeneratedArtworkProbe",
            operationId = Guid.NewGuid().ToString("D"), expectedModifyHash = artworkDeleteModifyHash
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

    internal static SchematicSymbol SymbolMultiplicityDefinition(SchematicSymbol template)
    {
        var definition = template.Clone();
        definition.UnitCount = 2; definition.DemorganBodyStyles = true; definition.PinsUseLocalCoordinates = true;
        definition.BodyStyle.Clear();
        definition.BodyStyle.Add(new[] { new SchematicBodyStyle { Name = "Standard" }, new SchematicBodyStyle { Name = "Alternate" } });
        definition.UnitDisplayNames.Clear();
        foreach (var child in definition.Items.Where(child => child.Item.Is(SchematicPin.Descriptor)).ToArray()) definition.Items.Remove(child);
        void Pin(string number, string name, int unit, int style, long x, long y, bool visible = true, bool alternates = false)
        {
            var pin = new SchematicPin
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") }, Number = number, Name = name,
                Position = new() { XNm = x, YNm = y }, Length = new() { ValueNm = 2_540_000 },
                Orientation = SchematicPinOrientation.SpoRight, Shape = SchematicPinShape.SpsLine,
                ElectricalType = Kiapi.Common.Types.ElectricalPinType.EptPassive, Visible = visible,
                NameTextSize = new() { ValueNm = 1_270_000 }, NumberTextSize = new() { ValueNm = 1_270_000 }
            };
            if (alternates)
            {
                pin.Alternates.Add(new SchematicPinAlternate { Name = "MODE", Shape = SchematicPinShape.SpsLine,
                    ElectricalType = Kiapi.Common.Types.ElectricalPinType.EptInput });
                pin.Alternates.Add(new SchematicPinAlternate { Name = "mode", Shape = SchematicPinShape.SpsInverted,
                    ElectricalType = Kiapi.Common.Types.ElectricalPinType.EptOutput });
            }
            definition.Items.Add(new SchematicSymbolChild { Unit = new() { Unit = unit }, BodyStyle = new() { Style = style }, Item = Any.Pack(pin) });
        }
        Pin("1", "COMMON", 0, 0, 0, 0);
        Pin("1", "UNIT2", 2, 1, 1_270_000, 0);
        Pin("2", "A", 1, 1, -2_540_000, 0, alternates: true);
        Pin("2", "B", 1, 1, 2_540_000, 0, alternates: true);
        Pin("2", "ALTERNATE", 1, 2, -2_540_000, 1_270_000, alternates: true);
        Pin("3", "STACK", 1, 0, 0, -2_540_000);
        Pin("4", "STACK", 1, 0, 0, -2_540_000, visible: false);
        Pin("[5-7]", "DATA", 2, 0, -1_270_000, 0);
        return definition;
    }

    private static string[] SymbolPinOccurrences(SchematicSymbol definition) => definition.Items
        .Where(child => child.Item.Is(SchematicPin.Descriptor))
        .Select(child => $"{child.Unit?.Unit ?? 0}:{child.BodyStyle?.Style ?? 0}:{SchematicJson.Formatter.Format(child.Item.Unpack<SchematicPin>())}")
        .Order(StringComparer.Ordinal).ToArray();

    private static async Task<byte[]> RenderLibraryPinDisplay(StdioMcpFixture mcp, NativeClient client,
        DocumentSpecifier document, SchematicSymbol definition, NativeCapabilitySymbolLibraryPinDisplay display,
        Box2 region, string evidence, string instanceId, string phase, CancellationToken token)
    {
        // Use the existing checked placement and native offscreen renderer.
        // The library definition remains library-owned; temporary placed pins
        // receive distinct identities and explicit links to their source pins.
        var checkedReply = await mcp.Tool("kicad_schematic_checked_state", new
        { instanceId, documentJson = SchematicJson.Formatter.Format(document) });
        RequireToolSuccess(checkedReply);
        var before = SchematicJson.Parser.Parse<CheckedSchematicState>(checkedReply.GetProperty("structuredContent").GetRawText());
        var presentationBefore = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(
            new() { Document = document }, token);
        static bool Intersects(Box2 a, Box2 b) => a.Position.XNm <= b.Position.XNm + b.Size.XNm
            && a.Position.XNm + a.Size.XNm >= b.Position.XNm && a.Position.YNm <= b.Position.YNm + b.Size.YNm
            && a.Position.YNm + a.Size.YNm >= b.Position.YNm;
        Assert.IsTrue(region.Position.XNm >= presentationBefore.PageBounds.Position.XNm
            && region.Position.YNm >= presentationBefore.PageBounds.Position.YNm
            && region.Position.XNm + region.Size.XNm <= presentationBefore.PageBounds.Position.XNm + presentationBefore.PageBounds.Size.XNm
            && region.Position.YNm + region.Size.YNm <= presentationBefore.PageBounds.Position.YNm + presentationBefore.PageBounds.Size.YNm);
        Assert.IsFalse(presentationBefore.Objects.Any(item => item.Visible && item.Bounds is not null && Intersects(item.Bounds, region)),
            "The pin-display comparison region must contain no existing visible object or field.");
        Assert.IsFalse(presentationBefore.Wires.Any(wire => Intersects(new Box2
        {
            Position = new() { XNm = Math.Min(wire.Start.XNm, wire.End.XNm), YNm = Math.Min(wire.Start.YNm, wire.End.YNm) },
            Size = new() { XNm = Math.Abs(wire.End.XNm - wire.Start.XNm), YNm = Math.Abs(wire.End.YNm - wire.Start.YNm) }
        }, region)), "The comparison region must contain no wire.");
        Assert.IsFalse(presentationBefore.Junctions.Any(point => point.XNm >= region.Position.XNm
            && point.XNm <= region.Position.XNm + region.Size.XNm && point.YNm >= region.Position.YNm
            && point.YNm <= region.Position.YNm + region.Size.YNm));
        var renderRequest = new RenderSchematicViews { Document = document };
        renderRequest.Views.Add(new SchematicRenderView
        { Key = "library-pin-display", WidthPixels = 800, HeightPixels = 800, Region = region.Clone() });
        var emptyRender = await mcp.Tool("kicad_schematic_render_views", new
        { instanceId, requestJson = SchematicJson.Formatter.Format(renderRequest) });
        RequireToolSuccess(emptyRender);
        static byte[] RenderPixels(JsonElement reply) => Convert.FromBase64String(reply.GetProperty("content").EnumerateArray()
            .Single(block => block.GetProperty("type").GetString() == "image").GetProperty("data").GetString()!);
        var emptyRaster = NativePresentationRaster.Decode(RenderPixels(emptyRender));
        var background = emptyRaster.Rgba.AsSpan(0, 4).ToArray();
        for (int pixel = 0; pixel < emptyRaster.Width * emptyRaster.Height; pixel++)
            Assert.IsTrue(emptyRaster.Rgba.AsSpan(pixel * 4, 4).SequenceEqual(background),
                "The native render must prove the comparison region is empty before placement.");
        var template = before.Electrical.Hierarchy.Data.Instances.Single(screen => Equals(screen.Metadata.Document, document))
            .Items.Where(item => item.Is(SchematicSymbolInstance.Descriptor)).Select(item => item.Unpack<SchematicSymbolInstance>())
            .OrderBy(symbol => symbol.Id.Value, StringComparer.Ordinal).First();
        string CaptureIdentity(string role) => new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(instanceId + ":pin-display:" + role)).AsSpan(0, 16)).ToString("D");
        var sample = template.Clone();
        sample.Id = new() { Value = CaptureIdentity("symbol") };
        sample.Position = new() { XNm = region.Position.XNm + region.Size.XNm / 2, YNm = region.Position.YNm + region.Size.YNm / 2 };
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
            pin.LibraryPinId = pin.Id.Clone(); pin.Id = new() { Value = CaptureIdentity("pin:" + pin.LibraryPinId.Value) };
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

        var render = await mcp.Tool("kicad_schematic_render_views", new
        { instanceId, requestJson = SchematicJson.Formatter.Format(renderRequest) });
        RequireToolSuccess(render);
        var rendered = SchematicJson.Parser.Parse<SchematicViewSet>(render.GetProperty("structuredContent").GetProperty("viewSet").GetRawText());
        var renderedSample = rendered.Snapshot.Data.Items.Where(item => item.Is(SchematicSymbolInstance.Descriptor))
            .Select(item => item.Unpack<SchematicSymbolInstance>()).Single(symbol => symbol.Id.Equals(sample.Id));
        Assert.AreEqual(display.ShowPinNames, renderedSample.ShowPinNames);
        Assert.AreEqual(display.ShowPinNumbers, renderedSample.ShowPinNumbers);
        Assert.AreEqual(display.PinNameOffsetNm, renderedSample.PinNameOffset.ValueNm);
        byte[] png = RenderPixels(render);
        Assert.IsGreaterThan(100, png.Length);
        Assert.AreEqual(800U, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)));
        Assert.AreEqual(800U, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-symbol-pin-display-" + phase + ".png"), png, token);
        var raster = NativePresentationRaster.Decode(png);
        var presentationAfter = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(
            new() { Document = document }, token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-symbol-pin-display-" + phase + ".json"), JsonSerializer.Serialize(new
        {
            phase, requestJson = SchematicJson.Formatter.Format(renderRequest),
            presentationBeforeJson = SchematicJson.Formatter.Format(presentationBefore),
            presentationAfterJson = SchematicJson.Formatter.Format(presentationAfter),
            viewSet = render.GetProperty("structuredContent").GetProperty("viewSet"),
            emptyViewSet = emptyRender.GetProperty("structuredContent").GetProperty("viewSet"),
            definitionJson = SchematicJson.Formatter.Format(definition), pinDisplayJson = SchematicJson.Formatter.Format(display),
            placedJson = SchematicJson.Formatter.Format(placed), renderedJson = SchematicJson.Formatter.Format(renderedSample),
            pngSha256 = Convert.ToHexStringLower(SHA256.HashData(png)), pixelsSha256 = Convert.ToHexStringLower(SHA256.HashData(raster.Rgba))
        }), token);
        var afterReply = await mcp.Tool("kicad_schematic_checked_state", new
        { instanceId, documentJson = SchematicJson.Formatter.Format(document) });
        RequireToolSuccess(afterReply);
        var delete = await mcp.Tool("kicad_schematic_delete_symbol_instance", new
        {
            instanceId, documentJson = SchematicJson.Formatter.Format(document), itemId = sample.Id.Value,
            expectedStateJson = afterReply.GetProperty("structuredContent").GetRawText(), operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(delete);
        // Compare exact decoded native pixels, independently of PNG encoding.
        // No pixel tolerance is applied; changed label settings must still fail.
        return raster.Rgba;
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
