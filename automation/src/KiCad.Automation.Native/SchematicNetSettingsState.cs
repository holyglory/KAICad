using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;
using Kiapi.Common.Project;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal static class SchematicNetSettingsState
{
    internal static void Validate(SchematicNetSettings value)
    {
        if (value.DefaultClass is not { Name: "Default" } defaults)
            throw Invalid("Net settings require an explicit Default class.");
        var names = new HashSet<string>(StringComparer.Ordinal) { "Default" };
        Class(defaults);
        foreach (var entry in value.Classes)
        {
            Class(entry);
            if (!names.Add(entry.Name)) throw Invalid("Declared netclass names must be unique.");
        }
        SchematicDataXml.Write(value);
    }

    private static void Class(NetClass value)
    {
        if (value.Name.Length == 0 || value.Name.Contains('\0') || !value.HasPriority
            || value.Type != NetClassType.NctExplicit || value.Constituents.Count != 0)
            throw Invalid("Net classes require exact names, declared priorities and explicit ownership.");
    }

    internal static SchematicNetSettings Declared(SchematicNetSettings value)
    {
        Validate(value);
        var result = value.Clone(); result.LabelAssignments.Clear();
        var ordered = result.Classes.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Clone()).ToArray();
        result.Classes.Clear(); result.Classes.Add(ordered);
        return result;
    }

    internal static bool SameDeclared(SchematicNetSettings? left, SchematicNetSettings? right) =>
        left is null || right is null ? left is null && right is null : Declared(left).Equals(Declared(right));

    internal static bool Merge(SchematicNetSettings? baseline, SchematicNetSettings? xml,
        SchematicNetSettings? native, out SchematicNetSettings? result)
    {
        result = null;
        if (baseline is null || xml is null || native is null)
        {
            if (!Choose(baseline, xml, native, out var chosen)) return false;
            result = chosen?.Clone(); return true;
        }
        var before = Declared(baseline); var desired = Declared(xml); var current = Declared(native);
        if (!MergeClass(before.DefaultClass, desired.DefaultClass, current.DefaultClass, out var defaults)
            || !Choose(before.Patterns, desired.Patterns, current.Patterns, out var patterns)
            || !MergeMap(before.NetColors, desired.NetColors, current.NetColors, out var colors)
            || !MergeMap(before.ChainNetclasses, desired.ChainNetclasses, current.ChainNetclasses, out var chains)) return false;
        var classes = new List<NetClass>();
        var b = before.Classes.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var x = desired.Classes.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var n = current.Classes.ToDictionary(c => c.Name, StringComparer.Ordinal);
        foreach (string name in b.Keys.Concat(x.Keys).Concat(n.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!MergeClass(b.GetValueOrDefault(name), x.GetValueOrDefault(name), n.GetValueOrDefault(name), out var item)) return false;
            if (item is not null) classes.Add(item);
        }
        var merged = new SchematicNetSettings { DefaultClass = defaults };
        merged.Classes.Add(classes);
        merged.Patterns.Add(patterns.Select(p => p.Clone()));
        foreach (var pair in colors) merged.NetColors.Add(pair.Key, pair.Value.Clone());
        merged.ChainNetclasses.Add(chains);
        // This projection belongs to actual native connectivity, never to the
        // editable project preferences. A later electrical checkpoint refreshes it.
        foreach (var pair in native.LabelAssignments) merged.LabelAssignments.Add(pair.Key, pair.Value.Clone());
        Validate(merged); result = merged; return true;
    }

    private static bool MergeClass(NetClass? baseline, NetClass? xml, NetClass? native, out NetClass? result)
    {
        if (Choose(baseline, xml, native, out var chosen))
        { result = chosen?.Clone(); return true; }
        result = null;
        if (baseline is null || xml is null || native is null) return false;
        if (!MergeFields(baseline, xml, native, out NetClass? merged, 3, 4, 6)
            || !MergeFields(baseline.Board, xml.Board, native.Board, out NetClassBoardSettings? board)
            || !MergeFields(baseline.Schematic, xml.Schematic, native.Schematic, out NetClassSchematicSettings? schematic)) return false;
        merged!.Board = board; merged.Schematic = schematic;
        result = merged; return true;
    }

    private static bool MergeFields<T>(T? baseline, T? xml, T? native, out T? result,
        params int[] omitted) where T : class, IMessage<T>, new()
    {
        if (Choose(baseline, xml, native, out var chosen))
        { result = chosen?.Clone(); return true; }
        result = null;
        if (baseline is null || xml is null || native is null) return false;
        var merged = new T();
        foreach (var field in merged.Descriptor.Fields.InFieldNumberOrder().Where(f => !omitted.Contains(f.FieldNumber)))
        {
            object? Value(T value) => field.HasPresence && !field.Accessor.HasValue(value)
                ? null : field.Accessor.GetValue(value);
            if (!Choose(Value(baseline), Value(xml), Value(native), out var value)) return false;
            if (value is not null) field.Accessor.SetValue(merged, value);
        }
        result = merged.Clone(); return true;
    }

    private static bool MergeMap<T>(MapField<string, T> baseline, MapField<string, T> xml,
        MapField<string, T> native, out Dictionary<string, T> result)
    {
        result = new(StringComparer.Ordinal);
        foreach (string key in baseline.Keys.Concat(xml.Keys).Concat(native.Keys).Distinct(StringComparer.Ordinal))
        {
            var b = (baseline.TryGetValue(key, out var bv), bv);
            var x = (xml.TryGetValue(key, out var xv), xv);
            var n = (native.TryGetValue(key, out var nv), nv);
            if (!Choose(b, x, n, out var value)) return false;
            if (value.Item1) result.Add(key, value.Item2);
        }
        return true;
    }

    private static bool Choose<T>(T baseline, T xml, T native, out T value)
    {
        if (Equals(xml, native) || Equals(native, baseline)) { value = xml; return true; }
        if (Equals(xml, baseline)) { value = native; return true; }
        value = default!; return false;
    }

    private static AutomationException Invalid(string message) => new("unsupported_schematic_delta", message);
}
