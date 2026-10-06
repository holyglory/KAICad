using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Managed admission boundary for the legacy symbol-library mutation wrappers.
///
/// The current native symbol-library commands only carry a process-local
/// modify hash and receipt. They do not expose the document revision, complete
/// dependency baselines, provider lock or rollback evidence required by
/// <see cref="LibraryTransactionContract"/>. A future checked caller can pass
/// those observations here; this bridge validates them and then fails closed,
/// so no legacy writer can be reached accidentally.
/// </summary>
internal static class SymbolLibraryTransactionAdmission
{
    internal const string MutationUnavailableCode = "library_mutation_unavailable";

    /// <summary>
    /// Builds the canonical request identity from the explicit document,
    /// process epoch and complete lifecycle checkpoint supplied by a checked
    /// caller. The request is only an identity value; it does not contact
    /// KiCad or reserve a native operation.
    /// </summary>
    internal static LibraryTransactionRequest BuildRequest(
        string operationId,
        DocumentSpecifier document,
        string processEpoch,
        LibraryLifecycleState expectedState,
        string operation,
        string operationPayload)
    {
        if (expectedState is null)
            throw Unavailable("A complete library lifecycle checkpoint is required before a checked mutation can be admitted.");

        // Keep request-owned document/state values detached from mutable
        // protobuf objects held by the caller. The fingerprint must continue
        // to identify the exact checkpoint that was presented at admission.
        LibraryLifecycleState ownedState = CloneState(expectedState);
        var unsigned = new LibraryTransactionRequest(
            operationId,
            document?.Clone() ?? throw new AutomationException(
                "invalid_library_transaction", "A checked symbol-library mutation requires an explicit document."),
            processEpoch,
            ownedState,
            operation,
            operationPayload,
            RequestFingerprintSha256: string.Empty);
        return unsigned with
        {
            RequestFingerprintSha256 = LibraryTransactionContract.ComputeFingerprint(unsigned)
        };
    }

    /// <summary>
    /// Validates an exact current observation or an exact retained receipt,
    /// then refuses because the native lifecycle owner is not implemented.
    /// Validation runs before the refusal so stale, foreign, partial and
    /// conflicting requests keep their precise contract error codes.
    /// </summary>
    internal static void Admit(
        LibraryTransactionRequest request,
        LibraryLifecycleState observedState,
        LibraryTransactionReceipt? retainedReceipt = null)
    {
        if (retainedReceipt is null)
            LibraryTransactionContract.ValidateAdmission(request, observedState);
        else
            LibraryTransactionContract.ValidateReplay(request, retainedReceipt);

        throw Unavailable(
            "Checked symbol-library mutation is unavailable until the native lifecycle owner provides document revisions, complete dependency tracking and rollback proof; no native writer was called.");
    }

    /// <summary>
    /// Convenience entry point for a checked caller that has not yet retained
    /// a receipt. It constructs the request, validates it against the current
    /// state, and fails closed before any legacy write/delete/clone dispatch.
    /// </summary>
    internal static void Admit(
        string operationId,
        DocumentSpecifier document,
        string processEpoch,
        LibraryLifecycleState expectedState,
        string operation,
        string operationPayload,
        LibraryLifecycleState observedState)
    {
        LibraryTransactionRequest request = BuildRequest(
            operationId, document, processEpoch, expectedState, operation, operationPayload);
        Admit(request, observedState);
    }

    /// <summary>
    /// Convenience entry point for an exact operation replay. Replay is
    /// identity-only and never retries a native operation.
    /// </summary>
    internal static void Replay(
        LibraryTransactionRequest request,
        LibraryTransactionReceipt retainedReceipt)
    {
        LibraryTransactionContract.ValidateReplay(request, retainedReceipt);
        throw Unavailable(
            "Checked symbol-library replay is unavailable until the native lifecycle owner can inspect its durable receipt; no native writer was called.");
    }

    private static LibraryLifecycleState CloneState(LibraryLifecycleState state) => state with
    {
        Document = state.Document?.Clone() ?? throw new AutomationException(
            "invalid_library_transaction", "A checked symbol-library mutation requires a complete lifecycle document."),
        Revision = state.Revision is null
            ? throw new AutomationException("invalid_library_transaction", "A checked symbol-library mutation requires a document revision.")
            : state.Revision with { },
        FileBaselines = state.FileBaselines is null
            ? throw new AutomationException("partial_library_state", "A checked symbol-library mutation requires complete dependency baselines.")
            : state.FileBaselines.Select(baseline => baseline is null
                ? throw new AutomationException("partial_library_state", "A checked symbol-library mutation cannot contain an empty dependency baseline.")
                : baseline with { }).ToArray()
    };

    private static AutomationException Unavailable(string message) =>
        new(MutationUnavailableCode, message);
}
