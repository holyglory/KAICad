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
public sealed record SchematicElectricalComparisonResult(bool PinBindingsComplete, bool ConnectivityEquivalent,
    IReadOnlyList<ElectricalBindingIssue> Issues, IReadOnlyList<ElectricalConnectivityDifference> Differences,
    IReadOnlyList<HierarchyCoverageGap> CoverageGaps, string? ErrorCode = null, string? ErrorMessage = null,
    IReadOnlyList<NativePinPartition>? PinPartitions = null, IReadOnlyList<UndrawnComponentPin>? UndrawnPins = null);

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
        foreach (var net in endpointNets.Where(p => p.Value is not null).GroupBy(p => p.Value!.Value))
        {
            var pins = net.Select(p => p.Key).ToArray();
            var partitions = pins.Select(p => expected.TryGetValue(p, out var id) ? "net:" + id : "pin:" + p.ComponentId + ":" + p.Pin)
                .Distinct(StringComparer.Ordinal).Count();
            if (partitions > 1) differences.Add(new("native_net_join", pins.Where(expected.ContainsKey)
                .Select(p => expected[p]).Distinct().Order().ToArray(), [net.Key], Ordered(pins)));
        }
        var nativePartitions = endpointNets.Where(p => p.Value is not null).GroupBy(p => p.Value!.Value)
            .Select(g => new NativePinPartition(g.Key, observed.Nets[g.Key].Name, Ordered(g.Select(p => p.Key))))
            .Concat(endpointNets.Where(p => p.Value is null).Select(p => new NativePinPartition(null, null, [p.Key])))
            .OrderBy(g => g.Pins[0].ComponentId).ThenBy(g => g.Pins[0].Pin, StringComparer.Ordinal).ToArray();
        return new(true, differences.Count == 0, [], differences, report.CoverageGaps, PinPartitions: nativePartitions,
            UndrawnPins: undrawn.OrderBy(p => p.Pin.ComponentId).ThenBy(p => p.Pin.Pin, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<PinEndpoint> Ordered(IEnumerable<PinEndpoint> pins) =>
        pins.OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal).ToArray();
    private static string Path(Kiapi.Common.Types.SheetPath? path) => path is null ? "" : string.Join('/', path.Path.Select(i => i.Value));
    private static bool Id(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D");
}
