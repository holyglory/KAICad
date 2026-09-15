using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

// Exact sheet-space transforms. Different angle/mirror encodings can describe
// the same native geometry; keep that equivalence separate from XML bytes.
internal static class SchematicOrientation
{
    internal readonly record struct Matrix(int Xx, int Xy, int Yx, int Yy)
    {
        internal Matrix Times(Matrix right) => new(Xx * right.Xx + Xy * right.Yx,
            Xx * right.Xy + Xy * right.Yy, Yx * right.Xx + Yy * right.Yx,
            Yx * right.Xy + Yy * right.Yy);
    }

    private static readonly SchematicConnectedTransformKind[] Kinds =
    [SchematicConnectedTransformKind.SctRotateClockwise, SchematicConnectedTransformKind.SctRotateCounterclockwise,
        SchematicConnectedTransformKind.SctMirrorLeftRight, SchematicConnectedTransformKind.SctMirrorUpDown];

    internal static Matrix Operation(SchematicConnectedTransformKind kind) => kind switch
    {
        SchematicConnectedTransformKind.SctRotateClockwise => new(0, -1, 1, 0),
        SchematicConnectedTransformKind.SctRotateCounterclockwise => new(0, 1, -1, 0),
        SchematicConnectedTransformKind.SctMirrorLeftRight => new(-1, 0, 0, 1),
        SchematicConnectedTransformKind.SctMirrorUpDown => new(1, 0, 0, -1),
        _ => throw new AutomationException("invalid_transform", "A connected quarter-turn or reflection is required.")
    };

    internal static Matrix Geometry(SymbolPlacement placement)
    {
        placement.Validate();
        Matrix matrix = new(1, 0, 0, 1);
        for (int angle = 0; angle < placement.RotationDegrees; angle += 90)
            matrix = Operation(SchematicConnectedTransformKind.SctRotateCounterclockwise).Times(matrix);
        if (placement.MirrorX) matrix = Operation(SchematicConnectedTransformKind.SctMirrorUpDown).Times(matrix);
        if (placement.MirrorY) matrix = Operation(SchematicConnectedTransformKind.SctMirrorLeftRight).Times(matrix);
        return matrix;
    }

    internal static bool Equivalent(SymbolPlacement? left, SymbolPlacement? right) => left == right
        || left is not null && right is not null && left.XMillimeters == right.XMillimeters
        && left.YMillimeters == right.YMillimeters && left.Locked == right.Locked && Geometry(left) == Geometry(right);

    internal static IReadOnlyList<SchematicConnectedTransformKind> Plan(SymbolPlacement current, SymbolPlacement desired)
    {
        var start = Geometry(current); var target = Geometry(desired);
        var seen = new HashSet<Matrix> { start };
        var queue = new Queue<(Matrix Geometry, SchematicConnectedTransformKind[] Steps)>();
        queue.Enqueue((start, []));
        while (queue.TryDequeue(out var entry))
        {
            if (entry.Geometry == target) return entry.Steps;
            foreach (var kind in Kinds)
            {
                var next = Operation(kind).Times(entry.Geometry);
                if (seen.Add(next)) queue.Enqueue((next, [.. entry.Steps, kind]));
            }
        }
        throw new AutomationException("unsupported_orientation", "No exact connected native orientation sequence exists.");
    }
}
