using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Model;

/// <summary>How much of a mapping is stated. Nothing is ever derived to reach Resolved.</summary>
public enum DiagramRealizationState { Unknown, Partial, Resolved }

public enum InterfaceRealizationTargetKind { ChildInterface, LocalConnection, Pin }

/// <summary>One interior element realizing a boundary interface: a direct child's interface, a
/// connection of this level (a root or a member) or an exact pin bound to this block.</summary>
public sealed record InterfaceRealizationTarget(InterfaceRealizationTargetKind Kind, Guid? BlockId, Guid? InterfaceId,
    Guid? ConnectionId, DiagramPinTarget? Pin)
{
    public static InterfaceRealizationTarget ChildInterface(Guid blockId, Guid interfaceId) =>
        new(InterfaceRealizationTargetKind.ChildInterface, blockId, interfaceId, null, null);
    public static InterfaceRealizationTarget LocalConnection(Guid connectionId) =>
        new(InterfaceRealizationTargetKind.LocalConnection, null, null, connectionId, null);
    public static InterfaceRealizationTarget ExactPin(DiagramPinTarget pin) => new(InterfaceRealizationTargetKind.Pin, null, null, null, pin);

    public void Validate()
    {
        bool valid = Kind switch
        {
            InterfaceRealizationTargetKind.ChildInterface => BlockId is { } block && block != Guid.Empty
                && InterfaceId is { } port && port != Guid.Empty && ConnectionId is null && Pin is null,
            InterfaceRealizationTargetKind.LocalConnection => ConnectionId is { } link && link != Guid.Empty
                && BlockId is null && InterfaceId is null && Pin is null,
            InterfaceRealizationTargetKind.Pin => Pin is not null && BlockId is null && InterfaceId is null && ConnectionId is null,
            _ => false
        };
        if (!valid) throw InterfaceRealization.Invalid("A realization target states exactly the identities its kind uses and nothing else.");
        Pin?.Validate();
    }

    public bool SameTarget(InterfaceRealizationTarget? other) => other is not null && Kind == other.Kind && BlockId == other.BlockId
        && InterfaceId == other.InterfaceId && ConnectionId == other.ConnectionId
        && (Pin is null ? other.Pin is null : other.Pin is not null && Pin.SamePin(other.Pin));

    /// <summary>Canonical order: kind, then block, interface and connection identities, then the pin.</summary>
    public static int Compare(InterfaceRealizationTarget left, InterfaceRealizationTarget right)
    {
        int result = left.Kind.CompareTo(right.Kind);
        if (result == 0) result = string.CompareOrdinal(Id(left.BlockId), Id(right.BlockId));
        if (result == 0) result = string.CompareOrdinal(Id(left.InterfaceId), Id(right.InterfaceId));
        if (result == 0) result = string.CompareOrdinal(Id(left.ConnectionId), Id(right.ConnectionId));
        if (result == 0) result = string.CompareOrdinal(PinKey(left.Pin), PinKey(right.Pin));
        return result;
    }

    internal static string PinKey(DiagramPinTarget? pin) => pin is null ? "" : string.Join("/", pin.DesignId.ToString("D"),
        pin.ComponentId.ToString("D"), string.Join(",", pin.SheetInstancePath.IsDefault ? [] : pin.SheetInstancePath.Select(s => s.ToString("D"))), pin.Pin);
    private static string Id(Guid? id) => id?.ToString("D") ?? "";
}

/// <summary>A block's boundary interface mapped to its interior (contract rbg-v2 section 4.4).
/// One boundary link may be realized by many internal targets; no one-link-to-one-net equivalence
/// is implied. No record at all means "not stated".</summary>
public sealed record InterfaceRealization(Guid InterfaceId, DiagramRealizationState State,
    ImmutableArray<InterfaceRealizationTarget> Targets, string? UnresolvedReason, ImmutableArray<SourceReference> Sources)
{
    [JsonIgnore] public ImmutableArray<InterfaceRealizationTarget> TargetList => Targets.IsDefault ? [] : Targets;
    [JsonIgnore] public ImmutableArray<SourceReference> SourceList => Sources.IsDefault ? [] : Sources;

    /// <summary>Context-free rules (IR2 exactness and distinctness, IR3 state). The owning graph
    /// checks that every target exists in the same revision (IR1, IR2).</summary>
    public void Validate()
    {
        if (InterfaceId == Guid.Empty || !Enum.IsDefined(State))
            throw Invalid("A realization needs its exact boundary interface and a supported state.");
        foreach (var target in TargetList)
        {
            if (target is null) throw Invalid("A realization target cannot be null.");
            target.Validate();
        }
        for (int i = 0; i < TargetList.Length; ++i)
            for (int j = 0; j < i; ++j)
                if (TargetList[i].SameTarget(TargetList[j])) throw Invalid("Realization targets must be distinct.");
        if (UnresolvedReason is { } reason) DiagramEndpointBinding.Text(reason);
        bool stated = !string.IsNullOrWhiteSpace(UnresolvedReason);
        bool valid = State switch
        {
            DiagramRealizationState.Unknown => TargetList.IsEmpty && stated,
            DiagramRealizationState.Partial => !TargetList.IsEmpty && stated,
            DiagramRealizationState.Resolved => !TargetList.IsEmpty && UnresolvedReason is null,
            _ => false
        };
        if (!valid) throw Invalid(State switch
        {
            DiagramRealizationState.Unknown => "An unknown realization has no targets and says why.",
            DiagramRealizationState.Partial => "A partial realization names what is known and says what remains.",
            _ => "A resolved realization names its targets and has no unresolved reason."
        });
        ValidateSources(SourceList, Invalid);
    }

    /// <summary>Targets in canonical order.</summary>
    public InterfaceRealization Canonical() => this with { Targets = [.. TargetList.Order(Comparer<InterfaceRealizationTarget>.Create(InterfaceRealizationTarget.Compare))],
        Sources = SourceList };

    public bool SameContents(InterfaceRealization? other)
    {
        if (other is null) return false;
        var a = Canonical(); var b = other.Canonical();
        return a.InterfaceId == b.InterfaceId && a.State == b.State && a.UnresolvedReason == b.UnresolvedReason
            && a.Targets.Length == b.Targets.Length && a.Targets.Zip(b.Targets).All(p => p.First.SameTarget(p.Second))
            && a.Sources.SequenceEqual(b.Sources);
    }

    /// <summary>Records sorted by interface identity, as the XML writer stores them.</summary>
    public static ImmutableArray<InterfaceRealization> CanonicalList(ImmutableArray<InterfaceRealization> records) =>
        [.. (records.IsDefault ? [] : records).OrderBy(r => r.InterfaceId.ToString("D"), StringComparer.Ordinal).Select(r => r.Canonical())];

    public static bool Same(ImmutableArray<InterfaceRealization> left, ImmutableArray<InterfaceRealization> right)
    {
        var a = CanonicalList(left); var b = CanonicalList(right);
        return a.Length == b.Length && a.Zip(b).All(p => p.First.SameContents(p.Second));
    }

    internal static void ValidateSources(ImmutableArray<SourceReference> sources, Func<string, AutomationException> invalid)
    {
        foreach (var source in sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.DocumentId) || string.IsNullOrWhiteSpace(source.Revision) || source.Page is <= 0)
                throw invalid("Realization sources require exact document revisions and positive page numbers when supplied.");
            DiagramEndpointBinding.Text(source.DocumentId); DiagramEndpointBinding.Text(source.Revision);
            if (source.Table is { } table) DiagramEndpointBinding.Text(table);
            if (source.PartVariant is { } variant) DiagramEndpointBinding.Text(variant);
        }
    }

    internal static AutomationException Invalid(string message) => new("invalid_interface_realization", message);
}
