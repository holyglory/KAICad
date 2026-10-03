using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbRouteOptimizationTests
{
    [TestMethod]
    public void MergesOnlyDegreeTwoCollinearTrackChains()
    {
        DocumentSpecifier document = Document();
        var candidate = new CreateItems { Header = new() { Document = document } };
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000001", 0, 0, 10, 0)));
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000002", 10, 0, 20, 0)));
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000003", 20, 0, 30, 0)));
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000004", 10, 0, 10, 10)));
        var plan = PcbRouteOptimizationTools.BuildPlan(candidate, new() { Nets = ["N"], MaxMerges = 8 });

        Assert.AreEqual(1, plan.MergedCount, "The branch at (10,0) must prevent a merge there; the straight chain at (20,0) is safe.");
        CollectionAssert.AreEqual(new[] { "00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0000-000000000003" }, plan.RemovedItemIds.ToArray());
        Track merged = plan.Create.Items.Single(item => item.Is(Track.Descriptor)).Unpack<Track>();
        Assert.AreEqual(10_000, merged.Start.XNm);
        Assert.AreEqual(30_000, merged.End.XNm);
        Assert.AreEqual(3, plan.PreservedItemCount);
    }

    [TestMethod]
    public void LeavesDifferentNetsAndWidthsUntouched()
    {
        var candidate = new CreateItems { Header = new() { Document = Document() } };
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000011", 0, 0, 10, 0, "A", 100)));
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000012", 10, 0, 20, 0, "A", 200)));
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000013", 10, 0, 20, 0, "B", 100)));
        var plan = PcbRouteOptimizationTools.BuildPlan(candidate, new() { Nets = ["A"], MaxMerges = 8 });
        Assert.AreEqual(0, plan.MergedCount);
        Assert.AreEqual(0, plan.Delete.ItemIds.Count);
        Assert.AreEqual(3, plan.PreservedItemCount);
    }

    [TestMethod]
    public void RouteTuningMeasuresPhysicalSegmentLength()
    {
        var points = new[]
        {
            new Vector2 { XNm = 0, YNm = 0 },
            new Vector2 { XNm = 3, YNm = 4 }
        };

        Assert.AreEqual(5, PcbRouteTuningTools.PathLength(points));
    }

    private static Track MakeTrack(string id, long x1, long y1, long x2, long y2, string net = "N", long width = 100)
        => new()
        {
            Id = new() { Value = id }, Start = new() { XNm = x1 * 1000, YNm = y1 * 1000 },
            End = new() { XNm = x2 * 1000, YNm = y2 * 1000 },
            Width = new() { ValueNm = width * 1000 }, Layer = BoardLayer.BlFCu, Net = new() { Name = net }
        };

    private static DocumentSpecifier Document() => new()
    {
        Type = DocumentType.DoctypePcb, BoardFilename = "optimization.kicad_pcb",
        Project = new() { Name = "optimization", Path = "/tmp/kicad-optimization" }
    };
}
