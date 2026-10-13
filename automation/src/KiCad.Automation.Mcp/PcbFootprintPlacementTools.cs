using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
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
/// Task-oriented preparation for placing a complete footprint definition on a board.
///
/// The board API already accepts <see cref="FootprintInstance"/> values through the
/// generic item-create command.  This planner gives agents a smaller, explicit boundary
/// for that operation: it validates the board target, lifecycle checkpoint, library ID,
/// placement layer and identity, then emits the exact Any-packed request that the checked
/// native item transaction can consume.  It deliberately does not claim that the native
/// request was committed or that DRC has been run.
/// </summary>
[McpServerToolType]
public sealed class PcbFootprintPlacementTools(InstanceRegistry registry)
{
    private static readonly SemaphoreSlim MutationGate = new(1, 1);
    private static readonly Dictionary<string, StoredReceipt> Receipts = new(StringComparer.Ordinal);

    [McpServerTool(Name = "kicad_pcb_footprint_place_plan_typed", ReadOnly = true),
     Description("Prepare an exact footprint placement using a structured PCB document, complete lifecycle checkpoint, fields, normal padstacks and segment/rectangle/circle/arc graphics. Coordinates are nanometers. Returns the complete native candidate and deterministic planId without contacting KiCad. It does not check board clearances, net connectivity or manufacturability."),
     KiCadCapability("pcb-placement", "compiled-mcp", "structured footprint geometry and complete PCB lifecycle observation"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbFootprintTypedMcpProcessTests.TypedPlacementPublishesSchemaAndExactDeterministicCandidate")]
    public Task<CallToolResult> PlanTyped(PcbFootprintPlacementInput input, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input is null) throw PcbFootprintPlacementInput.Invalid("A footprint placement input is required.");
            var native = input.ToNative(cancellationToken);
            return await Plan(SchematicJson.Formatter.Format(native.Document),
                SchematicJson.Formatter.Format(native.State), BoardJson.Formatter.Format(native.Footprint), cancellationToken);
        });

    [McpServerTool(Name = "kicad_pcb_footprint_place_apply_typed", ReadOnly = false),
     Description("Apply a structured footprint placement after checking the exact planId, explicit PCB document, complete lifecycle observation and operation UUID. Uses the same checked native create transaction and retained operation receipt as kicad_pcb_footprint_place_apply. Retain the same operation UUID, planId and exact input to reconcile a lost reply. Native board admission and postverification remain mandatory."),
     KiCadCapability("pcb-placement", "compiled-mcp plus native-board-api", "structured footprint, exact plan identity, complete lifecycle checkpoint, native undo transaction and operation receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbFootprintTypedMcpProcessTests.TypedPlacementRejectsChangedPlanAndMissingOperationBeforeNativeContact")]
    public Task<CallToolResult> ApplyTyped(string instanceId, PcbFootprintPlacementInput input,
        CancellationToken cancellationToken) => ExecuteMutation(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input is null) throw PcbFootprintPlacementInput.Invalid("A footprint placement input is required.");
        ValidateOperation(input.OperationId ?? "");
        var native = input.ToNative(cancellationToken);
        FootprintPlacementPlan plan = BuildPlan(native.Document, native.State, native.Footprint);
        if (!string.Equals(input.ExpectedPlanId, plan.PlanId, StringComparison.Ordinal))
            throw new AutomationException("stale_pcb_footprint_plan", "The exact footprint and lifecycle checkpoint must match the preview planId before applying.");
        return await Apply(instanceId, plan.CandidateRequestJson,
            SchematicJson.Formatter.Format(native.State), input.OperationId!, cancellationToken);
    });

    [McpServerTool(Name = "kicad_pcb_footprint_place_plan", ReadOnly = true),
     Description("Prepare a deterministic, revision-bound PCB footprint placement proposal. documentJson identifies an explicit PCB, expectedStateJson is the lifecycle observation the caller intends to use, and footprintJson is one complete FootprintInstance protobuf-JSON value including its library definition, pads and graphics. The planner validates the document checkpoint, canonical footprint/library identities, front/back copper placement, finite orientation, and bounded packed child size, then returns an exact CreateItems request. It does not validate child geometry or electrical connectivity, never contacts KiCad, commits the footprint, resolves a library entry implicitly, or claims DRC or manufacturability."),
     KiCadCapability("pcb-placement", "compiled-mcp", "explicit PCB document, lifecycle checkpoint, complete FootprintInstance definition"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Plan(string documentJson, string expectedStateJson,
        string footprintJson, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document = ParseDocument(documentJson);
            DocumentStateTools.ValidateTarget(document);
            if (document.Type != DocumentType.DoctypePcb)
                throw new AutomationException("invalid_pcb_footprint_target", "Footprint placement requires an explicit PCB document.");

            DocumentLifecycleState expected = ParseState(expectedStateJson);
            if (expected.Scope != DocumentLifecycleScope.DlsPcb
                || !expected.Document.Equals(document)
                || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
                throw new AutomationException("invalid_pcb_footprint_state",
                    "expectedStateJson must identify the same PCB and a native process epoch.");

            FootprintInstance footprint = ParseFootprint(footprintJson);
            FootprintPlacementPlan plan = BuildPlan(document, expected, footprint);
            return Task.FromResult(Result(new
            {
                planId = plan.PlanId,
                document,
                processEpoch = expected.ProcessEpoch,
                expectedStateJson = SchematicJson.Formatter.Format(expected),
                footprintId = footprint.Id.Value,
                libraryNickname = footprint.Definition.Id.LibraryNickname,
                entryName = footprint.Definition.Id.EntryName,
                position = new { xNm = footprint.Position.XNm, yNm = footprint.Position.YNm },
                rotationDegrees = footprint.Orientation.ValueDegrees,
                layer = footprint.Layer.ToString(),
                reference = footprint.ReferenceField?.Text?.Text?.Text_,
                value = footprint.ValueField?.Text?.Text?.Text_,
                definitionItemCount = footprint.Definition.Items.Count,
                candidateRequestJson = plan.CandidateRequestJson,
                proposalOnly = true,
                nativeCommit = false,
                drcValidated = false,
                deterministicReplay = true
            }));
        }
        catch (Exception error) when (error is AutomationException or InvalidProtocolBufferException
            or InvalidJsonException or InvalidOperationException or ArgumentException)
        {
            string code = error is AutomationException known ? known.Code : "invalid_pcb_footprint_plan";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message,
                nativeCommit = false, proposalOnly = true });
            return Task.FromResult(new CallToolResult
            {
                IsError = true,
                StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }]
            });
        }
    }

    [McpServerTool(Name = "kicad_pcb_footprint_place_apply"),
     Description("Apply one exact footprint placement candidate returned by kicad_pcb_footprint_place_plan. candidateRequestJson must contain exactly one FootprintInstance and the matching PCB document; expectedStateJson admits the native board checkpoint; operationId binds the retained checked native receipt for exact replay after a lost reply."),
     KiCadCapability("pcb-placement", "compiled-mcp plus native-board-api", "exact FootprintInstance candidate, lifecycle checkpoint, native undo transaction, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Apply(string instanceId, string candidateRequestJson, string expectedStateJson,
        string operationId, CancellationToken cancellationToken) => ExecuteMutation(async () =>
    {
        ValidateOperation(operationId);
        CreateItems request = ParseCandidate(candidateRequestJson);
        DocumentLifecycleState expected = ParseState(expectedStateJson);
        if (request.Header?.Document is null || request.Header.Document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_footprint_candidate", "The placement candidate must target an explicit PCB document.");
        DocumentStateTools.ValidateTarget(request.Header.Document);
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(request.Header.Document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_pcb_footprint_state", "expectedStateJson must identify the same PCB and native process epoch.");
        if (request.Items.Count != 1 || !request.Items[0].Is(FootprintInstance.Descriptor))
            throw new AutomationException("invalid_pcb_footprint_candidate", "The placement candidate must contain exactly one FootprintInstance.");
        FootprintInstance footprint = request.Items[0].Unpack<FootprintInstance>();
        FootprintPlacementPlan plan = BuildPlan(request.Header.Document, expected, footprint);
        string canonicalCandidate = BoardJson.Formatter.Format(request);
        if (!string.Equals(canonicalCandidate, plan.CandidateRequestJson, StringComparison.Ordinal))
            throw new AutomationException("invalid_pcb_footprint_candidate", "The candidate does not match the deterministic placement plan for this footprint and checkpoint.");
        string fingerprint = instanceId + "\n" + canonicalCandidate + "\n" + SchematicJson.Formatter.Format(expected);
        return await WithReceipt(operationId, fingerprint, async () =>
        {
            CheckedPcbItemTransaction checkedRequest = BuildCheckedRequest(expected, request, operationId);
            PcbCheckedItemDispatch.Outcome outcome = await PcbCheckedItemDispatch.ApplyAsync(
                registry, instanceId, checkedRequest, cancellationToken);
            return PlacementMutationData(outcome, plan);
        });
    });

    [McpServerTool(Name = "kicad_pcb_footprint_place_receipt", ReadOnly = true),
     Description("Read the MCP process-owned receipt for a footprint placement operation after a lost reply. The tool never resubmits the placement; receipts are discarded when this MCP process restarts and are not native cross-process receipts."),
     KiCadCapability("pcb-placement", "compiled-mcp plus native-board-api", "operation UUID and process-owned placement receipt"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Receipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken) => ExecuteMutation(async () =>
    {
        ValidateOperation(operationId);
        ValidateEpoch(expectedProcessEpoch);
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId is required to inspect a placement receipt.");
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (!Receipts.TryGetValue(operationId, out StoredReceipt? stored))
                return ReceiptData(new { instanceId, processEpoch = expectedProcessEpoch, operationId, found = false,
                    receiptScope = "mcp-process", nativeReceiptAvailable = false });
            JsonElement payload = stored.Result.StructuredContent ?? JsonSerializer.SerializeToElement(new { });
            string? processEpoch = payload.TryGetProperty("processEpoch", out JsonElement epoch) ? epoch.GetString() : null;
            if (!string.Equals(processEpoch, expectedProcessEpoch, StringComparison.Ordinal))
                throw new AutomationException("stale_process_epoch", "The placement receipt belongs to a different native process epoch.");
            string? receiptInstance = payload.TryGetProperty("instanceId", out JsonElement instance) ? instance.GetString() : null;
            if (!string.Equals(receiptInstance, instanceId, StringComparison.Ordinal))
                throw new AutomationException("wrong_instance_receipt", "The placement receipt belongs to a different MCP instance.");
            bool nativeReceiptAvailable = payload.TryGetProperty("nativeReceiptAvailable", out JsonElement native)
                && native.ValueKind == JsonValueKind.True;
            return ReceiptData(new { instanceId, processEpoch, operationId, found = true,
                receiptScope = "mcp-process", nativeReceiptAvailable, receipt = payload });
        }
        finally { MutationGate.Release(); }
    });

    /// <summary>Builds the canonical native request without contacting an editor.</summary>
    internal static FootprintPlacementPlan BuildPlan(DocumentSpecifier document,
        DocumentLifecycleState expected, FootprintInstance footprint)
    {
        ValidatePlacementCheckpoint(document, expected);

        if (footprint.Id is null || !CanonicalUuid(footprint.Id.Value))
            throw new AutomationException("invalid_pcb_footprint_id", "The footprint instance must have a nonempty canonical UUID.");

        if (footprint.Definition is null || footprint.Definition.Id is null
            || string.IsNullOrWhiteSpace(footprint.Definition.Id.LibraryNickname)
            || string.IsNullOrWhiteSpace(footprint.Definition.Id.EntryName)
            || footprint.Definition.Id.LibraryNickname.Contains('\0')
            || footprint.Definition.Id.EntryName.Contains('\0'))
            throw new AutomationException("invalid_pcb_footprint_library_id",
                "The footprint definition must name a library nickname and entry without NUL characters.");

        if (footprint.Layer is not (BoardLayer.BlFCu or BoardLayer.BlBCu))
            throw new AutomationException("invalid_pcb_footprint_layer",
                "Board footprint placement is supported only on F.Cu or B.Cu; use explicit board graphics for other layers.");

        if (!double.IsFinite(footprint.Orientation.ValueDegrees)
            || Math.Abs(footprint.Orientation.ValueDegrees) > 360_000)
            throw new AutomationException("invalid_pcb_footprint_orientation",
                "The footprint orientation must be finite and within +/-360000 degrees.");

        if (footprint.Definition.Items.Count > 16_384)
            throw new AutomationException("pcb_footprint_definition_oversized",
                "A footprint definition may contain at most 16384 child items per placement proposal.");

        int childBytes = footprint.Definition.Items.Sum(item => item.CalculateSize());
        if (childBytes > 16 * 1024 * 1024)
            throw new AutomationException("pcb_footprint_definition_oversized",
                "A footprint definition's packed child payload may not exceed 16 MiB per placement proposal.");

        var candidate = new CreateItems { Header = new() { Document = document } };
        candidate.Items.Add(Any.Pack(footprint));
        string candidateJson = BoardJson.Formatter.Format(candidate);
        string stateJson = SchematicJson.Formatter.Format(expected);
        string digestInput = candidateJson + "\n" + stateJson;
        string planId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(digestInput)));
        return new FootprintPlacementPlan(planId, candidateJson);
    }

    /// <summary>
    /// A raw protobuf-JSON caller reaches this planner without the typed input
    /// adapter.  Keep the same complete-checkpoint admission on that path so a
    /// plan cannot be produced from a partial observation and later fail only
    /// when the checked native transaction is built.
    /// </summary>
    private static void ValidatePlacementCheckpoint(DocumentSpecifier document,
        DocumentLifecycleState expected)
    {
        if (expected.Scope != DocumentLifecycleScope.DlsPcb
            || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch)
            || !expected.ProjectSettingsIncluded
            || !expected.CompleteChangeTracking
            || !expected.DiskBaselineChecked
            || expected.Revision is null
            || !CanonicalUuid(expected.NativeIdentity)
            || expected.Revision.Epoch != expected.NativeIdentity
            || !Digest(expected.StateSha256)
            || !CheckedSchematicContract.FileCoverage(expected))
            throw new AutomationException("invalid_pcb_footprint_state",
                "expectedStateJson must identify one complete, unchanged PCB lifecycle checkpoint including project settings, revision identity and file baselines.");
    }

    private static bool Digest(string value)
        => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    internal sealed record FootprintPlacementPlan(string PlanId, string CandidateRequestJson);

    internal static CheckedPcbItemTransaction BuildCheckedRequest(
        DocumentLifecycleState expected, CreateItems candidate, string operationId)
    {
        var request = new CheckedPcbItemTransaction
        {
            ExpectedState = expected.Clone(),
            ProcessEpoch = expected.ProcessEpoch,
            OperationId = operationId,
            MaximumResultBytes = 512 * 1024,
            Description = "MCP footprint placement"
        };
        request.Operations.Add(new PcbItemOperation { Create = candidate.Clone() });
        return request;
    }

    private static CallToolResult PlacementMutationData(PcbCheckedItemDispatch.Outcome outcome,
        FootprintPlacementPlan plan)
    {
        CheckedPcbItemTransactionReceipt? receipt = outcome.Receipt;
        bool success = outcome.Completed;
        bool retrySafe = outcome.NativeReceiptAvailable && !outcome.UntrustedReceipt;
        JsonElement? receiptJson = receipt is null || outcome.UntrustedReceipt ? null
            : JsonDocument.Parse(SchematicJson.Formatter.Format(receipt)).RootElement.Clone();
        string? before = receipt?.ObservedBefore is null || outcome.UntrustedReceipt ? null
            : SchematicJson.Formatter.Format(receipt.ObservedBefore);
        string? after = receipt?.ObservedAfter is null || outcome.UntrustedReceipt
            ? receipt?.ObservedRollback is null || outcome.UntrustedReceipt ? null : SchematicJson.Formatter.Format(receipt.ObservedRollback)
            : SchematicJson.Formatter.Format(receipt.ObservedAfter);
        JsonElement? response = null;
        if (!outcome.UntrustedReceipt && receipt is not null && receipt.Results.Count > 0
            && receipt.Results[0].ResultCase == PcbItemOperationResult.ResultOneofCase.Create)
            response = JsonDocument.Parse(BoardJson.Formatter.Format(receipt.Results[0].Create)).RootElement.Clone();

        JsonElement structured = JsonSerializer.SerializeToElement(new
        {
            instanceId = outcome.InstanceId,
            processEpoch = outcome.Request.ProcessEpoch,
            operationId = outcome.Request.OperationId,
            planId = plan.PlanId,
            operation = "footprint-place",
            operationOutcome = success ? "completed" : outcome.UntrustedReceipt
                ? CheckedPcbItemTransactionState.CpbtsIndeterminate.ToString() : receipt?.State.ToString(),
            state = outcome.UntrustedReceipt ? CheckedPcbItemTransactionState.CpbtsIndeterminate.ToString() : receipt?.State.ToString(),
            replayed = !outcome.UntrustedReceipt && receipt?.Replayed == true,
            receipt = receiptJson,
            nativeReceipt = receiptJson,
            untrustedReceipt = outcome.UntrustedReceipt && receipt is not null
                ? JsonDocument.Parse(SchematicJson.Formatter.Format(receipt)).RootElement.Clone() : (JsonElement?)null,
            nativeReceiptAvailable = outcome.NativeReceiptAvailable,
            mutationConfirmed = success,
            atomicTransaction = true,
            operationReceipt = retrySafe,
            replayableWithinNativeProcess = retrySafe,
            receiptScope = retrySafe ? "native-process" : "none",
            transportRetrySafe = retrySafe,
            beforeState = before,
            afterState = after,
            response,
            drcValidated = false,
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

    private static CreateItems ParseCandidate(string json)
    {
        try { return BoardJson.Parser.Parse<CreateItems>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException("invalid_pcb_footprint_candidate", "candidateRequestJson is not valid CreateItems protobuf JSON: " + error.Message); }
    }

    private static void ValidateOperation(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid operation) || operation == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "operationId must be a canonical nonempty UUID.");
    }

    private static void ValidateEpoch(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid epoch) || epoch == Guid.Empty)
            throw new AutomationException("invalid_process_epoch", "Provide the canonical process epoch from the attached KiCad session.");
    }

    private static async Task<CallToolResult> WithReceipt(string operationId, string fingerprint,
        Func<Task<CallToolResult>> action)
    {
        await MutationGate.WaitAsync();
        try
        {
            if (Receipts.TryGetValue(operationId, out StoredReceipt? existing))
            {
                if (existing.Fingerprint != fingerprint)
                    throw new AutomationException("operation_id_reused", "The operation ID is already bound to a different footprint placement.");
            }
            CallToolResult result = await action();
            if (!(result.IsError ?? false)) Receipts[operationId] = new(fingerprint, result);
            return result;
        }
        finally { MutationGate.Release(); }
    }

    private static Task<CallToolResult> ExecuteMutation(Func<Task<CallToolResult>> action)
        => Execute(action);

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (AutomationException error)
        {
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = error.Code, errorMessage = error.Message });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
        catch (NngException error)
        {
            string code = error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached";
            string message = error.RequestDelivered
                ? "KiCad received the footprint placement but did not answer; inspect the board before retrying."
                : "KiCad did not receive the footprint placement; no native result was confirmed.";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = message });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
        catch (Exception error) when (error is NativeApiException
            or InvalidProtocolBufferException or InvalidJsonException or JsonException or IOException)
        {
            string code = error is AutomationException known ? known.Code : "pcb_footprint_mutation_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private static CallToolResult ReceiptData(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value);
        return new CallToolResult { StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private sealed record StoredReceipt(string Fingerprint, CallToolResult Result);

    private static DocumentSpecifier ParseDocument(string json)
    {
        try { return SchematicJson.Parser.Parse<DocumentSpecifier>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException("invalid_pcb_footprint_target", "documentJson is not valid DocumentSpecifier protobuf JSON: " + error.Message); }
    }

    private static DocumentLifecycleState ParseState(string json)
    {
        try { return SchematicJson.Parser.Parse<DocumentLifecycleState>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException("invalid_pcb_footprint_state", "expectedStateJson is not valid lifecycle protobuf JSON: " + error.Message); }
    }

    private static FootprintInstance ParseFootprint(string json)
    {
        try { return BoardJson.Parser.Parse<FootprintInstance>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException("invalid_pcb_footprint", "footprintJson is not valid FootprintInstance protobuf JSON: " + error.Message); }
    }

    private static bool CanonicalUuid(string value)
        => Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty;

    private static CallToolResult Result(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new CallToolResult { StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }
}
