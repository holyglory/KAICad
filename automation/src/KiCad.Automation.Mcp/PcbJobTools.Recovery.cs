using System.ComponentModel;
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
/// Typed admission for the future native manufacturing manager.  The native
/// handlers are registered now but deliberately return AS_UNIMPLEMENTED until
/// detached snapshots, worker ownership and durable journals are enabled.
/// These wrappers therefore never claim cancellation or cross-process recovery.
/// </summary>
public sealed partial class PcbJobTools
{
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
