using System.ComponentModel;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class PcbControlTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_design_rules_get", ReadOnly = true)]
    public Task<CallToolResult> GetDesignRules(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<GetBoardDesignRules, BoardDesignRulesResponse>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_design_rules_set")]
    public Task<CallToolResult> SetDesignRules(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<SetBoardDesignRules, BoardDesignRulesResponse>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_custom_rules_get", ReadOnly = true)]
    public Task<CallToolResult> GetCustomRules(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<GetCustomDesignRules, CustomRulesResponse>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_custom_rules_set")]
    public Task<CallToolResult> SetCustomRules(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<SetCustomDesignRules, CustomRulesResponse>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_import_netlist")]
    public Task<CallToolResult> ImportNetlist(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<ImportNetlist, ImportNetlistResponse>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_plot_settings_get", ReadOnly = true)]
    public Task<CallToolResult> GetPlotSettings(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<GetBoardPlotSettings, BoardPlotSettingsResponse>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_plot_settings_set")]
    public Task<CallToolResult> SetPlotSettings(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<SetBoardPlotSettings, Empty>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_origin_get", ReadOnly = true)]
    public Task<CallToolResult> GetOrigin(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<GetBoardOrigin, Vector2>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_origin_set")]
    public Task<CallToolResult> SetOrigin(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<SetBoardOrigin, Empty>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_embedded_files_get", ReadOnly = true)]
    public Task<CallToolResult> GetEmbeddedFiles(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<GetEmbeddedFiles, EmbeddedFiles>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_embedded_files_set")]
    public Task<CallToolResult> SetEmbeddedFiles(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<SetEmbeddedFiles, Empty>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_embedded_files_add")]
    public Task<CallToolResult> AddEmbeddedFiles(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<AddEmbeddedFiles, Empty>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_appearance_get", ReadOnly = true)]
    public Task<CallToolResult> GetAppearance(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<GetBoardEditorAppearanceSettings, BoardEditorAppearanceSettings>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_appearance_set")]
    public Task<CallToolResult> SetAppearance(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<SetBoardEditorAppearanceSettings, Empty>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_cross_probe")]
    public Task<CallToolResult> CrossProbe(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<SyncSelection, SyncSelectionResponse>(instanceId, requestJson, cancellationToken);

    private async Task<CallToolResult> Run<TRequest, TResponse>(string instanceId, string requestJson,
        CancellationToken cancellationToken)
        where TRequest : IMessage<TRequest>, new()
        where TResponse : IMessage<TResponse>, new()
    {
        try
        {
            var request = JsonParser.Default.Parse<TRequest>(requestJson);
            var response = await registry.Client(instanceId).InvokeAsync<TRequest, TResponse>(request, cancellationToken);
            string json = JsonFormatter.Default.Format(response);
            return new() { Content = [new TextContentBlock { Text = json }] };
        }
        catch (Google.Protobuf.InvalidProtocolBufferException error) { return Failure("invalid_pcb_request", error.Message); }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
        catch (NativeApiException error) { return Failure("native_status_" + error.Status, error.Message); }
    }

    private static CallToolResult Failure(string code, string message)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(new { code, message });
        return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
