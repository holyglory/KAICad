using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Jobs;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbLayerManifestTests
{
    [TestMethod]
    public void LayerManifestPointsToExistingTypedSvgSubsetExportWithoutClaimingRendering()
    {
        IReadOnlyDictionary<string, object?> coverage = PcbLayerManifestTools.SvgExportCoverage;
        Assert.AreEqual("kicad_pcb_export", coverage["tool"]);
        Assert.AreEqual("exportSvg", coverage["job"]);
        foreach (string supported in new[] { "supported", "layerSubset", "composite", "oneFilePerLayer",
                     "lifecycleBound", "processEpochBound", "outputHashes" })
            Assert.AreEqual(true, coverage[supported], supported);
        Assert.AreEqual(false, coverage["boardMutation"]);
        StringAssert.Contains(string.Join(" ", (string[])coverage["limitations"]!),
            "does not contain SVG geometry or exact rendered pixels");

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach ((string mode, BoardJobPaginationMode expected) in new[]
        {
            ("BJPM_ALL_LAYERS_ONE_PAGE", BoardJobPaginationMode.BjpmAllLayersOnePage),
            ("BJPM_EACH_LAYER_OWN_PAGE", BoardJobPaginationMode.BjpmEachLayerOwnPage),
            ("BJPM_EACH_LAYER_OWN_FILE", BoardJobPaginationMode.BjpmEachLayerOwnFile)
        })
        {
            string json = "{\"job\":{\"exportSvg\":{\"plotSettings\":{\"layers\":[\"BL_F_Cu\",\"BL_B_Cu\"],\"commonLayers\":[\"BL_Edge_Cuts\"]},\"pageMode\":\"" + mode + "\"}}}";
            var input = JsonSerializer.Deserialize<PcbManufacturingExportInput>(json, options)!;
            RunBoardJobExportSvg request = input.Job!.ExportSvg!.ToNative();
            CollectionAssert.AreEqual(new[] { BoardLayer.BlFCu, BoardLayer.BlBCu }, request.PlotSettings.Layers);
            CollectionAssert.AreEqual(new[] { BoardLayer.BlEdgeCuts }, request.PlotSettings.CommonLayers);
            Assert.AreEqual(expected, request.PageMode, mode);
        }
    }

    [TestMethod]
    public void LayerManifestRequiresDistinctCanonicalNativeIdentities()
    {
        string id = "a1111111-1111-1111-1111-111111111111";
        PcbLayerManifestTools.ValidateNativeIdentities([id]);

        var duplicate = Assert.ThrowsExactly<AutomationException>(() =>
            PcbLayerManifestTools.ValidateNativeIdentities([id, id]));
        Assert.AreEqual("pcb_layer_manifest_identity", duplicate.Code);

        var uppercase = Assert.ThrowsExactly<AutomationException>(() =>
            PcbLayerManifestTools.ValidateNativeIdentities([id.ToUpperInvariant()]));
        Assert.AreEqual("pcb_layer_manifest_identity", uppercase.Code);

        var missing = Assert.ThrowsExactly<AutomationException>(() =>
            PcbLayerManifestTools.ValidateNativeIdentities([null]));
        Assert.AreEqual("pcb_layer_manifest_identity", missing.Code);
    }

    [TestMethod]
    public void LayerManifestOrdersTrackAndShapeObjectsDeterministically()
    {
        string first = "a1111111-1111-1111-1111-111111111111";
        string second = "b2222222-2222-2222-2222-222222222222";
        var rows = PcbLayerManifestTools.OrderObjects([
            new(first, "track", [nameof(BoardLayer.BlFCu)], null),
            new(second, "shape", [nameof(BoardLayer.BlFSilkS)], null),
            new(second, "arc", [nameof(BoardLayer.BlFCu)], null)
        ]);

        CollectionAssert.AreEqual(new[] { "arc", "shape", "track" }, rows.Select(row => row.Type).ToArray());
        CollectionAssert.AreEqual(new[] { second, second, first }, rows.Select(row => row.Id).ToArray());
    }

    [TestMethod]
    public void LayerManifestMapsEmbeddedAndNativeFootprintChildrenWithLayerFiltering()
    {
        string footprintId = "a1111111-1111-1111-1111-111111111111";
        string frontPadId = "b2222222-2222-2222-2222-222222222222";
        string backPadId = "c3333333-3333-3333-3333-333333333333";
        var footprint = new FootprintInstance { Id = new() { Value = footprintId }, Definition = new() };
        Pad frontPad = Pad(frontPadId, footprintId, BoardLayer.BlFCu);
        Pad backPad = Pad(backPadId, footprintId, BoardLayer.BlBCu);
        footprint.Definition.Items.Add(Any.Pack(frontPad));
        footprint.Definition.Items.Add(Any.Pack(backPad));

        // The top-level native query repeats the same pad envelope. The manifest must merge it
        // with the embedded definition instead of exposing a duplicate child row.
        var rows = PcbLayerManifestTools.BuildFootprintChildren(
            [footprint], [Any.Pack(frontPad.Clone()), Any.Pack(backPad.Clone())]);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(footprintId, rows[0].FootprintId);
        CollectionAssert.AreEqual(new[] { frontPadId, backPadId }, rows[0].Children.Select(child => child.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "pad", "pad" }, rows[0].Children.Select(child => child.Type).ToArray());
        Assert.IsTrue(rows[0].Children.All(child => child.ParentId == footprintId));

        var frontOnly = PcbLayerManifestTools.BuildFootprintChildren(
            [footprint], [Any.Pack(frontPad.Clone()), Any.Pack(backPad.Clone())],
            new HashSet<BoardLayer> { BoardLayer.BlFCu });
        Assert.AreEqual(1, frontOnly.Count);
        CollectionAssert.AreEqual(new[] { frontPadId }, frontOnly[0].Children.Select(child => child.Id).ToArray());
    }

    [TestMethod]
    public void LayerManifestRejectsDuplicateChildrenAndUnknownNativeParents()
    {
        string footprintId = "a1111111-1111-1111-1111-111111111111";
        string childId = "b2222222-2222-2222-2222-222222222222";
        var footprint = new FootprintInstance { Id = new() { Value = footprintId }, Definition = new() };
        Pad child = Pad(childId, footprintId, BoardLayer.BlFCu);
        footprint.Definition.Items.Add(Any.Pack(child));
        footprint.Definition.Items.Add(Any.Pack(child.Clone()));

        var duplicate = Assert.ThrowsExactly<AutomationException>(() =>
            PcbLayerManifestTools.BuildFootprintChildren([footprint], []));
        Assert.AreEqual("pcb_layer_manifest_identity", duplicate.Code);

        Pad orphan = Pad("c3333333-3333-3333-3333-333333333333",
            "d4444444-4444-4444-4444-444444444444", BoardLayer.BlFCu);
        var unknownParent = Assert.ThrowsExactly<AutomationException>(() =>
            PcbLayerManifestTools.BuildFootprintChildren([new FootprintInstance { Id = new() { Value = footprintId } }],
                [Any.Pack(orphan)]));
        Assert.AreEqual("pcb_layer_manifest_parent_identity", unknownParent.Code);
    }

    private static Pad Pad(string id, string parentId, BoardLayer layer) => new()
    {
        Id = new() { Value = id },
        Parent = new() { Value = parentId },
        PadStack = new() { Layers = { layer }, CopperLayers = { new PadStackLayer { Layer = layer,
            Shape = PadStackShape.PssCircle, Size = new() { XNm = 100, YNm = 100 } } } }
    };
}
