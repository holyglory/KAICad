using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record RecursiveBlockFileSnapshot(string Path, string ContentSha256, RecursiveBlockGraph Graph);

/// <summary>One guarded publication contains the root, all newly created ancestors and
/// requirement history. This is a persistence primitive, not native activation or an
/// independently advertised MCP mutation; the owning operation supplies its journal.</summary>
public static class RecursiveBlockFiles
{
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
        byte[] bytes = Encoding.UTF8.GetBytes(RecursiveBlockGraphXml.Write(saved.Graph));
        string hash = await DesignFilePublisher.WriteIfUnchangedAsync(loaded.Snapshot.Path, loaded.Bytes, bytes, token);
        return new(loaded.Snapshot.Path, hash, saved.Graph);
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
            if (RecursiveBlockGraphXml.Write(graph) != RecursiveBlockGraphXml.Write(loaded.Snapshot.Graph))
                throw new AutomationException("unselected_connection_change", "Select the changed connections in the block draft before saving them together.");
            return loaded.Snapshot;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(RecursiveBlockGraphXml.Write(saved.Graph));
        string hash = await DesignFilePublisher.WriteIfUnchangedAsync(loaded.Snapshot.Path, loaded.Bytes, bytes, token);
        return new(loaded.Snapshot.Path, hash, saved.Graph);
    }

    private static async Task<(RecursiveBlockFileSnapshot Snapshot, byte[] Bytes)> Load(string root, string path,
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
        var graph = RecursiveBlockGraphXml.Read(xml);
        if (graph.DocumentId != documentId)
            throw new AutomationException("recursive_document_identity_changed", "The file belongs to a different structural design; no file was changed.");
        return (new(source, Convert.ToHexStringLower(SHA256.HashData(bytes)), graph), bytes);
    }
}
