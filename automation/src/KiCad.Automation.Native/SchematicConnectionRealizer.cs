using System.Collections;
using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

// CN-1 realization (automation/design/contracts/cn1-wiring-intent.md §6 and §7). Lane 2A created this file under the CN-1
// integration grant (decision n2c2ef8777f8ace77). It measures the checkpoint natively and turns a planned connection
// intent into exact native items: orthogonal wires with junctions joining the new pins of a connection on one sheet,
// carrying one label with the net's name (milestone 2, routed by SchematicOrthogonalRouter), or, where no route fits or
// a sheet has fewer than two new pins of the connection, a short wire from each pin ending in such a label (milestone 1);
// sheet pins where a net crosses into a child sheet; and one final connectivity assertion that makes the editor prove the
// resulting pin partition before it commits anything.

/// <summary>How an island was drawn (cn1-wiring-intent.md §6.8).</summary>
public enum ConnectionRealizationStrategy { LabelStub = 1, OrthogonalWire = 2 }

/// <summary>One generated native item. <paramref name="TypeUrl"/> is the protobuf Any type of its payload.</summary>
public sealed record GeneratedConnectionItem(Guid Id, GeneratedConnectionRole Role, Guid ScreenId, IReadOnlyList<Guid> NetIds,
    Guid? PlacedPinId, Guid? SheetSymbolId, string TypeUrl);

/// <summary>What one island of the representative instance path received.</summary>
public sealed record ConnectionIslandOutcome(Guid NetId, Guid ScreenId, string RepresentativePathKey,
    ConnectionRealizationStrategy Strategy, IReadOnlyList<Guid> GeneratedIds, bool AttachedCarrier, string? FallbackReason);

/// <summary>A non-blocking realization note; <paramref name="Severity"/> is <c>info</c> or <c>warning</c>.</summary>
public sealed record ConnectionDiagnostic(string Code, string Severity, Guid? NetId, Guid? ItemId, string Message);

/// <summary>The realized design and the exact batch operations that create it, ending with the connectivity
/// assertion (cn1-wiring-intent.md §6.8).</summary>
public sealed record SchematicConnectionRealization(SchematicDesign Design, IReadOnlyList<SchematicItemOperation> Operations,
    IReadOnlyList<GeneratedConnectionItem> Generated, IReadOnlyList<ConnectionIslandOutcome> Outcomes,
    IReadOnlyList<ConnectionDiagnostic> Diagnostics, IReadOnlyList<string> Limitations);

/// <summary>Which label a generated stub carries.</summary>
internal enum ConnectionLabelKind { Local = 1, Global = 2, Hierarchical = 3 }

/// <summary>CN-1 realization: the new pins of a connection on one sheet are joined by orthogonal wires with junctions and
/// named by one label at the first pin (cn1-wiring-intent.md §7); a connection with fewer than two new pins on a sheet, or
/// one no route fits (its fallback reason recorded), is drawn as a short wire stub from each pin ending in a label (§6);
/// hierarchy crossings get sheet pins. Everything is measured by the editor at the checkpoint revision; nothing is guessed.
/// Every refusal is an <see cref="AutomationException"/> with a §13 realization code, raised before anything reaches the
/// editor's document.</summary>
public static class SchematicConnectionRealizer
{
    /// <summary>At most this many symbol and item candidates go into one measurement request.</summary>
    public const int MaxMeasuredCandidates = 256;
    /// <summary>The checked native controller refuses larger serialized requests (§0, §6.8 step 4).</summary>
    public const int MaxBatchBytes = 2_097_152;
    /// <summary>The batch description the executor uses for a realization (§6.8 step 4).</summary>
    public const string BatchDescription = "Apply XML connections";

    public static Task<SchematicConnectionRealization> RealizeAsync(SchematicConnectionIntent intent, SchematicDesign candidate,
        CheckedSchematicState checkpoint, Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> measure,
        SchematicConnectionPolicy policy, CancellationToken token = default) =>
        RealizeAsync(intent, candidate, checkpoint, measure, policy, SchematicRoutingLimits.Contract, token);

    /// <summary><see cref="RealizeAsync(SchematicConnectionIntent, SchematicDesign, CheckedSchematicState, Func{MeasureSchematicPlacement, CancellationToken, Task{SchematicPlacementGeometry}}, SchematicConnectionPolicy, CancellationToken)"/>
    /// with other routing budgets than the contract's (§7). A test that passes a zero node budget sees every connection fall
    /// back to label stubs exactly as it does when no route fits.</summary>
    internal static Task<SchematicConnectionRealization> RealizeAsync(SchematicConnectionIntent intent, SchematicDesign candidate,
        CheckedSchematicState checkpoint, Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> measure,
        SchematicConnectionPolicy policy, SchematicRoutingLimits limits, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(limits);
        token.ThrowIfCancellationRequested();
        return new Run(intent, candidate, checkpoint, measure, policy, limits, token).ExecuteAsync();
    }

    /// <summary>The exact label payload a stub carries (§6.6), also used for its measurement prototype. Besides the
    /// contract's fields the text attributes carry the angle and justification the spin style implies: native label
    /// decoding applies the spin style first and then every text attribute, so without them every label would read
    /// back facing right and centred on its anchor (a clarification of §6.6 reported to the integration owner).</summary>
    internal static IMessage LabelPayload(ConnectionLabelKind kind, Guid id, Vector2 position, string text,
        SchematicLabelSpinStyle spin, SchematicConnectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(policy);
        bool vertical = spin is SchematicLabelSpinStyle.SlssUp or SchematicLabelSpinStyle.SlssBottom;
        var attributes = new TextAttributes
        {
            Size = new() { XNm = policy.TextSizeNm, YNm = policy.TextSizeNm }, Multiline = false,
            Angle = new() { ValueDegrees = vertical ? 90 : 0 },
            HorizontalAlignment = spin switch
            {
                SchematicLabelSpinStyle.SlssRight or SchematicLabelSpinStyle.SlssUp => HorizontalAlignment.HaLeft,
                SchematicLabelSpinStyle.SlssLeft or SchematicLabelSpinStyle.SlssBottom => HorizontalAlignment.HaRight,
                _ => throw new ArgumentOutOfRangeException(nameof(spin), spin, "A generated label faces along one schematic axis.")
            },
            // Local labels sit on the wire; global and hierarchical label shapes are centred on it.
            VerticalAlignment = kind == ConnectionLabelKind.Local ? VerticalAlignment.VaBottom : VerticalAlignment.VaCenter
        };
        var caption = new Text { Text_ = text, Attributes = attributes };
        var kiid = new KIID { Value = id.ToString("D") };
        return kind switch
        {
            ConnectionLabelKind.Local => new LocalLabel { Id = kiid, Position = position.Clone(), Text = caption, SpinStyle = spin,
                Locked = LockedState.LsUnlocked },
            ConnectionLabelKind.Global => new GlobalLabel { Id = kiid, Position = position.Clone(), Text = caption, SpinStyle = spin,
                Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked },
            ConnectionLabelKind.Hierarchical => new HierarchicalLabel { Id = kiid, Position = position.Clone(), Text = caption, SpinStyle = spin,
                Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown generated label kind.")
        };
    }

    /// <summary>The protobuf type of a generated label kind, which enters its probe identity (§6.7).</summary>
    internal static MessageDescriptor Descriptor(ConnectionLabelKind kind) => kind switch
    {
        ConnectionLabelKind.Local => LocalLabel.Descriptor,
        ConnectionLabelKind.Global => GlobalLabel.Descriptor,
        ConnectionLabelKind.Hierarchical => HierarchicalLabel.Descriptor,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown generated label kind.")
    };

    /// <summary>The direction a label of <paramref name="spin"/> reads away from its anchor, the inverse of
    /// <see cref="SchematicConnectionGeometry.Spin"/>.</summary>
    internal static (int Dx, int Dy) Facing(SchematicLabelSpinStyle spin) => spin switch
    {
        SchematicLabelSpinStyle.SlssRight => (1, 0),
        SchematicLabelSpinStyle.SlssLeft => (-1, 0),
        SchematicLabelSpinStyle.SlssUp => (0, -1),
        SchematicLabelSpinStyle.SlssBottom => (0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(spin), spin, "A generated label faces along one schematic axis.")
    };

    private static AutomationException Error(string code, string message) => new(code, message);

    // ---- geometry primitives (sheet-space nanometres; y grows down) ----

    internal readonly record struct Pt(long X, long Y)
    {
        public static Pt Of(Vector2 v) => new(v.XNm, v.YNm);
        public Vector2 Vector() => new() { XNm = X, YNm = Y };
        public Pt Step((int Dx, int Dy) direction, long length) => new(checked(X + direction.Dx * length), checked(Y + direction.Dy * length));
    }

    /// <summary>A closed axis-aligned rectangle.</summary>
    internal readonly record struct Box(long L, long T, long R, long B)
    {
        public static Box Of(Box2 box) => new(box.Position.XNm, box.Position.YNm,
            checked(box.Position.XNm + box.Size.XNm), checked(box.Position.YNm + box.Size.YNm));
        public static Box Point(Pt p) => new(p.X, p.Y, p.X, p.Y);
        public static Box Segment(Pt a, Pt b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        public Box Offset(Pt d) => new(checked(L + d.X), checked(T + d.Y), checked(R + d.X), checked(B + d.Y));
        public Box Relative(Pt anchor) => new(checked(L - anchor.X), checked(T - anchor.Y), checked(R - anchor.X), checked(B - anchor.Y));
        public Box Union(Box o) => new(Math.Min(L, o.L), Math.Min(T, o.T), Math.Max(R, o.R), Math.Max(B, o.B));
        public Box Inflate(long c) => new(checked(L - c), checked(T - c), checked(R + c), checked(B + c));
        public bool Contains(Pt p) => p.X >= L && p.X <= R && p.Y >= T && p.Y <= B;
        public bool Within(Box outer) => L >= outer.L && R <= outer.R && T >= outer.T && B <= outer.B;
        public bool Touches(Box o) => L <= o.R && o.L <= R && T <= o.B && o.T <= B;
        /// <summary>Whether this rectangle's open interior meets <paramref name="closed"/>.</summary>
        public bool InteriorMeets(Box closed) => L < R && T < B && closed.L < R && closed.R > L && closed.T < B && closed.B > T;
        /// <summary>Whether the open interiors of two rectangles overlap; touching edges do not.</summary>
        public bool InteriorsOverlap(Box o) => L < R && T < B && o.L < o.R && o.T < o.B && o.L < R && o.R > L && o.T < B && o.B > T;
    }

    /// <summary>Whether the closed segment [a,b] meets the rectangle: its closed area, or with
    /// <paramref name="open"/> only its interior. Exact rational clipping on 128-bit integers.</summary>
    internal static bool SegmentMeets(Pt a, Pt b, Box box, bool open)
    {
        if (open && (box.L >= box.R || box.T >= box.B)) return false;
        // Parameter interval over t in [0,1]; bounds are fractions num/den with den > 0.
        Int128 loNum = 0, loDen = 1, hiNum = 1, hiDen = 1;
        bool loStrict = false, hiStrict = false;
        bool Clip(long start, long delta, long min, long max)
        {
            if (delta == 0)
                return open ? start > min && start < max : start >= min && start <= max;
            // min <= start + t*delta <= max
            Int128 den = delta > 0 ? delta : -(Int128)delta;
            Int128 lower = delta > 0 ? (Int128)min - start : (Int128)start - max;
            Int128 upper = delta > 0 ? (Int128)max - start : (Int128)start - min;
            // t >= lower/den (strict when open), t <= upper/den.
            int compareLower = (lower * loDen).CompareTo(loNum * den);
            if (compareLower > 0 || (compareLower == 0 && open)) { loNum = lower; loDen = den; loStrict = open || (compareLower == 0 && loStrict); }
            int compareUpper = (upper * hiDen).CompareTo(hiNum * den);
            if (compareUpper < 0 || (compareUpper == 0 && open)) { hiNum = upper; hiDen = den; hiStrict = open || (compareUpper == 0 && hiStrict); }
            return true;
        }
        if (!Clip(a.X, b.X - a.X, box.L, box.R) || !Clip(a.Y, b.Y - a.Y, box.T, box.B)) return false;
        int order = (loNum * hiDen).CompareTo(hiNum * loDen);
        return order < 0 || (order == 0 && !loStrict && !hiStrict);
    }

    /// <summary>Whether <paramref name="p"/> lies on the closed segment [a,b].</summary>
    internal static bool OnSegment(Pt p, Pt a, Pt b)
    {
        if (!Box.Segment(a, b).Contains(p)) return false;
        Int128 cross = (Int128)(b.X - a.X) * (p.Y - a.Y) - (Int128)(b.Y - a.Y) * (p.X - a.X);
        return cross == 0;
    }

    private static string PathOf(SheetPath path) => string.Join('/', path.Path.Select(id => id.Value));

    private static bool TryId(KIID? value, out Guid id) => Guid.TryParseExact(value?.Value, "D", out id) && id != Guid.Empty && value!.Value == id.ToString("D");

    private static Guid Id(KIID? value) => TryId(value, out var id) ? id
        : throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "Native geometry and snapshots need canonical non-empty identities.");

    // ---- measured sheet geometry (§6.2, §6.4) ----

    private static void Merge(PathView view, SchematicPlacementGeometry measured)
    {
        var page = Box.Of(measured.PageBounds);
        if (view.Page is { } known && known != page)
            throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "One sheet reported two page sizes.");
        view.Page = page;
        if (view.Measured && !Equals(view.DrawingSheet, measured.DrawingSheet))
            throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "One revision measured the drawing sheet of sheet " + view.Path + " twice differently.");
        view.DrawingSheet = measured.DrawingSheet;
        view.FieldsReported = (!view.Measured || view.FieldsReported) && measured.FieldBoundsReported;
        view.Measured = true;
        foreach (var obstacle in measured.Obstacles)
        {
            var id = Id(obstacle.Id);
            if (view.Obstacles.TryGetValue(id, out var prior) && !prior.Equals(obstacle))
                throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "One revision measured item " + id.ToString("D") + " twice differently.");
            view.Obstacles[id] = obstacle;
            if (obstacle.SymbolPins is not null) view.Pins[id] = obstacle.SymbolPins;
        }
        foreach (var measuredCandidate in measured.Candidates)
        {
            var id = Id(measuredCandidate.Id);
            view.Candidates[id] = measuredCandidate;
            if (measuredCandidate.SymbolPins is not null) view.Pins[id] = measuredCandidate.SymbolPins;
        }
    }

    /// <summary>How far in from each page edge KiCad's default drawing sheet draws the inside of its border (its 10 mm
    /// margin plus the 2 mm band of zone marks), and how much of the page bottom the fixture policy keeps for the title
    /// block (psu-cpu-fixture-and-ownership.md §1.6.3): the §7 route region of a sheet whose drawing sheet is not measured.</summary>
    internal const long DefaultFrameInsetNm = 12_000_000, DefaultTitleBlockReserveNm = 50_000_000;

    /// <summary>Where routed wires may run on one measured sheet instance (CN-1 §7 region, as corrected by the erratum lane 2A
    /// reported: the page shrunk by <c>PageInsetNm</c> reaches into the drawing sheet's frame and title block, which are no
    /// schematic items): the inside of the drawing sheet's innermost border frame, and the drawing-sheet art inside it (the
    /// title block, any logo or text) as keep-outs, overlapping pieces merged into one box. A border frame is a drawn
    /// rectangle around the middle of the drawing sheet's margin frame that spans more than half of it both ways. The region
    /// is shrunk by <paramref name="clearance"/>; keep-outs are returned as drawn and are inflated by the clearance where they
    /// are used. Without a measured drawing sheet the region is the inside of KiCad's default frame with the bottom
    /// <see cref="DefaultTitleBlockReserveNm"/> kept for the title block.</summary>
    internal static (Box Region, IReadOnlyList<Box> KeepOuts) RouteArea(Box page, SchematicWiringDrawingSheet? sheet, long clearance)
    {
        Box interior;
        var keepOuts = new List<Box>();
        if (sheet?.MarginFrame is null)
            interior = new(checked(page.L + DefaultFrameInsetNm), checked(page.T + DefaultFrameInsetNm), checked(page.R - DefaultFrameInsetNm),
                checked(page.B - DefaultTitleBlockReserveNm));
        else
        {
            var margin = Box.Of(sheet.MarginFrame);
            interior = Intersect(page, margin);
            // Compared at twice their size, so that the middle of the margin frame needs no rounding.
            Int128 middleX = (Int128)margin.L + margin.R, middleY = (Int128)margin.T + margin.B;
            var items = sheet.Items.Select(i => (i.Kind, Bounds: Box.Of(i.Bounds))).ToArray();
            bool Frame((SchematicWiringDrawingSheetItemKind Kind, Box Bounds) item) => item.Kind == SchematicWiringDrawingSheetItemKind.SwrDrawingSheetItemRectangle
                && 2 * (Int128)item.Bounds.L < middleX && 2 * (Int128)item.Bounds.R > middleX && 2 * (Int128)item.Bounds.T < middleY && 2 * (Int128)item.Bounds.B > middleY
                && 2 * ((Int128)item.Bounds.R - item.Bounds.L) > (Int128)margin.R - margin.L && 2 * ((Int128)item.Bounds.B - item.Bounds.T) > (Int128)margin.B - margin.T;
            foreach (var frame in items.Where(Frame)) interior = Intersect(interior, frame.Bounds);
            // Art inside the frame; a mark that only touches the frame's edge from outside stays outside it.
            foreach (var item in items.Where(i => !Frame(i)))
                if (item.Bounds.L < interior.R && item.Bounds.R > interior.L && item.Bounds.T < interior.B && item.Bounds.B > interior.T)
                    keepOuts.Add(item.Bounds);
            // Overlapping or touching pieces (the lines, texts and box of a title block) become one keep-out.
            for (bool merged = true; merged;)
            {
                merged = false;
                for (int i = 0; i < keepOuts.Count && !merged; i++)
                    for (int j = i + 1; j < keepOuts.Count && !merged; j++)
                        if (keepOuts[i].Touches(keepOuts[j]))
                        {
                            keepOuts[i] = keepOuts[i].Union(keepOuts[j]);
                            keepOuts.RemoveAt(j);
                            merged = true;
                        }
            }
        }
        var region = new Box(checked(interior.L + clearance), checked(interior.T + clearance), checked(interior.R - clearance), checked(interior.B - clearance));
        return (region, [.. keepOuts.OrderBy(k => k.L).ThenBy(k => k.T).ThenBy(k => k.R).ThenBy(k => k.B)]);

        static Box Intersect(Box a, Box b) => new(Math.Max(a.L, b.L), Math.Max(a.T, b.T), Math.Min(a.R, b.R), Math.Min(a.B, b.B));
    }

    // Union of every instance path: obstacles, connection points and connection segments of the one physical screen.
    private static void BuildGeometry(Screen screen, SchematicConnectionPolicy policy)
    {
        screen.Page = screen.Views[0].Page!.Value;
        if (screen.Views.Any(v => v.Page != screen.Page))
            throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "Instances of one sheet reported different pages.");
        long inset = policy.PageInsetNm;
        screen.Usable = new(screen.Page.L + inset, screen.Page.T + inset, screen.Page.R - inset, screen.Page.B - inset);
        // §7 region (erratum): every instance's drawing sheet is kept clear, so the region is where all of them allow wires.
        var areas = screen.Views.Select(v => RouteArea(screen.Page, v.DrawingSheet, policy.ClearanceNm)).ToArray();
        screen.RouteRegion = areas.Skip(1).Aggregate(areas[0].Region, (a, b) => new(Math.Max(a.L, b.Region.L), Math.Max(a.T, b.Region.T),
            Math.Min(a.R, b.Region.R), Math.Min(a.B, b.Region.B)));
        screen.RouteKeepOuts.AddRange(areas.SelectMany(a => a.KeepOuts).Distinct().OrderBy(k => k.L).ThenBy(k => k.T).ThenBy(k => k.R).ThenBy(k => k.B));
        screen.FieldsKnown = screen.Views.All(v => v.FieldsReported);
        foreach (var view in screen.Views)
            foreach (var (id, bounds) in view.Obstacles.Concat(view.Candidates))
                if (bounds.SymbolPins is not null)
                {
                    if (!screen.Fields.TryGetValue(id, out var fields)) screen.Fields.Add(id, fields = []);
                    foreach (var field in bounds.VisibleFieldBounds.Select(Box.Of))
                        if (!fields.Contains(field)) fields.Add(field);
                }
        var points = new HashSet<ForeignPoint>();
        var segments = new HashSet<ForeignSegment>();
        foreach (var view in screen.Views)
        {
            foreach (var (id, bounds) in view.Obstacles.Concat(view.Candidates))
            {
                var box = Box.Of(bounds.Bounds);
                screen.Obstacles[id] = screen.Obstacles.TryGetValue(id, out var prior) ? prior.Union(box) : box;
            }
            foreach (var (symbol, pins) in view.Pins)
                foreach (var pin in pins.Pins)
                    points.Add(new(Pt.Of(pin.Position), PointKind.Pin, Id(pin.Id), symbol, pin.Number));
            foreach (var (id, item) in SchematicItemDelta.Index(view.Native.Items))
            {
                switch (item)
                {
                    case SchematicLine line when line.Type is SchematicLineType.SltWire or SchematicLineType.SltBus:
                        points.Add(new(Pt.Of(line.Start), PointKind.WireEnd, id, null));
                        points.Add(new(Pt.Of(line.End), PointKind.WireEnd, id, null));
                        segments.Add(new(Pt.Of(line.Start), Pt.Of(line.End), id, line.Type == SchematicLineType.SltBus));
                        screen.ConnectionLines.Add(id);
                        break;
                    case Junction junction: points.Add(new(Pt.Of(junction.Position), PointKind.Junction, id, null)); break;
                    case NoConnectMarker marker: points.Add(new(Pt.Of(marker.Position), PointKind.NoConnect, id, null)); break;
                    case BusEntry entry:
                        points.Add(new(Pt.Of(entry.Position), PointKind.BusEntry, id, null));
                        points.Add(new(new(checked(entry.Position.XNm + (entry.Size?.XNm ?? 0)), checked(entry.Position.YNm + (entry.Size?.YNm ?? 0))),
                            PointKind.BusEntry, id, null));
                        break;
                    case LocalLabel label: points.Add(new(Pt.Of(label.Position), PointKind.Label, id, null)); break;
                    case GlobalLabel label: points.Add(new(Pt.Of(label.Position), PointKind.Label, id, null)); break;
                    case HierarchicalLabel label: points.Add(new(Pt.Of(label.Position), PointKind.Label, id, null)); break;
                    case DirectiveLabel label: points.Add(new(Pt.Of(label.Position), PointKind.Label, id, null)); break;
                    case SheetSymbol sheet:
                        foreach (var pin in sheet.Pins)
                            points.Add(new(Pt.Of(pin.Position), PointKind.SheetPin, Id(pin.Id), id));
                        break;
                }
            }
        }
        screen.Points.AddRange(points.OrderBy(p => p.Position.X).ThenBy(p => p.Position.Y).ThenBy(p => p.Owner));
        screen.Segments.AddRange(segments.OrderBy(s => s.Owner));
    }

    /// <summary>How far KiCad's measured bounds of a symbol reach past the end of each of its visible pins: the target KiCad
    /// draws on a pin end that nothing connects to (TARGET_PIN_RADIUS, 15 mil = 381,000 nm) plus the pin box's one-unit
    /// (100 nm) inflation in PIN_LAYOUT_CACHE::GetPinBoundingBox. Placement measurement reads a symbol's library pins, which
    /// are never connected, so every visible pin's bounds reach this far past its connection point whatever else the symbol
    /// draws, and a connected pin loses the target. The reach comes from each pin's own bounds, not from the symbol's
    /// fields, so leaving empty fields out of the measurement would not remove it.</summary>
    internal const long PinTargetReachNm = 381_100;

    // §6.4 admission of a stub from a to e, pointing outward, with label envelope r (null when attaching to a carrier):
    // null when admitted, otherwise the rule that refuses it. A piece of a routed connection (`routed`: a pin's escape, the
    // stub carrying its name label and that label, an attach wire to its existing part) must also keep to the §7 route region
    // (decision nfa2005d67574bfea, E1): inside the drawing sheet's innermost frame shrunk by the clearance, and at least the
    // clearance from its title block and other art, the wire and the label alike (a label may touch the shrunk frame, as a
    // wire end may, but not the keep-out inflated by the clearance).
    private static string? Refusal(SchematicConnectionPolicy policy, Screen screen, IslandState island, Pt a, Pt e, (int Dx, int Dy) outward,
        Box? r, Guid? ownPin, Guid owner, Guid? carrierPin, Guid? carrierSymbol, Variant variant, bool routed = false)
    {
        // 1. Inside the page inset.
        if (!screen.Usable.Contains(e) || (r is { } inside && !inside.Within(screen.Usable))) return "outside the page inset";
        if (routed)
        {
            if (!screen.RouteRegion.Contains(e) || (r is { } framed && !framed.Within(screen.RouteRegion)))
                return "outside the drawing sheet's frame";
            foreach (var keepOut in screen.RouteKeepOuts)
            {
                var kept = keepOut.Inflate(policy.ClearanceNm);
                if (SegmentMeets(a, e, kept, open: false)) return "within the clearance of the drawing sheet's title block";
                if (r is { } label && label.Touches(kept)) return "the label comes within the clearance of the drawing sheet's title block";
            }
        }
        var stub = Box.Segment(a, e);
        bool Excepted(ForeignPoint p) => (ownPin is { } own && p.Owner == own && p.Position == a)
            || (carrierPin is { } carrier && p.Owner == carrier && p.Position == e)
            || (p.Position == a && island.Same.Contains(p.Owner) && p.Kind != PointKind.Generated);
        foreach (var point in screen.Points)
        {
            if (Excepted(point)) continue;
            // 2. Nothing foreign on the stub or within the clearance of its end.
            if (stub.Contains(point.Position)) return Describe(point) + " lies on the stub";
            if (Math.Abs(point.Position.X - e.X) <= policy.ClearanceNm && Math.Abs(point.Position.Y - e.Y) <= policy.ClearanceNm)
                return Describe(point) + " lies within the clearance of the stub end";
            // 3. Nothing foreign inside the label.
            if (r is { } label && label.Contains(point.Position)) return Describe(point) + " lies inside the label";
        }
        var reach = stub.Inflate(policy.ClearanceNm);
        foreach (var segment in screen.Segments)
        {
            bool sameAtA = island.Same.Contains(segment.Owner) && (segment.A == a || segment.B == a);
            // An anchor label may overlap same-island wires that end at its anchor (rules 4 and 6).
            if (sameAtA && variant == Variant.AnchorLabel) continue;
            if (sameAtA && variant == Variant.JoinStub)
            {
                // A join stub may start on a same-island wire ending at its anchor, but not run along it.
                var other = segment.A == a ? segment.B : segment.A;
                var (dx, dy) = (e.X - a.X, e.Y - a.Y);
                if ((Int128)dx * (other.Y - a.Y) - (Int128)dy * (other.X - a.X) == 0
                    && (Int128)dx * (other.X - a.X) + (Int128)dy * (other.Y - a.Y) > 0)
                    return "the stub would run along wire " + segment.Owner.ToString("D") + " of the connection it joins";
            }
            // 4. No foreign segment on or near the stub; 6. none through the label.
            else if (SegmentMeets(segment.A, segment.B, reach, open: false))
                return "wire " + segment.Owner.ToString("D") + " runs on or within the clearance of the stub";
            if (r is { } label && SegmentMeets(segment.A, segment.B, label, open: true))
                return "wire " + segment.Owner.ToString("D") + " runs through the label";
        }
        // Symbols drawing a pin of this island exactly at the anchor (the owner's stacked pins, or another symbol's pin
        // stacked on it). Near the anchor their measured bounds are that pin and its target, which stop mattering once
        // the pin is connected there. Only a join stub may start across another such symbol's target (rule 5); an anchor
        // label on a pin another symbol shares is refused by rule 5, because that symbol's bounds hold the anchor.
        var stackedAtA = screen.Points.Where(p => p.Kind == PointKind.Pin && p.Position == a && island.Same.Contains(p.Owner))
            .Select(p => p.OwnerSymbol).OfType<Guid>().ToHashSet();
        foreach (var (id, bounds) in screen.Obstacles)
        {
            bool sameWireAtA = variant == Variant.AnchorLabel && island.Same.Contains(id)
                && screen.Segments.Any(s => s.Owner == id && (s.A == a || s.B == a));
            if (sameWireAtA) continue;
            // 5. The stub crosses no obstacle but its own symbol (and, attaching, the carrier's). A join stub may start on
            // same-island items that sit exactly at its anchor, and pass a stacked same-island pin's target.
            if (id != owner && id != carrierSymbol && SegmentMeets(a, e, bounds, open: false)
                && !(variant == Variant.JoinStub && ((island.Same.Contains(id) && OnlyWithin(a, e, bounds, 0))
                    || (stackedAtA.Contains(id) && OnlyWithin(a, e, bounds, PinTargetReachNm)))))
                return (a == e ? "the pin lies inside the bounds of " : "the stub crosses ") + Item(id);
            // 6. The label overlaps no obstacle, including its own symbol. An anchor label sits on its pin's connection
            // point: every real label reaches a little behind its anchor (at most LabelBackToleranceNm, by the §6.2
            // orientation guard), over its own pin, and KiCad's measured bounds of the pin's symbol end PinTargetReachNm
            // past the pin, at the edge of the pin's target. So only the part of an anchor label more than PinTargetReachNm
            // in front of the pin must be clear of its own symbol (a CN-1 clarification requested from the integration
            // owner); anything that symbol draws further in front of the pin refuses it. Every other symbol, including
            // one with a pin stacked on the anchor, must be clear of the whole label, as §6.4 rule 6 states.
            // KiCad measures a symbol as one rectangle around its body, pins and visible fields, so a field far from the
            // body (for example one dragged away) puts everything between them inside the symbol's own bounds.
            bool ownPinSymbol = variant == Variant.AnchorLabel && id == owner;
            if (r is { } labelBox && (ownPinSymbol ? Beyond(labelBox, a, outward, PinTargetReachNm) : labelBox).InteriorMeets(bounds))
                return ownPinSymbol ? "the label overlaps symbol " + Symbol(id) + " more than the pin target in front of the pin"
                    : id == owner ? "the label overlaps the bounds KiCad measures for its own symbol " + Symbol(id) + ", which take in all of that symbol's visible fields"
                    : "the label overlaps " + Item(id);
        }
        foreach (var envelope in screen.Envelopes)
        {
            if (SegmentMeets(a, e, envelope, open: false)) return "the stub crosses an earlier generated label";
            if (r is { } label && label.InteriorsOverlap(envelope)) return "the label overlaps an earlier generated label";
        }
        return null;

        // What a person reads: a symbol by its component's reference (U4, or TP802/TP803 for one drawn on a repeated sheet), and a
        // symbol's pin by that reference and its pin number (U4.4, TP802.1/TP803.1); an item without a reference (a wire, a sheet,
        // a text) by its identity.
        string Symbol(Guid id) => screen.References.TryGetValue(id, out var references) ? string.Join("/", references) : id.ToString("D");
        string Item(Guid id) => screen.References.ContainsKey(id) ? "symbol " + Symbol(id) : "item " + id.ToString("D");
        string Describe(ForeignPoint point) => point.Kind switch
        {
            PointKind.Pin when point.OwnerSymbol is { } symbol && point.Number.Length != 0 && screen.References.TryGetValue(symbol, out var references) =>
                "pin " + string.Join("/", references.Select(r => r + "." + point.Number)),
            PointKind.Pin => "pin " + (point.Number.Length != 0 ? point.Number : point.Owner.ToString("D"))
                + (point.OwnerSymbol is { } symbol ? " of symbol " + Symbol(symbol) : ""),
            PointKind.Generated => "a generated connection point",
            _ => point.Kind.ToString().ToLowerInvariant() + " point of " + point.Owner.ToString("D")
        };
    }

    // The part of rectangle r at least `band` in front of a along the outward direction.
    private static Box Beyond(Box r, Pt a, (int Dx, int Dy) outward, long band) => outward switch
    {
        (1, 0) => r with { L = Math.Max(r.L, checked(a.X + band)) },
        (-1, 0) => r with { R = Math.Min(r.R, checked(a.X - band)) },
        (0, 1) => r with { T = Math.Max(r.T, checked(a.Y + band)) },
        (0, -1) => r with { B = Math.Min(r.B, checked(a.Y - band)) },
        _ => throw Error(SchematicConnectionErrors.RealizationPinGeometryMismatch, "A generated label faces along one schematic axis.")
    };

    // Whether the axis-aligned stub from a to e meets the rectangle only within `band` of a.
    private static bool OnlyWithin(Pt a, Pt e, Box box, long band)
    {
        var toward = new Pt(Math.Sign(e.X - a.X), Math.Sign(e.Y - a.Y));
        long length = Math.Abs(e.X - a.X) + Math.Abs(e.Y - a.Y);
        if (length <= band) return true;
        return !SegmentMeets(new Pt(checked(a.X + toward.X * (band + 1)), checked(a.Y + toward.Y * (band + 1))), e, box, open: false);
    }

    /// <summary>The §6.4 verdict for a label placed on a measured pin itself (the anchor label that names an existing
    /// connection, §6.3 (a)) on a real measured sheet: null when the realizer admits it, otherwise the rule that refuses
    /// it. <paramref name="native"/> is the checkpoint's copy of the sheet, <paramref name="measured"/> the editor's
    /// measurement of it, <paramref name="label"/> the label's measured envelope at the pin, and
    /// <paramref name="connection"/> the identities of the pin's existing connection on this sheet. The placement-geometry
    /// journey applies the realizer's own rule with it to every visible pin of a live sheet.</summary>
    internal static string? AnchorLabelRefusal(SchematicScreenData native, SchematicPlacementGeometry measured, Guid owner,
        SchematicPinAnchor pin, Box2 label, IReadOnlyCollection<Guid> connection, SchematicConnectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(policy);
        var view = new PathView { Path = PathOf(native.Metadata.Document.SheetPath), Native = native, Planned = native };
        Merge(view, measured);
        var screenId = Id(measured.ScreenId);
        var screen = new Screen { Record = new(screenId, [view.Path], []), Views = [view] };
        BuildGeometry(screen, policy);
        var island = new IslandState(new ConnectionIsland(Guid.Empty, Guid.Empty, view.Path, screenId, ConnectionScope.Local, "",
            [], [.. connection], false, true, [], null, []));
        island.Same.Add(Id(pin.Id));
        var a = Pt.Of(pin.Position);
        return Refusal(policy, screen, island, a, a, SchematicConnectionGeometry.Outward(pin), Box.Of(label), Id(pin.Id), owner, null, null,
            Variant.AnchorLabel);
    }

    // ---- one realization ----

    private enum PointKind { Pin, WireEnd, Junction, NoConnect, BusEntry, Label, SheetPin, Generated }

    // A connection point on the sheet; a symbol's pin also carries its pin number, by which a reason a person reads names it.
    private sealed record ForeignPoint(Pt Position, PointKind Kind, Guid Owner, Guid? OwnerSymbol, string Number = "");

    private sealed record ForeignSegment(Pt A, Pt B, Guid Owner, bool Bus = false);

    private sealed record Combo(ConnectionLabelKind Kind, string Text, SchematicLabelSpinStyle Spin)
    {
        public SchematicLabelShape Shape => Kind == ConnectionLabelKind.Local ? SchematicLabelShape.SlshUnknown : SchematicLabelShape.SlshPassive;
    }

    private enum Variant { Stub, JoinStub, AnchorLabel }

    private sealed class PathView
    {
        public required string Path { get; init; }
        public required SchematicScreenData Native { get; init; }
        public required SchematicScreenData Planned { get; init; }
        public Box? Page { get; set; }
        /// <summary>Whether a measurement of this instance was merged yet.</summary>
        public bool Measured { get; set; }
        /// <summary>The drawing sheet KiCad draws on this instance; null when it was not measured.</summary>
        public SchematicWiringDrawingSheet? DrawingSheet { get; set; }
        /// <summary>Whether every measurement of this instance reported its symbols' visible field bounds.</summary>
        public bool FieldsReported { get; set; }
        public Dictionary<Guid, SchematicPlacementBounds> Obstacles { get; } = [];
        public Dictionary<Guid, SchematicPlacementBounds> Candidates { get; } = [];
        public Dictionary<Guid, SchematicSymbolPinGeometry> Pins { get; } = [];
    }

    private sealed class IslandState(ConnectionIsland island)
    {
        public ConnectionIsland Island { get; } = island;
        public List<Guid> Generated { get; } = [];
        public HashSet<Guid> Same { get; } = [.. island.AnchorItemIds, .. island.Members.Select(m => m.Pin.PlacedPinId)];
        public HashSet<Pt> Anchored { get; } = [];
        public bool Attached { get; set; }
        public bool Joined { get; set; }
        public bool UplinkLabelled { get; set; }
        /// <summary>New pins stacked on an existing connection whose optional stub for the hierarchical label had no room.</summary>
        public List<ConnectionPlacedPin> UplinkRefused { get; } = [];
        /// <summary>Stubs (by pin, or by sheet pin name) that had no room for the island's hierarchical label and carry a local
        /// label instead.</summary>
        public List<string> HierarchicalRefused { get; } = [];
        /// <summary>How the island was drawn: routed wires (§7) or label stubs (§6).</summary>
        public ConnectionRealizationStrategy Strategy { get; set; } = ConnectionRealizationStrategy.LabelStub;
        /// <summary>Why an island §7 applies to was drawn with label stubs instead.</summary>
        public string? FallbackReason { get; set; }
    }

    private sealed class Screen
    {
        public required ConnectionScreen Record { get; init; }
        public required List<PathView> Views { get; init; }
        public Box Page { get; set; }
        public Box Usable { get; set; }
        /// <summary>Where routed wires may run (§7 region erratum, <see cref="RouteArea"/>): the drawing sheet's frame interior
        /// less the clearance, on every instance.</summary>
        public Box RouteRegion { get; set; }
        /// <summary>The drawing-sheet art inside the frame (the title block): keep-outs of routed wires, inflated by the
        /// clearance.</summary>
        public List<Box> RouteKeepOuts { get; } = [];
        /// <summary>Whether every measurement of the sheet reported its symbols' visible field bounds.</summary>
        public bool FieldsKnown { get; set; }
        /// <summary>The visible field boxes of each symbol, on every instance.</summary>
        public Dictionary<Guid, List<Box>> Fields { get; } = [];
        public Dictionary<Guid, Box> Obstacles { get; } = [];
        /// <summary>Wires and buses on the sheet: segments the router crosses or avoids by its own rules, not obstacles.</summary>
        public HashSet<Guid> ConnectionLines { get; } = [];
        public List<ForeignPoint> Points { get; } = [];
        public List<ForeignSegment> Segments { get; } = [];
        public List<Box> Envelopes { get; } = [];
        public Dictionary<Combo, Box> Prototypes { get; } = [];
        public List<IMessage> Items { get; } = [];
        /// <summary>How a reason a person reads names a placed symbol: by the references the planned design gives the components
        /// it draws (U4; TP802/TP803 on a repeated sheet). Empty where no planned design names the symbols (<see cref="AnchorLabelRefusal"/> judges a live sheet on its
        /// own), which then names them by identity.</summary>
        public IReadOnlyDictionary<Guid, string[]> References { get; init; } = new Dictionary<Guid, string[]>();
    }

    private sealed class Run(SchematicConnectionIntent intent, SchematicDesign candidate, CheckedSchematicState checkpoint,
        Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> measure,
        SchematicConnectionPolicy policy, SchematicRoutingLimits limits, CancellationToken token)
    {
        private static readonly TypeRegistry Registry = TypeRegistry.FromFiles(SchematicText.Descriptor.File,
            SchematicPlacementGeometry.Descriptor.File);
        private readonly Dictionary<string, SchematicScreenData> native = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SchematicScreenData> planned = new(StringComparer.Ordinal);
        private readonly HashSet<Guid> used = [];
        private readonly HashSet<Guid> created = [.. intent.CreatedSymbolIds];
        private readonly Dictionary<(string Path, Guid Pin), int> groupOf = [];
        private readonly Dictionary<Guid, List<SheetPin>> sheetPins = [];
        private readonly List<GeneratedConnectionItem> generated = [];
        private readonly List<ConnectionIslandOutcome> outcomes = [];
        private readonly List<ConnectionDiagnostic> diagnostics = [];
        private readonly SortedSet<string> limitations = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, Screen> screens = [];
        // Component references by component identity and by the native symbol that places them, for Describe and DescribeSymbol.
        private readonly Dictionary<Guid, string> references = candidate.Engineering.Circuit.Components
            .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Reference);
        private readonly Dictionary<Guid, string[]> symbolReferences = SymbolReferences(candidate);
        private readonly KiCad.Automation.Protocol.DocumentRevision revision = checkpoint.State?.Revision ?? new();
        // Why the last stub, join stub or anchor label that was tried was refused, for the refusal a person reads.
        private string? lastRefusal;

        public async Task<SchematicConnectionRealization> ExecuteAsync()
        {
            if (intent.Version != SchematicConnectionIntent.CurrentVersion)
                throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "This build realizes connection intent version "
                    + SchematicConnectionIntent.CurrentVersion.ToString(CultureInfo.InvariantCulture) + " only.");
            var data = checkpoint.Electrical?.Hierarchy?.Data;
            if (checkpoint.State?.Revision is null || data is null)
                throw Error(SchematicConnectionErrors.RealizationMeasurementStale, "The native checkpoint has no revision or captured hierarchy.");
            if (revision.Epoch != intent.NativeRevision.Epoch || revision.Sequence != intent.NativeRevision.Sequence)
                throw Error(SchematicConnectionErrors.RealizationMeasurementStale, "The native checkpoint is not the revision the connections were planned for.");
            foreach (var screen in data.Instances) native.Add(PathOf(screen.Metadata.Document.SheetPath), screen);
            foreach (var screen in candidate.Schematic.Instances) planned.Add(PathOf(screen.Metadata.Document.SheetPath), screen);
            Collect(data, used);
            Collect(candidate.Schematic, used);
            for (int i = 0; i < intent.ExpectedGroups.Count; i++)
                foreach (var key in intent.ExpectedGroups[i])
                    if (!groupOf.TryAdd((key.SheetPathKey, key.PlacedPinId), i))
                        throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "A placed pin appears in two expected groups.");
            foreach (var screen in intent.Screens.OrderBy(s => s.ScreenId))
            {
                token.ThrowIfCancellationRequested();
                await RealizeScreen(screen);
            }
            return Assemble(data);
        }

        // ---- §6.2 measurement ----

        private async Task RealizeScreen(ConnectionScreen record)
        {
            if (record.InstancePathKeys.Count == 0)
                throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "A connection screen has no instance path.");
            var views = new List<PathView>();
            foreach (var path in record.InstancePathKeys)
            {
                if (!native.TryGetValue(path, out var nativeScreen) || !planned.TryGetValue(path, out var plannedScreen)
                    || Id(nativeScreen.Metadata.ScreenId) != record.ScreenId || Id(plannedScreen.Metadata.ScreenId) != record.ScreenId)
                    throw Error(SchematicConnectionErrors.RealizationMeasurementStale, "Sheet instance " + path
                        + " is not the planned screen in the native checkpoint.");
                views.Add(new PathView { Path = path, Native = nativeScreen, Planned = plannedScreen });
            }
            var screen = new Screen { Record = record, Views = views, References = symbolReferences };
            screens.Add(record.ScreenId, screen);
            // Round 1: every instance path, with the created symbols on that screen as candidates.
            foreach (var view in views)
            {
                var createdHere = view.Planned.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                    .Where(s => created.Contains(Id(s.Id))).OrderBy(s => s.Id.Value, StringComparer.Ordinal).ToArray();
                var chunks = createdHere.Length == 0 ? [Array.Empty<SchematicSymbolInstance>()] : createdHere.Chunk(MaxMeasuredCandidates).ToArray();
                foreach (var chunk in chunks)
                {
                    var request = Request(view);
                    request.Candidates.Add(chunk.Select(s => s.Clone()));
                    var measured = await Measure(request);
                    Validate(request, measured, view, screen.Record.ScreenId);
                    Merge(view, measured);
                }
            }
            BuildGeometry(screen, policy);
            List<IslandState> islands = [.. record.Islands.OrderBy(i => i.NetId).ThenBy(i => i.SheetPathKey, StringComparer.Ordinal)
                .Select(i => new IslandState(i))];
            foreach (var island in islands)
                if (island.Island.SheetPathKey != views[0].Path || island.Island.ScreenId != record.ScreenId)
                    throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "A connection island is not on its screen's representative path.");
            RequirePins(screen, islands);
            ConfirmPower(screen, islands);
            PreexistingContacts(screen, islands);
            await Prototypes(screen, islands);
            // §6.3 (a) and (b): joins and pin stubs, island by island. Islands whose labels are all fixed come first; islands
            // that still need their hierarchical label follow, in the same order, because any of their stubs may carry that
            // label: the first one with room for it (KindFor, MemberStub). Otherwise a hierarchical label drawn first beside a
            // neighbouring pin could leave that pin's fixed label no room at any stub length (a CN-1 §6.3 clarification
            // requested from the integration owner). §7: at its turn in that order, an island with two or more new pins to join
            // on this sheet is routed instead; one that no route fits falls back to its stubs at once, with the reason recorded.
            // Then §6.3 (c) and §6.5 sheet pins, and the check that every island crossing into its parent carries its
            // hierarchical label.
            // Routing never refuses what label stubs alone would draw (a CN-1 §7 clarification reported to the integration owner).
            // A route keeps clear the one-grid escape of every new pin still to be routed, and the shortest stub and its label of
            // every island still to be drawn with stubs (Reserved). When anything is still refused for want of room (no stub, no
            // join anchor, no sheet-pin slot) after a route was drawn on this sheet, the sheet is drawn again from the start with
            // all the milestone-1 room of the refused island kept clear of every route: each stub length with each label it may
            // carry, its join and anchor-label options, and its sheet-pin slots with their stubs and labels. Each redraw protects
            // one more island; when a protected island is refused again, the sheet is drawn once more with no route at all, every
            // island that would have been routed recording why, and only a refusal of that drawing stands.
            var start = Snapshot(screen);
            var protectedRoom = new HashSet<Guid>();
            string? abandoned = null;
            while (true)
            {
                islands = [.. record.Islands.OrderBy(i => i.NetId).ThenBy(i => i.SheetPathKey, StringComparer.Ordinal).Select(i => new IslandState(i))];
                IslandState? current = null;
                bool routed = false;
                try
                {
                    var ordered = islands.OrderBy(i => UplinkPending(i) ? 1 : 0).ToList();
                    var joinable = ordered.Where(i => RouteTerminals(screen, i).Count >= 2).ToHashSet();
                    var routable = abandoned is null ? joinable : [];
                    for (int turn = 0; turn < ordered.Count; turn++)
                    {
                        token.ThrowIfCancellationRequested();
                        var island = current = ordered[turn];
                        if (routable.Contains(island))
                        {
                            var later = ordered.Skip(turn + 1).ToArray();
                            var shortRoom = later.Where(i => !routable.Contains(i) && !protectedRoom.Contains(i.Island.NetId)).ToArray();
                            var fullRoom = later.Where(i => protectedRoom.Contains(i.Island.NetId)).ToArray();
                            // Sheet pins are drawn after every island, so the sheet-pin room of a protected island drawn earlier,
                            // or of this island itself, is kept as well.
                            var sheetRoom = ordered.Take(turn + 1).Where(i => protectedRoom.Contains(i.Island.NetId)).ToArray();
                            var escapes = later.Where(routable.Contains).SelectMany(i => RouteTerminals(screen, i).SelectMany(t => new[] { t.Terminal.Escape(policy.GridNm),
                                t.Terminal.Anchor.Step(t.Terminal.Outward, (Corridor(screen, i, t.Terminal).Steps ?? 1) * policy.GridNm) })).Distinct();
                            if (TryRoute(screen, island, shortRoom, fullRoom, sheetRoom, escapes)) { routed = true; continue; }
                        }
                        else if (abandoned is not null && joinable.Contains(island)) island.FallbackReason = abandoned;
                        Stubs(screen, island);
                    }
                    // §6.3 (c) and §6.5: sheet pins, allocated in port-text order.
                    foreach (var (island, sheet, port) in SheetPinOrder(screen, islands))
                    {
                        current = island;
                        SheetPinStub(screen, island, sheet, port);
                    }
                    foreach (var island in islands)
                    {
                        current = island;
                        RequireUplinkLabel(island);
                    }
                    break;
                }
                catch (AutomationException refusal) when (routed && current is not null && refusal.Code is SchematicConnectionErrors.RealizationNoFreeStub
                    or SchematicConnectionErrors.RealizationNoJoinAnchor or SchematicConnectionErrors.RealizationNoFreeSheetPinSlot)
                {
                    if (!protectedRoom.Add(current.Island.NetId))
                        abandoned = "wires are not drawn on sheet " + record.InstancePathKeys[0] + " because routes left net '" + NetName(current)
                            + "' no room even with its label-stub room kept clear (" + refusal.Message + ")";
                    Restore(screen, start);
                }
            }
            foreach (var island in islands)
            {
                outcomes.Add(new(island.Island.NetId, record.ScreenId, views[0].Path, island.Strategy,
                    island.Generated.ToArray(), island.Attached, island.FallbackReason));
                if (island.Joined)
                    diagnostics.Add(new(SchematicConnectionErrors.ExistingNetNamedByRealization, "info", island.Island.NetId, null,
                        "An existing unlabelled connection of net '" + NetName(island) + "' is named by a generated label on sheet "
                        + island.Island.SheetPathKey + "."));
            }
            if (islands.Count != 0)
                diagnostics.Add(new(SchematicConnectionErrors.RealizationPageReservationsUnspecified, "info", null, null,
                    "The drawing-sheet title block on screen " + record.ScreenId.ToString("D")
                    + " is not a schematic item; routed wires keep inside the drawing sheet's frame and clear of its title block as KiCad measures"
                    + " them, and label stubs only inside the page inset."));
        }

        // §6.3 (d) with (e): the island's first stub carries its hierarchical label unless that stub attaches to a same-net power
        // symbol, which leaves no room for a label (the power pin sits on the stub's line, so every longer stub runs through it), or
        // is the optional stub of a new pin stacked on an existing connection that has no room; then the next labelled stub carries
        // it. When no stub can carry it, the refusal names why (a CN-1 clarification requested from the integration owner).
        private void RequireUplinkLabel(IslandState island)
        {
            if (!UplinkPending(island)) return;
            throw island.HierarchicalRefused.Count != 0
                ? Error(SchematicConnectionErrors.RealizationNoFreeStub, "Net '" + NetName(island) + "' needs a hierarchical label on sheet "
                    + island.Island.SheetPathKey + ", but none of its new stubs there has room for one (so the stub of " + string.Join(", ", island.HierarchicalRefused)
                    + (island.HierarchicalRefused.Count == 1 ? " carries a local label" : " carry local labels") + " instead)" + (island.UplinkRefused.Count != 0 ? " and new pin " + string.Join(", ", island.UplinkRefused.Select(Describe))
                    + " sits on an existing connection with no free room for a stub" : "") + ". Clear the space next to one of those pins in the schematic editor.")
                : island.UplinkRefused.Count != 0
                ? Error(SchematicConnectionErrors.RealizationNoFreeStub, "Net '" + NetName(island) + "' needs a hierarchical label on sheet "
                    + island.Island.SheetPathKey + ", but new pin " + string.Join(", ", island.UplinkRefused.Select(Describe))
                    + " sits on an existing connection with no free room for a stub carrying that label, and no other new stub or sheet pin there "
                    + "can carry it" + (island.Attached ? " (every other new stub ends on one of its power symbols)" : "")
                    + ". Clear the space next to that pin in the schematic editor.")
                : island.Attached
                ? Error(SchematicConnectionErrors.RealizationNoFreeStub, "Net '" + NetName(island) + "' needs a hierarchical label on sheet "
                    + island.Island.SheetPathKey + ", but every new stub there ends on one of its power symbols, so none has room for the label. "
                    + "Move a power symbol away from the new pins in the schematic editor.")
                : Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "The plan crosses net '" + NetName(island)
                    + "' into its parent sheet but gives sheet " + island.Island.SheetPathKey + " nothing to carry its hierarchical label.");
        }

        // Everything drawing a screen's islands adds, so that the screen can be drawn again from the same start.
        private sealed record ScreenStart(int Items, int Points, int Segments, int Envelopes, int Generated, HashSet<Guid> Used,
            Dictionary<Guid, int> SheetPins);

        private ScreenStart Snapshot(Screen screen) =>
            new(screen.Items.Count, screen.Points.Count, screen.Segments.Count, screen.Envelopes.Count, generated.Count, [.. used],
                sheetPins.ToDictionary(p => p.Key, p => p.Value.Count));

        private void Restore(Screen screen, ScreenStart start)
        {
            screen.Items.RemoveRange(start.Items, screen.Items.Count - start.Items);
            screen.Points.RemoveRange(start.Points, screen.Points.Count - start.Points);
            screen.Segments.RemoveRange(start.Segments, screen.Segments.Count - start.Segments);
            screen.Envelopes.RemoveRange(start.Envelopes, screen.Envelopes.Count - start.Envelopes);
            generated.RemoveRange(start.Generated, generated.Count - start.Generated);
            used.Clear();
            used.UnionWith(start.Used);
            foreach (var sheet in sheetPins.Keys.ToArray())
            {
                int kept = start.SheetPins.GetValueOrDefault(sheet);
                if (kept == 0) sheetPins.Remove(sheet);
                else sheetPins[sheet].RemoveRange(kept, sheetPins[sheet].Count - kept);
            }
        }

        private MeasureSchematicPlacement Request(PathView view) => new()
        {
            Document = view.Native.Metadata.Document.Clone(),
            ExpectedRevision = revision.Clone()
        };

        // Every refusal by the editor's measurement becomes a §13 realization code; none escapes as a raw native error.
        private async Task<SchematicPlacementGeometry> Measure(MeasureSchematicPlacement request)
        {
            string sheet = PathOf(request.Document.SheetPath);
            try { return await measure(request, token) ?? throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "The editor returned no measurement."); }
            catch (NativeApiException error) when (error.Message.Contains("unsupported fields", StringComparison.Ordinal)
                || error.Status == UnhandledStatus)
            { throw Error(SchematicConnectionErrors.RealizationMeasurementUnsupported, "This KiCad cannot measure sheet " + sheet + " for generated connections: " + error.Message); }
            catch (NativeApiException error) when (error.Message.Contains("revision", StringComparison.Ordinal)
                || error.Message.Contains("changed during", StringComparison.Ordinal) || error.Status is BusyStatus or NotReadyStatus)
            { throw Error(SchematicConnectionErrors.RealizationMeasurementStale, "The schematic changed or was busy before sheet " + sheet + " could be measured: " + error.Message); }
            // A symbol whose library definition KiCad cannot resolve is measured by its own drawn bounds with its pins reported
            // incomplete (SPGIR_DEFINITION_UNRESOLVED); that is refused (RequirePins, Anchor) only when a pin of that symbol is
            // to be connected, so such a symbol no longer surfaces here as a refusal of the whole sheet.
            catch (NativeApiException error)
            { throw Error(SchematicConnectionErrors.RealizationMeasurementUnsupported, "KiCad refused to measure sheet " + sheet + " for generated connections: " + error.Message); }
        }

        // Native statuses of refusals that do not describe the measurement itself.
        private const int NotReadyStatus = (int)Kiapi.Common.ApiStatusCode.AsNotReady, UnhandledStatus = (int)Kiapi.Common.ApiStatusCode.AsUnhandled,
            BusyStatus = (int)Kiapi.Common.ApiStatusCode.AsBusy;

        private void Validate(MeasureSchematicPlacement request, SchematicPlacementGeometry measured, PathView view, Guid screenId)
        {
            if (!Equals(request.Document, measured.Document) || !Equals(request.ExpectedRevision, measured.Revision)
                || !TryId(measured.ScreenId, out var measuredScreen) || measuredScreen != screenId)
                throw Error(SchematicConnectionErrors.RealizationMeasurementStale, "A measurement of sheet " + view.Path
                    + " does not identify the requested sheet, screen and revision.");
            var expected = SchematicItemDelta.Index(view.Native.Items).Where(p => p.Value is not Group).Select(p => p.Key).ToHashSet();
            var obstacles = measured.Obstacles.Select(o => TryId(o.Id, out var id) ? id : Guid.Empty).ToArray();
            var candidates = measured.Candidates.Select(c => TryId(c.Id, out var id) ? id : Guid.Empty).ToArray();
            bool complete = obstacles.Distinct().Count() == obstacles.Length && expected.SetEquals(obstacles)
                && candidates.Distinct().Count() == candidates.Length
                && request.Candidates.Select(c => Id(c.Id)).ToHashSet().SetEquals(candidates)
                && measured.Candidates.All(c => Equals(c.Anchor, request.Candidates.Single(r => r.Id.Equals(c.Id)).Position))
                && measured.ItemCandidates.Count == request.ItemCandidates.Count
                && measured.PageBounds is not null && ValidBox(measured.PageBounds)
                && measured.Obstacles.Concat(measured.Candidates).Concat(measured.ItemCandidates).All(b => b.Anchor is not null && ValidBox(b.Bounds)
                    && b.VisibleFieldBounds.All(f => ValidBox(f) && Box.Of(f).Within(Box.Of(b.Bounds))))
                && measured.ItemCandidates.All(b => b.VisibleFieldBounds.Count == 0)
                && (measured.DrawingSheet is null || (ValidBox(measured.DrawingSheet.MarginFrame) && measured.DrawingSheet.Items.All(i => ValidBox(i.Bounds))));
            for (int i = 0; complete && i < request.ItemCandidates.Count; i++)
            {
                var prototype = UnpackItem(request.ItemCandidates[i]);
                complete = Equals(measured.ItemCandidates[i].Id, IdOf(prototype)) && Equals(measured.ItemCandidates[i].Anchor, PositionOf(prototype))
                    && measured.ItemCandidates[i].SymbolPins is null;
            }
            if (!complete)
                throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "The measurement of sheet " + view.Path
                    + " does not report every existing item and requested candidate exactly once.");
            if (!measured.PinGeometryAvailable)
                throw Error(SchematicConnectionErrors.RealizationMeasurementUnsupported, "This KiCad does not report exact pin geometry.");
            foreach (var limitation in measured.Limitations) limitations.Add(limitation);
        }

        // §6.2: the pins realization draws from or confirms must be completely measured and identical on every instance.
        // Every created symbol must report complete pins as well, even one with no connection: §6.3 (g) must prove
        // that none of its pins lands on an existing connection point, which it cannot do for pins it cannot see.
        private void RequirePins(Screen screen, List<IslandState> islands)
        {
            foreach (var view in screen.Views)
            foreach (var symbol in view.Candidates.Keys.Order())
            {
                if (!view.Pins.TryGetValue(symbol, out var geometry))
                    throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "The measurement of sheet " + view.Path
                        + " has no pin geometry for new symbol " + DescribeSymbol(symbol) + ".");
                if (!geometry.Complete)
                    throw Error(geometry.IncompleteReason == SchematicPinGeometryIncompleteReason.SpgirVariantPinMappingUnresolved
                            ? SchematicConnectionErrors.RealizationVariantPinIdentityUnresolved : SchematicConnectionErrors.RealizationPinGeometryIncomplete,
                        "KiCad cannot report exact pin positions for new symbol " + DescribeSymbol(symbol) + " on sheet " + view.Path
                        + ", so it cannot prove the symbol touches no existing connection"
                        + (geometry.Limitations.Count == 0 ? "." : ": " + string.Join("; ", geometry.Limitations)));
            }
            foreach (var island in islands)
            {
                var pins = island.Island.Members.Select(m => m.Pin).Concat(island.Island.JoinCandidates).DistinctBy(p => (p.SymbolId, p.PlacedPinId));
                foreach (var pin in pins)
                {
                    SchematicPinAnchor? first = null;
                    foreach (var view in screen.Views)
                    {
                        var anchor = Anchor(view, pin);
                        if (first is null) { first = anchor; continue; }
                        if (!Equals(first.Position, anchor.Position) || first.BodyDirectionX != anchor.BodyDirectionX
                            || first.BodyDirectionY != anchor.BodyDirectionY || first.Unit != anchor.Unit || first.BodyStyle != anchor.BodyStyle)
                            throw Error(SchematicConnectionErrors.RealizationPinGeometryMismatch, "Pin " + Describe(pin)
                                + " is drawn differently on instances of one repeated sheet, so one shared drawing cannot connect it.");
                    }
                    _ = SchematicConnectionGeometry.Outward(first!);
                }
            }
        }

        private SchematicPinAnchor Anchor(PathView view, ConnectionPlacedPin pin)
        {
            if (!view.Pins.TryGetValue(pin.SymbolId, out var geometry))
                throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "The measurement of sheet " + view.Path
                    + " has no pin geometry for the symbol of pin " + Describe(pin) + ".");
            if (!geometry.Complete)
                throw Error(geometry.IncompleteReason == SchematicPinGeometryIncompleteReason.SpgirVariantPinMappingUnresolved
                        ? SchematicConnectionErrors.RealizationVariantPinIdentityUnresolved : SchematicConnectionErrors.RealizationPinGeometryIncomplete,
                    "KiCad cannot report exact pin positions for the symbol of pin " + Describe(pin) + " on sheet " + view.Path
                    + (geometry.Limitations.Count == 0 ? "." : ": " + string.Join("; ", geometry.Limitations)));
            var anchor = geometry.Pins.FirstOrDefault(p => TryId(p.Id, out var id) && id == pin.PlacedPinId)
                ?? throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "KiCad does not report pin " + Describe(pin)
                    + " on sheet " + view.Path + ".");
            if (!TryId(anchor.LibraryPinId, out var library) || library != pin.LibraryPinId)
                throw Error(SchematicConnectionErrors.RealizationPinGeometryMismatch, "KiCad reports pin " + Describe(pin)
                    + " with another library pin identity than planned.");
            return anchor;
        }

        private static Pt At(Screen screen, ConnectionPlacedPin pin) => Pt.Of(AnchorOf(screen.Views[0], pin).Position);

        private static SchematicPinAnchor AnchorOf(PathView view, ConnectionPlacedPin pin) =>
            view.Pins[pin.SymbolId].Pins.First(p => p.Id.Value == pin.PlacedPinId.ToString("D"));

        // §6.2 native power confirmation: the editor's implicit connection of every member matches the plan.
        private void ConfirmPower(Screen screen, List<IslandState> islands)
        {
            foreach (var island in islands)
            foreach (var member in island.Island.Members)
            foreach (var view in screen.Views)
            {
                var anchor = Anchor(view, member.Pin);
                var (scope, name) = member.Role switch
                {
                    ConnectionMemberRole.PowerCarrier => (SymbolOf(view, member.Pin).Definition?.Type == SchematicSymbolType.SstLocalPower
                        ? SchematicPinPowerScope.SppsLocal : SchematicPinPowerScope.SppsGlobal, member.PowerName ?? ""),
                    ConnectionMemberRole.ImplicitPower => (SchematicPinPowerScope.SppsGlobal, member.PowerName ?? ""),
                    _ => (SchematicPinPowerScope.SppsNone, "")
                };
                if (anchor.PowerScope != scope || anchor.PowerNet != name)
                    throw Error(SchematicConnectionErrors.RealizationImplicitPowerMismatch, "KiCad connects pin " + Describe(member.Pin)
                        + " as " + Scope(anchor.PowerScope, anchor.PowerNet) + ", but the plan expects " + Scope(scope, name) + ".");
            }
            static string Scope(SchematicPinPowerScope scope, string name) => scope switch
            {
                SchematicPinPowerScope.SppsGlobal => "global power '" + name + "'",
                SchematicPinPowerScope.SppsLocal => "local power '" + name + "'",
                _ => "an ordinary pin"
            };
        }

        private SchematicSymbolInstance SymbolOf(PathView view, ConnectionPlacedPin pin)
        {
            foreach (var item in view.Planned.Items)
            {
                if (!item.Is(SchematicSymbolInstance.Descriptor)) continue;
                var symbol = item.Unpack<SchematicSymbolInstance>();
                if (TryId(symbol.Id, out var id) && id == pin.SymbolId) return symbol;
            }
            throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "The planned design has no symbol for pin " + Describe(pin) + ".");
        }

        // §6.3 (g): contacts that exist before anything is generated.
        private void PreexistingContacts(Screen screen, List<IslandState> islands)
        {
            var anchors = islands.SelectMany(i => i.Island.Members.Where(m => m.RequiresStub).Select(m => m.Pin).Concat(i.Island.JoinCandidates))
                .Select(p => (Pin: p, At: At(screen, p)));
            foreach (var (pin, at) in anchors)
                if (screen.Points.FirstOrDefault(p => p.Kind == PointKind.NoConnect && p.Position == at) is { } marker)
                    throw Error(SchematicConnectionErrors.RealizationNoConnectConflict, "Pin " + Describe(pin)
                        + " carries a no-connect marker (" + marker.Owner.ToString("D") + "), so it cannot be connected. Remove the marker in the schematic editor first.");
            foreach (var view in screen.Views)
            {
                var itemPoints = screen.Points.Where(p => p.Kind != PointKind.Pin).ToArray();
                var pinPoints = view.Pins.SelectMany(s => s.Value.Pins.Select(p => (Pin: Id(p.Id), At: Pt.Of(p.Position)))).ToArray();
                foreach (var symbol in view.Candidates.Keys)
                {
                    // RequirePins has proven every created symbol's pins complete on every instance path.
                    foreach (var pin in view.Pins[symbol].Pins)
                    {
                        Pt at = Pt.Of(pin.Position);
                        Guid id = Id(pin.Id);
                        groupOf.TryGetValue((view.Path, id), out int group);
                        bool grouped = groupOf.ContainsKey((view.Path, id));
                        bool contact = itemPoints.Any(p => p.Position == at)
                            || screen.Segments.Any(s => OnSegment(at, s.A, s.B))
                            || pinPoints.Any(p => p.Pin != id && p.At == at
                                && !(grouped && groupOf.TryGetValue((view.Path, p.Pin), out int other) && other == group));
                        if (contact)
                            throw Error(SchematicConnectionErrors.RealizationCreatedPinContact, "Created pin " + pin.Number + " of symbol "
                                + DescribeSymbol(symbol) + " on sheet " + view.Path + " lands on an existing connection point it must not join. Move the new symbol in the XML.");
                    }
                }
            }
        }

        // ---- §6.2 round 2: label prototypes ----

        private async Task Prototypes(Screen screen, List<IslandState> islands)
        {
            var positions = new Dictionary<Combo, Pt>();
            void Want(IEnumerable<ConnectionLabelKind> kinds, string text, (int Dx, int Dy) outward, Pt at)
            {
                foreach (var kind in kinds) positions.TryAdd(new(kind, text, SchematicConnectionGeometry.Spin(outward)), at);
            }
            long first = SchematicConnectionPolicy.StubMultiples[0] * policy.GridNm;
            foreach (var island in islands)
            {
                var kinds = Kinds(island.Island);
                foreach (var pin in island.Island.JoinCandidates)
                {
                    var anchor = AnchorOf(screen.Views[0], pin);
                    var outward = SchematicConnectionGeometry.Outward(anchor);
                    Want(kinds, island.Island.LabelText, outward, Pt.Of(anchor.Position).Step(outward, first));
                }
                foreach (var member in island.Island.Members.Where(m => m.RequiresStub))
                {
                    var anchor = AnchorOf(screen.Views[0], member.Pin);
                    var outward = SchematicConnectionGeometry.Outward(anchor);
                    Want(kinds, island.Island.LabelText, outward, Pt.Of(anchor.Position).Step(outward, first));
                }
                foreach (var sheetId in island.Island.ChildSheetSymbolIds)
                {
                    var sheet = SheetOf(screen, sheetId);
                    var (side, x) = Side(screen, island, sheet);
                    var outward = side == SheetSide.ShsLeft ? (-1, 0) : (1, 0);
                    Want(kinds, island.Island.LabelText, outward, new Pt(x, checked(sheet.Position.YNm + policy.SheetPinPitchNm)).Step(outward, first));
                }
            }
            if (positions.Count == 0) return;
            var combos = positions.Keys.OrderBy(c => c.Kind).ThenBy(c => c.Text, StringComparer.Ordinal).ThenBy(c => c.Spin).ToArray();
            var ids = new Dictionary<Combo, Guid>();
            foreach (var combo in combos)
            {
                var id = SchematicConnectionIdentity.Probe(intent.NativeRevision, screen.Record.ScreenId, Descriptor(combo.Kind), combo.Text, combo.Spin, combo.Shape);
                SchematicConnectionIdentity.Claim(used, id);
                ids.Add(combo, id);
            }
            foreach (var view in screen.Views)
            {
                foreach (var chunk in combos.Chunk(MaxMeasuredCandidates))
                {
                    var request = Request(view);
                    request.ItemCandidates.Add(chunk.Select(c => Any.Pack(LabelPayload(c.Kind, ids[c], positions[c].Vector(), c.Text, c.Spin, policy))));
                    var measured = await Measure(request);
                    Validate(request, measured, view, screen.Record.ScreenId);
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        var relative = Box.Of(measured.ItemCandidates[i].Bounds).Relative(Pt.Of(measured.ItemCandidates[i].Anchor));
                        screen.Prototypes[chunk[i]] = screen.Prototypes.TryGetValue(chunk[i], out var prior) ? prior.Union(relative) : relative;
                    }
                }
            }
            // Orientation guard: a label may reach at most the tolerance behind its anchor.
            foreach (var (combo, envelope) in screen.Prototypes)
            {
                var (dx, dy) = Facing(combo.Spin);
                long behind = dx > 0 ? -envelope.L : dx < 0 ? envelope.R : dy > 0 ? -envelope.T : envelope.B;
                if (behind > policy.LabelBackToleranceNm)
                    throw Error(SchematicConnectionErrors.RealizationLabelOrientationMismatch, "KiCad draws a " + combo.Kind.ToString().ToLowerInvariant()
                        + " label '" + combo.Text + "' facing " + combo.Spin + " " + behind.ToString(CultureInfo.InvariantCulture)
                        + " nm behind its anchor, so a stub cannot keep it clear of the pin.");
            }
        }

        private static IReadOnlyList<ConnectionLabelKind> Kinds(ConnectionIsland island) => island.Scope == ConnectionScope.Global
            ? [ConnectionLabelKind.Global]
            : island.UplinkSheetSymbolId is null ? [ConnectionLabelKind.Local] : [ConnectionLabelKind.Local, ConnectionLabelKind.Hierarchical];

        // §6.3 (d): global names use global labels; the first labelled stub of an uplinking island carries the
        // hierarchical label and every other local stub a local label of the same text.
        private static ConnectionLabelKind KindFor(IslandState island) => island.Island.Scope == ConnectionScope.Global ? ConnectionLabelKind.Global
            : UplinkPending(island) ? ConnectionLabelKind.Hierarchical : ConnectionLabelKind.Local;

        // Whether the island crosses into its parent sheet and no generated label carries that crossing yet.
        private static bool UplinkPending(IslandState island) =>
            island.Island.Scope != ConnectionScope.Global && island.Island.UplinkSheetSymbolId is not null && !island.UplinkLabelled;

        // ---- §6.3 algorithm ----

        private void Join(Screen screen, IslandState island)
        {
            var refused = new List<string>();
            // A join that would carry the island's hierarchical label and has no room for it at any candidate names the
            // connection with a local label instead; a later stub of the island then carries the hierarchical label.
            ConnectionLabelKind[] kinds = KindFor(island) == ConnectionLabelKind.Hierarchical
                ? [ConnectionLabelKind.Hierarchical, ConnectionLabelKind.Local] : [KindFor(island)];
            foreach (var kind in kinds)
            foreach (var pin in island.Island.JoinCandidates)
            {
                var anchor = AnchorOf(screen.Views[0], pin);
                var a = Pt.Of(anchor.Position);
                var outward = SchematicConnectionGeometry.Outward(anchor);
                var owner = pin.SymbolId;
                if (TryStub(screen, island, a, outward, pin.PlacedPinId, owner, Variant.JoinStub, kind, out var stub))
                {
                    if (kind != kinds[0]) island.HierarchicalRefused.Add("pin " + Describe(pin));
                    Accept(screen, island, stub, GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel,
                        SchematicConnectionIdentity.PinAnchorKey(pin.PlacedPinId), pin.PlacedPinId, null);
                    island.Joined = true;
                    return;
                }
                var combo = new Combo(kind, island.Island.LabelText, SchematicConnectionGeometry.Spin(outward));
                var envelope = screen.Prototypes[combo].Offset(a);
                if (Admit(screen, island, a, a, outward, envelope, pin.PlacedPinId, owner, null, null, Variant.AnchorLabel))
                {
                    if (kind != kinds[0]) island.HierarchicalRefused.Add("pin " + Describe(pin));
                    Accept(screen, island, new(a, a, combo, envelope, null), null, GeneratedConnectionRole.AnchorLabel,
                        SchematicConnectionIdentity.PinAnchorKey(pin.PlacedPinId), pin.PlacedPinId, null);
                    island.Joined = true;
                    return;
                }
                refused.Add("pin " + Describe(pin) + (kinds.Length > 1 ? " (" + kind.ToString().ToLowerInvariant() + " label)" : "") + ": " + lastRefusal);
            }
            throw Error(SchematicConnectionErrors.RealizationNoJoinAnchor, "Net '" + NetName(island) + "' must name its existing connection on sheet "
                + island.Island.SheetPathKey + ", but no existing pin of it has room for a label (a label on " + string.Join("; ", refused)
                + "). Make room next to one of its pins in the schematic editor.");
        }

        private void MemberStub(Screen screen, IslandState island, ConnectionMember member)
        {
            var anchor = AnchorOf(screen.Views[0], member.Pin);
            var a = Pt.Of(anchor.Position);
            // A pin stacked exactly on an earlier stub's pin of the same island is joined by that contact.
            if (island.Anchored.Contains(a)) return;
            var outward = SchematicConnectionGeometry.Outward(anchor);
            if (island.Island.Members.Any(m => m != member && m.AlreadyConnected && At(screen, m.Pin) == a))
            {
                // So is a pin stacked on an already connected pin of the same island: it needs nothing of its own. While
                // the island still needs its hierarchical label, a stub from this pin may carry it, starting on the
                // existing connection like a join stub and never ending on a power symbol (which would carry no label).
                // Without room for it, a later stub or sheet-pin stub carries the label; the attempt is kept so that the
                // final check names this pin when nothing does.
                if (!UplinkPending(island)) return;
                if (TryStub(screen, island, a, outward, member.Pin.PlacedPinId, member.Pin.SymbolId, Variant.JoinStub,
                        ConnectionLabelKind.Hierarchical, out var carrier, allowAttach: false))
                    Accept(screen, island, carrier, GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel,
                        SchematicConnectionIdentity.PinAnchorKey(member.Pin.PlacedPinId), member.Pin.PlacedPinId, null);
                else island.UplinkRefused.Add(member.Pin);
                return;
            }
            // A stub that would carry the island's hierarchical label and has no room for it at any length carries a local
            // label instead, and the island's next labelled stub carries the hierarchical label (§6.3 (d) clarification).
            var kind = KindFor(island);
            bool fits = TryStub(screen, island, a, outward, member.Pin.PlacedPinId, member.Pin.SymbolId, Variant.Stub, kind, out var stub);
            if (!fits && kind == ConnectionLabelKind.Hierarchical
                && TryStub(screen, island, a, outward, member.Pin.PlacedPinId, member.Pin.SymbolId, Variant.Stub, ConnectionLabelKind.Local, out stub))
            {
                island.HierarchicalRefused.Add("pin " + Describe(member.Pin));
                fits = true;
            }
            if (!fits)
                throw Error(SchematicConnectionErrors.RealizationNoFreeStub, "Pin " + Describe(member.Pin) + " of net '" + NetName(island)
                    + "' has no free room for a connection stub and label on sheet " + island.Island.SheetPathKey
                    + " (at the longest stub length tried, " + lastRefusal + "). Move the symbol or clear the space next to that pin.");
            Accept(screen, island, stub, GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel,
                SchematicConnectionIdentity.PinAnchorKey(member.Pin.PlacedPinId), member.Pin.PlacedPinId, null);
        }

        // §6.3 (a) and (b) for one island: its join, then a stub for each new pin.
        private void Stubs(Screen screen, IslandState island)
        {
            if (island.Island.JoinRequired) Join(screen, island);
            foreach (var member in island.Island.Members.Where(m => m.RequiresStub)) MemberStub(screen, island, member);
        }

        // ---- §7 orthogonal wires ----

        // The pins §7 joins on this island: its new pins (§6.3 (b)), one per point. A new pin stacked on a pin of the existing
        // connection is joined by that contact (as for stubs), and pins stacked on each other share one terminal, named by the
        // ordinal-first of them. Items counts the symbols with a pin at the point, as KiCad counts a point's connections.
        private List<(RouteTerminal Terminal, ConnectionPlacedPin Pin)> RouteTerminals(Screen screen, IslandState island)
        {
            var connected = island.Island.Members.Where(m => m.AlreadyConnected).Select(m => At(screen, m.Pin)).ToHashSet();
            var found = new Dictionary<Pt, (RouteTerminal Terminal, ConnectionPlacedPin Pin)>();
            foreach (var member in island.Island.Members.Where(m => m.RequiresStub))
            {
                var anchor = AnchorOf(screen.Views[0], member.Pin);
                var a = Pt.Of(anchor.Position);
                if (connected.Contains(a)) continue;
                if (found.TryGetValue(a, out var known)
                    && string.CompareOrdinal(known.Pin.PlacedPinId.ToString("D"), member.Pin.PlacedPinId.ToString("D")) <= 0) continue;
                int items = screen.Points.Where(p => p.Kind == PointKind.Pin && p.Position == a).Select(p => p.OwnerSymbol).Distinct().Count();
                found[a] = (new(member.Pin.PlacedPinId, a, SchematicConnectionGeometry.Outward(anchor), Math.Max(items, 1)), member.Pin);
            }
            return [.. found.Values];
        }

        // The symbols of the island that draw a pin exactly at `at`: the pin's own symbol and any symbol of the same island with a
        // pin stacked on it. A routed wire may cross their outline there, and only there (the escape corridor of a new pin, or
        // the one-grid attach wire at an already connected pin).
        private static Guid[] OwnersAt(Screen screen, IslandState island, Pt at) =>
            [.. screen.Points.Where(p => p.Kind == PointKind.Pin && p.Position == at && island.Same.Contains(p.Owner))
                .Select(p => p.OwnerSymbol).OfType<Guid>().Distinct().Order()];

        // E3 (decision nfa2005d67574bfea): a routed wire that runs from a pin at `a` straight out through the outline of its own
        // symbols (`owners`, see OwnersAt) to `end` keeps the clearance from every field those symbols paint, as KiCad measures
        // them. This holds for a new pin's escape corridor and for the one-grid attach wire at an already connected pin alike.
        // Null when it does; otherwise which field it comes too near, for the reason a person reads.
        private string? FieldTooNear(Screen screen, IReadOnlyList<Guid> owners, Pt a, Pt end)
        {
            foreach (var owner in owners)
                foreach (var field in screen.Fields.GetValueOrDefault(owner, []))
                    if (SegmentMeets(a, end, field.Inflate(policy.ClearanceNm), open: false))
                        return "symbol " + DescribeSymbol(owner) + "'s own field text at (" + Mm(field.L) + ", " + Mm(field.T) + ")-(" + Mm(field.R) + ", "
                            + Mm(field.B) + ") mm";
            return null;

            static string Mm(long nm) => (nm / 1_000_000m).ToString("0.####", CultureInfo.InvariantCulture);
        }

        // The escape corridor of a terminal (§7, E3 of decision nfa2005d67574bfea): the pin's straight way out through the outline
        // of its own symbol (and of any symbol of the same island with a pin stacked on it), which KiCad measures with the symbol's
        // visible fields, as far as the first grid node clear of that outline inflated by the clearance; a stub runs through the
        // same room (§6.4 rule 5). The outline stays an obstacle everywhere else (§7 "obstacle bounds"), so the corridor must reach
        // out of it. It never passes those symbols' own text: it keeps the clearance from each field they paint (FieldTooNear), or
        // the pin has no escape. The number of grid steps to that node, at least one (the escape), and at most the longest stub;
        // otherwise null and why.
        private (int? Steps, string? Blocked) Corridor(Screen screen, IslandState island, RouteTerminal terminal)
        {
            var owners = OwnersAt(screen, island, terminal.Anchor);
            var outlines = owners.Where(screen.Obstacles.ContainsKey).Select(o => screen.Obstacles[o].Inflate(policy.ClearanceNm)).ToArray();
            int longest = SchematicConnectionPolicy.StubMultiples[^1];
            int? found = null;
            for (int steps = 1; steps <= longest && found is null; steps++)
                if (!outlines.Any(o => o.Contains(terminal.Anchor.Step(terminal.Outward, steps * policy.GridNm)))) found = steps;
            if (found is not { } corridor) return (null, "is covered by its own symbol's outline for more than the longest stub");
            var end = terminal.Anchor.Step(terminal.Outward, corridor * policy.GridNm);
            if (FieldTooNear(screen, owners, terminal.Anchor, end) is { } field)
                return (null, "would leave its symbol within the clearance of " + field);
            return (corridor, null);
        }

        // §7: join the island's new pins with orthogonal wires named by one label at the first pin, or record why not (the island
        // then falls back to label stubs). Each pin's one-grid escape and the label stub at the first pin are admitted with the
        // §6.4 rules; the router keeps every other wire step clear of the sheet by the §7 rules. Nothing is kept unless the whole
        // island is routed.
        private bool TryRoute(Screen screen, IslandState island, IReadOnlyList<IslandState> shortRoom, IReadOnlyList<IslandState> fullRoom,
            IReadOnlyList<IslandState> sheetRoom, IEnumerable<Pt> escapes)
        {
            long grid = policy.GridNm;
            var found = RouteTerminals(screen, island);
            var terminals = SchematicOrthogonalRouter.Order(found.Select(f => f.Terminal), grid);
            var pins = found.ToDictionary(f => f.Terminal.PlacedPinId, f => f.Pin);
            bool Fallback(string reason)
            {
                island.FallbackReason = reason;
                return false;
            }
            // Without every symbol's field positions no wire can be kept off a symbol's own text (Corridor): fail closed.
            if (!screen.FieldsKnown)
                return Fallback("this KiCad does not report where symbol fields are drawn, so routed wires cannot be kept clear of symbol text");
            var corridors = new Dictionary<Guid, int>();
            foreach (var terminal in terminals)
            {
                var pin = pins[terminal.PlacedPinId];
                if (terminal.Anchor.X % grid != 0 || terminal.Anchor.Y % grid != 0)
                    return Fallback("pin " + Describe(pin) + " is not on the connection grid");
                var (steps, blocked) = Corridor(screen, island, terminal);
                if (steps is null) return Fallback("pin " + Describe(pin) + " " + blocked);
                corridors.Add(terminal.PlacedPinId, steps.Value);
                var escape = terminal.Escape(grid);
                if (!screen.RouteRegion.Contains(escape)) return Fallback("pin " + Describe(pin) + " leaves its symbol outside the drawing sheet's frame");
                if (Refusal(policy, screen, island, terminal.Anchor, escape, terminal.Outward, null, terminal.PlacedPinId, pin.SymbolId, null, null,
                        Variant.Stub, routed: true) is { } refused)
                    return Fallback("pin " + Describe(pin) + " has no room to leave its symbol by one grid step (" + refused + ")");
            }
            // The one label naming the connection sits on a stub from the tree's root: the global name, else the island's uplink
            // hierarchical label, else a local label with its name, which KiCad needs to give the net its XML name. E4 of decision
            // nfa2005d67574bfea: the root is the first terminal in (escape x, escape y, PlacedPinId) order whose name-label stub is
            // admissible in the route region and covers no other terminal's escape, neither by its wire nor by its label; the tree
            // grows from it and it carries the connection's one label; when no terminal qualifies the connection falls back to
            // label stubs. Every stub length is tried, in the §6.3 (f) order, until one is admissible and covers nothing, so a longer
            // stub that carries the label past another pin's escape still makes its pin the root. The label's stub reaches at least
            // one grid beyond the root's corridor, so that the tree has a node clear of the root's own symbol for the other pins to
            // join (a shorter stub leaves them only nodes inside that symbol's outline, or the label's own anchor; a CN-1 §7
            // wording requested from the integration owner).
            var kind = KindFor(island);
            Stub? labelled = null;
            RouteTerminal? root = null;
            var refusals = new List<string>();
            foreach (var candidate in terminals)
            {
                var candidatePin = pins[candidate.PlacedPinId];
                string? Covers(Stub admitted) => terminals.FirstOrDefault(t => t != candidate
                        && (SegmentMeets(t.Anchor, t.Escape(grid), admitted.Envelope!.Value, open: false)
                            || SegmentMeets(t.Anchor, t.Escape(grid), Box.Segment(admitted.A, admitted.E), open: false))) is { } covered
                    ? "its label stub would cover the escape of pin " + Describe(pins[covered.PlacedPinId])
                    : null;
                if (!TryStub(screen, island, candidate.Anchor, candidate.Outward, candidate.PlacedPinId, candidatePin.SymbolId, Variant.Stub, kind, out var tried,
                        allowAttach: false, minimumLength: (corridors[candidate.PlacedPinId] + 1) * grid, routed: true, reject: Covers))
                {
                    refusals.Add("pin " + Describe(candidatePin) + ": " + lastRefusal);
                    continue;
                }
                (labelled, root) = (tried, candidate);
                break;
            }
            if (labelled is not { } stub || root is null)
                return Fallback("the " + kind.ToString().ToLowerInvariant() + " label naming the connection has no room at any of its new pins ("
                    + string.Join("; ", refusals) + ")");
            var rootPin = pins[root.PlacedPinId];
            var envelope = stub.Envelope!.Value;
            terminals = [root, .. terminals.Where(t => t != root)];
            // An existing connection without the island's name (a join, §6.3 (a)) is reached by wire; one that already carries the
            // name is joined through the label.
            RouteExistingPart? existing = null;
            if (island.Island.JoinRequired)
            {
                existing = ExistingPart(screen, island);
                if (existing is null) return Fallback("the connection's existing wires and pins offer no free point to attach to");
            }
            var owners = new Dictionary<Guid, List<Pt>>();
            void Exempt(Guid symbol, params Pt[] nodes)
            {
                if (!owners.TryGetValue(symbol, out var list)) owners.Add(symbol, list = []);
                list.AddRange(nodes);
            }
            foreach (var terminal in terminals)
                foreach (var owner in OwnersAt(screen, island, terminal.Anchor))
                    Exempt(owner, [terminal.Anchor, .. Enumerable.Range(1, corridors[terminal.PlacedPinId]).Select(k => terminal.Anchor.Step(terminal.Outward, k * grid))]);
            if (existing is not null)
                foreach (var point in existing.Points.Where(p => p.Pin is not null))
                    foreach (var owner in OwnersAt(screen, island, point.Pin!.Value))
                        Exempt(owner, point.Pin!.Value, point.Node);
            var obstacles = screen.Obstacles.Where(o => !screen.ConnectionLines.Contains(o.Key)).OrderBy(o => o.Key)
                .Select(o => new RouteObstacle(o.Value, owners.TryGetValue(o.Key, out var exempt) ? exempt : []))
                .Concat(screen.RouteKeepOuts.Select(k => new RouteObstacle(k, []))).ToArray();
            // The one-grid escapes of the new pins still to be routed, and the shortest stub and its label at every pin of the islands
            // that will certainly be drawn with stubs, stay free (§7; a clarification reported to the integration owner: otherwise
            // one island's wires could take the only way out, or the only stub room, another island has). So does all the
            // milestone-1 room of every island a route crowded out on an earlier drawing of this sheet.
            var (reservedNodes, reservedLabels) = Reserved(screen, shortRoom, fullRoom, sheetRoom);
            reservedNodes.AddRange(escapes);
            var request = new OrthogonalRouteRequest(grid, policy.ClearanceNm, screen.RouteRegion, terminals, Length(stub), envelope, existing, obstacles,
                [.. screen.Points.Select(p => p.Position).Concat(reservedNodes).Distinct()],
                [.. screen.Segments.Select(s => new RouteSegment(s.A, s.B, !s.Bus && !island.Same.Contains(s.Owner)))],
                [.. screen.Envelopes, .. reservedLabels], limits);
            var result = SchematicOrthogonalRouter.Route(request, token);
            if (result.Route is not { } route)
            {
                // The router names pins by identity; the reason a person reads names them by component and pin number.
                string reason = result.FallbackReason ?? "no route joins its pins";
                foreach (var (id, pin) in pins) reason = reason.Replace(id.ToString("D"), Describe(pin), StringComparison.Ordinal);
                return Fallback(reason);
            }
            // Defence in depth: no junction may sit on anything foreign, which it would join.
            foreach (var junction in route.Junctions)
                if (screen.Points.Any(p => p.Position == junction && !island.Same.Contains(p.Owner))
                    || screen.Segments.Any(s => !island.Same.Contains(s.Owner) && OnSegment(junction, s.A, s.B)))
                    return Fallback("a junction at (" + junction.X.ToString(CultureInfo.InvariantCulture) + ", " + junction.Y.ToString(CultureInfo.InvariantCulture)
                        + ") would touch another connection");
            EmitRoute(screen, island, terminals, rootPin, stub, route);
            return true;

            static long Length(Stub s) => Math.Abs(s.E.X - s.A.X) + Math.Abs(s.E.Y - s.A.Y);
        }

        // The room label stubs need, kept clear of a route: for `shortRoom` islands the grid nodes of the shortest stub from each
        // new pin and join candidate and the label that stub would carry, in every kind the island may give it; for `fullRoom`
        // islands every stub length with its labels, a label on each join candidate's own pin, and the room of their sheet pins;
        // for `sheetRoom` islands (already drawn) the room of their sheet pins only.
        private (List<Pt> Nodes, List<Box> Labels) Reserved(Screen screen, IReadOnlyList<IslandState> shortRoom, IReadOnlyList<IslandState> fullRoom,
            IReadOnlyList<IslandState> sheetRoom)
        {
            var nodes = new List<Pt>();
            var labels = new List<Box>();
            long grid = policy.GridNm;
            void Stub(IslandState island, Pt a, (int Dx, int Dy) outward, IEnumerable<int> multiples)
            {
                foreach (int multiple in multiples)
                {
                    for (long step = grid; step <= multiple * grid; step += grid) nodes.Add(a.Step(outward, step));
                    foreach (var kind in Kinds(island.Island))
                        if (screen.Prototypes.TryGetValue(new(kind, island.Island.LabelText, SchematicConnectionGeometry.Spin(outward)), out var prototype))
                            labels.Add(prototype.Offset(a.Step(outward, multiple * grid)));
                }
            }
            IEnumerable<ConnectionPlacedPin> Pins(IslandState island) => island.Island.Members.Where(m => m.RequiresStub).Select(m => m.Pin)
                .Concat(island.Island.JoinCandidates).DistinctBy(p => (p.SymbolId, p.PlacedPinId));
            foreach (var island in shortRoom)
                foreach (var pin in Pins(island))
                {
                    var anchor = AnchorOf(screen.Views[0], pin);
                    Stub(island, Pt.Of(anchor.Position), SchematicConnectionGeometry.Outward(anchor), [SchematicConnectionPolicy.StubMultiples[0]]);
                }
            foreach (var island in fullRoom)
            {
                foreach (var pin in Pins(island))
                {
                    var anchor = AnchorOf(screen.Views[0], pin);
                    var (a, outward) = (Pt.Of(anchor.Position), SchematicConnectionGeometry.Outward(anchor));
                    Stub(island, a, outward, SchematicConnectionPolicy.StubMultiples);
                    if (island.Island.JoinCandidates.Any(c => c.PlacedPinId == pin.PlacedPinId))
                        foreach (var kind in Kinds(island.Island))
                            if (screen.Prototypes.TryGetValue(new(kind, island.Island.LabelText, SchematicConnectionGeometry.Spin(outward)), out var prototype))
                                labels.Add(prototype.Offset(a));
                }
                SheetPinRoom(island);
            }
            foreach (var island in sheetRoom) SheetPinRoom(island);
            return (nodes, labels);

            // Every free slot on the facing edge of each child sheet the island crosses into (§6.5), its stub at every length and
            // each label that stub may carry.
            void SheetPinRoom(IslandState island)
            {
                foreach (var sheetId in island.Island.ChildSheetSymbolIds)
                {
                    var sheet = SheetOf(screen, sheetId);
                    var (side, x) = Side(screen, island, sheet);
                    var outward = side == SheetSide.ShsLeft ? (-1, 0) : (1, 0);
                    var taken = sheet.Pins.Where(p => p.Side == side).Select(p => p.Position.YNm).ToArray();
                    long top = sheet.Position.YNm, bottom = checked(sheet.Position.YNm + sheet.Size.YNm);
                    for (long y = checked(top + policy.SheetPinPitchNm); y <= bottom - policy.SheetPinPitchNm; y = checked(y + grid))
                    {
                        if (taken.Any(t => Math.Abs(t - y) < policy.SheetPinPitchNm)) continue;
                        var a = new Pt(x, y);
                        nodes.Add(a);
                        Stub(island, a, outward, SchematicConnectionPolicy.StubMultiples);
                    }
                }
            }
        }

        // The island's existing connection on this sheet as places a route may attach to (§7): the free ends of its wires, and
        // a one-grid corridor out of each of its connected pins that a join stub could use (§6.4 join stub variant). A wire end
        // with anything foreign at it or within the clearance, or on a foreign segment, is left out, and so is a pin corridor
        // that would run within the clearance of its own symbols' field text (E3 of decision nfa2005d67574bfea, FieldTooNear).
        // Null when none remains.
        private RouteExistingPart? ExistingPart(Screen screen, IslandState island)
        {
            long grid = policy.GridNm;
            var anchor = island.Island.AnchorItemIds.ToHashSet();
            var own = screen.Segments.Where(s => anchor.Contains(s.Owner) && !s.Bus).ToArray();
            List<(int Dx, int Dy)> Exits(Pt at)
            {
                var exits = new List<(int Dx, int Dy)>();
                foreach (var s in own)
                {
                    if (s.A == at && s.B != at) exits.Add((Math.Sign(s.B.X - at.X), Math.Sign(s.B.Y - at.Y)));
                    else if (s.B == at && s.A != at) exits.Add((Math.Sign(s.A.X - at.X), Math.Sign(s.A.Y - at.Y)));
                    else if (OnSegment(at, s.A, s.B) && s.A != at && s.B != at)
                    {
                        exits.Add((Math.Sign(s.A.X - at.X), Math.Sign(s.A.Y - at.Y)));
                        exits.Add((Math.Sign(s.B.X - at.X), Math.Sign(s.B.Y - at.Y)));
                    }
                }
                return exits;
            }
            var points = new Dictionary<Pt, RouteAttachPoint>();
            foreach (var end in own.SelectMany(s => new[] { s.A, s.B }).Distinct().OrderBy(p => p.X).ThenBy(p => p.Y))
            {
                if (end.X % grid != 0 || end.Y % grid != 0 || !screen.RouteRegion.Contains(end)
                    || screen.RouteKeepOuts.Any(k => k.Inflate(policy.ClearanceNm).Contains(end))) continue;
                if (screen.Points.Any(p => !(p.Kind == PointKind.WireEnd && anchor.Contains(p.Owner) && p.Position == end)
                        && Math.Abs(p.Position.X - end.X) <= policy.ClearanceNm && Math.Abs(p.Position.Y - end.Y) <= policy.ClearanceNm)) continue;
                if (screen.Segments.Any(s => !anchor.Contains(s.Owner) && OnSegment(end, s.A, s.B))) continue;
                var exits = Exits(end);
                if (exits.Any(e => Math.Abs(e.Dx) + Math.Abs(e.Dy) != 1)) continue;
                points.TryAdd(end, new(end, null, null, exits, exits.Count));
            }
            foreach (var member in island.Island.Members.Where(m => m.AlreadyConnected && m.Role == ConnectionMemberRole.Signal))
            {
                var pinAnchor = AnchorOf(screen.Views[0], member.Pin);
                var a = Pt.Of(pinAnchor.Position);
                var outward = SchematicConnectionGeometry.Outward(pinAnchor);
                var node = a.Step(outward, grid);
                if (points.ContainsKey(node) || !screen.RouteRegion.Contains(node)) continue;
                if (Refusal(policy, screen, island, a, node, outward, null, member.Pin.PlacedPinId, member.Pin.SymbolId, null, null, Variant.JoinStub,
                        routed: true) is not null
                    || FieldTooNear(screen, OwnersAt(screen, island, a), a, node) is not null)
                    continue;
                var exits = Exits(a);
                int symbols = screen.Points.Where(p => p.Kind == PointKind.Pin && p.Position == a).Select(p => p.OwnerSymbol).Distinct().Count();
                points.Add(node, new(node, a, outward, exits, exits.Count + Math.Max(symbols, 1)));
            }
            if (points.Count == 0) return null;
            string tie = island.Island.Members.Where(m => m.AlreadyConnected).Select(m => m.Pin.PlacedPinId.ToString("D")).Min(StringComparer.Ordinal)!;
            return new(tie, [.. points.Values]);
        }

        // Record a routed island: its wires in identity order, its junctions, and its one label at the end of the stub from the
        // first pin (§6.6, §6.7 route-wire, junction and stub-label identities).
        private void EmitRoute(Screen screen, IslandState island, IReadOnlyList<RouteTerminal> terminals, ConnectionPlacedPin rootPin, Stub stub,
            OrthogonalRoute route)
        {
            var screenId = screen.Record.ScreenId;
            string netKey = SchematicConnectionIdentity.NetKey(terminals.Select(t => t.PlacedPinId));
            var ends = new HashSet<Pt>();
            for (int ordinal = 0; ordinal < route.Segments.Count; ordinal++)
            {
                var (start, end) = route.Segments[ordinal];
                var id = SchematicConnectionIdentity.Generated(intent.OriginId, intent.NativeRevision, intent.DesiredSha256, screenId,
                    GeneratedConnectionRole.RouteWire, SchematicConnectionIdentity.RouteAnchorKey(netKey, ordinal), ordinal);
                SchematicConnectionIdentity.Claim(used, id);
                var wire = new SchematicLine { Id = new() { Value = id.ToString("D") }, Start = start.Vector(), End = end.Vector(),
                    Type = SchematicLineType.SltWire, Locked = LockedState.LsUnlocked };
                screen.Items.Add(wire);
                screen.Segments.Add(new(start, end, id));
                island.Same.Add(id);
                island.Generated.Add(id);
                generated.Add(new(id, GeneratedConnectionRole.RouteWire, screenId, [island.Island.NetId], null, null, Any.Pack(wire).TypeUrl));
                ends.Add(start);
                ends.Add(end);
            }
            foreach (var at in route.Junctions)
            {
                var id = SchematicConnectionIdentity.Generated(intent.OriginId, intent.NativeRevision, intent.DesiredSha256, screenId,
                    GeneratedConnectionRole.Junction, SchematicConnectionIdentity.JunctionAnchorKey(netKey, at.X, at.Y));
                SchematicConnectionIdentity.Claim(used, id);
                var junction = new Junction { Id = new() { Value = id.ToString("D") }, Position = at.Vector(), Locked = LockedState.LsUnlocked };
                screen.Items.Add(junction);
                screen.Points.Add(new(at, PointKind.Generated, id, null));
                island.Same.Add(id);
                island.Generated.Add(id);
                generated.Add(new(id, GeneratedConnectionRole.Junction, screenId, [island.Island.NetId], null, null, Any.Pack(junction).TypeUrl));
            }
            // §6.4 lists every accepted generated point as foreign; the wire ends are recorded after the junctions so that a
            // junction is described as the junction it is.
            foreach (var end in ends.OrderBy(p => p.X).ThenBy(p => p.Y)) screen.Points.Add(new(end, PointKind.Generated, Guid.Empty, null));
            var combo = stub.Label!;
            var labelId = SchematicConnectionIdentity.Generated(intent.OriginId, intent.NativeRevision, intent.DesiredSha256, screenId,
                GeneratedConnectionRole.StubLabel, SchematicConnectionIdentity.PinAnchorKey(rootPin.PlacedPinId));
            SchematicConnectionIdentity.Claim(used, labelId);
            var label = LabelPayload(combo.Kind, labelId, stub.E.Vector(), combo.Text, combo.Spin, policy);
            if (label is LocalLabel local) local.FieldsAutoplaced = true;
            else if (label is HierarchicalLabel hierarchical) hierarchical.FieldsAutoplaced = true;
            screen.Items.Add(label);
            screen.Envelopes.Add(stub.Envelope!.Value);
            island.Same.Add(labelId);
            island.Generated.Add(labelId);
            if (combo.Kind == ConnectionLabelKind.Hierarchical) island.UplinkLabelled = true;
            generated.Add(new(labelId, GeneratedConnectionRole.StubLabel, screenId, [island.Island.NetId], rootPin.PlacedPinId, null, Any.Pack(label).TypeUrl));
            foreach (var terminal in terminals) island.Anchored.Add(terminal.Anchor);
            if (island.Island.JoinRequired && route.AttachedExisting) island.Joined = true;
            island.Strategy = ConnectionRealizationStrategy.OrthogonalWire;
        }

        private sealed record Stub(Pt A, Pt E, Combo? Label, Box? Envelope, Guid? Carrier);

        // §6.3 (e) and (f): the first stub length, in that order and of at least `minimumLength`, that §6.4 admits and `reject`
        // (when given) does not refuse; a stub ending on a same-net power symbol pin attaches to it. When none is taken,
        // `lastRefusal` says why the last length tried was not.
        private bool TryStub(Screen screen, IslandState island, Pt a, (int Dx, int Dy) outward, Guid? ownPin, Guid owner,
            Variant variant, ConnectionLabelKind kind, out Stub stub, bool allowAttach = true, long minimumLength = 0, bool routed = false,
            Func<Stub, string?>? reject = null)
        {
            var combo = new Combo(kind, island.Island.LabelText, SchematicConnectionGeometry.Spin(outward));
            foreach (int multiple in SchematicConnectionPolicy.StubMultiples)
            {
                if (multiple * policy.GridNm < minimumLength) continue;
                var e = a.Step(outward, checked(multiple * policy.GridNm));
                var carrier = allowAttach ? island.Island.Members.Where(m => m.Role == ConnectionMemberRole.PowerCarrier)
                    .FirstOrDefault(m => At(screen, m.Pin) == e) : null;
                if (carrier is not null)
                {
                    if (Admit(screen, island, a, e, outward, null, ownPin, owner, carrier.Pin.PlacedPinId, carrier.Pin.SymbolId, variant))
                    { stub = new(a, e, null, null, carrier.Pin.PlacedPinId); return true; }
                    continue;
                }
                var envelope = screen.Prototypes[combo].Offset(e);
                if (Admit(screen, island, a, e, outward, envelope, ownPin, owner, null, null, variant, routed))
                {
                    var admitted = new Stub(a, e, combo, envelope, null);
                    if (reject?.Invoke(admitted) is { } why) { lastRefusal = why; continue; }
                    stub = admitted;
                    return true;
                }
            }
            stub = null!;
            return false;
        }

        // §6.4 admission of a stub from a to e, pointing outward, with label envelope r (null when attaching to a carrier).
        private bool Admit(Screen screen, IslandState island, Pt a, Pt e, (int Dx, int Dy) outward, Box? r, Guid? ownPin, Guid owner,
            Guid? carrierPin, Guid? carrierSymbol, Variant variant, bool routed = false) =>
            (lastRefusal = Refusal(policy, screen, island, a, e, outward, r, ownPin, owner, carrierPin, carrierSymbol, variant, routed)) is null;

        // §6.5: which side of sheet symbol K a new pin goes on, and that side's x.
        private (SheetSide Side, long X) Side(Screen screen, IslandState island, SheetSymbol sheet)
        {
            var anchors = island.Island.Members.Select(m => At(screen, m.Pin).X).ToArray();
            decimal centre = sheet.Position.XNm + sheet.Size.XNm / 2m;
            bool left = anchors.Length != 0 && anchors.Average(x => (decimal)x) < centre;
            return left ? (SheetSide.ShsLeft, sheet.Position.XNm) : (SheetSide.ShsRight, checked(sheet.Position.XNm + sheet.Size.XNm));
        }

        private (SheetSide Side, long X) Side(Screen screen, ConnectionIsland island, SheetSymbol sheet) =>
            Side(screen, new IslandState(island), sheet);

        private SheetSymbol SheetOf(Screen screen, Guid id)
        {
            foreach (var item in screen.Views[0].Native.Items)
            {
                if (!item.Is(SheetSymbol.Descriptor)) continue;
                var sheet = item.Unpack<SheetSymbol>();
                if (TryId(sheet.Id, out var sheetId) && sheetId == id)
                    return sheet.Position is null || sheet.Size is null
                        ? throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "Sheet symbol " + id.ToString("D") + " has no position or size.")
                        : sheet;
            }
            throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Sheet symbol " + id.ToString("D")
                + " is not on the sheet whose connection crosses into it.");
        }

        private IEnumerable<(IslandState Island, SheetSymbol Sheet, ConnectionPort Port)> SheetPinOrder(Screen screen, List<IslandState> islands)
        {
            var order = new List<(IslandState Island, SheetSymbol Sheet, ConnectionPort Port)>();
            foreach (var island in islands)
                foreach (var sheetId in island.Island.ChildSheetSymbolIds)
                {
                    var port = intent.Ports.FirstOrDefault(p => p.NetId == island.Island.NetId && p.SheetSymbolId == sheetId
                        && p.ParentPathKey == island.Island.SheetPathKey)
                        ?? throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "A sheet crossing of net '" + NetName(island) + "' has no port.");
                    order.Add((island, SheetOf(screen, sheetId), port));
                }
            return order.OrderBy(o => o.Port.PortText, StringComparer.Ordinal).ThenBy(o => o.Port.SheetSymbolId);
        }

        private void SheetPinStub(Screen screen, IslandState island, SheetSymbol sheet, ConnectionPort port)
        {
            Guid sheetId = port.SheetSymbolId;
            var (side, x) = Side(screen, island, sheet);
            var outward = side == SheetSide.ShsLeft ? (-1, 0) : (1, 0);
            var taken = sheet.Pins.Where(p => p.Side == side).Select(p => p.Position.YNm)
                .Concat(sheetPins.GetValueOrDefault(sheetId)?.Where(p => p.Side == side).Select(p => p.Position.YNm) ?? []).ToArray();
            // The hierarchical label goes on the island's first labelled stub with room for it; when no pin stub had room, that is
            // its first crossing (in sheet-pin order). A crossing whose stub has room for it at no slot carries a local label,
            // and the island's next crossing carries the hierarchical label.
            ConnectionLabelKind[] kinds = UplinkPending(island) ? [ConnectionLabelKind.Hierarchical, ConnectionLabelKind.Local] : [ConnectionLabelKind.Local];
            long top = sheet.Position.YNm, bottom = checked(sheet.Position.YNm + sheet.Size.YNm);
            foreach (var kind in kinds)
            for (long y = checked(top + policy.SheetPinPitchNm); y <= bottom - policy.SheetPinPitchNm; y = checked(y + policy.GridNm))
            {
                if (taken.Any(t => Math.Abs(t - y) < policy.SheetPinPitchNm)) continue;
                var a = new Pt(x, y);
                // The new sheet pin itself is not yet a point; its own sheet symbol is the stub's owner.
                if (!TryStub(screen, island, a, outward, null, sheetId, Variant.Stub, kind, out var stub, allowAttach: false)) continue;
                if (kind != kinds[0]) island.HierarchicalRefused.Add("sheet pin '" + port.PortText + "' on sheet symbol " + sheetId.ToString("D"));
                string key = SchematicConnectionIdentity.SheetPinAnchorKey(sheetId, port.PortText);
                var pinId = SchematicConnectionIdentity.Generated(intent.OriginId, intent.NativeRevision, intent.DesiredSha256, screen.Record.ScreenId,
                    GeneratedConnectionRole.SheetPin, key);
                SchematicConnectionIdentity.Claim(used, pinId);
                var pin = new SheetPin
                {
                    Id = new() { Value = pinId.ToString("D") }, Position = a.Vector(),
                    Text = new() { Text_ = port.PortText, Attributes = new() { Size = new() { XNm = policy.TextSizeNm, YNm = policy.TextSizeNm }, Multiline = false } },
                    SpinStyle = side == SheetSide.ShsLeft ? SchematicLabelSpinStyle.SlssRight : SchematicLabelSpinStyle.SlssLeft,
                    Shape = SchematicLabelShape.SlshPassive, Side = side, Locked = LockedState.LsUnlocked
                };
                if (!sheetPins.TryGetValue(sheetId, out var list)) sheetPins.Add(sheetId, list = []);
                list.Add(pin);
                screen.Points.Add(new(a, PointKind.Generated, pinId, sheetId));
                island.Generated.Add(pinId);
                generated.Add(new(pinId, GeneratedConnectionRole.SheetPin, screen.Record.ScreenId, [island.Island.NetId], null, sheetId,
                    Any.Pack(pin).TypeUrl));
                Accept(screen, island, stub, GeneratedConnectionRole.SheetPinWire, GeneratedConnectionRole.SheetPinLabel, key, null, sheetId);
                return;
            }
            throw Error(SchematicConnectionErrors.RealizationNoFreeSheetPinSlot, "Net '" + NetName(island) + "' needs a new sheet pin '" + port.PortText
                + "' on sheet symbol " + sheetId.ToString("D") + ", but its " + (side == SheetSide.ShsLeft ? "left" : "right")
                + " edge has no free slot with room for a stub and label. Enlarge the sheet symbol or clear space beside it.");
        }

        // Record an admitted stub: its wire, its label (or the carrier it attaches to), and their geometry.
        private void Accept(Screen screen, IslandState island, Stub stub, GeneratedConnectionRole? wireRole, GeneratedConnectionRole labelRole,
            string key, Guid? placedPin, Guid? sheetSymbol)
        {
            if (wireRole is { } role && stub.A != stub.E)
            {
                var wireId = SchematicConnectionIdentity.Generated(intent.OriginId, intent.NativeRevision, intent.DesiredSha256, screen.Record.ScreenId, role, key);
                SchematicConnectionIdentity.Claim(used, wireId);
                var wire = new SchematicLine { Id = new() { Value = wireId.ToString("D") }, Start = stub.A.Vector(), End = stub.E.Vector(),
                    Type = SchematicLineType.SltWire, Locked = LockedState.LsUnlocked };
                screen.Items.Add(wire);
                screen.Segments.Add(new(stub.A, stub.E, wireId));
                island.Same.Add(wireId);
                island.Generated.Add(wireId);
                generated.Add(new(wireId, role, screen.Record.ScreenId, [island.Island.NetId], placedPin, sheetSymbol, Any.Pack(wire).TypeUrl));
            }
            // §6.4 lists every accepted generated point as foreign. This end always lies on the wire recorded above, or
            // is the pin's own measured anchor for an anchor label, so the segment and pin records already refuse
            // whatever this point would; it is recorded to follow the contract's list, not as the only guard.
            screen.Points.Add(new(stub.E, PointKind.Generated, Guid.Empty, null));
            island.Anchored.Add(stub.A);
            if (stub.Carrier is not null) { island.Attached = true; return; }
            var combo = stub.Label!;
            var labelId = SchematicConnectionIdentity.Generated(intent.OriginId, intent.NativeRevision, intent.DesiredSha256, screen.Record.ScreenId, labelRole, key);
            SchematicConnectionIdentity.Claim(used, labelId);
            var label = LabelPayload(combo.Kind, labelId, stub.E.Vector(), combo.Text, combo.Spin, policy);
            // A local or hierarchical label has no fields, and KiCad marks every such label it loads from a file as having
            // auto-placed fields; created without that mark, the label would change when its sheet is saved and reloaded. The
            // mark places no field, so the prototype measured without it has the same bounds (a CN-1 §6.6 clarification
            // requested from the integration owner). A global label keeps its intersheet-reference field as KiCad creates it.
            if (label is LocalLabel local) local.FieldsAutoplaced = true;
            else if (label is HierarchicalLabel hierarchical) hierarchical.FieldsAutoplaced = true;
            screen.Items.Add(label);
            screen.Envelopes.Add(stub.Envelope!.Value);
            island.Same.Add(labelId);
            island.Generated.Add(labelId);
            if (combo.Kind == ConnectionLabelKind.Hierarchical) island.UplinkLabelled = true;
            generated.Add(new(labelId, labelRole, screen.Record.ScreenId, [island.Island.NetId], placedPin, sheetSymbol, Any.Pack(label).TypeUrl));
        }

        // ---- §6.8 emission ----

        private SchematicConnectionRealization Assemble(SchematicHierarchyData current)
        {
            var schematic = candidate.Schematic.Clone();
            foreach (var screen in schematic.Instances)
            {
                var id = Id(screen.Metadata.ScreenId);
                if (screens.TryGetValue(id, out var realized))
                    screen.Items.Add(realized.Items.Select(Any.Pack));
                for (int i = 0; i < screen.Items.Count; i++)
                {
                    if (!screen.Items[i].Is(SheetSymbol.Descriptor)) continue;
                    var sheet = screen.Items[i].Unpack<SheetSymbol>();
                    if (!sheetPins.TryGetValue(Id(sheet.Id), out var pins)) continue;
                    sheet.Pins.Add(pins.Select(p => p.Clone()));
                    screen.Items[i] = Any.Pack(sheet);
                }
            }
            var design = candidate with { Schematic = schematic };
            List<SchematicItemOperation> operations;
            try { operations = [.. SchematicHierarchyDelta.Plan(current, schematic, token)]; }
            catch (AutomationException error)
            { throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "The generated connections cannot be expressed as native edits: " + error.Message); }
            RequireOnlyPlannedEdits(operations);
            var assertion = new SchematicConnectivityAssertion { Version = 1 };
            foreach (var group in intent.ExpectedGroups)
            {
                var pins = new SchematicPinGroup();
                foreach (var key in group)
                {
                    if (!native.TryGetValue(key.SheetPathKey, out var screen))
                        throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "An expected pin group names sheet " + key.SheetPathKey
                            + ", which the checkpoint does not contain.");
                    pins.Pins.Add(new SchematicNetChainPinAnchor { Path = screen.Metadata.Document.SheetPath.Clone(),
                        Pin = new() { Value = key.PlacedPinId.ToString("D") } });
                }
                assertion.ExpectedGroups.Add(pins);
            }
            operations.Add(new SchematicItemOperation { AssertConnectivity = assertion });
            // §6.8 step 3 for the schematic section; the executor round-trips the whole design with its libraries.
            string xml = SchematicDataXml.Write(schematic);
            if (SchematicDataXml.Write(SchematicDataXml.Read(xml)) != xml)
                throw Error(SchematicConnectionErrors.InconsistentDesignSerialization, "The generated connections do not round-trip through the design file.");
            var batch = new ApplySchematicItemBatch { Document = checkpoint.State.Document.Clone(), DocumentEpoch = revision.Epoch,
                ExpectedRevision = revision.Clone(), OperationId = Guid.Empty.ToString("D"), OriginId = intent.OriginId.ToString("D"),
                Description = BatchDescription };
            batch.Operations.Add(operations);
            if (new CheckedSchematicBatch { Batch = batch, ExpectedState = checkpoint.State.Clone() }.CalculateSize() > MaxBatchBytes)
                throw Error(SchematicConnectionErrors.RealizationBatchTooLarge, "The connections would need a larger native request than KiCad accepts. Save them in smaller XML revisions.");
            return new(design, operations, generated, outcomes, diagnostics, [.. limitations]);
        }

        // I6 before the assertion is added: the batch may create only the planned symbols and the generated items, each
        // once, give existing sheet symbols exactly their generated sheet pins, and extend a sheet's library cache only by
        // the definitions of symbols created on that sheet. Anything else would change an existing item, which native
        // connectivity cannot reveal and the resolution would find only after KiCad had committed it.
        private void RequireOnlyPlannedEdits(IReadOnlyList<SchematicItemOperation> operations)
        {
            var expected = generated.Where(g => g.Role != GeneratedConnectionRole.SheetPin).Select(g => g.Id).Concat(created).ToHashSet();
            var createdOnce = new HashSet<Guid>();
            var updated = new HashSet<Guid>();
            var replacedCaches = new HashSet<Guid>();
            foreach (var operation in operations)
            {
                switch (operation.OperationCase)
                {
                    case SchematicItemOperation.OperationOneofCase.Create:
                    {
                        var (id, item) = SchematicItemDelta.Index([operation.Create]).Single();
                        bool planned = item is SchematicSymbolInstance ? created.Contains(id) : expected.Contains(id) && !created.Contains(id);
                        if (!planned || !createdOnce.Add(id))
                            throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Realizing connections would create item "
                                + id.ToString("D") + ", which is neither a new symbol nor a generated connection item, or would create it twice.");
                        break;
                    }
                    case SchematicItemOperation.OperationOneofCase.Update:
                    {
                        var (id, item) = SchematicItemDelta.Index([operation.Update]).Single();
                        var path = operation.TargetDocument?.SheetPath is { } target ? PathOf(target) : "";
                        var before = native.TryGetValue(path, out var screen)
                            ? SchematicItemDelta.Index(screen.Items).GetValueOrDefault(id) as SheetSymbol : null;
                        if (item is not SheetSymbol sheet || before is null || !sheetPins.TryGetValue(id, out var pins))
                            throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Realizing connections would change existing item "
                                + id.ToString("D") + " on sheet " + path + ", which it must never touch.");
                        var gained = before.Clone();
                        gained.Pins.Add(pins.Select(p => p.Clone()));
                        // The delta carries sheet symbols without their projected variant descriptions.
                        if (!SchematicVariantProjection.Equivalent(gained, sheet))
                            throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Realizing connections would change sheet symbol "
                                + id.ToString("D") + " on sheet " + path + " beyond adding its generated sheet pins.");
                        updated.Add(id);
                        break;
                    }
                    case SchematicItemOperation.OperationOneofCase.ReplaceLibraryCache:
                        RequireCacheExtension(operation, replacedCaches);
                        break;
                    default:
                        throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Realizing connections may only create items, add sheet pins "
                            + "and extend library caches for new symbols, not " + operation.OperationCase + ".");
                }
            }
            if (!createdOnce.SetEquals(expected) || !updated.SetEquals(sheetPins.Keys))
                throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "The native edits do not carry every new symbol and generated connection item.");
        }

        // A library cache may be replaced only on a sheet that receives a new symbol, once, and only by adding the
        // definitions that new symbols on that sheet use: every added key must be the library cache key of a symbol
        // created on that sheet (CacheKeyOf), and every definition KiCad already holds there stays exactly as it is
        // (compared as KiCad keeps it, ignoring only the order of its drawn children).
        private void RequireCacheExtension(SchematicItemOperation operation, HashSet<Guid> replaced)
        {
            var state = operation.ReplaceLibraryCache;
            var path = operation.TargetDocument?.SheetPath is { } target ? PathOf(target) : "";
            if (!native.TryGetValue(path, out var screen) || !planned.TryGetValue(path, out var desired)
                || !TryId(state.ScreenId, out var screenId) || screenId != Id(screen.Metadata.ScreenId) || !replaced.Add(screenId))
                throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Realizing connections would replace a library cache that is not "
                    + "exactly one checkpoint sheet's (sheet " + path + ").");
            var newSymbolKeys = desired.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .Where(s => TryId(s.Id, out var id) && created.Contains(id)).Select(CacheKeyOf).ToHashSet(StringComparer.Ordinal);
            if (newSymbolKeys.Count == 0)
                throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Realizing connections would replace the library cache of sheet "
                    + path + ", which receives no new symbol.");
            var replacement = new Dictionary<string, SchematicCachedSymbol>(StringComparer.Ordinal);
            foreach (var definition in state.Definitions)
                if (!replacement.TryAdd(definition.CacheKey, definition))
                    throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Realizing connections would give sheet " + path
                        + " two library definitions named '" + definition.CacheKey + "'.");
            foreach (var kept in screen.CachedSymbols)
                if (!replacement.TryGetValue(kept.CacheKey, out var after) || !SchematicLibraryCacheEquivalence.Equal(kept, after))
                    throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Realizing connections would "
                        + (after is null ? "drop" : "change") + " the library definition '" + kept.CacheKey + "' KiCad already holds on sheet " + path
                        + "; only the definitions of new symbols on that sheet may be added.");
            var held = screen.CachedSymbols.Select(c => c.CacheKey).ToHashSet(StringComparer.Ordinal);
            foreach (var key in replacement.Keys.Where(k => !held.Contains(k)).Order(StringComparer.Ordinal))
                if (!newSymbolKeys.Contains(key))
                    throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Realizing connections would add the library definition '"
                        + key + "' to sheet " + path + ", which no new symbol on that sheet uses; only the definitions of new symbols on that sheet may be added.");
        }

        // The key a placed symbol's definition has in its sheet's library cache, as KiCad names it
        // (SCH_SYMBOL::GetSchSymbolLibraryName): its cache alias when it has one, otherwise its library identifier.
        private static string CacheKeyOf(SchematicSymbolInstance symbol)
        {
            if (symbol.LibName.Length != 0) return symbol.LibName;
            var library = symbol.LibraryId ?? symbol.Definition?.Id;
            return library is null ? "" : (library.LibraryNickname.Length == 0 ? "" : library.LibraryNickname + ":") + library.EntryName;
        }

        // ---- helpers ----

        private string NetName(IslandState island) => intent.Nets.FirstOrDefault(n => n.NetId == island.Island.NetId)?.Name ?? island.Island.NetId.ToString("D");

        // How a reason or refusal names a pin and a symbol for a person: by the reference the planned design gives its component
        // (U4.4, U4), or by identity when the design names none.
        private string Describe(ConnectionPlacedPin pin) =>
            (references.TryGetValue(pin.Endpoint.ComponentId, out var reference) ? reference : pin.Endpoint.ComponentId.ToString("D")) + "." + pin.Endpoint.Pin;

        private string DescribeSymbol(Guid nativeSymbol) => symbolReferences.TryGetValue(nativeSymbol, out var references)
            ? string.Join("/", references) : nativeSymbol.ToString("D");

        // The references of the components each native symbol draws, in ordinal order: one for a symbol on a sheet used once,
        // one per instance for a symbol on a repeated sheet (TP802/TP803 name the same drawn symbol).
        private static Dictionary<Guid, string[]> SymbolReferences(SchematicDesign design)
        {
            var components = design.Engineering.Circuit.Components.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Reference);
            var occurrences = design.Engineering.Circuit.Symbols.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().ComponentId);
            var result = new Dictionary<Guid, SortedSet<string>>();
            foreach (var binding in design.SymbolBindings)
                if (occurrences.TryGetValue(binding.SymbolOccurrenceId, out var component) && components.TryGetValue(component, out var reference))
                {
                    if (!result.TryGetValue(binding.NativeObjectId, out var references))
                        result.Add(binding.NativeObjectId, references = new(StringComparer.Ordinal));
                    references.Add(reference);
                }
            return result.ToDictionary(r => r.Key, r => r.Value.ToArray());
        }

        private static IMessage UnpackItem(Any any)
        {
            var descriptor = Registry.Find(Any.GetTypeName(any.TypeUrl))
                ?? throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Unknown measurement prototype type " + any.TypeUrl + ".");
            return descriptor.Parser.ParseFrom(any.Value);
        }

        private static KIID? IdOf(IMessage message) => message.Descriptor.FindFieldByName("id")?.Accessor.GetValue(message) as KIID;

        private static Vector2? PositionOf(IMessage message) => message.Descriptor.FindFieldByName("position")?.Accessor.GetValue(message) as Vector2;

        private static bool ValidBox(Box2? box) => box?.Position is not null && box.Size is not null && box.Size.XNm >= 0 && box.Size.YNm >= 0;

        // Every identity already present anywhere in a schematic: items, placed and library pins, sheet pins and fields.
        private static void Collect(IMessage message, ISet<Guid> ids)
        {
            switch (message)
            {
                case KIID kiid:
                    if (Guid.TryParseExact(kiid.Value, "D", out var id) && id != Guid.Empty) ids.Add(id);
                    return;
                case Any any:
                    if (Registry.Find(Any.GetTypeName(any.TypeUrl)) is { } descriptor) Collect(descriptor.Parser.ParseFrom(any.Value), ids);
                    return;
            }
            foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
            {
                if (field.FieldType != FieldType.Message) continue;
                var value = field.Accessor.GetValue(message);
                if (field.IsMap)
                {
                    foreach (DictionaryEntry entry in (IDictionary)value)
                        if (entry.Value is IMessage nested) Collect(nested, ids);
                }
                else if (field.IsRepeated)
                {
                    foreach (var nested in (IList)value) Collect((IMessage)nested, ids);
                }
                else if (value is IMessage nested) Collect(nested, ids);
            }
        }
    }
}
