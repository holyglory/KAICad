using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal sealed record InsertedParentProjection(SchematicDesign Baseline, IReadOnlySet<string> Paths,
    IReadOnlyList<Guid> Instances);

/// <summary>A private comparison view of new containers, not a native or electrical checkpoint.</summary>
internal static class SchematicInsertedParents
{
    internal static InsertedParentProjection Prepare(SchematicDesign baseline, SchematicHierarchyData observed,
        SchematicNativeSheetChanges changes, CancellationToken token, SchematicDesign? declared = null)
    {
        var needed = changes.Inserted.Where(parent => declared is not null || changes.Moved.Values.Any(path =>
            path.StartsWith(parent + "/", StringComparison.Ordinal))).ToHashSet(StringComparer.Ordinal);
        if (needed.Count == 0) return new(baseline, needed, []);
        var circuit = baseline.Engineering.Circuit;
        var originalScreens = baseline.Schematic.Instances.Select(s => s.Metadata.ScreenId.Value).ToHashSet(StringComparer.Ordinal);
        var observedScreens = observed.Instances.ToDictionary(SchematicNativeSheetChanges.Key, StringComparer.Ordinal);
        var identities = changes.Rebind(baseline.SheetBindings).Where(p => p.Value is not null)
            .ToDictionary(p => p.Value!, p => p.Key, StringComparer.Ordinal);
        var paths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => b.NativePath);
        var drawing = baseline.Schematic.Clone();
        var screens = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => drawing.Instances.Single(s =>
            SchematicNativeSheetChanges.Key(s) == SchematicDesignBindings.PathKey(b.NativePath)));
        var definitions = circuit.Sheets.ToList(); var instances = circuit.SheetInstances.ToList();
        var bindings = baseline.SheetBindings.ToList(); var added = new List<Guid>();
        foreach (string path in needed.OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var source = observedScreens[path];
            if (originalScreens.Contains(source.Metadata.ScreenId.Value)
                || observed.Instances.Count(s => s.Metadata.ScreenId.Equals(source.Metadata.ScreenId)) != 1)
                throw new AutomationException(SchematicNativeAdditionProjection.MoveIntoNewSheet,
                    "A newly shared parent needs explicit instance relocation choices before moving an existing child into it.");
            string parent = SchematicNativeSheetChanges.Parent(path);
            if (!identities.TryGetValue(parent, out Guid parentId))
                throw new AutomationException("native_sheet_incomplete", "The new parent has no resolved containing sheet.");
            var sheet = SchematicNativeSheetChanges.SheetSymbolOf(observed, path)?.Clone()
                ?? throw new AutomationException("native_sheet_incomplete", "The new parent has no observed sheet symbol.");
            var nativePath = path.Split('/').Select(Guid.Parse).ToArray();
            Guid nativeId = nativePath[^1];
            Guid id = SchematicNativeAdditionProjection.AdoptedIdentity("sheet-instance", circuit.Id, nativePath[..^1], nativeId);
            Guid definitionId = SchematicNativeAdditionProjection.AdoptedIdentity("sheet-definition", circuit.Id, nativePath[..^1], nativeId);
            var definition = new SheetDefinition(definitionId, sheet.NameField.Text.Text_, []);
            if (declared is not null)
            {
                var choice = declared.SheetBindings.SingleOrDefault(b => b.NativePath.SequenceEqual(nativePath))
                    ?? throw new AutomationException("xml_sheet_binding_changed", "Declare the exact path of each new parent.");
                var selected = declared.Engineering.Circuit.SheetInstances.Single(s => s.Id == choice.SheetInstanceId);
                definition = declared.Engineering.Circuit.Sheets.Single(s => s.Id == selected.DefinitionId);
                if (circuit.SheetInstances.Any(s => s.Id == selected.Id) || definitions.Any(s => s.Id == definition.Id)
                    || definition.Components.Count != 0 || selected.ParentId != parentId)
                    throw new AutomationException("xml_sheet_identity_changed", "Each new parent must declare its own empty definition and exact containing sheet.");
                id = selected.Id;
            }
            var syntheticPath = paths[parentId].Append(nativeId).ToArray();
            sheet.Path = new(); sheet.Path.Path.Add(paths[parentId].Select(p => new KIID { Value = p.ToString("D") }));
            if (sheet.InstanceRecords is { } records)
                foreach (var record in records.Records)
                    if (record.Path.Select(p => p.Value).SequenceEqual(parent.Split('/')))
                    { record.Path.Clear(); record.Path.Add(sheet.Path.Path.Select(p => p.Clone())); }
            screens[parentId].Items.Add(Any.Pack(sheet));
            var empty = source.Clone();
            for (int index = empty.Items.Count - 1; index >= 0; --index)
                if (empty.Items[index].Is(SheetSymbol.Descriptor) || empty.Items[index].Is(SchematicSymbolInstance.Descriptor))
                    empty.Items.RemoveAt(index);
            empty.Metadata.Document.SheetPath.Path.Clear();
            empty.Metadata.Document.SheetPath.Path.Add(syntheticPath.Select(p => new KIID { Value = p.ToString("D") }));
            drawing.Instances.Add(empty); screens.Add(id, empty); paths.Add(id, syntheticPath); identities.Add(path, id);
            definitions.Add(definition); instances.Add(new(id, definition.Id, parentId)); bindings.Add(new(id, syntheticPath)); added.Add(id);
        }
        return new(baseline with { Schematic = drawing, SheetBindings = bindings,
            Engineering = baseline.Engineering with { Circuit = circuit with { Sheets = definitions, SheetInstances = instances } } }, needed, added);
    }
}
