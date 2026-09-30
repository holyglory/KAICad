using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class SchematicAuthoringTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_apply_batch", UseStructuredContent = true),
     Description("Apply a typed native schematic batch containing supported create, update, delete and metadata operations. requestJson must be an ApplySchematicItemBatch with explicit document, document_epoch, expected_revision and operation_id. The native result remains authoritative and trackingComplete must be checked before using it as a complete revision proof.")]
    public async Task<CallToolResult> ApplyBatch(string instanceId, string requestJson, CancellationToken cancellationToken)
    {
        try
        {
            var request = SchematicJson.Parser.Parse<ApplySchematicItemBatch>(requestJson);
            if (string.IsNullOrWhiteSpace(request.OperationId) || string.IsNullOrWhiteSpace(request.DocumentEpoch)
                || request.ExpectedRevision is null || request.ExpectedRevision.Epoch != request.DocumentEpoch)
                throw new AutomationException("invalid_operation_identity", "The batch requires operation_id, document_epoch and a matching expected_revision epoch.");
            var result = await registry.Client(instanceId).InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(request, cancellationToken);
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, operationId = request.OperationId, status = "Completed",
                trackingComplete = result.TrackingComplete,
                result = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(result))
            });
            return new() { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
        catch (Exception error) when (error is Google.Protobuf.InvalidProtocolBufferException or InvalidJsonException or NativeApiException or NngException)
        { return Failure("schematic_batch_not_confirmed", error.Message); }
    }

    [McpServerTool(Name = "kicad_schematic_save")]
    public Task<CallToolResult> Save(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        InstanceToolBoundary.Run(async () =>
        {
            var document = SchematicJson.Parser.Parse<Kiapi.Common.Types.DocumentSpecifier>(documentJson);
            await registry.Client(instanceId).InvokeAsync<SaveDocument, Google.Protobuf.WellKnownTypes.Empty>(new() { Document = document }, cancellationToken);
            return documentJson;
        });

    private static CallToolResult Failure(string code, string message)
    {
        var json = JsonSerializer.SerializeToElement(new { code, message });
        return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
