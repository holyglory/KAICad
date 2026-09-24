using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

// CN-1 §9.4: after KiCad has committed a connection realization and proved its pin partition, check that what it
// actually holds is exactly what was planned before the XML may be published. Lane 2A created this file under the
// CN-1 integration grant (decision n2c2ef8777f8ace77).

/// <summary>Resolves a committed connection realization against its planned design (cn1-wiring-intent.md §9.4). The
/// editor may fill in only presentation details the plan leaves to it, listed in <see cref="AdoptedNativeValues"/>;
/// every other difference is refused with <c>realization_resolution_mismatch</c>, and the pending state is kept.</summary>
public static class SchematicConnectionResolution
{
    /// <summary>The only values taken from the editor instead of the plan (§9.5). Lane 2C freezes this list from the
    /// first native journey run; changing it is a contract amendment.</summary>
    public static readonly IReadOnlyList<string> AdoptedNativeValues =
    [
        "SchematicLine.stroke", "SchematicLine.start_ending", "SchematicLine.end_ending",
        "Junction.diameter", "Junction.color",
        "LocalLabel.text (except text_)", "LocalLabel.fields", "LocalLabel.fields_autoplaced",
        "GlobalLabel.text (except text_)", "GlobalLabel.fields", "GlobalLabel.fields_autoplaced", "GlobalLabel.intersheet_refs_field",
        "HierarchicalLabel.text (except text_)", "HierarchicalLabel.fields", "HierarchicalLabel.fields_autoplaced",
        "SheetPin.text (except text_)"
    ];

    /// <summary>Return <paramref name="planned"/> with the editor's committed schematic, after proving that the editor
    /// holds exactly the planned objects: bindings and sheet paths unchanged, screen metadata and library caches as
    /// planned, every pre-existing item as <paramref name="before"/> (sheet symbols compared without their new sheet
    /// pins), created symbols exactly as planned, and generated items with the planned type, geometry and identity.
    /// The resolved design must then be completely and electrically equivalent to the editor.</summary>
    public static SchematicDesign Resolve(SchematicDesign planned, SchematicHierarchyData before, SchematicElectricalState native,
        ApplySchematicItemBatch batch, SchematicItemBatchResult? result, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(libraries);
        token.ThrowIfCancellationRequested();
        if (result?.ConnectivityAssertionVerified != true)
            throw new AutomationException(SchematicConnectionErrors.RealizationAssertionUnverified,
                "KiCad committed the connections without confirming their pin partition; the pending operation is kept for inspection.");
        var committed = native.Hierarchy?.Data ?? throw Mismatch("KiCad returned no committed schematic.");
        var createdSymbols = new HashSet<Guid>();
        var generatedItems = new HashSet<Guid>();
        foreach (var operation in batch.Operations)
        {
            if (operation.Create is not { } create) continue;
            var id = CreatedId(create);
            if (create.Is(SchematicSymbolInstance.Descriptor)) createdSymbols.Add(id); else generatedItems.Add(id);
        }
        string Key(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(p => p.Value));
        var plannedScreens = planned.Schematic.Instances.ToDictionary(Key, StringComparer.Ordinal);
        var nativeScreens = committed.Instances.ToDictionary(Key, StringComparer.Ordinal);
        var beforeScreens = before.Instances.ToDictionary(Key, StringComparer.Ordinal);
        if (!Equals(planned.Schematic.Document, committed.Document) || !plannedScreens.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(nativeScreens.Keys)
            || !plannedScreens.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(beforeScreens.Keys))
            throw Mismatch("KiCad's sheet instances differ from the planned sheet instances.");
        foreach (var (path, plannedScreen) in plannedScreens.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var nativeScreen = nativeScreens[path];
            if (!Equals(plannedScreen.Metadata, nativeScreen.Metadata))
                throw Mismatch("KiCad's settings of sheet " + path + " differ from the plan.");
            if (!plannedScreen.CachedSymbols.OrderBy(c => c.CacheKey, StringComparer.Ordinal)
                    .SequenceEqual(nativeScreen.CachedSymbols.OrderBy(c => c.CacheKey, StringComparer.Ordinal))
                || !plannedScreen.UnrepresentedItems.SequenceEqual(nativeScreen.UnrepresentedItems))
                throw Mismatch("KiCad's symbol library cache of sheet " + path + " differs from the plan.");
            var plannedItems = SchematicItemDelta.Index(plannedScreen.Items);
            var nativeItems = SchematicItemDelta.Index(nativeScreen.Items);
            var beforeItems = SchematicItemDelta.Index(beforeScreens[path].Items);
            if (!plannedItems.Keys.ToHashSet().SetEquals(nativeItems.Keys))
                throw Mismatch("KiCad holds different items on sheet " + path + " than planned: "
                    + string.Join(", ", plannedItems.Keys.Except(nativeItems.Keys).Select(id => "missing " + id.ToString("D"))
                        .Concat(nativeItems.Keys.Except(plannedItems.Keys).Select(id => "unexpected " + id.ToString("D"))).Take(4)) + ".");
            foreach (var (id, plannedItem) in plannedItems.OrderBy(p => p.Key))
            {
                var nativeItem = nativeItems[id];
                if (plannedItem.Descriptor != nativeItem.Descriptor)
                    throw Mismatch("Item " + id.ToString("D") + " on sheet " + path + " has another type in KiCad than planned.");
                if (generatedItems.Contains(id))
                {
                    if (beforeItems.ContainsKey(id)) throw Mismatch("Generated item " + id.ToString("D") + " already existed before the realization.");
                    if (!Adopted(nativeItem, plannedItem).Equals(plannedItem))
                        throw Mismatch("KiCad drew generated item " + id.ToString("D") + " on sheet " + path + " differently than planned.");
                }
                else if (createdSymbols.Contains(id))
                {
                    if (!nativeItem.Equals(plannedItem))
                        throw Mismatch("KiCad created symbol " + id.ToString("D") + " on sheet " + path + " differently than planned.");
                }
                else if (!beforeItems.TryGetValue(id, out var beforeItem))
                    throw Mismatch("Item " + id.ToString("D") + " on sheet " + path + " was neither there before nor created by the realization.");
                else if (nativeItem is SheetSymbol nativeSheet && plannedItem is SheetSymbol plannedSheet && beforeItem is SheetSymbol beforeSheet)
                    RequireSheet(path, nativeSheet, plannedSheet, beforeSheet);
                else if (!nativeItem.Equals(beforeItem))
                    throw Mismatch("KiCad changed existing item " + id.ToString("D") + " on sheet " + path + ", which realization must never touch.");
            }
        }
        var candidate = planned with { Schematic = committed.Clone() };
        var bindings = SchematicDesignBindings.Inspect(candidate, libraries, token);
        if (!bindings.IdentitiesResolved || bindings.Differences.Count != 0)
            throw Mismatch("KiCad's symbols no longer resolve to their model components with the planned references, values and units.");
        var comparison = SchematicElectricalComparison.Compare(candidate, native, libraries, token);
        if (!comparison.PinBindingsComplete || !comparison.ConnectivityEquivalent)
            throw Mismatch("KiCad's committed pin connections do not match the planned connections.");
        return candidate;
    }

    // An existing sheet symbol may only gain the planned sheet pins; its other pins are compared as a set by identity.
    private static void RequireSheet(string path, SheetSymbol native, SheetSymbol planned, SheetSymbol before)
    {
        var existing = before.Pins.Select(p => p.Id.Value).ToHashSet(StringComparer.Ordinal);
        var added = planned.Pins.Where(p => !existing.Contains(p.Id.Value)).ToDictionary(p => p.Id.Value, StringComparer.Ordinal);
        if (!native.Pins.Select(p => p.Id.Value).ToHashSet(StringComparer.Ordinal).SetEquals(planned.Pins.Select(p => p.Id.Value)))
            throw Mismatch("Sheet symbol " + native.Id.Value + " on sheet " + path + " has different sheet pins in KiCad than planned.");
        foreach (var pin in native.Pins.Where(p => added.ContainsKey(p.Id.Value)))
            if (!Adopted(pin, added[pin.Id.Value]).Equals(added[pin.Id.Value]))
                throw Mismatch("KiCad drew generated sheet pin " + pin.Id.Value + " on sheet " + path + " differently than planned.");
        static SheetSymbol WithoutAdded(SheetSymbol sheet, IReadOnlyDictionary<string, SheetPin> added)
        {
            var copy = sheet.Clone();
            var kept = sheet.Pins.Where(p => !added.ContainsKey(p.Id.Value)).OrderBy(p => p.Id.Value, StringComparer.Ordinal).Select(p => p.Clone()).ToArray();
            copy.Pins.Clear();
            copy.Pins.Add(kept);
            return copy;
        }
        if (!WithoutAdded(native, added).Equals(WithoutAdded(before, added)))
            throw Mismatch("KiCad changed existing sheet symbol " + native.Id.Value + " on sheet " + path + " beyond adding its planned sheet pins.");
    }

    // The native item with only the adopted values (AdoptedNativeValues) replaced by the planned ones, so equality with
    // the planned item proves every other field is exactly as planned.
    private static IMessage Adopted(IMessage native, IMessage planned)
    {
        switch (native, planned)
        {
            case (SchematicLine n, SchematicLine p):
            {
                var copy = n.Clone();
                copy.Stroke = p.Stroke?.Clone(); copy.StartEnding = p.StartEnding?.Clone(); copy.EndEnding = p.EndEnding?.Clone();
                return copy;
            }
            case (Junction n, Junction p):
            {
                var copy = n.Clone();
                copy.Diameter = p.Diameter?.Clone(); copy.Color = p.Color?.Clone();
                return copy;
            }
            case (LocalLabel n, LocalLabel p):
            {
                var copy = n.Clone();
                copy.Text = Text(n.Text, p.Text); copy.FieldsAutoplaced = p.FieldsAutoplaced;
                copy.Fields.Clear(); copy.Fields.Add(p.Fields.Select(f => f.Clone()));
                return copy;
            }
            case (GlobalLabel n, GlobalLabel p):
            {
                var copy = n.Clone();
                copy.Text = Text(n.Text, p.Text); copy.FieldsAutoplaced = p.FieldsAutoplaced;
                copy.Fields.Clear(); copy.Fields.Add(p.Fields.Select(f => f.Clone()));
                copy.IntersheetRefsField = p.IntersheetRefsField?.Clone();
                return copy;
            }
            case (HierarchicalLabel n, HierarchicalLabel p):
            {
                var copy = n.Clone();
                copy.Text = Text(n.Text, p.Text); copy.FieldsAutoplaced = p.FieldsAutoplaced;
                copy.Fields.Clear(); copy.Fields.Add(p.Fields.Select(f => f.Clone()));
                return copy;
            }
            case (SheetPin n, SheetPin p):
            {
                var copy = n.Clone();
                copy.Text = Text(n.Text, p.Text);
                return copy;
            }
            default:
                return native;
        }

        // Every text attribute from the plan, but the text itself from KiCad.
        static Kiapi.Common.Types.Text? Text(Kiapi.Common.Types.Text? native, Kiapi.Common.Types.Text? planned)
        {
            if (planned is null) return native is null ? null : new() { Text_ = native.Text_ };
            var copy = planned.Clone();
            copy.Text_ = native?.Text_ ?? "";
            return copy;
        }
    }

    private static Guid CreatedId(Any create)
    {
        var item = SchematicItemDelta.Index([create]).Single();
        return item.Key;
    }

    private static AutomationException Mismatch(string message) =>
        new(SchematicConnectionErrors.RealizationResolutionMismatch, message + " The pending operation is kept for inspection; no XML is published.");
}
