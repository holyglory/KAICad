using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.ComponentModel;
using System.Text.Json;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>Small, typed constructors for the most common schematic connection edits.</summary>
public enum SchematicConnectionItemKind
{
    Wire = 1,
    Bus = 2,
    Junction = 3,
    BusEntry = 4,
    LocalLabel = 5,
    GlobalLabel = 6,
    HierarchicalLabel = 7,
    Text = 8
}

/// <summary>
/// Task-oriented schematic connection edits over the checked batch protocol. The generic checked batch
/// remains the escape hatch for object types that are not covered here; these methods keep the
/// common wire/label edits typed so an agent does not have to manufacture protobuf Any payloads.
/// </summary>
[McpServerToolType]
public sealed class SchematicTypedMutationTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_create_connection_item", ReadOnly = false, UseStructuredContent = true),
     Description("Create one explicitly typed schematic connection item (wire, bus, junction, bus entry, local/global/hierarchical label, or text) as one checked native undo step. Supply the exact root sheet documentJson and the complete expectedStateJson returned by kicad_schematic_checked_state, a new canonical itemId and operationId, coordinates in nanometres on 100 nm boundaries, and text for labels/text. Wire and bus require endXNm/endYNm; a bus entry uses those values as its size vector. The native batch is admitted only when process epoch, serialized state, revision and file baselines still match. Reuse the same operationId and identical arguments after a lost reply; a rejected or indeterminate result is not success. The edit is not saved to disk; use kicad_document_save after observing the resulting state."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root document, complete expected lifecycle state, process epoch, revision, item UUID and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> CreateConnectionItem(string instanceId, string documentJson,
        string expectedStateJson, SchematicConnectionItemKind kind, string itemId,
        long xNm, long yNm, string operationId, CancellationToken cancellationToken,
        long? endXNm = null, long? endYNm = null, string? text = null)
        => Execute(instanceId, documentJson, expectedStateJson, kind, itemId, xNm, yNm,
            endXNm, endYNm, text, operationId, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_read_connection_item", ReadOnly = true,
        UseStructuredContent = true),
     Description("Read one existing typed schematic connection item from the exact checked native state. Supply the explicit root documentJson, itemId and the complete checkedStateJson returned by kicad_schematic_checked_state; the request is refused if the process epoch, native identity, revision, digest or saved-file baselines no longer match. The returned item is the complete protobuf object, including label fields, custom properties and native attributes. Only wires, buses, junctions, bus entries, local/global/hierarchical labels and text are exposed; other native object kinds fail closed. This is a read-only native checkpoint and does not save or edit the schematic."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root document, item UUID, process epoch, complete lifecycle state and native hierarchy checkpoint"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ReadConnectionItem(string instanceId, string documentJson,
        string itemId, string expectedStateJson, CancellationToken cancellationToken,
        SchematicConnectionItemKind? expectedKind = null) =>
        ExecuteExisting(instanceId, documentJson, itemId, expectedStateJson, null,
            null, null, null, null, null, expectedKind, ExistingMutation.Read, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_update_connection_item", ReadOnly = false,
        UseStructuredContent = true),
     Description("Update one existing typed schematic connection item in one checked native undo step. Supply the exact root documentJson, itemId, complete checkedStateJson returned by kicad_schematic_checked_state, and a new canonical operationId. Use typed patch fields: xNm/yNm move a wire, bus, junction, bus entry or label; endXNm/endYNm replace a wire or bus endpoint or bus-entry size; text changes a label or text note. Coordinates must be paired and lie on 100 nm boundaries. The complete item captured in checkedStateJson is cloned before applying the patch, preserving label fields, custom properties, text attributes, lock state and all other native properties. The checked native receipt is retained for exact operation-ID replay after a lost reply; a rejected or indeterminate result is not success. The edit is not saved to disk; use kicad_document_save after observing the resulting state."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root document, item UUID, complete checked state, typed patch, checked process epoch/revision/file baselines and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> UpdateConnectionItem(string instanceId, string documentJson,
        string itemId, string expectedStateJson, string operationId, CancellationToken cancellationToken,
        long? xNm = null, long? yNm = null, long? endXNm = null, long? endYNm = null,
        string? text = null, SchematicConnectionItemKind? expectedKind = null) =>
        ExecuteExisting(instanceId, documentJson, itemId, expectedStateJson, operationId,
            xNm, yNm, endXNm, endYNm, text, expectedKind, ExistingMutation.Update, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_delete_connection_item", ReadOnly = false,
        UseStructuredContent = true),
     Description("Delete one explicitly identified existing schematic wire, bus, junction, bus entry, local/global/hierarchical label or text in one checked native undo step. Supply the exact root documentJson, itemId, complete expectedStateJson and a new canonical operationId. The item must be found in the checked hierarchy before native dispatch, and process epoch, native identity, revision, digest and file baselines must still match. The native receipt is retained for exact operation-ID replay after a lost reply; a rejected or indeterminate result is not success. The edit is not saved to disk; use kicad_document_save after observing the resulting state."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root document, item UUID, checked process epoch/revision/file baselines and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> DeleteConnectionItem(string instanceId, string documentJson,
        string itemId, string expectedStateJson, string operationId, CancellationToken cancellationToken) =>
        ExecuteExisting(instanceId, documentJson, itemId, expectedStateJson, operationId,
            null, null, null, null, null, null, ExistingMutation.Delete, cancellationToken);

    private enum ExistingMutation
    {
        Read,
        Update,
        Delete
    }

    private sealed record ExpectedObservation(DocumentLifecycleState State,
        SchematicElectricalState Electrical);

    private sealed record ConnectionItemSnapshot(Any Packed, IMessage Item,
        SchematicConnectionItemKind Kind, DocumentSpecifier Document);

    private async Task<CallToolResult> ExecuteExisting(string instanceId, string documentJson,
        string? itemId, string expectedStateJson, string? operationId,
        long? xNm, long? yNm, long? endXNm, long? endYNm, string? text,
        SchematicConnectionItemKind? expectedKind, ExistingMutation mutation,
        CancellationToken cancellationToken)
    {
        bool submitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document = ParseRootDocument(documentJson);
            ExpectedObservation expected = ParseExpectedObservation(expectedStateJson, document);
            ValidateIdentity(itemId!, "itemId");
            if (mutation != ExistingMutation.Read)
                ValidateIdentity(operationId!, "operationId");
            if (mutation == ExistingMutation.Update)
                ValidatePatch(xNm, yNm, endXNm, endYNm, text);

            var client = registry.Client(instanceId);
            var session = await client.HandshakeAsync(cancellationToken);
            ValidateExpectedState(document, expected, session.Epoch);

            ConnectionItemSnapshot source = FindConnectionItem(expected.Electrical, itemId!, expectedKind);
            if (mutation == ExistingMutation.Read)
            {
                var observedRead = await ObserveExpected(client, document, expected, cancellationToken);
                return ReadReply(instanceId, session.Epoch, document, observedRead.State, source);
            }

            ConnectionItemSnapshot target = mutation == ExistingMutation.Update
                ? ApplyPatch(source, xNm, yNm, endXNm, endYNm, text)
                : source;
            CheckedSchematicBatch request = BuildRequest(expected.State, source, target, operationId!, mutation);
            CheckedSchematicTools.ValidateRequest(request, session.Epoch);

            // Inspect the process-owned native receipt before taking a new checkpoint. A
            // completed update/delete changes the revision and removes the source item, but an
            // exact replay must return its original receipt without sending a second mutation.
            var prior = await client.InvokeAsync<ReadCheckedSchematicBatchReceipt, CheckedSchematicBatchReceipt>(
                new()
                {
                    Document = request.Batch.Document.Clone(), ProcessEpoch = session.Epoch,
                    OperationId = operationId!, ExpectedRequest = request.Clone()
                }, cancellationToken);
            CheckedSchematicTools.ValidateResult(request, prior, inspect: true);
            if (prior.Status != CheckedSchematicBatchStatus.CsbsNotFound)
                return MutationReply(instanceId, session.Epoch, document, source, operationId!,
                    mutation, prior, submitted: false, replayed: true);

            var observed = await ObserveExpected(client, document, expected, cancellationToken);
            ConnectionItemSnapshot live = FindConnectionItem(observed.Electrical, itemId!, expectedKind);
            if (live.Kind != source.Kind || !live.Packed.Equals(source.Packed))
                throw new AutomationException("stale_document_state",
                    "The target item changed after the checked observation; inspect a fresh checked state and retry.");
            // Build from the checkpoint item so the exact request inspected above is also the
            // request sent below. The complete payload keeps fields/custom properties intact.
            request = BuildRequest(expected.State, live,
                mutation == ExistingMutation.Update ? ApplyPatch(live, xNm, yNm, endXNm, endYNm, text) : live,
                operationId!, mutation);
            CheckedSchematicTools.ValidateRequest(request, session.Epoch);
            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(
                request, cancellationToken);
            CheckedSchematicTools.ValidateResult(request, receipt, inspect: false);
            return MutationReply(instanceId, session.Epoch, document, live, operationId!,
                mutation, receipt, submitted, replayed: false);
        }
        catch (Exception error) when (error is AutomationException or NativeApiException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException or NngException)
        {
            string code = error switch
            {
                AutomationException known => known.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode,
                _ => "invalid_checked_schematic_item"
            };
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, operation = mutation.ToString().ToLowerInvariant(), itemId, operationId,
                mutationSubmitted = submitted,
                outcome = submitted ? "not_confirmed" : "not_submitted",
                errorCode = code, errorMessage = error.Message
            });
            return new() { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private static ExpectedObservation ParseExpectedObservation(string json, DocumentSpecifier document)
    {
        try
        {
            using var root = JsonDocument.Parse(json);
            if (!root.RootElement.TryGetProperty("state", out _))
                throw new AutomationException("invalid_checked_state",
                    "expectedStateJson must be the complete structuredContent returned by kicad_schematic_checked_state.");
            var result = Parse<CheckedSchematicState>(json, "invalid_checked_state");
            if (result.State is null || result.Electrical?.Hierarchy?.Data?.Document is null)
                throw new AutomationException("invalid_checked_state",
                    "The checked state must include lifecycle and electrical hierarchy observations.");
            CheckedSchematicContract.ValidateObservation(result, document, result.State.ProcessEpoch);
            return new(result.State.Clone(), result.Electrical.Clone());
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidProtocolBufferException
            or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException("invalid_checked_state", "expectedStateJson is not a complete checked schematic observation: " + error.Message); }
    }

    private static async Task<ExpectedObservation> ObserveExpected(NativeClient client,
        DocumentSpecifier document, ExpectedObservation expected, CancellationToken token)
    {
        var observed = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        CheckedSchematicContract.ValidateObservation(observed, document, client.Epoch);
        if (!observed.State.Equals(expected.State) || !observed.Electrical.Equals(expected.Electrical))
            throw new AutomationException("stale_document_state",
                "The schematic changed after the checked observation; inspect a fresh state and retry.");
        return new(observed.State.Clone(), observed.Electrical.Clone());
    }

    private static void ValidateExpectedState(DocumentSpecifier document,
        ExpectedObservation expected, string processEpoch)
    {
        if (expected.State.ProcessEpoch != processEpoch)
            throw new AutomationException("stale_process_epoch",
                "The checked state belongs to another native process epoch.");
        var probe = new CheckedSchematicBatch
        {
            Batch = new ApplySchematicItemBatch
            {
                Document = document.Clone(), OperationId = Guid.NewGuid().ToString("D"),
                DocumentEpoch = expected.State.Revision?.Epoch ?? string.Empty,
                ExpectedRevision = expected.State.Revision?.Clone() ?? new()
            },
            ExpectedState = expected.State.Clone()
        };
        probe.Batch.Operations.Add(new SchematicItemOperation { SetPageSettings = new() });
        try { CheckedSchematicTools.ValidateRequest(probe, processEpoch); }
        catch (AutomationException error) when (error.Code == "invalid_checked_batch")
        { throw new AutomationException("invalid_checked_state", error.Message); }
    }

    private static void ValidatePatch(long? xNm, long? yNm, long? endXNm, long? endYNm, string? text)
    {
        if ((xNm.HasValue != yNm.HasValue) || (endXNm.HasValue != endYNm.HasValue))
            throw new AutomationException("invalid_connection_patch",
                "xNm/yNm and endXNm/endYNm must be supplied as coordinate pairs.");
        if (!xNm.HasValue && !endXNm.HasValue && text is null)
            throw new AutomationException("empty_connection_patch",
                "Supply a position, endpoint/size or text change.");
        foreach (var (value, name) in new[] { (xNm, "xNm"), (yNm, "yNm"), (endXNm, "endXNm"), (endYNm, "endYNm") })
            if (value is { } coordinate && coordinate % 100 != 0)
                throw new AutomationException("invalid_coordinate", name + " must be a multiple of 100 nm.");
        if (text is not null && (string.IsNullOrWhiteSpace(text) || text.Length > 4096))
            throw new AutomationException("invalid_connection_text", "Text changes require 1..4096 characters.");
    }

    private static ConnectionItemSnapshot ApplyPatch(ConnectionItemSnapshot source,
        long? xNm, long? yNm, long? endXNm, long? endYNm, string? text)
    {
        Vector2? position = xNm is { } x && yNm is { } y ? new() { XNm = x, YNm = y } : null;
        Vector2? end = endXNm is { } ex && endYNm is { } ey ? new() { XNm = ex, YNm = ey } : null;
        IMessage item = source.Item switch
        {
            SchematicLine line when source.Kind is SchematicConnectionItemKind.Wire or SchematicConnectionItemKind.Bus =>
                PatchLine(line, position, end, text),
            Junction junction => PatchJunction(junction, position, end, text),
            BusEntry entry => PatchBusEntry(entry, position, end, text),
            LocalLabel label => PatchLabel(label, position, end, text),
            GlobalLabel label => PatchLabel(label, position, end, text),
            HierarchicalLabel label => PatchLabel(label, position, end, text),
            SchematicText note => PatchText(note, position, end, text),
            _ => throw new AutomationException("unsupported_connection_item", "The native item is not a supported schematic connection item.")
        };
        return source with { Packed = Any.Pack(item), Item = item };
    }

    private static SchematicLine PatchLine(SchematicLine line, Vector2? position, Vector2? end, string? text)
    {
        if (text is not null) throw new AutomationException("invalid_connection_patch", "Wires and buses do not accept text.");
        if (position is null && end is null) throw new AutomationException("empty_connection_patch", "Supply a wire or bus position/endpoint change.");
        var result = line.Clone();
        if (position is not null) result.Start = position;
        if (end is not null) result.End = end;
        return result;
    }

    private static Junction PatchJunction(Junction junction, Vector2? position, Vector2? end, string? text)
    {
        if (end is not null || text is not null) throw new AutomationException("invalid_connection_patch", "Junctions accept only xNm/yNm.");
        if (position is null) throw new AutomationException("empty_connection_patch", "Supply xNm/yNm for the junction.");
        var result = junction.Clone(); result.Position = position; return result;
    }

    private static BusEntry PatchBusEntry(BusEntry entry, Vector2? position, Vector2? end, string? text)
    {
        if (text is not null || (position is null && end is null))
            throw new AutomationException("invalid_connection_patch", "Bus entries accept position and size changes only.");
        var result = entry.Clone();
        if (position is not null) result.Position = position;
        if (end is not null) result.Size = end;
        return result;
    }

    private static T PatchLabel<T>(T label, Vector2? position, Vector2? end, string? text) where T : class, IMessage<T>, new()
    {
        if (end is not null) throw new AutomationException("invalid_connection_patch", "Labels do not accept endXNm/endYNm.");
        if (position is null && text is null) throw new AutomationException("empty_connection_patch", "Supply a label position or text.");
        T result = label.Clone();
        switch (result)
        {
            case LocalLabel local:
                if (position is not null) local.Position = position;
                if (text is not null) local.Text ??= new Text();
                if (text is not null) local.Text.Text_ = text;
                break;
            case GlobalLabel global:
                if (position is not null) global.Position = position;
                if (text is not null) global.Text ??= new Text();
                if (text is not null) global.Text.Text_ = text;
                break;
            case HierarchicalLabel hierarchical:
                if (position is not null) hierarchical.Position = position;
                if (text is not null) hierarchical.Text ??= new Text();
                if (text is not null) hierarchical.Text.Text_ = text;
                break;
        }
        return result;
    }

    private static SchematicText PatchText(SchematicText note, Vector2? position, Vector2? end, string? text)
    {
        if (end is not null) throw new AutomationException("invalid_connection_patch", "Text notes do not accept endXNm/endYNm.");
        if (position is null && text is null) throw new AutomationException("empty_connection_patch", "Supply a note position or text.");
        var result = note.Clone(); result.Text ??= new Text();
        if (position is not null) result.Text.Position = position;
        if (text is not null) result.Text.Text_ = text;
        return result;
    }

    private static CheckedSchematicBatch BuildRequest(DocumentLifecycleState state,
        ConnectionItemSnapshot source, ConnectionItemSnapshot target, string operationId,
        ExistingMutation mutation)
    {
        var batch = new ApplySchematicItemBatch
        {
            Document = state.Document.Clone(), OperationId = operationId,
            DocumentEpoch = state.Revision.Epoch, ExpectedRevision = state.Revision.Clone(),
            Description = mutation == ExistingMutation.Update
                ? "Update typed schematic " + source.Kind.ToString().ToLowerInvariant()
                : "Delete typed schematic " + source.Kind.ToString().ToLowerInvariant()
        };
        var operation = new SchematicItemOperation { TargetDocument = source.Document.Clone() };
        if (mutation == ExistingMutation.Update) operation.Update = target.Packed.Clone();
        else operation.Remove = new KIID { Value = ItemId(source.Item) };
        batch.Operations.Add(operation);
        return new() { Batch = batch, ExpectedState = state.Clone() };
    }

    private static CallToolResult ReadReply(string instanceId, string processEpoch,
        DocumentSpecifier document, DocumentLifecycleState state, ConnectionItemSnapshot item)
    {
        JsonElement value = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(item.Packed));
        var structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch, document = ProtobufJson(document), operation = "read", itemId = ItemId(item.Item),
            kind = item.Kind.ToString(), targetDocument = ProtobufJson(item.Document), item = value,
            state = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(state)),
            nativeQuery = true, mutationSubmitted = false, revisionTracked = true,
            designFilesSaved = false
        });
        return new() { StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static CallToolResult MutationReply(string instanceId, string processEpoch,
        DocumentSpecifier document, ConnectionItemSnapshot item, string operationId,
        ExistingMutation mutation, CheckedSchematicBatchReceipt receipt, bool submitted, bool replayed)
    {
        JsonElement receiptJson = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(receipt));
        var structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch, document = ProtobufJson(document), operation = mutation.ToString().ToLowerInvariant(),
            itemId = ItemId(item.Item), kind = item.Kind.ToString(), targetDocument = ProtobufJson(item.Document),
            operationId, receipt = receiptJson, mutationSubmitted = submitted,
            nativeReceiptAvailable = true, replayed, designFilesSaved = false
        });
        return new() { IsError = receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted,
            StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static JsonElement ProtobufJson(IMessage message) =>
        JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(message));

    private static DocumentSpecifier ParseRootDocument(string documentJson)
    {
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_checked_document");
        try { DocumentStateTools.ValidateTarget(document); }
        catch (AutomationException error)
        { throw new AutomationException("invalid_checked_document", error.Message); }
        if ((int)document.Type != 1 || document.SheetPath?.Path.Count != 1)
            throw new AutomationException("invalid_checked_document", "The typed connection-item tools require an explicit schematic root document.");
        return document;
    }

    private static ConnectionItemSnapshot FindConnectionItem(SchematicElectricalState electrical,
        string itemId, SchematicConnectionItemKind? expectedKind)
    {
        ValidateIdentity(itemId, "itemId");
        var matches = new List<ConnectionItemSnapshot>();
        foreach (var screen in electrical.Hierarchy.Data.Instances)
        {
            if (screen.Metadata?.Document is not { } document) continue;
            foreach (var packed in screen.Items)
            {
                ConnectionItemSnapshot? item;
                try { item = RequireConnectionItem(packed); }
                catch (AutomationException error) when (error.Code == "unsupported_connection_item") { continue; }
                if (item is not null && ItemId(item.Item) == itemId)
                {
                    if (expectedKind is { } requested && item.Kind != requested)
                        throw new AutomationException("connection_item_kind_mismatch", $"The native item is {item.Kind}, not the requested {requested}.");
                    matches.Add(item with { Document = document.Clone() });
                }
            }
        }
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new AutomationException("connection_item_not_found", $"No supported schematic connection item has UUID {itemId} in the checked hierarchy."),
            _ => throw new AutomationException("ambiguous_connection_item", $"UUID {itemId} appears on more than one schematic screen; no mutation was sent.")
        };
    }

    private static ConnectionItemSnapshot RequireConnectionItem(Any packed)
    {
        if (packed is null || string.IsNullOrWhiteSpace(packed.TypeUrl))
            throw new AutomationException("invalid_connection_item", "A typed schematic item must carry a protobuf Any type.");
        IMessage item;
        SchematicConnectionItemKind kind;
        if (packed.Is(SchematicLine.Descriptor))
        {
            var line = packed.Unpack<SchematicLine>();
            kind = line.Type switch
            {
                SchematicLineType.SltWire => SchematicConnectionItemKind.Wire,
                SchematicLineType.SltBus => SchematicConnectionItemKind.Bus,
                _ => throw new AutomationException("unsupported_connection_item", "Only native wire and bus SchematicLine items are supported; graphic lines are refused.")
            };
            item = line;
        }
        else if (packed.Is(Junction.Descriptor)) { kind = SchematicConnectionItemKind.Junction; item = packed.Unpack<Junction>(); }
        else if (packed.Is(BusEntry.Descriptor)) { kind = SchematicConnectionItemKind.BusEntry; item = packed.Unpack<BusEntry>(); }
        else if (packed.Is(LocalLabel.Descriptor)) { kind = SchematicConnectionItemKind.LocalLabel; item = packed.Unpack<LocalLabel>(); }
        else if (packed.Is(GlobalLabel.Descriptor)) { kind = SchematicConnectionItemKind.GlobalLabel; item = packed.Unpack<GlobalLabel>(); }
        else if (packed.Is(HierarchicalLabel.Descriptor)) { kind = SchematicConnectionItemKind.HierarchicalLabel; item = packed.Unpack<HierarchicalLabel>(); }
        else if (packed.Is(SchematicText.Descriptor)) { kind = SchematicConnectionItemKind.Text; item = packed.Unpack<SchematicText>(); }
        else throw new AutomationException("unsupported_connection_item", "Only wires, buses, junctions, bus entries, local/global/hierarchical labels and text are supported.");
        string id = ItemId(item);
        ValidateIdentity(id, "itemId");
        return new ConnectionItemSnapshot(packed.Clone(), item, kind, new());
    }

    private static string ItemId(IMessage item) => item switch
    {
        SchematicLine line => line.Id?.Value ?? string.Empty,
        Junction junction => junction.Id?.Value ?? string.Empty,
        BusEntry entry => entry.Id?.Value ?? string.Empty,
        LocalLabel label => label.Id?.Value ?? string.Empty,
        GlobalLabel label => label.Id?.Value ?? string.Empty,
        HierarchicalLabel label => label.Id?.Value ?? string.Empty,
        SchematicText text => text.Id?.Value ?? string.Empty,
        _ => string.Empty
    };

    private async Task<CallToolResult> Execute(string instanceId, string documentJson,
        string expectedStateJson, SchematicConnectionItemKind kind, string itemId,
        long xNm, long yNm, long? endXNm, long? endYNm, string? text, string operationId,
        CancellationToken cancellationToken)
    {
        bool submitted = false;
        CheckedSchematicBatch? request = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_checked_document");
            try { DocumentStateTools.ValidateTarget(document); }
            catch (AutomationException error)
            { throw new AutomationException("invalid_checked_document", error.Message); }
            DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_checked_state");
            ValidateIdentity(itemId, "itemId");
            ValidateIdentity(operationId, "operationId");
            ValidateCoordinate(xNm, nameof(xNm));
            ValidateCoordinate(yNm, nameof(yNm));
            if (kind is SchematicConnectionItemKind.Wire or SchematicConnectionItemKind.Bus or SchematicConnectionItemKind.BusEntry)
            {
                if (endXNm is null || endYNm is null)
                    throw new AutomationException("missing_connection_endpoint", "Wire, bus and bus-entry creation require endXNm and endYNm.");
                ValidateCoordinate(endXNm.Value, nameof(endXNm));
                ValidateCoordinate(endYNm.Value, nameof(endYNm));
            }
            if (kind is SchematicConnectionItemKind.LocalLabel or SchematicConnectionItemKind.GlobalLabel
                or SchematicConnectionItemKind.HierarchicalLabel or SchematicConnectionItemKind.Text)
                if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
                    throw new AutomationException("invalid_connection_text", "Labels and text require 1..4096 characters.");

            IMessage item = BuildItem(kind, itemId, xNm, yNm, endXNm, endYNm, text);
            var batch = new ApplySchematicItemBatch
            {
                Document = document.Clone(),
                OperationId = operationId,
                DocumentEpoch = expected.Revision?.Epoch ?? string.Empty,
                ExpectedRevision = expected.Revision?.Clone() ?? new(),
                Description = "Create typed schematic " + kind.ToString().ToLowerInvariant()
            };
            batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(item) });
            request = new CheckedSchematicBatch { Batch = batch, ExpectedState = expected.Clone() };

            var client = registry.Client(instanceId);
            CheckedSchematicTools.ValidateRequest(request, client.Epoch);
            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, cancellationToken);
            CheckedSchematicTools.ValidateResult(request, receipt, inspect: false);
            string json = SchematicJson.Formatter.Format(receipt);
            var structured = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                instanceId, kind = kind.ToString(), itemId, operationId,
                receipt = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json),
                mutationSubmitted = submitted, designFilesSaved = false
            });
            return new() { IsError = receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted,
                StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException
            or InvalidProtocolBufferException or InvalidJsonException or NngException)
        {
            string code = error switch
            {
                AutomationException known => known.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode,
                _ => "invalid_checked_schematic_item"
            };
            var structured = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                instanceId, kind = kind.ToString(), itemId, operationId,
                mutationSubmitted = submitted,
                outcome = submitted ? "not_confirmed" : "not_submitted",
                errorCode = code, errorMessage = error.Message
            });
            return new() { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    internal static IMessage BuildItem(SchematicConnectionItemKind kind, string itemId,
        long xNm, long yNm, long? endXNm, long? endYNm, string? text)
    {
        ValidateIdentity(itemId, "itemId");
        ValidateCoordinate(xNm, nameof(xNm));
        ValidateCoordinate(yNm, nameof(yNm));
        var id = new KIID { Value = itemId };
        var position = new Vector2 { XNm = xNm, YNm = yNm };
        var unlocked = LockedState.LsUnlocked;
        return kind switch
        {
            SchematicConnectionItemKind.Wire => new SchematicLine
            {
                Id = id, Start = position, End = Endpoint(endXNm, endYNm),
                Type = SchematicLineType.SltWire, Locked = unlocked
            },
            SchematicConnectionItemKind.Bus => new SchematicLine
            {
                Id = id, Start = position, End = Endpoint(endXNm, endYNm),
                Type = SchematicLineType.SltBus, Locked = unlocked
            },
            SchematicConnectionItemKind.Junction => new Junction
            {
                Id = id, Position = position, Locked = unlocked
            },
            SchematicConnectionItemKind.BusEntry => new BusEntry
            {
                Id = id, Position = position, Size = Endpoint(endXNm, endYNm),
                Type = BusEntryType.BetWireToBus, Locked = unlocked
            },
            SchematicConnectionItemKind.LocalLabel => new LocalLabel
            {
                Id = id, Position = position, Text = Caption(text!),
                SpinStyle = SchematicLabelSpinStyle.SlssRight, Locked = unlocked
            },
            SchematicConnectionItemKind.GlobalLabel => new GlobalLabel
            {
                Id = id, Position = position, Text = Caption(text!),
                SpinStyle = SchematicLabelSpinStyle.SlssRight,
                Shape = SchematicLabelShape.SlshPassive, Locked = unlocked
            },
            SchematicConnectionItemKind.HierarchicalLabel => new HierarchicalLabel
            {
                Id = id, Position = position, Text = Caption(text!),
                SpinStyle = SchematicLabelSpinStyle.SlssRight,
                Shape = SchematicLabelShape.SlshPassive, Locked = unlocked
            },
            SchematicConnectionItemKind.Text => new SchematicText
            {
                Id = id, Text = new Text { Position = position, Text_ = text! }, Locked = unlocked
            },
            _ => throw new AutomationException("invalid_connection_kind", "Choose a supported schematic connection item kind.")
        };
    }

    private static Text Caption(string text) => new() { Text_ = text };

    private static Vector2 Endpoint(long? xNm, long? yNm)
    {
        if (xNm is null || yNm is null)
            throw new AutomationException("missing_connection_endpoint", "This item requires endXNm and endYNm.");
        ValidateCoordinate(xNm.Value, nameof(xNm));
        ValidateCoordinate(yNm.Value, nameof(yNm));
        return new() { XNm = xNm.Value, YNm = yNm.Value };
    }

    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    {
        try { return SchematicJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException(code, "Invalid protobuf JSON: " + error.Message); }
    }

    private static void ValidateIdentity(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || id.ToString("D") != value)
            throw new AutomationException("invalid_identity", name + " must be a canonical nonempty UUID.");
    }

    private static void ValidateCoordinate(long value, string name)
    {
        if (value % 100 != 0)
            throw new AutomationException("invalid_coordinate", name + " must be a multiple of 100 nm.");
    }
}
