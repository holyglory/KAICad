using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    /// <summary>
    /// Exercises the typed schematic cross-probe tools against the real
    /// graphical schematic editor.  Selection and net highlighting are GUI
    /// actions, so the lifecycle checkpoint and unchanged source bytes are the
    /// authoritative design-mutation boundary.
    /// </summary>
    private static async Task VerifyNativeSchematicCrossProbe(
        NativeClient client, DocumentSpecifier document, string schematicPath,
        InstanceRegistry registry, IReadOnlyList<(string Id, string Endpoint, string Project, string RootId)> launched,
        string evidence, string instanceId, CancellationToken token)
    {
        string statePath = Path.Combine(evidence, instanceId + "-cross-probe-mcp-state");
        string stderr = Path.Combine(evidence, instanceId + "-cross-probe-mcp.stderr.log");
        await using var mcp = await StdioMcpFixture.StartAsync(
            SyncHarnessProcessTests.ProductionStartInfo(), statePath, stderr, token);

        var listed = await mcp.ListTools(null);
        var names = listed.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        CollectionAssert.Contains(names.ToArray(), "kicad_schematic_sync_selection");
        CollectionAssert.Contains(names.ToArray(), "kicad_schematic_highlight_nets");
        CollectionAssert.Contains(names.ToArray(), "kicad_schematic_cross_probe_receipt");
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach",
            new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        DocumentLifecycleState before = await ObserveLifecycleState(client, document, token);
        Assert.IsFalse(before.NativeContentDirty, "Cross-probe must start from a saved native schematic.");
        string expectedStateJson = SchematicJson.Formatter.Format(before);
        byte[] sourceBefore = await File.ReadAllBytesAsync(schematicPath, token);
        object target = SchematicDocumentInput(document);

        // TP1 and TP2 are the real symbols in NativeSessionTests' electrical
        // fixture.  Include both a footprint and pad selection so the native
        // handler must resolve the exact typed request rather than an empty or
        // synthetic selection.
        object selection = new
        {
            context = "SSC_EXPLICIT",
            mode = "SSM_ITEMS_AND_NETS",
            focusItem = new { footprint = new { reference = "TP1" } },
            items = new object[]
            {
                new { footprint = new { reference = "TP1" } },
                new { pad = new { reference = "TP1", number = "1" } },
                new { footprint = new { reference = "TP2" } }
            }
        };
        string selectionOperation = Guid.NewGuid().ToString("D");
        JsonElement selectionReply = await mcp.Tool("kicad_schematic_sync_selection", new
        {
            instanceId, document = target, selection,
            expectedStateJson, operationId = selectionOperation
        });
        RequireToolSuccess(selectionReply);
        JsonElement selectionData = selectionReply.GetProperty("structuredContent");
        Assert.AreEqual(instanceId, selectionData.GetProperty("instanceId").GetString(), selectionReply.GetRawText());
        Assert.AreEqual(client.Epoch, selectionData.GetProperty("processEpoch").GetString(), selectionReply.GetRawText());
        Assert.AreEqual(selectionOperation, selectionData.GetProperty("operationId").GetString(), selectionReply.GetRawText());
        Assert.IsTrue(selectionData.GetProperty("nativeGuiOnly").GetBoolean(), selectionReply.GetRawText());
        Assert.IsTrue(selectionData.GetProperty("selectionChanged").GetBoolean(), selectionReply.GetRawText());
        Assert.IsTrue(selectionData.GetProperty("operationReceipt").GetBoolean(), selectionReply.GetRawText());
        Assert.IsFalse(selectionData.GetProperty("designMutation").GetBoolean(), selectionReply.GetRawText());
        Assert.AreEqual(expectedStateJson, selectionData.GetProperty("beforeState").GetString(), selectionReply.GetRawText());
        Assert.AreEqual(expectedStateJson, selectionData.GetProperty("afterState").GetString(), selectionReply.GetRawText());
        Assert.AreEqual(before, await ObserveLifecycleState(client, document, token),
            "Selection must not advance the schematic lifecycle revision.");

        // The process-owned receipt makes an exact retry idempotent.  Keep the
        // original checkpoint in the retry so the fingerprint is unchanged.
        JsonElement selectionReplay = await mcp.Tool("kicad_schematic_sync_selection", new
        {
            instanceId, document = target, selection,
            expectedStateJson, operationId = selectionOperation
        });
        RequireToolSuccess(selectionReplay);
        Assert.AreEqual(selectionReply.GetProperty("structuredContent").GetRawText(),
            selectionReplay.GetProperty("structuredContent").GetRawText(),
            "An exact selection retry must return its process receipt.");

        JsonElement selectionReceipt = await mcp.Tool("kicad_schematic_cross_probe_receipt", new
        {
            instanceId, document = target, expectedProcessEpoch = client.Epoch,
            operationId = selectionOperation
        });
        RequireToolSuccess(selectionReceipt);
        JsonElement selectionReceiptData = selectionReceipt.GetProperty("structuredContent");
        Assert.IsTrue(selectionReceiptData.GetProperty("found").GetBoolean(), selectionReceipt.GetRawText());
        Assert.AreEqual("mcp-process", selectionReceiptData.GetProperty("receiptScope").GetString(), selectionReceipt.GetRawText());
        Assert.AreEqual("process-cache", selectionReceiptData.GetProperty("receiptObservation").GetString(), selectionReceipt.GetRawText());

        string highlightOperation = Guid.NewGuid().ToString("D");
        object highlight = new { netNames = new[] { "SIGNAL" } };
        JsonElement highlightReply = await mcp.Tool("kicad_schematic_highlight_nets", new
        {
            instanceId, document = target, highlight,
            expectedStateJson, operationId = highlightOperation
        });
        RequireToolSuccess(highlightReply);
        JsonElement highlightData = highlightReply.GetProperty("structuredContent");
        Assert.AreEqual(highlightOperation, highlightData.GetProperty("operationId").GetString(), highlightReply.GetRawText());
        Assert.IsTrue(highlightData.GetProperty("nativeGuiOnly").GetBoolean(), highlightReply.GetRawText());
        Assert.IsTrue(highlightData.GetProperty("highlightChanged").GetBoolean(), highlightReply.GetRawText());
        Assert.IsTrue(highlightData.GetProperty("operationReceipt").GetBoolean(), highlightReply.GetRawText());
        Assert.IsFalse(highlightData.GetProperty("designMutation").GetBoolean(), highlightReply.GetRawText());
        Assert.AreEqual(expectedStateJson, highlightData.GetProperty("beforeState").GetString(), highlightReply.GetRawText());
        Assert.AreEqual(expectedStateJson, highlightData.GetProperty("afterState").GetString(), highlightReply.GetRawText());
        Assert.AreEqual(before, await ObserveLifecycleState(client, document, token),
            "Net highlighting must not advance the schematic lifecycle revision.");

        // Admission failures happen before native dispatch and preserve the
        // exact live checkpoint.  Use fresh operation IDs so no receipt can
        // mask the refusal path.
        DocumentLifecycleState staleRevision = before.Clone();
        staleRevision.Revision.Sequence++;
        JsonElement stale = await mcp.Tool("kicad_schematic_highlight_nets", new
        {
            instanceId, document = target, highlight,
            expectedStateJson = SchematicJson.Formatter.Format(staleRevision),
            operationId = Guid.NewGuid().ToString("D")
        });
        AssertSchematicToolError(stale, "schematic_cross_probe_state_changed");
        Assert.AreEqual(before, await ObserveLifecycleState(client, document, token));

        DocumentLifecycleState staleEpoch = before.Clone();
        staleEpoch.ProcessEpoch = Guid.NewGuid().ToString("D");
        JsonElement wrongEpoch = await mcp.Tool("kicad_schematic_sync_selection", new
        {
            instanceId, document = target, selection,
            expectedStateJson = SchematicJson.Formatter.Format(staleEpoch),
            operationId = Guid.NewGuid().ToString("D")
        });
        AssertSchematicToolError(wrongEpoch, "schematic_cross_probe_instance_changed");
        Assert.AreEqual(before, await ObserveLifecycleState(client, document, token));

        object wrongTarget = new
        {
            type = "DOCTYPE_PCB",
            project = new { name = document.Project.Name, path = document.Project.Path },
            boardFilename = "fixture.kicad_pcb"
        };
        JsonElement wrongDocument = await mcp.Tool("kicad_schematic_highlight_nets", new
        {
            instanceId, document = wrongTarget, highlight,
            expectedStateJson, operationId = Guid.NewGuid().ToString("D")
        });
        AssertSchematicToolError(wrongDocument, "invalid_schematic_cross_probe_target");
        Assert.AreEqual(before, await ObserveLifecycleState(client, document, token));

        // A sibling process has a different epoch.  Passing the live target
        // checkpoint to that process must be refused before its native handler
        // is called, proving process identity is part of admission.
        var sibling = launched.Single(targetProcess => targetProcess.Id != instanceId);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new
        {
            endpoint = sibling.Endpoint, expectedInstanceId = sibling.Id
        }));
        JsonElement wrongInstance = await mcp.Tool("kicad_schematic_highlight_nets", new
        {
            instanceId = sibling.Id, document = target, highlight,
            expectedStateJson, operationId = Guid.NewGuid().ToString("D")
        });
        AssertSchematicToolError(wrongInstance, "schematic_cross_probe_instance_changed");
        Assert.AreEqual(before, await ObserveLifecycleState(client, document, token));
        Assert.AreEqual(sibling.Id, registry.Get(sibling.Id).InstanceId);

        CollectionAssert.AreEqual(sourceBefore, await File.ReadAllBytesAsync(schematicPath, token),
            "Cross-probe selection/highlighting must not rewrite the schematic source.");

        // The native editor remains alive while the MCP process reconnects. A
        // new MCP process has no process-local receipt and must report that
        // absence honestly instead of claiming a native outcome or safe retry.
        string reconnectRoot = Directory.CreateTempSubdirectory("kicad-schematic-cross-probe-reconnect-").FullName;
        try
        {
            await using var reconnectMcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(), reconnectRoot,
                Path.Combine(evidence, instanceId + "-cross-probe-reconnect.stderr.log"), token);
            RequireToolSuccess(await reconnectMcp.Tool("kicad_instance_attach",
                new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            JsonElement missingReceipt = await reconnectMcp.Tool("kicad_schematic_cross_probe_receipt", new
            {
                instanceId, document = target, expectedProcessEpoch = client.Epoch,
                operationId = selectionOperation
            });
            RequireToolSuccess(missingReceipt);
            JsonElement missingData = missingReceipt.GetProperty("structuredContent");
            Assert.IsFalse(missingData.GetProperty("found").GetBoolean(), missingReceipt.GetRawText());
            Assert.AreEqual("mcp-process", missingData.GetProperty("receiptScope").GetString(), missingReceipt.GetRawText());
            Assert.AreEqual("not-found", missingData.GetProperty("receiptObservation").GetString(), missingReceipt.GetRawText());
            Assert.AreEqual("unknown-native-outcome", missingData.GetProperty("retrySafety").GetString(), missingReceipt.GetRawText());
        }
        finally { Directory.Delete(reconnectRoot, true); }

        Assert.AreEqual(before, await ObserveLifecycleState(client, document, token));
        CollectionAssert.AreEqual(sourceBefore, await File.ReadAllBytesAsync(schematicPath, token));
    }

    private static object SchematicDocumentInput(DocumentSpecifier document) => new
    {
        type = "DOCTYPE_SCHEMATIC",
        project = new { name = document.Project.Name, path = document.Project.Path },
        sheetPath = new
        {
            path = document.SheetPath.Path.Select(id => new { value = id.Value }).ToArray(),
            pathHumanReadable = document.SheetPath.PathHumanReadable
        }
    };

    private static void AssertSchematicToolError(JsonElement result, string expectedCode)
    {
        Assert.IsTrue(result.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean(),
            result.GetRawText());
        JsonElement structured = result.GetProperty("structuredContent");
        Assert.AreEqual(expectedCode, structured.GetProperty("errorCode").GetString(), result.GetRawText());
        Assert.IsFalse(structured.GetProperty("mutationSubmitted").GetBoolean(), result.GetRawText());
    }
}
