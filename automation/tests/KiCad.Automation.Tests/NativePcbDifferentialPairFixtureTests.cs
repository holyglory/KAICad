using System.Globalization;
using System.Text.Json;

namespace KiCad.Automation.Tests;

[TestClass, TestCategory("NativePcbItemsFixture")]
public sealed class NativePcbDifferentialPairFixtureTests
{
    [TestMethod]
    public void PairAcceptanceBoardStartsWithExactlyTwoUnconnectedPadPairs()
    {
        string text = DifferentialPairRouteFixture.BoardText();
        Assert.AreEqual(text, DifferentialPairRouteFixture.BoardText(), "Fixture identities and geometry must be repeatable.");
        var board = PsuCpuSexpr.Parse(text);
        Assert.AreEqual("kicad_pcb", board.Head);
        CollectionAssert.AreEquivalent(new[] { "", DifferentialPairRouteFixture.PositiveNet, DifferentialPairRouteFixture.NegativeNet },
            board.Children("net").Select(net => net.Value(2)).ToArray());
        Assert.IsEmpty(board.Children("segment"));
        Assert.IsEmpty(board.Children("arc"));
        Assert.IsEmpty(board.Children("via"));
        Assert.IsEmpty(board.Children("zone"));
        var footprints = board.Children("footprint").ToArray();
        Assert.HasCount(4, footprints);
        var pads = footprints.SelectMany(footprint => footprint.Children("pad")).ToArray();
        Assert.HasCount(4, pads);
        foreach (string net in new[] { DifferentialPairRouteFixture.PositiveNet, DifferentialPairRouteFixture.NegativeNet })
            Assert.AreEqual(2, pads.Count(pad => pad.Child("net").Value(2) == net));
        CollectionAssert.AreEquivalent(new[]
        {
            DifferentialPairRouteFixture.PositiveStartPadId, DifferentialPairRouteFixture.NegativeStartPadId,
            DifferentialPairRouteFixture.PositiveEndPadId, DifferentialPairRouteFixture.NegativeEndPadId
        }, pads.Select(pad => pad.Child("uuid").Value(1)).ToArray());
        string[] ids = footprints.Select(footprint => footprint.Child("uuid").Value(1))
            .Concat(pads.Select(pad => pad.Child("uuid").Value(1)))
            .Append(board.Child("gr_rect").Child("uuid").Value(1)).ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(ids.All(id => Guid.TryParseExact(id, "D", out var parsed) && parsed != Guid.Empty
            && parsed.ToString("D") == id));
    }

    [TestMethod]
    public void PairFixtureUsesMatchingNativeRuleDimensionsAndCenterlineDestination()
    {
        var board = PsuCpuSexpr.Parse(DifferentialPairRouteFixture.BoardText());
        var endpoints = board.Children("footprint").ToDictionary(footprint => footprint.Child("pad").Child("uuid").Value(1),
            footprint => Point(footprint.Child("at")), StringComparer.Ordinal);
        var pStart = endpoints[DifferentialPairRouteFixture.PositiveStartPadId];
        var nStart = endpoints[DifferentialPairRouteFixture.NegativeStartPadId];
        var pEnd = endpoints[DifferentialPairRouteFixture.PositiveEndPadId];
        var nEnd = endpoints[DifferentialPairRouteFixture.NegativeEndPadId];
        long spacing = DifferentialPairRouteFixture.WidthNm + DifferentialPairRouteFixture.GapNm;
        Assert.AreEqual(pStart.X, nStart.X);
        Assert.IsTrue(nStart.Y - pStart.Y > spacing, "Source spacing must require real native fan-in segments.");
        Assert.AreEqual(DifferentialPairRouteFixture.EndXNm, pEnd.X);
        Assert.AreEqual(pEnd.X, nEnd.X);
        Assert.AreEqual(spacing, nEnd.Y - pEnd.Y);
        Assert.AreEqual(DifferentialPairRouteFixture.CenterYNm * 2, pEnd.Y + nEnd.Y);
        Assert.AreEqual(pStart.Y + nStart.Y, pEnd.Y + nEnd.Y);
        using var project = JsonDocument.Parse(DifferentialPairRouteFixture.ProjectText);
        var classes = project.RootElement.GetProperty("net_settings").GetProperty("classes").EnumerateArray().ToArray();
        Assert.HasCount(1, classes);
        Assert.AreEqual("Default", classes[0].GetProperty("name").GetString());
        Assert.AreEqual(DifferentialPairRouteFixture.WidthNm, Nm(classes[0].GetProperty("diff_pair_width").GetDecimal()));
        Assert.AreEqual(DifferentialPairRouteFixture.GapNm, Nm(classes[0].GetProperty("diff_pair_gap").GetDecimal()));
        var rules = project.RootElement.GetProperty("board").GetProperty("design_settings").GetProperty("rules");
        Assert.IsTrue(Nm(rules.GetProperty("min_track_width").GetDecimal()) <= DifferentialPairRouteFixture.WidthNm);
        Assert.IsTrue(Nm(rules.GetProperty("min_clearance").GetDecimal()) <= DifferentialPairRouteFixture.GapNm);
        Assert.IsFalse(project.RootElement.GetProperty("board").GetProperty("design_settings").TryGetProperty("rule_severities", out _),
            "The acceptance fixture must not disable DRC findings.");
        Assert.IsFalse(project.RootElement.GetProperty("board").GetProperty("design_settings").TryGetProperty("drc_exclusions", out _));

        static (long X, long Y) Point(PsuCpuSexpr point) =>
            (Nm(decimal.Parse(point.Value(1), CultureInfo.InvariantCulture)), Nm(decimal.Parse(point.Value(2), CultureInfo.InvariantCulture)));
        static long Nm(decimal millimeters) => decimal.ToInt64(millimeters * 1_000_000);
    }
}

public sealed partial class NativeSessionTests
{
    [TestMethod, TestCategory("NativePcbItemsFixture")]
    public void LegacyShoveBoardRetainsItsIndependentRouteObstacles()
    {
        var board = PsuCpuSexpr.Parse(CleanRouteBoard());
        Assert.HasCount(7, board.Children("footprint"));
        Assert.IsEmpty(board.Children("via"), "The shove journey adds its property-bearing via itself.");
        var segments = board.Children("segment").ToArray();
        Assert.HasCount(2, segments);
        Assert.IsTrue(segments.All(segment => segment.Child("net").Value(1) == "1"));
        var obstacle = segments.Single(segment => segment.Child("layer").Value(1) == "F.Cu");
        Assert.AreEqual("39", obstacle.Child("start").Value(1));
        Assert.AreEqual("42", obstacle.Child("start").Value(2));
        Assert.AreEqual("41", obstacle.Child("end").Value(1));
        Assert.AreEqual("42", obstacle.Child("end").Value(2));
        CollectionAssert.AreEquivalent(new[] { "MULTILAYER_ROUTE", "MULTILAYER_BARRIER" },
            board.Children("net").Where(net => net.Value(1) is "4" or "5").Select(net => net.Value(2)).ToArray());
    }
}
