using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>The drawing policy for generated connection items (cn1-wiring-intent.md §6.1). Every
/// distance comes from the project's own connection grid and default text size, never from a
/// guessed constant: stubs are whole grid multiples, and clearances, page inset, sheet-pin pitch
/// and the label orientation tolerance are derived from that grid.</summary>
public sealed record SchematicConnectionPolicy(long GridNm, long ClearanceNm, long TextSizeNm, long PageInsetNm,
    long SheetPinPitchNm, long LabelBackToleranceNm)
{
    /// <summary>Stub lengths, in connection-grid multiples, tried in this order (§6.3 f).</summary>
    public static readonly int[] StubMultiples = [2, 3, 4, 6, 8];

    /// <summary>Derive the policy from a captured hierarchy. Every sheet instance must report the same
    /// persisted formatting; the connection grid must be positive and a whole number of the native
    /// 100 nm unit, and the default text size positive and exactly representable. Otherwise the
    /// editor did not supply what realization needs and this fails with
    /// <c>realization_grid_unavailable</c> before anything is generated.</summary>
    public static SchematicConnectionPolicy FromSnapshot(SchematicHierarchyData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Instances.Count == 0) throw Unavailable("The captured schematic has no sheet instance.");
        SchematicFormattingSettings? first = null;
        foreach (var screen in data.Instances)
        {
            var formatting = screen.Metadata?.Formatting
                ?? throw Unavailable("A sheet instance does not report the project's connection grid and text size.");
            first ??= formatting;
            if (formatting.ConnectionGridNm != first.ConnectionGridNm || formatting.DefaultTextSizeNm != first.DefaultTextSizeNm)
                throw Unavailable("Sheet instances disagree on the project's connection grid or default text size.");
        }
        long grid = first!.ConnectionGridNm, text = first.DefaultTextSizeNm;
        if (grid <= 0 || grid % 100 != 0 || grid > long.MaxValue / 2)
            throw Unavailable("The project's connection grid must be a positive whole number of 100 nm.");
        if (text <= 0 || text % 100 != 0)
            throw Unavailable("The project's default text size must be a positive whole number of 100 nm.");
        return new(grid, grid / 2 / 100 * 100, text, 2 * grid, 2 * grid, grid / 2);
    }

    private static AutomationException Unavailable(string message) =>
        new(SchematicConnectionErrors.RealizationGridUnavailable, message);
}
