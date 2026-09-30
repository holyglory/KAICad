namespace KiCad.Automation.Model;

// Classification, requirement strength and source verification are independent.
// In particular an absolute maximum is never an operating recommendation.
public enum ParameterKind { Unclassified, Nominal, OperatingLimit, AbsoluteMaximum, Measurement }
public enum ToleranceKind { Absolute, Percent }
public sealed record ParameterTolerance(ToleranceKind Kind, decimal Minus, decimal Plus);
public sealed record QuantityIssue(Guid StatementId, string Code, string Message);

// The enclosing guidance statement owns applicability, provenance and
// verification. This is not the standalone EngineeringQuantity evidence record.
public sealed record GuidanceQuantity(ParameterKind Kind, string Unit, decimal? Nominal = null,
    decimal? Minimum = null, decimal? Maximum = null, ParameterTolerance? Tolerance = null,
    string? UnknownReason = null)
{
    internal void ValidateShape()
    {
        if (!Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(Unit))
            throw Invalid("A quantity requires an explicit classification and unit.");
        if (Nominal is null && Minimum is null && Maximum is null && string.IsNullOrWhiteSpace(UnknownReason))
            throw Invalid("A quantity without known values needs an explicit reason; zero is not an unknown value.");
        if (UnknownReason is not null && string.IsNullOrWhiteSpace(UnknownReason))
            throw Invalid("An unknown reason cannot be blank.");
        if (Tolerance is { } tolerance && (!Enum.IsDefined(tolerance.Kind) || tolerance.Minus < 0 || tolerance.Plus < 0))
            throw Invalid("Tolerance requires an explicit absolute/percent kind and nonnegative minus/plus values.");
    }

    internal IEnumerable<QuantityIssue> Inspect(Guid statementId)
    {
        // Contradictory source claims remain stored and inspectable, rather
        // than being fixed, discarded or treated as a verified operating limit.
        if (Minimum is decimal minimum && Maximum is decimal maximum && minimum > maximum)
            yield return new(statementId, "inverted_range", "The declared minimum exceeds the maximum under this applicability.");
        if (Nominal is decimal nominal)
        {
            if (Minimum is decimal lower && nominal < lower)
                yield return new(statementId, "nominal_below_minimum", "The declared nominal value is below the minimum.");
            if (Maximum is decimal upper && nominal > upper)
                yield return new(statementId, "nominal_above_maximum", "The declared nominal value is above the maximum.");
        }
    }

    private static AutomationException Invalid(string message) => new("invalid_quantity", message);
}
