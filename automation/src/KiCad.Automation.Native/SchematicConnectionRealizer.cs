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

// CN-1 milestone 1: label-stub realization (automation/design/contracts/cn1-wiring-intent.md §6). Lane 2A created
// this file under the CN-1 integration grant (decision n2c2ef8777f8ace77). It measures the checkpoint natively and
// turns a planned connection intent into exact native items: a short wire from each pin that needs one, ending in a
// label that carries the net's name, sheet pins where a net crosses into a child sheet, and one final connectivity
// assertion that makes the editor prove the resulting pin partition before it commits anything.

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

/// <summary>Milestone 1 of CN-1: every connection a planned intent needs is drawn as a short wire stub from the pin,
/// ending in a label, plus sheet pins for hierarchy crossings (cn1-wiring-intent.md §6). Everything is measured by
/// the editor at the checkpoint revision; nothing is guessed. Every refusal is an <see cref="AutomationException"/>
/// with a §13 realization code, raised before anything reaches the editor's document.</summary>
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
        SchematicConnectionPolicy policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(policy);
        token.ThrowIfCancellationRequested();
        return new Run(intent, candidate, checkpoint, measure, policy, token).ExecuteAsync();
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

    // Union of every instance path: obstacles, connection points and connection segments of the one physical screen.
    private static void BuildGeometry(Screen screen, SchematicConnectionPolicy policy)
    {
        screen.Page = screen.Views[0].Page!.Value;
        if (screen.Views.Any(v => v.Page != screen.Page))
            throw Error(SchematicConnectionErrors.RealizationMeasurementIncomplete, "Instances of one sheet reported different pages.");
        long inset = policy.PageInsetNm;
        screen.Usable = new(screen.Page.L + inset, screen.Page.T + inset, screen.Page.R - inset, screen.Page.B - inset);
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
                    points.Add(new(Pt.Of(pin.Position), PointKind.Pin, Id(pin.Id), symbol));
            foreach (var (id, item) in SchematicItemDelta.Index(view.Native.Items))
            {
                switch (item)
                {
                    case SchematicLine line when line.Type is SchematicLineType.SltWire or SchematicLineType.SltBus:
                        points.Add(new(Pt.Of(line.Start), PointKind.WireEnd, id, null));
                        points.Add(new(Pt.Of(line.End), PointKind.WireEnd, id, null));
                        segments.Add(new(Pt.Of(line.Start), Pt.Of(line.End), id));
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
    // null when admitted, otherwise the rule that refuses it.
    private static string? Refusal(SchematicConnectionPolicy policy, Screen screen, IslandState island, Pt a, Pt e, (int Dx, int Dy) outward,
        Box? r, Guid? ownPin, Guid owner, Guid? carrierPin, Guid? carrierSymbol, Variant variant)
    {
        // 1. Inside the page inset.
        if (!screen.Usable.Contains(e) || (r is { } inside && !inside.Within(screen.Usable))) return "outside the page inset";
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
                return (a == e ? "the pin lies inside the bounds of item " : "the stub crosses item ") + id.ToString("D");
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
                return ownPinSymbol ? "the label overlaps symbol " + id.ToString("D") + " more than the pin target in front of the pin"
                    : id == owner ? "the label overlaps the bounds KiCad measures for its own symbol " + id.ToString("D") + ", which take in all of that symbol's visible fields"
                    : "the label overlaps item " + id.ToString("D");
        }
        foreach (var envelope in screen.Envelopes)
        {
            if (SegmentMeets(a, e, envelope, open: false)) return "the stub crosses an earlier generated label";
            if (r is { } label && label.InteriorsOverlap(envelope)) return "the label overlaps an earlier generated label";
        }
        return null;

        static string Describe(ForeignPoint point) => point.Kind switch
        {
            PointKind.Pin => "pin " + point.Owner.ToString("D") + " of symbol " + point.OwnerSymbol?.ToString("D"),
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

    private sealed record ForeignPoint(Pt Position, PointKind Kind, Guid Owner, Guid? OwnerSymbol);

    private sealed record ForeignSegment(Pt A, Pt B, Guid Owner);

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
    }

    private sealed class Screen
    {
        public required ConnectionScreen Record { get; init; }
        public required List<PathView> Views { get; init; }
        public Box Page { get; set; }
        public Box Usable { get; set; }
        public Dictionary<Guid, Box> Obstacles { get; } = [];
        public List<ForeignPoint> Points { get; } = [];
        public List<ForeignSegment> Segments { get; } = [];
        public List<Box> Envelopes { get; } = [];
        public Dictionary<Combo, Box> Prototypes { get; } = [];
        public List<IMessage> Items { get; } = [];
    }

    private sealed class Run(SchematicConnectionIntent intent, SchematicDesign candidate, CheckedSchematicState checkpoint,
        Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> measure,
        SchematicConnectionPolicy policy, CancellationToken token)
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
            var screen = new Screen { Record = record, Views = views };
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
            var islands = record.Islands.OrderBy(i => i.NetId).ThenBy(i => i.SheetPathKey, StringComparer.Ordinal)
                .Select(i => new IslandState(i)).ToList();
            foreach (var island in islands)
                if (island.Island.SheetPathKey != views[0].Path || island.Island.ScreenId != record.ScreenId)
                    throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "A connection island is not on its screen's representative path.");
            RequirePins(screen, islands);
            ConfirmPower(screen, islands);
            PreexistingContacts(screen, islands);
            await Prototypes(screen, islands);
            // §6.3 (a) and (b): joins and pin stubs, island by island.
            foreach (var island in islands)
            {
                token.ThrowIfCancellationRequested();
                if (island.Island.JoinRequired) Join(screen, island);
                foreach (var member in island.Island.Members.Where(m => m.RequiresStub)) MemberStub(screen, island, member);
            }
            // §6.3 (c) and §6.5: sheet pins, allocated in port-text order.
            foreach (var (island, sheet, port) in SheetPinOrder(screen, islands)) SheetPinStub(screen, island, sheet, port);
            foreach (var island in islands)
            {
                // §6.3 (d) with (e): the island's first stub carries its hierarchical label unless that stub attaches
                // to a same-net power symbol, which leaves no room for a label (the power pin sits on the stub's line,
                // so every longer stub runs through it), or is the optional stub of a new pin stacked on an existing
                // connection that has no room; then the next labelled stub carries it. When no stub can carry it, the
                // refusal names why (a CN-1 clarification requested from the integration owner).
                if (UplinkPending(island))
                    throw island.UplinkRefused.Count != 0
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
                outcomes.Add(new(island.Island.NetId, record.ScreenId, views[0].Path, ConnectionRealizationStrategy.LabelStub,
                    island.Generated.ToArray(), island.Attached, null));
                if (island.Joined)
                    diagnostics.Add(new(SchematicConnectionErrors.ExistingNetNamedByRealization, "info", island.Island.NetId, null,
                        "An existing unlabelled connection of net '" + NetName(island) + "' is named by a generated label on sheet "
                        + island.Island.SheetPathKey + "."));
            }
            if (islands.Count != 0)
                diagnostics.Add(new(SchematicConnectionErrors.RealizationPageReservationsUnspecified, "info", null, null,
                    "The drawing-sheet title block on screen " + record.ScreenId.ToString("D")
                    + " is not a schematic item; only the page inset keeps generated items away from it."));
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
                && measured.Obstacles.Concat(measured.Candidates).Concat(measured.ItemCandidates).All(b => b.Anchor is not null && ValidBox(b.Bounds));
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
                        + " has no pin geometry for new symbol " + symbol.ToString("D") + ".");
                if (!geometry.Complete)
                    throw Error(geometry.IncompleteReason == SchematicPinGeometryIncompleteReason.SpgirVariantPinMappingUnresolved
                            ? SchematicConnectionErrors.RealizationVariantPinIdentityUnresolved : SchematicConnectionErrors.RealizationPinGeometryIncomplete,
                        "KiCad cannot report exact pin positions for new symbol " + symbol.ToString("D") + " on sheet " + view.Path
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
                                + symbol.ToString("D") + " on sheet " + view.Path + " lands on an existing connection point it must not join. Move the new symbol in the XML.");
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
            foreach (var pin in island.Island.JoinCandidates)
            {
                var anchor = AnchorOf(screen.Views[0], pin);
                var a = Pt.Of(anchor.Position);
                var outward = SchematicConnectionGeometry.Outward(anchor);
                var owner = pin.SymbolId;
                var kind = KindFor(island);
                if (TryStub(screen, island, a, outward, pin.PlacedPinId, owner, Variant.JoinStub, kind, out var stub))
                {
                    Accept(screen, island, stub, GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel,
                        SchematicConnectionIdentity.PinAnchorKey(pin.PlacedPinId), pin.PlacedPinId, null);
                    island.Joined = true;
                    return;
                }
                var combo = new Combo(kind, island.Island.LabelText, SchematicConnectionGeometry.Spin(outward));
                var envelope = screen.Prototypes[combo].Offset(a);
                if (Admit(screen, island, a, a, outward, envelope, pin.PlacedPinId, owner, null, null, Variant.AnchorLabel))
                {
                    Accept(screen, island, new(a, a, combo, envelope, null), null, GeneratedConnectionRole.AnchorLabel,
                        SchematicConnectionIdentity.PinAnchorKey(pin.PlacedPinId), pin.PlacedPinId, null);
                    island.Joined = true;
                    return;
                }
                refused.Add("pin " + Describe(pin) + ": " + lastRefusal);
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
            if (!TryStub(screen, island, a, outward, member.Pin.PlacedPinId, member.Pin.SymbolId, Variant.Stub,
                    KindFor(island), out var stub))
                throw Error(SchematicConnectionErrors.RealizationNoFreeStub, "Pin " + Describe(member.Pin) + " of net '" + NetName(island)
                    + "' has no free room for a connection stub and label on sheet " + island.Island.SheetPathKey
                    + " (at the longest stub length tried, " + lastRefusal + "). Move the symbol or clear the space next to that pin.");
            Accept(screen, island, stub, GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel,
                SchematicConnectionIdentity.PinAnchorKey(member.Pin.PlacedPinId), member.Pin.PlacedPinId, null);
        }

        private sealed record Stub(Pt A, Pt E, Combo? Label, Box? Envelope, Guid? Carrier);

        // §6.3 (e) and (f): the first admissible stub length; a stub ending on a same-net power symbol pin attaches to it.
        private bool TryStub(Screen screen, IslandState island, Pt a, (int Dx, int Dy) outward, Guid? ownPin, Guid owner,
            Variant variant, ConnectionLabelKind kind, out Stub stub, bool allowAttach = true)
        {
            var combo = new Combo(kind, island.Island.LabelText, SchematicConnectionGeometry.Spin(outward));
            foreach (int multiple in SchematicConnectionPolicy.StubMultiples)
            {
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
                if (Admit(screen, island, a, e, outward, envelope, ownPin, owner, null, null, variant))
                { stub = new(a, e, combo, envelope, null); return true; }
            }
            stub = null!;
            return false;
        }

        // §6.4 admission of a stub from a to e, pointing outward, with label envelope r (null when attaching to a carrier).
        private bool Admit(Screen screen, IslandState island, Pt a, Pt e, (int Dx, int Dy) outward, Box? r, Guid? ownPin, Guid owner,
            Guid? carrierPin, Guid? carrierSymbol, Variant variant) =>
            (lastRefusal = Refusal(policy, screen, island, a, e, outward, r, ownPin, owner, carrierPin, carrierSymbol, variant)) is null;

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
            // The hierarchical label goes on the island's first labelled stub; after (a) and (b) that is its first crossing.
            var kind = island.Island.UplinkSheetSymbolId is not null && !island.UplinkLabelled && island.Island.ChildSheetSymbolIds[0] == sheetId
                ? ConnectionLabelKind.Hierarchical : ConnectionLabelKind.Local;
            long top = sheet.Position.YNm, bottom = checked(sheet.Position.YNm + sheet.Size.YNm);
            for (long y = checked(top + policy.SheetPinPitchNm); y <= bottom - policy.SheetPinPitchNm; y = checked(y + policy.GridNm))
            {
                if (taken.Any(t => Math.Abs(t - y) < policy.SheetPinPitchNm)) continue;
                var a = new Pt(x, y);
                // The new sheet pin itself is not yet a point; its own sheet symbol is the stub's owner.
                if (!TryStub(screen, island, a, outward, null, sheetId, Variant.Stub, kind, out var stub, allowAttach: false)) continue;
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
            limitations.Add("Milestone 1 draws label stubs only; orthogonal wiring between pins is not attempted.");
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

        private static string Describe(ConnectionPlacedPin pin) => pin.Endpoint.ComponentId.ToString("D") + "." + pin.Endpoint.Pin;

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
