using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>Admits a complete new-parent scaffold and existing-child move as one transaction.</summary>
internal static class SchematicXmlNewParentMoves
{
    internal static SchematicSynchronizationPlan? Prepare(DesignRecoveryState state, SchematicDesign desired,
        SchematicHierarchyMergeResult hierarchy, CancellationToken token)
    {
        var before = state.Baseline.Engineering.Circuit.SheetInstances.ToDictionary(s => s.Id);
        var added = desired.Engineering.Circuit.SheetInstances.Where(s => !before.ContainsKey(s.Id)).Select(s => s.Id).ToHashSet();
        if (added.Count == 0 || !desired.Engineering.Circuit.SheetInstances.Any(s => before.TryGetValue(s.Id, out var old) && old.ParentId != s.ParentId))
            return null;
        SchematicSynchronizationPlan Failure(string code, string message) =>
            new(null, null, [], hierarchy, null, null, [], [], null, [], true, code, message);
        try
        {
            bool nativeUnchanged;
            try { nativeUnchanged = SchematicRebuild.NativeUnchanged(state, token); }
            catch (AutomationException) { nativeUnchanged = false; }
            if (!nativeUnchanged)
                return Failure("ownership_change_with_xml_edits", "Synchronize the native changes before creating a parent and moving a sheet from XML.");
            var requested = SchematicDesignBindings.Inspect(desired, state.KnowledgeLibraries, token);
            if (!requested.IdentitiesResolved || requested.Differences.Count != 0)
                return Failure("xml_new_parent_bindings_required", "A combined parent creation and move needs a complete native scaffold and exact model bindings.");
            desired = NativeRepresentation(desired, added);
            var originalCircuit = state.Baseline.Engineering.Circuit;
            var originalOwners = originalCircuit.Components.Select(c => c.Id).ToHashSet();
            var newOwners = desired.Engineering.Circuit.Components.Where(c => !originalOwners.Contains(c.Id)).ToArray();
            var topologyDesired = desired;
            HashSet<Guid> addedOccurrences = [];
            if (newOwners.Length != 0)
            {
                var newOwnerIds = newOwners.Select(c => c.Id).ToHashSet();
                var originalOccurrences = originalCircuit.Symbols.Select(s => s.Id).ToHashSet();
                var originalDefinitions = originalCircuit.Sheets.SelectMany(s => s.Components).Select(c => c.Id).ToHashSet();
                var originalParts = originalCircuit.Parts.Select(p => p.Id).ToHashSet();
                var originalNets = originalCircuit.Nets.Select(n => n.Id).ToHashSet();
                var newOccurrences = desired.Engineering.Circuit.Symbols.Where(s => !originalOccurrences.Contains(s.Id)).ToArray();
                if (newOwners.Any(c => !added.Contains(c.SheetInstanceId))
                    || newOccurrences.Any(s => !newOwnerIds.Contains(s.ComponentId)
                        || !added.Contains(s.EffectiveSheetInstanceId(newOwners.Single(c => c.Id == s.ComponentId))))
                    || newOwners.Any(c => originalDefinitions.Contains(c.DefinitionId)))
                    return Failure("xml_new_parent_component_conflict", "New parent components need their own definitions and units on the new sheets; preserve existing component ownership and connections.");
                foreach (var owner in newOwners)
                {
                    var definition = desired.Engineering.Circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == owner.DefinitionId);
                    var part = desired.Engineering.Circuit.Parts.Single(p => p.Id == definition.PartId);
                    if (!newOccurrences.Where(s => s.ComponentId == owner.Id).Select(s => s.Unit).ToHashSet()
                        .SetEquals(Enumerable.Range(1, part.Units)))
                        return Failure("component_units_incomplete", "Provide every unit of a new parent component before creating it.");
                }
                addedOccurrences = newOccurrences.Select(s => s.Id).ToHashSet();
                var desiredComponents = desired.Engineering.Circuit.Components.ToDictionary(c => c.Id);
                var desiredOccurrences = newOccurrences.ToDictionary(s => s.Id);
                var desiredPaths = desired.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
                var removedNative = desired.SymbolBindings.Where(b => addedOccurrences.Contains(b.SymbolOccurrenceId))
                    .Select(b => desiredPaths[desiredOccurrences[b.SymbolOccurrenceId].EffectiveSheetInstanceId(
                        desiredComponents[desiredOccurrences[b.SymbolOccurrenceId].ComponentId])] + "#" + b.NativeObjectId.ToString("D")).ToHashSet(StringComparer.Ordinal);
                var drawing = desired.Schematic.Clone();
                foreach (var screen in drawing.Instances)
                {
                    for (int i = screen.Items.Count - 1; i >= 0; --i)
                        if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor)
                            && removedNative.Contains(SchematicNativeSheetChanges.Key(screen) + "#"
                                + screen.Items[i].Unpack<SchematicSymbolInstance>().Id.Value)) screen.Items.RemoveAt(i);
                    // Validate the topology with its original wiring. The final
                    // native batch and electrical comparison still validate every
                    // requested connection before publication.
                    var binding = desired.SheetBindings.Single(b => SchematicDesignBindings.PathKey(b.NativePath)
                        == SchematicNativeSheetChanges.Key(screen));
                    var oldBinding = state.Baseline.SheetBindings.SingleOrDefault(b => b.SheetInstanceId == binding.SheetInstanceId);
                    if (oldBinding is not null)
                    {
                        var original = state.Observed.Instances.Single(s => SchematicNativeSheetChanges.Key(s)
                            == SchematicDesignBindings.PathKey(oldBinding.NativePath));
                        for (int i = screen.Items.Count - 1; i >= 0; --i)
                            if (ConnectionItem(screen.Items[i])) screen.Items.RemoveAt(i);
                        screen.Items.Add(original.Items.Where(ConnectionItem).Select(i => i.Clone()));
                    }
                }
                var stripped = desired.Engineering.Circuit with
                {
                    Parts = desired.Engineering.Circuit.Parts.Where(p => originalParts.Contains(p.Id)).ToArray(),
                    Sheets = desired.Engineering.Circuit.Sheets.Select(s => s with
                        { Components = s.Components.Where(c => originalDefinitions.Contains(c.Id)).ToArray() }).ToArray(),
                    Components = desired.Engineering.Circuit.Components.Where(c => originalOwners.Contains(c.Id)).ToArray(),
                    Symbols = desired.Engineering.Circuit.Symbols.Where(s => originalOccurrences.Contains(s.Id)).ToArray(),
                    Nets = desired.Engineering.Circuit.Nets.Where(n => originalNets.Contains(n.Id))
                        .Select(n => n with { Pins = n.Pins.Where(p => originalOwners.Contains(p.ComponentId)).ToArray() }).ToArray()
                };
                topologyDesired = desired with { Schematic = drawing,
                    Engineering = state.Baseline.Engineering with { Circuit = stripped },
                    PartSymbols = desired.PartSymbols?.Where(p => originalParts.Contains(p.PartId)).ToArray(),
                    SymbolBindings = desired.SymbolBindings.Where(b => !addedOccurrences.Contains(b.SymbolOccurrenceId)).ToArray() };
            }
            var changes = SchematicNativeSheetChanges.Compare(state.Observed, topologyDesired.Schematic);
            if (changes.ErrorCode is not null) return Failure(changes.ErrorCode, changes.ErrorMessage!);
            var prepared = SchematicInsertedParents.Prepare(state.Baseline with { Schematic = state.Observed.Clone() },
                topologyDesired.Schematic, changes, token, topologyDesired);
            if (!prepared.Instances.ToHashSet().SetEquals(added))
                return Failure("xml_new_parent_bindings_required", "Declare unique new parent containers for the moved sheets and keep every existing sheet identity.");
            var result = SchematicXmlSheetChanges.Prepare(state, topologyDesired, hierarchy, token, prepared.Baseline);
            if (result is null) return Failure("xml_sheet_nothing_to_apply", "The new parent scaffold does not contain an existing sheet move.");
            if (newOwners.Length != 0 && result.CanPrepare && result.Candidate is { } shell)
            {
                // Only validate explicit declarations here. Neither the private
                // shell nor supplied XML becomes a native/electrical checkpoint.
                var declarations = desired with { Engineering = desired.Engineering with
                    { Circuit = desired.Engineering.Circuit with { Nets = shell.Engineering.Circuit.Nets } } };
                var validation = SchematicNativeAdditionProjection.Project(state with
                    { Baseline = shell, Observed = desired.Schematic, RepeatedSheetResolution = null }, [], declarations, null, token);
                if (validation.Adoption is not { } owners || !owners.AddedOccurrences.ToHashSet().SetEquals(addedOccurrences))
                    return Failure(validation.ErrorCode ?? "xml_new_parent_component_conflict", validation.ErrorMessage ?? "The new parent declarations do not match its exact native symbols.");
                var deltaTarget = desired.Schematic.Clone();
                var existingScreens = state.Observed.Instances.Select(s => s.Metadata.ScreenId.Value).ToHashSet(StringComparer.Ordinal);
                foreach (var screen in deltaTarget.Instances.Where(s => !existingScreens.Contains(s.Metadata.ScreenId.Value)))
                    screen.Metadata.UnrepresentedState.Clear(); // Already validated against root coverage by the topology plan.
                var operations = SchematicHierarchyDelta.Plan(state.Observed, deltaTarget, token);
                result = result with { Candidate = desired, CandidateXml = SchematicDesignXml.Write(desired, state.KnowledgeLibraries),
                    NativeOperations = operations, Electrical = result.Electrical! with { Candidate = desired.Engineering,
                        AddedComponents = newOwners.Select(c => c.Id).ToArray(), AddedSymbolOccurrences = addedOccurrences.Order().ToArray() } };
            }
            return result.Electrical is null ? result : result with
                { Electrical = result.Electrical with { AddedSheetInstances = prepared.Instances } };
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
    }

    private static bool ConnectionItem(Any item) => item.Is(GlobalLabel.Descriptor)
        || item.Is(LocalLabel.Descriptor) || item.Is(HierarchicalLabel.Descriptor)
        || item.Is(Junction.Descriptor) || item.Is(NoConnectMarker.Descriptor)
        || item.Is(SchematicLine.Descriptor) && item.Unpack<SchematicLine>().Type is
            SchematicLineType.SltWire or SchematicLineType.SltBus;

    private static SchematicDesign NativeRepresentation(SchematicDesign desired, IReadOnlySet<Guid> added)
    {
        var paths = desired.SheetBindings.Where(b => added.Contains(b.SheetInstanceId))
            .Select(b => SchematicDesignBindings.PathKey(b.NativePath)).ToHashSet(StringComparer.Ordinal);
        var data = desired.Schematic.Clone();
        foreach (var screen in data.Instances.Where(s => paths.Contains(SchematicNativeSheetChanges.Key(s))))
        {
            // PackSymbol orders placed pins by UUID; changing UUIDs on a copied
            // declaration changes their order, not their data or identity.
            for (int i = 0; i < screen.Items.Count; ++i)
                if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor))
                {
                    var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
                    SchematicNativeCreationProjection.OrderNativeDefinitionChildren(symbol.Definition);
                    SchematicNativeCreationProjection.OrderDefinitionPins(symbol.Definition, libraryIds: false);
                    screen.Items[i] = Any.Pack(symbol);
                }
            var cached = screen.CachedSymbols.OrderBy(c => c.CacheKey, StringComparer.Ordinal).ToArray();
            foreach (var entry in cached) SchematicNativeCreationProjection.OrderNativeDefinitionChildren(entry.Definition);
            screen.CachedSymbols.Clear(); screen.CachedSymbols.Add(cached);
        }
        return desired with { Schematic = data };
    }
}
