using Google.Protobuf;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>Validate actual native geometry against a previously checked move.
/// Never generate wire paths or accept unrelated native property changes.</summary>
internal static class SchematicLayoutResolution
{
    internal static SchematicDesign Resolve(SchematicDesign planned, SchematicElectricalState native,
        ApplySchematicItemBatch batch, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var actual = native.Hierarchy.Data;
        var candidate = planned with { Schematic = actual.Clone() };
        var bindings = SchematicDesignBindings.Inspect(candidate, libraries, token);
        if (!bindings.IdentitiesResolved || bindings.Differences.Count != 0)
            throw Error("Native symbol properties or identities differ from the requested design.");
        var symbols = SchematicModelProjection.NativeSymbols(candidate, actual);
        foreach (var occurrence in planned.Engineering.Circuit.Symbols.Where(s => s.Placement is not null))
            if (!SchematicOrientation.Equivalent(SchematicModelProjection.Placement(symbols[occurrence.Id]), occurrence.Placement))
                throw Error("Native connected movement did not reach the exact requested placement.");

        var wanted = planned.Schematic.Instances.ToDictionary(s => Path(s.Metadata.Document));
        var observed = actual.Instances.ToDictionary(s => Path(s.Metadata.Document));
        if (!wanted.Keys.ToHashSet().SetEquals(observed.Keys)) throw Error("Connected movement cannot change sheet ownership.");
        var moves = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        var transforms = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        var mirrors = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var operation in batch.Operations.Where(o => o.MoveConnectedSymbols is not null || o.TransformConnectedSymbols is not null))
        {
            string path = Path(operation.TargetDocument ?? batch.Document);
            if (!wanted.TryGetValue(path, out var screen)) throw Error("The connected move has no exact sheet target.");
            string physical = screen.Metadata.ScreenId.Value;
            if (!moves.TryGetValue(physical, out var ids)) moves.Add(physical, ids = []);
            var affected = (operation.MoveConnectedSymbols?.Symbols ?? operation.TransformConnectedSymbols.Symbols)
                .Select(s => Guid.Parse(s.Value)).ToArray();
            ids.UnionWith(affected);
            if (operation.TransformConnectedSymbols is { } transform)
            {
                if (!transforms.TryGetValue(physical, out var transformed)) transforms.Add(physical, transformed = []);
                transformed.UnionWith(affected);
                if (transform.Kind is SchematicConnectedTransformKind.SctMirrorLeftRight or SchematicConnectedTransformKind.SctMirrorUpDown)
                {
                    if (!mirrors.TryGetValue(physical, out var mirrored)) mirrors.Add(physical, mirrored = []);
                    mirrored.UnionWith(affected);
                }
            }
        }
        if (moves.Count == 0) throw Error("No connected movement was recorded.");
        foreach (var (path, before) in wanted)
        {
            token.ThrowIfCancellationRequested();
            var after = observed[path];
            var a = before.Clone(); a.Items.Clear();
            var b = after.Clone(); b.Items.Clear();
            if (!a.Equals(b)) throw Error("Connected movement changed sheet metadata or cached library definitions.");
            var oldItems = SchematicItemDelta.Index(before.Items);
            var newItems = SchematicItemDelta.Index(after.Items);
            bool moving = moves.TryGetValue(before.Metadata.ScreenId.Value, out var movedIds);
            bool transforming = transforms.TryGetValue(before.Metadata.ScreenId.Value, out var transformedIds);
            mirrors.TryGetValue(before.Metadata.ScreenId.Value, out var mirroredIds);
            foreach (Guid id in oldItems.Keys.Union(newItems.Keys))
            {
                oldItems.TryGetValue(id, out var oldItem); newItems.TryGetValue(id, out var newItem);
                if (Equals(oldItem, newItem)) continue;
                if (!moving || Locked(oldItem) || Locked(newItem)) throw Error("Connected movement changed an unrelated sheet or locked object.");
                if (oldItem is null || newItem is null)
                {
                    if (!WireGeometry(oldItem ?? newItem!)) throw Error("Connected movement created or removed a non-wire object.");
                    continue;
                }
                if (oldItem.Descriptor != newItem.Descriptor) throw Error("A native object changed its type.");
                bool transformed = transformedIds?.Contains(id) == true, mirrored = mirroredIds?.Contains(id) == true;
                if (mirrored && newItem is SchematicSymbolInstance { FieldsAutoplaced: true })
                    throw Error("A mirrored symbol must retain native manual field placement.");
                var oldProperties = WithoutGeometry(oldItem, movedIds!.Contains(id), transformed, mirrored, transforming);
                var newProperties = WithoutGeometry(newItem, movedIds.Contains(id), transformed, mirrored, transforming);
                if (!oldProperties.Equals(newProperties)) throw Error("Connected movement changed non-layout properties or unrelated symbol placement.");
            }
        }
        var connectivity = SchematicElectricalComparison.Compare(candidate, native, libraries, token);
        if (!connectivity.PinBindingsComplete || !connectivity.ConnectivityEquivalent)
            throw new AutomationException("native_sync_connectivity_mismatch", "The moved native pins do not match the requested electrical connections.");
        return candidate;
    }

    private static string Path(DocumentSpecifier document) => string.Join('/', document.SheetPath.Path.Select(p => p.Value));
    private static bool Locked(IMessage? item) => item is not null
        && item.Descriptor.FindFieldByName("locked")?.Accessor.GetValue(item) is LockedState.LsLocked;
    private static bool WireGeometry(IMessage item) => item is Junction
        || item is SchematicLine { Type: SchematicLineType.SltWire or SchematicLineType.SltBus };
    private static IMessage WithoutGeometry(IMessage input, bool movedSymbol, bool transformedSymbol, bool mirroredSymbol, bool transformedScreen)
    {
        var item = input.Descriptor.Parser.ParseFrom(input.ToByteArray());
        switch (item)
        {
            case SchematicLine line when line.Type is SchematicLineType.SltWire or SchematicLineType.SltBus:
                line.Start = null; line.End = null; break;
            case Junction junction: junction.Position = null; break;
            case BusEntry entry: entry.Position = null; if (transformedScreen) entry.Size = null; break;
            case NoConnectMarker marker: marker.Position = null; break;
            case LocalLabel label:
                label.Position = null; if (transformedScreen) label.SpinStyle = 0;
                Text(label.Text, transformedScreen); Fields(label.Fields, transformedScreen); break;
            case GlobalLabel label:
                label.Position = null; if (transformedScreen) label.SpinStyle = 0;
                Text(label.Text, transformedScreen); Fields(label.Fields, transformedScreen); Field(label.IntersheetRefsField, transformedScreen); break;
            case HierarchicalLabel label:
                label.Position = null; if (transformedScreen) label.SpinStyle = 0;
                Text(label.Text, transformedScreen); Fields(label.Fields, transformedScreen); break;
            case DirectiveLabel label:
                label.Position = null; if (transformedScreen) label.SpinStyle = 0;
                Text(label.Text, transformedScreen); Fields(label.Fields, transformedScreen); break;
            case SchematicSymbolInstance symbol when movedSymbol:
                symbol.Position = null;
                if (transformedSymbol) symbol.Transform = null;
                if (mirroredSymbol) symbol.FieldsAutoplaced = false;
                Field(symbol.ReferenceField); Field(symbol.ValueField); Field(symbol.FootprintField);
                Field(symbol.DatasheetField); Field(symbol.DescriptionField); Fields(symbol.UserFields); break;
        }
        return item;
    }
    private static void Text(Kiapi.Common.Types.Text? text, bool transformed = false)
    {
        if (text is null) return;
        text.Position = null;
        if (transformed && text.Attributes is { } attributes)
        {
            attributes.Angle = null; attributes.HorizontalAlignment = 0; attributes.VerticalAlignment = 0;
        }
    }
    private static void Field(SchematicField? field, bool transformed = false) { if (field is not null) Text(field.Text, transformed); }
    private static void Fields(IEnumerable<SchematicField> fields, bool transformed = false) { foreach (var field in fields) Field(field, transformed); }
    private static AutomationException Error(string message) => new("native_layout_projection_mismatch", message);
}
