using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbGeometryManifestTests
{
    [TestMethod]
    public void GeometryLayerAliasesResolveToCanonicalBoardLayersAndUnknownNamesAreRefused()
    {
        Assert.AreEqual(BoardLayer.BlFCu, PcbGeometryManifestTools.ParseLayer("F.Cu"));
        Assert.AreEqual(BoardLayer.BlFCu, PcbGeometryManifestTools.ParseLayer("BL_F_Cu"));
        Assert.AreEqual(BoardLayer.BlDwgsUser, PcbGeometryManifestTools.ParseLayer("Dwgs.User"));

        var unknown = Assert.ThrowsExactly<AutomationException>(() =>
            PcbGeometryManifestTools.ParseLayer("F.NotARealLayer"));
        Assert.AreEqual("invalid_pcb_render_geometry", unknown.Code);
    }

    [TestMethod]
    public void GeometryProjectionUsesImageYAxisAndReportsUnclippedViewportIntersection()
    {
        var viewport = new PcbGeometryManifestTools.Viewport
        {
            MinXNm = -100, MinYNm = -200, MaxXNm = 900, MaxYNm = 800
        };

        PcbGeometryManifestTools.PixelPointProjection topLeft =
            PcbGeometryManifestTools.ProjectPoint(-100, 800, viewport, 320, 240);
        PcbGeometryManifestTools.PixelPointProjection bottomRight =
            PcbGeometryManifestTools.ProjectPoint(900, -200, viewport, 320, 240);
        PcbGeometryManifestTools.PixelPointProjection center =
            PcbGeometryManifestTools.ProjectPoint(400, 300, viewport, 320, 240);

        Assert.AreEqual(0d, topLeft.X, 1e-9);
        Assert.AreEqual(0d, topLeft.Y, 1e-9);
        Assert.AreEqual(320d, bottomRight.X, 1e-9);
        Assert.AreEqual(240d, bottomRight.Y, 1e-9);
        Assert.AreEqual(160d, center.X, 1e-9);
        Assert.AreEqual(120d, center.Y, 1e-9);

        PcbGeometryManifestTools.PixelBoundsProjection intersecting =
            PcbGeometryManifestTools.ProjectBounds(-200, -300, 400, 900, viewport, 320, 240);
        Assert.IsTrue(intersecting.IntersectsViewport);
        Assert.AreEqual(-32d, intersecting.Left, 1e-9);
        Assert.AreEqual(-24d, intersecting.Top, 1e-9);
        Assert.AreEqual(160d, intersecting.Right, 1e-9);
        Assert.AreEqual(264d, intersecting.Bottom, 1e-9);
        Assert.AreEqual(192d, intersecting.Width, 1e-9);
        Assert.AreEqual(288d, intersecting.Height, 1e-9);

        PcbGeometryManifestTools.PixelBoundsProjection outside =
            PcbGeometryManifestTools.ProjectBounds(1000, 900, 1100, 1000, viewport, 320, 240);
        Assert.IsFalse(outside.IntersectsViewport);
        Assert.IsTrue(outside.Left > 320d);
        Assert.IsTrue(outside.Top < 0d);
    }

    [TestMethod]
    public void GeometryProjectionIncludesPadStackDrillAndZoneVertices()
    {
        var pad = new Pad
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") }, Number = "1",
            Position = new() { XNm = 100, YNm = 200 },
            PadStack = new()
            {
                Type = PadStackType.PstNormal,
                Layers = { BoardLayer.BlFCu, BoardLayer.BlBCu },
                Drill = new() { StartLayer = BoardLayer.BlFCu, EndLayer = BoardLayer.BlBCu,
                    Diameter = new() { XNm = 300, YNm = 300 }, Shape = DrillShape.DsCircle },
                CopperLayers = { new PadStackLayer { Layer = BoardLayer.BlFCu, Shape = PadStackShape.PssCircle,
                    Size = new() { XNm = 600, YNm = 600 } } }
            }
        };
        var zone = new Zone
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") }, Type = ZoneType.ZtCopper,
            Layers = { BoardLayer.BlFCu }, Outline = new() { Polygons = { new PolygonWithHoles
            {
                Outline = new PolyLine { Closed = true, Nodes =
                {
                    new PolyLineNode { Point = new() { XNm = 0, YNm = 0 } },
                    new PolyLineNode { Point = new() { XNm = 1000, YNm = 0 } },
                    new PolyLineNode { Point = new() { XNm = 0, YNm = 1000 } }
                } }
            } } }
        };

        JsonElement padGeometry = JsonSerializer.SerializeToElement(
            PcbGeometryManifestTools.Geometry(Any.Pack(pad)));
        Assert.AreEqual("pad", padGeometry.GetProperty("kind").GetString());
        Assert.AreEqual("PstNormal", padGeometry.GetProperty("padStack").GetProperty("type").GetString());
        Assert.AreEqual("DsCircle", padGeometry.GetProperty("padStack").GetProperty("drill").GetProperty("shape").GetString());
        Assert.AreEqual(600, padGeometry.GetProperty("padStack").GetProperty("copperLayers")[0]
            .GetProperty("size").GetProperty("xNm").GetInt64());

        JsonElement zoneGeometry = JsonSerializer.SerializeToElement(
            PcbGeometryManifestTools.Geometry(Any.Pack(zone)));
        Assert.AreEqual("zone", zoneGeometry.GetProperty("kind").GetString());
        Assert.AreEqual(3, zoneGeometry.GetProperty("outline").GetProperty("polygons")[0]
            .GetProperty("outline").GetProperty("nodes").GetArrayLength());
        Assert.AreEqual(1000, zoneGeometry.GetProperty("outline").GetProperty("polygons")[0]
            .GetProperty("outline").GetProperty("nodes")[1].GetProperty("point").GetProperty("xNm").GetInt64());
    }

    [TestMethod]
    public void GeometryManifestRequiresDistinctCanonicalNativeIdentities()
    {
        var id = new KIID { Value = "11111111-1111-1111-1111-111111111111" };
        PcbGeometryManifestTools.ValidateNativeIdentities([id]);

        var duplicate = Assert.ThrowsExactly<AutomationException>(() =>
            PcbGeometryManifestTools.ValidateNativeIdentities([id, id.Clone()]));
        Assert.AreEqual("pcb_render_geometry_bounds_incomplete", duplicate.Code);

        var uppercase = Assert.ThrowsExactly<AutomationException>(() =>
            PcbGeometryManifestTools.ValidateNativeIdentities([
                new KIID { Value = "a2222222-2222-2222-2222-222222222222".ToUpperInvariant() }]));
        Assert.AreEqual("pcb_render_geometry_bounds_incomplete", uppercase.Code);

        var missing = Assert.ThrowsExactly<AutomationException>(() =>
            PcbGeometryManifestTools.ValidateNativeIdentities([null]));
        Assert.AreEqual("pcb_render_geometry_bounds_incomplete", missing.Code);
    }
}
