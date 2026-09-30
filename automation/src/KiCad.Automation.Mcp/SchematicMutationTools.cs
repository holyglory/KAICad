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

public sealed record ConnectedMoveToolResult(string InstanceId, string OperationId, string Status,
    JsonElement? NativeResult, bool TrackingComplete, string? ErrorCode = null, string? ErrorMessage = null);

public enum ConnectedSymbolTransform { Clockwise, Counterclockwise, MirrorLeftRight, MirrorUpDown }

[McpServerToolType]
public sealed class SchematicMutationTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_move_connected_symbols", UseStructuredContent = true,
        OutputSchemaType = typeof(ConnectedMoveToolResult)),
     Description("Move explicitly identified symbols using native connected drag, retaining attached wiring and native undo. Requires the exact loaded sheet document JSON, symbol UUIDs, displacement in nm (multiples of 100), document epoch, observed revision sequence and a caller-stable operation ID. Non-displayed sheets and repeated instances use the actual pins of every active symbol unit without navigating or replacing the user's viewport and selection. Shared geometry moves in all instances; visible references keep their own instance context. A move that changes any loaded instance's pin connectivity is rejected and rolled back. Reuse identical arguments and operation ID after a timeout; never retry with a fresh ID without inspecting the old receipt. Returns changed/deleted native items, not a matched image or a complete electrical correctness certificate. Complete revision tracking remains unavailable. Cancellation of the MCP request does not prove native rollback.")]
    public Task<CallToolResult> MoveConnectedSymbols(string instanceId, string documentJson,
        string[] symbolIds, long deltaXNm, long deltaYNm, string documentEpoch, ulong expectedRevision,
        string operationId, CancellationToken cancellationToken)
        => EditConnectedSymbols(instanceId, documentJson, symbolIds, deltaXNm, deltaYNm, null,
            documentEpoch, expectedRevision, operationId, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_transform_connected_symbols", UseStructuredContent = true,
        OutputSchemaType = typeof(ConnectedMoveToolResult)),
     Description("Rotate connected symbols 90 degrees clockwise/counterclockwise, or mirror them left-right/up-down about an explicit sheet-space pivot in nm (multiples of 100). Left-right reverses X; up-down reverses Y. Requires exact instance, sheet document, symbol UUIDs, document epoch, observed revision and a caller-stable operation ID. Uses native connected editing and undo for all active units, including non-displayed repeated sheets, while preserving the visible sheet, viewport and surviving selection identities. Locked targets and changed pin connectivity are rejected with rollback. Reuse identical arguments and operation ID after a timeout; inspect the old receipt before choosing a new ID. Returns actual changed/deleted native objects, not XML synchronization, a matched image or a complete electrical certificate. Revision tracking is incomplete; request cancellation does not prove native rollback.")]
    public Task<CallToolResult> TransformConnectedSymbols(string instanceId, string documentJson,
        string[] symbolIds, ConnectedSymbolTransform transform, long pivotXNm, long pivotYNm,
        string documentEpoch, ulong expectedRevision, string operationId, CancellationToken cancellationToken)
        => EditConnectedSymbols(instanceId, documentJson, symbolIds, pivotXNm, pivotYNm, transform,
            documentEpoch, expectedRevision, operationId, cancellationToken);

    private async Task<CallToolResult> EditConnectedSymbols(string instanceId, string documentJson,
        string[] symbolIds, long xNm, long yNm, ConnectedSymbolTransform? transform, string documentEpoch,
        ulong expectedRevision, string operationId, CancellationToken cancellationToken)
    {
        bool submitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(operationId) || System.Text.Encoding.UTF8.GetByteCount(operationId) > 128
                || operationId.Contains('\0') || string.IsNullOrWhiteSpace(documentEpoch))
                throw new AutomationException("invalid_operation_identity", "Provide a document epoch and a bounded nonempty operation ID.");
            if (string.IsNullOrWhiteSpace(documentJson))
                throw new AutomationException("invalid_document", "An explicit schematic document is required.");
            var document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson);
            if ((int)document.Type != 1 || document.SheetPath is null || document.SheetPath.Path.Count == 0)
                throw new AutomationException("invalid_document", "An explicit sheet-instance path is required.");
            foreach (var id in document.SheetPath.Path) ValidateId(id.Value);
            if (symbolIds is null || symbolIds.Length == 0 || symbolIds.Distinct(StringComparer.Ordinal).Count() != symbolIds.Length)
                throw new AutomationException("invalid_symbols", "Specify distinct symbol UUIDs.");
            foreach (string id in symbolIds) ValidateId(id);
            foreach (long value in new[] { xNm, yNm })
                if (value % 100 != 0 || value / 100 < int.MinValue || value / 100 > int.MaxValue)
                    throw new AutomationException(transform is null ? "invalid_displacement" : "invalid_pivot",
                        "Use exactly representable native coordinates in 100 nm increments.");
            var operation = new SchematicItemOperation();
            if (transform is { } kind)
            {
                operation.TransformConnectedSymbols = new()
                {
                    Pivot = new() { XNm = xNm, YNm = yNm },
                    Kind = kind switch
                    {
                        ConnectedSymbolTransform.Clockwise => SchematicConnectedTransformKind.SctRotateClockwise,
                        ConnectedSymbolTransform.Counterclockwise => SchematicConnectedTransformKind.SctRotateCounterclockwise,
                        ConnectedSymbolTransform.MirrorLeftRight => SchematicConnectedTransformKind.SctMirrorLeftRight,
                        ConnectedSymbolTransform.MirrorUpDown => SchematicConnectedTransformKind.SctMirrorUpDown,
                        _ => throw new AutomationException("invalid_transform", "Choose a quarter-turn or an axis reflection.")
                    }
                };
                operation.TransformConnectedSymbols.Symbols.Add(symbolIds.Select(id => new KIID { Value = id }));
            }
            else
            {
                operation.MoveConnectedSymbols = new() { Delta = new() { XNm = xNm, YNm = yNm } };
                operation.MoveConnectedSymbols.Symbols.Add(symbolIds.Select(id => new KIID { Value = id }));
            }
            var request = new ApplySchematicItemBatch
            {
                Document = document, DocumentEpoch = documentEpoch, OperationId = operationId,
                ExpectedRevision = new() { Epoch = documentEpoch, Sequence = expectedRevision },
                Description = transform is null ? "Move connected symbols" : "Transform connected symbols"
            };
            request.Operations.Add(operation);
            var client = registry.Client(instanceId);
            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            var result = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(request, cancellationToken);
            return Reply(new(instanceId, operationId, "Completed",
                JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(result)), false));
        }
        catch (Exception error) when (error is AutomationException or NativeApiException
            or InvalidProtocolBufferException or InvalidJsonException or NngException)
        {
            string code = error switch
            {
                AutomationException automation => automation.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode,
                _ => "invalid_request"
            };
            // A lost or malformed reply cannot establish whether the editor committed.
            // Even a native error can report an indeterminate retained receipt.
            // Preserve that uncertainty instead of promising that no edit occurred.
            string status = submitted ? "NotConfirmed" : "Rejected";
            return Reply(new(instanceId, operationId, status, null, false, code, error.Message));
        }
    }

    private static void ValidateId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || id.ToString("D") != value)
            throw new AutomationException("invalid_identity", "Use exact canonical nonempty native UUIDs.");
    }

    private static CallToolResult Reply(ConnectedMoveToolResult result)
    {
        var structured = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { IsError = result.Status != "Completed", StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }
}
