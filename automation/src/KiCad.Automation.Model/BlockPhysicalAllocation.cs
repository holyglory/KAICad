using System.Collections.Immutable;

namespace KiCad.Automation.Model;

/// <summary>The physical level at which a structural block is realised. This is
/// deliberately separate from component identity: a functional block may span a
/// board, several boards, or a larger assembly without pretending that its native
/// schematic has already been generated.</summary>
public enum PhysicalAllocationKind
{
    Component,
    Board,
    BoardStack,
    Assembly
}

/// <summary>Whether a block's physical allocation is known completely, partially,
/// or not yet known. Unknown and partial states always retain an explanation.</summary>
public enum PhysicalAllocationState
{
    Unknown,
    Partial,
    Resolved
}

/// <summary>One stable physical target. Reference is an exact external identity
/// when available (for example a child design UUID or repository-relative board
/// path); it is never inferred from the display name.</summary>
public sealed record PhysicalAllocationTarget(Guid Id, PhysicalAllocationKind Kind, string Name,
    string? Reference = null, string? RepositoryPath = null, Guid? ParentId = null)
{
    public void Validate(IReadOnlySet<Guid> targetIds)
    {
        if (Id == Guid.Empty || !Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(Name))
            throw Invalid("Physical allocation targets need a stable identity, supported kind and name.");
        Text(Name);
        if (Reference is { } reference) Text(reference);
        if (RepositoryPath is { } path)
        {
            Text(path);
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\')
                || path.Split('/', '\\').Any(part => part == ".."))
                throw Invalid("Physical allocation paths must be non-empty repository-relative paths.");
        }
        if (ParentId == Id || (ParentId is { } parent && !targetIds.Contains(parent)))
            throw Invalid("A physical allocation target may only name an existing different parent target.");
    }

    private static void Text(string value)
    {
        try { System.Xml.XmlConvert.VerifyXmlChars(value); }
        catch (System.Xml.XmlException) { throw Invalid("Physical allocation text must be preservable in XML."); }
    }

    private static AutomationException Invalid(string message) => new("invalid_physical_allocation", message);
}

/// <summary>Explicit mapping of one block revision to physical targets. The
/// mapping is versioned with the block revision, while target identities remain
/// stable across revisions. It does not allocate components or boards by itself.</summary>
public sealed record BlockPhysicalAllocation(PhysicalAllocationState State,
    ImmutableArray<PhysicalAllocationTarget> Targets, string? UnknownReason = null)
{
    public static BlockPhysicalAllocation Unknown(string reason) => new(PhysicalAllocationState.Unknown, [], reason);

    public void Validate()
    {
        if (!Enum.IsDefined(State) || Targets.IsDefault || Targets.Any(t => t is null)
            || Targets.Select(t => t.Id).Distinct().Count() != Targets.Length)
            throw Invalid("Physical allocation needs supported state, materialized targets and distinct target identities.");
        var ids = Targets.Select(t => t.Id).ToHashSet();
        foreach (var target in Targets) target.Validate(ids);
        var parent = Targets.ToDictionary(t => t.Id, t => t.ParentId);
        foreach (var target in Targets)
        {
            var seen = new HashSet<Guid>();
            Guid? current = target.ParentId;
            while (current is { } id)
            {
                if (!seen.Add(id) || !parent.TryGetValue(id, out current))
                    throw Invalid("Physical allocation parent relationships must be acyclic.");
            }
        }
        switch (State)
        {
            case PhysicalAllocationState.Unknown when Targets.Length != 0 || string.IsNullOrWhiteSpace(UnknownReason):
                throw Invalid("An unknown physical allocation must retain a reason and no guessed targets.");
            case PhysicalAllocationState.Partial when Targets.Length == 0 || string.IsNullOrWhiteSpace(UnknownReason):
                throw Invalid("A partial physical allocation must retain mapped targets and explain what remains unknown.");
            case PhysicalAllocationState.Resolved when Targets.Length == 0 || UnknownReason is not null:
                throw Invalid("A resolved physical allocation needs targets and no unresolved reason.");
        }
        if (UnknownReason is { } reason) Text(reason);
    }

    public bool SameContents(BlockPhysicalAllocation other) => other is not null && State == other.State
        && Targets.SequenceEqual(other.Targets) && UnknownReason == other.UnknownReason;

    private static void Text(string value)
    {
        try { System.Xml.XmlConvert.VerifyXmlChars(value); }
        catch (System.Xml.XmlException) { throw Invalid("Physical allocation text must be preservable in XML."); }
    }

    private static AutomationException Invalid(string message) => new("invalid_physical_allocation", message);
}
