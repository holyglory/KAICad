using System.ComponentModel;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol.Diagrams;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class RecursiveEditorTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_diagram_proposal_publish"),
     Description("Publish a complete typed block implementation proposal: nested blocks, connection groups/members and partial endpoints, all three requirement fields, definitions and unresolved issues. Requires an existing original input, current source hash and process epoch. Retains the request in local state before publication. Creates independent alternatives without changing any existing head or selected root. Invalid closures reject as a whole. Stale requests stay retrievable; an identical already-present candidate is an observation, not a receipt resolving an earlier ambiguous file write. Repeating the same proposal and operation identity after an uncertain or cancelled call returns the recorded candidate (added=false) and, when this operation wrote it, its publication receipt; it never creates a second candidate. The result compares the candidate with today's design (comparison: stale, currentChanges since the proposal's base revision, proposalChanges, changedOnBothSides; see kicad_diagram_proposal_compare). A proposal sent with an outdated source token is refused (block_proposal_source_changed) with the same comparison against today's file, and nothing is written. This does not generate or activate native electrical designs.")]
    public Task<CallToolResult> PublishProposal(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, string expectedSourceToken, JsonElement proposalJson, Guid operationId, CancellationToken cancellationToken) => Execute(async () =>
    {
        BlockProposal proposal;
        try
        {
            proposal = JsonSerializer.Deserialize<BlockProposal>(proposalJson.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new AutomationException("invalid_block_proposal", "The proposal JSON is empty.");
            proposal = BlockProposalFiles.Normalize(proposal);
        }
        catch (Exception error) when (error is JsonException || Malformed(error))
        { throw new AutomationException("invalid_block_proposal", error.Message); }
        if (operationId == Guid.Empty) throw new AutomationException("invalid_operation_id", "A proposal publication needs an explicit operation identity.");
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        BlockProposalFileResult result;
        try
        {
            result = await BlockProposalFiles.PublishAsync(repositoryRoot, sourcePath, Identity(documentId), expectedSourceToken, proposal,
                registry.StateDirectory, cancellationToken, operationId);
        }
        catch (AutomationException error) when (error.Code == "block_proposal_source_changed")
        {
            // The agent built this proposal on a file that has changed since: nothing is written, and the refusal says exactly
            // what changed on each side against today's file (ledger pa48933d0fe0a5c2f).
            return await StaleRefusal(error, instanceId, session.Epoch, repositoryRoot, sourcePath, documentId,
                graph => BlockProposalComparer.Request(graph, proposal), cancellationToken);
        }
        catch (Exception error) when (Malformed(error))
        { throw new AutomationException("invalid_block_proposal", error.Message); }
        var publication = new BlockProposalReceipts(registry.StateDirectory).Read(operationId);
        // The candidate is saved at this point; a comparison that cannot be made is reported, never turned into a failed publication.
        var (comparison, unavailable) = Compare(() => BlockProposalComparer.Published(result.Snapshot.Graph, proposal.Id));
        return Data(new { instanceId, instanceEpoch = session.Epoch, documentId, sourceToken = result.Snapshot.ContentSha256,
            selectedRoot = result.Snapshot.Graph.SelectedRoot, result.Added, result.ContextStillSelected, proposal = result.Proposal,
            publication = publication is { ProposalId: var published } && published == proposal.Id ? Receipt(publication) : null,
            comparison, comparisonUnavailable = unavailable },
            result.Snapshot.UpgradedFromSchemaVersion);
    });

    [McpServerTool(Name = "kicad_diagram_proposal_read", ReadOnly = true),
     Description("Read a published proposal's exact candidate revision, original input context, unresolved issues and complete selected block/connection closure. Reading does not activate a candidate or move the native editor. Native views may be requested for the candidate after the editor has reloaded the saved file. Use the retained-request tool if publication failed before this candidate was saved.")]
    public Task<CallToolResult> ReadProposal(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        string expectedSourceToken, Guid proposalId, CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed.");
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, Identity(documentId), cancellationToken);
        if (loaded.ContentSha256 != expectedSourceToken) throw new AutomationException("block_proposal_source_changed", "Read the current source token before inspecting a proposal.");
        var proposal = loaded.Graph.Proposal(proposalId);
        var blocks = loaded.Graph.Walk(proposal.Candidate).Select(loaded.Graph.Inspect).ToArray();
        return Data(new { instanceId, instanceEpoch = session.Epoch, documentId, sourceToken = loaded.ContentSha256,
            selectedRoot = loaded.Graph.SelectedRoot, proposal,
            blocks = blocks.Select(b => new { block = b, requirements = loaded.Graph.Requirements(b.Selection).Requirements,
                connections = b.LocalDiagram.Connections.IsEmpty ? [] : loaded.Graph.Connections(b.Selection.BlockId).Walk(b.LocalDiagram.Connections)
                    .Select(s => new { connection = loaded.Graph.Connections(b.Selection.BlockId).Inspect(s),
                        requirements = loaded.Graph.Connections(b.Selection.BlockId).Requirements(s).Requirements }).ToArray() }) });
    });

    [McpServerTool(Name = "kicad_diagram_proposal_publication", ReadOnly = true),
     Description("Read the durable publication phase for one proposal operation. The result distinguishes prepared, replacing and published XML evidence; it does not retry, select the candidate or claim native activation. A replacing receipt remains an explicit recovery requirement.")]
    public Task<CallToolResult> ProposalPublication(string instanceId, string expectedInstanceEpoch, Guid operationId,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var receipt = new BlockProposalReceipts(registry.StateDirectory).Read(operationId)
            ?? throw new AutomationException("missing_block_proposal_receipt", "No durable proposal publication receipt has this operation identity.");
        return Data(new { instanceId, instanceEpoch = session.Epoch, receipt });
    });

    [McpServerTool(Name = "kicad_diagram_proposal_publication_resume"),
     Description("Inspect and explicitly resume an interrupted proposal publication or selection from its durable receipt. Requires the exact diagram/document, operation and native epoch. It completes only when the receipt preimage/postimage proves safety; competing XML or missing candidate files return NeedsReview without overwriting either version. It does not activate native schematic/PCB objects.")]
    public Task<CallToolResult> ResumeProposalPublication(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, Guid operationId, CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var result = await BlockProposalRecovery.ResumeAsync(repositoryRoot, sourcePath, Identity(documentId), operationId,
            registry.StateDirectory, cancellationToken);
        return Data(new { instanceId, instanceEpoch = session.Epoch, documentId, operationId, recovery = result }, result.UpgradedFromSchemaVersion);
    });

    [McpServerTool(Name = "kicad_diagram_proposal_retained", ReadOnly = true),
     Description("Retrieve the exact locally retained typed proposal request after a failed or stale publication. Requires its original diagram path/document and proposal ID. This does not publish, select, mutate or launch an agent, and request retention is not evidence of completed publication.")]
    public Task<CallToolResult> RetainedProposal(string instanceId, string sourcePath, string documentId, Guid proposalId,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed.");
        var request = BlockProposalFiles.ReadRetained(registry.StateDirectory, proposalId);
        if (request.DesignPath != sourcePath || request.DocumentId != Identity(documentId))
            throw new AutomationException("block_proposal_conflict", "The retained request belongs to a different target.");
        return Data(new { instanceId, instanceEpoch = session.Epoch, request });
    });

    [McpServerTool(Name = "kicad_diagram_proposal_select"),
     Description("Choose a published proposal for the exact current target block, updating its containing root snapshots together while preserving unrelated siblings. Requires the current source hash, process epoch and complete current root-to-block path. A proposal whose target changed after the revision it was built on, left the design or was already chosen is refused (proposal_target_changed), as is an outdated token (block_proposal_source_changed), an outdated expected root or a path starting at another root revision (stale_root_revision), a path naming a containing block revision the design no longer pins (stale_block_revision) and a containing implementation with a newer saved revision (stale_parent_revision): nothing is written, and the refusal carries the comparison of kicad_diagram_proposal_compare, naming every element changed on today's side and in the proposal and those changed on both, and today's path to the target. This changes the conceptual diagram selection only, not native schematic/PCB activation. Supply fresh ancestor revision IDs and an operation ID. Repeating an operation ID whose selection completed (for example after a cancelled or uncertain call, or from a reattached server) returns that recorded outcome (recorded=true, the source token and root it produced, and whether the file still has them) and never chooses again.")]
    public Task<CallToolResult> SelectProposal(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, string expectedSourceToken, Guid proposalId, BlockSelection expectedRoot,
        BlockSelection[] currentPath, Guid[] ancestorRevisionIds, Guid operationId, string actor, CancellationToken cancellationToken) => Execute(async () =>
    {
        var path = currentPath?.ToImmutableArray() ?? []; var ancestors = ancestorRevisionIds?.ToImmutableArray() ?? [];
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        if (operationId == Guid.Empty) throw new AutomationException("invalid_operation_id", "Identify this conceptual selection operation.");
        // A retried operation returns the outcome it recorded instead of choosing again (ledger pa48933d0fe0a5c2f).
        if (new BlockProposalReceipts(registry.StateDirectory).Read(operationId) is { Stage: BlockProposalOperationStage.Published,
                Kind: BlockProposalOperationKind.Select } receipt && receipt.ProposalId == proposalId && receipt.DocumentId is { } recordedDocument
            && recordedDocument == Identity(documentId))
        {
            var now = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, recordedDocument, cancellationToken);
            if (receipt.DesignPath == now.Path)
                return Data(new { instanceId, instanceEpoch = session.Epoch, documentId, operationId, recorded = true, sourceToken = receipt.AfterSha256,
                    selectedRoot = receipt.CandidateXml is { } chosen ? RecursiveBlockGraphXml.Read(chosen).SelectedRoot : null, proposalId,
                    currentSourceToken = now.ContentSha256, stillCurrent = now.ContentSha256 == receipt.AfterSha256, publication = Receipt(receipt) });
        }
        var origin = new RequirementRevisionOrigin(RequirementRevisionActor.Agent, actor, DateTimeOffset.UtcNow, "Choose proposed implementation", [], [operationId]);
        RecursiveBlockFileSnapshot result;
        try
        {
            result = await BlockProposalFiles.SelectAsync(repositoryRoot, sourcePath, Identity(documentId), proposalId, expectedSourceToken,
                expectedRoot, path, ancestors, origin, cancellationToken, registry.StateDirectory, operationId);
        }
        catch (AutomationException error) when (error.Code is "proposal_target_changed" or "block_proposal_source_changed"
            or "stale_root_revision" or "stale_parent_revision" or "stale_block_revision")
        {
            return await StaleRefusal(error, instanceId, session.Epoch, repositoryRoot, sourcePath, documentId,
                graph => BlockProposalComparer.Published(graph, proposalId), cancellationToken);
        }
        return Data(new { instanceId, instanceEpoch = session.Epoch, documentId, operationId, recorded = false, sourceToken = result.ContentSha256,
            selectedRoot = result.Graph.SelectedRoot, proposalId }, result.UpgradedFromSchemaVersion);
    });

    [McpServerTool(Name = "kicad_diagram_proposal_compare", ReadOnly = true),
     KiCadCapability("structural-diagram", "compiled-mcp", "source token, proposal identity"),
     Description("Compare a proposal with today's saved design before choosing it. The proposal is either published in the diagram or a request this server retained after a refused publication (kicad_diagram_proposal_retained). Its base is the target revision its original input captured. Returns stale (the proposal is not chosen and today's target is no longer that revision, so choosing it would be refused), candidateAdopted (the proposal was chosen: today's target is its candidate or descends from it, as a later revision of the candidate's implementation or an implementation made from one), candidateSelected (today's target is exactly the candidate), the target's current root-to-block path, and three lists by exact identity: proposalChanges (what the proposal changes from the base), currentChanges (what changed from the base to today's revision; for an adopted proposal only what changed after the candidate) and changedOnBothSides (always empty for an adopted proposal, whose changes are part of today's design). Each change names its level (levelPath: block ids from the target down; for a target that left the design, today's root-to-block path of the deepest block of its base path still in the design), the connection and members containing it (connectionPath), its category (Name, Requirement, Block, Connection, Interface, Comment, Definition, PhysicalAllocation, Layout, InterfaceRealization, InterconnectRealization), kind (Added, Removed, Changed, Reordered), the element's id and name, the requirement field or aspect, and for a child block or connection its revision before and after. Nothing is merged, chosen or written.")]
    public Task<CallToolResult> CompareProposal(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        string expectedSourceToken, Guid proposalId, CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        Guid id = Identity(documentId);
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (string.IsNullOrEmpty(expectedSourceToken) || loaded.ContentSha256 != expectedSourceToken)
            throw new AutomationException("recursive_block_file_changed", "Read the current file token before comparing a proposal with it.");
        BlockProposalComparison comparison;
        if (loaded.Graph.Proposals.Any(p => p.Id == proposalId)) comparison = BlockProposalComparer.Published(loaded.Graph, proposalId);
        else
        {
            var retained = BlockProposalFiles.TryReadRetained(registry.StateDirectory, proposalId)
                ?? throw new AutomationException("unknown_block_proposal", "No published or retained proposal has this identity.");
            if (retained.DesignPath != loaded.Path || retained.DocumentId != id)
                throw new AutomationException("block_proposal_conflict", "The retained request belongs to a different diagram.");
            // Preparing the request against today's graph refuses an invalid one with a code, as its publication would.
            try { comparison = BlockProposalComparer.Request(loaded.Graph, retained.Proposal); }
            catch (Exception error) when (Malformed(error)) { throw new AutomationException("invalid_block_proposal", error.Message); }
        }
        return Data(new { instanceId, instanceEpoch = session.Epoch, documentId, sourceToken = loaded.ContentSha256,
            selectedRoot = loaded.Graph.SelectedRoot, comparison });
    });

    [McpServerTool(Name = "kicad_diagram_agent_context", ReadOnly = true),
     KiCadCapability("structural-diagram", "compiled-mcp", "source token, exact root-to-level path or original input"),
     Description("Give an agent the revision-bound context of one saved diagram level, in a form no agent product or provider owns. Name the level by an original input (inputId, recorded with kicad_diagram_refinement_input_record: the level it captured, or a deeper level given by blockPath inside that input's revisions) or by blockPath alone: the exact root-to-level path, from a revision of the root block, each block pinned by the revision before it; historical revisions are allowed. The context holds the path, the level's block and its direct children (exact block, implementation and revision, name, General/Schematic/Routing text with its requirement revision, boundary ports, definition, component and physical choices, and how many children and connections lie below), every connection and member of the level (exact revision, kind, domain, direction, ends, members, the three fields, realization), the level's comments marked Element (on a block or connection) or FreeSpace (on the canvas, with any original sketch strokes), its interface realizations and saved layout, and, with an input, the original prompt, its author and captured file token, its scope and focus connections, and its attachment references (preserved asset path, SHA-256, byte count, media type, source). contextSha256 fingerprints exactly these contents, so the same revisions give the same context and fingerprint after later edits. Outside the context: today's source token and selected root, whether the level is on today's selected design (current, currentPath), today's name of each implementation the context names (implementations; a rename changes them, not the context) and the present integrity of each attachment (assets). Reads only; it never starts an agent, chooses anything or touches the editor.")]
    public Task<CallToolResult> AgentContext(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        string expectedSourceToken, CancellationToken cancellationToken, Guid? inputId = null, BlockSelection[]? blockPath = null) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        if (inputId is null && blockPath is null)
            throw new AutomationException("ambiguous_agent_context", "Name the level by an original input (inputId), a root-to-level block path (blockPath), or both.");
        Guid id = Identity(documentId);
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (string.IsNullOrEmpty(expectedSourceToken) || loaded.ContentSha256 != expectedSourceToken)
            throw new AutomationException("recursive_block_file_changed", "Read the current file token before asking for a context.");
        var input = inputId is { } recorded ? loaded.Graph.RefinementInput(recorded) : null;
        ImmutableArray<BlockSelection> path = blockPath is null ? input!.BlockPath : [.. blockPath];
        var context = RefinementContexts.Build(loaded.Graph, path, input);
        var assets = new List<RefinementAssetObservation>();
        foreach (var attachment in input?.Attachments ?? [])
            assets.Add(await RefinementAssetFiles.InspectAsync(repositoryRoot, attachment, cancellationToken));
        var verified = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (verified.ContentSha256 != loaded.ContentSha256)
            throw new AutomationException("recursive_block_file_changed", "The diagram changed while its context was read; read a fresh token.");
        var currentPath = BlockProposalCompiler.FindPath(loaded.Graph, path[^1].BlockId);
        return Data(new { instanceId, instanceEpoch = session.Epoch, documentId, sourceToken = loaded.ContentSha256,
            storedSchemaVersion = loaded.StoredSchemaVersion, selectedRoot = loaded.Graph.SelectedRoot,
            current = currentPath.SequenceEqual(path), currentPath, contextSha256 = context.Fingerprint(), context,
            implementations = RefinementContexts.Implementations(loaded.Graph, context), assets });
    });

    private static object Receipt(BlockProposalPublicationReceipt receipt) => new { receipt.OperationId, receipt.Kind, receipt.Stage,
        receipt.BeforeSha256, receipt.AfterSha256, receipt.ConfirmedAt };

    /// <summary>A refused publication or choice of a proposal built on an older revision: nothing was written, and the refusal
    /// carries the comparison with today's file, one detail per changed element, besides its code and message.</summary>
    private static async Task<CallToolResult> StaleRefusal(AutomationException refusal, string instanceId, string epoch, string repositoryRoot,
        string sourcePath, string documentId, Func<RecursiveBlockGraph, BlockProposalComparison> compare, CancellationToken cancellationToken)
    {
        RecursiveBlockFileSnapshot today;
        try { today = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, Identity(documentId), cancellationToken); }
        catch (Exception error) when (error is AutomationException or IOException or UnauthorizedAccessException) { throw refusal; }
        var (comparison, unavailable) = Compare(() => compare(today.Graph));
        var details = refusal.Details.Concat(comparison?.Details() ?? []).Select(d => new { kind = d.Kind, scopeBlockId = d.ScopeBlockId,
            objectId = d.ObjectId, message = d.Message });
        var data = JsonSerializer.SerializeToElement(new { code = refusal.Code, message = refusal.Message, details, instanceId, instanceEpoch = epoch,
            documentId, sourceToken = today.ContentSha256, selectedRoot = today.Graph.SelectedRoot, comparison, comparisonUnavailable = unavailable }, Web);
        return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }

    /// <summary>A comparison that cannot be made is reported beside the result or refusal it belongs to, with the code the
    /// preparation gave it (invalid_block_proposal for a malformed proposal, as publication reports it). It never replaces a
    /// refusal's own code or turns a saved publication into a failed call.</summary>
    private static (BlockProposalComparison? Comparison, object? Unavailable) Compare(Func<BlockProposalComparison> compare)
    {
        try { return (compare(), null); }
        catch (AutomationException error) { return (null, new { code = error.Code, message = error.Message }); }
        catch (Exception error) when (Malformed(error)) { return (null, new { code = "invalid_block_proposal", message = error.Message }); }
    }

    /// <summary>The failures a malformed proposal can raise while it is normalized or prepared, besides coded refusals.</summary>
    private static bool Malformed(Exception error) =>
        error is InvalidOperationException or ArgumentException or KeyNotFoundException or NullReferenceException;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static CallToolResult Data(object value, int upgradedFromSchemaVersion = 0)
    {
        var data = Upgraded(JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }), upgradedFromSchemaVersion);
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }

    /// <summary>Reports a silent version 1 to version 2 file upgrade made by this write (contract rbg-v2 section 8).</summary>
    private static JsonElement Upgraded(JsonElement data, int upgradedFromSchemaVersion)
    {
        if (upgradedFromSchemaVersion == 0) return data;
        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        node["upgradedFromSchemaVersion"] = upgradedFromSchemaVersion;
        return JsonSerializer.SerializeToElement(node);
    }

    [McpServerTool(Name = "kicad_diagram_refinement_publication"),
     Description("Inspect or explicitly resume an original-input publication using its durable receipt. Supply the exact native process epoch, diagram path/document and input ID. Inspection never writes. Resume completes only a verified preimage/postimage transition, preserves the original staged/retained files, and otherwise returns NeedsReview without replacing newer XML. CompletedPreviously reports historical success, not current XML equivalence. Does not start an agent or mutate native electrical designs.")]
    public Task<CallToolResult> RefinementPublication(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, Guid inputId, CancellationToken cancellationToken, bool resume = false) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var inspection = resume ? await RefinementInputRecovery.ResumeAsync(repositoryRoot, sourcePath, Identity(documentId), inputId,
            registry.StateDirectory, cancellationToken) : await RefinementInputRecovery.InspectAsync(repositoryRoot, sourcePath, Identity(documentId), inputId,
            registry.StateDirectory, cancellationToken);
        // A completed resume reports the version 1 to 2 upgrade its retained preimage proves (contract rbg-v2 section 8).
        var data = Upgraded(JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId, inputId, inspection },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }),
            inspection.UpgradedFromSchemaVersion);
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_refinement_asset_capture"),
     Description("Preserve original prompt/file/graphic bytes inside a repository-relative archive directory, keyed by their observed SHA256. Requires an exact diagram checkpoint and native instance epoch, explicit source path/hash/byte count and attachment identity. Returns actual verified attachment metadata; does not execute the file, extract invented text, change the diagram or start an agent. Reuses identical preserved bytes, never overwrites a corrupt archive. Record the returned attachment in an original refinement input separately.")]
    public Task<CallToolResult> CaptureRefinementAsset(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, string expectedSourceToken, string attachmentPath, string archiveDirectory,
        Guid attachmentId, string expectedSha256, long expectedByteCount, string mediaType, CancellationToken cancellationToken,
        SourceReference? source = null) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, Identity(documentId), cancellationToken);
        if (string.IsNullOrEmpty(expectedSourceToken) || loaded.ContentSha256 != expectedSourceToken)
            throw new AutomationException("refinement_context_changed", "Read the exact diagram checkpoint before capturing its input assets.");
        var attachment = await RefinementAssetFiles.CaptureAsync(repositoryRoot, attachmentPath, archiveDirectory, attachmentId,
            expectedSha256, expectedByteCount, mediaType, source, cancellationToken);
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId,
            sourceToken = loaded.ContentSha256, attachment }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_refinement_input_record"),
     Description("Record an immutable original prompt, exact block/connection revision context and verified preserved attachments in the diagram's input archive. Requires the observed process epoch and file hash. Does not select a design, edit native objects, replace an open draft or start AI. Repeating identical input content observes the existing record without another insertion; changing content under the same ID is rejected. An existing record is not a receipt resolving a previous ambiguous publication: retain and reconcile any displaced XML reported by the publisher.")]
    public Task<CallToolResult> RecordRefinementInput(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, string expectedSourceToken, DiagramRefinementInput input,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var recorded = await RefinementInputFiles.RecordAsync(repositoryRoot, sourcePath, Identity(documentId), expectedSourceToken, input,
            cancellationToken, registry.StateDirectory);
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId,
            inputId = recorded.Input.Id, sourceToken = recorded.Snapshot.ContentSha256, selectedRoot = recorded.Snapshot.Graph.SelectedRoot,
            added = recorded.Added, observation = recorded.Added ? "Recorded" : "AlreadyPresent" }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        data = Upgraded(data, recorded.Snapshot.UpgradedFromSchemaVersion);
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_refinement_input_read", ReadOnly = true),
     Description("Read an exact retained original input, its historical block/connection context and requirements, plus the current integrity of preserved attachment files. Historical references never follow newer implementation heads. Missing or changed assets remain explicit. This reads saved XML and original source data; it does not launch an agent, activate a revision or discard any editor draft.")]
    public Task<CallToolResult> ReadRefinementInput(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        string expectedSourceToken, Guid inputId, CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        Guid id = Identity(documentId);
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (string.IsNullOrEmpty(expectedSourceToken) || loaded.ContentSha256 != expectedSourceToken)
            throw new AutomationException("recursive_block_file_changed", "Read the current file token before inspecting an archived input.");
        var input = loaded.Graph.RefinementInput(inputId);
        var publication = new RefinementInputReceipts(registry.StateDirectory).Read(inputId);
        var assets = new List<RefinementAssetObservation>();
        foreach (var attachment in input.Attachments)
            assets.Add(await RefinementAssetFiles.InspectAsync(repositoryRoot, attachment, cancellationToken));
        var verified = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (verified.ContentSha256 != loaded.ContentSha256)
            throw new AutomationException("recursive_block_file_changed", "The diagram changed during input inspection; read a fresh observation.");
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId,
            sourceToken = loaded.ContentSha256, input, assets,
            publication = publication is null ? null : new { publication.Version, publication.InputId, publication.DesignPath,
                publication.BeforeSha256, publication.AfterSha256, publication.Stage, publication.StagedPath,
                publication.RetainedPath, publication.ConfirmedAt },
            blocks = input.BlockPath.Select(p => new { selection = p, block = loaded.Graph.Inspect(p), requirements = loaded.Graph.Requirements(p).Requirements }),
            connections = input.ConnectionPath.Select(p => new { selection = p, connection = loaded.Graph.Connections(input.BlockPath[^1].BlockId).Inspect(p),
                requirements = loaded.Graph.Connections(input.BlockPath[^1].BlockId).Requirements(p).Requirements }) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_components", ReadOnly = true),
     Description("Inspect the exact electrical component realizations of a saved block using a repository-relative hardware manifest and its observed SHA256. Reads only its declared design and knowledge-library files. Returns source hashes, actual components/parts and all mapped native symbol units and sheet paths, or explicit missing/replaced identities and binding diagnostics. These are saved XML snapshots, not live editor state, electrical correctness or inferred part choices. Never changes a diagram or native design.")]
    public Task<CallToolResult> Components(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        string expectedSourceToken, BlockSelection selection, string manifestPath, string expectedManifestToken,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        _ = HistorySelection(selection); Guid id = Identity(documentId);
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (string.IsNullOrEmpty(expectedSourceToken) || loaded.ContentSha256 != expectedSourceToken)
            throw new AutomationException("recursive_block_file_changed", "Read the exact saved diagram before resolving its components.");
        var inspection = await BlockComponentFiles.InspectAsync(repositoryRoot, manifestPath, expectedManifestToken,
            loaded.Graph.Inspect(selection).EffectiveComponentBindings, cancellationToken);
        var checkedDiagram = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (checkedDiagram.ContentSha256 != expectedSourceToken)
            throw new AutomationException("recursive_block_file_changed", "The diagram changed during component inspection; read a fresh observation.");
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId,
            sourceToken = loaded.ContentSha256, selection, inspection },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_components_set"),
     Description("Save explicit design/circuit/component identity bindings on one exact block revision. A block may map to zero, one or multiple components across child designs. Requires the observed native instance epoch, source hash, selected root and complete root-to-block path. Saves a new revision and retains old bindings and requirements in history, without creating components, changing native circuits or claiming that targets resolve. Use kicad_diagram_components to inspect the saved targets. Reload or reconcile open native drafts after this external save.")]
    public Task<CallToolResult> SetComponents(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, string expectedSourceToken, BlockSelection expectedRoot, BlockSelection[] blockPath,
        BlockComponentBindings bindings, Guid operationId, string actor, CancellationToken cancellationToken, Guid? refinementInputId = null) => Execute(async () =>
    {
        var path = blockPath?.ToArray() ?? [];
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        if (path.Length == 0 || path.Any(p => p is null) || expectedRoot is null || bindings is null
            || operationId == Guid.Empty || string.IsNullOrWhiteSpace(actor) || string.IsNullOrEmpty(expectedSourceToken))
            throw new AutomationException("invalid_component_binding_operation", "Provide the exact root/path, bindings, observed source, operation identity and actor.");
        bindings.Validate(); Guid id = Identity(documentId);
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (loaded.ContentSha256 != expectedSourceToken)
            throw new AutomationException("recursive_block_file_changed", "The saved diagram changed; retain the proposed bindings and compare with its latest revision.");
        var draft = loaded.Graph.StartDraft(path[^1]) with { ComponentBindings = bindings };
        var origin = new RequirementRevisionOrigin(RequirementRevisionActor.Agent, actor, DateTimeOffset.UtcNow,
            "Update component bindings", [], [operationId]);
        origin = RefinementInputFiles.AttachOrigin(loaded.Graph, [.. path], refinementInputId, origin);
        var saved = await RecursiveBlockFiles.SaveDraftAsync(repositoryRoot, sourcePath, id, expectedSourceToken, expectedRoot,
            [.. path], draft, operationId, Guid.NewGuid(), [.. path.Skip(1).Select(_ => Guid.NewGuid())], origin, token: cancellationToken);
        var selection = saved.Graph.Walk(saved.Graph.SelectedRoot).Single(s => s.BlockId == draft.Baseline.BlockId);
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId, operationId,
            sourceToken = saved.ContentSha256, selectedRoot = saved.Graph.SelectedRoot, selection,
            bindings = saved.Graph.Inspect(selection).EffectiveComponentBindings, changed = saved.ContentSha256 != loaded.ContentSha256 });
        data = Upgraded(data, saved.UpgradedFromSchemaVersion);
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_physical_allocation", ReadOnly = true),
     Description("Read the exact physical allocation attached to one saved block revision. Targets may be components, boards, board stacks or larger assemblies, and may remain unknown or partial with an explicit reason. This is a declared mapping, not inferred from names and not proof that a board or component exists on disk.")]
    public Task<CallToolResult> PhysicalAllocation(string instanceId, string repositoryRoot, string sourcePath,
        string documentId, string expectedSourceToken, BlockSelection selection, CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        _ = HistorySelection(selection); Guid id = Identity(documentId);
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (string.IsNullOrEmpty(expectedSourceToken) || loaded.ContentSha256 != expectedSourceToken)
            throw new AutomationException("recursive_block_file_changed", "Read the exact saved diagram before inspecting its physical allocation.");
        var revision = loaded.Graph.Inspect(selection);
        var checkedDiagram = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (checkedDiagram.ContentSha256 != loaded.ContentSha256)
            throw new AutomationException("recursive_block_file_changed", "The diagram changed during physical allocation inspection; read a fresh observation.");
        return Data(new { instanceId, instanceEpoch = session.Epoch, documentId, sourceToken = loaded.ContentSha256,
            selection, allocation = revision.PhysicalAllocation });
    });

    [McpServerTool(Name = "kicad_diagram_physical_allocation_set"),
     Description("Save an explicit physical allocation for one exact block revision. Preserve Unknown when no target is known, or Partial with mapped targets and a reason for the unresolved remainder; never infer a board, stack or assembly from a block name. Requires the observed source token, exact root-to-block path and operation identity. Creates a new conceptual revision and does not create files, components, boards or native electrical objects.")]
    public Task<CallToolResult> SetPhysicalAllocation(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, string expectedSourceToken, BlockSelection expectedRoot, BlockSelection[] blockPath,
        BlockPhysicalAllocation allocation, Guid operationId, string actor, CancellationToken cancellationToken,
        Guid? refinementInputId = null) => Execute(async () =>
    {
        var requestedPath = blockPath?.ToArray() ?? [];
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; reattach and inspect it again.");
        if (requestedPath.Length == 0 || requestedPath.Any(p => p is null) || expectedRoot is null || allocation is null
            || operationId == Guid.Empty || string.IsNullOrWhiteSpace(actor) || string.IsNullOrEmpty(expectedSourceToken))
            throw new AutomationException("invalid_physical_allocation_operation", "Provide the exact root/path, allocation, observed source, operation identity and actor.");
        allocation.Validate(); Guid id = Identity(documentId);
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (loaded.ContentSha256 != expectedSourceToken)
            throw new AutomationException("recursive_block_file_changed", "The saved design changed; retain the allocation proposal and compare it with the latest revision.");
        var draft = loaded.Graph.StartDraft(requestedPath[^1]) with { PhysicalAllocation = allocation };
        var origin = new RequirementRevisionOrigin(RequirementRevisionActor.Agent, actor, DateTimeOffset.UtcNow,
            "Update physical allocation", [], [operationId]);
        origin = RefinementInputFiles.AttachOrigin(loaded.Graph, [.. requestedPath], refinementInputId, origin);
        var saved = await RecursiveBlockFiles.SaveDraftAsync(repositoryRoot, sourcePath, id, expectedSourceToken, expectedRoot,
            [.. requestedPath], draft, operationId, Guid.NewGuid(), [.. requestedPath.Skip(1).Select(_ => Guid.NewGuid())], origin, token: cancellationToken);
        var selection = saved.Graph.Walk(saved.Graph.SelectedRoot).Single(s => s.BlockId == draft.Baseline.BlockId);
        var result = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId, operationId,
            sourceToken = saved.ContentSha256, selectedRoot = saved.Graph.SelectedRoot, selection,
            allocation = saved.Graph.Inspect(selection).PhysicalAllocation,
            changed = saved.ContentSha256 != loaded.ContentSha256 });
        result = Upgraded(result, saved.UpgradedFromSchemaVersion);
        return new() { Content = [new TextContentBlock { Text = result.GetRawText() }], StructuredContent = result };
    });

    [McpServerTool(Name = "kicad_diagram_definition_guidance", ReadOnly = true),
     Description("Resolve the exact knowledge-class choices of a saved block against explicitly supplied repository-relative library paths. Returns inherited guidance and source hashes, with each candidate separate and missing libraries/revisions/classes explicit. A selected class is not a selected electrical part or proof of compatibility. Does not change XML, the editor or any native component.")]
    public Task<CallToolResult> DefinitionGuidance(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        string expectedSourceToken, BlockSelection selection, string[] libraryPaths, CancellationToken cancellationToken) => Execute(async () =>
    {
        var paths = libraryPaths?.ToArray() ?? throw new AutomationException("missing_definition_libraries", "Provide explicit library paths, including an empty list when unavailable.");
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        _ = HistorySelection(selection);
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, Identity(documentId), cancellationToken);
        if (string.IsNullOrEmpty(expectedSourceToken) || loaded.ContentSha256 != expectedSourceToken)
            throw new AutomationException("recursive_block_file_changed", "Read the exact saved diagram before resolving its definition guidance.");
        var libraries = await BlockDefinitionLibraries.ReadAsync(repositoryRoot, paths, cancellationToken);
        var resolution = BlockDefinitionGuidance.Resolve(loaded.Graph.Inspect(selection).EffectiveDefinition, [.. libraries.Select(l => l.Library)]);
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId, sourceToken = loaded.ContentSha256,
            selection, resolution, libraries = libraries.Select(l => new { l.RelativePath, l.ContentSha256, l.Library.Id, l.Library.Revision }) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_definition_set"),
     Description("Save independent purpose/type/manufacturer/family/model/orderable-part/package/knowledge-class choices on one exact block revision. Choices retain unspecified, unknown, candidate or selected state, strength, conditions and sources. Requires the observed instance epoch, file token, selected root and root-to-block path. Creates one new revision without changing requirement text, siblings, connections or native electrical objects. This does not validate a part name against a library or materialize a schematic/footprint. Reload or reconcile an already-open native draft after this external save.")]
    public Task<CallToolResult> SetDefinition(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, string expectedSourceToken, BlockSelection expectedRoot, BlockSelection[] blockPath,
        BlockDefinition definition, Guid operationId, string actor, CancellationToken cancellationToken, Guid? refinementInputId = null) => Execute(async () =>
    {
        var requestedPath = blockPath?.ToArray() ?? [];
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        if (requestedPath.Length == 0 || requestedPath.Any(p => p is null) || expectedRoot is null
            || definition is null || operationId == Guid.Empty || string.IsNullOrWhiteSpace(actor) || string.IsNullOrEmpty(expectedSourceToken))
            throw new AutomationException("invalid_definition_operation", "Provide the exact root/path, definition, observed source, operation identity and actor.");
        definition.Validate(); Guid id = Identity(documentId);
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, id, cancellationToken);
        if (loaded.ContentSha256 != expectedSourceToken)
            throw new AutomationException("recursive_block_file_changed", "The saved design changed; retain the proposed definition and compare it with the latest revision.");
        var draft = loaded.Graph.StartDraft(requestedPath[^1]) with { Definition = definition };
        var origin = new RequirementRevisionOrigin(RequirementRevisionActor.Agent, actor, DateTimeOffset.UtcNow,
            "Refine block definition", [], [operationId]);
        origin = RefinementInputFiles.AttachOrigin(loaded.Graph, [.. requestedPath], refinementInputId, origin);
        var saved = await RecursiveBlockFiles.SaveDraftAsync(repositoryRoot, sourcePath, id, expectedSourceToken, expectedRoot,
            [.. requestedPath], draft, operationId, Guid.NewGuid(), [.. requestedPath.Skip(1).Select(_ => Guid.NewGuid())], origin, token: cancellationToken);
        var selected = saved.Graph.Walk(saved.Graph.SelectedRoot).Single(s => s.BlockId == draft.Baseline.BlockId);
        var definitionData = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(RecursiveBlockCodec.Encode(saved.Graph.Inspect(selected).EffectiveDefinition)));
        var result = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId, operationId,
            sourceToken = saved.ContentSha256, selectedRoot = saved.Graph.SelectedRoot, selection = selected, definition = definitionData,
            changed = saved.ContentSha256 != loaded.ContentSha256 });
        result = Upgraded(result, saved.UpgradedFromSchemaVersion);
        return new() { Content = [new TextContentBlock { Text = result.GetRawText() }], StructuredContent = result };
    });

    [McpServerTool(Name = "kicad_diagram_connection_endpoint_set"),
     KiCadCapability("structural-diagram", "compiled-mcp", "instance epoch, source token, exact root and level path, exact connection path, operation ID"),
     Description("Bind one end of one exact connection or member to a port, or leave it explicitly unresolved, in a saved system diagram. endpointIndex counts the connection's ends from 0. action \"bind\" needs blockId and interfaceId: one of the level's blocks and one of its ports, or the level's own block and a port on the level's boundary, which carries the end through that port to the parent level's connections using it. An end that states pins, candidates or a compatibility selector keeps them while it stays on its block; moving such an end to another block is refused. action \"unbind\" leaves the end Unresolved on its block (or on blockId, one of the level's blocks) with no port, pins or selector. intent, when given, replaces what the end says. The edit is checked against the current file (expectedSourceToken), the selected root (expectedRoot), the root-to-level blockPath and the root-to-member connectionPath, each by exact revision: a changed or no longer current target is refused (recursive_block_file_changed, stale_root_revision, stale_block_revision, stale_parent_revision, stale_connection_revision), a block, port, connection or end the level does not have is refused (connection_edit_target_missing), and an edit that does not say exactly one thing is refused (ambiguous_connection_edit); a refusal writes nothing. Otherwise one guarded write saves a new revision of the connection (and of the groups containing it), of the level and of each level above it; the earlier binding and every requirement field stay in history. An end that would not change writes nothing (changed=false). Returns the new source token, the block and connection paths now selected, the end before and after, and, for an end on a port of the level's boundary, how that port maps through the levels. An open editor shows the change after it reloads. Never touches schematic or PCB files.")]
    public Task<CallToolResult> SetConnectionEndpoint(string instanceId, string expectedInstanceEpoch, string repositoryRoot, string sourcePath,
        string documentId, string expectedSourceToken, BlockSelection expectedRoot, BlockSelection[] blockPath, ConnectionSelection[] connectionPath,
        int endpointIndex, string action, Guid operationId, string actor, CancellationToken cancellationToken, Guid? blockId = null,
        Guid? interfaceId = null, string? intent = null, SourceReference[]? sources = null, Guid? refinementInputId = null) => Execute(async () =>
    {
        var edit = await PrepareConnectionEdit(instanceId, expectedInstanceEpoch, repositoryRoot, sourcePath, documentId, expectedSourceToken,
            expectedRoot, blockPath, connectionPath, operationId, actor, sources, refinementInputId,
            action == "bind" ? "Bind connection end" : "Unbind connection end", cancellationToken);
        var kind = action switch
        {
            "bind" => ConnectionEndpointAction.Bind, "unbind" => ConnectionEndpointAction.Unbind,
            _ => throw new AutomationException("ambiguous_connection_edit", "Choose the action bind or unbind. Nothing was changed.")
        };
        var draft = RecursiveConnectionEdits.SetEndpoint(edit.Graph, edit.Target, endpointIndex, kind, blockId, interfaceId, intent);
        var (result, graph, levelPath, linkPath) = await SaveConnectionEdit(edit, draft, [], cancellationToken);
        var endpoint = graph.Connections(levelPath[^1].BlockId).Inspect(linkPath[^1]).Endpoints[endpointIndex];
        return Data(new { instanceId, instanceEpoch = edit.Epoch, documentId, operationId, sourceToken = result.SourceToken,
            changed = result.SaveSummary.Changed, selectedRoot = graph.SelectedRoot, blockPath = levelPath, connectionPath = linkPath, endpointIndex,
            previousEndpoint = edit.Target.Connection.Endpoints[endpointIndex], endpoint,
            boundaryMapping = RecursiveConnectionEdits.Boundary(graph, levelPath, endpoint),
            saveSummary = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(result.SaveSummary)) },
            checked((int)result.UpgradedFromSchemaVersion));
    });

    [McpServerTool(Name = "kicad_diagram_connection_members_refine"),
     KiCadCapability("structural-diagram", "compiled-mcp", "instance epoch, source token, exact root and level path, exact connection path, operation ID"),
     Description("Refine the members of one exact connection or member of a saved system diagram into groups, pairs and signals. memberIds is its direct member list afterwards, in order: ids of its current members and of new members. newMembers declares each new member: a fresh connectionId the agent chooses, its caption (name), its type (kind: Signal by default, SignalGroup, DifferentialPair, Interface or Abstract), the ids it groups in memberIds (current members or other new members; a DifferentialPair groups exactly two single signals, a Signal groups nothing) and its own General, Schematic and Routing requirement text. Every current member and every new member must appear exactly once across memberIds and the new members' lists: a refinement never drops a member (members are removed through the editor's removal cascade). A current member keeps its exact saved revision, requirement history and notes, and interface realizations naming it stay exact. New members run between the connection's ends as they are drawn (block and port; no pins or intent) and start their own requirement history with sources and refinementInputId recorded in its origin. The edit is checked against the current file, selected root, root-to-level blockPath and root-to-member connectionPath by exact revision; a changed or stale target, an identity the level does not have (connection_edit_target_missing), a member listed twice or left out (ambiguous_connection_edit), a reused identity (identity_reused) or an invalid group or pair (invalid_connection_refinement) is refused and writes nothing. Otherwise one guarded write saves the new members, a new revision of the connection (and of the groups containing it), of the level and of each level above it. An unchanged member list writes nothing (changed=false). Returns the new source token, the paths now selected, and the connection with every member below it and its requirement fields. Never touches schematic or PCB files.")]
    public Task<CallToolResult> RefineConnectionMembers(string instanceId, string expectedInstanceEpoch, string repositoryRoot, string sourcePath,
        string documentId, string expectedSourceToken, BlockSelection expectedRoot, BlockSelection[] blockPath, ConnectionSelection[] connectionPath,
        Guid[] memberIds, ConnectionMemberDefinition[] newMembers, Guid operationId, string actor, CancellationToken cancellationToken,
        SourceReference[]? sources = null, Guid? refinementInputId = null, string implementationName = "Initial") => Execute(async () =>
    {
        var edit = await PrepareConnectionEdit(instanceId, expectedInstanceEpoch, repositoryRoot, sourcePath, documentId, expectedSourceToken,
            expectedRoot, blockPath, connectionPath, operationId, actor, sources, refinementInputId, "Refine connection members", cancellationToken);
        if (memberIds is null || newMembers is null)
            throw new AutomationException("ambiguous_connection_edit", "List the connection's members afterwards (memberIds) and each new member (newMembers), even when a list is empty. Nothing was changed.");
        var (draft, created) = RecursiveConnectionEdits.RefineMembers(edit.Target, [.. memberIds], [.. newMembers], Guid.NewGuid, implementationName);
        var (result, graph, levelPath, linkPath) = await SaveConnectionEdit(edit, draft, created, cancellationToken);
        var level = result.Document.Graph.ConnectionArchives.Single(a => a.OwnerBlockId == levelPath[^1].BlockId.ToString("D"));
        var archive = graph.Connections(levelPath[^1].BlockId);
        var fresh = created.Select(m => m.Selection.ConnectionId).ToHashSet();
        object Row(ConnectionSelection selection)
        {
            var row = level.Revisions.Single(r => r.Selection.RevisionId == selection.RevisionId.ToString("D"));
            var fields = level.RequirementHistories.Single(h => h.StateId == row.Selection.StateId).Revisions.Single(r => r.Id == row.RequirementRevisionId);
            return new { created = fresh.Contains(selection.ConnectionId), revision = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(row)),
                requirements = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(fields)) };
        }
        return Data(new { instanceId, instanceEpoch = edit.Epoch, documentId, operationId, sourceToken = result.SourceToken,
            changed = result.SaveSummary.Changed, selectedRoot = graph.SelectedRoot, blockPath = levelPath, connectionPath = linkPath,
            connection = Row(linkPath[^1]), members = archive.Walk([linkPath[^1]]).Skip(1).Select(Row).ToArray(),
            saveSummary = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(result.SaveSummary)) },
            checked((int)result.UpgradedFromSchemaVersion));
    });

    private sealed record ConnectionEdit(string Epoch, string RepositoryRoot, string SourcePath, string DocumentId, string SourceToken,
        RecursiveBlockGraph Graph, ConnectionEditTarget Target, RequirementRevisionOrigin Origin);

    /// <summary>The checks every connection edit shares: the attached instance and its epoch, the operation and actor, the exact
    /// file the agent observed, and the target by exact identity in that file (ledger pf92d0ecdec8805b4). Nothing is written.</summary>
    private async Task<ConnectionEdit> PrepareConnectionEdit(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, string expectedSourceToken, BlockSelection expectedRoot, BlockSelection[] blockPath,
        ConnectionSelection[] connectionPath, Guid operationId, string actor, SourceReference[]? sources, Guid? refinementInputId,
        string summary, CancellationToken cancellationToken)
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(actor) || string.IsNullOrEmpty(expectedSourceToken))
            throw new AutomationException("invalid_connection_edit_operation", "Provide the observed source token, an operation identity and the agent's name.");
        if (expectedRoot is null || blockPath is null || connectionPath is null)
            throw new AutomationException("ambiguous_connection_edit", "Name the saved root, the block path to the level and the connection path by exact identity. Nothing was changed.");
        if (new[] { expectedRoot }.Concat(blockPath).Any(p => p is null || p.BlockId == Guid.Empty || p.StateId == Guid.Empty || p.RevisionId == Guid.Empty)
            || connectionPath.Any(p => p is null || p.ConnectionId == Guid.Empty || p.StateId == Guid.Empty || p.RevisionId == Guid.Empty))
            throw new AutomationException("invalid_diagram_identity", "Provide exact non-empty block and connection identities, implementations and revisions.");
        var loaded = await RecursiveBlockFiles.ReadAsync(repositoryRoot, sourcePath, Identity(documentId), cancellationToken);
        if (loaded.ContentSha256 != expectedSourceToken)
            throw new AutomationException("recursive_block_file_changed", "The saved diagram changed since it was read; read it again before editing a connection. Nothing was changed.");
        var target = RecursiveConnectionEdits.Locate(loaded.Graph, expectedRoot, [.. blockPath], [.. connectionPath]);
        var origin = new RequirementRevisionOrigin(RequirementRevisionActor.Agent, actor, DateTimeOffset.UtcNow, summary, [.. sources ?? []], [operationId]);
        origin.Validate();
        origin = RefinementInputFiles.AttachOrigin(loaded.Graph, target.BlockPath, refinementInputId, origin);
        return new(session.Epoch, repositoryRoot, sourcePath, documentId, expectedSourceToken, loaded.Graph, target, origin);
    }

    /// <summary>Saves a connection edit through the diagram companion's connection save (the action the helper process runs):
    /// the draft, the members it creates and fresh revision identities for the connection, the groups containing it, the
    /// level and every level above it, guarded by the observed file token.</summary>
    private static async Task<(RecursiveFileResult Result, RecursiveBlockGraph Graph, ImmutableArray<BlockSelection> BlockPath,
        ImmutableArray<ConnectionSelection> ConnectionPath)> SaveConnectionEdit(ConnectionEdit edit, DiagramConnectionDraft draft,
        ImmutableArray<NewConnectionMember> created, CancellationToken cancellationToken)
    {
        var save = new SaveConnectionDraftData { ExpectedRoot = RecursiveBlockCodec.EncodeSelection(edit.Graph.SelectedRoot),
            Draft = RecursiveBlockCodec.Encode(draft), NewConnectionRevisionId = Guid.NewGuid().ToString("D"),
            NewRequirementRevisionId = Guid.NewGuid().ToString("D"), NewBlockRevisionId = Guid.NewGuid().ToString("D"),
            NewBlockRequirementRevisionId = Guid.NewGuid().ToString("D"), Origin = RecursiveBlockCodec.EncodeOrigin(edit.Origin) };
        save.BlockPath.Add(edit.Target.BlockPath.Select(RecursiveBlockCodec.EncodeSelection));
        save.ConnectionPath.Add(edit.Target.ConnectionPath.Select(RecursiveBlockCodec.EncodeSelection));
        save.BlockAncestorRevisionIds.Add(edit.Target.BlockPath.Skip(1).Select(_ => Guid.NewGuid().ToString("D")));
        save.ConnectionAncestorRevisionIds.Add(edit.Target.ConnectionPath.Skip(1).Select(_ => Guid.NewGuid().ToString("D")));
        save.NewMembers.Add(created.Select(RecursiveBlockCodec.Encode));
        var result = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion,
            Action = RecursiveFileAction.RfaSaveConnection, RepositoryRoot = edit.RepositoryRoot, SourcePath = edit.SourcePath,
            DocumentId = edit.DocumentId, ExpectedSourceToken = edit.SourceToken, SaveConnection = save }, cancellationToken);
        var graph = RecursiveBlockCodec.Decode(result.Document.Graph);
        var (blockPath, connectionPath) = RecursiveConnectionEdits.Follow(graph, edit.Target.BlockPath, edit.Target.ConnectionPath);
        return (result, graph, blockPath, connectionPath);
    }

    [McpServerTool(Name = "kicad_diagram_observe", ReadOnly = true),
     Description("Render one or more native structural-diagram views with matching structured objects at the exact observed source token and editor view revision. Omit a view selection for the current canvas including its draft/preview; specify an exact selection for another saved level or revision. Optional diagram-unit viewport chooses a detail region. Rendering never navigates the user window or saves a draft. Each view returns PNG, actual viewport, coordinate system and object identities; this is not native schematic/PCB realization or electrical verification.")]
    public Task<CallToolResult> Observe(string instanceId, string documentId, string expectedSourceToken,
        ulong expectedViewRevision, DiagramObservationView[] views, CancellationToken cancellationToken) => Execute(async () =>
    {
        if (views is null || views.Length is < 1 or > 8 || string.IsNullOrEmpty(expectedSourceToken))
            throw new AutomationException("invalid_diagram_observation", "Specify the observed source/view revision and one to eight view requests.");
        var request = new ObserveRecursiveDiagramEditor { DocumentId = Identity(documentId).ToString("D"),
            ExpectedSourceToken = expectedSourceToken, ExpectedViewRevision = expectedViewRevision };
        foreach (var view in views)
        {
            if (view is null) throw new AutomationException("invalid_diagram_observation", "Every view needs an explicit identifier and pixel dimensions.");
            var item = new RecursiveDiagramViewRequest { ViewId = view.ViewId ?? "", PixelWidth = view.PixelWidth, PixelHeight = view.PixelHeight };
            if (view.Selection is not null) item.Selection = HistorySelection(view.Selection);
            if (view.Viewport is { } box) item.Viewport = new() { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height };
            request.Views.Add(item);
        }
        var native = registry.Client(instanceId);
        var observation = await native.InvokeAsync<ObserveRecursiveDiagramEditor, RecursiveDiagramObservation>(request, cancellationToken);
        if (observation.DocumentId != documentId || observation.SourceToken != expectedSourceToken || observation.ViewRevision != expectedViewRevision
            || observation.Views.Count != views.Length || observation.Editor is null || observation.Editor.DocumentId != documentId || observation.Editor.ViewRevision != expectedViewRevision)
            throw new AutomationException("diagram_observation_mismatch", "The image response does not match the requested document checkpoint.");
        var metadata = observation.Clone(); var content = new List<ContentBlock>(); var imageReferences = new List<object>();
        for (int i = 0; i < observation.Views.Count; ++i)
        {
            var view = observation.Views[i];
            byte[] png = view.Png.ToByteArray();
            if (view.ViewId != views[i].ViewId || png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || view.PixelWidth != views[i].PixelWidth || view.PixelHeight != views[i].PixelHeight
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) != view.PixelWidth
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) != view.PixelHeight)
                throw new AutomationException("diagram_observation_image", "The native view did not return the exact requested PNG dimensions and identity.");
            metadata.Views[i].Png = ByteString.Empty;
            imageReferences.Add(new { viewId = view.ViewId, contentIndex = 2 + i * 2, mimeType = "image/png",
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png)) });
            content.Add(new TextContentBlock { Text = "View: " + view.ViewId }); content.Add(ImageContentBlock.FromBytes(png, "image/png"));
        }
        // A measured zero origin or false draft flag is known information, not
        // an unspecified engineering fact. Keep these computed defaults explicit.
        var formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithFormatDefaultValues(true));
        var wire = JsonNode.Parse(formatter.Format(metadata))!.AsObject();
        // The native editor speaks schema 2 (level draft, tool, viewports and resolved layout); only the
        // never-implemented flat-conversion fields are left out.
        RecursiveBlockCodec.OmitFieldsBeyondSchema(metadata, wire, RecursiveBlockCodec.NativeEditorSchemaVersion);
        foreach (var view in wire["views"]!.AsArray()) view!.AsObject().Remove("png");
        var structured = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = native.Epoch,
            observation = wire, imageReferences });
        content.Insert(0, new TextContentBlock { Text = structured.GetRawText() });
        return new() { Content = content, StructuredContent = structured };
    });

    [McpServerTool(Name = "kicad_diagram_history", ReadOnly = true),
     Description("Read a bounded whole-diagram revision list at an exact block/implementation/revision context. Includes actual origin, version and local contents counts. Later implementation heads do not enter an older context. Reading or selecting a history row does not activate a design or change an open draft.")]
    public Task<CallToolResult> History(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        BlockSelection context, CancellationToken cancellationToken, int offset = 0, int limit = 50, string? expectedSourceToken = null) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        var result = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion, Action = RecursiveFileAction.RfaDiagramHistory,
            RepositoryRoot = repositoryRoot, SourcePath = sourcePath, DocumentId = documentId, ExpectedSourceToken = expectedSourceToken ?? "",
            Block = HistorySelection(context), Offset = offset, Limit = limit }, cancellationToken);
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId, sourceToken = result.SourceToken,
            history = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(result.DiagramHistory)) });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_history_compare", ReadOnly = true),
     Description("Compare an earlier whole diagram with the exact saved context in the same implementation. Reports content additions, removals, changes and ordering by stable object identity, not name similarity. The difference describes changes from inspected history to the context; no preview, activation or edit occurs.")]
    public Task<CallToolResult> CompareHistory(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        BlockSelection context, BlockSelection inspected, CancellationToken cancellationToken, string? expectedSourceToken = null) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        var result = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion, Action = RecursiveFileAction.RfaCompareDiagramHistory,
            RepositoryRoot = repositoryRoot, SourcePath = sourcePath, DocumentId = documentId, ExpectedSourceToken = expectedSourceToken ?? "",
            Block = HistorySelection(context), InspectedBlock = HistorySelection(inspected) }, cancellationToken);
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId, sourceToken = result.SourceToken,
            comparison = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(result.DiagramComparison)) });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_prepare_restoration", ReadOnly = true),
     Description("Prepare earlier whole-diagram contents as a new draft based on the exact saved context and file token. Returns the typed draft with original child/connection/comment references and restoration provenance. Does not write XML, replace an open editor draft, activate native designs or save; existing unsaved work remains the caller's responsibility to preserve.")]
    public Task<CallToolResult> PrepareRestoration(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        string expectedSourceToken, BlockSelection context, BlockSelection source, CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        if (string.IsNullOrEmpty(expectedSourceToken)) throw new AutomationException("missing_diagram_source_token", "Supply the exact observed file token before preparing a restoration.");
        var loaded = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion, RepositoryRoot = repositoryRoot,
            SourcePath = sourcePath, DocumentId = documentId, ExpectedSourceToken = expectedSourceToken }, cancellationToken);
        var graph = RecursiveBlockCodec.Decode(loaded.Document.Graph);
        var result = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion, Action = RecursiveFileAction.RfaPrepareDiagramRestoration,
            RepositoryRoot = repositoryRoot, SourcePath = sourcePath, DocumentId = documentId, ExpectedSourceToken = expectedSourceToken,
            Restoration = new() { Draft = RecursiveBlockCodec.Encode(graph.StartDraft(context)), Source = HistorySelection(source) } }, cancellationToken);
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId, sourceToken = result.SourceToken,
            draft = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(result.PreparedDraft)) });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    private static BlockSelectionData HistorySelection(BlockSelection selection) => selection is not null
        && selection.BlockId != Guid.Empty && selection.StateId != Guid.Empty && selection.RevisionId != Guid.Empty
        ? new() { BlockId = selection.BlockId.ToString("D"), StateId = selection.StateId.ToString("D"), RevisionId = selection.RevisionId.ToString("D") }
        : throw new AutomationException("invalid_diagram_identity", "Provide the exact non-empty block, implementation and revision selection.");

    [McpServerTool(Name = "kicad_diagram_manage_implementation"),
     Description("Create a new interior, duplicate, rename, remove from choices, or restore one implementation in the saved recursive model. Actions: new, duplicate, rename, remove, restore. Requires the observed instance epoch, exact file token/root/source and a non-empty operation ID. Creation stays unselected; removal retains historical revisions and rejects the active choice. Does not activate native schematics/PCBs or discard an open editor draft. After an external change, reload or reconcile that window explicitly.")]
    public Task<CallToolResult> ManageImplementation(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, string expectedSourceToken, BlockSelection expectedRoot, BlockSelection source,
        string action, Guid operationId, string actor, CancellationToken cancellationToken, string? name = null) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; reattach and inspect it again.");
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(actor) || expectedRoot is null || source is null)
            throw new AutomationException("invalid_implementation_operation", "Provide a stable operation UUID, agent name and exact root/source selections.");
        var kind = action switch { "new" => ImplementationActionKind.IakNew, "duplicate" => ImplementationActionKind.IakDuplicate,
            "rename" => ImplementationActionKind.IakRename, "remove" => ImplementationActionKind.IakArchive,
            "restore" => ImplementationActionKind.IakRestore, _ => throw new AutomationException("invalid_implementation_action", "Choose new, duplicate, rename, remove or restore.") };
        BlockSelectionData Selection(BlockSelection value) => new() { BlockId = value.BlockId.ToString("D"), StateId = value.StateId.ToString("D"), RevisionId = value.RevisionId.ToString("D") };
        var management = new ManageImplementationData { Action = kind, ExpectedRoot = Selection(expectedRoot), Source = Selection(source), Name = name ?? "",
            Origin = new() { Kind = DiagramActorKind.DakAgent, Actor = actor, Summary = "Manage implementation: " + action,
                RecordedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) } };
        management.Origin.InputIds.Add(operationId.ToString("D"));
        if (kind is ImplementationActionKind.IakNew or ImplementationActionKind.IakDuplicate)
        { management.NewStateId = operationId.ToString("D"); management.NewRevisionId = Guid.NewGuid().ToString("D"); management.NewRequirementRevisionId = Guid.NewGuid().ToString("D"); }
        else management.ChangeId = operationId.ToString("D");
        var result = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion, Action = RecursiveFileAction.RfaManageImplementation,
            RepositoryRoot = repositoryRoot, SourcePath = sourcePath, DocumentId = documentId, ExpectedSourceToken = expectedSourceToken, Implementation = management }, cancellationToken);
        var implementation = result.Document.Graph.States.Single(s => s.Id == result.ImplementationId);
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId, operationId,
            sourceToken = result.SourceToken, implementation = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(implementation)),
            selectedRoot = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(result.Document.Graph.SelectedRoot)) });
        data = Upgraded(data, checked((int)result.UpgradedFromSchemaVersion));
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_read", ReadOnly = true),
     Description("Read one exact saved recursive diagram level as structured data: requirements, direct child selections, boundary interfaces with their domain and direction, connection/member revisions with domain, direction and any stated interconnect realization, partial endpoints, comments, the level's saved layout (diagram units; absent entries are unplaced) and its interface realizations, plus implementation metadata. Omitting block/state/revision reads the saved root; otherwise provide all three exact IDs. Returns the source token, the stored file format (storedSchemaVersion 1 or 2), whether the file is writable and the native instance epoch. Unknown and Partial realizations are never resolved facts. Does not return an unsaved window draft, activate an implementation, or claim schematic/PCB realization.")]
    public Task<CallToolResult> ReadSaved(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        CancellationToken cancellationToken, string? blockId = null, string? stateId = null, string? revisionId = null,
        string? expectedSourceToken = null) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        var loaded = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion, RepositoryRoot = repositoryRoot,
            SourcePath = sourcePath, DocumentId = documentId, ExpectedSourceToken = expectedSourceToken ?? "" }, cancellationToken);
        var graph = RecursiveBlockCodec.Decode(loaded.Document.Graph);
        var selection = blockId is null && stateId is null && revisionId is null ? graph.SelectedRoot
            : blockId is not null && stateId is not null && revisionId is not null
                ? new BlockSelection(Identity(blockId), Identity(stateId), Identity(revisionId))
                : throw new AutomationException("incomplete_diagram_target", "Provide the complete block, implementation and revision target, or omit all three for the saved root.");
        var selected = graph.Inspect(selection);
        var protocol = loaded.Document.Graph;
        JsonElement Wire(IMessage value) => JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(value));
        var block = protocol.Revisions.Single(r => r.Selection.RevisionId == selection.RevisionId.ToString("D"));
        var fieldHistory = protocol.RequirementHistories.Single(h => h.StateId == block.Selection.StateId);
        var fields = fieldHistory.Revisions.Single(r => r.Id == block.RequirementRevisionId);
        var connections = new List<object>();
        if (!selected.LocalDiagram.Connections.IsEmpty)
        {
            var archive = graph.Connections(selection.BlockId);
            var rows = protocol.ConnectionArchives.Single(a => a.OwnerBlockId == selection.BlockId.ToString("D"));
            foreach (var connection in archive.Walk(selected.LocalDiagram.Connections))
            {
                var row = rows.Revisions.Single(r => r.Selection.RevisionId == connection.RevisionId.ToString("D"));
                var requirements = rows.RequirementHistories.Single(h => h.StateId == row.Selection.StateId).Revisions.Single(r => r.Id == row.RequirementRevisionId);
                connections.Add(new { revision = Wire(row), requirements = Wire(requirements) });
            }
        }
        var data = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = loaded.Document.SchemaVersion, storedSchemaVersion = loaded.Document.StoredSchemaVersion,
            sourceWritable = loaded.Document.SourceWritable, instanceId, instanceEpoch = session.Epoch, documentId,
            sourcePath = loaded.Document.SourcePath, sourceToken = loaded.SourceToken, units = "diagram-unit",
            selectedRoot = Wire(protocol.SelectedRoot), inspectedSelection = Wire(block.Selection),
            isSelected = graph.Walk(graph.SelectedRoot).Contains(selection), block = Wire(block), requirements = Wire(fields),
            children = selected.Children.Select(c => protocol.Revisions.Single(r => r.Selection.RevisionId == c.RevisionId.ToString("D")))
                .Select(r => new { selection = Wire(r.Selection), name = r.Name, requirementRevisionId = r.RequirementRevisionId, childCount = r.Children.Count }),
            implementations = protocol.States.Where(s => s.BlockId == selection.BlockId.ToString("D")).Select(Wire),
            implementationChanges = protocol.ImplementationChanges.Where(c => graph.States.Any(s => s.Id.ToString("D") == c.StateId && s.BlockId == selection.BlockId)).Select(Wire), connections
        });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_field_history", ReadOnly = true),
     Description("Read a bounded field-history page at an exact saved block revision, optionally for one exact connection/member in that local diagram. Field is General, Schematic or Routing. Returns original text, provenance, revision targets and total count; later unselected candidates do not appear as saved edits. An implementation made from another one (a duplicate, or a proposal that refined the block, connection or member) continues that implementation's history: after its own entries come the earlier implementation's entries, up to the revision it was made from, each with its own author, sources and linked inputs. Every entry names the implementation it was saved in (contextStateId, contextImplementation); its contextRevisionId and contextVersion belong to that implementation, which for such an entry is not the requested one. No draft, file or native selection is changed.")]
    public Task<CallToolResult> FieldHistory(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        string blockId, string stateId, string revisionId, string field, CancellationToken cancellationToken,
        int offset = 0, int limit = 50, string? connectionId = null, string? connectionStateId = null,
        string? connectionRevisionId = null, string? expectedSourceToken = null) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        var request = new RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion, Action = RecursiveFileAction.RfaBlockFieldHistory,
            RepositoryRoot = repositoryRoot, SourcePath = sourcePath, DocumentId = documentId, ExpectedSourceToken = expectedSourceToken ?? "",
            Block = new() { BlockId = Identity(blockId).ToString("D"), StateId = Identity(stateId).ToString("D"), RevisionId = Identity(revisionId).ToString("D") },
            Field = field switch { "General" => RequirementFieldKind.RfkGeneral, "Schematic" => RequirementFieldKind.RfkSchematic,
                "Routing" => RequirementFieldKind.RfkRouting, _ => throw new AutomationException("invalid_requirement_field", "Choose General, Schematic or Routing.") }, Offset = offset, Limit = limit };
        if (connectionId is not null || connectionStateId is not null || connectionRevisionId is not null)
        {
            if (connectionId is null || connectionStateId is null || connectionRevisionId is null)
                throw new AutomationException("incomplete_connection_target", "Provide the complete connection, implementation and revision target.");
            request.Action = RecursiveFileAction.RfaConnectionFieldHistory;
            request.Connection = new() { ConnectionId = Identity(connectionId).ToString("D"), StateId = Identity(connectionStateId).ToString("D"), RevisionId = Identity(connectionRevisionId).ToString("D") };
        }
        var result = await RecursiveEditorFiles.ExecuteAsync(request, cancellationToken);
        // Name the implementation each entry was saved in (an implementation made from another one continues its history),
        // read from the same saved file: the read is bound to the history's source token.
        var snapshot = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion,
            RepositoryRoot = repositoryRoot, SourcePath = sourcePath, DocumentId = documentId, ExpectedSourceToken = result.SourceToken }, cancellationToken);
        var saved = snapshot.Document.Graph;
        var contexts = new Dictionary<string, (string StateId, string Name)>();
        if (request.Action == RecursiveFileAction.RfaBlockFieldHistory)
        {
            var names = saved.States.ToDictionary(s => s.Id, s => s.Name);
            foreach (var revision in saved.Revisions.Where(r => r.Selection.BlockId == request.Block.BlockId))
                contexts[revision.Selection.RevisionId] = (revision.Selection.StateId, names[revision.Selection.StateId]);
        }
        else
        {
            var archive = saved.ConnectionArchives.Single(a => a.OwnerBlockId == request.Block.BlockId);
            var names = archive.States.ToDictionary(s => s.Id, s => s.Name);
            foreach (var revision in archive.Revisions.Where(r => r.Selection.ConnectionId == request.Connection.ConnectionId))
                contexts[revision.Selection.RevisionId] = (revision.Selection.StateId, names[revision.Selection.StateId]);
        }
        var history = JsonNode.Parse(JsonFormatter.Default.Format(result.History))!.AsObject();
        foreach (var entry in history["entries"]?.AsArray() ?? [])
        {
            var (contextState, implementation) = contexts[entry!["contextRevisionId"]!.GetValue<string>()];
            entry["contextStateId"] = contextState; entry["contextImplementation"] = implementation;
        }
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId,
            sourceToken = result.SourceToken, history });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    private static Guid Identity(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value
        ? id : throw new AutomationException("invalid_diagram_identity", "Specify exact canonical non-empty diagram UUIDs.");

    [McpServerTool(Name = "kicad_diagram_create"),
     KiCadCapability("structural-diagram", "compiled-mcp", "instance epoch, new diagram path, operation ID"),
     Description("Create a new system diagram file whose root block is only its caption (rootName, for example the product or board name), with one first implementation (\"Initial\" unless named). Requirement text, interfaces, blocks and connections are added later, when they are known. The project convention is <project>.system-diagram.xml next to the .kicad_pro file; kicad_diagram_discover reports that suggested path. The path must be an absolute .xml path inside the repository, in an existing folder, without filesystem links. An existing file is never overwritten: repeating the same operationId returns the diagram it created with created=false, and any other existing file is refused with diagram_file_exists; an unwritable folder is refused with diagram_file_read_only. Requires the observed native instance epoch. It opens no window, starts no agent and touches no schematic or PCB file.")]
    public Task<CallToolResult> CreateDiagram(string instanceId, string expectedInstanceEpoch, string repositoryRoot, string sourcePath,
        string rootName, Guid operationId, string actor, CancellationToken cancellationToken, string implementationName = "Initial") => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(actor))
            throw new AutomationException("invalid_diagram_create_request", "Provide a stable operation UUID and the agent's name.");
        var origin = new DiagramRevisionOriginData { Kind = DiagramActorKind.DakAgent, Actor = actor, Summary = "Create diagram",
            RecordedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };
        origin.InputIds.Add(operationId.ToString("D"));
        var result = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion,
            Action = RecursiveFileAction.RfaCreateDiagram, RepositoryRoot = repositoryRoot ?? "", SourcePath = sourcePath ?? "",
            Create = new() { OperationId = operationId.ToString("D"), RootName = rootName ?? "", ImplementationName = implementationName ?? "", Origin = origin } },
            cancellationToken);
        return Data(new { instanceId, instanceEpoch = session.Epoch, operationId, documentId = result.Document.DocumentId,
            sourcePath = result.Document.SourcePath, sourceToken = result.SourceToken, created = result.Created,
            selectedRoot = RecursiveBlockCodec.DecodeSelection(result.Document.Graph.SelectedRoot),
            storedSchemaVersion = (int)result.Document.StoredSchemaVersion });
    });

    [McpServerTool(Name = "kicad_diagram_discover", ReadOnly = true),
     KiCadCapability("structural-diagram", "compiled-mcp", "explicit project file"),
     Description("List the system diagrams that belong to one KiCad project, recognised by content among the project folder's top-level XML files: each with its path, status (DDS_READY, DDS_READ_ONLY, DDS_TOO_NEW, DDS_INVALID or DDS_UNREADABLE), document ID, stored format, root caption and source token. Also returns the repository root to create in, the suggested new diagram path <project>.system-diagram.xml and whether that path is taken, and whether the listing was truncated at 256 files. Other XML documents, including legacy flat structural diagrams, are not diagrams and are not listed. Never writes.")]
    public Task<CallToolResult> DiscoverDiagrams(string instanceId, string projectFile, CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        var result = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion,
            Action = RecursiveFileAction.RfaDiscoverDiagrams, Discover = new() { ProjectFile = projectFile ?? "" } }, cancellationToken);
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch,
            discovery = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(result.Discovery)) });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_open"),
     Description("Open the native per-level diagram editor for one exact diagram document and attached KiCad instance. The compiled companion validates XML before displaying it. The editor speaks diagram schema 2, so per-level layout, realizations, domains and directions are kept; a version 1 file is upgraded only by its first changed save. The returned ready/busy/error state is authoritative; opening is not proof of rendering, saving, or native electrical realization. Existing dirty windows are retained.")]
    public Task<CallToolResult> Open(string instanceId, string repositoryRoot, string sourcePath,
        string documentId, CancellationToken cancellationToken) => Execute(async () =>
    {
        var native = registry.Client(instanceId);
        var loaded = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest
        { SchemaVersion = RecursiveBlockCodec.SchemaVersion, RepositoryRoot = repositoryRoot, SourcePath = sourcePath, DocumentId = documentId }, cancellationToken);
        string executable = Environment.ProcessPath ?? throw new AutomationException("missing_companion", "The compiled companion path is unavailable.");
        string helper = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Assembly.GetEntryAssembly()!.Location : executable;
        var state = await native.InvokeAsync<OpenRecursiveDiagramEditor, RecursiveDiagramEditorState>(new()
        { SchemaVersion = RecursiveBlockCodec.NativeEditorSchemaVersion, DocumentId = documentId, RepositoryRoot = repositoryRoot, SourcePath = loaded.Document.SourcePath,
            ExpectedSourceToken = loaded.SourceToken, HelperPath = helper }, cancellationToken);
        if (state.DocumentId != documentId || state.SourcePath != loaded.Document.SourcePath)
            throw new AutomationException("recursive_diagram_target_mismatch", "The native window belongs to another diagram.");
        return Result(instanceId, state);
    });

    [McpServerTool(Name = "kicad_diagram_state", ReadOnly = true),
     Description("Inspect the exact open recursive diagram window, its current level, requirement draft, file token and pending/error state. Does not navigate, save, discard, restore, or start an AI request.")]
    public Task<CallToolResult> Read(string instanceId, string documentId, CancellationToken cancellationToken) => Execute(async () =>
    {
        if (!Guid.TryParseExact(documentId, "D", out var id) || id == Guid.Empty || id.ToString("D") != documentId)
            throw new AutomationException("invalid_diagram_identity", "Specify the exact canonical diagram document UUID.");
        var state = await registry.Client(instanceId).InvokeAsync<ReadRecursiveDiagramEditor, RecursiveDiagramEditorState>(new() { DocumentId = documentId }, cancellationToken);
        if (state.DocumentId != documentId) throw new AutomationException("recursive_diagram_target_mismatch", "The native window belongs to another diagram.");
        return Result(instanceId, state);
    });

    private static CallToolResult Result(string instanceId, RecursiveDiagramEditorState state)
    {
        var wire = JsonNode.Parse(JsonFormatter.Default.Format(state))!.AsObject();
        RecursiveBlockCodec.OmitFieldsBeyondSchema(state, wire, RecursiveBlockCodec.NativeEditorSchemaVersion);
        var data = JsonSerializer.SerializeToElement(new { instanceId, state = wire });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or IOException or UnauthorizedAccessException)
        {
            string code = error is AutomationException a ? a.Code : error is NativeApiException n ? "native_status_" + n.Status : "diagram_file_error";
            var data = JsonSerializer.SerializeToElement(new { code, message = error.Message,
                details = (error is AutomationException { Details: var causes } ? causes : []).Select(d => new { kind = d.Kind, scopeBlockId = d.ScopeBlockId,
                    objectId = d.ObjectId, message = d.Message }) });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }
}

public sealed record DiagramObservationViewport(double X, double Y, double Width, double Height);
public sealed record DiagramObservationView(string ViewId, uint PixelWidth, uint PixelHeight,
    BlockSelection? Selection = null, DiagramObservationViewport? Viewport = null);
