using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>Revision-paired access to native schematic ERC diagnostics.</summary>
[McpServerToolType]
public sealed class SchematicErcTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_erc_markers", ReadOnly = true),
     Description("Read the live ERC markers from every loaded screen of an explicitly identified schematic hierarchy at one exact lifecycle checkpoint. requestJson is SchematicTrackingReadErcMarkers protobuf JSON; expectedStateJson must be the matching kicad_document_state observation. Returns native marker identities, rule types, severity, exclusions, comments and sheet paths without changing the schematic. This reads the last native ERC result; it does not execute ERC or claim that zero markers proves the design is electrically complete."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-schematic-api", "explicit schematic document, process epoch, lifecycle state and live ERC marker snapshot"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ReadMarkers(string instanceId, string requestJson,
        string expectedStateJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        SchematicTrackingReadErcMarkers request = Parse<SchematicTrackingReadErcMarkers>(requestJson, "invalid_schematic_erc_request");
        if (request.Document is null || request.Document.Type != Kiapi.Common.Types.DocumentType.DoctypeSchematic
            || request.Document.SheetPath?.Path.Count != 1)
            throw new AutomationException("invalid_schematic_erc_target", "ERC markers require an explicit schematic root document.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_schematic_erc_state");
        if (expected.Scope != DocumentLifecycleScope.DlsSchematicHierarchy || !expected.Document.Equals(request.Document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_schematic_erc_state", "expectedStateJson must identify the same schematic root and native process epoch.");
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("schematic_instance_changed", "The expected schematic state belongs to another native process epoch.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = request.Document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("schematic_state_changed", "The schematic changed before ERC markers were read; inspect a fresh lifecycle state.");
        SchematicTrackingErcMarkers markers = await client.InvokeAsync<SchematicTrackingReadErcMarkers,
            SchematicTrackingErcMarkers>(request, cancellationToken);
        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = request.Document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("schematic_erc_state_changed", "The schematic changed while ERC markers were read; discard the result.");
        JsonElement response = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(markers));
        return Data(new
        {
            instanceId, processEpoch = session.Epoch, document = request.Document,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            response, markerCount = markers.Markers.Count,
            excludedCount = markers.Markers.Count(marker => marker.Excluded),
            nativeQuery = true, boardMutation = false, ercExecuted = false,
            zeroMarkersIsNotProofOfElectricalCompleteness = true
        });
    });

    [McpServerTool(Name = "kicad_schematic_erc_run"),
     Description("Run KiCad's native ERC checker without opening the ERC dialog. requestJson is NativeCapabilitySchematicRunErc with an explicit operation ID, process epoch and expected revision; expectedStateJson must be the matching lifecycle checkpoint. The native operation is replayable while the editor lives and returns marker counts plus a revision-paired live marker snapshot. Zero markers is not proof of electrical completeness; this tool reports the checker result and diagnostics only."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-schematic-api", "revision-checked native ERC execution, operation replay and live marker result"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Run(string instanceId, string requestJson,
        string expectedStateJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeCapabilitySchematicRunErc request = Parse<NativeCapabilitySchematicRunErc>(requestJson, "invalid_schematic_erc_run");
        if( request.Document is null || request.Document.Type != Kiapi.Common.Types.DocumentType.DoctypeSchematic
            || request.Document.SheetPath?.Path.Count != 1 || string.IsNullOrWhiteSpace(request.OperationId))
            throw new AutomationException("invalid_schematic_erc_run", "ERC execution requires a schematic root and operation ID.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_schematic_erc_state");
        if (expected.Scope != DocumentLifecycleScope.DlsSchematicHierarchy || !expected.Document.Equals(request.Document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch) || request.ProcessEpoch != expected.ProcessEpoch
            || request.ExpectedRevision is null || !request.ExpectedRevision.Equals(expected.Revision))
            throw new AutomationException("invalid_schematic_erc_state", "The ERC request and lifecycle checkpoint do not identify the same native revision.");
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("schematic_instance_changed", "The expected schematic state belongs to another native process epoch.");

        NativeErcOperationReceipt? retained =
            await TryReadNativeReceipt(client, request.Document, request.ProcessEpoch, request.OperationId,
                cancellationToken, tolerateUnsupported: true);
        if (retained is not null)
            return DescribeRun(instanceId, session.Epoch, request.Document, retained);

        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = request.Document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("schematic_state_changed", "The schematic changed before ERC execution; inspect a fresh lifecycle state.");
        try
        {
            NativeErcOperationReceipt result = await client.InvokeAsync<NativeCapabilitySchematicExecuteErc,
                NativeErcOperationReceipt>(new()
                {
                    OperationId = request.OperationId,
                    ExpectedState = expected,
                    ShowAllErrors = request.ShowAllErrors
                }, cancellationToken);
            return DescribeRun(instanceId, session.Epoch, request.Document, result);
        }
        catch (NativeApiException error) when (error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnhandled
            || error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnimplemented)
        {
            // Older native peers have only the revision-bound command. Pair its
            // immediate marker read with before/after lifecycle states and make
            // the absence of a retained native receipt explicit.
            NativeCapabilitySchematicRunErcResponse legacy = await client.InvokeAsync<NativeCapabilitySchematicRunErc,
                NativeCapabilitySchematicRunErcResponse>(request, cancellationToken);
            SchematicTrackingErcMarkers markers = await client.InvokeAsync<SchematicTrackingReadErcMarkers,
                SchematicTrackingErcMarkers>(new() { Document = request.Document }, cancellationToken);
            DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = request.Document }, cancellationToken);
            if (!after.Equals(expected))
                throw new AutomationException("schematic_erc_state_changed", "The schematic changed while legacy ERC was paired; discard the result.");
            return DescribeLegacyRun(instanceId, session.Epoch, request.Document, legacy, before, after, markers);
        }
    });

    [McpServerTool(Name = "kicad_schematic_erc_execute"),
     Description("Run KiCad's native ERC checker once for a complete typed schematic lifecycle observation. The native process reserves the operation before execution and retains the exact before/after lifecycle states and copied marker snapshot for replay after reconnect or later schematic edits. A zero marker result is diagnostic output, not proof of electrical completeness."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-schematic-api", "typed full lifecycle admission, retained ERC marker receipt and operation ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Execute(string instanceId, SchematicErcRunInput input,
        CancellationToken cancellationToken) => ExecuteAction(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input is null || string.IsNullOrWhiteSpace(input.OperationId))
            throw new AutomationException("invalid_schematic_erc_run", "A typed ERC request requires a nonempty operation ID.");
        ValidateOperationId(input.OperationId);
        DocumentLifecycleState expected = DocumentLifecycleInput.ToNative(input.ExpectedState);
        if (expected.Scope != DocumentLifecycleScope.DlsSchematicHierarchy)
            throw new AutomationException("invalid_schematic_erc_state", "Typed ERC execution requires a schematic hierarchy lifecycle state.");
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("schematic_instance_changed", "The typed lifecycle state belongs to another native process epoch.");
        NativeCapabilitySchematicExecuteErc request = new()
        {
            OperationId = input.OperationId,
            ExpectedState = expected,
            ShowAllErrors = input.ShowAllErrors
        };
        NativeErcOperationReceipt? retained = await TryReadNativeReceipt(
            client, expected.Document, expected.ProcessEpoch, request.OperationId, cancellationToken, tolerateUnsupported: false);
        if (retained is not null)
            return DescribeRun(instanceId, session.Epoch, expected.Document, retained);
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = expected.Document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("schematic_state_changed", "The schematic changed before typed ERC execution; inspect a fresh lifecycle state.");
        NativeErcOperationReceipt result = await client.InvokeAsync<NativeCapabilitySchematicExecuteErc,
            NativeErcOperationReceipt>(request, cancellationToken);
        if (!result.MarkersCaptured || result.CapturedMarkers is null)
        {
            if (result.LegacyResult is { ErrorCode.Length: > 0 })
                return DescribeRun(instanceId, session.Epoch, expected.Document, result);
            throw new AutomationException("native_erc_receipt_unavailable", "KiCad did not return an immutable ERC marker receipt for this execution.");
        }
        return DescribeRun(instanceId, session.Epoch, expected.Document, result);
    });

    [McpServerTool(Name = "kicad_schematic_erc_operation", ReadOnly = true),
     Description("Read one exact retained native ERC operation after a lost reply or MCP reconnect. The lookup is native-process owned and returns found=false when KiCad has no receipt; it never falls back to an MCP cache or submits a new ERC run."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-schematic-api", "typed document, process epoch and retained ERC operation ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Operation(string instanceId, NativeDocumentInput document,
        string expectedProcessEpoch, string operationId, CancellationToken cancellationToken) => ExecuteAction(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOperationId(operationId);
        ValidateEpoch(expectedProcessEpoch);
        DocumentSpecifier target = NativeDocumentInput.ToNative(document);
        if (target.Type != Kiapi.Common.Types.DocumentType.DoctypeSchematic || target.SheetPath?.Path.Count != 1)
            throw new AutomationException("invalid_schematic_erc_target", "ERC operation lookup requires an explicit schematic root document.");
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expectedProcessEpoch)
            throw new AutomationException("stale_process_epoch", "The ERC operation belongs to another native process epoch.");
        NativeErcOperationReceipt? receipt = await TryReadNativeReceipt(
            client, target, expectedProcessEpoch, operationId, cancellationToken, tolerateUnsupported: false);
        if (receipt is null)
            return Data(new { instanceId, processEpoch = session.Epoch, document = target, operationId, found = false,
                receiptScope = "native-process", nativeReceiptAvailable = true });
        return DescribeRun(instanceId, session.Epoch, target, receipt);
    });

    private static async Task<NativeErcOperationReceipt?> TryReadNativeReceipt(
        NativeClient client, DocumentSpecifier document, string processEpoch, string operationId,
        CancellationToken cancellationToken, bool tolerateUnsupported)
    {
        try
        {
            NativeCapabilitySchematicReadErcOperationResponse native = await client.InvokeAsync<
                NativeCapabilitySchematicReadErcOperation, NativeCapabilitySchematicReadErcOperationResponse>(
                    new() { Document = document, OperationId = operationId, ProcessEpoch = processEpoch }, cancellationToken);
            if (!native.Found) return null;
            if (native.Receipt is null || native.Receipt.LegacyResult is null
                || !native.Receipt.LegacyResult.Document.Equals(document)
                || native.Receipt.LegacyResult.OperationId != operationId
                || native.Receipt.LegacyResult.ProcessEpoch != processEpoch)
                throw new AutomationException("wrong_erc_operation_receipt", "KiCad returned an ERC receipt for a different operation target.");
            return native.Receipt;
        }
        catch (NativeApiException error) when (tolerateUnsupported
            && (error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnhandled
                || error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnimplemented))
        {
            return null;
        }
        catch (NativeApiException error) when (!tolerateUnsupported
            && (error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnhandled
                || error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnimplemented))
        {
            throw new AutomationException("native_erc_receipt_unavailable",
                "The connected KiCad peer does not provide native retained ERC receipts.");
        }
    }

    private static CallToolResult DescribeRun(string instanceId, string processEpoch,
        DocumentSpecifier document, NativeErcOperationReceipt result, bool nativeReceiptAvailable = true)
    {
        SchematicTrackingErcMarkers markers = result.CapturedMarkers ?? new SchematicTrackingErcMarkers();
        NativeCapabilitySchematicRunErcResponse legacy = result.LegacyResult ?? new NativeCapabilitySchematicRunErcResponse();
        JsonElement structured = JsonSerializer.SerializeToElement(new
        {
            instanceId,
            processEpoch,
            document,
            operationId = legacy.OperationId,
            beforeState = result.BeforeState is null ? null : SchematicJson.Formatter.Format(result.BeforeState),
            afterState = result.AfterState is null ? null : SchematicJson.Formatter.Format(result.AfterState),
            result = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(legacy)),
            markers = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(markers)),
            markerCount = legacy.MarkerCount,
            nativeExecution = true,
            operationReceipt = legacy.Completed,
            operationState = result.State.ToString(),
            admissionVerified = result.AdmissionVerified,
            executionStarted = result.ExecutionStarted,
            markersCaptured = result.MarkersCaptured,
            nativeReceiptAvailable,
            zeroMarkersIsNotProofOfElectricalCompleteness = true
        });
        return new CallToolResult { StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static CallToolResult DescribeLegacyRun(string instanceId, string processEpoch,
        DocumentSpecifier document, NativeCapabilitySchematicRunErcResponse legacy,
        DocumentLifecycleState before, DocumentLifecycleState after, SchematicTrackingErcMarkers markers)
    {
        var receipt = new NativeErcOperationReceipt
        {
            LegacyResult = legacy,
            BeforeState = before,
            AfterState = after,
            CapturedMarkers = markers,
            State = legacy.Completed ? NativeErcOperationState.NeosCompleted : NativeErcOperationState.NeosUnknown,
            AdmissionVerified = false,
            ExecutionStarted = legacy.Completed,
            MarkersCaptured = true
        };
        return DescribeRun(instanceId, processEpoch, document, receipt, nativeReceiptAvailable: false);
    }

    private static void ValidateOperationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "Provide a nonempty operation UUID.");
    }

    private static void ValidateEpoch(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_process_epoch", "Provide a nonempty native process epoch UUID.");
    }

    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    {
        try { return SchematicJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException(code, "Request JSON is not valid schematic protobuf JSON: " + error.Message); }
    }

    private static CallToolResult Data(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value);
        return new() { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException or IOException)
        {
            JsonElement structured = JsonSerializer.SerializeToElement(new
            { errorCode = error is AutomationException known ? known.Code : "schematic_erc_failed", errorMessage = error.Message });
            return new() { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private static Task<CallToolResult> ExecuteAction(Func<Task<CallToolResult>> action) => Execute(action);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicErcRunInput : NativeDocumentInputObject
{
    private DocumentLifecycleInput? expectedState;
    private string? operationId;
    private bool showAllErrors;

    [Description("Complete lifecycle observation captured immediately before ERC execution, including the full native file baseline set.")]
    public DocumentLifecycleInput? ExpectedState { get => expectedState; set => expectedState = BindOnce(value); }
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    public bool ShowAllErrors { get => showAllErrors; set => showAllErrors = BindOnce(value); }
}
