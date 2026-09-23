using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>StoredSchemaVersion is the on-disk format (1 or 2). UpgradedFromSchemaVersion is 1 when
/// the write that produced this snapshot moved a version 1 file to version 2, otherwise 0.</summary>
public sealed record RecursiveBlockFileSnapshot(string Path, string ContentSha256, RecursiveBlockGraph Graph,
    int StoredSchemaVersion = 1, int UpgradedFromSchemaVersion = 0);

/// <summary>One guarded publication contains the root, all newly created ancestors and
/// requirement history. This is a persistence primitive, not native activation or an
/// independently advertised MCP mutation; the owning operation supplies its journal.</summary>
public static class RecursiveBlockFiles
{
    public static async Task<RecursiveBlockFileSnapshot> SaveImplementationAsync(string repositoryRoot, string path,
        Guid documentId, string expectedContentSha256, BlockSelection expectedRoot, ImmutableArray<BlockSelection> blockPath,
        RecursiveBlockDraft draft, Guid revisionId, Guid requirementRevisionId, ImmutableArray<Guid> ancestorRevisionIds,
        RequirementRevisionOrigin origin, CancellationToken token = default) =>
        (await SaveImplementationWithSummaryAsync(repositoryRoot, path, documentId, expectedContentSha256, expectedRoot, blockPath, draft,
            revisionId, requirementRevisionId, ancestorRevisionIds, origin, token)).Snapshot;

    /// <summary>As <see cref="SaveImplementationAsync"/>, also returning what the save created and the
    /// exact graph it started from.</summary>
    public static async Task<(RecursiveBlockFileSnapshot Snapshot, RecursiveBlockSelectionResult Result, RecursiveBlockGraph Before)> SaveImplementationWithSummaryAsync(
        string repositoryRoot, string path, Guid documentId, string expectedContentSha256, BlockSelection expectedRoot,
        ImmutableArray<BlockSelection> blockPath, RecursiveBlockDraft draft, Guid revisionId, Guid requirementRevisionId,
        ImmutableArray<Guid> ancestorRevisionIds, RequirementRevisionOrigin origin, CancellationToken token = default)
    {
        var loaded = await Load(repositoryRoot, path, documentId, token);
        if (loaded.Snapshot.ContentSha256 != expectedContentSha256)
            throw new AutomationException("recursive_block_file_changed", "The saved design changed; retain the implementation draft for comparison.");
        token.ThrowIfCancellationRequested();
        var saved = loaded.Snapshot.Graph.SaveImplementationDraft(expectedRoot, blockPath, draft, revisionId,
            requirementRevisionId, ancestorRevisionIds, origin);
        if (!saved.Changed) return (loaded.Snapshot, saved, loaded.Snapshot.Graph);
        return (await PublishAsync(loaded, saved.Graph, token), saved, loaded.Snapshot.Graph);
    }

    public static async Task<RecursiveBlockFileSnapshot> SaveConnectionAsync(string repositoryRoot, string path,
        Guid documentId, string expectedContentSha256, BlockSelection expectedRoot, ImmutableArray<BlockSelection> blockPath,
        ImmutableArray<ConnectionSelection> connectionPath, DiagramConnectionDraft draft, Guid connectionRevisionId,
        Guid requirementRevisionId, ImmutableArray<Guid> connectionAncestorIds, Guid blockRevisionId, Guid blockRequirementRevisionId,
        ImmutableArray<Guid> blockAncestorIds, RequirementRevisionOrigin origin, CancellationToken token = default)
    {
        var loaded = await Load(repositoryRoot, path, documentId, token);
        if (loaded.Snapshot.ContentSha256 != expectedContentSha256)
            throw new AutomationException("recursive_block_file_changed", "The saved design changed; retain the connection draft and compare the latest version.");
        token.ThrowIfCancellationRequested();
        var saved = loaded.Snapshot.Graph.SaveConnectionDraft(expectedRoot, blockPath, connectionPath, draft,
            connectionRevisionId, requirementRevisionId, connectionAncestorIds, blockRevisionId, blockRequirementRevisionId, blockAncestorIds, origin);
        if (!saved.Changed) return loaded.Snapshot;
        return await PublishAsync(loaded, saved.Graph, token);
    }
    public static async Task<RecursiveBlockFileSnapshot> ReadAsync(string repositoryRoot, string path,
        Guid expectedDocumentId, CancellationToken token = default)
    {
        var loaded = await Load(repositoryRoot, path, expectedDocumentId, token);
        return loaded.Snapshot;
    }

    public static async Task<RecursiveBlockFileSnapshot> SaveDraftAsync(string repositoryRoot, string path,
        Guid expectedDocumentId, string expectedContentSha256, BlockSelection expectedRoot,
        ImmutableArray<BlockSelection> blockPath, RecursiveBlockDraft draft, Guid newRevisionId,
        Guid newRequirementRevisionId, ImmutableArray<Guid> ancestorRevisionIds, RequirementRevisionOrigin origin,
        IReadOnlyCollection<DiagramRequirementResolution>? resolutions = null,
        IReadOnlyCollection<DiagramConnectionArchive>? connectionArchives = null, CancellationToken token = default) =>
        (await SaveDraftWithSummaryAsync(repositoryRoot, path, expectedDocumentId, expectedContentSha256, expectedRoot, blockPath, draft,
            newRevisionId, newRequirementRevisionId, ancestorRevisionIds, origin, resolutions, connectionArchives, token)).Snapshot;

    /// <summary>As <see cref="SaveDraftAsync"/>, also returning what the save created (including the
    /// dormant layout entries it pruned) and the exact graph it started from.</summary>
    public static async Task<(RecursiveBlockFileSnapshot Snapshot, RecursiveBlockSelectionResult Result, RecursiveBlockGraph Before)> SaveDraftWithSummaryAsync(
        string repositoryRoot, string path, Guid expectedDocumentId, string expectedContentSha256, BlockSelection expectedRoot,
        ImmutableArray<BlockSelection> blockPath, RecursiveBlockDraft draft, Guid newRevisionId,
        Guid newRequirementRevisionId, ImmutableArray<Guid> ancestorRevisionIds, RequirementRevisionOrigin origin,
        IReadOnlyCollection<DiagramRequirementResolution>? resolutions = null,
        IReadOnlyCollection<DiagramConnectionArchive>? connectionArchives = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // Freeze caller-owned choices before any asynchronous file operation.
        var choices = resolutions?.ToImmutableArray();
        var archives = connectionArchives?.ToImmutableArray() ?? [];
        var loaded = await Load(repositoryRoot, path, expectedDocumentId, token);
        if (loaded.Snapshot.ContentSha256 != expectedContentSha256)
            throw new AutomationException("recursive_block_file_changed", "The saved design changed; retain your draft and compare it with the latest version.");
        token.ThrowIfCancellationRequested();
        var graph = loaded.Snapshot.Graph;
        if (draft?.Baseline is null)
            throw new AutomationException("invalid_block_draft", "Provide the exact saved block baseline and retained editing draft.");
        foreach (var archive in archives)
        {
            if (archive is null || archive.OwnerBlockId != draft.Baseline.BlockId)
                throw new AutomationException("wrong_connection_edit_scope", "Save connection changes in their exact owning block's draft.");
            graph = graph.WithConnections(archive);
        }
        var saved = graph.SaveDraft(expectedRoot, blockPath, draft, newRevisionId,
            newRequirementRevisionId, ancestorRevisionIds, origin, choices);
        if (!saved.Changed)
        {
            if (RecursiveBlockGraphXml.Write(graph, RecursiveBlockGraphXml.SchemaVersion)
                != RecursiveBlockGraphXml.Write(loaded.Snapshot.Graph, RecursiveBlockGraphXml.SchemaVersion))
                throw new AutomationException("unselected_connection_change", "Select the changed connections in the block draft before saving them together.");
            return (loaded.Snapshot, saved, loaded.Snapshot.Graph);
        }
        return (await PublishAsync(loaded, saved.Graph, token), saved, loaded.Snapshot.Graph);
    }

    /// <summary>Checks a move against the exact observed file; never writes (contract rbg-v2 action 14).</summary>
    public static async Task<(RecursiveBlockFileSnapshot Snapshot, ReparentPreview Preview)> PrepareReparentAsync(string repositoryRoot, string path,
        Guid documentId, string expectedContentSha256, ReparentRequest request, CancellationToken token = default)
    {
        var loaded = await Load(repositoryRoot, path, documentId, token);
        if (loaded.Snapshot.ContentSha256 != expectedContentSha256)
            throw new AutomationException("recursive_block_file_changed", "The design file changed; reload it before moving a block.");
        return (loaded.Snapshot, loaded.Snapshot.Graph.PrepareReparent(request));
    }

    /// <summary>Publishes a move as one guarded write protected by the same file token the preview
    /// used (contract rbg-v2 action 15).</summary>
    public static async Task<(RecursiveBlockFileSnapshot Snapshot, RecursiveBlockSelectionResult Result, RecursiveBlockGraph Before)> ReparentAsync(string repositoryRoot,
        string path, Guid documentId, string expectedContentSha256, ReparentRequest request, CancellationToken token = default)
    {
        var loaded = await Load(repositoryRoot, path, documentId, token);
        if (loaded.Snapshot.ContentSha256 != expectedContentSha256)
            throw new AutomationException("recursive_block_file_changed", "The design file changed; reload it and preview the move again.");
        token.ThrowIfCancellationRequested();
        var moved = loaded.Snapshot.Graph.Reparent(request);
        return (await PublishAsync(loaded, moved.Graph, token), moved, loaded.Snapshot.Graph);
    }

    /// <summary>The bytes of every changed write, for all writers of a diagram file (contract rbg-v2
    /// R4): always schema 2, so the first changed write of a version 1 file upgrades it and later
    /// writes keep version 2. Callers write nothing when content is unchanged, so an unchanged save
    /// leaves a version 1 file byte-identical. The upgrade adds no revision and no history row.</summary>
    internal static (byte[] Bytes, int SchemaVersion) Serialize(RecursiveBlockGraph graph) =>
        (Encoding.UTF8.GetBytes(RecursiveBlockGraphXml.Write(graph, RecursiveBlockGraphXml.SchemaVersion)), RecursiveBlockGraphXml.SchemaVersion);

    /// <summary>The snapshot of a changed write; it reports upgraded_from_schema_version = 1 when the
    /// loaded file was version 1.</summary>
    internal static RecursiveBlockFileSnapshot Published(RecursiveBlockFileSnapshot loaded, string path, string hash, RecursiveBlockGraph graph,
        int version) => new(path, hash, graph, version, version > loaded.StoredSchemaVersion ? loaded.StoredSchemaVersion : 0);

    private static async Task<RecursiveBlockFileSnapshot> PublishAsync((RecursiveBlockFileSnapshot Snapshot, byte[] Bytes) loaded,
        RecursiveBlockGraph graph, CancellationToken token)
    {
        var (bytes, version) = Serialize(graph);
        string hash = await DesignFilePublisher.WriteIfUnchangedAsync(loaded.Snapshot.Path, loaded.Bytes, bytes, token);
        return Published(loaded.Snapshot, loaded.Snapshot.Path, hash, graph, version);
    }

    internal static async Task<(RecursiveBlockFileSnapshot Snapshot, byte[] Bytes)> Load(string root, string path,
        Guid documentId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (documentId == Guid.Empty)
            throw new AutomationException("invalid_recursive_document_target", "Identify the exact structural design document.");
        string source = DiagramRequirementHistoryFiles.Contained(root, path, requireFile: true);
        byte[] bytes = await File.ReadAllBytesAsync(source, token);
        string xml;
        try { xml = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        { throw new AutomationException("invalid_recursive_block_encoding", "The structural design must be valid UTF-8; no file was changed."); }
        var (graph, version) = RecursiveBlockGraphXml.ReadVersioned(xml);
        if (graph.DocumentId != documentId)
            throw new AutomationException("recursive_document_identity_changed", "The file belongs to a different structural design; no file was changed.");
        return (new(source, Convert.ToHexStringLower(SHA256.HashData(bytes)), graph, version), bytes);
    }
}
