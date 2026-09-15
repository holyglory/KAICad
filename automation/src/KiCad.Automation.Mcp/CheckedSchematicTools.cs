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
    [McpServerTool(Name = "kicad_schematic_apply_checked_batch", ReadOnly = false),
     Description("Apply a typed schematic batch only to its exact observed native document state, including process epoch, native content digest, project settings and file baselines. requestJson is CheckedSchematicBatch with batch and expectedState from kicad_document_state; its batch requires an operation UUID and matching document epoch/revision. Reuse the identical request after a timeout or inspect its checked receipt. Old peers reject this distinct command; no unchecked fallback exists. Native commits remain undoable. A rejected or indeterminate result is not success; cancellation or transport failure does not prove rollback. Does not save files or complete XML synchronization.")]
    public Task<CallToolResult> Apply(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run(instanceId, requestJson, inspect: false, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_checked_batch_receipt", ReadOnly = true),
     Description("Inspect the process-owned receipt for an exact CheckedSchematicBatch requestJson and instance ID. Does not submit or repeat a mutation. The original process, operation UUID, document and complete request must match; missing receipts remain not-found, and indeterminate results require reconciliation rather than a new operation ID. Receipts do not prove that the live document has not changed since the recorded operation.")]
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
