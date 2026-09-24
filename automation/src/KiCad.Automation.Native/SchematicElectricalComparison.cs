using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public sealed record ElectricalBindingIssue(string Code, string? NativePath, string? NativeId, Guid? ComponentId = null);
public sealed record ElectricalConnectivityDifference(string Kind, IReadOnlyList<Guid> ModelNetIds,
    IReadOnlyList<int> SnapshotNetIndexes, IReadOnlyList<PinEndpoint> Pins);
public sealed record NativePinPartition(int? SnapshotNetIndex, string? NativeName, IReadOnlyList<PinEndpoint> Pins);
public sealed record UndrawnComponentPin(PinEndpoint Pin, int Unit, IReadOnlyList<string> LibraryPinIds);
/// <param name="StackedPins">The groups of model pins that one placed symbol's own definition draws at one point, which
/// KiCad always joins (decision kicad-stacked-pins-one-node-20260924), merged where they share a model pin; each is one
/// node of the comparison.</param>
public sealed record SchematicElectricalComparisonResult(bool PinBindingsComplete, bool ConnectivityEquivalent,
    IReadOnlyList<ElectricalBindingIssue> Issues, IReadOnlyList<ElectricalConnectivityDifference> Differences,
    IReadOnlyList<HierarchyCoverageGap> CoverageGaps, string? ErrorCode = null, string? ErrorMessage = null,
    IReadOnlyList<NativePinPartition>? PinPartitions = null, IReadOnlyList<UndrawnComponentPin>? UndrawnPins = null,
    IReadOnlyList<IReadOnlyList<PinEndpoint>>? StackedPins = null);

/// <summary>Compare pin partitions through exact sheet/symbol/placed-pin bindings.
/// Snapshot indexes are ephemeral; this never transfers net requirement identities.</summary>
public static class SchematicElectricalComparison
{
    public static SchematicElectricalComparisonResult Compare(SchematicDesign design,
        SchematicElectricalState observed, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (observed.Hierarchy?.Data is null || observed.Hierarchy.Revision is null
            || string.IsNullOrWhiteSpace(observed.Hierarchy.Revision.Epoch)
            || !Equals(design.Schematic.Document, observed.Hierarchy.Data.Document))
            throw new AutomationException("invalid_electrical_snapshot", "Require a revision-bearing snapshot of the exact bound schematic root.");
        var report = SchematicDesignBindings.Inspect(design with { Schematic = observed.Hierarchy.Data }, libraries, token);
        var issues = report.Issues.Select(i => new ElectricalBindingIssue(i.Code, i.NativePath, i.NativeObjectId?.ToString("D"), i.ModelId)).ToList();
        issues.AddRange(report.Differences.Where(d => d.Field == "unit").Select(d =>
            new ElectricalBindingIssue("native_unit_binding_changed", null, null, d.SymbolOccurrenceId)));
        if (issues.Count > 0) return new(false, false, issues, [], report.CoverageGaps);

        var circuit = design.Engineering.Circuit;
        var components = circuit.Components.ToDictionary(c => c.Id);
        var definitions = circuit.Sheets.SelectMany(s => s.Components).ToDictionary(d => d.Id);
        var parts = circuit.Parts.ToDictionary(p => p.Id);
        var sheets = design.SheetBindings.ToDictionary(s => s.SheetInstanceId, s => SchematicDesignBindings.PathKey(s.NativePath));
        var symbols = design.SymbolBindings.ToDictionary(s => s.SymbolOccurrenceId, s => s.NativeObjectId.ToString("D"));
        var screens = observed.Hierarchy.Data.Instances.ToDictionary(s => Path(s.Metadata.Document.SheetPath), StringComparer.Ordinal);
        var nativePins = new Dictionary<(string Path, string Id), PinEndpoint>();
        var knownItems = new HashSet<(string Path, string Id)>();
        foreach (var (path, screen) in screens)
        foreach (var packed in screen.Items)
        {
            var descriptor = SchematicText.Descriptor.File.MessageTypes.SingleOrDefault(d => packed.Is(d));
            if (descriptor is null) { issues.Add(new("unknown_snapshot_item_type", path, packed.TypeUrl)); continue; }
            var item = descriptor.Parser.ParseFrom(packed.Value);
            var field = descriptor.FindFieldByName("id");
            if (field?.FieldType == Google.Protobuf.Reflection.FieldType.Message
                && field.MessageType == Kiapi.Common.Types.KIID.Descriptor
                && field.Accessor.GetValue(item) is Kiapi.Common.Types.KIID id)
            {
                if (!Id(id.Value) || !knownItems.Add((path, id.Value))) issues.Add(new("ambiguous_snapshot_item", path, id.Value));
            }
            if (item is SheetSymbol sheet)
                foreach (var pin in sheet.Pins)
                    if (!Id(pin.Id?.Value) || !knownItems.Add((path, pin.Id!.Value)))
                        issues.Add(new("ambiguous_sheet_pin", path, pin.Id?.Value));
        }
        var modelPins = new Dictionary<PinEndpoint, List<(string Path, string Id)>>();
        var componentSymbols = new Dictionary<Guid, List<SchematicSymbolInstance>>();
        // Pins that one placed symbol's own definition draws at one point, by exact definition geometry.
        var stacked = new List<PinEndpoint[]>();
        foreach (var occurrence in circuit.Symbols)
        {
            token.ThrowIfCancellationRequested();
            var component = components[occurrence.ComponentId]; string path = sheets[occurrence.EffectiveSheetInstanceId(component)];
            var symbol = screens[path].Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == symbols[occurrence.Id]);
            if (!componentSymbols.TryGetValue(component.Id, out var owned)) componentSymbols.Add(component.Id, owned = []);
            owned.Add(symbol);
            if (!symbol.SeparatePinIdentities || symbol.Definition is null)
            { issues.Add(new("missing_placed_pin_identity", path, symbol.Id.Value, component.Id)); continue; }
            if (symbol.Definition.UnitCount < occurrence.Unit || symbol.BodyStyle?.Style is <= 0)
            { issues.Add(new("invalid_native_unit_definition", path, symbol.Id.Value, component.Id)); continue; }
            if (symbol.Definition.Items.Any(child => child.Item is null))
            { issues.Add(new("invalid_symbol_child", path, symbol.Id.Value, component.Id)); continue; }
            var part = parts[definitions[component.DefinitionId].PartId];
            foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                if ((child.Unit?.Unit is > 0 && child.Unit.Unit != occurrence.Unit)
                    || (child.BodyStyle?.Style is > 0 && child.BodyStyle.Style != (symbol.BodyStyle?.Style ?? 1)))
                    continue;
                var pin = child.Item.Unpack<SchematicPin>();
                if (pin.LibraryPinId is null) continue; // Inactive/library-only definitions are not placed pins.
                var declared = part.Pins.SingleOrDefault(p => p.Number == pin.Number && (p.Unit == 0 || p.Unit == occurrence.Unit));
                if (declared is null || !Id(pin.Id?.Value) || !Id(pin.LibraryPinId.Value))
                { issues.Add(new("unmapped_native_pin", path, pin.Id?.Value, component.Id)); continue; }
                var endpoint = new PinEndpoint(component.Id, declared.Number); var key = (path, pin.Id!.Value);
                if (!knownItems.Add(key)) issues.Add(new("ambiguous_snapshot_pin", path, pin.Id.Value, component.Id));
                if (!nativePins.TryAdd(key, endpoint))
                { issues.Add(new("ambiguous_placed_pin", path, pin.Id.Value, component.Id)); continue; }
                if (!modelPins.TryGetValue(endpoint, out var placements)) modelPins.Add(endpoint, placements = []);
                placements.Add(key);
            }
            stacked.AddRange(StackedEndpoints(symbol, occurrence.Unit, component.Id, part));
        }
        var undrawn = new List<UndrawnComponentPin>();
        foreach (var component in circuit.Components)
        foreach (var pin in parts[definitions[component.DefinitionId].PartId].Pins)
            if (!modelPins.ContainsKey(new(component.Id, pin.Number)))
            {
                // A known physical pin on a unit which is deliberately not
                // drawn has no placed UUID/net membership. Account for its
                // actual library declarations, never fabricate a placement.
                if (pin.Unit > 0 && componentSymbols.TryGetValue(component.Id, out var owned)
                    && owned.All(s => s.Unit.Unit != pin.Unit && s.Definition is not null))
                {
                    var declarations = owned.Select(symbol => symbol.Definition.Items
                        .Where(c => c.Unit?.Unit == pin.Unit && c.Item?.Is(SchematicPin.Descriptor) == true)
                        .Select(c => c.Item.Unpack<SchematicPin>()).Where(p => p.Number == pin.Number)
                        .Select(p => p.LibraryPinId?.Value ?? p.Id?.Value).ToArray()).ToArray();
                    if (declarations.All(ids => ids.Length > 0 && ids.All(Id)))
                    {
                        var endpoint = new PinEndpoint(component.Id, pin.Number);
                        modelPins.Add(endpoint, []);
                        undrawn.Add(new(endpoint, pin.Unit, declarations.SelectMany(ids => ids).Select(id => id!)
                            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
                        continue;
                    }
                }
                issues.Add(new("unmapped_model_pin", sheets[component.SheetInstanceId], pin.Number, component.Id));
            }

        var nativeMembership = new Dictionary<(string Path, string Id), int>();
        for (int index = 0; index < observed.Nets.Count; ++index)
        {
            if (observed.Nets[index].Sheets.Count == 0) issues.Add(new("empty_native_net", null, null));
            foreach (var sheet in observed.Nets[index].Sheets)
            {
                token.ThrowIfCancellationRequested(); string path = Path(sheet.Path);
                if (sheet.Items.Count == 0) issues.Add(new("empty_net_sheet_membership", path, null));
                if (!screens.ContainsKey(path))
                { issues.Add(new("net_sheet_not_in_snapshot", path, null)); continue; }
                foreach (var id in sheet.Items)
                {
                    if (!Id(id.Value)) { issues.Add(new("invalid_net_item_identity", path, id.Value)); continue; }
                    var key = (path, id.Value);
                    if (!knownItems.Contains(key)) issues.Add(new("net_item_not_in_snapshot", path, id.Value));
                    if (!nativeMembership.TryAdd(key, index))
                        issues.Add(new("duplicate_net_item_membership", path, id.Value));
                }
            }
        }
        var endpointNets = new Dictionary<PinEndpoint, int?>();
        foreach (var (endpoint, placements) in modelPins)
        {
            var groups = placements.Where(nativeMembership.ContainsKey).Select(p => nativeMembership[p]).Distinct().ToArray();
            if (groups.Length > 1) issues.Add(new("physical_pin_in_multiple_nets", null, endpoint.Pin, endpoint.ComponentId));
            endpointNets.Add(endpoint, groups.Length == 1 ? groups[0] : null);
        }
        if (issues.Count > 0) return new(false, false, issues, [], report.CoverageGaps);

        var expected = circuit.Nets.SelectMany(n => n.Pins.Select(p => (Pin: p, Net: n.Id))).ToDictionary(x => x.Pin, x => x.Net);
        var differences = new List<ElectricalConnectivityDifference>();
        foreach (var net in circuit.Nets)
        {
            token.ThrowIfCancellationRequested();
            var partitions = net.Pins.Select(p => endpointNets[p] is { } index ? "net:" + index : "pin:" + p.ComponentId + ":" + p.Pin)
                .Distinct(StringComparer.Ordinal).Count();
            if (partitions > 1) differences.Add(new("model_net_split", [net.Id],
                net.Pins.Where(p => endpointNets[p] is not null).Select(p => endpointNets[p]!.Value).Distinct().Order().ToArray(), Ordered(net.Pins)));
        }
        // KiCad always joins the pins one placed symbol stacks at one point, so each stacked node is one node of the
        // model: its pins share the one model net some of them name, or form one unnamed node when the model leaves all
        // of them unconnected. A node the model spreads over two nets stays split here, so KiCad's join is reported as a
        // native_net_join; planning refuses such XML over the same nodes (stacked_pins_on_different_nets). Pins of
        // different symbols or units are never grouped by position, even where they touch.
        var node = new Dictionary<PinEndpoint, string>();
        var stackedNodes = StackedNodes(stacked);
        foreach (var group in stackedNodes)
        {
            token.ThrowIfCancellationRequested();
            var nets = group.Where(expected.ContainsKey).Select(p => expected[p]).Distinct().ToArray();
            if (nets.Length > 1) continue;
            string key = nets.Length == 1 ? "net:" + nets[0] : "stack:" + group[0].ComponentId + ":" + group[0].Pin;
            foreach (var pin in group) node[pin] = key;
            if (group.Select(p => endpointNets[p] is { } index ? "net:" + index : "pin:" + p.ComponentId + ":" + p.Pin)
                .Distinct(StringComparer.Ordinal).Count() > 1)
                differences.Add(new("stacked_pins_split", nets, group.Where(p => endpointNets[p] is not null)
                    .Select(p => endpointNets[p]!.Value).Distinct().Order().ToArray(), group));
        }
        foreach (var net in endpointNets.Where(p => p.Value is not null).GroupBy(p => p.Value!.Value))
        {
            var pins = net.Select(p => p.Key).ToArray();
            var partitions = pins.Select(p => node.TryGetValue(p, out var joined) ? joined
                    : expected.TryGetValue(p, out var id) ? "net:" + id : "pin:" + p.ComponentId + ":" + p.Pin)
                .Distinct(StringComparer.Ordinal).Count();
            if (partitions > 1) differences.Add(new("native_net_join", pins.Where(expected.ContainsKey)
                .Select(p => expected[p]).Distinct().Order().ToArray(), [net.Key], Ordered(pins)));
        }
        var nativePartitions = endpointNets.Where(p => p.Value is not null).GroupBy(p => p.Value!.Value)
            .Select(g => new NativePinPartition(g.Key, observed.Nets[g.Key].Name, Ordered(g.Select(p => p.Key))))
            .Concat(endpointNets.Where(p => p.Value is null).Select(p => new NativePinPartition(null, null, [p.Key])))
            .OrderBy(g => g.Pins[0].ComponentId).ThenBy(g => g.Pins[0].Pin, StringComparer.Ordinal).ToArray();
        return new(true, differences.Count == 0, [], differences, report.CoverageGaps, PinPartitions: nativePartitions,
            UndrawnPins: undrawn.OrderBy(p => p.Pin.ComponentId).ThenBy(p => p.Pin.Pin, StringComparer.Ordinal).ToArray(),
            StackedPins: stackedNodes);
    }

    private static IReadOnlyList<PinEndpoint> Ordered(IEnumerable<PinEndpoint> pins) =>
        pins.OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal).ToArray();

    /// <summary>Stacked-pin rule (decision kicad-stacked-pins-one-node-20260924): the groups of pins that
    /// <paramref name="symbol"/>'s own definition draws at exactly one point for <paramref name="unit"/>. Only the pins that
    /// placement shows count: pins of that unit or common to all units, of its body style or common to all styles, that
    /// carry a library-pin link. KiCad joins pins that meet at one point whatever their visibility, so hidden pins count. A
    /// no-connect pin passes no connection on in KiCad and is left out, and a pin without a recorded position is never
    /// grouped: no stacking is inferred without exact geometry (declared symbols must record every pin position). Names
    /// are never compared, and pins of other symbols or other units are never grouped here, even where they touch.
    /// Groups and their pins keep definition order.</summary>
    internal static IReadOnlyList<IReadOnlyList<SchematicPin>> StackedDefinitionPins(SchematicSymbolInstance symbol, int unit)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (symbol.Definition is null) return [];
        int style = symbol.BodyStyle?.Style ?? 1;
        var points = new Dictionary<(long X, long Y), List<SchematicPin>>();
        var groups = new List<List<SchematicPin>>();
        foreach (var child in symbol.Definition.Items)
        {
            if (child.Item is null || !child.Item.Is(SchematicPin.Descriptor)) continue;
            if ((child.Unit?.Unit is > 0 && child.Unit.Unit != unit) || (child.BodyStyle?.Style is > 0 && child.BodyStyle.Style != style)) continue;
            var pin = child.Item.Unpack<SchematicPin>();
            if (pin.LibraryPinId is null || pin.Position is null || EffectivePinType(pin) == Kiapi.Common.Types.ElectricalPinType.EptNoConnect) continue;
            if (!points.TryGetValue((pin.Position.XNm, pin.Position.YNm), out var group))
            {
                points.Add((pin.Position.XNm, pin.Position.YNm), group = []);
                groups.Add(group);
            }
            group.Add(pin);
        }
        return [.. groups.Where(g => g.Count > 1)];
    }

    /// <summary>The stacked-pin nodes of <paramref name="design"/> as KiCad will join them, read from each symbol occurrence's
    /// exact bound native symbol in the design's own schematic (<see cref="StackedDefinitionPins"/>), as model pins. Groups
    /// that share a model pin are one node: a pin common to several units is one physical pin, so the pins each unit stacks
    /// on it are joined through it. Occurrences without an exact bound native symbol are left to the binding checks. Each
    /// node is returned once, its pins ordered.</summary>
    internal static IReadOnlyList<IReadOnlyList<PinEndpoint>> StackedPinNodes(SchematicDesign design, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(design);
        var circuit = design.Engineering.Circuit;
        var components = new Dictionary<Guid, ComponentInstance>();
        foreach (var component in circuit.Components) components.TryAdd(component.Id, component);
        var definitions = new Dictionary<Guid, ComponentDefinition>();
        foreach (var definition in circuit.Sheets.SelectMany(s => s.Components)) definitions.TryAdd(definition.Id, definition);
        var parts = new Dictionary<Guid, PartDefinition>();
        foreach (var part in circuit.Parts) parts.TryAdd(part.Id, part);
        var paths = new Dictionary<Guid, string>();
        foreach (var binding in design.SheetBindings) paths.TryAdd(binding.SheetInstanceId, SchematicDesignBindings.PathKey(binding.NativePath));
        var natives = new Dictionary<Guid, string>();
        foreach (var binding in design.SymbolBindings) natives.TryAdd(binding.SymbolOccurrenceId, binding.NativeObjectId.ToString("D"));
        var symbols = new Dictionary<(string Path, string Id), SchematicSymbolInstance>();
        foreach (var screen in design.Schematic.Instances)
        {
            string path = Path(screen.Metadata?.Document?.SheetPath);
            foreach (var packed in screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)))
            {
                var symbol = packed.Unpack<SchematicSymbolInstance>();
                if (symbol.Id?.Value is { } id) symbols.TryAdd((path, id), symbol);
            }
        }
        var stacked = new List<PinEndpoint[]>();
        foreach (var occurrence in circuit.Symbols)
        {
            token.ThrowIfCancellationRequested();
            if (!components.TryGetValue(occurrence.ComponentId, out var component) || !definitions.TryGetValue(component.DefinitionId, out var definition)
                || !parts.TryGetValue(definition.PartId, out var part) || !paths.TryGetValue(occurrence.EffectiveSheetInstanceId(component), out var path)
                || !natives.TryGetValue(occurrence.Id, out var native) || !symbols.TryGetValue((path, native), out var symbol))
                continue;
            stacked.AddRange(StackedEndpoints(symbol, occurrence.Unit, component.Id, part));
        }
        return StackedNodes(stacked);
    }

    // The model pins of one placed unit that its definition stacks at one point.
    private static IEnumerable<PinEndpoint[]> StackedEndpoints(SchematicSymbolInstance symbol, int unit, Guid component, PartDefinition part) =>
        StackedDefinitionPins(symbol, unit).Select(group => group
                .Select(pin => part.Pins.FirstOrDefault(p => p.Number == pin.Number && (p.Unit == 0 || p.Unit == unit))).OfType<PartPin>()
                .Select(p => new PinEndpoint(component, p.Number)).Distinct().ToArray())
            .Where(members => members.Length > 1);

    // KiCad applies an active alternate's electrical type.
    private static Kiapi.Common.Types.ElectricalPinType EffectivePinType(SchematicPin pin) => pin.HasActiveAlternate && pin.ActiveAlternate.Length != 0
        ? pin.Alternates.FirstOrDefault(a => a.Name == pin.ActiveAlternate)?.ElectricalType ?? Kiapi.Common.Types.ElectricalPinType.EptUnspecified
        : pin.ElectricalType;

    // Stacked groups that share a model pin (a pin common to several units, stacked in more than one of them) are one
    // node; each node is returned once, its pins ordered.
    private static IReadOnlyList<IReadOnlyList<PinEndpoint>> StackedNodes(IReadOnlyList<PinEndpoint[]> groups)
    {
        var parent = new Dictionary<PinEndpoint, PinEndpoint>();
        PinEndpoint Find(PinEndpoint pin)
        {
            var root = pin;
            while (parent.TryGetValue(root, out var next)) root = next;
            while (parent.TryGetValue(pin, out var next) && next != root) { parent[pin] = root; pin = next; }
            return root;
        }
        foreach (var group in groups)
            foreach (var pin in group.Skip(1))
            {
                PinEndpoint left = Find(group[0]), right = Find(pin);
                if (left != right) parent[right] = left;
            }
        return [.. groups.SelectMany(g => g).Distinct().GroupBy(Find).Select(g => Ordered(g))
            .OrderBy(g => g[0].ComponentId).ThenBy(g => g[0].Pin, StringComparer.Ordinal)];
    }
    private static string Path(Kiapi.Common.Types.SheetPath? path) => path is null ? "" : string.Join('/', path.Path.Select(i => i.Value));
    private static bool Id(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D");
}
