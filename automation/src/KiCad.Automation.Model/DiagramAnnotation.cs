using System.Collections.Immutable;
using System.Globalization;

namespace KiCad.Automation.Model;

public enum DiagramAnnotationTargetKind { Canvas, Block, Connection }
public enum DiagramAnnotationRole { Comment, Instruction }
public sealed record DiagramAnnotationPoint(decimal X, decimal Y)
{
    public static DiagramAnnotationPoint Parse(string x, string y) => new(Number(x), Number(y));
    private static decimal Number(string value)
    {
        if (value is null || !decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal parsed)
            || Canonical(value) != Canonical(parsed.ToString(CultureInfo.InvariantCulture)))
            throw new AutomationException("invalid_annotation_coordinate", "Annotation coordinates require exact decimal values without rounding.");
        return parsed;
    }
    private static string Canonical(string text)
    {
        bool negative = text.StartsWith('-'); text = text.TrimStart('+', '-');
        int dot = text.IndexOf('.'); string integer = (dot < 0 ? text : text[..dot]).TrimStart('0');
        string fraction = dot < 0 ? "" : text[(dot + 1)..].TrimEnd('0');
        if (integer.Length == 0) integer = "0";
        return (negative && (integer != "0" || fraction.Length != 0) ? "-" : "") + integer + (fraction.Length == 0 ? "" : "." + fraction);
    }
}
public sealed record DiagramAnnotationStroke(ImmutableArray<DiagramAnnotationPoint> Points);
public sealed record DiagramAnnotationTarget(DiagramAnnotationTargetKind Kind, Guid? TargetId,
    string? UnresolvedReason = null);

/// <summary>User/agent commentary and original sketch paths, pinned by the containing
/// block revision. Diagram-unit coordinates are presentation, never PCB or schematic
/// physical dimensions. Editing a note cannot overwrite its old root snapshots.</summary>
public sealed record DiagramAnnotation(Guid Id, DiagramAnnotationRole Role, string Text,
    DiagramAnnotationTarget Target, DiagramAnnotationPoint? Position,
    ImmutableArray<DiagramAnnotationStroke> Strokes, RequirementRevisionOrigin Origin)
{
    public void Validate()
    {
        if (Id == Guid.Empty || !Enum.IsDefined(Role) || Text is null || Target is null || Origin is null || Strokes.IsDefault
            || !Enum.IsDefined(Target.Kind)) throw Invalid("An annotation needs an identity, explicit role/target, original content and origin.");
        if (string.IsNullOrWhiteSpace(Text) && Strokes.IsEmpty)
            throw Invalid("An annotation must retain text or original sketch strokes; an empty note is not a saved instruction.");
        if (Target.Kind == DiagramAnnotationTargetKind.Canvas ? Target.TargetId is not null || Target.UnresolvedReason is not null
            : Target.TargetId is null || Target.TargetId == Guid.Empty)
            throw Invalid("Canvas notes belong to their local diagram; element notes need their exact original target identity.");
        if (Target.UnresolvedReason is { } reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw Invalid("An unresolved annotation target needs a reason.");
            DiagramEndpointBinding.Text(reason);
        }
        DiagramEndpointBinding.Text(Text); Origin.Validate();
        if (Position is { } position) Point(position);
        foreach (var stroke in Strokes)
        {
            if (stroke is null || stroke.Points.IsDefault || stroke.Points.Length < 2)
                throw Invalid("A sketch stroke needs at least two original points.");
            foreach (var point in stroke.Points) Point(point);
        }
    }

    public bool SameContents(DiagramAnnotation other) => other is not null && Id == other.Id && Role == other.Role
        && Text == other.Text && Target == other.Target && Position == other.Position && DiagramRequirementHistory.SameOrigin(Origin, other.Origin)
        && Strokes.Length == other.Strokes.Length && Strokes.Zip(other.Strokes).All(s => s.First.Points.SequenceEqual(s.Second.Points));

    private static void Point(DiagramAnnotationPoint? point)
    {
        if (point is null || point.X is < -1_000_000_000m or > 1_000_000_000m || point.Y is < -1_000_000_000m or > 1_000_000_000m)
            throw Invalid("Diagram annotations require finite presentation coordinates within the supported diagram-unit range.");
    }
    private static AutomationException Invalid(string message) => new("invalid_diagram_annotation", message);
}
