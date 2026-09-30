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
            desired = CanonicalPlacements(desired);
            var report = SchematicDesignBindings.Inspect(desired, state.KnowledgeLibraries, token);
            if (!report.IdentitiesResolved || report.Differences.Count != 0)
                return Failure("xml_shared_sheet_bindings_required", "Supply the complete native hierarchy and exact model bindings for every resulting instance.");
            var sheetChoices = SchematicSheetMoveChoices.FromDesigns(baseline, desired);
            var topology = SchematicNativeSheetChanges.Compare(state.Observed, desired.Schematic, sheetChoices);
            if (topology.ErrorCode is not null) return Failure(topology.ErrorCode, topology.ErrorMessage!);
            var afterSheets = after.SheetInstances.ToDictionary(s => s.Id);
            if (before.SheetInstances.Any(s => afterSheets.TryGetValue(s.Id, out var kept) && kept.DefinitionId != s.DefinitionId))
                return Failure("xml_shared_sheet_definition_mismatch", "A surviving sheet instance keeps its physical definition.");
            var retiredSheets = oldSheets.Where(id => !afterSheets.ContainsKey(id)).ToHashSet();
            var beforeOwners = before.Components.ToDictionary(c => c.Id);
            var survivingSymbols = before.Symbols.Where(s => !retiredSheets.Contains(s.EffectiveSheetInstanceId(beforeOwners[s.ComponentId]))).ToArray();
            var drawnBefore = before.Symbols.Select(s => s.ComponentId).ToHashSet();
            var drawnAfter = survivingSymbols.Select(s => s.ComponentId).ToHashSet();
            if (before.Components.Any(c => retiredSheets.Contains(c.SheetInstanceId) && drawnAfter.Contains(c.Id)))
                return Failure(SchematicNativeRemovalProjection.SplitComponent, "A retired owning sheet cannot leave units of its component on surviving sheets.");
            var retiredOwners = before.Components.Where(c => retiredSheets.Contains(c.SheetInstanceId)
                || drawnBefore.Contains(c.Id) && !drawnAfter.Contains(c.Id)).Select(c => c.Id).ToHashSet();
            var addedIds = added.Select(s => s.Id).ToHashSet();
            var oldComponents = before.Components.Select(c => c.Id).ToHashSet();
            var oldOccurrences = before.Symbols.Select(s => s.Id).ToHashSet();
            var oldNets = before.Nets.Select(n => n.Id).ToHashSet();
            var allComponents = after.Components.ToDictionary(c => c.Id);
            var existingOwners = before.Components.ToDictionary(c => c.Id);
            var componentDefinitions = before.Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);
            var partsById = before.Parts.ToDictionary(p => p.Id);
            var newlyDrawnPins = after.Symbols.Where(s => !oldOccurrences.Contains(s.Id) && oldComponents.Contains(s.ComponentId))
                .SelectMany(s => partsById[componentDefinitions[existingOwners[s.ComponentId].DefinitionId].PartId].Pins
                    .Where(p => (p.Unit == 0 || p.Unit == s.Unit) && !before.Symbols.Any(old => old.ComponentId == s.ComponentId
                        && (p.Unit == 0 || p.Unit == old.Unit))).Select(p => new PinEndpoint(s.ComponentId, p.Number))).ToHashSet();
            bool AddedPin(PinEndpoint pin) => !oldComponents.Contains(pin.ComponentId) || newlyDrawnPins.Contains(pin);
            if (after.Components.Any(c => !oldComponents.Contains(c.Id) && !addedIds.Contains(c.SheetInstanceId))
                || after.Symbols.Any(s => !oldOccurrences.Contains(s.Id)
                    && !addedIds.Contains(s.EffectiveSheetInstanceId(allComponents[s.ComponentId]))))
                return Failure("xml_shared_sheet_owner_conflict", "New components must belong to new instances, and every new unit must be drawn on a new instance.");
            var survivingOwners = before.Components.Where(c => !retiredOwners.Contains(c.Id)).ToArray();
            var usedDefinitions = after.Components.Select(c => c.DefinitionId).ToHashSet();
            var retiredDefinitions = before.Components.Where(c => retiredOwners.Contains(c.Id)).Select(c => c.DefinitionId)
                .Where(id => !usedDefinitions.Contains(id)).ToHashSet();
            var hiddenDefinitions = before.SheetInstances.Where(s => retiredSheets.Contains(s.Id)).Select(s => s.DefinitionId)
                .Except(after.SheetInstances.Select(s => s.DefinitionId)).ToHashSet();
            var projectedNets = before.Nets.Select(n => n with { Pins = n.Pins.Where(p => !retiredOwners.Contains(p.ComponentId)).ToArray() })
                .Where(n => n.Pins.Count != 0 || before.Nets.Single(old => old.Id == n.Id).Pins.Count == 0).ToArray();
            var expected = before with
            {
                Sheets = before.Sheets.Where(s => !hiddenDefinitions.Contains(s.Id)).Select(s => s with
                    { Components = s.Components.Where(c => !retiredDefinitions.Contains(c.Id)).ToArray() }).ToArray(),
                SheetInstances = before.SheetInstances.Where(s => !retiredSheets.Contains(s.Id)).Select(s => s with
                    { ParentId = afterSheets[s.Id].ParentId }).ToArray(),
                Components = survivingOwners, Symbols = survivingSymbols, Nets = projectedNets
            };
            expected = expected with
            {
                SheetInstances = [.. expected.SheetInstances, .. added],
                Components = [.. expected.Components, .. after.Components.Where(c => !oldComponents.Contains(c.Id))],
                Symbols = [.. expected.Symbols, .. after.Symbols.Where(s => !oldOccurrences.Contains(s.Id))],
                Nets = [.. projectedNets.Select(n => n with { Pins = [.. n.Pins,
                    .. (after.Nets.SingleOrDefault(wanted => wanted.Id == n.Id)?.Pins ?? []).Where(AddedPin)] }),
                    .. after.Nets.Where(n => !oldNets.Contains(n.Id))]
            };
            if (CircuitXml.Write(after) != CircuitXml.Write(expected)
                || after.Nets.Where(n => !oldNets.Contains(n.Id)).Any(n => n.Pins.Any(p => !AddedPin(p))))
                return Failure("xml_shared_sheet_existing_changed", "Preserve surviving owners, parts and connections; retire only the owners and units whose sheets leave the design.");
            var survivingOccurrenceIds = survivingSymbols.Select(s => s.Id).ToHashSet();
            if (baseline.SymbolBindings.Where(b => survivingOccurrenceIds.Contains(b.SymbolOccurrenceId)).Any(b => !desired.SymbolBindings.Contains(b))
                || desired.SymbolBindings.Any(b => oldOccurrences.Contains(b.SymbolOccurrenceId) && !survivingOccurrenceIds.Contains(b.SymbolOccurrenceId)))
                return Failure("xml_shared_sheet_binding_changed", "Keep exact symbol identities for every surviving occurrence.");
            var current = state.Observed.Instances.ToDictionary(SchematicNativeSheetChanges.Key, StringComparer.Ordinal);
            var wanted = desired.Schematic.Instances.ToDictionary(SchematicNativeSheetChanges.Key, StringComparer.Ordinal);
            var addedPaths = desired.SheetBindings.Where(b => addedIds.Contains(b.SheetInstanceId))
                .Select(b => SchematicDesignBindings.PathKey(b.NativePath)).ToHashSet(StringComparer.Ordinal);
            if (!topology.Inserted.ToHashSet(StringComparer.Ordinal).SetEquals(addedPaths))
                return Failure("xml_shared_sheet_paths_changed", "Every added native path must name its declared new model instance.");
            var physicalScreens = current.Values.Select(s => s.Metadata.ScreenId.Value).ToHashSet(StringComparer.Ordinal);
            if (addedPaths.Any(p => !physicalScreens.Contains(wanted[p].Metadata.ScreenId.Value)))
                return Failure("xml_shared_sheet_file_changed", "A shared instance must reuse the physical screen of its existing definition.");
            var oldOwners = existingOwners;
            var oldSymbols = before.Symbols.ToDictionary(s => s.Id);
            var definitionsById = before.Sheets.SelectMany(s => s.Components.Select(c => (Sheet: s.Id, Component: c)))
                .ToDictionary(p => p.Component.Id);
            var sheetDefinitions = before.SheetInstances.ToDictionary(s => s.Id, s => s.DefinitionId);
            var oldPaths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
            var templateDefinitions = baseline.SymbolBindings.GroupBy(b =>
            {
                var occurrence = oldSymbols[b.SymbolOccurrenceId];
                string path = oldPaths[occurrence.EffectiveSheetInstanceId(oldOwners[occurrence.ComponentId])];
                return current[path].Metadata.ScreenId.Value + "#" + b.NativeObjectId.ToString("D");
            }).ToDictionary(g => g.Key, g => g.Select(b =>
            {
                var occurrence = oldSymbols[b.SymbolOccurrenceId]; var owner = oldOwners[occurrence.ComponentId];
                return (Definition: owner.DefinitionId,
                    Local: sheetDefinitions[owner.SheetInstanceId] == sheetDefinitions[occurrence.EffectiveSheetInstanceId(owner)]);
            }).Distinct().ToArray());
            var newOwners = after.Components.ToDictionary(c => c.Id);
            var newSymbols = after.Symbols.ToDictionary(s => s.Id);
            var allPaths = desired.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
            foreach (var binding in desired.SymbolBindings.Where(b => !oldOccurrences.Contains(b.SymbolOccurrenceId)))
            {
                var occurrence = newSymbols[binding.SymbolOccurrenceId]; var owner = newOwners[occurrence.ComponentId];
                string path = allPaths[occurrence.EffectiveSheetInstanceId(owner)];
                if (!templateDefinitions.TryGetValue(wanted[path].Metadata.ScreenId.Value + "#" + binding.NativeObjectId.ToString("D"), out var choices))
                    return Failure("xml_shared_sheet_definition_mismatch", "Every new occurrence needs an existing physical symbol binding.");
                var local = choices.Where(c => c.Local).Select(c => c.Definition).Distinct().ToArray();
                bool matches = local.Length != 0 ? local.Length == 1 && local[0] == owner.DefinitionId
                    : definitionsById.TryGetValue(owner.DefinitionId, out var selected)
                        && choices.Any(c => definitionsById[c.Definition].Component.PartId == selected.Component.PartId);
                if (!matches)
                    return Failure("xml_shared_sheet_definition_mismatch", "Reuse each locally owned definition; an externally owned unit must explicitly select a component of the same part.");
            }

            bool RecordsKept<T>(IEnumerable<T> oldRecords, IEnumerable<T> newRecords,
                Func<T, string> path, Func<T, string> project, Action<T, string> repath, string screenId) where T : IMessage<T>
            {
                string Key(T record) => project(record) + "#" + path(record);
                var next = newRecords.ToDictionary(Key, StringComparer.Ordinal);
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var previous in oldRecords)
                {
                    string oldPath = path(previous);
                    bool ownProject = project(previous).Length == 0 || project(previous) == state.Observed.Document.Project.Name;
                    string? destination = ownProject ? topology.Now(oldPath) : oldPath;
                    if (destination is null)
                    {
                        // A detached historical record may remain verbatim, but
                        // cannot be silently edited or reused as a live owner.
                        if (next.TryGetValue(Key(previous), out var detached))
                        { if (!previous.Equals(detached)) return false; used.Add(Key(previous)); }
                        continue;
                    }
                    var expectedRecord = previous.Clone(); repath(expectedRecord, destination);
                    if (!next.TryGetValue(Key(expectedRecord), out var actual) || !expectedRecord.Equals(actual)) return false;
                    used.Add(Key(expectedRecord));
                }
                return next.Where(p => !used.Contains(p.Key)).All(p =>
                    (project(p.Value).Length == 0 || project(p.Value) == state.Observed.Document.Project.Name)
                    && addedPaths.Contains(path(p.Value)) && wanted[path(p.Value)].Metadata.ScreenId.Value == screenId);
            }
            static void Repath(Google.Protobuf.Collections.RepeatedField<Kiapi.Common.Types.KIID> path, string value)
            { path.Clear(); path.Add(value.Split('/').Select(id => new Kiapi.Common.Types.KIID { Value = id })); }
            var operations = SchematicHierarchyDelta.PlanWithSheetChoices(state.Observed, desired.Schematic, sheetChoices, token);
            foreach (var operation in operations)
            {
                if (operation.SetSheetInstancePaths is not null) continue;
                string target = string.Join('/', operation.TargetDocument.SheetPath.Path.Select(p => p.Value));
                if (operation.Create?.Is(SheetSymbol.Descriptor) == true)
                {
                    var sheet = operation.Create.Unpack<SheetSymbol>();
                    if (!wanted.ContainsKey(target + "/" + sheet.Id.Value) || !physicalScreens.Contains(sheet.ChildScreenId.Value))
                        return Failure("xml_shared_sheet_operation_invalid", "A shared-sheet batch may create only its declared sheet references.");
                    continue;
                }
                if (!current.TryGetValue(target, out var screen))
                {
                    if (!wanted.TryGetValue(target, out var desiredScreen))
                        return Failure("xml_shared_sheet_operation_invalid", "The operation must target a declared sheet instance.");
                    screen = current.Values.FirstOrDefault(s => s.Metadata.ScreenId.Equals(desiredScreen.Metadata.ScreenId));
                    if (screen is null) return Failure("xml_shared_sheet_operation_invalid", "Reuse an existing physical screen.");
                }
                if (operation.ReplaceLibraryCache is { } cache
                    && cache.Definitions.OrderBy(c => c.CacheKey, StringComparer.Ordinal)
                        .SequenceEqual(screen.CachedSymbols.OrderBy(c => c.CacheKey, StringComparer.Ordinal))) continue;
                var originals = SchematicItemDelta.Index(screen.Items);
                if (operation.Remove is { } removed && originals.TryGetValue(Guid.Parse(removed.Value), out var removedItem)
                    && removedItem is SheetSymbol) continue;
                if (operation.Update?.Is(SchematicSymbolInstance.Descriptor) == true)
                {
                    var next = operation.Update.Unpack<SchematicSymbolInstance>();
                    if (!originals.TryGetValue(Guid.Parse(next.Id.Value), out var raw) || raw is not SchematicSymbolInstance old
                        || old.InstanceRecords is null || next.InstanceRecords is null
                        || !RecordsKept(old.InstanceRecords.Records, next.InstanceRecords.Records,
                            r => string.Join('/', r.Path.Select(p => p.Value)), r => r.ProjectName,
                            (r, value) => Repath(r.Path, value), screen.Metadata.ScreenId.Value))
                        return Failure("xml_shared_sheet_records_changed", "Keep all existing symbol placements and add only the new instance records.");
                    old = old.Clone(); old.InstanceRecords = null; next.InstanceRecords = null;
                    old.Path = null; next.Path = null; old.Unit = null; next.Unit = null;
                    old.ReferenceField.Text.Text_ = ""; next.ReferenceField.Text.Text_ = "";
                    old.Variants = null; next.Variants = null;
                    if (old.Equals(next)) continue;
                }
                if (operation.Update?.Is(SheetSymbol.Descriptor) == true)
                {
                    var next = operation.Update.Unpack<SheetSymbol>();
                    if (!originals.TryGetValue(Guid.Parse(next.Id.Value), out var raw) || raw is not SheetSymbol old
                        || old.InstanceRecords is null || next.InstanceRecords is null
                        || !RecordsKept(old.InstanceRecords.Records, next.InstanceRecords.Records,
                            r => string.Join('/', r.Path.Select(p => p.Value)), r => r.ProjectName,
                            (r, value) => Repath(r.Path, value), screen.Metadata.ScreenId.Value))
                        return Failure("xml_shared_sheet_records_changed", "Keep all existing child-sheet placements and add only the new instance records.");
                    old = old.Clone(); old.InstanceRecords = null; next.InstanceRecords = null;
                    old.Path = null; next.Path = null; old.PageNumber = ""; next.PageNumber = "";
                    old.Variants = null; next.Variants = null;
                    if (old.Equals(next)) continue;
                }
                return Failure("xml_shared_sheet_contents_changed", "Adding an instance cannot also change the existing physical drawing or project settings.");
            }
            if (!operations.Any(o => o.Create?.Is(SheetSymbol.Descriptor) == true))
                return Failure("xml_shared_sheet_reference_missing", "The new instance needs a native sheet reference.");
            var retiredReferences = retiredOwners.Order().Select(id => new ComponentReferenceChange(new(id), ComponentReferenceChangeKind.Removed,
                "The component's owning sheet or final native occurrence was explicitly retired.", [])).ToArray();
            var retiredNets = before.Nets.Where(n => !after.Nets.Any(currentNet => currentNet.Id == n.Id)).Select(n =>
                new NetIdentityChange(n.Id, NetBindingChangeKind.Removed, "Every endpoint of this connection was retired.", [])).ToArray();
            desired = desired with { Engineering = ComponentReferenceRetention.Retain(desired.Engineering, after,
                retiredReferences, state.KnowledgeLibraries, retiredNets, before) };
            return new(desired, SchematicDesignXml.Write(desired, state.KnowledgeLibraries), operations,
                hierarchy, new(desired.Engineering, [], retiredNets, [], report.CoverageGaps, ComponentChanges: retiredReferences)
                    { AddedSheetInstances = added.Select(s => s.Id).ToArray(), RemovedSheetInstances = retiredSheets.Order().ToArray(),
                        MovedSheetInstances = before.SheetInstances.Where(s => afterSheets.TryGetValue(s.Id, out var next) && next.ParentId != s.ParentId)
                            .Select(s => s.Id).ToArray(),
                        RemovedSymbolOccurrences = before.Symbols.Where(s => !survivingOccurrenceIds.Contains(s.Id)).Select(s => s.Id).ToArray(),
                        AddedComponents = after.Components.Where(c => !oldComponents.Contains(c.Id)).Select(c => c.Id).ToArray(),
                        AddedSymbolOccurrences = after.Symbols.Where(s => !oldOccurrences.Contains(s.Id)).Select(s => s.Id).ToArray() },
                null, [], [], null, report.CoverageGaps, true);
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
                    var ordered = records.Records.OrderBy(r => r.Path.Count)
                        .ThenBy(r => string.Join('/', r.Path.Select(p => p.Value)), StringComparer.Ordinal).ToArray();
                    records.Records.Clear(); records.Records.Add(ordered); screen.Items[i] = Any.Pack(symbol);
                }
                else if (item.Is(SheetSymbol.Descriptor))
                {
                    var sheet = item.Unpack<SheetSymbol>();
                    if (sheet.InstanceRecords is not { } records) continue;
                    var ordered = records.Records.OrderBy(r => r.Path.Count)
                        .ThenBy(r => string.Join('/', r.Path.Select(p => p.Value)), StringComparer.Ordinal).ToArray();
                    records.Records.Clear(); records.Records.Add(ordered); screen.Items[i] = Any.Pack(sheet);
                }
            }
        return design with { Schematic = data };
    }
}
