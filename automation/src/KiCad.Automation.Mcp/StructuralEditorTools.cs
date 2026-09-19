using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol.Structural;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class StructuralEditorTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_structure_open"),
     Description("Open a native structural-diagram editor for a complete engineering design XML file inside an explicit repository root and attached KiCad instance. Loads and validates the real model and declared knowledge libraries before opening; does not replace an existing dirty window. XML remains behind the native canvas and inspector. This preliminary editor does not infer electrical realization or certify full automatic structural synchronization.")]
    public Task<CallToolResult> Open(string instanceId, string repositoryRoot, string sourcePath,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        var client = registry.Client(instanceId);
        var document = await StructuralEditorFiles.ExecuteAsync(new StructuralFileRequest
            { SchemaVersion = 1, RepositoryRoot = repositoryRoot, SourcePath = sourcePath }, cancellationToken);
        string process = Environment.ProcessPath ?? throw new AutomationException("missing_companion", "The compiled companion path is unavailable.");
        string helper = Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Assembly.GetEntryAssembly()!.Location : process;
        var result = await client.InvokeAsync<OpenStructuralEditor, StructuralEditorState>(new()
        { SchemaVersion = 1, RepositoryRoot = repositoryRoot, HelperPath = helper, Document = document }, cancellationToken);
        if (result.Document?.DocumentId != document.DocumentId || result.Document.SourcePath != document.SourcePath)
            throw new AutomationException("structural_target_mismatch", "The native structural window belongs to another document.");
        return Result(instanceId, result);
    });

    [McpServerTool(Name = "kicad_structure_state", ReadOnly = true),
     Description("Read the explicitly identified native structural window, including its current draft, dirty state and save-in-progress state. A dirty draft is not proof that XML was saved. Does not navigate, save or change either view.")]
    public Task<CallToolResult> Read(string instanceId, string documentId, CancellationToken cancellationToken) => Execute(async () =>
    {
        if (!Guid.TryParseExact(documentId, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_structural_target", "Specify the exact structural document UUID.");
        var result = await registry.Client(instanceId).InvokeAsync<ReadStructuralEditor, StructuralEditorState>(
            new() { DocumentId = documentId }, cancellationToken);
        if (result.Document?.DocumentId != documentId)
            throw new AutomationException("structural_target_mismatch", "The native response belongs to another structural document.");
        return Result(instanceId, result);
    });

    private static CallToolResult Result(string instanceId, StructuralEditorState state)
    {
        var data = JsonSerializer.SerializeToElement(new { instanceId,
            state = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(state)) });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> operation)
    {
        try { return await operation(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or IOException or UnauthorizedAccessException)
        {
            string code = error is AutomationException a ? a.Code : error is NativeApiException n ? "native_status_" + n.Status : "structural_file_error";
            var data = JsonSerializer.SerializeToElement(new { code, message = error.Message });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }
}
