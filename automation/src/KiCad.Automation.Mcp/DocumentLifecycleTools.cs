using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class DocumentLifecycleTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_document_save", ReadOnly = false),
     Description("Save an explicit schematic hierarchy or PCB only if the supplied kicad_document_state observation still matches the native process, document, revision, content and loaded file versions. Supply a new operation UUID, and reuse that exact request after a timeout. Returns a retained operation result; failed or uncertain saves require inspection. Does not close editors or promise atomic multi-file persistence.")]
    public async Task<CallToolResult> Save(string instanceId, string expectedStateJson, string operationId,
        CancellationToken cancellationToken)
    {
        LifecycleOperationResult? result = null;
        var response = await InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = Parse<DocumentLifecycleState>(expectedStateJson);
            DocumentStateTools.ValidateTarget(expected.Document);
            string id = OperationId(operationId);
            var client = registry.Client(instanceId);
            if (expected.ProcessEpoch != client.Epoch || !Guid.TryParseExact(expected.NativeIdentity, "D", out _)
                || expected.Revision is null || !Guid.TryParseExact(expected.Revision.Epoch, "D", out _)
                || expected.StateSha256.Length != 64 || !expected.StateSha256.All(char.IsAsciiHexDigitLower))
                throw new AutomationException("invalid_save_observation", "Use a complete observation from this native process; it cannot be replaced by a filename or guessed revision.");
            result = await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(new()
                { Document = expected.Document.Clone(), ExpectedState = expected, OperationId = id }, cancellationToken);
            ValidateResult(result, expected.Document, id, client.Epoch, LifecycleOperationStatus.LosSaved);
            return SchematicJson.Formatter.Format(result);
        });
        if (result is not null && !(response.IsError ?? false))
        {
            using var json = JsonDocument.Parse(SchematicJson.Formatter.Format(result));
            response.StructuredContent = json.RootElement.Clone();
            response.IsError = result.Status != LifecycleOperationStatus.LosSaved;
        }
        return response;
    }

    // Intentionally not exported yet: PCB close persists editor-owned project
    // settings which must first join the exact snapshot/explicit-save contract.
    public async Task<CallToolResult> Close(string instanceId, string expectedStateJson, string operationId,
        CancellationToken cancellationToken)
    {
        LifecycleOperationResult? result = null;
        var response = await InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = Parse<DocumentLifecycleState>(expectedStateJson);
            DocumentStateTools.ValidateTarget(expected.Document);
            string id = OperationId(operationId);
            var client = registry.Client(instanceId);
            if (expected.ProcessEpoch != client.Epoch)
                throw new AutomationException("stale_process_epoch", "Use an observation from this native process epoch.");
            result = await client.InvokeAsync<CheckedCloseDocument, LifecycleOperationResult>(new()
                { Document = expected.Document.Clone(), ExpectedState = expected, OperationId = id }, cancellationToken);
            ValidateResult(result, expected.Document, id, client.Epoch, LifecycleOperationStatus.LosClosed);
            return SchematicJson.Formatter.Format(result);
        });
        if (result is not null && !(response.IsError ?? false))
        {
            using var json = JsonDocument.Parse(SchematicJson.Formatter.Format(result));
            response.StructuredContent = json.RootElement.Clone();
            response.IsError = result.Status != LifecycleOperationStatus.LosClosed;
        }
        return response;
    }

    [McpServerTool(Name = "kicad_document_operation", ReadOnly = true),
     Description("Read the retained result of an exact document lifecycle operation in its original native process. This never retries a save, launches an editor or adopts a restarted process. Receipts survive editor closure and MCP reconnect while that native process remains alive.")]
    public Task<CallToolResult> Operation(string instanceId, string documentJson, string operationId,
        string processEpoch, CancellationToken cancellationToken) => InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = Parse<DocumentSpecifier>(documentJson);
            DocumentStateTools.ValidateTarget(document);
            string id = OperationId(operationId);
            var client = registry.Client(instanceId);
            if (processEpoch != client.Epoch)
                throw new AutomationException("stale_process_epoch", "The operation belongs to another native process epoch.");
            var result = await client.InvokeAsync<ReadLifecycleOperation, LifecycleOperationResult>(new()
                { Document = document, OperationId = id, ProcessEpoch = processEpoch }, cancellationToken);
            ValidateResult(result, document, id, processEpoch);
            return SchematicJson.Formatter.Format(result);
        });

    private static T Parse<T>(string json) where T : IMessage<T>, new()
    {
        try { return SchematicJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_lifecycle_request", "Provide a valid native lifecycle observation or document descriptor: " + error.Message); }
    }

    private static string OperationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "Provide a nonempty operation UUID.");
        return id.ToString("D");
    }

    private static void ValidateResult(LifecycleOperationResult result, DocumentSpecifier document, string id, string epoch,
        LifecycleOperationStatus? expectedSuccess = null)
    {
        if (!document.Equals(result.Document) || result.OperationId != id || result.ProcessEpoch != epoch
            || result.Status is not (LifecycleOperationStatus.LosSaved or LifecycleOperationStatus.LosRejected
                or LifecycleOperationStatus.LosFailed or LifecycleOperationStatus.LosIndeterminate or LifecycleOperationStatus.LosClosed)
            || (expectedSuccess is not null && result.Status is (LifecycleOperationStatus.LosSaved or LifecycleOperationStatus.LosClosed)
                && result.Status != expectedSuccess))
            throw new AutomationException("invalid_lifecycle_result", "Native operation result did not match the requested target and identity.");
    }
}
