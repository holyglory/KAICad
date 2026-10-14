using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using KiCad.Automation.Protocol;
using KiCad.Automation.Native;
using KiCad.Automation.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class RasterTraceTools(InstanceRegistry registry)
{
    private static readonly JsonFormatter TraceFormatter =
        new(JsonFormatter.Settings.Default.WithFormatDefaultValues(true));

    [McpServerTool(Name = "kicad_artwork_raster_trace", ReadOnly = true),
     Description("Trace one exact local PNG into a deterministic vector proposal using KiCad's existing Potrace boundary. The native peer reads and hashes the exact bytes once, checks PNG dimensions before decoding, preserves signed contour parent/hole topology, returns integer-nanometre contours and hole-free filled pieces, and never mutates a document or commits CAD. Human/generated calls preserve their optional source attribution; imagegen calls require a typed provenance envelope bound to the exact local bytes, but this validates manifest identity rather than external generator authenticity."),
     KiCadCapability("generated-artwork-cad", "compiled-mcp plus native-potrace", "typed PNG source, exact SHA-256, bounded trace settings, contour topology and filled proposal"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Trace(string instanceId, RasterTraceInput input,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        RasterTraceInput.Validate(input);
        RasterTraceProvenanceInput? provenance = null;
        if (input!.SourceKind == "imagegen" || input.Provenance is not null)
        {
            byte[] source = await File.ReadAllBytesAsync(input.SourcePath!, cancellationToken);
            string actual = Convert.ToHexStringLower(SHA256.HashData(source));
            if (!string.Equals(actual, input.ExpectedSourceSha256, StringComparison.Ordinal))
                throw new AutomationException("raster_trace_source_changed", "Source bytes do not match ExpectedSourceSha256.");
            provenance = RasterTraceProvenanceInput.Validate(input.Provenance, input.SourceKind,
                actual, (ulong)source.Length);
        }
        var client = registry.Client(instanceId);
        var request = input.ToNative();
        RasterTraceResult result = await client.InvokeAsync<RasterTraceRequest, RasterTraceResult>(request, cancellationToken);
        JsonElement trace = FormatTraceResult(result);
        return Data(new
        {
            instanceId,
            trace,
            sourceKind = input.SourceKind,
            sourceOriginDeclared = input.SourceOrigin,
            hostReceiptDeclared = input.HostReceipt,
            provenance,
            sourceProvenanceVerified = provenance is not null,
            targetProjection = "deferred",
            proposalOnly = true,
            nativeDocumentMutated = result.NativeDocumentMutated,
            cadProjectionCommitted = result.CadProjectionCommitted,
            electricalValidated = result.ElectricalValidated,
            manufacturingValidated = result.ManufacturingValidated
        });
    });

    // Raster proposals carry an explicit acceptance decision. Keep that
    // decision and the rejection code in the MCP JSON even when protobuf
    // defaults would otherwise be omitted, so callers can distinguish a
    // rejected proposal from a malformed response.
    internal static JsonElement FormatTraceResult(RasterTraceResult result) =>
        JsonSerializer.Deserialize<JsonElement>(TraceFormatter.Format(result));

    private static CallToolResult Data(object value, bool error = false)
    {
        JsonElement data = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { IsError = error, StructuredContent = data,
            Content = [new TextContentBlock { Text = data.GetRawText() }] };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or IOException or InvalidProtocolBufferException or InvalidOperationException)
        {
            return Data(new { errorCode = ErrorCode(error),
                errorMessage = error.Message, proposalOnly = true, nativeDocumentMutated = false }, true);
        }
    }

    private static string ErrorCode(Exception error)
    {
        if (error is AutomationException known) return known.Code;
        if (error is NativeApiException native)
        {
            int separator = native.Message.IndexOf(':');
            if (separator > 0)
            {
                string code = native.Message[..separator];
                if (code.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) return code;
            }
            return "native_status_" + native.Status;
        }
        return "raster_trace_failed";
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTraceInput
{
    public string? SourcePath { get; set; }
    public string? ExpectedSourceSha256 { get; set; }
    [Description("Caller-declared source kind. ImageGen requires Provenance; human/generated remain optional attribution.")]
    public string? SourceKind { get; set; }
    public RasterTraceProvenanceInput? Provenance { get; set; }
    [Description("Physical width of the decoded raster in integer nanometres; height follows the image aspect ratio.")]
    public long PhysicalWidthNm { get; set; }
    public uint Threshold { get; set; } = 128;
    public bool Invert { get; set; }
    public uint TurdSize { get; set; }
    public long CurveToleranceNm { get; set; } = 1_000;
    public uint MaxContours { get; set; } = 4096;
    public uint MaxVertices { get; set; } = 1_000_000;
    [Description("Caller-declared source origin; this is returned as attribution and is not independently verified.")]
    public string? SourceOrigin { get; set; }
    [Description("Optional caller-declared host receipt; this is returned as attribution and is not independently verified.")]
    public string? HostReceipt { get; set; }

    internal RasterTraceRequest ToNative() => new()
    {
        SourcePath = SourcePath!, ExpectedSourceSha256 = ExpectedSourceSha256!, PhysicalWidthNm = PhysicalWidthNm,
        Threshold = Threshold, Invert = Invert, TurdSize = TurdSize, CurveToleranceNm = CurveToleranceNm,
        MaxContours = MaxContours, MaxVertices = MaxVertices
    };

    internal static void Validate(RasterTraceInput? input)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.SourcePath)
            || !Path.IsPathFullyQualified(input.SourcePath) || input.SourcePath.IndexOf('\0') >= 0)
            throw new AutomationException("invalid_raster_trace_source", "SourcePath must be an absolute NUL-free path.");
        if (Path.GetExtension(input.SourcePath).Equals(".png", StringComparison.OrdinalIgnoreCase) == false)
            throw new AutomationException("unsupported_raster_format", "The bounded raster trace boundary currently accepts PNG sources only.");
        if (input.ExpectedSourceSha256 is null || input.ExpectedSourceSha256.Length != 64
            || input.ExpectedSourceSha256.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new AutomationException("invalid_raster_trace_source", "ExpectedSourceSha256 must be lowercase SHA-256.");
        if (input.SourceKind is not (null or "imagegen" or "human" or "generated"))
            throw new AutomationException("invalid_raster_trace_source", "SourceKind must be imagegen, human or generated.");
        if (input.SourceKind == "imagegen" && input.Provenance is null)
            throw new AutomationException("invalid_raster_provenance", "ImageGen raster traces require a typed provenance envelope.");
        if (input.PhysicalWidthNm <= 0 || input.CurveToleranceNm <= 0 || input.Threshold > 255
            || input.MaxContours is 0 or > 4096 || input.MaxVertices is < 3 or > 1_000_000)
            throw new AutomationException("invalid_raster_trace_settings", "Trace dimensions, threshold, tolerance and limits are invalid.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTraceProvenanceInput
{
    internal const string ExpectedManifestSchema = "kicad.ai.raster_trace.v1";

    public string? AssetId { get; set; }
    public string? SourceSha256 { get; set; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public ulong SourceBytes { get; set; }
    public string? Generator { get; set; }
    public string? SourceKind { get; set; }
    public string? ManifestSchema { get; set; }

    internal static RasterTraceProvenanceInput? Validate(RasterTraceProvenanceInput? provenance,
        string? expectedSourceKind, string actualSourceSha256, ulong actualSourceBytes)
    {
        if (expectedSourceKind == "imagegen" && provenance is null)
            throw new AutomationException("invalid_raster_provenance", "ImageGen raster traces require a typed provenance envelope.");
        if (provenance is null)
            return null;

        if (!Guid.TryParseExact(provenance.AssetId, "D", out Guid asset)
            || asset == Guid.Empty || provenance.AssetId != asset.ToString("D"))
            throw new AutomationException("invalid_raster_provenance", "Provenance AssetId must be a canonical nonempty UUID.");
        if (!IsSha256(provenance.SourceSha256)
            || !string.Equals(provenance.SourceSha256, actualSourceSha256, StringComparison.Ordinal))
            throw new AutomationException("invalid_raster_provenance", "Provenance SourceSha256 must match the exact raster bytes.");
        if (provenance.SourceBytes != actualSourceBytes)
            throw new AutomationException("invalid_raster_provenance", "Provenance SourceBytes must match the exact raster bytes.");
        if (string.IsNullOrWhiteSpace(provenance.Generator) || provenance.Generator.Length > 256
            || provenance.Generator.Contains('\0'))
            throw new AutomationException("invalid_raster_provenance", "Provenance Generator must be nonempty, bounded and NUL-free.");
        if (provenance.SourceKind is not ("imagegen" or "human" or "generated")
            || !string.Equals(provenance.SourceKind, expectedSourceKind, StringComparison.Ordinal))
            throw new AutomationException("invalid_raster_provenance", "Provenance SourceKind must match the requested source kind.");
        if (!string.Equals(provenance.ManifestSchema, ExpectedManifestSchema, StringComparison.Ordinal))
            throw new AutomationException("invalid_raster_provenance", $"Provenance ManifestSchema must be {ExpectedManifestSchema}.");
        return provenance;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
