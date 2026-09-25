using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Model;

/// <summary>A member created while one connection's members are refined into groups, pairs and signals (ledger
/// pf92d0ecdec8805b4). It is a new connection occurrence of the level with one implementation, one revision and its own
/// requirement history holding the three fields. <see cref="Members"/> names what it groups by connection identity: direct
/// members of the refined connection, which keep their exact saved revision and requirement history, or other members
/// created by the same save. Nothing is matched by name.</summary>
public sealed record NewConnectionMember(ConnectionSelection Selection, Guid RequirementRevisionId, string ImplementationName,
    string Name, DiagramConnectionKind Kind, ImmutableArray<DiagramEndpointBinding> Endpoints, DiagramRequirements Requirements,
    ImmutableArray<Guid> Members, DiagramDomain Domain = DiagramDomain.Unspecified,
    DiagramConnectionDirection Direction = DiagramConnectionDirection.Unspecified)
{
    [JsonIgnore] public ImmutableArray<Guid> MemberList => Members.IsDefault ? [] : Members;
}

/// <summary>A new member an agent asks for when it refines a connection's members (kicad_diagram_connection_members_refine).
/// ConnectionId is the fresh identity the agent chose for it, so other entries can name it. MemberIds lists, in order, what
/// it groups: current members of the refined connection or other new members. An omitted list reads as empty: a new
/// signal. General, Schematic and Routing are its own requirement fields.</summary>
public sealed record ConnectionMemberDefinition(Guid ConnectionId, string Name, ImmutableArray<Guid> MemberIds,
    DiagramConnectionKind Kind = DiagramConnectionKind.Signal, string General = "", string Schematic = "", string Routing = "")
{
    [JsonIgnore] public ImmutableArray<Guid> MemberIdList => MemberIds.IsDefault ? [] : MemberIds;
}

public enum ConnectionEndpointAction { Bind, Unbind }

/// <summary>The connection or member one agent edit targets, found by exact identity in the current saved design: the
/// root-to-level block path, the level's revision and connection archive, and the root-to-member connection path.</summary>
public sealed record ConnectionEditTarget(ImmutableArray<BlockSelection> BlockPath, RecursiveBlockRevision Level,
    DiagramConnectionArchive Archive, ImmutableArray<ConnectionSelection> ConnectionPath, DiagramConnectionRevision Connection);

/// <summary>How a port on a level's own boundary maps through the levels: the block this level details, the parent level
/// whose connections use the port from outside (none for the root), those connections by exact revision, and the level's
/// own realization record for the port when one is stated.</summary>
public sealed record ConnectionBoundaryMapping(Guid BlockId, Guid InterfaceId, string InterfaceName, BlockSelection? ParentLevel,
    ImmutableArray<ConnectionSelection> ParentConnections, InterfaceRealization? Realization);

public sealed partial class RecursiveBlockGraph
{
    /// <summary>Every end must name this level (a port on its own boundary) or one of its blocks, and a port that block has
    /// in the revision the level pins. The graph would refuse the rest later with a less specific error.</summary>
    private void CheckEndpointTargets(RecursiveBlockRevision level, IEnumerable<DiagramEndpointBinding> endpoints)
    {
        var available = level.Children.Select(Inspect).Append(level).ToDictionary(r => r.Selection.BlockId);
        var missing = new List<AutomationErrorDetail>();
        foreach (var endpoint in endpoints)
        {
            if (endpoint is null) continue;
            if (!available.TryGetValue(endpoint.BlockId, out var target))
                missing.Add(new("endpoint", level.Selection.BlockId, endpoint.BlockId,
                    $"Block {endpoint.BlockId:D} is neither the level '{level.Name}' nor one of its blocks."));
            else if (endpoint.InterfaceId is { } port && !target.LocalDiagram.Interfaces.Any(i => i.Id == port))
                missing.Add(new("endpoint", level.Selection.BlockId, port, $"'{target.Name}' has no port {port:D} in the revision this level pins."));
        }
        if (missing.Count != 0)
            throw new AutomationException("connection_edit_target_missing",
                "A connection end names a block or port that this level does not have. Nothing was changed.", missing);
    }

    /// <summary>Adds the new members to the level's archive (contract erratum requested with ledger pf92d0ecdec8805b4): each
    /// gets fresh identities, one implementation, one revision with no parent and one requirement revision with its three
    /// fields; each groups direct members of the refined connection or other new members, each at most once across the
    /// draft's member list and every new member; every new member is reached from the draft's member list; a single signal
    /// groups nothing and a differential pair exactly two signals. The draft itself is committed by the caller.</summary>
    private DiagramConnectionArchive WithNewMembers(DiagramConnectionArchive archive, DiagramConnectionDraft draft,
        ImmutableArray<NewConnectionMember> newMembers, RequirementRevisionOrigin origin, IEnumerable<Guid> saveIds)
    {
        origin.Validate();
        var baseline = archive.Inspect(draft.Baseline);
        var used = RetainedIdentities(); used.UnionWith(saveIds);
        void Fresh(Guid id)
        {
            if (id == Guid.Empty || !used.Add(id))
                throw new AutomationException("identity_reused", "Every new member and its implementation, revision and requirement revision needs a fresh identity that is not used anywhere in this diagram.");
        }
        var created = new Dictionary<Guid, NewConnectionMember>();
        foreach (var member in newMembers)
        {
            if (member?.Selection is not { } selection || member.Requirements is null || member.Endpoints.IsDefault)
                throw Refinement("Declare each new member with its identities, caption, ends and three requirement fields.");
            RefinementText(member.Name, "A new member needs a caption.");
            RefinementText(member.ImplementationName, "A new member needs an implementation name.");
            if (!Enum.IsDefined(member.Kind) || !Enum.IsDefined(member.Domain) || !Enum.IsDefined(member.Direction))
                throw Refinement("A new member uses a supported type, domain and direction.");
            member.Requirements.Validate();
            Fresh(selection.ConnectionId); Fresh(selection.StateId); Fresh(selection.RevisionId); Fresh(member.RequirementRevisionId);
            created.Add(selection.ConnectionId, member);
        }
        var direct = baseline.Members.ToDictionary(m => m.ConnectionId);
        var claimed = new HashSet<Guid>();
        foreach (var member in draft.Members)
        {
            if (member is null || !claimed.Add(member.ConnectionId))
                throw Refinement("The refined connection lists each of its members once.");
            if (created.TryGetValue(member.ConnectionId, out var added) && added.Selection != member)
                throw Refinement("The refined connection names each new member by its exact new selection.");
        }
        foreach (var member in newMembers)
            foreach (var id in member.MemberList)
            {
                if (id == member.Selection.ConnectionId || !(direct.ContainsKey(id) || created.ContainsKey(id)))
                    throw Refinement($"New member '{member.Name}' can group only members of '{baseline.Name}' or other new members of this refinement.");
                if (!claimed.Add(id))
                    throw Refinement($"New member '{member.Name}' names a member that is already placed elsewhere; each member belongs to one place.");
            }
        var reached = new HashSet<Guid>();
        var pending = new Stack<Guid>(draft.Members.Select(m => m.ConnectionId).Where(created.ContainsKey));
        while (pending.TryPop(out var id))
            if (reached.Add(id))
                foreach (var inner in created[id].MemberList.Where(created.ContainsKey)) pending.Push(inner);
        if (reached.Count != created.Count)
            throw Refinement($"Every new member belongs to '{baseline.Name}', directly or inside another new member, and no group contains itself.");
        // A new member's ids are direct members or new members (checked above); the draft's members are exact selections.
        DiagramConnectionKind KindOf(ConnectionSelection member) =>
            created.TryGetValue(member.ConnectionId, out var added) ? added.Kind : archive.Inspect(member).Kind;
        void Shape(string name, DiagramConnectionKind kind, IReadOnlyCollection<ConnectionSelection> members)
        {
            if (kind == DiagramConnectionKind.Signal && members.Count != 0)
                throw Refinement($"'{name}' is a single signal, which has no members of its own; give it another type first.");
            if (kind == DiagramConnectionKind.DifferentialPair && (members.Count != 2 || members.Any(m => KindOf(m) != DiagramConnectionKind.Signal)))
                throw Refinement($"'{name}' is a differential pair, which has exactly two single signals.");
        }
        ConnectionSelection Grouped(Guid id) => created.TryGetValue(id, out var added) ? added.Selection : direct[id];
        foreach (var member in newMembers) Shape(member.Name, member.Kind, member.MemberList.Select(Grouped).ToArray());
        Shape(draft.Name, draft.Kind, draft.Members);

        var states = archive.States.ToBuilder(); var revisions = archive.Revisions.ToBuilder(); var histories = archive.RequirementHistories.ToBuilder();
        foreach (var member in newMembers)
        {
            var selection = member.Selection;
            states.Add(new(selection.StateId, selection.ConnectionId, member.ImplementationName, selection.RevisionId));
            revisions.Add(new(selection, null, member.Name, member.Kind, member.Endpoints, member.RequirementRevisionId,
                [.. member.MemberList.Select(Grouped)], origin, member.Domain, member.Direction));
            histories.Add(new(new(DocumentId, selection.ConnectionId, selection.StateId), [new(member.RequirementRevisionId, null, member.Requirements, origin, [])]));
        }
        return new DiagramConnectionArchive(DocumentId, archive.OwnerBlockId, states, revisions, histories);
    }

    private static void RefinementText(string? text, string message)
    {
        if (string.IsNullOrWhiteSpace(text)) throw Refinement(message);
        try { System.Xml.XmlConvert.VerifyXmlChars(text); }
        catch (System.Xml.XmlException) { throw Refinement("Diagram text cannot contain characters that XML cannot preserve."); }
    }

    internal static AutomationException Refinement(string message) => new("invalid_connection_refinement", message);
}

/// <summary>What an agent's connection edits mean against the current saved design (ledger pf92d0ecdec8805b4): the exact
/// target, an end bound to a port of a block of the level or of the level's own boundary, an end left explicitly
/// unresolved, and a connection's members refined into groups, pairs and signals. Each check names the exact identity it
/// could not find (<c>connection_edit_target_missing</c>), refuses an edit that does not say exactly one thing
/// (<c>ambiguous_connection_edit</c>) or a target that is no longer current (the stale codes of a save), and builds the
/// draft a guarded connection save then writes. Nothing here reads or writes a file.</summary>
public static class RecursiveConnectionEdits
{
    /// <summary>Finds the connection or member at the exact paths in the saved design the caller observed. Every block on the
    /// block path and every connection on the connection path must be the revision its implementation's history ends with.</summary>
    public static ConnectionEditTarget Locate(RecursiveBlockGraph graph, BlockSelection expectedRoot, ImmutableArray<BlockSelection> blockPath,
        ImmutableArray<ConnectionSelection> connectionPath)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (expectedRoot is null || blockPath.IsDefaultOrEmpty || connectionPath.IsDefaultOrEmpty
            || blockPath.Any(p => p is null) || connectionPath.Any(p => p is null))
            throw Ambiguous("Name the saved root, the block path from the root to the level and the connection path from one of the level's connections, each by exact identity.");
        if (expectedRoot != graph.SelectedRoot)
            throw new AutomationException("stale_root_revision", "The selected design changed; read it again before editing a connection. Nothing was changed.");
        if (blockPath[0] != expectedRoot)
            throw Ambiguous("The block path starts at the saved root the edit expects.");
        var level = graph.Inspect(blockPath[0]);
        for (int i = 1; i < blockPath.Length; ++i)
        {
            if (!level.Children.Contains(blockPath[i]))
                throw level.Children.Any(c => c.BlockId == blockPath[i].BlockId)
                    ? new AutomationException("stale_block_revision", $"Block {blockPath[i].BlockId:D} has another revision in the selected design; read it again. Nothing was changed.")
                    : Missing($"Block {blockPath[i].BlockId:D} is not a block of '{level.Name}' in the selected design.");
            level = graph.Inspect(blockPath[i]);
        }
        foreach (var block in blockPath)
            if (graph.States.Single(s => s.Id == block.StateId).HeadRevisionId != block.RevisionId)
                throw new AutomationException(block == blockPath[^1] ? "stale_block_revision" : "stale_parent_revision",
                    $"Block {block.BlockId:D} has a newer saved revision than the selected design pins; compare it before editing. Nothing was changed.");
        if (level.LocalDiagram.Connections.IsEmpty)
            throw Missing($"'{level.Name}' has no connections.");
        var archive = graph.Connections(level.Selection.BlockId);
        var members = level.LocalDiagram.Connections; string owner = level.Name;
        DiagramConnectionRevision? connection = null;
        foreach (var step in connectionPath)
        {
            if (!members.Contains(step))
                throw members.Any(m => m.ConnectionId == step.ConnectionId)
                    ? new AutomationException("stale_connection_revision", $"Connection {step.ConnectionId:D} has another revision in '{owner}'; read it again. Nothing was changed.")
                    : Missing($"Connection {step.ConnectionId:D} is not {(connection is null ? "a connection of '" + owner + "'" : "a member of '" + owner + "'")}.");
            connection = archive.Inspect(step);
            if (archive.States.Single(s => s.Id == step.StateId).HeadRevisionId != step.RevisionId)
                throw new AutomationException(step == connectionPath[^1] ? "stale_connection_revision" : "stale_connection_parent",
                    $"'{connection.Name}' has a newer saved revision than the level pins; compare it before editing. Nothing was changed.");
            members = connection.Members; owner = connection.Name;
        }
        return new(blockPath, level, archive, connectionPath, connection!);
    }

    /// <summary>Binds one end to a port, or leaves it explicitly unresolved. Bind needs the block (one of the level's blocks, or
    /// the level itself for a port on its own boundary) and the port, which that block has in the revision the level pins; an
    /// end that states pins, candidates or a compatibility selector keeps them when it stays on its block, and is refused when
    /// it would move to another block. Unbind leaves the end Unresolved on its block (or the block named), without a port.
    /// Intent, when given, replaces what the end says; otherwise the end keeps it.</summary>
    public static DiagramConnectionDraft SetEndpoint(RecursiveBlockGraph graph, ConnectionEditTarget target, int index,
        ConnectionEndpointAction action, Guid? blockId, Guid? interfaceId, string? intent)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(target);
        var connection = target.Connection;
        if (index < 0 || index >= connection.Endpoints.Length)
            throw Missing($"'{connection.Name}' has ends 0 to {connection.Endpoints.Length - 1}; there is no end {index}.");
        if (!Enum.IsDefined(action)) throw Ambiguous("Choose bind or unbind.");
        var current = connection.Endpoints[index];
        DiagramEndpointBinding next;
        if (action == ConnectionEndpointAction.Bind)
        {
            if (blockId is null || interfaceId is null)
                throw Ambiguous("Binding names both the block (one of the level's blocks, or the level itself for a port on its boundary) and the port.");
            var block = LevelBlock(graph, target.Level, blockId.Value);
            if (!block.LocalDiagram.Interfaces.Any(i => i.Id == interfaceId))
                throw Missing($"'{block.Name}' has no port {interfaceId:D} in the revision this level pins.");
            bool detailed = current.Kind is DiagramEndpointKind.Compatible or DiagramEndpointKind.Candidates or DiagramEndpointKind.Pin;
            if (detailed && current.BlockId != blockId)
                throw Ambiguous("This end states pins of another block; unbind it before binding it to a port of a different block.");
            next = current with { Kind = detailed ? current.Kind : DiagramEndpointKind.Interface, BlockId = blockId.Value,
                InterfaceId = interfaceId, Intent = intent ?? current.Intent };
        }
        else
        {
            if (interfaceId is not null) throw Ambiguous("Unbinding leaves the end without a port; name no port.");
            var owner = blockId ?? current.BlockId;
            _ = LevelBlock(graph, target.Level, owner);
            next = DiagramEndpointBinding.Unknown(owner, intent ?? current.Intent);
        }
        next.Validate();
        var draft = target.Archive.StartDraft(connection.Selection);
        return draft with { Endpoints = draft.Endpoints.SetItem(index, next) };
    }

    /// <summary>Refines the target's members. <paramref name="memberIds"/> is its direct member list afterwards; each new member
    /// groups the ids it lists. Every current member and every new member appears exactly once across both (a refinement never
    /// drops a member; a member leaves through the level's removal cascade). New members run between the target's ends as they
    /// are drawn (block and port, no pins or intent) and get fresh implementation, revision and requirement revision
    /// identities from <paramref name="newId"/>.</summary>
    public static (DiagramConnectionDraft Draft, ImmutableArray<NewConnectionMember> NewMembers) RefineMembers(ConnectionEditTarget target,
        ImmutableArray<Guid> memberIds, ImmutableArray<ConnectionMemberDefinition> definitions, Func<Guid> newId, string implementationName)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(newId);
        if (memberIds.IsDefault || definitions.IsDefault || definitions.Any(d => d is null))
            throw Ambiguous("List the connection's members afterwards and each new member, even when a list is empty.");
        var connection = target.Connection;
        var existing = connection.Members.ToDictionary(m => m.ConnectionId);
        var defined = new Dictionary<Guid, ConnectionMemberDefinition>();
        foreach (var definition in definitions)
        {
            if (existing.ContainsKey(definition.ConnectionId))
                throw new AutomationException("identity_reused", $"New member '{definition.Name}' reuses the identity of a current member of '{connection.Name}'.");
            if (definition.ConnectionId == Guid.Empty || !defined.TryAdd(definition.ConnectionId, definition))
                throw Ambiguous("Give each new member its own fresh identity.");
        }
        string Name(Guid id) => existing.TryGetValue(id, out var member) ? target.Archive.Inspect(member).Name : defined[id].Name;
        var placed = new HashSet<Guid>();
        foreach (var id in memberIds.Concat(definitions.SelectMany(d => d.MemberIdList)))
        {
            if (!existing.ContainsKey(id) && !defined.ContainsKey(id))
                throw Missing($"{id:D} is neither a member of '{connection.Name}' nor a new member of this refinement.");
            if (!placed.Add(id))
                throw Ambiguous($"'{Name(id)}' is listed more than once; each member belongs to one place.");
        }
        var left = existing.Keys.Concat(defined.Keys).Where(id => !placed.Contains(id)).Select(Name).ToArray();
        if (left.Length != 0)
            throw Ambiguous($"{string.Join(", ", left.Select(n => "'" + n + "'"))} would be left out; a refinement keeps every member, and a member is removed through the level's removal cascade.");
        var ends = connection.Endpoints.Select(e => new DiagramEndpointBinding(e.InterfaceId is null ? DiagramEndpointKind.Unresolved : DiagramEndpointKind.Interface,
            e.BlockId, e.InterfaceId, "", null, [], null)).ToImmutableArray();
        var created = definitions.Select(d => new NewConnectionMember(new(d.ConnectionId, newId(), newId()), newId(), implementationName, d.Name, d.Kind,
            ends, new(d.General ?? "", d.Schematic ?? "", d.Routing ?? ""), d.MemberIdList)).ToImmutableArray();
        var selections = created.ToDictionary(m => m.Selection.ConnectionId, m => m.Selection);
        var draft = target.Archive.StartDraft(connection.Selection);
        return (draft with { Members = [.. memberIds.Select(id => existing.TryGetValue(id, out var member) ? member : selections[id])] }, created);
    }

    /// <summary>The block path and connection path that pin the same occurrences in <paramref name="graph"/>'s selected
    /// design, followed by identity (a save gives the level, its ancestors and the edited connection new revisions).</summary>
    public static (ImmutableArray<BlockSelection> BlockPath, ImmutableArray<ConnectionSelection> ConnectionPath) Follow(RecursiveBlockGraph graph,
        ImmutableArray<BlockSelection> blockPath, ImmutableArray<ConnectionSelection> connectionPath)
    {
        var blocks = ImmutableArray.CreateBuilder<BlockSelection>(); var at = graph.SelectedRoot; blocks.Add(at);
        foreach (var block in blockPath.Skip(1)) { at = graph.Inspect(at).Children.Single(c => c.BlockId == block.BlockId); blocks.Add(at); }
        var archive = graph.Connections(at.BlockId);
        var links = ImmutableArray.CreateBuilder<ConnectionSelection>(); var members = graph.Inspect(at).LocalDiagram.Connections;
        foreach (var step in connectionPath)
        {
            var next = members.Single(m => m.ConnectionId == step.ConnectionId);
            links.Add(next); members = archive.Inspect(next).Members;
        }
        return (blocks.ToImmutable(), links.ToImmutable());
    }

    /// <summary>For an end on a port of the level's own boundary: that port, the parent level's connections that use it from
    /// outside, and the level's realization record for it. Null for an end on a block of the level or without a port.</summary>
    public static ConnectionBoundaryMapping? Boundary(RecursiveBlockGraph graph, ImmutableArray<BlockSelection> blockPath, DiagramEndpointBinding endpoint)
    {
        var level = graph.Inspect(blockPath[^1]);
        if (endpoint.BlockId != level.Selection.BlockId || endpoint.InterfaceId is not { } port) return null;
        var boundary = level.LocalDiagram.Interfaces.Single(i => i.Id == port);
        ImmutableArray<ConnectionSelection> outside = [];
        BlockSelection? parent = blockPath.Length > 1 ? blockPath[^2] : null;
        if (parent is not null && graph.Inspect(parent) is { LocalDiagram.Connections.IsEmpty: false } above)
        {
            var archive = graph.Connections(parent.BlockId);
            outside = [.. archive.Walk(above.LocalDiagram.Connections).Where(c => archive.Inspect(c).Endpoints
                .Any(e => e.BlockId == level.Selection.BlockId && e.InterfaceId == port))];
        }
        return new(level.Selection.BlockId, port, boundary.Name, parent, outside, level.LocalDiagram.Realizations.SingleOrDefault(r => r.InterfaceId == port));
    }

    private static RecursiveBlockRevision LevelBlock(RecursiveBlockGraph graph, RecursiveBlockRevision level, Guid blockId) =>
        blockId == level.Selection.BlockId ? level
        : level.Children.SingleOrDefault(c => c.BlockId == blockId) is { } child ? graph.Inspect(child)
        : throw Missing($"Block {blockId:D} is neither the level '{level.Name}' nor one of its blocks.");

    private static AutomationException Missing(string message) => new("connection_edit_target_missing", message + " Nothing was changed.");
    private static AutomationException Ambiguous(string message) => new("ambiguous_connection_edit", message + " Nothing was changed.");
}
