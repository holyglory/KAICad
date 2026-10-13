using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

public sealed partial class PcbJobTools
{
    [McpServerTool(Name = "kicad_pcb_export_plan", ReadOnly = true),
     Description("Plan one typed PCB manufacturing export without dispatching it, writing files or reserving the operation ID. Accepts the same document, full lifecycle checkpoint, operationId, outputDirectory and concrete job as kicad_pcb_export. Checks the live process and board twice, reports destination/staging conflicts, inspects an existing native receipt and returns the exact normalized request fingerprint. Planning covers managed input, live identity and local paths; it does not prove native job success, output contents, model availability or manufacturing correctness. Execute with the same input using kicad_pcb_export, which rechecks admission and owns the export receipt."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-task-export", "typed job, explicit PCB document, full lifecycle revision, process epoch, operation ID, read-only destination preflight"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbManufacturingContractTestsPlanning.CompiledStdioPlanValidatesJobAndRefusesStaleStateWithoutExporting")]
    public Task<CallToolResult> ExportPlan(string instanceId, PcbManufacturingExportInput input,
        CancellationToken cancellationToken) => ExecutePlan(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunPcbManufacturingExport request = BuildManufacturingRequest(instanceId, input);
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != request.ProcessEpoch)
            throw new AutomationException("stale_process_epoch", "The lifecycle observation belongs to another native process epoch.");

        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = request.Document }, cancellationToken);
        if (!before.Equals(request.ExpectedState))
            throw new AutomationException("pcb_export_state_changed", "The PCB changed before export planning; read its current lifecycle state.");

        string fingerprint = PcbExportRecoveryPolicy.Fingerprint(request.OperationId, request.ProcessEpoch,
            SchematicJson.Formatter.Format(request.Document), request.OutputDirectory, request);
        PcbManufacturingExportReceiptResponse existing = await client.InvokeAsync<ReadPcbManufacturingExport,
            PcbManufacturingExportReceiptResponse>(new()
            {
                Document = request.Document,
                OperationId = request.OperationId,
                ProcessEpoch = request.ProcessEpoch
            }, cancellationToken);
        if (existing.Found)
            PcbManufacturingContract.ValidateReceipt(existing.Receipt, request.Document, request.ExpectedState,
                request.OperationId, request.ProcessEpoch, fingerprint);

        string staging = PcbExportRecoveryPolicy.StagingDirectory(request.OutputDirectory, request.OperationId);
        string parent = Path.GetDirectoryName(request.OutputDirectory)!;
        FileAttributes? parentAttributes = EntryAttributes(parent);
        bool outputExists = EntryAttributes(request.OutputDirectory) is not null;
        bool stagingExists = EntryAttributes(staging) is not null;
        bool parentIsDirectory = parentAttributes is { } attributes
            && (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
        List<string> blockers = [];
        if (!existing.Found)
        {
            if (outputExists) blockers.Add("output_path_exists");
            if (stagingExists) blockers.Add("staging_path_exists");
            if (!parentIsDirectory) blockers.Add("output_parent_unavailable");
        }

        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = request.Document }, cancellationToken);
        if (!after.Equals(before))
            throw new AutomationException("pcb_export_state_changed", "The PCB changed during export planning; discard the plan and inspect the current state.");

        return Data(new
        {
            instanceId, processEpoch = session.Epoch, operationId = request.OperationId,
            document = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(request.Document)),
            revision = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(before.Revision)),
            planStatus = existing.Found ? "existing_operation" : blockers.Count == 0 ? "planned" : "blocked",
            preflightPassed = blockers.Count == 0,
            job = ManufacturingJobKind(request.JobCase),
            requestFingerprintSha256 = fingerprint,
            normalizedRequest = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(request)),
            beforeState = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(before)),
            afterState = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(after)),
            destination = new { outputDirectory = request.OutputDirectory, stagingDirectory = staging,
                parentDirectory = parent, parentIsDirectory, outputExists, stagingExists },
            blockers,
            existingOperationFound = existing.Found,
            existingReceipt = existing.Found
                ? JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(existing.Receipt)) : (JsonElement?)null,
            existingOutcome = existing.Found ? PcbExportRecoveryPolicy.Outcome(existing.Receipt, existing.Receipt.NativeDispatch) : null,
            operationReserved = false, exportDispatched = false, filesWritten = false, boardMutation = false,
            nativeExecutionVerified = false, outputManifestVerified = false,
            nextTool = "kicad_pcb_export", admissionRecheckedOnExport = true
        });
    }, input?.OperationId, input?.ExpectedState?.ProcessEpoch);

    // Planning and execution must fingerprint exactly the same validated request.
    internal static RunPcbManufacturingExport BuildManufacturingRequest(string instanceId,
        PcbManufacturingExportInput input)
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
            OperationId = input.OperationId, ProcessEpoch = expected.ProcessEpoch,
            Document = document.Clone(), ExpectedState = expected,
            OutputDirectory = Path.GetFullPath(input.OutputDirectory)
        };
        if (input.Job is null)
            throw new AutomationException("invalid_pcb_export_job", "Exactly one concrete PCB manufacturing job setting is required.");
        input.Job.ApplyTo(request);
        PcbManufacturingContract.ValidateRequest(request);
        return request;
    }

    private static string ManufacturingJobKind(RunPcbManufacturingExport.JobOneofCase job) => job switch
    {
        RunPcbManufacturingExport.JobOneofCase.Export3D => "3d",
        RunPcbManufacturingExport.JobOneofCase.ExportRender => "render",
        RunPcbManufacturingExport.JobOneofCase.ExportSvg => "svg",
        RunPcbManufacturingExport.JobOneofCase.ExportDxf => "dxf",
        RunPcbManufacturingExport.JobOneofCase.ExportPdf => "pdf",
        RunPcbManufacturingExport.JobOneofCase.ExportPs => "ps",
        RunPcbManufacturingExport.JobOneofCase.ExportGerbers => "gerbers",
        RunPcbManufacturingExport.JobOneofCase.ExportDrill => "drill",
        RunPcbManufacturingExport.JobOneofCase.ExportPosition => "position",
        RunPcbManufacturingExport.JobOneofCase.ExportGencad => "gencad",
        RunPcbManufacturingExport.JobOneofCase.ExportIpc2581 => "ipc2581",
        RunPcbManufacturingExport.JobOneofCase.ExportIpcD356 => "ipc-d356",
        RunPcbManufacturingExport.JobOneofCase.ExportOdb => "odb",
        RunPcbManufacturingExport.JobOneofCase.ExportStats => "stats",
        _ => throw new AutomationException("invalid_pcb_export_job", "The manufacturing job is unknown.")
    };

    private static FileAttributes? EntryAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static async Task<CallToolResult> ExecutePlan(Func<Task<CallToolResult>> operation,
        string? operationId, string? processEpoch)
    {
        try { return await operation(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or OperationCanceledException or IOException or UnauthorizedAccessException or ArgumentException
            or InvalidProtocolBufferException or InvalidJsonException or JsonException or InvalidOperationException)
        {
            return Data(new
            {
                operationId, processEpoch,
                errorCode = error is AutomationException known ? known.Code
                    : error is OperationCanceledException ? "pcb_export_plan_cancelled"
                    : error is NativeApiException native ? "native_status_" + native.Status : "pcb_export_plan_failed",
                errorMessage = error.Message,
                exportDispatched = false, operationReserved = false, filesWritten = false,
                mutationSubmitted = false, boardMutation = false
            }, true);
        }
    }
}
