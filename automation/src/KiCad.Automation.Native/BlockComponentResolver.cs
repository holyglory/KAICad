using System.Collections.Immutable;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public enum ComponentRealizationStatus { MissingDesign, UnavailableDesign, CircuitChanged, MissingComponent, ComponentResolved }
public sealed record ComponentSymbolLocation(Guid SymbolOccurrenceId, int Unit, Guid SheetInstanceId,
    ImmutableArray<Guid> NativeSheetPath, Guid NativeObjectId);
public sealed record ComponentRealizationInspection(ComponentRealization Target, ComponentRealizationStatus Status,
    ComponentInstance? Component, ComponentDefinition? Definition, PartDefinition? Part,
    ImmutableArray<ComponentSymbolLocation> NativeLocations, SchematicBindingReport? NativeBindings);

/// <summary>Resolve saved identities only. A component may be resolved while its native
/// bindings are not; neither case proves electrical correctness or a live editor revision.</summary>
public static class BlockComponentResolver
{
    public static ImmutableArray<ComponentRealizationInspection> Inspect(BlockComponentBindings bindings,
        HardwareRepository repository, IReadOnlyDictionary<Guid, SchematicDesign> designs,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken token = default)
    {
        bindings.Validate(); repository.Validate();
        var declared = repository.Designs.Select(d => d.Id).ToHashSet();
        if (designs.Keys.Any(id => !declared.Contains(id)))
            throw new AutomationException("undeclared_realization_design", "Only inspect designs declared by this repository manifest.");
        var reports = new Dictionary<Guid, SchematicBindingReport>();
        var result = ImmutableArray.CreateBuilder<ComponentRealizationInspection>();
        foreach (var target in bindings.Targets)
        {
            token.ThrowIfCancellationRequested();
            if (!declared.Contains(target.DesignId)) { Missing(ComponentRealizationStatus.MissingDesign); continue; }
            if (!designs.TryGetValue(target.DesignId, out var design)) { Missing(ComponentRealizationStatus.UnavailableDesign); continue; }
            var circuit = design.Engineering.Circuit;
            if (circuit.Id != target.CircuitId) { Missing(ComponentRealizationStatus.CircuitChanged); continue; }
            if (!reports.TryGetValue(target.DesignId, out var report))
                reports.Add(target.DesignId, report = SchematicDesignBindings.Inspect(design, libraries, token));
            var component = circuit.Components.SingleOrDefault(c => c.Id == target.ComponentId);
            if (component is null) { Missing(ComponentRealizationStatus.MissingComponent); continue; }
            var definition = circuit.Sheets.SelectMany(s => s.Components).Single(c => c.Id == component.DefinitionId);
            var part = circuit.Parts.Single(p => p.Id == definition.PartId);
            var locations = ImmutableArray.CreateBuilder<ComponentSymbolLocation>();
            // Do not offer a native navigation target from a design with unresolved
            // hierarchy/identity mappings. Return its complete diagnostics instead.
            // Property drift and coverage gaps remain separately visible in the report.
            if (report.IdentitiesResolved)
                foreach (var occurrence in circuit.Symbols.Where(s => s.ComponentId == component.Id))
                {
                    Guid sheet = occurrence.EffectiveSheetInstanceId(component);
                    locations.Add(new(occurrence.Id, occurrence.Unit, sheet,
                        [.. design.SheetBindings.Single(s => s.SheetInstanceId == sheet).NativePath],
                        design.SymbolBindings.Single(s => s.SymbolOccurrenceId == occurrence.Id).NativeObjectId));
                }
            result.Add(new(target, ComponentRealizationStatus.ComponentResolved, component, definition, part, locations.ToImmutable(), report));
            void Missing(ComponentRealizationStatus status) => result.Add(new(target, status, null, null, null, [], null));
        }
        return result.ToImmutable();
    }
}
