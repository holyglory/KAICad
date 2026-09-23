using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

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
