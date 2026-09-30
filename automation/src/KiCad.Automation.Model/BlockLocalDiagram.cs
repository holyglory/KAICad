using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Model;

/// <summary>What a boundary or link carries. Unspecified is a valid "not stated" value.</summary>
public enum DiagramDomain { Unspecified, Power, Data, Control, Analog, Mechanical }

/// <summary>Direction relative to the owning block. Unspecified is a valid "not stated" value.</summary>
public enum DiagramInterfaceDirection { Unspecified, Input, Output, Bidirectional }

public sealed record DiagramBoundaryInterface(Guid Id, string Name, string Intent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DiagramDomain Domain = DiagramDomain.Unspecified,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DiagramInterfaceDirection Direction = DiagramInterfaceDirection.Unspecified);

/// <summary>Interfaces and selected relationships at one diagram level. Child interiors
/// are not duplicated here. The per-level layout and the boundary-to-interior realization map
/// are versioned with this revision (contract rbg-v2 sections 4.3 and 4.4); physical allocation
/// is a separate concern.</summary>
[method: JsonConstructor]
public sealed record BlockLocalDiagram(ImmutableArray<DiagramBoundaryInterface> Interfaces,
    ImmutableArray<ConnectionSelection> Connections, ImmutableArray<DiagramAnnotation> Annotations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagramPresentationView? Presentation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<InterfaceRealization> InterfaceRealizations)
{
    public BlockLocalDiagram(ImmutableArray<DiagramBoundaryInterface> Interfaces, ImmutableArray<ConnectionSelection> Connections)
        : this(Interfaces, Connections, [], null, default) { }
    public BlockLocalDiagram(ImmutableArray<DiagramBoundaryInterface> Interfaces, ImmutableArray<ConnectionSelection> Connections,
        ImmutableArray<DiagramAnnotation> Annotations)
        : this(Interfaces, Connections, Annotations, null, default) { }
    // Keep optional presentation collections materialized so MCP's JSON-schema
    // exporter can describe the record without enumerating a default ImmutableArray.
    public static BlockLocalDiagram Empty { get; } = new([], [], []);
    public ImmutableArray<DiagramAnnotation> Notes => Annotations.IsDefault ? [] : Annotations;
    /// <summary>The stored layout; an absent view is the empty view (everything unplaced).</summary>
    [JsonIgnore] public DiagramPresentationView Layout => Presentation ?? DiagramPresentationView.Empty;
    /// <summary>Interface realization records; absent means none is stated.</summary>
    [JsonIgnore] public ImmutableArray<InterfaceRealization> Realizations => InterfaceRealizations.IsDefault ? [] : InterfaceRealizations;
    /// <summary>True when this level carries a fact that only schema 2 can store.</summary>
    [JsonIgnore] public bool UsesSchemaTwo => Presentation is { IsEmpty: false } || !Realizations.IsEmpty
        || Interfaces.Any(i => i.Domain != DiagramDomain.Unspecified || i.Direction != DiagramInterfaceDirection.Unspecified);

    public void Validate() => Validate(null);

    public void Validate(Guid? scopeBlockId)
    {
        if (Interfaces.IsDefault || Connections.IsDefault)
            throw new AutomationException("invalid_block_local_diagram", "Provide explicit interface and connection lists, including empty lists when unresolved.");
        var ids = new HashSet<Guid>();
        foreach (var item in Interfaces)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Name) || item.Intent is null)
                throw new AutomationException("invalid_block_local_diagram", "Boundary interfaces require distinct identities, names and explicit intent text.");
            if (!Enum.IsDefined(item.Domain) || !Enum.IsDefined(item.Direction))
                throw new AutomationException("invalid_block_local_diagram", "Boundary interfaces use a supported domain and direction, or leave them unspecified.");
            DiagramEndpointBinding.Text(item.Name); DiagramEndpointBinding.Text(item.Intent);
        }
        if (Connections.Any(c => c is null) || Connections.Select(c => c.ConnectionId).Distinct().Count() != Connections.Length)
            throw new AutomationException("invalid_block_local_diagram", "A local diagram must pin each connection occurrence exactly once.");
        foreach (var note in Notes)
        {
            if (note is null || !ids.Add(note.Id)) throw new AutomationException("invalid_block_local_diagram", "Annotation identities must be distinct from each other and boundary interfaces.");
            note.Validate();
        }
        Presentation?.Validate(scopeBlockId);
        var realized = new HashSet<Guid>();
        foreach (var realization in Realizations)
        {
            if (realization is null) throw InterfaceRealization.Invalid("A realization record cannot be null.");
            realization.Validate();
            if (!Interfaces.Any(i => i.Id == realization.InterfaceId))
                throw InterfaceRealization.Invalid("A realization maps one of this block's own boundary interfaces in this revision.");
            if (!realized.Add(realization.InterfaceId))
                throw InterfaceRealization.Invalid("Record at most one realization per boundary interface.");
        }
    }

    /// <summary>Complete content equality, including dormant layout entries.</summary>
    public bool SameContents(BlockLocalDiagram other) => SameStructure(other) && DiagramPresentationView.Same(Presentation, other.Presentation);

    /// <summary>Content equality of everything except the layout.</summary>
    public bool SameStructure(BlockLocalDiagram other) => other is not null
        && Interfaces.SequenceEqual(other.Interfaces) && Connections.SequenceEqual(other.Connections)
        && Notes.Length == other.Notes.Length && Notes.Zip(other.Notes).All(n => n.First.SameContents(n.Second))
        && InterfaceRealization.Same(Realizations, other.Realizations);
}
