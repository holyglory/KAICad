using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Model;

/// <summary>Where a comment sits: on an element of the level (a block, a connection or the level's own block), or in
/// the free space of the level's canvas, where it may carry the original sketch strokes.</summary>
public enum RefinementCommentPlacement { Element, FreeSpace }

/// <summary>One block of the path from the root to the context's level, by exact revision.</summary>
public sealed record RefinementContextLevel(BlockSelection Selection, string Name);

/// <summary>A block as the context shows it: its exact block, implementation and revision, its three requirement fields
/// with the requirement revision they belong to, its boundary ports, its definition, component and physical choices, and
/// how much lies below it (read another level through its own context).</summary>
public sealed record RefinementContextBlock(BlockSelection Selection, string Name,
    Guid RequirementRevisionId, DiagramRequirements Requirements, ImmutableArray<DiagramBoundaryInterface> Interfaces,
    BlockDefinition Definition, BlockComponentBindings ComponentBindings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BlockPhysicalAllocation? PhysicalAllocation,
    int ChildCount, int ConnectionCount, RequirementRevisionOrigin Origin);

/// <summary>A connection or member of the context's level by exact revision, with its three requirement fields. A member
/// names the connection that groups it (ParentConnectionId); a connection of the level itself has none.</summary>
public sealed record RefinementContextConnection(ConnectionSelection Selection,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ParentConnectionId,
    string Name, DiagramConnectionKind Kind, DiagramDomain Domain, DiagramConnectionDirection Direction,
    ImmutableArray<DiagramEndpointBinding> Endpoints, ImmutableArray<ConnectionSelection> Members,
    Guid RequirementRevisionId, DiagramRequirements Requirements,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InterconnectRealization? Realization,
    RequirementRevisionOrigin Origin);

/// <summary>The name an implementation named by a context has today. Names can be changed and implementations removed from
/// the choices, so they are reported beside the context, not inside it.</summary>
public sealed record RefinementContextImplementation(Guid StateId, Guid OwnerId, string Name, bool Archived);

/// <summary>A comment of the context's level, exactly as saved with that level's revision.</summary>
public sealed record RefinementContextComment(RefinementCommentPlacement Placement, DiagramAnnotation Comment);

/// <summary>The original request the context belongs to: the prompt as the user gave it, who recorded it, the file token
/// it was captured at, the exact scope it names, and references to its preserved attachments.</summary>
public sealed record RefinementContextInput(Guid Id, string Prompt, RequirementRevisionOrigin Origin, string SourceSha256,
    ImmutableArray<BlockSelection> BlockPath, ImmutableArray<ConnectionSelection> ConnectionPath,
    ImmutableArray<DiagramRefinementAttachment> Attachments);

/// <summary>A provider-neutral, revision-bound context for an agent (ledger pa48933d0fe0a5c2f): one level of the recursive
/// diagram by exact revision, the path to it, its direct children and their boundary ports, its connections and members,
/// the three requirement fields of each, the level's element and free-space comments and its layout, and, when it was
/// asked for through an original input, that input's prompt and attachments. Everything is read from immutable saved
/// revisions, so the same context is returned unchanged after later edits, renames included; the fingerprint of a context
/// depends only on these contents.</summary>
public sealed record RefinementContext(int Version, Guid DocumentId, ImmutableArray<RefinementContextLevel> Path,
    ImmutableArray<ConnectionSelection> Focus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RefinementContextInput? Input,
    RefinementContextBlock Block, ImmutableArray<RefinementContextBlock> Children,
    ImmutableArray<RefinementContextConnection> Connections, ImmutableArray<RefinementContextComment> Comments,
    ImmutableArray<InterfaceRealization> InterfaceRealizations, DiagramPresentationView Layout)
{
    public const int CurrentVersion = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>SHA-256 of the context's JSON form: equal for the same revisions, input and contents.</summary>
    public string Fingerprint() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, Json)));
}

public static class RefinementContexts
{
    /// <summary>Build the context of one saved level. <paramref name="blockPath"/> runs from a revision of the root block to
    /// the level, each block pinned by the revision before it; it may be historical. With an original input the level is
    /// the input's own or one below it inside the input's pinned revisions, and the input's connection path is the focus
    /// when the level is the input's. Reads only.</summary>
    public static RefinementContext Build(RecursiveBlockGraph graph, ImmutableArray<BlockSelection> blockPath, DiagramRefinementInput? input = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (blockPath.IsDefaultOrEmpty || blockPath.Any(s => s is null || s.BlockId == Guid.Empty || s.StateId == Guid.Empty || s.RevisionId == Guid.Empty))
            throw new AutomationException("invalid_diagram_identity", "Name the level by the exact block, implementation and revision of every block from the root down.");
        if (blockPath[0].BlockId != graph.SelectedRoot.BlockId)
            throw Scope("The path starts at a revision of the diagram's root block.");
        for (int i = 0; i < blockPath.Length; ++i)
        {
            var revision = graph.Inspect(blockPath[i]);
            if (i + 1 < blockPath.Length && !revision.Children.Contains(blockPath[i + 1]))
                throw Scope("Each block of the path must be pinned, at that exact revision, by the revision before it.");
        }
        ImmutableArray<ConnectionSelection> focus = [];
        if (input is not null)
        {
            input.ValidateAgainst(graph);
            if (blockPath.Length < input.BlockPath.Length || !blockPath.Take(input.BlockPath.Length).SequenceEqual(input.BlockPath))
                throw Scope("With an original input, read the input's own level or a level below it inside the input's revisions.");
            if (blockPath.Length == input.BlockPath.Length) focus = input.ConnectionPath;
        }
        var level = blockPath[^1]; var scope = graph.Inspect(level);
        RefinementContextBlock Block(BlockSelection selection)
        {
            var revision = graph.Inspect(selection); var fields = graph.Requirements(selection);
            return new(selection, revision.Name, fields.RevisionId, fields.Requirements,
                revision.LocalDiagram.Interfaces, revision.EffectiveDefinition, revision.EffectiveComponentBindings,
                revision.PhysicalAllocation, revision.Children.Length, revision.LocalDiagram.Connections.Length, revision.Origin);
        }
        var connections = ImmutableArray.CreateBuilder<RefinementContextConnection>();
        if (!scope.LocalDiagram.Connections.IsEmpty)
        {
            var archive = graph.Connections(level.BlockId);
            void Add(ConnectionSelection selection, Guid? parent)
            {
                var revision = archive.Inspect(selection); var fields = archive.Requirements(selection);
                connections.Add(new(selection, parent, revision.Name, revision.Kind, revision.Domain, revision.Direction,
                    revision.Endpoints, revision.Members, fields.RevisionId, fields.Requirements, revision.Realization, revision.Origin));
                foreach (var member in revision.Members) Add(member, selection.ConnectionId);
            }
            foreach (var root in scope.LocalDiagram.Connections) Add(root, null);
        }
        var comments = scope.LocalDiagram.Notes.Select(n => new RefinementContextComment(
            n.Target.Kind == DiagramAnnotationTargetKind.Canvas ? RefinementCommentPlacement.FreeSpace : RefinementCommentPlacement.Element, n)).ToImmutableArray();
        var path = blockPath.Select(s => new RefinementContextLevel(s, graph.Inspect(s).Name)).ToImmutableArray();
        var original = input is null ? null : new RefinementContextInput(input.Id, input.Prompt, input.Origin, input.SourceSha256,
            input.BlockPath, input.ConnectionPath, input.Attachments);
        return new(RefinementContext.CurrentVersion, graph.DocumentId, path, focus, original, Block(level),
            [.. scope.Children.Select(Block)], connections.ToImmutable(), comments,
            [.. InterfaceRealization.CanonicalList(scope.LocalDiagram.Realizations)], graph.ActivePresentation(level).Canonical());
    }

    /// <summary>Today's name of every implementation the context names: its path, block, children, connections and members.</summary>
    public static ImmutableArray<RefinementContextImplementation> Implementations(RecursiveBlockGraph graph, RefinementContext context)
    {
        var blocks = graph.States.ToDictionary(s => s.Id);
        var result = ImmutableArray.CreateBuilder<RefinementContextImplementation>(); var seen = new HashSet<Guid>();
        foreach (var selection in context.Path.Select(p => p.Selection).Append(context.Block.Selection).Concat(context.Children.Select(c => c.Selection)))
            if (seen.Add(selection.StateId) && blocks[selection.StateId] is var state)
                result.Add(new(state.Id, state.BlockId, state.Name, state.Archived));
        if (!context.Connections.IsEmpty)
        {
            var links = graph.Connections(context.Block.Selection.BlockId).States.ToDictionary(s => s.Id);
            foreach (var connection in context.Connections)
                if (seen.Add(connection.Selection.StateId) && links[connection.Selection.StateId] is var state)
                    result.Add(new(state.Id, state.ConnectionId, state.Name, false));
        }
        return result.ToImmutable();
    }

    private static AutomationException Scope(string message) => new("invalid_agent_context_scope", message);
}
