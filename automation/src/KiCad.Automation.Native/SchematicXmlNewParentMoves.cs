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
            var changes = SchematicNativeSheetChanges.Compare(state.Observed, desired.Schematic);
            if (changes.ErrorCode is not null) return Failure(changes.ErrorCode, changes.ErrorMessage!);
            var prepared = SchematicInsertedParents.Prepare(state.Baseline with { Schematic = state.Observed.Clone() },
                desired.Schematic, changes, token, desired);
            if (!prepared.Instances.ToHashSet().SetEquals(added))
                return Failure("xml_new_parent_bindings_required", "Declare unique new parent containers for the moved sheets and keep every existing sheet identity.");
            var result = SchematicXmlSheetChanges.Prepare(state, desired, hierarchy, token, prepared.Baseline);
            if (result is null) return Failure("xml_sheet_nothing_to_apply", "The new parent scaffold does not contain an existing sheet move.");
            return result.Electrical is null ? result : result with
                { Electrical = result.Electrical with { AddedSheetInstances = prepared.Instances } };
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
    }
}
