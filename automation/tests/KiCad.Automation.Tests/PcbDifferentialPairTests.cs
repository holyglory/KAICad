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
}
