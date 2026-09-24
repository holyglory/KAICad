using System.Collections.Immutable;
using System.Text.Json;

namespace KiCad.Automation.Model;

public enum LevelConflictKind { RequirementText, Name, Interface, ChildSet, Connection, Annotation, Definition, ComponentBindings,
    PhysicalAllocation, InterfaceRealization, StaleChild, SelectedImplementationChanged, OwnerRemoved, DanglingReference }

/// <summary>One part of a level draft that cannot be combined with the newer saved level without a
/// person's choice. RequirementText conflicts name the owner (the level, a child or a connection) and
/// the field, and can be resolved with <see cref="DiagramRequirementResolution"/>; the others keep the
/// draft until the user decides (contract rbg-v2 section 4.8).</summary>
public sealed record LevelConflict(LevelConflictKind Kind, Guid OwnerId, Guid? OwnerStateId, Guid? ObjectId, DiagramRequirementField? Field,
    string Baseline, string Draft, string Saved, string Message);

/// <summary>A layout element both sides moved differently; the draft's position was kept.</summary>
public sealed record PresentationOverride(string ElementKey, string SavedValueJson);

public sealed record RecursiveLevelMergeResult(RecursiveLevelDraft? Candidate, ImmutableArray<LevelConflict> Conflicts,
    ImmutableArray<PresentationOverride> Overrides, BlockSelection ExpectedRoot, ImmutableArray<BlockSelection> Path)
{
    public bool CanSave => Candidate is not null && Conflicts.IsEmpty;
}

/// <summary>Rebases a level draft onto the latest saved design after a stale save (contract rbg-v2
/// section 4.8). The level is found by its exact block identity in the selected design, never by name.
/// Independent changes compose: requirement text merges per field, whole records (caption, definition,
/// components, physical allocation, interfaces, notes, realizations) take the one side that changed,
/// children and connections compose by identity, and layout takes the draft's position with a notice
/// when both sides moved the same element. Anything else is reported and the draft is kept.</summary>
public sealed class RecursiveLevelMerge
{
    public RecursiveBlockGraph Latest { get; }
    public RecursiveLevelDraft OriginalDraft { get; }
    public BlockSelection ExpectedRoot { get; }
    public ImmutableArray<BlockSelection> Path { get; }
    public int BaseContextVersion { get; }
    public int SavedContextVersion { get; }
    public RequirementRevisionOrigin? SavedOrigin { get; }
    private readonly LevelConflict? _located;

    private RecursiveLevelMerge(RecursiveBlockGraph latest, RecursiveLevelDraft draft, ImmutableArray<BlockSelection> path, LevelConflict? located)
    {
        Latest = latest; OriginalDraft = draft; ExpectedRoot = latest.SelectedRoot; Path = path; _located = located;
        if (located is not null) return;
        var contexts = latest.History(draft.Scope.Baseline.StateId).Reverse().ToArray();
        BaseContextVersion = Array.FindIndex(contexts, r => r.Selection == draft.Scope.Baseline) + 1;
        SavedContextVersion = Array.FindIndex(contexts, r => r.Selection == path[^1]) + 1;
        SavedOrigin = latest.Inspect(path[^1]).Origin;
    }

    public static RecursiveLevelMerge Prepare(RecursiveBlockGraph latest, RecursiveLevelDraft draft)
    {
        if (latest is null || draft?.Scope?.Baseline is null || draft.ChildDrafts.IsDefault || draft.ConnectionDrafts.IsDefault
            || draft.NewChildren.IsDefault || draft.NewConnections.IsDefault)
            throw RecursiveBlockGraph.Level("Provide the latest saved design and the complete retained level draft.");
        var scope = draft.Scope.Baseline;
        _ = latest.Inspect(scope);
        if (draft.Scope.Requirements.Baseline != latest.Requirements(scope))
            throw new AutomationException("changed_draft_baseline", "The draft no longer identifies its exact saved requirement baseline.");
        var path = BlockProposalCompiler.FindPath(latest, scope.BlockId);
        if (path.IsDefaultOrEmpty)
            return new(latest, draft, [], new(LevelConflictKind.OwnerRemoved, scope.BlockId, scope.StateId, null, null, "", "", "",
                "This diagram level is no longer part of the selected design; the draft was kept."));
        var selected = path[^1];
        if (selected.StateId != scope.StateId)
            return new(latest, draft, path, new(LevelConflictKind.SelectedImplementationChanged, scope.BlockId, scope.StateId, null, null, "", "", "",
                "Another implementation of this block is now selected; the draft was not applied to it."));
        if (latest.States.Single(s => s.Id == selected.StateId).HeadRevisionId != selected.RevisionId)
            throw new AutomationException("unselected_block_candidate", "This implementation has a newer unselected saved revision; compare it before rebasing the draft.");
        return new(latest, draft, path, null);
    }

    public RecursiveLevelMergeResult Inspect(IEnumerable<DiagramRequirementResolution>? resolutions = null)
    {
        if (_located is not null) return new(null, [_located], [], ExpectedRoot, Path);
        var choices = resolutions?.ToImmutableArray() ?? [];
        if (choices.Any(c => c is null)) throw RecursiveBlockGraph.Level("A requirement resolution cannot be missing.");
        var conflicts = new List<LevelConflict>();
        var overrides = new List<PresentationOverride>();
        var draft = OriginalDraft;
        var graph = Latest;
        var baseRevision = graph.Inspect(draft.Scope.Baseline);
        var savedRevision = graph.Inspect(Path[^1]);
        Guid level = baseRevision.Selection.BlockId;
        var used = graph.RetainedIdentities();
        foreach (var child in draft.NewChildren)
            if (used.Contains(child.Selection.BlockId) || used.Contains(child.Selection.StateId) || used.Contains(child.Selection.RevisionId))
                throw new AutomationException("identity_reused", "A block drawn in this draft now shares an identity with the saved design; nothing was merged.");
        foreach (var link in draft.NewConnections)
            if (used.Contains(link.Selection.ConnectionId) || used.Contains(link.Selection.StateId) || used.Contains(link.Selection.RevisionId))
                throw new AutomationException("identity_reused", "A connection drawn in this draft now shares an identity with the saved design; nothing was merged.");

        // Requirement text of the level itself.
        var scopeText = Text(draft.Scope.Requirements, graph.Requirements(savedRevision.Selection), level, level, savedRevision.Selection.StateId, choices, conflicts);
        var scope = draft.Scope;
        string name = Whole(baseRevision.Name, scope.Name, savedRevision.Name, string.Equals, LevelConflictKind.Name, level, conflicts,
            baseRevision.Name, scope.Name, savedRevision.Name, "Both sides renamed this level differently.");
        var definition = Whole(baseRevision.Definition, scope.Definition, savedRevision.Definition,
            (a, b) => (a ?? BlockDefinition.Empty).SameContents(b ?? BlockDefinition.Empty), LevelConflictKind.Definition, level, conflicts,
            "", "", "", "Both sides changed this level's component definition differently.");
        var bindings = Whole(baseRevision.ComponentBindings, scope.ComponentBindings, savedRevision.ComponentBindings,
            (a, b) => (a ?? BlockComponentBindings.Empty).SameContents(b ?? BlockComponentBindings.Empty), LevelConflictKind.ComponentBindings, level, conflicts,
            "", "", "", "Both sides changed this level's component bindings differently.");
        var physical = Whole(baseRevision.PhysicalAllocation, scope.PhysicalAllocation, savedRevision.PhysicalAllocation,
            (a, b) => a is null ? b is null : b is not null && a.SameContents(b), LevelConflictKind.PhysicalAllocation, level, conflicts,
            "", "", "", "Both sides changed this level's physical allocation differently.");

        // Children and root connections compose by identity; later saved pins are kept.
        var children = Compose(baseRevision.Children, scope.Children, savedRevision.Children, s => s.BlockId, level, LevelConflictKind.ChildSet,
            conflicts, "Both sides changed the same child block differently.");
        var baseLocal = baseRevision.LocalDiagram; var draftLocal = scope.LocalDiagram; var savedLocal = savedRevision.LocalDiagram;
        var roots = Compose(baseLocal.Connections, draftLocal.Connections, savedLocal.Connections, s => s.ConnectionId, level, LevelConflictKind.Connection,
            conflicts, "Both sides changed the same connection differently.");
        var interfaces = Keyed(baseLocal.Interfaces, draftLocal.Interfaces, savedLocal.Interfaces, i => i.Id, (a, b) => a == b,
            LevelConflictKind.Interface, level, conflicts, "Both sides changed this boundary interface differently.");
        var notes = Keyed(baseLocal.Notes, draftLocal.Notes, savedLocal.Notes, n => n.Id, (a, b) => a.SameContents(b),
            LevelConflictKind.Annotation, level, conflicts, "Both sides changed this note differently.");
        var realizations = Keyed(baseLocal.Realizations, draftLocal.Realizations, savedLocal.Realizations, r => r.InterfaceId, (a, b) => a.SameContents(b),
            LevelConflictKind.InterfaceRealization, level, conflicts, "Both sides changed how this boundary interface is realized differently.");
        var presentation = Layout(baseLocal.Layout, draftLocal.Layout, savedLocal.Layout, overrides);

        // Child drafts: rebase text onto a newer saved child; other concurrent child changes are reported.
        var childDrafts = ImmutableArray.CreateBuilder<RecursiveBlockDraft>();
        foreach (var child in draft.ChildDrafts)
        {
            var pinned = children.FirstOrDefault(c => c.BlockId == child.Baseline.BlockId);
            var original = graph.Inspect(child.Baseline);
            bool edited = !SameChild(child, original, graph.Requirements(child.Baseline).Requirements);
            if (!edited) continue;
            if (pinned is null)
            {
                conflicts.Add(new(LevelConflictKind.StaleChild, child.Baseline.BlockId, child.Baseline.StateId, child.Baseline.BlockId, null, "", "", "",
                    $"'{child.Name}' was removed from the saved level while this draft changed it."));
                continue;
            }
            if (pinned == child.Baseline) { childDrafts.Add(child); continue; }
            if (pinned.StateId != child.Baseline.StateId || !SameChild(child, original, child.Requirements.Requirements, requirementsOnly: true))
            {
                conflicts.Add(new(LevelConflictKind.StaleChild, child.Baseline.BlockId, child.Baseline.StateId, child.Baseline.BlockId, null, "", "", "",
                    $"'{child.Name}' changed in the saved design while this draft changed more than its requirement text."));
                continue;
            }
            var text = Text(child.Requirements, graph.Requirements(pinned), level, child.Baseline.BlockId, pinned.StateId, choices, conflicts);
            if (text is not null) childDrafts.Add(graph.StartDraft(pinned) with { Requirements = text });
        }
        // Connection drafts: rebase text onto a newer saved connection; concurrent structural changes are reported.
        var connectionDrafts = ImmutableArray.CreateBuilder<DiagramConnectionDraft>();
        if (!draft.ConnectionDrafts.IsEmpty)
        {
            var archive = graph.Connections(level);
            foreach (var link in draft.ConnectionDrafts)
            {
                var original = archive.Inspect(link.Baseline);
                bool structural = !SameConnection(link, original);
                bool textual = link.Requirements.Requirements != archive.Requirements(link.Baseline).Requirements;
                if (!structural && !textual) continue;
                var pinned = roots.FirstOrDefault(c => c.ConnectionId == link.Baseline.ConnectionId);
                if (pinned is null)
                {
                    conflicts.Add(new(LevelConflictKind.Connection, link.Baseline.ConnectionId, link.Baseline.StateId, link.Baseline.ConnectionId, null, "", "", "",
                        $"'{link.Name}' was removed from the saved level while this draft changed it."));
                    continue;
                }
                if (pinned == link.Baseline) { connectionDrafts.Add(link); continue; }
                if (structural || pinned.StateId != link.Baseline.StateId)
                {
                    conflicts.Add(new(LevelConflictKind.Connection, link.Baseline.ConnectionId, link.Baseline.StateId, link.Baseline.ConnectionId, null, "", "", "",
                        $"'{link.Name}' changed in the saved design while this draft changed it too."));
                    continue;
                }
                var text = Text(link.Requirements, archive.Requirements(pinned), level, link.Baseline.ConnectionId, pinned.StateId, choices, conflicts);
                if (text is not null) connectionDrafts.Add(archive.StartDraft(pinned) with { Requirements = text });
            }
        }

        // Anything merged that points at an object the other side removed.
        var childIds = children.Select(c => c.BlockId).Append(level).ToHashSet();
        var interfaceOwners = new Dictionary<Guid, HashSet<Guid>> { [level] = interfaces.Select(i => i.Id).ToHashSet() };
        foreach (var child in children)
        {
            var added = draft.NewChildren.FirstOrDefault(c => c.Selection == child);
            var edited = childDrafts.FirstOrDefault(c => c.Baseline.BlockId == child.BlockId);
            interfaceOwners[child.BlockId] = (added?.Interfaces ?? edited?.LocalDiagram.Interfaces
                ?? (graph.Revisions.Any(r => r.Selection == child) ? graph.Inspect(child).LocalDiagram.Interfaces : [])).Select(i => i.Id).ToHashSet();
        }
        var connectionIds = new HashSet<Guid>();
        DiagramConnectionArchive? savedArchive = graph.ConnectionArchives.SingleOrDefault(a => a.OwnerBlockId == level);
        foreach (var root in roots)
        {
            if (draft.NewConnections.FirstOrDefault(c => c.Selection == root) is { } added)
            {
                connectionIds.Add(root.ConnectionId);
                foreach (var endpoint in added.Endpoints) Dangling(endpoint, root.ConnectionId);
                continue;
            }
            foreach (var member in savedArchive!.Walk([root]))
            {
                connectionIds.Add(member.ConnectionId);
                var endpoints = member == root && connectionDrafts.FirstOrDefault(c => c.Baseline.ConnectionId == root.ConnectionId) is { } edited
                    ? edited.Endpoints : savedArchive.Inspect(member).Endpoints;
                foreach (var endpoint in endpoints) Dangling(endpoint, member.ConnectionId);
            }
        }
        void Dangling(DiagramEndpointBinding endpoint, Guid connection)
        {
            if (!childIds.Contains(endpoint.BlockId) || endpoint.InterfaceId is { } port && !interfaceOwners[endpoint.BlockId].Contains(port))
                conflicts.Add(new(LevelConflictKind.DanglingReference, level, null, connection, null, "", "", "",
                    "A connection in the combined level ends on a block or interface that the other side removed."));
        }
        foreach (var note in notes)
            if (note.Target.UnresolvedReason is null && note.Target.TargetId is { } target
                && (note.Target.Kind == DiagramAnnotationTargetKind.Block && !childIds.Contains(target)
                    || note.Target.Kind == DiagramAnnotationTargetKind.Connection && !connectionIds.Contains(target)))
                conflicts.Add(new(LevelConflictKind.DanglingReference, level, null, note.Id, null, "", "", "",
                    "A note in the combined level points at a block or connection that the other side removed."));
        foreach (var record in realizations)
            foreach (var target in record.TargetList)
                if (target.Kind == InterfaceRealizationTargetKind.ChildInterface
                        && (!childIds.Contains(target.BlockId!.Value) || !interfaceOwners[target.BlockId.Value].Contains(target.InterfaceId!.Value))
                    || target.Kind == InterfaceRealizationTargetKind.LocalConnection && !connectionIds.Contains(target.ConnectionId!.Value))
                    conflicts.Add(new(LevelConflictKind.DanglingReference, level, null, record.InterfaceId, null, "", "", "",
                        "A realization in the combined level uses a block, interface or connection that the other side removed."));

        if (conflicts.Count != 0 || scopeText is null)
            return new(null, [.. conflicts], [.. overrides], ExpectedRoot, Path);
        BlockLocalDiagram? diagram = savedRevision.Diagram is null && draft.Scope.Diagram is null ? null
            : new BlockLocalDiagram(interfaces, roots, notes, presentation.IsEmpty ? null : presentation, realizations.IsEmpty ? default : realizations);
        var mergedScope = draft.Scope with
        {
            Baseline = savedRevision.Selection, Name = name, Children = children, Requirements = scopeText, Diagram = diagram,
            Definition = definition, ComponentBindings = bindings, PhysicalAllocation = physical
        };
        var candidate = new RecursiveLevelDraft(mergedScope, childDrafts.ToImmutable(), connectionDrafts.ToImmutable(),
            draft.NewChildren, draft.NewConnections);
        return new(candidate, [], [.. overrides], ExpectedRoot, Path);
    }

    /// <summary>Three-way requirement text. Returns the rebased draft, or null with conflicts added.</summary>
    private DiagramRequirementDraft? Text(DiagramRequirementDraft draft, DiagramRequirementSnapshot saved, Guid level, Guid owner, Guid state,
        ImmutableArray<DiagramRequirementResolution> choices, List<LevelConflict> conflicts)
    {
        var mine = choices.Where(c => c.Scope == saved.Scope).ToArray();
        var result = DiagramRequirementMerge.Prepare(draft.Baseline, draft.Requirements, saved).Inspect(mine);
        foreach (var conflict in result.Conflicts)
            conflicts.Add(new(LevelConflictKind.RequirementText, owner, state, owner == level ? null : owner, conflict.Field,
                conflict.Base, conflict.Draft, conflict.Saved, "Both sides changed this requirement text differently."));
        if (result.Candidate is null) return null;
        var history = Latest.RequirementHistories.SingleOrDefault(h => h.Scope == saved.Scope)
            ?? Latest.ConnectionArchives.SelectMany(a => a.RequirementHistories).Single(h => h.Scope == saved.Scope);
        // A resolution may choose saved text instead of a restored field; keep only true restorations.
        var restored = draft.RestoredFields.Where(r => history.Inspect(r.Value).Requirements.Get(r.Key) == result.Candidate.Get(r.Key)).ToImmutableDictionary();
        return new(saved, result.Candidate, restored);
    }

    private static T Whole<T>(T baseline, T draft, T saved, Func<T, T, bool> same, LevelConflictKind kind, Guid level, List<LevelConflict> conflicts,
        string baseText, string draftText, string savedText, string message)
    {
        if (same(draft, baseline)) return saved;
        if (same(saved, baseline) || same(draft, saved)) return draft;
        conflicts.Add(new(kind, level, null, null, null, baseText, draftText, savedText, message));
        return draft;
    }

    /// <summary>Children or root connections by identity: saved order, then the draft's additions.
    /// A removal or addition on one side composes; both sides changing one pin differently conflicts.</summary>
    private static ImmutableArray<T> Compose<T>(ImmutableArray<T> baseline, ImmutableArray<T> draft, ImmutableArray<T> saved, Func<T, Guid> key,
        Guid level, LevelConflictKind kind, List<LevelConflict> conflicts, string message) where T : class =>
        Keyed(baseline, draft, saved, key, (a, b) => a.Equals(b), kind, level, conflicts, message);

    private static ImmutableArray<T> Keyed<T>(ImmutableArray<T> baseline, ImmutableArray<T> draft, ImmutableArray<T> saved, Func<T, Guid> key,
        Func<T, T, bool> same, LevelConflictKind kind, Guid level, List<LevelConflict> conflicts, string message) where T : class
    {
        var b = baseline.ToDictionary(key); var d = draft.ToDictionary(key); var s = saved.ToDictionary(key);
        bool Same(T? left, T? right) => left is null ? right is null : right is not null && same(left, right);
        var result = ImmutableArray.CreateBuilder<T>();
        foreach (var id in saved.Select(key).Concat(draft.Select(key).Where(id => !s.ContainsKey(id)))
                     .Concat(baseline.Select(key).Where(id => !s.ContainsKey(id) && !d.ContainsKey(id))))
        {
            T? bv = b.GetValueOrDefault(id), dv = d.GetValueOrDefault(id), sv = s.GetValueOrDefault(id);
            T? chosen;
            if (Same(dv, bv)) chosen = sv;
            else if (Same(sv, bv) || Same(dv, sv)) chosen = dv;
            else { conflicts.Add(new(kind, level, null, id, null, "", "", "", message)); chosen = dv; }
            if (chosen is not null) result.Add(chosen);
        }
        return result.ToImmutable();
    }

    private static DiagramPresentationView Layout(DiagramPresentationView baseline, DiagramPresentationView draft, DiagramPresentationView saved,
        List<PresentationOverride> overrides)
    {
        T? Pick<T>(string key, T? b, T? d, T? s, Func<T, T, bool> same) where T : class
        {
            bool Same(T? left, T? right) => left is null ? right is null : right is not null && same(left, right);
            if (Same(d, b)) return s;
            if (Same(s, b) || Same(d, s)) return d;
            overrides.Add(new(key, s is null ? "null" : JsonSerializer.Serialize(s, JsonOptions)));
            return d;
        }
        var frame = Pick("frame", baseline.Frame, draft.Frame, saved.Frame, (a, c) => a == c);
        // Blocks keep z-order: the draft's order when it reordered common blocks, otherwise the saved order.
        var bb = baseline.BlockPlacements.ToDictionary(p => p.BlockId); var db = draft.BlockPlacements.ToDictionary(p => p.BlockId);
        var sb = saved.BlockPlacements.ToDictionary(p => p.BlockId);
        var draftCommon = draft.BlockPlacements.Select(p => p.BlockId).Where(bb.ContainsKey).ToList();
        var baseCommon = baseline.BlockPlacements.Select(p => p.BlockId).Where(db.ContainsKey).ToList();
        bool reordered = !draftCommon.SequenceEqual(baseCommon);
        var order = (reordered
                ? draft.BlockPlacements.Select(p => p.BlockId).Where(sb.ContainsKey).Concat(saved.BlockPlacements.Select(p => p.BlockId).Where(id => !db.ContainsKey(id)))
                : saved.BlockPlacements.Select(p => p.BlockId))
            .Concat(draft.BlockPlacements.Select(p => p.BlockId).Where(id => !sb.ContainsKey(id)))
            .Concat(baseline.BlockPlacements.Select(p => p.BlockId).Where(id => !sb.ContainsKey(id) && !db.ContainsKey(id))).Distinct();
        var blocks = ImmutableArray.CreateBuilder<DiagramBlockPlacement>();
        foreach (var id in order)
            if (Pick("block:" + id.ToString("D"), bb.GetValueOrDefault(id), db.GetValueOrDefault(id), sb.GetValueOrDefault(id), (a, c) => a == c) is { } chosen)
                blocks.Add(chosen);
        var ports = Entries(baseline.PortPlacements, draft.PortPlacements, saved.PortPlacements, DiagramPresentationView.Key, (a, c) => a == c);
        var routes = Entries(baseline.ConnectionRoutes, draft.ConnectionRoutes, saved.ConnectionRoutes, DiagramPresentationView.Key, (a, c) => a.SameContents(c));
        return new DiagramPresentationView(blocks.ToImmutable(), ports, routes, frame).Canonical();

        ImmutableArray<T> Entries<T>(ImmutableArray<T> b, ImmutableArray<T> d, ImmutableArray<T> s, Func<T, string> key, Func<T, T, bool> same) where T : class
        {
            var bi = b.ToDictionary(key); var di = d.ToDictionary(key); var si = s.ToDictionary(key);
            var result = ImmutableArray.CreateBuilder<T>();
            foreach (var id in si.Keys.Concat(di.Keys).Concat(bi.Keys).Distinct())
                if (Pick(id, bi.GetValueOrDefault(id), di.GetValueOrDefault(id), si.GetValueOrDefault(id), same) is { } chosen) result.Add(chosen);
            return result.ToImmutable();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static bool SameChild(RecursiveBlockDraft child, RecursiveBlockRevision saved, DiagramRequirements requirements, bool requirementsOnly = false) =>
        child.Name == saved.Name && child.LocalDiagram.Interfaces.SequenceEqual(saved.LocalDiagram.Interfaces)
        && child.EffectiveDefinition.SameContents(saved.EffectiveDefinition) && child.EffectiveComponentBindings.SameContents(saved.EffectiveComponentBindings)
        && (child.PhysicalAllocation is null ? saved.PhysicalAllocation is null : saved.PhysicalAllocation is not null && child.PhysicalAllocation.SameContents(saved.PhysicalAllocation))
        && (requirementsOnly || child.Requirements.Requirements == requirements);

    private static bool SameConnection(DiagramConnectionDraft draft, DiagramConnectionRevision saved) =>
        draft.Name == saved.Name && draft.Kind == saved.Kind && draft.Members.SequenceEqual(saved.Members)
        && draft.Endpoints.Length == saved.Endpoints.Length && draft.Endpoints.Zip(saved.Endpoints).All(p => p.First.SameDefinition(p.Second))
        && draft.Domain == saved.Domain && draft.Direction == saved.Direction && InterconnectRealization.Same(draft.Realization, saved.Realization);
}
