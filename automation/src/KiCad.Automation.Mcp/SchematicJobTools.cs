using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Types;
using Kiapi.Schematic.Jobs;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class SchematicJobTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_export_svg", ReadOnly = false),
     Description("Export an explicitly identified schematic to SVG through KiCad's native schematic job handler. requestJson is RunSchematicJobExportSvg protobuf JSON with an absolute output path and explicit schematic root; expectedStateJson is the matching lifecycle observation used for admission and post-export verification. Returns bounded output paths, byte counts and SHA-256 hashes; the schematic is never mutated."),
     KiCadCapability("schematic-manufacturing", "compiled-mcp plus native-schematic-job", "explicit schematic root, lifecycle state and hashed SVG output manifest"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ExportSvg(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) =>
        ExportJob<RunSchematicJobExportSvg>(instanceId, requestJson, expectedStateJson, "svg",
            request => request.JobSettings, outputIsDirectory: true, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_export_dxf", ReadOnly = false),
     Description("Export an explicitly identified schematic to DXF through KiCad's native schematic job handler. requestJson is RunSchematicJobExportDxf protobuf JSON with an absolute output path and explicit schematic root; expectedStateJson is the matching lifecycle observation used for admission and post-export verification. Returns bounded output paths, byte counts and SHA-256 hashes; the schematic is never mutated."),
     KiCadCapability("schematic-manufacturing", "compiled-mcp plus native-schematic-job", "explicit schematic root, lifecycle state and hashed DXF output manifest"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ExportDxf(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) =>
        ExportJob<RunSchematicJobExportDxf>(instanceId, requestJson, expectedStateJson, "dxf",
            request => request.JobSettings, outputIsDirectory: true, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_export_pdf", ReadOnly = false),
     Description("Export an explicitly identified schematic to PDF through KiCad's native schematic job handler. requestJson is RunSchematicJobExportPdf protobuf JSON with an absolute output path and explicit schematic root; expectedStateJson is the matching lifecycle observation used for admission and post-export verification. Returns bounded output paths, byte counts and SHA-256 hashes; the schematic is never mutated."),
     KiCadCapability("schematic-manufacturing", "compiled-mcp plus native-schematic-job", "explicit schematic root, lifecycle state and hashed PDF output manifest"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ExportPdf(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) =>
        ExportJob<RunSchematicJobExportPdf>(instanceId, requestJson, expectedStateJson, "pdf",
            request => request.JobSettings, outputIsDirectory: false, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_export_ps", ReadOnly = false),
     Description("Export an explicitly identified schematic to PostScript through KiCad's native schematic job handler. requestJson is RunSchematicJobExportPs protobuf JSON with an absolute output path and explicit schematic root; expectedStateJson is the matching lifecycle observation used for admission and post-export verification. Returns bounded output paths, byte counts and SHA-256 hashes; the schematic is never mutated."),
     KiCadCapability("schematic-manufacturing", "compiled-mcp plus native-schematic-job", "explicit schematic root, lifecycle state and hashed PostScript output manifest"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ExportPs(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) =>
        ExportJob<RunSchematicJobExportPs>(instanceId, requestJson, expectedStateJson, "ps",
            request => request.JobSettings, outputIsDirectory: true, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_export_netlist", ReadOnly = false),
     Description("Export an explicitly identified schematic netlist through KiCad's native schematic job handler. requestJson is RunSchematicJobExportNetlist protobuf JSON with a valid SchematicNetlistFormat, absolute output path and explicit schematic root; expectedStateJson is the matching lifecycle observation used for admission and post-export verification. Returns bounded output paths, byte counts and SHA-256 hashes; the schematic is never mutated."),
     KiCadCapability("schematic-manufacturing", "compiled-mcp plus native-schematic-job", "explicit schematic root, lifecycle state and hashed netlist output manifest"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ExportNetlist(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) =>
        ExportJob<RunSchematicJobExportNetlist>(instanceId, requestJson, expectedStateJson, "netlist",
            request => request.JobSettings, outputIsDirectory: false, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_export_bom", ReadOnly = false),
     Description("Export an explicitly identified schematic bill of materials through KiCad's native schematic job handler. requestJson is RunSchematicJobExportBOM protobuf JSON with an absolute output path, explicit schematic root and optional format/field, variant, grouping and do-not-populate settings; expectedStateJson is the matching lifecycle observation used for admission and post-export verification. Returns bounded output paths, byte counts and SHA-256 hashes; the schematic is never mutated."),
     KiCadCapability("schematic-manufacturing", "compiled-mcp plus native-schematic-job", "explicit schematic root, lifecycle state and hashed BOM output manifest"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ExportBom(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) =>
        ExportJob<RunSchematicJobExportBOM>(instanceId, requestJson, expectedStateJson, "bom",
            request => request.JobSettings, outputIsDirectory: false, cancellationToken);

    private async Task<CallToolResult> ExportJob<TRequest>(string instanceId, string requestJson,
        string expectedStateJson, string jobKind, Func<TRequest, RunJobSettings?> settings,
        bool outputIsDirectory, CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>, new()
    {
        bool nativeDispatchStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TRequest request;
            try { request = JsonParser.Default.Parse<TRequest>(requestJson); }
            catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
            {
                throw new AutomationException("invalid_schematic_export_request",
                    $"{jobKind} requestJson is not valid protobuf JSON: {error.Message}");
            }

            RunJobSettings? job = settings(request);
            if (job?.Document is null || job.Document.Type != DocumentType.DoctypeSchematic
                || job.Document.SheetPath?.Path.Count != 1)
                throw new AutomationException("invalid_schematic_export_target",
                    "The export request must contain an explicit schematic root document.");
            try { DocumentStateTools.ValidateTarget(job.Document); }
            catch (AutomationException error) when (error.Code is "invalid_document_target" or "unsupported_document_type")
            { throw new AutomationException("invalid_schematic_export_target", error.Message); }
            if (string.IsNullOrWhiteSpace(job.OutputPath) || !Path.IsPathFullyQualified(job.OutputPath))
                throw new AutomationException("invalid_schematic_export_path",
                    "The schematic export output path must be absolute.");
            if (job.OutputPath.Length > 4096)
                throw new AutomationException("invalid_schematic_export_path",
                    "The schematic export output path is too long.");

            DocumentLifecycleState expected = ParseExpectedState(expectedStateJson);
            if (expected is null || expected.Scope != DocumentLifecycleScope.DlsSchematicHierarchy
                || expected.Document is null || !expected.Document.Equals(job.Document)
                || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
                throw new AutomationException("invalid_schematic_export_state",
                    "expectedStateJson must identify the same schematic root and native process epoch.");

            NativeClient client = registry.Client(instanceId);
            AutomationSession session = await client.HandshakeAsync(cancellationToken);
            if (session.Epoch != expected.ProcessEpoch)
                throw new AutomationException("schematic_instance_changed",
                    "The native process epoch changed before export.");
            DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = job.Document }, cancellationToken);
            if (!before.Equals(expected))
                throw new AutomationException("schematic_state_changed",
                    "The schematic changed before export; inspect a fresh lifecycle checkpoint.");
            if (before.NativeContentDirty)
                throw new AutomationException("schematic_export_dirty",
                    "The schematic has unsaved native changes; save before exporting.");

            nativeDispatchStarted = true;
            RunJobResponse result = await client.InvokeAsync<TRequest, RunJobResponse>(request, cancellationToken);
            if (result.Status != JobStatus.JsSuccess)
                throw new AutomationException("schematic_export_failed", string.IsNullOrWhiteSpace(result.Message)
                    ? $"KiCad did not complete the schematic {jobKind} export." : result.Message);
            IReadOnlyList<object> outputs = await BuildManifest(result.OutputPath, job.OutputPath,
                outputIsDirectory, cancellationToken);
            DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = job.Document }, cancellationToken);
            if (!after.Equals(expected))
                throw new AutomationException("schematic_export_state_changed",
                    "The schematic changed while exporting; discard this output evidence.");

            JsonElement structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, processEpoch = session.Epoch, job = jobKind, document = job.Document,
                beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
                outputs, nativeJob = true, boardMutation = false, schematicMutation = false,
                nativeDispatch = true, dispatchOutcome = "confirmed",
                outcome = "completed"
            });
            return new CallToolResult { StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or IOException or UnauthorizedAccessException or ArgumentException or InvalidProtocolBufferException
            or InvalidJsonException or InvalidOperationException)
        {
            bool transportDelivered = nativeDispatchStarted && error is NngException deliveredTransport
                && deliveredTransport.RequestDelivered;
            bool nativeDispatch = nativeDispatchStarted && (error is not NngException
                || ((NngException)error).RequestDelivered);
            string code = error is AutomationException known ? known.Code
                : error is NativeApiException native ? "native_status_" + native.Status
                : error is NngException transport ? "transport_status_" + transport.ErrorCode
                : "schematic_export_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(new
            {
                errorCode = code, errorMessage = error.Message, job = jobKind,
                nativeDispatch,
                dispatchOutcome = transportDelivered ? "indeterminate" : nativeDispatch ? "confirmed" : "unsubmitted",
                outcome = transportDelivered ? "unknown" : "failed"
            });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private static DocumentLifecycleState ParseExpectedState(string? expectedStateJson)
    {
        try
        {
            if (expectedStateJson is null)
                throw new ArgumentNullException(nameof(expectedStateJson));
            return SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
            or InvalidOperationException or ArgumentException)
        {
            throw new AutomationException("invalid_schematic_export_state",
                "expectedStateJson is not a valid schematic lifecycle state: " + error.Message);
        }
    }

    private static async Task<IReadOnlyList<object>> BuildManifest(IEnumerable<string> paths,
        string requestedOutputPath, bool outputIsDirectory, CancellationToken cancellationToken)
    {
        const int maxFiles = 1024;
        string requestedPath;
        try { requestedPath = Path.GetFullPath(requestedOutputPath); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { throw new AutomationException("invalid_schematic_export_path", "The schematic export output path is not valid: " + error.Message); }

        if (outputIsDirectory && File.Exists(requestedPath)
            || !outputIsDirectory && Directory.Exists(requestedPath))
            throw new AutomationException("invalid_schematic_export_path",
                outputIsDirectory
                    ? "The schematic export output path must be a directory."
                    : "The schematic export output path must be a file.");

        var files = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);

        void AddFile(string path)
        {
            if (known.Contains(path)) return;
            if (files.Count >= maxFiles)
                throw new AutomationException("schematic_export_invalid_output",
                    "The export produced no files or more than 1024 files.");
            known.Add(path);
            files.Add(path);
        }

        void ValidateOwned(string path)
        {
            if (outputIsDirectory)
            {
                string relative = Path.GetRelativePath(requestedPath, path);
                if (relative is "." or ".."
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || Path.IsPathFullyQualified(relative))
                    throw new AutomationException("schematic_export_invalid_output",
                        "The export reported an output outside the requested directory.");
            }
            else if (!PathEquals(path, requestedPath))
                throw new AutomationException("schematic_export_invalid_output",
                    "The export reported a file different from the requested output path.");
        }

        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path;
            try { path = Path.GetFullPath(raw); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            { throw new AutomationException("schematic_export_invalid_output", "KiCad reported an invalid output path: " + error.Message); }

            if (File.Exists(path))
            {
                ValidateOwned(path);
                AddFile(path);
            }
            else if (Directory.Exists(path))
            {
                if (!outputIsDirectory)
                    throw new AutomationException("schematic_export_invalid_output",
                        "The export reported a directory for a file output.");
                string relative = Path.GetRelativePath(requestedPath, path);
                if (relative is ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || Path.IsPathFullyQualified(relative))
                    throw new AutomationException("schematic_export_invalid_output",
                        "The export reported an output directory outside the requested directory.");
                foreach (string child in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string file = Path.GetFullPath(child);
                    ValidateOwned(file);
                    AddFile(file);
                }
            }
            else throw new AutomationException("schematic_export_missing_output", $"KiCad reported an output that does not exist: {path}");
        }
        files = files.OrderBy(path => path, StringComparer.Ordinal).ToList();
        if (files.Count == 0)
            throw new AutomationException("schematic_export_invalid_output", "The export produced no files or more than 1024 files.");
        long total = 0;
        var result = new List<object>(files.Count);
        foreach (string path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > 128L * 1024 * 1024 || (total += info.Length) > 512L * 1024 * 1024)
                throw new AutomationException("schematic_export_oversized_output", "The schematic export exceeds the bounded MCP artifact budget.");
            await using FileStream stream = File.OpenRead(path);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            result.Add(new { path, bytes = info.Length, sha256 = hash });
        }
        return result;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> operation)
    {
        try { return await operation(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or IOException or UnauthorizedAccessException or ArgumentException or InvalidProtocolBufferException
            or InvalidJsonException or InvalidOperationException)
        {
            string code = error is AutomationException known ? known.Code
                : error is NativeApiException native ? "native_status_" + native.Status
                : error is NngException transport ? "transport_status_" + transport.ErrorCode
                : "schematic_export_failed";
            var structured = JsonSerializer.SerializeToElement(new { errorCode = code,
                errorMessage = error.Message, nativeDispatch = error is NativeApiException or NngException,
                outcome = "failed" });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }
}
