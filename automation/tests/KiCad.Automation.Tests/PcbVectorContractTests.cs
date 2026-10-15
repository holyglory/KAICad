using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    [TestMethod]
    public void SvgParserRejectsHiddenGeometryAndOutOfViewBoxCoordinates()
    {
        var invalidSources = new[]
        {
            (Name: "root transform", Svg: "<svg viewBox=\"0 0 2 2\" transform=\"translate(1)\"><line x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"/></svg>",
                Message: "SVG transforms, scripts, images, text and external references are not allowed in a PCB guide."),
            (Name: "transform", Svg: "<svg viewBox=\"0 0 2 2\"><g transform=\"translate(1)\"><line x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"/></g></svg>",
                Message: "SVG transforms, scripts, images, text and external references are not allowed in a PCB guide."),
            (Name: "image", Svg: "<svg viewBox=\"0 0 2 2\"><image href=\"asset.png\" x=\"0\" y=\"0\" width=\"1\" height=\"1\"/></svg>",
                Message: "SVG transforms, scripts, images, text and external references are not allowed in a PCB guide."),
            (Name: "text", Svg: "<svg viewBox=\"0 0 2 2\"><text x=\"0\" y=\"1\">hidden</text></svg>",
                Message: "SVG transforms, scripts, images, text and external references are not allowed in a PCB guide."),
            (Name: "out-of-viewBox", Svg: "<svg viewBox=\"0 0 2 2\"><line x1=\"-1\" y1=\"0\" x2=\"1\" y2=\"1\"/></svg>",
                Message: "SVG guide geometry must remain inside its declared viewBox.")
        };

        var tools = new PcbVectorTools();
        foreach (var testCase in invalidSources)
        {
            var result = tools.ValidateSvg(testCase.Svg);
            Assert.IsTrue(result.IsError, testCase.Name);
            var data = result.StructuredContent!.Value;
            Assert.AreEqual("invalid_pcb_svg_guide", data.GetProperty("errorCode").GetString(), testCase.Name);
            Assert.AreEqual(testCase.Message, data.GetProperty("errorMessage").GetString(), testCase.Name);
            Assert.IsFalse(data.GetProperty("boardMutation").GetBoolean(), testCase.Name);
            Assert.IsFalse(data.GetProperty("nativeCommit").GetBoolean(), testCase.Name);
        }
    }

    [TestMethod]
    public void SvgValidationRejectsSegmentBoundsAndInvalidConversionParameters()
    {
        const string svg = "<svg viewBox=\"0 0 2 2\"><line x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"/></svg>";
        var invalidParameters = new[]
        {
            (Name: "maximumSegments below minimum", Result: new PcbVectorTools().ValidateSvg(svg, maximumSegments: 0),
                Message: "maximumSegments must be between 1 and 20,000."),
            (Name: "maximumSegments above maximum", Result: new PcbVectorTools().ValidateSvg(svg, maximumSegments: 20_001),
                Message: "maximumSegments must be between 1 and 20,000."),
            (Name: "zero scale", Result: new PcbVectorTools().ValidateSvg(svg, nanometersPerSvgUnit: "0"),
                Message: "nanometersPerSvgUnit must be positive."),
            (Name: "negative scale", Result: new PcbVectorTools().ValidateSvg(svg, nanometersPerSvgUnit: "-1"),
                Message: "nanometersPerSvgUnit must be positive."),
            (Name: "zero stroke width", Result: new PcbVectorTools().ValidateSvg(svg, strokeWidthNm: 0),
                Message: "strokeWidthNm must be positive."),
            (Name: "negative stroke width", Result: new PcbVectorTools().ValidateSvg(svg, strokeWidthNm: -1),
                Message: "strokeWidthNm must be positive.")
        };

        foreach (var testCase in invalidParameters)
        {
            Assert.IsTrue(testCase.Result.IsError, testCase.Name);
            var data = testCase.Result.StructuredContent!.Value;
            Assert.AreEqual("invalid_pcb_svg_guide", data.GetProperty("errorCode").GetString(), testCase.Name);
            Assert.AreEqual(testCase.Message, data.GetProperty("errorMessage").GetString(), testCase.Name);
            Assert.IsFalse(data.GetProperty("boardMutation").GetBoolean(), testCase.Name);
            Assert.IsFalse(data.GetProperty("nativeCommit").GetBoolean(), testCase.Name);
        }
    }

    [TestMethod]
    public void ValidPolygonAndPathConversionPreservesShaAndRemainsReadOnly()
    {
        const string svg = "<svg viewBox=\"0 0 10 10\"><polygon points=\"1,1 4,1 4,4\"/><path d=\"M6 2 H9 V5 Z\"/></svg>";
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(svg)));

        var result = new PcbVectorTools().ValidateSvg(svg, hash, "Dwgs.User", "1000", "-2000", "1000", 80_000);
        Assert.IsFalse(result.IsError, result.StructuredContent?.ToString());
        var data = result.StructuredContent!.Value;
        Assert.IsTrue(data.GetProperty("vectorValidation").GetBoolean());
        Assert.IsTrue(data.GetProperty("provenanceBound").GetBoolean());
        Assert.AreEqual(hash, data.GetProperty("sourceSha256").GetString());
        Assert.AreEqual(6, data.GetProperty("segmentCount").GetInt32());
        Assert.AreEqual(2_000, data.GetProperty("boundsNm").GetProperty("minXNm").GetInt64());
        Assert.AreEqual(-1_000, data.GetProperty("boundsNm").GetProperty("minYNm").GetInt64());
        Assert.AreEqual(10_000, data.GetProperty("boundsNm").GetProperty("maxXNm").GetInt64());
        Assert.AreEqual(3_000, data.GetProperty("boundsNm").GetProperty("maxYNm").GetInt64());
        var segments = data.GetProperty("segments");
        Assert.AreEqual(2_000, segments[0].GetProperty("start").GetProperty("xNm").GetInt64());
        Assert.AreEqual(-1_000, segments[0].GetProperty("start").GetProperty("yNm").GetInt64());
        Assert.AreEqual(7_000, segments[5].GetProperty("end").GetProperty("xNm").GetInt64());
        Assert.AreEqual(0, segments[5].GetProperty("end").GetProperty("yNm").GetInt64());
        Assert.IsFalse(data.GetProperty("boardMutation").GetBoolean());
        Assert.IsFalse(data.GetProperty("nativeCommit").GetBoolean());
    }

    [TestMethod]
    public void VectorValidationReportsExactEndpointTopologyAndResolvedOutlineStyle()
    {
        const string svg = "<svg viewBox=\"0 0 10 10\"><polygon fill=\"none\" stroke=\"#000\" stroke-width=\"0.25\" points=\"1,1 4,1 4,4\"/><line fill=\"none\" stroke=\"#000\" stroke-width=\"0.25\" x1=\"6\" y1=\"1\" x2=\"8\" y2=\"1\"/></svg>";
        JsonElement data = new PcbVectorTools().ValidateSvg(svg).StructuredContent!.Value;

        JsonElement topology = data.GetProperty("topology");
        Assert.AreEqual(2, topology.GetProperty("shapeCount").GetInt32());
        Assert.AreEqual(1, topology.GetProperty("closedShapeCount").GetInt32());
        Assert.AreEqual(1, topology.GetProperty("openShapeCount").GetInt32());
        Assert.AreEqual(4, topology.GetProperty("segmentCount").GetInt32());
        Assert.AreEqual(5, topology.GetProperty("vertexCount").GetInt32());
        Assert.AreEqual(2, topology.GetProperty("connectedComponents").GetInt32());
        Assert.AreEqual(2, topology.GetProperty("danglingEndpoints").GetInt32());
        Assert.IsTrue(topology.GetProperty("exactEndpointGraph").GetBoolean());
        Assert.IsFalse(topology.GetProperty("selfIntersectionsChecked").GetBoolean());

        JsonElement style = data.GetProperty("style");
        Assert.AreEqual(0, style.GetProperty("unsupportedShapeCount").GetInt32());
        Assert.AreEqual(2, style.GetProperty("fillNoneCount").GetInt32());
        Assert.AreEqual(2, style.GetProperty("strokePaintCount").GetInt32());
        Assert.AreEqual(2, style.GetProperty("strokeWidthSpecifiedCount").GetInt32());
        Assert.IsTrue(style.GetProperty("fillSemanticsExplicit").GetBoolean());
        Assert.IsTrue(style.GetProperty("strokeSemanticsExplicit").GetBoolean());
        Assert.IsTrue(style.GetProperty("readyForOutlineCommit").GetBoolean());
        Assert.IsTrue(data.GetProperty("authoritativeGeometryReady").GetBoolean());
    }

    [TestMethod]
    public void VectorValidationKeepsFilledAndInheritedStylesExplicitlyUnresolved()
    {
        const string svg = "<svg viewBox=\"0 0 10 10\"><g style=\"fill:#f00;stroke:url(#gradient)\"><polygon points=\"1,1 4,1 4,4\"/></g></svg>";
        JsonElement data = new PcbVectorTools().ValidateSvg(svg).StructuredContent!.Value;

        JsonElement style = data.GetProperty("style");
        Assert.AreEqual(1, style.GetProperty("unsupportedShapeCount").GetInt32());
        Assert.AreEqual(1, style.GetProperty("fillPaintCount").GetInt32());
        Assert.AreEqual(1, style.GetProperty("strokeUnsupportedCount").GetInt32());
        Assert.IsTrue(style.GetProperty("fillSemanticsExplicit").GetBoolean());
        Assert.IsTrue(style.GetProperty("strokeSemanticsExplicit").GetBoolean());
        Assert.IsFalse(style.GetProperty("readyForOutlineCommit").GetBoolean());
        Assert.IsFalse(data.GetProperty("authoritativeGeometryReady").GetBoolean());
        StringAssert.Contains(style.GetProperty("limitation").GetString()!, "does not preserve SVG fill paint");
    }
}
