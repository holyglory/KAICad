using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>Fail-closed validation for the checked direct footprint-file receipt.</summary>
internal static class FootprintLibraryCheckedContract
{
    private static readonly string[] TerminalStatuses =
        ["committed_postverified", "rejected", "unavailable", "rolled_back_verified", "indeterminate"];

    internal static IReadOnlyList<string> ValidateCandidate(FootprintInstance footprint,
        CancellationToken token)
    {
        var definition = footprint.Definition;
        if (definition is null)
            return ["missing_library_definition:definition"];

        // Library footprints legitimately contain unnumbered mounting holes, repeated electrical
        // numbers and typed fields/points/zones.  The board-assignment checker intentionally has
        // a different boundary, so use the detached definition's own typed coverage here.
        var coverage = FootprintLibraryContract.InspectDefinitionCoverage(new() { Footprint = footprint });
        var issues = new List<string>();
        if (!coverage.NestedItemsDecoded || !coverage.PadsDecoded || !coverage.PropertiesPresent)
            issues.Add("incomplete_library_definition:definition");
        if (footprint.Id is null || !FootprintLibraryContract.IsCanonicalUuid(footprint.Id.Value))
            issues.Add("missing_instance_id:id");

        foreach (Any item in definition.Items)
        {
            token.ThrowIfCancellationRequested();
            if (!item.Is(Pad.Descriptor))
                continue;
            try
            {
                Pad pad = item.Unpack<Pad>();
                if (pad.Number.Length > 128 || pad.Number.Contains('\0'))
                    issues.Add("invalid_pad_number:definition.items");
                if (pad.PadStack is null || pad.PadStack.Layers.Count == 0
                    || pad.PadStack.CopperLayers.Count == 0
                    || pad.PadStack.CopperLayers.Any(layer =>
                    layer.Shape == PadStackShape.PssUnknown || layer.Size is null
                    || layer.Size.XNm <= 0 || layer.Size.YNm <= 0))
                    issues.Add("invalid_pad_shape:definition.items");
            }
            catch (InvalidProtocolBufferException)
            {
                issues.Add("malformed_child_payload:definition.items");
            }
        }
        return issues;
    }

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
        if (response.ReceiptStatus == NativeCapabilityFootprintLibraryCheckedReceiptStatus.NcpFlcsUnknown
            || response.Scope != "mcp_direct_footprint_file" || !response.ExpectedRequestVerified
            || response.CompleteChangeTracking || response.Request is null
            || !response.Request.Equals(request) || response.RequestFingerprintSha256.Length != 64
            || !response.RequestFingerprintSha256.All(char.IsAsciiHexDigitLower)
            || response.RequestFingerprintSha256 != ComputeFingerprint(request))
            throw Invalid("The checked native footprint receipt must retain the exact request and deterministic fingerprint.");

        if (committed)
        {
            if (response.ReceiptStatus != NativeCapabilityFootprintLibraryCheckedReceiptStatus.NcpFlcsCommittedPostverified
                || !response.RevisionTracked || response.CompleteLifecycle || !response.NativeWriteStarted
                || !response.Postverified || !response.TargetVerified || !response.UnrelatedEntriesVerified
                || response.RollbackAttempted || response.ObservedBefore is null || response.ObservedAfter is null
                || !ValidateState(response.ObservedBefore, request.ExpectedState)
                || !response.ObservedBefore.Equals(request.ExpectedState)
                || !ValidateState(response.ObservedAfter, request.ExpectedState)
                || response.AfterSnapshotSha256 != response.ObservedAfter.StateSha256
                || response.ObservedAfter.Revision is null
                || response.ObservedAfter.Revision.Sequence <= response.ObservedBefore.Revision.Sequence
                || !ValidDigest(response.BeforeDefinitionSha256)
                || !ValidDigest(response.AfterDefinitionSha256)
                || response.BeforeDefinitionSha256 != response.ObservedBefore.DetachedDefinitionSha256
                || response.AfterDefinitionSha256 != response.ObservedAfter.DetachedDefinitionSha256)
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
                || response.ObservedRollback is null
                || !ValidateState(response.ObservedRollback, request.ExpectedState)
                || !response.ObservedRollback.Equals(request.ExpectedState)))
            throw Invalid("A rolled-back checked footprint receipt requires native rollback evidence.");

        if (response.Status != "rolled_back_verified" && response.RollbackCompleted)
            throw Invalid("Rollback completion is only valid for a rolled-back checked footprint response.");
        if (!string.IsNullOrEmpty(response.BeforeSnapshotSha256))
            RequireDigest(response.BeforeSnapshotSha256, "before snapshot");
        if (!string.IsNullOrEmpty(response.AfterSnapshotSha256))
            RequireDigest(response.AfterSnapshotSha256, "after snapshot");
    }

    private static void RequireDigest(string value, string name)
    {
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigitLower))
            throw Invalid($"The checked native footprint response has an invalid {name} digest.");
    }

    internal static string ComputeFingerprint(NativeCapabilityFootprintLibraryCheckedWrite request)
    {
        using var stream = new MemoryStream();
        var coded = new CodedOutputStream(stream, leaveOpen: true) { Deterministic = true };
        request.WriteTo(coded);
        coded.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    internal static string ComputeStateDigest(NativeCapabilityFootprintLibraryCheckedState state)
    {
        var content = state.Clone();
        content.StateSha256 = string.Empty;
        using var stream = new MemoryStream();
        var coded = new CodedOutputStream(stream, leaveOpen: true) { Deterministic = true };
        content.WriteTo(coded);
        coded.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static bool ValidateState(
        NativeCapabilityFootprintLibraryCheckedState state,
        NativeCapabilityFootprintLibraryCheckedState expected)
    {
        if (state is null || expected is null || !state.Document.Equals(expected.Document)
            || state.ProcessEpoch != expected.ProcessEpoch || state.Scope != "mcp_direct_footprint_file"
            || !state.StateComplete || !state.DiskBaselineChecked || state.CompleteChangeTracking
            || state.Revision is null || state.Revision.Epoch != expected.ProcessEpoch
            || !ValidDigest(state.StateSha256) || state.StateSha256 != ComputeStateDigest(state))
            return false;

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var baseline in state.DependencyBaselines)
        {
            if (!baseline.Known || string.IsNullOrWhiteSpace(baseline.Path) || !paths.Add(baseline.Path)
                || (baseline.Exists && (!ValidDigest(baseline.Sha256) || baseline.Bytes == 0))
                || (!baseline.Exists && (baseline.Sha256.Length != 0 || baseline.Bytes != 0)))
                return false;
        }
        if (paths.Count == 0)
            return false;

        var expectedPaths = expected.DependencyBaselines.Select(item => item.Path)
            .ToHashSet(StringComparer.Ordinal);
        return paths.SetEquals(expectedPaths)
            && state.TablePath == expected.TablePath
            && state.TableScope == expected.TableScope
            && state.RawUri == expected.RawUri
            && state.ExpandedUri == expected.ExpandedUri
            && state.Provider == expected.Provider
            && state.ProviderOptions == expected.ProviderOptions;
    }

    private static bool ValidDigest(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static AutomationException Invalid(string message) =>
        new("invalid_footprint_library_response", message);
}
