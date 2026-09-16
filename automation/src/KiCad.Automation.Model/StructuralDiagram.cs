namespace KiCad.Automation.Model;

public enum EngineeringStatementRole { Intent, Interpretation, Realization }
public enum StructuralConnectionKind { Unspecified, Power, Data, Control, Analog, Mechanical }
public enum NetBindingChangeKind { Split, Merged, Removed, Reidentified }
public sealed record UnresolvedNetBinding(Guid OwnerId, Guid FormerNetId, NetBindingChangeKind Change,
    string Reason, IReadOnlyList<Guid> CandidateNetIds);
public sealed record NetIdentityChange(Guid FormerNetId, NetBindingChangeKind Change,
    string Reason, IReadOnlyList<Guid> CandidateNetIds);

// Strength and specificity are orthogonal: a concrete pin connection can be a
// preference, while prose can be mandatory. A realization is not a requirement.
public sealed record EngineeringStatement(Guid Id, Guid TargetId, EngineeringStatementRole Role,
    GuidanceStrength? Strength, string Text, PinConnectionDetail? Connection,
    IReadOnlyList<Guid> DerivedFrom, IReadOnlyList<SourceReference> Sources);
public sealed record PinConnectionDetail(PinEndpoint First, PinEndpoint Second);
public sealed record StructuralBlock(Guid Id, string Name, Guid? ParentId, IReadOnlyList<Guid> ComponentIds);
public sealed record StructuralPort(Guid Id, Guid BlockId, string Name);
public sealed record StructuralConnection(Guid Id, Guid FirstPortId, Guid SecondPortId,
    StructuralConnectionKind Kind, string Description, IReadOnlyList<Guid> NetIds);

/// <summary>Architectural entities and explicit realization links. Diagram
/// coordinates belong to presentation, not connectivity or requirement strength.</summary>
public sealed record StructuralDiagram(Guid Id, IReadOnlyList<StructuralBlock> Blocks,
    IReadOnlyList<StructuralPort> Ports, IReadOnlyList<StructuralConnection> Connections,
    IReadOnlyList<EngineeringStatement> Statements,
    IReadOnlyList<UnresolvedNetBinding>? UnresolvedNetBindings = null,
    IReadOnlyList<UnresolvedComponentReference>? UnresolvedComponentReferences = null)
{
    public bool HasUnresolvedNetBindings => UnresolvedNetBindings is { Count: > 0 };

    public void Validate(Circuit circuit)
    {
        circuit.Validate();
        var unresolvedComponents = ComponentReferenceValidation.Structure(this, circuit);
        var electricalIds = new HashSet<Guid>(circuit.Parts.Select(p => p.Id)
            .Concat(circuit.Sheets.Select(s => s.Id)).Concat(circuit.Sheets.SelectMany(s => s.Components).Select(c => c.Id))
            .Concat(circuit.SheetInstances.Select(s => s.Id)).Concat(circuit.Components.Select(c => c.Id))
            .Concat(circuit.Nets.Select(n => n.Id)).Concat(circuit.Symbols.Select(s => s.Id)).Append(circuit.Id));
        var identities = new HashSet<Guid>();
        void Add(Guid id)
        {
            if (id == Guid.Empty || electricalIds.Contains(id) || !identities.Add(id))
                throw Invalid("Structural identities must be unique, non-empty and distinct from electrical identities; use explicit realization links.");
        }
        Add(Id);
        var componentIds = circuit.Components.Select(c => c.Id).ToHashSet();
        var netIds = circuit.Nets.Select(n => n.Id).ToHashSet();
        var unresolved = new Dictionary<(Guid Owner, Guid Former), UnresolvedNetBinding>();
        foreach (var binding in UnresolvedNetBindings ?? [])
        {
            if (binding.OwnerId == Guid.Empty || binding.FormerNetId == Guid.Empty
                || !Enum.IsDefined(binding.Change) || string.IsNullOrWhiteSpace(binding.Reason)
                || !unresolved.TryAdd((binding.OwnerId, binding.FormerNetId), binding)
                || (electricalIds.Contains(binding.FormerNetId) && !netIds.Contains(binding.FormerNetId))
                || binding.CandidateNetIds.Distinct().Count() != binding.CandidateNetIds.Count
                || binding.CandidateNetIds.Any(id => !netIds.Contains(id)))
                throw Invalid("Unresolved net bindings require exact owners, former net identities, reasons and distinct existing candidates.");
        }
        foreach (var block in Blocks)
        {
            Add(block.Id);
            if (string.IsNullOrWhiteSpace(block.Name)) throw Invalid("A structural block needs a name.");
            if (block.ComponentIds.Distinct().Count() != block.ComponentIds.Count
                || block.ComponentIds.Any(id => !componentIds.Contains(id)))
                throw Invalid("Block realization must reference distinct existing component instances.");
        }
        var blocks = Blocks.ToDictionary(b => b.Id);
        foreach (var block in Blocks)
        {
            var visited = new HashSet<Guid> { block.Id };
            Guid? parent = block.ParentId;
            while (parent is Guid id)
            {
                if (!visited.Add(id) || !blocks.TryGetValue(id, out var ancestor))
                    throw Invalid("Block parent is missing or the hierarchy contains a cycle.");
                parent = ancestor.ParentId;
            }
        }
        foreach (var port in Ports)
        {
            Add(port.Id);
            if (!blocks.ContainsKey(port.BlockId) || string.IsNullOrWhiteSpace(port.Name))
                throw Invalid("A port requires an existing owner block and a name.");
        }
        var ports = Ports.ToDictionary(p => p.Id);
        foreach (var connection in Connections)
        {
            Add(connection.Id);
            if (!ports.ContainsKey(connection.FirstPortId) || !ports.ContainsKey(connection.SecondPortId)
                || connection.FirstPortId == connection.SecondPortId || !Enum.IsDefined(connection.Kind))
                throw Invalid("An architectural connection needs two different existing ports and a supported kind.");
            if (connection.NetIds.Distinct().Count() != connection.NetIds.Count
                || connection.NetIds.Any(id => !netIds.Contains(id)))
                throw Invalid("Connection realization references a missing or duplicate net.");
        }
        var targets = identities.Concat(componentIds).Concat(netIds).ToHashSet();
        var definitions = circuit.Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);
        var parts = circuit.Parts.ToDictionary(p => p.Id);
        var components = circuit.Components.ToDictionary(c => c.Id);
        bool PinExists(PinEndpoint endpoint) => components.TryGetValue(endpoint.ComponentId, out var component)
            && parts[definitions[component.DefinitionId].PartId].Pins.Any(pin => pin.Number == endpoint.Pin);
        foreach (var statement in Statements)
        {
            Add(statement.Id);
            if (!targets.Contains(statement.TargetId) && !unresolved.ContainsKey((statement.Id, statement.TargetId))
                && !unresolvedComponents.Contains(new(statement.Id, ComponentReferenceSlot.StatementTarget, new(statement.TargetId))))
                throw Invalid("Statement target is unresolved without an explicit retained binding.");
            if (!Enum.IsDefined(statement.Role) || (statement.Strength is GuidanceStrength strength && !Enum.IsDefined(strength)))
                throw Invalid("Unknown statement role or strength.");
            if (statement.Role == EngineeringStatementRole.Intent && statement.Strength is null)
                throw Invalid("Intent requires an explicit strength, independently of its specificity.");
            if (statement.Role == EngineeringStatementRole.Realization && statement.Strength is not null)
                throw Invalid("A realized choice cannot silently become a requirement or preference; record separate intent.");
            if (string.IsNullOrWhiteSpace(statement.Text) && statement.Connection is null)
                throw Invalid("Statement needs text or concrete connection details.");
            if (statement.Connection is PinConnectionDetail detail
                && ((!PinExists(detail.First) && !unresolvedComponents.Contains(new(statement.Id, ComponentReferenceSlot.FirstPin,
                        new(detail.First.ComponentId, detail.First.Pin))))
                    || (!PinExists(detail.Second) && !unresolvedComponents.Contains(new(statement.Id, ComponentReferenceSlot.SecondPin,
                        new(detail.Second.ComponentId, detail.Second.Pin)))) || detail.First == detail.Second))
                throw Invalid("Concrete connection details need distinct existing component pins.");
            if (statement.DerivedFrom.Distinct().Count() != statement.DerivedFrom.Count)
                throw Invalid("Repeated statement provenance reference.");
            foreach (var source in statement.Sources)
                if (string.IsNullOrWhiteSpace(source.DocumentId) || string.IsNullOrWhiteSpace(source.Revision)
                    || source.Page is <= 0)
                    throw Invalid("Sources require a document and revision, with positive page numbers when specified.");
        }
        var statements = Statements.ToDictionary(s => s.Id);
        var connections = Connections.ToDictionary(c => c.Id);
        foreach (var binding in unresolved.Values)
        {
            if (identities.Contains(binding.FormerNetId))
                throw Invalid("An unresolved former net cannot name a structural entity or statement.");
            if (statements.TryGetValue(binding.OwnerId, out var statement))
            {
                if (statement.TargetId != binding.FormerNetId)
                    throw Invalid("The retained requirement must keep its original unresolved net target.");
            }
            else if (!connections.TryGetValue(binding.OwnerId, out var connection)
                || connection.NetIds.Contains(binding.FormerNetId))
                throw Invalid("Unresolved bindings need an existing owner and cannot simultaneously be resolved connection nets.");
        }
        // Provenance is a graph, not an ordering-dependent list.
        var complete = new HashSet<Guid>();
        foreach (var statement in Statements)
        {
            var active = new HashSet<Guid>();
            var stack = new Stack<(Guid Id, bool Exit)>();
            stack.Push((statement.Id, false));
            while (stack.TryPop(out var entry))
            {
                if (entry.Exit) { active.Remove(entry.Id); complete.Add(entry.Id); continue; }
                if (complete.Contains(entry.Id)) continue;
                if (!active.Add(entry.Id) || !statements.TryGetValue(entry.Id, out var current))
                    throw Invalid("Statement provenance contains an unresolved reference or a cycle.");
                stack.Push((entry.Id, true));
                foreach (Guid source in current.DerivedFrom) stack.Push((source, false));
            }
        }
    }

    /// <summary>Preserve affected owners after an explicitly identified electrical
    /// change. Candidate nets are possibilities, never inferred assignments.</summary>
    public StructuralDiagram RetainUnresolvedNet(Circuit before, Circuit after, Guid formerNetId,
        NetBindingChangeKind change, string reason, IReadOnlyList<Guid> candidateNetIds) =>
        RetainUnresolvedNets(before, after, [new(formerNetId, change, reason, candidateNetIds)]);

    public StructuralDiagram RetainUnresolvedNets(Circuit before, Circuit after, IReadOnlyList<NetIdentityChange> changes)
    {
        Validate(before); after.Validate();
        var result = RetainNetReferences(before, after, changes);
        result.Validate(after);
        return result;
    }

    // Ownership and net changes may invalidate references together. Their
    // composed model operation validates once after retaining both families.
    internal StructuralDiagram RetainNetReferences(Circuit before, Circuit after, IReadOnlyList<NetIdentityChange> changes)
    {
        if (changes.Select(c => c.FormerNetId).Distinct().Count() != changes.Count
            || changes.Any(c => !before.Nets.Any(net => net.Id == c.FormerNetId)
                || !Enum.IsDefined(c.Change) || string.IsNullOrWhiteSpace(c.Reason)
                || c.CandidateNetIds.Distinct().Count() != c.CandidateNetIds.Count
                || c.CandidateNetIds.Any(id => !after.Nets.Any(net => net.Id == id))))
            throw Invalid("Each net change requires one exact former net, a reason and distinct existing candidate nets.");
        var formerIds = changes.Select(c => c.FormerNetId).ToHashSet();
        var changesById = changes.ToDictionary(c => c.FormerNetId);
        var currentIds = after.Nets.Select(n => n.Id).ToHashSet();
        var retained = (UnresolvedNetBindings ?? []).Select(binding => binding with
        {
            // Candidates remain possibilities. Further electrical changes can
            // retire one; follow only the explicitly supplied identity changes.
            CandidateNetIds = binding.CandidateNetIds.SelectMany(id => currentIds.Contains(id) ? [id]
                : changesById.TryGetValue(id, out var change) ? change.CandidateNetIds
                : throw Invalid("A removed unresolved candidate requires an explicit net identity change."))
                .Distinct().Order().ToArray()
        }).ToList();
        var retainedOwners = retained.Select(b => (b.OwnerId, b.FormerNetId)).ToHashSet();
        foreach (var change in changes)
        {
            var owners = Connections.Where(c => c.NetIds.Contains(change.FormerNetId)).Select(c => c.Id)
                .Concat(Statements.Where(s => s.TargetId == change.FormerNetId).Select(s => s.Id)).ToArray();
            foreach (Guid owner in owners)
                if (retainedOwners.Add((owner, change.FormerNetId)))
                    retained.Add(new(owner, change.FormerNetId, change.Change, change.Reason, change.CandidateNetIds.ToArray()));
        }
        var result = this with
        {
            Connections = Connections.Select(c => c with { NetIds = c.NetIds.Where(id => !formerIds.Contains(id)).ToArray() }).ToArray(),
            UnresolvedNetBindings = retained.Count == 0 ? null : retained
        };
        return result;
    }

    /// <summary>Explicitly reassign one retained owner. Other requirements that
    /// referenced the same former net stay unresolved.</summary>
    public StructuralDiagram ResolveUnresolvedNet(Circuit circuit, Guid ownerId, Guid formerNetId, Guid selectedNetId)
    {
        Validate(circuit);
        if (!(UnresolvedNetBindings ?? []).Any(b => b.OwnerId == ownerId && b.FormerNetId == formerNetId)
            || !circuit.Nets.Any(n => n.Id == selectedNetId))
            throw Invalid("Select an existing net for one exact unresolved owner and former net.");
        var remaining = UnresolvedNetBindings!.Where(b => b.OwnerId != ownerId || b.FormerNetId != formerNetId).ToArray();
        var result = this with
        {
            Connections = Connections.Select(c => c.Id == ownerId
                ? c with { NetIds = c.NetIds.Contains(selectedNetId) ? c.NetIds : c.NetIds.Append(selectedNetId).ToArray() } : c).ToArray(),
            Statements = Statements.Select(s => s.Id == ownerId ? s with { TargetId = selectedNetId } : s).ToArray(),
            UnresolvedNetBindings = remaining.Length == 0 ? null : remaining
        };
        result.Validate(circuit);
        return result;
    }

    private static AutomationException Invalid(string message) => new("invalid_structural_diagram", message);
}
