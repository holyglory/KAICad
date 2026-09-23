using System.Runtime.CompilerServices;

namespace KiCad.Automation.Model;

/// <summary>One object that caused a structured refusal, for example a connection or a
/// realization that still uses a boundary interface (contract rbg-v2 section 4.5, G6).</summary>
public sealed record AutomationErrorDetail(string Kind, Guid? ScopeBlockId, Guid? ObjectId, string Message);

/// <summary>Structured details attached to an <see cref="AutomationException"/>.
/// Contract rbg-v2 G6 adds a details parameter to AutomationException itself, which lives in the
/// frozen shared Identity.cs; until that seam lands this side table carries the same list without
/// changing the shared type. Details never replace the stable error code.</summary>
public static class DiagramErrorDetails
{
    private static readonly ConditionalWeakTable<AutomationException, IReadOnlyList<AutomationErrorDetail>> Details = new();

    public static AutomationException Attach(AutomationException error, IEnumerable<AutomationErrorDetail> details)
    {
        ArgumentNullException.ThrowIfNull(error); ArgumentNullException.ThrowIfNull(details);
        Details.AddOrUpdate(error, details.ToArray());
        return error;
    }

    public static IReadOnlyList<AutomationErrorDetail> Of(Exception? error) =>
        error is AutomationException automation && Details.TryGetValue(automation, out var details) ? details : [];
}
