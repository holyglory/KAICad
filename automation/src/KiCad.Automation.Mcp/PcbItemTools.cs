using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class PcbItemTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_items_read", ReadOnly = true),
     Description("Read explicitly typed native PCB items through KiCad's board API. requestJson is GetItems protobuf JSON and may select footprints, pads, tracks, arcs, vias, zones or graphics. Any-packed board fields are preserved; this does not infer routing or claim DRC freshness.")]
    public Task<CallToolResult> Read(string instanceId, string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        var request = BoardJson.Parser.Parse<GetItems>(requestJson);
        ValidateHeader(request.Header, requireItems: false);
        var result = await registry.Client(instanceId).InvokeAsync<GetItems, GetItemsResponse>(request, cancellationToken);
        return Data(new { instanceId, status = result.Status.ToString(), response = JsonDocument.Parse(BoardJson.Formatter.Format(result)).RootElement.Clone() });
    });

    [McpServerTool(Name = "kicad_pcb_items_create"),
     Description("Create typed native PCB items with an exact lifecycle checkpoint. requestJson is CreateItems protobuf JSON containing Any-packed Track, Via, Footprint, Pad or graphic objects; expectedStateJson is the matching kicad_document_state observation. The native API commit is undoable. A changed board is rejected before mutation; the result returns the native created identities and a fresh state observation.")]
    public Task<CallToolResult> Create(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) => Mutate<CreateItems, CreateItemsResponse>(instanceId, requestJson, expectedStateJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_items_update"),
     Description("Update typed native PCB items with an exact lifecycle checkpoint. requestJson is UpdateItems protobuf JSON; expectedStateJson must identify the same open board and native content digest. Stale boards are rejected and no unchecked fallback is used. This is a primitive edit, not automatic routing.")]
    public Task<CallToolResult> Update(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) => Mutate<UpdateItems, UpdateItemsResponse>(instanceId, requestJson, expectedStateJson, cancellationToken);

    private async Task<CallToolResult> Mutate<TRequest, TResponse>(string instanceId, string requestJson,
        string expectedStateJson, CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>, new()
    {
        TRequest request = typeof(TRequest) == typeof(CreateItems)
            ? (TRequest)(object)BoardJson.Parser.Parse<CreateItems>(requestJson)
            : (TRequest)(object)BoardJson.Parser.Parse<UpdateItems>(requestJson);
        ItemHeader header = request switch
        {
            CreateItems create => create.Header,
            UpdateItems update => update.Header,
            _ => throw new AutomationException("invalid_pcb_item_request", "Use a typed board create or update request.")
        };
        ValidateHeader(header, requireItems: true);
        if (request is CreateItems createRequest && createRequest.Items.Count == 0
            || request is UpdateItems updateRequest && updateRequest.Items.Count == 0)
            throw new AutomationException("invalid_pcb_item_request", "Supply at least one typed board item.");
        var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        if (expected.Scope != DocumentLifecycleScope.DlsPcb)
            throw new AutomationException("invalid_pcb_state", "The expected state must be a PCB lifecycle observation.");
        var client = registry.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (expected.ProcessEpoch != session.Epoch)
            throw new AutomationException("pcb_instance_changed", "The expected PCB state belongs to another native process epoch.");
        var before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = header.Document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The board changed after the supplied lifecycle checkpoint; retain the edit and inspect it again.");
        var result = await client.InvokeAsync<TRequest, TResponse>(request, cancellationToken);
        var after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = header.Document }, cancellationToken);
        var response = JsonDocument.Parse(typeof(TResponse) == typeof(CreateItemsResponse)
            ? BoardJson.Formatter.Format((CreateItemsResponse)(object)result)
            : BoardJson.Formatter.Format((UpdateItemsResponse)(object)result)).RootElement.Clone();
        return Data(new { instanceId, processEpoch = session.Epoch, beforeState = SchematicJson.Formatter.Format(before),
            afterState = SchematicJson.Formatter.Format(after), response, mutationConfirmed = true });
    }

    private static void ValidateHeader(ItemHeader header, bool requireItems)
    {
        if (header?.Document is null || header.Document.Type != DocumentType.DoctypePcb
            || string.IsNullOrWhiteSpace(header.Document.BoardFilename)
            || Path.GetFileName(header.Document.BoardFilename) != header.Document.BoardFilename
            || !header.Document.BoardFilename.EndsWith(".kicad_pcb", StringComparison.Ordinal))
            throw new AutomationException("invalid_pcb_item_request", "Target an explicit open .kicad_pcb document.");
        if (requireItems)
        {
            bool hasItems = header.Document is not null;
            // The concrete request validation below rejects empty item collections;
            // keep this branch here so read requests can intentionally have no type filter.
            _ = hasItems;
        }
    }

    private static CallToolResult Data(object value, bool error = false)
    {
        var structured = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { IsError = error, Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
    }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidProtocolBufferException or InvalidJsonException)
        { return Data(new { errorCode = error is AutomationException known ? known.Code : "pcb_item_operation_failed", errorMessage = error.Message }, true); }
    }
}
