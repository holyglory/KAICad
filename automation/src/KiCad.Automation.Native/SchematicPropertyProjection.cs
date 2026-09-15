using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>Project resolved engineering reference/value fields into the full
/// typed native candidate. This is pure preparation, never live admission.</summary>
public static class SchematicPropertyProjection
{
    public static SchematicDesign Project(SchematicDesign baseline, SchematicDesign candidate,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken token = default)
    {
        var report = SchematicDesignBindings.Inspect(candidate, libraries, token);
        if (!report.IdentitiesResolved)
            throw Error("unresolved_design_bindings", "Resolve exact component and sheet identities before projecting properties.");
        var projected = candidate with { Schematic = candidate.Schematic.Clone() };
        var components = candidate.Engineering.Circuit.Components.ToDictionary(c => c.Id);
        var definitions = candidate.Engineering.Circuit.Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);
        var oldOccurrences = baseline.Engineering.Circuit.Symbols.ToDictionary(s => s.Id);
        var sheetPaths = candidate.SheetBindings.ToDictionary(s => s.SheetInstanceId, s => SchematicDesignBindings.PathKey(s.NativePath));
        var screens = projected.Schematic.Instances.ToDictionary(s => PathKey(s.Metadata.Document.SheetPath));
        var bindings = candidate.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId.ToString("D"));
        var grouped = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var occurrence in candidate.Engineering.Circuit.Symbols)
        {
            token.ThrowIfCancellationRequested();
            var component = components[occurrence.ComponentId];
            var screen = screens[sheetPaths[component.SheetInstanceId]];
            string id = bindings[occurrence.Id];
            int index = screen.Items.ToList().FindIndex(item => item.Is(SchematicSymbolInstance.Descriptor)
                && item.Unpack<SchematicSymbolInstance>().Id.Value == id);
            if (index < 0 || !oldOccurrences.TryGetValue(occurrence.Id, out var original))
                throw Error("unresolved_design_bindings", "Property changes require an existing exact symbol occurrence.");
            var symbol = screen.Items[index].Unpack<SchematicSymbolInstance>();
            if (occurrence.Unit != original.Unit || symbol.Unit?.Unit != occurrence.Unit)
                throw Error("unit_change_requires_electrical_update", "Unit changes require explicit pin-ownership and electrical reconciliation.");
            string key = screen.Metadata.ScreenId.Value + "#" + id;
            if (!grouped.TryGetValue(key, out var entries)) grouped.Add(key, entries = []);
            entries.Add(new(screen, index, symbol, component.Reference, definitions[component.DefinitionId].Value));
        }

        foreach (var entries in grouped.Values)
        {
            token.ThrowIfCancellationRequested();
            var first = entries[0];
            if (entries.Select(e => e.Value).Distinct(StringComparer.Ordinal).Count() != 1)
                throw Error("shared_symbol_value_conflict", "Shared physical symbols cannot carry different requested values.");
            if (entries.All(e => e.Symbol.ReferenceField?.Text?.Text_ == e.Reference && e.Symbol.ValueField?.Text?.Text_ == e.Value))
                continue;
            if (first.Symbol.InstanceRecords is null || entries.Any(e => e.Symbol.ReferenceField?.Text is null
                || e.Symbol.ValueField?.Text is null || !Equals(e.Symbol.InstanceRecords, first.Symbol.InstanceRecords)))
                throw Error("incomplete_symbol_property_projection", "Property changes require complete, consistent shared placement records and fields.");
            var records = first.Symbol.InstanceRecords.Clone();
            foreach (var entry in entries)
            {
                var matching = records.Records.Where(r => r.Path.SequenceEqual(entry.Symbol.Path.Path)).ToArray();
                if (matching.Length != 1 || matching[0].ProjectName != entry.Screen.Metadata.Document.Project.Name)
                    throw Error("ambiguous_symbol_property_owner", "The reference must identify exactly one placement in the owning project.");
                matching[0].Reference = entry.Reference;
            }
            // The full instance list is physically shared. Update every copy,
            // but preserve references/variants of unrelated project placements.
            foreach (var entry in entries)
            {
                entry.Symbol.ReferenceField.Text.Text_ = entry.Reference;
                entry.Symbol.ValueField.Text.Text_ = entry.Value;
                entry.Symbol.InstanceRecords = records.Clone();
                entry.Screen.Items[entry.Index] = Any.Pack(entry.Symbol);
            }
        }
        return projected;
    }

    private sealed record Entry(SchematicScreenData Screen, int Index, SchematicSymbolInstance Symbol, string Reference, string Value);
    private static string PathKey(Kiapi.Common.Types.SheetPath path) => string.Join('/', path.Path.Select(id => id.Value));
    private static AutomationException Error(string code, string message) => new(code, message);
}
