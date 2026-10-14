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
     Description("Render one exact native PCB view through KiCad's existing 3D renderer and return the PNG alongside the matching lifecycle state. requestJson is RunBoardJobExportRender protobuf JSON with an explicit PCB document; the MCP service owns the temporary output path. The operation is read-only, rejects unsupported dimensions/formats and discards the image if the board changes during rendering. Renderer metadata is complete only for the covered, shown footprint-model rows; board copper/pads/tracks/zones, texture identities, exact pixels and occlusion remain explicit limitations. Invoke it repeatedly for top/bottom/side or zoomed views; this is observation, not routing or RF correctness."),
     KiCadCapability("pcb-observation", "compiled-mcp plus native-board-job-renderer", "board lifecycle state, native image snapshot"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> Render3D(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) => Observe3D(instanceId, requestJson, expectedStateJson, true,
            cancellationToken);

    [McpServerTool(Name = "kicad_pcb_render_3d_geometry", ReadOnly = true),
     Description("Read lifecycle-bound native 3D geometry metadata for one typed RunBoardJobExportRender request and expected PCB state. This invokes the same native renderer as kicad_pcb_render_3d, including its temporary PNG rendering cost, but returns text only. It exposes the camera matrices, shown footprint-model identities and content hashes, native model/object transforms and conservative scene/projected bounds. The current handler does not expose mesh vertices, board copper/pads/tracks/zones or layer overlays, secondary texture identities, exact visible pixels or occlusion depth; the whole-scene result is explicitly incomplete. An older peer without the inspection handler is refused rather than substituting image-only output."),
     KiCadCapability("pcb-observation", "compiled-mcp plus native-board-job-renderer", "exact PCB lifecycle state, native camera/model transforms, conservative model bounds and asset coverage"),
     KiCadVerification(KiCadVerificationLevel.InProcess, "PcbRenderContractTests.GeometryObservationStatusIsExplicitlyIncompleteAndNeverClaimsRasterCoverage")]
    public Task<CallToolResult> Read3DGeometry(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) => Observe3D(instanceId, requestJson, expectedStateJson, false,
            cancellationToken);

    private Task<CallToolResult> Observe3D(string instanceId, string requestJson, string expectedStateJson,
        bool includeImage, CancellationToken cancellationToken) => Execute(async () =>
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
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || expected.Document is null
            || !expected.Document.Equals(document)
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
                PcbRenderInspectionContract.ValidateBinding(inspection, document, expected);
            }
            else
            {
                if (!includeImage)
                    throw new AutomationException("pcb_render_metadata_unavailable",
                        "The native peer does not implement PcbRenderInspectionRequest; 3D geometry metadata is unavailable.");
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
            PcbRenderCoverage coverage = inspection is null
                ? PcbRenderCoverage.Unavailable
                : PcbRenderCoverageContract.Evaluate(inspection.Metadata, metadataValidation.IsValid,
                    inspection.MetadataComplete);
            if (inspection is not null)
                limitations.AddRange(inspection.Limitations);
            limitations.AddRange(metadataValidation.Failures.Select(failure => "renderer metadata contract: " + failure));
            limitations.AddRange(PcbRenderObservationContract.Limitations);
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, processEpoch = session.Epoch, document,
                status = PcbRenderObservationContract.Status(inspection is not null),
                complete = PcbRenderObservationContract.Complete,
                inputIdentity = new
                {
                    boardDocument = document,
                    processEpoch = expected.ProcessEpoch,
                    revision = expected.Revision,
                    nativeIdentity = expected.NativeIdentity,
                    boardSnapshotSha256 = expected.StateSha256
                },
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
                metadataCoverage = new
                {
                    modelInstancesComplete = coverage.ModelInstancesComplete,
                    boardGeometryComplete = coverage.BoardGeometryComplete,
                    secondaryAssetIdentitiesComplete = coverage.SecondaryAssetIdentitiesComplete,
                    exactVisiblePixels = coverage.ExactVisiblePixels,
                    occlusionDepth = coverage.OcclusionDepth
                },
                geometryScope = new
                {
                    objectKind = "shown-footprint-model-instances",
                    transformSource = "native-renderer",
                    boundsKind = "conservative-scene-and-projected-aabb",
                    meshVerticesAvailable = false,
                    layerGeometryAvailable = false
                },
                metadata,
                limitations,
                nativeRender = true, rasterImageIncluded = includeImage,
                boardMutation = false, electricalValidation = false
            });
            return new CallToolResult
            {
                Content = includeImage
                    ? [ImageContentBlock.FromBytes(bytes, "image/png"), new TextContentBlock { Text = structured.GetRawText() }]
                    : [new TextContentBlock { Text = structured.GetRawText() }],
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

internal static class PcbRenderInspectionContract
{
    internal static void ValidateBinding(PcbRenderInspectionResponse inspection,
        DocumentSpecifier document, DocumentLifecycleState expected)
    {
        if (inspection is null
            || inspection.Document is null
            || expected.Document is null
            || !inspection.Document.Equals(expected)
            || inspection.Document.Document is null
            || !inspection.Document.Document.Equals(document))
            throw new AutomationException("pcb_render_identity_mismatch",
                "The native render inspection returned metadata for a different PCB lifecycle checkpoint.");

        if (!string.Equals(inspection.ProcessEpoch, expected.ProcessEpoch, StringComparison.Ordinal))
            throw new AutomationException("pcb_render_identity_mismatch",
                "The native render inspection returned metadata from a different process epoch.");

        if (inspection.Job is null || inspection.Metadata is null)
            throw new AutomationException("pcb_render_metadata_unavailable",
                "The native render inspection did not return its job and metadata envelopes.");
    }
}

internal static class PcbRenderObservationContract
{
    internal static bool Complete => false;
    internal static IReadOnlyList<string> Limitations { get; } =
    [
        "3D geometry covers shown footprint-model instances only; mesh vertices and PCB layer geometry are unavailable",
        "projected bounds are conservative and do not prove exact visible pixels or occlusion depth"
    ];

    internal static string Status(bool metadataAvailable) => metadataAvailable ? "incomplete" : "unavailable";
}

internal readonly record struct PcbRenderCoverage(
    bool ModelInstancesComplete,
    bool BoardGeometryComplete,
    bool SecondaryAssetIdentitiesComplete,
    bool ExactVisiblePixels,
    bool OcclusionDepth)
{
    internal static PcbRenderCoverage Unavailable => new(false, false, false, false, false);
}

internal static class PcbRenderCoverageContract
{
    internal static PcbRenderCoverage Evaluate(PcbRenderMetadata metadata,
        bool metadataValidated, bool nativeMetadataComplete)
    {
        bool modelInstancesComplete = nativeMetadataComplete
            && metadataValidated
            && metadata.Models.Count > 0
            && metadata.Models.All(HasCompleteModelIdentityAndGeometry);
        bool secondaryAssetsComplete = modelInstancesComplete
            && metadata.Models.All(model => model.SecondaryAssetsComplete);

        // The current native hook deliberately emits conservative mesh bounds;
        // it does not inspect raster visibility, depth, or board copper geometry.
        // Coverage notes are descriptive evidence, not a typed claim that can
        // extend the current protocol's footprint-model-only boundary.
        return new(modelInstancesComplete, false, secondaryAssetsComplete, false, false);
    }

    private static bool HasCompleteModelIdentityAndGeometry(PcbRenderModelMetadata model) =>
        CanonicalUuid(model.NativeUuid)
        && !string.IsNullOrWhiteSpace(model.RequestedIdentity)
        && !string.IsNullOrWhiteSpace(model.ResolvedIdentity)
        && model.Loaded
        && !model.Missing
        && ValidSha256(model.ContentSha256)
        && model.SceneTransformColumnMajor.Count == 16
        && model.SceneTransformColumnMajor.All(double.IsFinite)
        && Invertible(model.SceneTransformColumnMajor)
        && model.NativeGeometryBoundsAvailable
        && model.NativeGeometryBounds.Count == 6
        && model.NativeGeometryBounds.All(double.IsFinite)
        && model.NativeGeometryBounds[3] >= model.NativeGeometryBounds[0]
        && model.NativeGeometryBounds[4] >= model.NativeGeometryBounds[1]
        && model.NativeGeometryBounds[5] >= model.NativeGeometryBounds[2];

    private static bool CanonicalUuid(string value) =>
        Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty
        && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);

    private static bool ValidSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool Invertible(IReadOnlyList<double> values)
    {
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

        var modelsByKey = new Dictionary<string, PcbRenderModelMetadata>(StringComparer.Ordinal);
        foreach (PcbRenderModelMetadata model in metadata.Models)
        {
            string key = GeometryKey(model.NativeUuid, model.ModelIndex);
            if (!CanonicalUuid(model.NativeUuid) || !modelsByKey.TryAdd(key, model))
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
            if (!CanonicalUuid(item.NativeUuid) || !objectKeys.Add(key) || !modelsByKey.ContainsKey(key))
                failures.Add("object metadata must map one-to-one to a declared native model");
            ValidateTransform(item.SceneTransformColumnMajor, "object", failures);
            ValidateBounds(item.NativeGeometryBounds, item.NativeGeometryBoundsAvailable, 6, "object native geometry", failures);
            // The native producer copies these scene-space fields from the
            // model row. A matching identity cannot describe two locations.
            if (modelsByKey.TryGetValue(key, out PcbRenderModelMetadata? model)
                && (!item.SceneTransformColumnMajor.SequenceEqual(model.SceneTransformColumnMajor)
                    || !item.NativeGeometryBounds.SequenceEqual(model.NativeGeometryBounds)))
                failures.Add("object metadata must preserve its declared model geometry");
            if (item.ProjectedBoundsAvailable)
            {
                ValidateBounds(item.ProjectedConservativeBounds, true, 4, "object projected geometry", failures);
                ValidateProjectedBounds(item, metadata, failures);
            }
            if (item.ExactVisiblePixels || item.OcclusionTested)
                failures.Add("object metadata cannot claim exact pixels or occlusion testing");
        }

        if (objectKeys.Count != modelsByKey.Count)
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

    private static void ValidateProjectedBounds(PcbRenderObjectMetadata item,
        PcbRenderMetadata metadata, List<string> failures)
    {
        // Object bounds are emitted in scene space by the native producer, so
        // the declared pixel bounds must conservatively contain the projection
        // of those same bounds under the captured camera matrices. Without
        // this check a producer could pair valid camera/model rows with pixel
        // coordinates from a different view and still pass the metadata
        // boundary.
        if (metadata.WidthPixels == 0 || metadata.HeightPixels == 0
            || metadata.ViewMatrixColumnMajor.Count != 16
            || metadata.ProjectionMatrixColumnMajor.Count != 16
            || !metadata.ViewMatrixColumnMajor.All(double.IsFinite)
            || !metadata.ProjectionMatrixColumnMajor.All(double.IsFinite)
            || item.NativeGeometryBounds.Count != 6
            || !item.NativeGeometryBounds.All(double.IsFinite))
            return;

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        for (int x = 0; x < 2; x++)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int z = 0; z < 2; z++)
                {
                    double sceneX = item.NativeGeometryBounds[x == 0 ? 0 : 3];
                    double sceneY = item.NativeGeometryBounds[y == 0 ? 1 : 4];
                    double sceneZ = item.NativeGeometryBounds[z == 0 ? 2 : 5];
                    double viewX = MatrixProduct(metadata.ViewMatrixColumnMajor, 0, sceneX, sceneY, sceneZ);
                    double viewY = MatrixProduct(metadata.ViewMatrixColumnMajor, 1, sceneX, sceneY, sceneZ);
                    double viewZ = MatrixProduct(metadata.ViewMatrixColumnMajor, 2, sceneX, sceneY, sceneZ);
                    double viewW = MatrixProduct(metadata.ViewMatrixColumnMajor, 3, sceneX, sceneY, sceneZ);
                    double clipX = MatrixProduct(metadata.ProjectionMatrixColumnMajor, 0, viewX, viewY, viewZ, viewW);
                    double clipY = MatrixProduct(metadata.ProjectionMatrixColumnMajor, 1, viewX, viewY, viewZ, viewW);
                    double clipW = MatrixProduct(metadata.ProjectionMatrixColumnMajor, 3, viewX, viewY, viewZ, viewW);
                    if (!double.IsFinite(clipX) || !double.IsFinite(clipY) || !double.IsFinite(clipW)
                        || Math.Abs(clipW) < double.Epsilon)
                        continue;

                    double pixelX = (clipX / clipW + 1.0) * 0.5 * metadata.WidthPixels;
                    double pixelY = (1.0 - clipY / clipW) * 0.5 * metadata.HeightPixels;
                    minX = Math.Min(minX, pixelX);
                    minY = Math.Min(minY, pixelY);
                    maxX = Math.Max(maxX, pixelX);
                    maxY = Math.Max(maxY, pixelY);
                }
            }
        }

        if (!double.IsFinite(minX) || !double.IsFinite(minY)
            || !double.IsFinite(maxX) || !double.IsFinite(maxY))
            return;

        const double tolerance = 1e-6;
        IReadOnlyList<double> declared = item.ProjectedConservativeBounds;
        if (declared[0] > minX + tolerance || declared[1] > minY + tolerance
            || declared[2] < maxX - tolerance || declared[3] < maxY - tolerance)
            failures.Add("object projected geometry must conservatively contain the captured camera projection");
    }

    private static double MatrixProduct(IReadOnlyList<double> matrix, int row,
        double x, double y, double z, double w = 1.0)
    {
        double[] vector = [x, y, z, w];
        double result = 0;
        for (int column = 0; column < 4; column++)
            result += matrix[column * 4 + row] * vector[column];
        return result;
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
