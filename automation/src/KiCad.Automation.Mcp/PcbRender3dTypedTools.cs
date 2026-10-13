using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Kiapi.Board.Jobs;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Typed admission boundary for the lifecycle-bound native 3D renderer.
/// The existing PcbRenderTools handler remains the render and metadata
/// receipt authority; this adapter supplies a writable document, lifecycle
/// checkpoint and reviewed render-settings DTO without exposing output paths.
/// </summary>
[McpServerToolType]
public sealed class PcbRender3dTypedTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_render_3d_geometry_typed", ReadOnly = true),
     Description("Read lifecycle-bound native 3D geometry metadata from one typed PCB input. input carries an explicit PCB document, complete lifecycle observation and camera/render settings. The service owns the temporary PNG path, invokes the same native renderer as kicad_pcb_render_3d_geometry and returns the existing conservative footprint-model metadata with its explicit incomplete coverage flags. No board mutation or exact raster/occlusion claim is made."),
     KiCadCapability("pcb-observation", "compiled-mcp plus-native-board-renderer", "typed PCB document, complete lifecycle checkpoint and 3D render settings"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbRender3dTypedMcpProcessTests.TypedRender3dSchemaAndIncompleteInputRefusalAreVisibleOverStdio")]
    public Task<CallToolResult> ReadGeometry(string instanceId, PcbRender3dTypedInput input,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        if (input is null)
            throw Invalid("input is required.");

        (string document, string expectedState, string request) = input.ToPayload();
        CallToolResult result = await new PcbRenderTools(registry)
            .Read3DGeometry(instanceId, request, expectedState, cancellationToken);
        return MarkTyped(result);
    });

    private static CallToolResult MarkTyped(CallToolResult result)
    {
        if (result.StructuredContent is not JsonElement structured
            || structured.ValueKind != JsonValueKind.Object)
            return result;

        Dictionary<string, JsonElement> fields = structured.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        fields["typedInput"] = JsonSerializer.SerializeToElement(true);
        fields["typed3dInspection"] = JsonSerializer.SerializeToElement(true);
        JsonElement typed = JsonSerializer.SerializeToElement(fields);
        return new CallToolResult
        {
            IsError = result.IsError,
            StructuredContent = typed,
            Content = [new TextContentBlock { Text = typed.GetRawText() }]
        };
    }

    private static AutomationException Invalid(string message)
        => new("invalid_pcb_render_3d_typed_input", message);

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or JsonException
            or InvalidProtocolBufferException or InvalidJsonException or ArgumentException)
        {
            JsonElement structured = JsonSerializer.SerializeToElement(new
            {
                errorCode = error is AutomationException known
                    ? known.Code : "invalid_pcb_render_3d_typed_input",
                errorMessage = error.Message
            });
            return new CallToolResult
            {
                IsError = true,
                StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }]
            };
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbRender3dTypedInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private DocumentLifecycleInput? expectedState;
    private RunBoardJobExportRenderInput? request;

    [Description("The exact PCB document being inspected.")]
    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }

    [Description("The complete lifecycle observation captured immediately before the 3D inspection.")]
    public DocumentLifecycleInput? ExpectedState { get => expectedState; set => expectedState = BindOnce(value); }

    [Description("The reviewed 3D render settings. The service supplies the temporary output path and forces PNG metadata capture.")]
    public RunBoardJobExportRenderInput? Request { get => request; set => request = BindOnce(value); }

    internal (string Document, string ExpectedState, string Request) ToPayload()
    {
        if (Document is null || ExpectedState is null || Request is null)
            throw Invalid("document, expectedState and request are required.");

        DocumentSpecifier document;
        DocumentLifecycleState state;
        RunBoardJobExportRender render;
        try
        {
            document = NativeDocumentInput.ToNative(Document);
            state = DocumentLifecycleInput.ToNative(ExpectedState);
            render = Request.ToNative();
        }
        catch (AutomationException error)
        {
            throw Invalid(error.Message);
        }

        if (document.Type != DocumentType.DoctypePcb)
            throw Invalid("document must identify a PCB.");
        if (state.Scope != DocumentLifecycleScope.DlsPcb || !state.Document.Equals(document)
            || string.IsNullOrWhiteSpace(state.ProcessEpoch))
            throw Invalid("expectedState must be a complete observation of the same PCB and native process.");
        if (render.Format is not RenderFormat.RfUnknown and not RenderFormat.RfPng)
            throw Invalid("request.format must be unknown or PNG; the typed observation does not return JPEG output.");
        if (render.Width is < 64 or > 2048 || render.Height is < 64 or > 2048
            || (long)render.Width * render.Height > 8_388_608)
            throw Invalid("request.width and request.height must be 64..2048 pixels with at most 8,388,608 pixels.");
        if (!double.IsFinite(render.Zoom) || render.Zoom <= 0)
            throw Invalid("request.zoom must be a finite positive number.");

        render.JobSettings = new() { Document = document.Clone() };
        return (SchematicJson.Formatter.Format(document), SchematicJson.Formatter.Format(state),
            SchematicJson.Formatter.Format(render));
    }

    private static AutomationException Invalid(string message)
        => new("invalid_pcb_render_3d_typed_input", message);
}
