using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Structured PCB inspection boundary.  The legacy board query tools accept
/// protobuf JSON for compatibility; this contract gives agents a typed
/// document, complete lifecycle observation and query selector without an
/// opaque request payload.
/// </summary>
[McpServerToolType]
public sealed class PcbBoardTypedTools(InstanceRegistry registry)
{
    private static readonly KiCadObjectType[] ConnectivityTypes =
    [
        KiCadObjectType.KotPcbPad,
        KiCadObjectType.KotPcbTrace,
        KiCadObjectType.KotPcbArc,
        KiCadObjectType.KotPcbVia,
        KiCadObjectType.KotPcbZone
    ];

    [McpServerTool(Name = "kicad_pcb_query_typed", ReadOnly = true),
     Description("Read one typed native PCB inspection result at an exact lifecycle checkpoint. input contains a structured PCB document, the complete expected lifecycle observation, a query kind and only the selector fields needed by that query. Supported kinds are nets, itemsByNet, itemsByNetClass, connectedItems, designRules, enabledLayers, stackup, netClasses and plotSettings. The tool never mutates the board, accepts no opaque protobuf request string, and rejects stale or incomplete observations before contacting KiCad."),
     KiCadCapability("pcb-query", "compiled-mcp plus native-board-api", "typed PCB document, lifecycle observation, selector and process epoch"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbBoardTypedMcpProcessTests.TypedPcbQueryPublishesStructuredSchemaAndRejectsIncompleteState", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Query(string instanceId, PcbBoardQueryInput input,
        CancellationToken cancellationToken) => Execute(instanceId, input, cancellationToken);

    private async Task<CallToolResult> Execute(string instanceId, PcbBoardQueryInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document = NativeDocumentInput.ToNative(input.Document);
            if (document.Type != DocumentType.DoctypePcb)
                throw new AutomationException("invalid_pcb_query_target", "The typed PCB query requires a PCB document.");
            DocumentLifecycleState expected = DocumentLifecycleInput.ToNative(input.ExpectedState);
            if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document))
                throw new AutomationException("invalid_pcb_query_state", "ExpectedState must be a complete observation of the same PCB document.");

            var client = registry.Client(instanceId);
            var session = await client.HandshakeAsync(cancellationToken);
            if (!string.Equals(session.Epoch, expected.ProcessEpoch, StringComparison.Ordinal))
                throw new AutomationException("pcb_instance_changed", "The expected PCB state belongs to another native process epoch.");

            switch (input.Query)
            {
                case PcbBoardQueryKind.Nets:
                    RequireOnly(input, allowNetClasses: true);
                    var nets = new GetNets { Board = document };
                    AddNames(nets.NetclassFilter, input.NetClasses, "netClasses");
                    return await Read<GetNets, NetsResponse>(client, session.Epoch, document, expected, "nets", nets, cancellationToken);
                case PcbBoardQueryKind.ItemsByNet:
                    RequireOnly(input, allowNets: true, requireNets: true);
                    var byNet = new GetItemsByNet { Header = new ItemHeader { Document = document } };
                    byNet.Types_.Add(ConnectivityTypes);
                    AddNetNames(byNet.Nets, input.Nets, "nets");
                    return await Read<GetItemsByNet, GetItemsResponse>(client, session.Epoch, document, expected, "items-by-net", byNet, cancellationToken);
                case PcbBoardQueryKind.ItemsByNetClass:
                    RequireOnly(input, allowNetClasses: true, requireNetClasses: true);
                    var byClass = new GetItemsByNetClass { Header = new ItemHeader { Document = document } };
                    byClass.Types_.Add(ConnectivityTypes);
                    AddNames(byClass.NetClasses, input.NetClasses, "netClasses");
                    return await Read<GetItemsByNetClass, GetItemsResponse>(client, session.Epoch, document, expected, "items-by-net-class", byClass, cancellationToken);
                case PcbBoardQueryKind.ConnectedItems:
                    RequireOnly(input, allowItemIds: true, requireItemIds: true);
                    var connected = new GetConnectedItems { Header = new ItemHeader { Document = document } };
                    connected.Types_.Add(ConnectivityTypes);
                    AddIds(connected.Items, input.ItemIds);
                    return await Read<GetConnectedItems, GetItemsResponse>(client, session.Epoch, document, expected, "connected-items", connected, cancellationToken);
                case PcbBoardQueryKind.DesignRules:
                    RequireOnly(input);
                    return await Read<GetBoardDesignRules, BoardDesignRulesResponse>(client, session.Epoch, document, expected, "design-rules",
                        new GetBoardDesignRules { Board = document }, cancellationToken);
                case PcbBoardQueryKind.EnabledLayers:
                    RequireOnly(input);
                    return await Read<GetBoardEnabledLayers, BoardEnabledLayersResponse>(client, session.Epoch, document, expected, "enabled-layers",
                        new GetBoardEnabledLayers { Board = document }, cancellationToken);
                case PcbBoardQueryKind.Stackup:
                    RequireOnly(input);
                    return await Read<GetBoardStackup, BoardStackupResponse>(client, session.Epoch, document, expected, "stackup",
                        new GetBoardStackup { Board = document }, cancellationToken);
                case PcbBoardQueryKind.NetClasses:
                    RequireOnly(input, allowNets: true, requireNets: true);
                    var classes = new GetNetClassForNets();
                    AddNetNames(classes.Net, input.Nets, "nets");
                    return await Read<GetNetClassForNets, NetClassForNetsResponse>(client, session.Epoch, document, expected, "net-classes", classes, cancellationToken);
                case PcbBoardQueryKind.PlotSettings:
                    RequireOnly(input);
                    return await Read<GetBoardPlotSettings, BoardPlotSettingsResponse>(client, session.Epoch, document, expected, "plot-settings",
                        new GetBoardPlotSettings { Board = document }, cancellationToken);
                default:
                    throw new AutomationException("invalid_pcb_query_kind", "The typed PCB query kind is not supported.");
            }
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidOperationException or JsonException or ArgumentException)
        {
            var result = JsonSerializer.SerializeToElement(new
            {
                errorCode = error is AutomationException known ? known.Code : "pcb_query_failed",
                errorMessage = error.Message
            });
            return new CallToolResult
            {
                IsError = true,
                StructuredContent = result,
                Content = [new TextContentBlock { Text = result.GetRawText() }]
            };
        }
    }

    private async Task<CallToolResult> Read<TRequest, TResponse>(NativeClient client, string epoch,
        DocumentSpecifier document, DocumentLifecycleState expected, string query, TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage<TResponse>, new()
    {
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        RequireState(before, expected, "The PCB changed before the typed query; inspect a fresh lifecycle state and retry.");
        TResponse response = await client.InvokeAsync<TRequest, TResponse>(request, cancellationToken);
        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        RequireState(after, expected, "The board changed while reading; discard the typed query result.");

        string responseJson = BoardJson.Formatter.Format(response);
        if (System.Text.Encoding.UTF8.GetByteCount(responseJson) > 32 * 1024 * 1024)
            throw new AutomationException("pcb_query_oversized", "The native typed query exceeds the bounded MCP response budget; narrow its selector.");
        using var parsed = JsonDocument.Parse(responseJson);
        var structured = JsonSerializer.SerializeToElement(new
        {
            query,
            document,
            processEpoch = epoch,
            beforeState = SchematicJson.Formatter.Format(before),
            afterState = SchematicJson.Formatter.Format(after),
            response = parsed.RootElement.Clone(),
            typedInput = true,
            nativeQuery = true,
            boardMutation = false
        });
        return new CallToolResult
        {
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }

    private static void RequireState(DocumentLifecycleState actual, DocumentLifecycleState expected, string message)
    {
        if (!actual.Equals(expected))
            throw new AutomationException("pcb_query_state_changed", message);
    }

    private static void RequireOnly(PcbBoardQueryInput input, bool allowNets = false, bool allowNetClasses = false,
        bool allowItemIds = false, bool requireNets = false, bool requireNetClasses = false,
        bool requireItemIds = false)
    {
        if ((!allowNets && input.Nets is { Count: > 0 }) || (requireNets && input.Nets is not { Count: > 0 }))
            throw new AutomationException("invalid_pcb_query_selector", requireNets ? "This query requires one or more net names." : "This query does not accept nets.");
        if ((!allowNetClasses && input.NetClasses is { Count: > 0 }) || (requireNetClasses && input.NetClasses is not { Count: > 0 }))
            throw new AutomationException("invalid_pcb_query_selector", requireNetClasses ? "This query requires one or more netclass names." : "This query does not accept netClasses.");
        if ((!allowItemIds && input.ItemIds is { Count: > 0 }) || (requireItemIds && input.ItemIds is not { Count: > 0 }))
            throw new AutomationException("invalid_pcb_query_selector", requireItemIds ? "This query requires one or more item IDs." : "This query does not accept itemIds.");
    }

    private static void AddNames(RepeatedField<string> destination, IReadOnlyList<string>? values, string field)
    {
        if (values is null) return;
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
                throw new AutomationException("invalid_pcb_query_selector", $"{field} contains an empty or oversized value.");
            if (!destination.Contains(value)) destination.Add(value);
        }
    }

    private static void AddNetNames(RepeatedField<Net> destination, IReadOnlyList<string>? values, string field)
    {
        if (values is null || values.Count == 0)
            throw new AutomationException("invalid_pcb_query_selector", $"{field} requires one or more net names.");
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
                throw new AutomationException("invalid_pcb_query_selector", $"{field} contains an empty or oversized value.");
            if (!destination.Any(net => net.Name == value)) destination.Add(new Net { Name = value });
        }
    }

    private static void AddIds(RepeatedField<KIID> destination, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            throw new AutomationException("invalid_pcb_query_selector", "itemIds requires one or more canonical UUIDs.");
        foreach (string value in values)
        {
            if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
                throw new AutomationException("invalid_pcb_query_selector", "itemIds must be nonempty lowercase UUIDs in D format.");
            if (!destination.Any(item => item.Value == value)) destination.Add(new KIID { Value = value });
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbBoardQueryInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private DocumentLifecycleInput? expectedState;
    private PcbBoardQueryKind query;
    private List<string>? nets;
    private List<string>? netClasses;
    private List<string>? itemIds;

    [Description("The exact PCB document being inspected.")]
    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    [Description("The complete kicad_document_state observation captured immediately before the query.")]
    public DocumentLifecycleInput? ExpectedState { get => expectedState; set => expectedState = BindOnce(value); }
    public PcbBoardQueryKind Query { get => query; set => query = BindOnce(value); }
    public List<string>? Nets { get => nets; set => nets = BindOnce(value); }
    public List<string>? NetClasses { get => netClasses; set => netClasses = BindOnce(value); }
    public List<string>? ItemIds { get => itemIds; set => itemIds = BindOnce(value); }
}

[JsonConverter(typeof(JsonStringEnumConverter<PcbBoardQueryKind>))]
public enum PcbBoardQueryKind
{
    [JsonStringEnumMemberName("nets")] Nets = 1,
    [JsonStringEnumMemberName("itemsByNet")] ItemsByNet = 2,
    [JsonStringEnumMemberName("itemsByNetClass")] ItemsByNetClass = 3,
    [JsonStringEnumMemberName("connectedItems")] ConnectedItems = 4,
    [JsonStringEnumMemberName("designRules")] DesignRules = 5,
    [JsonStringEnumMemberName("enabledLayers")] EnabledLayers = 6,
    [JsonStringEnumMemberName("stackup")] Stackup = 7,
    [JsonStringEnumMemberName("netClasses")] NetClasses = 8,
    [JsonStringEnumMemberName("plotSettings")] PlotSettings = 9
}
