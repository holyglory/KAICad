using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
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
