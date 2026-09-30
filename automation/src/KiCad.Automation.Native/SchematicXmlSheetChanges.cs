using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>Projects model sheet removals and parent changes onto exact native paths.
/// Native batch application, file publication and history remain owned by the existing executor.</summary>
internal static class SchematicXmlSheetChanges
{
    internal static SchematicSynchronizationPlan? Prepare(DesignRecoveryState state, SchematicDesign desired,
        SchematicHierarchyMergeResult hierarchy, CancellationToken token)
    {
        var baseline = state.Baseline;
        var before = baseline.Engineering.Circuit.SheetInstances.ToDictionary(s => s.Id);
        var after = desired.Engineering.Circuit.SheetInstances.ToDictionary(s => s.Id);
        if (after.Keys.Any(id => !before.ContainsKey(id))
            || before.Values.OrderBy(s => s.Id).SequenceEqual(after.Values.OrderBy(s => s.Id))) return null;
        var gaps = new List<HierarchyCoverageGap>();
        SchematicSynchronizationPlan Failure(string code, string message, IReadOnlyList<SchematicBindingIssue>? issues = null) =>
            new(null, null, [], hierarchy, null, null, issues ?? [], [], null, gaps, true, code, message);
        try
        {
            token.ThrowIfCancellationRequested();
            var original = SchematicDesignBindings.Inspect(baseline, state.KnowledgeLibraries, token);
            gaps.AddRange(original.CoverageGaps);
            if (!original.IdentitiesResolved) return Failure("unresolved_design_bindings", "Resolve the saved sheet bindings first.", original.Issues);
            if (after.Values.Any(s => s.DefinitionId != before[s.Id].DefinitionId)
                || !before.Values.Where(s => s.ParentId is null).SequenceEqual(after.Values.Where(s => s.ParentId is null)))
                return Failure("xml_sheet_identity_changed", "Keep the same root and sheet definitions when moving or removing sheets.");
            bool nativeUnchanged;
            try { nativeUnchanged = SchematicRebuild.NativeUnchanged(state, token); }
            catch (AutomationException) { nativeUnchanged = false; }
            if (!nativeUnchanged)
                return Failure("ownership_change_with_xml_edits", "The XML changes sheet ownership while KiCad also changed. Both versions are kept; synchronize or undo the native change first.");

            var oldPaths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
            var newPaths = new Dictionary<Guid, string>();
            string NewPath(Guid id)
            {
                if (newPaths.TryGetValue(id, out var known)) return known;
                var sheet = after[id];
                string path = sheet.ParentId is { } parent ? NewPath(parent) + "/" + SchematicNativeSheetChanges.Last(oldPaths[id]) : oldPaths[id];
                newPaths.Add(id, path); return path;
            }
            foreach (Guid id in after.Keys) NewPath(id);
            var pathMap = newPaths.ToDictionary(p => oldPaths[p.Key], p => p.Value, StringComparer.Ordinal);
            foreach (var binding in desired.SheetBindings)
                if (!oldPaths.TryGetValue(binding.SheetInstanceId, out var old)
                    || SchematicDesignBindings.PathKey(binding.NativePath) != old
                    && (!newPaths.TryGetValue(binding.SheetInstanceId, out var next) || SchematicDesignBindings.PathKey(binding.NativePath) != next))
                    return Failure("xml_sheet_binding_changed", "A sheet move keeps its native sheet-symbol identity; preserve its current binding or the corresponding new parent path.");

            var sourceScreens = state.Observed.Instances.ToDictionary(SchematicNativeSheetChanges.Key, StringComparer.Ordinal);
            var fileNames = new Dictionary<Guid, string>();
            string NativeFile(Guid id)
            {
                if (fileNames.TryGetValue(id, out var known)) return known;
                var sheet = before[id];
                string file;
                if (sheet.ParentId is not { } parent)
                    file = Path.Combine(state.Observed.Document.Project.Path, state.Observed.Document.Project.Name + ".kicad_sch");
                else
                {
                    string relative = SchematicNativeSheetChanges.SheetSymbolOf(state.Observed, oldPaths[id])?.FilenameField?.Text?.Text_
                        ?? throw new AutomationException("xml_sheet_filename_missing", "A moved sheet needs its recorded native filename.");
                    relative = relative.Replace("${KIPRJMOD}", state.Observed.Document.Project.Path, StringComparison.Ordinal)
                        .Replace("$(KIPRJMOD)", state.Observed.Document.Project.Path, StringComparison.Ordinal);
                    if (relative.Contains('$') || relative.Contains('%'))
                        throw new AutomationException("xml_sheet_filename_unresolved", "The sheet filename depends on an unrecorded variable; resolve its native file before moving it between directories.");
                    file = Path.GetFullPath(relative, Path.GetDirectoryName(NativeFile(parent))!);
                }
                fileNames.Add(id, file); return file;
            }
            var drawing = state.Observed.Clone(); drawing.Instances.Clear();
            var output = new Dictionary<Guid, SchematicScreenData>();
            foreach (Guid id in after.Keys)
            {
                var screen = sourceScreens[oldPaths[id]].Clone();
                SetPath(screen.Metadata.Document.SheetPath.Path, newPaths[id]);
                // Rebuild only the sheet-reference list below, retaining every other native item.
                for (int index = screen.Items.Count - 1; index >= 0; --index)
                {
                    var packed = screen.Items[index];
                    if (packed.Is(SheetSymbol.Descriptor)) { screen.Items.RemoveAt(index); continue; }
                    if (packed.Is(SchematicSymbolInstance.Descriptor))
                    {
                        var symbol = packed.Unpack<SchematicSymbolInstance>();
                        SetPath(symbol.Path.Path, newPaths[id]);
                        if (symbol.InstanceRecords is { } placements)
                        {
                            foreach (var record in placements.Records)
                                if ((record.ProjectName.Length == 0 || record.ProjectName == state.Observed.Document.Project.Name)
                                    && pathMap.TryGetValue(PathKey(record.Path), out var moved)) SetPath(record.Path, moved);
                            // PackSymbol emits records in native path order. A reparent
                            // can change that order without changing any record values.
                            var ordered = placements.Records.OrderBy(r => PathKey(r.Path), StringComparer.Ordinal).ToArray();
                            placements.Records.Clear(); placements.Records.Add(ordered);
                        }
                        screen.Items[index] = Any.Pack(symbol);
                    }
                }
                output.Add(id, screen); drawing.Instances.Add(screen);
            }
            foreach (var sheet in after.Values.Where(s => s.ParentId is not null))
            {
                string oldPath = oldPaths[sheet.Id];
                var symbol = SchematicNativeSheetChanges.SheetSymbolOf(state.Observed, oldPath)?.Clone()
                    ?? throw new AutomationException("xml_sheet_reference_missing", "The saved sheet has no exact native parent reference.");
                Guid parent = sheet.ParentId!.Value;
                SetPath(symbol.Path.Path, newPaths[parent]);
                if (before[sheet.Id].ParentId != parent)
                {
                    string oldDirectory = Path.GetDirectoryName(NativeFile(before[sheet.Id].ParentId!.Value))!;
                    string newDirectory = Path.GetDirectoryName(NativeFile(parent))!;
                    if (!string.Equals(oldDirectory, newDirectory, StringComparison.Ordinal))
                        symbol.FilenameField.Text.Text_ = Path.GetRelativePath(newDirectory, NativeFile(sheet.Id)).Replace('\\', '/');
                }
                if (symbol.InstanceRecords is { } placements)
                {
                    // A sheet placement records its parent path, unlike a component's full path.
                    // Preserve foreign/root records and records for detached instances.
                    foreach (var record in placements.Records)
                    {
                        if (record.ProjectName.Length != 0 && record.ProjectName != state.Observed.Document.Project.Name) continue;
                        string recordPath = PathKey(record.Path);
                        var owner = before.Values.FirstOrDefault(s => s.ParentId is not null
                            && SchematicNativeSheetChanges.Last(oldPaths[s.Id]) == symbol.Id.Value
                            && SchematicNativeSheetChanges.Parent(oldPaths[s.Id]) == recordPath);
                        if (owner is not null && after.TryGetValue(owner.Id, out var kept) && kept.ParentId is { } keptParent)
                            SetPath(record.Path, newPaths[keptParent]);
                    }
                    var ordered = placements.Records.OrderBy(r => PathKey(r.Path), StringComparer.Ordinal).ToArray();
                    placements.Records.Clear(); placements.Records.Add(ordered);
                }
                output[parent].Items.Add(Any.Pack(symbol));
            }
            // Reuse native ownership rules: removed components, cross-sheet units and
            // net endpoints must have exactly the same meaning in either direction.
            var projected = SchematicNativeRemovalProjection.Project(baseline, drawing, state.KnowledgeLibraries, token);
            gaps.AddRange(projected.CoverageGaps);
            if (projected.BindingCandidate is not { } nativeModel)
                return Failure(projected.ErrorCode ?? "xml_sheet_projection_failed", projected.ErrorMessage ?? "The requested hierarchy cannot preserve its component ownership.", projected.Issues);
            if (CircuitXml.Write(nativeModel.Engineering.Circuit) != CircuitXml.Write(desired.Engineering.Circuit))
                return Failure("xml_sheet_model_conflict", "A sheet move or removal must keep its surviving components, parts and nets unchanged, removing only owners and pins that leave with the sheet.");
            var keptBindings = nativeModel.SymbolBindings.ToHashSet();
            if (desired.SymbolBindings.Any(b => !baseline.SymbolBindings.Contains(b))
                || !desired.SymbolBindings.Where(b => keptBindings.Contains(b)).ToHashSet().SetEquals(keptBindings))
                return Failure("xml_sheet_binding_changed", "Keep the exact native bindings of surviving symbols when changing sheet ownership.");
            bool SameDrawing(SchematicHierarchyData a, SchematicHierarchyData b)
            {
                try { return a.Instances.Count == b.Instances.Count && SchematicHierarchyDelta.Plan(a, b, token).Count == 0; }
                catch (AutomationException) { return false; }
            }
            if (!SameDrawing(desired.Schematic, baseline.Schematic) && !SameDrawing(desired.Schematic, drawing))
                return Failure("xml_sheet_native_edits_conflict", "Change sheet ownership separately from other edits to the native XML drawing; both versions are preserved.");
            var engineering = ComponentReferenceRetention.Retain(desired.Engineering, nativeModel.Engineering.Circuit,
                projected.ComponentChanges, state.KnowledgeLibraries, projected.RetiredNets);
            var candidate = desired with { Engineering = engineering, Schematic = drawing,
                SheetBindings = nativeModel.SheetBindings, SymbolBindings = nativeModel.SymbolBindings };
            var bindings = SchematicDesignBindings.Inspect(candidate, state.KnowledgeLibraries, token);
            if (!bindings.IdentitiesResolved) return Failure("xml_sheet_binding_invalid", "The requested hierarchy has unresolved native owners.", bindings.Issues);
            var operations = SchematicHierarchyDelta.Plan(state.Observed, drawing, token);
            if (operations.Count == 0) return Failure("xml_sheet_nothing_to_apply", "The requested sheet change produced no native hierarchy operation.");
            var electrical = new SchematicNetReconciliationResult(engineering, [], projected.RetiredNets, [], projected.CoverageGaps,
                RemovedSymbolOccurrences: projected.RemovedOccurrences, ComponentChanges: projected.ComponentChanges)
            { RemovedSheetInstances = projected.RemovedSheetInstances, MovedSheetInstances = projected.MovedSheetInstances };
            return new(candidate, SchematicDesignXml.Write(candidate, state.KnowledgeLibraries), operations, hierarchy,
                electrical, null, [], [], null, gaps, true);
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
    }

    private static string PathKey(IEnumerable<KIID> path) => string.Join('/', path.Select(p => p.Value));
    private static void SetPath(RepeatedField<KIID> path, string value)
    { path.Clear(); path.Add(value.Split('/').Select(id => new KIID { Value = id })); }
}
