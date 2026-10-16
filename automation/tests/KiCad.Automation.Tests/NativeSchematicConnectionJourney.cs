using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
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
    /// Qualifies the task-oriented typed connection boundary against a live
    /// editor. The generic checked-batch journey already covers the lower
    /// protocol; this lane deliberately uses only the typed wire/label/
    /// junction tools and verifies their native history and file round trip.
    /// </summary>
    private static async Task VerifyNativeSchematicConnections(
        NativeClient client, DocumentSpecifier document, int processId, string display,
        string evidence, string instanceId, CancellationToken token)
    {
        string statePath = Path.Combine(evidence, instanceId + "-connections-mcp-state");
        string stderr = Path.Combine(evidence, instanceId + "-connections-mcp.stderr.log");
        await using var mcp = await StdioMcpFixture.StartAsync(
            SyncHarnessProcessTests.ProductionStartInfo(), statePath, stderr, token);

        var listed = await mcp.ListTools(null);
        var names = listed.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string name in new[]
        {
            "kicad_schematic_create_connection_item", "kicad_schematic_read_connection_item",
            "kicad_schematic_update_connection_item", "kicad_schematic_delete_connection_item"
        })
            CollectionAssert.Contains(names.ToArray(), name);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach",
            new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        async Task<CheckedSchematicState> Capture() =>
            await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
                new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);

        string documentJson = SchematicJson.Formatter.Format(document);
        CheckedSchematicState initial = await Capture();
        string initialJson = SchematicJson.Formatter.Format(initial);
        static Vector2 At(int x, int y) => new() { XNm = x * 1_270_000L, YNm = y * 1_270_000L };
        static Any Item(JsonElement result) => SchematicJson.Parser.Parse<Any>(
            result.GetProperty("structuredContent").GetProperty("item").GetRawText());
        static CheckedSchematicBatchReceipt Receipt(JsonElement result) =>
            SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(
                result.GetProperty("structuredContent").GetProperty("receipt").GetRawText());
        static T Find<T>(CheckedSchematicState state, string id) where T : class, IMessage<T>, new() =>
            state.Electrical.Hierarchy.Data.Instances.SelectMany(screen => screen.Items)
                .Where(item => item.Is(Descriptor<T>())).Select(item => item.Unpack<T>())
                .Single(item => ItemId(item) == id);
        static MessageDescriptor Descriptor<T>() where T : class, IMessage<T>, new() =>
            typeof(T) == typeof(SchematicLine) ? SchematicLine.Descriptor
            : typeof(T) == typeof(LocalLabel) ? LocalLabel.Descriptor
            : typeof(T) == typeof(Junction) ? Junction.Descriptor
            : throw new InvalidOperationException("Unexpected typed connection item.");
        static string ItemId(IMessage item) => item switch
        {
            SchematicLine line => line.Id.Value,
            Junction junction => junction.Id.Value,
            LocalLabel label => label.Id.Value,
            _ => throw new InvalidOperationException("Unexpected typed connection item.")
        };

        Vector2 wireStart = At(40, 140), wireEnd = At(55, 140);
        string wireId = Guid.NewGuid().ToString("D");
        string wireCreateOperation = Guid.NewGuid().ToString("D");
        object WireCreateArguments(string expectedState, string operation) => new
        {
            instanceId, documentJson, expectedStateJson = expectedState,
            kind = "wire", itemId = wireId,
            xNm = wireStart.XNm, yNm = wireStart.YNm,
            endXNm = wireEnd.XNm, endYNm = wireEnd.YNm, operationId = operation
        };
        JsonElement wireCreated = await mcp.Tool("kicad_schematic_create_connection_item",
            WireCreateArguments(initialJson, wireCreateOperation));
        RequireToolSuccess(wireCreated);
        CheckedSchematicBatchReceipt wireReceipt = Receipt(wireCreated);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, wireReceipt.Status,
            wireCreated.GetRawText());
        SchematicLine createdWire = wireReceipt.Result.Items.Single().Unpack<SchematicLine>();
        Assert.AreEqual(wireId, createdWire.Id.Value);
        Assert.AreEqual(wireStart, createdWire.Start);
        Assert.AreEqual(wireEnd, createdWire.End);

        JsonElement wireReplay = await mcp.Tool("kicad_schematic_create_connection_item",
            WireCreateArguments(initialJson, wireCreateOperation));
        RequireToolSuccess(wireReplay);
        Assert.AreEqual(wireReceipt, Receipt(wireReplay),
            "The same typed create operation must return its native receipt without another edit.");
        CheckedSchematicState afterWire = await Capture();

        JsonElement wireRead = await mcp.Tool("kicad_schematic_read_connection_item", new
        {
            instanceId, documentJson, itemId = wireId,
            expectedStateJson = SchematicJson.Formatter.Format(afterWire), expectedKind = "wire"
        });
        RequireToolSuccess(wireRead);
        SchematicLine readWire = Item(wireRead).Unpack<SchematicLine>();
        Assert.AreEqual(createdWire, readWire);

        Vector2 movedStart = At(42, 142), movedEnd = At(58, 142);
        string wireUpdateOperation = Guid.NewGuid().ToString("D");
        object WireUpdateArguments(string expectedState, string operation) => new
        {
            instanceId, documentJson, itemId = wireId, expectedStateJson = expectedState,
            xNm = movedStart.XNm, yNm = movedStart.YNm,
            endXNm = movedEnd.XNm, endYNm = movedEnd.YNm, operationId = operation,
            expectedKind = "wire"
        };
        JsonElement wireUpdated = await mcp.Tool("kicad_schematic_update_connection_item",
            WireUpdateArguments(SchematicJson.Formatter.Format(afterWire), wireUpdateOperation));
        RequireToolSuccess(wireUpdated);
        CheckedSchematicState afterWireUpdate = await Capture();
        Assert.AreEqual(movedStart, Find<SchematicLine>(afterWireUpdate, wireId).Start);
        Assert.AreEqual(movedEnd, Find<SchematicLine>(afterWireUpdate, wireId).End);
        JsonElement wireUpdateReplay = await mcp.Tool("kicad_schematic_update_connection_item",
            WireUpdateArguments(SchematicJson.Formatter.Format(afterWire), wireUpdateOperation));
        RequireToolSuccess(wireUpdateReplay);
        Assert.IsTrue(wireUpdateReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            wireUpdateReplay.GetRawText());

        string labelId = Guid.NewGuid().ToString("D");
        string labelCreateOperation = Guid.NewGuid().ToString("D");
        object LabelCreateArguments(string expectedState, string operation) => new
        {
            instanceId, documentJson, expectedStateJson = expectedState,
            kind = "localLabel", itemId = labelId,
            xNm = movedEnd.XNm, yNm = movedEnd.YNm,
            text = "MCP_TYPED_NET", operationId = operation
        };
        JsonElement labelCreated = await mcp.Tool("kicad_schematic_create_connection_item",
            LabelCreateArguments(SchematicJson.Formatter.Format(afterWireUpdate), labelCreateOperation));
        RequireToolSuccess(labelCreated);
        LocalLabel createdLabel = Receipt(labelCreated).Result.Items.Single().Unpack<LocalLabel>();
        Assert.AreEqual(labelId, createdLabel.Id.Value);
        Assert.AreEqual("MCP_TYPED_NET", createdLabel.Text.Text_);
        CheckedSchematicState afterLabelCreate = await Capture();
        Assert.AreEqual("MCP_TYPED_NET", Find<LocalLabel>(afterLabelCreate, labelId).Text.Text_);
        JsonElement labelReplay = await mcp.Tool("kicad_schematic_create_connection_item",
            LabelCreateArguments(SchematicJson.Formatter.Format(afterWireUpdate), labelCreateOperation));
        RequireToolSuccess(labelReplay);
        Assert.IsTrue(labelReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            labelReplay.GetRawText());

        // A junction on a straight wire is intentionally normalized away by
        // KiCad on save. Give it a real branch so its native persistence is
        // observable rather than mistaking that normalization for success.
        string branchId = Guid.NewGuid().ToString("D");
        string branchCreateOperation = Guid.NewGuid().ToString("D");
        Vector2 branchEnd = At(50, 150);
        JsonElement branchCreated = await mcp.Tool("kicad_schematic_create_connection_item", new
        {
            instanceId, documentJson,
            expectedStateJson = SchematicJson.Formatter.Format(afterLabelCreate),
            kind = "wire", itemId = branchId,
            xNm = At(50, 142).XNm, yNm = At(50, 142).YNm,
            endXNm = branchEnd.XNm, endYNm = branchEnd.YNm,
            operationId = branchCreateOperation
        });
        RequireToolSuccess(branchCreated);
        CheckedSchematicState afterBranch = await Capture();
        Assert.AreEqual(branchEnd, Find<SchematicLine>(afterBranch, branchId).End);

        string junctionId = Guid.NewGuid().ToString("D");
        string junctionCreateOperation = Guid.NewGuid().ToString("D");
        JsonElement junctionCreated = await mcp.Tool("kicad_schematic_create_connection_item", new
        {
            instanceId, documentJson,
            expectedStateJson = SchematicJson.Formatter.Format(afterBranch),
            kind = "junction", itemId = junctionId,
            xNm = At(50, 142).XNm, yNm = At(50, 142).YNm,
            operationId = junctionCreateOperation
        });
        RequireToolSuccess(junctionCreated);
        CheckedSchematicState afterJunction = await Capture();
        Assert.AreEqual(At(50, 142), Find<Junction>(afterJunction, junctionId).Position);

        string labelUpdateOperation = Guid.NewGuid().ToString("D");
        JsonElement labelUpdated = await mcp.Tool("kicad_schematic_update_connection_item", new
        {
            instanceId, documentJson, itemId = labelId,
            expectedStateJson = SchematicJson.Formatter.Format(afterJunction),
            text = "MCP_TYPED_NET_UPDATED", operationId = labelUpdateOperation,
            expectedKind = "localLabel"
        });
        RequireToolSuccess(labelUpdated);
        CheckedSchematicState afterLabelUpdate = await Capture();
        Assert.AreEqual("MCP_TYPED_NET_UPDATED", Find<LocalLabel>(afterLabelUpdate, labelId).Text.Text_);
        JsonElement labelUpdateReplay = await mcp.Tool("kicad_schematic_update_connection_item", new
        {
            instanceId, documentJson, itemId = labelId,
            expectedStateJson = SchematicJson.Formatter.Format(afterJunction),
            text = "MCP_TYPED_NET_UPDATED", operationId = labelUpdateOperation,
            expectedKind = "localLabel"
        });
        RequireToolSuccess(labelUpdateReplay);
        Assert.IsTrue(labelUpdateReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            labelUpdateReplay.GetRawText());

        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
        CheckedSchematicState reloaded = await Capture();
        Assert.AreEqual(movedStart, Find<SchematicLine>(reloaded, wireId).Start);
        Assert.AreEqual(movedEnd, Find<SchematicLine>(reloaded, wireId).End);
        Assert.AreEqual("MCP_TYPED_NET_UPDATED", Find<LocalLabel>(reloaded, labelId).Text.Text_);
        Assert.AreEqual(branchEnd, Find<SchematicLine>(reloaded, branchId).End);
        Assert.AreEqual(At(50, 142), Find<Junction>(reloaded, junctionId).Position);

        // Delete the label through MCP, then use the actual editor undo/redo
        // shortcuts. This proves the typed mutation is in KiCad's native undo
        // stack, rather than only in the checked service's state.
        string labelDeleteOperation = Guid.NewGuid().ToString("D");
        JsonElement labelDeleted = await mcp.Tool("kicad_schematic_delete_connection_item", new
        {
            instanceId, documentJson, itemId = labelId,
            expectedStateJson = SchematicJson.Formatter.Format(reloaded),
            operationId = labelDeleteOperation, expectedKind = "localLabel"
        });
        RequireToolSuccess(labelDeleted);
        CheckedSchematicState afterLabelDelete = await Capture();
        Assert.ThrowsExactly<InvalidOperationException>(() => Find<LocalLabel>(afterLabelDelete, labelId));
        JsonElement labelDeleteReplay = await mcp.Tool("kicad_schematic_delete_connection_item", new
        {
            instanceId, documentJson, itemId = labelId,
            expectedStateJson = SchematicJson.Formatter.Format(reloaded),
            operationId = labelDeleteOperation, expectedKind = "localLabel"
        });
        RequireToolSuccess(labelDeleteReplay);
        Assert.IsTrue(labelDeleteReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            labelDeleteReplay.GetRawText());
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        CheckedSchematicState undoLabel = await WaitForRevision(Capture,
            afterLabelDelete.State.Revision.Sequence, token);
        Assert.AreEqual("MCP_TYPED_NET_UPDATED", Find<LocalLabel>(undoLabel, labelId).Text.Text_);
        NativeKeyboard.SchematicShortcut(display, processId, "y");
        CheckedSchematicState redoLabel = await WaitForRevision(Capture,
            undoLabel.State.Revision.Sequence, token);
        Assert.ThrowsExactly<InvalidOperationException>(() => Find<LocalLabel>(redoLabel, labelId));

        // Remove the remaining typed objects and reload the saved file so this
        // disposable fixture leaves no generated wire or junction behind.
        string junctionDeleteOperation = Guid.NewGuid().ToString("D");
        JsonElement junctionDeleted = await mcp.Tool("kicad_schematic_delete_connection_item", new
        {
            instanceId, documentJson, itemId = junctionId,
            expectedStateJson = SchematicJson.Formatter.Format(redoLabel),
            operationId = junctionDeleteOperation, expectedKind = "junction"
        });
        RequireToolSuccess(junctionDeleted);
        CheckedSchematicState afterJunctionDelete = await Capture();
        string branchDeleteOperation = Guid.NewGuid().ToString("D");
        JsonElement branchDeleted = await mcp.Tool("kicad_schematic_delete_connection_item", new
        {
            instanceId, documentJson, itemId = branchId,
            expectedStateJson = SchematicJson.Formatter.Format(afterJunctionDelete),
            operationId = branchDeleteOperation, expectedKind = "wire"
        });
        RequireToolSuccess(branchDeleted);
        CheckedSchematicState afterBranchDelete = await Capture();
        string wireDeleteOperation = Guid.NewGuid().ToString("D");
        JsonElement wireDeleted = await mcp.Tool("kicad_schematic_delete_connection_item", new
        {
            instanceId, documentJson, itemId = wireId,
            expectedStateJson = SchematicJson.Formatter.Format(afterBranchDelete),
            operationId = wireDeleteOperation, expectedKind = "wire"
        });
        RequireToolSuccess(wireDeleted);
        CheckedSchematicState final = await Capture();
        Assert.ThrowsExactly<InvalidOperationException>(() => Find<SchematicLine>(final, wireId));
        Assert.ThrowsExactly<InvalidOperationException>(() => Find<SchematicLine>(final, branchId));
        Assert.ThrowsExactly<InvalidOperationException>(() => Find<Junction>(final, junctionId));
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
        CheckedSchematicState persistedCleanup = await Capture();
        Assert.ThrowsExactly<InvalidOperationException>(() => Find<SchematicLine>(persistedCleanup, wireId));
        Assert.ThrowsExactly<InvalidOperationException>(() => Find<SchematicLine>(persistedCleanup, branchId));
        Assert.ThrowsExactly<InvalidOperationException>(() => Find<Junction>(persistedCleanup, junctionId));

        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-connections-summary.json"),
            JsonSerializer.Serialize(new
            {
                wireId, branchId, labelId, junctionId,
                wireCreated = wireStart, wireUpdated = new { start = movedStart, end = movedEnd },
                labelText = "MCP_TYPED_NET_UPDATED",
                savedAndReloaded = true, exactReplay = true, nativeUndoRedo = true,
                cleanupPersisted = true, processEpoch = client.Epoch,
                finalRevision = persistedCleanup.State.Revision
            }, new JsonSerializerOptions { WriteIndented = true }), token);
    }
}
