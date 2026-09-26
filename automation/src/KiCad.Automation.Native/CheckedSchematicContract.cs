using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>Shared checked-request/receipt validation for tools and durable recovery.
/// Does not contact native code or establish current-state admission.</summary>
public static class CheckedSchematicContract
{
    public static void ValidateObservation(CheckedSchematicState result, Kiapi.Common.Types.DocumentSpecifier document, string processEpoch)
    {
        var state = result.State; var electrical = result.Electrical;
        if (state?.Revision is null || electrical?.Hierarchy?.Data?.Document is null
            || document?.SheetPath?.Path.Count != 1 || (int)document.Type != 1
            || !Uuid(processEpoch) || state.ProcessEpoch != processEpoch
            || !Uuid(state.NativeIdentity) || !Uuid(state.Revision.Epoch)
            || !Equals(state.Document, document) || !Equals(electrical.Hierarchy.Data.Document, document)
            || !Equals(state.Revision, electrical.Hierarchy.Revision)
            || state.Scope != DocumentLifecycleScope.DlsSchematicHierarchy || !state.ProjectSettingsIncluded
            || state.StateSha256.Length != 64 || !state.StateSha256.All(char.IsAsciiHexDigitLower))
            throw Invalid("The combined observation must identify one exact native schematic checkpoint.");
        if (electrical.Hierarchy.Data.Instances.Any(screen => screen.Metadata?.UnrepresentedState.Any(
                limitation => limitation.Contains("_snapshot_schema_", StringComparison.Ordinal)) == true))
            throw Invalid("The native reader returned a legacy-projected snapshot; use the matching current-schema KiCad build.");
    }

    public static void ValidateRequest(CheckedSchematicBatch request, string processEpoch)
    {
        var batch = request.Batch; var expected = request.ExpectedState;
        if (batch is null || expected is null || batch.ExpectedRevision is null)
            throw Invalid("A batch and full expected native state are required.");
        var document = batch.Document;
        if (document?.SheetPath is null || document.SheetPath.Path.Count == 0
            || document.SheetPath.Path.Any(id => !Uuid(id.Value)) || document.Project is null
            || string.IsNullOrWhiteSpace(document.Project.Name) || !Path.IsPathFullyQualified(document.Project.Path))
            throw Invalid("An explicit schematic sheet and project are required.");
        if ((int)batch.Document.Type != 1 || batch.Operations.Count == 0 || !Uuid(batch.OperationId)
            || !Uuid(batch.DocumentEpoch) || !Uuid(expected.NativeIdentity) || expected.ProcessEpoch != processEpoch
            || !Uuid(processEpoch) || expected.Scope != DocumentLifecycleScope.DlsSchematicHierarchy
            || !expected.ProjectSettingsIncluded || expected.StateSha256.Length != 64
            || !expected.StateSha256.All(char.IsAsciiHexDigitLower)
            || !Equals(batch.Document, expected.Document) || !Equals(batch.ExpectedRevision, expected.Revision)
            || expected.Revision.Epoch != batch.DocumentEpoch)
            throw Invalid("The checked batch must match an exact schematic observation from this process.");
    }

    public static void ValidateResult(CheckedSchematicBatch request, CheckedSchematicBatchReceipt result, bool inspect)
    {
        if (!Equals(result.Document, request.Batch.Document) || result.OperationId != request.Batch.OperationId
            || result.ProcessEpoch != request.ExpectedState.ProcessEpoch || !Enum.IsDefined(result.Status)
            || result.Status == CheckedSchematicBatchStatus.CsbsUnknown
            || (result.Status == CheckedSchematicBatchStatus.CsbsNotFound
                ? !inspect || result.ExpectedRequestVerified || result.Result is not null
                    || result.ObservedBefore is not null || result.ObservedAfter is not null
                    || result.ErrorCode.Length != 0 || result.ErrorMessage.Length != 0
                : !result.ExpectedRequestVerified))
            throw Invalid("The native checked receipt does not identify this exact request.");
        if (result.Status == CheckedSchematicBatchStatus.CsbsCompleted
            && (result.Result?.Revision is null || result.ObservedAfter?.Revision is null
                || !Equals(result.ObservedBefore, request.ExpectedState)
                || !Equals(result.Result.Revision, result.ObservedAfter.Revision)
                || result.Result.Revision.Epoch != request.Batch.DocumentEpoch
                || result.Result.Revision.Sequence < request.Batch.ExpectedRevision.Sequence
                || result.ObservedAfter.ProcessEpoch != request.ExpectedState.ProcessEpoch
                || result.ObservedAfter.NativeIdentity != IdentityAfter(request, result.Result)
                || !Equals(result.ObservedAfter.Document, request.Batch.Document)
                || result.ObservedAfter.StateSha256.Length != 64
                || !result.ObservedAfter.StateSha256.All(char.IsAsciiHexDigitLower)
                || !result.ObservedAfter.ProjectSettingsIncluded
                || result.ObservedAfter.Scope != DocumentLifecycleScope.DlsSchematicHierarchy
                || !FileCoverage(result.ObservedAfter)
                || result.ErrorCode.Length != 0 || result.ErrorMessage.Length != 0))
            throw Invalid("The completed checked receipt lacks consistent native before/after state.");
        if (result.Status == CheckedSchematicBatchStatus.CsbsRejected && result.Result is not null)
            throw Invalid("A rejected checked receipt cannot also claim a committed batch result.");
        if (result.Status == CheckedSchematicBatchStatus.CsbsRejected && result.ObservedAfter is not null
            && !Equals(result.ObservedBefore, result.ObservedAfter))
            throw Invalid("A rejected checked receipt cannot claim that changed native state was rolled back.");
        if (result.Status is CheckedSchematicBatchStatus.CsbsRejected or CheckedSchematicBatchStatus.CsbsIndeterminate
            && string.IsNullOrEmpty(result.ErrorCode))
            throw Invalid("A failed or indeterminate checked receipt must explain its failure.");
        // CN-1 §8.2: a pin-partition proof exists only for a batch that asserted one, and a
        // post-condition refusal is only credible as an unchanged rejection of such a batch.
        bool asserted = Asserts(request.Batch);
        if (result.Status == CheckedSchematicBatchStatus.CsbsCompleted && (result.Result?.ConnectivityAssertionVerified ?? false) != asserted)
            throw Invalid(asserted ? "A completed asserted batch must carry the native connectivity proof."
                : "A completed batch cannot claim a connectivity proof it did not request.");
        if (result.ErrorCode == SchematicConnectionErrors.ConnectivityPostconditionFailed
            && (result.Status != CheckedSchematicBatchStatus.CsbsRejected || !asserted || result.ObservedAfter is null
                || !result.ErrorMessage.StartsWith(SchematicConnectionErrors.ConnectivityPostconditionFailed + ":", StringComparison.Ordinal)))
            throw Invalid("Only an unchanged rejection of an asserted batch can report a failed connectivity post-condition.");
    }

    /// <summary>Whether <paramref name="batch"/> carries a CN-1 pin-partition assertion.</summary>
    public static bool Asserts(ApplySchematicItemBatch? batch) =>
        batch?.Operations.Any(operation => operation.OperationCase == SchematicItemOperation.OperationOneofCase.AssertConnectivity) == true;

    // The native identity is the root screen's. Only a rebuild's first operation changes it, to exactly the identity
    // the rebuilt root's saved file had (SchematicRebuild); every other batch keeps it.
    internal static string IdentityAfter(CheckedSchematicBatch request, SchematicItemBatchResult result) =>
        request.Batch.Operations.Count != 0 && request.Batch.Operations[0].RebuildScreenIdentity is { } identity && result.ScreenIdentityChanged
            ? identity.Value : request.ExpectedState.NativeIdentity;

    internal static bool FileCoverage(DocumentLifecycleState state)
    {
        var files = state.NativeFiles.ToHashSet(StringComparer.Ordinal);
        if (files.Count == 0 || files.Count != state.NativeFiles.Count || files.Count != state.FileBaselines.Count) return false;
        foreach (var file in state.FileBaselines)
        {
            if (!files.Remove(file.Path) || !Path.IsPathFullyQualified(file.Path)
                || file.Status != NativeFileBaselineStatus.NfbsUnchanged || !file.BaselineKnown || !file.CurrentKnown
                || file.BaselinePath != file.Path || file.BaselineExists != file.CurrentExists
                || file.BaselineSha256 != file.CurrentSha256 || file.BaselineBytes != file.CurrentBytes) return false;
            if (file.BaselineExists ? file.BaselineSha256.Length != 64 || !file.BaselineSha256.All(char.IsAsciiHexDigitLower)
                : file.BaselineSha256.Length != 0 || file.BaselineBytes != 0) return false;
        }
        return files.Count == 0;
    }

    /// <summary>Why <see cref="FileCoverage"/> refuses <paramref name="state"/>, for a person: each file KiCad saves this
    /// schematic to that no longer holds what KiCad loaded (changed, deleted, created where KiCad has saved nothing yet, or
    /// not comparable), and what makes synchronization possible again. Names files by their file name, or by their full
    /// path when two share a name.</summary>
    internal static string FileConflictMessage(DocumentLifecycleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        List<string> changed = [], deleted = [], created = [], unknown = [];
        var listed = state.NativeFiles.ToHashSet(StringComparer.Ordinal);
        foreach (var file in state.FileBaselines)
        {
            bool mine = listed.Remove(file.Path);
            bool clean = file.BaselineExists ? file.BaselineSha256.Length == 64 && file.BaselineSha256.All(char.IsAsciiHexDigitLower)
                : file.BaselineSha256.Length == 0 && file.BaselineBytes == 0;
            if (!mine || !Path.IsPathFullyQualified(file.Path) || file.BaselinePath != file.Path || !file.BaselineKnown || !file.CurrentKnown
                || file.Status is NativeFileBaselineStatus.NfbsWrongPath or NativeFileBaselineStatus.NfbsUnreadable or NativeFileBaselineStatus.NfbsUnknown
                || !clean)
                unknown.Add(file.Path);
            else if (file.BaselineExists && !file.CurrentExists) deleted.Add(file.Path);
            else if (!file.BaselineExists && file.CurrentExists) created.Add(file.Path);
            else if (file.Status != NativeFileBaselineStatus.NfbsUnchanged || file.BaselineSha256 != file.CurrentSha256
                || file.BaselineBytes != file.CurrentBytes) changed.Add(file.Path);
        }
        unknown.AddRange(listed.Order(StringComparer.Ordinal));
        var names = changed.Concat(deleted).Concat(created).Concat(unknown).Select(p => Path.GetFileName(p)).ToArray();
        string Name(string path) => names.Count(n => n == Path.GetFileName(path)) == 1 && Path.GetFileName(path).Length != 0 ? Path.GetFileName(path) : path;
        string Names(List<string> paths) => paths.Count == 1 ? Name(paths[0])
            : string.Join(", ", paths.SkipLast(1).Select(Name)) + " and " + Name(paths[^1]);
        static string It(int count) => count == 1 ? "it" : "them";
        var parts = new List<string>();
        if (changed.Count != 0) parts.Add(Names(changed) + " changed on disk after KiCad loaded " + It(changed.Count));
        if (deleted.Count != 0) parts.Add(Names(deleted) + (deleted.Count == 1 ? " was" : " were") + " deleted after KiCad loaded " + It(deleted.Count));
        if (created.Count != 0)
            parts.Add(Names(created) + (created.Count == 1 ? " was" : " were") + " created on disk where KiCad has not saved " + It(created.Count) + " yet");
        if (unknown.Count != 0) parts.Add("KiCad cannot compare " + Names(unknown) + " with what it loaded");
        if (parts.Count == 0) parts.Add("KiCad reports no file it saves this schematic to");
        int restorable = changed.Count + deleted.Count;
        // A reopened KiCad whose schematic files are lost has no root to reattach to until one is created.
        string reopen = "reopen the project in KiCad and reattach the recovery record (kicad_design_recovery_reattach; if its schematic "
            + "files are lost, first create the root with kicad_schematic_create)";
        string fix = unknown.Count != 0 || parts.Count == 1 && restorable + created.Count == 0 ? reopen
            : created.Count == 0 ? "restore " + It(restorable) + ", or " + reopen
            : restorable == 0 ? "remove " + It(created.Count) + ", or " + reopen
            : "restore the changed and deleted files and remove the created ones, or " + reopen;
        return string.Join("; ", parts) + "; " + fix + ", then plan again. Nothing was sent to KiCad and no file was written.";
    }

    private static bool Uuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;
    private static AutomationException Invalid(string message) => new("invalid_checked_batch", message);
}
