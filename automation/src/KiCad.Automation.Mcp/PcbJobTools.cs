using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Native;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Kiapi.Board.Jobs;
using Kiapi.Common.Types;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Lifecycle-guarded wrappers over KiCad's existing board manufacturing jobs.
/// The native jobs already know how to generate the formats; this layer keeps
/// the export paired with the exact board observation that produced it and
/// returns bounded, hashable output evidence for an agent.
/// </summary>
[McpServerToolType]
public sealed class PcbJobTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_export", ReadOnly = false),
     Description("Run exactly one typed native PCB manufacturing export into a new absolute outputDirectory. Supply an explicit operationId, typed document and complete lifecycle observation, plus exactly one concrete job setting (3D, render, SVG, DXF, PDF, PostScript, Gerbers, drill, position, GenCAD, IPC-2581, IPC-D-356, ODB++ or board statistics). KiCad stages and atomically publishes the directory without replacing existing files. Reuse the same operationId after a lost reply; cancellation never claims native cancellation."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-task-export", "typed operation ID, explicit document, complete lifecycle state, owned output directory, native receipt"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportTyped(string instanceId, PcbManufacturingExportInput input,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        if (input is null)
            throw new AutomationException("invalid_pcb_export_input", "A typed PCB manufacturing export input is required.");
        ValidateOperationId(input.OperationId);
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId is required for a PCB manufacturing export.");
        if (string.IsNullOrWhiteSpace(input.OutputDirectory) || !Path.IsPathFullyQualified(input.OutputDirectory))
            throw new AutomationException("invalid_pcb_export_path", "outputDirectory must be an absolute path.");

        DocumentSpecifier document = NativeDocumentInput.ToNative(input.Document);
        DocumentLifecycleState expected = DocumentLifecycleInput.ToNative(input.ExpectedState);
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb || !expected.Document.Equals(document)
            || expected.Scope != DocumentLifecycleScope.DlsPcb || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_pcb_export_state", "The typed document and lifecycle observation must identify the same PCB.");

        RunPcbManufacturingExport request = new()
        {
            OperationId = input.OperationId,
            ProcessEpoch = expected.ProcessEpoch,
            Document = document.Clone(),
            ExpectedState = expected,
            OutputDirectory = Path.GetFullPath(input.OutputDirectory)
        };
        if (input.Job is null)
            throw new AutomationException("invalid_pcb_export_job", "Exactly one concrete PCB manufacturing job setting is required.");
        input.Job.ApplyTo(request);
        PcbManufacturingContract.ValidateRequest(request);

        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("stale_process_epoch", "The lifecycle observation belongs to another native process epoch.");
        string documentJson = SchematicJson.Formatter.Format(document);
        string requestFingerprint = PcbExportRecoveryPolicy.Fingerprint(input.OperationId,
            expected.ProcessEpoch, documentJson, request.OutputDirectory, request);
        var recovery = new PcbExportRecoveryStore(registry.StateDirectory);
        recovery.Begin(instanceId, input.OperationId, expected.ProcessEpoch, documentJson,
            requestFingerprint, request.OutputDirectory);

        // A cancellation before the request is handed to NNG is known to be
        // unsubmitted. Once InvokeAsync starts, cancellation is deliberately
        // indeterminate because the native exporter may still be running.
        if (cancellationToken.IsCancellationRequested)
        {
            recovery.MarkCancelled(input.OperationId);
            throw new AutomationException("operation_cancelled",
                "The manufacturing export was cancelled before native dispatch; no native cancellation was requested.");
        }

        PcbManufacturingExportReceipt receipt;
        try
        {
            receipt = await client.InvokeAsync<RunPcbManufacturingExport,
                PcbManufacturingExportReceipt>(request, cancellationToken);
            PcbManufacturingContract.ValidateReceipt(receipt, document, expected,
                input.OperationId, expected.ProcessEpoch, requestFingerprint);
            recovery.MarkReceipt(input.OperationId, receipt,
                Directory.Exists(receipt.OutputDirectory),
                PcbExportRecoveryPolicy.OutputMatches(receipt),
                Directory.Exists(receipt.StagingDirectory));
        }
        catch (NngException error)
        {
            recovery.MarkUnknown(input.OperationId, error.RequestDelivered);
            throw;
        }
        catch (OperationCanceledException)
        {
            recovery.MarkUnknown(input.OperationId, null);
            throw;
        }
        catch
        {
            recovery.MarkUnknown(input.OperationId, true);
            throw;
        }
        return Data(new
        {
            instanceId,
            processEpoch = session.Epoch,
            operationId = receipt.OperationId,
            requestFingerprintSha256 = receipt.RequestFingerprintSha256,
            job = receipt.JobKind,
            status = receipt.Status.ToString(),
            outcome = PcbExportRecoveryPolicy.Outcome(receipt, receipt.NativeDispatch),
            dispatchStage = receipt.DispatchStage.ToString(),
            nativeDispatch = receipt.NativeDispatch,
            terminal = receipt.Terminal,
            errorCode = receipt.ErrorCode,
            errorMessage = receipt.ErrorMessage,
            outputDirectory = receipt.OutputDirectory,
            stagingDirectory = receipt.StagingDirectory,
            publicationState = receipt.PublicationState.ToString(),
            artifacts = receipt.Artifacts.Select(artifact => new
            {
                path = artifact.Path,
                relativePath = artifact.RelativePath,
                bytes = artifact.Bytes,
                sha256 = artifact.Sha256,
                ownership = artifact.Ownership.ToString()
            }).ToArray(),
            outputManifestComplete = receipt.OutputManifestComplete,
            outputManifestSha256 = receipt.OutputManifestSha256,
            beforeState = SchematicJson.Formatter.Format(receipt.ObservedBefore),
            afterState = receipt.ObservedAfter is null || receipt.ObservedAfter.ProcessEpoch.Length == 0
                ? "" : SchematicJson.Formatter.Format(receipt.ObservedAfter)
        }, receipt.Status is not (PcbManufacturingExportStatus.PcbMfgExportCompleted),
            receipt.ErrorCode.Length == 0 ? null : receipt.ErrorCode,
            receipt.ErrorMessage.Length == 0 ? null : receipt.ErrorMessage);
    }, input?.OperationId, input?.ExpectedState?.ProcessEpoch);

    [McpServerTool(Name = "kicad_pcb_export_receipt", ReadOnly = true),
     Description("Read the native receipt for one typed PCB manufacturing export after MCP reconnect or a lost reply. Supply the exact PCB document, native process epoch and operationId. A missing receipt is reported as found=false; a different epoch or document is an error. This read never retries or republishes an export."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-task-export", "typed document, process epoch, operation ID, native receipt"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportReceipt(string instanceId, NativeDocumentInput documentInput,
        string processEpoch, string operationId, CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateOperationId(operationId);
        ValidateEpoch(processEpoch);
        DocumentSpecifier document = NativeDocumentInput.ToNative(documentInput);
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_export_target", "The receipt target must be an explicit PCB document.");
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != processEpoch)
            throw new AutomationException("stale_process_epoch", "The requested receipt belongs to another native process epoch.");
        PcbManufacturingExportReceiptResponse response = await client.InvokeAsync<ReadPcbManufacturingExport,
            PcbManufacturingExportReceiptResponse>(new()
            {
                Document = document,
                OperationId = operationId,
                ProcessEpoch = processEpoch
            }, cancellationToken);
        if (response.Found)
        {
            PcbManufacturingContract.ValidateReceipt(response.Receipt, document, null, operationId, processEpoch);
            var recovery = new PcbExportRecoveryStore(registry.StateDirectory);
            if (recovery.Read(operationId) is { } record)
            {
                PcbExportRecoveryPolicy.ValidateReceiptBinding(record, instanceId, processEpoch,
                    SchematicJson.Formatter.Format(document));
                if (!string.Equals(record.RequestFingerprintSha256,
                        response.Receipt.RequestFingerprintSha256, StringComparison.Ordinal))
                    throw new AutomationException("export_recovery_identity_mismatch",
                        "The native receipt fingerprint does not match the durable export request.");
                recovery.MarkReceipt(operationId, response.Receipt,
                    Directory.Exists(response.Receipt.OutputDirectory),
                    PcbExportRecoveryPolicy.OutputMatches(response.Receipt),
                    Directory.Exists(response.Receipt.StagingDirectory));
            }
        }
        JsonElement? receiptJson = response.Found
            ? JsonDocument.Parse(SchematicJson.Formatter.Format(response.Receipt)).RootElement.Clone()
            : null;
        return Data(new
        {
            instanceId,
            processEpoch = session.Epoch,
            operationId,
            found = response.Found,
            outcome = response.Found
                ? PcbExportRecoveryPolicy.Outcome(response.Receipt, response.Receipt.NativeDispatch)
                : PcbExportRecoveryPolicy.Unknown,
            receipt = receiptJson
        });
    }, operationId, processEpoch);

    [McpServerTool(Name = "kicad_pcb_export_reconcile", ReadOnly = true),
     Description("Reconcile one durable typed PCB export after an MCP or native reconnect. Supply the exact instance, PCB document and operationId. A live native receipt is preferred when its process epoch still matches; otherwise the durable receipt and owned output/staging paths are inspected. Only a verified completed publication or a terminal result with no owned paths is retryable; indeterminate outcomes never authorize an automatic retry."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus-native-task-export-recovery", "instance, typed PCB document, operation ID, durable request identity and owned output paths"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbManufacturingMcpProcessTests.TypedManufacturingToolPublishesJobSchemaAndRejectsMalformedIdentity")]
    public Task<CallToolResult> ExportReconcile(string instanceId, NativeDocumentInput documentInput,
        string operationId, CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateOperationId(operationId);
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId is required to reconcile a PCB export.");
        DocumentSpecifier document = NativeDocumentInput.ToNative(documentInput);
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_export_target", "The recovery target must be an explicit PCB document.");

        var recovery = new PcbExportRecoveryStore(registry.StateDirectory);
        PcbExportRecoveryRecord? record = recovery.Read(operationId);
        if (record is null)
            return Data(new
            {
                instanceId, operationId, status = "untracked", retryAllowed = false,
                publicationMayHaveHappened = false, outputExists = false, stagingExists = false,
                nativeReceiptFound = false, reason = "No durable export operation record exists for this operation ID."
            });
        string documentJson = SchematicJson.Formatter.Format(document);
        if (record.InstanceId != instanceId || record.DocumentJson != documentJson)
            throw new AutomationException("export_recovery_identity_mismatch",
                "The durable export record belongs to another instance or PCB document.");

        bool outputExists = Directory.Exists(record.OutputDirectory);
        bool stagingExists = Directory.Exists(record.StagingDirectory);
        bool outputVerified = false;
        bool nativeReceiptFound = false;
        PcbManufacturingExportReceipt? receipt = null;
        AutomationSession? session = null;
        string? nativeUnavailableReason = null;

        try
        {
            NativeClient client = registry.Client(instanceId);
            session = await client.HandshakeAsync(cancellationToken);
            if (session.Epoch == record.ProcessEpoch)
            {
                PcbManufacturingExportReceiptResponse response = await client.InvokeAsync<
                    ReadPcbManufacturingExport, PcbManufacturingExportReceiptResponse>(new()
                    {
                        Document = document,
                        OperationId = operationId,
                        ProcessEpoch = record.ProcessEpoch
                    }, cancellationToken);
                if (response.Found)
                {
                    PcbManufacturingContract.ValidateReceipt(response.Receipt, document, null,
                        operationId, record.ProcessEpoch);
                    if (!string.Equals(record.RequestFingerprintSha256,
                            response.Receipt.RequestFingerprintSha256, StringComparison.Ordinal))
                        throw new AutomationException("export_recovery_identity_mismatch",
                            "The native receipt fingerprint does not match the durable export request.");
                    receipt = response.Receipt;
                    nativeReceiptFound = true;
                    outputExists = Directory.Exists(receipt.OutputDirectory);
                    outputVerified = PcbExportRecoveryPolicy.OutputMatches(receipt);
                    stagingExists = Directory.Exists(receipt.StagingDirectory);
                    recovery.MarkReceipt(operationId, receipt, outputExists, outputVerified, stagingExists);
                }
            }
            else
            {
                nativeUnavailableReason = "The native process epoch changed; using durable recovery state.";
            }
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException)
        {
            nativeUnavailableReason = error.Message;
        }

        if (receipt is null && record.ReceiptJson is not null)
        {
            receipt = PcbExportRecoveryStore.ParseReceipt(record);
            outputVerified = PcbExportRecoveryPolicy.OutputMatches(receipt);
        }

        PcbExportRecoveryDecision decision = PcbExportRecoveryPolicy.Reconcile(record,
            nativeReceiptFound, receipt, outputExists, outputVerified, stagingExists);
        recovery.ApplyDecision(operationId, decision);
        JsonElement? receiptJson = receipt is null
            ? null
            : JsonDocument.Parse(SchematicJson.Formatter.Format(receipt)).RootElement.Clone();
        return Data(new
        {
            instanceId,
            processEpoch = session?.Epoch ?? record.ProcessEpoch,
            operationId,
            status = decision.Status,
            outcome = receipt is not null
                ? PcbExportRecoveryPolicy.Outcome(receipt, receipt.NativeDispatch)
                : decision.Status == PcbExportRecoveryPolicy.Cancelled
                    ? PcbExportRecoveryPolicy.Cancelled
                    : decision.Status == PcbExportRecoveryPolicy.Completed
                        ? PcbExportRecoveryPolicy.OutcomeCompleted
                        : PcbExportRecoveryPolicy.Unknown,
            requestFingerprintSha256 = record.RequestFingerprintSha256,
            retryAllowed = decision.RetryAllowed,
            publicationMayHaveHappened = decision.PublicationMayHaveHappened,
            outputExists = decision.OutputExists,
            stagingExists = decision.StagingExists,
            nativeReceiptFound = decision.NativeReceiptFound,
            receiptScope = nativeReceiptFound ? "native-process" : receipt is null ? null : "mcp-state",
            receipt = receiptJson,
            reason = decision.Reason,
            nativeUnavailableReason
        });
    }, operationId, null);

    [McpServerTool(Name = "kicad_pcb_export_cancel", ReadOnly = false),
     Description("Request cancellation of one exact typed PCB manufacturing export. KiCad's synchronous manufacturing protocol has no native cancellation command, so this tool always refuses cancellation after inspecting the durable operation identity and current receipt. It never reports a successful cancellation; use kicad_pcb_export_reconcile or kicad_pcb_export_receipt to recover the native outcome."),
     KiCadCapability("pcb-manufacturing", "cancellation-refusal", "exact instance, PCB document, process epoch and operation ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbManufacturingMcpProcessTests.TypedManufacturingToolPublishesJobSchemaAndRejectsMalformedIdentity")]
    public Task<CallToolResult> ExportCancel(string instanceId, NativeDocumentInput documentInput,
        string processEpoch, string operationId, CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateOperationId(operationId);
        ValidateEpoch(processEpoch);
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId is required to cancel a PCB export.");
        DocumentSpecifier document = NativeDocumentInput.ToNative(documentInput);
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_export_target", "The cancellation target must be an explicit PCB document.");

        var recovery = new PcbExportRecoveryStore(registry.StateDirectory);
        PcbExportRecoveryRecord? record = recovery.Read(operationId);
        if (record is null)
        {
            return Data(new
            {
                instanceId, processEpoch, operationId, cancellationSupported = false,
                outcome = PcbExportRecoveryPolicy.Unknown, status = "untracked",
                reason = "No durable export operation record exists; native cancellation is unavailable."
            }, true, "pcb_export_cancellation_unsupported",
                "KiCad does not expose a native cancellation command for synchronous manufacturing exports.");
        }

        string documentJson = SchematicJson.Formatter.Format(document);
        if (record.InstanceId != instanceId || record.DocumentJson != documentJson
            || record.ProcessEpoch != processEpoch)
            throw new AutomationException("export_recovery_identity_mismatch",
                "The cancellation target does not match the durable export operation identity.");

        PcbManufacturingExportReceipt? receipt = record.ReceiptJson is null
            ? null : PcbExportRecoveryStore.ParseReceipt(record);
        string outcome = receipt is not null
            ? PcbExportRecoveryPolicy.Outcome(receipt, receipt.NativeDispatch)
            : record.Status switch
            {
                PcbExportRecoveryPolicy.Cancelled => PcbExportRecoveryPolicy.Cancelled,
                PcbExportRecoveryPolicy.Completed => PcbExportRecoveryPolicy.OutcomeCompleted,
                PcbExportRecoveryPolicy.Retryable => PcbExportRecoveryPolicy.Failed,
                _ => PcbExportRecoveryPolicy.Unknown
            };
        JsonElement? receiptJson = receipt is null
            ? null : JsonDocument.Parse(SchematicJson.Formatter.Format(receipt)).RootElement.Clone();
        return Data(new
        {
            instanceId, processEpoch, operationId, cancellationSupported = false,
            cancellationRequested = false, outcome, status = record.Status,
            requestFingerprintSha256 = record.RequestFingerprintSha256,
            receipt = receiptJson,
            reason = "Native manufacturing cancellation is unsupported; the recorded outcome was left unchanged."
        }, true, "pcb_export_cancellation_unsupported",
            "KiCad does not expose a native cancellation command for synchronous manufacturing exports.");
    }, operationId, processEpoch);

    [McpServerTool(Name = "kicad_pcb_export_3d", ReadOnly = false),
     Description("Export an explicitly identified PCB as a native 3D model through KiCad's board job. requestJson is RunBoardJobExport3D protobuf JSON with an absolute output path, explicit PCB document and format such as STEP, VRML, GLB or STL; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state, native 3D export format"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Export3D(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExport3D>(instanceId, requestJson, expectedStateJson, "3d", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_svg", ReadOnly = false),
     Description("Export an explicitly identified PCB to SVG through KiCad's native board job. requestJson is RunBoardJobExportSvg protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Supports one composite SVG or one file per selected layer. Returns every native output path, byte count and SHA-256; the board is never changed."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportSvg(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportSvg>(instanceId, requestJson, expectedStateJson, "svg", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_dxf", ReadOnly = false),
     Description("Export an explicitly identified PCB to DXF through KiCad's native board job. requestJson is RunBoardJobExportDxf protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportDxf(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportDxf>(instanceId, requestJson, expectedStateJson, "dxf", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_pdf", ReadOnly = false),
     Description("Export an explicitly identified PCB to PDF through KiCad's native board job. requestJson is RunBoardJobExportPdf protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportPdf(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportPdf>(instanceId, requestJson, expectedStateJson, "pdf", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_ps", ReadOnly = false),
     Description("Export an explicitly identified PCB to PostScript through KiCad's native board job. requestJson is RunBoardJobExportPs protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportPs(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportPs>(instanceId, requestJson, expectedStateJson, "ps", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_gerbers", ReadOnly = false),
     Description("Export an explicitly identified PCB's Gerbers through KiCad's native board job. requestJson is RunBoardJobExportGerbers protobuf JSON with an absolute output directory and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns a bounded manifest with hashes for every generated file and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportGerbers(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportGerbers>(instanceId, requestJson, expectedStateJson, "gerbers", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_drill", ReadOnly = false),
     Description("Export an explicitly identified PCB's drill files through KiCad's native board job. requestJson is RunBoardJobExportDrill protobuf JSON with an absolute output path or directory and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportDrill(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportDrill>(instanceId, requestJson, expectedStateJson, "drill", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_position", ReadOnly = false),
     Description("Export an explicitly identified PCB's pick-and-place position files through KiCad's native board job. requestJson is RunBoardJobExportPosition protobuf JSON with an absolute output path or directory and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportPosition(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportPosition>(instanceId, requestJson, expectedStateJson, "position", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_gencad", ReadOnly = false),
     Description("Export an explicitly identified PCB to GenCAD through KiCad's native board job. requestJson is RunBoardJobExportGencad protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportGencad(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportGencad>(instanceId, requestJson, expectedStateJson, "gencad", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_ipc2581", ReadOnly = false),
     Description("Export an explicitly identified PCB to IPC-2581 through KiCad's native board job. requestJson is RunBoardJobExportIpc2581 protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportIpc2581(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportIpc2581>(instanceId, requestJson, expectedStateJson, "ipc2581", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_ipc_d356", ReadOnly = false),
     Description("Export an explicitly identified PCB netlist to IPC-D-356 through KiCad's native board job. requestJson is RunBoardJobExportIpcD356 protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportIpcD356(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportIpcD356>(instanceId, requestJson, expectedStateJson, "ipc-d356", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_odb", ReadOnly = false),
     Description("Export an explicitly identified PCB to ODB++ through KiCad's native board job. requestJson is RunBoardJobExportODB protobuf JSON with an absolute output path or directory and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns a bounded manifest with hashes for every generated file and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportOdb(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportODB>(instanceId, requestJson, expectedStateJson, "odb", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_stats", ReadOnly = false),
     Description("Export machine-readable or human-readable board statistics through KiCad's native board job. requestJson is RunBoardJobExportStats protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportStats(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportStats>(instanceId, requestJson, expectedStateJson, "stats", request => request.JobSettings, cancellationToken);

    private async Task<CallToolResult> Export<TRequest>(string instanceId, string requestJson,
        string expectedStateJson, string jobKind, Func<TRequest, RunJobSettings> settings,
        CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>, new()
    {
        bool nativeDispatchStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TRequest request;
            try { request = JsonParser.Default.Parse<TRequest>(requestJson); }
            catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
            { throw new AutomationException("invalid_pcb_export_request", $"{jobKind} requestJson is not valid protobuf JSON: {error.Message}"); }

            RunJobSettings job = settings(request);
            if (job is null || job.Document is null || job.Document.Type != DocumentType.DoctypePcb)
                throw new AutomationException("invalid_pcb_export_target", "The export request must contain an explicit PCB document.");
            DocumentStateTools.ValidateTarget(job.Document);
            if (string.IsNullOrWhiteSpace(job.OutputPath) || !Path.IsPathFullyQualified(job.OutputPath))
                throw new AutomationException("invalid_pcb_export_path", "The native export output path must be absolute.");
            if (job.OutputPath.Length > 4096)
                throw new AutomationException("invalid_pcb_export_path", "The native export output path is too long.");

            var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
            if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(job.Document)
                || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
                throw new AutomationException("invalid_pcb_export_state", "expectedStateJson must identify the same PCB and native process epoch.");

            var client = registry.Client(instanceId);
            var session = await client.HandshakeAsync(cancellationToken);
            if (session.Epoch != expected.ProcessEpoch)
                throw new AutomationException("pcb_instance_changed", "The native process epoch changed before export.");
            var before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = job.Document }, cancellationToken);
            if (!before.Equals(expected))
                throw new AutomationException("pcb_state_changed", "The PCB changed before export; inspect a fresh lifecycle state and retry.");

            nativeDispatchStarted = true;
            var result = await InvokeJob<TRequest>(client, request, cancellationToken);
            if (result.Status != JobStatus.JsSuccess)
                throw new AutomationException("pcb_export_failed", string.IsNullOrWhiteSpace(result.Message)
                    ? $"KiCad did not complete the {jobKind} export." : result.Message);
            if (result.OutputPath.Count == 0)
                throw new AutomationException("pcb_export_missing_output", "KiCad reported success without an output path.");

            var outputs = await BuildManifest(result.OutputPath, cancellationToken);
            var after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = job.Document }, cancellationToken);
            if (!after.Equals(expected))
                throw new AutomationException("pcb_export_state_changed", "The board changed while exporting; output evidence is discarded from this result.");

            return new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    instanceId, processEpoch = session.Epoch, job = jobKind, document = job.Document,
                    beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
                    outputs, nativeJob = true, boardMutation = false,
                    outcome = PcbExportRecoveryPolicy.OutcomeCompleted
                }),
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new
                {
                    instanceId, processEpoch = session.Epoch, job = jobKind, outputs,
                    nativeJob = true, boardMutation = false,
                    outcome = PcbExportRecoveryPolicy.OutcomeCompleted
                }) }]
            };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        {
            var structured = JsonSerializer.SerializeToElement(new
            {
                errorCode = error is AutomationException known ? known.Code : "pcb_export_failed",
                errorMessage = error.Message, job = jobKind,
                nativeDispatch = nativeDispatchStarted,
                dispatchOutcome = nativeDispatchStarted ? "indeterminate" : "unsubmitted",
                mutationSubmitted = nativeDispatchStarted,
                outcome = nativeDispatchStarted ? PcbExportRecoveryPolicy.Unknown : PcbExportRecoveryPolicy.Failed
            });
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
        }
    }

    private static Task<RunJobResponse> InvokeJob<TRequest>(NativeClient client, TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>, new() =>
        client.InvokeAsync<TRequest, RunJobResponse>(request, cancellationToken);

    private static async Task<IReadOnlyList<object>> BuildManifest(IEnumerable<string> nativePaths,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        foreach (string nativePath in nativePaths)
        {
            if (string.IsNullOrWhiteSpace(nativePath)) continue;
            string path = Path.GetFullPath(nativePath);
            if (File.Exists(path)) files.Add(path);
            else if (Directory.Exists(path))
                files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
            else throw new AutomationException("pcb_export_missing_output", $"KiCad reported an output that does not exist: {path}");
        }
        files = files.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToList();
        if (files.Count == 0 || files.Count > 1024)
            throw new AutomationException("pcb_export_invalid_output", "The export produced no files or more than 1024 files.");

        long totalBytes = 0;
        var manifest = new List<object>(files.Count);
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.Length > 128L * 1024 * 1024 || (totalBytes += info.Length) > 512L * 1024 * 1024)
                throw new AutomationException("pcb_export_oversized_output", "The export exceeds the bounded MCP artifact budget.");
            await using var stream = File.OpenRead(file);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
            manifest.Add(new { path = file, bytes = info.Length, sha256 = Convert.ToHexStringLower(hash) });
        }
        return manifest;
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

    private static CallToolResult Data(object value, bool error = false, string? errorCode = null,
        string? errorMessage = null)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (errorCode is null)
            return new() { IsError = error, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };

        using JsonDocument parsed = JsonDocument.Parse(structured.GetRawText());
        var map = parsed.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        map["errorCode"] = JsonSerializer.SerializeToElement(errorCode);
        map["errorMessage"] = JsonSerializer.SerializeToElement(errorMessage
            ?? "The native export reported an unsuccessful result; inspect the retained receipt and staged artifacts.");
        JsonElement withError = JsonSerializer.SerializeToElement(map);
        return new() { IsError = error, StructuredContent = withError,
            Content = [new TextContentBlock { Text = withError.GetRawText() }] };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> operation,
        string? operationId = null, string? processEpoch = null)
    {
        try { return await operation(); }
        catch (NngException error)
        {
            var structured = JsonSerializer.SerializeToElement(new
            {
                operationId, processEpoch,
                errorCode = error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
                errorMessage = error.Message,
                nativeDispatch = error.RequestDelivered,
                dispatchOutcome = error.RequestDelivered ? "indeterminate" : "unsubmitted",
                outcome = error.RequestDelivered ? PcbExportRecoveryPolicy.Unknown : PcbExportRecoveryPolicy.Failed
            });
            return new CallToolResult { IsError = true,
                Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
        }
        catch (OperationCanceledException)
        {
            var structured = JsonSerializer.SerializeToElement(new
            {
                operationId, processEpoch,
                errorCode = "operation_outcome_unknown",
                errorMessage = "The MCP request was cancelled before its native outcome was confirmed; read the retained export receipt with the same operationId.",
                nativeDispatch = (bool?)null,
                dispatchOutcome = "indeterminate",
                outcome = PcbExportRecoveryPolicy.Unknown,
                cancellationRequested = true
            });
            return new CallToolResult { IsError = true,
                Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException
            or ArgumentException or IOException or JsonException)
        {
            string errorCode = error is AutomationException automation ? automation.Code : "pcb_export_failed";
            var structured = JsonSerializer.SerializeToElement(new
            {
                operationId, processEpoch,
                errorCode,
                errorMessage = error.Message,
                dispatchOutcome = "unknown",
                outcome = errorCode == "operation_cancelled"
                    ? PcbExportRecoveryPolicy.Cancelled : PcbExportRecoveryPolicy.Failed
            });
            return new CallToolResult { IsError = true,
                Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
        }
    }
}
