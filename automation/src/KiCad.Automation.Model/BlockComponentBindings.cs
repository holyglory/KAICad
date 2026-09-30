using System.Collections.Immutable;

namespace KiCad.Automation.Model;

/// <summary>An explicit realization, not a component inferred from a block name or part choice.
/// The circuit identity prevents replacement of a child design file from silently rebinding it.</summary>
public sealed record ComponentRealization(Guid DesignId, Guid CircuitId, Guid ComponentId);

public sealed record BlockComponentBindings(ImmutableArray<ComponentRealization> Targets)
{
    public static BlockComponentBindings Empty { get; } = new([]);

    public void Validate()
    {
        if (Targets.IsDefault || Targets.Any(t => t is null || t.DesignId == Guid.Empty
                || t.CircuitId == Guid.Empty || t.ComponentId == Guid.Empty)
            || Targets.Distinct().Count() != Targets.Length)
            throw new AutomationException("invalid_block_component_bindings",
                "Component bindings require distinct exact design, circuit and component identities.");
        // A block cannot claim that the same child design currently contains two different circuits.
        if (Targets.GroupBy(t => t.DesignId).Any(g => g.Select(t => t.CircuitId).Distinct().Count() != 1))
            throw new AutomationException("ambiguous_block_circuit", "Use one exact circuit identity per child design in a block revision.");
    }

    public bool SameContents(BlockComponentBindings other) => other is not null && Targets.SequenceEqual(other.Targets);
}
