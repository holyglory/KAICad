using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeFootprintLibraryJourney
{
    private const string CheckedFootprintName = "Checked_Write_Probe";

    // The rich editor fixture deliberately has an incomplete transport projection. Keep
    // this separate supported library definition for the real positive checked-write lane.
    private const string CheckedFootprintFixture = """
        (footprint "Checked_Write_Probe"
          (version 20240108)
          (generator "pcbnew")
          (layer "F.Cu")
          (descr "checked definition before")
          (tags "checked native fixture")
          (property "Reference" "REF**"
            (at 0 -2 0) (layer "F.SilkS")
            (uuid "a6b3822f-de47-4cb1-b981-a7a7b24bcb10")
            (effects (font (size 1 1) (thickness 0.15))))
          (property "Value" "Checked_Write_Probe"
            (at 0 2 0) (layer "F.Fab")
            (uuid "57a27286-a357-44f0-a0bc-03d9c4c45e71")
            (effects (font (size 1 1) (thickness 0.15))))
          (property "Datasheet" "fixture-datasheet"
            (at 0 0 0) (layer "F.Fab") (hide yes)
            (uuid "be598f21-2b10-409f-a5c5-a334f22d7e72")
            (effects (font (size 1 1) (thickness 0.15))))
          (property "Description" "supported native field"
            (at 0 0 0) (layer "F.Fab") (hide yes)
            (uuid "b037b3cb-0fd2-4ae9-abeb-5fcbd5b7152e")
            (effects (font (size 1 1) (thickness 0.15))))
          (attr smd)
          (clearance 0.12)
          (solder_mask_margin 0.01)
          (solder_paste_margin -0.02)
          (solder_paste_margin_ratio 0.1)
          (fp_line (start -1.5 -1) (end 1.5 -1)
            (stroke (width 0.15) (type solid)) (layer "F.SilkS")
            (uuid "73e76190-1fe5-419f-a590-4bc7d07d75eb"))
          (pad "1" smd rect (at -1 0) (size 0.8 1.2)
            (layers "F.Cu" "F.Paste" "F.Mask")
            (uuid "e3d24bcd-86de-49c6-b35e-8da6a6c7de80"))
          (pad "2" smd circle (at 1 0) (size 0.9 0.9)
            (layers "F.Cu" "F.Paste" "F.Mask")
            (uuid "13d6ceda-3a5c-49f1-8cf6-5cdb324887d9"))
        )
        """;

    private static async Task VerifyCheckedFootprintWrite(StdioMcpFixture mcp, string instanceId,
        string epoch, string endpoint, string projectDirectory, string footprintPath,
        string siblingPath, string editorDocumentJson, string scratch, string evidence,
        CancellationToken token)
    {
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeFootprint,
            Project = new()
            {
                Name = "fixture",
                Path = projectDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar
            },
            LibId = new() { LibraryNickname = "Fixture", EntryName = CheckedFootprintName }
        };
        string documentJson = SchematicJson.Formatter.Format(document);
        string tablePath = Path.Combine(projectDirectory, "fp-lib-table");
        string projectPath = Path.Combine(projectDirectory, "fixture.kicad_pro");
        byte[] targetBeforeBytes = await File.ReadAllBytesAsync(footprintPath, token);
        byte[] siblingBeforeBytes = await File.ReadAllBytesAsync(siblingPath, token);
        byte[] tableBeforeBytes = await File.ReadAllBytesAsync(tablePath, token);
        byte[] projectBeforeBytes = await File.ReadAllBytesAsync(projectPath, token);
        DateTime siblingBeforeTime = File.GetLastWriteTimeUtc(siblingPath);
        int toolSequence = 0;

        async Task<JsonElement> Tool(IMcpToolClient client, string name, object arguments)
        {
            JsonElement result = await client.Tool(name, arguments);
            string file = $"checked-footprint-{++toolSequence:D2}-{name}.json";
            await File.WriteAllTextAsync(Path.Combine(evidence, file),
                JsonSerializer.Serialize(new { tool = name, arguments, result }), token);
            return result;
        }

        async Task<JsonElement> ReadDefinition(IMcpToolClient client, string requestedDocument,
            string source)
        {
            JsonElement result = await Tool(client, "kicad_footprint_definition_read", new
            { instanceId, expectedProcessEpoch = epoch, documentJson = requestedDocument, source });
            RequireCheckedFootprintSuccess(result);
            return result.GetProperty("structuredContent");
        }

        async Task<NativeCapabilityFootprintLibraryCheckedState> Snapshot(IMcpToolClient client)
        {
            JsonElement result = await Tool(client, "kicad_library_snapshot_read", new
            {
                instanceId, expectedProcessEpoch = epoch, documentJson,
                libraryNickname = "Fixture", entryName = CheckedFootprintName, forcedRefresh = true
            });
            RequireCheckedFootprintSuccess(result);
            JsonElement data = result.GetProperty("structuredContent");
            Assert.AreEqual("complete", data.GetProperty("status").GetString(), result.GetRawText());
            Assert.IsTrue(data.GetProperty("forcedRefresh").GetBoolean(), result.GetRawText());
            Assert.IsTrue(data.GetProperty("contentSnapshotComplete").GetBoolean(), result.GetRawText());
            Assert.IsTrue(data.GetProperty("diskBaselineChecked").GetBoolean(), result.GetRawText());
            Assert.IsTrue(data.GetProperty("mutationAvailable").GetBoolean(), result.GetRawText());
            var state = SchematicJson.Parser.Parse<NativeCapabilityFootprintLibraryCheckedState>(
                data.GetProperty("checkedState").GetRawText());
            Assert.AreEqual(document, state.Document);
            Assert.AreEqual(epoch, state.ProcessEpoch);
            Assert.AreEqual("mcp_direct_footprint_file", state.Scope);
            Assert.IsTrue(state.EntryExists && state.StateComplete && state.DiskBaselineChecked);
            Assert.IsFalse(state.CompleteChangeTracking);
            Assert.AreEqual(64, state.StateSha256.Length);
            Assert.AreEqual(epoch, state.Revision.Epoch);
            Assert.IsTrue(state.Revision.Sequence > 0);
            await AssertCheckedFootprintBaseline(state, footprintPath, token);
            await AssertCheckedFootprintBaseline(state, siblingPath, token);
            await AssertCheckedFootprintBaseline(state, tablePath, token);
            return state;
        }

        Task<JsonElement> Write(IMcpToolClient client, NativeCapabilityFootprintLibraryCheckedWrite request) =>
            Tool(client, "kicad_footprint_library_write_checked", new
            {
                instanceId, expectedProcessEpoch = epoch, documentJson,
                footprintJson = BoardJson.Formatter.Format(request.Footprint),
                operationId = request.OperationId, expectedSnapshotSha256 = request.ExpectedSnapshotSha256,
                expectedStateJson = SchematicJson.Formatter.Format(request.ExpectedState), overwrite = request.Overwrite
            });

        async Task<NativeCapabilityFootprintLibraryCheckedWriteResponse> Receipt(IMcpToolClient client,
            NativeCapabilityFootprintLibraryCheckedWrite request)
        {
            JsonElement result = await Tool(client, "kicad_footprint_library_checked_receipt", new
            {
                instanceId, expectedProcessEpoch = epoch, documentJson, operationId = request.OperationId,
                expectedRequestJson = SchematicJson.Formatter.Format(request)
            });
            RequireCheckedFootprintSuccess(result);
            JsonElement data = result.GetProperty("structuredContent");
            Assert.IsTrue(data.GetProperty("found").GetBoolean(), result.GetRawText());
            Assert.IsTrue(data.GetProperty("nativeReceiptAvailable").GetBoolean(), result.GetRawText());
            Assert.IsFalse(data.GetProperty("retryDispatched").GetBoolean(), result.GetRawText());
            var receipt = SchematicJson.Parser.Parse<NativeCapabilityFootprintLibraryCheckedWriteResponse>(
                data.GetProperty("receipt").GetRawText());
            Assert.AreEqual(document, receipt.Document);
            Assert.AreEqual(epoch, receipt.ProcessEpoch);
            Assert.AreEqual(request.OperationId, receipt.OperationId);
            Assert.AreEqual(request, receipt.Request, "The native receipt must retain every exact request field.");
            Assert.IsTrue(receipt.ExpectedRequestVerified);
            Assert.AreEqual("mcp_direct_footprint_file", receipt.Scope);
            return receipt;
        }

        JsonElement editorBefore = await ReadDefinition(mcp, editorDocumentJson, "editor");
        var editorFootprintBefore = BoardJson.Parser.Parse<FootprintInstance>(
            editorBefore.GetProperty("footprint").GetRawText());
        JsonElement openBefore = await Tool(mcp, "kicad_footprint_open_documents", new
        { instanceId, expectedProcessEpoch = epoch });
        RequireCheckedFootprintSuccess(openBefore);

        async Task AssertIsolation(IMcpToolClient client)
        {
            CollectionAssert.AreEqual(siblingBeforeBytes, await File.ReadAllBytesAsync(siblingPath, token),
                "A direct checked library write must leave its rich sibling byte-for-byte unchanged.");
            Assert.AreEqual(siblingBeforeTime, File.GetLastWriteTimeUtc(siblingPath));
            CollectionAssert.AreEqual(tableBeforeBytes, await File.ReadAllBytesAsync(tablePath, token));
            CollectionAssert.AreEqual(projectBeforeBytes, await File.ReadAllBytesAsync(projectPath, token));
            JsonElement editorAfter = await ReadDefinition(client, editorDocumentJson, "editor");
            Assert.AreEqual(editorBefore.GetProperty("canonicalSha256").GetString(),
                editorAfter.GetProperty("canonicalSha256").GetString(),
                "The direct writer must not change the footprint currently loaded in the editor.");
            AssertCheckedFootprintContent(editorFootprintBefore,
                BoardJson.Parser.Parse<FootprintInstance>(editorAfter.GetProperty("footprint").GetRawText()));
            JsonElement openAfter = await Tool(client, "kicad_footprint_open_documents", new
            { instanceId, expectedProcessEpoch = epoch });
            RequireCheckedFootprintSuccess(openAfter);
            Assert.AreEqual(openBefore.GetProperty("structuredContent").GetProperty("response")
                    .GetProperty("documents").GetRawText(),
                openAfter.GetProperty("structuredContent").GetProperty("response")
                    .GetProperty("documents").GetRawText());
        }

        try
        {
            JsonElement before = await ReadDefinition(mcp, documentJson, "persisted_library");
            AssertCompleteCheckedFootprintDefinition(before, footprintPath, targetBeforeBytes);
            var footprint = BoardJson.Parser.Parse<FootprintInstance>(before.GetProperty("footprint").GetRawText());
            Assert.AreEqual("checked definition before", footprint.Definition.Attributes.Description);
            Assert.AreEqual("fixture-datasheet", footprint.Definition.DatasheetField.Text.Text.Text_);
            Assert.AreEqual("supported native field", footprint.Definition.DescriptionField.Text.Text.Text_);
            Assert.AreEqual(120_000L, footprint.Definition.Overrides.CopperClearance.ValueNm);
            Assert.AreEqual(10_000L, footprint.Definition.Overrides.SolderMask.SolderMaskMargin.ValueNm);
            Assert.AreEqual(-20_000L, footprint.Definition.Overrides.SolderPaste.SolderPasteMargin.ValueNm);
            Assert.AreEqual(0.1, footprint.Definition.Overrides.SolderPaste.SolderPasteMarginRatio.Value);
            Assert.HasCount(2, footprint.Definition.Items.Where(item => item.Is(Pad.Descriptor)));
            Assert.HasCount(1, footprint.Definition.Items.Where(item => item.Is(BoardGraphicShape.Descriptor)));
            Assert.IsFalse(footprint.Definition.Items.Any(item => item.Is(Footprint3DModel.Descriptor)));

            NativeCapabilityFootprintLibraryCheckedState state = await Snapshot(mcp);
            var candidate = footprint.Clone();
            candidate.Definition.Attributes.Description = "checked definition after";
            candidate.Definition.Attributes.Keywords = "checked writer persisted geometry";
            int padIndex = candidate.Definition.Items.Select((item, index) => (item, index))
                .Single(pair => pair.item.Is(Pad.Descriptor) && pair.item.Unpack<Pad>().Number == "1").index;
            Pad changedPad = candidate.Definition.Items[padIndex].Unpack<Pad>();
            changedPad.PadStack.CopperLayers.Single(layer => layer.Layer == BoardLayer.BlFCu).Size.XNm = 1_100_000;
            candidate.Definition.Items[padIndex] = Any.Pack(changedPad);
            var request = new NativeCapabilityFootprintLibraryCheckedWrite
            {
                Document = document.Clone(), ProcessEpoch = epoch, OperationId = Guid.NewGuid().ToString("D"),
                ExpectedSnapshotSha256 = state.StateSha256, ExpectedState = state.Clone(),
                Footprint = candidate.Clone(), Overwrite = true
            };
            await File.WriteAllTextAsync(Path.Combine(evidence, "checked-footprint-request.json"),
                SchematicJson.Formatter.Format(request), token);
            await File.WriteAllBytesAsync(Path.Combine(evidence, "checked-footprint-before.kicad_mod"), targetBeforeBytes, token);

            // This lane does not capture external model dependencies. Refuse a structurally
            // valid model-bearing candidate before touching either library entry.
            var modelRequest = request.Clone();
            modelRequest.OperationId = Guid.NewGuid().ToString("D");
            modelRequest.Footprint.Definition.Items.Add(Any.Pack(new Footprint3DModel
            {
                Filename = "uncaptured-model.step", Scale = new() { XNm = 1, YNm = 1, ZNm = 1 },
                Rotation = new(), Offset = new(), Visible = true, Opacity = 1
            }));
            JsonElement modelRefusal = await Write(mcp, modelRequest);
            RequireCheckedFootprintRefusal(modelRefusal, "unsupported_footprint_dependency");
            Assert.IsFalse(modelRefusal.GetProperty("structuredContent").GetProperty("nativeWriteStarted").GetBoolean());
            CollectionAssert.AreEqual(targetBeforeBytes, await File.ReadAllBytesAsync(footprintPath, token));
            await AssertIsolation(mcp);

            JsonElement committed = await Write(mcp, request);
            RequireCheckedFootprintSuccess(committed);
            JsonElement committedData = committed.GetProperty("structuredContent");
            Assert.AreEqual("committed_postverified", committedData.GetProperty("status").GetString());
            Assert.IsTrue(committedData.GetProperty("committed").GetBoolean());
            Assert.IsTrue(committedData.GetProperty("persistenceVerified").GetBoolean());
            Assert.IsTrue(committedData.GetProperty("nativeWriteStarted").GetBoolean());
            Assert.IsTrue(committedData.GetProperty("postverified").GetBoolean());
            Assert.IsTrue(committedData.GetProperty("targetVerified").GetBoolean());
            Assert.IsTrue(committedData.GetProperty("unrelatedEntriesVerified").GetBoolean());
            Assert.IsTrue(committedData.GetProperty("revisionTracked").GetBoolean());
            Assert.IsFalse(committedData.GetProperty("completeLifecycle").GetBoolean());
            Assert.IsFalse(committedData.GetProperty("rollbackAttempted").GetBoolean());
            Assert.AreEqual(state.StateSha256, committedData.GetProperty("beforeSnapshotSha256").GetString());

            byte[] committedBytes = await File.ReadAllBytesAsync(footprintPath, token);
            Assert.AreNotEqual(CheckedFootprintSha256(targetBeforeBytes), CheckedFootprintSha256(committedBytes));
            JsonElement persisted = await ReadDefinition(mcp, documentJson, "persisted_library");
            AssertCompleteCheckedFootprintDefinition(persisted, footprintPath, committedBytes);
            Assert.AreEqual(CheckedFootprintSha256(committedBytes), persisted.GetProperty("canonicalSha256").GetString(),
                "The native canonical writer digest must match the actual committed library file.");
            AssertCheckedFootprintContent(candidate,
                BoardJson.Parser.Parse<FootprintInstance>(persisted.GetProperty("footprint").GetRawText()));
            await File.WriteAllBytesAsync(Path.Combine(evidence, "checked-footprint-committed.kicad_mod"), committedBytes, token);

            NativeCapabilityFootprintLibraryCheckedWriteResponse receipt = await Receipt(mcp, request);
            Assert.AreEqual("committed_postverified", receipt.Status);
            Assert.AreEqual(NativeCapabilityFootprintLibraryCheckedReceiptStatus.NcpFlcsCommittedPostverified, receipt.ReceiptStatus);
            Assert.IsTrue(receipt.Committed && receipt.NativeWriteStarted && receipt.Postverified
                && receipt.TargetVerified && receipt.UnrelatedEntriesVerified && receipt.RevisionTracked);
            Assert.IsFalse(receipt.CompleteLifecycle || receipt.CompleteChangeTracking || receipt.RollbackAttempted);
            Assert.AreEqual(state, receipt.ObservedBefore);
            Assert.AreEqual(state.StateSha256, receipt.BeforeSnapshotSha256);
            Assert.AreEqual(receipt.ObservedAfter.StateSha256, receipt.AfterSnapshotSha256);
            Assert.AreEqual(before.GetProperty("canonicalSha256").GetString(), receipt.BeforeDefinitionSha256);
            Assert.AreEqual(CheckedFootprintSha256(committedBytes), receipt.AfterDefinitionSha256);
            Assert.IsTrue(receipt.ObservedAfter.Revision.Sequence > state.Revision.Sequence);
            Assert.AreEqual(committedData.GetProperty("requestFingerprintSha256").GetString(), receipt.RequestFingerprintSha256);
            NativeCapabilityFootprintLibraryCheckedState after = await Snapshot(mcp);
            Assert.AreEqual(receipt.ObservedAfter, after);
            await AssertIsolation(mcp);

            // Preserve both timestamp and length so timestamp-only provider caching cannot
            // satisfy stale admission. The externally changed bytes must survive every refusal,
            // exact replay and receipt lookup that follows.
            DateTime committedTime = File.GetLastWriteTimeUtc(footprintPath);
            string driftedText = Encoding.UTF8.GetString(committedBytes).Replace(
                "checked definition after", "checked definition later", StringComparison.Ordinal);
            byte[] driftedBytes = Encoding.UTF8.GetBytes(driftedText);
            Assert.AreEqual(committedBytes.Length, driftedBytes.Length);
            Assert.AreNotEqual(CheckedFootprintSha256(committedBytes), CheckedFootprintSha256(driftedBytes));
            await File.WriteAllBytesAsync(footprintPath, driftedBytes, token);
            File.SetLastWriteTimeUtc(footprintPath, committedTime);
            Assert.AreEqual(committedTime, File.GetLastWriteTimeUtc(footprintPath));
            Assert.AreEqual(committedBytes.Length, new FileInfo(footprintPath).Length);
            await File.WriteAllBytesAsync(Path.Combine(evidence, "checked-footprint-external-drift.kicad_mod"), driftedBytes, token);

            var staleRequest = request.Clone();
            staleRequest.OperationId = Guid.NewGuid().ToString("D");
            staleRequest.ExpectedState = after.Clone();
            staleRequest.ExpectedSnapshotSha256 = after.StateSha256;
            JsonElement stale = await Write(mcp, staleRequest);
            RequireCheckedFootprintRefusal(stale, "stale_library_state");
            Assert.IsFalse(stale.GetProperty("structuredContent").GetProperty("nativeWriteStarted").GetBoolean());
            NativeCapabilityFootprintLibraryCheckedWriteResponse staleReceipt = await Receipt(mcp, staleRequest);
            Assert.AreEqual("rejected", staleReceipt.Status);
            Assert.AreEqual("stale_library_state", staleReceipt.ErrorCode);
            Assert.IsFalse(staleReceipt.Committed || staleReceipt.NativeWriteStarted || staleReceipt.RollbackAttempted);
            NativeCapabilityFootprintLibraryCheckedState driftedState = await Snapshot(mcp);
            Assert.AreNotEqual(after.StateSha256, driftedState.StateSha256);
            Assert.AreNotEqual(after.DetachedDefinitionSha256, driftedState.DetachedDefinitionSha256);
            Assert.IsTrue(driftedState.Revision.Sequence > after.Revision.Sequence);
            JsonElement driftedDefinition = await ReadDefinition(mcp, documentJson, "persisted_library");
            AssertCompleteCheckedFootprintDefinition(driftedDefinition, footprintPath, driftedBytes);
            var driftedFootprint = BoardJson.Parser.Parse<FootprintInstance>(driftedDefinition.GetProperty("footprint").GetRawText());
            var expectedDrift = candidate.Clone();
            expectedDrift.Definition.Attributes.Description = "checked definition later";
            AssertCheckedFootprintContent(expectedDrift, driftedFootprint);

            JsonElement replay = await Write(mcp, request);
            RequireCheckedFootprintSuccess(replay);
            Assert.IsTrue(replay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean());
            Assert.AreEqual(receipt.AfterSnapshotSha256,
                replay.GetProperty("structuredContent").GetProperty("afterSnapshotSha256").GetString());
            Assert.AreEqual(receipt.RequestFingerprintSha256,
                replay.GetProperty("structuredContent").GetProperty("requestFingerprintSha256").GetString());
            CollectionAssert.AreEqual(driftedBytes, await File.ReadAllBytesAsync(footprintPath, token));
            Assert.AreEqual(committedTime, File.GetLastWriteTimeUtc(footprintPath));
            Assert.AreEqual(driftedState, await Snapshot(mcp), "Replay must not advance the current library revision.");

            var conflictingRequest = request.Clone();
            conflictingRequest.Footprint.Definition.Attributes.Keywords = "operation id conflict";
            JsonElement conflict = await Write(mcp, conflictingRequest);
            RequireCheckedFootprintRefusal(conflict, "operation_id_conflict");
            Assert.IsFalse(conflict.GetProperty("structuredContent").GetProperty("nativeWriteStarted").GetBoolean());
            Assert.AreEqual(receipt, await Receipt(mcp, request),
                "A conflicting operation must preserve the original exact checked receipt.");
            CollectionAssert.AreEqual(driftedBytes, await File.ReadAllBytesAsync(footprintPath, token));

            // A separate managed MCP process has no original receipt cache. Its lookup must
            // recover the native owner's receipt without retrying the saved request.
            await using var reconnect = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(scratch, "checked-footprint-mcp-reconnect"),
                Path.Combine(evidence, "checked-footprint-mcp-reconnect.stderr.log"), token);
            RequireCheckedFootprintSuccess(await Tool(reconnect, "kicad_instance_attach", new
            { endpoint, expectedInstanceId = instanceId }));
            Assert.AreEqual(receipt, await Receipt(reconnect, request));
            Assert.AreEqual(staleReceipt, await Receipt(reconnect, staleRequest));
            Assert.AreEqual(driftedState, await Snapshot(reconnect));
            JsonElement reconnectedDefinition = await ReadDefinition(reconnect, documentJson, "persisted_library");
            AssertCompleteCheckedFootprintDefinition(reconnectedDefinition, footprintPath, driftedBytes);
            AssertCheckedFootprintContent(expectedDrift,
                BoardJson.Parser.Parse<FootprintInstance>(reconnectedDefinition.GetProperty("footprint").GetRawText()));
            CollectionAssert.AreEqual(driftedBytes, await File.ReadAllBytesAsync(footprintPath, token));
            Assert.AreEqual(committedTime, File.GetLastWriteTimeUtc(footprintPath));
            await AssertIsolation(reconnect);
        }
        finally
        {
            // Even a rejected positive write leaves independent evidence of collateral state.
            await AssertIsolation(mcp);
        }
    }

    private static void RequireCheckedFootprintSuccess(JsonElement result) =>
        Assert.IsFalse(result.TryGetProperty("isError", out JsonElement error) && error.GetBoolean(), result.GetRawText());

    private static void RequireCheckedFootprintRefusal(JsonElement result, string code)
    {
        Assert.IsTrue(result.TryGetProperty("isError", out JsonElement error) && error.GetBoolean(), result.GetRawText());
        Assert.AreEqual(code, result.GetProperty("structuredContent").GetProperty("errorCode").GetString(), result.GetRawText());
        Assert.IsFalse(result.GetProperty("structuredContent").GetProperty("committed").GetBoolean(), result.GetRawText());
    }

    private static void AssertCompleteCheckedFootprintDefinition(JsonElement data, string path, byte[] bytes)
    {
        Assert.IsTrue(data.GetProperty("definitionComplete").GetBoolean(), data.GetRawText());
        Assert.HasCount(0, data.GetProperty("limitations").EnumerateArray(), data.GetRawText());
        var footprint = BoardJson.Parser.Parse<FootprintInstance>(data.GetProperty("footprint").GetRawText());
        Assert.IsNotNull(footprint.Definition);
        Assert.AreEqual("Fixture", footprint.Definition.Id.LibraryNickname);
        Assert.AreEqual(CheckedFootprintName, footprint.Definition.Id.EntryName);
        Assert.IsNotNull(footprint.Definition.ReferenceField);
        Assert.IsNotNull(footprint.Definition.ValueField);
        Assert.IsNotNull(footprint.Definition.DatasheetField);
        Assert.IsNotNull(footprint.Definition.DescriptionField);
        Assert.IsNotNull(footprint.Definition.Attributes);
        Assert.IsNotNull(footprint.Definition.Overrides);
        Assert.IsNotNull(footprint.Definition.Jumpers);
        var baseline = SchematicJson.Parser.Parse<NativeFileBaselineState>(data.GetProperty("fileBaseline").GetRawText());
        Assert.AreEqual(Path.GetFullPath(path), Path.GetFullPath(baseline.Path));
        Assert.AreEqual(NativeFileBaselineStatus.NfbsUnchanged, baseline.Status);
        Assert.IsTrue(baseline.BaselineKnown && baseline.BaselineExists && baseline.CurrentKnown && baseline.CurrentExists);
        Assert.AreEqual(CheckedFootprintSha256(bytes), baseline.BaselineSha256);
        Assert.AreEqual(CheckedFootprintSha256(bytes), baseline.CurrentSha256);
        Assert.AreEqual((ulong)bytes.LongLength, baseline.BaselineBytes);
        Assert.AreEqual((ulong)bytes.LongLength, baseline.CurrentBytes);
    }

    private static async Task AssertCheckedFootprintBaseline(NativeCapabilityFootprintLibraryCheckedState state,
        string path, CancellationToken token)
    {
        var baseline = state.DependencyBaselines.Single(item => Path.GetFullPath(item.Path) == Path.GetFullPath(path));
        byte[] bytes = await File.ReadAllBytesAsync(path, token);
        Assert.IsTrue(baseline.Known && baseline.Exists, path);
        Assert.AreEqual(CheckedFootprintSha256(bytes), baseline.Sha256, path);
        Assert.AreEqual((ulong)bytes.LongLength, baseline.Bytes, path);
    }

    private static void AssertCheckedFootprintContent(FootprintInstance expected, FootprintInstance actual)
    {
        // Library loads allocate a new container identity. Parent IDs are read-only API
        // references, not persisted library content; compare every other definition field.
        static Footprint Detached(Footprint definition)
        {
            Footprint value = definition.Clone();
            foreach (var field in new[] { value.ReferenceField, value.ValueField, value.DatasheetField, value.DescriptionField })
                field.Text.Parent = null;
            for (int index = 0; index < value.Items.Count; index++)
            {
                Any item = value.Items[index];
                if (item.Is(Pad.Descriptor))
                {
                    Pad pad = item.Unpack<Pad>(); pad.Parent = null; value.Items[index] = Any.Pack(pad);
                }
                else if (item.Is(BoardGraphicShape.Descriptor))
                {
                    BoardGraphicShape shape = item.Unpack<BoardGraphicShape>(); shape.Parent = null; value.Items[index] = Any.Pack(shape);
                }
                else if (item.Is(BoardText.Descriptor))
                {
                    BoardText text = item.Unpack<BoardText>(); text.Parent = null; value.Items[index] = Any.Pack(text);
                }
                else if (item.Is(Kiapi.Board.Types.Field.Descriptor))
                {
                    var field = item.Unpack<Kiapi.Board.Types.Field>(); field.Text.Parent = null; value.Items[index] = Any.Pack(field);
                }
            }
            return value;
        }
        Assert.AreEqual(Detached(expected.Definition), Detached(actual.Definition),
            "The entire persisted definition must match the supported fields, child identities and geometry of the exact candidate.");
        Assert.AreEqual(expected.Attributes, actual.Attributes);
        Assert.AreEqual(expected.Overrides, actual.Overrides);
        Assert.AreEqual(expected.Layer, actual.Layer);
        Assert.AreEqual(expected.Position, actual.Position);
        Assert.AreEqual(expected.Orientation, actual.Orientation);
        Assert.AreEqual(expected.Locked, actual.Locked);
    }

    private static string CheckedFootprintSha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
