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
/// Task-oriented keepout authoring over KiCad's existing typed board API.
/// The wrapper maps task-oriented keepout input into a typed Zone while adding stale-state admission,
/// an undoable native transaction and a post-commit lifecycle observation.
/// </summary>
[McpServerToolType]
public sealed class PcbKeepoutAuthoringTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_keepout_read", ReadOnly = true),
     Description("Read one typed native keepout by canonical KIID at an exact PCB lifecycle checkpoint. documentJson identifies the board, keepoutId identifies one rule area, and expectedStateJson must carry the matching process epoch, revision and digest. Derived fill polygons and parent links are returned as native state but are never treated as authoring input."),
     KiCadCapability("pcb-keepouts", "compiled-mcp plus-native-board-api", "outcome:p079fb6adb164b14c; explicit PCB document, keepout KIID, process epoch, lifecycle revision and digest"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbKeepoutMcpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Read(string instanceId, string documentJson, string keepoutId,
        string expectedStateJson, CancellationToken cancellationToken) =>
        Execute(() => ReadCore(instanceId, documentJson, keepoutId, expectedStateJson, cancellationToken));

    [McpServerTool(Name = "kicad_pcb_keepout_create"),
     Description("Create one typed keepout through KiCad's native board API. keepout is task-oriented input with a canonical UUID, explicit physical layers, closed point-only polygon rings, a name, border settings and five explicit keepout flags; documentJson and expectedStateJson identify the exact board checkpoint. Placement ownership and derived fill polygons are unavailable in this input. The native operation is undoable and lifecycle-guarded; operationId is correlation only, so inspect the board after a lost reply before retrying."),
     KiCadCapability("pcb-keepouts", "compiled-mcp plus-native-board-api", "outcome:p079fb6adb164b14c; explicit PCB document, typed keepout input, process epoch, lifecycle revision and digest, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbKeepoutMcpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Create(string instanceId, string documentJson, PcbKeepoutAuthoringInput keepout,
        string expectedStateJson, string operationId, CancellationToken cancellationToken) =>
        Execute(() => MutateCore(instanceId, documentJson, keepout, null, expectedStateJson, operationId,
            KeepoutMutation.Create, cancellationToken));

    [McpServerTool(Name = "kicad_pcb_keepout_update"),
     Description("Update one existing typed keepout through KiCad's native board API. keepout is task-oriented input and must retain the existing canonical UUID; the target must already be a native rule area with no placement ownership, and the board must still equal expectedStateJson. The update is one undoable lifecycle-guarded transaction. Native-derived fill polygons and parent links are preserved rather than silently overwritten."),
     KiCadCapability("pcb-keepouts", "compiled-mcp plus-native-board-api", "outcome:p079fb6adb164b14c; explicit PCB document, typed keepout input and KIID, process epoch, lifecycle revision and digest, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbKeepoutMcpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Update(string instanceId, string documentJson, PcbKeepoutAuthoringInput keepout,
        string expectedStateJson, string operationId, CancellationToken cancellationToken) =>
        Execute(() => MutateCore(instanceId, documentJson, keepout, null, expectedStateJson, operationId,
            KeepoutMutation.Update, cancellationToken));

    [McpServerTool(Name = "kicad_pcb_keepout_delete"),
     Description("Delete one explicitly identified native keepout by canonical KIID. The target is read and required to be a rule area before an undoable native transaction opens; documentJson and expectedStateJson guard the exact PCB revision. operationId is correlation only; a lost reply requires a fresh keepout read before retrying."),
     KiCadCapability("pcb-keepouts", "compiled-mcp plus-native-board-api", "outcome:p079fb6adb164b14c; explicit PCB document, keepout KIID, process epoch, lifecycle revision and digest, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbKeepoutMcpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Delete(string instanceId, string documentJson, string keepoutId,
        string expectedStateJson, string operationId, CancellationToken cancellationToken) =>
        Execute(() => MutateCore(instanceId, documentJson, null, keepoutId, expectedStateJson, operationId,
            KeepoutMutation.Delete, cancellationToken));

    private async Task<CallToolResult> ReadCore(string instanceId, string documentJson, string keepoutId,
        string expectedStateJson, CancellationToken cancellationToken)
    {
        var document = ParseDocument(documentJson);
        PcbKeepoutAuthoringContract.ValidateDeleteId(keepoutId);
        var expected = ParseState(expectedStateJson, document);
        var client = registry.Client(instanceId);
        var session = await client.HandshakeAsync(cancellationToken);
        RequireEpoch(session.Epoch, expected.ProcessEpoch);
        var before = await ReadState(client, document, cancellationToken);
        RequireState(before, expected, "The PCB changed before the keepout read; inspect a fresh lifecycle state and retry.");
        GetItemsResponse response;
        try
        {
            response = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            {
                Header = new() { Document = document },
                Items = { new KIID { Value = keepoutId } }
            }, cancellationToken);
        }
        catch (NativeApiException error) when (error.Status == 3)
        {
            throw new AutomationException("pcb_keepout_not_found", $"Native keepout {keepoutId} was not found.");
        }
        Zone zone;
        try
        {
            zone = RequireSingleKeepout(response, keepoutId);
        }
        catch (NativeApiException error) when (error.Status == 3)
        {
            throw new AutomationException("pcb_keepout_not_found", $"Native keepout {keepoutId} was not found.");
        }
        PcbKeepoutAuthoringContract.ValidateExisting(zone, expected.NativeIdentity);
        var after = await ReadState(client, document, cancellationToken);
        RequireState(after, expected, "The PCB changed while reading the keepout; discard this snapshot.");
        return Data(new
        {
            instanceId, processEpoch = session.Epoch, operation = "read", keepoutId,
            document, beforeState = SchematicJson.Formatter.Format(before),
            afterState = SchematicJson.Formatter.Format(after),
            keepout = JsonDocument.Parse(BoardJson.Formatter.Format(zone)).RootElement.Clone(),
            nativeQuery = true, boardMutation = false
        });
    }

    private async Task<CallToolResult> MutateCore(string instanceId, string documentJson, PcbKeepoutAuthoringInput? input,
        string? zoneIdInput, string expectedStateJson, string operationId, KeepoutMutation mutation, CancellationToken cancellationToken)
    {
        var document = ParseDocument(documentJson);
        PcbKeepoutAuthoringContract.ValidateOperationId(operationId);
        var expected = ParseState(expectedStateJson, document);
        var client = registry.Client(instanceId);
        var session = await client.HandshakeAsync(cancellationToken);
        RequireEpoch(session.Epoch, expected.ProcessEpoch);
        Zone? zone = null;
        Zone? existing = null;
        string targetId;
        PcbItemOperation operation;
        switch (mutation)
        {
            case KeepoutMutation.Create:
                zone = input?.ToNative() ?? throw new AutomationException("invalid_pcb_keepout", "Supply typed keepout input.");
                PcbKeepoutAuthoringContract.ValidateCreate(zone);
                targetId = zone.Id.Value;
                var create = new CreateItems { Header = new() { Document = document } };
                create.Items.Add(Any.Pack(zone));
                operation = new PcbItemOperation { Create = create };
                break;
            case KeepoutMutation.Update:
                zone = input?.ToNative() ?? throw new AutomationException("invalid_pcb_keepout", "Supply typed keepout input.");
                PcbKeepoutAuthoringContract.ValidateUpdate(zone);
                targetId = zone.Id.Value;
                var update = new UpdateItems { Header = new() { Document = document } };
                update.Items.Add(Any.Pack(zone));
                operation = new PcbItemOperation { Update = update };
                break;
            case KeepoutMutation.Delete:
                PcbKeepoutAuthoringContract.ValidateDeleteId(zoneIdInput ?? "");
                targetId = zoneIdInput!;
                var delete = new DeleteItems { Header = new() { Document = document } };
                delete.ItemIds.Add(new KIID { Value = targetId });
                operation = new PcbItemOperation { Delete = delete };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        // Rule-area validation and the safe update merge retain the old
        // keepout authoring semantics. The checked native controller performs
        // the authoritative admission immediately afterwards and owns the
        // transaction receipt; it is the only mutation path here.
        if (mutation != KeepoutMutation.Create)
        {
            try
            {
                existing = await ReadExistingZone(client, document, targetId, cancellationToken);
            }
            catch (NativeApiException error) when (error.Status == 3)
            {
                throw new AutomationException("pcb_keepout_not_found", $"Native keepout {targetId} was not found; no mutation was attempted.");
            }
            PcbKeepoutAuthoringContract.ValidateExisting(existing, expected.NativeIdentity);
            if (mutation == KeepoutMutation.Update)
            {
                zone = MergeUpdate(existing, zone!);
                operation.Update.Items[0] = Any.Pack(zone);
            }
        }

        var checkedRequest = new CheckedPcbItemTransaction
        {
            ExpectedState = expected.Clone(),
            ProcessEpoch = session.Epoch,
            OperationId = operationId,
            MaximumResultBytes = 512 * 1024,
            Description = "MCP typed PCB keepout transaction"
        };
        checkedRequest.Operations.Add(operation);
        var outcome = await PcbCheckedItemDispatch.ApplyAsync(registry, instanceId, checkedRequest, cancellationToken);
        return KeepoutMutationData(outcome, mutation, targetId);
    }

    private static CallToolResult KeepoutMutationData(PcbCheckedItemDispatch.Outcome outcome,
        KeepoutMutation mutation, string targetId)
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
            keepoutId = targetId,
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
            postCommitVerified = success && receipt?.PostCommitState == CheckedPcbItemPostCommitState.CpbpsConfirmed,
            undoableTransaction = outcome.NativeReceiptAvailable,
            atomicTransaction = true,
            transactionBoundary = "native checked PCB item transaction",
            operationReceipt = outcome.NativeReceiptAvailable,
            replaySafe = outcome.NativeReceiptAvailable,
            transportRetrySafe = outcome.NativeReceiptAvailable,
            operationIdCorrelationOnly = false,
            settlement,
            errorCode = success ? null : outcome.ErrorCode,
            errorMessage = success ? null : outcome.ErrorMessage
        });
        return new CallToolResult
        {
            IsError = !success,
            StructuredContent = data,
            Content = [new TextContentBlock { Text = data.GetRawText() }]
        };
    }

    private static DocumentSpecifier ParseDocument(string json)
    {
        try
        {
            var document = SchematicJson.Parser.Parse<DocumentSpecifier>(json);
            PcbKeepoutAuthoringContract.ValidateDocument(document);
            return document;
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_pcb_keepout_target", "documentJson is not valid PCB protobuf JSON: " + error.Message); }
    }

    private static DocumentLifecycleState ParseState(string json, DocumentSpecifier document)
    {
        try
        {
            var state = SchematicJson.Parser.Parse<DocumentLifecycleState>(json);
            PcbKeepoutAuthoringContract.ValidateState(document, state);
            return state;
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_pcb_keepout_state", "expectedStateJson is not valid lifecycle protobuf JSON: " + error.Message); }
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
        return RequireSingleKeepout(response, zoneId);
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
        merged.Priority = existing.Priority;
        if (existing.RuleAreaSettings is { } source && merged.RuleAreaSettings is { } target)
        {
            target.PlacementEnabled = source.PlacementEnabled;
            target.PlacementSource = source.PlacementSource;
            target.PlacementSourceType = source.PlacementSourceType;
        }
        return merged;
    }

    private static async Task<string> ConfirmUncommitted(NativeClient client, DocumentSpecifier document,
        DocumentLifecycleState expected, KeepoutMutation mutation, string zoneId, Zone? existing,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await ReadState(client, document, cancellationToken);
            if (!state.Equals(expected)) return "indeterminate";
            if (mutation == KeepoutMutation.Create)
            {
                try { _ = await ReadExistingZone(client, document, zoneId, cancellationToken); return "indeterminate"; }
                catch (NativeApiException error) when (error.Status == 3) { return "rolled_back"; }
            }
            if (existing is null) return "indeterminate";
            var current = await ReadExistingZone(client, document, zoneId, cancellationToken);
            // Native state digest equality is the primary proof. This read confirms the target
            // still exists and remains a zone; exact metadata is native-owned during rollback.
            return current.Id.Value == existing.Id.Value && current.Type == existing.Type ? "rolled_back" : "indeterminate";
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException)
        { return "indeterminate"; }
    }

    private static async Task<bool> ConfirmDeleted(NativeClient client, DocumentSpecifier document,
        string zoneId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await ReadExistingZone(client, document, zoneId, cancellationToken);
            throw new AutomationException("pcb_keepout_delete_unverified", $"Native readback still found keepout {zoneId} after the committed delete.");
        }
        catch (NativeApiException error) when (error.Status == 3)
        {
            return true;
        }
    }

    private static Zone RequireSingleKeepout(GetItemsResponse response, string zoneId)
    {
        if (response.Items.Count != 1 || !response.Items[0].Is(Zone.Descriptor))
            throw new AutomationException("pcb_keepout_not_found", $"Native item {zoneId} is not one keepout; no mutation was attempted.");
        var zone = response.Items[0].Unpack<Zone>();
        if (zone.Id is null || zone.Id.Value != zoneId)
            throw new AutomationException("pcb_keepout_identity_mismatch", "Native readback returned a different zone identity.");
        if (zone.Type != ZoneType.ZtRuleArea)
            throw new AutomationException("pcb_keepout_not_found", $"Native item {zoneId} is not a rule area keepout; no mutation was attempted.");
        return zone;
    }

    private static void RequireCreated(CreateItemsResponse response, string zoneId)
    {
        if (response.Status != ItemRequestStatus.IrsOk || response.CreatedItems.Count != 1
            || response.CreatedItems[0].Status.Code != ItemStatusCode.IscOk
            || !response.CreatedItems[0].Item.Is(Zone.Descriptor)
            || response.CreatedItems[0].Item.Unpack<Zone>().Id.Value != zoneId)
            throw new AutomationException("pcb_keepout_transaction_rejected", "The native board did not acknowledge creation of the requested keepout; transaction settlement is reported separately.");
    }

    private static void RequireUpdated(UpdateItemsResponse response, string zoneId)
    {
        if (response.Status != ItemRequestStatus.IrsOk || response.UpdatedItems.Count != 1
            || response.UpdatedItems[0].Status.Code != ItemStatusCode.IscOk
            || !response.UpdatedItems[0].Item.Is(Zone.Descriptor)
            || response.UpdatedItems[0].Item.Unpack<Zone>().Id.Value != zoneId)
            throw new AutomationException("pcb_keepout_transaction_rejected", "The native board did not acknowledge update of the requested keepout; transaction settlement is reported separately.");
    }

    private static void RequireDeleted(DeleteItemsResponse response, string zoneId)
    {
        if (response.Status != ItemRequestStatus.IrsOk || response.DeletedItems.Count != 1
            || response.DeletedItems[0].Status != ItemDeletionStatus.IdsOk
            || response.DeletedItems[0].Id.Value != zoneId)
            throw new AutomationException("pcb_keepout_transaction_rejected", "The native board did not acknowledge deletion of the requested keepout; transaction settlement is reported separately.");
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
                errorCode = error is AutomationException known ? known.Code : "pcb_keepout_operation_failed",
                errorMessage = error.Message
            }, true);
        }
    }

    private enum KeepoutMutation
    {
        Create,
        Update,
        Delete
    }
}
