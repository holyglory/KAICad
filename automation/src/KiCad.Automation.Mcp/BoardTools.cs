using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Model;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class BoardTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_open"),
     Description("Open an existing PCB in the explicitly attached KiCad instance. The path must belong to that instance's project; no project switching or implicit editor launch occurs.")]
    public Task<CallToolResult> Open(string instanceId, string path, CancellationToken cancellationToken) =>
        InstanceToolBoundary.Run(async () => JsonFormatter.Default.Format(
            await registry.Client(instanceId).OpenBoardAsync(path, cancellationToken)));

    [McpServerTool(Name = "kicad_pcb_create"),
     Description("Create an unsaved empty PCB document at an absolute project path when the file is missing. Existing files and open documents are preserved; save is explicit.")]
    public Task<CallToolResult> Create(string instanceId, string path, CancellationToken cancellationToken) =>
        InstanceToolBoundary.Run(async () => JsonFormatter.Default.Format(
            await registry.Client(instanceId).CreateBoardAsync(path, cancellationToken)));

    [McpServerTool(Name = "kicad_pcb_stackup", ReadOnly = true),
     Description("Read the complete native PCB stackup for an explicitly identified open board, including copper, dielectric, soldermask and mechanical layer properties.")]
    public Task<CallToolResult> Stackup(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var document = Parse<DocumentSpecifier>(documentJson);
            var result = await registry.Client(instanceId).InvokeAsync<GetBoardStackup, BoardStackupResponse>(
                new() { Board = document }, cancellationToken);
            return JsonFormatter.Default.Format(result);
        });

    [McpServerTool(Name = "kicad_pcb_layers", ReadOnly = true),
     Description("Read enabled, visible and active PCB layers for an explicit board. This supports independent per-layer agent views without changing the human editor.")]
    public Task<CallToolResult> Layers(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var document = Parse<DocumentSpecifier>(documentJson);
            var client = registry.Client(instanceId);
            var enabled = await client.InvokeAsync<GetBoardEnabledLayers, BoardEnabledLayersResponse>(new() { Board = document }, cancellationToken);
            var visible = await client.InvokeAsync<GetVisibleLayers, BoardLayers>(new() { Board = document }, cancellationToken);
            var active = await client.InvokeAsync<GetActiveLayer, BoardLayerResponse>(new() { Board = document }, cancellationToken);
            return JsonSerializer.Serialize(new
            {
                enabled = JsonFormatter.Default.Format(enabled),
                visible = JsonFormatter.Default.Format(visible),
                active = JsonFormatter.Default.Format(active)
            });
        });

    [McpServerTool(Name = "kicad_pcb_set_layers"),
     Description("Set the visible and active PCB layers for an explicitly identified board. This changes editor presentation only; it does not change board geometry.")]
    public Task<CallToolResult> SetLayers(string instanceId, string documentJson, string[] visibleLayers,
        string? activeLayer, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var document = Parse<DocumentSpecifier>(documentJson);
            var client = registry.Client(instanceId);
            var layers = visibleLayers.Select(ParseLayer).ToArray();
            await client.InvokeAsync<SetVisibleLayers, Empty>(new() { Board = document, Layers = { layers } }, cancellationToken);
            if (!string.IsNullOrWhiteSpace(activeLayer))
                await client.InvokeAsync<SetActiveLayer, Empty>(new() { Board = document, Layer = ParseLayer(activeLayer) }, cancellationToken);
            return JsonSerializer.Serialize(new { document = documentJson, visibleLayers = visibleLayers, activeLayer });
        });

    [McpServerTool(Name = "kicad_pcb_nets", ReadOnly = true),
     Description("Read board nets, optionally filtered by netclass, for an explicit open PCB.")]
    public Task<CallToolResult> Nets(string instanceId, string documentJson, string[]? netclassFilter,
        CancellationToken cancellationToken) =>
        Execute(async () => JsonFormatter.Default.Format(await registry.Client(instanceId)
            .InvokeAsync<GetNets, NetsResponse>(new() { Board = Parse<DocumentSpecifier>(documentJson), NetclassFilter = { netclassFilter ?? [] } }, cancellationToken)));

    [McpServerTool(Name = "kicad_pcb_items", ReadOnly = true),
     Description("Read selected PCB items by exact native UUID. Use this for pads, footprints, tracks, vias, zones, graphics and other board objects before planning a mutation or route.")]
    public Task<CallToolResult> Items(string instanceId, string headerJson, string[] itemIds,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var header = Parse<ItemHeader>(headerJson);
            var request = new GetItemsById { Header = header };
            request.Items.Add(itemIds.Select(ParseId));
            return JsonFormatter.Default.Format(await registry.Client(instanceId)
                .InvokeAsync<GetItemsById, GetItemsResponse>(request, cancellationToken));
        });

    [McpServerTool(Name = "kicad_pcb_connected_items", ReadOnly = true),
     Description("Read all copper-connected board items from exact source UUIDs, optionally filtered by KiCad object type. This is the connectivity primitive used by routing and DRC planning.")]
    public Task<CallToolResult> ConnectedItems(string instanceId, string headerJson, string[] itemIds,
        int[]? objectTypes, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var request = new GetConnectedItems { Header = Parse<ItemHeader>(headerJson) };
            request.Items.Add(itemIds.Select(ParseId));
            if (objectTypes is not null) request.Types_.Add(objectTypes.Select(value => (KiCadObjectType)value));
            return JsonFormatter.Default.Format(await registry.Client(instanceId)
                .InvokeAsync<GetConnectedItems, GetItemsResponse>(request, cancellationToken));
        });

    [McpServerTool(Name = "kicad_pcb_refill_zones"),
     Description("Refill selected PCB zones or all zones and return only after synchronous completion where the native context supports it. The caller must inspect the board afterward before treating copper as current.")]
    public Task<CallToolResult> RefillZones(string instanceId, string documentJson, string[]? zoneIds,
        CancellationToken cancellationToken) =>
        Execute(async () => JsonFormatter.Default.Format(await registry.Client(instanceId)
            .InvokeAsync<RefillZones, Empty>(new()
            {
                Board = Parse<DocumentSpecifier>(documentJson),
                Zones = { (zoneIds ?? []).Select(ParseId) }
            }, cancellationToken)));

    [McpServerTool(Name = "kicad_pcb_save"),
     Description("Save the explicitly identified PCB through KiCad's native editor and return the saved document identity. This is a persisted mutation and must be followed by a fresh inspection when used in a larger transaction.")]
    public Task<CallToolResult> Save(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var document = Parse<DocumentSpecifier>(documentJson);
            await registry.Client(instanceId).InvokeAsync<SaveDocument, Empty>(new() { Document = document }, cancellationToken);
            return documentJson;
        });

    [McpServerTool(Name = "kicad_pcb_revert"),
     Description("Revert the explicitly identified PCB through KiCad's native editor. The caller must inspect save state and board contents before using the result as a recovery boundary.")]
    public Task<CallToolResult> Revert(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var document = Parse<DocumentSpecifier>(documentJson);
            await registry.Client(instanceId).InvokeAsync<RevertDocument, Empty>(new() { Document = document }, cancellationToken);
            return documentJson;
        });

    [McpServerTool(Name = "kicad_pcb_create_items"),
     Description("Create typed PCB items in one native request. Each item is protobuf JSON with an @type discriminator for a KiCad board type. The operation is atomic at the native editor commit boundary and returns normalized created items.")]
    public Task<CallToolResult> CreateItems(string instanceId, string headerJson, string[] itemJson,
        string? containerId, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var request = new CreateItems { Header = Parse<ItemHeader>(headerJson) };
            if (!string.IsNullOrWhiteSpace(containerId)) request.Container = ParseId(containerId);
            request.Items.Add(itemJson.Select(BoardJson.Parser.Parse<Any>));
            return BoardJson.Formatter.Format(await registry.Client(instanceId)
                .InvokeAsync<CreateItems, CreateItemsResponse>(request, cancellationToken));
        });

    [McpServerTool(Name = "kicad_pcb_update_items"),
     Description("Update typed PCB items by exact native UUID in one native request. Each item is protobuf JSON with an @type discriminator; omitted fields follow the native item-update contract.")]
    public Task<CallToolResult> UpdateItems(string instanceId, string headerJson, string[] itemJson,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var request = new UpdateItems { Header = Parse<ItemHeader>(headerJson) };
            request.Items.Add(itemJson.Select(BoardJson.Parser.Parse<Any>));
            return BoardJson.Formatter.Format(await registry.Client(instanceId)
                .InvokeAsync<UpdateItems, UpdateItemsResponse>(request, cancellationToken));
        });

    [McpServerTool(Name = "kicad_pcb_delete_items"),
     Description("Delete PCB items by exact native UUID in one native request. The returned per-item status must be inspected before treating the mutation as complete.")]
    public Task<CallToolResult> DeleteItems(string instanceId, string headerJson, string[] itemIds,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var request = new DeleteItems { Header = Parse<ItemHeader>(headerJson) };
            request.ItemIds.Add(itemIds.Select(ParseId));
            return BoardJson.Formatter.Format(await registry.Client(instanceId)
                .InvokeAsync<DeleteItems, DeleteItemsResponse>(request, cancellationToken));
        });

    private static T Parse<T>(string json) where T : IMessage<T>, new() => JsonParser.Default.Parse<T>(json);

    private static KIID ParseId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || id.ToString("D") != value)
            throw new AutomationException("invalid_identity", "Use exact canonical nonempty native UUIDs.");
        return new KIID { Value = value };
    }

    private static BoardLayer ParseLayer(string value)
    {
        if (System.Enum.TryParse<BoardLayer>(value, true, out var layer)) return layer;
        if (int.TryParse(value, out var numeric) && System.Enum.IsDefined(typeof(BoardLayer), numeric)) return (BoardLayer)numeric;
        throw new AutomationException("invalid_layer", "Use a native BoardLayer enum name or numeric value.");
    }

    private static async Task<CallToolResult> Execute(Func<Task<string>> operation)
    {
        try
        {
            string value = await operation();
            var json = JsonSerializer.SerializeToElement(value);
            return new() { StructuredContent = json, Content = [new TextContentBlock { Text = value }] };
        }
        catch (AutomationException error)
        {
            var json = JsonSerializer.SerializeToElement(new { code = error.Code, message = error.Message });
            return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
        catch (NativeApiException error)
        {
            var json = JsonSerializer.SerializeToElement(new { code = "native_status_" + error.Status, message = error.Message });
            return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
    }
}
