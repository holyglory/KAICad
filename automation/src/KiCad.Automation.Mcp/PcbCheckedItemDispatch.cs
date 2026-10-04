using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// The managed boundary for the checked native PCB item command. Primitive
/// wrappers use this instead of opening a legacy BeginCommit transaction so a
/// native receipt remains available when a reply is lost. This helper performs
/// no live-state reread before dispatch: the native controller must first look
/// up an existing operation ID and replay its retained result.
/// </summary>
internal static class PcbCheckedItemDispatch
{
    internal sealed record Outcome(
        string InstanceId,
        CheckedPcbItemTransaction Request,
        CheckedPcbItemTransactionReceipt? Receipt,
        string? ErrorCode = null,
        string? ErrorMessage = null,
        bool NativeReceiptAvailable = false,
        bool UntrustedReceipt = false)
    {
        internal bool Completed => !UntrustedReceipt
            && Receipt?.State == CheckedPcbItemTransactionState.CpbtsCompleted;
    }

    internal static async Task<Outcome> ApplyAsync(InstanceRegistry registry, string instanceId,
        CheckedPcbItemTransaction request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeClient client = registry.Client(instanceId);
        AutomationSession session;
        try
        {
            session = await client.HandshakeAsync(cancellationToken);
        }
        catch (NngException error)
        {
            // A handshake is not the checked mutation. There is no native item
            // receipt to inspect when the peer could not be reached yet.
            return Failure(instanceId, request, "native_not_reached",
                $"KiCad could not establish the checked PCB item session ({error.Message}); no item transaction was started.");
        }
        catch (NativeApiException error)
        {
            return Failure(instanceId, request, $"native_status_{error.Status}", error.Message);
        }

        if (session.Epoch != request.ProcessEpoch)
            return Failure(instanceId, request, "pcb_instance_changed",
                "The expected PCB state belongs to another native process epoch.");

        try
        {
            // inspect:false keeps the same complete unchanged-file baseline
            // requirement as the native admission path. This validates the
            // original expected state and payload before any IPC mutation.
            CheckedPcbItemContract.ValidateRequest(request, session.Epoch);
        }
        catch (AutomationException error)
        {
            return Failure(instanceId, request, "invalid_pcb_item_request", error.Message);
        }

        CheckedPcbItemTransactionReceipt receipt;
        try
        {
            // This is intentionally the first operation after handshake and
            // validation. In particular, do not read live state here: a retry
            // with the same operation ID must reach native replay before a
            // stale checkpoint can reject it.
            receipt = await client.InvokeAsync<CheckedPcbItemTransaction,
                CheckedPcbItemTransactionReceipt>(request, cancellationToken);
        }
        catch (NngException error)
        {
            return Failure(instanceId, request, error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
                error.RequestDelivered
                    ? $"KiCad received checked PCB item operation {request.OperationId} but did not answer ({error.Message}). Inspect the native PCB item receipt before retrying."
                    : $"KiCad did not receive checked PCB item operation {request.OperationId} ({error.Message}); no item transaction was started.");
        }
        catch (NativeApiException error)
        {
            // An older peer that does not know the checked command is never
            // retried through the old unchecked primitive calls.
            return Failure(instanceId, request, $"native_status_{error.Status}", error.Message);
        }

        try
        {
            CheckedPcbItemContract.ValidateResult(request, receipt, inspect: false);
        }
        catch (AutomationException error)
        {
            return new Outcome(instanceId, request, receipt,
                ErrorCode: "invalid_checked_pcb_receipt",
                ErrorMessage: error.Code + ": " + error.Message,
                NativeReceiptAvailable: true, UntrustedReceipt: true);
        }

        return new Outcome(instanceId, request, receipt,
            ErrorCode: receipt.State == CheckedPcbItemTransactionState.CpbtsCompleted
                ? null : CompatibilityErrorCode(receipt.ErrorCode),
            ErrorMessage: receipt.State == CheckedPcbItemTransactionState.CpbtsCompleted
                ? null : receipt.ErrorMessage,
            NativeReceiptAvailable: true);
    }

    /// <summary>
    /// Inspect the native process-owned receipt before any live-state
    /// preflight. A CPBTS_NOT_FOUND result is a successful inspection that
    /// authorizes the caller to prepare its first request; every other
    /// terminal or in-progress receipt must be returned to the caller so it
    /// can settle the operation without rereading mutable board state.
    /// </summary>
    internal static async Task<Outcome> InspectAsync(InstanceRegistry registry, string instanceId,
        CheckedPcbItemTransaction request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeClient client = registry.Client(instanceId);
        AutomationSession session;
        try
        {
            session = await client.HandshakeAsync(cancellationToken);
        }
        catch (NngException error)
        {
            return Failure(instanceId, request, "native_not_reached",
                $"KiCad could not establish the checked PCB item session ({error.Message}); no item receipt was inspected.");
        }
        catch (NativeApiException error)
        {
            return Failure(instanceId, request, $"native_status_{error.Status}", error.Message);
        }

        if (session.Epoch != request.ProcessEpoch)
            return Failure(instanceId, request, "pcb_instance_changed",
                "The expected PCB state belongs to another native process epoch.");

        try
        {
            // Receipt inspection deliberately permits an incomplete file
            // baseline: it never mutates or admits a new transaction.
            CheckedPcbItemContract.ValidateRequest(request, session.Epoch, inspect: true);
        }
        catch (AutomationException error)
        {
            return Failure(instanceId, request, "invalid_pcb_item_request", error.Message);
        }

        var query = new ReadCheckedPcbItemTransactionReceipt
        {
            Document = request.ExpectedState!.Document.Clone(),
            ProcessEpoch = session.Epoch,
            OperationId = request.OperationId,
            ExpectedRequest = request
        };
        CheckedPcbItemTransactionReceipt receipt;
        try
        {
            receipt = await client.InvokeAsync<ReadCheckedPcbItemTransactionReceipt,
                CheckedPcbItemTransactionReceipt>(query, cancellationToken);
        }
        catch (NngException error)
        {
            return Failure(instanceId, request, error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
                error.RequestDelivered
                    ? $"KiCad received checked PCB receipt inspection for {request.OperationId} but did not answer ({error.Message})."
                    : $"KiCad did not receive checked PCB receipt inspection for {request.OperationId} ({error.Message}).");
        }
        catch (NativeApiException error)
        {
            return Failure(instanceId, request, $"native_status_{error.Status}", error.Message);
        }

        try
        {
            CheckedPcbItemContract.ValidateResult(request, receipt, inspect: true);
        }
        catch (AutomationException error)
        {
            return new Outcome(instanceId, request, receipt,
                ErrorCode: "invalid_checked_pcb_receipt",
                ErrorMessage: error.Code + ": " + error.Message,
                NativeReceiptAvailable: true, UntrustedReceipt: true);
        }

        // CPBTS_NOT_FOUND is an authoritative negative inspection, not a
        // native mutation result. Keep it available to the caller as the
        // branch signal for its first-call preflight.
        return new Outcome(instanceId, request, receipt,
            ErrorCode: receipt.State == CheckedPcbItemTransactionState.CpbtsNotFound
                ? null : CompatibilityErrorCode(receipt.ErrorCode),
            ErrorMessage: receipt.State == CheckedPcbItemTransactionState.CpbtsNotFound
                ? null : receipt.ErrorMessage,
            NativeReceiptAvailable: receipt.State != CheckedPcbItemTransactionState.CpbtsNotFound);
    }

    private static Outcome Failure(string instanceId, CheckedPcbItemTransaction request, string code, string message) =>
        new(instanceId, request, null,
            ErrorCode: code, ErrorMessage: message, NativeReceiptAvailable: false);

    internal static string CompatibilityErrorCode(string nativeCode) => nativeCode switch
    {
        "stale_document_state" => "pcb_state_changed",
        "create_rejected" or "update_rejected" or "delete_rejected" => "pcb_item_transaction_rejected",
        _ => string.IsNullOrWhiteSpace(nativeCode) ? "pcb_item_operation_failed" : nativeCode
    };
}
