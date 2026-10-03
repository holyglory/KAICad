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
    [McpServerTool(Name = "kicad_pcb_export_3d", ReadOnly = true),
     Description("Export an explicitly identified PCB as a native 3D model through KiCad's board job. requestJson is RunBoardJobExport3D protobuf JSON with an absolute output path, explicit PCB document and format such as STEP, VRML, GLB or STL; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state, native 3D export format"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Export3D(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExport3D>(instanceId, requestJson, expectedStateJson, "3d", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_svg", ReadOnly = true),
     Description("Export an explicitly identified PCB to SVG through KiCad's native board job. requestJson is RunBoardJobExportSvg protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Supports one composite SVG or one file per selected layer. Returns every native output path, byte count and SHA-256; the board is never changed."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportSvg(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportSvg>(instanceId, requestJson, expectedStateJson, "svg", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_dxf", ReadOnly = true),
     Description("Export an explicitly identified PCB to DXF through KiCad's native board job. requestJson is RunBoardJobExportDxf protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportDxf(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportDxf>(instanceId, requestJson, expectedStateJson, "dxf", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_pdf", ReadOnly = true),
     Description("Export an explicitly identified PCB to PDF through KiCad's native board job. requestJson is RunBoardJobExportPdf protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportPdf(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportPdf>(instanceId, requestJson, expectedStateJson, "pdf", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_ps", ReadOnly = true),
     Description("Export an explicitly identified PCB to PostScript through KiCad's native board job. requestJson is RunBoardJobExportPs protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportPs(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportPs>(instanceId, requestJson, expectedStateJson, "ps", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_gerbers", ReadOnly = true),
     Description("Export an explicitly identified PCB's Gerbers through KiCad's native board job. requestJson is RunBoardJobExportGerbers protobuf JSON with an absolute output directory and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns a bounded manifest with hashes for every generated file and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportGerbers(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportGerbers>(instanceId, requestJson, expectedStateJson, "gerbers", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_drill", ReadOnly = true),
     Description("Export an explicitly identified PCB's drill files through KiCad's native board job. requestJson is RunBoardJobExportDrill protobuf JSON with an absolute output path or directory and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportDrill(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportDrill>(instanceId, requestJson, expectedStateJson, "drill", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_position", ReadOnly = true),
     Description("Export an explicitly identified PCB's pick-and-place position files through KiCad's native board job. requestJson is RunBoardJobExportPosition protobuf JSON with an absolute output path or directory and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportPosition(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportPosition>(instanceId, requestJson, expectedStateJson, "position", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_gencad", ReadOnly = true),
     Description("Export an explicitly identified PCB to GenCAD through KiCad's native board job. requestJson is RunBoardJobExportGencad protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportGencad(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportGencad>(instanceId, requestJson, expectedStateJson, "gencad", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_ipc2581", ReadOnly = true),
     Description("Export an explicitly identified PCB to IPC-2581 through KiCad's native board job. requestJson is RunBoardJobExportIpc2581 protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportIpc2581(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportIpc2581>(instanceId, requestJson, expectedStateJson, "ipc2581", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_ipc_d356", ReadOnly = true),
     Description("Export an explicitly identified PCB netlist to IPC-D-356 through KiCad's native board job. requestJson is RunBoardJobExportIpcD356 protobuf JSON with an absolute output path and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns bounded output hashes and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportIpcD356(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportIpcD356>(instanceId, requestJson, expectedStateJson, "ipc-d356", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_odb", ReadOnly = true),
     Description("Export an explicitly identified PCB to ODB++ through KiCad's native board job. requestJson is RunBoardJobExportODB protobuf JSON with an absolute output path or directory and explicit PCB document; expectedStateJson is the lifecycle observation used for admission and post-export verification. Returns a bounded manifest with hashes for every generated file and never changes the board."),
     KiCadCapability("pcb-manufacturing", "compiled-mcp plus native-board-job", "explicit PCB document, process epoch, lifecycle state"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ExportOdb(string instanceId, string requestJson, string expectedStateJson, CancellationToken cancellationToken) =>
        Export<RunBoardJobExportODB>(instanceId, requestJson, expectedStateJson, "odb", request => request.JobSettings, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_stats", ReadOnly = true),
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
                    outputs, nativeJob = true, boardMutation = false
                }),
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new
                {
                    instanceId, processEpoch = session.Epoch, job = jobKind, outputs,
                    nativeJob = true, boardMutation = false
                }) }]
            };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        {
            var structured = JsonSerializer.SerializeToElement(new
            {
                errorCode = error is AutomationException known ? known.Code : "pcb_export_failed",
                errorMessage = error.Message, job = jobKind, mutationSubmitted = false
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
}
