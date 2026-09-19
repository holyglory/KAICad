using System.Collections.Immutable;

namespace KiCad.Automation.Model;

public sealed record DiagramBoundaryInterface(Guid Id, string Name, string Intent);

/// <summary>Interfaces and selected relationships at one diagram level. Child interiors
/// are not duplicated here. Geometry and physical allocation are separate concerns.</summary>
public sealed record BlockLocalDiagram(ImmutableArray<DiagramBoundaryInterface> Interfaces,
    ImmutableArray<ConnectionSelection> Connections)
{
    public static BlockLocalDiagram Empty { get; } = new([], []);

    public void Validate()
    {
        if (Interfaces.IsDefault || Connections.IsDefault)
            throw new AutomationException("invalid_block_local_diagram", "Provide explicit interface and connection lists, including empty lists when unresolved.");
        var ids = new HashSet<Guid>();
        foreach (var item in Interfaces)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Name) || item.Intent is null)
                throw new AutomationException("invalid_block_local_diagram", "Boundary interfaces require distinct identities, names and explicit intent text.");
            DiagramEndpointBinding.Text(item.Name); DiagramEndpointBinding.Text(item.Intent);
        }
        if (Connections.Any(c => c is null) || Connections.Select(c => c.ConnectionId).Distinct().Count() != Connections.Length)
            throw new AutomationException("invalid_block_local_diagram", "A local diagram must pin each connection occurrence exactly once.");
    }

    public bool SameContents(BlockLocalDiagram other) => Interfaces.SequenceEqual(other.Interfaces) && Connections.SequenceEqual(other.Connections);
}
