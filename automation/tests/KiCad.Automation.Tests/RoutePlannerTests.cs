using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RoutePlannerTests
{
    [TestMethod]
    public void DirectRouteIsDeterministicAndOrthogonal()
    {
        var route = RoutePlanner.Plan(new(0, 0), new(1000, 500), 100, 50, []);

        Assert.AreEqual("horizontal-then-vertical", route.Strategy);
        Assert.AreEqual(1500, route.TotalLengthNm);
        CollectionAssert.AreEqual(new[] { new RouteSegment(new(0, 0), new(1000, 0)), new RouteSegment(new(1000, 0), new(1000, 500)) }, route.Segments.ToArray());
    }

    [TestMethod]
    public void ObstacleProducesStableDetour()
    {
        var route = RoutePlanner.Plan(new(0, 0), new(1000, 0), 100, 50,
            [new RouteObstacle(400, -100, 200, 200)]);

        Assert.AreEqual("detour-bottom", route.Strategy);
        Assert.IsTrue(route.Segments.Count >= 3);
        Assert.IsGreaterThan(1000, route.TotalLengthNm);
    }

    [TestMethod]
    public void InvalidDimensionsAreRejected()
    {
        var error = Assert.ThrowsExactly<AutomationException>(() => RoutePlanner.Plan(new(0, 0), new(1, 1), 0, 0, []));
        Assert.AreEqual("invalid_route_width", error.Code);
    }
}
