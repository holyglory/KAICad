using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Any = Google.Protobuf.WellKnownTypes.Any;
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

/// <summary>Deterministic, read-only rip-up/cleanup planning for an observed board.</summary>
[McpServerToolType]
public sealed class PcbRouteCleanupTools(InstanceRegistry registry)
{
    private static readonly ConcurrentDictionary<string, StoredReceipt> Receipts = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim MutationGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = true };

    [McpServerTool(Name = "kicad_pcb_route_cleanup_plan", ReadOnly = true),
     Description("Plan deterministic PCB route cleanup/rip-up from one exact board observation. requestJson contains nets[], removeZeroLength, removeDuplicateGeometry, removeAllForSelectedNets and maxItems. The tool reads native Track/Arc/Via items, emits an exact DeleteItems candidate for duplicate, zero-length, or explicitly selected-net rip-up, and never mutates the board or claims DRC/route quality. Apply the candidate only through a separate revision- and receipt-checked mutation.")]
    [KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "exact PCB lifecycle state, deterministic duplicate/zero-length route analysis, DeleteItems candidate"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Plan(string instanceId, string documentJson, string expectedStateJson,
        string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_route_cleanup_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_route_cleanup_target", "Route cleanup requires an explicit PCB document.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_route_cleanup_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_route_cleanup_state", "expectedStateJson must identify the same PCB and native process epoch.");
        CleanupRequest request = JsonSerializer.Deserialize<CleanupRequest>(requestJson, JsonOptions)
            ?? throw new AutomationException("invalid_route_cleanup", "requestJson must be a JSON object.");
        if (request.Nets is null || request.Nets.Length > 4096 || request.Nets.Any(string.IsNullOrWhiteSpace))
            throw new AutomationException("invalid_route_cleanup", "nets must contain at most 4096 nonempty names.");
        if (request.MaxItems is < 1 or > 4096)
            throw new AutomationException("invalid_route_cleanup", "maxItems must be between 1 and 4096.");
        if (!request.RemoveZeroLength && !request.RemoveDuplicateGeometry && !request.RemoveAllForSelectedNets)
            throw new AutomationException("invalid_route_cleanup", "Enable removeZeroLength, removeDuplicateGeometry, or removeAllForSelectedNets.");
        if (request.RemoveAllForSelectedNets && request.Nets.Length == 0)
            throw new AutomationException("invalid_route_cleanup", "removeAllForSelectedNets requires one or more explicitly named nets.");

        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed before cleanup planning.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected)) throw new AutomationException("pcb_state_changed", "The board changed before cleanup planning.");
        GetItemsResponse items = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = document },
            Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia }
        }, cancellationToken);
        var selected = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Any packed in items.Items)
        {
            Candidate? candidate = packed.Is(Track.Descriptor) ? TrackCandidate(packed.Unpack<Track>(), request)
                : packed.Is(Arc.Descriptor) ? ArcCandidate(packed.Unpack<Arc>(), request)
                : packed.Is(Via.Descriptor) ? ViaCandidate(packed.Unpack<Via>(), request) : null;
            if (candidate is null) continue;
            bool zero = candidate.IsZero;
            bool duplicate = request.RemoveDuplicateGeometry && !zero && !seen.Add(candidate.Key);
            bool ripup = request.RemoveAllForSelectedNets && Selected(candidate.NetName, request.Nets);
            if ((zero && request.RemoveZeroLength) || duplicate || ripup)
            {
                selected.Add(candidate with { Reason = ripup ? "rip-up" : zero ? "zero-length" : "duplicate" });
                if (selected.Count >= request.MaxItems) break;
            }
            else if (!zero) seen.Add(candidate.Key);
        }
        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!after.Equals(expected)) throw new AutomationException("route_cleanup_changed", "The board changed while cleanup obstacles were read.");
        var delete = new DeleteItems { Header = new() { Document = document } };
        delete.ItemIds.Add(selected.Select(item => new KIID { Value = item.Id }));
        string candidateJson = BoardJson.Formatter.Format(delete);
        string planSeed = candidateJson + "\n" + SchematicJson.Formatter.Format(expected);
        string planId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(planSeed)));
        return Data(new
        {
            instanceId, processEpoch = session.Epoch, document, planId,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            candidateRequestJson = BoardJson.Formatter.Format(delete), candidateItemIds = selected.Select(item => item.Id).ToArray(),
            candidateCount = selected.Count,
            reasonCounts = selected.GroupBy(item => item.Reason, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            proposalOnly = true, nativeCommit = false, drcValidated = false, deterministicReplay = true,
            limitations = new[] { "proposal_only", "does_not_reroute", "does_not_prove_drc", "does_not_optimize_corners" }
        });
    });

    [McpServerTool(Name = "kicad_pcb_route_cleanup_apply", ReadOnly = false),
     Description("Apply one exact route cleanup DeleteItems candidate through KiCad's checked native PCB item transaction. requestJson must be the candidateRequestJson returned by kicad_pcb_route_cleanup_plan; expectedStateJson must be the matching board observation; operationId binds the retained native receipt for exact replay after a lost reply.")]
    [KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "exact DeleteItems candidate, lifecycle checkpoint, native undo transaction, operation ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Apply(string instanceId, string requestJson, string expectedStateJson,
        string operationId, CancellationToken cancellationToken) => ExecuteResult(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty)
            throw new AutomationException("invalid_route_cleanup_operation", "operationId must be a canonical nonempty UUID.");
        DeleteItems request = BoardJson.Parser.Parse<DeleteItems>(requestJson);
        if (request.Header?.Document is null || request.ItemIds.Count == 0)
            throw new AutomationException("invalid_route_cleanup_candidate", "Supply a board DeleteItems candidate with at least one item ID.");
        DocumentStateTools.ValidateTarget(request.Header.Document);
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_route_cleanup_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(request.Header.Document))
            throw new AutomationException("invalid_route_cleanup_state", "The expected state must identify the same PCB document.");
        string canonicalRequest = BoardJson.Formatter.Format(request);
        string fingerprint = instanceId + "\n" + canonicalRequest + "\n" + SchematicJson.Formatter.Format(expected);
        return await WithReceipt(operationId, fingerprint, async () =>
        {
            CheckedPcbItemTransaction checkedRequest = BuildCheckedRequest(expected, request, operationId);
            PcbCheckedItemDispatch.Outcome outcome = await PcbCheckedItemDispatch.ApplyAsync(
                registry, instanceId, checkedRequest, cancellationToken);
            return CleanupMutationData(outcome);
        });
    });

    [McpServerTool(Name = "kicad_pcb_route_cleanup_receipt", ReadOnly = true),
     Description("Read the MCP process-owned receipt for a route-cleanup operation after a lost reply. The tool never resubmits a deletion; receipts are retained only while this MCP process lives and do not replace a native cross-process receipt."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "operation UUID and process-owned cleanup receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> Receipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateOperationId(operationId);
            if (string.IsNullOrWhiteSpace(instanceId))
                throw new AutomationException("invalid_route_cleanup_instance", "instanceId is required to identify the originating MCP instance.");
            if (string.IsNullOrWhiteSpace(expectedProcessEpoch))
                throw new AutomationException("invalid_route_cleanup_state", "expectedProcessEpoch is required to inspect a cleanup receipt.");
            await MutationGate.WaitAsync(cancellationToken);
            try
            {
                if (!Receipts.TryGetValue(operationId, out StoredReceipt? stored))
                    return ReceiptData(new { instanceId, processEpoch = expectedProcessEpoch, operationId, found = false,
                        receiptScope = "mcp-process", nativeReceiptAvailable = false });
                JsonElement payload = stored.Result.StructuredContent ?? JsonSerializer.SerializeToElement(new { });
                string? processEpoch = payload.TryGetProperty("processEpoch", out JsonElement epoch) ? epoch.GetString() : null;
                if (!string.Equals(processEpoch, expectedProcessEpoch, StringComparison.Ordinal))
                    throw new AutomationException("stale_process_epoch", "The cleanup receipt belongs to a different native process epoch.");
                string? receiptInstance = payload.TryGetProperty("instanceId", out JsonElement instance) ? instance.GetString() : null;
                if (!string.Equals(receiptInstance, instanceId, StringComparison.Ordinal))
                    throw new AutomationException("wrong_instance_receipt", "The cleanup receipt belongs to a different MCP instance.");
                bool nativeReceiptAvailable = payload.TryGetProperty("nativeReceiptAvailable", out JsonElement native)
                    && native.ValueKind == JsonValueKind.True;
                return ReceiptData(new { instanceId, processEpoch, operationId, found = true,
                    receiptScope = "mcp-process", nativeReceiptAvailable, receipt = payload });
            }
            finally { MutationGate.Release(); }
        }
        catch (Exception error) when (error is AutomationException or JsonException or OperationCanceledException)
        {
            string code = error is AutomationException known ? known.Code : "route_cleanup_receipt_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    internal static CheckedPcbItemTransaction BuildCheckedRequest(
        DocumentLifecycleState expected, DeleteItems candidate, string operationId)
    {
        var request = new CheckedPcbItemTransaction
        {
            ExpectedState = expected.Clone(),
            ProcessEpoch = expected.ProcessEpoch,
            OperationId = operationId,
            MaximumResultBytes = 512 * 1024,
            Description = "MCP route cleanup"
        };
        request.Operations.Add(new PcbItemOperation { Delete = candidate.Clone() });
        return request;
    }

    private static CallToolResult CleanupMutationData(PcbCheckedItemDispatch.Outcome outcome)
    {
        CheckedPcbItemTransactionReceipt? receipt = outcome.Receipt;
        bool success = outcome.Completed;
        bool retrySafe = outcome.NativeReceiptAvailable && !outcome.UntrustedReceipt;
        JsonElement? receiptJson = receipt is null || outcome.UntrustedReceipt
            ? null : JsonDocument.Parse(SchematicJson.Formatter.Format(receipt)).RootElement.Clone();
        string? before = receipt?.ObservedBefore is null || outcome.UntrustedReceipt
            ? null : SchematicJson.Formatter.Format(receipt.ObservedBefore);
        string? rollback = receipt?.ObservedRollback is null || outcome.UntrustedReceipt
            ? null : SchematicJson.Formatter.Format(receipt.ObservedRollback);
        string? after = receipt?.ObservedAfter is null || outcome.UntrustedReceipt
            ? rollback : SchematicJson.Formatter.Format(receipt.ObservedAfter);
        JsonElement? response = null;
        if (!outcome.UntrustedReceipt && receipt is not null && receipt.Results.Count > 0
            && receipt.Results[0].ResultCase == PcbItemOperationResult.ResultOneofCase.Delete)
            response = JsonDocument.Parse(BoardJson.Formatter.Format(receipt.Results[0].Delete)).RootElement.Clone();

        JsonElement structured = JsonSerializer.SerializeToElement(new
        {
            instanceId = outcome.InstanceId,
            processEpoch = outcome.Request.ProcessEpoch,
            operationId = outcome.Request.OperationId,
            operationOutcome = success ? "completed" : outcome.UntrustedReceipt
                ? CheckedPcbItemTransactionState.CpbtsIndeterminate.ToString() : receipt?.State.ToString(),
            state = outcome.UntrustedReceipt
                ? CheckedPcbItemTransactionState.CpbtsIndeterminate.ToString()
                : receipt?.State.ToString(),
            replayed = !outcome.UntrustedReceipt && receipt?.Replayed == true,
            receipt = receiptJson,
            nativeReceipt = receiptJson,
            untrustedReceipt = outcome.UntrustedReceipt && receipt is not null
                ? JsonDocument.Parse(SchematicJson.Formatter.Format(receipt)).RootElement.Clone() : (JsonElement?)null,
            nativeReceiptAvailable = outcome.NativeReceiptAvailable,
            mutationConfirmed = success,
            atomicTransaction = true,
            operationReceipt = retrySafe,
            replayableWithinProcess = retrySafe,
            receiptScope = retrySafe ? "native-process" : "none",
            transportRetrySafe = retrySafe,
            beforeState = before,
            rollbackState = rollback,
            afterState = after,
            response,
            errorCode = success ? null : outcome.ErrorCode,
            errorMessage = success ? null : outcome.ErrorMessage
        });
        return new CallToolResult
        {
            IsError = !success,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }

    private static Candidate? TrackCandidate(Track track, CleanupRequest request)
    {
        if (!Selected(track.Net.Name, request.Nets)) return null;
        string a = Point(track.Start), b = Point(track.End);
        string key = string.CompareOrdinal(a, b) <= 0 ? $"track|{track.Net.Name}|{track.Layer}|{track.Width.ValueNm}|{a}|{b}" : $"track|{track.Net.Name}|{track.Layer}|{track.Width.ValueNm}|{b}|{a}";
        return new(track.Id.Value, track.Net.Name, track.Start.Equals(track.End), key, "");
    }

    private static Candidate? ArcCandidate(Arc arc, CleanupRequest request)
    {
        if (!Selected(arc.Net.Name, request.Nets)) return null;
        string key = $"arc|{arc.Net.Name}|{arc.Layer}|{arc.Width.ValueNm}|{Point(arc.Start)}|{Point(arc.Mid)}|{Point(arc.End)}";
        return new(arc.Id.Value, arc.Net.Name, arc.Start.Equals(arc.End) && arc.Mid.Equals(arc.End), key, "");
    }

    private static Candidate? ViaCandidate(Via via, CleanupRequest request)
    {
        if (!Selected(via.Net.Name, request.Nets)) return null;
        string key = $"via|{via.Net.Name}|{Point(via.Position)}|{string.Join(',', via.PadStack?.Layers ?? [])}";
        return new(via.Id.Value, via.Net.Name, false, key, "");
    }

    private static bool Selected(string net, string[] nets) => nets.Length == 0 || nets.Contains(net, StringComparer.Ordinal);
    private static string Point(Vector2 point) => point.XNm + "," + point.YNm;
    private static void ValidateOperationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid operation) || operation == Guid.Empty)
            throw new AutomationException("invalid_route_cleanup_operation", "operationId must be a canonical nonempty UUID.");
    }
    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    { try { return SchematicJson.Parser.Parse<T>(json); } catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException) { throw new AutomationException(code, error.Message); } }

    private sealed record Candidate(string Id, string NetName, bool IsZero, string Key, string Reason);
    private sealed class CleanupRequest
    {
        public string[] Nets { get; set; } = [];
        public bool RemoveZeroLength { get; set; } = true;
        public bool RemoveDuplicateGeometry { get; set; } = true;
        public bool RemoveAllForSelectedNets { get; set; }
        public int MaxItems { get; set; } = 256;
    }
    private sealed record StoredReceipt(string Fingerprint, CallToolResult Result);

    private static async Task<CallToolResult> WithReceipt(string operationId, string fingerprint,
        Func<Task<CallToolResult>> action)
    {
        await MutationGate.WaitAsync();
        try
        {
            if (Receipts.TryGetValue(operationId, out StoredReceipt? existing))
            {
                if (existing.Fingerprint != fingerprint)
                    throw new AutomationException("operation_id_reused", "operationId is already bound to a different cleanup request.");
            }
            CallToolResult result = await action();
            if (!(result.IsError ?? false)) Receipts[operationId] = new(fingerprint, result);
            return result;
        }
        finally { MutationGate.Release(); }
    }

    private static CallToolResult ReceiptData(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value);
        return new CallToolResult { StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value);
    private static async Task<CallToolResult> Execute(Func<Task<JsonElement>> action)
    {
        try { JsonElement structured = await action(); return new() { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] }; }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or InvalidProtocolBufferException or InvalidJsonException or JsonException or IOException)
        { var structured = JsonSerializer.SerializeToElement(new { errorCode = error is AutomationException known ? known.Code : "pcb_route_cleanup_failed", errorMessage = error.Message }); return new() { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] }; }
    }

    private static async Task<CallToolResult> ExecuteResult(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or JsonException or IOException)
        {
            string code = error is AutomationException known ? known.Code : "pcb_route_cleanup_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }
}
