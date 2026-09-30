using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

internal sealed record SchematicNativeCreationResult(SchematicDesign Candidate,
    IReadOnlyList<SchematicItemOperation> Operations, IReadOnlyList<Guid> CreatedOccurrences);

/// <summary>The physical native symbol that created occurrences share: one definition and unit
/// on one physical screen. <paramref name="Component"/> is set only when several components place
/// that unit on a screen with a single sheet instance, where each needs its own symbol.</summary>
internal readonly record struct SchematicCreatedSymbolKey(string PhysicalScreen, Guid Definition, int Unit, Guid? Component);

internal sealed record SchematicCreatedSymbolGroup(SchematicCreatedSymbolKey Key, IReadOnlyList<SymbolOccurrence> Occurrences);

/// <summary>
/// Creates the native representation for a narrow, unambiguous XML-first
/// addition. The new component must use an exact part/unit template or an explicitly
/// declared standalone symbol definition, have
/// explicit placement, and have no new net membership unless an admitted connected
/// addition asks for it (<c>allowConnected</c>). Each unit is created on the
/// sheet its occurrence names, which may differ from the component's own sheet; all
/// units keep the one component identity, reference and definition. Connectivity-changing
/// creation remains a separate ownership operation and is never inferred here: this
/// projection only places the symbols, and the connected-addition plan realizes and
/// proves their connections.
/// </summary>
internal static class SchematicNativeCreationProjection
{
    internal static bool IsSupportedAddition(SchematicDesign baseline, EngineeringDesign desired)
    {
        var oldCircuit = baseline.Engineering.Circuit;
        var newCircuit = desired.Circuit;
        var oldComponents = oldCircuit.Components.ToDictionary(c => c.Id);
        var newComponents = newCircuit.Components.ToDictionary(c => c.Id);
        bool added = newComponents.Keys.Except(oldComponents.Keys).Any();
        bool removed = oldComponents.Keys.Except(newComponents.Keys).Any();
        return added && !removed && oldCircuit.Id == newCircuit.Id
            && oldCircuit.Parts.All(part => newCircuit.Parts.SingleOrDefault(p => p.Id == part.Id) is { } next
                && NormalizePart(part) == NormalizePart(next))
            && oldCircuit.SheetInstances.OrderBy(instance => instance.Id).SequenceEqual(newCircuit.SheetInstances.OrderBy(instance => instance.Id))
            && oldCircuit.Nets.OrderBy(net => net.Id).Select(NormalizeNet)
                .SequenceEqual(newCircuit.Nets.OrderBy(net => net.Id).Select(NormalizeNet))
            && oldCircuit.Components.All(c => newComponents.TryGetValue(c.Id, out var current) && c.Equals(current))
            && oldCircuit.Symbols.All(s => newCircuit.Symbols.Any(current => current.Id == s.Id && current.Equals(s)))
            && oldCircuit.Sheets.All(sheet => newCircuit.Sheets.SingleOrDefault(current => current.Id == sheet.Id) is { } current
                && sheet.Components.All(component => current.Components.Any(next => next.Id == component.Id && next.Equals(component))));

        static string NormalizeNet(CircuitNet net) => JsonSerializer.Serialize(new
        {
            net.Id,
            net.Name,
            Pins = net.Pins.OrderBy(pin => pin.ComponentId).ThenBy(pin => pin.Pin, StringComparer.Ordinal).ToArray()
        });
    }

    /// <summary>Order-independent part identity used by the creation and connected-addition
    /// shape checks (cn1-wiring-intent.md §4.1): name, unit count and exact (number, name, unit) pins.</summary>
    internal static string NormalizePart(PartDefinition part) => JsonSerializer.Serialize(new
    {
        part.Id,
        part.Name,
        part.Units,
        Pins = part.Pins.OrderBy(pin => pin.Number, StringComparer.Ordinal)
            .ThenBy(pin => pin.Unit).Select(pin => new { pin.Number, pin.Name, pin.Unit }).ToArray()
    });

    /// <param name="allowConnected">Only for an admitted connected addition (cn1-wiring-intent.md §4.3):
    /// skip exactly the <c>created_component_connectivity_requires_resolution</c> refusal, because that
    /// plan realizes the new pins' connections natively in the same batch. Every other check still applies,
    /// including <see cref="RequireUndeclaredPowerPinsAlone"/>, which then counts the nets the XML declares.</param>
    internal static SchematicNativeCreationResult Project(SchematicDesign baseline, EngineeringDesign desired,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken token = default, bool allowConnected = false)
        => Project(baseline, baseline with { Engineering = desired }, libraries, token, allowConnected);

    /// <inheritdoc cref="Project(SchematicDesign, EngineeringDesign, IReadOnlyCollection{ComponentKnowledgeLibrary}, CancellationToken, bool)"/>
    internal static SchematicNativeCreationResult Project(SchematicDesign baseline, SchematicDesign desiredDesign,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken token = default, bool allowConnected = false)
    {
        var desired = desiredDesign.Engineering;
        token.ThrowIfCancellationRequested();
        baseline.Engineering.Validate(libraries);
        desired.Validate(libraries);
        SchematicPartSymbols.Validate(desiredDesign, token);
        var declared = (desiredDesign.PartSymbols ?? []).ToDictionary(s => s.PartId);
        var baselineReport = SchematicDesignBindings.Inspect(baseline, libraries, token);
        if (!baselineReport.IdentitiesResolved)
            throw Invalid("unresolved_design_bindings", "The existing design must have exact native bindings before creating a component.");

        var oldCircuit = baseline.Engineering.Circuit;
        var newCircuit = desired.Circuit;
        var oldParts = oldCircuit.Parts.ToDictionary(p => p.Id);
        var newParts = newCircuit.Parts.ToDictionary(p => p.Id);
        if (oldParts.Any(pair => !newParts.TryGetValue(pair.Key, out var next) || !SamePart(pair.Value, next)))
            throw Invalid("part_definition_change_requires_resolution", "Preserve existing part definitions while creating new components.");
        if (newParts.Keys.Except(oldParts.Keys).Any(id => !declared.ContainsKey(id)))
            throw Invalid("new_part_requires_library_definition", "Creating a new part requires an explicit native library definition.");
        if (!oldCircuit.SheetInstances.OrderBy(instance => instance.Id).SequenceEqual(newCircuit.SheetInstances.OrderBy(instance => instance.Id))
            || !oldCircuit.Sheets.Select(s => s.Id).ToHashSet().SetEquals(newCircuit.Sheets.Select(s => s.Id)))
            throw Invalid("sheet_ownership_change_requires_resolution", "Sheet insertion, removal or reparenting requires explicit ownership reconciliation.");

        var oldSheets = oldCircuit.Sheets.ToDictionary(s => s.Id);
        var newSheets = newCircuit.Sheets.ToDictionary(s => s.Id);
        var addedDefinitions = new HashSet<Guid>();
        foreach (var oldSheet in oldCircuit.Sheets)
        {
            if (!newSheets.TryGetValue(oldSheet.Id, out var newSheet))
                throw Invalid("sheet_ownership_change_requires_resolution", "An existing sheet definition disappeared.");
            var oldDefinitions = oldSheet.Components.ToDictionary(c => c.Id);
            var newDefinitions = newSheet.Components.ToDictionary(c => c.Id);
            foreach (var (id, definition) in oldDefinitions)
                if (!newDefinitions.TryGetValue(id, out var current) || !definition.Equals(current))
                    throw Invalid("component_rebinding_requires_resolution", "Changing an existing component definition is not inferred during creation.");
            foreach (var definition in newSheet.Components.Where(c => !oldDefinitions.ContainsKey(c.Id)))
                if (!addedDefinitions.Add(definition.Id))
                    throw Invalid("duplicate_component_definition", "A new component definition must have one exact owning sheet.");
        }

        var oldComponents = oldCircuit.Components.ToDictionary(c => c.Id);
        var newComponents = newCircuit.Components.ToDictionary(c => c.Id);
        var addedComponents = newCircuit.Components.Where(c => !oldComponents.ContainsKey(c.Id)).ToArray();
        foreach (var oldComponent in oldCircuit.Components)
            if (!newComponents.TryGetValue(oldComponent.Id, out var current) || !oldComponent.Equals(current))
                throw Invalid("component_rebinding_requires_resolution", "Changing an existing component owner or reference requires explicit ownership reconciliation.");
        if (addedComponents.Length == 0)
            throw Invalid("no_component_creation", "The requested design contains no new component instance.");
        foreach (var component in addedComponents)
        {
            if (!addedDefinitions.Contains(component.DefinitionId))
                throw Invalid("component_definition_required", "A new component must introduce an exact component definition.");
            if (!newCircuit.SheetInstances.Any(s => s.Id == component.SheetInstanceId))
                throw Invalid("unknown_component_sheet", "A new component must target an existing sheet instance.");
            if (!allowConnected && newCircuit.Nets.Any(n => n.Pins.Any(pin => pin.ComponentId == component.Id)))
                throw Invalid("created_component_connectivity_requires_resolution", "A connected component requires an explicit native wiring operation.");
        }

        var oldSymbols = oldCircuit.Symbols.ToDictionary(s => s.Id);
        var newSymbols = newCircuit.Symbols.ToDictionary(s => s.Id);
        foreach (var oldSymbol in oldCircuit.Symbols)
            if (!newSymbols.TryGetValue(oldSymbol.Id, out var current) || !oldSymbol.Equals(current))
                throw Invalid("symbol_rebinding_requires_resolution", "Changing an existing symbol occurrence is not inferred during creation.");
        var addedSymbols = newCircuit.Symbols.Where(s => !oldSymbols.ContainsKey(s.Id)).ToArray();
        var addedComponentIds = addedComponents.Select(c => c.Id).ToHashSet();
        if (addedSymbols.Any(s => !addedComponentIds.Contains(s.ComponentId)))
            throw Invalid("symbol_owner_requires_resolution", "A new symbol occurrence must belong to a new component instance.");
        foreach (var component in addedComponents)
        {
            var definition = newSheets.Values.SelectMany(s => s.Components).Single(c => c.Id == component.DefinitionId);
            var part = newParts[definition.PartId];
            var occurrences = addedSymbols.Where(s => s.ComponentId == component.Id).ToArray();
            if (occurrences.Select(s => s.Unit).Distinct().Count() != occurrences.Length
                || !occurrences.Select(s => s.Unit).ToHashSet().SetEquals(Enumerable.Range(1, part.Units)))
                throw Invalid("component_units_incomplete", "A created component must provide exactly one native symbol occurrence for every declared unit.");
        }

        var existingPaths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId,
            b => SchematicDesignBindings.PathKey(b.NativePath));
        var nativeIds = baseline.Schematic.Instances.SelectMany(s => s.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>().Id.Value))
            .ToHashSet(StringComparer.Ordinal);
        var bindings = baseline.SymbolBindings.ToList();
        var augmented = baseline.Schematic.Clone();
        var screens = augmented.Instances.ToDictionary(s => Path(s.Metadata.Document), StringComparer.Ordinal);
        var created = new List<Guid>();
        // A unit may be placed on another sheet than its component (EffectiveSheetInstanceId);
        // it then lands on that sheet's physical screen while keeping the component identity.
        foreach (var group in PhysicalSymbols(baseline, newCircuit, addedSymbols, token))
        {
            token.ThrowIfCancellationRequested();
            var occurrences = group.Occurrences.ToArray();
            var representative = occurrences[0];
            if (occurrences.Any(s => s.Placement is null))
                throw Invalid("created_symbol_placement_required", "Coordinate-free creation needs layout resolution before a native symbol can be inserted.");
            if (occurrences.Skip(1).Any(s => !SchematicOrientation.Equivalent(s.Placement, representative.Placement)))
                throw Invalid("shared_symbol_placement_conflict", "Repeated instances of one physical symbol must request the same placement.");
            string nativeId = StablePhysicalId(group.Key);
            if (nativeIds.Contains(nativeId)) throw Invalid("created_native_identity_collision", "The deterministic native identity is already in use.");
            var representativeComponent = newComponents[representative.ComponentId];
            var representativeDefinition = newSheets.Values.SelectMany(s => s.Components).Single(c => c.Id == representativeComponent.DefinitionId);
            var part = newParts[representativeDefinition.PartId];
            var declaration = declared.GetValueOrDefault(part.Id);
            var template = declaration is null
                ? FindTemplate(baseline, oldCircuit, oldComponents, oldSheets, representative, part, screens, existingPaths, token)
                : (Symbol: InstantiateDeclaration(declaration), Path: "");
            var createdSymbols = new List<SchematicSymbolInstance>();
            var targetScreens = new List<SchematicScreenData>();
            foreach (var occurrence in occurrences)
            {
                token.ThrowIfCancellationRequested();
                var component = newComponents[occurrence.ComponentId];
                var definition = newSheets.Values.SelectMany(s => s.Components).Single(c => c.Id == component.DefinitionId);
                Guid sheetInstance = occurrence.EffectiveSheetInstanceId(component);
                if (!existingPaths.TryGetValue(sheetInstance, out var path) || !screens.TryGetValue(path, out var target))
                    throw Invalid("missing_native_sheet", "A created symbol must target a bound existing sheet instance.");
                var symbol = CreateSymbol(template.Symbol, target, occurrence, component, definition.Value, part, nativeIds, nativeId, token);
                if (declaration is null) CopyLibraryCache(screens[template.Path], target, template.Symbol);
                else CopyLibraryCache(declaration.Symbol, target);
                target.Items.Add(Any.Pack(symbol));
                createdSymbols.Add(symbol); targetScreens.Add(target);
                bindings.Add(new(occurrence.Id, Guid.Parse(symbol.Id.Value)));
                created.Add(occurrence.Id);
            }
            var records = new SymbolSheetRecords();
            foreach (var occurrence in occurrences
                .OrderBy(s => screens[existingPaths[s.EffectiveSheetInstanceId(newComponents[s.ComponentId])]].Metadata.Document.SheetPath.Path.Count)
                .ThenBy(s => existingPaths[s.EffectiveSheetInstanceId(newComponents[s.ComponentId])], StringComparer.Ordinal))
            {
                var component = newComponents[occurrence.ComponentId];
                var path = existingPaths[occurrence.EffectiveSheetInstanceId(component)];
                var target = screens[path];
                var record = new SymbolSheetRecord { ProjectName = target.Metadata.Document.Project.Name,
                    Reference = component.Reference, Unit = occurrence.Unit, Variants = template.Symbol.Variants?.Clone() ?? new() };
                record.Path.Add(target.Metadata.Document.SheetPath.Path.Select(p => p.Clone()));
                records.Records.Add(record);
            }
            for (int index = 0; index < createdSymbols.Count; index++)
            {
                createdSymbols[index].InstanceRecords = records.Clone();
                int itemIndex = targetScreens[index].Items.ToList().FindIndex(item =>
                    item.Is(SchematicSymbolInstance.Descriptor)
                    && item.Unpack<SchematicSymbolInstance>().Id.Value == nativeId);
                if (itemIndex < 0) throw Invalid("created_native_identity_missing", "The generated symbol was not retained in its target screen.");
                targetScreens[index].Items[itemIndex] = Any.Pack(createdSymbols[index]);
            }
            nativeIds.Add(nativeId);
        }

        var candidate = baseline with { Engineering = desired, Schematic = augmented, PartSymbols = desiredDesign.PartSymbols,
            SymbolBindings = bindings.OrderBy(b => b.SymbolOccurrenceId).ToArray() };
        var report = SchematicDesignBindings.Inspect(candidate, libraries, token);
        if (!report.IdentitiesResolved)
            throw Invalid("created_binding_invalid", "The generated native identities did not resolve exactly: "
                + string.Join(",", report.Issues.Select(issue => issue.Code)));
        // Every created symbol now has its exact definition geometry. Pins that one symbol stacks at one
        // point are one connection in KiCad, so nets that split them are refused before anything is sent.
        SchematicPlacedPins.RequireStackedPinsOnOneNet(candidate, token);
        // A created power pin that the XML leaves out of every net is joined by KiCad to every other source of its name
        // (cn1-wiring-intent.md §5.3 for created symbols, §5.5 for local names), so it must be the only one. This holds in
        // both modes: a connected addition only declares more nets, which this check counts, and its connection intent
        // then checks the power pins those nets hold.
        RequireUndeclaredPowerPinsAlone(baseline, candidate, created, token);
        var operations = SchematicHierarchyDelta.Plan(baseline.Schematic, candidate.Schematic, token);
        return new(candidate, operations, created.Order().ToArray());
    }

    /// <summary>Refuse a creation in which KiCad would join a created pin to other pins by name alone although the XML declares
    /// no such connection (ledger pb41c5714361c378a; cn1-wiring-intent.md §5.3 for created symbols, §5.5 for local names). A
    /// power input of a new global power symbol, and a hidden power input of a new ordinary symbol, joins every other source of
    /// the same global name: a global label, a power symbol or a hidden power input already on any sheet, or another such pin
    /// of the creation. A power input of a new local power symbol joins every label, hierarchical label and local power symbol
    /// with the same name on its own sheet instance. A created power pin that the XML leaves out of every net may therefore
    /// keep its name only while nothing else has it; otherwise KiCad would connect it silently and the published XML would no
    /// longer describe the schematic. Sources are compared by model node, as the electrical comparison compares them: a pin
    /// common to several units is one physical pin however many units show it, and pins one symbol stacks at one point are
    /// one connection, so those may share a name. A created pin the XML puts in a net is checked by the connection intent,
    /// and counts here as one more source of its name; so is a pin its symbol draws at one point with a pin of the same name
    /// that the XML puts in a net, because KiCad joins it to that net through that pin (decision n757c07fe60e30e87, ledger
    /// p2d40d4ec87d01d32). Throws <c>connected_implicit_power_conflict</c> for a hidden power
    /// input, <c>connected_global_name_conflict</c> for a global power symbol, <c>connected_net_name_conflict</c> for a local
    /// power symbol, and <c>connected_power_name_unresolved</c> when a created power symbol, or an existing item such a pin
    /// could join, has no literal name; always before anything reaches KiCad.</summary>
    internal static void RequireUndeclaredPowerPinsAlone(SchematicDesign baseline, SchematicDesign candidate,
        IReadOnlyCollection<Guid> createdOccurrences, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(createdOccurrences);
        var sources = CreatedPowerSources(candidate, createdOccurrences, token);
        var netOf = new Dictionary<PinEndpoint, CircuitNet>();
        foreach (var net in candidate.Engineering.Circuit.Nets)
            foreach (var pin in net.Pins) netOf.TryAdd(pin, net);
        if (sources.All(s => netOf.ContainsKey(s.Endpoint))) return;
        // The model node of each pin: the stacked-pin node the electrical comparison uses, otherwise the model pin itself,
        // which every placement of a pin common to several units shares.
        var nodes = new Dictionary<PinEndpoint, PinEndpoint>();
        foreach (var group in SchematicElectricalComparison.StackedPinNodes(candidate, token))
            foreach (var pin in group) nodes[pin] = group[0];
        PinEndpoint Node(PinEndpoint pin) => nodes.GetValueOrDefault(pin, pin);
        // A pin its symbol draws at one point with a pin of the same name that the XML declares in a net is one connection with
        // that pin in KiCad (decision n757c07fe60e30e87, ledger p2d40d4ec87d01d32): it is declared through that pin, and the
        // connection intent checks the names that net carries. Every other created power pin in no net must be alone.
        bool DeclaredThroughPartner(CreatedPowerPin source) => sources.Any(other => netOf.ContainsKey(other.Endpoint)
            && Node(other.Endpoint) == Node(source.Endpoint) && other.Global == source.Global && other.Name == source.Name
            && (other.Global || other.Path == source.Path));
        var undeclared = sources.Where(s => !netOf.ContainsKey(s.Endpoint) && !DeclaredThroughPartner(s)).ToArray();
        if (undeclared.Length == 0) return;
        var existing = ExistingPowerSources(baseline, candidate, netOf, undeclared.Any(s => s.Global),
            undeclared.Where(s => !s.Global).Select(s => s.Path).ToHashSet(StringComparer.Ordinal), token);
        foreach (var source in undeclared)
        {
            token.ThrowIfCancellationRequested();
            // Among created pins, one the XML lists in a net is named first, so that the remedy names that net and the pin it lists.
            var other = (source.Global ? existing.Global.GetValueOrDefault(source.Name) : existing.Local.GetValueOrDefault((source.Path, source.Name)))
                ?? sources.Where(s => s.Global == source.Global && s.Name == source.Name && (s.Global || s.Path == source.Path)
                        && Node(s.Endpoint) != Node(source.Endpoint))
                    .OrderBy(s => netOf.ContainsKey(s.Endpoint) ? 0 : 1)
                    .Select(s => new PowerNameSource(s.Description, s.Member, netOf.GetValueOrDefault(s.Endpoint)?.Name)).FirstOrDefault();
            if (other is null) continue;
            string code = !source.Global ? SchematicConnectionErrors.ConnectedNetNameConflict
                : source.Carrier ? SchematicConnectionErrors.ConnectedGlobalNameConflict : SchematicConnectionErrors.ConnectedImplicitPowerConflict;
            string joins = source.Global ? "KiCad joins everything with that name into one net"
                : "KiCad joins it to every label, hierarchical label and local power symbol with that name on its sheet";
            // Name the way to declare the connection: the net that already holds the other pin, a new net holding both pins,
            // or, for a label, the net of the pins that label connects.
            string remedy = other.Net is { } net ? "Add " + source.Member + " to net '" + net + "', which holds " + other.Member + ", so the connection is declared"
                : other.Member is { } member ? "Declare a net holding " + source.Member + " and " + member + " so the connection is declared"
                : "Add " + source.Member + " to the XML net whose pins " + other.Description + " connects" + (source.Global ? "" : " on that sheet")
                    + " so the connection is declared, or rename that label in the schematic editor";
            throw Invalid(code, Capital(source.Description) + " is named '" + source.Name + "', and " + joins + ", including " + other.Description
                + ", but the XML leaves " + source.Member + " out of every net. " + remedy + "; nothing was changed.");
        }
        // An existing name that KiCad resolves only when it builds the nets may be any of the created names.
        if (existing.Unresolved is { } unresolved)
        {
            var subject = undeclared.First(s => unresolved.Global ? s.Global : !s.Global && s.Path == unresolved.Path);
            throw Invalid(SchematicConnectionErrors.ConnectedPowerNameUnresolved, Capital(unresolved.Description) + " is named '" + unresolved.Name
                + "', which is empty or still contains a text variable, so KiCad may join it to " + subject.Description + " ('" + subject.Name
                + "') although the XML leaves " + subject.Member + " out of every net. Give that item a literal name in the schematic editor; nothing was changed.");
        }
    }

    /// <summary>The groups the placed pins of the <paramref name="created"/> occurrences form in KiCad among themselves, with no
    /// wire and before anything else in the design is counted: pins one symbol stacks at one point (decision
    /// kicad-stacked-pins-one-node-20260924); created pins that KiCad joins to a global net by name, per name, which includes
    /// every placement of a hidden power input common to several units, each unit showing it; and created local power pins,
    /// per sheet instance and name. Every other placed pin is alone. Each placed pin appears once per sheet instance that
    /// shows it. Keys are ordered by (sheet path ordinal, placed pin) and groups by their first key, as cn1-wiring-intent.md
    /// §5.8 orders expected groups. Joins with items that exist already are exactly what
    /// <see cref="RequireUndeclaredPowerPinsAlone"/> refuses, so they never extend these groups.</summary>
    internal static IReadOnlyList<IReadOnlyList<ConnectionPinKey>> CreatedPinGroups(SchematicDesign candidate,
        IEnumerable<SymbolOccurrence> created, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(created);
        var parent = new Dictionary<ConnectionPinKey, ConnectionPinKey>();
        var globalNames = new Dictionary<string, ConnectionPinKey>(StringComparer.Ordinal);
        var localNames = new Dictionary<(string Path, string Name), ConnectionPinKey>();
        ConnectionPinKey Find(ConnectionPinKey key)
        {
            var root = key;
            while (parent[root] != root) root = parent[root];
            while (parent[key] != root) { var next = parent[key]; parent[key] = root; key = next; }
            return root;
        }
        void Union(ConnectionPinKey left, ConnectionPinKey right)
        {
            if (!parent.ContainsKey(left) || !parent.ContainsKey(right))
                throw new InvalidOperationException("A joined created pin is not a placed pin of its symbol.");
            ConnectionPinKey a = Find(left), b = Find(right);
            if (a != b) parent[b] = a;
        }
        foreach (var (occurrence, path, _, symbol) in CreatedSymbols(candidate, created, token))
        {
            foreach (var pin in SchematicPlacedPins.Active(symbol, occurrence.Unit))
            {
                var key = Key(path, pin);
                if (!parent.TryAdd(key, key))
                    throw new InvalidOperationException("A created placed pin appears twice in the creation's pin partition.");
                string name = SchematicPowerPins.PowerName(symbol, pin);
                if (SchematicPowerPins.IsGlobalPowerPin(symbol, pin) && !globalNames.TryAdd(name, key)) Union(globalNames[name], key);
                else if (SchematicPowerPins.IsLocalPowerPin(symbol, pin) && !localNames.TryAdd((path, name), key)) Union(localNames[(path, name)], key);
            }
            foreach (var stack in SchematicElectricalComparison.StackedDefinitionPins(symbol, occurrence.Unit))
                foreach (var pin in stack.Skip(1)) Union(Key(path, stack[0]), Key(path, pin));
        }
        return [.. parent.Keys.GroupBy(Find).Select(g => (IReadOnlyList<ConnectionPinKey>)[.. g.OrderBy(k => k.SheetPathKey, StringComparer.Ordinal).ThenBy(k => k.PlacedPinId)])
            .OrderBy(g => g[0].SheetPathKey, StringComparer.Ordinal).ThenBy(g => g[0].PlacedPinId)];

        static ConnectionPinKey Key(string path, SchematicPin pin) => Guid.TryParseExact(pin.Id?.Value, "D", out var id)
            ? new(path, id) : throw new InvalidOperationException("A created placed pin has no canonical identity.");
    }

    /// <summary>The exact pin partition an unconnected creation must leave in KiCad, as the batch's final
    /// <c>assert_connectivity</c> operation (cn1-wiring-intent.md §8.1): the placed pins of the created symbols, one key per
    /// sheet instance that shows a pin, grouped by <see cref="CreatedPinGroups"/>: alone, except pins KiCad joins among
    /// themselves without any wire (pins one symbol stacks at one point, and created power pins that share a global name, or a
    /// local name on one sheet instance, such as every unit's placement of one common hidden power input). Every group that
    /// existed before stays exactly as it was, because the assertion names only created pins. This is the lane half of the
    /// seam request for ledger pb41c5714361c378a. Nothing in production sends it yet: once the parent's executor change lands
    /// (decision n16a9af7c671d9c09, item 6), the executor will append it as the last operation of an unconnected creation sent
    /// to a KiCad that advertises <see cref="SchematicConnectedAddition.NativeCapability"/>, so that a join no plan can foresee
    /// (such as a new pin placed on an existing wire end) is refused by KiCad without any change instead of being found after
    /// the commit. Until then, and on a KiCad without that capability, an unconnected creation is checked only after it is
    /// committed.</summary>
    internal static SchematicItemOperation CreationAssertion(SchematicDesign baseline, SchematicDesign candidate, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        var existing = baseline.Engineering.Circuit.Symbols.Select(s => s.Id).ToHashSet();
        var documents = candidate.Schematic.Instances.ToDictionary(s => Path(s.Metadata.Document), s => s.Metadata.Document.SheetPath, StringComparer.Ordinal);
        var assertion = new SchematicConnectivityAssertion { Version = 1 };
        foreach (var group in CreatedPinGroups(candidate, candidate.Engineering.Circuit.Symbols.Where(s => !existing.Contains(s.Id)), token))
        {
            var pins = new SchematicPinGroup();
            pins.Pins.Add(group.Select(k => new SchematicNetChainPinAnchor { Path = documents[k.SheetPathKey].Clone(), Pin = new() { Value = k.PlacedPinId.ToString("D") } }));
            assertion.ExpectedGroups.Add(pins);
        }
        return new SchematicItemOperation { AssertConnectivity = assertion };
    }

    /// <summary>A created pin that KiCad joins by name: its global or local name, whether it is a power symbol's pin, its
    /// model pin and sheet instance path, how a message names it (<paramref name="Description"/>) and how a net lists it
    /// (<paramref name="Member"/>).</summary>
    private sealed record CreatedPowerPin(string Name, bool Global, bool Carrier, PinEndpoint Endpoint, string Path, string Description, string Member);

    /// <summary>Another source of a name: how a message names it, how a net lists it (null for a label) and the XML net
    /// that already holds it, if any.</summary>
    private sealed record PowerNameSource(string Description, string? Member, string? Net);

    private sealed record UnresolvedPowerName(string Description, string Name, bool Global, string Path);

    private sealed record ExistingPowerNames(IReadOnlyDictionary<string, PowerNameSource> Global,
        IReadOnlyDictionary<(string Path, string Name), PowerNameSource> Local, UnresolvedPowerName? Unresolved);

    // Every created pin that KiCad joins to a net by name, in reference, unit and occurrence order. A created power symbol
    // must have a literal name, because otherwise the net KiCad joins it to cannot be known.
    private static List<CreatedPowerPin> CreatedPowerSources(SchematicDesign candidate, IReadOnlyCollection<Guid> createdOccurrences, CancellationToken token)
    {
        var wanted = createdOccurrences.ToHashSet();
        var components = candidate.Engineering.Circuit.Components.ToDictionary(c => c.Id);
        var result = new List<CreatedPowerPin>();
        var created = candidate.Engineering.Circuit.Symbols.Where(s => wanted.Contains(s.Id))
            .OrderBy(s => components[s.ComponentId].Reference, StringComparer.Ordinal).ThenBy(s => s.Unit).ThenBy(s => s.Id);
        foreach (var (occurrence, path, _, symbol) in CreatedSymbols(candidate, created, token))
        {
            var component = components[occurrence.ComponentId];
            foreach (var pin in SchematicPlacedPins.Active(symbol, occurrence.Unit))
            {
                bool global = SchematicPowerPins.IsGlobalPowerPin(symbol, pin);
                if (!global && !SchematicPowerPins.IsLocalPowerPin(symbol, pin)) continue;
                bool carrier = SchematicPowerPins.IsPowerSymbol(symbol);
                string name = SchematicPowerPins.PowerName(symbol, pin);
                if (carrier && Unresolved(name))
                    throw Invalid(SchematicConnectionErrors.ConnectedPowerNameUnresolved, "Power symbol " + component.Reference + " is named '" + name
                        + "', which is empty or still contains a text variable, so the net KiCad would join it to cannot be known. "
                        + "Give it a literal power name; nothing was changed.");
                result.Add(new(name, global, carrier, new(component.Id, pin.Number), path,
                    PowerSource(symbol, pin, component.Reference), Member(symbol, pin, component.Reference)));
            }
        }
        return result;
    }

    // The existing items a created power pin could join by name, the first of each name in sheet and item order: global
    // labels and global power pins on every sheet when global names are wanted, and labels, hierarchical labels and local
    // power pins on each of the sheet instance paths local names are wanted on. The first of those items whose name is empty
    // or still contains a text variable is kept as unresolved.
    private static ExistingPowerNames ExistingPowerSources(SchematicDesign baseline, SchematicDesign candidate,
        IReadOnlyDictionary<PinEndpoint, CircuitNet> netOf, bool global, IReadOnlySet<string> localPaths, CancellationToken token)
    {
        var globals = new Dictionary<string, PowerNameSource>(StringComparer.Ordinal);
        var locals = new Dictionary<(string Path, string Name), PowerNameSource>();
        UnresolvedPowerName? unresolved = null;
        // The component each existing native symbol places on each sheet instance, for its reference and its XML nets.
        var circuit = candidate.Engineering.Circuit;
        var components = circuit.Components.ToDictionary(c => c.Id);
        var paths = candidate.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var natives = candidate.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId.ToString("D"));
        var owners = new Dictionary<(string Path, string Symbol), ComponentInstance>();
        foreach (var occurrence in circuit.Symbols)
            if (components.TryGetValue(occurrence.ComponentId, out var component) && natives.TryGetValue(occurrence.Id, out var native)
                && paths.TryGetValue(occurrence.EffectiveSheetInstanceId(component), out var at))
                owners.TryAdd((at, native), component);
        foreach (var screen in baseline.Schematic.Instances)
        {
            string path = Path(screen.Metadata.Document);
            bool local = localPaths.Contains(path);
            if (!global && !local) continue;
            foreach (var packed in screen.Items)
            {
                token.ThrowIfCancellationRequested();
                if (global && packed.Is(GlobalLabel.Descriptor))
                    Label(true, packed.Unpack<GlobalLabel>().Text?.Text_ ?? "", "the global label '");
                else if (local && packed.Is(LocalLabel.Descriptor))
                    Label(false, packed.Unpack<LocalLabel>().Text?.Text_ ?? "", "the label '");
                else if (local && packed.Is(HierarchicalLabel.Descriptor))
                    Label(false, packed.Unpack<HierarchicalLabel>().Text?.Text_ ?? "", "the hierarchical label '");
                else if (packed.Is(SchematicSymbolInstance.Descriptor))
                {
                    var symbol = packed.Unpack<SchematicSymbolInstance>();
                    var owner = owners.GetValueOrDefault((path, symbol.Id?.Value ?? ""));
                    string reference = owner?.Reference ?? Reference(symbol, screen.Metadata.Document.SheetPath);
                    foreach (var pin in SchematicPlacedPins.Active(symbol, symbol.Unit?.Unit ?? 1))
                    {
                        bool isGlobal = SchematicPowerPins.IsGlobalPowerPin(symbol, pin);
                        if (isGlobal ? !global : !local || !SchematicPowerPins.IsLocalPowerPin(symbol, pin)) continue;
                        string? net = owner is null ? null : netOf.GetValueOrDefault(new PinEndpoint(owner.Id, pin.Number))?.Name;
                        Add(isGlobal, SchematicPowerPins.PowerName(symbol, pin), new(PowerSource(symbol, pin, reference), Member(symbol, pin, reference), net));
                    }
                }
            }

            void Label(bool isGlobal, string text, string what) => Add(isGlobal, text, new(what + text + "'", null, null));

            void Add(bool isGlobal, string name, PowerNameSource source)
            {
                if (Unresolved(name)) unresolved ??= new(source.Description, name, isGlobal, path);
                else if (isGlobal) globals.TryAdd(name, source);
                else locals.TryAdd((path, name), source);
            }
        }
        return new(globals, locals, unresolved);
    }

    // The native symbol each created occurrence is bound to, with its sheet instance path and screen.
    private static IEnumerable<(SymbolOccurrence Occurrence, string Path, SchematicScreenData Screen, SchematicSymbolInstance Symbol)> CreatedSymbols(
        SchematicDesign candidate, IEnumerable<SymbolOccurrence> occurrences, CancellationToken token)
    {
        var components = candidate.Engineering.Circuit.Components.ToDictionary(c => c.Id);
        var paths = candidate.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var natives = candidate.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId.ToString("D"));
        var screens = candidate.Schematic.Instances.ToDictionary(s => Path(s.Metadata.Document), StringComparer.Ordinal);
        foreach (var occurrence in occurrences)
        {
            token.ThrowIfCancellationRequested();
            if (!components.TryGetValue(occurrence.ComponentId, out var component)
                || !paths.TryGetValue(occurrence.EffectiveSheetInstanceId(component), out var path) || !screens.TryGetValue(path, out var screen)
                || !natives.TryGetValue(occurrence.Id, out var native))
                throw Invalid("created_binding_invalid", "A created symbol occurrence has no exact native symbol.");
            var symbol = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .SingleOrDefault(s => s.Id?.Value == native) ?? throw Invalid("created_binding_invalid", "A created symbol occurrence has no exact native symbol.");
            yield return (occurrence, path, screen, symbol);
        }
    }

    // How a message names a power pin, and how a net lists it.
    private static string PowerSource(SchematicSymbolInstance symbol, SchematicPin pin, string reference) =>
        SchematicPowerPins.IsGlobalPowerSymbol(symbol) ? "power symbol " + reference
        : SchematicPowerPins.IsPowerSymbol(symbol) ? "local power symbol " + reference : "hidden power pin " + reference + "." + pin.Number;

    private static string Member(SchematicSymbolInstance symbol, SchematicPin pin, string reference) =>
        SchematicPowerPins.IsPowerSymbol(symbol) ? "the pin of power symbol " + reference : reference + "." + pin.Number;

    // A name KiCad resolves only when it builds the nets, or none at all.
    private static bool Unresolved(string name) => name.Length == 0 || name.Contains("${", StringComparison.Ordinal);

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // The reference a symbol shows on one sheet instance.
    private static string Reference(SchematicSymbolInstance symbol, SheetPath path) =>
        symbol.InstanceRecords?.Records.FirstOrDefault(r => r.Path.SequenceEqual(path.Path))?.Reference is { Length: > 0 } reference ? reference
        : symbol.ReferenceField?.Text?.Text_ is { Length: > 0 } text ? text : symbol.Id?.Value ?? "";

    private static (SchematicSymbolInstance Symbol, string Path) FindTemplate(SchematicDesign baseline,
        Circuit circuit, IReadOnlyDictionary<Guid, ComponentInstance> components,
        IReadOnlyDictionary<Guid, SheetDefinition> sheets,
        SymbolOccurrence wanted, PartDefinition part, IReadOnlyDictionary<string, SchematicScreenData> screens,
        IReadOnlyDictionary<Guid, string> paths, CancellationToken token)
    {
        var candidates = new List<(SchematicSymbolInstance Symbol, string Path)>();
        foreach (var existing in circuit.Symbols.Where(s => s.Unit == wanted.Unit).OrderBy(s => s.Id))
        {
            token.ThrowIfCancellationRequested();
            var owner = components[existing.ComponentId];
            var definition = sheets.Values.SelectMany(s => s.Components).Single(c => c.Id == owner.DefinitionId);
            if (definition.PartId != part.Id || !paths.TryGetValue(existing.EffectiveSheetInstanceId(owner), out var path)
                || !screens.TryGetValue(path, out var screen)) continue;
            var binding = baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == existing.Id);
            var symbol = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == binding.NativeObjectId.ToString("D"));
            ValidateTemplate(symbol, part, existing.Unit);
            candidates.Add((symbol, path));
        }
        if (candidates.Count == 0)
            throw Invalid("missing_symbol_template", "No existing exact part/unit native symbol can be used to create this component.");
        var first = candidates[0];
        if (candidates.Skip(1).Any(c => !NormalizedDefinition(c.Symbol.Definition!).Equals(NormalizedDefinition(first.Symbol.Definition!))))
            throw Invalid("ambiguous_symbol_template", "Multiple native symbol definitions match the part/unit; select an explicit library identity.");
        return first;
    }

    private static SchematicSymbolInstance CreateSymbol(SchematicSymbolInstance template, SchematicScreenData target,
        SymbolOccurrence occurrence, ComponentInstance component, string value, PartDefinition part,
        IReadOnlySet<string> usedIds, string nativeId, CancellationToken token)
    {
        ValidateTemplate(template, part, occurrence.Unit);
        var result = template.Clone();
        result.Id = new() { Value = nativeId };
        if (usedIds.Contains(result.Id.Value)) throw Invalid("created_native_identity_collision", "The deterministic native identity is already in use.");
        result.Path = target.Metadata.Document.SheetPath.Clone();
        result.Unit = new() { Unit = occurrence.Unit };
        result.Position = new()
        {
            XNm = Coordinates.MillimetersToNanometers(occurrence.Placement!.XMillimeters),
            YNm = Coordinates.MillimetersToNanometers(occurrence.Placement.YMillimeters)
        };
        result.Transform = new()
        {
            Orientation = (SchematicSymbolOrientation)(occurrence.Placement.RotationDegrees / 90 + 1),
            MirrorX = occurrence.Placement.MirrorX,
            MirrorY = occurrence.Placement.MirrorY
        };
        result.Locked = occurrence.Placement.Locked ? LockedState.LsLocked : LockedState.LsUnlocked;
        PlaceFields(result, result.Position.XNm - template.Position.XNm, result.Position.YNm - template.Position.YNm);
        if (result.ReferenceField?.Text is null || result.ValueField?.Text is null)
            throw Invalid("incomplete_symbol_fields", "A created symbol requires complete reference and value fields.");
        result.ReferenceField.Text.Text_ = component.Reference;
        result.ValueField.Text.Text_ = value;
        if (result.InstanceRecords is null)
            throw Invalid("incomplete_symbol_instance_records", "A created symbol requires complete instance records.");
        var records = result.InstanceRecords.Clone();
        var matching = records.Records.Where(r => r.Path.SequenceEqual(target.Metadata.Document.SheetPath.Path)).ToArray();
        if (matching.Length > 1)
            throw Invalid("ambiguous_symbol_instance_record", "A created symbol needs one exact instance record for its target sheet.");
        var record = matching.Length == 1 ? matching[0] : new SymbolSheetRecord
        {
            ProjectName = target.Metadata.Document.Project.Name,
            Reference = component.Reference,
            Unit = occurrence.Unit,
            Variants = result.Variants?.Clone() ?? new()
        };
        if (matching.Length == 0) record.Path.Add(target.Metadata.Document.SheetPath.Path.Select(p => p.Clone()));
        record.Reference = component.Reference;
        record.Unit = occurrence.Unit;
        if (string.IsNullOrWhiteSpace(record.ProjectName)) record.ProjectName = target.Metadata.Document.Project.Name;
        if (matching.Length == 0) records.Records.Add(record);
        result.InstanceRecords = records;
        int pinOrdinal = 0;
        foreach (var child in result.Definition.Items.Where(c => c.Item?.Is(SchematicPin.Descriptor) == true))
        {
            token.ThrowIfCancellationRequested();
            var pin = child.Item.Unpack<SchematicPin>();
            if (pin.LibraryPinId is not null)
                pin.Id = new() { Value = StablePinId(nativeId, pin.LibraryPinId?.Value, pin.Number, pinOrdinal++) };
            child.Item = Any.Pack(pin);
        }
        OrderDefinitionPins(result.Definition, libraryIds: false);
        return result;
    }

    private static void ValidateTemplate(SchematicSymbolInstance symbol, PartDefinition part, int unit)
    {
        if (symbol.Definition is null || !symbol.SeparatePinIdentities || symbol.Position is null
            || symbol.Transform is null || symbol.InstanceRecords is null)
            throw Invalid("incomplete_symbol_template", "The existing part/unit symbol lacks persistent native definition, pin or placement state.");
        if (symbol.Definition.UnitCount < part.Units)
            throw Invalid("symbol_unit_mismatch", "The native symbol template does not contain every declared unit.");
        var pins = symbol.Definition.Items.Where(c => c.Item?.Is(SchematicPin.Descriptor) == true)
            .Select(c => (Child: c, Pin: c.Item.Unpack<SchematicPin>()))
            .Where(p => p.Child.Unit is null || p.Child.Unit.Unit == 0 || p.Child.Unit.Unit == unit)
            .Where(p => p.Pin.LibraryPinId is not null).Select(p => p.Pin.Number).ToHashSet(StringComparer.Ordinal);
        var required = part.Pins.Where(p => p.Unit == 0 || p.Unit == unit).Select(p => p.Number).ToHashSet(StringComparer.Ordinal);
        if (!pins.SetEquals(required)) throw Invalid("symbol_pin_template_mismatch", "The native template pins do not exactly cover the declared part pins.");
    }

    private static SchematicSymbol NormalizedDefinition(SchematicSymbol definition)
    {
        var result = definition.Clone();
        foreach (var child in result.Items.Where(c => c.Item?.Is(SchematicPin.Descriptor) == true))
        {
            var pin = child.Item.Unpack<SchematicPin>();
            if (pin.LibraryPinId is not null) pin.Id = null;
            child.Item = Any.Pack(pin);
        }
        return result;
    }

    /// <summary>A created symbol's fields are laid out as its library definition lays them out, exactly as KiCad does when it
    /// places a symbol from its library or resets a placed symbol's fields from it (<c>SCH_SYMBOL::UpdateFields</c> with style
    /// update, which calls <c>SCH_FIELD::ImportValues</c> and <c>SetPrivate</c>): each field the definition defines sits at the
    /// symbol's position plus the library field's position and takes every text attribute of the library field (angle,
    /// justification, size, stroke width, font, bold, italic, colour, mirroring, line spacing and multi-line mode), its
    /// visibility, whether its name is shown, whether it may be placed automatically, and whether it is private. Only the text
    /// stays the component's own. Nothing follows the placed symbol that is copied: which placed symbol that is depends on
    /// generated identities, and a person may have dragged its fields anywhere, hidden or shown them, or resized them. A copy
    /// would take such a field with it, so the new symbol's visible text and the one rectangle KiCad measures around its
    /// visible fields would depend on that choice, and there could be no room at its pins for their connections (the likely
    /// cause of the refusal in governed run t20260924T114705Z-b90471, whose recording was not kept). A field the definition
    /// does not define keeps its place and style relative to the copied symbol (moved by <paramref name="dx"/>,
    /// <paramref name="dy"/>), as KiCad's own reset keeps it. That is the one way the copy still shows: a visible field a person
    /// added to the copied symbol and dragged far aside comes along and can widen the new symbol's measured outline, which
    /// placing the part from its library would not do. A declared part's symbol is already made from its definition's
    /// fields, so this changes nothing there.</summary>
    private static void PlaceFields(SchematicSymbolInstance symbol, long dx, long dy)
    {
        var definition = symbol.Definition;
        var library = definition?.Items.Where(c => c.Item?.Is(SchematicField.Descriptor) == true)
            .Select(c => c.Item.Unpack<SchematicField>()).ToArray() ?? [];
        var pairs = new (SchematicField? Placed, SchematicField? Library)[]
        {
            (symbol.ReferenceField, definition?.ReferenceField), (symbol.ValueField, definition?.ValueField),
            (symbol.FootprintField, definition?.FootprintField), (symbol.DatasheetField, definition?.DatasheetField),
            (symbol.DescriptionField, definition?.DescriptionField)
        }.Concat(symbol.UserFields.Select(field => ((SchematicField?)field, library.FirstOrDefault(l => l.Name == field.Name))));
        bool fromLibrary = false;
        foreach (var (placed, source) in pairs)
        {
            if (placed?.Text?.Position is not { } position) continue;
            if (source?.Text?.Position is { } local)
            {
                placed.Text.Position = new() { XNm = symbol.Position.XNm + local.XNm, YNm = symbol.Position.YNm + local.YNm };
                if (source.Text.Attributes is { } attributes) placed.Text.Attributes = attributes.Clone();
                placed.Visible = source.Visible;
                placed.ShowName = source.ShowName;
                placed.AllowAutoPlace = source.AllowAutoPlace;
                placed.IsPrivate = source.IsPrivate;
                fromLibrary = true;
            }
            else { position.XNm += dx; position.YNm += dy; }
        }
        // Nothing arranged these fields automatically; they are where the library puts them.
        if (fromLibrary) symbol.FieldsAutoplaced = false;
    }

    private static SchematicSymbolInstance InstantiateDeclaration(SchematicPartSymbol source)
    {
        var definition = source.Symbol.Definition;
        if (new[] { definition.ReferenceField, definition.ValueField, definition.FootprintField,
                definition.DatasheetField, definition.DescriptionField }.Any(f => f?.Text?.Position is null))
            throw Invalid("incomplete_symbol_fields", "A declared symbol requires all five standard fields with explicit local positions.");
        // The created symbol takes the form KiCad saves and loads, so a save and reload shows it unchanged: a cache alias
        // equal to the library identifier is not kept (KiCad's reader drops it), and pin-name spacing lives on the
        // library definition only, because a placed symbol never saves its own value.
        string libraryKey = (source.LibraryId.LibraryNickname.Length == 0 ? "" : source.LibraryId.LibraryNickname + ":") + source.LibraryId.EntryName;
        var result = new SchematicSymbolInstance
        {
            Definition = definition.Clone(), LibraryId = source.LibraryId.Clone(),
            LibName = source.Symbol.CacheKey == libraryKey ? "" : source.Symbol.CacheKey,
            Position = new(), Transform = new() { Orientation = SchematicSymbolOrientation.Sso0 },
            Locked = LockedState.LsUnlocked,
            BodyStyle = definition.BodyStyle.Count > 1 ? new() { Style = source.BodyStyle } : null,
            Passthrough = SchematicPassthroughMode.SpmDefault,
            SeparatePinIdentities = true, InstanceRecords = new(), Variants = new(),
            ShowPinNames = source.Symbol.ShowPinNames, ShowPinNumbers = source.Symbol.ShowPinNumbers,
            PinNameOffset = new(), DefinitionPinNameOffset = source.Symbol.PinNameOffset.Clone(),
            Attributes = definition.Attributes?.Clone() ?? new(),
            ReferenceField = definition.ReferenceField.Clone(), ValueField = definition.ValueField.Clone(),
            FootprintField = definition.FootprintField.Clone(), DatasheetField = definition.DatasheetField.Clone(),
            DescriptionField = definition.DescriptionField.Clone()
        };
        foreach (var child in result.Definition.Items)
        {
            if (child.Item.Is(SchematicField.Descriptor)) result.UserFields.Add(child.Item.Unpack<SchematicField>());
            if (!child.Item.Is(SchematicPin.Descriptor)) continue;
            var pin = child.Item.Unpack<SchematicPin>();
            int style = child.BodyStyle?.Style ?? 0;
            if (style == 0 || style == source.BodyStyle)
            {
                // The declaration retains its owned UUID. CreateSymbol assigns the distinct
                // deterministic placed UUID; inactive styles remain library-owned only.
                pin.LibraryPinId = pin.Id.Clone();
                child.Item = Any.Pack(pin);
            }
        }
        // Native cache enumeration is not an identity. Assign deterministic placed
        // IDs in owned-pin order, then match native PackSymbol's placed-pin order.
        OrderNativeDefinitionChildren(result.Definition);
        OrderDefinitionPins(result.Definition, libraryIds: true);
        return result;
    }

    internal static void OrderDefinitionPins(SchematicSymbol definition, bool libraryIds)
    {
        var others = definition.Items.Where(c => !c.Item.Is(SchematicPin.Descriptor)).ToArray();
        var pins = definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)).OrderBy(c =>
        {
            var pin = c.Item.Unpack<SchematicPin>();
            return libraryIds ? pin.LibraryPinId?.Value ?? pin.Id.Value : pin.Id.Value;
        }, StringComparer.Ordinal).ToArray();
        definition.Items.Clear(); definition.Items.Add(others); definition.Items.Add(pins);
    }

    internal static void OrderNativeDefinitionChildren(SchematicSymbol definition)
    {
        // LIB_ITEMS_CONTAINER is MULTIVECTOR<SCH_ITEM, SCH_SHAPE_T, SCH_PIN_T>.
        // Match that pinned type-bucket traversal without changing the order within
        // a bucket or mutating the independently retained declaration.
        foreach (var child in definition.Items)
        {
            child.Unit ??= new();
            child.BodyStyle ??= new();
        }
        var ordered = definition.Items.OrderBy(child => child.Item switch
        {
            var item when item.Is(SchematicGraphicShape.Descriptor) => 0,
            var item when item.Is(SchematicField.Descriptor) => 1,
            var item when item.Is(SchematicText.Descriptor) => 2,
            var item when item.Is(SchematicTextBox.Descriptor) => 3,
            var item when item.Is(SchematicPin.Descriptor) => 4,
            _ => throw Invalid("unsupported_symbol_child", "The declared symbol contains an unsupported native library child.")
        }).ToArray();
        definition.Items.Clear(); definition.Items.Add(ordered);
    }

    private static void CopyLibraryCache(SchematicScreenData source, SchematicScreenData target, SchematicSymbolInstance symbol)
    {
        var library = symbol.LibraryId ?? symbol.Definition.Id;
        string key = symbol.LibName.Length != 0 ? symbol.LibName : library is null ? ""
            : (library.LibraryNickname.Length == 0 ? "" : library.LibraryNickname + ":") + library.EntryName;
        var entry = source.CachedSymbols.SingleOrDefault(c => c.CacheKey == key);
        if (entry is null) return; // Legacy, explicitly incomplete DTOs remain inspectable, not live proof.
        CopyLibraryCache(entry, target);
    }

    private static void CopyLibraryCache(SchematicCachedSymbol entry, SchematicScreenData target)
    {
        entry = entry.Clone();
        OrderNativeDefinitionChildren(entry.Definition);
        var existing = target.CachedSymbols.SingleOrDefault(c => c.CacheKey == entry.CacheKey);
        if (existing is not null && !SchematicLibraryCacheEquivalence.Equal(existing, entry))
            throw Invalid("created_symbol_cache_conflict", "The target screen has a different definition for the selected library key.");
        if (existing is not null) return;
        // KiCad keeps a screen's library cache ordered by key and always reports it in that order, so the created cache
        // entry takes its place there: the published XML then lists the cache exactly as KiCad shows, saves and reloads it.
        int index = 0;
        while (index < target.CachedSymbols.Count && string.CompareOrdinal(target.CachedSymbols[index].CacheKey, entry.CacheKey) < 0) ++index;
        target.CachedSymbols.Insert(index, entry);
    }

    /// <summary>Partition created occurrences into the physical native symbols they will occupy.
    /// Each occurrence lands on the physical screen of its effective sheet instance, which is the
    /// component's own sheet unless the occurrence places the unit on another sheet. Occurrences of
    /// one definition and unit that reach every instance of their screen exactly once share one
    /// symbol, as repeated sheets do. On a screen with one instance each occurrence is its own
    /// symbol, so units of several components can share a sheet. Any other arrangement would show a
    /// unit on a sheet instance that has no model occurrence for it, or show it twice, and is
    /// rejected before anything is created.
    /// <para>Identity invariant: on a single-instance screen a unit that is alone in its
    /// (screen, definition, unit) bucket takes the shared key, while two or more occurrences there
    /// take per-component keys. The key of one occurrence therefore depends on the other occurrences
    /// of the same definition and unit in <paramref name="added"/>. That is deterministic only because
    /// <c>component_definition_required</c> admits a component only with a definition introduced by the
    /// same creation, so every component of a definition, and every occurrence of it, is projected in one
    /// call. A later caller that projects a subset of a definition's occurrences (for example a rebuild of
    /// one sheet) must pass the definition's complete occurrence set, or it will compute different
    /// native identities. That precondition is enforced: when a definition appears in
    /// <paramref name="added"/>, every occurrence of it in <paramref name="desired"/> must be supplied,
    /// otherwise this throws <see cref="InvalidOperationException"/> (a caller defect, not a user
    /// refusal).</para></summary>
    internal static IReadOnlyList<SchematicCreatedSymbolGroup> PhysicalSymbols(SchematicDesign baseline, Circuit desired,
        IEnumerable<SymbolOccurrence> added, CancellationToken token = default)
    {
        var supplied = added.ToArray();
        var omitted = OmittedOccurrences(desired, supplied);
        if (omitted.Count != 0)
            throw new InvalidOperationException("Created native identities depend on every occurrence of a definition; supply all of them. Omitted: "
                + string.Join(", ", omitted.Select(id => id.ToString("D"))));
        var components = desired.Components.ToDictionary(c => c.Id);
        var paths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var screens = baseline.Schematic.Instances.ToDictionary(s => Path(s.Metadata.Document), s => s.Metadata.ScreenId.Value,
            StringComparer.Ordinal);
        var instancesOfScreen = screens.GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var located = new List<(SymbolOccurrence Occurrence, string Path, string Screen, Guid Definition)>();
        foreach (var occurrence in supplied)
        {
            token.ThrowIfCancellationRequested();
            if (!components.TryGetValue(occurrence.ComponentId, out var component))
                throw Invalid("symbol_owner_requires_resolution", "A new symbol occurrence must belong to a known component instance.");
            if (!paths.TryGetValue(occurrence.EffectiveSheetInstanceId(component), out var path) || !screens.TryGetValue(path, out var screen))
                throw Invalid("missing_native_sheet", "A created symbol must target a bound existing sheet instance.");
            located.Add((occurrence, path, screen, component.DefinitionId));
        }
        var result = new List<SchematicCreatedSymbolGroup>();
        foreach (var bucket in located.GroupBy(x => (x.Screen, x.Definition, x.Occurrence.Unit)))
        {
            var members = bucket.OrderBy(x => x.Occurrence.Id).ToArray();
            var reached = members.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
            var instances = instancesOfScreen[bucket.Key.Screen];
            if (reached.Count == members.Length && reached.SetEquals(instances))
                result.Add(new(new(bucket.Key.Screen, bucket.Key.Definition, bucket.Key.Unit, null),
                    members.Select(x => x.Occurrence).ToArray()));
            else if (instances.Count == 1)
                result.AddRange(members.Select(x => new SchematicCreatedSymbolGroup(
                    new(bucket.Key.Screen, bucket.Key.Definition, bucket.Key.Unit, x.Occurrence.ComponentId), [x.Occurrence])));
            else
                throw Invalid("created_unit_sheet_coverage_mismatch", "Unit " + bucket.Key.Unit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " would appear on a repeated sheet without exactly one symbol occurrence for each of its "
                    + instances.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " instances. Place the unit once on every instance of that sheet, or on a sheet with a single instance.");
        }
        return result.OrderBy(g => g.Key.PhysicalScreen, StringComparer.Ordinal).ThenBy(g => g.Key.Definition)
            .ThenBy(g => g.Key.Unit).ThenBy(g => g.Key.Component ?? Guid.Empty).ToArray();
    }

    /// <summary>The occurrences in <paramref name="desired"/> of definitions that <paramref name="added"/>
    /// mentions but does not supply, in identity order. Empty exactly when <see cref="PhysicalSymbols"/>
    /// may compute identities for <paramref name="added"/>. Creation always satisfies this, because a new
    /// component needs a new definition and may bring units only for itself; a caller that meets another
    /// shape (a new unit of an existing component) reports creation's own refusal instead.</summary>
    internal static IReadOnlyList<Guid> OmittedOccurrences(Circuit desired, IEnumerable<SymbolOccurrence> added)
    {
        var components = new Dictionary<Guid, ComponentInstance>();
        foreach (var component in desired.Components) components.TryAdd(component.Id, component);
        var supplied = added.ToArray();
        var suppliedIds = supplied.Select(s => s.Id).ToHashSet();
        var definitions = supplied.Where(s => components.ContainsKey(s.ComponentId))
            .Select(s => components[s.ComponentId].DefinitionId).ToHashSet();
        return [.. desired.Symbols.Where(s => !suppliedIds.Contains(s.Id) && components.TryGetValue(s.ComponentId, out var owner)
            && definitions.Contains(owner.DefinitionId)).Select(s => s.Id).Order()];
    }

    private static string StablePhysicalId(SchematicCreatedSymbolKey key)
    {
        // A per-component symbol extends the shared-symbol preimage, so neither form can
        // reproduce the other and existing shared identities stay unchanged. Which form a key
        // takes depends on the whole occurrence set of its definition (see PhysicalSymbols), so
        // recomputing an identity requires that complete set, never one occurrence alone.
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("kicad-created-symbol-v2\n" + key.PhysicalScreen
            + "\n" + key.Definition.ToString("D") + "\n" + key.Unit
            + (key.Component is Guid component ? "\ncomponent\n" + component.ToString("D") : "")));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x50); digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    private static string StablePinId(string nativeId, string? libraryPin, string number, int ordinal)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("kicad-created-pin-v2\n" + nativeId
            + "\n" + (libraryPin ?? "") + "\n" + number + "\n" + ordinal));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x50); digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    private static string Path(DocumentSpecifier document) => string.Join('/', document.SheetPath.Path.Select(p => p.Value));
    private static bool SamePart(PartDefinition left, PartDefinition right) => left.Id == right.Id
        && left.Name == right.Name && left.Units == right.Units
        && left.Pins.OrderBy(pin => pin.Number, StringComparer.Ordinal).ThenBy(pin => pin.Unit)
            .SequenceEqual(right.Pins.OrderBy(pin => pin.Number, StringComparer.Ordinal).ThenBy(pin => pin.Unit));
    private static AutomationException Invalid(string code, string message) => new(code, message);
}
