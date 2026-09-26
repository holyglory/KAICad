using System.Collections.Immutable;
using System.Text;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>A component the design holds that no block owns, and the block that owns the sheet it sits on.</summary>
public sealed record BlockOwnerAssignment(Guid ComponentId, string Reference, Guid SheetInstanceId, Guid BlockId, string BlockName,
    string Reason);

/// <summary>A component whose owning block cannot be decided from exact identities. The person binds it to the block they
/// choose (kicad_diagram_components_set); synchronization never picks one.</summary>
public sealed record BlockOwnerRequest(string Code, Guid ComponentId, string Reference, Guid SheetInstanceId,
    IReadOnlyList<Guid> CandidateBlockIds, string Reason);

/// <summary>Block ownership of one design's components in the selected root of a block graph. Detached components are
/// bound by a block but no longer in the design: their binding, the block's requirements and every connection endpoint
/// that names them are kept as they are, as detached realization targets, so an undo in KiCad finds them again.</summary>
public sealed record BlockOwnershipPlan(Guid DocumentId, BlockSelection SelectedRoot, Guid DesignId, Guid CircuitId,
    IReadOnlyList<BlockOwnerAssignment> Assignments, IReadOnlyList<BlockOwnerRequest> Requests,
    IReadOnlyList<Guid> DetachedComponents)
{
    public bool ResolutionRequired => Requests.Count != 0;
}

public sealed record BlockOwnershipResult(BlockOwnershipPlan Plan, string BlockGraphSha256, BlockSelection SelectedRoot,
    IReadOnlyList<BlockSelection> SavedBlocks)
{
    public bool Changed => SavedBlocks.Count != 0;
}

/// <summary>Native edits reach the owning block by exact identity (ledger p74ee7c1da24272d9). A component the design
/// holds and no block of the selected root owns (for example one placed in KiCad and adopted by synchronization) is
/// bound to the block of the sheet it sits on. Sheets are not blocks (psu-cpu-fixture-and-ownership.md §0 rule 3), so a
/// sheet names a block only when one block owns every other component that sits on it or draws a unit there. No name,
/// reference or position is used, and nothing is inferred from the block hierarchy: a sheet with no owned component, a
/// sheet whose components several blocks own (the request offers every block from the selected root down to each of
/// them) and a component two blocks claim are resolution requests the person answers. Bindings are written as new block
/// revisions through the block graph's own draft API, which updates the selected root snapshot and keeps every earlier
/// revision; an unchanged plan writes nothing.</summary>
public static class BlockOwnershipSynchronization
{
    public const string ResolutionRequired = "block_owner_resolution_required";
    public const string OwnerUnresolved = "block_owner_unresolved";
    public const string OwnerAmbiguous = "block_owner_ambiguous";

    public static BlockOwnershipPlan Plan(RecursiveBlockGraph graph, Guid designId, SchematicDesign design, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(design);
        if (designId == Guid.Empty) throw Error("invalid_block_design", "Identify the exact design whose components the blocks own.");
        token.ThrowIfCancellationRequested();
        var circuit = design.Engineering.Circuit;
        var closure = graph.Walk(graph.SelectedRoot);
        var parents = new Dictionary<Guid, Guid?> { [graph.SelectedRoot.BlockId] = null };
        var names = new Dictionary<Guid, string>();
        var owners = new Dictionary<Guid, List<Guid>>();
        foreach (var selection in closure)
        {
            var revision = graph.Inspect(selection);
            names[selection.BlockId] = revision.Name;
            foreach (var child in revision.Children) parents[child.BlockId] = selection.BlockId;
            foreach (var target in revision.EffectiveComponentBindings.Targets.Where(t => t.DesignId == designId))
            {
                if (target.CircuitId != circuit.Id)
                    throw Error("block_design_circuit_changed",
                        "The blocks bind another circuit of this design; reconcile the design identity before synchronizing ownership.");
                if (!owners.TryGetValue(target.ComponentId, out var list)) owners.Add(target.ComponentId, list = []);
                if (!list.Contains(selection.BlockId)) list.Add(selection.BlockId);
            }
        }
        var components = circuit.Components.ToDictionary(c => c.Id);
        var assignments = new List<BlockOwnerAssignment>();
        var requests = new List<BlockOwnerRequest>();
        foreach (var (component, blocks) in owners.Where(o => o.Value.Count > 1 && components.ContainsKey(o.Key)).OrderBy(o => o.Key))
            requests.Add(new(OwnerAmbiguous, component, components[component].Reference, components[component].SheetInstanceId,
                [.. blocks.Order()], "Several blocks of the selected design own this component; keep it in exactly one."));

        var order = closure.Select((s, i) => (s.BlockId, i)).GroupBy(p => p.BlockId).ToDictionary(g => g.Key, g => g.First().i);
        IEnumerable<Guid> Chain(Guid block)
        {
            for (Guid? current = block; current is { } id; current = parents[id]) yield return id;
        }
        foreach (var component in circuit.Components.Where(c => !owners.ContainsKey(c.Id)).OrderBy(c => c.Id))
        {
            token.ThrowIfCancellationRequested();
            Guid sheet = component.SheetInstanceId;
            // Every other component that sits on this sheet or draws a unit there, and the blocks that own it.
            var owning = circuit.Components.Where(c => c.Id != component.Id && owners.ContainsKey(c.Id)
                    && (c.SheetInstanceId == sheet || circuit.Symbols.Any(s => s.ComponentId == c.Id && s.EffectiveSheetInstanceId(c) == sheet)))
                .SelectMany(c => owners[c.Id]).Distinct().ToArray();
            if (owning.Length == 0)
            {
                requests.Add(new(OwnerUnresolved, component.Id, component.Reference, sheet, [],
                    "No block owns a component on this sheet, so the sheet names no block; bind the component to the block that owns it."));
                continue;
            }
            if (owning.Length == 1)
            {
                assignments.Add(new(component.Id, component.Reference, sheet, owning[0], names[owning[0]],
                    "Every other component on its sheet belongs to this block."));
                continue;
            }
            // Several blocks own what this sheet shows, so the sheet names no single block. Offer every block from the
            // selected root down to each of them; the person decides.
            requests.Add(new(OwnerUnresolved, component.Id, component.Reference, sheet,
                [.. owning.SelectMany(Chain).Distinct().OrderBy(b => order[b])],
                "Several blocks own the components on this sheet, so the sheet names no single block; bind the component to the block that owns it."));
        }
        var detached = owners.Keys.Where(id => !components.ContainsKey(id)).Order().ToArray();
        return new(graph.DocumentId, graph.SelectedRoot, designId, circuit.Id, assignments, requests, detached);
    }

    /// <summary>Bind the design's unowned components to the blocks <see cref="Plan"/> decides, as one new revision of each
    /// owning block, and return the plan with the file written. Nothing is written when the plan has no assignment. A plan
    /// with resolution requests still writes the decided assignments; the requests stay for the person to answer.</summary>
    public static async Task<BlockOwnershipResult> SynchronizeAsync(string blockGraphPath, Guid designId, SchematicDesign design,
        RequirementRevisionOrigin origin, string? expectedSha256 = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(origin);
        var (root, path, documentId) = await Locate(blockGraphPath, token);
        var snapshot = await RecursiveBlockFiles.ReadAsync(root, path, documentId, token);
        if (expectedSha256 is not null && snapshot.ContentSha256 != expectedSha256)
            throw Error("recursive_block_file_changed", "The block graph changed; plan block ownership again before applying it.");
        var plan = Plan(snapshot.Graph, designId, design, token);
        var saved = new List<BlockSelection>();
        foreach (var group in plan.Assignments.GroupBy(a => a.BlockId).OrderBy(g => Order(snapshot.Graph, g.Key)))
        {
            token.ThrowIfCancellationRequested();
            var graph = snapshot.Graph;
            var blockPath = PathTo(graph, group.Key);
            var draft = graph.StartDraft(blockPath[^1]);
            var targets = draft.EffectiveComponentBindings.Targets.AddRange(group.OrderBy(a => a.ComponentId)
                .Select(a => new ComponentRealization(designId, plan.CircuitId, a.ComponentId)));
            draft = draft with { ComponentBindings = new BlockComponentBindings(targets) };
            snapshot = await RecursiveBlockFiles.SaveDraftAsync(root, path, documentId, snapshot.ContentSha256, graph.SelectedRoot, blockPath,
                draft, Guid.NewGuid(), Guid.NewGuid(), [.. blockPath.Skip(1).Select(_ => Guid.NewGuid())], origin, token: token);
            saved.Add(snapshot.Graph.Walk(snapshot.Graph.SelectedRoot).Single(s => s.BlockId == group.Key));
        }
        return new(plan, snapshot.ContentSha256, snapshot.Graph.SelectedRoot, saved);
    }

    /// <summary>The plan for the block graph file as it is now, and the file's content hash.</summary>
    public static async Task<(BlockOwnershipPlan Plan, string BlockGraphSha256)> PlanAsync(string blockGraphPath, Guid designId,
        SchematicDesign design, CancellationToken token = default)
    {
        var (root, path, documentId) = await Locate(blockGraphPath, token);
        var snapshot = await RecursiveBlockFiles.ReadAsync(root, path, documentId, token);
        return (Plan(snapshot.Graph, designId, design, token), snapshot.ContentSha256);
    }

    /// <summary>The origin of block revisions written for edits made in KiCad.</summary>
    public static RequirementRevisionOrigin NativeOrigin(string summary) =>
        new(RequirementRevisionActor.Editor, "KiCad", DateTimeOffset.UtcNow, summary, [], []);

    private static async Task<(string Root, string Path, Guid DocumentId)> Locate(string blockGraphPath, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(blockGraphPath) || !System.IO.Path.IsPathFullyQualified(blockGraphPath))
            throw Error("invalid_block_graph_path", "Provide the absolute path of the block graph file.");
        string path = System.IO.Path.GetFullPath(blockGraphPath);
        string root = System.IO.Path.GetDirectoryName(path) ?? throw Error("invalid_block_graph_path", "The block graph file needs a directory.");
        string xml;
        try { xml = new UTF8Encoding(false, true).GetString(await File.ReadAllBytesAsync(path, token)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { throw Error("block_graph_unavailable", "The block graph file cannot be read: " + error.Message); }
        try { return (root, path, RecursiveBlockGraphXml.Read(xml).DocumentId); }
        catch (System.Xml.XmlException error) { throw Error("block_graph_unavailable", "The block graph file is not readable XML: " + error.Message); }
    }

    private static int Order(RecursiveBlockGraph graph, Guid block) =>
        graph.Walk(graph.SelectedRoot).Select((s, i) => (s, i)).Single(p => p.s.BlockId == block).i;

    private static ImmutableArray<BlockSelection> PathTo(RecursiveBlockGraph graph, Guid block)
    {
        var path = new List<BlockSelection>();
        bool Visit(BlockSelection selection)
        {
            path.Add(selection);
            if (selection.BlockId == block) return true;
            foreach (var child in graph.Inspect(selection).Children)
                if (Visit(child)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }
        if (!Visit(graph.SelectedRoot)) throw Error("block_owner_missing", "The owning block is no longer in the selected design.");
        return [.. path];
    }

    private static AutomationException Error(string code, string message) => new(code, message);
}
