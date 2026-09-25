using System.Numerics;

namespace KiCad.Automation.Model;

public readonly record struct PresentationPoint(long XNm, long YNm);
public sealed record PresentationBounds(long LeftNm, long TopNm, long RightNm, long BottomNm)
{
    public bool Contains(PresentationBounds other) => LeftNm <= other.LeftNm && TopNm <= other.TopNm
        && RightNm >= other.RightNm && BottomNm >= other.BottomNm;
    internal void Validate()
    {
        if (LeftNm > RightNm || TopNm > BottomNm) throw PresentationVerifier.Invalid("Inverted rendered bounds.");
    }
}

public enum PresentationObjectKind { Graphic, Image, Text, ReferenceDesignator }
// What an object is on its sheet, which decides the overlap rules that apply to it. Other objects
// (graphics, wires, notes, images, sheet pins) are checked for page fit and text rules only.
public enum PresentationRole { Other, Symbol, Sheet, Label, Field, SheetPin, Text }
// FullBounds are measured before clipping, not the already-cropped visible box. For a field they
// are its painted glyphs. Visible incorporates native field, unit, layer and rendering visibility.
// ReadingAngleDegrees is the painted reading direction, counter-clockwise from left to right.
public sealed record PresentationObject(Guid Id, PresentationObjectKind Kind, PresentationBounds FullBounds,
    bool Visible, decimal? TextHeightMm = null, string? Text = null, PresentationBounds? ClipBounds = null,
    PresentationBounds? TextBounds = null, PresentationRole Role = PresentationRole.Other, Guid? OwnerId = null,
    decimal? ReadingAngleDegrees = null);
// SignalKey is scoped to this document revision; it is not an XML net identity.
public sealed record PresentationWire(Guid Id, string SignalKey, PresentationPoint Start, PresentationPoint End);
// Name is the human-readable sheet-instance path (for example "/CPU/CPU_POWER/").
public sealed record PresentationSheet(IReadOnlyList<Guid> SheetPath, PresentationBounds PageBounds,
    IReadOnlyList<PresentationObject> Objects, IReadOnlyList<Guid> RequiredDesignators,
    IReadOnlyList<PresentationWire> Wires, IReadOnlyList<PresentationPoint> Junctions, string? Name = null);
public sealed record PresentationSnapshot(Guid DocumentId, DocumentRevision Revision, bool CoverageComplete,
    IReadOnlyList<PresentationSheet> Sheets);
// Font limits are caller-selected presentation policy, not an electrical standard. Overlap tolerance
// absorbs the pen-width inflation of native bounding boxes where objects are meant to touch (a label
// or power symbol on a pin end); it must stay below half the 1.27 mm schematic grid, so an overlap of
// half a grid step or more is always reported.
public sealed record PresentationPolicy(decimal MinimumTextHeightMm, decimal MaximumTextHeightMm,
    int MaximumCrossingsPerSignal = 2, decimal OverlapToleranceMm = PresentationPolicy.DefaultOverlapToleranceMm)
{
    public const decimal DefaultOverlapToleranceMm = 0.5m;
    // Half the 1.27 mm schematic grid; the tolerance must be less than this.
    public const decimal OverlapToleranceLimitMm = 0.635m;
    // Text reads left to right (0) up to bottom to top (90); beyond that it reads upside down or top to bottom.
    public const decimal MaximumReadingAngleDegrees = 90m;
}
public enum PresentationSeverity { Warning, Error, Unavailable }
// One localized repair region; crossing findings may contain several regions
// rather than requiring a close-up of the whole signal's bounding box.
public sealed record PresentationLocation(PresentationBounds Bounds, IReadOnlyList<Guid> ObjectIds);
// Measured and Limit share Unit: "mm" for text size, overflow, clipping, overlap depth and shared wire length,
// "degrees" for reading direction, and "count" for crossings, joined signals and visible, annotated or present
// reference designators (measured 0 against a required 1). Only the two Unavailable notices, which say a
// measurement could not be made, carry no measurement. Revision is the document revision the finding was
// measured at; SheetName is the human-readable sheet-instance path.
public sealed record PresentationFinding(string Rule, PresentationSeverity Severity, string SheetPath,
    IReadOnlyList<Guid> ObjectIds, PresentationBounds? Bounds, decimal? Measured, decimal? Limit, string Message,
    string? SignalKey = null, IReadOnlyList<PresentationLocation>? Locations = null, DocumentRevision? Revision = null,
    string? SheetName = null, string? Unit = null);
public static class PresentationUnits
{
    public const string Millimetres = "mm";
    public const string Degrees = "degrees";
    public const string Count = "count";
}
// Every sheet instance the report covers, so a missing sheet is visible rather than silently clean.
public sealed record PresentationSheetCoverage(string SheetPath, string? SheetName, int Objects, int Wires);
// Policy is the policy the report was measured against, so a report without findings still says which text
// limits, crossing limit and overlap tolerance it applied.
public sealed record PresentationReport(Guid DocumentId, DocumentRevision Revision, bool CoverageComplete,
    IReadOnlyList<PresentationFinding> Findings, IReadOnlyList<PresentationSheetCoverage>? Sheets = null,
    PresentationPolicy? Policy = null)
{
    public bool Clear => CoverageComplete && Findings.Count == 0;
}

/// <summary>Deterministic checks over native rendering facts. This module does
/// not manufacture bounds from screenshots or claim the native extractor exists.</summary>
public static class PresentationVerifier
{
    /// <summary>Refuse a policy that cannot be applied: text limits must be positive and ordered, the crossing limit
    /// nonnegative, and the overlap tolerance at least 0 and below <see cref="PresentationPolicy.OverlapToleranceLimitMm"/>
    /// (half the schematic grid), so no tolerance can switch the overlap rules off.</summary>
    public static void RequireValid(PresentationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MinimumTextHeightMm <= 0 || policy.MaximumTextHeightMm < policy.MinimumTextHeightMm)
            throw Invalid("Invalid presentation policy: the minimum text height must be positive and not above the maximum.");
        if (policy.MaximumCrossingsPerSignal < 0)
            throw Invalid("Invalid presentation policy: the crossing limit must not be negative.");
        if (policy.OverlapToleranceMm < 0 || policy.OverlapToleranceMm >= PresentationPolicy.OverlapToleranceLimitMm)
            throw Invalid($"Invalid presentation policy: the overlap tolerance must be at least 0 mm and below "
                + $"{PresentationPolicy.OverlapToleranceLimitMm} mm (half the 1.27 mm schematic grid), not {policy.OverlapToleranceMm} mm.");
    }

    public static PresentationReport Verify(PresentationSnapshot snapshot, PresentationPolicy policy)
    {
        if (snapshot.DocumentId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.Revision.Epoch))
            throw Invalid("An identified document revision is required.");
        RequireValid(policy);
        var findings = new List<PresentationFinding>();
        var coverage = new List<PresentationSheetCoverage>();
        if (!snapshot.CoverageComplete)
            findings.Add(new("coverage_incomplete", PresentationSeverity.Unavailable, "", [], null, null, null,
                "Native rendering facts are incomplete; absence of findings cannot establish a clear diagram.",
                Revision: snapshot.Revision));
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sheet in snapshot.Sheets)
        {
            if (sheet.SheetPath.Count == 0 || sheet.SheetPath.Any(id => id == Guid.Empty))
                throw Invalid("An exact sheet-instance path is required.");
            string path = string.Join('/', sheet.SheetPath.Select(id => id.ToString("D")));
            if (!paths.Add(path)) throw Invalid("Duplicate sheet instance.");
            sheet.PageBounds.Validate();
            coverage.Add(new(path, sheet.Name, sheet.Objects.Count, sheet.Wires.Count));
            var ids = new HashSet<Guid>();
            foreach (var item in sheet.Objects)
            {
                if (item.Id == Guid.Empty || !ids.Add(item.Id) || !Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.Role)
                    || (item.Role == PresentationRole.Field && item.OwnerId is null) || item.OwnerId == Guid.Empty)
                    throw Invalid("Invalid or duplicate rendered object identity, role or owner.");
                item.FullBounds.Validate(); item.ClipBounds?.Validate(); item.TextBounds?.Validate();
                if (item.Visible && !sheet.PageBounds.Contains(item.FullBounds))
                    Add("page_overflow", PresentationSeverity.Error, [item.Id], item.FullBounds,
                        Millimetres(Outside(sheet.PageBounds, item.FullBounds)), 0m, PresentationUnits.Millimetres,
                        "Rendered content extends beyond this sheet's page.");
                bool clipped = item.ClipBounds is not null && !item.ClipBounds.Contains(item.FullBounds);
                if (item.Visible && item.Kind == PresentationObjectKind.Image
                    && (clipped || !sheet.PageBounds.Contains(item.FullBounds)))
                    Add("image_cropped", PresentationSeverity.Error, [item.Id], item.FullBounds,
                        Millimetres(Math.Max(Outside(sheet.PageBounds, item.FullBounds),
                            item.ClipBounds is null ? 0 : Outside(item.ClipBounds, item.FullBounds))), 0m, PresentationUnits.Millimetres,
                        "Part of the image is outside its clip region or page.");
                if (item.Visible && item.Kind is PresentationObjectKind.Text or PresentationObjectKind.ReferenceDesignator)
                {
                    if (item.TextBounds is PresentationBounds textBounds && !string.IsNullOrWhiteSpace(item.Text))
                    {
                        if (!item.FullBounds.Contains(textBounds))
                            Add("text_container_overflow", PresentationSeverity.Warning, [item.Id], textBounds,
                                Millimetres(Outside(item.FullBounds, textBounds)), 0m, PresentationUnits.Millimetres,
                                "Native text bounds extend beyond their container; enlarge the box or adjust the text layout.");
                        if (!sheet.PageBounds.Contains(textBounds) && sheet.PageBounds.Contains(item.FullBounds))
                            Add("page_overflow", PresentationSeverity.Error, [item.Id], textBounds,
                                Millimetres(Outside(sheet.PageBounds, textBounds)), 0m, PresentationUnits.Millimetres,
                                "Text extends beyond this sheet's page even though its container fits.");
                    }
                    if (item.TextHeightMm is not decimal height || height <= 0)
                        Add("text_metrics_missing", PresentationSeverity.Unavailable, [item.Id], item.FullBounds, null, null, null,
                            "Effective native text height was not supplied.");
                    else if (height < policy.MinimumTextHeightMm || height > policy.MaximumTextHeightMm)
                        Add("text_size", PresentationSeverity.Warning, [item.Id], item.FullBounds, height,
                            height < policy.MinimumTextHeightMm ? policy.MinimumTextHeightMm : policy.MaximumTextHeightMm,
                            PresentationUnits.Millimetres, "Text height is outside the selected presentation policy.");
                    if (item.ReadingAngleDegrees is decimal angle && !string.IsNullOrWhiteSpace(item.Text)
                        && ReadingAngle(angle) is var reading && reading > PresentationPolicy.MaximumReadingAngleDegrees)
                        Add("text_orientation", PresentationSeverity.Warning, [item.Id], item.FullBounds, reading,
                            PresentationPolicy.MaximumReadingAngleDegrees, PresentationUnits.Degrees,
                            reading < 270m
                                ? "Text is painted upside down; rotate it to read left to right or bottom to top."
                                : "Text is painted reading top to bottom; rotate it to read left to right or bottom to top.");
                }
            }
            var objects = sheet.Objects.ToDictionary(o => o.Id);
            if (sheet.RequiredDesignators.Distinct().Count() != sheet.RequiredDesignators.Count
                || sheet.RequiredDesignators.Contains(Guid.Empty)) throw Invalid("Invalid required designator identities.");
            // A designator that cannot be read is measured as 0 visible (present, annotated) designators against the 1
            // required; one the page edge or a clip cuts off is measured by how far its painted text reaches beyond it.
            foreach (var id in sheet.RequiredDesignators)
            {
                if (!objects.TryGetValue(id, out var field))
                    Add("designator_missing", PresentationSeverity.Error, [id], null, 0m, 1m, PresentationUnits.Count,
                        "Required reference designator is absent from the rendering facts.");
                else if (field.Kind != PresentationObjectKind.ReferenceDesignator)
                    throw Invalid("Required designator identity resolves to another object kind.");
                else if (string.IsNullOrWhiteSpace(field.Text))
                    Add("designator_not_visible", PresentationSeverity.Error, [id], field.FullBounds, 0m, 1m, PresentationUnits.Count,
                        "Required reference designator is empty.");
                else if (!field.Visible)
                    Add("designator_not_visible", PresentationSeverity.Error, [id], field.FullBounds, 0m, 1m, PresentationUnits.Count,
                        $"Required reference designator '{field.Text}' is hidden.");
                else if (!sheet.PageBounds.Contains(field.FullBounds))
                    Add("designator_not_visible", PresentationSeverity.Error, [id], field.FullBounds,
                        Millimetres(Outside(sheet.PageBounds, field.FullBounds)), 0m, PresentationUnits.Millimetres,
                        $"Required reference designator '{field.Text}' is cut off by the page edge.");
                else if (field.ClipBounds is not null && !field.ClipBounds.Contains(field.FullBounds))
                    Add("designator_not_visible", PresentationSeverity.Error, [id], field.FullBounds,
                        Millimetres(Outside(field.ClipBounds, field.FullBounds)), 0m, PresentationUnits.Millimetres,
                        $"Required reference designator '{field.Text}' is clipped.");
                else if (field.Text.Contains('?', StringComparison.Ordinal))
                    Add("designator_unannotated", PresentationSeverity.Error, [id], field.FullBounds, 0m, 1m, PresentationUnits.Count,
                        $"Reference designator '{field.Text}' is not annotated; annotate the schematic so every symbol has its own designator.");
            }
            CheckOverlaps(sheet.Objects);
            var wireIds = new HashSet<Guid>();
            foreach (var wire in sheet.Wires)
            {
                if (wire.Id == Guid.Empty || string.IsNullOrWhiteSpace(wire.SignalKey) || !wireIds.Add(wire.Id) || wire.Start == wire.End
                    || (objects.TryGetValue(wire.Id, out var graphical) && graphical.Kind != PresentationObjectKind.Graphic))
                    throw Invalid("Invalid wire or signal identity, duplicate object, or zero-length wire.");
                var bounds = WireBounds(wire);
                if (!objects.ContainsKey(wire.Id) && !sheet.PageBounds.Contains(bounds))
                    Add("page_overflow", PresentationSeverity.Error, [wire.Id], bounds,
                        Millimetres(Outside(sheet.PageBounds, bounds)), 0m, PresentationUnits.Millimetres,
                        "Wire geometry extends beyond this sheet's page.");
            }
            var crossings = new Dictionary<string, Dictionary<ExactIntersection, HashSet<Guid>>>(StringComparer.Ordinal);
            var ordered = sheet.Wires.OrderBy(w => Math.Min(w.Start.XNm, w.End.XNm)).ThenBy(w => w.Id).ToArray();
            for (int i = 0; i < ordered.Length; ++i)
            for (int j = i + 1; j < ordered.Length; ++j)
            {
                var first = ordered[i]; var second = ordered[j];
                if (Math.Min(second.Start.XNm, second.End.XNm) > Math.Max(first.Start.XNm, first.End.XNm)) break;
                if (first.SignalKey == second.SignalKey) continue;
                var crossing = Intersect(first, second);
                if (crossing is null)
                {
                    // Measured as the length both signals share, against none allowed.
                    if (CollinearOverlap(first, second) is PresentationBounds overlap)
                        Add("overlapping_signals", PresentationSeverity.Warning, [first.Id, second.Id], overlap,
                            Millimetres(Math.Max(overlap.RightNm - overlap.LeftNm, overlap.BottomNm - overlap.TopNm)), 0m,
                            PresentationUnits.Millimetres, "Unrelated signals share a segment, making connectivity visually ambiguous.");
                    continue;
                }
                if (sheet.Junctions.Any(p => crossing == ExactIntersection.At(p)))
                {
                    // Measured as the number of distinct signals the junction joins, against the one it may join.
                    Add("junction_net_conflict", PresentationSeverity.Error, [first.Id, second.Id], crossing.Bounds, 2m, 1m,
                        PresentationUnits.Count, "A junction joins wires carrying different native signal identities; reconcile connectivity.");
                    continue;
                }
                Count(first.SignalKey, crossing, first.Id, second.Id);
                Count(second.SignalKey, crossing, first.Id, second.Id);
            }
            foreach (var (signal, locations) in crossings.OrderBy(p => p.Key, StringComparer.Ordinal))
                if (locations.Count > policy.MaximumCrossingsPerSignal)
                    Add("excessive_crossings", PresentationSeverity.Warning,
                        locations.Values.SelectMany(v => v).Distinct().Order().ToArray(),
                        new(locations.Keys.Min(p => p.Bounds.LeftNm), locations.Keys.Min(p => p.Bounds.TopNm),
                            locations.Keys.Max(p => p.Bounds.RightNm), locations.Keys.Max(p => p.Bounds.BottomNm)),
                        locations.Count, policy.MaximumCrossingsPerSignal, PresentationUnits.Count,
                        "Signal crosses unrelated wiring too many times on this sheet.", signal,
                        locations.OrderBy(p => p.Key.Bounds.LeftNm).ThenBy(p => p.Key.Bounds.TopNm)
                            .ThenBy(p => p.Key.Bounds.RightNm).ThenBy(p => p.Key.Bounds.BottomNm)
                            .Select(p => new PresentationLocation(p.Key.Bounds, p.Value.Order().ToArray())).ToArray());

            void Count(string signal, ExactIntersection location, Guid first, Guid second)
            {
                if (!crossings.TryGetValue(signal, out var locations)) crossings[signal] = locations = [];
                if (!locations.TryGetValue(location, out var involved)) locations[location] = involved = [];
                involved.Add(first); involved.Add(second);
            }
            // Symbol and sheet bodies, label bodies and painted field glyphs must not cover each other. A field
            // may sit on its own symbol, sheet or label (the library or user placed it there); it may not cover
            // anything else, including another field of the same owner.
            void CheckOverlaps(IReadOnlyList<PresentationObject> all)
            {
                var candidates = all.Where(o => o.Visible && o.Role switch
                    {
                        PresentationRole.Symbol or PresentationRole.Sheet or PresentationRole.Label => true,
                        PresentationRole.Field => !string.IsNullOrWhiteSpace(o.Text),
                        _ => false
                    })
                    .OrderBy(o => o.FullBounds.LeftNm).ThenBy(o => o.Id).ToArray();
                for (int i = 0; i < candidates.Length; ++i)
                for (int j = i + 1; j < candidates.Length; ++j)
                {
                    var first = candidates[i]; var second = candidates[j];
                    if (second.FullBounds.LeftNm >= first.FullBounds.RightNm) break;
                    if (first.OwnerId == second.Id || second.OwnerId == first.Id) continue;
                    if (Intersection(first.FullBounds, second.FullBounds) is not PresentationBounds shared) continue;
                    decimal depth = Millimetres(Math.Min(shared.RightNm - shared.LeftNm, shared.BottomNm - shared.TopNm));
                    if (depth <= policy.OverlapToleranceMm) continue;
                    Guid[] pair = first.Id.CompareTo(second.Id) < 0 ? [first.Id, second.Id] : [second.Id, first.Id];
                    if (first.Role == PresentationRole.Field || second.Role == PresentationRole.Field)
                        Add("field_overlap", PresentationSeverity.Warning, pair, shared, depth, policy.OverlapToleranceMm,
                            PresentationUnits.Millimetres,
                            "A field's painted text overlaps another symbol, sheet, label or field; move the field or the object it covers.");
                    else if (first.Role == PresentationRole.Label || second.Role == PresentationRole.Label)
                        Add("label_overlap", PresentationSeverity.Warning, pair, shared, depth, policy.OverlapToleranceMm,
                            PresentationUnits.Millimetres, "A label overlaps a symbol, sheet or another label; move the label clear of it.");
                    else
                        Add("body_overlap", PresentationSeverity.Error, pair, shared, depth, policy.OverlapToleranceMm,
                            PresentationUnits.Millimetres, "Two symbol or sheet bodies overlap; move one of them clear of the other.");
                }
            }
            void Add(string rule, PresentationSeverity severity, IReadOnlyList<Guid> objects,
                PresentationBounds? bounds, decimal? measured, decimal? limit, string? unit, string message, string? signal = null,
                IReadOnlyList<PresentationLocation>? locations = null) =>
                findings.Add(new(rule, severity, path, objects, bounds, measured, limit, message, signal, locations,
                    snapshot.Revision, sheet.Name, unit));
        }
        if (snapshot.Sheets.Count == 0) throw Invalid("At least one sheet is required.");
        return new(snapshot.DocumentId, snapshot.Revision,
            snapshot.CoverageComplete && findings.All(f => f.Severity != PresentationSeverity.Unavailable), findings, coverage, policy);
    }

    // How far the inner box reaches beyond the outer one, in nanometres (0 when it fits).
    private static long Outside(PresentationBounds outer, PresentationBounds inner)
    {
        BigInteger reach = BigInteger.Max(BigInteger.Max((BigInteger)outer.LeftNm - inner.LeftNm, (BigInteger)outer.TopNm - inner.TopNm),
            BigInteger.Max((BigInteger)inner.RightNm - outer.RightNm, (BigInteger)inner.BottomNm - outer.BottomNm));
        return reach.Sign <= 0 ? 0 : reach > long.MaxValue ? long.MaxValue : (long)reach;
    }
    private static decimal Millimetres(long nanometres) => nanometres / 1_000_000m;
    // Boxes that only touch share no area.
    private static PresentationBounds? Intersection(PresentationBounds a, PresentationBounds b)
    {
        long left = Math.Max(a.LeftNm, b.LeftNm), top = Math.Max(a.TopNm, b.TopNm);
        long right = Math.Min(a.RightNm, b.RightNm), bottom = Math.Min(a.BottomNm, b.BottomNm);
        return left < right && top < bottom ? new(left, top, right, bottom) : null;
    }
    private static decimal ReadingAngle(decimal degrees)
    {
        decimal angle = degrees % 360m;
        if (angle < 0) angle += 360m;
        // Native angles are exact tenths of a degree; anything this close to a full turn reads left to right.
        return 360m - angle < 0.001m ? 0m : angle;
    }

    // Rational coordinates deduplicate exact intersections even when wires are
    // segmented differently. BigInteger avoids overflow at native coordinate limits.
    private sealed record ExactIntersection(BigInteger X, BigInteger Y, BigInteger Denominator)
    {
        public PresentationBounds Bounds => new(Round(X, false), Round(Y, false), Round(X, true), Round(Y, true));
        private long Round(BigInteger value, bool ceiling)
        {
            var quotient = BigInteger.DivRem(value, Denominator, out var remainder);
            return checked((long)(ceiling ? (remainder.Sign > 0 ? quotient + 1 : quotient)
                : (remainder.Sign < 0 ? quotient - 1 : quotient)));
        }
        public static ExactIntersection At(PresentationPoint p) => new(p.XNm, p.YNm, 1);
        public static ExactIntersection Create(BigInteger x, BigInteger y, BigInteger denominator)
        {
            if (denominator.Sign < 0) { x = -x; y = -y; denominator = -denominator; }
            var divisor = BigInteger.GreatestCommonDivisor(BigInteger.GreatestCommonDivisor(BigInteger.Abs(x), BigInteger.Abs(y)), denominator);
            return new(x / divisor, y / divisor, denominator / divisor);
        }
    }
    private static ExactIntersection? Intersect(PresentationWire a, PresentationWire b)
    {
        if (Math.Max(a.Start.XNm, a.End.XNm) < Math.Min(b.Start.XNm, b.End.XNm)
            || Math.Max(b.Start.XNm, b.End.XNm) < Math.Min(a.Start.XNm, a.End.XNm)
            || Math.Max(a.Start.YNm, a.End.YNm) < Math.Min(b.Start.YNm, b.End.YNm)
            || Math.Max(b.Start.YNm, b.End.YNm) < Math.Min(a.Start.YNm, a.End.YNm)) return null;
        BigInteger rx = (BigInteger)a.End.XNm - a.Start.XNm, ry = (BigInteger)a.End.YNm - a.Start.YNm;
        BigInteger sx = (BigInteger)b.End.XNm - b.Start.XNm, sy = (BigInteger)b.End.YNm - b.Start.YNm;
        BigInteger qx = (BigInteger)b.Start.XNm - a.Start.XNm, qy = (BigInteger)b.Start.YNm - a.Start.YNm;
        BigInteger denominator = rx * sy - ry * sx;
        if (denominator.IsZero) return null; // Collinear overlap is not an X/T crossing.
        BigInteger t = qx * sy - qy * sx, u = qx * ry - qy * rx;
        if (denominator.Sign < 0) { denominator = -denominator; t = -t; u = -u; }
        if (t < 0 || t > denominator || u < 0 || u > denominator) return null;
        return ExactIntersection.Create((BigInteger)a.Start.XNm * denominator + rx * t,
            (BigInteger)a.Start.YNm * denominator + ry * t, denominator);
    }
    private static PresentationBounds WireBounds(PresentationWire wire) => new(
        Math.Min(wire.Start.XNm, wire.End.XNm), Math.Min(wire.Start.YNm, wire.End.YNm),
        Math.Max(wire.Start.XNm, wire.End.XNm), Math.Max(wire.Start.YNm, wire.End.YNm));

    private static PresentationBounds? CollinearOverlap(PresentationWire a, PresentationWire b)
    {
        BigInteger rx = (BigInteger)a.End.XNm - a.Start.XNm, ry = (BigInteger)a.End.YNm - a.Start.YNm;
        bool OnLine(PresentationPoint point) => rx * ((BigInteger)point.YNm - a.Start.YNm)
            == ry * ((BigInteger)point.XNm - a.Start.XNm);
        if (!OnLine(b.Start) || !OnLine(b.End)) return null;
        var first = WireBounds(a); var second = WireBounds(b);
        var overlap = new PresentationBounds(Math.Max(first.LeftNm, second.LeftNm), Math.Max(first.TopNm, second.TopNm),
            Math.Min(first.RightNm, second.RightNm), Math.Min(first.BottomNm, second.BottomNm));
        return overlap.LeftNm <= overlap.RightNm && overlap.TopNm <= overlap.BottomNm
            && (overlap.LeftNm < overlap.RightNm || overlap.TopNm < overlap.BottomNm) ? overlap : null;
    }
    internal static AutomationException Invalid(string message) => new("invalid_presentation", message);
}
