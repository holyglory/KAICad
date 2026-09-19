using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol.Diagrams;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class RecursiveEditorTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_diagram_open"),
     Description("Open a native recursive diagram editor for one exact diagram document and attached KiCad instance. The compiled companion validates XML before displaying it. The returned ready/busy/error state is authoritative; opening is not proof of rendering, saving, or native electrical realization. Existing dirty windows are retained.")]
    public Task<CallToolResult> Open(string instanceId, string repositoryRoot, string sourcePath,
        string documentId, CancellationToken cancellationToken) => Execute(async () =>
    {
        var native = registry.Client(instanceId);
        var loaded = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest
        { SchemaVersion = 1, RepositoryRoot = repositoryRoot, SourcePath = sourcePath, DocumentId = documentId }, cancellationToken);
        string executable = Environment.ProcessPath ?? throw new AutomationException("missing_companion", "The compiled companion path is unavailable.");
        string helper = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Assembly.GetEntryAssembly()!.Location : executable;
        var state = await native.InvokeAsync<OpenRecursiveDiagramEditor, RecursiveDiagramEditorState>(new()
        { SchemaVersion = 1, DocumentId = documentId, RepositoryRoot = repositoryRoot, SourcePath = loaded.Document.SourcePath,
            ExpectedSourceToken = loaded.SourceToken, HelperPath = helper }, cancellationToken);
        if (state.DocumentId != documentId || state.SourcePath != loaded.Document.SourcePath)
            throw new AutomationException("recursive_diagram_target_mismatch", "The native window belongs to another diagram.");
        return Result(instanceId, state);
    });

    [McpServerTool(Name = "kicad_diagram_state", ReadOnly = true),
     Description("Inspect the exact open recursive diagram window, its current level, requirement draft, file token and pending/error state. Does not navigate, save, discard, restore, or start an AI request.")]
    public Task<CallToolResult> Read(string instanceId, string documentId, CancellationToken cancellationToken) => Execute(async () =>
    {
        if (!Guid.TryParseExact(documentId, "D", out var id) || id == Guid.Empty || id.ToString("D") != documentId)
            throw new AutomationException("invalid_diagram_identity", "Specify the exact canonical diagram document UUID.");
        var state = await registry.Client(instanceId).InvokeAsync<ReadRecursiveDiagramEditor, RecursiveDiagramEditorState>(new() { DocumentId = documentId }, cancellationToken);
        if (state.DocumentId != documentId) throw new AutomationException("recursive_diagram_target_mismatch", "The native window belongs to another diagram.");
        return Result(instanceId, state);
    });

    private static CallToolResult Result(string instanceId, RecursiveDiagramEditorState state)
    {
        var data = JsonSerializer.SerializeToElement(new { instanceId, state = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(state)) });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or IOException or UnauthorizedAccessException)
        {
            string code = error is AutomationException a ? a.Code : error is NativeApiException n ? "native_status_" + n.Status : "diagram_file_error";
            var data = JsonSerializer.SerializeToElement(new { code, message = error.Message });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }
}
