using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>Validates the identity and terminal evidence of retained schematic ERC work.</summary>
internal static class SchematicErcExecutionContract
{
    public static void ValidateRequest(NativeCapabilitySchematicExecuteErc request)
    {
        if (request is null)
            throw Invalid("A typed schematic ERC request is required.");

        CanonicalUuid(request.OperationId, "operation ID");
        if (request.ExpectedState is null || request.ExpectedState.Document is null)
            throw InvalidState("A complete schematic lifecycle observation is required.");

        DocumentStateTools.ValidateTarget(request.ExpectedState.Document);
        ValidateLifecycleState(request.ExpectedState, request.ExpectedState.Document,
            request.ExpectedState.ProcessEpoch);
    }

    public static void ValidateReceipt(NativeCapabilitySchematicExecuteErc request,
        NativeErcOperationReceipt receipt)
    {
        ValidateRequest(request);
        ValidateReceipt(receipt, request.ExpectedState!.Document, request.ExpectedState,
            request.OperationId, request.ExpectedState.ProcessEpoch);
        if (!receipt.AdmissionVerified)
            throw Invalid("A typed schematic ERC receipt must confirm lifecycle admission.");
    }

    /// <summary>
    /// Validates a receipt returned through the legacy revision-bound Run seam.
    /// Legacy requests predate the typed lifecycle admission flag, so their
    /// receipts are checked for exact identity, revision, lifecycle and terminal
    /// evidence without requiring <c>admission_verified</c>.
    /// </summary>
    public static void ValidateReceipt(NativeCapabilitySchematicRunErc request,
        NativeErcOperationReceipt receipt)
    {
        ValidateLegacyRequest(request);
        ValidateReceipt(receipt, request.Document, expectedState: null,
            request.OperationId, request.ProcessEpoch);
        if (receipt.BeforeState is null || !receipt.BeforeState.Revision.Equals(request.ExpectedRevision))
            throw Invalid("The native schematic ERC receipt belongs to a different legacy revision.");
    }

    private static void ValidateLegacyRequest(NativeCapabilitySchematicRunErc request)
    {
        if (request is null)
            throw Invalid("A legacy schematic ERC request is required.");

        CanonicalUuid(request.OperationId, "operation ID");
        CanonicalUuid(request.ProcessEpoch, "process epoch");
        if (request.Document is null || request.Document.Type != DocumentType.DoctypeSchematic)
            throw Invalid("A schematic document is required for a legacy ERC request.");
        DocumentStateTools.ValidateTarget(request.Document);
        if (request.ExpectedRevision is null
            || !IsCanonicalUuid(request.ExpectedRevision.Epoch))
            throw InvalidState("A legacy schematic ERC request requires a complete document revision.");
    }

    public static void ValidateReceipt(NativeErcOperationReceipt receipt,
        DocumentSpecifier document, DocumentLifecycleState? expectedState,
        string operationId, string processEpoch)
    {
        if (receipt is null)
            throw Invalid("The native schematic ERC receipt is missing.");

        CanonicalUuid(operationId, "operation ID");
        CanonicalUuid(processEpoch, "process epoch");
        if (document is null || document.Type != DocumentType.DoctypeSchematic)
            throw Invalid("A schematic document is required for an ERC receipt.");
        DocumentStateTools.ValidateTarget(document);

        NativeCapabilitySchematicRunErcResponse? legacy = receipt.LegacyResult;
        if (legacy is null || !legacy.Document.Equals(document)
            || legacy.OperationId != operationId || legacy.ProcessEpoch != processEpoch)
            throw Invalid("The native schematic ERC receipt does not match the requested document, process epoch or operation.");

        if (receipt.BeforeState is null)
            throw Invalid("The native schematic ERC receipt lacks its admitted lifecycle state.");
        ValidateLifecycleState(receipt.BeforeState, document, processEpoch);
        if (expectedState is not null && !receipt.BeforeState.Equals(expectedState))
            throw Invalid("The native schematic ERC receipt belongs to a different lifecycle revision.");
        if (legacy.CheckedRevision is null || !legacy.CheckedRevision.Equals(receipt.BeforeState.Revision))
            throw Invalid("The native schematic ERC receipt does not retain the admitted document revision.");

        if (receipt.AfterState is not null)
        {
            ValidateLifecycleState(receipt.AfterState, document, processEpoch);
            if (legacy.ObservedRevision is null || !legacy.ObservedRevision.Equals(receipt.AfterState.Revision))
                throw Invalid("The native schematic ERC receipt does not retain its observed document revision.");
        }

        if (receipt.MarkersCaptured != (receipt.CapturedMarkers is not null))
            throw Invalid("The native schematic ERC marker-capture flag does not match its payload.");
        if (receipt.MarkersCaptured && legacy.MarkerCount != receipt.CapturedMarkers!.Markers.Count)
            throw Invalid("The native schematic ERC marker count does not match its captured marker snapshot.");

        if (!Enum.IsDefined(receipt.State) || receipt.State == NativeErcOperationState.NeosUnknown)
            throw Invalid("The native schematic ERC receipt has an unknown lifecycle state.");

        switch (receipt.State)
        {
            case NativeErcOperationState.NeosAdmitted:
                if (legacy.Completed || receipt.ExecutionStarted || receipt.MarkersCaptured
                    || receipt.AfterState is not null || legacy.ObservedRevision is not null
                    || !string.IsNullOrWhiteSpace(legacy.ErrorCode)
                    || !string.IsNullOrWhiteSpace(legacy.ErrorMessage))
                    throw Invalid("An admitted schematic ERC receipt cannot claim execution or a terminal error.");
                break;

            case NativeErcOperationState.NeosCompleted:
                if (!legacy.Completed || !receipt.ExecutionStarted || !receipt.MarkersCaptured
                    || receipt.AfterState is null || !receipt.BeforeState.Equals(receipt.AfterState)
                    || !string.IsNullOrWhiteSpace(legacy.ErrorCode)
                    || !string.IsNullOrWhiteSpace(legacy.ErrorMessage))
                    throw Invalid("A completed schematic ERC receipt must retain a stable state and captured markers.");
                break;

            case NativeErcOperationState.NeosFailed:
                if (legacy.Completed || receipt.ExecutionStarted || receipt.MarkersCaptured
                    || receipt.AfterState is not null || legacy.ObservedRevision is not null
                    || string.IsNullOrWhiteSpace(legacy.ErrorCode)
                    || string.IsNullOrWhiteSpace(legacy.ErrorMessage))
                    throw Invalid("A failed schematic ERC receipt must retain its pre-execution error.");
                break;

            case NativeErcOperationState.NeosIndeterminate:
                if (legacy.Completed || !receipt.ExecutionStarted
                    || string.IsNullOrWhiteSpace(legacy.ErrorCode)
                    || string.IsNullOrWhiteSpace(legacy.ErrorMessage))
                    throw Invalid("An indeterminate schematic ERC receipt must retain its uncertainty and error.");
                break;

            default:
                throw Invalid("The native schematic ERC receipt has an unsupported lifecycle state.");
        }
    }

    /// <summary>
    /// Validates the explicit recovery classification returned by a native
    /// operation lookup. A missing receipt is an evidence gap, and a retained
    /// terminal receipt is replay-only; neither result authorizes an implicit
    /// second ERC execution.
    /// </summary>
    public static void ValidateRecoveryResponse(
        NativeCapabilitySchematicReadErcOperationResponse response,
        DocumentSpecifier document, string operationId, string processEpoch)
    {
        if (response is null)
            throw InvalidRecovery("The native schematic ERC recovery response is missing.");

        CanonicalUuid(operationId, "operation ID");
        CanonicalUuid(processEpoch, "process epoch");
        if (document is null || document.Type != DocumentType.DoctypeSchematic)
            throw InvalidRecovery("A schematic document is required for ERC recovery.");
        DocumentStateTools.ValidateTarget(document);

        if (!Enum.IsDefined(response.RecoveryState)
            || response.RecoveryState == NativeErcRecoveryState.NativeErcRecoveryUnknown
            || !Enum.IsDefined(response.RetrySafety))
            throw InvalidRecovery("The native schematic ERC recovery classification is unknown.");

        if (!response.Found)
        {
            if (response.Receipt is not null
                || response.RecoveryState != NativeErcRecoveryState.NativeErcRecoveryMissing
                || response.RetrySafety != NativeErcRetrySafety.NativeErcRetryUnknown)
                throw InvalidRecovery("A missing ERC receipt must report missing recovery state and unknown retry safety.");
            return;
        }

        if (response.Receipt is null)
            throw InvalidRecovery("A found ERC operation must include its retained receipt.");
        ValidateReceipt(response.Receipt, document, expectedState: null, operationId, processEpoch);

        NativeErcRecoveryState expectedState;
        NativeErcRetrySafety expectedRetry;
        switch (response.Receipt.State)
        {
            case NativeErcOperationState.NeosAdmitted:
                expectedState = NativeErcRecoveryState.NativeErcRecoveryPending;
                expectedRetry = NativeErcRetrySafety.NativeErcRetryUnknown;
                break;
            case NativeErcOperationState.NeosCompleted:
            case NativeErcOperationState.NeosFailed:
                expectedState = NativeErcRecoveryState.NativeErcRecoveryTerminal;
                expectedRetry = NativeErcRetrySafety.NativeErcRetryReplayOnly;
                break;
            case NativeErcOperationState.NeosIndeterminate:
                expectedState = NativeErcRecoveryState.NativeErcRecoveryTerminal;
                expectedRetry = NativeErcRetrySafety.NativeErcRetryUnknown;
                break;
            default:
                throw InvalidRecovery("The native schematic ERC receipt has no recoverable operation state.");
        }

        if (response.RecoveryState != expectedState || response.RetrySafety != expectedRetry)
            throw InvalidRecovery("The native schematic ERC recovery classification does not match its receipt state.");
    }

    /// <summary>Rejects an admitted, failed or indeterminate result at the execution boundary.</summary>
    public static void RequireSuccessful(NativeErcOperationReceipt receipt)
    {
        if (receipt is null)
            throw Invalid("The native schematic ERC receipt is missing.");

        switch (receipt.State)
        {
            case NativeErcOperationState.NeosCompleted:
                return;
            case NativeErcOperationState.NeosFailed:
                throw new AutomationException("erc_execution_failed", ErrorMessage(receipt));
            case NativeErcOperationState.NeosIndeterminate:
                throw new AutomationException("erc_execution_indeterminate", ErrorMessage(receipt));
            case NativeErcOperationState.NeosAdmitted:
                throw new AutomationException("erc_execution_incomplete", "Native ERC remains admitted but has no terminal result.");
            default:
                throw Invalid("The native schematic ERC receipt has an unknown lifecycle state.");
        }
    }

    private static void ValidateLifecycleState(DocumentLifecycleState state,
        DocumentSpecifier document, string processEpoch)
    {
        if (state.Scope != DocumentLifecycleScope.DlsSchematicHierarchy
            || state.Document is null || !state.Document.Equals(document)
            || state.ProcessEpoch != processEpoch
            || !IsCanonicalUuid(state.NativeIdentity) || state.Revision is null
            || !IsCanonicalUuid(state.Revision.Epoch)
            || state.StateSha256.Length != 64
            || !state.StateSha256.All(char.IsAsciiHexDigitLower))
            throw InvalidState("The native schematic ERC lifecycle state is incomplete or targets another revision.");
    }

    private static string ErrorMessage(NativeErcOperationReceipt receipt)
    {
        NativeCapabilitySchematicRunErcResponse? legacy = receipt.LegacyResult;
        return string.IsNullOrWhiteSpace(legacy?.ErrorMessage)
            ? "Native ERC did not produce a successful terminal result."
            : legacy.ErrorMessage;
    }

    private static void CanonicalUuid(string value, string label)
    {
        if (!IsCanonicalUuid(value))
            throw Invalid($"A nonempty canonical {label} is required.");
    }

    private static bool IsCanonicalUuid(string? value) =>
        Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty
        && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);

    private static AutomationException Invalid(string message) =>
        new("invalid_schematic_erc_receipt", message);

    private static AutomationException InvalidState(string message) =>
        new("invalid_schematic_erc_state", message);

    private static AutomationException InvalidRecovery(string message) =>
        new("invalid_schematic_erc_recovery", message);
}
