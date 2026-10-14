using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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

/// <summary>Revision-paired board connectivity and rule observations used by agent planners.</summary>
[McpServerToolType]
public sealed class PcbBoardTools(InstanceRegistry registry)
{
    private readonly RouteOptimizationReceiptStore plotReceipts = new(registry.StateDirectory);
    private readonly RouteOptimizationReceiptStore enabledLayerReceipts =
        new(registry.StateDirectory, "pcb-enabled-layer-receipts");
    [McpServerTool(Name = "kicad_pcb_nets", ReadOnly = true),
     Description("Read the native PCB net inventory at one exact lifecycle checkpoint. requestJson is GetNets protobuf JSON with an explicit PCB board and optional netclass filters; expectedStateJson must be the matching kicad_document_state observation. The response is a typed native net list paired with unchanged before/after board state."),
     KiCadCapability("pcb-connectivity", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Nets(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Read<GetNets, NetsResponse>(instanceId, requestJson, expectedStateJson, "nets", request => request.Board, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_items_by_net", ReadOnly = true),
     Description("Read pads, tracks, vias and zones belonging to explicitly named native PCB nets. requestJson is GetItemsByNet protobuf JSON with an explicit ItemHeader.document and typed net filters; expectedStateJson must be the matching lifecycle observation. Any-packed items and unchanged before/after state are returned; no route or DRC conclusion is inferred."),
     KiCadCapability("pcb-connectivity", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state, net filter"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ItemsByNet(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Read<GetItemsByNet, GetItemsResponse>(instanceId, requestJson, expectedStateJson, "items-by-net", request => request.Header?.Document, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_items_by_net_class", ReadOnly = true),
     Description("Read pads, tracks, vias and zones belonging to explicitly named native PCB netclasses. requestJson is GetItemsByNetClass protobuf JSON with an explicit ItemHeader.document and typed netclass filters; expectedStateJson must be the matching lifecycle observation. Any-packed items and unchanged before/after state are returned."),
     KiCadCapability("pcb-connectivity", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state, netclass filter"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ItemsByNetClass(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Read<GetItemsByNetClass, GetItemsResponse>(instanceId, requestJson, expectedStateJson, "items-by-net-class", request => request.Header?.Document, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_connected_items", ReadOnly = true),
     Description("Read the native copper connectivity closure for explicit PCB item KIIDs at one lifecycle checkpoint. requestJson is GetConnectedItems protobuf JSON with an explicit ItemHeader.document and source IDs; expectedStateJson must be the matching observation. This exposes native connectivity for route planning without claiming DRC cleanliness."),
     KiCadCapability("pcb-connectivity", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state, source item IDs"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ConnectedItems(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Read<GetConnectedItems, GetItemsResponse>(instanceId, requestJson, expectedStateJson, "connected-items", request => request.Header?.Document, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_design_rules", ReadOnly = true),
     Description("Read the native board design rules and custom-rule compilation status at one exact PCB lifecycle checkpoint. requestJson is GetBoardDesignRules protobuf JSON with an explicit board; expectedStateJson must match the same board and process epoch. Rule data is authoritative input for planners, not proof that a proposed route is valid."),
     KiCadCapability("pcb-rules", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state, custom-rule status"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> DesignRules(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Read<GetBoardDesignRules, BoardDesignRulesResponse>(instanceId, requestJson, expectedStateJson, "design-rules", request => request.Board, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_net_classes", ReadOnly = true),
     Description("Read effective native netclass properties for explicitly named PCB nets at one exact lifecycle checkpoint. documentJson identifies the board because the native GetNetClassForNets request carries only net values; requestJson is GetNetClassForNets protobuf JSON; expectedStateJson must match the board and process epoch. This is authoritative planning input, not route validation."),
     KiCadCapability("pcb-rules", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state, net names"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> NetClasses(string instanceId, string documentJson, string requestJson,
        string expectedStateJson, CancellationToken cancellationToken) => ReadNetClasses(instanceId, documentJson, requestJson, expectedStateJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_enabled_layers", ReadOnly = true),
     Description("Read the native enabled-layer and copper-layer-count configuration at one exact PCB lifecycle checkpoint. requestJson is GetBoardEnabledLayers protobuf JSON with an explicit board; expectedStateJson must match the same board and process epoch."),
     KiCadCapability("pcb-rules", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state, enabled layers"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> EnabledLayers(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Read<GetBoardEnabledLayers, BoardEnabledLayersResponse>(instanceId, requestJson, expectedStateJson, "enabled-layers", request => request.Board, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_stackup", ReadOnly = true),
     Description("Read the complete native PCB stackup at one exact lifecycle checkpoint, including enabled layers, user layer names, copper/dielectric thickness, material, mask and silkscreen properties. requestJson is GetBoardStackup protobuf JSON with an explicit board; expectedStateJson must match the same board and process epoch. This is authoritative planning input for layer changes, impedance and manufacturing; it does not mutate the board or certify a route."),
     KiCadCapability("pcb-rules", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state, stackup layers and materials"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Stackup(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Read<GetBoardStackup, BoardStackupResponse>(instanceId, requestJson, expectedStateJson, "stackup", request => request.Board, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_plot_settings", ReadOnly = true),
     Description("Read native PCB plot settings at one exact lifecycle checkpoint. requestJson is GetBoardPlotSettings protobuf JSON with an explicit board; expectedStateJson must match the same board and process epoch. This is observation only."),
     KiCadCapability("pcb-rules", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state, plot settings"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> PlotSettings(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Read<GetBoardPlotSettings, BoardPlotSettingsResponse>(instanceId, requestJson, expectedStateJson, "plot-settings", request => request.Board, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_plot_settings_set_typed", ReadOnly = false),
     Description("Apply typed PCB plot settings at one exact lifecycle checkpoint and verify normalized native readback. MCP state records dispatch intent and exact terminal results; a lost reply remains explicitly unknown and is never resubmitted."),
     KiCadCapability("pcb-rules", "typed-native-plot-settings-mutation", "explicit PCB document, complete lifecycle state, typed plot settings, operation ID and normalized native readback"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbBoardTypedMcpProcessTests.TypedPcbPlotSettingsMutationPublishesSchemaAndRejectsIncompleteInput")]
    public Task<CallToolResult> SetPlotSettingsTyped(string instanceId,
        PcbPlotSettingsMutationInput input, CancellationToken cancellationToken) => ExecutePlotSettingsMutation(instanceId, input, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_enabled_layers_set_typed", ReadOnly = false),
     Description("Apply typed PCB enabled-layer settings at one exact lifecycle checkpoint and verify normalized native readback. Dispatch intent and terminal results are durable in MCP state; an interrupted native setter remains unknown and is never resubmitted."),
     KiCadCapability("pcb-rules", "typed-native-enabled-layers-mutation", "explicit PCB document, complete lifecycle state, copper-layer count, layer set, operation ID and normalized native readback"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbBoardTypedMcpProcessTests.TypedPcbEnabledLayersMutationPublishesSchemaAndRejectsIncompleteInput")]
    public Task<CallToolResult> SetEnabledLayersTyped(string instanceId,
        PcbEnabledLayersMutationInput input, CancellationToken cancellationToken) => ExecuteEnabledLayersMutation(instanceId, input, cancellationToken);

    private async Task<CallToolResult> ExecuteEnabledLayersMutation(string instanceId,
        PcbEnabledLayersMutationInput input, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input is null || !Guid.TryParseExact(input.OperationId, "D", out Guid operation) || operation == Guid.Empty)
                throw new AutomationException("invalid_operation_id", "operationId must be a canonical nonempty UUID.");
            DocumentSpecifier document = NativeDocumentInput.ToNative(input.Document);
            ValidateBoard(document);
            DocumentLifecycleState expected = DocumentLifecycleInput.ToNative(input.ExpectedState);
            if (!expected.Document.Equals(document) || expected.Scope != DocumentLifecycleScope.DlsPcb
                || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
                throw new AutomationException("invalid_pcb_enabled_layers_state", "ExpectedState must be a complete observation of the same PCB document.");
            if (input.CopperLayerCount == 0 || input.CopperLayerCount % 2 != 0)
                throw new AutomationException("invalid_pcb_enabled_layers", "copperLayerCount must be a positive even number.");
            NativeClient client = registry.Client(instanceId);
            AutomationSession session = await client.HandshakeAsync(cancellationToken);
            if (session.Epoch != expected.ProcessEpoch)
                throw new AutomationException("pcb_instance_changed", "The expected PCB state belongs to another native process epoch.");
            string operationId = operation.ToString("D");
            string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                SchematicJson.Formatter.Format(document) + "\n" + SchematicJson.Formatter.Format(expected)
                + "\n" + input.CopperLayerCount + "\n" + string.Join(",", input.Layers))));
            using FileStream lease = enabledLayerReceipts.Acquire(instanceId, operationId);
            RouteOptimizationReceiptRecord? previous = enabledLayerReceipts.Read(instanceId, operationId);
            if (previous is not null)
            {
                if (!string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new AutomationException("pcb_enabled_layers_operation_reused", "operationId is already bound to different enabled-layer settings.");
                using JsonDocument priorJson = JsonDocument.Parse(previous.ResultJson);
                if (priorJson.RootElement.TryGetProperty("status", out JsonElement status)
                    && status.GetString() == "unknown")
                    throw new AutomationException("pcb_enabled_layers_outcome_unknown", "The native setter may have run before the previous reply was lost; inspect the board before retrying.");
                return RouteOptimizationReceiptStore.ReplayResult(previous);
            }
            DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
            if (!before.Equals(expected))
                throw new AutomationException("pcb_state_changed", "The PCB changed before enabled-layer mutation; inspect a fresh lifecycle state.");
            var request = new SetBoardEnabledLayers { Board = document, CopperLayerCount = input.CopperLayerCount };
            request.Layers.Add(input.Layers);
            enabledLayerReceipts.Write(instanceId, session.Epoch, operationId, fingerprint, JsonSerializer.SerializeToElement(new
            {
                instanceId, processEpoch = session.Epoch, operationId, status = "unknown", nativeMutation = true
            }));
            BoardEnabledLayersResponse normalized = await client.InvokeAsync<SetBoardEnabledLayers, BoardEnabledLayersResponse>(request, cancellationToken);
            DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
            JsonElement result = JsonSerializer.SerializeToElement(new
            {
                instanceId, processEpoch = session.Epoch, operationId, document,
                beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
                requestedCopperLayerCount = input.CopperLayerCount, requestedLayers = input.Layers,
                readback = JsonDocument.Parse(BoardJson.Formatter.Format(normalized)).RootElement.Clone(),
                readbackVerified = normalized.CopperLayerCount == input.CopperLayerCount,
                nativeMutation = true, replaySupported = true, status = "completed"
            });
            enabledLayerReceipts.Write(instanceId, session.Epoch, operationId, fingerprint, result);
            return new CallToolResult { StructuredContent = result,
                Content = [new TextContentBlock { Text = result.GetRawText() }] };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException or ArgumentException)
        {
            var structured = JsonSerializer.SerializeToElement(new
            {
                errorCode = error is AutomationException known ? known.Code : "pcb_enabled_layers_failed",
                errorMessage = error.Message, nativeMutation = false, replaySupported = false
            });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private async Task<CallToolResult> ExecutePlotSettingsMutation(string instanceId,
        PcbPlotSettingsMutationInput input, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input is null || string.IsNullOrWhiteSpace(input.OperationId)
                || !Guid.TryParseExact(input.OperationId, "D", out Guid operation) || operation == Guid.Empty)
                throw new AutomationException("invalid_operation_id", "operationId must be a canonical nonempty UUID.");
            DocumentSpecifier document = NativeDocumentInput.ToNative(input.Document);
            ValidateBoard(document);
            DocumentLifecycleState expected = DocumentLifecycleInput.ToNative(input.ExpectedState);
            if (!expected.Document.Equals(document) || expected.Scope != DocumentLifecycleScope.DlsPcb
                || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
                throw new AutomationException("invalid_pcb_plot_settings_state", "ExpectedState must be a complete observation of the same PCB document.");
            Kiapi.Board.Jobs.BoardPlotSettings settings = input.Settings?.ToNative()
                ?? throw new AutomationException("invalid_pcb_plot_settings", "settings is required.");
            NativeClient client = registry.Client(instanceId);
            AutomationSession session = await client.HandshakeAsync(cancellationToken);
            if (session.Epoch != expected.ProcessEpoch)
                throw new AutomationException("pcb_instance_changed", "The expected PCB state belongs to another native process epoch.");
            string operationId = operation.ToString("D");
            string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                SchematicJson.Formatter.Format(document) + "\n" + SchematicJson.Formatter.Format(expected)
                + "\n" + BoardJson.Formatter.Format(settings))));
            using FileStream lease = plotReceipts.Acquire(instanceId, operationId);
            RouteOptimizationReceiptRecord? previous = plotReceipts.Read(instanceId, operationId);
            if (previous is not null)
            {
                if (!string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new AutomationException("pcb_plot_settings_operation_reused", "operationId is already bound to different plot settings.");
                using JsonDocument priorJson = JsonDocument.Parse(previous.ResultJson);
                if (priorJson.RootElement.TryGetProperty("status", out JsonElement status)
                    && status.GetString() == "unknown")
                    throw new AutomationException("pcb_plot_settings_outcome_unknown", "The native setter may have run before the previous reply was lost; inspect the board before retrying.");
                return RouteOptimizationReceiptStore.ReplayResult(previous);
            }
            DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
            if (!before.Equals(expected))
                throw new AutomationException("pcb_state_changed", "The PCB changed before plot-settings mutation; inspect a fresh lifecycle state.");
            plotReceipts.Write(instanceId, session.Epoch, operationId, fingerprint, JsonSerializer.SerializeToElement(new
            {
                instanceId, processEpoch = session.Epoch, operationId, status = "unknown",
                nativeMutation = true, replaySupported = false,
                errorCode = "pcb_plot_settings_dispatch_unknown"
            }));
            await client.InvokeAsync<SetBoardPlotSettings, Empty>(
                new() { Board = document, PlotSettings = settings }, cancellationToken);
            DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
            BoardPlotSettingsResponse normalized = await client.InvokeAsync<GetBoardPlotSettings, BoardPlotSettingsResponse>(
                new() { Board = document }, cancellationToken);
            string requestedJson = BoardJson.Formatter.Format(settings);
            string normalizedJson = BoardJson.Formatter.Format(normalized.PlotSettings);
            JsonElement result = JsonSerializer.SerializeToElement(new
            {
                instanceId, processEpoch = session.Epoch, operationId, document,
                beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
                requested = JsonDocument.Parse(requestedJson).RootElement.Clone(),
                readback = JsonDocument.Parse(normalizedJson).RootElement.Clone(),
                readbackVerified = normalized.PlotSettings is not null,
                nativeMutation = true, replaySupported = false, status = "completed",
                limitation = "The native setter has no native operation receipt; exact retries are served from MCP state, while an interrupted dispatch remains explicitly unknown."
            });
            plotReceipts.Write(instanceId, session.Epoch, operationId, fingerprint, result);
            return new CallToolResult { StructuredContent = result,
                Content = [new TextContentBlock { Text = result.GetRawText() }] };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException or ArgumentException)
        {
            var structured = JsonSerializer.SerializeToElement(new
            {
                errorCode = error is AutomationException known ? known.Code : "pcb_plot_settings_failed",
                errorMessage = error.Message, nativeMutation = false, replaySupported = false
            });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private async Task<CallToolResult> Read<TRequest, TResponse>(string instanceId, string requestJson,
        string expectedStateJson, string queryKind, Func<TRequest, DocumentSpecifier?> target,
        CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage<TResponse>, new()
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TRequest request;
            try { request = BoardJson.Parser.Parse<TRequest>(requestJson); }
            catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
            { throw new AutomationException("invalid_pcb_query", $"{queryKind} requestJson is not valid board protobuf JSON: {error.Message}"); }
            var document = target(request);
            ValidateBoard(document);
            var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
            if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
                || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
                throw new AutomationException("invalid_pcb_query_state", "expectedStateJson must identify the same PCB and native process epoch.");

            var client = registry.Client(instanceId);
            var session = await client.HandshakeAsync(cancellationToken);
            if (session.Epoch != expected.ProcessEpoch)
                throw new AutomationException("pcb_instance_changed", "The native process epoch changed before the query.");
            var before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
            if (!before.Equals(expected))
                throw new AutomationException("pcb_state_changed", "The PCB changed before the query; inspect a fresh lifecycle state and retry.");
            var response = await client.InvokeAsync<TRequest, TResponse>(request, cancellationToken);
            var after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
            if (!after.Equals(expected))
                throw new AutomationException("pcb_query_state_changed", "The board changed while reading; discard the query and its result.");

            string responseJson = BoardJson.Formatter.Format(response);
            if (response is GetItemsResponse items && items.Items.Count > 8192)
                throw new AutomationException("pcb_query_oversized", "The native query returned more than 8192 typed items; narrow the filter before retrying.");
            if (Encoding.UTF8.GetByteCount(responseJson) > 32 * 1024 * 1024)
                throw new AutomationException("pcb_query_oversized", "The native query result exceeds the bounded MCP response budget; narrow the filter before retrying.");
            using var parsed = JsonDocument.Parse(responseJson);
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, processEpoch = session.Epoch, query = queryKind, document,
                beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
                response = parsed.RootElement.Clone(), nativeQuery = true, boardMutation = false
            });
            return new CallToolResult { StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException or ArgumentException)
        {
            var structured = JsonSerializer.SerializeToElement(new
            {
                errorCode = error is AutomationException known ? known.Code : "pcb_query_failed",
                errorMessage = error.Message, query = queryKind
            });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private async Task<CallToolResult> ReadNetClasses(string instanceId, string documentJson, string requestJson,
        string expectedStateJson, CancellationToken cancellationToken)
    {
        try
        {
            var document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson);
            ValidateBoard(document);
            var request = BoardJson.Parser.Parse<GetNetClassForNets>(requestJson);
            var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
            if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
                || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
                throw new AutomationException("invalid_pcb_query_state", "expectedStateJson must identify the same PCB and native process epoch.");
            var client = registry.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
            if (session.Epoch != expected.ProcessEpoch)
                throw new AutomationException("pcb_instance_changed", "The native process epoch changed before the query.");
            var before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, cancellationToken);
            if (!before.Equals(expected))
                throw new AutomationException("pcb_state_changed", "The PCB changed before the netclass query; inspect it again.");
            var response = await client.InvokeAsync<GetNetClassForNets, NetClassForNetsResponse>(request, cancellationToken);
            var after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, cancellationToken);
            if (!after.Equals(expected))
                throw new AutomationException("pcb_query_state_changed", "The board changed while reading netclasses; discard the result.");
            using var parsed = JsonDocument.Parse(BoardJson.Formatter.Format(response));
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, processEpoch = session.Epoch, query = "net-classes", document,
                beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
                response = parsed.RootElement.Clone(), nativeQuery = true, boardMutation = false
            });
            return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or ArgumentException)
        {
            var structured = JsonSerializer.SerializeToElement(new
            { errorCode = error is AutomationException known ? known.Code : "pcb_query_failed", errorMessage = error.Message, query = "net-classes" });
            return new CallToolResult { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private static void ValidateBoard(DocumentSpecifier? document)
    {
        DocumentStateTools.ValidateTarget(document);
        if (document!.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_query_target", "The query target must be an explicit PCB document.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbPlotSettingsMutationInput
{
    public NativeDocumentInput Document { get; set; } = new();
    public DocumentLifecycleInput ExpectedState { get; set; } = new();
    public BoardPlotSettingsInput Settings { get; set; } = new();
    public string OperationId { get; set; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbEnabledLayersMutationInput
{
    public NativeDocumentInput Document { get; set; } = new();
    public DocumentLifecycleInput ExpectedState { get; set; } = new();
    [JsonConverter(typeof(BoardLayerListJsonConverter))]
    public List<BoardLayer> Layers { get; set; } = [];
    public uint CopperLayerCount { get; set; }
    public string OperationId { get; set; } = "";
}
