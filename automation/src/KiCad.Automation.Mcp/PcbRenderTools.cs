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
     Description("Render one exact native PCB view through KiCad's existing 3D renderer and return the PNG alongside the matching lifecycle state. requestJson is RunBoardJobExportRender protobuf JSON with an explicit PCB document; the MCP service owns the temporary output path. The operation is read-only, rejects unsupported dimensions/formats and discards the image if the board changes during rendering. Invoke it repeatedly for top/bottom/side or zoomed views; this is observation, not routing or RF correctness."),
     KiCadCapability("pcb-observation", "compiled-mcp plus native-board-job-renderer", "board lifecycle state, native image snapshot"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
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
            var requestForHash = request.Clone();
            requestForHash.JobSettings.OutputPath = string.Empty;
            string requestSha256 = Convert.ToHexStringLower(SHA256.HashData(requestForHash.ToByteArray()));
            PcbRenderInspectionResponse? inspection = null;
            var limitations = new List<string>();

            // The dedicated request is advertised by the native handshake. An
            // older peer is still allowed to produce the compatible PNG, but
            // its renderer metadata is explicitly unavailable.
            if (client.LastHandshakeHandles(PcbRenderInspectionRequest.Descriptor.FullName))
            {
                inspection = await client.InvokeAsync<PcbRenderInspectionRequest, PcbRenderInspectionResponse>(
                    new() { RenderRequest = request, ExpectedState = expected }, cancellationToken);
            }
            else
            {
                limitations.Add("native peer does not implement PcbRenderInspectionRequest; renderer metadata is unavailable");
            }

            var result = inspection?.Job ?? await client.InvokeAsync<RunBoardJobExportRender, RunJobResponse>(request, cancellationToken);
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

            JsonElement metadata = inspection is null
                ? JsonSerializer.SerializeToElement(new { available = false })
                : JsonDocument.Parse(JsonFormatter.Default.Format(inspection.Metadata)).RootElement.Clone();
            PcbRenderMetadataValidation metadataValidation = inspection is null
                ? PcbRenderMetadataValidation.Unavailable
                : PcbRenderMetadataContract.Validate(inspection.Metadata, request, expected.StateSha256, requestSha256);
            if (inspection is not null)
                limitations.AddRange(inspection.Limitations);
            limitations.AddRange(metadataValidation.Failures.Select(failure => "renderer metadata contract: " + failure));
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, processEpoch = session.Epoch, document,
                beforeState = SchematicJson.Formatter.Format(before),
                afterState = SchematicJson.Formatter.Format(after),
                view = new { side = request.Side.ToString(), width = request.Width, height = request.Height,
                    perspective = request.Perspective, zoom = request.Zoom },
                image = new { mimeType = "image/png", width = request.Width, height = request.Height,
                    bytes = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) },
                requestSha256,
                metadataAvailable = inspection is not null,
                metadataValidated = metadataValidation.IsValid,
                metadataComplete = inspection?.MetadataComplete == true && metadataValidation.IsValid,
                metadata,
                limitations,
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

internal readonly record struct PcbRenderMetadataValidation(bool IsValid, IReadOnlyList<string> Failures)
{
    internal static PcbRenderMetadataValidation Unavailable => new(false, ["renderer metadata is unavailable"]);
}

internal static class PcbRenderMetadataContract
{
    private const string ProjectionConvention =
        "column-major; clip=projection*view*board_to_scene*[x_nm,y_nm,z_nm,1]; pixel origin top-left";

    internal static PcbRenderMetadataValidation Validate(PcbRenderMetadata metadata,
        RunBoardJobExportRender request, string expectedStateSha256, string requestSha256)
    {
        var failures = new List<string>();

        // The native ray tracer may align its work to an inset content region,
        // so the metadata describes a PNG that is smaller than the requested
        // window. It must still be a positive viewport contained by that
        // request; larger or empty dimensions cannot describe this render.
        if (metadata.WidthPixels == 0 || metadata.HeightPixels == 0
            || metadata.WidthPixels > request.Width || metadata.HeightPixels > request.Height)
            failures.Add("camera metadata dimensions must be positive and no larger than the requested image");

        if (metadata.ViewMatrixColumnMajor.Count != 16
            || metadata.ProjectionMatrixColumnMajor.Count != 16
            || metadata.BoardNanometresToSceneColumnMajor.Count != 16)
            failures.Add("camera metadata must contain exactly three 4x4 column-major matrices");

        if (!metadata.ViewMatrixColumnMajor.All(double.IsFinite)
            || !metadata.ProjectionMatrixColumnMajor.All(double.IsFinite)
            || !metadata.BoardNanometresToSceneColumnMajor.All(double.IsFinite)
            || !metadata.MatricesFinite || !metadata.MatricesInvertible
            || !Invertible(metadata.ViewMatrixColumnMajor)
            || !Invertible(metadata.ProjectionMatrixColumnMajor)
            || !Invertible(metadata.BoardNanometresToSceneColumnMajor))
            failures.Add("camera matrices must be finite and invertible");

        if (!string.Equals(metadata.ProjectionConvention, ProjectionConvention, StringComparison.Ordinal))
            failures.Add("camera projection convention is missing or unsupported");

        // Orthographic native views use a symmetric clip range around the
        // camera (for example -far..+far), so the near plane may be negative.
        if (!double.IsFinite(metadata.NearClip) || !double.IsFinite(metadata.FarClip)
            || metadata.FarClip <= metadata.NearClip)
            failures.Add("camera clip planes must be finite and ordered");

        if (metadata.ObservedCameraPosition.Count != 3 || metadata.ObservedLookAt.Count != 3
            || !metadata.ObservedCameraPosition.All(double.IsFinite)
            || !metadata.ObservedLookAt.All(double.IsFinite))
            failures.Add("camera position and look-at metadata must contain three finite values each");

        string expectedSide = request.Side switch
        {
            RenderSide.RsTop => "top",
            RenderSide.RsBottom => "bottom",
            RenderSide.RsLeft => "left",
            RenderSide.RsRight => "right",
            RenderSide.RsFront => "front",
            RenderSide.RsBack => "back",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(expectedSide) || !string.Equals(metadata.ObservedSide, expectedSide, StringComparison.Ordinal))
            failures.Add("observed camera side does not match the typed render request");
        if (metadata.ObservedPerspective != request.Perspective)
            failures.Add("observed camera perspective does not match the typed render request");
        if (!double.IsFinite(metadata.ObservedZoom) || metadata.ObservedZoom <= 0)
            failures.Add("observed camera zoom must be finite and positive");

        if (!ValidSha256(expectedStateSha256)
            || !string.Equals(metadata.SourceBoardSnapshotSha256, expectedStateSha256, StringComparison.OrdinalIgnoreCase))
            failures.Add("camera metadata is not bound to the expected board snapshot");
        if (!ValidSha256(requestSha256)
            || !string.Equals(metadata.EffectiveRenderInputSha256, requestSha256, StringComparison.OrdinalIgnoreCase))
            failures.Add("camera metadata is not bound to the effective render input");

        var modelKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (PcbRenderModelMetadata model in metadata.Models)
        {
            string key = GeometryKey(model.NativeUuid, model.ModelIndex);
            if (!CanonicalUuid(model.NativeUuid) || !modelKeys.Add(key))
                failures.Add("model metadata must identify each native model exactly once");
            if (string.IsNullOrWhiteSpace(model.RequestedIdentity) || string.IsNullOrWhiteSpace(model.ResolvedIdentity))
                failures.Add("model metadata must include requested and resolved asset identities");
            if (!model.Loaded || model.Missing || !ValidSha256(model.ContentSha256))
                failures.Add("model asset metadata must prove a loaded, hashed, non-missing native asset");
            ValidateTransform(model.SceneTransformColumnMajor, "model", failures);
            ValidateBounds(model.NativeGeometryBounds, model.NativeGeometryBoundsAvailable, 6, "model native geometry", failures);
            if (!model.SecondaryAssetsComplete)
                failures.Add("model metadata cannot claim complete secondary assets");
        }

        var objectKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (PcbRenderObjectMetadata item in metadata.Objects)
        {
            string key = GeometryKey(item.NativeUuid, item.ModelIndex);
            if (!CanonicalUuid(item.NativeUuid) || !objectKeys.Add(key) || !modelKeys.Contains(key))
                failures.Add("object metadata must map one-to-one to a declared native model");
            ValidateTransform(item.SceneTransformColumnMajor, "object", failures);
            ValidateBounds(item.NativeGeometryBounds, item.NativeGeometryBoundsAvailable, 6, "object native geometry", failures);
            if (item.ProjectedBoundsAvailable)
                ValidateBounds(item.ProjectedConservativeBounds, true, 4, "object projected geometry", failures);
            if (item.ExactVisiblePixels || item.OcclusionTested)
                failures.Add("object metadata cannot claim exact pixels or occlusion testing");
        }

        if (objectKeys.Count != modelKeys.Count)
            failures.Add("every declared native model must have one corresponding object geometry row");

        return new(failures.Count == 0, failures);
    }

    private static string GeometryKey(string nativeUuid, uint modelIndex) => nativeUuid + "\u001f" + modelIndex;

    private static bool CanonicalUuid(string value) =>
        Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty
        && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);

    private static void ValidateTransform(IReadOnlyList<double> values, string kind, List<string> failures)
    {
        if (values.Count != 16 || !values.All(double.IsFinite) || !Invertible(values))
            failures.Add($"{kind} transforms must contain exactly 16 finite and invertible values");
    }

    private static void ValidateBounds(IReadOnlyList<double> values, bool available, int expectedCount,
        string kind, List<string> failures)
    {
        if (!available || values.Count != expectedCount || !values.All(double.IsFinite))
        {
            failures.Add($"{kind} bounds must be available with {expectedCount} finite values");
            return;
        }

        int half = expectedCount / 2;
        for (int index = 0; index < half; index++)
        {
            if (values[index + half] < values[index])
            {
                failures.Add($"{kind} bounds must have ordered min/max values on every axis");
                break;
            }
        }
    }

    private static bool ValidSha256(string value)
    {
        if (value.Length != 64)
            return false;
        return value.All(Uri.IsHexDigit);
    }

    // Pivot each column separately so a valid nanometres-to-scene scale is
    // not rejected merely because its determinant is very small.
    private static bool Invertible(IReadOnlyList<double> values)
    {
        if (values.Count != 16 || !values.All(double.IsFinite))
            return false;
        var matrix = new double[4, 4];
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                matrix[row, column] = values[column * 4 + row];

        for (int column = 0; column < 4; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < 4; row++)
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                    pivot = row;
            if (matrix[pivot, column] == 0)
                return false;
            for (int index = column; index < 4; index++)
                (matrix[column, index], matrix[pivot, index]) = (matrix[pivot, index], matrix[column, index]);
            for (int row = column + 1; row < 4; row++)
            {
                double factor = matrix[row, column] / matrix[column, column];
                for (int index = column + 1; index < 4; index++)
                    matrix[row, index] -= factor * matrix[column, index];
            }
        }
        return true;
    }
}
