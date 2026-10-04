using Kiapi.Board.Jobs;
using KiCad.Automation.Mcp;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbRenderContractTests
{
    [TestMethod]
    public void CompleteCameraMetadataIsBoundToTheTypedRenderInput()
    {
        var request = RenderRequest();
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        PcbRenderMetadataValidation validation = PcbRenderMetadataContract.Validate(
            CameraMetadata(stateHash, inputHash), request, stateHash, inputHash);

        Assert.IsTrue(validation.IsValid, string.Join("; ", validation.Failures));
    }

    [TestMethod]
    public void NativeInsetViewportAndSymmetricOrthographicClipPlanesAreValid()
    {
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
        metadata.WidthPixels = 312;
        metadata.HeightPixels = 224;
        metadata.ObservedPerspective = false;
        metadata.NearClip = -110.37;
        metadata.FarClip = 110.37;

        PcbRenderMetadataValidation validation = PcbRenderMetadataContract.Validate(
            metadata, RenderRequest(), stateHash, inputHash);

        Assert.IsTrue(validation.IsValid, string.Join("; ", validation.Failures));
    }

    [TestMethod]
    public void CameraMetadataDimensionsMustBePositiveAndContainedByTheRequest()
    {
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        foreach ((uint width, uint height) in new[]
        {
            (0u, 240u), (320u, 0u), (321u, 240u), (320u, 241u)
        })
        {
            PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
            metadata.WidthPixels = width;
            metadata.HeightPixels = height;

            PcbRenderMetadataValidation validation = PcbRenderMetadataContract.Validate(
                metadata, RenderRequest(), stateHash, inputHash);

            Assert.IsFalse(validation.IsValid, $"Unexpectedly accepted {width}x{height} metadata.");
            StringAssert.Contains(string.Join("; ", validation.Failures), "camera metadata dimensions");
        }
    }

    [TestMethod]
    public void CameraClipPlanesMustBeFiniteAndOrdered()
    {
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        foreach ((double nearClip, double farClip) in new[]
        {
            (1.0, 1.0), (10.0, -1.0), (double.NaN, 1.0), (1.0, double.PositiveInfinity)
        })
        {
            PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
            metadata.NearClip = nearClip;
            metadata.FarClip = farClip;

            PcbRenderMetadataValidation validation = PcbRenderMetadataContract.Validate(
                metadata, RenderRequest(), stateHash, inputHash);

            Assert.IsFalse(validation.IsValid, $"Unexpectedly accepted clip range {nearClip}..{farClip}.");
            StringAssert.Contains(string.Join("; ", validation.Failures), "camera clip planes");
        }
    }

    [TestMethod]
    public void InvalidCameraTransformAndAssetRowsCannotSupportNativeGeometryClaims()
    {
        var request = RenderRequest();
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
        metadata.Models.Add(new PcbRenderModelMetadata
        {
            NativeUuid = "footprint-1",
            ModelIndex = 0,
            RequestedIdentity = "${KICAD6_3DMODEL_DIR}/resistor.wrl",
            ResolvedIdentity = "/models/resistor.wrl",
            ContentSha256 = "bad-hash",
            Loaded = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 },
            NativeGeometryBoundsAvailable = true,
            SecondaryAssetsComplete = true
        });
        metadata.Objects.Add(new PcbRenderObjectMetadata
        {
            NativeUuid = "footprint-1",
            ModelIndex = 0,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, double.NaN, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 },
            NativeGeometryBoundsAvailable = true,
            ProjectedConservativeBounds = { 0, 0, 320, 240 },
            ProjectedBoundsAvailable = true
        });

        PcbRenderMetadataValidation validation = PcbRenderMetadataContract.Validate(
            metadata, request, stateHash, inputHash);

        Assert.IsFalse(validation.IsValid);
        string failures = string.Join("; ", validation.Failures);
        StringAssert.Contains(failures, "model asset metadata");
        StringAssert.Contains(failures, "object transforms");
    }

    [TestMethod]
    public void SingularModelTransformCannotSupportLoadedAssetProvenance()
    {
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
        metadata.Models.Add(new PcbRenderModelMetadata
        {
            NativeUuid = "footprint-1", ModelIndex = 0,
            RequestedIdentity = "${KICAD6_3DMODEL_DIR}/resistor.wrl",
            ResolvedIdentity = "/models/resistor.wrl", ContentSha256 = new string('c', 64),
            Loaded = true, SecondaryAssetsComplete = true, NativeGeometryBoundsAvailable = true,
            // A loaded, hashed asset still cannot support a geometry claim when
            // its scene transform collapses an axis.
            SceneTransformColumnMajor = { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 }
        });
        metadata.Objects.Add(new PcbRenderObjectMetadata
        {
            NativeUuid = "footprint-1", ModelIndex = 0, NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 }
        });

        PcbRenderMetadataValidation validation = PcbRenderMetadataContract.Validate(
            metadata, RenderRequest(), stateHash, inputHash);

        Assert.IsFalse(validation.IsValid);
        StringAssert.Contains(string.Join("; ", validation.Failures), "model transforms");
    }

    [TestMethod]
    public void NativeFiniteFlagsCannotHideSingularCameraMatricesOrChangedInput()
    {
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
        metadata.ViewMatrixColumnMajor[15] = 0;
        metadata.EffectiveRenderInputSha256 = new string('c', 64);

        PcbRenderMetadataValidation validation = PcbRenderMetadataContract.Validate(
            metadata, RenderRequest(), stateHash, inputHash);

        Assert.IsFalse(validation.IsValid);
        string failures = string.Join("; ", validation.Failures);
        StringAssert.Contains(failures, "camera matrices");
        StringAssert.Contains(failures, "effective render input");
    }

    [TestMethod]
    public void FlatModelsAndUnavailableOffscreenProjectionsRemainValid()
    {
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
        metadata.BoardNanometresToSceneColumnMajor[0] = 1e-12;
        metadata.BoardNanometresToSceneColumnMajor[5] = -1e-12;
        metadata.BoardNanometresToSceneColumnMajor[10] = 1e-12;
        metadata.Models.Add(new PcbRenderModelMetadata
        {
            NativeUuid = "footprint-1", ModelIndex = 0,
            RequestedIdentity = "${KICAD6_3DMODEL_DIR}/resistor.wrl",
            ResolvedIdentity = "/models/resistor.wrl", ContentSha256 = new string('c', 64),
            Loaded = true, SecondaryAssetsComplete = true, NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 0 }
        });
        metadata.Objects.Add(new PcbRenderObjectMetadata
        {
            NativeUuid = "footprint-1", ModelIndex = 0, NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 0 },
            ProjectedBoundsAvailable = false
        });

        PcbRenderMetadataValidation validation = PcbRenderMetadataContract.Validate(
            metadata, RenderRequest(), stateHash, inputHash);

        Assert.IsTrue(validation.IsValid, string.Join("; ", validation.Failures));
    }

    private static RunBoardJobExportRender RenderRequest() => new()
    {
        Width = 320,
        Height = 240,
        Side = RenderSide.RsTop,
        Zoom = 1,
        Perspective = false
    };

    private static PcbRenderMetadata CameraMetadata(string stateHash, string inputHash)
    {
        var metadata = new PcbRenderMetadata
        {
            WidthPixels = 320,
            HeightPixels = 240,
            ProjectionConvention = "column-major; clip=projection*view*board_to_scene*[x_nm,y_nm,z_nm,1]; pixel origin top-left",
            NearClip = 0.1,
            FarClip = 1000,
            ObservedSide = "top",
            ObservedZoom = 1,
            SourceBoardSnapshotSha256 = stateHash,
            EffectiveRenderInputSha256 = inputHash,
            MatricesFinite = true,
            MatricesInvertible = true
        };
        metadata.ViewMatrixColumnMajor.AddRange(IdentityMatrix());
        metadata.ProjectionMatrixColumnMajor.AddRange(IdentityMatrix());
        metadata.BoardNanometresToSceneColumnMajor.AddRange(IdentityMatrix());
        metadata.ObservedCameraPosition.AddRange([0, 0, 1]);
        metadata.ObservedLookAt.AddRange([0, 0, 0]);
        return metadata;
    }

    private static double[] IdentityMatrix() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
}
