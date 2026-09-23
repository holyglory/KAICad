using System.Collections.Immutable;
using Google.Protobuf;
using KiCad.Automation.Model;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Native;

/// <summary>Finite operations used by the native editor. Reads and history queries cannot
/// save or activate; the separate save action requires the exact file, root and draft baseline.
/// Request, document and graph schema versions move together (contract rbg-v2 section 2.3):
/// schema 2 carries every implemented schema 2 fact; schema 1, which the native editor of this
/// build still speaks, works only on documents without schema 2 content and is refused otherwise.
/// That schema 1 acceptance is the interim bridge described at RecursiveBlockCodec.IsSupportedSchema.
/// Every changed write stores schema 2 (R4), also for a schema 1 exchange, whose schema 1 result
/// cannot carry the upgrade report.</summary>
public static class RecursiveEditorFiles
{
    public static async Task<P.RecursiveFileResult> ExecuteAsync(P.RecursiveFileRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // Actions 11-13 and their payloads (level draft, edit and rebase) are declared for contract rbg-v2
        // but not implemented yet: they fail closed here, before any file access, exactly like the
        // unknown values they were before the declaration. Flat-diagram conversion (actions 17 and 18 and
        // the migrate payload) is never implemented: legacy flat diagrams are discarded, not converted
        // (owner decision n9af098253fec71da). The move actions 14 and 15, create and discover (16 and 19)
        // and every schema 2 field of the diagram data are implemented for schema 2.
        if (request is null || !RecursiveBlockCodec.IsSupportedSchema(request.SchemaVersion) || !Enum.IsDefined(request.Action)
            || !Implemented(request.Action, request.SchemaVersion)
            || request.LevelEdit is not null || request.SaveLevel is not null || request.RebaseLevel is not null || request.Migrate is not null
            || (request.Create is not null || request.Discover is not null || request.Reparent is not null) && request.SchemaVersion < 2
            || RecursiveBlockCodec.CarriesFieldBeyondSchema(request, request.SchemaVersion)
            || !request.Equals(P.RecursiveFileRequest.Parser.ParseJson(JsonFormatter.Default.Format(request))))
            throw Invalid("unsupported_diagram_file_request", "Use a supported typed recursive diagram request without unknown fields.");
        uint schema = request.SchemaVersion;
        // Create and discover name no existing document (contract rbg-v2 section 7): they are dispatched
        // before any document identity is read.
        if (request.Action is P.RecursiveFileAction.RfaCreateDiagram or P.RecursiveFileAction.RfaDiscoverDiagrams)
            return await RecursiveDiagramFiles.ExecuteAsync(request, token);
        bool move = request.Action is P.RecursiveFileAction.RfaPrepareReparent or P.RecursiveFileAction.RfaReparentBlock;
        if (request.Action == P.RecursiveFileAction.RfaRead && (request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0)
            || request.Action == P.RecursiveFileAction.RfaBlockFieldHistory && request.Connection is not null
            || request.Action is not (P.RecursiveFileAction.RfaSaveBlock or P.RecursiveFileAction.RfaSaveImplementation) && request.Save is not null
            || request.Action != P.RecursiveFileAction.RfaRebaseRequirements && request.Rebase is not null
            || request.Action != P.RecursiveFileAction.RfaSaveConnection && request.SaveConnection is not null
            || request.Action != P.RecursiveFileAction.RfaManageImplementation && request.Implementation is not null
            || request.Action != P.RecursiveFileAction.RfaCompareDiagramHistory && request.InspectedBlock is not null
            || request.Action != P.RecursiveFileAction.RfaPrepareDiagramRestoration && request.Restoration is not null
            || !move && request.Reparent is not null
            || request.Create is not null || request.Discover is not null
            || move && (request.Reparent is null || request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0))
            throw Invalid("ambiguous_diagram_file_request", "Use only the targets and paging fields belonging to the selected read operation.");
        Guid document = Id(request.DocumentId);
        if (schema < 2 && request.ExpectedSourceToken.Length == 64 && request.Action is P.RecursiveFileAction.RfaSaveBlock
                or P.RecursiveFileAction.RfaSaveImplementation or P.RecursiveFileAction.RfaSaveConnection or P.RecursiveFileAction.RfaManageImplementation)
            await RequireSchemaOneFile(request, document, token);
        if (move)
        {
            if (request.ExpectedSourceToken.Length != 64)
                throw Invalid("invalid_reparent_request", "A move needs the exact observed file token its preview used.");
            var reparent = RecursiveBlockCodec.Decode(request.Reparent!);
            if (request.Action == P.RecursiveFileAction.RfaPrepareReparent)
            {
                var (observed, preview) = await RecursiveBlockFiles.PrepareReparentAsync(request.RepositoryRoot, request.SourcePath, document,
                    request.ExpectedSourceToken, reparent, token);
                var previewResult = new P.RecursiveFileResult { Success = true, SourceToken = observed.ContentSha256,
                    ReparentPreview = RecursiveBlockCodec.Encode(preview) };
                return previewResult;
            }
            var (moved, outcome, before) = await RecursiveBlockFiles.ReparentAsync(request.RepositoryRoot, request.SourcePath, document,
                request.ExpectedSourceToken, reparent, token);
            var movedResult = Describe(moved, schema);
            movedResult.SaveSummary = RecursiveBlockCodec.Summary(before, moved.Graph, outcome);
            return movedResult;
        }
        if (request.Action == P.RecursiveFileAction.RfaManageImplementation)
        {
            if (request.Implementation is null || request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0)
                throw Invalid("invalid_implementation_request", "Provide the exact implementation management request without unrelated targets.");
            var managed = await ImplementationFiles.ApplyAsync(request.RepositoryRoot, request.SourcePath, document,
                request.ExpectedSourceToken, request.Implementation, token);
            var managedResult = Describe(managed.Snapshot, schema); managedResult.ImplementationId = managed.StateId.ToString("D"); return managedResult;
        }
        if (request.Action == P.RecursiveFileAction.RfaSaveConnection)
        {
            if (request.SaveConnection is not { } save || save.Draft is null || save.Origin is null || save.ExpectedRoot is null
                || request.ExpectedSourceToken.Length != 64 || request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0)
                throw Invalid("invalid_connection_save_request", "Save needs the exact source token, root, block/member paths, connection draft and change origin.");
            var saved = await RecursiveBlockFiles.SaveConnectionAsync(request.RepositoryRoot, request.SourcePath, document, request.ExpectedSourceToken,
                RecursiveBlockCodec.DecodeSelection(save.ExpectedRoot), save.BlockPath.Select(RecursiveBlockCodec.DecodeSelection).ToImmutableArray(),
                save.ConnectionPath.Select(RecursiveBlockCodec.DecodeSelection).ToImmutableArray(), RecursiveBlockCodec.Decode(save.Draft, document, schema),
                Id(save.NewConnectionRevisionId), Id(save.NewRequirementRevisionId), save.ConnectionAncestorRevisionIds.Select(Id).ToImmutableArray(),
                Id(save.NewBlockRevisionId), Id(save.NewBlockRequirementRevisionId), save.BlockAncestorRevisionIds.Select(Id).ToImmutableArray(),
                RecursiveBlockCodec.DecodeOrigin(save.Origin), token);
            return Describe(saved, schema);
        }
        if (request.Action is P.RecursiveFileAction.RfaSaveBlock or P.RecursiveFileAction.RfaSaveImplementation)
        {
            if (request.Save is not { } save || save.Draft is null || save.Origin is null || save.ExpectedRoot is null
                || request.ExpectedSourceToken.Length != 64 || request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0)
                throw Invalid("invalid_diagram_save_request", "Save needs its exact source token, root, block path, draft and change origin.");
            var (saved, outcome, before) = request.Action == P.RecursiveFileAction.RfaSaveImplementation
                ? await RecursiveBlockFiles.SaveImplementationWithSummaryAsync(request.RepositoryRoot, request.SourcePath, document,
                    request.ExpectedSourceToken, RecursiveBlockCodec.DecodeSelection(save.ExpectedRoot),
                    save.BlockPath.Select(RecursiveBlockCodec.DecodeSelection).ToImmutableArray(), RecursiveBlockCodec.Decode(save.Draft, document, schema),
                    Id(save.NewRevisionId), Id(save.NewRequirementRevisionId), save.AncestorRevisionIds.Select(Id).ToImmutableArray(),
                    RecursiveBlockCodec.DecodeOrigin(save.Origin), token)
                : await RecursiveBlockFiles.SaveDraftWithSummaryAsync(request.RepositoryRoot, request.SourcePath, document,
                    request.ExpectedSourceToken, RecursiveBlockCodec.DecodeSelection(save.ExpectedRoot),
                    save.BlockPath.Select(RecursiveBlockCodec.DecodeSelection).ToImmutableArray(), RecursiveBlockCodec.Decode(save.Draft, document, schema),
                    Id(save.NewRevisionId), Id(save.NewRequirementRevisionId), save.AncestorRevisionIds.Select(Id).ToImmutableArray(),
                    RecursiveBlockCodec.DecodeOrigin(save.Origin), token: token);
            var savedResult = Describe(saved, schema);
            // The save summary is a schema 2 result field; a schema 1 exchange keeps its schema 1 shape.
            if (schema >= 2) savedResult.SaveSummary = RecursiveBlockCodec.Summary(before, saved.Graph, outcome);
            return savedResult;
        }
        var loaded = await RecursiveBlockFiles.ReadAsync(request.RepositoryRoot, request.SourcePath, document, token);
        if (schema < 2) RequireSchemaOneContent(loaded.Graph);
        if (request.Action == P.RecursiveFileAction.RfaRebaseRequirements)
        {
            if (request.Rebase?.Draft is null || request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0)
                throw Invalid("invalid_requirement_rebase_request", "Provide the retained requirement draft and any exact conflict resolutions.");
            if (request.Rebase.Resolutions.Count != 0 && request.ExpectedSourceToken != loaded.ContentSha256)
                throw Invalid("stale_requirement_resolution", "The saved design changed again; preserve the resolution and compare the latest version.");
            var draft = RecursiveBlockCodec.Decode(request.Rebase.Draft, document, schema);
            var merge = RecursiveRequirementMerge.Prepare(loaded.Graph, draft);
            var comparison = Describe(loaded, schema);
            comparison.Merge = RecursiveBlockCodec.Encode(merge, request.Rebase.Resolutions.Select(RecursiveBlockCodec.Decode));
            return comparison;
        }
        if (request.ExpectedSourceToken.Length != 0 && request.ExpectedSourceToken != loaded.ContentSha256)
            throw Invalid("recursive_block_file_changed", "The design file changed; retain the editing draft and reload its saved context.");
        var result = new P.RecursiveFileResult { Success = true, SourceToken = loaded.ContentSha256 };
        if (request.Action == P.RecursiveFileAction.RfaRead)
            return Describe(loaded, schema);
        if (request.Action == P.RecursiveFileAction.RfaPrepareDiagramRestoration)
        {
            if (request.Restoration?.Draft is null || request.Restoration.Source is null || request.ExpectedSourceToken.Length != 64
                || request.Block is not null || request.Connection is not null || request.Field != P.RequirementFieldKind.RfkUnknown
                || request.Offset != 0 || request.Limit != 0)
                throw Invalid("invalid_diagram_restoration_request", "Provide the retained clean draft, exact history source and observed file token; restoration only prepares a new draft.");
            var draft = RecursiveBlockCodec.Decode(request.Restoration.Draft, document, schema);
            var source = RecursiveBlockCodec.DecodeSelection(request.Restoration.Source);
            _ = DiagramHistoryQuery.Inspect(loaded.Graph, draft.Baseline, source);
            result.PreparedDraft = RecursiveBlockCodec.Encode(loaded.Graph.RestoreAsDraft(draft, source));
            return result;
        }
        if (request.Block is null) throw Invalid("missing_diagram_target", "Select the exact block context for this history query.");
        var block = new BlockSelection(Id(request.Block.BlockId), Id(request.Block.StateId), Id(request.Block.RevisionId));
        var revision = loaded.Graph.Inspect(block);
        if (request.Action is P.RecursiveFileAction.RfaDiagramHistory or P.RecursiveFileAction.RfaCompareDiagramHistory)
        {
            if (request.Connection is not null || request.Field != P.RequirementFieldKind.RfkUnknown
                || request.Action == P.RecursiveFileAction.RfaCompareDiagramHistory && (request.InspectedBlock is null || request.Offset != 0 || request.Limit != 0))
                throw Invalid("invalid_diagram_history_request", "Use the exact diagram context and only the page or comparison fields belonging to this operation.");
            if (request.Action == P.RecursiveFileAction.RfaDiagramHistory)
                result.DiagramHistory = RecursiveBlockCodec.Encode(DiagramHistoryQuery.Read(loaded.Graph, block, request.Offset, request.Limit));
            else
            {
                var inspected = request.InspectedBlock ?? throw Invalid("missing_diagram_history_revision", "Select the exact historical revision to compare.");
                result.DiagramComparison = RecursiveBlockCodec.Encode(DiagramHistoryQuery.Compare(loaded.Graph, block, RecursiveBlockCodec.DecodeSelection(inspected)));
            }
            return result;
        }
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

    private static bool Implemented(P.RecursiveFileAction action, uint schema) => action <= P.RecursiveFileAction.RfaPrepareDiagramRestoration
        || schema >= 2 && action is P.RecursiveFileAction.RfaPrepareReparent or P.RecursiveFileAction.RfaReparentBlock
            or P.RecursiveFileAction.RfaCreateDiagram or P.RecursiveFileAction.RfaDiscoverDiagrams;

    /// <summary>A schema 1 writer (the native editor of this build) would drop schema 2 facts it cannot
    /// represent; it may act only on the exact observed bytes, and only when they hold none.</summary>
    private static async Task RequireSchemaOneFile(P.RecursiveFileRequest request, Guid document, CancellationToken token)
    {
        var observed = await RecursiveBlockFiles.ReadAsync(request.RepositoryRoot, request.SourcePath, document, token);
        if (observed.ContentSha256 != request.ExpectedSourceToken)
            throw Invalid("recursive_block_file_changed", "The saved design changed; retain the draft and reload its saved context.");
        RequireSchemaOneContent(observed.Graph);
    }

    private static void RequireSchemaOneContent(RecursiveBlockGraph graph)
    {
        if (RecursiveBlockGraphXml.RequiredSchemaVersion(graph) > 1)
            throw Invalid("unsupported_diagram_file_request", "This diagram holds schema 2 content (per-level layout, realizations, domains, directions or harness targets); "
                + "open it with an editor that speaks diagram schema 2. Nothing was read into the editor or changed.");
    }

    internal static P.RecursiveFileResult Describe(RecursiveBlockFileSnapshot loaded, uint schema)
    {
        var result = new P.RecursiveFileResult
        {
            Success = true, SourceToken = loaded.ContentSha256,
            Document = new() { SchemaVersion = schema, DocumentId = loaded.Graph.DocumentId.ToString("D"), SourcePath = loaded.Path,
                SourceToken = loaded.ContentSha256, Graph = RecursiveBlockCodec.Encode(loaded.Graph, schema) }
        };
        if (schema >= 2)
        {
            result.Document.StoredSchemaVersion = checked((uint)loaded.StoredSchemaVersion);
            result.Document.SourceWritable = Writable(loaded.Path);
            result.UpgradedFromSchemaVersion = checked((uint)loaded.UpgradedFromSchemaVersion);
        }
        return result;
    }

    /// <summary>Opens the file for writing without truncating it and closes it at once: bytes and
    /// modification time stay unchanged. False means Save must be disabled with an explanation.</summary>
    internal static bool Writable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { return false; }
    }

    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value
        ? id : throw Invalid("invalid_diagram_identity", "Diagram targets require canonical non-empty UUIDs.");
    private static AutomationException Invalid(string code, string message) => new(code, message);
}
