using System.Security.Cryptography;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>UpgradedFromSchemaVersion is 1 when a resumed publication is complete and its retained
/// preimage shows that it moved a version 1 diagram file to version 2 (contract rbg-v2 R4 and section 8);
/// tools report it next to the observation, so it is not repeated inside it.</summary>
public sealed record RefinementInputRecoveryObservation(RefinementInputPublicationReceipt Receipt,
    RefinementRecoveryDisposition Disposition, PublicationFileObservation Current,
    PublicationFileObservation? Staged, PublicationFileObservation? Retained,
    [property: System.Text.Json.Serialization.JsonIgnore] int UpgradedFromSchemaVersion = 0);

/// <summary>Recover only an evidenced original-input publication. Native designs
/// are unaffected; conflicting or unreadable preimages remain for explicit review.</summary>
public static class RefinementInputRecovery
{
    public static async Task<RefinementInputRecoveryObservation> InspectAsync(string repositoryRoot, string designPath,
        Guid documentId, Guid inputId, string stateDirectory, CancellationToken token = default)
    {
        var receipt = ReadReceipt(repositoryRoot, designPath, documentId, inputId, new(stateDirectory));
        return await Observe(repositoryRoot, receipt, token);
    }

    public static async Task<RefinementInputRecoveryObservation> ResumeAsync(string repositoryRoot, string designPath,
        Guid documentId, Guid inputId, string stateDirectory, CancellationToken token = default)
    {
        var observed = await ResumeCoreAsync(repositoryRoot, designPath, documentId, inputId, stateDirectory, token);
        return observed.Disposition != RefinementRecoveryDisposition.CompletedPreviously ? observed
            : observed with { UpgradedFromSchemaVersion = RecursiveBlockFiles.UpgradedFromRetained(observed.Retained, observed.Receipt.BeforeSha256,
                observed.Current, observed.Receipt.AfterSha256) };
    }

    private static async Task<RefinementInputRecoveryObservation> ResumeCoreAsync(string repositoryRoot, string designPath,
        Guid documentId, Guid inputId, string stateDirectory, CancellationToken token)
    {
        var store = new RefinementInputReceipts(stateDirectory);
        // A prepared intent has not attempted replacement. The normal record path
        // performs its own locked, exact request/preimage checks when resuming it.
        var initial = ReadReceipt(repositoryRoot, designPath, documentId, inputId, store);
        if (initial.Stage == RefinementPublicationStage.Prepared)
        {
            var observed = await Observe(repositoryRoot, initial, token);
            if (observed.Disposition != RefinementRecoveryDisposition.ResumePrepared) return observed;
            await RefinementInputFiles.RecordAsync(repositoryRoot, designPath, documentId, initial.BeforeSha256,
                initial.Intent().Input, token, stateDirectory);
            return await InspectAsync(repositoryRoot, designPath, documentId, inputId, stateDirectory, token);
        }
        using var operation = store.Acquire(inputId);
        var receipt = ReadReceipt(repositoryRoot, designPath, documentId, inputId, store);
        if (receipt.Stage == RefinementPublicationStage.Published) return await Observe(repositoryRoot, receipt, token);
        using var designWriter = new FileStream(designPath + ".sync.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var state = await Observe(repositoryRoot, receipt, token);
        if (state.Disposition == RefinementRecoveryDisposition.NeedsReview) return state;
        foreach (var asset in receipt.Intent().Input.Attachments) await RefinementAssetFiles.RequireAvailable(repositoryRoot, asset, token);
        token.ThrowIfCancellationRequested();
        if (state.Disposition == RefinementRecoveryDisposition.ResumePrepared)
        {
            // Complete the original staged exchange; do not create another attempt
            // whose paths would erase the receipt's existing recovery evidence.
            PreservingFileReplacement.Replace(designPath, receipt.StagedPath!);
            state = await Observe(repositoryRoot, receipt, CancellationToken.None);
        }
        if (state.Disposition != RefinementRecoveryDisposition.ConfirmPublication) return state;
        var published = receipt with { Stage = RefinementPublicationStage.Published, ConfirmedAt = DateTimeOffset.UtcNow };
        store.Write(published);
        return await Observe(repositoryRoot, published, CancellationToken.None);
    }

    private static RefinementInputPublicationReceipt ReadReceipt(string root, string path, Guid documentId, Guid inputId, RefinementInputReceipts store)
    {
        var receipt = store.Read(inputId) ?? throw new AutomationException("missing_refinement_receipt", "No publication receipt exists for this exact input.");
        if (receipt.DesignPath != path || receipt.Intent().Input.DocumentId != documentId)
            throw new AutomationException("refinement_receipt_conflict", "The receipt belongs to another document or path.");
        _ = DiagramRequirementHistoryFiles.Contained(root, path, requireFile: false);
        return receipt;
    }

    private static async Task<RefinementInputRecoveryObservation> Observe(string root, RefinementInputPublicationReceipt receipt, CancellationToken token)
    {
        var current = await ReadFile(root, receipt.DesignPath, token);
        var staged = receipt.StagedPath is { } stage ? await ReadFile(root, stage, token) : null;
        var retained = receipt.RetainedPath == receipt.StagedPath ? staged
            : receipt.RetainedPath is { } previous ? await ReadFile(root, previous, token) : null;
        return new(receipt, RefinementPublicationProof.Inspect(receipt.Intent(), current, staged, retained), current, staged, retained);
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
