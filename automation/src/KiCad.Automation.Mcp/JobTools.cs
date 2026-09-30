using System.ComponentModel;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Kiapi.Board.Jobs;
using Kiapi.Common.Types;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class JobTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_export_svg"),
     Description("Run KiCad's native PCB SVG plot job for an explicit board. requestJson is RunBoardJobExportSvg JSON with an absolute output path and optional plot/layer settings; the response reports native output paths and status.")]
    public Task<CallToolResult> ExportSvg(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<RunBoardJobExportSvg>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_render_3d"),
     Description("Run KiCad's native PCB raytraced render job. requestJson is RunBoardJobExportRender JSON and can select camera, layers, dimensions and background; output is the native rendered image path.")]
    public Task<CallToolResult> Render3d(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<RunBoardJobExportRender>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_3d"),
     Description("Run KiCad's native PCB 3D export job. requestJson is RunBoardJobExport3D JSON and supports board, component, track, pad, zone and inner-copper export controls.")]
    public Task<CallToolResult> Export3d(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run<RunBoardJobExport3D>(instanceId, requestJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_export_manufacturing"),
     Description("Run a native PCB manufacturing export job selected by kind: gerbers, drill, position, dxf, pdf, ps, gencad, ipc2581, ipc356, odb or stats. requestJson must match the selected KiCad job protobuf and includes its RunJobSettings output path.")]
    public Task<CallToolResult> ExportManufacturing(string instanceId, string kind, string requestJson,
        CancellationToken cancellationToken) => kind switch
        {
            "dxf" => Run<RunBoardJobExportDxf>(instanceId, requestJson, cancellationToken),
            "pdf" => Run<RunBoardJobExportPdf>(instanceId, requestJson, cancellationToken),
            "ps" => Run<RunBoardJobExportPs>(instanceId, requestJson, cancellationToken),
            "gerbers" => Run<RunBoardJobExportGerbers>(instanceId, requestJson, cancellationToken),
            "drill" => Run<RunBoardJobExportDrill>(instanceId, requestJson, cancellationToken),
            "position" => Run<RunBoardJobExportPosition>(instanceId, requestJson, cancellationToken),
            "gencad" => Run<RunBoardJobExportGencad>(instanceId, requestJson, cancellationToken),
            "ipc2581" => Run<RunBoardJobExportIpc2581>(instanceId, requestJson, cancellationToken),
            "ipc356" => Run<RunBoardJobExportIpcD356>(instanceId, requestJson, cancellationToken),
            "odb" => Run<RunBoardJobExportODB>(instanceId, requestJson, cancellationToken),
            "stats" => Run<RunBoardJobExportStats>(instanceId, requestJson, cancellationToken),
            _ => Task.FromResult(Failure("invalid_job_kind", "Choose dxf, pdf, ps, gerbers, drill, position, gencad, ipc2581, ipc356, odb or stats."))
        };

    private async Task<CallToolResult> Run<T>(string instanceId, string requestJson, CancellationToken cancellationToken)
        where T : IMessage<T>, new()
    {
        try
        {
            T request = JsonParser.Default.Parse<T>(requestJson);
            var response = await registry.Client(instanceId).InvokeAsync<T, RunJobResponse>(request, cancellationToken);
            string json = JsonFormatter.Default.Format(response);
            return new() { Content = [new TextContentBlock { Text = json }] };
        }
        catch (Google.Protobuf.InvalidProtocolBufferException error) { return Failure("invalid_job_request", error.Message); }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
        catch (NativeApiException error) { return Failure("native_status_" + error.Status, error.Message); }
    }

    private static CallToolResult Failure(string code, string message)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(new { code, message });
        return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
