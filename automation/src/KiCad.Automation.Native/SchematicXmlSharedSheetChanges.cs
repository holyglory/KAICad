using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>Admits explicitly bound XML instances of existing physical sheets.
/// The supplied candidate owns the new references and model identities; old owners are immutable.</summary>
internal static class SchematicXmlSharedSheetChanges
{
    internal static SchematicSynchronizationPlan? Prepare(DesignRecoveryState state, SchematicDesign desired,
        SchematicHierarchyMergeResult hierarchy, CancellationToken token)
    {
        var baseline = state.Baseline; var before = baseline.Engineering.Circuit; var after = desired.Engineering.Circuit;
        var oldSheets = before.SheetInstances.Select(s => s.Id).ToHashSet();
        var added = after.SheetInstances.Where(s => !oldSheets.Contains(s.Id)).ToArray();
        if (added.Length == 0 || added.Any(s => !before.Sheets.Any(d => d.Id == s.DefinitionId))) return null;
        SchematicSynchronizationPlan Failure(string code, string message) =>
            new(null, null, [], hierarchy, null, null, [], [], null, [], true, code, message);
        try
        {
            token.ThrowIfCancellationRequested();
            bool unchanged;
            try { unchanged = SchematicRebuild.NativeUnchanged(state, token); }
            catch (AutomationException) { unchanged = false; }
            if (!unchanged) return Failure("ownership_change_with_xml_edits", "Synchronize the native changes before adding a shared sheet from XML; both versions are preserved.");
            var addedIds = added.Select(s => s.Id).ToHashSet();
            var oldComponents = before.Components.Select(c => c.Id).ToHashSet();
            var oldOccurrences = before.Symbols.Select(s => s.Id).ToHashSet();
            var oldNets = before.Nets.Select(n => n.Id).ToHashSet();
            if (after.Components.Any(c => !oldComponents.Contains(c.Id) && !addedIds.Contains(c.SheetInstanceId))
                || after.Symbols.Any(s => !oldOccurrences.Contains(s.Id) && oldComponents.Contains(s.ComponentId)))
                return Failure("xml_shared_sheet_owner_conflict", "New shared-sheet components must belong to the new instances; resolve external unit ownership separately.");
            var stripped = after with
            {
                SheetInstances = after.SheetInstances.Where(s => oldSheets.Contains(s.Id)).ToArray(),
                Components = after.Components.Where(c => oldComponents.Contains(c.Id)).ToArray(),
                Symbols = after.Symbols.Where(s => oldOccurrences.Contains(s.Id)).ToArray(),
                Nets = after.Nets.Where(n => oldNets.Contains(n.Id)).Select(n => n with
                    { Pins = n.Pins.Where(p => oldComponents.Contains(p.ComponentId)).ToArray() }).ToArray()
            };
            if (CircuitXml.Write(stripped) != CircuitXml.Write(before)
                || after.Nets.Where(n => !oldNets.Contains(n.Id)).Any(n => n.Pins.Any(p => oldComponents.Contains(p.ComponentId))))
                return Failure("xml_shared_sheet_existing_changed", "Adding a shared instance must preserve existing sheets, components, parts and connections.");
            if (baseline.SheetBindings.Any(b => !desired.SheetBindings.Any(d => d.SheetInstanceId == b.SheetInstanceId && d.NativePath.SequenceEqual(b.NativePath)))
                || baseline.SymbolBindings.Any(b => !desired.SymbolBindings.Contains(b)))
                return Failure("xml_shared_sheet_binding_changed", "Keep every existing sheet and symbol binding when adding a shared instance.");
            // Native PackSymbol/PackSheet emit placement records in path order.
            // Prepare that representation before journaling; every record value
            // and the original requested XML bytes still remain independently checked.
            desired = CanonicalPlacements(desired);
            var report = SchematicDesignBindings.Inspect(desired, state.KnowledgeLibraries, token);
            if (!report.IdentitiesResolved || report.Differences.Count != 0)
                return Failure("xml_shared_sheet_bindings_required", "A shared-sheet addition needs its complete native snapshot, exact path bindings, component references and unit placements before application.");
            var current = state.Observed.Instances.ToDictionary(SchematicNativeSheetChanges.Key, StringComparer.Ordinal);
            var wanted = desired.Schematic.Instances.ToDictionary(SchematicNativeSheetChanges.Key, StringComparer.Ordinal);
            var addedPaths = desired.SheetBindings.Where(b => addedIds.Contains(b.SheetInstanceId))
                .Select(b => SchematicDesignBindings.PathKey(b.NativePath)).ToHashSet(StringComparer.Ordinal);
            if (current.Keys.Any(p => !wanted.ContainsKey(p)) || !wanted.Keys.Except(current.Keys).ToHashSet(StringComparer.Ordinal).SetEquals(addedPaths))
                return Failure("xml_shared_sheet_paths_changed", "The native snapshot may add only the declared shared instances.");
            var physicalScreens = current.Values.Select(s => s.Metadata.ScreenId.Value).ToHashSet(StringComparer.Ordinal);
            if (addedPaths.Any(p => !physicalScreens.Contains(wanted[p].Metadata.ScreenId.Value)))
                return Failure("xml_shared_sheet_file_changed", "A shared instance must reuse the physical screen of its existing definition.");
            var oldOwners = before.Components.ToDictionary(c => c.Id);
            var oldSymbols = before.Symbols.ToDictionary(s => s.Id);
            var oldPaths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
            var templateDefinitions = baseline.SymbolBindings.GroupBy(b =>
            {
                var occurrence = oldSymbols[b.SymbolOccurrenceId];
                string path = oldPaths[occurrence.EffectiveSheetInstanceId(oldOwners[occurrence.ComponentId])];
                return current[path].Metadata.ScreenId.Value + "#" + b.NativeObjectId.ToString("D");
            }).ToDictionary(g => g.Key, g => g.Select(b => oldOwners[oldSymbols[b.SymbolOccurrenceId].ComponentId].DefinitionId).Distinct().ToArray());
            var newOwners = after.Components.ToDictionary(c => c.Id);
            var newSymbols = after.Symbols.ToDictionary(s => s.Id);
            var allPaths = desired.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
            foreach (var binding in desired.SymbolBindings.Where(b => !oldOccurrences.Contains(b.SymbolOccurrenceId)))
            {
                var occurrence = newSymbols[binding.SymbolOccurrenceId]; var owner = newOwners[occurrence.ComponentId];
                string path = allPaths[occurrence.EffectiveSheetInstanceId(owner)];
                if (!templateDefinitions.TryGetValue(wanted[path].Metadata.ScreenId.Value + "#" + binding.NativeObjectId.ToString("D"), out var choices)
                    || choices.Length != 1 || choices[0] != owner.DefinitionId)
                    return Failure("xml_shared_sheet_definition_mismatch", "Bind each new occurrence to the same component definition as its existing physical symbol.");
            }

            bool RecordsKept<T>(IEnumerable<T> oldRecords, IEnumerable<T> newRecords,
                Func<T, string> path, string screenId) where T : IMessage<T>
            {
                var existing = oldRecords.ToDictionary(path, StringComparer.Ordinal);
                var next = newRecords.ToDictionary(path, StringComparer.Ordinal);
                return existing.All(p => next.TryGetValue(p.Key, out var value) && p.Value.Equals(value))
                    && next.Keys.Except(existing.Keys).All(p => addedPaths.Contains(p) && wanted[p].Metadata.ScreenId.Value == screenId);
            }
            var operations = SchematicHierarchyDelta.Plan(state.Observed, desired.Schematic, token);
            foreach (var operation in operations)
            {
                string target = string.Join('/', operation.TargetDocument.SheetPath.Path.Select(p => p.Value));
                if (operation.Create?.Is(SheetSymbol.Descriptor) == true)
                {
                    var sheet = operation.Create.Unpack<SheetSymbol>();
                    if (!addedPaths.Contains(target + "/" + sheet.Id.Value) || !physicalScreens.Contains(sheet.ChildScreenId.Value))
                        return Failure("xml_shared_sheet_operation_invalid", "A shared-sheet batch may create only its declared sheet references.");
                    continue;
                }
                if (!current.TryGetValue(target, out var screen))
                    return Failure("xml_shared_sheet_operation_invalid", "A shared-sheet batch may update only existing physical contents.");
                if (operation.ReplaceLibraryCache is { } cache
                    && cache.Definitions.OrderBy(c => c.CacheKey, StringComparer.Ordinal)
                        .SequenceEqual(screen.CachedSymbols.OrderBy(c => c.CacheKey, StringComparer.Ordinal))) continue;
                var originals = SchematicItemDelta.Index(screen.Items);
                if (operation.Update?.Is(SchematicSymbolInstance.Descriptor) == true)
                {
                    var next = operation.Update.Unpack<SchematicSymbolInstance>();
                    if (!originals.TryGetValue(Guid.Parse(next.Id.Value), out var raw) || raw is not SchematicSymbolInstance old
                        || old.InstanceRecords is null || next.InstanceRecords is null
                        || !RecordsKept(old.InstanceRecords.Records, next.InstanceRecords.Records,
                            r => string.Join('/', r.Path.Select(p => p.Value)), screen.Metadata.ScreenId.Value))
                        return Failure("xml_shared_sheet_records_changed", "Keep all existing symbol placements and add only the new instance records.");
                    old = old.Clone(); old.InstanceRecords = null; next.InstanceRecords = null;
                    if (old.Equals(next)) continue;
                }
                if (operation.Update?.Is(SheetSymbol.Descriptor) == true)
                {
                    var next = operation.Update.Unpack<SheetSymbol>();
                    if (!originals.TryGetValue(Guid.Parse(next.Id.Value), out var raw) || raw is not SheetSymbol old
                        || old.InstanceRecords is null || next.InstanceRecords is null
                        || !RecordsKept(old.InstanceRecords.Records, next.InstanceRecords.Records,
                            r => string.Join('/', r.Path.Select(p => p.Value)), screen.Metadata.ScreenId.Value))
                        return Failure("xml_shared_sheet_records_changed", "Keep all existing child-sheet placements and add only the new instance records.");
                    old = old.Clone(); old.InstanceRecords = null; next.InstanceRecords = null;
                    if (old.Equals(next)) continue;
                }
                return Failure("xml_shared_sheet_contents_changed", "Adding an instance cannot also change the existing physical drawing or project settings.");
            }
            if (!operations.Any(o => o.Create?.Is(SheetSymbol.Descriptor) == true))
                return Failure("xml_shared_sheet_reference_missing", "The new instance needs a native sheet reference.");
            return new(desired, SchematicDesignXml.Write(desired, state.KnowledgeLibraries), operations,
                hierarchy, new(desired.Engineering, [], [], [], report.CoverageGaps)
                    { AddedSheetInstances = added.Select(s => s.Id).ToArray() }, null, [], [], null, report.CoverageGaps, true);
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
    }

    private static SchematicDesign CanonicalPlacements(SchematicDesign design)
    {
        var data = design.Schematic.Clone();
        foreach (var screen in data.Instances)
            for (int i = 0; i < screen.Items.Count; ++i)
            {
                var item = screen.Items[i];
                if (item.Is(SchematicSymbolInstance.Descriptor))
                {
                    var symbol = item.Unpack<SchematicSymbolInstance>();
                    if (symbol.InstanceRecords is not { } records) continue;
                    var ordered = records.Records.OrderBy(r => string.Join('/', r.Path.Select(p => p.Value)), StringComparer.Ordinal).ToArray();
                    records.Records.Clear(); records.Records.Add(ordered); screen.Items[i] = Any.Pack(symbol);
                }
                else if (item.Is(SheetSymbol.Descriptor))
                {
                    var sheet = item.Unpack<SheetSymbol>();
                    if (sheet.InstanceRecords is not { } records) continue;
                    var ordered = records.Records.OrderBy(r => string.Join('/', r.Path.Select(p => p.Value)), StringComparer.Ordinal).ToArray();
                    records.Records.Clear(); records.Records.Add(ordered); screen.Items[i] = Any.Pack(sheet);
                }
            }
        return design with { Schematic = data };
    }
}
