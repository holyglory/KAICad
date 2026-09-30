using System.Security.Cryptography;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public enum BlockProposalRecoveryDisposition { ResumePrepared, ConfirmPublication, CompletedPreviously, NeedsReview }
/// <summary>UpgradedFromSchemaVersion is 1 when a resumed publication is complete and its retained
/// preimage shows that it moved a version 1 diagram file to version 2 (contract rbg-v2 R4 and section 8);
/// tools report it next to the observation, so it is not repeated inside it.</summary>
public sealed record BlockProposalRecoveryObservation(BlockProposalPublicationReceipt Receipt,
    BlockProposalRecoveryDisposition Disposition, PublicationFileObservation Current,
    PublicationFileObservation? Staged, PublicationFileObservation? Retained,
    [property: System.Text.Json.Serialization.JsonIgnore] int UpgradedFromSchemaVersion = 0);

/// <summary>Recover a candidate XML publication only from its durable receipt and
/// exact file preimage/postimage evidence. Selection and proposal publication use
/// the same mechanism because both are guarded XML mutations.</summary>
public static class BlockProposalRecovery
{
    public static async Task<BlockProposalRecoveryObservation> InspectAsync(string repositoryRoot, string designPath,
        Guid documentId, Guid operationId, string stateDirectory, CancellationToken token = default)
    {
        var receipt = ReadReceipt(repositoryRoot, designPath, documentId, operationId, new(stateDirectory));
        return await Observe(repositoryRoot, receipt, token);
    }

    public static async Task<BlockProposalRecoveryObservation> ResumeAsync(string repositoryRoot, string designPath,
        Guid documentId, Guid operationId, string stateDirectory, CancellationToken token = default)
    {
        var observed = await ResumeCoreAsync(repositoryRoot, designPath, documentId, operationId, stateDirectory, token);
        return observed.Disposition != BlockProposalRecoveryDisposition.CompletedPreviously ? observed
            : observed with { UpgradedFromSchemaVersion = RecursiveBlockFiles.UpgradedFromRetained(observed.Retained, observed.Receipt.BeforeSha256,
                observed.Current, observed.Receipt.AfterSha256, observed.Receipt.CandidateXml) };
    }

    private static async Task<BlockProposalRecoveryObservation> ResumeCoreAsync(string repositoryRoot, string designPath,
        Guid documentId, Guid operationId, string stateDirectory, CancellationToken token)
    {
        var store = new BlockProposalReceipts(stateDirectory);
        using var operation = store.AcquireOperation(operationId);
        var receipt = ReadReceipt(repositoryRoot, designPath, documentId, operationId, store);
        if (receipt.Stage == BlockProposalOperationStage.Published)
            return await Observe(repositoryRoot, receipt, token);
        if (receipt.Version != 2 || receipt.CandidateXml is null || receipt.DocumentId != documentId)
            throw new AutomationException("block_proposal_recovery_unavailable", "This receipt lacks the exact candidate XML and document identity required for safe recovery.");
        var observed = await Observe(repositoryRoot, receipt, token);
        if (observed.Disposition == BlockProposalRecoveryDisposition.NeedsReview)
            return observed;
        if (observed.Disposition == BlockProposalRecoveryDisposition.ConfirmPublication)
        {
            var published = receipt with { Stage = BlockProposalOperationStage.Published, ConfirmedAt = DateTimeOffset.UtcNow };
            store.Write(published); return await Observe(repositoryRoot, published, CancellationToken.None);
        }
        if (observed.Disposition == BlockProposalRecoveryDisposition.ResumePrepared)
        {
            if (receipt.Stage == BlockProposalOperationStage.Replacing)
            {
                if (receipt.StagedPath is null) return observed;
                using var designWriter = new FileStream(designPath + ".sync.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                token.ThrowIfCancellationRequested();
                PreservingFileReplacement.Replace(designPath, receipt.StagedPath);
                var exchanged = await Observe(repositoryRoot, receipt, CancellationToken.None);
                if (exchanged.Disposition != BlockProposalRecoveryDisposition.ConfirmPublication) return exchanged;
                var completed = receipt with { Stage = BlockProposalOperationStage.Published, ConfirmedAt = DateTimeOffset.UtcNow };
                store.Write(completed); return await Observe(repositoryRoot, completed, CancellationToken.None);
            }
            byte[] before = await File.ReadAllBytesAsync(designPath, token);
            byte[] replacement = System.Text.Encoding.UTF8.GetBytes(receipt.CandidateXml);
            if (Convert.ToHexStringLower(SHA256.HashData(before)) != receipt.BeforeSha256)
                return await Observe(repositoryRoot, receipt, token);
            string? staged = null; string? retained = null;
            await DesignFilePublisher.WriteCoreAsync(designPath, before, replacement, null, (target, temporary) =>
            {
                staged = temporary; retained = PreservingFileReplacement.PreviousPath(temporary);
                store.Write(receipt with { Stage = BlockProposalOperationStage.Replacing, StagedPath = staged, RetainedPath = retained });
                PreservingFileReplacement.Replace(target, temporary);
            }, token);
            var complete = receipt with { Stage = BlockProposalOperationStage.Published, StagedPath = staged,
                RetainedPath = retained, ConfirmedAt = DateTimeOffset.UtcNow };
            store.Write(complete); return await Observe(repositoryRoot, complete, CancellationToken.None);
        }
        return observed;
    }

    private static BlockProposalPublicationReceipt ReadReceipt(string root, string designPath, Guid documentId,
        Guid operationId, BlockProposalReceipts store)
    {
        var receipt = store.Read(operationId) ?? throw new AutomationException("missing_block_proposal_receipt", "No proposal operation receipt exists.");
        if (receipt.DesignPath != designPath || receipt.DocumentId != documentId)
            throw new AutomationException("block_proposal_receipt_conflict", "The receipt belongs to another diagram or document identity.");
        _ = DiagramRequirementHistoryFiles.Contained(root, designPath, requireFile: false);
        return receipt;
    }

    private static async Task<BlockProposalRecoveryObservation> Observe(string root, BlockProposalPublicationReceipt receipt,
        CancellationToken token)
    {
        var current = await ReadFile(root, receipt.DesignPath, token);
        var staged = receipt.StagedPath is { } stage ? await ReadFile(root, stage, token) : null;
        var retained = receipt.RetainedPath == receipt.StagedPath ? staged
            : receipt.RetainedPath is { } prior ? await ReadFile(root, prior, token) : null;
        if (receipt.Stage == BlockProposalOperationStage.Published)
            return new(receipt, BlockProposalRecoveryDisposition.CompletedPreviously, current, staged, retained);
        bool Current(string hash) => current.Status == PublicationFileStatus.Present && current.Sha256 == hash;
        if (receipt.Stage == BlockProposalOperationStage.Prepared)
            return new(receipt, Current(receipt.BeforeSha256) ? BlockProposalRecoveryDisposition.ResumePrepared : BlockProposalRecoveryDisposition.NeedsReview,
                current, staged, retained);
        if (staged is null || retained is null)
            return new(receipt, BlockProposalRecoveryDisposition.NeedsReview, current, staged, retained);
        bool after = Current(receipt.AfterSha256); bool before = Current(receipt.BeforeSha256);
        bool retainedBefore = retained.Status == PublicationFileStatus.Present && retained.Sha256 == receipt.BeforeSha256;
        bool stagedAfter = staged.Status == PublicationFileStatus.Present && staged.Sha256 == receipt.AfterSha256;
        if (after && retainedBefore) return new(receipt, BlockProposalRecoveryDisposition.ConfirmPublication, current, staged, retained);
        if (before && stagedAfter) return new(receipt, BlockProposalRecoveryDisposition.ResumePrepared, current, staged, retained);
        return new(receipt, BlockProposalRecoveryDisposition.NeedsReview, current, staged, retained);
    }

    private static async Task<PublicationFileObservation> ReadFile(string root, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            _ = DiagramRequirementHistoryFiles.Contained(root, path, requireFile: true);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new(path, PublicationFileStatus.Present, Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token)));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new(path, PublicationFileStatus.Missing, null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AutomationException)
        { return new(path, PublicationFileStatus.Unreadable, null); }
    }
}
