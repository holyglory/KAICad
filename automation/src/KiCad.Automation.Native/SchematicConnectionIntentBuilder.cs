using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

// CN-1 connection intent (automation/design/contracts/cn1-wiring-intent.md §5). Pure planning: it reads the
// saved recovery record and the pre-realization candidate, never the editor, and decides exactly which
// placed pins each changed net must join, how every sheet crossing is carried, which label text each
// island uses and which native pin partition the editor must prove afterwards. Label texts are outputs;
// every identity comes from exact component, occurrence, binding and placed-pin identities. Lane 2A created
// this file under the cn1-intent integration grant. The ownership map (psu-cpu-fixture-and-ownership.md
// §2.3) does not list it yet; registering it for 2A is a seam request to the integration owner.

/// <summary>The placed pins a symbol placement shows for one unit (cn1-wiring-intent.md §5.1): definition
/// pins of that unit or common to all units, of the placement's body style or common to all styles, that
/// carry a library-pin link. This is the lookup <see cref="SchematicElectricalComparison"/> applies; that
/// frozen file keeps its own copy until the integration owner switches it to this helper.</summary>
internal static class SchematicPlacedPins
{
    internal static IEnumerable<SchematicPin> Active(SchematicSymbolInstance symbol, int unit)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (symbol.Definition is null) yield break;
        int style = symbol.BodyStyle?.Style ?? 1;
        foreach (var child in symbol.Definition.Items)
        {
            if (child.Item is null || !child.Item.Is(SchematicPin.Descriptor)) continue;
            if ((child.Unit?.Unit is > 0 && child.Unit.Unit != unit) || (child.BodyStyle?.Style is > 0 && child.BodyStyle.Style != style)) continue;
            var pin = child.Item.Unpack<SchematicPin>();
            if (pin.LibraryPinId is null) continue;
            yield return pin;
        }
    }
}

/// <summary>Builds the frozen <see cref="SchematicConnectionIntent"/> for an admitted connected addition
/// (cn1-wiring-intent.md §5). Every refusal is an <see cref="AutomationException"/> with a §13 planning code
/// and a message that names the net, pin or sheet to change; nothing reaches the editor.</summary>
public static partial class SchematicConnectionIntentBuilder
{
    /// <summary>At most this many nets may change in one revision (§5.10).</summary>
    public const int MaxChangedNets = 1024;
    /// <summary>At most this many placed pins may need a generated stub in one revision (§5.10).</summary>
    public const int MaxStubPlacements = 4096;

    /// <summary>Build the connection intent for <paramref name="candidate"/>, the pre-realization design of an
    /// admitted classification (§4.4 step 2). <paramref name="state"/> supplies the saved baseline, the exact
    /// desired file bytes and the observed native pin partition the executor will require unchanged.</summary>
    public static SchematicConnectionIntent Build(DesignRecoveryState state, SchematicDesign candidate,
        SchematicConnectedAdditionClassification shape, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Kind != SchematicConnectedAdditionKind.Admitted)
            throw new ArgumentException("Only an admitted connected addition has a connection intent.", nameof(shape));
        token.ThrowIfCancellationRequested();
        if (shape.ChangedNetIds.Count > MaxChangedNets)
            throw Error(SchematicConnectionErrors.ConnectedScopeTooLarge, "This XML revision changes "
                + shape.ChangedNetIds.Count.ToString(CultureInfo.InvariantCulture) + " nets, but one synchronization can connect at most "
                + MaxChangedNets.ToString(CultureInfo.InvariantCulture) + ". Save the connections in smaller XML revisions.");
        return new Builder(state, candidate, shape, token).Build();
    }

    /// <summary>A compact, JSON-serializable description of <paramref name="intent"/> for previews
    /// (cn1-wiring-intent.md §9.5): nets with scope and global name, islands with label text, members, roles,
    /// stub and join needs, ports and the expected group count.</summary>
    internal static object Summary(SchematicConnectionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return new
        {
            version = intent.Version, circuitId = intent.CircuitId, nativeRevision = new { epoch = intent.NativeRevision.Epoch, sequence = intent.NativeRevision.Sequence },
            desiredSha256 = intent.DesiredSha256,
            nets = intent.Nets.Select(n => new { netId = n.NetId, name = n.Name, scope = n.Scope.ToString(), globalName = n.GlobalName,
                addedPins = n.AddedPins.Select(p => new { componentId = p.ComponentId, pin = p.Pin }).ToArray() }).ToArray(),
            screens = intent.Screens.Select(s => new
            {
                screenId = s.ScreenId, instancePaths = s.InstancePathKeys,
                islands = s.Islands.Select(i => new
                {
                    netId = i.NetId, sheetPath = i.SheetPathKey, scope = i.Scope.ToString(), labelText = i.LabelText,
                    members = i.Members.Select(m => new { componentId = m.Pin.Endpoint.ComponentId, pin = m.Pin.Endpoint.Pin,
                        placedPinId = m.Pin.PlacedPinId, symbolId = m.Pin.SymbolId, createdSymbol = m.Pin.CreatedSymbol, role = m.Role.ToString(),
                        alreadyConnected = m.AlreadyConnected, requiresStub = m.RequiresStub, powerName = m.PowerName }).ToArray(),
                    anchorHasMatchingDriver = i.AnchorHasMatchingDriver, joinRequired = i.JoinRequired,
                    joinCandidates = i.JoinCandidates.Select(p => p.PlacedPinId).ToArray(),
                    uplinkSheetSymbolId = i.UplinkSheetSymbolId, childSheetSymbolIds = i.ChildSheetSymbolIds
                }).ToArray()
            }).ToArray(),
            ports = intent.Ports.Select(p => new { netId = p.NetId, childPath = p.ChildPathKey, parentPath = p.ParentPathKey,
                sheetSymbolId = p.SheetSymbolId, portText = p.PortText, sheetPinExists = p.SheetPinExists, uplinkLabelExists = p.UplinkLabelExists }).ToArray(),
            expectedGroupCount = intent.ExpectedGroups.Count,
            expectedPinCount = intent.ExpectedGroups.Sum(g => g.Count),
            createdSymbolIds = intent.CreatedSymbolIds
        };
    }

    internal static AutomationException Error(string code, string message) => new(code, message);

    /// <summary>Whether <paramref name="text"/> can label a generated connection (§5.5): 1 to 128 Unicode
    /// scalar values, no whitespace or control character, none of <c>{ } [ ] / \ $ ~ ^ , "</c>, and not
    /// starting with <c>#</c>.</summary>
    internal static bool ValidLabelText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text[0] == '#') return false;
        int count = 0;
        for (int index = 0; index < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out int consumed) != System.Buffers.OperationStatus.Done) return false;
            index += consumed;
            if (++count > 128 || Rune.IsWhiteSpace(rune) || Rune.IsControl(rune)) return false;
            if (rune.IsAscii && "{}[]/\\$~^,\"".Contains((char)rune.Value, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>The part of a net name after its last <c>/</c> (§5.5), or the whole name.</summary>
    internal static string LocalName(string name) => name[(name.LastIndexOf('/') + 1)..];

    private readonly record struct ItemKey(string Path, Guid Id);

    private enum ItemKind { Other, Symbol, Pin, LocalLabel, GlobalLabel, HierarchicalLabel, SheetSymbol, SheetPin, BusEntry, BusLine }

    private sealed record NativeItem(ItemKind Kind, string Text, Guid? Owner, SchematicSymbolInstance? Symbol, SchematicPin? Pin,
        SheetSymbol? Sheet);

    private sealed record Placement(ConnectionPlacedPin Pin, SchematicSymbolInstance Symbol, SchematicPin NativePin)
    {
        public ItemKey Key => new(Pin.SheetPathKey, Pin.PlacedPinId);
    }

    /// <summary>The native partition that already holds a net's baseline pins: one observed net entry, or
    /// the single baseline pin itself when the editor reports it in no entry.</summary>
    private sealed record Anchor(string PartitionId, IReadOnlySet<ItemKey> Items);

    private sealed class Island
    {
        public required Guid NetId { get; init; }
        public required Guid Instance { get; init; }
        public required string Path { get; init; }
        public required Guid Screen { get; init; }
        public required ConnectionScope Scope { get; init; }
        public required List<ConnectionMember> Members { get; init; }
        public string Text { get; set; } = "";
        public bool TextFromName { get; set; }
        public List<Guid> AnchorItems { get; } = [];
        public bool Matching { get; set; }
        public bool Join { get; set; }
        public List<ConnectionPlacedPin> JoinCandidates { get; } = [];
        public Guid? Uplink { get; set; }
        public SortedSet<Guid> Children { get; } = [];
        public bool Stubs => Members.Any(m => m.RequiresStub);
        public bool Needs => Stubs || Join || Uplink is not null || Children.Count != 0;

        public ConnectionIsland Record() => new(NetId, Instance, Path, Screen, Scope, Text, Members.ToArray(),
            AnchorItems.Order().ToArray(), Matching, Join, JoinCandidates.ToArray(), Uplink, Children.ToArray());

        // Everything that decides the generated items on a shared screen, except the per-instance sheet
        // symbol an uplink crosses: the hierarchical label sits on this screen, while each instance's
        // sheet pin is appended by its own parent island (ChildSheetSymbolIds).
        public string PhysicalIdentity() => Scope + "|uplink:" + (Uplink is null ? "0" : "1") + "|children:"
            + string.Join(",", Children.Select(c => c.ToString("D"))) + "|members:"
            + string.Join(",", Members.Select(m => Physical(m.Pin)).Order(StringComparer.Ordinal));

        public string PhysicalKey() => PhysicalIdentity() + "|text:" + Text + "|stubs:"
            + string.Join(",", Members.Where(m => m.RequiresStub).Select(m => Physical(m.Pin)).Order(StringComparer.Ordinal))
            + "|join:" + string.Join(",", JoinCandidates.Select(Physical)) + "|matching:" + Matching + "|joinRequired:" + Join;

        private static string Physical(ConnectionPlacedPin pin) => pin.SymbolId.ToString("D") + "/" + pin.PlacedPinId.ToString("D");
    }

    private sealed class NetPlan
    {
        public required CircuitNet Net { get; init; }
        public required IReadOnlyList<PinEndpoint> Added { get; init; }
        public required IReadOnlyList<Placement> Placements { get; init; }
        public required Anchor? Anchor { get; init; }
        public Dictionary<ItemKey, Placement> ByKey => byKey ??= Placements.ToDictionary(p => p.Key);
        private Dictionary<ItemKey, Placement>? byKey;
        public List<ConnectionMember> Members { get; } = [];
        public ConnectionScope Scope { get; set; } = ConnectionScope.Local;
        public string? GlobalName { get; set; }
        // The global name comes from a hidden power input, so a clash is an implicit-power conflict (§5.3).
        public bool ImplicitName { get; set; }
        public List<Island> Islands { get; } = [];
        public List<ConnectionPort> Ports { get; } = [];
    }

    private sealed partial class Builder
    {
        private readonly DesignRecoveryState state;
        private readonly SchematicDesign candidate;
        private readonly SchematicConnectedAdditionClassification shape;
        private readonly CancellationToken token;
        private readonly Circuit circuit;
        private readonly Dictionary<Guid, ComponentInstance> components = [];
        private readonly Dictionary<Guid, ComponentDefinition> definitions = [];
        private readonly Dictionary<Guid, PartDefinition> parts = [];
        private readonly Dictionary<Guid, Model.SheetInstance> instances = [];
        private readonly Dictionary<Guid, IReadOnlyList<Guid>> nativePaths = [];
        private readonly Dictionary<Guid, string> pathKeys = [];
        private readonly Dictionary<Guid, Guid> symbolBindings = [];
        private readonly Dictionary<string, Guid> screenOfPath = new(StringComparer.Ordinal);
        private readonly Dictionary<ItemKey, SchematicSymbolInstance> candidateSymbols = [];
        private readonly HashSet<Guid> createdSymbols = [];
        private readonly Dictionary<ItemKey, NativeItem> nativeItems = [];
        private readonly Dictionary<string, List<(ItemKey Key, NativeItem Item)>> nativeByPath = new(StringComparer.Ordinal);
        private readonly Dictionary<ItemKey, int> netOf = [];
        private readonly List<List<ItemKey>> netItems = [];
        private readonly Dictionary<PinEndpoint, IReadOnlyList<Placement>> resolved = [];
        private readonly Dictionary<(ItemKey Symbol, int Unit), ILookup<string, SchematicPin>> activePins = [];

        public Builder(DesignRecoveryState state, SchematicDesign candidate, SchematicConnectedAdditionClassification shape, CancellationToken token)
        {
            this.state = state; this.candidate = candidate; this.shape = shape; this.token = token;
            circuit = candidate.Engineering.Circuit;
            foreach (var component in circuit.Components)
                if (!components.TryAdd(component.Id, component)) throw Inconsistent("A component identity appears twice.");
            foreach (var definition in circuit.Sheets.SelectMany(s => s.Components))
                if (!definitions.TryAdd(definition.Id, definition)) throw Inconsistent("A component definition identity appears twice.");
            foreach (var part in circuit.Parts)
                if (!parts.TryAdd(part.Id, part)) throw Inconsistent("A part identity appears twice.");
            foreach (var instance in circuit.SheetInstances)
                if (!instances.TryAdd(instance.Id, instance)) throw Inconsistent("A sheet instance identity appears twice.");
            foreach (var binding in candidate.SheetBindings)
            {
                if (!nativePaths.TryAdd(binding.SheetInstanceId, binding.NativePath)) throw Inconsistent("A sheet instance is bound twice.");
                pathKeys.Add(binding.SheetInstanceId, SchematicDesignBindings.PathKey(binding.NativePath));
            }
            foreach (var binding in candidate.SymbolBindings)
                if (!symbolBindings.TryAdd(binding.SymbolOccurrenceId, binding.NativeObjectId)) throw Inconsistent("A symbol occurrence is bound twice.");
            foreach (var screen in candidate.Schematic.Instances)
            {
                string path = PathOf(screen.Metadata?.Document?.SheetPath);
                if (!Guid.TryParseExact(screen.Metadata?.ScreenId?.Value, "D", out var screenId) || !screenOfPath.TryAdd(path, screenId))
                    throw Inconsistent("A sheet instance of the candidate has no unique screen identity.");
                foreach (var packed in screen.Items)
                {
                    if (!packed.Is(SchematicSymbolInstance.Descriptor)) continue;
                    var symbol = packed.Unpack<SchematicSymbolInstance>();
                    if (Guid.TryParseExact(symbol.Id?.Value, "D", out var id)) candidateSymbols.TryAdd(new(path, id), symbol);
                }
            }
            var added = shape.AddedComponentIds.ToHashSet();
            foreach (var occurrence in circuit.Symbols.Where(s => added.Contains(s.ComponentId)))
                if (symbolBindings.TryGetValue(occurrence.Id, out var native)) createdSymbols.Add(native);
            IndexNative(state.ObservedElectrical ?? throw Error("missing_electrical_observation", "Capture current matching electrical state first."));
        }

        public SchematicConnectionIntent Build()
        {
            var delta = SchematicConnectedAddition.Delta(state.Baseline.Engineering.Circuit, circuit)
                ?? throw Inconsistent("The candidate circuit no longer resolves every net pin to a declared part pin.");
            if (!delta.Added.Keys.Order().SequenceEqual(shape.ChangedNetIds.Order()))
                throw Inconsistent("The classified nets differ from the nets this revision actually changes.");
            var nets = circuit.Nets.ToDictionary(n => n.Id);
            var baselineNets = state.Baseline.Engineering.Circuit.Nets.ToDictionary(n => n.Id);
            // §5.1, §5.2: every endpoint of every changed net resolves to exact placed pins, and each net finds
            // the native partition that already holds its baseline pins.
            var plans = new List<NetPlan>();
            foreach (var id in shape.ChangedNetIds.Order())
            {
                token.ThrowIfCancellationRequested();
                var net = nets.GetValueOrDefault(id) ?? throw Inconsistent("A changed net is not in the candidate circuit.");
                var placements = new List<Placement>();
                var seen = new HashSet<ItemKey>();
                foreach (var endpoint in Ordered(net.Pins))
                foreach (var placement in Resolve(endpoint, net))
                    if (!seen.Add(placement.Key)) throw Inconsistent("Net '" + net.Name + "' names one placed pin twice.");
                    else placements.Add(placement);
                var baseline = baselineNets.GetValueOrDefault(id);
                plans.Add(new NetPlan { Net = net, Added = delta.Added[id], Placements = placements,
                    Anchor = AnchorOf(net, baseline is null ? [] : Ordered(baseline.Pins).SelectMany(e => Resolve(e, net)).ToArray()) });
            }
            var createdPlacements = CreatedPlacements();
            foreach (var plan in plans) Members(plan);
            // §5.10: the stubs to generate stay bounded.
            int stubs = plans.Sum(p => p.Members.Count(m => m.RequiresStub));
            if (stubs > MaxStubPlacements)
                throw Error(SchematicConnectionErrors.ConnectedScopeTooLarge, "This XML revision needs " + stubs.ToString(CultureInfo.InvariantCulture)
                    + " new pin connections drawn, but one synchronization can draw at most " + MaxStubPlacements.ToString(CultureInfo.InvariantCulture)
                    + ". Save the connections in smaller XML revisions.");
            Scopes(plans, createdPlacements);
            foreach (var plan in plans)
            {
                token.ThrowIfCancellationRequested();
                if (plan.Scope == ConnectionScope.Global) GlobalIslands(plan); else LocalIslands(plan);
            }
            SharedScreens(plans);
            Variants(plans, createdPlacements);
            var groups = ExpectedGroups(plans, createdPlacements);
            return Assemble(plans, groups);
        }

        // ---- §5.1 endpoint to placements ----

        private IReadOnlyList<Placement> Resolve(PinEndpoint endpoint, CircuitNet net)
        {
            if (resolved.TryGetValue(endpoint, out var known)) return known;
            token.ThrowIfCancellationRequested();
            if (!components.TryGetValue(endpoint.ComponentId, out var component)
                || !definitions.TryGetValue(component.DefinitionId, out var definition) || !parts.TryGetValue(definition.PartId, out var part))
                throw Error(SchematicConnectionErrors.ConnectedPinUnresolved, "Net '" + net.Name + "' names component "
                    + endpoint.ComponentId.ToString("D") + ", which the design does not declare.");
            var declared = part.Pins.Where(p => p.Number == endpoint.Pin).ToArray();
            if (declared.Length != 1)
                throw Error(SchematicConnectionErrors.ConnectedPinUnresolved, "Net '" + net.Name + "' names pin " + Describe(endpoint)
                    + ", which part '" + part.Name + "' does not declare exactly once.");
            int unit = declared[0].Unit;
            var occurrences = circuit.Symbols.Where(s => s.ComponentId == component.Id && (unit == 0 || s.Unit == unit))
                .OrderBy(s => s.Unit).ThenBy(s => s.Id).ToArray();
            if (occurrences.Length == 0)
                throw Error(SchematicConnectionErrors.ConnectedPinUnresolved, "Net '" + net.Name + "' connects pin " + Describe(endpoint)
                    + ", but unit " + unit.ToString(CultureInfo.InvariantCulture) + " of " + component.Reference
                    + " is not drawn on any sheet. Add that unit's symbol occurrence in the XML, or remove the pin from the net.");
            var result = new List<Placement>();
            foreach (var occurrence in occurrences)
            {
                Guid sheet = occurrence.EffectiveSheetInstanceId(component);
                if (!pathKeys.TryGetValue(sheet, out var path) || !screenOfPath.TryGetValue(path, out var screen)
                    || !symbolBindings.TryGetValue(occurrence.Id, out var native) || !candidateSymbols.TryGetValue(new(path, native), out var symbol))
                    throw Error(SchematicConnectionErrors.ConnectedPinIdentityMissing, "Pin " + Describe(endpoint)
                        + " has no exact native symbol for its unit " + occurrence.Unit.ToString(CultureInfo.InvariantCulture) + ".");
                if (!symbol.SeparatePinIdentities || symbol.Definition is null)
                    throw Error(SchematicConnectionErrors.ConnectedPinIdentityMissing, "The native symbol of " + component.Reference
                        + " does not give its pins their own identities, so pin " + Describe(endpoint) + " cannot be connected exactly.");
                var cacheKey = (new ItemKey(path, native), occurrence.Unit);
                if (!activePins.TryGetValue(cacheKey, out var byNumber))
                    activePins.Add(cacheKey, byNumber = SchematicPlacedPins.Active(symbol, occurrence.Unit).ToLookup(p => p.Number, StringComparer.Ordinal));
                var pins = byNumber[endpoint.Pin].ToArray();
                if (pins.Length == 0)
                    throw Error(SchematicConnectionErrors.ConnectedPinUnresolved, "The native symbol of " + component.Reference + " unit "
                        + occurrence.Unit.ToString(CultureInfo.InvariantCulture) + " has no placed pin " + endpoint.Pin + ".");
                if (pins.Length > 1)
                    throw Error(SchematicConnectionErrors.ConnectedPinAmbiguous, "The native symbol of " + component.Reference + " unit "
                        + occurrence.Unit.ToString(CultureInfo.InvariantCulture) + " has " + pins.Length.ToString(CultureInfo.InvariantCulture)
                        + " placed pins numbered " + endpoint.Pin + ", so the connection cannot pick one exactly.");
                if (!Canonical(pins[0].Id?.Value, out var placedPin) || !Canonical(pins[0].LibraryPinId?.Value, out var libraryPin)
                    || !Canonical(symbol.Id?.Value, out var symbolId))
                    throw Error(SchematicConnectionErrors.ConnectedPinIdentityMissing, "Pin " + Describe(endpoint) + " has no canonical native identity.");
                result.Add(new(new(endpoint, occurrence.Id, sheet, path, screen, symbolId, placedPin, libraryPin, createdSymbols.Contains(symbolId)),
                    symbol, pins[0]));
            }
            resolved.Add(endpoint, result);
            return result;
        }

        // Every placed pin of every created symbol, one per sharing instance path (§5.1, §5.3, §5.8).
        private List<Placement> CreatedPlacements()
        {
            var result = new List<Placement>();
            var added = shape.AddedComponentIds.ToHashSet();
            foreach (var occurrence in circuit.Symbols.Where(s => added.Contains(s.ComponentId)).OrderBy(s => s.Id))
            {
                token.ThrowIfCancellationRequested();
                var component = components[occurrence.ComponentId];
                var part = parts[definitions[component.DefinitionId].PartId];
                Guid sheet = occurrence.EffectiveSheetInstanceId(component);
                if (!pathKeys.TryGetValue(sheet, out var path) || !screenOfPath.TryGetValue(path, out var screen)
                    || !symbolBindings.TryGetValue(occurrence.Id, out var native) || !candidateSymbols.TryGetValue(new(path, native), out var symbol)
                    || !symbol.SeparatePinIdentities || symbol.Definition is null)
                    throw Error(SchematicConnectionErrors.ConnectedPinIdentityMissing, "The created symbol of " + component.Reference
                        + " unit " + occurrence.Unit.ToString(CultureInfo.InvariantCulture) + " has no exact placed pin identities.");
                foreach (var pin in SchematicPlacedPins.Active(symbol, occurrence.Unit))
                {
                    var endpoint = new PinEndpoint(component.Id, pin.Number);
                    if (!part.Pins.Any(p => p.Number == pin.Number && (p.Unit == 0 || p.Unit == occurrence.Unit))
                        || !Canonical(pin.Id?.Value, out var placedPin) || !Canonical(pin.LibraryPinId?.Value, out var libraryPin))
                        throw Error(SchematicConnectionErrors.ConnectedPinIdentityMissing, "Created pin " + Describe(endpoint)
                            + " has no exact declared part pin or native identity.");
                    result.Add(new(new(endpoint, occurrence.Id, sheet, path, screen, native, placedPin, libraryPin, true), symbol, pin));
                }
            }
            return result;
        }

        // ---- §5.2 members, roles and the anchor partition ----

        private Anchor? AnchorOf(CircuitNet net, IReadOnlyList<Placement> baseline)
        {
            var partitions = baseline.Select(p => PartitionId(p.Key)).Distinct(StringComparer.Ordinal).ToArray();
            if (partitions.Length == 0) return null;
            if (partitions.Length > 1)
                throw Inconsistent("The editor no longer shows net '" + net.Name + "' as one connection; refresh the observation first.");
            var key = baseline[0].Key;
            return netOf.TryGetValue(key, out int index)
                ? new Anchor(partitions[0], netItems[index].ToHashSet())
                : new Anchor(partitions[0], new HashSet<ItemKey> { key });
        }

        private void Members(NetPlan plan)
        {
            var added = plan.Added.ToHashSet();
            foreach (var placement in plan.Placements)
            {
                var symbol = placement.Symbol; var pin = placement.NativePin;
                var role = ConnectionMemberRole.Signal; string? name = null;
                if (IsPowerSymbol(symbol)) { role = ConnectionMemberRole.PowerCarrier; name = CarrierName(symbol); }
                else if (IsImplicitPower(symbol, pin)) { role = ConnectionMemberRole.ImplicitPower; name = ImplicitName(pin); }
                bool connected = plan.Anchor?.Items.Contains(placement.Key) == true;
                plan.Members.Add(new(placement.Pin, role, connected, name));
                // §5.2: an existing pin the XML adds must not already be driven. Under an aligned baseline such a
                // driver names another connection or is a lone label; merging it would silently rename it.
                if (added.Contains(placement.Pin.Endpoint) && !placement.Pin.CreatedSymbol && Driver(placement.Key) is { } driver)
                    throw Error(SchematicConnectionErrors.ConnectedPinDriverConflict, "Net '" + plan.Net.Name + "' adds pin "
                        + Describe(placement.Pin.Endpoint) + ", but in the editor that pin is already driven by " + driver
                        + ". Remove that label or power connection in the schematic editor first, or leave the pin out of the net.");
            }
        }

        // A driver in the pin's own native partition: a global, local or hierarchical label, or a power pin.
        private string? Driver(ItemKey pin)
        {
            IEnumerable<ItemKey> partition = netOf.TryGetValue(pin, out int index) ? netItems[index] : [pin];
            foreach (var key in partition)
            {
                if (!nativeItems.TryGetValue(key, out var item)) continue;
                switch (item.Kind)
                {
                    case ItemKind.GlobalLabel: return "global label '" + item.Text + "'";
                    case ItemKind.LocalLabel: return "label '" + item.Text + "'";
                    case ItemKind.HierarchicalLabel: return "hierarchical label '" + item.Text + "'";
                    case ItemKind.Pin when IsGlobalPowerPin(item.Symbol!, item.Pin!) || IsLocalPowerPin(item.Symbol!, item.Pin!):
                        return "power connection '" + PowerName(item.Symbol!, item.Pin!) + "'";
                }
            }
            return null;
        }

        // ---- §5.3 scope, global name and implicit power ----

        private void Scopes(List<NetPlan> plans, List<Placement> created)
        {
            var nativeCarriers = NativeGlobalCarriers();
            foreach (var plan in plans)
            {
                var sources = new List<(string Name, bool Implicit, string Origin)>();
                foreach (var member in plan.Members)
                {
                    if (member.Role == ConnectionMemberRole.PowerCarrier)
                    {
                        RequireResolvedPowerName(member.PowerName, "power symbol " + Describe(member.Pin.Endpoint));
                        if (IsGlobalPowerSymbol(PlacementOf(plan, member).Symbol))
                            sources.Add((member.PowerName!, false, "power symbol " + Describe(member.Pin.Endpoint)));
                    }
                    else if (member.Role == ConnectionMemberRole.ImplicitPower)
                        sources.Add((member.PowerName!, true, "hidden power pin " + Describe(member.Pin.Endpoint)));
                }
                foreach (var key in plan.Anchor?.Items ?? new HashSet<ItemKey>())
                {
                    if (!nativeItems.TryGetValue(key, out var item)) continue;
                    if (item.Kind == ItemKind.GlobalLabel) sources.Add((item.Text, false, "global label '" + item.Text + "'"));
                    else if (item.Kind == ItemKind.Pin && IsGlobalPowerPin(item.Symbol!, item.Pin!))
                    {
                        string name = PowerName(item.Symbol!, item.Pin!);
                        RequireResolvedPowerName(name, "an existing power connection of net '" + plan.Net.Name + "'");
                        sources.Add((name, !IsPowerSymbol(item.Symbol!), "existing power connection '" + name + "'"));
                    }
                }
                var names = sources.Select(s => s.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (names.Length > 1)
                    throw Error(sources.Any(s => s.Implicit) ? SchematicConnectionErrors.ConnectedImplicitPowerConflict : SchematicConnectionErrors.ConnectedGlobalNameConflict,
                        "Net '" + plan.Net.Name + "' would join different global power names: "
                        + string.Join("; ", sources.Select(s => s.Origin + " is '" + s.Name + "'").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                        + ". Keep each global power name in its own net.");
                if (names.Length == 1) { plan.Scope = ConnectionScope.Global; plan.GlobalName = names[0]; plan.ImplicitName = sources.Any(s => s.Implicit); }
            }
            foreach (var group in plans.Where(p => p.GlobalName is not null).GroupBy(p => p.GlobalName!, StringComparer.Ordinal))
                if (group.Count() > 1)
                    throw Error(group.Any(p => p.ImplicitName) ? SchematicConnectionErrors.ConnectedImplicitPowerConflict : SchematicConnectionErrors.ConnectedGlobalNameConflict,
                        "Nets " + string.Join(" and ", group.Select(p => "'" + p.Net.Name + "'"))
                        + " would both carry the global name '" + group.Key + "', which joins them into one connection. Merge them in the XML or rename a power symbol.");
            foreach (var plan in plans.Where(p => p.GlobalName is not null))
            {
                var foreign = nativeCarriers.GetValueOrDefault(plan.GlobalName!) ?? [];
                if (foreign.Any(partition => partition != plan.Anchor?.PartitionId))
                    throw Error(plan.ImplicitName ? SchematicConnectionErrors.ConnectedImplicitPowerConflict : SchematicConnectionErrors.ConnectedGlobalNameConflict,
                        "Net '" + plan.Net.Name + "' would carry the global name '"
                        + plan.GlobalName + "', but another connection in the editor already carries it. Add the pins to that net in the XML instead.");
            }
            // Every created power pin must land in the net of its own global name, unless that name exists nowhere else.
            var createdSources = created.Where(p => IsGlobalPowerSymbol(p.Symbol) || IsImplicitPower(p.Symbol, p.NativePin)).ToArray();
            var memberOf = new Dictionary<ItemKey, NetPlan>();
            foreach (var plan in plans)
                foreach (var placement in plan.Placements)
                    if (!memberOf.TryAdd(placement.Key, plan))
                        throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Pin " + Describe(placement.Pin.Endpoint)
                            + " belongs to nets '" + memberOf[placement.Key].Net.Name + "' and '" + plan.Net.Name + "' at once.");
            foreach (var placement in createdSources)
            {
                bool carrier = IsGlobalPowerSymbol(placement.Symbol);
                string name = carrier ? CarrierName(placement.Symbol) : ImplicitName(placement.NativePin);
                if (carrier) RequireResolvedPowerName(name, "power symbol " + Describe(placement.Pin.Endpoint));
                string code = carrier ? SchematicConnectionErrors.ConnectedGlobalNameConflict : SchematicConnectionErrors.ConnectedImplicitPowerConflict;
                string what = (carrier ? "Power symbol " : "Hidden power pin ") + Describe(placement.Pin.Endpoint);
                if (memberOf.TryGetValue(placement.Key, out var plan))
                {
                    if (plan.GlobalName != name)
                        throw Error(code, what + " is named '" + name + "' but sits in net '" + plan.Net.Name + "'. Put it in the net of '" + name + "'.");
                    continue;
                }
                bool elsewhere = nativeCarriers.ContainsKey(name) || plans.Any(p => p.GlobalName == name)
                    || createdSources.Any(other => other.Key != placement.Key
                        && (IsGlobalPowerSymbol(other.Symbol) ? CarrierName(other.Symbol) : ImplicitName(other.NativePin)) == name);
                if (elsewhere)
                    throw Error(code, what + " is named '" + name + "', which KiCad connects to every other '" + name
                        + "' in the design, but the XML leaves it out of any net. Put it in the net of '" + name + "'.");
            }
        }

        // Global name -> the native partitions that carry it (global labels and global power pins).
        private Dictionary<string, HashSet<string>> NativeGlobalCarriers()
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (key, item) in nativeItems)
            {
                string? name = item.Kind == ItemKind.GlobalLabel ? item.Text
                    : item.Kind == ItemKind.Pin && IsGlobalPowerPin(item.Symbol!, item.Pin!) ? PowerName(item.Symbol!, item.Pin!) : null;
                if (name is null) continue;
                if (!result.TryGetValue(name, out var partitions)) result.Add(name, partitions = new(StringComparer.Ordinal));
                partitions.Add(PartitionId(key));
            }
            return result;
        }

        // ---- §5.4 and §5.5 islands, ports, joins and label text ----

        private void GlobalIslands(NetPlan plan)
        {
            string name = plan.GlobalName!;
            RequireValidText(name, plan.Net, fromName: false);
            foreach (var group in plan.Members.GroupBy(m => m.Pin.SheetInstanceId).OrderBy(g => pathKeys[g.Key], StringComparer.Ordinal))
            {
                var island = NewIsland(plan, group.Key, ConnectionScope.Global, [.. group]);
                island.Text = name;
                island.Matching = AnchorOn(plan, island.Path).Any(x => (x.Item.Kind == ItemKind.GlobalLabel && x.Item.Text == name)
                    || (x.Item.Kind == ItemKind.Pin && IsGlobalPowerPin(x.Item.Symbol!, x.Item.Pin!) && PowerName(x.Item.Symbol!, x.Item.Pin!) == name));
                RequireLocalCarriers(plan, island);
                plan.Islands.Add(island);
            }
            bool carried = plan.Anchor is not null && plan.Anchor.Items.Any(key => nativeItems.TryGetValue(key, out var item)
                && ((item.Kind == ItemKind.GlobalLabel && item.Text == name)
                    || (item.Kind == ItemKind.Pin && IsGlobalPowerPin(item.Symbol!, item.Pin!) && PowerName(item.Symbol!, item.Pin!) == name)));
            if (plan.Anchor is null || carried) return;
            // The existing connection must be given the global name at exactly one island.
            var connected = plan.Islands.SelectMany(i => i.Members.Where(m => m.AlreadyConnected).Select(m => (Island: i, Member: m)))
                .OrderBy(x => x.Member.Role == ConnectionMemberRole.Signal ? 0 : 1).ThenBy(x => x.Member.Pin.Endpoint.ComponentId)
                .ThenBy(x => x.Member.Pin.Endpoint.Pin, StringComparer.Ordinal).ThenBy(x => x.Member.Pin.PlacedPinId)
                .ThenBy(x => x.Island.Path, StringComparer.Ordinal).FirstOrDefault();
            if (connected.Island is null) throw Inconsistent("Net '" + plan.Net.Name + "' has an existing connection without any of its pins.");
            connected.Island.Join = true;
            connected.Island.JoinCandidates.AddRange(connected.Island.Members
                .Where(m => m.AlreadyConnected && m.Role == ConnectionMemberRole.Signal).Select(m => m.Pin));
        }

        private void LocalIslands(NetPlan plan)
        {
            var bySheet = plan.Members.GroupBy(m => m.Pin.SheetInstanceId).ToDictionary(g => g.Key, g => g.ToList());
            // Tree: every member sheet up to the members' lowest common ancestor.
            var chains = bySheet.Keys.OrderBy(k => pathKeys[k], StringComparer.Ordinal).Select(Chain).ToArray();
            var common = chains[0].ToHashSet();
            foreach (var chain in chains.Skip(1)) common.IntersectWith(chain);
            if (common.Count == 0)
                throw Error(SchematicConnectionErrors.ConnectedHierarchyUnsupported, "Net '" + plan.Net.Name
                    + "' joins sheets that share no parent sheet, so no hierarchical connection can reach all of them.");
            Guid top = chains[0].First(common.Contains);
            var nodes = new SortedDictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var chain in chains)
                foreach (var node in chain.TakeWhile(n => n != top).Append(top)) nodes[pathKeys[node]] = node;
            var islands = new Dictionary<Guid, Island>();
            foreach (var (_, node) in nodes)
            {
                var island = NewIsland(plan, node, ConnectionScope.Local, bySheet.GetValueOrDefault(node) ?? []);
                LabelText(plan, island);
                islands.Add(node, island);
            }
            foreach (var (_, node) in nodes.Where(n => n.Value != top))
            {
                var child = islands[node];
                var parentId = instances[node].ParentId!.Value;
                var parent = islands[parentId];
                var childPath = nativePaths[node];
                if (childPath.Count < 2 || !childPath.Take(childPath.Count - 1).SequenceEqual(nativePaths[parentId]))
                    throw Inconsistent("Sheet '" + SheetName(node) + "' is not bound below its parent sheet.");
                Guid k = childPath[^1];
                if (!nativeItems.TryGetValue(new(parent.Path, k), out var symbolItem) || symbolItem.Kind != ItemKind.SheetSymbol)
                    throw Inconsistent("Sheet '" + SheetName(node) + "' has no sheet symbol in its parent sheet.");
                if (symbolItem.Sheet!.Locked == LockedState.LsLocked)
                    throw Error(SchematicConnectionErrors.ConnectedLockedSheetSymbol, "Net '" + plan.Net.Name + "' must cross into sheet '"
                        + SheetName(node) + "', but its sheet symbol is locked. Unlock it in the schematic editor first.");
                string text = child.Text;
                var anchor = plan.Anchor?.Items ?? new HashSet<ItemKey>();
                var pins = new List<ItemKey>();
                foreach (var pin in symbolItem.Sheet.Pins.Where(p => p.Text?.Text_ == text))
                    pins.Add(Canonical(pin.Id?.Value, out var pinId) ? new(parent.Path, pinId)
                        : throw Inconsistent("A sheet pin of sheet '" + SheetName(node) + "' has no canonical identity."));
                bool pinExists = pins.Any(anchor.Contains);
                if (pins.Any(p => !anchor.Contains(p)))
                    throw Error(SchematicConnectionErrors.ConnectedNetNameConflict, "Net '" + plan.Net.Name + "' needs sheet pin '" + text
                        + "' on sheet '" + SheetName(node) + "', but that sheet already has a pin '" + text
                        + "' for another connection. Rename one of the nets in the XML.");
                bool labelExists = AnchorOn(plan, child.Path).Any(x => x.Item.Kind == ItemKind.HierarchicalLabel && x.Item.Text == text);
                plan.Ports.Add(new(plan.Net.Id, node, child.Path, child.Screen, parentId, parent.Path, parent.Screen, k, text, pinExists, labelExists));
                if (!labelExists) child.Uplink = k;
                if (!pinExists) parent.Children.Add(k);
            }
            foreach (var island in islands.Values.OrderBy(i => i.Path, StringComparer.Ordinal))
            {
                var anchorHere = AnchorOn(plan, island.Path);
                if (anchorHere.Any(x => x.Item.Kind is ItemKind.BusEntry or ItemKind.BusLine))
                    throw Error(SchematicConnectionErrors.ConnectedBusRealizationUnsupported, "Net '" + plan.Net.Name + "' already runs through a bus on sheet '"
                        + SheetName(island.Instance) + "'. Bus connections are not drawn from XML; connect this pin in the schematic editor.");
                island.Matching = anchorHere.Any(x => ((x.Item.Kind is ItemKind.LocalLabel or ItemKind.HierarchicalLabel) && x.Item.Text == island.Text)
                    || (x.Item.Kind == ItemKind.Pin && IsLocalPowerPin(x.Item.Symbol!, x.Item.Pin!) && PowerName(x.Item.Symbol!, x.Item.Pin!) == island.Text));
                bool connected = island.Members.Any(m => m.AlreadyConnected);
                bool needs = island.Stubs || island.Uplink is not null || island.Children.Count != 0;
                island.Join = needs && connected && (!island.Matching || (island.Uplink is not null && !island.Stubs && island.Children.Count == 0));
                if (island.Join)
                    island.JoinCandidates.AddRange(island.Members.Where(m => m.AlreadyConnected && m.Role == ConnectionMemberRole.Signal).Select(m => m.Pin));
                // A crossing needs something on this sheet to carry its hierarchical label: a new stub, a new sheet
                // pin, or a join at an existing pin. A sheet reached only through an existing sheet pin has none.
                if (needs && !island.Stubs && island.Children.Count == 0 && !island.Join)
                    throw Error(SchematicConnectionErrors.ConnectedHierarchyUnsupported, "Net '" + plan.Net.Name + "' must continue through sheet '"
                        + SheetName(island.Instance) + "', which has no pin of the net where a hierarchical label could be attached. Connect it there in the schematic editor.");
                plan.Islands.Add(island);
            }
        }

        private List<Guid> Chain(Guid instance)
        {
            var chain = new List<Guid>();
            var seen = new HashSet<Guid>();
            for (Guid? current = instance; current is { } id; current = instances.GetValueOrDefault(id)?.ParentId)
            {
                if (!instances.ContainsKey(id) || !pathKeys.ContainsKey(id) || !seen.Add(id))
                    throw Error(SchematicConnectionErrors.ConnectedHierarchyUnsupported, "Sheet instance " + id.ToString("D") + " has no bound parent chain.");
                chain.Add(id);
            }
            return chain;
        }

        private Island NewIsland(NetPlan plan, Guid instance, ConnectionScope scope, List<ConnectionMember> members)
        {
            string path = pathKeys[instance];
            if (!screenOfPath.TryGetValue(path, out var screen)) throw Inconsistent("A net sheet has no captured screen.");
            var island = new Island { NetId = plan.Net.Id, Instance = instance, Path = path, Screen = screen,
                Scope = scope, Members = [.. members.OrderBy(m => m.Pin.Endpoint.ComponentId).ThenBy(m => m.Pin.Endpoint.Pin, StringComparer.Ordinal)
                    .ThenBy(m => m.Pin.PlacedPinId).ThenBy(m => m.Pin.SheetPathKey, StringComparer.Ordinal)] };
            island.AnchorItems.AddRange(AnchorOn(plan, path).Select(x => x.Key.Id));
            return island;
        }

        // §5.5: local carrier value, then the ordinal-first existing label, then the net's own name.
        private void LabelText(NetPlan plan, Island island)
        {
            RequireLocalCarriers(plan, island);
            var carriers = LocalCarrierValues(plan, island);
            if (carriers.Length == 1) island.Text = carriers[0];
            else if (AnchorOn(plan, island.Path).Where(x => x.Item.Kind is ItemKind.LocalLabel or ItemKind.HierarchicalLabel)
                .Select(x => x.Item.Text).Order(StringComparer.Ordinal).FirstOrDefault() is { } label) island.Text = label;
            else { island.Text = LocalName(plan.Net.Name); island.TextFromName = true; }
            RequireValidText(island.Text, plan.Net, island.TextFromName);
            // An existing local driver with this text on the sheet belongs to another connection.
            var anchor = plan.Anchor?.Items ?? new HashSet<ItemKey>();
            foreach (var (key, item) in nativeByPath.GetValueOrDefault(island.Path) ?? [])
            {
                bool named = ((item.Kind is ItemKind.LocalLabel or ItemKind.HierarchicalLabel) && item.Text == island.Text)
                    || (item.Kind == ItemKind.Pin && IsLocalPowerPin(item.Symbol!, item.Pin!) && PowerName(item.Symbol!, item.Pin!) == island.Text);
                if (named && !anchor.Contains(key))
                    throw Error(SchematicConnectionErrors.ConnectedNetNameConflict, "Net '" + plan.Net.Name + "' would be labelled '" + island.Text
                        + "' on sheet '" + SheetName(island.Instance) + "', but that sheet already uses '" + island.Text
                        + "' for another connection. Rename the net in the XML.");
            }
        }

        private string[] LocalCarrierValues(NetPlan plan, Island island) => island.Members
            .Where(m => m.Role == ConnectionMemberRole.PowerCarrier && !IsGlobalPowerSymbol(PlacementOf(plan, m).Symbol))
            .Select(m => m.PowerName!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        // Local carriers on one island share one value, and it is the island's label text.
        private void RequireLocalCarriers(NetPlan plan, Island island)
        {
            var values = LocalCarrierValues(plan, island);
            foreach (var value in values) RequireResolvedPowerName(value, "a local power symbol of net '" + plan.Net.Name + "'");
            if (values.Length > 1 || (plan.Scope == ConnectionScope.Global && values.Length == 1 && values[0] != plan.GlobalName))
                throw Error(SchematicConnectionErrors.ConnectedNetNameConflict, "Net '" + plan.Net.Name + "' on sheet '" + SheetName(island.Instance)
                    + "' has power symbols named " + string.Join(", ", values.Append(plan.GlobalName ?? "").Where(v => v.Length != 0).Distinct(StringComparer.Ordinal).Select(v => "'" + v + "'"))
                    + ", which cannot name one connection. Keep one power name per net.");
        }

        // ---- §5.5 conflicts across islands and §5.6 repeated screens ----

        private void SharedScreens(List<NetPlan> plans)
        {
            var islands = plans.SelectMany(p => p.Islands).Where(i => i.Needs).ToArray();
            // Two nets drawn with one local text on one sheet instance join there.
            foreach (var group in islands.Where(i => i.Scope == ConnectionScope.Local).GroupBy(i => (i.Path, i.Text)))
                if (group.Select(i => i.NetId).Distinct().Count() > 1)
                    throw Error(SchematicConnectionErrors.ConnectedNetNameConflict, "Nets " + string.Join(" and ", group.Select(i => "'" + NetName(plans, i.NetId) + "'").Distinct(StringComparer.Ordinal))
                        + " would both be labelled '" + group.Key.Text + "' on sheet '" + SheetName(group.First().Instance) + "', which joins them. Give them different names in the XML.");
            foreach (var screen in screenOfPath.GroupBy(p => p.Value).OrderBy(g => g.Key))
            {
                token.ThrowIfCancellationRequested();
                var paths = screen.Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
                var needing = islands.Where(i => i.Screen == screen.Key).ToArray();
                if (paths.Length < 2 || needing.Length == 0) continue;
                // Every instance of a repeated sheet shows the same generated labels, so different nets drawn at
                // different pins with one text would join on every instance.
                foreach (var byText in needing.Where(i => i.Scope == ConnectionScope.Local).GroupBy(i => i.Text, StringComparer.Ordinal))
                    foreach (var a in byText)
                        if (byText.FirstOrDefault(other => other.NetId != a.NetId && other.PhysicalIdentity() != a.PhysicalIdentity()) is { } clash)
                            throw Error(SchematicConnectionErrors.ConnectedNetNameConflict, "Nets '" + NetName(plans, a.NetId) + "' and '" + NetName(plans, clash.NetId)
                                + "' would both be labelled '" + a.Text + "' on the repeated sheet '" + SheetName(a.Instance)
                                + "', where every instance shows the same labels. Give them different names in the XML.");
                // The same physical island must carry the same text on every instance (§5.5).
                foreach (var byIdentity in needing.GroupBy(i => i.PhysicalIdentity(), StringComparer.Ordinal))
                {
                    var texts = byIdentity.GroupBy(i => i.Path, StringComparer.Ordinal)
                        .Select(g => string.Join("\n", g.Select(i => i.Text).Order(StringComparer.Ordinal))).ToArray();
                    bool sameCount = byIdentity.GroupBy(i => i.Path, StringComparer.Ordinal).Select(g => g.Count()).Distinct().Count() == 1;
                    if (sameCount && texts.Distinct(StringComparer.Ordinal).Count() > 1)
                        throw Error(SchematicConnectionErrors.ConnectedLabelTextDivergent, "The repeated sheet '" + SheetName(byIdentity.First().Instance)
                            + "' shows one set of labels on every instance, but the nets drawn there would need different names ("
                            + string.Join(", ", byIdentity.Select(i => "'" + i.Text + "'").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                            + "). Give each instance's net the same name after its last '/'.");
                }
                string Set(string path) => string.Join("\n", needing.Where(i => i.Path == path).Select(i => i.PhysicalKey()).Order(StringComparer.Ordinal));
                string first = Set(paths[0]);
                bool divergent = paths.Skip(1).Any(p => Set(p) != first);
                // A shared symbol whose instances select different units shows different pins per instance.
                var involved = needing.SelectMany(i => i.Members).Select(m => m.Pin.SymbolId).Distinct().ToArray();
                divergent |= involved.Any(symbol => paths.Select(p => candidateSymbols.TryGetValue(new(p, symbol), out var copy) ? copy.Unit?.Unit ?? -1 : -1)
                    .Distinct().Count() > 1);
                if (divergent)
                    throw Error(SchematicConnectionErrors.ConnectedRepeatedScreenDivergent, "Sheet '" + SheetName(needing[0].Instance) + "' is used "
                        + paths.Length.ToString(CultureInfo.InvariantCulture) + " times and every use shows the same drawing, but the XML connects its pins differently in each use. "
                        + "Make the connections the same for every instance of the sheet, or make it a separate sheet.");
            }
        }

        // ---- §5.7 variant gate ----

        private void Variants(List<NetPlan> plans, List<Placement> created)
        {
            var symbols = new List<(SchematicSymbolInstance Symbol, string What)>();
            foreach (var plan in plans)
            {
                var byKey = plan.Placements.ToDictionary(p => p.Key);
                foreach (var island in plan.Islands)
                {
                    foreach (var member in island.Members.Where(m => m.RequiresStub || m.Role == ConnectionMemberRole.PowerCarrier))
                        symbols.Add((byKey[new(member.Pin.SheetPathKey, member.Pin.PlacedPinId)].Symbol, Describe(member.Pin.Endpoint)));
                    foreach (var pin in island.JoinCandidates)
                        symbols.Add((byKey[new(pin.SheetPathKey, pin.PlacedPinId)].Symbol, Describe(pin.Endpoint)));
                }
            }
            symbols.AddRange(created.Select(p => (p.Symbol, Describe(p.Pin.Endpoint))));
            foreach (var (symbol, what) in symbols)
            {
                var reference = symbol.LibraryId ?? symbol.Definition?.Id ?? new LibraryIdentifier();
                IEnumerable<SchematicSymbolVariant> variants = symbol.Variants?.Variants ?? Enumerable.Empty<SchematicSymbolVariant>();
                if (symbol.InstanceRecords is not null)
                    variants = variants.Concat(symbol.InstanceRecords.Records.SelectMany(r => r.Variants?.Variants ?? Enumerable.Empty<SchematicSymbolVariant>()));
                var overridden = variants.FirstOrDefault(v => v.SymbolOverride is { } o && o.EntryName.Length != 0
                    && (o.EntryName != reference.EntryName || o.LibraryNickname != reference.LibraryNickname));
                if (overridden is not null)
                    throw Error(SchematicConnectionErrors.ConnectedVariantSymbolUnsupported, "Pin " + what + " belongs to a symbol whose design variant '"
                        + overridden.Name + "' swaps in another library symbol. Its pins cannot be connected exactly from XML yet; connect it in the schematic editor.");
            }
        }

        // ---- §5.8 expected native groups ----

        private List<IReadOnlyList<ConnectionPinKey>> ExpectedGroups(List<NetPlan> plans, List<Placement> created)
        {
            var owner = new Dictionary<ItemKey, int>();
            var groups = new List<SortedSet<ItemKey>>();
            var order = Comparer<ItemKey>.Create((a, b) => string.CompareOrdinal(a.Path, b.Path) is var c and not 0 ? c : a.Id.CompareTo(b.Id));
            void Claim(ItemKey key, int group, string net)
            {
                if (owner.TryGetValue(key, out int other) && other != group)
                    throw Error(SchematicConnectionErrors.ConnectedInternalInconsistency, "Placed pin " + key.Id.ToString("D") + " would belong to net '"
                        + net + "' and to another connection at once.");
                owner[key] = group; groups[group].Add(key);
            }
            foreach (var plan in plans)
            {
                groups.Add(new SortedSet<ItemKey>(order));
                int index = groups.Count - 1;
                foreach (var placement in plan.Placements)
                {
                    Claim(placement.Key, index, plan.Net.Name);
                    if (netOf.TryGetValue(placement.Key, out int net))
                        foreach (var key in netItems[net].Where(k => nativeItems.TryGetValue(k, out var item) && item.Kind == ItemKind.Pin))
                            Claim(key, index, plan.Net.Name);
                }
            }
            foreach (var placement in created.Where(p => !owner.ContainsKey(p.Key)))
            {
                groups.Add(new SortedSet<ItemKey>(order) { placement.Key });
                owner[placement.Key] = groups.Count - 1;
            }
            return [.. groups.Select(g => (IReadOnlyList<ConnectionPinKey>)g.Select(k => new ConnectionPinKey(k.Path, k.Id)).ToArray())
                .OrderBy(g => g[0].SheetPathKey, StringComparer.Ordinal).ThenBy(g => g[0].PlacedPinId)];
        }

        // ---- §5.9 records ----

        private SchematicConnectionIntent Assemble(List<NetPlan> plans, List<IReadOnlyList<ConnectionPinKey>> groups)
        {
            var islands = plans.SelectMany(p => p.Islands).ToArray();
            var screens = new List<ConnectionScreen>();
            var createdScreens = candidateSymbols.Where(s => createdSymbols.Contains(s.Key.Id)).Select(s => screenOfPath[s.Key.Path]).ToHashSet();
            foreach (var screen in screenOfPath.GroupBy(p => p.Value).OrderBy(g => g.Key))
            {
                var paths = screen.Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
                bool realized = islands.Any(i => i.Screen == screen.Key && i.Needs);
                if (!realized && !createdScreens.Contains(screen.Key)) continue;
                screens.Add(new(screen.Key, paths, islands.Where(i => i.Screen == screen.Key && i.Path == paths[0] && i.Needs)
                    .OrderBy(i => i.NetId).ThenBy(i => i.Path, StringComparer.Ordinal).Select(i => i.Record()).ToArray()));
            }
            var nets = plans.Select(p => new ConnectionNet(p.Net.Id, p.Net.Name, p.Scope, p.GlobalName, p.Added.ToArray())).ToArray();
            var ports = plans.SelectMany(p => p.Ports).OrderBy(p => p.NetId).ThenBy(p => p.ChildPathKey, StringComparer.Ordinal).ToArray();
            string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(state.DesiredFileBytes));
            return new(SchematicConnectionIntent.CurrentVersion, state.OriginId, circuit.Id,
                new(state.NativeRevision.Epoch, state.NativeRevision.Sequence), sha, nets, screens, ports, groups,
                createdSymbols.Order().ToArray());
        }

        // ---- native index ----

        private void IndexNative(SchematicElectricalState observed)
        {
            foreach (var screen in observed.Hierarchy?.Data?.Instances ?? Enumerable.Empty<SchematicScreenData>())
            {
                string path = PathOf(screen.Metadata?.Document?.SheetPath);
                foreach (var packed in screen.Items)
                {
                    if (packed.Is(SchematicSymbolInstance.Descriptor))
                    {
                        var symbol = packed.Unpack<SchematicSymbolInstance>();
                        if (!Canonical(symbol.Id?.Value, out var id)) continue;
                        Add(new(path, id), new(ItemKind.Symbol, "", null, symbol, null, null));
                        if (!symbol.SeparatePinIdentities || symbol.Definition is null) continue;
                        foreach (var pin in SchematicPlacedPins.Active(symbol, symbol.Unit?.Unit ?? 1))
                            if (Canonical(pin.Id?.Value, out var pinId)) Add(new(path, pinId), new(ItemKind.Pin, "", id, symbol, pin, null));
                    }
                    else if (packed.Is(SheetSymbol.Descriptor))
                    {
                        var sheet = packed.Unpack<SheetSymbol>();
                        if (!Canonical(sheet.Id?.Value, out var id)) continue;
                        Add(new(path, id), new(ItemKind.SheetSymbol, "", null, null, null, sheet));
                        foreach (var pin in sheet.Pins)
                            if (Canonical(pin.Id?.Value, out var pinId)) Add(new(path, pinId), new(ItemKind.SheetPin, pin.Text?.Text_ ?? "", id, null, null, sheet));
                    }
                    else if (packed.Is(LocalLabel.Descriptor))
                    { var label = packed.Unpack<LocalLabel>(); Label(label.Id, label.Text, ItemKind.LocalLabel); }
                    else if (packed.Is(GlobalLabel.Descriptor))
                    { var label = packed.Unpack<GlobalLabel>(); Label(label.Id, label.Text, ItemKind.GlobalLabel); }
                    else if (packed.Is(HierarchicalLabel.Descriptor))
                    { var label = packed.Unpack<HierarchicalLabel>(); Label(label.Id, label.Text, ItemKind.HierarchicalLabel); }
                    else if (packed.Is(BusEntry.Descriptor))
                    { if (Canonical(packed.Unpack<BusEntry>().Id?.Value, out var id)) Add(new(path, id), new(ItemKind.BusEntry, "", null, null, null, null)); }
                    else if (packed.Is(SchematicLine.Descriptor))
                    {
                        var line = packed.Unpack<SchematicLine>();
                        if (Canonical(line.Id?.Value, out var id))
                            Add(new(path, id), new(line.Type == SchematicLineType.SltBus ? ItemKind.BusLine : ItemKind.Other, "", null, null, null, null));
                    }
                }

                void Label(KIID? id, Text? text, ItemKind kind)
                {
                    if (Canonical(id?.Value, out var item)) Add(new(path, item), new(kind, text?.Text_ ?? "", null, null, null, null));
                }

                void Add(ItemKey key, NativeItem item)
                {
                    if (!nativeItems.TryAdd(key, item)) throw Inconsistent("The observed schematic repeats item " + key.Id.ToString("D") + ".");
                    if (!nativeByPath.TryGetValue(key.Path, out var list)) nativeByPath.Add(key.Path, list = []);
                    list.Add((key, item));
                }
            }
            foreach (var net in observed.Nets)
            {
                var items = new List<ItemKey>();
                foreach (var sheet in net.Sheets)
                {
                    string path = PathOf(sheet.Path);
                    foreach (var id in sheet.Items)
                    {
                        if (!Canonical(id.Value, out var item)) throw Inconsistent("The observed pin partition has a non-canonical item identity.");
                        var key = new ItemKey(path, item);
                        if (!netOf.TryAdd(key, netItems.Count)) throw Inconsistent("The observed pin partition lists item " + item.ToString("D") + " twice.");
                        items.Add(key);
                    }
                }
                netItems.Add(items);
            }
        }

        // ---- helpers ----

        private string PartitionId(ItemKey key) => netOf.TryGetValue(key, out int index)
            ? "net:" + index.ToString(CultureInfo.InvariantCulture) : "item:" + key.Path + "/" + key.Id.ToString("D");

        private static readonly NativeItem Unindexed = new(ItemKind.Other, "", null, null, null, null);

        private List<(ItemKey Key, NativeItem Item)> AnchorOn(NetPlan plan, string path)
        {
            var result = new List<(ItemKey Key, NativeItem Item)>();
            if (plan.Anchor is null) return result;
            foreach (var key in plan.Anchor.Items.Where(k => k.Path == path).OrderBy(k => k.Id))
                result.Add((key, nativeItems.TryGetValue(key, out var item) ? item : Unindexed));
            return result;
        }

        private static Placement PlacementOf(NetPlan plan, ConnectionMember member) => plan.ByKey[new(member.Pin.SheetPathKey, member.Pin.PlacedPinId)];

        private string Describe(PinEndpoint endpoint) =>
            (components.TryGetValue(endpoint.ComponentId, out var component) ? component.Reference : endpoint.ComponentId.ToString("D")) + "." + endpoint.Pin;

        private string SheetName(Guid instance) => instances.TryGetValue(instance, out var sheet)
            ? circuit.Sheets.FirstOrDefault(s => s.Id == sheet.DefinitionId)?.Name ?? instance.ToString("D") : instance.ToString("D");

        private static string NetName(List<NetPlan> plans, Guid net) => plans.First(p => p.Net.Id == net).Net.Name;

        private static IEnumerable<PinEndpoint> Ordered(IEnumerable<PinEndpoint> pins) =>
            pins.OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal);

        private static string PathOf(SheetPath? path) => path is null ? "" : string.Join('/', path.Path.Select(id => id.Value));

        private static bool Canonical(string? value, out Guid id) =>
            Guid.TryParseExact(value, "D", out id) && id != Guid.Empty && value == id.ToString("D");

        private static AutomationException Inconsistent(string message) => Error(SchematicConnectionErrors.ConnectedInternalInconsistency,
            message + " Refresh the observation and plan again.");

        private static void RequireResolvedPowerName(string? name, string what)
        {
            if (string.IsNullOrEmpty(name) || name.Contains("${", StringComparison.Ordinal))
                throw Error(SchematicConnectionErrors.ConnectedPowerNameUnresolved, "The name of " + what + " is '" + (name ?? "")
                    + "', which is empty or still contains a text variable. Give it a literal power name.");
        }

        private static void RequireValidText(string text, CircuitNet net, bool fromName)
        {
            if (fromName && (net.Name.Contains("Net-(", StringComparison.Ordinal) || net.Name.Contains("unconnected-(", StringComparison.Ordinal)
                || GeneratedName().IsMatch(net.Name)))
                throw Error(SchematicConnectionErrors.ConnectedLabelTextInvalid, "Net '" + net.Name
                    + "' has an automatically generated name, which cannot label a new connection. Give the net an explicit name in the XML.");
            if (!ValidLabelText(text))
                throw Error(SchematicConnectionErrors.ConnectedLabelTextInvalid, "Net '" + net.Name + "' would be labelled '" + text
                    + "', which is not a valid label: use 1 to 128 characters without spaces, control characters or any of { } [ ] / \\ $ ~ ^ , \" and not starting with #.");
        }

        private static bool IsPowerSymbol(SchematicSymbolInstance symbol) =>
            symbol.Definition?.Type is SchematicSymbolType.SstGlobalPower or SchematicSymbolType.SstLocalPower;

        private static bool IsGlobalPowerSymbol(SchematicSymbolInstance symbol) => symbol.Definition?.Type == SchematicSymbolType.SstGlobalPower;

        private static string CarrierName(SchematicSymbolInstance symbol) => symbol.ValueField?.Text?.Text_ ?? "";

        // KiCad applies an active alternate's electrical type and name.
        private static ElectricalPinType EffectiveType(SchematicPin pin) => pin.HasActiveAlternate && pin.ActiveAlternate.Length != 0
            ? pin.Alternates.FirstOrDefault(a => a.Name == pin.ActiveAlternate)?.ElectricalType ?? ElectricalPinType.EptUnspecified
            : pin.ElectricalType;

        private static string ImplicitName(SchematicPin pin) => pin.HasActiveAlternate && pin.ActiveAlternate.Length != 0 ? pin.ActiveAlternate : pin.Name;

        // A hidden power input on an ordinary symbol joins the global net of its name (legacy implicit power).
        private static bool IsImplicitPower(SchematicSymbolInstance symbol, SchematicPin pin) =>
            !IsPowerSymbol(symbol) && !pin.Visible && EffectiveType(pin) == ElectricalPinType.EptPowerInput;

        private static bool IsGlobalPowerPin(SchematicSymbolInstance symbol, SchematicPin pin) =>
            EffectiveType(pin) == ElectricalPinType.EptPowerInput
            && (IsGlobalPowerSymbol(symbol) || (symbol.Definition?.Type != SchematicSymbolType.SstLocalPower && !pin.Visible));

        private static bool IsLocalPowerPin(SchematicSymbolInstance symbol, SchematicPin pin) =>
            EffectiveType(pin) == ElectricalPinType.EptPowerInput && symbol.Definition?.Type == SchematicSymbolType.SstLocalPower;

        private static string PowerName(SchematicSymbolInstance symbol, SchematicPin pin) => IsPowerSymbol(symbol) ? CarrierName(symbol) : ImplicitName(pin);

        [GeneratedRegex("^NET-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
        private static partial Regex GeneratedName();
    }
}
