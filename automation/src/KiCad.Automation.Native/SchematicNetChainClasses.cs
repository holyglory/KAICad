using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal static class SchematicNetChainClasses
{
    internal static SchematicNetChainClassState Normalize(SchematicNetChainClassState value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        static bool Valid(string text) => text.Length != 0 && !text.Contains('\0');
        if (value.Definitions.Any(name => !Valid(name) || !names.Add(name)))
            throw Invalid("Class definitions must be unique nonempty names without NUL.");
        if (value.Assignments.Any(pair => !Valid(pair.Key) || !Valid(pair.Value) || !names.Contains(pair.Value)))
            throw Invalid("Class assignments require nonempty chain names and declared classes.");
        SchematicDataXml.Write(value); // Reject unknown descriptor fields after domain validation.
        var result = value.Clone(); result.Definitions.Clear();
        result.Definitions.Add(names.Order(StringComparer.Ordinal));
        return result;
    }

    internal static bool Same(SchematicNetChainClassState? a, SchematicNetChainClassState? b) =>
        a is null || b is null ? a is null && b is null : Normalize(a).Equals(Normalize(b));

    internal static bool Merge(SchematicNetChainClassState? baseline, SchematicNetChainClassState? xml,
        SchematicNetChainClassState? native, out SchematicNetChainClassState? merged)
    {
        // Keep the chosen representation for unchanged or one-sided edits.
        if (Same(baseline, xml)) { merged = native?.Clone(); return true; }
        if (Same(baseline, native)) { merged = xml?.Clone(); return true; }
        if (Same(xml, native)) { merged = native?.Clone(); return true; }
        merged = null;
        if (baseline is null || xml is null || native is null) return false;
        var before = Normalize(baseline); var desired = Normalize(xml); var observed = Normalize(native);
        var b = before.Definitions.ToHashSet(StringComparer.Ordinal);
        var x = desired.Definitions.ToHashSet(StringComparer.Ordinal);
        var n = observed.Definitions.ToHashSet(StringComparer.Ordinal);
        var result = new SchematicNetChainClassState();
        foreach (string name in b.Union(x).Union(n).Order(StringComparer.Ordinal))
        {
            bool present = x.Contains(name) == b.Contains(name) ? n.Contains(name) : x.Contains(name);
            if (present) result.Definitions.Add(name);
        }
        foreach (string chain in before.Assignments.Keys.Union(desired.Assignments.Keys)
            .Union(observed.Assignments.Keys).Order(StringComparer.Ordinal))
        {
            string? old = before.Assignments.GetValueOrDefault(chain);
            string? fromXml = desired.Assignments.GetValueOrDefault(chain);
            string? fromNative = observed.Assignments.GetValueOrDefault(chain);
            string? choice;
            if (fromXml == old) choice = fromNative;
            else if (fromNative == old || fromXml == fromNative) choice = fromXml;
            else return false;
            if (choice is not null) result.Assignments.Add(chain, choice);
        }
        // Deleting a class while the other side assigns a new chain to it is
        // an actual ownership conflict, not permission to invent a replacement.
        if (result.Assignments.Values.Any(name => !result.Definitions.Contains(name))) return false;
        merged = result; return true;
    }

    private static AutomationException Invalid(string message) => new("invalid_net_chain_classes", message);
}
