using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal sealed record SchematicNativeRemovalResult(SchematicDesign? BindingCandidate,
    IReadOnlyList<Guid> RemovedOccurrences, IReadOnlyList<ComponentReferenceChange> ComponentChanges,
    IReadOnlyList<NetIdentityChange> RetiredNets, IReadOnlyList<SchematicBindingIssue> Issues,
    IReadOnlyList<HierarchyCoverageGap> CoverageGaps, string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>Project exact native symbol deletions onto engineering ownership.
/// The result resolves bindings only: its properties and provisional net groups
/// still require three-way reconciliation against the original checkpoints.</summary>
internal static class SchematicNativeRemovalProjection
{
    internal static SchematicNativeRemovalResult Project(SchematicDesign baseline, SchematicHierarchyData observed,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var gaps = new List<HierarchyCoverageGap>();
        SchematicNativeRemovalResult Failure(string code, string? message = null,
            IReadOnlyList<SchematicBindingIssue>? issues = null) => new(null, [], [], [], issues ?? [],
                gaps.Distinct().ToArray(), code, message);
        try
        {
            var original = SchematicDesignBindings.Inspect(baseline, libraries, token);
            gaps.AddRange(original.CoverageGaps);
            if (!original.IdentitiesResolved) return Failure("unresolved_design_bindings", issues: original.Issues);
            var topology = SchematicHierarchyTopology.Inspect(observed, token);
            gaps.AddRange(topology.CoverageGaps);
            if (!topology.IsValid) return Failure("invalid_native_hierarchy", "Resolve the reported native hierarchy before projecting deletions.");
            var screens = observed.Instances.ToDictionary(s => Path(s.Metadata.Document.SheetPath.Path.Select(p => p.Value)));
            var beforeScreens = baseline.Schematic.Instances.ToDictionary(s => Path(s.Metadata.Document.SheetPath.Path.Select(p => p.Value)));
            if (!screens.Keys.ToHashSet().SetEquals(beforeScreens.Keys)
                || screens.Any(pair => pair.Value.Metadata.ScreenId.Value != beforeScreens[pair.Key].Metadata.ScreenId.Value))
                return Failure("sheet_ownership_changed", "Sheet insertion, removal or reparenting requires explicit ownership reconciliation.");

            var nativeIds = screens.ToDictionary(pair => pair.Key, pair => pair.Value.Items
                .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>().Id.Value)
                .ToHashSet(StringComparer.Ordinal));
            var circuit = baseline.Engineering.Circuit;
            var components = circuit.Components.ToDictionary(c => c.Id);
            var paths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
            var bindings = baseline.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId);
            var removed = circuit.Symbols.Where(s => !nativeIds[paths[s.EffectiveSheetInstanceId(components[s.ComponentId])]]
                .Contains(bindings[s.Id].NativeObjectId.ToString("D"))).Select(s => s.Id).ToHashSet();
            // Restrict only a private comparison copy. The real baseline and
            // electrical checkpoints remain untouched and must be verified later.
            var survivingNative = baseline.Schematic.Clone();
            foreach (var screen in survivingNative.Instances)
            {
                token.ThrowIfCancellationRequested();
                var live = nativeIds[Path(screen.Metadata.Document.SheetPath.Path.Select(p => p.Value))];
                for (int i = screen.Items.Count - 1; i >= 0; --i)
                    if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor)
                        && !live.Contains(screen.Items[i].Unpack<SchematicSymbolInstance>().Id.Value)) screen.Items.RemoveAt(i);
            }
            if (SchematicNetReconciliation.NativeOwners(survivingNative) != SchematicNetReconciliation.NativeOwners(observed))
                return Failure("electrical_ownership_changed", "New symbols, unit changes or changed library pin identities require explicit ownership reconciliation.");

            var remainingSymbols = circuit.Symbols.Where(s => !removed.Contains(s.Id)).ToArray();
            var hadDrawing = circuit.Symbols.Select(s => s.ComponentId).ToHashSet();
            var hasDrawing = remainingSymbols.Select(s => s.ComponentId).ToHashSet();
            var retired = circuit.Components.Where(c => hadDrawing.Contains(c.Id) && !hasDrawing.Contains(c.Id))
                .Select(c => c.Id).ToHashSet();
            var remainingComponents = circuit.Components.Where(c => !retired.Contains(c.Id)).ToArray();
            var retiredDefinitions = circuit.Components.Where(c => retired.Contains(c.Id)).Select(c => c.DefinitionId)
                .Except(remainingComponents.Select(c => c.DefinitionId)).ToHashSet();
            var nets = circuit.Nets.Select(n => (Original: n, Pins: n.Pins.Where(p => !retired.Contains(p.ComponentId)).ToArray()))
                .Where(n => n.Pins.Length > 0 || n.Original.Pins.Count == 0)
                .Select(n => n.Original with { Pins = n.Pins }).ToArray();
            var next = circuit with { Symbols = remainingSymbols, Components = remainingComponents, Nets = nets,
                Sheets = circuit.Sheets.Select(s => s with { Components = s.Components.Where(c => !retiredDefinitions.Contains(c.Id)).ToArray() }).ToArray() };
            try { next.Validate(); }
            catch (AutomationException error)
            {
                return Failure("component_ownership_resolution_required", error.Message);
            }
            var changes = retired.Order().Select(id => new ComponentReferenceChange(new(id), ComponentReferenceChangeKind.Removed,
                "The last native symbol occurrence of this component was removed.", [])).ToArray();
            var liveNets = nets.Select(n => n.Id).ToHashSet();
            var netChanges = circuit.Nets.Where(n => !liveNets.Contains(n.Id)).Select(n => new NetIdentityChange(n.Id,
                NetBindingChangeKind.Removed, "Every component pin realizing this net was removed.", [])).ToArray();
            var engineering = ComponentReferenceRetention.Retain(baseline.Engineering, next, changes, libraries, netChanges);
            var candidate = baseline with { Engineering = engineering, Schematic = observed.Clone(),
                SymbolBindings = baseline.SymbolBindings.Where(b => !removed.Contains(b.SymbolOccurrenceId)).ToArray() };
            var report = SchematicDesignBindings.Inspect(candidate, libraries, token);
            gaps.AddRange(report.CoverageGaps);
            if (!report.IdentitiesResolved) return Failure("unresolved_design_bindings", issues: report.Issues);
            return new(candidate, removed.Order().ToArray(), changes, netChanges, [], gaps.Distinct().ToArray());
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
    }

    private static string Path(IEnumerable<string> path) => string.Join('/', path);
}
