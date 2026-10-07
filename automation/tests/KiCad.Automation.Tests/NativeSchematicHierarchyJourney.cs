using System.Text.Json;
using Google.Protobuf;
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
    /// Exercises the public checked hierarchy tools against the populated native
    /// editor fixture. The direct client is used only for paired observations,
    /// save/reload and history; every hierarchy mutation goes through MCP STDIO.
    /// </summary>
    private static async Task VerifyNativeSchematicHierarchy(
        NativeClient client, DocumentSpecifier document, HierarchyFixture fixture,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        string statePath = Path.Combine(evidence, instanceId + "-hierarchy-mcp-state");
        string stderr = Path.Combine(evidence, instanceId + "-hierarchy-mcp.stderr.log");
        await using var mcp = await StdioMcpFixture.StartAsync(
            SyncHarnessProcessTests.ProductionStartInfo(), statePath, stderr, token);

        var listed = await mcp.ListTools(null);
        var names = listed.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string name in new[]
        {
            "kicad_schematic_create_sheet_symbol", "kicad_schematic_read_sheet_symbol",
            "kicad_schematic_update_sheet_symbol", "kicad_schematic_delete_sheet_symbol",
            "kicad_schematic_create_sheet_pin", "kicad_schematic_read_sheet_pin",
            "kicad_schematic_update_sheet_pin", "kicad_schematic_delete_sheet_pin",
            "kicad_schematic_create_hierarchical_label", "kicad_schematic_read_hierarchical_label",
            "kicad_schematic_update_hierarchical_label", "kicad_schematic_delete_hierarchical_label"
        })
            CollectionAssert.Contains(names.ToArray(), name);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach",
            new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState,
            CheckedSchematicState>(new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        string documentJson = SchematicJson.Formatter.Format(document);
        CheckedSchematicState initial = await Capture();
        string initialJson = SchematicJson.Formatter.Format(initial);
        var root = initial.Electrical.Hierarchy.Data.Instances.Single(screen =>
            screen.Metadata?.Document?.Equals(document) == true);
        var original = root.Items.Where(item => item.Is(SheetSymbol.Descriptor))
            .Select(item => item.Unpack<SheetSymbol>())
            .Single(sheet => sheet.Id.Value == fixture.First);
        Assert.IsNotNull(original.ChildScreenId, "The repeated-sheet fixture must expose a child screen identity.");
        var childDocument = document.Clone(); childDocument.SheetPath.Path.Add(original.Id.Clone());
        Assert.IsTrue(initial.Electrical.Hierarchy.Data.Instances.Any(screen =>
            screen.Metadata?.Document?.Equals(childDocument) == true),
            "The public child target must be present in the checked hierarchy.");
        string childJson = SchematicJson.Formatter.Format(childDocument);

        // Create a third repeated instance of the already loaded shared child.
        // This covers shared/repeated identity without inventing an unobserved
        // child screen, which the typed tool deliberately refuses.
        var createdSheet = original.Clone();
        string sheetId = Guid.NewGuid().ToString("D");
        createdSheet.Id.Value = sheetId;
        createdSheet.Path = document.SheetPath.Clone();
        createdSheet.Position.XNm += 40_000_000;
        createdSheet.NameField.Text.Text_ = "MCP Channel C";
        createdSheet.PageNumber = "4";
        if (createdSheet.InstanceRecords is not null)
            foreach (var placement in createdSheet.InstanceRecords.Records)
                placement.PageNumber = createdSheet.PageNumber;
        createdSheet.Pins.Clear();
        string createOperation = Guid.NewGuid().ToString("D");
        JsonElement createdReply = await mcp.Tool("kicad_schematic_create_sheet_symbol", new
        {
            instanceId, documentJson, targetDocumentJson = documentJson,
            expectedStateJson = initialJson,
            sheetJson = SchematicJson.Formatter.Format(createdSheet), operationId = createOperation
        });
        RequireToolSuccess(createdReply);
        Assert.IsTrue(createdReply.GetProperty("structuredContent").GetProperty("mutationSubmitted").GetBoolean(),
            createdReply.GetRawText());
        Assert.AreEqual(document, ParseDocument(createdReply.GetProperty("structuredContent").GetProperty("targetDocument")));
        CheckedSchematicState afterCreate = await Capture();
        Assert.AreEqual("MCP Channel C", Sheet(afterCreate, sheetId).NameField.Text.Text_);
        Assert.AreEqual(original.ChildScreenId, Sheet(afterCreate, sheetId).ChildScreenId,
            "Repeated instances must retain the shared child-screen identity.");

        JsonElement createReplay = await mcp.Tool("kicad_schematic_create_sheet_symbol", new
        {
            instanceId, documentJson, targetDocumentJson = documentJson,
            expectedStateJson = initialJson,
            sheetJson = SchematicJson.Formatter.Format(createdSheet), operationId = createOperation
        });
        RequireToolSuccess(createReplay);
        Assert.IsTrue(createReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            createReplay.GetRawText());
        Assert.AreEqual(afterCreate, await Capture(), "A sheet create replay must not advance native state.");

        JsonElement readSheet = await mcp.Tool("kicad_schematic_read_sheet_symbol", new
        {
            instanceId, documentJson, sheetId, expectedStateJson = SchematicJson.Formatter.Format(afterCreate)
        });
        RequireToolSuccess(readSheet);
        Assert.AreEqual(sheetId, Unpack<SheetSymbol>(readSheet, "item").Id.Value);
        Assert.AreEqual(document, ParseDocument(readSheet.GetProperty("structuredContent").GetProperty("targetDocument")));

        // A complete replacement keeps the child identity and moves only the
        // requested sheet fields.
        var updatedSheet = createdSheet.Clone();
        updatedSheet.Position.YNm += 10_000_000;
        updatedSheet.NameField.Text.Text_ = "MCP Channel C Updated";
        string updateOperation = Guid.NewGuid().ToString("D");
        JsonElement updatedReply = await mcp.Tool("kicad_schematic_update_sheet_symbol", new
        {
            instanceId, documentJson, sheetId,
            expectedStateJson = SchematicJson.Formatter.Format(afterCreate),
            sheetJson = SchematicJson.Formatter.Format(updatedSheet), operationId = updateOperation
        });
        RequireToolSuccess(updatedReply);
        CheckedSchematicState afterUpdate = await Capture();
        Assert.AreEqual("MCP Channel C Updated", Sheet(afterUpdate, sheetId).NameField.Text.Text_);
        Assert.AreEqual(updatedSheet.Position, Sheet(afterUpdate, sheetId).Position);
        JsonElement updateReplay = await mcp.Tool("kicad_schematic_update_sheet_symbol", new
        {
            instanceId, documentJson, sheetId,
            expectedStateJson = SchematicJson.Formatter.Format(afterCreate),
            sheetJson = SchematicJson.Formatter.Format(updatedSheet), operationId = updateOperation
        });
        RequireToolSuccess(updateReplay);
        Assert.IsTrue(updateReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            updateReplay.GetRawText());

        // A checked replacement may move geometry and presentation fields, but
        // it cannot silently rebind the sheet to another file or child screen.
        // Such a relink needs a document-level hierarchy transaction. Verify
        // that the public MCP boundary refuses it before dispatching anything.
        var relinkedSheet = updatedSheet.Clone();
        relinkedSheet.FilenameField.Text.Text_ = "mcp-other-child.kicad_sch";
        JsonElement relinkRefused = await mcp.Tool("kicad_schematic_update_sheet_symbol", new
        {
            instanceId, documentJson, sheetId,
            expectedStateJson = SchematicJson.Formatter.Format(afterUpdate),
            sheetJson = SchematicJson.Formatter.Format(relinkedSheet),
            operationId = Guid.NewGuid().ToString("D")
        });
        Assert.IsTrue(relinkRefused.GetProperty("isError").GetBoolean(), relinkRefused.GetRawText());
        Assert.AreEqual("sheet_relink_requires_hierarchy_transaction",
            relinkRefused.GetProperty("structuredContent").GetProperty("errorCode").GetString(),
            relinkRefused.GetRawText());
        Assert.AreEqual(afterUpdate, await Capture(),
            "A refused relink must leave the native hierarchy unchanged.");

        // Add, read, update and delete a nested sheet pin. Each call replaces
        // the parent atomically, preserving the other SheetSymbol properties.
        string pinId = Guid.NewGuid().ToString("D");
        var pin = new SheetPin
        {
            Id = new() { Value = pinId },
            Position = new() { XNm = updatedSheet.Position.XNm + updatedSheet.Size.XNm, YNm = updatedSheet.Position.YNm + 5_000_000 },
            Text = new() { Text_ = "MCP_DATA" },
            SpinStyle = SchematicLabelSpinStyle.SlssLeft, Shape = SchematicLabelShape.SlshPassive,
            Side = SheetSide.ShsRight, Locked = LockedState.LsUnlocked
        };
        string pinCreateOperation = Guid.NewGuid().ToString("D");
        JsonElement pinCreated = await mcp.Tool("kicad_schematic_create_sheet_pin", new
        {
            instanceId, documentJson, parentSheetId = sheetId,
            expectedStateJson = SchematicJson.Formatter.Format(afterUpdate),
            pinJson = SchematicJson.Formatter.Format(pin), operationId = pinCreateOperation
        });
        RequireToolSuccess(pinCreated);
        CheckedSchematicState afterPinCreate = await Capture();
        Assert.AreEqual("MCP_DATA", Sheet(afterPinCreate, sheetId).Pins.Single().Text.Text_);
        JsonElement pinCreateReplay = await mcp.Tool("kicad_schematic_create_sheet_pin", new
        {
            instanceId, documentJson, parentSheetId = sheetId,
            expectedStateJson = SchematicJson.Formatter.Format(afterUpdate),
            pinJson = SchematicJson.Formatter.Format(pin), operationId = pinCreateOperation
        });
        RequireToolSuccess(pinCreateReplay);
        Assert.IsTrue(pinCreateReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            pinCreateReplay.GetRawText());

        JsonElement readPin = await mcp.Tool("kicad_schematic_read_sheet_pin", new
        {
            instanceId, documentJson, parentSheetId = sheetId, pinId,
            expectedStateJson = SchematicJson.Formatter.Format(afterPinCreate)
        });
        RequireToolSuccess(readPin);
        Assert.AreEqual(pinId, Unpack<SheetPin>(readPin, "item").Id.Value);
        var updatedPin = pin.Clone(); updatedPin.Text.Text_ = "MCP_DATA_UPDATED";
        string pinUpdateOperation = Guid.NewGuid().ToString("D");
        JsonElement pinUpdated = await mcp.Tool("kicad_schematic_update_sheet_pin", new
        {
            instanceId, documentJson, parentSheetId = sheetId, pinId,
            expectedStateJson = SchematicJson.Formatter.Format(afterPinCreate),
            pinJson = SchematicJson.Formatter.Format(updatedPin), operationId = pinUpdateOperation
        });
        RequireToolSuccess(pinUpdated);
        CheckedSchematicState afterPinUpdate = await Capture();
        Assert.AreEqual("MCP_DATA_UPDATED", Sheet(afterPinUpdate, sheetId).Pins.Single().Text.Text_);
        JsonElement pinUpdateReplay = await mcp.Tool("kicad_schematic_update_sheet_pin", new
        {
            instanceId, documentJson, parentSheetId = sheetId, pinId,
            expectedStateJson = SchematicJson.Formatter.Format(afterPinCreate),
            pinJson = SchematicJson.Formatter.Format(updatedPin), operationId = pinUpdateOperation
        });
        RequireToolSuccess(pinUpdateReplay);
        Assert.IsTrue(pinUpdateReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            pinUpdateReplay.GetRawText());

        string pinDeleteOperation = Guid.NewGuid().ToString("D");
        JsonElement pinDeleted = await mcp.Tool("kicad_schematic_delete_sheet_pin", new
        {
            instanceId, documentJson, parentSheetId = sheetId, pinId,
            expectedStateJson = SchematicJson.Formatter.Format(afterPinUpdate), operationId = pinDeleteOperation
        });
        RequireToolSuccess(pinDeleted);
        CheckedSchematicState afterPinDelete = await Capture();
        Assert.IsEmpty(Sheet(afterPinDelete, sheetId).Pins);
        JsonElement pinDeleteReplay = await mcp.Tool("kicad_schematic_delete_sheet_pin", new
        {
            instanceId, documentJson, parentSheetId = sheetId, pinId,
            expectedStateJson = SchematicJson.Formatter.Format(afterPinUpdate), operationId = pinDeleteOperation
        });
        RequireToolSuccess(pinDeleteReplay);
        Assert.IsTrue(pinDeleteReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            pinDeleteReplay.GetRawText());

        // Create and update a hierarchical endpoint on a child document while
        // the root remains the checked mutation document. The response target
        // must carry the child path explicitly.
        string labelId = Guid.NewGuid().ToString("D");
        var label = new HierarchicalLabel
        {
            Id = new() { Value = labelId }, Position = new() { XNm = 30_000, YNm = 30_000 },
            Text = new() { Text_ = "MCP_HIER" }, SpinStyle = SchematicLabelSpinStyle.SlssRight,
            Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked,
            FieldsAutoplaced = true
        };
        string labelCreateOperation = Guid.NewGuid().ToString("D");
        JsonElement labelCreated = await mcp.Tool("kicad_schematic_create_hierarchical_label", new
        {
            instanceId, documentJson, targetDocumentJson = childJson,
            expectedStateJson = SchematicJson.Formatter.Format(afterPinDelete),
            labelJson = SchematicJson.Formatter.Format(label), operationId = labelCreateOperation
        });
        RequireToolSuccess(labelCreated);
        Assert.AreEqual(childDocument, ParseDocument(labelCreated.GetProperty("structuredContent").GetProperty("targetDocument")));
        CheckedSchematicState afterLabelCreate = await Capture();
        Assert.AreEqual("MCP_HIER", Label(afterLabelCreate, childDocument, labelId).Text.Text_);
        JsonElement labelCreateReplay = await mcp.Tool("kicad_schematic_create_hierarchical_label", new
        {
            instanceId, documentJson, targetDocumentJson = childJson,
            expectedStateJson = SchematicJson.Formatter.Format(afterPinDelete),
            labelJson = SchematicJson.Formatter.Format(label), operationId = labelCreateOperation
        });
        RequireToolSuccess(labelCreateReplay);
        Assert.IsTrue(labelCreateReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            labelCreateReplay.GetRawText());
        AssertTargetDocument(labelCreateReplay, childDocument);
        JsonElement labelRead = await mcp.Tool("kicad_schematic_read_hierarchical_label", new
        {
            instanceId, documentJson, labelId,
            expectedStateJson = SchematicJson.Formatter.Format(afterLabelCreate),
            targetDocumentJson = childJson
        });
        RequireToolSuccess(labelRead);
        Assert.AreEqual(labelId, Unpack<HierarchicalLabel>(labelRead, "item").Id.Value);
        AssertTargetDocument(labelRead, childDocument);
        var updatedLabel = label.Clone(); updatedLabel.Text.Text_ = "MCP_HIER_UPDATED";
        string labelUpdateOperation = Guid.NewGuid().ToString("D");
        JsonElement labelUpdated = await mcp.Tool("kicad_schematic_update_hierarchical_label", new
        {
            instanceId, documentJson, labelId,
            expectedStateJson = SchematicJson.Formatter.Format(afterLabelCreate),
            labelJson = SchematicJson.Formatter.Format(updatedLabel), operationId = labelUpdateOperation,
            targetDocumentJson = childJson
        });
        RequireToolSuccess(labelUpdated);
        AssertTargetDocument(labelUpdated, childDocument);
        CheckedSchematicState afterLabelUpdate = await Capture();
        Assert.AreEqual("MCP_HIER_UPDATED", Label(afterLabelUpdate, childDocument, labelId).Text.Text_);
        JsonElement labelUpdateReplay = await mcp.Tool("kicad_schematic_update_hierarchical_label", new
        {
            instanceId, documentJson, labelId,
            expectedStateJson = SchematicJson.Formatter.Format(afterLabelCreate),
            labelJson = SchematicJson.Formatter.Format(updatedLabel), operationId = labelUpdateOperation,
            targetDocumentJson = childJson
        });
        RequireToolSuccess(labelUpdateReplay);
        Assert.IsTrue(labelUpdateReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            labelUpdateReplay.GetRawText());
        AssertTargetDocument(labelUpdateReplay, childDocument);

        // Persistence is observed at the native boundary before exercising
        // undo/redo. A stale checkpoint from before the update must be refused.
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
        CheckedSchematicState reloaded = await Capture();
        Assert.AreEqual("MCP Channel C Updated", Sheet(reloaded, sheetId).NameField.Text.Text_);
        Assert.AreEqual("MCP_HIER_UPDATED", Label(reloaded, childDocument, labelId).Text.Text_);
        JsonElement staleRead = await mcp.Tool("kicad_schematic_read_sheet_symbol", new
        {
            instanceId, documentJson, sheetId, expectedStateJson = SchematicJson.Formatter.Format(afterCreate)
        });
        Assert.IsTrue(staleRead.GetProperty("isError").GetBoolean(), staleRead.GetRawText());
        Assert.AreEqual("stale_document_state", staleRead.GetProperty("structuredContent")
            .GetProperty("errorCode").GetString(), staleRead.GetRawText());

        string labelDeleteOperation = Guid.NewGuid().ToString("D");
        JsonElement labelDeleted = await mcp.Tool("kicad_schematic_delete_hierarchical_label", new
        {
            instanceId, documentJson, labelId,
            expectedStateJson = SchematicJson.Formatter.Format(reloaded), operationId = labelDeleteOperation,
            targetDocumentJson = childJson
        });
        RequireToolSuccess(labelDeleted);
        AssertTargetDocument(labelDeleted, childDocument);
        CheckedSchematicState afterLabelDelete = await Capture();
        Assert.ThrowsExactly<InvalidOperationException>(() => Label(afterLabelDelete, childDocument, labelId));
        JsonElement labelDeleteReplay = await mcp.Tool("kicad_schematic_delete_hierarchical_label", new
        {
            instanceId, documentJson, labelId,
            expectedStateJson = SchematicJson.Formatter.Format(reloaded), operationId = labelDeleteOperation,
            targetDocumentJson = childJson
        });
        RequireToolSuccess(labelDeleteReplay);
        Assert.IsTrue(labelDeleteReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            labelDeleteReplay.GetRawText());
        AssertTargetDocument(labelDeleteReplay, childDocument);

        NativeKeyboard.SchematicShortcut(display, processId, "z");
        CheckedSchematicState undoLabel = await WaitForRevision(Capture,
            afterLabelDelete.State.Revision.Sequence, token);
        Assert.AreEqual("MCP_HIER_UPDATED", Label(undoLabel, childDocument, labelId).Text.Text_);
        NativeKeyboard.SchematicShortcut(display, processId, "y");
        CheckedSchematicState redoLabel = await WaitForRevision(Capture,
            undoLabel.State.Revision.Sequence, token);
        Assert.ThrowsExactly<InvalidOperationException>(() => Label(redoLabel, childDocument, labelId));

        string sheetDeleteOperation = Guid.NewGuid().ToString("D");
        JsonElement sheetDeleted = await mcp.Tool("kicad_schematic_delete_sheet_symbol", new
        {
            instanceId, documentJson, sheetId,
            expectedStateJson = SchematicJson.Formatter.Format(redoLabel), operationId = sheetDeleteOperation
        });
        RequireToolSuccess(sheetDeleted);
        CheckedSchematicState afterSheetDelete = await Capture();
        Assert.ThrowsExactly<InvalidOperationException>(() => Sheet(afterSheetDelete, sheetId));
        JsonElement sheetDeleteReplay = await mcp.Tool("kicad_schematic_delete_sheet_symbol", new
        {
            instanceId, documentJson, sheetId,
            expectedStateJson = SchematicJson.Formatter.Format(redoLabel), operationId = sheetDeleteOperation
        });
        RequireToolSuccess(sheetDeleteReplay);
        Assert.IsTrue(sheetDeleteReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
            sheetDeleteReplay.GetRawText());
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
        CheckedSchematicState final = await Capture();
        Assert.ThrowsExactly<InvalidOperationException>(() => Sheet(final, sheetId));
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-hierarchy-summary.json"),
            JsonSerializer.Serialize(new
            {
                createdSheetId = sheetId, createdPinId = pinId, createdLabelId = labelId,
                rootDocument = documentJson, childDocument = childJson,
                savedAndReloaded = true, replayed = true, undoRedo = true
            }, new JsonSerializerOptions { WriteIndented = true }), token);
    }

    private static SheetSymbol Sheet(CheckedSchematicState state, string id) =>
        state.Electrical.Hierarchy.Data.Instances.SelectMany(screen => screen.Items)
            .Where(item => item.Is(SheetSymbol.Descriptor)).Select(item => item.Unpack<SheetSymbol>())
            .Single(sheet => sheet.Id.Value == id);

    private static HierarchicalLabel Label(CheckedSchematicState state, DocumentSpecifier document, string id) =>
        state.Electrical.Hierarchy.Data.Instances.Where(screen => screen.Metadata?.Document?.Equals(document) == true)
            .SelectMany(screen => screen.Items).Where(item => item.Is(HierarchicalLabel.Descriptor))
            .Select(item => item.Unpack<HierarchicalLabel>()).Single(label => label.Id.Value == id);

    private static T Unpack<T>(JsonElement result, string property) where T : class, IMessage<T>, new()
    {
        Any packed = SchematicJson.Parser.Parse<Any>(result.GetProperty("structuredContent")
            .GetProperty(property).GetRawText());
        return packed.Unpack<T>();
    }

    private static DocumentSpecifier ParseDocument(JsonElement json) =>
        SchematicJson.Parser.Parse<DocumentSpecifier>(json.GetRawText());

    private static void AssertTargetDocument(JsonElement result, DocumentSpecifier expected)
    {
        Assert.AreEqual(expected, ParseDocument(result.GetProperty("structuredContent")
            .GetProperty("targetDocument")), result.GetRawText());
    }
}
