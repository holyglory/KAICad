using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Kiapi.Common.Commands;
using Kiapi.Common.Project;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>
/// Pure admission and receipt validation for the future checked project
/// netclass owner.  This contract deliberately does not contact KiCad and no
/// MCP mutation tool is registered until a native owner can provide the same
/// rollback and undo/redo guarantees.
/// </summary>
public static class CheckedProjectNetClassesContract
{
    private const int MaximumClasses = 4096;
    private const int MaximumRequestBytes = 4 * 1024 * 1024;
    private const int MaximumResultBytes = 4 * 1024 * 1024;
    private const int MinimumResultBytes = 64 * 1024;

    public static string NetClassesDigest(NetClassesResponse response)
    {
        if (response is null)
            throw Invalid("A netclass response is required to compute its digest.");

        ValidateClasses(response, "netclasses");
        var bytes = new List<byte>();
        foreach (NetClass netClass in response.NetClasses.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            byte[] name = Encoding.UTF8.GetBytes(netClass.Name);
            byte[] serialized = netClass.ToByteArray();
            bytes.AddRange(Encoding.UTF8.GetBytes(name.Length.ToString()));
            bytes.Add((byte)'\n');
            bytes.AddRange(name);
            bytes.AddRange(Encoding.UTF8.GetBytes(serialized.Length.ToString()));
            bytes.Add((byte)'\n');
            bytes.AddRange(serialized);
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    public static void ValidateRequest(CheckedProjectNetClassesTransaction request,
        string processEpoch, bool inspect = false)
    {
        if (request is null || request.Project is null || request.Document is null || request.ExpectedState is null)
            throw Invalid("A checked project netclass request requires project, PCB document and lifecycle state.");

        if (!Uuid(processEpoch) || request.ProcessEpoch != processEpoch || !Uuid(request.OperationId))
            throw Invalid("The checked project netclass request must identify one operation and native process epoch.");

        if (request.Project.Type != DocumentType.DoctypeProject
            || request.Project.Project is null || string.IsNullOrWhiteSpace(request.Project.Project.Name)
            || string.IsNullOrWhiteSpace(request.Project.Project.Path)
            || !Path.IsPathFullyQualified(request.Project.Project.Path))
            throw Invalid("The project descriptor must contain a name and absolute path.");

        var documentProject = request.Document.Project;
        if (request.Document.Type != DocumentType.DoctypePcb
            || string.IsNullOrWhiteSpace(request.Document.BoardFilename) || documentProject is null
            || !string.Equals(documentProject.Name, request.Project.Project.Name, StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(documentProject.Path),
                Path.GetFullPath(request.Project.Project.Path), StringComparison.Ordinal))
            throw Invalid("The checked project netclass request must target a PCB in the named project.");

        DocumentLifecycleState expected = request.ExpectedState;
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || expected.Document is null
            || !expected.Document.Equals(request.Document) || expected.ProcessEpoch != processEpoch
            || !expected.ProjectSettingsIncluded || expected.Revision is null
            || !Uuid(expected.NativeIdentity) || expected.Revision.Epoch != expected.NativeIdentity
            || !Digest(expected.StateSha256))
            throw Invalid("The checked project netclass request must match one exact PCB lifecycle observation.");

        if (!inspect && !CheckedSchematicContract.FileCoverage(expected))
            throw Invalid("The checked PCB state lacks complete unchanged file-baseline coverage.");

        if (!Digest(request.ExpectedNetClassesSha256))
            throw Invalid("expectedNetClassesSha256 must be a lowercase SHA-256 digest.");
        ValidateClasses(new NetClassesResponse { NetClasses = { request.NetClasses } }, "request");
        if (request.MergeMode is not (MapMergeMode.MmmMerge or MapMergeMode.MmmReplace))
            throw Invalid("mergeMode must be MMM_MERGE or MMM_REPLACE.");
        if (request.MaximumResultBytes != 0
            && (request.MaximumResultBytes < MinimumResultBytes || request.MaximumResultBytes > MaximumResultBytes))
            throw Invalid("The checked project netclass receipt allowance must be zero or between 64 KiB and 4 MiB.");
        if (request.CalculateSize() > MaximumRequestBytes)
            throw Invalid("The checked project netclass request exceeds its bounded 4 MiB size allowance.");
    }

    public static void ValidateResult(CheckedProjectNetClassesTransaction request,
        CheckedProjectNetClassesReceipt result, bool inspect)
    {
        if (request is null || result is null)
            throw Invalid("A checked project netclass request and receipt are both required.");

        ValidateRequest(request, request.ProcessEpoch, inspect: true);
        if (result.Project is null || !result.Project.Equals(request.Project)
            || result.Document is null || !result.Document.Equals(request.Document)
            || result.ProcessEpoch != request.ProcessEpoch || result.OperationId != request.OperationId
            || !Enum.IsDefined(result.State) || result.State == CheckedProjectNetClassesState.CpcsUnknown)
            throw Invalid("The native checked project netclass receipt does not identify the exact request.");

        if (result.State == CheckedProjectNetClassesState.CpcsNotFound)
        {
            if (!inspect || result.RequestVerified || result.Replayed || result.BeforeNetClasses is not null
                || result.AfterNetClasses is not null || result.ObservedBefore is not null
                || result.ObservedRollback is not null || result.ObservedAfter is not null
                || result.ErrorCode.Length != 0 || result.ErrorMessage.Length != 0)
                throw Invalid("A missing project netclass receipt is inspection-only and cannot claim a result.");
            return;
        }

        if (result.State == CheckedProjectNetClassesState.CpcsUnavailable)
        {
            if (result.RequestVerified || result.Replayed || result.BeforeNetClasses is not null
                || result.AfterNetClasses is not null || result.ObservedBefore is not null
                || result.ObservedRollback is not null || result.ObservedAfter is not null
                || result.DesignFilesSaved || result.ReloadVerified || result.UndoRedoSupported
                || !string.Equals(result.ErrorCode, "project_settings_checked_unavailable", StringComparison.Ordinal))
                throw Invalid("An unavailable project netclass owner cannot claim mutation or recovery evidence.");
            return;
        }

        if (!result.RequestVerified
            || (result.State != CheckedProjectNetClassesState.CpcsCompleted
                && (string.IsNullOrWhiteSpace(result.ErrorCode) || string.IsNullOrWhiteSpace(result.ErrorMessage))))
            throw Invalid("A native project netclass receipt must verify the request and explain failed outcomes.");

        ValidateSnapshot(result.BeforeNetClasses, result.BeforeNetClassesSha256, "before");
        if (result.AfterNetClasses is not null)
            ValidateSnapshot(result.AfterNetClasses, result.AfterNetClassesSha256, "after");
        if (result.ObservedBefore is not null && !result.ObservedBefore.Equals(request.ExpectedState))
            throw Invalid("The project netclass receipt contains a before-state for another revision.");

        switch (result.State)
        {
            case CheckedProjectNetClassesState.CpcsCompleted:
                if (result.ErrorCode.Length != 0 || result.ErrorMessage.Length != 0
                    || result.Phase != CheckedProjectNetClassesPhase.CpcpComplete
                    || result.RollbackState != CheckedProjectNetClassesRollbackState.CpcrsNotAttempted
                    || result.PostCommitState != CheckedProjectNetClassesPostCommitState.CpcpsConfirmed
                    || result.BeforeNetClasses is null || result.AfterNetClasses is null
                    || result.ObservedBefore is null || result.ObservedAfter is null
                    || result.ObservedRollback is not null || !result.UndoRedoSupported
                    || result.DesignFilesSaved || result.ReloadVerified)
                    throw Invalid("A completed project netclass receipt must prove an unsaved undoable before/after result.");
                ValidateStateAfter(result.ObservedAfter, request.ExpectedState);
                break;
            case CheckedProjectNetClassesState.CpcsRolledBack:
                if (result.Phase != CheckedProjectNetClassesPhase.CpcpRollbackConfirmed
                    || result.RollbackState != CheckedProjectNetClassesRollbackState.CpcrsConfirmed
                    || result.PostCommitState != CheckedProjectNetClassesPostCommitState.CpcpsNotAttempted
                    || result.BeforeNetClasses is null || result.ObservedBefore is null
                    || result.ObservedRollback is null || result.ObservedAfter is not null
                    || !string.Equals(result.BeforeNetClassesSha256, result.AfterNetClassesSha256, StringComparison.Ordinal))
                    throw Invalid("A rolled-back project netclass receipt must prove return to its before checkpoint.");
                if (!result.ObservedRollback.Equals(result.ObservedBefore))
                    throw Invalid("A rolled-back project netclass receipt must retain unchanged lifecycle identity.");
                break;
            case CheckedProjectNetClassesState.CpcsRejected:
                if (result.BeforeNetClasses is not null || result.AfterNetClasses is not null
                    || result.ObservedRollback is not null
                    || result.ObservedAfter is not null && result.ObservedBefore is not null
                        && !result.ObservedAfter.Equals(result.ObservedBefore))
                    throw Invalid("A rejected project netclass request cannot claim a staged or committed result.");
                break;
            case CheckedProjectNetClassesState.CpcsIndeterminate:
                if (result.ObservedAfter is not null)
                    ValidateStateAfter(result.ObservedAfter, request.ExpectedState);
                break;
            default:
                throw Invalid("The native checked project netclass receipt has an unsupported terminal state.");
        }
    }

    private static void ValidateSnapshot(NetClassesResponse? response, string digest, string name)
    {
        if (response is null) return;
        ValidateClasses(response, name);
        if (!Digest(digest) || !string.Equals(NetClassesDigest(response), digest, StringComparison.Ordinal))
            throw Invalid($"The project netclass {name} snapshot digest does not match its contents.");
    }

    private static void ValidateClasses(NetClassesResponse response, string name)
    {
        if (response.NetClasses.Count > MaximumClasses
            || response.NetClasses.Any(value => value is null || string.IsNullOrWhiteSpace(value.Name)
                || value.Name.Contains('\0') || value.Type != NetClassType.NctExplicit
                || value.Constituents.Count != 0)
            || response.NetClasses.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count()
                != response.NetClasses.Count)
            throw Invalid($"The project netclass {name} list must contain unique named explicit classes.");
    }

    private static void ValidateStateAfter(DocumentLifecycleState actual, DocumentLifecycleState expected)
    {
        if (actual.Document is null || !actual.Document.Equals(expected.Document)
            || actual.ProcessEpoch != expected.ProcessEpoch || actual.Scope != DocumentLifecycleScope.DlsPcb
            || !actual.ProjectSettingsIncluded || !Uuid(actual.NativeIdentity) || !Digest(actual.StateSha256)
            || !CheckedSchematicContract.FileCoverage(actual))
            throw Invalid("The project netclass receipt contains an after-state for another PCB or process.");
    }

    private static bool Uuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        && id.ToString("D") == value;

    private static bool Digest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static AutomationException Invalid(string message) =>
        new("invalid_checked_project_net_classes", message);
}
