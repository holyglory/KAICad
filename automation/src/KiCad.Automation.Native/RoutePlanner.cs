using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public readonly record struct RoutePoint(long XNm, long YNm);
public readonly record struct RouteObstacle(long XNm, long YNm, long WidthNm, long HeightNm);
public readonly record struct RouteSegment(RoutePoint Start, RoutePoint End);
public sealed record PlannedRoute(IReadOnlyList<RouteSegment> Segments, long TotalLengthNm,
    long ClearanceNm, string Strategy);

/// <summary>Deterministic, layer-local orthogonal route planner. Native DRC remains the final authority.</summary>
public static class RoutePlanner
{
    public static PlannedRoute Plan(RoutePoint start, RoutePoint end, long widthNm, long clearanceNm,
        IReadOnlyList<RouteObstacle> obstacles)
    {
        if (widthNm <= 0) throw new AutomationException("invalid_route_width", "Route width must be positive.");
        if (clearanceNm < 0) throw new AutomationException("invalid_route_clearance", "Route clearance cannot be negative.");
        if (start == end) throw new AutomationException("invalid_route_endpoints", "Route endpoints must differ.");

        long margin = clearanceNm + (widthNm + 1) / 2;
        bool Clear(RoutePoint a, RoutePoint b) => a.XNm == b.XNm || a.YNm == b.YNm
            ? !obstacles.Any(o => Intersects(a, b, Expand(o, margin))) : false;
        var candidates = new List<(string Strategy, IReadOnlyList<RoutePoint> Points)>();
        candidates.Add(("direct", [start, end]));
        candidates.Add(("horizontal-then-vertical", [start, new(end.XNm, start.YNm), end]));
        candidates.Add(("vertical-then-horizontal", [start, new(start.XNm, end.YNm), end]));

        foreach (var obstacle in obstacles.OrderBy(o => o.XNm).ThenBy(o => o.YNm))
        {
            var box = Expand(obstacle, margin);
            candidates.Add(("detour-left", [start, new(box.XNm - 1, start.YNm), new(box.XNm - 1, end.YNm), end]));
            candidates.Add(("detour-right", [start, new(box.XNm + box.WidthNm + 1, start.YNm), new(box.XNm + box.WidthNm + 1, end.YNm), end]));
            candidates.Add(("detour-bottom", [start, new(start.XNm, box.YNm - 1), new(end.XNm, box.YNm - 1), end]));
            candidates.Add(("detour-top", [start, new(start.XNm, box.YNm + box.HeightNm + 1), new(end.XNm, box.YNm + box.HeightNm + 1), end]));
        }

        var valid = candidates
            .Select(c => (c.Strategy, Points: Simplify(c.Points), Valid: Simplify(c.Points).Zip(Simplify(c.Points).Skip(1), Clear).All(x => x)))
            .Where(c => c.Valid)
            .Select(c => (c.Strategy, c.Points, Length: Length(c.Points)))
            .OrderBy(c => c.Length).ThenBy(c => c.Strategy, StringComparer.Ordinal)
            .FirstOrDefault();
        if (valid.Points is null)
            throw new AutomationException("route_blocked", "No deterministic orthogonal route clears the supplied obstacles.");
        var segments = valid.Points.Zip(valid.Points.Skip(1), (a, b) => new RouteSegment(a, b)).ToArray();
        return new(segments, valid.Length, clearanceNm, valid.Strategy);
    }

    private static RouteObstacle Expand(RouteObstacle o, long margin) =>
        new(o.XNm - margin, o.YNm - margin, checked(o.WidthNm + 2 * margin), checked(o.HeightNm + 2 * margin));

    private static bool Intersects(RoutePoint a, RoutePoint b, RouteObstacle o)
    {
        if (a.XNm != b.XNm && a.YNm != b.YNm) return true;
        if (a.XNm == b.XNm)
            return a.XNm >= o.XNm && a.XNm <= o.XNm + o.WidthNm
                && Math.Max(Math.Min(a.YNm, b.YNm), o.YNm) <= Math.Min(Math.Max(a.YNm, b.YNm), o.YNm + o.HeightNm);
        return a.YNm >= o.YNm && a.YNm <= o.YNm + o.HeightNm
            && Math.Max(Math.Min(a.XNm, b.XNm), o.XNm) <= Math.Min(Math.Max(a.XNm, b.XNm), o.XNm + o.WidthNm);
    }

    private static IReadOnlyList<RoutePoint> Simplify(IReadOnlyList<RoutePoint> points)
    {
        var result = new List<RoutePoint>();
        foreach (var point in points)
        {
            if (result.Count > 0 && result[^1] == point) continue;
            if (result.Count >= 2)
            {
                var a = result[^2]; var b = result[^1];
                if ((a.XNm == b.XNm && b.XNm == point.XNm) || (a.YNm == b.YNm && b.YNm == point.YNm)) result[^1] = point;
                else result.Add(point);
            }
            else result.Add(point);
        }
        return result;
    }

    private static long Length(IReadOnlyList<RoutePoint> points) =>
        points.Zip(points.Skip(1), (a, b) => Math.Abs(a.XNm - b.XNm) + Math.Abs(a.YNm - b.YNm)).Sum();
}
