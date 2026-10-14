using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Kiapi.Common;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Typed admission for the native manufacturing manager.  SVG requests may
/// complete through KiCad's synchronous owner-thread plot adapter; detached
/// snapshots, worker ownership and cross-process publication remain separate
/// unsupported boundaries.  These wrappers therefore never claim cancellation
/// or restart recovery for those boundaries.
/// </summary>
public sealed partial class PcbJobTools
{
    [McpServerTool(Name = "kicad_pcb_export_start_typed", ReadOnly = false),
     Description("Admit one typed PCB manufacturing export to the native recovery manager. SVG requests dispatch synchronously through KiCad's owner-thread plot adapter and return a journaled publication receipt; other job families remain fail-closed as export_adapter_unavailable. Detached workers and restart recovery are not claimed, and the existing synchronous kicad_pcb_export tool remains unchanged."),
     KiCadCapability("pcb-manufacturing", "typed-recovery-manager-admission", "typed export request, durable journal admission and explicit adapter availability"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbManufacturingRecoveryContractTests.CompiledStdioPublishesTypedRecoveryToolsAndFailsClosedForIncompleteInput")]
    public Task<CallToolResult> ExportStartTyped(string instanceId,
        PcbManufacturingExportInput input, CancellationToken cancellationToken) => ExecuteRecovery(async () =>
    {
        if (input is null)
            throw new AutomationException("invalid_pcb_export_input", "A typed PCB manufacturing export input is required.");
        RunPcbManufacturingExport request = BuildManufacturingRequest(instanceId, input);
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != request.ProcessEpoch)
            throw new AutomationException("stale_process_epoch", "The lifecycle observation belongs to another native process epoch.");
        PcbManufacturingExportReceipt receipt = await client.InvokeAsync<StartPcbManufacturingExportJob,
            PcbManufacturingExportReceipt>(new() { Request = request }, cancellationToken);
        ValidateManagerReceipt(receipt, request, session.Epoch);
        bool adapterAvailable = receipt.ExportAdapterAvailable;
        bool completed = receipt.Status == PcbManufacturingExportStatus.PcbMfgExportCompleted;
        string? errorCode = completed ? null
            : adapterAvailable && !string.IsNullOrWhiteSpace(receipt.ErrorCode)
                ? receipt.ErrorCode : "export_adapter_unavailable";
        string errorMessage = completed ? "" : receipt.ErrorMessage.Length == 0
            ? adapterAvailable
                ? "The synchronous SVG adapter did not produce a completed publication receipt."
                : "The SVG owner-thread adapter is unavailable; no output was generated."
            : receipt.ErrorMessage;
        return Data(new
        {
            instanceId, processEpoch = session.Epoch, operationId = receipt.OperationId,
            managerAccepted = true, recoverySupported = true,
            exportAdapterAvailable = adapterAvailable,
            operationOutcome = completed
                ? PcbExportRecoveryPolicy.OutcomeCompleted : PcbExportRecoveryPolicy.Unknown,
            status = receipt.Status.ToString(), recoveryState = receipt.RecoveryState.ToString(),
            terminal = receipt.Terminal, workerFinished = receipt.WorkerFinished,
            nativeDispatch = receipt.NativeDispatch, outputGenerated = receipt.Artifacts.Count != 0,
            receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(receipt))
        }, !completed, errorCode, errorMessage);
    }, input?.OperationId, input?.ExpectedState?.ProcessEpoch);

    [McpServerTool(Name = "kicad_pcb_export_cancel_typed", ReadOnly = false),
     Description("Request cancellation of one typed PCB manufacturing export through the native manager. The current synchronous exporter has no cancellation manager, so the call fails closed with recoverySupported=false and preserves the durable operation outcome. No output is deleted or declared cancelled by this boundary."),
     KiCadCapability("pcb-manufacturing", "typed-recovery-contract", "explicit PCB document, operation ID, current process epoch and fail-closed cancellation result"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbManufacturingRecoveryContractTests.CompiledStdioPublishesTypedRecoveryToolsAndFailsClosedForIncompleteInput")]
    public Task<CallToolResult> ExportCancelTyped(string instanceId,
        PcbManufacturingCancelInput input, CancellationToken cancellationToken) => ExecuteRecovery(async () =>
    {
        if (input is null)
            throw new AutomationException("invalid_pcb_export_cancel_input", "input is required.");
        CancelPcbManufacturingExport request = input.ToNative();
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != request.ProcessEpoch)
            throw new AutomationException("stale_process_epoch", "The cancellation request belongs to another native process epoch.");

        try
        {
            PcbManufacturingExportReceipt receipt = await client.InvokeAsync<CancelPcbManufacturingExport,
                PcbManufacturingExportReceipt>(request, cancellationToken);
            ValidateManagerReceiptIdentity(receipt, request.Document, request.OperationId, request.ProcessEpoch);
            if( receipt.RecoveryState != PcbManufacturingRecoveryState.PcbMfgRecoveryCancelled )
                throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                    "The native manager did not cancel this operation; its previous outcome remains unchanged.");
            if( receipt.RecoveryState == PcbManufacturingRecoveryState.PcbMfgRecoveryCancelled
                && (!receipt.CancellationRequested || !receipt.Terminal || receipt.NativeDispatch
                    || receipt.Artifacts.Count != 0 || receipt.PublicationCommitted) )
                throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                    "The native cancellation receipt does not prove a pre-dispatch cancellation.");
            return Data(new
            {
                instanceId, processEpoch = session.Epoch, operationId = request.OperationId,
                recoverySupported = true, cancellationSupported = true,
                cancellationRequested = receipt.CancellationRequested,
                workerFinished = receipt.WorkerFinished,
                recoveryState = receipt.RecoveryState.ToString(),
                receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(receipt))
            });
        }
        catch (NativeApiException error) when (error.Status == (int)ApiStatusCode.AsUnimplemented)
        {
            throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                "The native manufacturing manager does not yet provide cancellation; the existing synchronous export outcome is unchanged.");
        }
    }, input?.OperationId, input?.ProcessEpoch);

    [McpServerTool(Name = "kicad_pcb_export_reconcile_typed", ReadOnly = true),
     Description("Reconcile one typed PCB manufacturing export across an MCP or native reconnect through the native manager. The current peer has only process-owned receipts, so cross-process reconciliation fails closed with recoverySupported=false and does not retry or publish staged files."),
     KiCadCapability("pcb-manufacturing", "typed-recovery-contract", "explicit PCB document, operation ID, fingerprint, current and prior process epochs"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbManufacturingRecoveryContractTests.CompiledStdioPublishesTypedRecoveryToolsAndFailsClosedForIncompleteInput")]
    public Task<CallToolResult> ExportReconcileTyped(string instanceId,
        PcbManufacturingReconcileInput input, CancellationToken cancellationToken) => ExecuteRecovery(async () =>
    {
        if (input is null)
            throw new AutomationException("invalid_pcb_export_reconcile_input", "input is required.");
        ReconcilePcbManufacturingExport request = input.ToNative();
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != request.ProcessEpoch)
            throw new AutomationException("stale_process_epoch", "The reconciliation request belongs to another native process epoch.");

        try
        {
            PcbManufacturingExportReceiptResponse response = await client.InvokeAsync<ReconcilePcbManufacturingExport,
                PcbManufacturingExportReceiptResponse>(request, cancellationToken);
            ValidateReconcileResponseSupport(response, request.AllowPriorProcessEpoch, request.ProcessEpoch);
            if( response.Found )
            {
                ValidateManagerReceiptIdentity(response.Receipt, request.Document, request.OperationId,
                    request.AllowPriorProcessEpoch ? request.PriorProcessEpoch! : request.ProcessEpoch);
            }
            return Data(new
            {
                instanceId, processEpoch = session.Epoch, operationId = request.OperationId,
                recoverySupported = true, found = response.Found,
                receipt = response.Found
                    ? JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.Receipt))
                    : (JsonElement?)null
            });
        }
        catch (NativeApiException error) when (error.Status == (int)ApiStatusCode.AsUnimplemented)
        {
            throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                "The native manufacturing manager does not yet provide cross-process reconciliation; no retry or publication was attempted.");
        }
    }, input?.OperationId, input?.ProcessEpoch);

    /// <summary>
    /// A not-found response is meaningful only when the native peer proved it
    /// could inspect its journal. Older peers may answer the request without
    /// that capability; such a response must remain an explicit recovery
    /// refusal instead of being exposed as evidence that no operation exists.
    /// </summary>
    internal static void ValidateReconcileResponseSupport(
        PcbManufacturingExportReceiptResponse response, bool allowPriorProcessEpoch,
        string expectedObservingProcessEpoch)
    {
        if( response is null || !response.ReconciliationSupported )
            throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                "The native manufacturing manager does not provide a verified reconciliation journal; no retry or publication was attempted.");

        if( string.IsNullOrWhiteSpace(expectedObservingProcessEpoch)
            || response.ObservingProcessEpoch != expectedObservingProcessEpoch )
            throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                "The native reconciliation response was observed by a different process epoch; no retry or publication was attempted.");

        if( response.Found && (!response.RequestFingerprintVerified || !response.JournalVerified
            || (allowPriorProcessEpoch && !response.PriorProcessEpochVerified)) )
            throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                "The native reconciliation response did not prove its journal and identity checks.");
    }

    [McpServerTool(Name = "kicad_pcb_export_manager_capabilities_typed", ReadOnly = true),
     Description("Read the native manufacturing manager's supported recovery boundaries for one PCB. The manager reports durable admission and journal recovery plus synchronous SVG owner-thread dispatch; detached workers, restart publication and complete dependency snapshots remain unavailable."),
     KiCadCapability("pcb-manufacturing", "typed-recovery-capabilities", "explicit PCB document and current process epoch"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbManufacturingRecoveryContractTests.CompiledStdioPublishesTypedRecoveryToolsAndFailsClosedForIncompleteInput")]
    public Task<CallToolResult> ExportManagerCapabilitiesTyped(string instanceId,
        PcbManufacturingManagerCapabilitiesInput input, CancellationToken cancellationToken) => ExecuteRecovery(async () =>
    {
        if (input is null) throw new AutomationException("invalid_pcb_export_capabilities_input", "input is required.");
        NativeDocumentInput document = input.Document ?? throw new AutomationException("invalid_pcb_export_capabilities_input", "document is required.");
        DocumentSpecifier target = RecoveryInputValidation.Document(document, "invalid_pcb_export_capabilities_input");
        string epoch = RecoveryInputValidation.Uuid(input.ProcessEpoch, "processEpoch", "invalid_pcb_export_capabilities_input");
        NativeClient client = registry.Client(instanceId); AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != epoch) throw new AutomationException("stale_process_epoch", "The capability request belongs to another native process epoch.");
        PcbManufacturingManagerCapabilities result = await client.InvokeAsync<GetPcbManufacturingManagerCapabilities, PcbManufacturingManagerCapabilities>(new() { Document = target, ProcessEpoch = epoch }, cancellationToken);
        if( !result.Document.Equals(target) || result.ProcessEpoch != epoch || string.IsNullOrWhiteSpace(result.UnavailableReason)
            || !result.SynchronousSvgExportSupported
            || !result.AdmissionSupported || !result.RequestJournalSupported || !result.JournalReadSupported
            || result.DetachedExportSupported || result.PublicationSupported || result.CompleteSnapshotSupported )
            throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                "The native manager capability response did not bind the requested PCB and process epoch.");
        return Data(new { instanceId, processEpoch = session.Epoch,
            synchronousSvgExportSupported = result.SynchronousSvgExportSupported,
            capabilities = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(result)) });
    }, null, input?.ProcessEpoch);

    [McpServerTool(Name = "kicad_pcb_export_read_typed", ReadOnly = true),
     Description("Read one exact durable manufacturing-manager journal by PCB document, operation ID, current process epoch and request fingerprint. The result is read-only and never resumes or republishes work."),
     KiCadCapability("pcb-manufacturing", "typed-recovery-journal-read", "explicit PCB document, operation ID, process epoch and request fingerprint"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbManufacturingRecoveryContractTests.CompiledStdioPublishesTypedRecoveryToolsAndFailsClosedForIncompleteInput")]
    public Task<CallToolResult> ExportReadTyped(string instanceId,
        PcbManufacturingReadInput input, CancellationToken cancellationToken) => ExecuteRecovery(async () =>
    {
        if (input is null) throw new AutomationException("invalid_pcb_export_read_input", "input is required.");
        DocumentSpecifier target = RecoveryInputValidation.Document(input.Document, "invalid_pcb_export_read_input");
        string epoch = RecoveryInputValidation.Uuid(input.ProcessEpoch, "processEpoch", "invalid_pcb_export_read_input");
        string operation = RecoveryInputValidation.Uuid(input.OperationId, "operationId", "invalid_pcb_export_read_input");
        string fingerprint = RecoveryInputValidation.Digest(input.RequestFingerprintSha256, "requestFingerprintSha256", "invalid_pcb_export_read_input");
        NativeClient client = registry.Client(instanceId); AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if( session.Epoch != epoch ) throw new AutomationException("stale_process_epoch", "The journal read belongs to another native process epoch.");
        PcbManufacturingExportReceiptResponse response = await client.InvokeAsync<ReadPcbManufacturingExportJob, PcbManufacturingExportReceiptResponse>(
            new() { Document = target, OperationId = operation, ProcessEpoch = epoch, RequestFingerprintSha256 = fingerprint }, cancellationToken);
        ValidateReconcileResponseSupport(response, allowPriorProcessEpoch: false,
            expectedObservingProcessEpoch: epoch);
        if( response.Found )
        {
            ValidateManagerReceiptIdentity(response.Receipt, target, operation, epoch);
            if( response.Receipt.RequestFingerprintSha256 != fingerprint )
                throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable, "The native journal fingerprint does not match the requested operation.");
        }
        return Data(new { instanceId, processEpoch = epoch, operationId = operation, found = response.Found,
            recoverySupported = response.ReconciliationSupported, requestFingerprintVerified = response.RequestFingerprintVerified,
            journalVerified = response.JournalVerified, receipt = response.Found
                ? JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.Receipt)) : (JsonElement?)null });
    }, input?.OperationId, input?.ProcessEpoch);

    private static async Task<CallToolResult> ExecuteRecovery(Func<Task<CallToolResult>> operation,
        string? operationId, string? processEpoch)
    {
        try { return await operation(); }
        catch (OperationCanceledException)
        {
            return Data(new
            {
                operationId, processEpoch, recoverySupported = false,
                cancellationRequested = false, mutationSubmitted = false,
                outcome = PcbExportRecoveryPolicy.Unknown,
                errorMessage = "The recovery request was cancelled before a manager result was confirmed."
            }, true, PcbExportRecoveryPolicy.RecoveryUnavailable,
                "Native manufacturing recovery is unavailable until the manager owns cancellation and durable journals.");
        }
        catch (Exception error) when (error is AutomationException or NativeApiException
            or NngException or JsonException or InvalidOperationException or ArgumentException
            or InvalidProtocolBufferException or InvalidJsonException or IOException)
        {
            string code = error is AutomationException known ? known.Code
                : error is NativeApiException native && native.Status == (int)ApiStatusCode.AsUnimplemented
                    ? PcbExportRecoveryPolicy.RecoveryUnavailable
                    : "pcb_export_recovery_failed";
            return Data(new
            {
                operationId, processEpoch, recoverySupported = false,
                cancellationRequested = false, mutationSubmitted = false,
                outcome = PcbExportRecoveryPolicy.Unknown,
                errorMessage = error.Message
            }, true, code, error.Message);
        }
    }

    private static void ValidateManagerReceipt(PcbManufacturingExportReceipt receipt,
        RunPcbManufacturingExport request, string processEpoch)
    {
        ValidateManagerReceiptIdentity(receipt, request.Document, request.OperationId, processEpoch);
        if( receipt.RequestFingerprintSha256 != Convert.ToHexStringLower(SHA256.HashData(request.ToByteArray()))
            || receipt.ExpectedState is null || !receipt.ExpectedState.Equals(request.ExpectedState)
            || receipt.ObservedBefore is null || !receipt.ObservedBefore.Equals(request.ExpectedState) )
            throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                "The native manager receipt does not bind the exact request and lifecycle checkpoint.");
        if( !receipt.ExportAdapterAvailable && (receipt.Status != PcbManufacturingExportStatus.PcbMfgExportUnsubmitted
            || !receipt.Terminal || receipt.NativeDispatch || receipt.Artifacts.Count != 0
            || receipt.OutputManifestComplete || receipt.PublicationCommitted
            || receipt.PublicationState != PcbManufacturingPublicationState.PcbMfgPublicationUnspecified) )
            throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                "The unavailable exporter receipt claims work or publication that was not performed.");
        if( receipt.ExportAdapterAvailable && receipt.Status != PcbManufacturingExportStatus.PcbMfgExportCompleted
            && (!receipt.Terminal || !receipt.NativeDispatch || string.IsNullOrWhiteSpace(receipt.ErrorCode)) )
            throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                "The available exporter receipt does not close a failed or indeterminate dispatch explicitly.");
    }

    private static void ValidateManagerReceiptIdentity(PcbManufacturingExportReceipt receipt,
        DocumentSpecifier document, string operationId, string processEpoch)
    {
        if( receipt is null || !receipt.Document.Equals(document) || receipt.OperationId != operationId
            || receipt.ProcessEpoch != processEpoch || receipt.DurableJournalSha256.Length != 64
            || !receipt.DurableJournalSha256.All(char.IsAsciiHexDigitLower) )
            throw new AutomationException(PcbExportRecoveryPolicy.RecoveryUnavailable,
                "The native manager receipt has an untrusted operation, document, epoch or journal identity.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbManufacturingCancelInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private string? processEpoch;
    private string? operationId;
    private bool beforeNativeDispatchOnly;

    [Description("The exact PCB document whose export may be cancelled.")]
    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    public bool BeforeNativeDispatchOnly { get => beforeNativeDispatchOnly; set => beforeNativeDispatchOnly = BindOnce(value); }

    internal CancelPcbManufacturingExport ToNative()
    {
        DocumentSpecifier target = RecoveryInputValidation.Document(Document, "invalid_pcb_export_cancel_input");
        string epoch = RecoveryInputValidation.Uuid(ProcessEpoch, "processEpoch", "invalid_pcb_export_cancel_input");
        string operation = RecoveryInputValidation.Uuid(OperationId, "operationId", "invalid_pcb_export_cancel_input");
        return new CancelPcbManufacturingExport
        {
            Document = target, ProcessEpoch = epoch, OperationId = operation,
            BeforeNativeDispatchOnly = BeforeNativeDispatchOnly
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbManufacturingReconcileInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private string? processEpoch;
    private string? operationId;
    private string? requestFingerprintSha256;
    private string? priorProcessEpoch;
    private bool allowPriorProcessEpoch;

    [Description("The exact PCB document whose export journal is being reconciled.")]
    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    public string? RequestFingerprintSha256 { get => requestFingerprintSha256; set => requestFingerprintSha256 = BindOnce(value); }
    public string? PriorProcessEpoch { get => priorProcessEpoch; set => priorProcessEpoch = BindOnce(value); }
    public bool AllowPriorProcessEpoch { get => allowPriorProcessEpoch; set => allowPriorProcessEpoch = BindOnce(value); }

    internal ReconcilePcbManufacturingExport ToNative()
    {
        DocumentSpecifier target = RecoveryInputValidation.Document(Document, "invalid_pcb_export_reconcile_input");
        string epoch = RecoveryInputValidation.Uuid(ProcessEpoch, "processEpoch", "invalid_pcb_export_reconcile_input");
        string operation = RecoveryInputValidation.Uuid(OperationId, "operationId", "invalid_pcb_export_reconcile_input");
        string fingerprint = RecoveryInputValidation.Digest(RequestFingerprintSha256,
            "requestFingerprintSha256", "invalid_pcb_export_reconcile_input");
        if (AllowPriorProcessEpoch != !string.IsNullOrWhiteSpace(PriorProcessEpoch))
            throw new AutomationException("invalid_pcb_export_reconcile_input",
                "allowPriorProcessEpoch must agree with priorProcessEpoch.");
        string prior = AllowPriorProcessEpoch
            ? RecoveryInputValidation.Uuid(PriorProcessEpoch, "priorProcessEpoch", "invalid_pcb_export_reconcile_input")
            : "";
        return new ReconcilePcbManufacturingExport
        {
            Document = target, ProcessEpoch = epoch, OperationId = operation,
            RequestFingerprintSha256 = fingerprint, PriorProcessEpoch = prior,
            AllowPriorProcessEpoch = AllowPriorProcessEpoch
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbManufacturingManagerCapabilitiesInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private string? processEpoch;
    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbManufacturingReadInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private string? processEpoch;
    private string? operationId;
    private string? requestFingerprintSha256;
    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    public string? RequestFingerprintSha256 { get => requestFingerprintSha256; set => requestFingerprintSha256 = BindOnce(value); }
}

internal static class RecoveryInputValidation
{
    internal static DocumentSpecifier Document(NativeDocumentInput? input, string code)
    {
        if (input is null)
            throw new AutomationException(code, "document is required.");
        DocumentSpecifier document = NativeDocumentInput.ToNative(input);
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException(code, "document must identify an explicit PCB.");
        return document;
    }

    internal static string Uuid(string? value, string name, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParseExact(value, "D", out Guid id)
            || id == Guid.Empty || value != id.ToString("D"))
            throw new AutomationException(code, name + " must be a canonical lowercase UUID.");
        return value;
    }

    internal static string Digest(string? value, string name, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || !value.All(char.IsAsciiHexDigitLower))
            throw new AutomationException(code, name + " must be a lowercase SHA-256 digest.");
        return value;
    }
}
