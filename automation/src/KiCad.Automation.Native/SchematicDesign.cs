using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record SchematicSheetBinding(Guid SheetInstanceId, IReadOnlyList<Guid> NativePath);
public sealed record SchematicSymbolBinding(Guid SymbolOccurrenceId, Guid NativeObjectId);
public sealed record SchematicDesign(EngineeringDesign Engineering, SchematicHierarchyData Schematic,
    IReadOnlyList<SchematicSheetBinding> SheetBindings, IReadOnlyList<SchematicSymbolBinding> SymbolBindings);
public sealed record SchematicBindingIssue(string Code, Guid? ModelId, string? NativePath, Guid? NativeObjectId);
public sealed record SchematicBindingDifference(Guid SymbolOccurrenceId, string Field, string? ModelValue, string? NativeValue);
public sealed record SchematicBindingReport(IReadOnlyList<SchematicBindingIssue> Issues,
    IReadOnlyList<SchematicBindingDifference> Differences, IReadOnlyList<HierarchyCoverageGap> CoverageGaps)
{
    // This proves only identity resolution, never electrical equivalence, native coverage or admission.
    public bool IdentitiesResolved => Issues.Count == 0;
}

/// <summary>Exact, explicit cross-view identity resolution. No reference/name/position matching,
/// no live access and no mutation. Drift remains distinct from missing or ambiguous identity.</summary>
public static class SchematicDesignBindings
{
    public static SchematicBindingReport Inspect(SchematicDesign design,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        design.Engineering.Validate(libraries);
        var topology = SchematicHierarchyTopology.Inspect(design.Schematic, cancellationToken);
        var issues = new List<SchematicBindingIssue>();
        var differences = new List<SchematicBindingDifference>();
        void Issue(string code, Guid? id = null, string? path = null, Guid? native = null) => issues.Add(new(code, id, path, native));
        foreach (var issue in topology.Issues) Issue("native_" + issue.Code, path: issue.InstancePath,
            native: Guid.TryParse(issue.ObjectId, out var native) ? native : null);
        if (!topology.IsValid) return new(issues, differences, topology.CoverageGaps);

        var circuit = design.Engineering.Circuit;
        var modelSheets = circuit.SheetInstances.ToDictionary(s => s.Id);
        var components = circuit.Components.ToDictionary(c => c.Id);
        var definitions = circuit.Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);
        var occurrences = circuit.Symbols.ToDictionary(s => s.Id);
        var screens = design.Schematic.Instances.ToDictionary(s =>
            string.Join('/', s.Metadata.Document.SheetPath.Path.Select(id => id.Value)), StringComparer.Ordinal);
        var mappedSheets = new Dictionary<Guid, (string Path, SchematicScreenData Screen)>();
        var duplicateSheets = design.SheetBindings.GroupBy(s => s.SheetInstanceId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var duplicatePaths = design.SheetBindings.GroupBy(s => PathKey(s.NativePath), StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var binding in design.SheetBindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = PathKey(binding.NativePath);
            if (binding.SheetInstanceId == Guid.Empty || binding.NativePath.Count == 0 || binding.NativePath.Contains(Guid.Empty))
                Issue("invalid_sheet_binding", binding.SheetInstanceId, path);
            else if (duplicateSheets.Contains(binding.SheetInstanceId) || duplicatePaths.Contains(path))
                Issue("ambiguous_sheet_binding", binding.SheetInstanceId, path);
            else if (!modelSheets.ContainsKey(binding.SheetInstanceId))
                Issue("unknown_model_sheet", binding.SheetInstanceId, path);
            else if (!screens.TryGetValue(path, out var screen))
                Issue("missing_native_sheet", binding.SheetInstanceId, path);
            else mappedSheets.Add(binding.SheetInstanceId, (path, screen));
        }
        foreach (var sheet in modelSheets.Values)
        {
            if (!mappedSheets.TryGetValue(sheet.Id, out var target)) { Issue("unmapped_model_sheet", sheet.Id); continue; }
            int split = target.Path.LastIndexOf('/');
            if (sheet.ParentId is Guid parent)
            {
                if (!mappedSheets.TryGetValue(parent, out var parentTarget) || split < 0 || parentTarget.Path != target.Path[..split])
                    Issue("sheet_parent_mismatch", sheet.Id, target.Path);
            }
            else if (split >= 0) Issue("sheet_parent_mismatch", sheet.Id, target.Path);
        }
        foreach (var group in mappedSheets.GroupBy(p => modelSheets[p.Key].DefinitionId))
            if (group.Select(p => p.Value.Screen.Metadata.ScreenId.Value).Distinct(StringComparer.Ordinal).Count() > 1)
                Issue("shared_definition_mismatch", group.Key);
        foreach (var group in mappedSheets.GroupBy(p => p.Value.Screen.Metadata.ScreenId.Value, StringComparer.Ordinal))
            if (group.Select(p => modelSheets[p.Key].DefinitionId).Distinct().Count() > 1)
                Issue("shared_screen_mismatch", path: group.First().Value.Path);
        var usedPaths = mappedSheets.Values.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        foreach (string path in screens.Keys)
            if (!usedPaths.Contains(path)) Issue("unmapped_native_sheet", path: path);

        var nativeSymbols = new Dictionary<string, (string Path, Guid Id, SchematicSymbolInstance Symbol)>(StringComparer.Ordinal);
        var ambiguousNative = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, screen) in screens)
        foreach (var packed in screen.Items.Where(p => p.Is(SchematicSymbolInstance.Descriptor)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = packed.Unpack<SchematicSymbolInstance>();
            if (!Guid.TryParseExact(symbol.Id?.Value, "D", out Guid id) || id == Guid.Empty
                || symbol.Id.Value != id.ToString("D") || !Equals(symbol.Path, screen.Metadata.Document.SheetPath))
            {
                Issue("invalid_native_symbol_identity", path: path);
                continue;
            }
            string key = ObjectKey(path, id);
            if (!nativeSymbols.TryAdd(key, (path, id, symbol)))
            {
                ambiguousNative.Add(key);
                Issue("duplicate_native_symbol", path: path, native: id);
            }
        }
        var duplicateOccurrences = design.SymbolBindings.GroupBy(s => s.SymbolOccurrenceId)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var candidates = new List<(SchematicSymbolBinding Binding, SymbolOccurrence Occurrence, string Path, string Key)>();
        foreach (var binding in design.SymbolBindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (binding.SymbolOccurrenceId == Guid.Empty || binding.NativeObjectId == Guid.Empty)
                Issue("invalid_symbol_binding", binding.SymbolOccurrenceId, native: binding.NativeObjectId);
            else if (duplicateOccurrences.Contains(binding.SymbolOccurrenceId))
                Issue("ambiguous_symbol_binding", binding.SymbolOccurrenceId, native: binding.NativeObjectId);
            else if (!occurrences.TryGetValue(binding.SymbolOccurrenceId, out var occurrence))
                Issue("unknown_model_symbol", binding.SymbolOccurrenceId, native: binding.NativeObjectId);
            else if (!mappedSheets.TryGetValue(occurrence.EffectiveSheetInstanceId(components[occurrence.ComponentId]), out var sheet))
                Issue("unresolved_symbol_sheet", binding.SymbolOccurrenceId, native: binding.NativeObjectId);
            else candidates.Add((binding, occurrence, sheet.Path, ObjectKey(sheet.Path, binding.NativeObjectId)));
        }
        var duplicateTargets = candidates.GroupBy(c => c.Key, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var mappedOccurrences = new HashSet<Guid>();
        var mappedNative = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var binding = candidate.Binding;
            if (duplicateTargets.Contains(candidate.Key) || ambiguousNative.Contains(candidate.Key))
                Issue("ambiguous_native_symbol_binding", binding.SymbolOccurrenceId, candidate.Path, binding.NativeObjectId);
            else if (!nativeSymbols.TryGetValue(candidate.Key, out var target))
                Issue("missing_native_symbol", binding.SymbolOccurrenceId, candidate.Path, binding.NativeObjectId);
            else
            {
                mappedOccurrences.Add(binding.SymbolOccurrenceId); mappedNative.Add(candidate.Key);
                var component = components[candidate.Occurrence.ComponentId];
                void Compare(string field, string? model, string? native)
                {
                    if (model != native) differences.Add(new(binding.SymbolOccurrenceId, field, model, native));
                }
                Compare("reference", component.Reference, target.Symbol.ReferenceField?.Text?.Text_);
                Compare("value", definitions[component.DefinitionId].Value, target.Symbol.ValueField?.Text?.Text_);
                Compare("unit", candidate.Occurrence.Unit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    target.Symbol.Unit?.Unit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        foreach (Guid id in occurrences.Keys)
            if (!mappedOccurrences.Contains(id)) Issue("unmapped_model_symbol", id);
        foreach (var (key, symbol) in nativeSymbols)
            if (!mappedNative.Contains(key)) Issue("unmapped_native_symbol", path: symbol.Path, native: symbol.Id);
        return new(issues.OrderBy(i => i.Code, StringComparer.Ordinal).ThenBy(i => i.ModelId)
                .ThenBy(i => i.NativePath, StringComparer.Ordinal).ThenBy(i => i.NativeObjectId).ToArray(),
            differences.OrderBy(d => d.SymbolOccurrenceId).ThenBy(d => d.Field, StringComparer.Ordinal).ToArray(), topology.CoverageGaps);
    }

    internal static string PathKey(IReadOnlyList<Guid> path) => string.Join('/', path.Select(id => id.ToString("D")));
    private static string ObjectKey(string path, Guid id) => path + "#" + id.ToString("D");
}
