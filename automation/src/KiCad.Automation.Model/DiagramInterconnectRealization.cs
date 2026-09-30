using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Model;

public enum InterconnectSegmentKind { BoardNet, Connector, Harness, HardwareInterface, External }

/// <summary>One typed piece of the physical path realizing a connection revision. The owning
/// design is always explicit; a label is display text and never an identity. A link is never
/// equated with copper: only the stated segments and joins are recorded.</summary>
public sealed record InterconnectSegment(Guid Id, InterconnectSegmentKind Kind, string? Label, Guid? DesignId, Guid? CircuitId,
    Guid? NetId, Guid? ComponentId, ImmutableArray<DiagramPinTarget> Pins, Guid? PhysicalTargetId, Guid? HardwareInterfaceId,
    string? Reference, string? RepositoryPath, string? UnresolvedReason)
{
    [JsonIgnore] public ImmutableArray<DiagramPinTarget> PinList => Pins.IsDefault ? [] : Pins;

    /// <summary>All required identities are present, a connector names its pins and nothing is unresolved.</summary>
    [JsonIgnore] public bool IsComplete => UnresolvedReason is null && Kind switch
    {
        InterconnectSegmentKind.BoardNet => DesignId is not null && CircuitId is not null && NetId is not null,
        InterconnectSegmentKind.Connector => DesignId is not null && CircuitId is not null && ComponentId is not null && !PinList.IsEmpty,
        InterconnectSegmentKind.Harness => PhysicalTargetId is not null,
        InterconnectSegmentKind.HardwareInterface => HardwareInterfaceId is not null,
        InterconnectSegmentKind.External => !string.IsNullOrWhiteSpace(Label) && (Reference is not null || RepositoryPath is not null),
        _ => false
    };

    /// <summary>IC2 and IC3 for one segment: fields a kind does not use stay empty, and an
    /// incomplete segment says why.</summary>
    public void Validate()
    {
        if (Id == Guid.Empty || !Enum.IsDefined(Kind)) throw InterconnectRealization.Invalid("A segment needs an exact identity and a supported kind.");
        if (new[] { DesignId, CircuitId, NetId, ComponentId, PhysicalTargetId, HardwareInterfaceId }.Any(id => id == Guid.Empty))
            throw InterconnectRealization.Invalid("Segment identities must be exact non-empty UUIDs when stated.");
        bool allowed = Kind switch
        {
            InterconnectSegmentKind.BoardNet => ComponentId is null && PinList.IsEmpty && PhysicalTargetId is null && HardwareInterfaceId is null
                && Reference is null && RepositoryPath is null,
            InterconnectSegmentKind.Connector => NetId is null && PhysicalTargetId is null && HardwareInterfaceId is null
                && Reference is null && RepositoryPath is null,
            InterconnectSegmentKind.Harness => DesignId is null && CircuitId is null && NetId is null && ComponentId is null && PinList.IsEmpty
                && HardwareInterfaceId is null && Reference is null && RepositoryPath is null,
            InterconnectSegmentKind.HardwareInterface => DesignId is null && CircuitId is null && NetId is null && ComponentId is null
                && PinList.IsEmpty && PhysicalTargetId is null && Reference is null && RepositoryPath is null,
            InterconnectSegmentKind.External => DesignId is null && CircuitId is null && NetId is null && ComponentId is null
                && PinList.IsEmpty && PhysicalTargetId is null && HardwareInterfaceId is null,
            _ => false
        };
        if (!allowed) throw InterconnectRealization.Invalid($"A {Kind} segment states only the identities its kind uses.");
        foreach (string? text in new[] { Label, Reference, RepositoryPath, UnresolvedReason })
            if (text is not null)
            {
                if (string.IsNullOrWhiteSpace(text)) throw InterconnectRealization.Invalid("Stated segment text cannot be blank.");
                DiagramEndpointBinding.Text(text);
            }
        if (RepositoryPath is { } path)
        {
            try { HardwareRepository.ValidatePath(path); }
            catch (AutomationException) { throw InterconnectRealization.Invalid("An external segment path is repository-relative and slash-separated."); }
        }
        for (int i = 0; i < PinList.Length; ++i)
        {
            var pin = PinList[i] ?? throw InterconnectRealization.Invalid("A connector pin cannot be null.");
            pin.Validate();
            if ((DesignId is { } design && pin.DesignId != design) || (ComponentId is { } component && pin.ComponentId != component)
                || pin.DesignId != PinList[0].DesignId || pin.ComponentId != PinList[0].ComponentId)
                throw InterconnectRealization.Invalid("Every connector pin belongs to the segment's own design and component.");
            for (int j = 0; j < i; ++j)
                if (pin.SamePin(PinList[j])) throw InterconnectRealization.Invalid("Connector pins must be distinct exact pins.");
        }
        if (!IsComplete && string.IsNullOrWhiteSpace(UnresolvedReason))
            throw InterconnectRealization.Invalid("An incomplete segment must say what remains unresolved.");
    }

    public bool SameContents(InterconnectSegment? other) => other is not null && Id == other.Id && Kind == other.Kind && Label == other.Label
        && DesignId == other.DesignId && CircuitId == other.CircuitId && NetId == other.NetId && ComponentId == other.ComponentId
        && PinList.Length == other.PinList.Length && PinList.Zip(other.PinList).All(p => p.First.SamePin(p.Second))
        && PhysicalTargetId == other.PhysicalTargetId && HardwareInterfaceId == other.HardwareInterfaceId && Reference == other.Reference
        && RepositoryPath == other.RepositoryPath && UnresolvedReason == other.UnresolvedReason;
}

/// <summary>Two segments of the same record that are physically joined. An empty join list means
/// the topology is not stated.</summary>
public sealed record InterconnectJoin(Guid FirstSegmentId, Guid SecondSegmentId);

/// <summary>The stated physical path of one connection revision (contract rbg-v2 section 4.2).
/// Absent means "not stated"; Unknown and Partial are never treated as resolved.</summary>
public sealed record InterconnectRealization(DiagramRealizationState State, ImmutableArray<InterconnectSegment> Segments,
    ImmutableArray<InterconnectJoin> Joins, string? UnresolvedReason, ImmutableArray<SourceReference> Sources)
{
    [JsonIgnore] public ImmutableArray<InterconnectSegment> SegmentList => Segments.IsDefault ? [] : Segments;
    [JsonIgnore] public ImmutableArray<InterconnectJoin> JoinList => Joins.IsDefault ? [] : Joins;
    [JsonIgnore] public ImmutableArray<SourceReference> SourceList => Sources.IsDefault ? [] : Sources;

    /// <summary>IC2-IC4 and the in-record part of IC5. Existence of designs, nets and components is
    /// deliberately not checked here (IC7).</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(State)) throw Invalid("An interconnect realization needs a supported state.");
        var ids = new HashSet<Guid>();
        foreach (var segment in SegmentList)
        {
            if (segment is null) throw Invalid("A segment cannot be null.");
            segment.Validate();
            if (!ids.Add(segment.Id)) throw Invalid("Segment identities are distinct within a realization.");
        }
        var pairs = new HashSet<(Guid, Guid)>();
        foreach (var join in JoinList)
        {
            if (join is null || !ids.Contains(join.FirstSegmentId) || !ids.Contains(join.SecondSegmentId) || join.FirstSegmentId == join.SecondSegmentId)
                throw Invalid("A join connects two different segments of the same realization.");
            var pair = string.CompareOrdinal(join.FirstSegmentId.ToString("D"), join.SecondSegmentId.ToString("D")) < 0
                ? (join.FirstSegmentId, join.SecondSegmentId) : (join.SecondSegmentId, join.FirstSegmentId);
            if (!pairs.Add(pair)) throw Invalid("Each pair of segments is joined at most once.");
        }
        if (UnresolvedReason is { } reason) DiagramEndpointBinding.Text(reason);
        bool stated = !string.IsNullOrWhiteSpace(UnresolvedReason);
        bool valid = State switch
        {
            DiagramRealizationState.Resolved => !SegmentList.IsEmpty && SegmentList.All(s => s.IsComplete) && UnresolvedReason is null,
            DiagramRealizationState.Partial => !SegmentList.IsEmpty && stated,
            DiagramRealizationState.Unknown => SegmentList.IsEmpty && JoinList.IsEmpty && stated,
            _ => false
        };
        if (!valid) throw Invalid(State switch
        {
            DiagramRealizationState.Resolved => "A resolved interconnect has complete segments only and no unresolved reason.",
            DiagramRealizationState.Partial => "A partial interconnect names its known segments and says what remains.",
            _ => "An unknown interconnect has no segments or joins and says why."
        });
        var circuits = new Dictionary<Guid, Guid>();
        foreach (var segment in SegmentList)
            if (segment.DesignId is { } design && segment.CircuitId is { } circuit)
            {
                if (circuits.TryGetValue(design, out var known) && known != circuit)
                    throw new AutomationException("ambiguous_block_circuit", "Use one exact circuit identity per design within an interconnect realization.");
                circuits[design] = circuit;
            }
        InterfaceRealization.ValidateSources(SourceList, Invalid);
    }

    /// <summary>(design, circuit) pairs stated by the segments.</summary>
    [JsonIgnore] public IEnumerable<(Guid Design, Guid Circuit)> Circuits => SegmentList
        .Where(s => s.DesignId is not null && s.CircuitId is not null).Select(s => (s.DesignId!.Value, s.CircuitId!.Value)).Distinct();

    public bool SameContents(InterconnectRealization? other) => other is not null && State == other.State && UnresolvedReason == other.UnresolvedReason
        && SegmentList.Length == other.SegmentList.Length && SegmentList.Zip(other.SegmentList).All(p => p.First.SameContents(p.Second))
        && JoinList.SequenceEqual(other.JoinList) && SourceList.SequenceEqual(other.SourceList);

    public static bool Same(InterconnectRealization? left, InterconnectRealization? right) =>
        left is null ? right is null : left.SameContents(right);

    internal static AutomationException Invalid(string message) => new("invalid_interconnect_realization", message);
}
