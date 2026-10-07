using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    /// <summary>
    /// Runs the public typed metadata tools against an already populated native
    /// editor.  The caller owns the KiCad process and its evidence directory;
    /// this method owns only the compiled STDIO MCP child.  It intentionally
    /// leaves the capability/verification annotations on the public test method
    /// in NativeSessionTests, where the coordinator's native graph discovers it.
    /// </summary>
    private static async Task VerifyNativeSchematicMetadata(
        NativeClient client, DocumentSpecifier document, int processId, string display,
        string evidence, string instanceId, CancellationToken token)
    {
        string statePath = Path.Combine(evidence, instanceId + "-metadata-mcp-state");
        string stderr = Path.Combine(evidence, instanceId + "-metadata-mcp.stderr.log");
        await using var mcp = await StdioMcpFixture.StartAsync(
            SyncHarnessProcessTests.ProductionStartInfo(), statePath, stderr, token);

        var listed = await mcp.ListTools(null);
        var names = listed.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        CollectionAssert.Contains(names.ToArray(), "kicad_schematic_replace_bus_aliases");
        CollectionAssert.Contains(names.ToArray(), "kicad_schematic_replace_variant_registry");
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach",
            new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        async Task<CheckedSchematicState> Capture() =>
            await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
                new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);

        string documentJson = SchematicJson.Formatter.Format(document);
        CheckedSchematicState initial = await Capture();
        string initialJson = SchematicJson.Formatter.Format(initial);
        var aliases = new[] { new { name = "MCP_METADATA_BUS", members = new[] { "MCP_D0", "MCP_D1" } } };
        string aliasOperation = Guid.NewGuid().ToString("D");
        object AliasArguments(string expected, string operation, object values) => new
        {
            instanceId, documentJson, expectedStateJson = expected,
            aliases = values, operationId = operation
        };

        JsonElement createdReply = await mcp.Tool("kicad_schematic_replace_bus_aliases",
            AliasArguments(initialJson, aliasOperation, aliases));
        RequireToolSuccess(createdReply);
        Assert.IsTrue(createdReply.GetProperty("structuredContent").GetProperty("mutationSubmitted").GetBoolean(),
            createdReply.GetRawText());
        Assert.IsFalse(createdReply.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            createdReply.GetRawText());
        CheckedSchematicState afterAlias = await Capture();
        Assert.AreEqual("MCP_METADATA_BUS", Root(afterAlias).BusAliases.Single().Name);

        // Exact request replay uses the original checkpoint and returns the
        // saved receipt before attempting a new checkpoint or native mutation.
        JsonElement replayReply = await mcp.Tool("kicad_schematic_replace_bus_aliases",
            AliasArguments(initialJson, aliasOperation, aliases));
        RequireToolSuccess(replayReply);
        Assert.IsTrue(replayReply.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            replayReply.GetRawText());
        Assert.IsFalse(replayReply.GetProperty("structuredContent").GetProperty("mutationSubmitted").GetBoolean(),
            replayReply.GetRawText());
        Assert.AreEqual(afterAlias, await Capture(), "An exact retry must not add a revision.");

        // Rebinding the operation ID to a different typed payload is refused by
        // the process-owned receipt table and leaves the live state untouched.
        JsonElement conflictReply = await mcp.Tool("kicad_schematic_replace_bus_aliases",
            AliasArguments(initialJson, aliasOperation,
                new[] { new { name = "MCP_METADATA_BUS", members = new[] { "OTHER" } } }));
        Assert.IsTrue(conflictReply.GetProperty("isError").GetBoolean(), conflictReply.GetRawText());
        Assert.AreEqual(afterAlias, await Capture(), "A conflicting operation ID must not mutate the schematic.");

        // A fresh operation planned from the original checkpoint is stale after
        // the first commit.  The public typed adapter must refuse before native
        // dispatch and retain the current alias.
        JsonElement staleReply = await mcp.Tool("kicad_schematic_replace_bus_aliases",
            AliasArguments(initialJson, Guid.NewGuid().ToString("D"), Array.Empty<object>()));
        Assert.IsTrue(staleReply.GetProperty("isError").GetBoolean(), staleReply.GetRawText());
        Assert.AreEqual("stale_document_state", staleReply.GetProperty("structuredContent")
            .GetProperty("errorCode").GetString(), staleReply.GetRawText());
        Assert.AreEqual(afterAlias, await Capture());

        // Malformed typed input is rejected before receipt inspection.  The
        // current checked observation remains the exact state after the commit.
        JsonElement malformedAlias = await mcp.Tool("kicad_schematic_replace_bus_aliases",
            AliasArguments(SchematicJson.Formatter.Format(afterAlias), Guid.NewGuid().ToString("D"),
                new[] { new { name = " MCP_BAD", members = new[] { "D0" } } }));
        Assert.IsTrue(malformedAlias.GetProperty("isError").GetBoolean(), malformedAlias.GetRawText());
        Assert.AreEqual("invalid_bus_aliases", malformedAlias.GetProperty("structuredContent")
            .GetProperty("errorCode").GetString(), malformedAlias.GetRawText());
        Assert.AreEqual(afterAlias, await Capture());

        // Clear the catalogue and exercise an explicit no-op clear.  Both are
        // real checked mutations; the second must keep the native revision.
        string afterAliasJson = SchematicJson.Formatter.Format(afterAlias);
        JsonElement clearedReply = await mcp.Tool("kicad_schematic_replace_bus_aliases",
            AliasArguments(afterAliasJson, Guid.NewGuid().ToString("D"), Array.Empty<object>()));
        RequireToolSuccess(clearedReply);
        CheckedSchematicState cleared = await Capture();
        Assert.IsEmpty(Root(cleared).BusAliases);
        JsonElement noopReply = await mcp.Tool("kicad_schematic_replace_bus_aliases",
            AliasArguments(SchematicJson.Formatter.Format(cleared), Guid.NewGuid().ToString("D"), Array.Empty<object>()));
        RequireToolSuccess(noopReply);
        CheckedSchematicState afterNoop = await Capture();
        Assert.AreEqual(cleared, afterNoop, "Clearing an already empty catalogue must be a native no-op.");

        var variants = new[]
        {
            new { name = "MCP_Assembly", description = "Typed MCP metadata journey" },
            new { name = "MCP_Tooling", description = "" }
        };
        string variantOperation = Guid.NewGuid().ToString("D");
        JsonElement variantReply = await mcp.Tool("kicad_schematic_replace_variant_registry", new
        {
            instanceId, documentJson, expectedStateJson = SchematicJson.Formatter.Format(afterNoop),
            entries = variants, operationId = variantOperation
        });
        RequireToolSuccess(variantReply);
        CheckedSchematicState afterVariant = await Capture();
        Assert.AreEqual("Typed MCP metadata journey", Root(afterVariant).VariantDescriptions["MCP_Assembly"]);

        JsonElement variantReplay = await mcp.Tool("kicad_schematic_replace_variant_registry", new
        {
            instanceId, documentJson, expectedStateJson = SchematicJson.Formatter.Format(afterNoop),
            entries = variants, operationId = variantOperation
        });
        RequireToolSuccess(variantReplay);
        Assert.IsTrue(variantReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            variantReplay.GetRawText());
        Assert.AreEqual(afterVariant, await Capture());

        JsonElement malformedVariant = await mcp.Tool("kicad_schematic_replace_variant_registry", new
        {
            instanceId, documentJson, expectedStateJson = SchematicJson.Formatter.Format(afterVariant),
            entries = new[] { new { name = "MCP_Bad", description = "bad\0value" } },
            operationId = Guid.NewGuid().ToString("D")
        });
        Assert.IsTrue(malformedVariant.GetProperty("isError").GetBoolean(), malformedVariant.GetRawText());
        Assert.AreEqual("invalid_variant_registry", malformedVariant.GetProperty("structuredContent")
            .GetProperty("errorCode").GetString(), malformedVariant.GetRawText());
        Assert.AreEqual(afterVariant, await Capture());

        // Native undo/redo is observed through the same checked state that
        // admitted the MCP edit.  Wait on the revision event instead of polling
        // a process status or guessing a keyboard completion delay.
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        CheckedSchematicState undone = await WaitForRevision(Capture, afterVariant.State.Revision.Sequence, token);
        Assert.IsFalse(Root(undone).VariantDescriptions.ContainsKey("MCP_Assembly"),
            "Undo must remove the typed variant registry edit.");
        NativeKeyboard.SchematicShortcut(display, processId, "y");
        CheckedSchematicState redone = await WaitForRevision(Capture, undone.State.Revision.Sequence, token);
        Assert.AreEqual("Typed MCP metadata journey", Root(redone).VariantDescriptions["MCP_Assembly"]);

        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
        CheckedSchematicState reloaded = await Capture();
        Assert.AreEqual("Typed MCP metadata journey", Root(reloaded).VariantDescriptions["MCP_Assembly"],
            "The typed variant registry must survive native save/reload.");
        Assert.IsEmpty(Root(reloaded).BusAliases, "The explicit alias clear must survive native save/reload.");

        // Cancellation may race the checked native dispatch.  Use a separate
        // compiled MCP child and reconcile the exact operation receipt through
        // the direct native client afterward.  A reply, when one wins the race,
        // must agree with the observed receipt; a missing reply never becomes a
        // claimed rollback or an invented `not_submitted` success.
        await AssessCancellation(client, document, instanceId, evidence, reloaded, token);

        // Reuse the established electrical and XML persistence assertions so
        // this lane proves the project metadata is meaningful to KiCad's bus
        // connectivity and variant persistence, not only visible in a snapshot.
        string projectPath = (await client.HandshakeAsync(token)).ProjectPath;
        using (var project = JsonDocument.Parse(await File.ReadAllTextAsync(projectPath, token)))
        {
            var persisted = project.RootElement.GetProperty("schematic").GetProperty("variants")
                .EnumerateArray().ToDictionary(v => v.GetProperty("name").GetString()!,
                    v => v.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "");
            Assert.AreEqual("Typed MCP metadata journey", persisted["MCP_Assembly"]);
            Assert.AreEqual("", persisted["MCP_Tooling"]);
        }
        await VerifyBusAliasConnectivity(client, document, processId, display, token);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
    }

    private static async Task AssessCancellation(NativeClient client, DocumentSpecifier document,
        string instanceId, string evidence, CheckedSchematicState before, CancellationToken token)
    {
        string operationId = Guid.NewGuid().ToString("D");
        var aliasState = new SchematicBusAliasState();
        aliasState.Aliases.Add(new SchematicBusAlias { Name = "MCP_CANCEL_BUS", Members = { "MCP_CANCEL" } });
        CheckedSchematicBatch request = MetadataBatch(document, before, operationId, aliasState);
        await using var cancellable = await CancellableMcpClient.StartAsync(
            Path.Combine(evidence, instanceId + "-metadata-cancel-state"),
            Path.Combine(evidence, instanceId + "-metadata-cancel.stderr.log"), token);
        RequireToolSuccess(await cancellable.Tool("kicad_instance_attach",
            new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var (requestId, pending) = cancellable.StartTool("kicad_schematic_replace_bus_aliases", new
        {
            instanceId,
            documentJson = SchematicJson.Formatter.Format(document),
            expectedStateJson = SchematicJson.Formatter.Format(before),
            aliases = new[] { new { name = "MCP_CANCEL_BUS", members = new[] { "MCP_CANCEL" } } },
            operationId
        });
        await cancellable.Cancel(requestId, "Metadata cancellation race assessment");
        JsonElement? reply = null;
        try { reply = await pending.WaitAsync(TimeSpan.FromSeconds(3), token); }
        catch (TimeoutException) { }

        CheckedSchematicBatchReceipt receipt = await client.InvokeAsync<ReadCheckedSchematicBatchReceipt,
            CheckedSchematicBatchReceipt>(new()
            {
                Document = document.Clone(), ProcessEpoch = client.Epoch,
                OperationId = operationId, ExpectedRequest = request.Clone()
            }, token);
        CheckedSchematicContract.ValidateResult(request, receipt, inspect: true);
        CheckedSchematicState observed = await client.InvokeAsync<ReadCheckedSchematicState,
            CheckedSchematicState>(new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        if (receipt.Status == CheckedSchematicBatchStatus.CsbsCompleted)
        {
            Assert.AreEqual("MCP_CANCEL_BUS", Root(observed).BusAliases.Single().Name,
                "A cancelled call that reached native commit must be reconciled as committed.");
            if (reply is { } completed)
            {
                JsonElement structured = completed.GetProperty("structuredContent");
                Assert.IsTrue(structured.GetProperty("mutationSubmitted").GetBoolean(), completed.GetRawText());
            }
        }
        else
        {
            Assert.AreEqual(before, observed,
                "A cancelled call without a completed receipt must leave the checked state unchanged.");
            if (reply is { } refused)
            {
                JsonElement structured = refused.GetProperty("structuredContent");
                Assert.AreNotEqual("not_confirmed", structured.GetProperty("outcome").GetString(),
                    "A pre-dispatch cancellation must not be reported as an indeterminate native commit.");
            }
        }
    }

    private static CheckedSchematicBatch MetadataBatch(DocumentSpecifier document,
        CheckedSchematicState expected, string operationId, SchematicBusAliasState aliases)
    {
        var batch = new ApplySchematicItemBatch
        {
            Document = document.Clone(), DocumentEpoch = expected.State.Revision.Epoch,
            ExpectedRevision = expected.State.Revision.Clone(), OperationId = operationId,
            Description = "Cancellation metadata assessment"
        };
        batch.Operations.Add(new SchematicItemOperation { ReplaceBusAliases = aliases });
        return new CheckedSchematicBatch { Batch = batch, ExpectedState = expected.State.Clone() };
    }

    private static async Task<CheckedSchematicState> WaitForRevision(
        Func<Task<CheckedSchematicState>> capture, ulong previous, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            CheckedSchematicState state = await capture();
            if (state.State.Revision.Sequence != previous) return state;
            await Task.Delay(100, wait.Token);
        }
    }

    private static SchematicMetadata Root(CheckedSchematicState state) =>
        state.Electrical.Hierarchy.Data.Instances
            .Single(screen => screen.Metadata.Document.SheetPath.Path.Count == 1).Metadata;
}
