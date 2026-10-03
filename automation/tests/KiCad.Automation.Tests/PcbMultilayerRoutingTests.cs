using System.Collections.Immutable;
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
    public void ThroughViasCrossAFullForeignWallAndReturnToTheFrontLayer()
    {
        var enabled = new[] { BoardLayer.BlFCu, BoardLayer.BlIn1Cu, BoardLayer.BlBCu }.ToImmutableArray();
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
                && via.PadStack.Layers.SequenceEqual(enabled)
                && via.PadStack.Drill.StartLayer == BoardLayer.BlFCu
                && via.PadStack.Drill.EndLayer == BoardLayer.BlBCu;
        }));
        Assert.IsTrue(first.Candidate.Items.Where(item => item.Is(Track.Descriptor)).All(item => item.Unpack<Track>().Layer is BoardLayer.BlFCu or BoardLayer.BlBCu));
        CollectionAssert.AreEqual(first.Candidate.Items.Select(item => item.ToString()).ToArray(), replay.Candidate!.Items.Select(item => item.ToString()).ToArray());
        Assert.AreEqual(first.PlanId, replay.PlanId);
    }

    [TestMethod]
    public void ThroughViaMustSpanThePhysicalCopperStack()
    {
        var enabled = new[] { BoardLayer.BlFCu, BoardLayer.BlIn1Cu, BoardLayer.BlBCu }.ToImmutableArray();
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
}
