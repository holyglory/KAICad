using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.Reflection;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using DocumentRevision = KiCad.Automation.Model.DocumentRevision;

namespace KiCad.Automation.Native;

// The wiring planner's building blocks (cn1-wiring-intent.md §6.1 and §6.7): the drawing policy taken
// from the project's grid and text size, axis-aligned stub geometry, and the deterministic identities
// of generated connection items and measurement probes. Lane 2A owns this file.

/// <summary>The drawing policy for generated connection items (cn1-wiring-intent.md §6.1). Every
/// distance comes from the project's own connection grid and default text size, never from a
/// guessed constant: stubs are whole grid multiples, and clearances, page inset, sheet-pin pitch
/// and the label orientation tolerance are derived from that grid.</summary>
public sealed record SchematicConnectionPolicy(long GridNm, long ClearanceNm, long TextSizeNm, long PageInsetNm,
    long SheetPinPitchNm, long LabelBackToleranceNm)
{
    /// <summary>Stub lengths, in connection-grid multiples, tried in this order (§6.3 f).</summary>
    public static readonly int[] StubMultiples = [2, 3, 4, 6, 8];

    /// <summary>Derive the policy from a captured hierarchy (§6.1). Every sheet instance must report the
    /// same connection grid, and it must be positive and a whole number of the native 100 nm unit. The
    /// default text size is the project's setting as the first captured instance reports it, and it must
    /// be positive. Otherwise the editor did not supply what realization needs and this fails with
    /// <c>realization_grid_unavailable</c> before anything is generated. A grid whose derived page inset
    /// (twice the grid) cannot be represented is treated as unavailable too.</summary>
    public static SchematicConnectionPolicy FromSnapshot(SchematicHierarchyData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Instances.Count == 0) throw Unavailable("The captured schematic has no sheet instance.");
        SchematicFormattingSettings? first = null;
        foreach (var screen in data.Instances)
        {
            var formatting = screen.Metadata?.Formatting
                ?? throw Unavailable("A sheet instance does not report the project's connection grid.");
            first ??= formatting;
            if (formatting.ConnectionGridNm != first.ConnectionGridNm)
                throw Unavailable("Sheet instances disagree on the project's connection grid.");
        }
        long grid = first!.ConnectionGridNm, text = first.DefaultTextSizeNm;
        if (grid <= 0 || grid % 100 != 0 || grid > long.MaxValue / 2)
            throw Unavailable("The project's connection grid must be a positive whole number of 100 nm.");
        if (text <= 0)
            throw Unavailable("The project's default text size must be positive.");
        return new(grid, grid / 2 / 100 * 100, text, 2 * grid, 2 * grid, grid / 2);
    }

    private static AutomationException Unavailable(string message) =>
        new(SchematicConnectionErrors.RealizationGridUnavailable, message);
}

/// <summary>Axis-aligned stub geometry for generated connections (cn1-wiring-intent.md §6.1). A stub
/// leaves a pin away from its body; its label faces the same way, matching the native label shapes
/// and sheet-pin sides in §0.</summary>
public static class SchematicConnectionGeometry
{
    /// <summary>The unit vector pointing from the pin's connection point away from the symbol body,
    /// the reverse of the measured <c>body_direction</c>. Anything but one of the four axis
    /// directions fails with <c>realization_pin_geometry_mismatch</c>.</summary>
    public static (int Dx, int Dy) Outward(SchematicPinAnchor pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (!IsAxisUnit(pin.BodyDirectionX, pin.BodyDirectionY))
            throw Mismatch("A measured pin must point along exactly one schematic axis.");
        return (-pin.BodyDirectionX, -pin.BodyDirectionY);
    }

    /// <summary>The end of a stub of <paramref name="length"/> nanometres leaving
    /// <paramref name="anchor"/> in the <paramref name="outward"/> direction.</summary>
    public static Vector2 StubEnd(Vector2 anchor, (int Dx, int Dy) outward, long length)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (!IsAxisUnit(outward.Dx, outward.Dy)) throw Mismatch("A stub must leave its pin along exactly one schematic axis.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return new() { XNm = checked(anchor.XNm + outward.Dx * length), YNm = checked(anchor.YNm + outward.Dy * length) };
    }

    /// <summary>The label spin style for a stub leaving in <paramref name="outward"/>:
    /// (1,0) right, (-1,0) left, (0,-1) up and (0,1) bottom, in schematic coordinates where y grows down.</summary>
    public static SchematicLabelSpinStyle Spin((int Dx, int Dy) outward) => outward switch
    {
        (1, 0) => SchematicLabelSpinStyle.SlssRight,
        (-1, 0) => SchematicLabelSpinStyle.SlssLeft,
        (0, -1) => SchematicLabelSpinStyle.SlssUp,
        (0, 1) => SchematicLabelSpinStyle.SlssBottom,
        _ => throw Mismatch("A label must face along exactly one schematic axis.")
    };

    private static bool IsAxisUnit(int dx, int dy) => (Math.Abs((long)dx) + Math.Abs((long)dy)) == 1;

    private static AutomationException Mismatch(string message) =>
        new(SchematicConnectionErrors.RealizationPinGeometryMismatch, message);
}

/// <summary>What a generated connection item is for (cn1-wiring-intent.md §6.7 and §6.8).</summary>
public enum GeneratedConnectionRole { StubWire = 1, StubLabel = 2, AnchorLabel = 3, SheetPin = 4, SheetPinWire = 5, SheetPinLabel = 6, RouteWire = 7, Junction = 8 }

/// <summary>Deterministic identities for generated connection items and measurement probes
/// (cn1-wiring-intent.md §6.7). The same origin, native revision, desired XML bytes, screen and
/// attachment always give the same ID, and any change to one of them gives another, so a retried
/// realization repeats its IDs exactly while a later XML edit never reuses them. Net IDs are not
/// part of the material, so every instance of a repeated screen receives identical items.
/// Changing either construction requires a new namespace string.</summary>
public static class SchematicConnectionIdentity
{
    public const string RealizationNamespace = "kicad-connection-realization-v1";
    public const string ProbeNamespace = "kicad-connection-probe-v1";

    /// <summary>The role word that enters the identity material.</summary>
    public static string RoleName(GeneratedConnectionRole role) => role switch
    {
        GeneratedConnectionRole.StubWire => "stub-wire",
        GeneratedConnectionRole.StubLabel => "stub-label",
        GeneratedConnectionRole.AnchorLabel => "anchor-label",
        GeneratedConnectionRole.SheetPin => "sheet-pin",
        GeneratedConnectionRole.SheetPinWire => "sheet-pin-wire",
        GeneratedConnectionRole.SheetPinLabel => "sheet-pin-label",
        GeneratedConnectionRole.RouteWire => "route-wire",
        GeneratedConnectionRole.Junction => "junction",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown generated connection role.")
    };

    /// <summary>The ID of one generated item. <paramref name="revision"/> is the intent's native
    /// revision, <paramref name="desiredSha256"/> the lowercase hex SHA-256 of the desired XML bytes, and
    /// <paramref name="anchorKey"/> comes from the matching key helper below. <paramref name="ordinal"/> is
    /// the route segment number for <see cref="GeneratedConnectionRole.RouteWire"/> and 0 otherwise.</summary>
    public static Guid Generated(Guid origin, DocumentRevision revision, string desiredSha256, Guid screenId,
        GeneratedConnectionRole role, string anchorKey, int ordinal = 0)
    {
        RequireId(origin, nameof(origin));
        RequireRevision(revision);
        ArgumentNullException.ThrowIfNull(desiredSha256);
        if (desiredSha256.Length != 64 || desiredSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The desired XML digest must be 64 lowercase hexadecimal characters.", nameof(desiredSha256));
        RequireId(screenId, nameof(screenId));
        RequireText(anchorKey, nameof(anchorKey), allowEmpty: false);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        if (role != GeneratedConnectionRole.RouteWire && ordinal != 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Only route segments carry a non-zero ordinal.");
        return Hash(RealizationNamespace, origin.ToString("D"), revision.Epoch,
            revision.Sequence.ToString(CultureInfo.InvariantCulture), desiredSha256, screenId.ToString("D"),
            RoleName(role), anchorKey, ordinal.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The ID of a measurement-only prototype: a label prototype, or with
    /// <paramref name="symbolId"/> and <paramref name="rotation"/> a rotated symbol probe. Probe IDs are
    /// measured but never committed. §6.7 does not spell out how the kind, spin and shape are written
    /// into the material, so this build fixes it: <paramref name="kind"/> enters as the probed item's
    /// full protobuf message name (for example <c>kiapi.schematic.types.LocalLabel</c>), and spin and
    /// shape as their protobuf enum numbers, which stay stable when enum names change. Changing this
    /// encoding requires a new namespace (§14).</summary>
    public static Guid Probe(DocumentRevision checkpointRevision, Guid screenId, MessageDescriptor kind, string text,
        SchematicLabelSpinStyle spin, SchematicLabelShape shape, Guid? symbolId = null, int? rotation = null)
    {
        RequireRevision(checkpointRevision);
        RequireId(screenId, nameof(screenId));
        ArgumentNullException.ThrowIfNull(kind);
        RequireText(text, nameof(text), allowEmpty: true);
        if (symbolId.HasValue != rotation.HasValue)
            throw new ArgumentException("A symbol probe needs both its symbol and its rotation.", nameof(rotation));
        if (symbolId is { } symbol) RequireId(symbol, nameof(symbolId));
        if (rotation is { } degrees && degrees is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(rotation), degrees, "A probe rotation is 0, 90, 180 or 270 degrees.");
        string[] fields = [ProbeNamespace, checkpointRevision.Epoch, checkpointRevision.Sequence.ToString(CultureInfo.InvariantCulture),
            screenId.ToString("D"), kind.FullName, text, ((int)spin).ToString(CultureInfo.InvariantCulture),
            ((int)shape).ToString(CultureInfo.InvariantCulture)];
        return symbolId is { } probed
            ? Hash([.. fields, probed.ToString("D"), rotation!.Value.ToString(CultureInfo.InvariantCulture)])
            : Hash(fields);
    }

    /// <summary>Anchor key of stub, join and anchor-label items: the attached placed pin.</summary>
    public static string PinAnchorKey(Guid placedPinId)
    {
        RequireId(placedPinId, nameof(placedPinId));
        return placedPinId.ToString("D");
    }

    /// <summary>Anchor key of sheet-pin items: the sheet symbol and the port text.</summary>
    public static string SheetPinAnchorKey(Guid sheetSymbolId, string portText)
    {
        RequireId(sheetSymbolId, nameof(sheetSymbolId));
        RequireText(portText, nameof(portText), allowEmpty: false);
        return sheetSymbolId.ToString("D") + "#" + portText;
    }

    /// <summary>A routed net's key: the ordinal-minimum placed-pin ID among its terminals.</summary>
    public static string NetKey(IEnumerable<Guid> terminalPlacedPinIds)
    {
        ArgumentNullException.ThrowIfNull(terminalPlacedPinIds);
        var ids = terminalPlacedPinIds.ToArray();
        if (ids.Length == 0) throw new ArgumentException("A routed net needs at least one terminal.", nameof(terminalPlacedPinIds));
        foreach (var id in ids) RequireId(id, nameof(terminalPlacedPinIds));
        return ids.Select(id => id.ToString("D")).Min(StringComparer.Ordinal)!;
    }

    /// <summary>Anchor key of one route segment.</summary>
    public static string RouteAnchorKey(string netKey, int segmentOrdinal)
    {
        RequireText(netKey, nameof(netKey), allowEmpty: false);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentOrdinal);
        return netKey + "#" + segmentOrdinal.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Anchor key of a junction at (<paramref name="xNm"/>, <paramref name="yNm"/>).</summary>
    public static string JunctionAnchorKey(string netKey, long xNm, long yNm)
    {
        RequireText(netKey, nameof(netKey), allowEmpty: false);
        return netKey + "#" + xNm.ToString(CultureInfo.InvariantCulture) + "," + yNm.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Record <paramref name="id"/> as used. An ID already present in the observed schematic,
    /// the candidate, or among earlier generated or probe IDs fails with <c>realization_identity_collision</c>.</summary>
    public static void Claim(ISet<Guid> used, Guid id)
    {
        ArgumentNullException.ThrowIfNull(used);
        if (!used.Add(id))
            throw new AutomationException(SchematicConnectionErrors.RealizationIdentityCollision,
                "A generated connection identity " + id.ToString("D") + " is already in use.");
    }

    private static Guid Hash(params string[] fields)
    {
        byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', fields)));
        b[6] = (byte)((b[6] & 0x0F) | 0x80);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b.AsSpan(0, 16), bigEndian: true);
    }

    private static void RequireRevision(DocumentRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        RequireText(revision.Epoch, nameof(revision), allowEmpty: false);
    }

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty) throw new ArgumentException("A non-empty identity is required.", name);
    }

    // The material joins fields with line feeds, so no field may contain a control character.
    private static void RequireText(string value, string name, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if ((!allowEmpty && value.Length == 0) || value.Any(char.IsControl))
            throw new ArgumentException("Identity text must be non-empty and free of control characters.", name);
    }
}
