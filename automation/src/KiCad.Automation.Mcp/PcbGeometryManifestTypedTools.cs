using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Typed admission boundary for the existing PCB geometry manifest.  The
/// legacy tool still accepts protobuf JSON strings; this wrapper binds the
/// native PCB document, its complete lifecycle observation and the layer or
/// composite geometry selector in one model-visible request.
/// </summary>
public sealed class PcbGeometryManifestTypedTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_render_geometry_typed", ReadOnly = true),
     Description("Read a deterministic PCB geometry manifest from one typed input. input carries an explicit PCB document, complete lifecycle observation, nanometre viewport, output size and optional layer/object-type selectors. Multiple layers form one composite inspection. The legacy kicad_pcb_render_geometry JSON tool remains available; this boundary rejects incomplete identity or lifecycle data before native contact."),
     KiCadCapability("pcb-rendering", "compiled-mcp plus-native-board-api", "typed PCB document, complete lifecycle observation, per-layer or composite selector and native bounding-box projection"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbRenderTypedMcpProcessTests.TypedGeometrySchemaAndIncompleteInputRefusal", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ReadTyped(string instanceId, PcbRenderGeometryTypedInput input,
        CancellationToken cancellationToken)
    {
        return Execute(async () =>
        {
            if (input is null)
                throw Invalid("input is required.");

            (string document, string expectedState, string request) = input.ToPayload();
            CallToolResult result = await new PcbGeometryManifestTools(registry)
                .Read(instanceId, document, expectedState, request, cancellationToken);
            if (result.StructuredContent is JsonElement structured)
            {
                using JsonDocument parsed = JsonDocument.Parse(structured.GetRawText());
                Dictionary<string, JsonElement> fields = parsed.RootElement.EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
                fields["typedInput"] = JsonSerializer.SerializeToElement(true);
                JsonElement typed = JsonSerializer.SerializeToElement(fields);
                result.StructuredContent = typed;
                result.Content = [new TextContentBlock { Text = typed.GetRawText() }];
            }
            return result;
        });
    }

    private static AutomationException Invalid(string message)
        => new("invalid_pcb_render_geometry_typed_input", message);

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or JsonException)
        {
            JsonElement structured = JsonSerializer.SerializeToElement(new
            { errorCode = error is AutomationException known ? known.Code : "invalid_pcb_render_geometry_typed_input", errorMessage = error.Message });
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
public sealed class PcbRenderGeometryTypedInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private DocumentLifecycleInput? expectedState;
    private PcbRenderGeometryRequestInput? request;

    [Description("The exact PCB document being inspected.")]
    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }

    [Description("The complete lifecycle observation captured immediately before inspection.")]
    public DocumentLifecycleInput? ExpectedState { get => expectedState; set => expectedState = BindOnce(value); }

    [Description("Viewport, pixel dimensions and optional layer/object-type selectors. An empty layer list means the composite board view.")]
    public PcbRenderGeometryRequestInput? Request { get => request; set => request = BindOnce(value); }

    internal (string Document, string ExpectedState, string Request) ToPayload()
    {
        if (Document is null || ExpectedState is null || Request is null)
            throw Invalid("document, expectedState and request are required.");

        DocumentSpecifier document;
        DocumentLifecycleState state;
        try
        {
            document = NativeDocumentInput.ToNative(Document);
            state = DocumentLifecycleInput.ToNative(ExpectedState);
        }
        catch (AutomationException error)
        {
            throw Invalid(error.Message);
        }

        if (document.Type != DocumentType.DoctypePcb)
            throw Invalid("document must identify a PCB.");
        if (state.Scope != DocumentLifecycleScope.DlsPcb || !state.Document.Equals(document))
            throw Invalid("expectedState must be a complete observation of the same PCB.");

        Request.Validate();
        return (SchematicJson.Formatter.Format(document), SchematicJson.Formatter.Format(state),
            Request.ToRequestJson());
    }

    private static AutomationException Invalid(string message)
        => new("invalid_pcb_render_geometry_typed_input", message);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbRenderGeometryRequestInput : NativeDocumentInputObject
{
    private PcbRenderViewportInput? viewport;
    private int width = 1024;
    private int height = 1024;
    private List<string> layers = [];
    private List<string> objectTypes = [];
    private bool includeGeometry = true;
    private int maxObjects = 8192;

    public PcbRenderViewportInput? Viewport { get => viewport; set => viewport = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Width { get => width; set => width = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Height { get => height; set => height = BindOnce(value); }

    [Description("Layer names such as F.Cu, B.Cu or BL_F_Cu. An empty list requests a composite board selection.")]
    public List<string> Layers { get => layers; set => layers = BindOnce(value ?? throw new JsonException("layers cannot be null.")); }

    public List<string> ObjectTypes { get => objectTypes; set => objectTypes = BindOnce(value ?? throw new JsonException("objectTypes cannot be null.")); }

    public bool IncludeGeometry { get => includeGeometry; set => includeGeometry = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int MaxObjects { get => maxObjects; set => maxObjects = BindOnce(value); }

    internal void Validate()
    {
        if (Viewport is null || Viewport.MinXNm >= Viewport.MaxXNm || Viewport.MinYNm >= Viewport.MaxYNm)
            throw Invalid("request.viewport must contain positive min/max nanometre extents.");
        if (Width is < 1 or > 8192 || Height is < 1 or > 8192)
            throw Invalid("request.width and request.height must be between 1 and 8192 pixels.");
        if (Layers is null || ObjectTypes is null || Layers.Count > 128 || Layers.Any(string.IsNullOrWhiteSpace)
            || ObjectTypes.Count > 7 || ObjectTypes.Any(string.IsNullOrWhiteSpace))
            throw Invalid("request.layers and request.objectTypes must contain bounded nonempty values.");
        if (MaxObjects is < 1 or > 8192)
            throw Invalid("request.maxObjects must be between 1 and 8192.");
        foreach (string layer in Layers)
            _ = PcbGeometryManifestTools.ParseLayer(layer);
        string[] knownTypes = ["track", "arc", "via", "pad", "footprint", "shape", "zone"];
        if (ObjectTypes.Any(type => !knownTypes.Contains(type, StringComparer.OrdinalIgnoreCase)))
            throw Invalid("request.objectTypes must use track, arc, via, pad, footprint, shape or zone.");
    }

    internal string ToRequestJson() => JsonSerializer.Serialize(new
    {
        viewport = Viewport,
        width = Width,
        height = Height,
        layers = Layers,
        objectTypes = ObjectTypes,
        includeGeometry = IncludeGeometry,
        maxObjects = MaxObjects
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static AutomationException Invalid(string message)
        => new("invalid_pcb_render_geometry_typed_input", message);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbRenderViewportInput : NativeDocumentInputObject
{
    private long minXNm;
    private long minYNm;
    private long maxXNm;
    private long maxYNm;

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long MinXNm { get => minXNm; set => minXNm = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long MinYNm { get => minYNm; set => minYNm = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long MaxXNm { get => maxXNm; set => maxXNm = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long MaxYNm { get => maxYNm; set => maxYNm = BindOnce(value); }
}
