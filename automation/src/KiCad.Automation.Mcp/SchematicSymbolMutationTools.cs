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

/// <summary>
/// Checked, task-oriented CRUD for one placed schematic symbol instance.
///
/// The generic checked batch remains available for multi-object edits. These
/// methods make the common single-symbol/component operation safe to call
/// without manufacturing a protobuf Any payload or guessing the owning sheet.
/// They deliberately require the complete native instance returned by KiCad;
/// this adapter does not synthesize a library definition, component identity,
/// pin IDs, fields or repeated-sheet placements.
/// </summary>
[McpServerToolType]
public sealed class SchematicSymbolMutationTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_create_symbol_instance", ReadOnly = false,
        UseStructuredContent = true),
     Description("Create one complete schematic symbol instance in an explicitly targeted loaded sheet as one checked native undo step. documentJson identifies the schematic root; targetDocumentJson identifies the physical sheet and defaults to the root; expectedStateJson is the complete structuredContent returned by kicad_schematic_checked_state; symbolJson is a complete SchematicSymbolInstance with explicit definition, library link, unit, fields, pin identities and exactly one matching instance record; operationId is a new canonical UUID. The request refuses incomplete or cross-sheet payloads before native dispatch and is admitted only when the process epoch, hierarchy content, revision and file baselines still match. Reuse an identical request and operationId after a lost reply; inspect the receipt before creating a new operation. The edit is not saved to disk; use kicad_document_save after observing the result. This operation creates an unconnected symbol only; wiring and component ownership remain separate checked workflows."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and target sheet, complete symbol instance, checked hierarchy state, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SchematicSymbolMutationMcpProcessTests.DiscoverAndRejectMalformedSymbolMutations")]
    public Task<CallToolResult> Create(string instanceId, string documentJson, string expectedStateJson,
        string symbolJson, string operationId, CancellationToken cancellationToken,
        string? targetDocumentJson = null) => ExecuteMutation(instanceId, documentJson, targetDocumentJson,
            expectedStateJson, symbolJson, null, operationId, SymbolMutation.Create, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_read_symbol_instance", ReadOnly = true,
        UseStructuredContent = true),
     Description("Read one placed schematic symbol instance from the exact checked native hierarchy. documentJson identifies the schematic root; itemId is the canonical symbol UUID; expectedStateJson is the complete structuredContent returned by kicad_schematic_checked_state. The request is refused when the process epoch, hierarchy content, revision or file baselines no longer match. The response returns the complete native instance and its owning sheet, including library identity, fields, unit, pin identities, variants and instance records. This is a read-only checkpoint and does not save or edit the schematic."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root document, symbol UUID and complete checked hierarchy state"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SchematicSymbolMutationMcpProcessTests.DiscoverAndRejectMalformedSymbolMutations")]
    public Task<CallToolResult> Read(string instanceId, string documentJson, string itemId,
        string expectedStateJson, CancellationToken cancellationToken) => ExecuteMutation(instanceId,
            documentJson, null, expectedStateJson, null, itemId, null, SymbolMutation.Read, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_update_symbol_instance", ReadOnly = false,
        UseStructuredContent = true),
     Description("Replace one complete placed schematic symbol instance in one checked native undo step. documentJson identifies the schematic root; itemId is the existing canonical symbol UUID; symbolJson is a complete replacement with the same UUID, exact owning sheet, library link and unit; expectedStateJson is the complete structuredContent returned by kicad_schematic_checked_state; operationId is a new canonical UUID. Library identity, unit and sheet ownership changes are refused so component rebinding cannot be inferred from a patch. The checked native receipt supports exact operation-ID replay after a lost reply. The edit is not saved to disk; use kicad_document_save after observing the resulting state."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and existing symbol UUID, complete replacement payload, checked hierarchy state, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SchematicSymbolMutationMcpProcessTests.DiscoverAndRejectMalformedSymbolMutations")]
    public Task<CallToolResult> Update(string instanceId, string documentJson, string itemId,
        string expectedStateJson, string symbolJson, string operationId,
        CancellationToken cancellationToken) => ExecuteMutation(instanceId, documentJson, null,
            expectedStateJson, symbolJson, itemId, operationId, SymbolMutation.Update, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_delete_symbol_instance", ReadOnly = false,
        UseStructuredContent = true),
     Description("Delete one explicitly identified schematic symbol instance in one checked native undo step. documentJson identifies the schematic root; itemId is the canonical symbol UUID; expectedStateJson is the complete structuredContent returned by kicad_schematic_checked_state; operationId is a new canonical UUID. The owning sheet is resolved only from the checked hierarchy, and process epoch, hierarchy content, revision and file baselines must still match. Reuse an identical request and operationId after a lost reply; inspect the receipt before creating a new operation. The edit is not saved to disk; use kicad_document_save after observing the result. Deleting a symbol does not infer or rewrite component ownership or wiring."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root and symbol UUID, checked hierarchy state, process epoch, revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SchematicSymbolMutationMcpProcessTests.DiscoverAndRejectMalformedSymbolMutations")]
    public Task<CallToolResult> Delete(string instanceId, string documentJson, string itemId,
        string expectedStateJson, string operationId, CancellationToken cancellationToken) => ExecuteMutation(
            instanceId, documentJson, null, expectedStateJson, null, itemId, operationId,
            SymbolMutation.Delete, cancellationToken);

    private enum SymbolMutation
    {
        Create,
        Read,
        Update,
        Delete
    }

    private sealed record ExpectedObservation(DocumentLifecycleState State,
        SchematicElectricalState Electrical);

    private sealed record SymbolSnapshot(Any Packed, SchematicSymbolInstance Symbol,
        DocumentSpecifier Document);

    private async Task<CallToolResult> ExecuteMutation(string instanceId, string documentJson,
        string? targetDocumentJson, string expectedStateJson, string? symbolJson, string? itemId,
        string? operationId, SymbolMutation mutation, CancellationToken cancellationToken)
    {
        bool submitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_checked_document");
            ValidateRootDocument(document);
            ExpectedObservation expected = ParseExpectedObservation(expectedStateJson, document);

            if (mutation == SymbolMutation.Read)
            {
                ValidateIdentity(itemId!, "itemId");
                NativeClient client = registry.Client(instanceId);
                AutomationSession session = await client.HandshakeAsync(cancellationToken);
                ValidateProcessEpoch(expected.State, session.Epoch);
                SymbolSnapshot source = FindSymbol(expected.Electrical, itemId!);
                await EnsureExpectedStillCurrent(client, document, expected, cancellationToken);
                return ReadReply(instanceId, session.Epoch, document, source, expected.State);
            }

            ValidateIdentity(operationId!, "operationId");
            DocumentSpecifier? target = targetDocumentJson is null
                ? document.Clone()
                : Parse<DocumentSpecifier>(targetDocumentJson, "invalid_target_document");
            ValidateTargetDocument(target, document, expected.Electrical);

            SymbolSnapshot? sourceForMutation = null;
            if (mutation is SymbolMutation.Update or SymbolMutation.Delete)
            {
                ValidateIdentity(itemId!, "itemId");
                sourceForMutation = FindSymbol(expected.Electrical, itemId!);
                target = sourceForMutation.Document.Clone();
            }

            SchematicSymbolInstance? payload = symbolJson is null
                ? null
                : Parse<SchematicSymbolInstance>(symbolJson, "invalid_symbol_instance");
            if (mutation is SymbolMutation.Create or SymbolMutation.Update)
            {
                if (payload is null) throw new AutomationException("invalid_symbol_instance", "symbolJson is required.");
                ValidateSymbolPayload(payload, target!, mutation == SymbolMutation.Update, sourceForMutation?.Symbol);
                if (mutation == SymbolMutation.Update && payload.Id.Value != itemId)
                    throw new AutomationException("symbol_identity_mismatch", "symbolJson.id must match itemId.");
            }

            var clientForMutation = registry.Client(instanceId);
            AutomationSession mutationSession = await clientForMutation.HandshakeAsync(cancellationToken);
            ValidateProcessEpoch(expected.State, mutationSession.Epoch);

            CheckedSchematicBatch request = BuildRequest(expected.State, target!, payload,
                sourceForMutation, itemId, operationId!, mutation);
            CheckedSchematicTools.ValidateRequest(request, mutationSession.Epoch);

            // Read the native receipt before taking another checkpoint. A completed create/update/delete
            // changes the revision, but an exact replay must return its original receipt without sending
            // a second mutation.
            var prior = await clientForMutation.InvokeAsync<ReadCheckedSchematicBatchReceipt,
                CheckedSchematicBatchReceipt>(new()
                {
                    Document = request.Batch.Document.Clone(), ProcessEpoch = mutationSession.Epoch,
                    OperationId = operationId!, ExpectedRequest = request.Clone()
                }, cancellationToken);
            CheckedSchematicTools.ValidateResult(request, prior, inspect: true);
            if (prior.Status != CheckedSchematicBatchStatus.CsbsNotFound)
                return MutationReply(instanceId, mutation, operationId!, target!, prior,
                    submitted: false, replayed: true);

            await EnsureExpectedStillCurrent(clientForMutation, document, expected, cancellationToken);
            // The source/target was resolved from the supplied checkpoint. Re-read the live hierarchy
            // and refuse if the item or requested target changed while we were preparing the request.
            var live = await ReadExpected(clientForMutation, document, expected, cancellationToken);
            if (mutation is SymbolMutation.Update or SymbolMutation.Delete)
            {
                SymbolSnapshot current = FindSymbol(live.Electrical, itemId!);
                if (sourceForMutation is null || !Equals(current.Document, sourceForMutation.Document)
                    || !current.Packed.Equals(sourceForMutation.Packed))
                    throw new AutomationException("stale_document_state",
                        "The symbol changed after the checked observation; inspect a fresh checked state and retry.");
            }
            else ValidateTargetDocument(target!, document, live.Electrical);

            // Reuse the exact request built from the caller's checked state only after all current
            // identity checks pass. The native checked controller supplies the final atomic guard.
            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            var receipt = await clientForMutation.InvokeAsync<CheckedSchematicBatch,
                CheckedSchematicBatchReceipt>(request, cancellationToken);
            CheckedSchematicTools.ValidateResult(request, receipt, inspect: false);
            return MutationReply(instanceId, mutation, operationId!, target!, receipt,
                submitted, replayed: false);
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
                _ => "invalid_symbol_instance"
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

    private static CheckedSchematicBatch BuildRequest(DocumentLifecycleState state,
        DocumentSpecifier target, SchematicSymbolInstance? payload, SymbolSnapshot? source,
        string? itemId, string operationId, SymbolMutation mutation)
    {
        var batch = new ApplySchematicItemBatch
        {
            Document = state.Document.Clone(), DocumentEpoch = state.Revision.Epoch,
            ExpectedRevision = state.Revision.Clone(), OperationId = operationId,
            Description = mutation switch
            {
                SymbolMutation.Create => "Create checked schematic symbol instance",
                SymbolMutation.Update => "Update checked schematic symbol instance",
                SymbolMutation.Delete => "Delete checked schematic symbol instance",
                _ => throw new AutomationException("invalid_symbol_operation", "Read does not create a native batch.")
            }
        };
        var operation = new SchematicItemOperation { TargetDocument = target.Clone() };
        switch (mutation)
        {
            case SymbolMutation.Create:
                operation.Create = Any.Pack(payload!);
                break;
            case SymbolMutation.Update:
                operation.Update = Any.Pack(payload!);
                break;
            case SymbolMutation.Delete:
                operation.Remove = new KIID { Value = itemId! };
                break;
            default:
                throw new AutomationException("invalid_symbol_operation", "Read does not create a native batch.");
        }
        batch.Operations.Add(operation);
        return new() { Batch = batch, ExpectedState = state.Clone() };
    }

    private static ExpectedObservation ParseExpectedObservation(string json, DocumentSpecifier document)
    {
        try
        {
            using JsonDocument root = JsonDocument.Parse(json);
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

    private static async Task<ExpectedObservation> ReadExpected(NativeClient client,
        DocumentSpecifier document, ExpectedObservation expected, CancellationToken token)
    {
        var observed = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        CheckedSchematicContract.ValidateObservation(observed, document, client.Epoch);
        if (!observed.State.Equals(expected.State) || !observed.Electrical.Equals(expected.Electrical))
            throw new AutomationException("stale_document_state",
                "The schematic changed after the checked observation; inspect a fresh checked state and retry.");
        return new(observed.State.Clone(), observed.Electrical.Clone());
    }

    private static async Task EnsureExpectedStillCurrent(NativeClient client,
        DocumentSpecifier document, ExpectedObservation expected, CancellationToken token)
    {
        await ReadExpected(client, document, expected, token);
    }

    private static void ValidateProcessEpoch(DocumentLifecycleState state, string processEpoch)
    {
        if (state.ProcessEpoch != processEpoch)
            throw new AutomationException("stale_process_epoch", "The checked state belongs to another native process epoch.");
    }

    private static SymbolSnapshot FindSymbol(SchematicElectricalState electrical, string itemId)
    {
        var matches = new List<SymbolSnapshot>();
        foreach (var screen in electrical.Hierarchy.Data.Instances)
        {
            if (screen.Metadata?.Document is not { } document) continue;
            foreach (var packed in screen.Items)
            {
                if (!packed.Is(SchematicSymbolInstance.Descriptor)) continue;
                var symbol = packed.Unpack<SchematicSymbolInstance>();
                if (symbol.Id?.Value == itemId)
                    matches.Add(new(packed.Clone(), symbol, document.Clone()));
            }
        }
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new AutomationException("symbol_instance_not_found",
                $"No schematic symbol instance has UUID {itemId} in the checked hierarchy."),
            _ => throw new AutomationException("ambiguous_symbol_instance",
                $"UUID {itemId} appears on more than one schematic screen; no mutation was sent.")
        };
    }

    private static void ValidateTargetDocument(DocumentSpecifier target, DocumentSpecifier root,
        SchematicElectricalState electrical)
    {
        ValidateSchematicDocument(target, "targetDocumentJson");
        if (!Equals(target.Project, root.Project))
            throw new AutomationException("invalid_target_document", "The target sheet must belong to the same project as documentJson.");
        if (!electrical.Hierarchy.Data.Instances.Any(screen => Equals(screen.Metadata?.Document, target)))
            throw new AutomationException("target_sheet_not_loaded", "The target sheet is not present in the checked hierarchy.");
    }

    private static void ValidateRootDocument(DocumentSpecifier document)
    {
        ValidateSchematicDocument(document, "documentJson");
        if (document.SheetPath.Path.Count != 1)
            throw new AutomationException("invalid_checked_document", "documentJson must identify the schematic root sheet.");
    }

    private static void ValidateSchematicDocument(DocumentSpecifier document, string name)
    {
        if (document.Type != DocumentType.DoctypeSchematic || document.Project is null
            || document.SheetPath is null || document.SheetPath.Path.Count == 0)
            throw new AutomationException("invalid_checked_document", name + " must identify a schematic sheet in a project.");
        foreach (var id in document.SheetPath.Path) ValidateIdentity(id.Value, name + ".sheetPath");
    }

    private static void ValidateSymbolPayload(SchematicSymbolInstance symbol, DocumentSpecifier target,
        bool update, SchematicSymbolInstance? source)
    {
        ValidateIdentity(symbol.Id?.Value ?? string.Empty, "symbolJson.id");
        if (symbol.Position is null || symbol.Position.XNm % 100 != 0 || symbol.Position.YNm % 100 != 0)
            throw new AutomationException("invalid_symbol_position", "symbolJson.position is required on a 100 nm boundary.");
        if (symbol.Transform is null || symbol.Transform.Orientation is < SchematicSymbolOrientation.Sso0
            or > SchematicSymbolOrientation.Sso270)
            throw new AutomationException("invalid_symbol_transform", "symbolJson.transform must specify a supported orientation.");
        if (symbol.Locked is LockedState.LsUnknown or > LockedState.LsLocked)
            throw new AutomationException("invalid_symbol_lock", "symbolJson.locked must be LS_UNLOCKED or LS_LOCKED.");
        if (symbol.Unit is null || symbol.Unit.Unit < 1)
            throw new AutomationException("invalid_symbol_unit", "symbolJson.unit must specify a positive unit number.");
        if (symbol.Definition is null || symbol.Definition.Id is null
            || string.IsNullOrWhiteSpace(symbol.Definition.Id.LibraryNickname)
            || string.IsNullOrWhiteSpace(symbol.Definition.Id.EntryName)
            || symbol.Definition.UnitCount < symbol.Unit.Unit)
            throw new AutomationException("incomplete_symbol_definition",
                "symbolJson.definition must carry a complete library identity and the requested unit.");
        if (symbol.LibraryId is null || string.IsNullOrWhiteSpace(symbol.LibraryId.LibraryNickname)
            || string.IsNullOrWhiteSpace(symbol.LibraryId.EntryName))
            throw new AutomationException("missing_symbol_library_id",
                "symbolJson.libraryId must preserve the original library identity explicitly.");
        if (!symbol.LibraryId.Equals(symbol.Definition.Id))
            throw new AutomationException("symbol_library_mismatch",
                "symbolJson.libraryId must match symbolJson.definition.id for this checked placement.");
        if (!symbol.SeparatePinIdentities)
            throw new AutomationException("missing_symbol_pin_identities",
                "symbolJson must carry separate pin identities so native connectivity can be observed exactly.");
        if (symbol.InstanceRecords is null || symbol.InstanceRecords.Records.Count != 1)
            throw new AutomationException("invalid_symbol_instance_records",
                "symbolJson.instanceRecords must contain exactly one placement record for the targeted sheet.");
        var record = symbol.InstanceRecords.Records[0];
        if (!record.Path.SequenceEqual(target.SheetPath.Path)
            || record.Unit != symbol.Unit.Unit || string.IsNullOrWhiteSpace(record.ProjectName)
            || string.IsNullOrWhiteSpace(record.Reference))
            throw new AutomationException("symbol_instance_record_mismatch",
                "The one instance record must name the target sheet, project, unit and reference.");
        if (symbol.Path is not null && !symbol.Path.Equals(target.SheetPath))
            throw new AutomationException("symbol_path_mismatch", "symbolJson.path must match the targeted sheet.");
        if (symbol.ReferenceField?.Text is null || string.IsNullOrWhiteSpace(symbol.ReferenceField.Text.Text_)
            || symbol.ValueField?.Text is null)
            throw new AutomationException("incomplete_symbol_fields",
                "symbolJson must carry reference and value fields with explicit text payloads.");
        if (!string.Equals(symbol.ReferenceField.Text.Text_, record.Reference, StringComparison.Ordinal))
            throw new AutomationException("symbol_reference_mismatch", "The instance record reference must match referenceField.text.");

        var pins = symbol.Definition.Items
            .Where(item => item.Item?.Is(SchematicPin.Descriptor) == true
                && (item.Unit is null || item.Unit.Unit == 0 || item.Unit.Unit == symbol.Unit.Unit))
            .Select(item => item.Item.Unpack<SchematicPin>()).ToArray();
        if (pins.Length == 0 || pins.Any(pin => pin.Id is null || pin.LibraryPinId is null
            || string.IsNullOrWhiteSpace(pin.Id.Value) || string.IsNullOrWhiteSpace(pin.LibraryPinId.Value)
            || string.IsNullOrWhiteSpace(pin.Number) || pin.Position is null))
            throw new AutomationException("incomplete_symbol_pins",
                "Every active definition pin must carry its native and library identities, number and position.");

        if (update && source is not null)
        {
            if (source.Path is not null && symbol.Path is not null && !source.Path.Equals(symbol.Path))
                throw new AutomationException("symbol_path_change_requires_resolution", "Changing sheet ownership requires an explicit hierarchy operation.");
            if (source.Unit?.Unit != symbol.Unit.Unit)
                throw new AutomationException("symbol_unit_change_requires_resolution", "Changing a component unit requires an explicit ownership operation.");
            LibraryIdentifier sourceLibrary = source.LibraryId ?? source.Definition?.Id
                ?? throw new AutomationException("missing_symbol_library_id", "The existing symbol has no resolvable library identity.");
            if (!sourceLibrary.Equals(symbol.LibraryId))
                throw new AutomationException("symbol_rebinding_requires_resolution", "Changing library identity requires an explicit component rebind.");
        }
    }

    private static CallToolResult ReadReply(string instanceId, string processEpoch,
        DocumentSpecifier root, SymbolSnapshot source, DocumentLifecycleState state)
    {
        JsonElement symbol = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(source.Symbol));
        var structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch, document = ProtobufJson(root), itemId = source.Symbol.Id.Value,
            targetDocument = ProtobufJson(source.Document), operation = "read", symbol,
            state = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(state)),
            nativeQuery = true, revisionTracked = true, designFilesSaved = false
        });
        return new() { StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static CallToolResult MutationReply(string instanceId, SymbolMutation mutation,
        string operationId, DocumentSpecifier target, CheckedSchematicBatchReceipt receipt,
        bool submitted, bool replayed)
    {
        JsonElement receiptJson = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(receipt));
        var structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, operation = mutation.ToString().ToLowerInvariant(), operationId,
            targetDocument = ProtobufJson(target), receipt = receiptJson,
            mutationSubmitted = submitted, nativeReceiptAvailable = true, replayed,
            designFilesSaved = false
        });
        return new() { IsError = receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted,
            StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static JsonElement ProtobufJson(IMessage message) =>
        JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(message));

    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    {
        try { return SchematicJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
            or InvalidOperationException or JsonException)
        { throw new AutomationException(code, "Invalid protobuf JSON: " + error.Message); }
    }

    private static void ValidateIdentity(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty
            || id.ToString("D") != value)
            throw new AutomationException("invalid_identity", name + " must be a canonical nonempty UUID.");
    }
}
