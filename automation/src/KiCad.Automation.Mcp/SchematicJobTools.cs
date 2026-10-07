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
        CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunSchematicJobExportSvg request;
        try { request = JsonParser.Default.Parse<RunSchematicJobExportSvg>(requestJson); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException("invalid_schematic_export_request", "requestJson is not valid RunSchematicJobExportSvg protobuf JSON: " + error.Message); }

        RunJobSettings settings = request.JobSettings;
        if (settings?.Document is null || settings.Document.Type != DocumentType.DoctypeSchematic
            || settings.Document.SheetPath?.Path.Count != 1)
            throw new AutomationException("invalid_schematic_export_target", "The export request must contain an explicit schematic root document.");
        DocumentStateTools.ValidateTarget(settings.Document);
        if (string.IsNullOrWhiteSpace(settings.OutputPath) || !Path.IsPathFullyQualified(settings.OutputPath))
            throw new AutomationException("invalid_schematic_export_path", "The schematic export output path must be absolute.");

        DocumentLifecycleState expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        if (expected.Scope != DocumentLifecycleScope.DlsSchematicHierarchy
            || !expected.Document.Equals(settings.Document) || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_schematic_export_state", "expectedStateJson must identify the same schematic root and native process epoch.");

        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("schematic_instance_changed", "The native process epoch changed before export.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = settings.Document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("schematic_state_changed", "The schematic changed before export; inspect a fresh lifecycle checkpoint.");

        RunJobResponse result = await client.InvokeAsync<RunSchematicJobExportSvg, RunJobResponse>(request, cancellationToken);
        if (result.Status != JobStatus.JsSuccess)
            throw new AutomationException("schematic_export_failed", string.IsNullOrWhiteSpace(result.Message)
                ? "KiCad did not complete the schematic SVG export." : result.Message);
        IReadOnlyList<object> outputs = await BuildManifest(result.OutputPath, cancellationToken);
        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = settings.Document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("schematic_export_state_changed", "The schematic changed while exporting; discard this output evidence.");

        var structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = session.Epoch, document = settings.Document,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            outputs, nativeJob = true, boardMutation = false, schematicMutation = false,
            outcome = "completed"
        });
        return new CallToolResult { StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    });

    private static async Task<IReadOnlyList<object>> BuildManifest(IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path = Path.GetFullPath(raw);
            if (File.Exists(path)) files.Add(path);
            else if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
            else throw new AutomationException("schematic_export_missing_output", $"KiCad reported an output that does not exist: {path}");
        }
        files = files.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToList();
        if (files.Count == 0 || files.Count > 1024)
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
