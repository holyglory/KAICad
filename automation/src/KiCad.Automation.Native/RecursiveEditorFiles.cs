using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Native;

/// <summary>Finite operations used by the native editor. Reads and history queries cannot
/// save or activate; the separate save actions require the exact file, root and draft baseline.
/// Request, document and graph schema versions move together (contract rbg-v2 section 2.3): every
/// request is schema 2 and carries every implemented schema 2 fact; other versions are refused before
/// any file access. Every changed write stores schema 2 (R4).</summary>
public static class RecursiveEditorFiles
{
    /// <summary>Reads one helper request from the protobuf JSON that the native editor, the project manager or a script
    /// sends to <c>kicad-mcp --diagram-file</c>. Strict unknown-field rejection (contract rbg-v2 section 2.3) covers names
    /// too: a field or enum value this build does not declare, including one the protocol retired and reserves, is refused
    /// as <c>unsupported_diagram_file_request</c> before any file access. Malformed JSON text stays an ordinary command error.</summary>
    public static P.RecursiveFileRequest ParseRequest(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try { return P.RecursiveFileRequest.Parser.ParseJson(json); }
        catch (InvalidProtocolBufferException error)
        {
            throw RetiredName(json) is { } retired ? Retired($"'{retired}'")
                : Invalid("unsupported_diagram_file_request", "Use a supported typed recursive diagram request without unknown fields or values. "
                    + error.Message);
        }
    }

    public static async Task<P.RecursiveFileResult> ExecuteAsync(P.RecursiveFileRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // A retired action (the flat-diagram conversion's 17 and 18) is refused first, naming why, before any file access.
        if (request is not null && IsRetired(request.Action)) throw Retired($"Diagram file action {(int)request.Action}");
        if (request is null || !RecursiveBlockCodec.IsSupportedSchema(request.SchemaVersion) || !Enum.IsDefined(request.Action)
            || RecursiveBlockCodec.CarriesFieldBeyondSchema(request, request.SchemaVersion)
            || !request.Equals(P.RecursiveFileRequest.Parser.ParseJson(JsonFormatter.Default.Format(request))))
            throw Invalid("unsupported_diagram_file_request", "Use a supported typed recursive diagram request without unknown fields.");
        uint schema = request.SchemaVersion;
        // Create and discover name no existing document (contract rbg-v2 section 7): they are dispatched
        // before any document identity is read.
        if (request.Action is P.RecursiveFileAction.RfaCreateDiagram or P.RecursiveFileAction.RfaDiscoverDiagrams)
            return await RecursiveDiagramFiles.ExecuteAsync(request, token);
        bool move = request.Action is P.RecursiveFileAction.RfaPrepareReparent or P.RecursiveFileAction.RfaReparentBlock;
        bool level = request.Action is P.RecursiveFileAction.RfaPrepareLevelEdit or P.RecursiveFileAction.RfaSaveLevel or P.RecursiveFileAction.RfaRebaseLevel;
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
            || request.Action != P.RecursiveFileAction.RfaPrepareLevelEdit && request.LevelEdit is not null
            || request.Action != P.RecursiveFileAction.RfaSaveLevel && request.SaveLevel is not null
            || request.Action != P.RecursiveFileAction.RfaRebaseLevel && request.RebaseLevel is not null
            || level && (request.Block is not null || request.Connection is not null || request.Field != P.RequirementFieldKind.RfkUnknown
                || request.Offset != 0 || request.Limit != 0)
            || request.Create is not null || request.Discover is not null
            || move && (request.Reparent is null || request.Block is not null || request.Connection is not null
                || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0))
            throw Invalid("ambiguous_diagram_file_request", "Use only the targets and paging fields belonging to the selected read operation.");
        Guid document = Id(request.DocumentId);
        if (level) return await ExecuteLevelAsync(request, document, token);
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
            savedResult.SaveSummary = RecursiveBlockCodec.Summary(before, saved.Graph, outcome);
            return savedResult;
        }
        var loaded = await RecursiveBlockFiles.ReadAsync(request.RepositoryRoot, request.SourcePath, document, token);
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

    // What the protocol retired and reserves, read from the descriptors so diagram_revision_types.proto stays the only
    // record. Today that is the flat-diagram conversion: actions 17 and 18 and the migrate payload, retired unimplemented
    // because legacy flat diagrams are discarded, not converted (owner decision n9af098253fec71da).
    private static readonly Google.Protobuf.Reflection.FieldDescriptor ActionField =
        P.RecursiveFileRequest.Descriptor.FindFieldByNumber(P.RecursiveFileRequest.ActionFieldNumber);
    private static readonly Google.Protobuf.Reflection.DescriptorProto RequestShape = P.RecursiveFileRequest.Descriptor.ToProto();
    private static readonly Google.Protobuf.Reflection.EnumDescriptorProto ActionShape = ActionField.EnumType.ToProto();

    private static bool IsRetired(P.RecursiveFileAction action) =>
        ActionShape.ReservedRange.Any(range => (int)action >= range.Start && (int)action <= range.End); // Enum ranges are inclusive.

    /// <summary>The retired request field or action value an unparsable request names, if any.</summary>
    private static string? RetiredName(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (RequestShape.ReservedName.FirstOrDefault(name => property.NameEquals(name) || property.NameEquals(JsonName(name))) is { } field)
                    return field;
                if ((property.NameEquals(ActionField.JsonName) || property.NameEquals(ActionField.Name)) && property.Value.ValueKind == JsonValueKind.String
                    && ActionShape.ReservedName.Contains(property.Value.GetString()!))
                    return property.Value.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    // The protobuf JSON name of a field name: underscores dropped, each following letter upper-cased.
    private static string JsonName(string name)
    {
        var json = new StringBuilder(name.Length);
        bool upper = false;
        foreach (char c in name)
        {
            if (c == '_') { upper = true; continue; }
            json.Append(upper ? char.ToUpperInvariant(c) : c); upper = false;
        }
        return json.ToString();
    }

    private static AutomationException Retired(string what) => Invalid("unsupported_diagram_file_request",
        what + " was retired from the recursive diagram protocol (legacy flat diagrams are discarded, not converted), so this build does not "
        + "support it. Nothing was read or changed.");

    /// <summary>Actions 11-13 (contract rbg-v2 section 7): a removal is prepared against the exact observed
    /// file and never writes; a level save writes only when something changed; a level rebase compares the
    /// retained draft with the latest file and never writes. A request without its own payload, or a save or
    /// removal without the observed file token, is ambiguous (section 7 step 3; section 11 stable codes).</summary>
    private static async Task<P.RecursiveFileResult> ExecuteLevelAsync(P.RecursiveFileRequest request, Guid document, CancellationToken token)
    {
        if (request.Action == P.RecursiveFileAction.RfaSaveLevel)
        {
            if (request.SaveLevel is not { } save || request.ExpectedSourceToken.Length != 64)
                throw Invalid("ambiguous_diagram_file_request", "A level save needs its level save payload and the exact observed file token.");
            var (root, path, draft, ids, origin, resolutions, choose) = RecursiveBlockCodec.Decode(save, document);
            var (snapshot, saved) = await RecursiveBlockFiles.SaveLevelAsync(request.RepositoryRoot, request.SourcePath, document, request.ExpectedSourceToken,
                root, path, draft, ids, origin, resolutions, choose, token);
            var result = Describe(snapshot, request.SchemaVersion);
            result.SaveSummary = RecursiveBlockCodec.Summary(saved);
            return result;
        }
        var loaded = await RecursiveBlockFiles.ReadAsync(request.RepositoryRoot, request.SourcePath, document, token);
        if (request.Action == P.RecursiveFileAction.RfaPrepareLevelEdit)
        {
            if (request.LevelEdit is not { } edit || request.ExpectedSourceToken.Length != 64)
                throw Invalid("ambiguous_diagram_file_request", "A removal needs its removal payload and the exact observed file token.");
            if (request.ExpectedSourceToken != loaded.ContentSha256)
                throw Invalid("recursive_block_file_changed", "The design file changed; save or reload the level before removing anything.");
            var (root, path, draft, command) = RecursiveBlockCodec.Decode(edit, document);
            if (root != loaded.Graph.SelectedRoot)
                throw Invalid("stale_root_revision", "The selected design changed; reload it before removing anything.");
            var (next, effects) = RecursiveLevelEdits.Apply(loaded.Graph, path, draft, command);
            return new P.RecursiveFileResult { Success = true, SourceToken = loaded.ContentSha256, LevelEdit = RecursiveBlockCodec.Encode(next, effects) };
        }
        if (request.RebaseLevel is not { } rebase)
            throw Invalid("ambiguous_diagram_file_request", "A level rebase needs its rebase payload with the retained level draft.");
        var (retained, choices) = RecursiveBlockCodec.Decode(rebase, document);
        if (choices.Length != 0 && request.ExpectedSourceToken != loaded.ContentSha256)
            throw Invalid("stale_requirement_resolution", "The saved design changed again; preserve the resolution and compare the latest version.");
        var merge = RecursiveLevelMerge.Prepare(loaded.Graph, retained);
        var comparison = Describe(loaded, request.SchemaVersion);
        comparison.LevelMerge = RecursiveBlockCodec.Encode(merge, merge.Inspect(choices));
        return comparison;
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
