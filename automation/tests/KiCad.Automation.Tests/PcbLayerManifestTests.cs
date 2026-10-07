using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Kiapi.Board.Jobs;
using Kiapi.Board.Types;
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
}
