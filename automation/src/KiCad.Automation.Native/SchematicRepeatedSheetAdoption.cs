using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal sealed record RepeatedSheetAdoption(SchematicDesign Design, IReadOnlyList<Guid> Components,
    IReadOnlyList<Guid> Occurrences, IReadOnlyList<string> NativeKeys,
    IReadOnlyList<SchematicSheetComponentResolutionRequest> Requests);

/// <summary>Reuse physical component definitions when KiCad inserts another instance
/// of a known sheet. Native UUIDs and existing bindings establish ownership, never names.</summary>
internal static class SchematicRepeatedSheetAdoption
{
    internal static RepeatedSheetAdoption Project(SchematicDesign existing, SchematicDesign expanded,
        SchematicHierarchyData observed, IReadOnlySet<Guid> addedSheets, CancellationToken token,
        IReadOnlyList<SchematicSheetComponentReference>? answers = null)
    {
        var circuit = existing.Engineering.Circuit;
        var sheetInstances = circuit.SheetInstances.ToDictionary(s => s.Id);
        var components = circuit.Components.ToDictionary(c => c.Id);
        var occurrences = circuit.Symbols.ToDictionary(s => s.Id);
        var paths = existing.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var screens = observed.Instances.ToDictionary(SchematicNativeSheetChanges.Key, StringComparer.Ordinal);
        var definitions = circuit.Sheets.ToDictionary(s => s.Id);
        var physical = existing.SymbolBindings.Select(b =>
        {
            var occurrence = occurrences[b.SymbolOccurrenceId]; var component = components[occurrence.ComponentId];
            string screen = screens[paths[occurrence.EffectiveSheetInstanceId(component)]].Metadata.ScreenId.Value;
            return (Key: screen + "#" + b.NativeObjectId.ToString("D"), Definition: component.DefinitionId,
                OwnerDefinition: sheetInstances[component.SheetInstanceId].DefinitionId);
        }).GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var newComponents = new List<ComponentInstance>();
        var newOccurrences = new List<SymbolOccurrence>();
        var newBindings = new List<SchematicSymbolBinding>();
        var nativeKeys = new List<string>();
        var requests = new List<SchematicSheetComponentResolutionRequest>();
        var supplied = (answers ?? []).ToDictionary(a => (a.SheetInstanceId, a.ComponentDefinitionId));
        foreach (var sheet in expanded.Engineering.Circuit.SheetInstances.Where(s => addedSheets.Contains(s.Id)))
        {
            token.ThrowIfCancellationRequested();
            if (!definitions.TryGetValue(sheet.DefinitionId, out var definition)) continue;
            var path = expanded.SheetBindings.Single(b => b.SheetInstanceId == sheet.Id).NativePath;
            string pathKey = SchematicDesignBindings.PathKey(path);
            var screen = screens[pathKey];
            var mapped = new List<(SchematicSymbolInstance Symbol, Guid Definition)>();
            foreach (var packed in screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)))
            {
                var symbol = packed.Unpack<SchematicSymbolInstance>();
                if (!physical.TryGetValue(screen.Metadata.ScreenId.Value + "#" + symbol.Id.Value, out var owners)) continue;
                var local = owners.Where(o => o.OwnerDefinition == sheet.DefinitionId).Select(o => o.Definition).Distinct().ToArray();
                if (local.Length > 1)
                    throw Error("The existing repeated instances bind one physical symbol to different component definitions; resolve that ownership first.");
                if (local.Length == 1) mapped.Add((symbol, local[0]));
                // Units owned on another sheet continue through explicit unit-owner
                // resolution in the ordinary addition projection.
            }
            foreach (var component in definition.Components)
            {
                Guid owner = SchematicNativeAdditionProjection.AdoptedIdentity("sheet-component", circuit.Id, path, component.Id);
                var units = mapped.Where(m => m.Definition == component.Id).Select(m => m.Symbol).ToArray();
                if (units.Length == 0)
                {
                    if (supplied.Remove((sheet.Id, component.Id), out var answer))
                        newComponents.Add(new(owner, component.Id, sheet.Id, answer.Reference));
                    else requests.Add(new(SchematicRepeatedSheetChoices.ReferenceRequired, sheet.Id, component.Id, owner,
                        path, component.PartId, "This component has no drawing on its owning sheet. Give the reference for its new instance; then resolve any units drawn on other sheets."));
                    continue;
                }
                var references = units.Select(u => u.ReferenceField?.Text?.Text_).Distinct(StringComparer.Ordinal).ToArray();
                if (references.Length != 1 || string.IsNullOrWhiteSpace(references[0]))
                    throw Error("Units of one component in the new sheet instance have different native references; resolve them before adoption.");
                newComponents.Add(new(owner, component.Id, sheet.Id, references[0]!));
                foreach (var symbol in units)
                {
                    Guid native = Guid.Parse(symbol.Id.Value);
                    Guid occurrence = SchematicNativeAdditionProjection.AdoptedIdentity("occurrence", circuit.Id, path, native);
                    newOccurrences.Add(new(occurrence, owner, symbol.Unit.Unit, SchematicModelProjection.Placement(symbol)));
                    newBindings.Add(new(occurrence, native)); nativeKeys.Add(pathKey + "#" + native.ToString("D"));
                }
            }
        }
        if (supplied.Count != 0)
            throw new AutomationException(SchematicRepeatedSheetChoices.Invalid,
                "A supplied reference does not name an undrawn component of a newly inserted repeated sheet.");
        var assignedReferences = expanded.Engineering.Circuit.Components.Concat(newComponents).Select(c => c.Reference)
            .Where(reference => !reference.EndsWith("?", StringComparison.Ordinal)).ToArray();
        if (assignedReferences.Distinct(StringComparer.Ordinal).Count() != assignedReferences.Length)
            throw new AutomationException("native_addition_conflict", "Assigned component references must be unique, including a partial repeated-sheet answer.");
        return new(expanded with { Engineering = expanded.Engineering with { Circuit = expanded.Engineering.Circuit with
        { Components = [.. expanded.Engineering.Circuit.Components, .. newComponents], Symbols = [.. expanded.Engineering.Circuit.Symbols, .. newOccurrences] } },
            SymbolBindings = [.. expanded.SymbolBindings, .. newBindings] }, newComponents.Select(c => c.Id).ToArray(),
            newOccurrences.Select(s => s.Id).ToArray(), nativeKeys, requests);
    }

    private static AutomationException Error(string message) => new("native_repeated_sheet_ownership_unresolved", message);
}
