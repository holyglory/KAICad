using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal static class SchematicReferenceInventoryState
{
    internal static SchematicReferenceInventory Normalize(SchematicReferenceInventory value)
    {
        if (value.Allocated.Any(r => string.IsNullOrEmpty(r) || r.Contains('\0'))
            || value.Allocated.Distinct(StringComparer.Ordinal).Count() != value.Allocated.Count)
            throw new AutomationException("unsupported_schematic_delta",
                "Allocated reference entries must be unique, nonempty and NUL-free; native persistence validates their canonical form.");
        var result = value.Clone();
        result.Allocated.Clear(); result.Allocated.Add(value.Allocated.Order(StringComparer.Ordinal));
        return result;
    }

    internal static bool Same(SchematicReferenceInventory? a, SchematicReferenceInventory? b) =>
        a is null || b is null ? a is null && b is null : Normalize(a).Equals(Normalize(b));
}
