using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // The typed symbol-instance MCP tools are qualified through the same disposable
    // schematic and native checked-state admission as the generic CRUD journey. The
    // complete symbol payload comes from KiCad's own hierarchy observation; this test
    // only changes its identity, placement and reference to create a second unconnected
    // instance, then exercises read/update/delete and exact operation replay.
    private static async Task VerifyTypedSymbolMutations(StdioMcpFixture mcp, NativeClient client,
        DocumentSpecifier document, string evidence, string instanceId, CancellationToken token)
    {
        string documentJson = SchematicJson.Formatter.Format(document);

        async Task<CheckedSchematicState> Observe()
        {
            var reply = await mcp.Tool("kicad_schematic_checked_state", new { instanceId, documentJson });
            RequireToolSuccess(reply);
            return SchematicJson.Parser.Parse<CheckedSchematicState>(reply.GetProperty("structuredContent").GetRawText());
        }

        static SchematicSymbolInstance Symbol(CheckedSchematicState state, DocumentSpecifier document, string id) =>
            state.Electrical.Hierarchy.Data.Instances
                .Single(screen => Equals(screen.Metadata?.Document, document)).Items
                .Where(item => item.Is(SchematicSymbolInstance.Descriptor))
                .Select(item => item.Unpack<SchematicSymbolInstance>())
                .Single(symbol => symbol.Id.Value == id);

        static CheckedSchematicBatchReceipt Receipt(JsonElement reply) =>
            SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(
                reply.GetProperty("structuredContent").GetProperty("receipt").GetRawText());

        var before = await Observe();
        var existing = before.Electrical.Hierarchy.Data.Instances
            .Single(screen => Equals(screen.Metadata?.Document, document)).Items
            .Where(item => item.Is(SchematicSymbolInstance.Descriptor))
            .Select(item => item.Unpack<SchematicSymbolInstance>())
            .OrderBy(symbol => symbol.Id.Value, StringComparer.Ordinal)
            .First();
        string existingId = existing.Id.Value;

        var initialRead = await mcp.Tool("kicad_schematic_read_symbol_instance", new
        {
            instanceId, documentJson, itemId = existingId,
            expectedStateJson = SchematicJson.Formatter.Format(before)
        });
        RequireToolSuccess(initialRead);
        var initialReadData = initialRead.GetProperty("structuredContent");
        Assert.AreEqual(existingId, initialReadData.GetProperty("itemId").GetString());
        Assert.AreEqual(existing, SchematicJson.Parser.Parse<SchematicSymbolInstance>(
            initialReadData.GetProperty("symbol").GetRawText()));

        const string reference = "MCP1";
        string createdId = Guid.NewGuid().ToString("D");
        long shiftX = 40_000_000L;
        var created = existing.Clone();
        created.Id = new KIID { Value = createdId };
        created.Position.XNm += shiftX;
        created.ReferenceField.Text.Text_ = reference;
        created.ReferenceField.Text.Position.XNm += shiftX;
        created.ValueField.Text.Position.XNm += shiftX;
        foreach (var field in created.UserFields)
            field.Text.Position.XNm += shiftX;
        created.Path = document.SheetPath.Clone();
        var record = created.InstanceRecords.Records.Single();
        record.Path.Clear();
        record.Path.Add(document.SheetPath.Path.Select(path => path.Clone()));
        record.Reference = reference;

        string createOperation = Guid.NewGuid().ToString("D");
        var createArguments = new
        {
            instanceId, documentJson,
            expectedStateJson = SchematicJson.Formatter.Format(before),
            symbolJson = SchematicJson.Formatter.Format(created), operationId = createOperation
        };
        var create = await mcp.Tool("kicad_schematic_create_symbol_instance", createArguments);
        RequireToolSuccess(create);
        var createReceipt = Receipt(create);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, createReceipt.Status);
        var createdNative = createReceipt.Result.Items.Single().Unpack<SchematicSymbolInstance>();
        Assert.AreEqual(createdId, createdNative.Id.Value);
        Assert.AreEqual(reference, createdNative.ReferenceField.Text.Text_);

        // A duplicate request after the native revision changed must return the
        // retained receipt without dispatching another mutation.
        var createReplay = await mcp.Tool("kicad_schematic_create_symbol_instance", createArguments);
        RequireToolSuccess(createReplay);
        Assert.IsTrue(createReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean());
        Assert.AreEqual(createReceipt, Receipt(createReplay));

        var afterCreate = await Observe();
        Assert.AreEqual(createdId, Symbol(afterCreate, document, createdId).Id.Value);
        var createdRead = await mcp.Tool("kicad_schematic_read_symbol_instance", new
        {
            instanceId, documentJson, itemId = createdId,
            expectedStateJson = SchematicJson.Formatter.Format(afterCreate)
        });
        RequireToolSuccess(createdRead);
        Assert.AreEqual(createdId, createdRead.GetProperty("structuredContent").GetProperty("itemId").GetString());

        var updated = Symbol(afterCreate, document, createdId);
        updated.ValueField.Text.Text_ = "Probe MCP updated";
        string updateOperation = Guid.NewGuid().ToString("D");
        var updateArguments = new
        {
            instanceId, documentJson, itemId = createdId,
            expectedStateJson = SchematicJson.Formatter.Format(afterCreate),
            symbolJson = SchematicJson.Formatter.Format(updated), operationId = updateOperation
        };
        var update = await mcp.Tool("kicad_schematic_update_symbol_instance", updateArguments);
        RequireToolSuccess(update);
        var updateReceipt = Receipt(update);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, updateReceipt.Status);
        Assert.AreEqual("Probe MCP updated", updateReceipt.Result.Items.Single()
            .Unpack<SchematicSymbolInstance>().ValueField.Text.Text_);
        var updateReplay = await mcp.Tool("kicad_schematic_update_symbol_instance", updateArguments);
        RequireToolSuccess(updateReplay);
        Assert.IsTrue(updateReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean());
        Assert.AreEqual(updateReceipt, Receipt(updateReplay));

        var afterUpdate = await Observe();
        Assert.AreEqual("Probe MCP updated", Symbol(afterUpdate, document, createdId).ValueField.Text.Text_);
        var delete = await mcp.Tool("kicad_schematic_delete_symbol_instance", new
        {
            instanceId, documentJson, itemId = createdId,
            expectedStateJson = SchematicJson.Formatter.Format(afterUpdate),
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(delete);
        var deleteReceipt = Receipt(delete);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, deleteReceipt.Status);
        CollectionAssert.Contains(deleteReceipt.Result.Removed.Select(id => id.Value).ToArray(), createdId);

        var afterDelete = await Observe();
        Assert.ThrowsExactly<InvalidOperationException>(() => Symbol(afterDelete, document, createdId));
        Assert.AreEqual(existing, Symbol(afterDelete, document, existingId),
            "Typed symbol CRUD must leave the pre-existing fixture symbol unchanged.");

        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-typed-symbol-crud.json"),
            JsonSerializer.Serialize(new
            {
                existingId, createdId, createOperation, updateOperation,
                createReceipt = SchematicJson.Formatter.Format(createReceipt),
                updateReceipt = SchematicJson.Formatter.Format(updateReceipt),
                deleteReceipt = SchematicJson.Formatter.Format(deleteReceipt)
            }), token);
    }
}
