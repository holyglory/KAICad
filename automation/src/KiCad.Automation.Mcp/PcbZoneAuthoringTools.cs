using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
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

/// <summary>
/// Task-oriented copper-zone authoring over KiCad's existing typed board API.
/// The wrapper maps task-oriented zone input into a typed Zone while adding stale-state admission,
/// an undoable native transaction and a post-commit lifecycle observation.
/// </summary>
[McpServerToolType]
public sealed class PcbZoneAuthoringTools(InstanceRegistry registry)
{
    // Keep the exact checked update envelope that was sent. A retry after a
    // lost reply must be able to inspect the native receipt without rereading
    // a target that may have changed or disappeared.
    private readonly ConcurrentDictionary<string, CachedCheckedRequest> checkedRequests = new(StringComparer.Ordinal);

    [McpServerTool(Name = "kicad_pcb_zone_read", ReadOnly = true),
     Description("Read one typed native copper zone by canonical KIID at an exact PCB lifecycle checkpoint. documentJson identifies the board, zoneId identifies one zone, and expectedStateJson must carry the matching process epoch, revision and digest. Derived fill polygons are returned as native state but are never treated as authoring input."),
     KiCadCapability("pcb-zones", "compiled-mcp plus-native-board-api", "explicit PCB document, zone KIID, process epoch, lifecycle revision and digest"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Read(string instanceId, string documentJson, string zoneId,
        string expectedStateJson, CancellationToken cancellationToken) =>
        Execute(() => ReadCore(instanceId, documentJson, zoneId, expectedStateJson, cancellationToken));

    [McpServerTool(Name = "kicad_pcb_zone_create"),
     Description("Create one typed copper zone through KiCad's native board API. zone is task-oriented input with a canonical UUID, explicit copper layers, closed point-only polygon rings, net and fill settings; documentJson and expectedStateJson identify the exact board checkpoint. The checked native operation is lifecycle-guarded and retains a receipt so the same operationId can be replayed safely after a lost reply. Native-derived fill polygons and parent links are unavailable in this input."),
     KiCadCapability("pcb-zones", "compiled-mcp plus-native-board-api", "outcome:p16f42b1cbade388b; explicit PCB document, typed zone input, process epoch, lifecycle revision and digest, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Create(string instanceId, string documentJson, PcbZoneAuthoringInput zone,
        string expectedStateJson, string operationId, CancellationToken cancellationToken) =>
        Execute(() => MutateCore(instanceId, documentJson, zone, null, expectedStateJson, operationId,
            ZoneMutation.Create, cancellationToken));

    [McpServerTool(Name = "kicad_pcb_zone_update"),
     Description("Update one existing typed copper zone through KiCad's native board API. zone is task-oriented input and must retain the existing canonical UUID; the target must already be a native copper Zone and the board must still equal expectedStateJson. The checked native operation is lifecycle-guarded and retains a receipt for exact operationId replay. Native-owned metadata that is outside the authoring input is preserved where safe; derived fill polygons are discarded and refillRequired remains true until the separate native refill operation completes."),
     KiCadCapability("pcb-zones", "compiled-mcp plus-native-board-api", "outcome:p16f42b1cbade388b; explicit PCB document, typed zone input and KIID, process epoch, lifecycle revision and digest, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Update(string instanceId, string documentJson, PcbZoneAuthoringInput zone,
        string expectedStateJson, string operationId, CancellationToken cancellationToken) =>
        Execute(() => MutateCore(instanceId, documentJson, zone, null, expectedStateJson, operationId,
            ZoneMutation.Update, cancellationToken));

    [McpServerTool(Name = "kicad_pcb_zone_delete"),
     Description("Delete one explicitly identified native copper zone by canonical KIID. The target is read and required to be a copper Zone before a checked lifecycle-guarded operation is dispatched; documentJson and expectedStateJson guard the exact PCB revision. The native receipt retains operationId and the exact result for safe replay after a lost reply."),
     KiCadCapability("pcb-zones", "compiled-mcp plus-native-board-api", "outcome:p16f42b1cbade388b; explicit PCB document, zone KIID, process epoch, lifecycle revision and digest, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Delete(string instanceId, string documentJson, string zoneId,
        string expectedStateJson, string operationId, CancellationToken cancellationToken) =>
        Execute(() => MutateCore(instanceId, documentJson, null, zoneId, expectedStateJson, operationId,
            ZoneMutation.Delete, cancellationToken));

    private async Task<CallToolResult> ReadCore(string instanceId, string documentJson, string zoneId,
        string expectedStateJson, CancellationToken cancellationToken)
    {
        var document = ParseDocument(documentJson);
        PcbZoneAuthoringContract.ValidateDeleteId(zoneId);
        var expected = ParseState(expectedStateJson, document);
        var client = registry.Client(instanceId);
        var session = await client.HandshakeAsync(cancellationToken);
        RequireEpoch(session.Epoch, expected.ProcessEpoch);
        var before = await ReadState(client, document, cancellationToken);
        RequireState(before, expected, "The PCB changed before the zone read; inspect a fresh lifecycle state and retry.");
        GetItemsResponse response;
        try
        {
            response = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            {
                Header = new() { Document = document },
                Items = { new KIID { Value = zoneId } }
            }, cancellationToken);
        }
        catch (NativeApiException error) when (error.Status == 3)
        {
            throw new AutomationException("pcb_zone_not_found", $"Native zone {zoneId} was not found.");
        }
        Zone zone = RequireSingleZone(response, zoneId);
        PcbZoneAuthoringContract.ValidateExisting(zone, expected.NativeIdentity);
        var after = await ReadState(client, document, cancellationToken);
        RequireState(after, expected, "The PCB changed while reading the zone; discard this snapshot.");
        return Data(new
        {
            instanceId, processEpoch = session.Epoch, operation = "read", zoneId,
            document, beforeState = SchematicJson.Formatter.Format(before),
            afterState = SchematicJson.Formatter.Format(after),
            zone = JsonDocument.Parse(BoardJson.Formatter.Format(zone)).RootElement.Clone(),
            nativeQuery = true, boardMutation = false
        });
    }

    private async Task<CallToolResult> MutateCore(string instanceId, string documentJson, PcbZoneAuthoringInput? input,
        string? zoneIdInput, string expectedStateJson, string operationId, ZoneMutation mutation, CancellationToken cancellationToken)
    {
        var document = ParseDocument(documentJson);
        PcbZoneAuthoringContract.ValidateOperationId(operationId);
        var expected = ParseState(expectedStateJson, document);
        var callerExpected = expected.Clone();
        var client = registry.Client(instanceId);
        var session = await client.HandshakeAsync(cancellationToken);
        RequireEpoch(session.Epoch, expected.ProcessEpoch);
        Zone? zone = null;
        Zone? existing = null;
        string targetId;
        PcbItemOperation operation;
        switch (mutation)
        {
            case ZoneMutation.Create:
                zone = input?.ToNative() ?? throw new AutomationException("invalid_pcb_zone", "Supply typed zone input.");
                PcbZoneAuthoringContract.ValidateCreate(zone);
                targetId = zone.Id.Value;
                var create = new CreateItems { Header = new() { Document = document } };
                create.Items.Add(Any.Pack(zone));
                operation = new PcbItemOperation { Create = create };
                break;
            case ZoneMutation.Update:
                zone = input?.ToNative() ?? throw new AutomationException("invalid_pcb_zone", "Supply typed zone input.");
                PcbZoneAuthoringContract.ValidateUpdate(zone);
                targetId = zone.Id.Value;
                var update = new UpdateItems { Header = new() { Document = document } };
                update.Items.Add(Any.Pack(zone));
                operation = new PcbItemOperation { Update = update };
                break;
            case ZoneMutation.Delete:
                PcbZoneAuthoringContract.ValidateDeleteId(zoneIdInput ?? "");
                targetId = zoneIdInput!;
                var delete = new DeleteItems { Header = new() { Document = document } };
                delete.ItemIds.Add(new KIID { Value = targetId });
                operation = new PcbItemOperation { Delete = delete };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Zone? requestedZone = mutation == ZoneMutation.Update ? zone!.Clone() : null;

        // Inspect the native process-owned receipt before any live target read.
        // A retry with the same operation ID must settle from its exact retained
        // request even when the target is gone or the live board is unavailable.
        var rawRequest = mutation == ZoneMutation.Update
            && TryCachedUpdate(instanceId, operationId, targetId, callerExpected, requestedZone, out var cachedRequest)
            ? cachedRequest
            : CheckedRequest(expected, session.Epoch, operation, operationId);
        var inspected = await PcbCheckedItemDispatch.InspectAsync(registry, instanceId, rawRequest, cancellationToken);
        if (inspected.Receipt is { State: not CheckedPcbItemTransactionState.CpbtsNotFound })
        {
            inspected.Receipt.Replayed = true;
            return ZoneMutationData(inspected, mutation, targetId,
                AuthoredZone(rawRequest, mutation));
        }
        if (inspected.ErrorCode is not null)
            return ZoneMutationData(inspected, mutation, targetId,
                AuthoredZone(rawRequest, mutation));

        // The checked native controller performs authoritative lifecycle
        // admission immediately after this managed target preflight.
        if (mutation != ZoneMutation.Create)
        {
            try
            {
                existing = await ReadExistingZone(client, document, targetId, cancellationToken);
            }
            catch (NativeApiException error) when (error.Status == 3)
            {
                throw new AutomationException("pcb_zone_not_found", $"Native zone {targetId} was not found; no mutation was attempted.");
            }
            PcbZoneAuthoringContract.ValidateExisting(existing, expected.NativeIdentity);
            // Native reads may advance an observation cursor while materializing
            // derived fill state. Require semantic equality before admitting the
            // checked mutation, then bind it to the refreshed checkpoint.
            var refreshed = await ReadState(client, document, cancellationToken);
            if (!SemanticStateEqual(expected, refreshed))
                throw new AutomationException("pcb_state_changed", "The PCB changed while reading the zone; inspect a fresh lifecycle state and retry.");
            expected = refreshed;
            if (mutation == ZoneMutation.Update)
            {
                zone = MergeUpdate(existing, zone!);
                operation.Update.Items[0] = Any.Pack(zone);
            }
        }

        var checkedRequest = CheckedRequest(expected, session.Epoch, operation, operationId);
        if (mutation == ZoneMutation.Update)
        {
            checkedRequests[CacheKey(instanceId, operationId)] = new CachedCheckedRequest(
                mutation, targetId, requestedZone!.Clone(), callerExpected, checkedRequest.Clone());
        }
        var outcome = await PcbCheckedItemDispatch.ApplyAsync(registry, instanceId, checkedRequest, cancellationToken);
        return ZoneMutationData(outcome, mutation, targetId,
            AuthoredZone(checkedRequest, mutation));
    }

    private bool TryCachedUpdate(string instanceId, string operationId, string targetId,
        DocumentLifecycleState callerExpected, Zone? requestedZone,
        out CheckedPcbItemTransaction request)
    {
        request = null!;
        if (requestedZone is null
            || !checkedRequests.TryGetValue(CacheKey(instanceId, operationId), out var cached)
            || cached.Mutation != ZoneMutation.Update
            || cached.TargetId != targetId
            || !cached.CallerExpected.Equals(callerExpected)
            || !cached.RequestedZone.Equals(requestedZone)
            || cached.Request.ProcessEpoch != callerExpected.ProcessEpoch)
            return false;

        request = cached.Request.Clone();
        return true;
    }

    private static string CacheKey(string instanceId, string operationId) => instanceId + "\0" + operationId;

    private static CheckedPcbItemTransaction CheckedRequest(DocumentLifecycleState expected,
        string processEpoch, PcbItemOperation operation, string operationId)
    {
        var request = new CheckedPcbItemTransaction
        {
            ExpectedState = expected.Clone(),
            ProcessEpoch = processEpoch,
            OperationId = operationId,
            MaximumResultBytes = 512 * 1024,
            Description = "MCP typed PCB copper-zone transaction"
        };
        request.Operations.Add(operation.Clone());
        return request;
    }

    private static bool SemanticStateEqual(DocumentLifecycleState expected, DocumentLifecycleState actual) =>
        expected.Document.Equals(actual.Document)
        && expected.ProcessEpoch == actual.ProcessEpoch
        && expected.NativeIdentity == actual.NativeIdentity
        && expected.StateSha256 == actual.StateSha256
        && expected.ProjectSettingsIncluded == actual.ProjectSettingsIncluded
        && CheckedSchematicContract.FileCoverage(expected)
        && CheckedSchematicContract.FileCoverage(actual);

    private static Zone? AuthoredZone(CheckedPcbItemTransaction request, ZoneMutation mutation)
    {
        if (mutation == ZoneMutation.Delete || request.Operations.Count == 0)
            return null;

        PcbItemOperation operation = request.Operations[0];
        Any? item = operation.ActionCase switch
        {
            PcbItemOperation.ActionOneofCase.Create when operation.Create.Items.Count == 1
                => operation.Create.Items[0],
            PcbItemOperation.ActionOneofCase.Update when operation.Update.Items.Count == 1
                => operation.Update.Items[0],
            _ => null
        };
        return item is not null && item.Is(Zone.Descriptor) ? item.Unpack<Zone>() : null;
    }

    private static Zone? CommittedZone(CheckedPcbItemTransactionReceipt receipt,
        ZoneMutation mutation)
    {
        if (receipt.Results.Count == 0)
            return null;

        PcbItemOperationResult result = receipt.Results[0];
        Any? item = mutation switch
        {
            ZoneMutation.Create when result.ResultCase == PcbItemOperationResult.ResultOneofCase.Create
                && result.Create.CreatedItems.Count == 1
                => result.Create.CreatedItems[0].Item,
            ZoneMutation.Update when result.ResultCase == PcbItemOperationResult.ResultOneofCase.Update
                && result.Update.UpdatedItems.Count == 1
                => result.Update.UpdatedItems[0].Item,
            _ => null
        };
        return item is not null && item.Is(Zone.Descriptor) ? item.Unpack<Zone>() : null;
    }

    private static CallToolResult ZoneMutationData(PcbCheckedItemDispatch.Outcome outcome,
        ZoneMutation mutation, string targetId, Zone? authoredZone)
    {
        CheckedPcbItemTransactionReceipt? receipt = outcome.Receipt;
        JsonElement? receiptJson = receipt is null || outcome.UntrustedReceipt
            ? null : JsonDocument.Parse(SchematicJson.Formatter.Format(receipt)).RootElement.Clone();
        string? before = receipt?.ObservedBefore is null || outcome.UntrustedReceipt
            ? null : SchematicJson.Formatter.Format(receipt.ObservedBefore);
        string? rollback = receipt?.ObservedRollback is null || outcome.UntrustedReceipt
            ? null : SchematicJson.Formatter.Format(receipt.ObservedRollback);
        string? after = receipt?.ObservedAfter is null || outcome.UntrustedReceipt
            ? rollback : SchematicJson.Formatter.Format(receipt.ObservedAfter);
        JsonElement? response = null;
        if (!outcome.UntrustedReceipt && receipt is not null && receipt.Results.Count > 0)
        {
            var operationResult = receipt.Results[0];
            string? json = operationResult.ResultCase switch
            {
                PcbItemOperationResult.ResultOneofCase.Create => BoardJson.Formatter.Format(operationResult.Create),
                PcbItemOperationResult.ResultOneofCase.Update => BoardJson.Formatter.Format(operationResult.Update),
                PcbItemOperationResult.ResultOneofCase.Delete => BoardJson.Formatter.Format(operationResult.Delete),
                _ => null
            };
            if (json is not null) response = JsonDocument.Parse(json).RootElement.Clone();
        }

        bool success = outcome.Completed;
        bool authoredReadbackVerified = false;
        AutomationException? readbackError = null;
        if (success && authoredZone is not null && receipt is not null && !outcome.UntrustedReceipt)
        {
            try
            {
                Zone? committedZone = CommittedZone(receipt, mutation);
                if (committedZone is null)
                    throw new AutomationException("pcb_zone_readback_mismatch",
                        "The checked native receipt did not return the committed copper zone.");
                PcbZoneAuthoringContract.ValidateReadback(authoredZone, committedZone);
                authoredReadbackVerified = true;
            }
            catch (AutomationException error)
            {
                readbackError = error;
            }
        }
        // A zone create/update receipt is only post-commit verified when the
        // receipt also carries the authored geometry and settings that passed
        // the task-oriented readback contract. Lifecycle settlement alone does
        // not prove that KiCad committed the requested zone content.
        bool postCommitVerified = success
            && receipt?.PostCommitState == CheckedPcbItemPostCommitState.CpbpsConfirmed
            && (authoredZone is null || authoredReadbackVerified);
        bool resultAccepted = success && (authoredZone is null || authoredReadbackVerified);
        string settlement = receipt?.State switch
        {
            CheckedPcbItemTransactionState.CpbtsCompleted => "committed",
            CheckedPcbItemTransactionState.CpbtsRolledBack => "rolled_back",
            CheckedPcbItemTransactionState.CpbtsRejected => "rejected",
            CheckedPcbItemTransactionState.CpbtsIndeterminate => "indeterminate",
            _ => "not_submitted"
        };
        var data = JsonSerializer.SerializeToElement(new
        {
            outcome.InstanceId,
            processEpoch = receipt?.ProcessEpoch ?? outcome.Request.ProcessEpoch,
            operationId = outcome.Request.OperationId,
            operation = mutation.ToString().ToLowerInvariant(),
            document = outcome.Request.ExpectedState.Document,
            zoneId = targetId,
            state = outcome.UntrustedReceipt
                ? CheckedPcbItemTransactionState.CpbtsIndeterminate.ToString() : receipt?.State.ToString(),
            replayed = receipt?.Replayed ?? false,
            receipt = receiptJson,
            nativeReceipt = receiptJson,
            nativeReceiptAvailable = outcome.NativeReceiptAvailable,
            untrustedReceipt = outcome.UntrustedReceipt && receipt is not null
                ? JsonDocument.Parse(SchematicJson.Formatter.Format(receipt)).RootElement.Clone() : (JsonElement?)null,
            beforeState = before,
            rollbackState = rollback,
            afterState = after,
            response,
            mutationConfirmed = success,
            authoredReadbackVerified,
            postCommitVerified,
            refillRequired = success && (mutation is ZoneMutation.Create or ZoneMutation.Update),
            undoableTransaction = outcome.NativeReceiptAvailable,
            atomicTransaction = true,
            transactionBoundary = "native checked PCB item transaction",
            operationReceipt = outcome.NativeReceiptAvailable,
            replaySafe = outcome.NativeReceiptAvailable,
            transportRetrySafe = outcome.NativeReceiptAvailable,
            operationIdCorrelationOnly = false,
            settlement,
            errorCode = readbackError?.Code ?? (success ? null : outcome.ErrorCode),
            errorMessage = readbackError?.Message ?? (success ? null : outcome.ErrorMessage)
        });
        return new CallToolResult
        {
            IsError = !resultAccepted,
            StructuredContent = data,
            Content = [new TextContentBlock { Text = data.GetRawText() }]
        };
    }

    private static DocumentSpecifier ParseDocument(string json)
    {
        try
        {
            var document = SchematicJson.Parser.Parse<DocumentSpecifier>(json);
            PcbZoneAuthoringContract.ValidateDocument(document);
            return document;
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_pcb_zone_target", "documentJson is not valid PCB protobuf JSON: " + error.Message); }
    }

    private static DocumentLifecycleState ParseState(string json, DocumentSpecifier document)
    {
        try
        {
            var state = SchematicJson.Parser.Parse<DocumentLifecycleState>(json);
            PcbZoneAuthoringContract.ValidateState(document, state);
            return state;
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_pcb_zone_state", "expectedStateJson is not valid lifecycle protobuf JSON: " + error.Message); }
    }

    private static async Task<DocumentLifecycleState> ReadState(NativeClient client,
        DocumentSpecifier document, CancellationToken cancellationToken) =>
        await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);

    private static async Task<Zone> ReadExistingZone(NativeClient client, DocumentSpecifier document,
        string zoneId, CancellationToken cancellationToken)
    {
        var response = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
        {
            Header = new() { Document = document }, Items = { new KIID { Value = zoneId } }
        }, cancellationToken);
        return RequireSingleZone(response, zoneId);
    }

    private static Zone MergeUpdate(Zone existing, Zone requested)
    {
        // The task DTO intentionally exposes only the safe authoring subset. Preserve
        // native-owned metadata and settings that the DTO cannot represent before sending
        // the typed replacement back through UpdateItems.
        var merged = requested.Clone();
        merged.Locked = existing.Locked;
        merged.LayerProperties.Clear();
        merged.LayerProperties.Add(existing.LayerProperties.Select(property => property.Clone()));
        merged.CustomProperties.Clear();
        merged.CustomProperties.Add(existing.CustomProperties.Select(property => property.Clone()));
        if (existing.CopperSettings is { } source && merged.CopperSettings is { } target)
        {
            target.Teardrop = source.Teardrop?.Clone();
            target.ThievingSettings = source.ThievingSettings?.Clone();
            target.HatchSettings = source.HatchSettings?.Clone();
            target.CornerSmoothing = source.CornerSmoothing;
            target.CornerRadius = source.CornerRadius?.Clone();
            if (source.Connection is { } sourceConnection && target.Connection is { } targetConnection)
                targetConnection.ThermalSpokes = sourceConnection.ThermalSpokes?.Clone();
        }
        return merged;
    }

    private static Zone RequireSingleZone(GetItemsResponse response, string zoneId)
    {
        if (response.Items.Count != 1 || !response.Items[0].Is(Zone.Descriptor))
            throw new AutomationException("pcb_zone_not_found", $"Native item {zoneId} is not one copper zone; no mutation was attempted.");
        var zone = response.Items[0].Unpack<Zone>();
        if (zone.Id is null || zone.Id.Value != zoneId)
            throw new AutomationException("pcb_zone_identity_mismatch", "Native readback returned a different zone identity.");
        return zone;
    }

    private static void RequireEpoch(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new AutomationException("pcb_instance_changed", "The expected PCB state belongs to another native process epoch.");
    }

    private static void RequireState(DocumentLifecycleState actual, DocumentLifecycleState expected, string message)
    {
        if (!actual.Equals(expected))
            throw new AutomationException("pcb_state_changed", message);
    }

    private static CallToolResult Data(object value, bool error = false)
    {
        var structured = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new CallToolResult { IsError = error, StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or ArgumentException or InvalidOperationException)
        {
            return Data(new
            {
                errorCode = error is AutomationException known ? known.Code : "pcb_zone_operation_failed",
                errorMessage = error.Message
            }, true);
        }
    }

    private enum ZoneMutation
    {
        Create,
        Update,
        Delete
    }

    private sealed record CachedCheckedRequest(
        ZoneMutation Mutation,
        string TargetId,
        Zone RequestedZone,
        DocumentLifecycleState CallerExpected,
        CheckedPcbItemTransaction Request);
}
