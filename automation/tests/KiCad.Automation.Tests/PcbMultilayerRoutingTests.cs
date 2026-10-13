using System.Collections.Immutable;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

// Isolated engine checks for the deterministic multilayer proposal.  The live
// MCP journey remains the authoritative proof that these exact Any-packed
// candidates cross the native board boundary.
[TestClass]
public sealed class PcbMultilayerRoutingTests
{
    [TestMethod]
    public void RuleAreaTrackAndViaRestrictionsRemainIndependentAndDeterministic()
    {
        var enabled = new[] { BoardLayer.BlFCu, BoardLayer.BlIn1Cu, BoardLayer.BlIn2Cu, BoardLayer.BlBCu }.ToImmutableArray();
        var request = Request(enabled);
        DocumentSpecifier document = new() { BoardFilename = "fixture.kicad_pcb", Type = DocumentType.DoctypePcb };
        DocumentLifecycleState state = new() { StateSha256 = new string('a', 64) };
        Zone trackOnly = RuleArea("11111111-1111-1111-1111-111111111111", keepTracks: true);
        Zone viaOnly = RuleArea("22222222-2222-2222-2222-222222222222", keepVias: true);
        Zone footprintOnly = RuleArea("33333333-3333-3333-3333-333333333333", keepFootprints: true);
        Zone copperOnly = RuleArea("44444444-4444-4444-4444-444444444444", keepCopper: true);

        var first = MultilayerRoutePlanner.Plan(MultilayerRoutePlanner.Validate(request, enabled),
            [Any.Pack(viaOnly), Any.Pack(copperOnly), Any.Pack(trackOnly), Any.Pack(footprintOnly)], enabled, document, state);
        var replay = MultilayerRoutePlanner.Plan(MultilayerRoutePlanner.Validate(request, enabled),
            [Any.Pack(trackOnly), Any.Pack(footprintOnly), Any.Pack(copperOnly), Any.Pack(viaOnly)], enabled, document, state);
        Assert.AreEqual(first.PlanId, replay.PlanId);

        JsonElement report = JsonSerializer.SerializeToElement(first.ObstacleReport);
        JsonElement track = report.EnumerateArray().Single(item => item.GetProperty("id").GetString() == trackOnly.Id.Value);
        JsonElement via = report.EnumerateArray().Single(item => item.GetProperty("id").GetString() == viaOnly.Id.Value);
        Assert.IsTrue(track.GetProperty("blocksTracks").GetBoolean());
        Assert.IsFalse(track.GetProperty("blocksVias").GetBoolean());
        Assert.IsFalse(via.GetProperty("blocksTracks").GetBoolean());
        Assert.IsTrue(via.GetProperty("blocksVias").GetBoolean());
        Assert.IsFalse(report.EnumerateArray().Any(item => item.GetProperty("id").GetString() == footprintOnly.Id.Value));
        Assert.IsFalse(report.EnumerateArray().Any(item => item.GetProperty("id").GetString() == copperOnly.Id.Value));
    }
    [TestMethod]
    public void ThroughViasCrossAFullForeignWallAndReturnToTheFrontLayer()
    {
        var enabled = new[] { BoardLayer.BlFCu, BoardLayer.BlIn1Cu, BoardLayer.BlIn2Cu, BoardLayer.BlBCu }.ToImmutableArray();
        var request = Request(enabled);
        var validated = MultilayerRoutePlanner.Validate(request, enabled);
        var wall = new Track
        {
            Id = new() { Value = "wall" },
            Start = new() { XNm = 2_000_000, YNm = -1_000_000 },
            End = new() { XNm = 2_000_000, YNm = 1_000_000 },
            Width = new() { ValueNm = 100_000 }, Layer = BoardLayer.BlFCu,
            Net = new() { Name = "OTHER" }
        };
        var document = new DocumentSpecifier { BoardFilename = "fixture.kicad_pcb", Type = DocumentType.DoctypePcb };
        var state = new DocumentLifecycleState { StateSha256 = new string('a', 64) };

        var first = MultilayerRoutePlanner.Plan(validated, [Any.Pack(wall)], enabled, document, state);
        var replay = MultilayerRoutePlanner.Plan(validated, [Any.Pack(wall)], enabled, document, state);

        Assert.IsTrue(first.RouteFound, first.FailureCode);
        Assert.AreEqual(2, first.ViaCount);
        Assert.AreEqual(2, first.Candidate!.Items.Count(item => item.Is(Via.Descriptor)));
        Assert.IsTrue(first.Candidate.Items.Where(item => item.Is(Via.Descriptor)).All(item =>
        {
            Via via = item.Unpack<Via>();
            return via.Type == ViaType.VtThrough
                && via.PadStack.Type == PadStackType.PstNormal
                && via.PadStack.Layers.SequenceEqual(enabled)
                && via.PadStack.Drill.StartLayer == BoardLayer.BlFCu
                && via.PadStack.Drill.EndLayer == BoardLayer.BlBCu
                && via.PadStack.Drill.Shape == DrillShape.DsCircle
                && via.PadStack.Drill.Diameter.XNm == 200_000
                && via.PadStack.Drill.Diameter.YNm == 200_000
                && via.PadStack.CopperLayers.Count == 1
                && via.PadStack.CopperLayers[0].Layer == BoardLayer.BlFCu
                && via.PadStack.CopperLayers[0].Shape == PadStackShape.PssCircle
                && via.PadStack.CopperLayers[0].CustomAnchorShape == PadStackShape.PssRectangle
                && via.PadStack.CopperLayers[0].Size.XNm == 400_000
                && via.PadStack.CopperLayers[0].Size.YNm == 400_000;
        }));
        Assert.IsTrue(first.Candidate.Items.Where(item => item.Is(Track.Descriptor)).All(item => item.Unpack<Track>().Layer is BoardLayer.BlFCu or BoardLayer.BlBCu));
        CollectionAssert.AreEqual(first.Candidate.Items.Select(item => item.ToString()).ToArray(), replay.Candidate!.Items.Select(item => item.ToString()).ToArray());
        Assert.AreEqual(first.PlanId, replay.PlanId);
    }

    [TestMethod]
    public void ThroughViaMustSpanThePhysicalCopperStack()
    {
        var enabled = new[] { BoardLayer.BlFCu, BoardLayer.BlIn1Cu, BoardLayer.BlIn2Cu, BoardLayer.BlBCu }.ToImmutableArray();
        var request = Request(enabled);
        request.Vias[0].EndLayer = "In1.Cu";
        AutomationException error = Assert.ThrowsExactly<AutomationException>(() => MultilayerRoutePlanner.Validate(request, enabled));
        Assert.AreEqual("invalid_multilayer_via", error.Code);
    }

    private static MultilayerRoutePlanner.MultilayerRouteRequest Request(ImmutableArray<BoardLayer> enabled)
        => new()
        {
            NetName = "SIGNAL",
            Start = new() { XNm = 0, YNm = 0 },
            End = new() { XNm = 4_000_000, YNm = 0 },
            StartLayer = "F.Cu",
            EndLayer = "F.Cu",
            Layers = enabled.Select(MultilayerRoutePlanner.LayerName).ToArray(),
            WidthNm = 100_000,
            ClearanceNm = 0,
            GridNm = 1_000_000,
            Bounds = new() { MinXNm = 0, MinYNm = -1_000_000, MaxXNm = 4_000_000, MaxYNm = 1_000_000 },
            MaxNodes = 10_000,
            MaxVias = 4,
            Vias =
            [
                new() { Type = "through", StartLayer = "F.Cu", EndLayer = "B.Cu", DiameterNm = 400_000, DrillNm = 200_000, CostNm = 500_000 }
            ]
        };

    private static Zone RuleArea(string id, bool keepTracks = false, bool keepVias = false,
        bool keepFootprints = false, bool keepCopper = false) => new()
    {
        Id = new() { Value = id }, Type = ZoneType.ZtRuleArea, Layers = { BoardLayer.BlFCu },
        Outline = new PolySet
        {
            Polygons =
            {
                new PolygonWithHoles
                {
                    Outline = new PolyLine { Closed = true, Nodes =
                    {
                        new PolyLineNode { Point = new() { XNm = 1_500_000, YNm = -500_000 } },
                        new PolyLineNode { Point = new() { XNm = 2_500_000, YNm = -500_000 } },
                        new PolyLineNode { Point = new() { XNm = 2_500_000, YNm = 500_000 } },
                        new PolyLineNode { Point = new() { XNm = 1_500_000, YNm = 500_000 } }
                    } }
                }
            }
        },
        RuleAreaSettings = new RuleAreaSettings
        {
            KeepoutTracks = keepTracks, KeepoutVias = keepVias,
            KeepoutFootprints = keepFootprints, KeepoutCopper = keepCopper
        }
    };
}
