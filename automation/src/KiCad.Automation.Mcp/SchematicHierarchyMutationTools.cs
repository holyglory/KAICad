using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Typed authoring for the objects that connect schematic hierarchy levels.
/// Sheet pins are persisted inside their owning <see cref="SheetSymbol"/>;
/// updating one therefore submits a complete parent replacement so no other
/// pin or sheet property is silently discarded.
/// </summary>
[McpServerToolType]
public sealed class SchematicHierarchyMutationTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_create_sheet_symbol", ReadOnly = false,
        UseStructuredContent = true),
     Description("Create one complete hierarchical sheet symbol on the explicitly targeted loaded parent sheet. documentJson identifies the schematic root, targetDocumentJson identifies the parent sheet instance, expectedStateJson is the complete checked state returned by kicad_schematic_checked_state, and sheetJson is a complete SheetSymbol with a new UUID, childScreenId, filename, path and fields. Existing loaded child screens may be referenced, including repeated/shared instances; creating an unseen child requires a document-level hierarchy transaction and is refused. The checked native receipt makes an identical operationId retry safe after a lost reply. The edit remains unsaved until kicad_document_save is called."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and target sheet, complete checked hierarchy, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> CreateSheetSymbol(string instanceId, string documentJson,
        string targetDocumentJson, string expectedStateJson, string sheetJson, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, targetDocumentJson,
            expectedStateJson, sheetJson, null, null, operationId, Mutation.CreateSheet,
            cancellationToken);

    [McpServerTool(Name = "kicad_schematic_read_sheet_symbol", ReadOnly = true,
        UseStructuredContent = true),
     Description("Read one exact SheetSymbol from the checked native hierarchy by UUID. The complete checked state, process epoch, native identity, revision and file baselines must still describe the same root; a changed hierarchy is rejected and no native edit occurs. The response includes the owning parent sheet and complete sheet-pin list."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and sheet UUID, complete checked hierarchy and process epoch"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> ReadSheetSymbol(string instanceId, string documentJson,
        string sheetId, string expectedStateJson, CancellationToken cancellationToken) =>
        ExecuteRead(instanceId, documentJson, expectedStateJson, sheetId, null, ReadKind.Sheet,
            cancellationToken);

    [McpServerTool(Name = "kicad_schematic_update_sheet_symbol", ReadOnly = false,
        UseStructuredContent = true),
     Description("Replace one complete SheetSymbol while retaining the explicit sheetId, parent sheet, child-screen identity and filename binding. Supply the exact checked state and a new operationId. Rebinding a sheet to another child or moving it to another parent requires an explicit hierarchy transaction and is refused. The edit is one native undo step and is unsaved until kicad_document_save."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and sheet UUID, complete replacement, checked hierarchy, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> UpdateSheetSymbol(string instanceId, string documentJson,
        string sheetId, string expectedStateJson, string sheetJson, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, null,
            expectedStateJson, sheetJson, sheetId, null, operationId, Mutation.UpdateSheet,
            cancellationToken);

    [McpServerTool(Name = "kicad_schematic_delete_sheet_symbol", ReadOnly = false,
        UseStructuredContent = true),
     Description("Delete one explicitly identified SheetSymbol from its checked parent sheet. The complete hierarchy checkpoint and child-screen identity are checked before dispatch; native rejection leaves the hierarchy unchanged. Reuse the same operationId only with the same request after a lost reply. The edit is unsaved until kicad_document_save."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and sheet UUID, checked hierarchy, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> DeleteSheetSymbol(string instanceId, string documentJson,
        string sheetId, string expectedStateJson, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, null,
            expectedStateJson, null, sheetId, null, operationId, Mutation.DeleteSheet,
            cancellationToken);

    [McpServerTool(Name = "kicad_schematic_create_sheet_pin", ReadOnly = false,
        UseStructuredContent = true),
     Description("Add one typed SheetPin to an existing SheetSymbol. parentSheetId identifies the exact parent object in the checked hierarchy; pinJson is a complete SheetPin with a new UUID, text, position, side and electrical shape. The parent SheetSymbol is replaced atomically, preserving every other pin and field. The operation is checked, replayable and unsaved until kicad_document_save."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and parent sheet UUID, complete checked hierarchy, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> CreateSheetPin(string instanceId, string documentJson,
        string parentSheetId, string expectedStateJson, string pinJson, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, null,
            expectedStateJson, pinJson, null, parentSheetId, operationId, Mutation.CreatePin,
            cancellationToken);

    [McpServerTool(Name = "kicad_schematic_read_sheet_pin", ReadOnly = true,
        UseStructuredContent = true),
     Description("Read one exact SheetPin nested in a SheetSymbol from the checked hierarchy. Supply the parent sheet UUID and pin UUID. The response includes the complete pin, owning SheetSymbol and parent sheet document; it refuses stale state before returning data."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root, parent sheet UUID and pin UUID, complete checked hierarchy and process epoch"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> ReadSheetPin(string instanceId, string documentJson,
        string parentSheetId, string pinId, string expectedStateJson,
        CancellationToken cancellationToken) => ExecuteRead(instanceId, documentJson,
            expectedStateJson, pinId, parentSheetId, ReadKind.Pin, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_update_sheet_pin", ReadOnly = false,
        UseStructuredContent = true),
     Description("Replace one complete SheetPin identified by pinId inside its explicitly identified parent SheetSymbol. Its UUID and parent remain fixed; the checked batch preserves all sibling pins and sheet properties, and exact operation replay is supported. The edit is unsaved until kicad_document_save."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root, parent sheet UUID and pin UUID, complete replacement, checked hierarchy, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> UpdateSheetPin(string instanceId, string documentJson,
        string parentSheetId, string pinId, string expectedStateJson, string pinJson, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, null,
            expectedStateJson, pinJson, pinId, parentSheetId, operationId, Mutation.UpdatePin,
            cancellationToken);

    [McpServerTool(Name = "kicad_schematic_delete_sheet_pin", ReadOnly = false,
        UseStructuredContent = true),
     Description("Delete one SheetPin from the explicitly identified parent SheetSymbol. The complete checked hierarchy and parent UUID are admitted before one atomic parent replacement; stale, rejected and indeterminate receipts remain errors and do not claim a deletion. The edit is unsaved until kicad_document_save."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root, parent sheet UUID and pin UUID, checked hierarchy, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> DeleteSheetPin(string instanceId, string documentJson,
        string parentSheetId, string pinId, string expectedStateJson, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, null,
            expectedStateJson, null, pinId, parentSheetId, operationId, Mutation.DeletePin,
            cancellationToken);

    [McpServerTool(Name = "kicad_schematic_create_hierarchical_label", ReadOnly = false,
        UseStructuredContent = true),
     Description("Create a typed HierarchicalLabel on an explicitly targeted loaded child or root sheet. labelJson carries a new UUID, text, position, shape, spin and fields; targetDocumentJson binds the label to its physical sheet. The checked batch is atomic, replayable by operationId and unsaved until kicad_document_save."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and target sheet, complete checked hierarchy, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> CreateHierarchicalLabel(string instanceId, string documentJson,
        string targetDocumentJson, string expectedStateJson, string labelJson, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, targetDocumentJson,
            expectedStateJson, labelJson, null, null, operationId, Mutation.CreateLabel,
            cancellationToken);

    [McpServerTool(Name = "kicad_schematic_read_hierarchical_label", ReadOnly = true,
        UseStructuredContent = true),
     Description("Read one exact HierarchicalLabel by UUID from the checked hierarchy, including its owning sheet. A complete state checkpoint is required and stale observations are refused."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and label UUID, complete checked hierarchy and process epoch"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> ReadHierarchicalLabel(string instanceId, string documentJson,
        string labelId, string expectedStateJson, CancellationToken cancellationToken) =>
        ExecuteRead(instanceId, documentJson, expectedStateJson, labelId, null, ReadKind.Label,
            cancellationToken);

    [McpServerTool(Name = "kicad_schematic_update_hierarchical_label", ReadOnly = false,
        UseStructuredContent = true),
     Description("Replace one complete HierarchicalLabel identified by labelId while retaining its UUID and owning sheet. The checked state, native receipt and operationId prevent stale or duplicate edits; the result remains unsaved until kicad_document_save."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and label UUID, complete replacement, checked hierarchy, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> UpdateHierarchicalLabel(string instanceId, string documentJson,
        string labelId, string expectedStateJson, string labelJson, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, null,
            expectedStateJson, labelJson, labelId, null, operationId, Mutation.UpdateLabel,
            cancellationToken);

    [McpServerTool(Name = "kicad_schematic_delete_hierarchical_label", ReadOnly = false,
        UseStructuredContent = true),
     Description("Delete one explicitly identified HierarchicalLabel from its checked sheet. The operation is atomic, revision guarded and replayable; use kicad_document_save to persist it."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and label UUID, checked hierarchy, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TypedSchematicHierarchyAuthoringPersistsThroughMcp")]
    public Task<CallToolResult> DeleteHierarchicalLabel(string instanceId, string documentJson,
        string labelId, string expectedStateJson, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, null,
            expectedStateJson, labelId, null, null, operationId, Mutation.DeleteLabel,
            cancellationToken);

    private enum Mutation { CreateSheet, UpdateSheet, DeleteSheet, CreatePin, UpdatePin, DeletePin, CreateLabel, UpdateLabel, DeleteLabel }
    private enum ReadKind { Sheet, Pin, Label }

    private sealed record ExpectedObservation(DocumentLifecycleState State, SchematicElectricalState Electrical);
    private sealed record Located(Any Packed, IMessage Item, DocumentSpecifier Document, string? ParentSheetId = null);
    private sealed record Plan(Any? ReplyItem, DocumentSpecifier Target, SchematicItemOperation Operation,
        string ItemId, string? ParentSheetId);

    private async Task<CallToolResult> Execute(string instanceId, string documentJson,
        string? targetDocumentJson, string expectedStateJson, string? payloadJson, string? itemId,
        string? parentSheetId, string operationId, Mutation mutation, CancellationToken token)
    {
        bool submitted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            DocumentSpecifier root = ParseRoot(documentJson);
            DocumentSpecifier? requestedTarget = targetDocumentJson is null ? null
                : ParseSheet(targetDocumentJson, "targetDocumentJson");
            ExpectedObservation expected = ParseExpected(expectedStateJson, root);
            ValidateIdentity(operationId, "operationId");
            if (itemId is not null) ValidateIdentity(itemId, "itemId");
            if (parentSheetId is not null) ValidateIdentity(parentSheetId, "parentSheetId");
            Plan plan = BuildPlan(expected, requestedTarget, payloadJson, itemId, parentSheetId, operationId, mutation);
            NativeClient client = registry.Client(instanceId);
            AutomationSession session = await client.HandshakeAsync(token);
            if (expected.State.ProcessEpoch != session.Epoch)
                throw new AutomationException("stale_process_epoch", "The checked state belongs to another native process epoch.");
            CheckedSchematicBatch request = new()
            {
                Batch = new ApplySchematicItemBatch
                {
                    Document = root.Clone(), DocumentEpoch = expected.State.Revision.Epoch,
                    ExpectedRevision = expected.State.Revision.Clone(), OperationId = operationId,
                    Description = Description(mutation)
                }, ExpectedState = expected.State.Clone()
            };
            request.Batch.Operations.Add(plan.Operation);
            CheckedSchematicTools.ValidateRequest(request, session.Epoch);

            // Receipt inspection must precede a new native capture: a completed edit has
            // advanced the revision, but an exact replay is still safe and deterministic.
            CheckedSchematicBatchReceipt prior = await client.InvokeAsync<ReadCheckedSchematicBatchReceipt,
                CheckedSchematicBatchReceipt>(new()
                {
                    Document = root.Clone(), ProcessEpoch = session.Epoch,
                    OperationId = operationId, ExpectedRequest = request.Clone()
                }, token);
            CheckedSchematicTools.ValidateResult(request, prior, inspect: true);
            if (prior.Status != CheckedSchematicBatchStatus.CsbsNotFound)
                return Reply(instanceId, session.Epoch, root, plan, mutation, prior,
                    submitted: false, replayed: true);

            CheckedSchematicState observed = await client.InvokeAsync<ReadCheckedSchematicState,
                CheckedSchematicState>(new() { Document = root.Clone(), ProcessEpoch = session.Epoch }, token);
            CheckedSchematicContract.ValidateObservation(observed, root, session.Epoch);
            if (!observed.State.Equals(expected.State) || !observed.Electrical.Equals(expected.Electrical))
                throw new AutomationException("stale_document_state",
                    "The schematic changed after the checked observation; inspect a fresh checked state and retry.");
            token.ThrowIfCancellationRequested();
            submitted = true;
            CheckedSchematicBatchReceipt receipt = await client.InvokeAsync<CheckedSchematicBatch,
                CheckedSchematicBatchReceipt>(request, token);
            CheckedSchematicTools.ValidateResult(request, receipt, inspect: false);
            return Reply(instanceId, session.Epoch, root, plan, mutation, receipt,
                submitted: true, replayed: false);
        }
        catch (Exception error) when (error is AutomationException or NativeApiException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException
            or JsonException or NngException)
        {
            string code = error switch
            {
                AutomationException known => known.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode,
                _ => "invalid_schematic_hierarchy_item"
            };
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, operation = mutation.ToString(), itemId, parentSheetId, operationId,
                mutationSubmitted = submitted,
                outcome = submitted ? "not_confirmed" : "not_submitted",
                errorCode = code, errorMessage = error.Message
            });
            return new() { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private async Task<CallToolResult> ExecuteRead(string instanceId, string documentJson,
        string expectedStateJson, string itemId, string? parentSheetId, ReadKind kind, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            DocumentSpecifier root = ParseRoot(documentJson);
            ExpectedObservation expected = ParseExpected(expectedStateJson, root);
            ValidateIdentity(itemId, "itemId");
            if (parentSheetId is not null) ValidateIdentity(parentSheetId, "parentSheetId");
            Located located = Find(expected.Electrical, itemId, parentSheetId, kind);
            NativeClient client = registry.Client(instanceId);
            AutomationSession session = await client.HandshakeAsync(token);
            if (expected.State.ProcessEpoch != session.Epoch)
                throw new AutomationException("stale_process_epoch", "The checked state belongs to another native process epoch.");
            CheckedSchematicState observed = await client.InvokeAsync<ReadCheckedSchematicState,
                CheckedSchematicState>(new() { Document = root.Clone(), ProcessEpoch = session.Epoch }, token);
            CheckedSchematicContract.ValidateObservation(observed, root, session.Epoch);
            if (!observed.State.Equals(expected.State) || !observed.Electrical.Equals(expected.Electrical))
                throw new AutomationException("stale_document_state",
                    "The schematic changed after the checked observation; inspect a fresh checked state and retry.");
            return ReadReply(instanceId, session.Epoch, root, located, kind);
        }
        catch (Exception error) when (error is AutomationException or NativeApiException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException
            or JsonException or NngException)
        {
            string code = error switch
            {
                AutomationException known => known.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode,
                _ => "invalid_schematic_hierarchy_item"
            };
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, operation = "read", itemId, parentSheetId,
                mutationSubmitted = false, outcome = "not_submitted",
                errorCode = code, errorMessage = error.Message
            });
            return new() { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private static Plan BuildPlan(ExpectedObservation expected, DocumentSpecifier? requestedTarget,
        string? payloadJson, string? itemId, string? parentSheetId, string operationId, Mutation mutation)
    {
        var hierarchy = expected.Electrical.Hierarchy.Data;
        Located? source = itemId is null ? null : Find(hierarchy, itemId, parentSheetId,
            mutation is Mutation.DeletePin or Mutation.UpdatePin or Mutation.CreatePin ? ReadKind.Pin
                : mutation is Mutation.DeleteLabel or Mutation.UpdateLabel or Mutation.CreateLabel ? ReadKind.Label : ReadKind.Sheet);
        DocumentSpecifier target;
        string id;
        switch (mutation)
        {
            case Mutation.CreateSheet:
                target = RequireTarget(requestedTarget, hierarchy, "targetDocumentJson");
                var newSheet = Parse<SheetSymbol>(payloadJson, "invalid_sheet_symbol");
                ValidateSheet(newSheet, target, hierarchy, source: null);
                id = newSheet.Id.Value;
                EnsureAbsent(hierarchy, id, ReadKind.Sheet);
                return NewPlan(expected.State, target, Any.Pack(newSheet), id, null, "create");
            case Mutation.UpdateSheet:
                if (source is null) throw new AutomationException("sheet_symbol_not_found", "No SheetSymbol has the requested UUID.");
                target = source.Document;
                var updatedSheet = Parse<SheetSymbol>(payloadJson, "invalid_sheet_symbol");
                ValidateSheet(updatedSheet, target, hierarchy, source.Item as SheetSymbol);
                id = RequireSameId(updatedSheet.Id.Value, source.Item, "sheet symbol");
                return NewPlan(expected.State, target, Any.Pack(updatedSheet), id, null, "update");
            case Mutation.DeleteSheet:
                if (source is null) throw new AutomationException("sheet_symbol_not_found", "No SheetSymbol has the requested UUID.");
                target = source.Document; id = itemId!;
                return NewPlan(expected.State, target, null, id, null, "delete");
            case Mutation.CreatePin:
                ValidateIdentity(parentSheetId ?? string.Empty, "parentSheetId");
                source = Find(hierarchy, parentSheetId!, null, ReadKind.Sheet);
                target = source.Document; var newPin = Parse<SheetPin>(payloadJson, "invalid_sheet_pin");
                ValidatePin(newPin); id = newPin.Id.Value;
                EnsurePinAbsent((SheetSymbol)source.Item, id);
                return NewParentPlan(expected.State, target, (SheetSymbol)source.Item, newPin, id, create: true);
            case Mutation.UpdatePin:
                source = Find(hierarchy, itemId ?? string.Empty, parentSheetId, ReadKind.Pin);
                target = source.Document; var updatedPin = Parse<SheetPin>(payloadJson, "invalid_sheet_pin");
                ValidatePin(updatedPin); id = RequireSameId(updatedPin.Id.Value, source.Item, "sheet pin");
                return NewParentPlan(expected.State, target, FindParent(hierarchy, parentSheetId!, itemId!), updatedPin, id, create: false);
            case Mutation.DeletePin:
                source = Find(hierarchy, itemId ?? string.Empty, parentSheetId, ReadKind.Pin);
                target = source.Document; id = itemId!;
                var parent = FindParent(hierarchy, parentSheetId!, itemId!);
                var removed = parent.Clone(); removed.Pins.Remove(removed.Pins.Single(p => p.Id.Value == id));
                var deletePin = NewPlan(expected.State, target, Any.Pack(removed), id, parentSheetId, "update");
                return deletePin with { ReplyItem = null };
            case Mutation.CreateLabel:
                target = RequireTarget(requestedTarget, hierarchy, "targetDocumentJson");
                var newLabel = Parse<HierarchicalLabel>(payloadJson, "invalid_hierarchical_label");
                ValidateLabel(newLabel); id = newLabel.Id.Value;
                EnsureAbsent(hierarchy, id, ReadKind.Label);
                return NewPlan(expected.State, target, Any.Pack(newLabel), id, null, "create");
            case Mutation.UpdateLabel:
                if (source is null) throw new AutomationException("hierarchical_label_not_found", "No HierarchicalLabel has the requested UUID.");
                target = source.Document; var updatedLabel = Parse<HierarchicalLabel>(payloadJson, "invalid_hierarchical_label");
                ValidateLabel(updatedLabel); id = RequireSameId(updatedLabel.Id.Value, source.Item, "hierarchical label");
                return NewPlan(expected.State, target, Any.Pack(updatedLabel), id, null, "update");
            case Mutation.DeleteLabel:
                if (source is null) throw new AutomationException("hierarchical_label_not_found", "No HierarchicalLabel has the requested UUID.");
                target = source.Document; id = itemId!;
                return NewPlan(expected.State, target, null, id, null, "delete");
            default: throw new AutomationException("invalid_schematic_hierarchy_item", "Unsupported hierarchy mutation.");
        }
    }

    private static Plan NewPlan(DocumentLifecycleState state, DocumentSpecifier target, Any? payload,
        string id, string? parent, string operation)
    {
        var item = new SchematicItemOperation { TargetDocument = target.Clone() };
        if (operation == "create") item.Create = payload!.Clone();
        else if (operation == "update") item.Update = payload!.Clone();
        else item.Remove = new KIID { Value = id };
        return new(payload, target, item, id, parent);
    }

    private static Plan NewParentPlan(DocumentLifecycleState state, DocumentSpecifier target,
        SheetSymbol parent, SheetPin pin, string id, bool create)
    {
        var changed = parent.Clone();
        if (create) changed.Pins.Add(pin.Clone());
        else
        {
            int index = changed.Pins.ToList().FindIndex(p => p.Id?.Value == id);
            if (index < 0) throw new AutomationException("sheet_pin_not_found", "The parent SheetSymbol no longer contains the requested pin.");
            changed.Pins[index] = pin.Clone();
        }
        var plan = NewPlan(state, target, Any.Pack(changed), id, parent.Id?.Value, "update");
        return plan with { ReplyItem = Any.Pack(pin) };
    }

    private static Located Find(SchematicHierarchyData hierarchy, string id, string? parentSheetId, ReadKind kind)
    {
        var matches = new List<Located>();
        foreach (var screen in hierarchy.Instances)
        {
            if (screen.Metadata?.Document is not { } document) continue;
            foreach (var packed in screen.Items)
            {
                if (kind == ReadKind.Sheet && packed.Is(SheetSymbol.Descriptor))
                {
                    var sheet = packed.Unpack<SheetSymbol>();
                    if (sheet.Id?.Value == id) matches.Add(new(packed.Clone(), sheet, document.Clone()));
                }
                else if (kind == ReadKind.Label && packed.Is(HierarchicalLabel.Descriptor))
                {
                    var label = packed.Unpack<HierarchicalLabel>();
                    if (label.Id?.Value == id) matches.Add(new(packed.Clone(), label, document.Clone()));
                }
                else if (kind == ReadKind.Pin && packed.Is(SheetSymbol.Descriptor))
                {
                    var sheet = packed.Unpack<SheetSymbol>();
                    if (sheet.Id?.Value != parentSheetId) continue;
                    foreach (var pin in sheet.Pins.Where(pin => pin.Id?.Value == id))
                        matches.Add(new(Any.Pack(pin), pin, document.Clone(), sheet.Id?.Value ?? parentSheetId!));
                }
            }
        }
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new AutomationException(kind switch { ReadKind.Sheet => "sheet_symbol_not_found", ReadKind.Pin => "sheet_pin_not_found", _ => "hierarchical_label_not_found" },
                $"No {kind switch { ReadKind.Sheet => "SheetSymbol", ReadKind.Pin => "SheetPin", _ => "HierarchicalLabel" }} has the requested UUID."),
            _ => throw new AutomationException("ambiguous_schematic_hierarchy_item", "The requested UUID appears more than once; no mutation was sent.")
        };
    }

    private static Located Find( SchematicElectricalState electrical, string id, string? parentSheetId, ReadKind kind) =>
        Find(electrical.Hierarchy.Data, id, parentSheetId, kind);

    private static SheetSymbol FindParent(SchematicHierarchyData hierarchy, string parentId, string pinId)
    {
        foreach (var screen in hierarchy.Instances)
            foreach (var packed in screen.Items)
                if (packed.Is(SheetSymbol.Descriptor) && packed.Unpack<SheetSymbol>().Id.Value == parentId)
                {
                    var sheet = packed.Unpack<SheetSymbol>();
                    if (sheet.Pins.Any(pin => pin.Id?.Value == pinId)) return sheet;
                }
        throw new AutomationException("sheet_symbol_not_found", "The parent SheetSymbol was not found.");
    }

    private static void EnsureAbsent(SchematicHierarchyData hierarchy, string id, ReadKind kind)
    {
        try { _ = Find(hierarchy, id, null, kind); }
        catch (AutomationException error) when (error.Code is "sheet_symbol_not_found" or "hierarchical_label_not_found") { return; }
        throw new AutomationException("duplicate_schematic_hierarchy_item", "The UUID already exists in the checked hierarchy.");
    }

    private static void EnsurePinAbsent(SheetSymbol sheet, string id)
    {
        if (sheet.Pins.Any(pin => pin.Id?.Value == id))
            throw new AutomationException("duplicate_sheet_pin", "The parent SheetSymbol already contains this pin UUID.");
    }

    private static string RequireSameId(string id, IMessage source, string name)
    {
        string current = source switch
        {
            SheetSymbol sheet => sheet.Id?.Value ?? string.Empty,
            SheetPin pin => pin.Id?.Value ?? string.Empty,
            HierarchicalLabel label => label.Id?.Value ?? string.Empty,
            _ => string.Empty
        };
        if (id != current) throw new AutomationException("identity_change_requires_resolution", $"The {name} UUID cannot change during an update.");
        return id;
    }

    private static void ValidateSheet(SheetSymbol sheet, DocumentSpecifier target, SchematicHierarchyData hierarchy, SheetSymbol? source)
    {
        ValidateIdentity(sheet.Id?.Value ?? string.Empty, "sheetJson.id");
        if (sheet.Path is null || !sheet.Path.Equals(target.SheetPath))
            throw new AutomationException("sheet_parent_mismatch", "sheetJson.path must match targetDocumentJson.sheetPath.");
        if (sheet.ChildScreenId is null || !Guid.TryParseExact(sheet.ChildScreenId.Value, "D", out var child) || child == Guid.Empty)
            throw new AutomationException("missing_child_screen_identity", "sheetJson.childScreenId is required for a checked hierarchy edit.");
        if (!hierarchy.Instances.Any(screen => screen.Metadata?.ScreenId?.Value == sheet.ChildScreenId.Value))
            throw new AutomationException("new_child_screen_requires_hierarchy_transaction", "The referenced child screen is not loaded in the checked hierarchy; create its contents with a document-level hierarchy transaction first.");
        if (sheet.FilenameField?.Text is null || string.IsNullOrWhiteSpace(sheet.FilenameField.Text.Text_)
            || sheet.FilenameField.Text.Text_.Contains('\0'))
            throw new AutomationException("invalid_sheet_filename", "sheetJson.filenameField.text must be nonempty and NUL-free.");
        if (sheet.NameField?.Text is null || string.IsNullOrWhiteSpace(sheet.NameField.Text.Text_)
            || sheet.NameField.Text.Text_.Contains('\0'))
            throw new AutomationException("invalid_sheet_name", "sheetJson.nameField.text must be nonempty and NUL-free.");
        if (sheet.Position is null || sheet.Size is null || sheet.Size.XNm <= 0 || sheet.Size.YNm <= 0
            || sheet.Position.XNm % 100 != 0 || sheet.Position.YNm % 100 != 0
            || sheet.Size.XNm % 100 != 0 || sheet.Size.YNm % 100 != 0)
            throw new AutomationException("invalid_sheet_geometry", "Sheet position and positive size must lie on 100 nm boundaries.");
        ValidatePins(sheet.Pins);
        if (source is not null && (source.ChildScreenId?.Value != sheet.ChildScreenId.Value
            || source.FilenameField?.Text?.Text_ != sheet.FilenameField.Text.Text_))
            throw new AutomationException("sheet_relink_requires_hierarchy_transaction", "Changing child-screen identity or filename requires an explicit hierarchy transaction.");
    }

    private static void ValidatePins(IEnumerable<SheetPin> pins)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in pins)
        {
            ValidatePin(pin);
            if (!ids.Add(pin.Id.Value)) throw new AutomationException("duplicate_sheet_pin", "A SheetSymbol cannot contain duplicate pin UUIDs.");
        }
    }

    private static void ValidatePin(SheetPin pin)
    {
        ValidateIdentity(pin.Id?.Value ?? string.Empty, "pinJson.id");
        if (pin.Position is null || pin.Position.XNm % 100 != 0 || pin.Position.YNm % 100 != 0)
            throw new AutomationException("invalid_sheet_pin_geometry", "pinJson.position must lie on a 100 nm boundary.");
        if (pin.Text is null || string.IsNullOrWhiteSpace(pin.Text.Text_) || pin.Text.Text_.Contains('\0'))
            throw new AutomationException("invalid_sheet_pin_text", "pinJson.text must be nonempty and NUL-free.");
        if (pin.Side == SheetSide.ShsUnknown || !System.Enum.IsDefined(pin.Side))
            throw new AutomationException("invalid_sheet_pin_side", "pinJson.side must be LEFT, RIGHT, TOP or BOTTOM.");
        if (!System.Enum.IsDefined(pin.Shape) || !System.Enum.IsDefined(pin.SpinStyle)
            || pin.Locked is LockedState.LsUnknown or > LockedState.LsLocked)
            throw new AutomationException("invalid_sheet_pin_attributes", "pinJson shape, spinStyle and locked state must be supported values.");
    }

    private static void ValidateLabel(HierarchicalLabel label)
    {
        ValidateIdentity(label.Id?.Value ?? string.Empty, "labelJson.id");
        if (label.Position is null || label.Position.XNm % 100 != 0 || label.Position.YNm % 100 != 0)
            throw new AutomationException("invalid_hierarchical_label_geometry", "labelJson.position must lie on a 100 nm boundary.");
        if (label.Text is null || string.IsNullOrWhiteSpace(label.Text.Text_) || label.Text.Text_.Contains('\0'))
            throw new AutomationException("invalid_hierarchical_label_text", "labelJson.text must be nonempty and NUL-free.");
        if (!System.Enum.IsDefined(label.Shape) || !System.Enum.IsDefined(label.SpinStyle)
            || label.Locked is LockedState.LsUnknown or > LockedState.LsLocked)
            throw new AutomationException("invalid_hierarchical_label_attributes", "labelJson shape, spinStyle and locked state must be supported values.");
    }

    private static DocumentSpecifier RequireTarget(DocumentSpecifier? target, SchematicHierarchyData hierarchy, string name)
    {
        if (target is null) throw new AutomationException("missing_target_document", name + " is required for child or root hierarchy authoring.");
        if (!hierarchy.Instances.Any(screen => Equals(screen.Metadata?.Document, target)))
            throw new AutomationException("target_sheet_not_loaded", "The target sheet is not present in the checked hierarchy.");
        return target;
    }

    private static string Description(Mutation mutation) => mutation switch
    {
        Mutation.CreateSheet => "Create checked schematic sheet symbol",
        Mutation.UpdateSheet => "Update checked schematic sheet symbol",
        Mutation.DeleteSheet => "Delete checked schematic sheet symbol",
        Mutation.CreatePin => "Create checked schematic sheet pin",
        Mutation.UpdatePin => "Update checked schematic sheet pin",
        Mutation.DeletePin => "Delete checked schematic sheet pin",
        Mutation.CreateLabel => "Create checked schematic hierarchical label",
        Mutation.UpdateLabel => "Update checked schematic hierarchical label",
        Mutation.DeleteLabel => "Delete checked schematic hierarchical label",
        _ => "Checked schematic hierarchy edit"
    };

    private static CallToolResult Reply(string instanceId, string epoch, DocumentSpecifier root,
        Plan plan, Mutation mutation, CheckedSchematicBatchReceipt receipt, bool submitted, bool replayed)
    {
        JsonElement receiptJson = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(receipt));
        JsonElement? item = plan.ReplyItem is null ? null
            : JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(plan.ReplyItem));
        var structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = epoch, document = Proto(root), targetDocument = Proto(plan.Target),
            operation = mutation.ToString(), operationId = receipt.OperationId, itemId = plan.ItemId,
            parentSheetId = plan.ParentSheetId, item, receipt = receiptJson,
            mutationSubmitted = submitted, replayed, nativeReceiptAvailable = true,
            designFilesSaved = false, revisionTracked = true
        });
        return new() { IsError = receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted,
            StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static CallToolResult ReadReply(string instanceId, string epoch, DocumentSpecifier root,
        Located located, ReadKind kind)
    {
        JsonElement item = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(located.Packed));
        var structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = epoch, document = Proto(root), targetDocument = Proto(located.Document),
            operation = "Read", kind = kind.ToString(), itemId = located.Item switch
            {
                SheetSymbol sheet => sheet.Id.Value,
                SheetPin pin => pin.Id.Value,
                HierarchicalLabel label => label.Id.Value,
                _ => string.Empty
            }, parentSheetId = located.ParentSheetId, item,
            nativeQuery = true, mutationSubmitted = false, revisionTracked = true, designFilesSaved = false
        });
        return new() { StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static ExpectedObservation ParseExpected(string json, DocumentSpecifier document)
    {
        try
        {
            using JsonDocument root = JsonDocument.Parse(json);
            if (!root.RootElement.TryGetProperty("state", out _))
                throw new AutomationException("invalid_checked_state", "expectedStateJson must be the complete checked state returned by kicad_schematic_checked_state.");
            CheckedSchematicState result = Parse<CheckedSchematicState>(json, "invalid_checked_state");
            if (result.State is null || result.Electrical?.Hierarchy?.Data?.Document is null)
                throw new AutomationException("invalid_checked_state", "The checked state must include lifecycle and hierarchy observations.");
            CheckedSchematicContract.ValidateObservation(result, document, result.State.ProcessEpoch);
            return new(result.State.Clone(), result.Electrical.Clone());
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException("invalid_checked_state", "expectedStateJson is not a complete checked schematic observation: " + error.Message); }
    }

    private static DocumentSpecifier ParseRoot(string json)
    {
        DocumentSpecifier document = Parse<DocumentSpecifier>(json, "invalid_checked_document");
        try { DocumentStateTools.ValidateTarget(document); }
        catch (AutomationException error) { throw new AutomationException("invalid_checked_document", error.Message); }
        if (document.Type != DocumentType.DoctypeSchematic || document.SheetPath?.Path.Count != 1)
            throw new AutomationException("invalid_checked_document", "documentJson must identify the schematic root sheet.");
        return document;
    }

    private static DocumentSpecifier ParseSheet(string json, string name)
    {
        DocumentSpecifier document = Parse<DocumentSpecifier>(json, "invalid_checked_document");
        try { DocumentStateTools.ValidateTarget(document); }
        catch (AutomationException error) { throw new AutomationException("invalid_checked_document", name + ": " + error.Message); }
        if (document.Type != DocumentType.DoctypeSchematic || document.SheetPath?.Path.Count == 0)
            throw new AutomationException("invalid_checked_document", name + " must identify a schematic sheet.");
        return document;
    }

    private static T Parse<T>(string? json, string code) where T : class, IMessage<T>, new()
    {
        if (string.IsNullOrWhiteSpace(json)) throw new AutomationException(code, "A complete protobuf JSON payload is required.");
        try { return SchematicJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException(code, "Invalid protobuf JSON: " + error.Message); }
    }

    private static JsonElement Proto(IMessage message) =>
        JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(message));

    private static void ValidateIdentity(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || id.ToString("D") != value)
            throw new AutomationException("invalid_identity", name + " must be a canonical nonempty UUID.");
    }
}
