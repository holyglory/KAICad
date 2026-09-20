using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record BlockProposalFileResult(RecursiveBlockFileSnapshot Snapshot, BlockProposalRecord Proposal,
    bool Added, bool ContextStillSelected);
public sealed record RetainedBlockProposal(int Version, string DesignPath, Guid DocumentId, string RequestSha256,
    BlockProposal Proposal);

/// <summary>Retain an agent's typed request before attempting diagram publication.
/// An identical stored request is retryable; it is not a completed write receipt.</summary>
public static class BlockProposalFiles
{
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static string Fingerprint(BlockProposal proposal)
    {
        try { return Hash(JsonSerializer.SerializeToUtf8Bytes(Normalize(proposal), Json)); }
        catch (InvalidOperationException error) { throw new AutomationException("invalid_block_proposal", error.Message); }
    }

    internal static BlockProposal Normalize(BlockProposal proposal)
    {
        if (proposal is null) throw new AutomationException("invalid_block_proposal", "A proposal is required.");
        if (proposal.BasePath.IsDefault || proposal.Blocks.IsDefault || proposal.Connections.IsDefault || proposal.Issues.IsDefault)
            throw new AutomationException("invalid_block_proposal", "Proposal collections must be explicitly supplied, including empty issues or connections.");
        var blocks = proposal.Blocks.Select(block => block with
        {
            Children = block.Children.IsDefault ? [] : block.Children,
            Diagram = block.Diagram with
            {
                Interfaces = block.Diagram.Interfaces.IsDefault ? [] : block.Diagram.Interfaces,
                Connections = block.Diagram.Connections.IsDefault ? [] : block.Diagram.Connections,
                Annotations = block.Diagram.Annotations.IsDefault ? [] : block.Diagram.Annotations
            }
        }).ToImmutableArray();
        var connections = proposal.Connections.Select(connection => connection with
        {
            Endpoints = connection.Endpoints.IsDefault ? [] : connection.Endpoints,
            Members = connection.Members.IsDefault ? [] : connection.Members
        }).ToImmutableArray();
        var issues = proposal.Issues.Select(issue => issue with { Sources = issue.Sources.IsDefault ? [] : issue.Sources }).ToImmutableArray();
        var origin = proposal.Origin with
        {
            Sources = proposal.Origin.Sources.IsDefault ? [] : proposal.Origin.Sources,
            InputIds = proposal.Origin.InputIds.IsDefault ? [] : proposal.Origin.InputIds
        };
        return proposal with { BasePath = proposal.BasePath, Blocks = blocks, Connections = connections, Issues = issues, Origin = origin };
    }

    public static async Task<BlockProposalFileResult> PublishAsync(string root, string path, Guid documentId,
        string expectedSourceToken, BlockProposal proposal, string stateDirectory, CancellationToken token = default,
        Guid? operationId = null)
    {
        token.ThrowIfCancellationRequested();
        if (proposal?.Id is not { } id || id == Guid.Empty || documentId == Guid.Empty)
            throw new AutomationException("invalid_block_proposal", "Identify the exact proposal and target diagram.");
        proposal = Normalize(proposal);
        path = DiagramRequirementHistoryFiles.Contained(root, path, requireFile: true);
        string fingerprint = Fingerprint(proposal);
        Retain(new(1, path, documentId, fingerprint, proposal), stateDirectory);
        var receipts = operationId is { } ? new BlockProposalReceipts(stateDirectory) : null;
        var loaded = await RecursiveBlockFiles.Load(root, path, documentId, token);
        if (loaded.Snapshot.Graph.Proposals.SingleOrDefault(p => p.Id == id) is { } existing)
        {
            if (existing.RequestSha256 != fingerprint) throw new AutomationException("block_proposal_conflict", "Different candidate content already owns this proposal identity.");
            return new(loaded.Snapshot, existing, false,
                existing.BasePath.SequenceEqual(BlockProposalCompiler.FindPath(loaded.Snapshot.Graph, existing.BasePath[^1].BlockId)));
        }
        if (string.IsNullOrEmpty(expectedSourceToken) || loaded.Snapshot.ContentSha256 != expectedSourceToken)
            throw new AutomationException("block_proposal_source_changed", "The diagram changed. The typed proposal was retained in local state; read the latest diagram and compare before republishing.");
        var prepared = BlockProposalCompiler.Prepare(loaded.Snapshot.Graph, proposal);
        var record = new BlockProposalRecord(proposal.Id, proposal.InputId, fingerprint, proposal.BasePath, proposal.Candidate,
            proposal.Issues, prepared.Graph.Inspect(proposal.Candidate).Origin);
        var updated = prepared.Graph.WithProposal(record);
        byte[] replacement = Encoding.UTF8.GetBytes(RecursiveBlockGraphXml.Write(updated));
        BlockProposalPublicationReceipt? preparedReceipt = null;
        if (operationId is { } operation)
        {
            preparedReceipt = new(1, proposal.Id, operation, BlockProposalOperationKind.Publish, path, fingerprint,
                loaded.Snapshot.ContentSha256, Convert.ToHexStringLower(SHA256.HashData(replacement)), BlockProposalOperationStage.Prepared, null, null);
            receipts!.Write(preparedReceipt);
        }
        string? staged = null; string? retained = null;
        string hash = await DesignFilePublisher.WriteCoreAsync(path, loaded.Bytes, replacement, null, (target, temporary) =>
        {
            staged = temporary; retained = PreservingFileReplacement.PreviousPath(temporary);
            if (preparedReceipt is not null)
                receipts!.Write(preparedReceipt with { Stage = BlockProposalOperationStage.Replacing, StagedPath = staged, RetainedPath = retained });
            PreservingFileReplacement.Replace(target, temporary);
        }, token);
        if (preparedReceipt is not null)
            receipts!.Write(preparedReceipt with { Stage = BlockProposalOperationStage.Published, StagedPath = staged,
                RetainedPath = retained, ConfirmedAt = DateTimeOffset.UtcNow });
        return new(new(path, hash, updated), record, true, prepared.ContextStillSelected);
    }

    public static async Task<RecursiveBlockFileSnapshot> SelectAsync(string root, string path, Guid documentId, Guid proposalId,
        string expectedSourceToken, BlockSelection expectedRoot, ImmutableArray<BlockSelection> currentPath,
        ImmutableArray<Guid> ancestorIds, RequirementRevisionOrigin origin, CancellationToken token = default)
    {
        var loaded = await RecursiveBlockFiles.Load(root, path, documentId, token);
        if (string.IsNullOrEmpty(expectedSourceToken) || loaded.Snapshot.ContentSha256 != expectedSourceToken)
            throw new AutomationException("block_proposal_source_changed", "The diagram changed; retain the candidate and inspect the latest target before choosing it.");
        var selected = BlockProposalCompiler.Select(loaded.Snapshot.Graph, proposalId, expectedRoot, currentPath, ancestorIds, origin);
        token.ThrowIfCancellationRequested();
        if (!selected.Changed) return loaded.Snapshot;
        string hash = await DesignFilePublisher.WriteIfUnchangedAsync(loaded.Snapshot.Path, loaded.Bytes,
            Encoding.UTF8.GetBytes(RecursiveBlockGraphXml.Write(selected.Graph)), token);
        return new(loaded.Snapshot.Path, hash, selected.Graph);
    }

    public static RetainedBlockProposal ReadRetained(string stateDirectory, Guid proposalId)
    {
        string path = RetainedPath(stateDirectory, proposalId);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new AutomationException("invalid_block_proposal_state", "Retained proposals must be ordinary state files.");
        try
        {
            var result = JsonSerializer.Deserialize<RetainedBlockProposal>(File.ReadAllBytes(path), Json)
                ?? throw new AutomationException("invalid_block_proposal_state", "The retained request is empty.");
            if (result.Version != 1 || result.DocumentId == Guid.Empty || !Path.IsPathFullyQualified(result.DesignPath)
                || result.Proposal is null || result.Proposal.Id != proposalId || Fingerprint(result.Proposal) != result.RequestSha256)
                throw new AutomationException("invalid_block_proposal_state", "The retained proposal identity or content hash does not match.");
            return result;
        }
        catch (JsonException error) { throw new AutomationException("invalid_block_proposal_state", error.Message); }
    }

    private static void Retain(RetainedBlockProposal request, string stateDirectory)
    {
        string path = RetainedPath(stateDirectory, request.Proposal.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) != 0)
            throw new AutomationException("invalid_block_proposal_state", "Use the owned ordinary proposal state directory.");
        if (File.Exists(path)) { Same(); return; }
        string temporary = path + ".new-" + Guid.NewGuid().ToString("N"); bool created = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; output.Write(JsonSerializer.SerializeToUtf8Bytes(request, Json)); output.Flush(flushToDisk: true); }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { Same(); }
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        void Same()
        {
            var existing = ReadRetained(stateDirectory, request.Proposal.Id);
            if (existing.DesignPath != request.DesignPath || existing.DocumentId != request.DocumentId || existing.RequestSha256 != request.RequestSha256)
                throw new AutomationException("block_proposal_conflict", "This proposal identity already retains a different request; neither request was replaced.");
        }
    }

    private static string RetainedPath(string directory, Guid id)
    {
        if (!Path.IsPathFullyQualified(directory) || id == Guid.Empty)
            throw new AutomationException("invalid_block_proposal_state", "Use the existing absolute local state directory and exact proposal identity.");
        return Path.Combine(directory, "block-proposals", id.ToString("N") + ".json");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
