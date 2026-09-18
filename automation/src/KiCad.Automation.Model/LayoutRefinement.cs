namespace KiCad.Automation.Model;

public enum LayoutRefinementReason { InitialGeneration, AddedSymbols, MissingPlacement, ExplicitRequest, ConnectivityChanged }
public sealed record SymbolLayoutChange(Guid SymbolId, SymbolPlacement Placement);

/// <summary>A revision-bound request for AI arrangement, not an automatic claim
/// that the result is readable. Applying a candidate still requires native
/// connectivity checks and rendered review before acceptance.</summary>
public sealed class LayoutRefinement
{
    private readonly string baseline;
    private readonly HashSet<Guid> editable;
    public Guid CircuitId { get; }
    public DocumentRevision Revision { get; }
    public LayoutRefinementReason Reason { get; }
    public IReadOnlyList<Guid> AffectedSymbols { get; }
    public string UserInstructions { get; }

    private LayoutRefinement(Circuit circuit, DocumentRevision revision,
        LayoutRefinementReason reason, HashSet<Guid> scope, string instructions)
    {
        CircuitId = circuit.Id;
        Revision = revision;
        Reason = reason;
        editable = scope;
        AffectedSymbols = Array.AsReadOnly(scope.Order().ToArray());
        UserInstructions = instructions;
        baseline = CircuitXml.Write(circuit);
    }

    /// <summary>Existing coordinates are preserved by default, whether or not
    /// locked. Changed connections include their affected component units;
    /// callers may explicitly include an additional affected existing region.
    /// Locked objects can be context but cannot be moved by a candidate.</summary>
    public static LayoutRefinement? Request(Circuit? previous, Circuit current,
        DocumentRevision revision, string userInstructions,
        IReadOnlyCollection<Guid>? affectedSymbols = null)
    {
        current.Validate();
        if (string.IsNullOrWhiteSpace(revision.Epoch))
            throw Invalid("A layout request requires a process epoch and document revision.");
        previous?.Validate();
        if (previous is not null && previous.Id != current.Id)
            throw Invalid("Layout history must belong to the same circuit.");
        var symbols = current.Symbols.ToDictionary(s => s.Id);
        var scope = new HashSet<Guid>(affectedSymbols ?? []);
        if (scope.Any(id => !symbols.ContainsKey(id)))
            throw Invalid("An affected symbol is not in this circuit.");
        bool explicitRequest = scope.Count > 0;
        var changedComponents = ChangedConnections(previous, current);
        var previousIds = previous?.Symbols.Select(s => s.Id).ToHashSet() ?? [];
        bool added = false;
        foreach (var symbol in current.Symbols)
        {
            bool isNew = previous is not null && !previousIds.Contains(symbol.Id);
            added |= isNew;
            if (symbol.Placement is null || isNew || changedComponents.Contains(symbol.ComponentId))
                scope.Add(symbol.Id);
        }
        if (scope.Count == 0) return null;
        var reason = explicitRequest ? LayoutRefinementReason.ExplicitRequest
            : previous is null ? LayoutRefinementReason.InitialGeneration
            : added ? LayoutRefinementReason.AddedSymbols
            : changedComponents.Count > 0 ? LayoutRefinementReason.ConnectivityChanged
            : LayoutRefinementReason.MissingPlacement;
        return new(current, revision, reason, scope, userInstructions);
    }

    /// <summary>Refine newly created symbols only. Existing native geometry
    /// remains protected even when its optional XML coordinates are absent.</summary>
    public static LayoutRefinement ForAddedSymbols(Circuit previous, Circuit current,
        DocumentRevision revision, string userInstructions)
    {
        previous.Validate(); current.Validate();
        if (previous.Id != current.Id || string.IsNullOrWhiteSpace(revision.Epoch))
            throw Invalid("Additions require the same circuit and an explicit document revision.");
        var old = previous.Symbols.Select(s => s.Id).ToHashSet();
        var scope = current.Symbols.Where(s => !old.Contains(s.Id)).Select(s => s.Id).ToHashSet();
        if (scope.Count == 0 || previous.Symbols.Any(s => !current.Symbols.Any(c => c.Id == s.Id && c.Equals(s))))
            throw Invalid("Addition refinement must preserve existing symbols and contain a new symbol.");
        return new(current, revision, LayoutRefinementReason.AddedSymbols, scope, userInstructions);
    }

    // Exact identities and endpoint sets, never reference names or geometry.
    // Include both sides of removed/replaced connections so a split or pin swap
    // cannot leave the former connection region outside the refinement scope.
    private static HashSet<Guid> ChangedConnections(Circuit? previous, Circuit current)
    {
        var affected = new HashSet<Guid>();
        if (previous is null) return affected;
        var oldNets = previous.Nets.ToDictionary(n => n.Id);
        var newNets = current.Nets.ToDictionary(n => n.Id);
        foreach (var id in oldNets.Keys.Union(newNets.Keys))
        {
            oldNets.TryGetValue(id, out var oldNet);
            newNets.TryGetValue(id, out var newNet);
            if (oldNet is not null && newNet is not null
                && oldNet.Pins.ToHashSet().SetEquals(newNet.Pins)) continue;
            foreach (var pin in oldNet?.Pins ?? []) affected.Add(pin.ComponentId);
            foreach (var pin in newNet?.Pins ?? []) affected.Add(pin.ComponentId);
        }
        return affected;
    }

    /// <summary>Validate the whole proposed placement batch before returning a
    /// new model. Never edits the caller's circuit, changes connectivity, drops
    /// locks, or expands the request's scope. A candidate is not accepted layout.</summary>
    public Circuit ApplyCandidate(Circuit current, DocumentRevision revision,
        IReadOnlyList<SymbolLayoutChange> changes)
    {
        Revision.RequireSame(revision);
        if (CircuitXml.Write(current) != baseline)
            throw new AutomationException("stale_revision", "Layout context changed; request refinement again.");
        var existing = current.Symbols.ToDictionary(s => s.Id);
        var proposed = new Dictionary<Guid, SymbolPlacement>();
        foreach (var change in changes)
        {
            if (!editable.Contains(change.SymbolId) || !proposed.TryAdd(change.SymbolId, change.Placement))
                throw Invalid("A candidate contains an out-of-scope or duplicate symbol.");
            change.Placement.Validate();
            var old = existing[change.SymbolId].Placement;
            if (old?.Locked == true && old != change.Placement)
                throw Invalid("A candidate cannot alter a locked symbol or remove its lock.");
            if (old?.Locked != true && change.Placement.Locked)
                throw Invalid("AI arrangement cannot silently create a user placement lock.");
        }
        Circuit result = current with
        {
            Symbols = current.Symbols.Select(s => proposed.TryGetValue(s.Id, out var placement)
                ? s with { Placement = placement } : s).ToArray()
        };
        if (result.Symbols.Any(s => editable.Contains(s.Id) && s.Placement is null))
            throw Invalid("The candidate leaves affected symbols without placement.");
        result.Validate();
        return result;
    }

    private static AutomationException Invalid(string message) => new("invalid_layout", message);
}
