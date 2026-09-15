using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicOrientationTests
{
    [TestMethod]
    public void AllEncodingsAndPairsHaveExactDeterministicShortestSequences()
    {
        var placements = (from angle in new[] { 0, 90, 180, 270 }
            from x in new[] { false, true } from y in new[] { false, true }
            select new SymbolPlacement(12, 34, angle, x, y, false)).ToArray();
        Assert.AreEqual(8, placements.Select(SchematicOrientation.Geometry).Distinct().Count());
        foreach (var from in placements)
        foreach (var to in placements)
        {
            var steps = SchematicOrientation.Plan(from, to);
            CollectionAssert.AreEqual(steps.ToArray(), SchematicOrientation.Plan(from, to).ToArray());
            Assert.IsTrue(steps.Count <= 2);
            var actual = Point(from);
            foreach (var step in steps) actual = Apply(actual, step);
            Assert.AreEqual(Point(to), actual);
            Assert.AreEqual(Point(from) == Point(to), SchematicOrientation.Equivalent(from, to));
            if (steps.Count == 2)
                foreach (var single in new[] { SchematicConnectedTransformKind.SctRotateClockwise,
                    SchematicConnectedTransformKind.SctRotateCounterclockwise, SchematicConnectedTransformKind.SctMirrorLeftRight,
                    SchematicConnectedTransformKind.SctMirrorUpDown })
                    Assert.AreNotEqual(Point(to), Apply(Point(from), single));
        }
        var original = placements[0];
        Assert.IsFalse(SchematicOrientation.Equivalent(original, original with { XMillimeters = 13 }));
        Assert.IsFalse(SchematicOrientation.Equivalent(original, original with { Locked = true }));
        Assert.IsFalse(SchematicOrientation.Equivalent(null, original));
        Assert.IsTrue(SchematicOrientation.Equivalent(null, null));
    }

    // Unequal, nonzero coordinates distinguish every signed-axis orientation.
    private static (int X, int Y) Point(SymbolPlacement placement)
    {
        var point = placement.RotationDegrees switch { 0 => (2, 5), 90 => (5, -2), 180 => (-2, -5), _ => (-5, 2) };
        return (placement.MirrorY ? -point.Item1 : point.Item1, placement.MirrorX ? -point.Item2 : point.Item2);
    }
    private static (int X, int Y) Apply((int X, int Y) point, SchematicConnectedTransformKind operation) => operation switch
    {
        SchematicConnectedTransformKind.SctRotateClockwise => (-point.Y, point.X),
        SchematicConnectedTransformKind.SctRotateCounterclockwise => (point.Y, -point.X),
        SchematicConnectedTransformKind.SctMirrorLeftRight => (-point.X, point.Y),
        _ => (point.X, -point.Y)
    };
}
