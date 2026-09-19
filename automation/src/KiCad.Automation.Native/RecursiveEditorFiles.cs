using Google.Protobuf;
using KiCad.Automation.Model;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Native;

/// <summary>Finite read-only operations used by the native editor. All targets are exact,
/// and the response carries the observed file token; history browsing cannot save or activate.</summary>
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
            || request.Action == P.RecursiveFileAction.RfaBlockFieldHistory && request.Connection is not null)
            throw Invalid("ambiguous_diagram_file_request", "Use only the targets and paging fields belonging to the selected read operation.");
        Guid document = Id(request.DocumentId);
        var loaded = await RecursiveBlockFiles.ReadAsync(request.RepositoryRoot, request.SourcePath, document, token);
        if (request.ExpectedSourceToken.Length != 0 && request.ExpectedSourceToken != loaded.ContentSha256)
            throw Invalid("recursive_block_file_changed", "The design file changed; retain the editing draft and reload its saved context.");
        var result = new P.RecursiveFileResult { Success = true, SourceToken = loaded.ContentSha256 };
        if (request.Action == P.RecursiveFileAction.RfaRead)
        {
            result.Document = new() { SchemaVersion = 1, DocumentId = document.ToString("D"), SourcePath = loaded.Path,
                SourceToken = loaded.ContentSha256, Graph = RecursiveBlockCodec.Encode(loaded.Graph) };
            return result;
        }
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

    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value
        ? id : throw Invalid("invalid_diagram_identity", "Diagram targets require canonical non-empty UUIDs.");
    private static AutomationException Invalid(string code, string message) => new(code, message);
}
