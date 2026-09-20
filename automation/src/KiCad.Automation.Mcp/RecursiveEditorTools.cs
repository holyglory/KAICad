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
     Description("Publish a complete typed block implementation proposal: nested blocks, connection groups/members and partial endpoints, all three requirement fields, definitions and unresolved issues. Requires an existing original input, current source hash and process epoch. Retains the request in local state before publication. Creates independent alternatives without changing any existing head or selected root. Invalid closures reject as a whole. Stale requests stay retrievable; an identical already-present candidate is an observation, not a receipt resolving an earlier ambiguous file write. This does not generate or activate native electrical designs.")]
    public Task<CallToolResult> PublishProposal(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, string expectedSourceToken, JsonElement proposalJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        BlockProposal proposal;
        try
        {
            proposal = JsonSerializer.Deserialize<BlockProposal>(proposalJson.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new AutomationException("invalid_block_proposal", "The proposal JSON is empty.");
            proposal = BlockProposalFiles.Normalize(proposal);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new AutomationException("invalid_block_proposal", error.Message); }
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        BlockProposalFileResult result;
        try
        {
            result = await BlockProposalFiles.PublishAsync(repositoryRoot, sourcePath, Identity(documentId), expectedSourceToken, proposal,
                registry.StateDirectory, cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException or NullReferenceException)
        { throw new AutomationException("invalid_block_proposal", error.Message); }
        return Data(new { instanceId, instanceEpoch = session.Epoch, documentId, sourceToken = result.Snapshot.ContentSha256,
            selectedRoot = result.Snapshot.Graph.SelectedRoot, result.Added, result.ContextStillSelected, proposal = result.Proposal });
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
     Description("Choose a published proposal for the exact current target block, updating its containing root snapshots together while preserving unrelated siblings. Requires the current source hash, process epoch and complete current root-to-block path; a changed target is rejected for comparison. This changes the conceptual diagram selection only, not native schematic/PCB activation. Supply fresh ancestor revision IDs and an operation ID; a completed selection retry must be inspected before another mutation.")]
    public Task<CallToolResult> SelectProposal(string instanceId, string expectedInstanceEpoch, string repositoryRoot,
        string sourcePath, string documentId, string expectedSourceToken, Guid proposalId, BlockSelection expectedRoot,
        BlockSelection[] currentPath, Guid[] ancestorRevisionIds, Guid operationId, string actor, CancellationToken cancellationToken) => Execute(async () =>
    {
        var path = currentPath?.ToImmutableArray() ?? []; var ancestors = ancestorRevisionIds?.ToImmutableArray() ?? [];
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("recursive_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        if (operationId == Guid.Empty) throw new AutomationException("invalid_operation_id", "Identify this conceptual selection operation.");
        var origin = new RequirementRevisionOrigin(RequirementRevisionActor.Agent, actor, DateTimeOffset.UtcNow, "Choose proposed implementation", [], [operationId]);
        var result = await BlockProposalFiles.SelectAsync(repositoryRoot, sourcePath, Identity(documentId), proposalId, expectedSourceToken,
            expectedRoot, path, ancestors, origin, cancellationToken);
        return Data(new { instanceId, instanceEpoch = session.Epoch, documentId, operationId, sourceToken = result.ContentSha256,
            selectedRoot = result.Graph.SelectedRoot, proposalId });
    });

    private static CallToolResult Data(object value)
    {
        var data = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
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
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId, inputId, inspection },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
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
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
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
        return new() { Content = [new TextContentBlock { Text = result.GetRawText() }], StructuredContent = result };
    });

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
        var result = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = 1, Action = RecursiveFileAction.RfaDiagramHistory,
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
        var result = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = 1, Action = RecursiveFileAction.RfaCompareDiagramHistory,
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
        var loaded = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = 1, RepositoryRoot = repositoryRoot,
            SourcePath = sourcePath, DocumentId = documentId, ExpectedSourceToken = expectedSourceToken }, cancellationToken);
        var graph = RecursiveBlockCodec.Decode(loaded.Document.Graph);
        var result = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = 1, Action = RecursiveFileAction.RfaPrepareDiagramRestoration,
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
        var result = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = 1, Action = RecursiveFileAction.RfaManageImplementation,
            RepositoryRoot = repositoryRoot, SourcePath = sourcePath, DocumentId = documentId, ExpectedSourceToken = expectedSourceToken, Implementation = management }, cancellationToken);
        var implementation = result.Document.Graph.States.Single(s => s.Id == result.ImplementationId);
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId, operationId,
            sourceToken = result.SourceToken, implementation = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(implementation)),
            selectedRoot = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(result.Document.Graph.SelectedRoot)) });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_diagram_read", ReadOnly = true),
     Description("Read one exact saved recursive diagram level as structured data: requirements, direct child selections, boundary interfaces, connection/member revisions, partial endpoints, comments and implementation metadata. Omitting block/state/revision reads the saved root; otherwise provide all three exact IDs. Returns the source token and native instance epoch. Does not return an unsaved window draft, activate an implementation, or claim schematic/PCB realization.")]
    public Task<CallToolResult> ReadSaved(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        CancellationToken cancellationToken, string? blockId = null, string? stateId = null, string? revisionId = null,
        string? expectedSourceToken = null) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        var loaded = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest { SchemaVersion = 1, RepositoryRoot = repositoryRoot,
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
            schemaVersion = 1, instanceId, instanceEpoch = session.Epoch, documentId,
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
     Description("Read a bounded field-history page at an exact saved block revision, optionally for one exact connection/member in that local diagram. Field is General, Schematic or Routing. Returns original text, provenance, revision targets and total count; later unselected candidates do not appear as saved edits. No draft, file or native selection is changed.")]
    public Task<CallToolResult> FieldHistory(string instanceId, string repositoryRoot, string sourcePath, string documentId,
        string blockId, string stateId, string revisionId, string field, CancellationToken cancellationToken,
        int offset = 0, int limit = 50, string? connectionId = null, string? connectionStateId = null,
        string? connectionRevisionId = null, string? expectedSourceToken = null) => Execute(async () =>
    {
        var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId) throw new AutomationException("recursive_instance_changed", "The native instance identity changed; reattach explicitly.");
        var request = new RecursiveFileRequest { SchemaVersion = 1, Action = RecursiveFileAction.RfaBlockFieldHistory,
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
        var data = JsonSerializer.SerializeToElement(new { instanceId, instanceEpoch = session.Epoch, documentId,
            sourceToken = result.SourceToken, history = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(result.History)) });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    private static Guid Identity(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value
        ? id : throw new AutomationException("invalid_diagram_identity", "Specify exact canonical non-empty diagram UUIDs.");

    [McpServerTool(Name = "kicad_diagram_open"),
     Description("Open a native recursive diagram editor for one exact diagram document and attached KiCad instance. The compiled companion validates XML before displaying it. The returned ready/busy/error state is authoritative; opening is not proof of rendering, saving, or native electrical realization. Existing dirty windows are retained.")]
    public Task<CallToolResult> Open(string instanceId, string repositoryRoot, string sourcePath,
        string documentId, CancellationToken cancellationToken) => Execute(async () =>
    {
        var native = registry.Client(instanceId);
        var loaded = await RecursiveEditorFiles.ExecuteAsync(new RecursiveFileRequest
        { SchemaVersion = 1, RepositoryRoot = repositoryRoot, SourcePath = sourcePath, DocumentId = documentId }, cancellationToken);
        string executable = Environment.ProcessPath ?? throw new AutomationException("missing_companion", "The compiled companion path is unavailable.");
        string helper = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Assembly.GetEntryAssembly()!.Location : executable;
        var state = await native.InvokeAsync<OpenRecursiveDiagramEditor, RecursiveDiagramEditorState>(new()
        { SchemaVersion = 1, DocumentId = documentId, RepositoryRoot = repositoryRoot, SourcePath = loaded.Document.SourcePath,
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
        var data = JsonSerializer.SerializeToElement(new { instanceId, state = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(state)) });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or IOException or UnauthorizedAccessException)
        {
            string code = error is AutomationException a ? a.Code : error is NativeApiException n ? "native_status_" + n.Status : "diagram_file_error";
            var data = JsonSerializer.SerializeToElement(new { code, message = error.Message });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }
}

public sealed record DiagramObservationViewport(double X, double Y, double Width, double Height);
public sealed record DiagramObservationView(string ViewId, uint PixelWidth, uint PixelHeight,
    BlockSelection? Selection = null, DiagramObservationViewport? Viewport = null);
