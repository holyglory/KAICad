using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record SchematicProjectionConflict(Guid ModelId, string Field, string Reason,
    JsonElement Baseline, JsonElement Desired, JsonElement Native);
public sealed record SchematicModelProjectionResult(EngineeringDesign? Candidate,
    IReadOnlyList<SchematicProjectionConflict> Conflicts, IReadOnlyList<SchematicBindingIssue> BindingIssues,
    bool UnprojectedSnapshotChanges, IReadOnlyList<HierarchyCoverageGap> CoverageGaps,
    string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>Three-way reverse projection of reference, value, unit and symbol placement.
/// Pure preparation: does not save, apply native operations, compare connectivity or advance a
/// synchronized checkpoint. All unprojected snapshot differences remain explicit, including
/// wiring/pin changes and other persisted native fields. Coverage and live revision admission
/// are independent prerequisites even when a candidate has no property conflicts.</summary>
public static class SchematicModelProjection
{
    public static SchematicModelProjectionResult Reconcile(SchematicDesign baseline, EngineeringDesign desired,
        SchematicHierarchyData observed, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var beforeReport = SchematicDesignBindings.Inspect(baseline, libraries, cancellationToken);
        var afterReport = SchematicDesignBindings.Inspect(baseline with { Engineering = desired, Schematic = observed }, libraries, cancellationToken);
        var issues = beforeReport.Issues.Concat(afterReport.Issues).Distinct().ToArray();
        var gaps = beforeReport.CoverageGaps.Concat(afterReport.CoverageGaps).Distinct().ToArray();
        if (issues.Length > 0) return new(null, [], issues, true, gaps, "unresolved_design_bindings");
        if (beforeReport.Differences.Count > 0)
            return new(null, [], [], true, gaps, "unaligned_projection_baseline",
                "The baseline reference/value/unit projection must agree before reconciling later edits.");
        if (!SharedPlacementAgrees(baseline.Schematic) || !SharedPlacementAgrees(observed))
            return new(null, [], [], true, gaps, "inconsistent_shared_native_placement",
                "Repeated instances of a shared native object disagree about its placement.");

        var before = NativeSymbols(baseline, baseline.Schematic);
        var after = NativeSymbols(baseline, observed);
        var source = baseline.Engineering.Circuit;
        // This projection handles properties, not rebinding or structural edits. Preserve such
        // input for the hierarchy/electrical reconciler instead of indexing stale owners.
        if (!source.Components.Select(c => (c.Id, c.DefinitionId, c.SheetInstanceId)).ToHashSet().SetEquals(
                desired.Circuit.Components.Select(c => (c.Id, c.DefinitionId, c.SheetInstanceId)))
            || !source.SheetInstances.ToHashSet().SetEquals(desired.Circuit.SheetInstances)
            || !source.Sheets.SelectMany(s => s.Components.Select(c => (Sheet: s.Id, c.Id, c.PartId))).ToHashSet().SetEquals(
                desired.Circuit.Sheets.SelectMany(s => s.Components.Select(c => (Sheet: s.Id, c.Id, c.PartId))))
            || !source.Symbols.Select(s => (s.Id, s.ComponentId)).ToHashSet().SetEquals(
                desired.Circuit.Symbols.Select(s => (s.Id, s.ComponentId))))
            return new(null, [], [], true, gaps, "model_topology_changed", "Reconcile changed model ownership before projecting native properties.");
        var conflicts = new List<SchematicProjectionConflict>();
        var components = desired.Circuit.Components.ToDictionary(c => c.Id);
        var definitions = desired.Circuit.Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);
        var symbols = desired.Circuit.Symbols.ToDictionary(s => s.Id);
        var oldComponents = source.Components.ToDictionary(c => c.Id);
        bool remainder = HasUnprojectedChanges(baseline.Schematic, observed);

        T Merge<T>(Guid owner, string field, T originalModel, T wanted, T originalNative, T currentNative,
            Func<T, T, bool>? equivalent = null)
        {
            equivalent ??= EqualityComparer<T>.Default.Equals;
            if (equivalent(originalNative, currentNative)) return wanted;
            if (equivalent(wanted, originalModel) || equivalent(wanted, currentNative))
                return currentNative;
            conflicts.Add(new(owner, field, "competing_edit", JsonSerializer.SerializeToElement(originalModel),
                JsonSerializer.SerializeToElement(wanted), JsonSerializer.SerializeToElement(currentNative)));
            return wanted;
        }

        string? Shared(Guid owner, string field, string original, string wanted,
            IEnumerable<SymbolOccurrence> occurrences, Func<SchematicSymbolInstance, string?> read)
        {
            var ids = occurrences.Select(s => s.Id).ToArray();
            if (ids.Length == 0) return wanted;
            var previous = ids.Select(id => read(before[id])).Distinct(StringComparer.Ordinal).ToArray();
            var current = ids.Select(id => read(after[id])).Distinct(StringComparer.Ordinal).ToArray();
            if (previous.Length != 1 || current.Length != 1 || previous[0] is null || current[0] is null)
            {
                conflicts.Add(new(owner, field, "inconsistent_native_owners", JsonSerializer.SerializeToElement(previous),
                    JsonSerializer.SerializeToElement(wanted), JsonSerializer.SerializeToElement(current)));
                return wanted;
            }
            return Merge(owner, field, original, wanted, previous[0], current[0]);
        }

        foreach (var component in source.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = components[component.Id];
            string? reference = Shared(component.Id, "reference", component.Reference, wanted.Reference,
                source.Symbols.Where(s => s.ComponentId == component.Id), s => s.ReferenceField?.Text?.Text_);
            components[component.Id] = wanted with { Reference = reference! };
        }
        foreach (var definition in source.Sheets.SelectMany(s => s.Components))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = definitions[definition.Id];
            string? value = Shared(definition.Id, "value", definition.Value, wanted.Value,
                source.Symbols.Where(s => oldComponents[s.ComponentId].DefinitionId == definition.Id), s => s.ValueField?.Text?.Text_);
            definitions[definition.Id] = wanted with { Value = value! };
        }
        foreach (var occurrence in source.Symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = symbols[occurrence.Id]; var oldNative = before[occurrence.Id]; var newNative = after[occurrence.Id];
            if (newNative.Unit is null)
            {
                conflicts.Add(new(occurrence.Id, "unit", "missing_native_unit", JsonSerializer.SerializeToElement(occurrence.Unit),
                    JsonSerializer.SerializeToElement(wanted.Unit), JsonSerializer.SerializeToElement<object?>(null)));
                continue;
            }
            int unit = Merge(occurrence.Id, "unit", occurrence.Unit, wanted.Unit, oldNative.Unit.Unit, newNative.Unit.Unit);
            var placement = wanted.Placement;
            // Only project a real native presentation change. In particular, an unchanged
            // coordinate-free model must not acquire coordinates during a no-op synchronization.
            if (!Equals(oldNative.Position, newNative.Position) || !Equals(oldNative.Transform, newNative.Transform)
                || oldNative.Locked != newNative.Locked)
            {
                try
                {
                    var previous = Placement(oldNative); var current = Placement(newNative);
                    if (occurrence.Placement is not null && !SchematicOrientation.Equivalent(occurrence.Placement, previous))
                        conflicts.Add(new(occurrence.Id, "placement", "unaligned_projection_baseline",
                            JsonSerializer.SerializeToElement(occurrence.Placement), JsonSerializer.SerializeToElement(wanted.Placement),
                            JsonSerializer.SerializeToElement(new { previous, current })));
                    else placement = Merge(occurrence.Id, "placement", occurrence.Placement, wanted.Placement, previous, current,
                        (a, b) => SchematicOrientation.Equivalent(a, b));
                }
                catch (AutomationException error)
                {
                    conflicts.Add(new(occurrence.Id, "placement", error.Code, JsonSerializer.SerializeToElement(occurrence.Placement),
                        JsonSerializer.SerializeToElement(wanted.Placement), JsonSerializer.SerializeToElement(error.Message)));
                }
            }
            symbols[occurrence.Id] = wanted with { Unit = unit, Placement = placement };
        }
        if (conflicts.Count > 0) return new(null, conflicts, [], remainder, gaps);
        var candidate = desired with
        {
            Circuit = desired.Circuit with
            {
                Components = desired.Circuit.Components.Select(c => components[c.Id]).ToArray(),
                Sheets = desired.Circuit.Sheets.Select(s => s with { Components = s.Components.Select(c => definitions[c.Id]).ToArray() }).ToArray(),
                Symbols = desired.Circuit.Symbols.Select(s => symbols[s.Id]).ToArray()
            }
        };
        try { candidate.Validate(libraries); }
        catch (AutomationException error) { return new(null, [], [], remainder, gaps, error.Code, error.Message); }
        return new(candidate, [], [], remainder, gaps);
    }

    internal static Dictionary<Guid, SchematicSymbolInstance> NativeSymbols(SchematicDesign design, SchematicHierarchyData snapshot)
    {
        var sheets = design.SheetBindings.ToDictionary(s => s.SheetInstanceId, s => SchematicDesignBindings.PathKey(s.NativePath));
        var components = design.Engineering.Circuit.Components.ToDictionary(c => c.Id);
        var occurrences = design.Engineering.Circuit.Symbols.ToDictionary(s => s.Id);
        var native = snapshot.Instances.SelectMany(screen => screen.Items.Where(p => p.Is(SchematicSymbolInstance.Descriptor))
            .Select(p => (Path: string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(id => id.Value)), Symbol: p.Unpack<SchematicSymbolInstance>())))
            .ToDictionary(p => p.Path + "#" + p.Symbol.Id.Value, p => p.Symbol, StringComparer.Ordinal);
        return design.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => native[
            sheets[components[occurrences[b.SymbolOccurrenceId].ComponentId].SheetInstanceId] + "#" + b.NativeObjectId.ToString("D")]);
    }

    internal static SymbolPlacement Placement(SchematicSymbolInstance symbol)
    {
        if (symbol.Position is null || symbol.Transform is null || (int)symbol.Transform.Orientation is < 1 or > 4
            || (int)symbol.Locked is < 1 or > 2)
            throw new AutomationException("unsupported_native_placement", "Native position, orientation and lock state must be explicit.");
        var result = new SymbolPlacement(Coordinates.NanometersToMillimeters(symbol.Position.XNm),
            Coordinates.NanometersToMillimeters(symbol.Position.YNm), ((int)symbol.Transform.Orientation - 1) * 90,
            symbol.Transform.MirrorX, symbol.Transform.MirrorY, (int)symbol.Locked == 2);
        result.Validate();
        return result;
    }

    private static bool SharedPlacementAgrees(SchematicHierarchyData snapshot)
    {
        var owners = new Dictionary<string, SchematicSymbolInstance>(StringComparer.Ordinal);
        foreach (var screen in snapshot.Instances)
        foreach (var packed in screen.Items.Where(p => p.Is(SchematicSymbolInstance.Descriptor)))
        {
            var symbol = packed.Unpack<SchematicSymbolInstance>();
            string key = screen.Metadata.ScreenId.Value + "#" + symbol.Id.Value;
            if (owners.TryGetValue(key, out var previous)
                && (!Equals(previous.Position, symbol.Position) || !Equals(previous.Transform, symbol.Transform) || previous.Locked != symbol.Locked))
                return false;
            owners.TryAdd(key, symbol);
        }
        return true;
    }

    private static bool HasUnprojectedChanges(SchematicHierarchyData baseline, SchematicHierarchyData observed)
    {
        SchematicHierarchyData Strip(SchematicHierarchyData input)
        {
            var copy = input.Clone();
            foreach (var screen in copy.Instances)
            for (int i = 0; i < screen.Items.Count; i++)
            {
                if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
                var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
                if (symbol.ReferenceField?.Text is { } reference) reference.Text_ = "";
                if (symbol.ValueField?.Text is { } value) value.Text_ = "";
                symbol.Unit = null; symbol.Position = null; symbol.Transform = null; symbol.Locked = 0;
                screen.Items[i] = Any.Pack(symbol);
            }
            return copy;
        }
        return !Strip(baseline).Equals(Strip(observed));
    }
}
