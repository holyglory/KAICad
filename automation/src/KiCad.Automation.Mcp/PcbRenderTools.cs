using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Board.Jobs;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class PcbRenderTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_render_3d", ReadOnly = true),
     Description("Render one exact native PCB view through KiCad's existing 3D renderer and return the PNG alongside the matching lifecycle state. requestJson is RunBoardJobExportRender protobuf JSON with an explicit PCB document; the MCP service owns the temporary output path. The operation is read-only, rejects unsupported dimensions/formats and discards the image if the board changes during rendering. Invoke it repeatedly for top/bottom/side or zoomed views; this is observation, not routing or RF correctness.")]
    public Task<CallToolResult> Render3D(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = JsonParser.Default.Parse<RunBoardJobExportRender>(requestJson);
        if (request.JobSettings is null)
            throw new AutomationException("invalid_render_target", "The render request must contain job settings and an explicit PCB document.");
        var document = request.JobSettings.Document;
        DocumentStateTools.ValidateTarget(document);
        if (document is null || document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_render_target", "The render target must be an explicit PCB document.");
        if (request.Format is not RenderFormat.RfUnknown and not RenderFormat.RfPng)
            throw new AutomationException("unsupported_render_format", "MCP image observations currently require PNG output.");
        if (request.Width is < 64 or > 2048 || request.Height is < 64 or > 2048
            || (long)request.Width * request.Height > 8_388_608)
            throw new AutomationException("invalid_render_dimensions", "Render dimensions must be 64..2048 pixels with at most 8,388,608 pixels.");

        var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_render_state", "The expected observation must identify the same PCB and native process epoch.");
        var client = registry.Client(instanceId);
        var session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed before rendering.");
        var before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed before rendering; discard the stale observation and inspect again.");

        string temporaryDirectory = Directory.CreateTempSubdirectory("kicad-pcb-render-").FullName;
        string outputPath = Path.Combine(temporaryDirectory, "view.png");
        try
        {
            request.Format = RenderFormat.RfPng;
            request.Quality = request.Quality is RenderQuality.RqUnknown ? RenderQuality.RqBasic : request.Quality;
            request.BackgroundStyle = request.BackgroundStyle is RenderBackgroundStyle.RbsUnknown
                ? RenderBackgroundStyle.RbsOpaque : request.BackgroundStyle;
            request.Side = request.Side is RenderSide.RsUnknown ? RenderSide.RsTop : request.Side;
            request.JobSettings.OutputPath = outputPath;
            var result = await client.InvokeAsync<RunBoardJobExportRender, RunJobResponse>(request, cancellationToken);
            if (result.Status != JobStatus.JsSuccess)
                throw new AutomationException("pcb_render_failed", string.IsNullOrWhiteSpace(result.Message)
                    ? "KiCad did not produce a successful PCB render." : result.Message);
            if (!File.Exists(outputPath))
                throw new AutomationException("pcb_render_missing_output", "KiCad reported a successful render but no PNG was produced.");
            var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            if (bytes.Length == 0 || bytes.Length > 32 * 1024 * 1024)
                throw new AutomationException("pcb_render_invalid_output", "The native renderer returned an empty or oversized PNG.");
            var after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
            if (!after.Equals(expected))
                throw new AutomationException("pcb_render_state_changed", "The board changed while rendering; the image and state are discarded rather than paired incorrectly.");
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, processEpoch = session.Epoch, document,
                beforeState = SchematicJson.Formatter.Format(before),
                afterState = SchematicJson.Formatter.Format(after),
                view = new { side = request.Side.ToString(), width = request.Width, height = request.Height,
                    perspective = request.Perspective, zoom = request.Zoom },
                image = new { mimeType = "image/png", width = request.Width, height = request.Height,
                    bytes = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) },
                nativeRender = true, boardMutation = false, electricalValidation = false
            });
            return new CallToolResult
            {
                Content = [ImageContentBlock.FromBytes(bytes, "image/png"), new TextContentBlock { Text = structured.GetRawText() }],
                StructuredContent = structured
            };
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, true); } catch { /* Retain the native result; this is disposable scratch. */ }
        }
    });

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidProtocolBufferException or InvalidJsonException)
        {
            var structured = JsonSerializer.SerializeToElement(new
            {
                errorCode = error is AutomationException known ? known.Code : "pcb_render_failed",
                errorMessage = error.Message
            });
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
        }
    }
}
