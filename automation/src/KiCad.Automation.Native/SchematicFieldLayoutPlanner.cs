using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public enum SchematicFieldSlot { Reference, Value, Footprint, Datasheet, Description, User }
public sealed record SchematicFieldAddress(Guid SymbolOccurrenceId, SchematicFieldSlot Slot,
    string ExpectedName, int? UserIndex = null);
public sealed record SchematicFieldPlacement(SchematicFieldAddress Field, long XNm, long YNm,
    int RotationDegrees, HorizontalAlignment HorizontalAlignment, VerticalAlignment VerticalAlignment);
public sealed record SchematicFieldLayoutResult(SchematicDesign Candidate, string DesiredXml,
    IReadOnlyList<SchematicItemOperation> Operations, IReadOnlyList<Guid> AffectedSymbols)
{
    public bool RequiresNativeConnectivityValidation => true;
    public bool RequiresRenderedReview => true;
}

/// <summary>Exact field geometry only. No text/visibility/circuit changes, native
/// mutation or persistence. Native fields have no persistent UUID: their slot
/// and expected name are guarded by the owning symbol and recovery snapshot.</summary>
public static class SchematicFieldLayoutPlanner
{
    public static SchematicFieldLayoutResult Propose(DesignRecoveryState state,
        IReadOnlyList<SchematicFieldPlacement> changes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _ = SchematicElectricalCheckpoints.Require(state);
        var before = SchematicSynchronizationPlanner.Plan(state, token);
        if (!before.CanPrepare || before.NativeOperations.Count != 0 || before.NativeConnectivityValidationRequired)
            throw Error("field_layout_requires_synchronized_design", "Resolve pending native/model edits before proposing field layout.");
        var original = before.Candidate!;
        if (changes.Count == 0) throw Error("empty_field_layout", "Select at least one field to arrange.");
        var result = original with { Schematic = original.Schematic.Clone() };
        var occurrences = original.Engineering.Circuit.Symbols.ToDictionary(s => s.Id);
        var components = original.Engineering.Circuit.Components.ToDictionary(c => c.Id);
        var bindings = original.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId);
        var paths = original.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var screens = result.Schematic.Instances.ToDictionary(s => Path(s.Metadata.Document), StringComparer.Ordinal);
        var edits = new Dictionary<(Guid Screen, Guid Symbol, SchematicFieldSlot Slot, int? Index), SchematicFieldPlacement>();
        var selected = new HashSet<SchematicFieldAddress>();
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            if (!selected.Add(change.Field) || !System.Enum.IsDefined(change.Field.Slot)
                || !occurrences.TryGetValue(change.Field.SymbolOccurrenceId, out var occurrence)
                || !bindings.TryGetValue(occurrence.Id, out Guid native))
                throw Error("invalid_field_address", "Select each exact bound symbol field only once.");
            if ((change.Field.Slot == SchematicFieldSlot.User) != (change.Field.UserIndex is not null)
                || change.Field.UserIndex is < 0 || change.Field.ExpectedName is null)
                throw Error("invalid_field_address", "Custom fields require an exact index and expected name; standard fields use their defined slot.");
            if (change.RotationDegrees is not (0 or 90 or 180 or 270)
                || change.HorizontalAlignment is not (HorizontalAlignment.HaLeft or HorizontalAlignment.HaCenter or HorizontalAlignment.HaRight)
                || change.VerticalAlignment is not (VerticalAlignment.VaTop or VerticalAlignment.VaCenter or VerticalAlignment.VaBottom))
                throw Error("invalid_field_geometry", "Use cardinal text rotation and concrete native text alignment.");
            foreach (long value in new[] { change.XNm, change.YNm })
                if (value % 100 != 0 || value / 100 < int.MinValue || value / 100 > int.MaxValue)
                    throw Error("invalid_field_geometry", "Field positions must fit native coordinates at 100 nm precision.");
            var screen = screens[paths[occurrence.EffectiveSheetInstanceId(components[occurrence.ComponentId])]];
            var symbol = NativeSymbol(screen, native);
            if (symbol.Locked == LockedState.LsLocked || LockedByGroup(screen, native))
                throw Error("locked_field_owner", "Preserve a locked symbol or containing group's field layout.");
            _ = Field(symbol, change.Field);
            var key = (Guid.Parse(screen.Metadata.ScreenId.Value), native, change.Field.Slot, change.Field.UserIndex);
            if (edits.TryGetValue(key, out var previous))
            {
                if (previous with { Field = change.Field } != change)
                    throw Error("shared_field_layout_conflict", "Repeated instances requested different geometry for one physical field.");
            }
            else edits.Add(key, change);
        }
        var affected = new HashSet<Guid>();
        foreach (var screen in result.Schematic.Instances)
        {
            Guid physical = Guid.Parse(screen.Metadata.ScreenId.Value);
            foreach (var group in edits.Where(e => e.Key.Screen == physical).GroupBy(e => e.Key.Symbol))
            {
                token.ThrowIfCancellationRequested();
                var symbol = NativeSymbol(screen, group.Key);
                bool changed = false;
                foreach (var edit in group)
                {
                    var field = Field(symbol, edit.Value.Field); var wanted = edit.Value;
                    var text = field.Text;
                    if (text.Position.XNm == wanted.XNm && text.Position.YNm == wanted.YNm
                        && (text.Attributes.Angle?.ValueDegrees ?? 0) == wanted.RotationDegrees
                        && text.Attributes.HorizontalAlignment == wanted.HorizontalAlignment
                        && text.Attributes.VerticalAlignment == wanted.VerticalAlignment) continue;
                    text.Position = new() { XNm = wanted.XNm, YNm = wanted.YNm };
                    text.Attributes.Angle = new() { ValueDegrees = wanted.RotationDegrees };
                    text.Attributes.HorizontalAlignment = wanted.HorizontalAlignment;
                    text.Attributes.VerticalAlignment = wanted.VerticalAlignment;
                    changed = true;
                }
                if (!changed) continue;
                symbol.FieldsAutoplaced = false;
                int index = screen.Items.ToList().FindIndex(i => i.Is(SchematicSymbolInstance.Descriptor)
                    && i.Unpack<SchematicSymbolInstance>().Id.Value == group.Key.ToString("D"));
                screen.Items[index] = Any.Pack(symbol);
                affected.UnionWith(bindings.Where(b => b.Value == group.Key).Select(b => b.Key));
            }
        }
        var operations = SchematicHierarchyDelta.Plan(original.Schematic, result.Schematic, token);
        token.ThrowIfCancellationRequested();
        return new(result, SchematicDesignXml.Write(result, state.KnowledgeLibraries), operations, affected.Order().ToArray());
    }

    private static SchematicSymbolInstance NativeSymbol(SchematicScreenData screen, Guid id) => screen.Items
        .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
        .Single(s => s.Id.Value == id.ToString("D"));

    private static SchematicField Field(SchematicSymbolInstance symbol, SchematicFieldAddress address)
    {
        var field = address.Slot switch
        {
            SchematicFieldSlot.Reference => symbol.ReferenceField,
            SchematicFieldSlot.Value => symbol.ValueField,
            SchematicFieldSlot.Footprint => symbol.FootprintField,
            SchematicFieldSlot.Datasheet => symbol.DatasheetField,
            SchematicFieldSlot.Description => symbol.DescriptionField,
            SchematicFieldSlot.User when address.UserIndex is { } i && i >= 0 && i < symbol.UserFields.Count => symbol.UserFields[i],
            _ => null
        };
        if (field?.Text?.Position is null || field.Text.Attributes is null || field.Name != address.ExpectedName)
            throw Error("field_address_changed", "The selected field is absent or its exact name no longer matches the snapshot.");
        return field;
    }

    private static bool LockedByGroup(SchematicScreenData screen, Guid id)
    {
        var ancestors = new HashSet<Guid> { id };
        var groups = screen.Items.Where(i => i.Is(Group.Descriptor)).Select(i => i.Unpack<Group>()).ToArray();
        bool more;
        do
        {
            more = false;
            foreach (var group in groups.Where(g => g.Items.Any(item => ancestors.Contains(Guid.Parse(item.Value)))))
            {
                if (group.Locked == LockedState.LsLocked) return true;
                more |= ancestors.Add(Guid.Parse(group.Id.Value));
            }
        } while (more);
        return false;
    }

    private static string Path(DocumentSpecifier document) => string.Join('/', document.SheetPath.Path.Select(p => p.Value));
    private static AutomationException Error(string code, string message) => new(code, message);
}
