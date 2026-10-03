using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbVectorContractTests
{
    [TestMethod]
    public void ValidSvgReturnsProvenanceBoundIntegerNanometreGeometry()
    {
        const string svg = "<svg viewBox=\"0 0 10 10\"><path d=\"M1 2 H5 V6 Z\"/></svg>";
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(svg)));

        var result = new PcbVectorTools().ValidateSvg(svg, hash, "Dwgs.User", "1000", "-2000", "100000", 80_000);
        Assert.IsFalse(result.IsError, result.StructuredContent?.ToString());
        var data = result.StructuredContent!.Value;
        Assert.IsTrue(data.GetProperty("vectorValidation").GetBoolean());
        Assert.IsTrue(data.GetProperty("provenanceBound").GetBoolean());
        Assert.AreEqual(hash, data.GetProperty("sourceSha256").GetString());
        Assert.AreEqual("BlDwgsUser", data.GetProperty("layer").GetString());
        Assert.IsTrue(data.GetProperty("layerOwnershipValidated").GetBoolean());
        Assert.AreEqual(3, data.GetProperty("segmentCount").GetInt32());
        Assert.AreEqual(101_000, data.GetProperty("boundsNm").GetProperty("minXNm").GetInt64());
        Assert.AreEqual(598_000, data.GetProperty("boundsNm").GetProperty("maxYNm").GetInt64());
        Assert.IsFalse(data.GetProperty("boardMutation").GetBoolean());
        Assert.IsFalse(data.GetProperty("nativeCommit").GetBoolean());
    }

    [TestMethod]
    public void VectorValidationRejectsChangedSourceAndCopperOwnership()
    {
        const string svg = "<svg viewBox=\"0 0 2 2\"><line x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"/></svg>";
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(svg)));
        var changed = new PcbVectorTools().ValidateSvg(svg + " ", hash);
        Assert.IsTrue(changed.IsError);
        Assert.AreEqual("pcb_guide_source_changed", changed.StructuredContent!.Value.GetProperty("errorCode").GetString());

        var copper = new PcbVectorTools().ValidateSvg(svg, hash, "F.Cu");
        Assert.IsTrue(copper.IsError);
        Assert.AreEqual("invalid_pcb_svg_guide_layer", copper.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }
}
