using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
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
    public void ApplyCandidateAcceptsCompleteMultiSegmentTrackGeometry()
    {
        var document = new DocumentSpecifier { Type = DocumentType.DoctypePcb, BoardFilename = "pair.kicad_pcb",
            Project = new() { Name = "pair", Path = Path.GetTempPath() } };
        var candidate = new CreateItems { Header = new() { Document = document } };
        candidate.Items.Add(Google.Protobuf.WellKnownTypes.Any.Pack(
            Track("00000000-0000-0000-0000-000000000031", "USB_D_P", BoardLayer.BlFCu, 0, 1_000_000)));
        candidate.Items.Add(Google.Protobuf.WellKnownTypes.Any.Pack(
            Track("00000000-0000-0000-0000-000000000032", "USB_D_P", BoardLayer.BlFCu, 0, 2_000_000, startX: 1_000_000)));
        candidate.Items.Add(Google.Protobuf.WellKnownTypes.Any.Pack(
            Track("00000000-0000-0000-0000-000000000033", "USB_D_N", BoardLayer.BlFCu, 500_000, 1_000_000)));
        candidate.Items.Add(Google.Protobuf.WellKnownTypes.Any.Pack(
            Track("00000000-0000-0000-0000-000000000034", "USB_D_N", BoardLayer.BlFCu, 500_000, 2_000_000, startX: 1_000_000)));

        PcbDifferentialPairTools.ValidateApplyCandidate("USB_D_P", "USB_D_N", candidate);
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
            WidthNm = request.WidthNm, MinimumGapNm = request.GapNm, TargetGapNm = request.GapNm,
            MaxSkewNm = 1, ToleranceNm = 0
        };
        var candidate = PcbDifferentialPairTools.BuildCandidate(document, BoardLayer.BlFCu, request,
            new() { XNm = 10_000_000, YNm = 19_835_000 }, new() { XNm = 30_000_000, YNm = 19_835_000 },
            new() { XNm = 10_000_000, YNm = 20_165_000 }, new() { XNm = 30_000_000, YNm = 20_165_000 });

        var result = PcbDifferentialPairTools.ValidateGeometry(validation, candidate.Items);

        Assert.IsTrue(result.Compliant);
        Assert.AreEqual(20_000_000d, result.Positive.LengthNm!.Value, 0.001);
        Assert.AreEqual(20_000_000d, result.Negative.LengthNm!.Value, 0.001);
        Assert.AreEqual(0d, result.SkewNm!.Value, 0.001);
        Assert.AreEqual(330_000d, result.CenterlineSpacingNm!.Value, 0.001);
        Assert.AreEqual(180_000d, result.CopperEdgeGapNm!.Value, 0.001);
        Assert.AreEqual(0d, result.MaximumParallelError, 0.000_000_001);
        Assert.AreEqual(20_000_000d, result.LongitudinalOverlapNm!.Value, 0.001);
        Assert.AreEqual(0d, result.LongitudinalEndpointDeltaNm!.Value, 0.001);
        Assert.IsEmpty(result.Violations);
    }

    [TestMethod]
    public void ValidationSupportsMatchedConnectedMultiSegmentRoutes()
    {
        var validation = new PcbDifferentialPairTools.PairValidationRequest
        {
            PositiveNet = "USB_D_P", NegativeNet = "USB_D_N", WidthNm = 150_000,
            MinimumGapNm = 350_000, TargetGapNm = 350_000, MaxSkewNm = 0, ToleranceNm = 0
        };
        var items = new[]
        {
            Track("00000000-0000-0000-0000-000000000041", "USB_D_P", BoardLayer.BlFCu, 0, 1_000_000),
            Track("00000000-0000-0000-0000-000000000042", "USB_D_P", BoardLayer.BlFCu, 0, 2_000_000, startX: 1_000_000),
            Track("00000000-0000-0000-0000-000000000043", "USB_D_N", BoardLayer.BlFCu, 500_000, 1_000_000),
            Track("00000000-0000-0000-0000-000000000044", "USB_D_N", BoardLayer.BlFCu, 500_000, 2_000_000, startX: 1_000_000)
        }.Select(Google.Protobuf.WellKnownTypes.Any.Pack);

        var result = PcbDifferentialPairTools.ValidateGeometry(validation, items);

        Assert.IsTrue(result.Compliant);
        Assert.IsTrue(result.SupportedScope);
        Assert.AreEqual(2, result.MatchedSegmentCount);
        Assert.AreEqual(2_000_000d, result.Positive.LengthNm!.Value, 0.001);
        Assert.AreEqual(2_000_000d, result.Negative.LengthNm!.Value, 0.001);
        Assert.AreEqual(0d, result.SkewNm!.Value, 0.001);
        Assert.AreEqual(500_000d, result.CenterlineSpacingNm!.Value, 0.001);
        Assert.AreEqual(350_000d, result.CopperEdgeGapNm!.Value, 0.001);
        Assert.AreEqual(2_000_000d, result.LongitudinalOverlapNm!.Value, 0.001);
        Assert.AreEqual(0d, result.LongitudinalEndpointDeltaNm!.Value, 0.001);
        Assert.AreEqual(0d, result.MaximumParallelError, 0.000_000_001);
        Assert.IsEmpty(result.Violations);
    }

    [TestMethod]
    public void ValidationReportsWorstMultiSegmentSpacingAndParallelismViolations()
    {
        var validation = new PcbDifferentialPairTools.PairValidationRequest
        {
            PositiveNet = "USB_D_P", NegativeNet = "USB_D_N", WidthNm = 150_000,
            MinimumGapNm = 350_000, TargetGapNm = 350_000, MaxSkewNm = 1, ToleranceNm = 0,
            ParallelTolerance = 1e-9
        };
        var items = new[]
        {
            Track("00000000-0000-0000-0000-000000000051", "USB_D_P", BoardLayer.BlFCu, 0, 1_000_000),
            Track("00000000-0000-0000-0000-000000000052", "USB_D_P", BoardLayer.BlFCu, 0, 2_000_000, startX: 1_000_000, endY: 200_000),
            Track("00000000-0000-0000-0000-000000000053", "USB_D_N", BoardLayer.BlFCu, 500_000, 1_000_000),
            Track("00000000-0000-0000-0000-000000000054", "USB_D_N", BoardLayer.BlFCu, 500_000, 2_000_000, startX: 1_000_000, endY: 800_000)
        }.Select(Google.Protobuf.WellKnownTypes.Any.Pack);

        var result = PcbDifferentialPairTools.ValidateGeometry(validation, items);
        var codes = result.Violations.Select(issue => issue.Code).ToHashSet(StringComparer.Ordinal);

        Assert.IsFalse(result.Compliant);
        Assert.IsTrue(result.SupportedScope);
        Assert.AreEqual(2, result.MatchedSegmentCount);
        Assert.IsTrue(result.CenterlineSpacingNm is > 0 and < 500_000);
        Assert.IsTrue(result.SkewNm > 1);
        Assert.IsTrue(result.MaximumParallelError > validation.ParallelTolerance);
        Assert.IsTrue(codes.Contains("copper_gap_below_minimum"));
        Assert.IsTrue(codes.Contains("copper_gap_target_mismatch"));
        Assert.IsTrue(codes.Contains("non_parallel"));
        Assert.IsTrue(codes.Contains("skew_exceeded"));
    }

    [TestMethod]
    public void ValidationReportsLayerSpacingAndSkewViolations()
    {
        var validation = new PcbDifferentialPairTools.PairValidationRequest
        {
            PositiveNet = "USB_D_P", NegativeNet = "USB_D_N", WidthNm = 150_000, MinimumGapNm = 180_000,
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
        Assert.IsTrue(codes.Contains("copper_gap_below_minimum"));
        Assert.IsTrue(codes.Contains("skew_exceeded"));
    }

    [TestMethod]
    public void ValidationMarksDisconnectedAndMultiSegmentGeometryIncomplete()
    {
        var validation = new PcbDifferentialPairTools.PairValidationRequest
        {
            PositiveNet = "USB_D_P", NegativeNet = "USB_D_N", WidthNm = 150_000, MinimumGapNm = 0
        };
        var items = new[]
        {
            Track("00000000-0000-0000-0000-000000000011", "USB_D_P", BoardLayer.BlFCu, 0, 1_000_000),
            Track("00000000-0000-0000-0000-000000000012", "USB_D_P", BoardLayer.BlFCu, 0, 2_000_000, startX: 2_000_000),
            Track("00000000-0000-0000-0000-000000000013", "USB_D_N", BoardLayer.BlFCu, 500_000, 3_000_000, startX: 2_000_000)
        }.Select(Google.Protobuf.WellKnownTypes.Any.Pack);

        var result = PcbDifferentialPairTools.ValidateGeometry(validation, items);
        var codes = result.Violations.Select(issue => issue.Code).ToHashSet(StringComparer.Ordinal);

        Assert.IsFalse(result.SupportedScope);
        Assert.IsFalse(result.Compliant);
        Assert.IsNull(result.Positive.LengthNm);
        Assert.IsTrue(result.Positive.PartialStraightLengthNm is > 0);
        Assert.IsTrue(codes.Contains("single_segment_scope"));
    }

    [TestMethod]
    public void ValidationRejectsExcessWidthAndArcAsUnsupportedInsteadOfPassing()
    {
        var validation = new PcbDifferentialPairTools.PairValidationRequest
        {
            PositiveNet = "USB_D_P", NegativeNet = "USB_D_N", WidthNm = 150_000,
            MinimumGapNm = 180_000, TargetGapNm = 180_000
        };
        var wideItems = new[]
        {
            Track("00000000-0000-0000-0000-000000000021", "USB_D_P", BoardLayer.BlFCu, 0, 10_000_000, widthNm: 500_000),
            Track("00000000-0000-0000-0000-000000000022", "USB_D_N", BoardLayer.BlFCu, 330_000, 10_000_000)
        }.Select(Google.Protobuf.WellKnownTypes.Any.Pack);
        var wideResult = PcbDifferentialPairTools.ValidateGeometry(validation, wideItems);
        var wideCodes = wideResult.Violations.Select(issue => issue.Code).ToHashSet(StringComparer.Ordinal);
        Assert.IsFalse(wideResult.Compliant);
        Assert.IsTrue(wideCodes.Contains("positive_width_mismatch"));
        Assert.IsTrue(wideCodes.Contains("copper_gap_below_minimum"));

        var arcItems = new Google.Protobuf.WellKnownTypes.Any[]
        {
            Google.Protobuf.WellKnownTypes.Any.Pack(Arc("00000000-0000-0000-0000-000000000023", "USB_D_P", BoardLayer.BlFCu)),
            Google.Protobuf.WellKnownTypes.Any.Pack(Track("00000000-0000-0000-0000-000000000024", "USB_D_N", BoardLayer.BlFCu, 330_000, 10_000_000))
        };
        var arcResult = PcbDifferentialPairTools.ValidateGeometry(validation, arcItems);
        Assert.IsFalse(arcResult.SupportedScope);
        Assert.IsFalse(arcResult.Compliant);
        Assert.IsNull(arcResult.Positive.LengthNm);
        Assert.IsTrue(arcResult.Violations.Any(issue => issue.Code == "arc_not_supported"));
    }

    private static Track Track(string id, string net, BoardLayer layer, long y, long endX,
        long widthNm = 150_000, long startX = 0, long? endY = null) => new()
    {
        Id = new() { Value = id },
        Start = new() { XNm = startX, YNm = y },
        End = new() { XNm = endX, YNm = endY ?? y },
        Width = new() { ValueNm = widthNm },
        Layer = layer,
        Net = new() { Name = net }
    };

    private static Arc Arc(string id, string net, BoardLayer layer) => new()
    {
        Id = new() { Value = id },
        Start = new() { XNm = 0, YNm = 0 },
        Mid = new() { XNm = 5_000_000, YNm = 1_000_000 },
        End = new() { XNm = 10_000_000, YNm = 0 },
        Width = new() { ValueNm = 150_000 },
        Layer = layer,
        Net = new() { Name = net }
    };
}
