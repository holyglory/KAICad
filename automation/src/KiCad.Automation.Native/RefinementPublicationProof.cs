using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public enum RefinementPublicationStage { Prepared, Replacing, Published }
public enum PublicationFileStatus { Present, Missing, Unreadable }
public enum RefinementRecoveryDisposition { ResumePrepared, ConfirmPublication, CompletedPreviously, NeedsReview }
public sealed record PublicationFileObservation(string Path, PublicationFileStatus Status, string? Sha256);

/// <summary>Intended and confirmed states of one original-input publication, not
/// a design revision or a claim that the current design still has these bytes.</summary>
public sealed record RefinementPublicationIntent(int Version, string DesignPath, DiagramRefinementInput Input,
    string BeforeSha256, string AfterSha256, RefinementPublicationStage Stage, string? StagedPath,
    string? RetainedPath, DateTimeOffset? ConfirmedAt)
{
    public void Validate()
    {
        if (Input is null) throw Invalid("Retain the original input before attempting publication.");
        Input.Validate();
        if (Version != 1 || !Canonical(DesignPath) || !Digest(BeforeSha256) || !Digest(AfterSha256)
            || BeforeSha256 == AfterSha256 || Input.SourceSha256 != BeforeSha256 || !Enum.IsDefined(Stage))
            throw Invalid("A publication intent needs its exact original input, design path and distinct before/after hashes.");
        if (Stage == RefinementPublicationStage.Prepared)
        {
            if (StagedPath is not null || RetainedPath is not null || ConfirmedAt is not null)
                throw Invalid("An unattempted publication cannot claim replacement paths or confirmation.");
        }
        else if (!Canonical(StagedPath) || !Canonical(RetainedPath) || !StagedPath!.StartsWith(DesignPath + ".sync-", StringComparison.Ordinal)
            || Path.GetDirectoryName(StagedPath) != Path.GetDirectoryName(DesignPath)
            || (RetainedPath != StagedPath && RetainedPath != StagedPath + ".previous")
            || (Stage == RefinementPublicationStage.Published
                ? ConfirmedAt is null || ConfirmedAt == DateTimeOffset.MinValue || ConfirmedAt.Value.Offset != TimeSpan.Zero
                : ConfirmedAt is not null))
            throw Invalid("Replacement and confirmation must retain the actual publisher paths and explicit UTC acknowledgement.");
    }

    internal static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static bool Canonical(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try { return Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path; }
        catch (ArgumentException) { return false; }
    }
    internal static AutomationException Invalid(string message) => new("invalid_refinement_publication", message);
}

/// <summary>Decision only, no I/O or repair. Presence of an input in the current
/// graph is deliberately insufficient: an interrupted exchange needs its preimage
/// and postimage, including the different Unix-exchange and Windows-backup paths.</summary>
public static class RefinementPublicationProof
{
    public static RefinementRecoveryDisposition Inspect(RefinementPublicationIntent intent,
        PublicationFileObservation current, PublicationFileObservation? staged = null, PublicationFileObservation? retained = null)
    {
        intent.Validate(); Check(current, intent.DesignPath);
        if (intent.Stage == RefinementPublicationStage.Published) return RefinementRecoveryDisposition.CompletedPreviously;
        bool Current(string hash) => current.Status == PublicationFileStatus.Present && current.Sha256 == hash;
        if (intent.Stage == RefinementPublicationStage.Prepared)
            return Current(intent.BeforeSha256) ? RefinementRecoveryDisposition.ResumePrepared : RefinementRecoveryDisposition.NeedsReview;
        Check(staged, intent.StagedPath!); Check(retained, intent.RetainedPath!);
        if (intent.StagedPath == intent.RetainedPath && staged != retained)
            return RefinementRecoveryDisposition.NeedsReview; // inconsistent observation of one physical path
        if (Current(intent.AfterSha256) && retained!.Status == PublicationFileStatus.Present && retained.Sha256 == intent.BeforeSha256)
            return RefinementRecoveryDisposition.ConfirmPublication;
        if (Current(intent.BeforeSha256) && staged!.Status == PublicationFileStatus.Present && staged.Sha256 == intent.AfterSha256
            && (intent.StagedPath == intent.RetainedPath || retained!.Status == PublicationFileStatus.Missing))
            return RefinementRecoveryDisposition.ResumePrepared;
        return RefinementRecoveryDisposition.NeedsReview;
    }

    private static void Check(PublicationFileObservation? file, string expectedPath)
    {
        if (file is null || file.Path != expectedPath || !Enum.IsDefined(file.Status)
            || (file.Status == PublicationFileStatus.Present ? !RefinementPublicationIntent.Digest(file.Sha256) : file.Sha256 is not null))
            throw RefinementPublicationIntent.Invalid("Observe the exact named file, distinguishing missing from unreadable bytes.");
    }
}
