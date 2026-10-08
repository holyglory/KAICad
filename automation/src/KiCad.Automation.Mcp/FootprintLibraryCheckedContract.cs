using Google.Protobuf;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>Fail-closed validation for the checked direct footprint-file receipt.</summary>
internal static class FootprintLibraryCheckedContract
{
    private static readonly string[] TerminalStatuses =
        ["committed_postverified", "rejected", "unavailable", "rolled_back_verified", "indeterminate"];

    internal static void ValidateResponse(
        NativeCapabilityFootprintLibraryCheckedWriteResponse response,
        NativeCapabilityFootprintLibraryCheckedWrite request)
    {
        if (response is null || request is null || request.Document is null)
            throw Invalid("The checked native footprint response is missing its request identity.");
        if (!response.Document.Equals(request.Document))
            throw Invalid("The checked native footprint response changed the requested document.");
        if (response.ProcessEpoch != request.ProcessEpoch)
            throw new AutomationException("stale_process_epoch", "The checked native footprint response belongs to another process epoch.");
        if (response.OperationId != request.OperationId)
            throw new AutomationException("footprint_operation_mismatch", "The checked native footprint response belongs to another operation UUID.");
        if (!TerminalStatuses.Contains(response.Status, StringComparer.Ordinal))
            throw Invalid("The checked native footprint response has an unknown status.");
        if (string.IsNullOrWhiteSpace(response.ErrorCode) != string.IsNullOrWhiteSpace(response.ErrorMessage))
            throw Invalid("A checked native footprint response must carry both an error code and message, or neither.");

        bool committed = response.Status == "committed_postverified";
        if (response.Committed != committed)
            throw Invalid("The checked native footprint status and committed flag disagree.");
        if (response.ReceiptStatus != NativeCapabilityFootprintLibraryCheckedReceiptStatus.NcpFlcsUnknown)
        {
            if (response.Scope != "mcp_direct_footprint_file" || !response.ExpectedRequestVerified
                || response.CompleteChangeTracking || response.Request is null
                || !response.Request.Equals(request) || response.RequestFingerprintSha256.Length != 64
                || !response.RequestFingerprintSha256.All(char.IsAsciiHexDigitLower))
                throw Invalid("The checked native footprint receipt must retain the exact request and direct-file scope.");
        }

        if (committed)
        {
            if (!response.RevisionTracked || response.CompleteLifecycle || !response.NativeWriteStarted
                || !response.Postverified || !response.TargetVerified || !response.UnrelatedEntriesVerified
                || response.RollbackAttempted || response.ObservedBefore is null || response.ObservedAfter is null
                || response.ObservedBefore.StateSha256 != request.ExpectedState.StateSha256
                || response.AfterSnapshotSha256 != response.ObservedAfter.StateSha256
                || response.ObservedAfter.Revision is null
                || response.ObservedAfter.Revision.Sequence <= response.ObservedBefore.Revision.Sequence)
                throw Invalid("A committed checked footprint receipt requires complete target, sibling and advancing revision evidence.");
            RequireDigest(response.BeforeSnapshotSha256, "before snapshot");
            RequireDigest(response.AfterSnapshotSha256, "after snapshot");
            if (response.BeforeSnapshotSha256 != request.ExpectedSnapshotSha256)
                throw new AutomationException("stale_library_state", "The native checked footprint commit used a different snapshot.");
            return;
        }

        if (response.RevisionTracked || response.CompleteLifecycle || string.IsNullOrWhiteSpace(response.ErrorCode))
            throw Invalid("An uncommitted checked footprint response must explain its refusal or unresolved outcome.");
        if (response.Status == "rolled_back_verified"
            && (response.ReceiptStatus != NativeCapabilityFootprintLibraryCheckedReceiptStatus.NcpFlcsRolledBackVerified
                || !response.NativeWriteStarted || !response.RollbackAttempted || !response.RollbackCompleted
                || response.ObservedRollback is null))
            throw Invalid("A rolled-back checked footprint receipt requires native rollback evidence.");
    }

    private static void RequireDigest(string value, string name)
    {
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigitLower))
            throw Invalid($"The checked native footprint response has an invalid {name} digest.");
    }

    private static AutomationException Invalid(string message) =>
        new("invalid_footprint_library_response", message);
}
