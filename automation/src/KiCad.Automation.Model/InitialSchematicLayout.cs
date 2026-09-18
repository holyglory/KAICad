namespace KiCad.Automation.Model;

public sealed record InitialLayoutObstacle(Guid Id, PresentationBounds Bounds);
public sealed record InitialLayoutSheet(Guid Id, PresentationBounds AvailableBounds,
    IReadOnlyList<InitialLayoutObstacle> Obstacles);
// Bounds are measured relative to the symbol's anchor, in its requested orientation.
// Repeated native instances share one body with the union of their measured bounds.
public sealed record InitialLayoutBody(Guid Id, Guid SheetId, IReadOnlyList<Guid> SymbolOccurrences,
    PresentationBounds RelativeBounds, PresentationPoint? FixedAnchor = null, Guid? FunctionalGroupId = null);
public sealed record InitialLayoutPolicy(long GridNm, long ClearanceNm, long PageInsetNm);
public sealed record InitialLayoutPlacement(Guid BodyId, Guid SheetId, IReadOnlyList<Guid> SymbolOccurrences,
    PresentationPoint Anchor, PresentationBounds Bounds, bool Fixed);
public sealed record InitialLayoutIssue(string Code, Guid BodyId, Guid SheetId,
    IReadOnlyList<Guid> ObstacleIds);
public sealed record InitialLayoutCandidate(IReadOnlyList<InitialLayoutPlacement>? Placements,
    IReadOnlyList<InitialLayoutIssue> Issues)
{
    public bool CanPropose => Placements is not null && Issues.Count == 0;
    public bool RequiresVisualReview => true;
}

/// <summary>Deterministic initial placement over caller-measured native rectangles.
/// Does not infer symbol sizes, move existing geometry, wire a circuit or certify readability.
/// The native adapter must bind measurements to the same revision as the layout request.</summary>
public static class InitialSchematicLayout
{
    public static InitialLayoutCandidate Propose(IReadOnlyList<InitialLayoutSheet> sheets,
        IReadOnlyList<InitialLayoutBody> bodies, InitialLayoutPolicy policy, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try { return ProposeCore(sheets, bodies, policy, token); }
        catch (OverflowException) { throw Invalid("Measured geometry exceeds the supported coordinate range."); }
    }

    private static InitialLayoutCandidate ProposeCore(IReadOnlyList<InitialLayoutSheet> sheets,
        IReadOnlyList<InitialLayoutBody> bodies, InitialLayoutPolicy policy, CancellationToken token)
    {
        if (policy.GridNm <= 0 || policy.GridNm % 100 != 0 || policy.ClearanceNm < 0 || policy.PageInsetNm < 0)
            throw Invalid("Use a positive native-grid multiple of 100 nm and nonnegative clearance and inset.");
        var pages = new Dictionary<Guid, PresentationBounds>();
        var obstacles = new Dictionary<Guid, List<InitialLayoutObstacle>>();
        foreach (var sheet in sheets)
        {
            token.ThrowIfCancellationRequested();
            RequireBounds(sheet.AvailableBounds);
            if (sheet.Id == Guid.Empty || pages.ContainsKey(sheet.Id)) throw Invalid("Physical sheets need distinct nonempty identities.");
            var page = sheet.AvailableBounds;
            var available = new PresentationBounds(checked(page.LeftNm + policy.PageInsetNm), checked(page.TopNm + policy.PageInsetNm),
                checked(page.RightNm - policy.PageInsetNm), checked(page.BottomNm - policy.PageInsetNm));
            RequireBounds(available);
            pages.Add(sheet.Id, available);
            var ids = new HashSet<Guid>();
            foreach (var obstacle in sheet.Obstacles)
            {
                if (obstacle.Id == Guid.Empty || !ids.Add(obstacle.Id)) throw Invalid("Obstacles require exact distinct native identities per sheet.");
                RequireBounds(obstacle.Bounds);
            }
            obstacles.Add(sheet.Id, sheet.Obstacles.OrderBy(o => o.Id).ToList());
        }
        var physicalIds = new HashSet<(Guid, Guid)>();
        var occurrences = new HashSet<Guid>();
        foreach (var body in bodies)
        {
            token.ThrowIfCancellationRequested();
            RequireBounds(body.RelativeBounds);
            if (body.RelativeBounds.LeftNm == body.RelativeBounds.RightNm || body.RelativeBounds.TopNm == body.RelativeBounds.BottomNm)
                throw Invalid("A measured symbol must have positive width and height.");
            if (body.Id == Guid.Empty || !pages.ContainsKey(body.SheetId) || !physicalIds.Add((body.SheetId, body.Id))
                || body.FunctionalGroupId == Guid.Empty || body.SymbolOccurrences.Count == 0
                || body.SymbolOccurrences.Any(id => id == Guid.Empty || !occurrences.Add(id))
                || obstacles[body.SheetId].Any(o => o.Id == body.Id))
                throw Invalid("Bodies need exact physical/occurrence ownership without collisions with existing obstacles.");
            if (body.FixedAnchor is { } fixedAnchor && (fixedAnchor.XNm % 100 != 0 || fixedAnchor.YNm % 100 != 0))
                throw Invalid("Preserved anchors must be representable on KiCad's native 100 nm coordinate quantum.");
        }
        var result = new List<InitialLayoutPlacement>();
        var issues = new List<InitialLayoutIssue>();
        var groups = new Dictionary<(Guid Sheet, Guid Group), PresentationPoint>();
        // Fixed positions occupy space before free positions are considered.
        foreach (var body in bodies.OrderBy(b => b.FixedAnchor is null).ThenBy(b => b.SheetId)
                     .ThenBy(b => b.FunctionalGroupId ?? b.Id).ThenBy(b => b.Id))
        {
            token.ThrowIfCancellationRequested();
            var page = pages[body.SheetId]; var occupied = obstacles[body.SheetId];
            PresentationPoint? preferred = body.FunctionalGroupId is { } group && groups.TryGetValue((body.SheetId, group), out var center)
                ? center : null;
            var anchor = body.FixedAnchor ?? Find(body.RelativeBounds, page, occupied, policy, preferred, token);
            if (anchor is null)
            {
                issues.Add(new("no_free_region", body.Id, body.SheetId, occupied.Select(o => o.Id).Order().ToArray()));
                continue;
            }
            var bounds = Translate(body.RelativeBounds, anchor.Value);
            var collisions = occupied.Where(o => Intersects(bounds, o.Bounds, policy.ClearanceNm)).Select(o => o.Id).Order().ToArray();
            if (!page.Contains(bounds) || collisions.Length > 0)
            {
                issues.Add(new(!page.Contains(bounds) ? "pinned_page_overflow" : "pinned_obstacle_overlap", body.Id, body.SheetId, collisions));
                continue;
            }
            result.Add(new(body.Id, body.SheetId, body.SymbolOccurrences.Order().ToArray(), anchor.Value, bounds, body.FixedAnchor is not null));
            occupied.Add(new(body.Id, bounds));
            if (body.FunctionalGroupId is { } groupId) groups.TryAdd((body.SheetId, groupId), anchor.Value);
        }
        // A failed placement is not permission to apply a partial circuit layout.
        return issues.Count > 0 ? new(null, issues) : new(result.OrderBy(p => p.SheetId).ThenBy(p => p.BodyId).ToArray(), []);
    }

    private static PresentationPoint? Find(PresentationBounds body, PresentationBounds page,
        IReadOnlyList<InitialLayoutObstacle> occupied, InitialLayoutPolicy policy, PresentationPoint? preferred, CancellationToken token)
    {
        long grid = policy.GridNm, gap = policy.ClearanceNm;
        decimal minX = Ceiling((decimal)page.LeftNm - body.LeftNm, grid), maxX = Floor((decimal)page.RightNm - body.RightNm, grid);
        decimal minY = Ceiling((decimal)page.TopNm - body.TopNm, grid), maxY = Floor((decimal)page.BottomNm - body.BottomNm, grid);
        if (minX > maxX || minY > maxY) return null;
        var rows = new SortedSet<decimal> { minY };
        foreach (var obstacle in occupied)
        {
            decimal y = Ceiling((decimal)obstacle.Bounds.BottomNm + gap - body.TopNm, grid);
            if (y >= minY && y <= maxY) rows.Add(y);
        }
        if (preferred is { } center) rows.Add(Math.Clamp(Ceiling(center.YNm, grid), minY, maxY));
        PresentationPoint? best = null;
        (decimal Distance, decimal Y, decimal X)? bestScore = null;
        foreach (decimal y in rows)
        {
            token.ThrowIfCancellationRequested();
            decimal x = minX;
            var blocked = occupied.Where(o => y + body.BottomNm + gap > o.Bounds.TopNm
                    && y + body.TopNm - gap < o.Bounds.BottomNm)
                .OrderBy(o => o.Bounds.LeftNm).ThenBy(o => o.Bounds.RightNm).ThenBy(o => o.Id);
            foreach (var obstacle in blocked)
            {
                token.ThrowIfCancellationRequested();
                Offer(x, Math.Min(maxX, Floor((decimal)obstacle.Bounds.LeftNm - gap - body.RightNm, grid)), y);
                x = Math.Max(x, Ceiling((decimal)obstacle.Bounds.RightNm + gap - body.LeftNm, grid));
                if (x > maxX) break;
            }
            Offer(x, maxX, y);
            if (best is not null && preferred is null) return best;
        }
        return best;

        void Offer(decimal left, decimal right, decimal y)
        {
            if (left > right) return;
            decimal x = preferred is { } center ? Math.Clamp(Floor(center.XNm, grid), left, right) : left;
            decimal distance = preferred is { } p ? Math.Abs(x - p.XNm) + Math.Abs(y - p.YNm) : 0;
            var score = (distance, y, x);
            if (bestScore is null || score.CompareTo(bestScore.Value) < 0)
            {
                best = new(checked((long)x), checked((long)y)); bestScore = score;
            }
        }
    }

    private static decimal Ceiling(decimal value, long grid) => decimal.Ceiling(value / grid) * grid;
    private static decimal Floor(decimal value, long grid) => decimal.Floor(value / grid) * grid;
    private static bool Intersects(PresentationBounds a, PresentationBounds b, long gap) =>
        (decimal)a.LeftNm - gap < b.RightNm && (decimal)a.RightNm + gap > b.LeftNm
        && (decimal)a.TopNm - gap < b.BottomNm && (decimal)a.BottomNm + gap > b.TopNm;
    private static PresentationBounds Translate(PresentationBounds b, PresentationPoint p) =>
        new(checked(b.LeftNm + p.XNm), checked(b.TopNm + p.YNm), checked(b.RightNm + p.XNm), checked(b.BottomNm + p.YNm));
    private static void RequireBounds(PresentationBounds bounds)
    {
        if (bounds.LeftNm > bounds.RightNm || bounds.TopNm > bounds.BottomNm) throw Invalid("Measured rectangles must not be inverted.");
    }
    private static AutomationException Invalid(string message) => new("invalid_initial_layout", message);
}
