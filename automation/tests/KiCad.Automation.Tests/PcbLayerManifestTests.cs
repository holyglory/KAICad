using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbLayerManifestTests
{
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
