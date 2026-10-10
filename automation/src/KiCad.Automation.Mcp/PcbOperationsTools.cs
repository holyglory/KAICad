using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Commands;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Board operations bind their target and lifecycle checkpoint explicitly.
/// Native operation receipts and editor responsiveness are separate evidence;
/// neither makes a transport retry safe.
/// </summary>
[McpServerToolType]
public sealed class PcbOperationsTools(InstanceRegistry registry)
{
    private static readonly SemaphoreSlim MutationGate = new(1, 1);
    private static readonly Dictionary<string, StoredReceipt> Receipts = new(StringComparer.Ordinal);

    [McpServerTool(Name = "kicad_pcb_import_netlist"),
     Description("Import a schematic netlist into an explicitly identified PCB through KiCad's native netlist updater. Provide exactly one of requestJson (legacy ImportNetlist protobuf JSON) or request (structured PCB netlist input), and exactly one of expectedStateJson or expectedState. operationId is a caller-owned correlation UUID. Dry runs are required for planning. Native receipts remain available while the KiCad process lives; interactive asynchronous work remains explicit."),
     KiCadCapability("pcb-operations", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state, operation correlation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ImportNetlistStructured(string instanceId, string operationId,
        CancellationToken cancellationToken, string? requestJson = null, PcbImportNetlistInput? request = null,
        string? expectedStateJson = null, DocumentLifecycleInput? expectedState = null) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string resolvedRequest = ResolveRequestJson(requestJson, request, operationId);
        string resolvedState = ResolveExpectedStateJson(expectedStateJson, expectedState);
        return await ImportNetlist(instanceId, resolvedRequest, resolvedState, operationId, cancellationToken);
    });

    // Legacy CLR entry point.  It intentionally remains callable by existing
    // clients and tests, while MCP discovery exposes the structured wrapper.
    public Task<CallToolResult> ImportNetlist(string instanceId, string requestJson, string expectedStateJson,
        string operationId, CancellationToken cancellationToken) =>
        Execute(() => WithReceipt(instanceId, operationId, "netlist-import", requestJson, expectedStateJson,
            () => ImportNetlistCore(instanceId, requestJson, expectedStateJson, operationId, cancellationToken)));

    [McpServerTool(Name = "kicad_pcb_refill_zones"),
     Description("Refill all or selected copper zones on an explicitly identified PCB through KiCad's native zone filler. Provide exactly one of requestJson (legacy RefillZones protobuf JSON) or request (structured PCB refill input), and exactly one of expectedStateJson or expectedState. operationId is a caller-owned correlation UUID. Selected zones and headless all-zone fills complete synchronously and retain native-process receipts; interactive all-zone fills remain explicitly asynchronous."),
     KiCadCapability("pcb-operations", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state, operation correlation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> RefillZonesStructured(string instanceId, string operationId,
        CancellationToken cancellationToken, string? requestJson = null, PcbRefillZonesInput? request = null,
        string? expectedStateJson = null, DocumentLifecycleInput? expectedState = null) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string resolvedRequest = ResolveRequestJson(requestJson, request, operationId);
        string resolvedState = ResolveExpectedStateJson(expectedStateJson, expectedState);
        return await RefillZones(instanceId, resolvedRequest, resolvedState, operationId, cancellationToken);
    });

    // Legacy CLR entry point retained for callers that already provide
    // protobuf JSON.  It shares the exact receipt and native transaction path.
    public Task<CallToolResult> RefillZones(string instanceId, string requestJson, string expectedStateJson,
        string operationId, CancellationToken cancellationToken) =>
        Execute(() => WithReceipt(instanceId, operationId, "zone-refill", requestJson, expectedStateJson,
            () => RefillZonesCore(instanceId, requestJson, expectedStateJson, operationId, cancellationToken)));

    [McpServerTool(Name = "kicad_pcb_operation_receipt", ReadOnly = true),
     Description("Inspect an exact PCB netlist-import or zone-refill receipt after a lost reply without resubmitting the operation. A receipt retained by KiCad can be read after an MCP reconnect; an MCP-process receipt is only a historical fallback when an older native peer lacks the receipt handler. The current native receipt is always queried first. Instance, epoch and connection failures remain errors rather than receipt absence."),
     KiCadCapability("pcb-operations", "compiled-mcp plus native-board-api", "operation UUID and native-process PCB operation receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Receipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateOperationId(operationId);
        ValidateEpoch(expectedProcessEpoch);
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId is required to inspect a PCB operation receipt.");
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            NativeClient client = registry.Client(instanceId);
            AutomationSession session = await client.HandshakeAsync(cancellationToken);
            if (session.Epoch != expectedProcessEpoch)
                throw new AutomationException("stale_process_epoch", "The PCB operation receipt belongs to a different native process epoch.");

            try
            {
                NativeCapabilityPcbReadOperationResponse native = await client.InvokeAsync<NativeCapabilityPcbReadOperation,
                    NativeCapabilityPcbReadOperationResponse>(new() { OperationId = operationId }, cancellationToken);
                if (native.Found && !string.Equals(native.OperationId, operationId, StringComparison.Ordinal))
                    throw new AutomationException("wrong_operation_receipt", "KiCad returned a receipt for a different PCB operation.");
                if (native.Found && native.ProcessEpoch.Length != 0
                    && !string.Equals(native.ProcessEpoch, expectedProcessEpoch, StringComparison.Ordinal))
                    throw new AutomationException("stale_process_epoch", "The native PCB operation receipt belongs to a different process epoch.");
                return Data(new { instanceId, processEpoch = session.Epoch, operationId, found = native.Found,
                    receiptScope = "native-process", nativeReceiptAvailable = native.Found,
                    nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(native)) });
            }
            catch (NativeApiException error) when (
                error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnhandled
                || error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnimplemented)
            {
                // Only a genuinely older peer may use the process-local result.
                if (Receipts.TryGetValue(operationId, out StoredReceipt? stored))
                {
                    JsonElement payload = stored.Result.StructuredContent ?? JsonSerializer.SerializeToElement(new { });
                    string? processEpoch = payload.TryGetProperty("processEpoch", out JsonElement epoch)
                        ? epoch.GetString() : null;
                    if (!string.Equals(processEpoch, expectedProcessEpoch, StringComparison.Ordinal))
                        throw new AutomationException("stale_process_epoch", "The PCB operation receipt belongs to a different native process epoch.");
                    string? receiptInstance = payload.TryGetProperty("instanceId", out JsonElement instance)
                        ? instance.GetString() : null;
                    if (!string.Equals(receiptInstance, instanceId, StringComparison.Ordinal))
                        throw new AutomationException("wrong_instance_receipt", "The PCB operation receipt belongs to a different MCP instance.");
                    return Data(new { instanceId, processEpoch, operationId, found = true,
                        receiptScope = "mcp-process", nativeReceiptAvailable = false,
                        receiptObservation = "historical-process-cache", receipt = payload });
                }

                return Data(new { instanceId, processEpoch = session.Epoch, operationId, found = (bool?)null,
                    receiptScope = "mcp-process", nativeReceiptAvailable = false,
                    receiptObservation = "native-unavailable", nativeReceiptStatus = error.Status,
                    nativeReceiptUnavailableReason = "receipt-handler-unavailable" });
            }
        }
        finally { MutationGate.Release(); }
    });

    [McpServerTool(Name = "kicad_pcb_operation_wait", ReadOnly = true),
     Description("Observe one accepted zone-refill operation using its exact native terminal receipt and PCB lifecycle state. documentJson, expectedProcessEpoch and operationId bind the read to the document and process; timeoutMs is bounded to 0..15000 milliseconds. completionConfirmed is true only when a fresh terminal native receipt reports the explicit completed zone-fill outcome with known commit state and no error. A responsive lifecycle read confirms only lifecycleSettled; queued work remains pending until its terminal receipt is available. Timeout returns pending."),
     KiCadCapability("pcb-operations", "compiled-mcp plus native-board-api", "operation UUID, explicit PCB document, process epoch, bounded native busy-settlement observation"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> Wait(string instanceId, string expectedProcessEpoch, string documentJson,
        string operationId, int timeoutMs, CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateOperationId(operationId);
        ValidateEpoch(expectedProcessEpoch);
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId is required to wait for a PCB operation.");
        if (timeoutMs is < 0 or > 15_000)
            throw new AutomationException("invalid_operation_wait_timeout", "timeoutMs must be between 0 and 15000 milliseconds.");

        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_pcb_operation_target");
        ValidateBoard(document, "invalid_pcb_operation_target");
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expectedProcessEpoch)
            throw new AutomationException("stale_process_epoch", "The PCB operation belongs to a different native process epoch.");

                NativeCapabilityPcbReadOperationResponse nativeReceipt = await ReadNativeReceipt(client, operationId, cancellationToken);
                if (!nativeReceipt.Found)
                    return Data(new { instanceId, processEpoch = session.Epoch, operationId, found = false,
                        operationOutcome = "not_found", completionConfirmed = false, nativeReceiptAvailable = false });
                PcbOperationsContract.ValidateNativeReceiptIdentity(nativeReceipt, operationId,
                    expectedProcessEpoch, document, "zone-refill");

        string nativeReceiptJson = SchematicJson.Formatter.Format(nativeReceipt);
        if (HasTerminalZoneOutcome(nativeReceipt))
        {
            DocumentLifecycleState settled = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
            bool completed = IsSuccessfulZoneOutcome(nativeReceipt);
            return Data(new { instanceId, processEpoch = session.Epoch, operationId, found = true,
                operation = nativeReceipt.Operation, operationOutcome = ZoneOutcomeName((int) nativeReceipt.ZoneFillOutcome),
                completionConfirmed = completed,
                completionSource = "native-operation-receipt",
                nativeReceiptAvailable = true, nativeReceiptCompleted = completed,
                terminal = nativeReceipt.Terminal, zoneFillOutcome = ZoneOutcomeName((int) nativeReceipt.ZoneFillOutcome),
                commitStateKnown = nativeReceipt.CommitStateKnown, rollbackCompleted = nativeReceipt.RollbackCompleted,
                failureStage = nativeReceipt.FailureStage, errorCode = nativeReceipt.ErrorCode,
                errorMessage = nativeReceipt.ErrorMessage,
                nativeReceipt = JsonSerializer.Deserialize<JsonElement>(nativeReceiptJson),
                lifecycleSettled = true, lifecycleState = SchematicJson.Formatter.Format(settled) });
        }

        Stopwatch timer = Stopwatch.StartNew();
        int delayMs = 25;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.ElapsedMilliseconds >= timeoutMs)
                break;

            int remaining = timeoutMs - (int)Math.Min(timer.ElapsedMilliseconds, timeoutMs);
            await Task.Delay(Math.Min(delayMs, remaining), cancellationToken);
            nativeReceipt = await ReadNativeReceipt(client, operationId, cancellationToken);
            nativeReceiptJson = SchematicJson.Formatter.Format(nativeReceipt);
            if (nativeReceipt.Found)
                PcbOperationsContract.ValidateNativeReceiptIdentity(nativeReceipt, operationId,
                    expectedProcessEpoch, document, "zone-refill");
            if (HasTerminalZoneOutcome(nativeReceipt))
            {
                DocumentLifecycleState settled = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                    new() { Document = document }, cancellationToken);
                bool completed = IsSuccessfulZoneOutcome(nativeReceipt);
                return Data(new { instanceId, processEpoch = session.Epoch, operationId,
                    found = true, operation = nativeReceipt.Operation,
                    operationOutcome = ZoneOutcomeName((int) nativeReceipt.ZoneFillOutcome),
                    completionConfirmed = completed, completionSource = "native-operation-receipt",
                    nativeReceiptAvailable = true, nativeReceiptCompleted = completed,
                    terminal = nativeReceipt.Terminal, zoneFillOutcome = ZoneOutcomeName((int) nativeReceipt.ZoneFillOutcome),
                    commitStateKnown = nativeReceipt.CommitStateKnown, rollbackCompleted = nativeReceipt.RollbackCompleted,
                    failureStage = nativeReceipt.FailureStage, errorCode = nativeReceipt.ErrorCode,
                    errorMessage = nativeReceipt.ErrorMessage,
                    nativeReceipt = JsonSerializer.Deserialize<JsonElement>(nativeReceiptJson),
                    lifecycleSettled = true, lifecycleState = SchematicJson.Formatter.Format(settled) });
            }
            try
            {
                // A successful lifecycle read proves the editor is responsive.
                // It does not prove the queued fill succeeded or update the
                // incomplete native operation receipt.
                DocumentLifecycleState settled = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                    new() { Document = document }, cancellationToken);
                return Data(new { instanceId, processEpoch = session.Epoch, operationId,
                    found = true, operation = nativeReceipt.Operation, operationOutcome = "pending",
                    completionConfirmed = false, nativeReceiptAvailable = true, nativeReceiptCompleted = false,
                    terminal = nativeReceipt.Terminal, zoneFillOutcome = ZoneOutcomeName((int) nativeReceipt.ZoneFillOutcome),
                    commitStateKnown = nativeReceipt.CommitStateKnown, rollbackCompleted = nativeReceipt.RollbackCompleted,
                    failureStage = nativeReceipt.FailureStage, errorCode = nativeReceipt.ErrorCode,
                    errorMessage = nativeReceipt.ErrorMessage,
                    nativeReceipt = JsonSerializer.Deserialize<JsonElement>(nativeReceiptJson),
                    lifecycleSettled = true, lifecycleState = SchematicJson.Formatter.Format(settled) });
            }
            catch (NativeApiException error) when (error.Status == 7)
            {
                // AS_BUSY leaves lifecycle settlement unobserved.
                delayMs = Math.Min(delayMs * 2, 1000);
            }
        }

        return Data(new { instanceId, processEpoch = session.Epoch, operationId,
            found = true, operation = nativeReceipt.Operation, operationOutcome = "pending",
            completionConfirmed = false, nativeReceiptAvailable = true, nativeReceiptCompleted = false,
            terminal = nativeReceipt.Terminal, zoneFillOutcome = ZoneOutcomeName((int) nativeReceipt.ZoneFillOutcome),
            commitStateKnown = nativeReceipt.CommitStateKnown, rollbackCompleted = nativeReceipt.RollbackCompleted,
            failureStage = nativeReceipt.FailureStage, errorCode = nativeReceipt.ErrorCode,
            errorMessage = nativeReceipt.ErrorMessage,
            nativeReceipt = JsonSerializer.Deserialize<JsonElement>(nativeReceiptJson),
            lifecycleSettled = false, timedOut = true });
    });

    [McpServerTool(Name = "kicad_pcb_sync_selection"),
     Description("Synchronize explicitly described PCB selections in the attached KiCad editor. documentJson and expectedStateJson identify the board checkpoint; requestJson is SyncSelection protobuf JSON; operationId is a caller correlation UUID. This changes GUI selection and optional net highlighting only, never board design data. It is unavailable in headless sessions and is not a board mutation receipt."),
     KiCadCapability("pcb-operations", "compiled-mcp plus native-gui-api", "explicit PCB document, process epoch, lifecycle state, operation correlation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> SyncSelection(string instanceId, string documentJson, string requestJson,
        string expectedStateJson, string operationId, CancellationToken cancellationToken) =>
        Execute(() => CrossProbeCore<SyncSelection, SyncSelectionResponse>(instanceId, documentJson, requestJson,
            expectedStateJson, operationId, "sync-selection", cancellationToken));

    [McpServerTool(Name = "kicad_pcb_highlight_nets"),
     Description("Highlight explicitly named PCB nets in the attached KiCad editor. documentJson and expectedStateJson identify the board checkpoint; requestJson is HighlightNets protobuf JSON; operationId is a caller correlation UUID. This changes GUI highlighting only, never board design data. It is unavailable in headless sessions and is not a board mutation receipt."),
     KiCadCapability("pcb-operations", "compiled-mcp plus native-gui-api", "explicit PCB document, process epoch, lifecycle state, operation correlation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> HighlightNets(string instanceId, string documentJson, string requestJson,
        string expectedStateJson, string operationId, CancellationToken cancellationToken) =>
        Execute(() => CrossProbeCore<HighlightNets, HighlightNetsResponse>(instanceId, documentJson, requestJson,
            expectedStateJson, operationId, "highlight-nets", cancellationToken));

    private async Task<CallToolResult> ImportNetlistCore(string instanceId, string requestJson,
        string expectedStateJson, string operationId, CancellationToken cancellationToken)
    {
        ValidateOperationId(operationId);
        ImportNetlist request = Parse<ImportNetlist>(requestJson, "invalid_pcb_netlist_request");
        request.OperationId = operationId;
        ValidateBoard(request.Board, "invalid_pcb_netlist_target");
        ValidateNetlistPath(request.Board, request.NetlistPath);
        if ((int)request.MatchMode is < 0 or > 2)
            throw new AutomationException("invalid_pcb_netlist_request", "matchMode must be UUID or reference matching.");
        var (client, session, before) = await Admit(instanceId, request.Board, expectedStateJson, "netlist import", cancellationToken);
        ImportNetlistResponse response;
        try
        {
            response = await client.InvokeAsync<ImportNetlist, ImportNetlistResponse>(request, cancellationToken);
        }
        catch (NngException error)
        {
            throw MutationTransport(error, "netlist import", operationId);
        }
        var after = await ReadAfter(client, request.Board, "netlist import", operationId, cancellationToken);
        NativeCapabilityPcbReadOperationResponse nativeReceipt = await ReadNativeReceipt(client, operationId, cancellationToken);
        PcbOperationsContract.ValidateNativeReceiptIdentity(nativeReceipt, operationId,
            session.Epoch, request.Board, "netlist-import");
        bool changed = !before.Equals(after);
        if (request.DryRun && changed)
            throw new AutomationException("pcb_netlist_dry_run_changed", "The native dry-run changed the board; discard the result and inspect the board before any retry.");

        bool reportedErrors = response.ErrorCount != 0;
        var payload = new
        {
            instanceId, processEpoch = session.Epoch, operationId, operationReceipt = true,
            nativeReceiptAvailable = nativeReceipt.Found, receiptScope = nativeReceipt.Found ? "native-process" : "mcp-process", replayableWithinProcess = true,
            nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeReceipt)),
            operationOutcome = reportedErrors ? "reported_errors" : "completed",
            document = request.Board, beforeState = SchematicJson.Formatter.Format(before),
            afterState = SchematicJson.Formatter.Format(after), response = JsonDocument.Parse(BoardJson.Formatter.Format(response)).RootElement.Clone(),
            dryRun = request.DryRun, boardMutation = !request.DryRun && changed,
            mutationConfirmed = !request.DryRun && !reportedErrors, transportRetrySafe = false
        };
        return Data(payload, reportedErrors, reportedErrors ? "pcb_netlist_import_reported_errors" : null);
    }

    private async Task<CallToolResult> RefillZonesCore(string instanceId, string requestJson,
        string expectedStateJson, string operationId, CancellationToken cancellationToken)
    {
        ValidateOperationId(operationId);
        RefillZones request = Parse<RefillZones>(requestJson, "invalid_pcb_refill_request");
        request.OperationId = operationId;
        ValidateBoard(request.Board, "invalid_pcb_refill_target");
        if (request.Zones.Count > 4096 || request.Zones.Any(zone => !CanonicalUuid(zone.Value)))
            throw new AutomationException("invalid_pcb_refill_request", "zones must contain at most 4096 canonical zone KIIDs.");
        var (client, session, before) = await Admit(instanceId, request.Board, expectedStateJson, "zone refill", cancellationToken);
        try
        {
            await client.InvokeAsync<RefillZones, Empty>(request, cancellationToken);
        }
        catch (NngException error)
        {
            throw MutationTransport(error, "zone refill", operationId);
        }
        var after = await ReadAfter(client, request.Board, "zone refill", operationId, cancellationToken);
        NativeCapabilityPcbReadOperationResponse nativeReceipt = await ReadNativeReceipt(client, operationId, cancellationToken);
        PcbOperationsContract.ValidateNativeReceiptIdentity(nativeReceipt, operationId,
            session.Epoch, request.Board, "zone-refill");
        bool mayBeAsynchronous = request.Zones.Count == 0;
        bool terminal = HasTerminalZoneOutcome(nativeReceipt);
        bool completed = terminal && IsSuccessfulZoneOutcome(nativeReceipt);
        return Data(new
        {
            instanceId, processEpoch = session.Epoch, operationId, operationReceipt = true,
            nativeReceiptAvailable = nativeReceipt.Found, receiptScope = nativeReceipt.Found ? "native-process" : "mcp-process", replayableWithinProcess = true,
            nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeReceipt)),
            operationOutcome = terminal ? ZoneOutcomeName((int) nativeReceipt.ZoneFillOutcome)
                : mayBeAsynchronous ? "accepted" : "indeterminate",
            document = request.Board, beforeState = SchematicJson.Formatter.Format(before),
            afterState = SchematicJson.Formatter.Format(after), selectedZoneCount = request.Zones.Count,
            mayBeAsynchronous, terminal, zoneFillOutcome = ZoneOutcomeName((int) nativeReceipt.ZoneFillOutcome),
            primaryFillOutcome = ZoneOutcomeName((int) nativeReceipt.PrimaryFillOutcome),
            generatorRefillOutcome = ZoneOutcomeName((int) nativeReceipt.GeneratorRefillOutcome),
            generatorRefillRequired = nativeReceipt.GeneratorRefillRequired,
            primaryCommitApplied = nativeReceipt.PrimaryCommitApplied,
            generatorRefillCommitApplied = nativeReceipt.GeneratorRefillCommitApplied,
            rollbackCompleted = nativeReceipt.RollbackCompleted,
            commitStateKnown = nativeReceipt.CommitStateKnown,
            failureStage = nativeReceipt.FailureStage, errorCode = nativeReceipt.ErrorCode,
            errorMessage = nativeReceipt.ErrorMessage,
            boardMutation = !before.Equals(after), mutationConfirmed = completed,
            transportRetrySafe = false
        });
    }

    private static Task<NativeCapabilityPcbReadOperationResponse> ReadNativeReceipt(
        NativeClient client, string operationId, CancellationToken cancellationToken)
        => client.InvokeAsync<NativeCapabilityPcbReadOperation, NativeCapabilityPcbReadOperationResponse>(
            new() { OperationId = operationId }, cancellationToken);

    private static bool HasTerminalZoneOutcome(NativeCapabilityPcbReadOperationResponse receipt)
        => receipt.Terminal && (int) receipt.ZoneFillOutcome != 0;

    private static bool IsSuccessfulZoneOutcome(NativeCapabilityPcbReadOperationResponse receipt)
        => HasTerminalZoneOutcome(receipt) && (int) receipt.ZoneFillOutcome == 2
            && receipt.CommitStateKnown && receipt.ErrorCode.Length == 0;

    private static string ZoneOutcomeName(int value) => value switch
    {
        1 => "not_run",
        2 => "completed",
        3 => "cancelled",
        4 => "not_converged",
        5 => "unchanged_or_declined",
        6 => "failed",
        _ => "unknown"
    };

    private async Task<CallToolResult> CrossProbeCore<TRequest, TResponse>(string instanceId, string documentJson,
        string requestJson, string expectedStateJson, string operationId, string operation,
        CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage<TResponse>, new()
    {
        ValidateOperationId(operationId);
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_pcb_cross_probe_target");
        ValidateBoard(document, "invalid_pcb_cross_probe_target");
        TRequest request = Parse<TRequest>(requestJson, "invalid_pcb_cross_probe_request");
        ValidateCrossProbeRequest(request);
        var (client, session, before) = await Admit(instanceId, document, expectedStateJson, operation, cancellationToken);
        TResponse response;
        try
        {
            response = await client.InvokeAsync<TRequest, TResponse>(request, cancellationToken);
        }
        catch (NngException error)
        {
            throw MutationTransport(error, operation, operationId);
        }
        var after = await ReadAfter(client, document, operation, operationId, cancellationToken);
        if (!before.Equals(after))
            throw new AutomationException("pcb_cross_probe_changed_board", "Cross-probe changed board data; discard the GUI result and inspect the board.");
        int status = response switch
        {
            SyncSelectionResponse selection => (int)selection.Status,
            HighlightNetsResponse highlight => (int)highlight.Status,
            _ => 0
        };
        bool ok = status == 1;
        return Data(new
        {
            instanceId, processEpoch = session.Epoch, operationId, operation, document,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            response = JsonDocument.Parse(SchematicJson.Formatter.Format(response)).RootElement.Clone(),
            nativeGuiOnly = true, boardMutation = false, selectionChanged = ok, nativeReceiptAvailable = false,
            operationOutcome = ok ? "completed" : "rejected", transportRetrySafe = false
        }, !ok, ok ? null : "pcb_cross_probe_rejected");
    }

    private static void ValidateCrossProbeRequest<TRequest>(TRequest request)
        where TRequest : class, IMessage<TRequest>, new()
    {
        switch (request)
        {
            case SyncSelection selection:
                if (selection.Context is not (SyncSelectionContext.SscImplicit or SyncSelectionContext.SscExplicit))
                    throw new AutomationException("invalid_pcb_cross_probe_request",
                        "SyncSelection.context must be SSC_IMPLICIT or SSC_EXPLICIT.");
                if (selection.Mode is not (SyncSelectionMode.SsmItemsOnly or SyncSelectionMode.SsmItemsAndNets))
                    throw new AutomationException("invalid_pcb_cross_probe_request",
                        "SyncSelection.mode must be SSM_ITEMS_ONLY or SSM_ITEMS_AND_NETS.");
                if (selection.Items.Count > 4096)
                    throw new AutomationException("invalid_pcb_cross_probe_request",
                        "SyncSelection.items must contain at most 4096 selection specs.");
                if (selection.FocusItem is not null)
                    ValidateSelectionSpec(selection.FocusItem, "focusItem");
                for (int index = 0; index < selection.Items.Count; index++)
                    ValidateSelectionSpec(selection.Items[index], $"items[{index}]");
                break;

            case HighlightNets highlight:
                if (highlight.NetName.Count > 4096 || highlight.NetName.Any(string.IsNullOrWhiteSpace))
                    throw new AutomationException("invalid_pcb_cross_probe_request",
                        "HighlightNets.netName must contain at most 4096 nonempty net names.");
                break;
        }
    }

    private static void ValidateSelectionSpec(SelectionSpec? spec, string field)
    {
        if (spec is null)
            throw new AutomationException("invalid_pcb_cross_probe_request", $"{field} must name a footprint, pad or sheet path.");

        switch (spec.SpecCase)
        {
            case SelectionSpec.SpecOneofCase.Footprint when !string.IsNullOrWhiteSpace(spec.Footprint.Reference):
            case SelectionSpec.SpecOneofCase.Pad when !string.IsNullOrWhiteSpace(spec.Pad.Reference)
                && !string.IsNullOrWhiteSpace(spec.Pad.Number):
                return;
            case SelectionSpec.SpecOneofCase.SheetPath when spec.SheetPath.Path.Count > 0
                && spec.SheetPath.Path.All(id => CanonicalUuid(id.Value)):
                return;
            default:
                throw new AutomationException("invalid_pcb_cross_probe_request",
                    $"{field} must name a footprint, pad or sheet path with complete identity fields.");
        }
    }

    private static string ResolveRequestJson(string? legacyJson, PcbImportNetlistInput? typed,
        string operationId)
    {
        bool hasLegacy = legacyJson is not null;
        bool hasTyped = typed is not null;
        if (hasLegacy == hasTyped)
            throw new AutomationException("invalid_pcb_operation_input",
                "Provide exactly one PCB netlist request: requestJson or request.");
        if (hasLegacy)
        {
            if (string.IsNullOrWhiteSpace(legacyJson))
                throw new AutomationException("invalid_pcb_operation_input",
                    "requestJson must be a nonempty protobuf JSON document.");
            return legacyJson!;
        }
        return BoardJson.Formatter.Format(typed!.ToNative(operationId));
    }

    private static string ResolveRequestJson(string? legacyJson, PcbRefillZonesInput? typed,
        string operationId)
    {
        bool hasLegacy = legacyJson is not null;
        bool hasTyped = typed is not null;
        if (hasLegacy == hasTyped)
            throw new AutomationException("invalid_pcb_operation_input",
                "Provide exactly one PCB refill request: requestJson or request.");
        if (hasLegacy)
        {
            if (string.IsNullOrWhiteSpace(legacyJson))
                throw new AutomationException("invalid_pcb_operation_input",
                    "requestJson must be a nonempty protobuf JSON document.");
            return legacyJson!;
        }
        return BoardJson.Formatter.Format(typed!.ToNative(operationId));
    }

    private static string ResolveExpectedStateJson(string? legacyJson, DocumentLifecycleInput? typed)
    {
        bool hasLegacy = legacyJson is not null;
        bool hasTyped = typed is not null;
        if (hasLegacy == hasTyped)
            throw new AutomationException("invalid_pcb_operation_input",
                "Provide exactly one PCB lifecycle observation: expectedStateJson or expectedState.");
        if (hasLegacy)
        {
            if (string.IsNullOrWhiteSpace(legacyJson))
                throw new AutomationException("invalid_pcb_operation_input",
                    "expectedStateJson must be a nonempty lifecycle observation.");
            return legacyJson!;
        }
        return SchematicJson.Formatter.Format(DocumentLifecycleInput.ToNative(typed));
    }

    private async Task<(NativeClient Client, AutomationSession Session, DocumentLifecycleState Before)> Admit(
        string instanceId, DocumentSpecifier document, string expectedStateJson, string operation,
        CancellationToken cancellationToken)
    {
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_pcb_operation_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_pcb_operation_state", $"expectedStateJson must identify the same PCB and native process epoch for {operation}.");
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The expected PCB state belongs to another native process epoch.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", $"The board changed before {operation}; inspect a fresh lifecycle state and retry.");
        return (client, session, before);
    }

    private static async Task<DocumentLifecycleState> ReadAfter(NativeClient client, DocumentSpecifier document,
        string operation, string operationId, CancellationToken cancellationToken)
    {
        try
        {
            return await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
        }
        catch (NngException error)
        {
            throw MutationTransport(error, operation, operationId);
        }
        catch (NativeApiException error)
        {
            throw new AutomationException("operation_outcome_unknown",
                $"KiCad accepted {operation} operation {operationId} but lifecycle verification failed ({error.Message}). Inspect the board before retrying; the native request has no retained receipt.");
        }
    }

    private static T Parse<T>(string json, string code) where T : IMessage<T>, new()
    {
        try { return BoardOrSchematic<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException(code, $"Request JSON is not valid protobuf JSON: {error.Message}"); }
    }

    private static T BoardOrSchematic<T>(string json) where T : IMessage<T>, new()
    {
        // BoardJson carries the board command and Any registry. Commands without
        // Any are also accepted by it; schematic lifecycle messages need the
        // broader registry, so try it second.
        try { return BoardJson.Parser.Parse<T>(json); }
        catch (InvalidOperationException) { return SchematicJson.Parser.Parse<T>(json); }
        catch (InvalidProtocolBufferException) { return SchematicJson.Parser.Parse<T>(json); }
        catch (InvalidJsonException) { return SchematicJson.Parser.Parse<T>(json); }
    }

    private static void ValidateBoard(DocumentSpecifier? document, string code)
    {
        try { DocumentStateTools.ValidateTarget(document); }
        catch (AutomationException error) { throw new AutomationException(code, error.Message); }
        if (document!.Type != DocumentType.DoctypePcb)
            throw new AutomationException(code, "The operation target must be an explicit PCB document.");
    }

    private static void ValidateNetlistPath(DocumentSpecifier board, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0'))
            throw new AutomationException("invalid_pcb_netlist_path", "netlistPath must be a nonempty path shorter than 4096 bytes.");
        string root = Path.GetFullPath(board.Project.Path);
        string full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(root, path));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(full, root, StringComparison.Ordinal))
            throw new AutomationException("invalid_pcb_netlist_path", "netlistPath must remain inside the explicit project directory.");
        if (!File.Exists(full))
            throw new AutomationException("invalid_pcb_netlist_path", $"The netlist file does not exist: {full}");
    }

    private static void ValidateOperationId(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "Provide a nonempty operation UUID in canonical D format.");
    }

    private static void ValidateEpoch(string processEpoch)
    {
        if (!Guid.TryParseExact(processEpoch, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_process_epoch", "Provide the canonical native process epoch.");
    }

    private static async Task<CallToolResult> WithReceipt(string instanceId, string operationId,
        string operation, string requestJson, string expectedStateJson, Func<Task<CallToolResult>> action)
    {
        ValidateOperationId(operationId);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            instanceId + "\n" + operation + "\n" + requestJson + "\n" + expectedStateJson)));
        await MutationGate.WaitAsync();
        try
        {
            if (Receipts.TryGetValue(operationId, out StoredReceipt? existing))
            {
                if (existing.Fingerprint != fingerprint)
                    throw new AutomationException("operation_id_reused", "The operation ID is already bound to a different PCB operation.");
                return existing.Result;
            }
            CallToolResult result = await action();
            // A native response that reports import errors is still a terminal,
            // inspectable outcome. Retain it as a receipt; transport exceptions
            // never reach this point and remain indeterminate.
            Receipts[operationId] = new(fingerprint, result);
            return result;
        }
        finally { MutationGate.Release(); }
    }

    private static bool CanonicalUuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;

    private static AutomationException MutationTransport(NngException error, string operation, string operationId) =>
        new(error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
            error.RequestDelivered
                ? $"KiCad received {operation} operation {operationId} but did not answer. Inspect the board state before retrying; the native request has no retained receipt."
                : $"KiCad did not receive {operation} operation {operationId}; no native mutation was confirmed. Inspect the instance and retry only with a fresh checkpoint.");

    private static CallToolResult Data(object value, bool error = false, string? errorCode = null)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (errorCode is null)
            return new() { IsError = error, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        using JsonDocument parsed = JsonDocument.Parse(structured.GetRawText());
        var map = parsed.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        map["errorCode"] = JsonSerializer.SerializeToElement(errorCode);
        map["errorMessage"] = JsonSerializer.SerializeToElement("The native operation reported an unsuccessful result; inspect the returned response and board state.");
        JsonElement withError = JsonSerializer.SerializeToElement(map);
        return new() { IsError = error, StructuredContent = withError, Content = [new TextContentBlock { Text = withError.GetRawText() }] };
    }

    private sealed record StoredReceipt(string Fingerprint, CallToolResult Result);

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> operation)
    {
        try { return await operation(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or JsonException or InvalidOperationException
            or ArgumentException or IOException)
        {
            string code = error is AutomationException known ? known.Code
                : error is JsonException ? "invalid_pcb_operation_input"
                : error is NativeApiException native && native.Status == (int)Kiapi.Common.ApiStatusCode.AsTokenMismatch
                    ? "pcb_instance_changed"
                    : error is NativeApiException nativeError ? "native_status_" + nativeError.Status
                    : "pcb_operation_failed";
            bool submitted = error is AutomationException outcome && outcome.Code == "operation_outcome_unknown";
            var structured = JsonSerializer.SerializeToElement(new
            {
                errorCode = code, errorMessage = error.Message,
                mutationSubmitted = submitted, outcome = submitted ? "indeterminate" : "not_submitted"
            });
            return new() { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }
}
