using System.Security.Cryptography;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

internal sealed record DesignPublicationResult(StoredDesignRecovery Recovery, string FileSha256,
    bool ReplacementPerformed, string? PreviousPath);

/// <summary>Resume the exact publication retained in design recovery. This is
/// not a native editor transaction and never advances the design baseline.</summary>
internal static class DesignPublicationCommitter
{
    internal static async Task<DesignPublicationResult> CommitAsync(DesignRecoveryStore store,
        string expectedRevisionToken, LifecycleOperationResult confirmedSave, CancellationToken token = default,
        Action<DesignPublicationPhase>? afterPhase = null, Action? afterReplacement = null,
        Action? beforeReplacement = null, Func<string, CancellationToken, Task>? checkpoint = null)
    {
        token.ThrowIfCancellationRequested();
        var saved = RequireRecord(store, expectedRevisionToken);
        var intent = saved.State.PendingPublication ?? throw Error("missing_design_publication", "No exact XML publication is recorded.");
        var request = saved.State.PendingNativeSave;
        if (request is null || confirmedSave.Status != LifecycleOperationStatus.LosSaved
            || !Equals(confirmedSave.Document, request.Document) || confirmedSave.OperationId != request.OperationId
            || confirmedSave.ProcessEpoch != request.ExpectedState.ProcessEpoch
            || confirmedSave.ObservedState is not { } after || !Equals(after.Document, request.Document)
            || after.ProcessEpoch != confirmedSave.ProcessEpoch || after.NativeIdentity != request.ExpectedState.NativeIdentity
            || after.Revision?.Epoch != request.ExpectedState.Revision.Epoch
            || after.Revision.Sequence < request.ExpectedState.Revision.Sequence || after.NativeContentDirty
            || !after.ProjectSettingsIncluded || !SavedAsPlanned(after, request.ExpectedState)
            || !CheckedSchematicContract.FileCoverage(after))
            throw Error("native_save_not_confirmed", "An exact successful native-save result is required before publishing XML.");
        if (intent.PreviousPath != PreservingFileReplacement.PreviousPath(intent.StagedPath))
            throw Error("publication_platform_changed", "Resume this publication on the operating system that owns its recorded paths.");
        if (!Same(saved.State.DesiredFileBytes, intent.ExpectedFileBytes)
            && !Same(saved.State.DesiredFileBytes, intent.CandidateFileBytes))
            throw Error("publication_desired_changed", "A newer desired XML version must be reconciled with the pending candidate.");

        // Same local ownership convention as the store; never unlink this inode.
        using var ownership = new FileStream(intent.DesignPath + ".sync.lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        RequireRecord(store, saved.RevisionToken);

        if (intent.Phase is DesignPublicationPhase.Published or DesignPublicationPhase.Converged)
            return await VerifyCompleted();

        byte[]? target = await ReadFile(intent.DesignPath, token);
        if (intent.Phase is DesignPublicationPhase.Prepared or DesignPublicationPhase.Staged)
        {
            if (Same(target, intent.CandidateFileBytes))
            {
                // Independent identical changes converge without exchanging again.
                SavePhase(DesignPublicationPhase.Converged);
                return await VerifyCompleted();
            }
            if (!Same(target, intent.ExpectedFileBytes))
                throw Error("publication_target_changed", "The XML changed before replacement; all recorded versions remain intact.");
            if (intent.PreviousPath != intent.StagedPath && await ReadFile(intent.PreviousPath, token) is not null)
                throw Error("publication_unexpected_backup", "A retained file exists before replacement admission; inspect it without overwriting it.");
            byte[]? staged = await ReadFile(intent.StagedPath, token);
            if (staged is null && intent.Phase == DesignPublicationPhase.Prepared)
            {
                await using var output = new FileStream(intent.StagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await output.WriteAsync(intent.CandidateFileBytes, token);
                await output.FlushAsync(token); output.Flush(flushToDisk: true);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(intent.StagedPath, File.GetUnixFileMode(intent.DesignPath));
            }
            else if (!Same(staged, intent.CandidateFileBytes))
                throw Error("publication_stage_changed", $"The recorded stage is missing or incomplete: {intent.StagedPath}. Preserve it for recovery.");
            token.ThrowIfCancellationRequested();
            if (intent.Phase == DesignPublicationPhase.Prepared)
            {
                SavePhase(DesignPublicationPhase.Staged);
                if (checkpoint is not null) await checkpoint("publication-staged", token);
            }
            SavePhase(DesignPublicationPhase.Attempting);
        }

        // Attempting is durable BEFORE the native call. Byte identity determines
        // whether it happened; blindly swapping on resume would undo publication.
        target = await ReadFile(intent.DesignPath, token);
        byte[]? previous = await ReadFile(intent.PreviousPath, token);
        byte[]? stage = intent.PreviousPath == intent.StagedPath ? previous : await ReadFile(intent.StagedPath, token);
        if (Same(target, intent.CandidateFileBytes) && MatchesPrevious(previous))
        {
            SavePhase(DesignPublicationPhase.Published);
            return await VerifyCompleted();
        }
        if (!Same(stage, intent.CandidateFileBytes))
            throw Error("publication_conflict_preserved", $"Inspect the retained version at {intent.PreviousPath}; replacement cannot be repeated safely.");
        RequireRecord(store, saved.RevisionToken);
        token.ThrowIfCancellationRequested();
        beforeReplacement?.Invoke();
        if (Same(target, intent.ExpectedFileBytes)
            && (intent.PreviousPath == intent.StagedPath || previous is null))
            PreservingFileReplacement.Replace(intent.DesignPath, intent.StagedPath);
        else if (OperatingSystem.IsWindows() && target is null && MatchesPrevious(previous))
            // ReplaceFileW can retain the original backup yet fail to install
            // the candidate. Complete only into an absent path, never overwrite.
            File.Move(intent.StagedPath, intent.DesignPath, overwrite: false);
        else
            throw Error("publication_conflict_preserved", $"XML or retained files changed; preserve {intent.DesignPath} and {intent.PreviousPath} for reconciliation.");
        afterReplacement?.Invoke();
        if (checkpoint is not null) await checkpoint("publication-replaced", CancellationToken.None);

        // Report/persist the known outcome even if cancellation follows the swap.
        if (!MatchesPrevious(await ReadFile(intent.PreviousPath, CancellationToken.None)))
            throw Error("publication_conflict_preserved", $"The displaced XML differs from the expected version and remains at {intent.PreviousPath}.");
        if (!Same(await ReadFile(intent.DesignPath, CancellationToken.None), intent.CandidateFileBytes))
            throw Error("publication_target_changed", "XML changed after replacement; do not advance the synchronized baseline.");
        SavePhase(DesignPublicationPhase.Published);
        return await VerifyCompleted();

        bool MatchesPrevious(byte[]? bytes) => Same(bytes, intent.ExpectedFileBytes) || Same(bytes, intent.CandidateFileBytes);
        void SavePhase(DesignPublicationPhase phase)
        {
            saved = store.Save(saved.State with { PendingPublication = intent with { Phase = phase } }, saved.RevisionToken);
            intent = saved.State.PendingPublication!;
            afterPhase?.Invoke(phase);
        }
        async Task<DesignPublicationResult> VerifyCompleted()
        {
            if (!Same(await ReadFile(intent.DesignPath, CancellationToken.None), intent.CandidateFileBytes)
                || (intent.Phase == DesignPublicationPhase.Published
                    && !MatchesPrevious(await ReadFile(intent.PreviousPath, CancellationToken.None))))
                throw Error("publication_target_changed", "The published XML or its retained predecessor changed; reconcile before completing synchronization.");
            RequireRecord(store, saved.RevisionToken);
            return new(saved, Convert.ToHexStringLower(SHA256.HashData(intent.CandidateFileBytes)),
                intent.Phase == DesignPublicationPhase.Published,
                intent.Phase == DesignPublicationPhase.Published ? intent.PreviousPath : null);
        }
    }

    /// <summary>Whether the saved state is the state observed before the save. KiCad's save also writes the project-file
    /// entries it derives from the schematic itself (the sheet list, the top-level sheet list, the root sheet's revision
    /// kept for IPC-2581 and the project file name). A project whose file KiCad has not written yet, or whose sheets were
    /// added or renamed since its last save, therefore changes <c>state_sha256</c> on its next save although KiCad saved
    /// exactly the planned design. That change, and no other, is recognized: the native save-stable digest, which leaves
    /// out exactly those entries, must be present before the save and unchanged by it. Without it (an editor that does
    /// not report it) the full digest must be unchanged.</summary>
    internal static bool SavedAsPlanned(DocumentLifecycleState after, DocumentLifecycleState expected) =>
        after.StateSha256 == expected.StateSha256
        || (IsDigest(expected.SaveStableStateSha256) && after.SaveStableStateSha256 == expected.SaveStableStateSha256);

    private static bool IsDigest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static StoredDesignRecovery RequireRecord(DesignRecoveryStore store, string token)
    {
        var saved = store.Read();
        return saved is not null && saved.RevisionToken == token ? saved : throw Error("design_recovery_changed", "Recovery changed; reload the exact publication record.");
    }

    private static async Task<byte[]?> ReadFile(string path, CancellationToken token)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw Error("invalid_publication_file", $"Publication requires a regular file: {path}.");
            return await File.ReadAllBytesAsync(path, token);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool Same(byte[]? a, byte[] b) => a is not null && a.AsSpan().SequenceEqual(b);
    private static AutomationException Error(string code, string message) => new(code, message);
}
