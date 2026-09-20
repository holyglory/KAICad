using System.Collections.Immutable;
using Google.Protobuf;
using KiCad.Automation.Model;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Native;

/// <summary>Finite operations used by the native editor. Reads and history queries cannot
/// save or activate; the separate save action requires the exact file, root and draft baseline.</summary>
public static class RecursiveEditorFiles
{
    public static async Task<P.RecursiveFileResult> ExecuteAsync(P.RecursiveFileRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (request is null || request.SchemaVersion != 1 || !Enum.IsDefined(request.Action)
            || !request.Equals(P.RecursiveFileRequest.Parser.ParseJson(JsonFormatter.Default.Format(request))))
            throw Invalid("unsupported_diagram_file_request", "Use a supported typed recursive diagram request without unknown fields.");
        if (request.Action == P.RecursiveFileAction.RfaRead && (request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0)
            || request.Action == P.RecursiveFileAction.RfaBlockFieldHistory && request.Connection is not null
            || request.Action is not (P.RecursiveFileAction.RfaSaveBlock or P.RecursiveFileAction.RfaSaveImplementation) && request.Save is not null
            || request.Action != P.RecursiveFileAction.RfaRebaseRequirements && request.Rebase is not null
            || request.Action != P.RecursiveFileAction.RfaSaveConnection && request.SaveConnection is not null
            || request.Action != P.RecursiveFileAction.RfaManageImplementation && request.Implementation is not null)
            throw Invalid("ambiguous_diagram_file_request", "Use only the targets and paging fields belonging to the selected read operation.");
        Guid document = Id(request.DocumentId);
        if (request.Action == P.RecursiveFileAction.RfaManageImplementation)
        {
            if (request.Implementation is null || request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0)
                throw Invalid("invalid_implementation_request", "Provide the exact implementation management request without unrelated targets.");
            var managed = await ImplementationFiles.ApplyAsync(request.RepositoryRoot, request.SourcePath, document,
                request.ExpectedSourceToken, request.Implementation, token);
            var managedResult = Describe(managed.Snapshot); managedResult.ImplementationId = managed.StateId.ToString("D"); return managedResult;
        }
        if (request.Action == P.RecursiveFileAction.RfaSaveConnection)
        {
            if (request.SaveConnection is not { } save || save.Draft is null || save.Origin is null || save.ExpectedRoot is null
                || request.ExpectedSourceToken.Length != 64 || request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0)
                throw Invalid("invalid_connection_save_request", "Save needs the exact source token, root, block/member paths, connection draft and change origin.");
            var saved = await RecursiveBlockFiles.SaveConnectionAsync(request.RepositoryRoot, request.SourcePath, document, request.ExpectedSourceToken,
                RecursiveBlockCodec.DecodeSelection(save.ExpectedRoot), save.BlockPath.Select(RecursiveBlockCodec.DecodeSelection).ToImmutableArray(),
                save.ConnectionPath.Select(RecursiveBlockCodec.DecodeSelection).ToImmutableArray(), RecursiveBlockCodec.Decode(save.Draft, document),
                Id(save.NewConnectionRevisionId), Id(save.NewRequirementRevisionId), save.ConnectionAncestorRevisionIds.Select(Id).ToImmutableArray(),
                Id(save.NewBlockRevisionId), Id(save.NewBlockRequirementRevisionId), save.BlockAncestorRevisionIds.Select(Id).ToImmutableArray(),
                RecursiveBlockCodec.DecodeOrigin(save.Origin), token);
            return Describe(saved);
        }
        if (request.Action is P.RecursiveFileAction.RfaSaveBlock or P.RecursiveFileAction.RfaSaveImplementation)
        {
            if (request.Save is not { } save || save.Draft is null || save.Origin is null || save.ExpectedRoot is null
                || request.ExpectedSourceToken.Length != 64 || request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0)
                throw Invalid("invalid_diagram_save_request", "Save needs its exact source token, root, block path, draft and change origin.");
            if (request.Action == P.RecursiveFileAction.RfaSaveImplementation)
            {
                var selected = await RecursiveBlockFiles.SaveImplementationAsync(request.RepositoryRoot, request.SourcePath, document,
                    request.ExpectedSourceToken, RecursiveBlockCodec.DecodeSelection(save.ExpectedRoot),
                    save.BlockPath.Select(RecursiveBlockCodec.DecodeSelection).ToImmutableArray(), RecursiveBlockCodec.Decode(save.Draft, document),
                    Id(save.NewRevisionId), Id(save.NewRequirementRevisionId), save.AncestorRevisionIds.Select(Id).ToImmutableArray(),
                    RecursiveBlockCodec.DecodeOrigin(save.Origin), token);
                return Describe(selected);
            }
            var saved = await RecursiveBlockFiles.SaveDraftAsync(request.RepositoryRoot, request.SourcePath, document,
                request.ExpectedSourceToken, RecursiveBlockCodec.DecodeSelection(save.ExpectedRoot),
                save.BlockPath.Select(RecursiveBlockCodec.DecodeSelection).ToImmutableArray(), RecursiveBlockCodec.Decode(save.Draft, document),
                Id(save.NewRevisionId), Id(save.NewRequirementRevisionId), save.AncestorRevisionIds.Select(Id).ToImmutableArray(),
                RecursiveBlockCodec.DecodeOrigin(save.Origin), token: token);
            return Describe(saved);
        }
        var loaded = await RecursiveBlockFiles.ReadAsync(request.RepositoryRoot, request.SourcePath, document, token);
        if (request.Action == P.RecursiveFileAction.RfaRebaseRequirements)
        {
            if (request.Rebase?.Draft is null || request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0)
                throw Invalid("invalid_requirement_rebase_request", "Provide the retained requirement draft and any exact conflict resolutions.");
            if (request.Rebase.Resolutions.Count != 0 && request.ExpectedSourceToken != loaded.ContentSha256)
                throw Invalid("stale_requirement_resolution", "The saved design changed again; preserve the resolution and compare the latest version.");
            var draft = RecursiveBlockCodec.Decode(request.Rebase.Draft, document);
            var merge = RecursiveRequirementMerge.Prepare(loaded.Graph, draft);
            var comparison = Describe(loaded);
            comparison.Merge = RecursiveBlockCodec.Encode(merge, request.Rebase.Resolutions.Select(RecursiveBlockCodec.Decode));
            return comparison;
        }
        if (request.ExpectedSourceToken.Length != 0 && request.ExpectedSourceToken != loaded.ContentSha256)
            throw Invalid("recursive_block_file_changed", "The design file changed; retain the editing draft and reload its saved context.");
        var result = new P.RecursiveFileResult { Success = true, SourceToken = loaded.ContentSha256 };
        if (request.Action == P.RecursiveFileAction.RfaRead)
            return Describe(loaded);
        if (request.Block is null) throw Invalid("missing_diagram_target", "Select the exact block context for this history query.");
        var block = new BlockSelection(Id(request.Block.BlockId), Id(request.Block.StateId), Id(request.Block.RevisionId));
        var revision = loaded.Graph.Inspect(block);
        var field = (DiagramRequirementField)((int)request.Field - 1);
        if (request.Action == P.RecursiveFileAction.RfaBlockFieldHistory)
            result.History = RecursiveBlockCodec.Encode(DiagramFieldHistoryQuery.Block(loaded.Graph, block, field, request.Offset, request.Limit));
        else
        {
            if (request.Connection is null) throw Invalid("missing_connection_target", "Select an exact connection in this block's pinned diagram.");
            var connection = new ConnectionSelection(Id(request.Connection.ConnectionId), Id(request.Connection.StateId), Id(request.Connection.RevisionId));
            var archive = loaded.Graph.Connections(block.BlockId);
            if (!archive.Walk(revision.LocalDiagram.Connections).Contains(connection))
                throw Invalid("connection_not_in_diagram", "The requested connection is not part of this exact diagram revision.");
            result.History = RecursiveBlockCodec.Encode(DiagramFieldHistoryQuery.Connection(archive, connection, field, request.Offset, request.Limit));
        }
        return result;
    }

    private static P.RecursiveFileResult Describe(RecursiveBlockFileSnapshot loaded) => new()
    {
        Success = true, SourceToken = loaded.ContentSha256,
        Document = new() { SchemaVersion = 1, DocumentId = loaded.Graph.DocumentId.ToString("D"), SourcePath = loaded.Path,
            SourceToken = loaded.ContentSha256, Graph = RecursiveBlockCodec.Encode(loaded.Graph) }
    };

    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value
        ? id : throw Invalid("invalid_diagram_identity", "Diagram targets require canonical non-empty UUIDs.");
    private static AutomationException Invalid(string code, string message) => new(code, message);
}
