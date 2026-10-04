using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbDifferentialPairTests
{
    [TestMethod]
    public void ProposalKeepsMatchedLengthAndExplicitNetRoles()
    {
        var document = new DocumentSpecifier { Type = DocumentType.DoctypePcb, BoardFilename = "pair.kicad_pcb",
            Project = new() { Name = "pair", Path = Path.GetTempPath() } };
        var request = new PcbDifferentialPairTools.PairRequest
        {
            PositiveNet = "USB_D_P", NegativeNet = "USB_D_N", Layer = "F.Cu",
            WidthNm = 150_000, GapNm = 180_000,
            Start = new() { XNm = 10_000_000, YNm = 20_000_000 },
            End = new() { XNm = 30_000_000, YNm = 20_000_000 },
            Bounds = new() { MinXNm = 0, MinYNm = 0, MaxXNm = 40_000_000, MaxYNm = 40_000_000 }
        };
        var candidate = PcbDifferentialPairTools.BuildCandidate(document, BoardLayer.BlFCu, request,
            new() { XNm = 10_000_000, YNm = 19_835_000 }, new() { XNm = 30_000_000, YNm = 19_835_000 },
            new() { XNm = 10_000_000, YNm = 20_165_000 }, new() { XNm = 30_000_000, YNm = 20_165_000 });
        Assert.HasCount(2, candidate.Items);
        var tracks = candidate.Items.Select(item => item.Unpack<Track>()).ToArray();
        Assert.AreEqual("USB_D_P", tracks[0].Net.Name);
        Assert.AreEqual("USB_D_N", tracks[1].Net.Name);
        Assert.AreEqual(tracks[0].Start.XNm, tracks[1].Start.XNm);
        Assert.AreEqual(tracks[0].End.XNm, tracks[1].End.XNm);
        Assert.AreEqual(330_000, Math.Abs(tracks[0].Start.YNm - tracks[1].Start.YNm));
    }

    [TestMethod]
    public void ValidationReportsMatchedLengthSpacingAndParallelGeometry()
    {
        var document = new DocumentSpecifier { Type = DocumentType.DoctypePcb, BoardFilename = "pair.kicad_pcb",
            Project = new() { Name = "pair", Path = Path.GetTempPath() } };
        var request = new PcbDifferentialPairTools.PairRequest
        {
            PositiveNet = "USB_D_P", NegativeNet = "USB_D_N", Layer = "F.Cu",
            WidthNm = 150_000, GapNm = 180_000,
            Start = new() { XNm = 10_000_000, YNm = 20_000_000 },
            End = new() { XNm = 30_000_000, YNm = 20_000_000 },
            Bounds = new() { MinXNm = 0, MinYNm = 0, MaxXNm = 40_000_000, MaxYNm = 40_000_000 }
        };
        var validation = new PcbDifferentialPairTools.PairValidationRequest
        {
            PositiveNet = request.PositiveNet, NegativeNet = request.NegativeNet,
            WidthNm = request.WidthNm, GapNm = request.GapNm, MaxSkewNm = 1, ToleranceNm = 0
        };
        var candidate = PcbDifferentialPairTools.BuildCandidate(document, BoardLayer.BlFCu, request,
            new() { XNm = 10_000_000, YNm = 19_835_000 }, new() { XNm = 30_000_000, YNm = 19_835_000 },
            new() { XNm = 10_000_000, YNm = 20_165_000 }, new() { XNm = 30_000_000, YNm = 20_165_000 });

        var result = PcbDifferentialPairTools.ValidateGeometry(validation, candidate.Items);

        Assert.IsTrue(result.Compliant);
        Assert.AreEqual(20_000_000, result.Positive.LengthNm, 0.001);
        Assert.AreEqual(20_000_000, result.Negative.LengthNm, 0.001);
        Assert.AreEqual(0, result.SkewNm, 0.001);
        Assert.AreEqual(330_000, result.MinimumSpacingNm!.Value, 0.001);
        Assert.AreEqual(0, result.MaximumParallelError, 0.000_000_001);
        Assert.IsEmpty(result.Violations);
    }

    [TestMethod]
    public void ValidationReportsLayerSpacingAndSkewViolations()
    {
        var validation = new PcbDifferentialPairTools.PairValidationRequest
        {
            PositiveNet = "USB_D_P", NegativeNet = "USB_D_N", WidthNm = 150_000, GapNm = 180_000,
            MaxSkewNm = 100_000, ToleranceNm = 0
        };
        var items = new[]
        {
            Track("00000000-0000-0000-0000-000000000001", "USB_D_P", BoardLayer.BlFCu, 0, 10_000_000),
            Track("00000000-0000-0000-0000-000000000002", "USB_D_N", BoardLayer.BlBCu, 50_000, 8_000_000)
        }.Select(Google.Protobuf.WellKnownTypes.Any.Pack);

        var result = PcbDifferentialPairTools.ValidateGeometry(validation, items);
        var codes = result.Violations.Select(issue => issue.Code).ToHashSet(StringComparer.Ordinal);

        Assert.IsFalse(result.Compliant);
        Assert.IsTrue(codes.Contains("layer_mismatch"));
        Assert.IsTrue(codes.Contains("spacing_below_gap"));
        Assert.IsTrue(codes.Contains("skew_exceeded"));
    }

    private static Track Track(string id, string net, BoardLayer layer, long y, long endX) => new()
    {
        Id = new() { Value = id },
        Start = new() { XNm = 0, YNm = y },
        End = new() { XNm = endX, YNm = y },
        Width = new() { ValueNm = 150_000 },
        Layer = layer,
        Net = new() { Name = net }
    };
}
