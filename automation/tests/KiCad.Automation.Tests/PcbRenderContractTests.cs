using Kiapi.Board.Jobs;
using KiCad.Automation.Mcp;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbRenderContractTests
{
    [TestMethod]
    public void GeometryObservationStatusIsExplicitlyIncompleteAndNeverClaimsRasterCoverage()
    {
        Assert.AreEqual("incomplete", PcbRenderObservationContract.Status(true));
        Assert.AreEqual("unavailable", PcbRenderObservationContract.Status(false));
        CollectionAssert.Contains(PcbRenderObservationContract.Limitations.ToArray(),
            "3D geometry covers shown footprint-model instances only; mesh vertices and PCB layer geometry are unavailable");
        CollectionAssert.Contains(PcbRenderObservationContract.Limitations.ToArray(),
            "complete PCB layer overlays are unavailable; the native inspection does not enumerate board-layer raster/vector overlays");
        CollectionAssert.Contains(PcbRenderObservationContract.Limitations.ToArray(),
            "DRC overlays are unavailable; the native inspection does not capture DRC marker geometry or visibility");
        CollectionAssert.Contains(PcbRenderObservationContract.Limitations.ToArray(),
            "projected bounds are conservative and do not prove exact visible pixels or occlusion depth");
    }

    [TestMethod]
    public void CapabilityBoundaryReportsSeparateBoardGeometryAndSecondaryAssetProof()
    {
        JsonElement unavailable = JsonSerializer.SerializeToElement(
            PcbRenderCapabilityContract.Describe(null, PcbRenderCoverage.Unavailable, false));

        JsonElement boardGeometry = unavailable.GetProperty("boardGeometry");
        Assert.AreEqual("separate_lifecycle_bound_inspection", boardGeometry.GetProperty("status").GetString());
        Assert.AreEqual("kicad_pcb_render_geometry_typed", boardGeometry.GetProperty("tool").GetString());
        Assert.IsTrue(boardGeometry.GetProperty("lifecycleBound").GetBoolean());
        Assert.IsFalse(boardGeometry.GetProperty("sameRenderProjection").GetBoolean());
        Assert.IsFalse(boardGeometry.GetProperty("exact3dPixels").GetBoolean());
        StringAssert.Contains(boardGeometry.GetProperty("limitation").GetString()!,
            "does not project board primitives");

        JsonElement secondary = unavailable.GetProperty("secondaryModelAssets");
        Assert.AreEqual("unavailable", secondary.GetProperty("status").GetString());
        Assert.IsFalse(secondary.GetProperty("perModelRows").GetBoolean());
        Assert.IsFalse(secondary.GetProperty("identitiesAndHashes").GetBoolean());
        Assert.IsTrue(secondary.GetProperty("limitations").GetArrayLength() > 0);

        JsonElement complete = JsonSerializer.SerializeToElement(
            PcbRenderCapabilityContract.Describe(new PcbRenderMetadata(),
                new PcbRenderCoverage(true, false, false, false, true, false, false), true));
        Assert.AreEqual("complete_for_declared_model_rows",
            complete.GetProperty("secondaryModelAssets").GetProperty("status").GetString());
        Assert.AreEqual(0,
            complete.GetProperty("secondaryModelAssets").GetProperty("limitations").GetArrayLength());
    }

    [TestMethod]
    public void CoverageNotesCannotUpgradeTheTypedBoardGeometryBoundary()
    {
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
        metadata.CoverageNotes.Add("board geometry are enumerated");
        metadata.Models.Add(new PcbRenderModelMetadata
        {
            NativeUuid = "44444444-4444-4444-4444-444444444444", ModelIndex = 0,
            RequestedIdentity = "${KICAD6_3DMODEL_DIR}/resistor.wrl",
            ResolvedIdentity = "/models/resistor.wrl", ContentSha256 = new string('d', 64),
            Loaded = true, SecondaryAssetsComplete = true, NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 }
        });
        metadata.Objects.Add(new PcbRenderObjectMetadata
        {
            NativeUuid = "44444444-4444-4444-4444-444444444444", ModelIndex = 0,
            NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 }
        });

        PcbRenderCoverage coverage = PcbRenderCoverageContract.Evaluate(metadata, true, true);

        Assert.IsTrue(coverage.ModelInstancesComplete);
        Assert.IsTrue(coverage.SecondaryAssetIdentitiesComplete);
        Assert.IsFalse(coverage.BoardGeometryComplete,
            "A prose coverage note must not claim typed board-layer geometry that the native hook does not enumerate.");
        Assert.IsFalse(coverage.CompletePcbLayerOverlays,
            "The native hook has no complete PCB-layer overlay rows.");
        Assert.IsFalse(coverage.DrcOverlays,
            "The native hook has no DRC overlay rows.");
        Assert.IsFalse(coverage.ExactVisiblePixels);
        Assert.IsFalse(coverage.OcclusionDepth);
    }

    [TestMethod]
    public void IncompleteNativeMetadataCannotClaimCompleteAssetCoverage()
    {
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
        metadata.Models.Add(new PcbRenderModelMetadata
        {
            NativeUuid = "55555555-5555-5555-5555-555555555555", ModelIndex = 0,
            RequestedIdentity = "${KICAD6_3DMODEL_DIR}/resistor.wrl",
            ResolvedIdentity = "/models/resistor.wrl", ContentSha256 = new string('e', 64),
            Loaded = true, SecondaryAssetsComplete = true, NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 }
        });
        metadata.Objects.Add(new PcbRenderObjectMetadata
        {
            NativeUuid = "55555555-5555-5555-5555-555555555555", ModelIndex = 0,
            NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 }
        });

        PcbRenderCoverage coverage = PcbRenderCoverageContract.Evaluate(metadata, true, false);

        Assert.IsFalse(coverage.ModelInstancesComplete);
        Assert.IsFalse(coverage.SecondaryAssetIdentitiesComplete,
            "Asset coverage depends on the native complete signal as well as row fields.");
        Assert.IsFalse(coverage.BoardGeometryComplete);
    }

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
            NativeUuid = "11111111-1111-1111-1111-111111111111",
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
            NativeUuid = "11111111-1111-1111-1111-111111111111",
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
    public void ModelAndObjectRowsRequireCanonicalNativeUuidIdentity()
    {
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
        string nonCanonical = "a2222222-2222-2222-2222-222222222222".ToUpperInvariant();
        metadata.Models.Add(new PcbRenderModelMetadata
        {
            NativeUuid = nonCanonical, ModelIndex = 0,
            RequestedIdentity = "${KICAD6_3DMODEL_DIR}/resistor.wrl",
            ResolvedIdentity = "/models/resistor.wrl", ContentSha256 = new string('c', 64),
            Loaded = true, SecondaryAssetsComplete = true, NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 }
        });
        metadata.Objects.Add(new PcbRenderObjectMetadata
        {
            NativeUuid = nonCanonical, ModelIndex = 0, NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 }
        });

        PcbRenderMetadataValidation validation = PcbRenderMetadataContract.Validate(
            metadata, RenderRequest(), stateHash, inputHash);

        Assert.IsFalse(validation.IsValid);
        StringAssert.Contains(string.Join("; ", validation.Failures), "model metadata");
    }

    [TestMethod]
    public void SingularModelTransformCannotSupportLoadedAssetProvenance()
    {
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
        metadata.Models.Add(new PcbRenderModelMetadata
        {
            NativeUuid = "22222222-2222-2222-2222-222222222222", ModelIndex = 0,
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
            NativeUuid = "22222222-2222-2222-2222-222222222222", ModelIndex = 0, NativeGeometryBoundsAvailable = true,
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
            NativeUuid = "33333333-3333-3333-3333-333333333333", ModelIndex = 0,
            RequestedIdentity = "${KICAD6_3DMODEL_DIR}/resistor.wrl",
            ResolvedIdentity = "/models/resistor.wrl", ContentSha256 = new string('c', 64),
            Loaded = true, SecondaryAssetsComplete = true, NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 0 }
        });
        metadata.Objects.Add(new PcbRenderObjectMetadata
        {
            NativeUuid = "33333333-3333-3333-3333-333333333333", ModelIndex = 0, NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 0 },
            ProjectedBoundsAvailable = false
        });

        PcbRenderMetadataValidation validation = PcbRenderMetadataContract.Validate(
            metadata, RenderRequest(), stateHash, inputHash);

        Assert.IsTrue(validation.IsValid, string.Join("; ", validation.Failures));

        // The native producer copies the model transform and scene bounds
        // into its corresponding object row. Individually valid rows cannot
        // locate the same model differently while claiming verified geometry.
        var movedObject = metadata.Clone();
        movedObject.Objects[0].SceneTransformColumnMajor[12] = 5;
        var movedBounds = metadata.Clone();
        movedBounds.Objects[0].NativeGeometryBounds[3] = 2;
        foreach (PcbRenderMetadata inconsistent in new[] { movedObject, movedBounds })
        {
            PcbRenderMetadataValidation mismatch = PcbRenderMetadataContract.Validate(
                inconsistent, RenderRequest(), stateHash, inputHash);
            Assert.IsFalse(mismatch.IsValid, "Contradictory model/object geometry was accepted.");
            StringAssert.Contains(string.Join("; ", mismatch.Failures), "declared model geometry");
        }
    }

    [TestMethod]
    public void ProjectedBoundsMustContainTheCapturedCameraProjection()
    {
        const string stateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string inputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        PcbRenderMetadata metadata = CameraMetadata(stateHash, inputHash);
        metadata.Models.Add(new PcbRenderModelMetadata
        {
            NativeUuid = "66666666-6666-6666-6666-666666666666", ModelIndex = 0,
            RequestedIdentity = "${KICAD6_3DMODEL_DIR}/resistor.wrl",
            ResolvedIdentity = "/models/resistor.wrl", ContentSha256 = new string('f', 64),
            Loaded = true, SecondaryAssetsComplete = true, NativeGeometryBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 }
        });
        metadata.Objects.Add(new PcbRenderObjectMetadata
        {
            NativeUuid = "66666666-6666-6666-6666-666666666666", ModelIndex = 0,
            NativeGeometryBoundsAvailable = true, ProjectedBoundsAvailable = true,
            SceneTransformColumnMajor = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            NativeGeometryBounds = { 0, 0, 0, 1, 1, 1 },
            // Identity view/projection maps the scene bounds to x=160..320
            // and y=0..120 in the 320x240 top-left-origin image.
            ProjectedConservativeBounds = { 160, 0, 320, 120 }
        });

        PcbRenderMetadataValidation valid = PcbRenderMetadataContract.Validate(
            metadata, RenderRequest(), stateHash, inputHash);
        Assert.IsTrue(valid.IsValid, string.Join("; ", valid.Failures));

        metadata.Objects[0].ProjectedConservativeBounds[0] = 161;
        PcbRenderMetadataValidation underCovered = PcbRenderMetadataContract.Validate(
            metadata, RenderRequest(), stateHash, inputHash);
        Assert.IsFalse(underCovered.IsValid);
        StringAssert.Contains(string.Join("; ", underCovered.Failures), "captured camera projection");
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
