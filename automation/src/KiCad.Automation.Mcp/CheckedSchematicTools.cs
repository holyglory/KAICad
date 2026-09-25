using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class CheckedSchematicTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_checked_state", ReadOnly = true),
     Description("Capture whole-schematic electrical data, typed hierarchy and full native admission state together at one native checkpoint. Requires an explicit root documentJson and instance ID. The native reader rejects changed or pending captures. The returned state can guard a CheckedSchematicBatch planned from its paired electrical snapshot; it may become stale afterward. Reports existing file conflicts and incomplete tracking without authorizing mutation. No design edits, file writes, navigation or image capture occur."),
     KiCadCapability("schematic-design", "native-api", "explicit instance ID and root document"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.CheckedBatchesRejectChangedStateAndPreserveNativeUndo")]
    public async Task<CallToolResult> Observe(string instanceId, string documentJson, CancellationToken cancellationToken)
    {
        CheckedSchematicState? result = null;
        var response = await InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = SchematicJson.Parser.Parse<Kiapi.Common.Types.DocumentSpecifier>(documentJson);
            DocumentStateTools.ValidateTarget(document);
            if ((int)document.Type != 1 || document.SheetPath?.Path.Count != 1)
                throw new AutomationException("invalid_checked_document", "Select the explicit schematic root for combined capture.");
            var client = registry.Client(instanceId);
            result = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                { Document = document, ProcessEpoch = client.Epoch }, cancellationToken);
            CheckedSchematicContract.ValidateObservation(result, document, client.Epoch);
            return SchematicJson.Formatter.Format(result);
        });
        if (result is not null && !(response.IsError ?? false))
        {
            using var json = JsonDocument.Parse(SchematicJson.Formatter.Format(result));
            response.StructuredContent = json.RootElement.Clone();
        }
        return response;
    }

    [McpServerTool(Name = "kicad_schematic_apply_checked_batch", ReadOnly = false),
     Description("Create, change and delete schematic objects in one step, only while KiCad still holds the exact observed native state (process epoch, native content digest, project settings and file baselines). requestJson is a CheckedSchematicBatch in protobuf JSON: batch {document: the sheet to edit, operations, operationId: a new UUID, documentEpoch and expectedRevision copied from the observed state, description} and expectedState: the state from kicad_schematic_checked_state or kicad_document_state. Each operation is one of: create with a new object carrying a new UUID, or update with the complete changed object carrying its existing UUID, both as a typed Any, for example {\"create\":{\"@type\":\"type.googleapis.com/kiapi.schematic.types.LocalLabel\",...}} for a LocalLabel, SchematicLine (wire), Junction, SchematicText or SchematicSymbolInstance; remove with the object's UUID ({\"remove\":{\"value\":\"<uuid>\"}}); or a sheet or project setting. Add, change or remove a symbol field by updating the symbol with its complete userFields list. The operations apply together as one native undo step, or not at all: a rejected receipt means KiCad is unchanged, and when KiCad refuses an operation the errorMessage starts 'Atomic operation N rejected:' with its zero-based index. A completed receipt lists every created or changed object as KiCad stored it (result.items, in operation order) and every removed UUID (result.removed). Sending the identical request again, for example after a timeout, returns the same receipt and never edits again, even after the batch was undone; kicad_schematic_checked_batch_receipt reads it without sending. An operation ID stays bound to its request: after any refusal or change, observe again and send a new operation ID. Old peers reject this distinct command; no unchecked fallback exists. A rejected or indeterminate result is not success; cancellation or transport failure does not prove rollback. Does not save files or complete XML synchronization."),
     KiCadCapability("schematic-design", "native-api", "document state observation, process epoch, document revision, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.CheckedBatchesRejectChangedStateAndPreserveNativeUndo")]
    public Task<CallToolResult> Apply(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run(instanceId, requestJson, inspect: false, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_checked_batch_receipt", ReadOnly = true),
     Description("Inspect the process-owned receipt for an exact CheckedSchematicBatch requestJson and instance ID. Does not submit or repeat a mutation. The original process, operation UUID, document and complete request must match; missing receipts remain not-found, and indeterminate results require reconciliation rather than a new operation ID. Receipts do not prove that the live document has not changed since the recorded operation."),
     KiCadCapability("schematic-design", "native-api", "exact checked request, process epoch, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.CheckedBatchesRejectChangedStateAndPreserveNativeUndo")]
    public Task<CallToolResult> Inspect(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run(instanceId, requestJson, inspect: true, cancellationToken);

    private async Task<CallToolResult> Run(string instanceId, string requestJson, bool inspect, CancellationToken token)
    {
        bool submitted = false;
        CheckedSchematicBatch? request = null;
        try
        {
            token.ThrowIfCancellationRequested();
            request = SchematicJson.Parser.Parse<CheckedSchematicBatch>(requestJson);
            var client = registry.Client(instanceId);
            ValidateRequest(request, client.Epoch);
            token.ThrowIfCancellationRequested();
            submitted = !inspect;
            var result = inspect
                ? await client.InvokeAsync<ReadCheckedSchematicBatchReceipt, CheckedSchematicBatchReceipt>(new()
                {
                    Document = request.Batch.Document.Clone(), ProcessEpoch = client.Epoch,
                    OperationId = request.Batch.OperationId, ExpectedRequest = request.Clone()
                }, token)
                : await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
            ValidateResult(request, result, inspect);
            using var json = JsonDocument.Parse(SchematicJson.Formatter.Format(result));
            var data = JsonSerializer.SerializeToElement(new { instanceId, receipt = json.RootElement.Clone(),
                mutationSubmitted = submitted, liveStateStillCurrent = false, designFilesSaved = false });
            return new() { IsError = !inspect && result.Status != CheckedSchematicBatchStatus.CsbsCompleted,
                Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or InvalidProtocolBufferException or InvalidJsonException or NngException)
        {
            string code = error switch { AutomationException known => known.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode, _ => "invalid_checked_batch" };
            var data = JsonSerializer.SerializeToElement(new { instanceId, operationId = request?.Batch?.OperationId,
                mutationSubmitted = submitted, outcome = submitted ? "not_confirmed" : "not_submitted", errorCode = code, errorMessage = error.Message });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }

    internal static void ValidateRequest(CheckedSchematicBatch request, string processEpoch) =>
        CheckedSchematicContract.ValidateRequest(request, processEpoch);

    internal static void ValidateResult(CheckedSchematicBatch request, CheckedSchematicBatchReceipt result, bool inspect) =>
        CheckedSchematicContract.ValidateResult(request, result, inspect);
}
