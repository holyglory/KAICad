namespace KiCad.Automation.Model;

/// <summary>Retain design intent across an explicitly identified ownership change.
/// Does not discover native identities, guess successors or edit a native document.</summary>
public static class ComponentReferenceRetention
{
    public static EngineeringDesign Retain(EngineeringDesign previous, Circuit circuit,
        IReadOnlyList<ComponentReferenceChange> changes, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries,
        IReadOnlyList<NetIdentityChange>? netChanges = null)
    {
        previous.Validate(libraries); circuit.Validate();
        var before = new ComponentReferenceIndex(previous.Circuit);
        var after = new ComponentReferenceIndex(circuit);
        var knownFormer = (previous.Structure.UnresolvedComponentReferences ?? []).Select(r => r.FormerTarget)
            .Concat((previous.Structure.UnresolvedComponentReferences ?? []).Select(r => new ComponentReferenceTarget(r.FormerTarget.ComponentId)))
            .Concat((previous.UnresolvedGuidanceBindings ?? []).Select(r => new ComponentReferenceTarget(r.ComponentInstanceId))).ToHashSet();
        var byTarget = new Dictionary<ComponentReferenceTarget, ComponentReferenceChange>();
        foreach (var change in changes)
        {
            bool pin = change.FormerTarget.PinNumber is not null;
            before.Validate(change.FormerTarget, pin, candidate: false);
            if (!Enum.IsDefined(change.Change) || string.IsNullOrWhiteSpace(change.Reason)
                || (!before.Exists(change.FormerTarget) && !knownFormer.Contains(change.FormerTarget))
                || (change.Change == ComponentReferenceChangeKind.Removed && after.Exists(change.FormerTarget))
                || !byTarget.TryAdd(change.FormerTarget, change)
                || change.CandidateTargets.Distinct().Count() != change.CandidateTargets.Count)
                throw ComponentReferenceIndex.Invalid("Ownership changes require distinct known former targets, truthful removal state and explicit reasons/candidates.");
            foreach (var target in change.CandidateTargets) after.Validate(target, pin, candidate: true);
        }
        ComponentReferenceChange? Changed(ComponentReferenceTarget target)
        {
            if (byTarget.TryGetValue(target, out var exact)) return exact;
            // A removed component also invalidates its concrete pin references.
            // A replacement component does not establish any replacement pin.
            return target.PinNumber is not null && byTarget.TryGetValue(new(target.ComponentId), out var component)
                ? new(target, component.Change, component.Reason, []) : null;
        }
        IEnumerable<ComponentReferenceTarget> Follow(ComponentReferenceTarget target) => after.Exists(target) ? [target]
            : Changed(target)?.CandidateTargets
                ?? throw ComponentReferenceIndex.Invalid("A removed unresolved candidate requires an explicit component/pin identity change.");
        var retained = (previous.Structure.UnresolvedComponentReferences ?? []).ToDictionary(
            r => new ComponentReferenceKey(r.OwnerId, r.Slot, r.FormerTarget),
            r => r with { CandidateTargets = r.CandidateTargets.SelectMany(Follow).Distinct()
                .OrderBy(t => t.ComponentId).ThenBy(t => t.PinNumber, StringComparer.Ordinal).ToArray() });
        bool Keep(Guid owner, ComponentReferenceSlot slot, ComponentReferenceTarget target)
        {
            var key = new ComponentReferenceKey(owner, slot, target);
            if (retained.ContainsKey(key)) return true;
            if (Changed(target) is not { } change) return false;
            retained.Add(key, new(owner, slot, target, change.Change, change.Reason, change.CandidateTargets.ToArray()));
            return true;
        }
        foreach (var statement in previous.Structure.Statements)
        {
            Keep(statement.Id, ComponentReferenceSlot.StatementTarget, new(statement.TargetId));
            if (statement.Connection is { } connection)
            {
                Keep(statement.Id, ComponentReferenceSlot.FirstPin, new(connection.First.ComponentId, connection.First.Pin));
                Keep(statement.Id, ComponentReferenceSlot.SecondPin, new(connection.Second.ComponentId, connection.Second.Pin));
            }
        }
        var blocks = previous.Structure.Blocks.Select(block => block with
        { ComponentIds = block.ComponentIds.Where(id => !Keep(block.Id, ComponentReferenceSlot.BlockRealization, new(id))).ToArray() }).ToArray();
        var guidance = (previous.UnresolvedGuidanceBindings ?? []).ToDictionary(r => r.ComponentInstanceId,
            r => r with { CandidateComponentIds = r.CandidateComponentIds.SelectMany(id => Follow(new(id)))
                .Select(t => t.ComponentId).Distinct().Order().ToArray() });
        foreach (var binding in previous.ComponentBindings)
            if (!guidance.ContainsKey(binding.ComponentInstanceId) && Changed(new(binding.ComponentInstanceId)) is { } change)
                guidance.Add(binding.ComponentInstanceId, new(binding.ComponentInstanceId, change.Change, change.Reason,
                    change.CandidateTargets.Select(t => t.ComponentId).ToArray()));
        var result = previous with
        {
            Circuit = circuit,
            Structure = previous.Structure.RetainNetReferences(previous.Circuit, circuit, netChanges ?? []) with
            { Blocks = blocks, UnresolvedComponentReferences = retained.Count == 0 ? null : retained.Values
                .OrderBy(r => r.OwnerId).ThenBy(r => r.Slot).ThenBy(r => r.FormerTarget.ComponentId)
                .ThenBy(r => r.FormerTarget.PinNumber, StringComparer.Ordinal).ToArray() },
            UnresolvedGuidanceBindings = guidance.Count == 0 ? null : guidance.Values.OrderBy(r => r.ComponentInstanceId).ToArray()
        };
        // Net requirements use their existing independent retention contract.
        // Missing unreported references remain errors, never silent deletions.
        result.Validate(libraries);
        return result;
    }

    public static EngineeringDesign Resolve(EngineeringDesign design, Guid owner, ComponentReferenceSlot slot,
        ComponentReferenceTarget former, ComponentReferenceTarget target, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries)
    {
        design.Validate(libraries);
        var index = new ComponentReferenceIndex(design.Circuit);
        index.Validate(target, slot is ComponentReferenceSlot.FirstPin or ComponentReferenceSlot.SecondPin, candidate: true);
        var pending = design.Structure.UnresolvedComponentReferences ?? [];
        var selected = pending.SingleOrDefault(r => r.OwnerId == owner && r.Slot == slot && r.FormerTarget == former)
            ?? throw ComponentReferenceIndex.Invalid("Select one exact retained component/pin reference before resolving it.");
        var statements = design.Structure.Statements.Select(s => s.Id != owner ? s : slot switch
        {
            ComponentReferenceSlot.StatementTarget => s with { TargetId = target.ComponentId },
            ComponentReferenceSlot.FirstPin => s with { Connection = s.Connection! with { First = new(target.ComponentId, target.PinNumber!) } },
            ComponentReferenceSlot.SecondPin => s with { Connection = s.Connection! with { Second = new(target.ComponentId, target.PinNumber!) } },
            _ => s
        }).ToArray();
        var blocks = design.Structure.Blocks.Select(b => b.Id == owner && slot == ComponentReferenceSlot.BlockRealization
            ? b with { ComponentIds = b.ComponentIds.Append(target.ComponentId).Distinct().ToArray() } : b).ToArray();
        var remaining = pending.Where(r => !ReferenceEquals(r, selected)).ToArray();
        var result = design with { Structure = design.Structure with
            { Statements = statements, Blocks = blocks, UnresolvedComponentReferences = remaining.Length == 0 ? null : remaining } };
        result.Validate(libraries);
        return result;
    }

    public static EngineeringDesign ResolveGuidance(EngineeringDesign design, Guid formerComponent, Guid targetComponent,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries)
    {
        design.Validate(libraries);
        new ComponentReferenceIndex(design.Circuit).Validate(new(targetComponent), pin: false, candidate: true);
        var pending = design.UnresolvedGuidanceBindings ?? [];
        if (!pending.Any(r => r.ComponentInstanceId == formerComponent))
            throw ComponentReferenceIndex.Invalid("Select the exact retained guidance assignment before resolving it.");
        if (formerComponent != targetComponent && design.ComponentBindings.Any(b => b.ComponentInstanceId == targetComponent))
            throw ComponentReferenceIndex.Invalid("The chosen component already has guidance; resolve the competing assignments explicitly.");
        var remaining = pending.Where(r => r.ComponentInstanceId != formerComponent).ToArray();
        var result = design with { ComponentBindings = design.ComponentBindings.Select(b => b.ComponentInstanceId == formerComponent
                ? b with { ComponentInstanceId = targetComponent } : b).ToArray(),
            UnresolvedGuidanceBindings = remaining.Length == 0 ? null : remaining };
        result.Validate(libraries);
        return result;
    }
}
