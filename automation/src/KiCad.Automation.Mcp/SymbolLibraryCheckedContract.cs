using System.Security.Cryptography;
using Google.Protobuf;
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

    // Compatibility helper for older managed contract fixtures and peers whose checked seam is
    // still the digest-only unavailable response. New production callers pass the full original
    // protobuf request so typed receipts can validate every field and its deterministic digest.
    internal static void ValidateResponse(
        NativeCapabilitySymbolLibraryCheckedWriteResponse response,
        DocumentSpecifier expectedDocument,
        string expectedProcessEpoch,
        string operationId,
        string expectedSnapshotSha256) => ValidateResponse(response, new()
        {
            Document = expectedDocument.Clone(), ProcessEpoch = expectedProcessEpoch,
            OperationId = operationId, ExpectedSnapshotSha256 = expectedSnapshotSha256
        });

    internal static void ValidateResponse(
        NativeCapabilitySymbolLibraryCheckedWriteResponse response,
        NativeCapabilitySymbolLibraryCheckedWrite expectedRequest)
    {
        if (response is null || expectedRequest is null || expectedRequest.Document is null)
            throw Invalid("The checked native symbol response is missing its document identity.");

        DocumentSpecifier expectedDocument = expectedRequest.Document;
        string expectedProcessEpoch = expectedRequest.ProcessEpoch;
        string operationId = expectedRequest.OperationId;
        string expectedSnapshotSha256 = expectedRequest.ExpectedSnapshotSha256;

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
        if (response.ReceiptStatus != NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsUnknown)
        {
            if (response.Scope != "mcp_direct_symbol_file" || !response.ExpectedRequestVerified
                || response.CompleteChangeTracking || response.RequestFingerprintSha256.Length != 64
                || !response.RequestFingerprintSha256.All(char.IsAsciiHexDigitLower))
                throw Invalid("A checked symbol receipt must prove the direct-file scope, request identity and fingerprint.");
            if (response.Request is null || !response.Request.Equals(expectedRequest)
                || response.RequestFingerprintSha256 != ComputeFingerprint(expectedRequest))
                throw Invalid("A checked symbol receipt must retain the exact admitted request.");
        }

        if (response.Committed)
        {
            bool typedDirectReceipt = response.ReceiptStatus != NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsUnknown;
            if (!response.RevisionTracked || (typedDirectReceipt ? response.CompleteLifecycle : !response.CompleteLifecycle)
                || response.RollbackCompleted || hasCode)
                throw Invalid("A committed checked symbol response requires complete lifecycle and revision evidence without an error.");
            if (response.ReceiptStatus != NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsUnknown
                && (response.ReceiptStatus != NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsCommittedPostverified
                    || response.CompleteLifecycle || !response.NativeWriteStarted || !response.Postverified
                    || !response.TargetVerified || !response.UnrelatedEntriesVerified || response.RollbackAttempted
                    || response.ObservedBefore is null || response.ObservedAfter is null
                    || !ValidateState(response.ObservedBefore, expectedRequest.ExpectedState)
                    || !ValidateState(response.ObservedAfter, expectedRequest.ExpectedState)
                    || !response.ObservedBefore.Equals(expectedRequest.ExpectedState)
                    || response.ObservedAfter.StateSha256 != response.AfterSnapshotSha256
                    || response.ObservedAfter.Revision!.Sequence < response.ObservedBefore.Revision!.Sequence
                    || (response.ObservedAfter.Revision.Sequence == response.ObservedBefore.Revision.Sequence
                        && !response.ObservedAfter.Equals(response.ObservedBefore)))
                throw Invalid("A committed checked symbol receipt requires native write, target, unrelated-entry and non-regressing revision evidence.");
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
            if (response.ReceiptStatus != NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsUnknown
                && (response.ReceiptStatus != NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsRolledBackVerified
                    || !response.NativeWriteStarted || !response.RollbackAttempted
                    || response.ObservedRollback is null
                    || !ValidateState(response.ObservedRollback, expectedRequest.ExpectedState)
                    || !response.ObservedRollback.Equals(expectedRequest.ExpectedState)))
                throw Invalid("A rolled-back checked symbol receipt requires native write and rollback evidence.");
        }
        else if (response.RollbackCompleted)
            throw Invalid("Rollback completion is only valid for a rolled-back checked symbol response.");

        if (!string.IsNullOrEmpty(response.BeforeSnapshotSha256))
            RequireDigest(response.BeforeSnapshotSha256, "before snapshot");
        if (!string.IsNullOrEmpty(response.AfterSnapshotSha256))
            RequireDigest(response.AfterSnapshotSha256, "after snapshot");
    }

    internal static string ComputeFingerprint(NativeCapabilitySymbolLibraryCheckedWrite request)
    {
        using var stream = new MemoryStream();
        var coded = new CodedOutputStream(stream, leaveOpen: true) { Deterministic = true };
        request.WriteTo(coded);
        coded.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    internal static string ComputeStateDigest(NativeCapabilitySymbolLibraryCheckedState state)
    {
        var content = state.Clone();
        content.StateSha256 = string.Empty;
        using var stream = new MemoryStream();
        var coded = new CodedOutputStream(stream, leaveOpen: true) { Deterministic = true };
        content.WriteTo(coded);
        coded.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void RequireDigest(string value, string name)
    {
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigitLower))
            throw Invalid($"The checked native symbol response has an invalid {name} digest.");
    }

    private static AutomationException Invalid(string message) =>
        new("invalid_symbol_library_response", message);

    private static bool ValidateState(
        NativeCapabilitySymbolLibraryCheckedState state,
        NativeCapabilitySymbolLibraryCheckedState expected)
    {
        if (state is null || expected is null || !state.Document.Equals(expected.Document)
            || state.ProcessEpoch != expected.ProcessEpoch || state.Scope != "mcp_direct_symbol_file"
            || !state.StateComplete || !state.DiskBaselineChecked || state.CompleteChangeTracking
            || state.Revision is null || state.Revision.Epoch != expected.ProcessEpoch
            || state.StateSha256.Length != 64 || !state.StateSha256.All(char.IsAsciiHexDigitLower))
            return false;

        if (state.StateSha256 != ComputeStateDigest(state))
            return false;

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var baseline in state.DependencyBaselines)
        {
            if (!baseline.Known || string.IsNullOrWhiteSpace(baseline.Path) || !paths.Add(baseline.Path)
                || (baseline.Exists && (baseline.Sha256.Length != 64 || !baseline.Sha256.All(char.IsAsciiHexDigitLower)))
                || (!baseline.Exists && (baseline.Sha256.Length != 0 || baseline.Bytes != 0)))
                return false;
        }

        return paths.Count != 0;
    }

}
