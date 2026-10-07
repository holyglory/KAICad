using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Validates the terminal response for a checked symbol-library mutation.
///
/// The native checked command owns the transaction and receipt.  This boundary
/// keeps the MCP result honest when an older native peer still reports an
/// unavailable capability, and rejects a response that claims a commit without
/// the document, epoch, operation and complete snapshot evidence that admitted
/// the request.
/// </summary>
internal static class SymbolLibraryCheckedContract
{
    private static readonly string[] TerminalStatuses =
        ["committed", "committed_postverified", "rejected", "unavailable", "rolled_back", "rolled_back_verified", "indeterminate"];

    internal static void ValidateResponse(
        NativeCapabilitySymbolLibraryCheckedWriteResponse response,
        DocumentSpecifier expectedDocument,
        string expectedProcessEpoch,
        string operationId,
        string expectedSnapshotSha256)
    {
        if (response is null || expectedDocument is null)
            throw Invalid("The checked native symbol response is missing its document identity.");

        if (!response.Document.Equals(expectedDocument))
            throw Invalid("The checked native symbol response changed the requested project or library document.");
        if (!string.Equals(response.ProcessEpoch, expectedProcessEpoch, StringComparison.Ordinal))
            throw new AutomationException("stale_process_epoch",
                "The checked native symbol response belongs to another process epoch.");
        if (!string.Equals(response.OperationId, operationId, StringComparison.Ordinal))
            throw new AutomationException("symbol_library_operation_mismatch",
                "The checked native symbol response belongs to another operation UUID.");

        string status = response.Status ?? string.Empty;
        if (!TerminalStatuses.Contains(status, StringComparer.Ordinal))
            throw Invalid("The checked native symbol response has an unknown or non-terminal status.");

        bool hasCode = !string.IsNullOrWhiteSpace(response.ErrorCode);
        bool hasMessage = !string.IsNullOrWhiteSpace(response.ErrorMessage);
        if (hasCode != hasMessage)
            throw Invalid("The checked native symbol response must carry both an error code and message, or neither.");

        bool committedStatus = status is "committed" or "committed_postverified";
        if (response.Committed != committedStatus)
            throw Invalid("The checked native symbol response status and committed flag disagree.");

        // New native peers attach the typed receipt envelope.  Older peers leave the enum at
        // UNKNOWN and are still handled as the historical unavailable/read-only seam below.
        if (response.ReceiptStatus != NativeCapabilitySymbolLibraryCheckedReceiptStatus.SlcwsUnknown)
        {
            if (response.Scope != "mcp_direct_symbol_file" || !response.ExpectedRequestVerified
                || response.CompleteChangeTracking || response.RequestFingerprintSha256.Length != 64
                || !response.RequestFingerprintSha256.All(char.IsAsciiHexDigitLower))
                throw Invalid("A checked symbol receipt must prove the direct-file scope, request identity and fingerprint.");
            if (response.Request is null || !response.Request.Document.Equals(expectedDocument)
                || response.Request.ProcessEpoch != expectedProcessEpoch || response.Request.OperationId != operationId
                || response.Request.ExpectedSnapshotSha256 != expectedSnapshotSha256)
                throw Invalid("A checked symbol receipt must retain the exact admitted request.");
        }

        if (response.Committed)
        {
            if (!response.RevisionTracked || !response.CompleteLifecycle || response.RollbackCompleted || hasCode)
                throw Invalid("A committed checked symbol response requires complete lifecycle and revision evidence without an error.");
            if (response.ReceiptStatus != NativeCapabilitySymbolLibraryCheckedReceiptStatus.SlcwsUnknown
                && (response.ReceiptStatus != NativeCapabilitySymbolLibraryCheckedReceiptStatus.SlcwsCommittedPostverified
                    || !response.NativeWriteStarted || !response.Postverified || !response.TargetVerified
                    || !response.UnrelatedEntriesVerified || response.RollbackAttempted
                    || response.ObservedBefore is null || response.ObservedAfter is null
                    || response.ObservedBefore.Revision is null || response.ObservedAfter.Revision is null
                    || response.ObservedAfter.Revision.Sequence <= response.ObservedBefore.Revision.Sequence))
                throw Invalid("A committed checked symbol receipt requires native write, target, unrelated-entry and advancing revision evidence.");
            RequireDigest(response.BeforeSnapshotSha256, "before snapshot");
            RequireDigest(response.AfterSnapshotSha256, "after snapshot");
            if (!string.Equals(response.BeforeSnapshotSha256, expectedSnapshotSha256, StringComparison.Ordinal))
                throw new AutomationException("stale_library_state",
                    "The native checked symbol commit was admitted from a different library snapshot.");
            return;
        }

        if (response.RevisionTracked || response.CompleteLifecycle)
            throw Invalid("An uncommitted checked symbol response cannot claim complete lifecycle evidence.");
        if (!hasCode)
            throw Invalid("An uncommitted checked symbol response must explain why the mutation did not commit.");

        if (status is "rolled_back" or "rolled_back_verified")
        {
            if (!response.RollbackCompleted)
                throw Invalid("A rolled-back checked symbol response must prove rollback completion.");
            if (response.ReceiptStatus != NativeCapabilitySymbolLibraryCheckedReceiptStatus.SlcwsUnknown
                && (response.ReceiptStatus != NativeCapabilitySymbolLibraryCheckedReceiptStatus.SlcwsRolledBackVerified
                    || !response.NativeWriteStarted || !response.RollbackAttempted
                    || response.ObservedRollback is null))
                throw Invalid("A rolled-back checked symbol receipt requires native write and rollback evidence.");
        }
        else if (response.RollbackCompleted)
            throw Invalid("Rollback completion is only valid for a rolled-back checked symbol response.");

        if (!string.IsNullOrEmpty(response.BeforeSnapshotSha256))
            RequireDigest(response.BeforeSnapshotSha256, "before snapshot");
        if (!string.IsNullOrEmpty(response.AfterSnapshotSha256))
            RequireDigest(response.AfterSnapshotSha256, "after snapshot");
    }

    private static void RequireDigest(string value, string name)
    {
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigitLower))
            throw Invalid($"The checked native symbol response has an invalid {name} digest.");
    }

    private static AutomationException Invalid(string message) =>
        new("invalid_symbol_library_response", message);
}
