using System.ComponentModel;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class LibraryTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_library_open"),
     Description("Open an explicitly identified symbol or footprint library item in the attached native editor and return its document descriptor.")]
    public Task<CallToolResult> Open(string instanceId, string kind, string libraryNickname, string entryName,
        CancellationToken cancellationToken) =>
        InstanceToolBoundary.Run(async () =>
        {
            int type = kind switch { "symbol" => 2, "footprint" => 4, _ => throw new AutomationException("invalid_library_kind", "Choose symbol or footprint.") };
            var response = await registry.Client(instanceId).InvokeAsync<OpenLibraryItem, OpenDocumentResponse>(new()
            {
                Type = (DocumentType)type, Identifier = new LibraryIdentifier { LibraryNickname = libraryNickname, EntryName = entryName }
            }, cancellationToken);
            return JsonFormatter.Default.Format(response.Document);
        });

    [McpServerTool(Name = "kicad_library_create_items"),
     Description("Create typed graphics, pins, fields or other supported library items inside an explicitly identified symbol or footprint document. Item JSON uses the KiCad protobuf @type discriminator.")]
    public Task<CallToolResult> CreateItems(string instanceId, string headerJson, string[] itemJson,
        string? containerId, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var header = SchematicJson.Parser.Parse<ItemHeader>(headerJson);
            var request = new CreateItems { Header = header };
            if (!string.IsNullOrWhiteSpace(containerId)) request.Container = ParseId(containerId);
            request.Items.Add(itemJson.Select(json => ParseAny(header, json)));
            return FormatAny(header, await registry.Client(instanceId)
                .InvokeAsync<CreateItems, CreateItemsResponse>(request, cancellationToken));
        });

    [McpServerTool(Name = "kicad_library_update_items"),
     Description("Update typed symbol or footprint library items by exact native UUID. The native editor validates item ownership and immutable fields.")]
    public Task<CallToolResult> UpdateItems(string instanceId, string headerJson, string[] itemJson,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var header = SchematicJson.Parser.Parse<ItemHeader>(headerJson);
            var request = new UpdateItems { Header = header };
            request.Items.Add(itemJson.Select(json => ParseAny(header, json)));
            return FormatAny(header, await registry.Client(instanceId)
                .InvokeAsync<UpdateItems, UpdateItemsResponse>(request, cancellationToken));
        });

    [McpServerTool(Name = "kicad_library_delete_items"),
     Description("Delete symbol or footprint library items by exact UUID and return per-item native deletion status.")]
    public Task<CallToolResult> DeleteItems(string instanceId, string headerJson, string[] itemIds,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var request = new DeleteItems { Header = SchematicJson.Parser.Parse<ItemHeader>(headerJson) };
            request.ItemIds.Add(itemIds.Select(ParseId));
            return SchematicJson.Formatter.Format(await registry.Client(instanceId)
                .InvokeAsync<DeleteItems, DeleteItemsResponse>(request, cancellationToken));
        });

    private static KIID ParseId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || id.ToString("D") != value)
            throw new AutomationException("invalid_identity", "Use exact canonical nonempty native UUIDs.");
        return new KIID { Value = value };
    }

    private static Any ParseAny(ItemHeader header, string json) =>
        (int)header.Document.Type == 4 ? BoardJson.Parser.Parse<Any>(json) : SchematicJson.Parser.Parse<Any>(json);

    private static string FormatAny(ItemHeader header, IMessage message) =>
        (int)header.Document.Type == 4 ? BoardJson.Formatter.Format(message) : SchematicJson.Formatter.Format(message);

    private static async Task<CallToolResult> Execute(Func<Task<string>> action)
    {
        try { string value = await action(); return new() { Content = [new TextContentBlock { Text = value }] }; }
        catch (AutomationException error)
        {
            var json = System.Text.Json.JsonSerializer.SerializeToElement(new { code = error.Code, message = error.Message });
            return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
    }
}
