namespace KiCad.Automation.Model;

// The electrical section of design.xml. This is not a native schematic snapshot:
// drawing objects, requirements and project settings have separate owners.
public sealed record Circuit(
    Guid Id,
    IReadOnlyList<PartDefinition> Parts,
    IReadOnlyList<SheetDefinition> Sheets,
    IReadOnlyList<SheetInstance> SheetInstances,
    IReadOnlyList<ComponentInstance> Components,
    IReadOnlyList<CircuitNet> Nets,
    IReadOnlyList<SymbolOccurrence> Symbols)
{
    public void Validate()
    {
        var identities = new HashSet<Guid>();
        void Identity(Guid id)
        {
            if (id == Guid.Empty || !identities.Add(id))
                throw Invalid("Every entity requires a unique, non-empty identity.");
        }
        Identity(Id);
        foreach (var part in Parts)
        {
            Identity(part.Id);
            RequireText(part.Name, "Part name");
            if (part.Units < 1) throw Invalid("A part requires at least one symbol unit.");
            var pins = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pin in part.Pins)
            {
                RequireText(pin.Number, "Pin number");
                if (!pins.Add(pin.Number)) throw Invalid("Part pin numbers must be unique.");
                if (pin.Unit < 0 || pin.Unit > part.Units)
                    throw Invalid("A pin must belong to a declared unit, or unit zero for common pins.");
            }
        }
        foreach (var sheet in Sheets)
        {
            Identity(sheet.Id);
            RequireText(sheet.Name, "Sheet name");
            foreach (var component in sheet.Components)
            {
                Identity(component.Id);
                if (!Parts.Any(p => p.Id == component.PartId)) throw Invalid("Component references an unknown part.");
            }
        }
        var sheets = Sheets.ToDictionary(s => s.Id);
        foreach (var instance in SheetInstances)
        {
            Identity(instance.Id);
            if (!sheets.ContainsKey(instance.DefinitionId)) throw Invalid("Sheet instance references an unknown definition.");
        }
        var instances = SheetInstances.ToDictionary(s => s.Id);
        if (SheetInstances.Count == 0) throw Invalid("A circuit requires a sheet instance.");
        foreach (var instance in SheetInstances)
        {
            var ancestors = new HashSet<Guid> { instance.Id };
            Guid? parent = instance.ParentId;
            while (parent is Guid id)
            {
                if (!ancestors.Add(id)) throw Invalid("Sheet hierarchy contains a cycle.");
                if (!instances.TryGetValue(id, out var ancestor)) throw Invalid("Sheet parent does not exist.");
                parent = ancestor.ParentId;
            }
        }
        var instantiatedDefinitions = new HashSet<(Guid, Guid)>();
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in Components)
        {
            Identity(component.Id);
            RequireText(component.Reference, "Component reference");
            // KiCad uses a trailing '?' for an unannotated reference. Such
            // placeholders are intentionally repeatable until annotation;
            // assigned references remain unique design-wide.
            if (!component.Reference.EndsWith("?", StringComparison.Ordinal)
                && !references.Add(component.Reference))
                throw Invalid("Assigned component references must be unique in the design.");
            if (!instances.TryGetValue(component.SheetInstanceId, out var instance)
                || !sheets[instance.DefinitionId].Components.Any(c => c.Id == component.DefinitionId))
                throw Invalid("Component does not belong to its sheet definition.");
            if (!instantiatedDefinitions.Add((component.SheetInstanceId, component.DefinitionId)))
                throw Invalid("A sheet instance cannot instantiate a component definition twice.");
        }
        foreach (var instance in SheetInstances)
            foreach (var component in sheets[instance.DefinitionId].Components)
                if (!instantiatedDefinitions.Contains((instance.Id, component.Id)))
                    throw Invalid("Every sheet component must have an explicit instance.");

        var components = Components.ToDictionary(c => c.Id);
        var definitions = Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);
        var parts = Parts.ToDictionary(p => p.Id);
        var connectedPins = new HashSet<PinEndpoint>();
        foreach (var net in Nets)
        {
            Identity(net.Id);
            foreach (var endpoint in net.Pins)
            {
                if (!components.TryGetValue(endpoint.ComponentId, out var component)
                    || !parts[definitions[component.DefinitionId].PartId].Pins.Any(p => p.Number == endpoint.Pin))
                    throw Invalid("A net endpoint references an unknown component pin.");
                if (!connectedPins.Add(endpoint)) throw Invalid("A pin cannot occur twice or belong to two nets.");
            }
        }
        var units = new HashSet<(Guid, int)>();
        foreach (var symbol in Symbols)
        {
            Identity(symbol.Id);
            if (!components.TryGetValue(symbol.ComponentId, out var component))
                throw Invalid("Symbol references an unknown component instance.");
            if (symbol.Unit < 1 || symbol.Unit > parts[definitions[component.DefinitionId].PartId].Units
                || !units.Add((symbol.ComponentId, symbol.Unit)))
                throw Invalid("Symbol units must be declared and unique per component instance.");
            if (!instances.ContainsKey(symbol.EffectiveSheetInstanceId(component)))
                throw Invalid("Symbol occurrence references an unknown sheet instance.");
            symbol.Placement?.Validate();
        }
    }

    public Circuit WithoutPlacement() => this with
    {
        Symbols = Symbols.Select(symbol => symbol with { Placement = null }).ToArray()
    };

    private static void RequireText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Invalid(field + " is required.");
    }

    internal static AutomationException Invalid(string message) => new("invalid_circuit", message);
}

public sealed record PartDefinition(Guid Id, string Name, int Units, IReadOnlyList<PartPin> Pins);
public sealed record PartPin(string Number, string Name, int Unit);
public sealed record SheetDefinition(Guid Id, string Name, IReadOnlyList<ComponentDefinition> Components);
public sealed record ComponentDefinition(Guid Id, Guid PartId, string Value);
public sealed record SheetInstance(Guid Id, Guid DefinitionId, Guid? ParentId);
public sealed record ComponentInstance(Guid Id, Guid DefinitionId, Guid SheetInstanceId, string Reference);
public sealed record PinEndpoint(Guid ComponentId, string Pin);
public sealed record CircuitNet(Guid Id, string Name, IReadOnlyList<PinEndpoint> Pins);
public sealed record SymbolOccurrence(Guid Id, Guid ComponentId, int Unit, SymbolPlacement? Placement, Guid? SheetInstanceId = null)
{
    public Guid EffectiveSheetInstanceId(ComponentInstance component) => component.Id == ComponentId
        ? SheetInstanceId ?? component.SheetInstanceId
        : throw Circuit.Invalid("Resolve a symbol location only through its exact component owner.");
}

// Millimetres and degrees are explicit in the field names and XML attributes.
// Mirror is represented independently from rotation; neither affects connectivity.
public sealed record SymbolPlacement(decimal XMillimeters, decimal YMillimeters, int RotationDegrees,
                                     bool MirrorX, bool MirrorY, bool Locked)
{
    public void Validate()
    {
        Coordinates.MillimetersToSchematicUnits(XMillimeters);
        Coordinates.MillimetersToSchematicUnits(YMillimeters);
        if (RotationDegrees is not (0 or 90 or 180 or 270))
            throw Circuit.Invalid("Symbol rotation must be 0, 90, 180 or 270 degrees.");
    }
}
