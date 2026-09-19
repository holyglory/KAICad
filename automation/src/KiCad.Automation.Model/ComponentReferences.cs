namespace KiCad.Automation.Model;

public enum ComponentReferenceSlot { StatementTarget, FirstPin, SecondPin, BlockRealization }
public enum ComponentReferenceChangeKind { Removed, Reidentified }
public sealed record ComponentReferenceTarget(Guid ComponentId, string? PinNumber = null);
public sealed record ComponentReferenceChange(ComponentReferenceTarget FormerTarget, ComponentReferenceChangeKind Change,
    string Reason, IReadOnlyList<ComponentReferenceTarget> CandidateTargets);
public sealed record UnresolvedComponentReference(Guid OwnerId, ComponentReferenceSlot Slot,
    ComponentReferenceTarget FormerTarget, ComponentReferenceChangeKind Change, string Reason,
    IReadOnlyList<ComponentReferenceTarget> CandidateTargets);
public sealed record UnresolvedGuidanceBinding(Guid ComponentInstanceId, ComponentReferenceChangeKind Change,
    string Reason, IReadOnlyList<Guid> CandidateComponentIds);

internal readonly record struct ComponentReferenceKey(Guid Owner, ComponentReferenceSlot Slot, ComponentReferenceTarget Target);

internal sealed class ComponentReferenceIndex
{
    private readonly HashSet<Guid> components;
    private readonly HashSet<ComponentReferenceTarget> pins;
    internal readonly HashSet<Guid> OtherElectricalIds;

    internal ComponentReferenceIndex(Circuit circuit)
    {
        components = circuit.Components.Select(c => c.Id).ToHashSet();
        var definitions = circuit.Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);
        var parts = circuit.Parts.ToDictionary(p => p.Id);
        pins = circuit.Components.SelectMany(c => parts[definitions[c.DefinitionId].PartId].Pins
            .Select(p => new ComponentReferenceTarget(c.Id, p.Number))).ToHashSet();
        OtherElectricalIds = circuit.Parts.Select(p => p.Id).Concat(circuit.Sheets.Select(s => s.Id))
            .Concat(definitions.Keys).Concat(circuit.SheetInstances.Select(s => s.Id)).Concat(circuit.Nets.Select(n => n.Id))
            .Concat(circuit.Symbols.Select(s => s.Id)).Append(circuit.Id).ToHashSet();
    }

    internal bool Exists(ComponentReferenceTarget target) => target.PinNumber is null
        ? components.Contains(target.ComponentId) : pins.Contains(target);

    internal void Validate(ComponentReferenceTarget target, bool pin, bool candidate)
    {
        if (target.ComponentId == Guid.Empty || (pin ? string.IsNullOrWhiteSpace(target.PinNumber) : target.PinNumber is not null)
            || OtherElectricalIds.Contains(target.ComponentId) || (candidate && !Exists(target)))
            throw Invalid("Component references require exact component identities and the appropriate existing candidate pin or component.");
    }

    internal static AutomationException Invalid(string message) => new("invalid_component_reference", message);
}

internal static class ComponentReferenceValidation
{
    internal static HashSet<ComponentReferenceKey> Structure(StructuralDiagram diagram, Circuit circuit)
    {
        if (diagram.UnresolvedComponentReferences is not { Count: > 0 }) return [];
        var index = new ComponentReferenceIndex(circuit);
        var statements = diagram.Statements.ToLookup(s => s.Id);
        var blocks = diagram.Blocks.ToLookup(b => b.Id);
        var structuralIds = diagram.Blocks.Select(b => b.Id).Concat(diagram.Ports.Select(p => p.Id))
            .Concat(diagram.Connections.Select(c => c.Id)).Concat(diagram.Statements.Select(s => s.Id)).Append(diagram.Id).ToHashSet();
        var formerNets = (diagram.UnresolvedNetBindings ?? []).Select(b => b.FormerNetId).ToHashSet();
        var keys = new HashSet<ComponentReferenceKey>();
        var slots = new HashSet<(Guid Owner, ComponentReferenceSlot Slot)>();
        foreach (var reference in diagram.UnresolvedComponentReferences ?? [])
        {
            bool pin = reference.Slot is ComponentReferenceSlot.FirstPin or ComponentReferenceSlot.SecondPin;
            index.Validate(reference.FormerTarget, pin, candidate: false);
            if (reference.OwnerId == Guid.Empty || !Enum.IsDefined(reference.Slot) || !Enum.IsDefined(reference.Change)
                || string.IsNullOrWhiteSpace(reference.Reason) || structuralIds.Contains(reference.FormerTarget.ComponentId)
                || formerNets.Contains(reference.FormerTarget.ComponentId)
                || !keys.Add(new(reference.OwnerId, reference.Slot, reference.FormerTarget))
                || (reference.Slot != ComponentReferenceSlot.BlockRealization && !slots.Add((reference.OwnerId, reference.Slot))))
                throw ComponentReferenceIndex.Invalid("Unresolved component references need distinct exact owners/slots, reasons and unambiguous target kinds.");
            if (reference.CandidateTargets.Distinct().Count() != reference.CandidateTargets.Count)
                throw ComponentReferenceIndex.Invalid("Candidate component or pin identities cannot repeat.");
            foreach (var candidate in reference.CandidateTargets) index.Validate(candidate, pin, candidate: true);
            if (reference.Slot == ComponentReferenceSlot.BlockRealization)
            {
                if (blocks[reference.OwnerId].Count() != 1
                    || blocks[reference.OwnerId].Single().ComponentIds.Contains(reference.FormerTarget.ComponentId))
                    throw ComponentReferenceIndex.Invalid("A retired block realization requires one block owner and cannot also be a live realization.");
                continue;
            }
            if (statements[reference.OwnerId].Count() != 1)
                throw ComponentReferenceIndex.Invalid("The unresolved statement owner is missing or ambiguous.");
            var statement = statements[reference.OwnerId].Single();
            var target = reference.Slot switch
            {
                ComponentReferenceSlot.StatementTarget => new ComponentReferenceTarget(statement.TargetId),
                ComponentReferenceSlot.FirstPin when statement.Connection is { } detail => new(detail.First.ComponentId, detail.First.Pin),
                ComponentReferenceSlot.SecondPin when statement.Connection is { } detail => new(detail.Second.ComponentId, detail.Second.Pin),
                _ => null
            };
            if (target != reference.FormerTarget)
                throw ComponentReferenceIndex.Invalid("The retained instruction must keep its exact original component or pin target.");
        }
        return keys;
    }

    internal static HashSet<Guid> Guidance(EngineeringDesign design)
    {
        if (design.UnresolvedGuidanceBindings is not { Count: > 0 }) return [];
        var index = new ComponentReferenceIndex(design.Circuit);
        var bindings = design.ComponentBindings.ToLookup(b => b.ComponentInstanceId);
        var structuralIds = design.Structure.Blocks.Select(b => b.Id).Concat(design.Structure.Ports.Select(p => p.Id))
            .Concat(design.Structure.Connections.Select(c => c.Id)).Concat(design.Structure.Statements.Select(s => s.Id))
            .Concat((design.Structure.Properties ?? []).Select(p => p.Statement.Id))
            .Append(design.Structure.Id).ToHashSet();
        var owners = new HashSet<Guid>();
        var formerNets = (design.Structure.UnresolvedNetBindings ?? []).Select(r => r.FormerNetId).ToHashSet();
        foreach (var reference in design.UnresolvedGuidanceBindings ?? [])
        {
            index.Validate(new(reference.ComponentInstanceId), pin: false, candidate: false);
            if (!owners.Add(reference.ComponentInstanceId) || !Enum.IsDefined(reference.Change)
                || string.IsNullOrWhiteSpace(reference.Reason) || structuralIds.Contains(reference.ComponentInstanceId)
                || formerNets.Contains(reference.ComponentInstanceId)
                || bindings[reference.ComponentInstanceId].Count() != 1
                || reference.CandidateComponentIds.Distinct().Count() != reference.CandidateComponentIds.Count)
                throw ComponentReferenceIndex.Invalid("Unresolved guidance requires its original unique binding owner, reason and distinct live candidates.");
            foreach (Guid candidate in reference.CandidateComponentIds) index.Validate(new(candidate), pin: false, candidate: true);
        }
        return owners;
    }
}
