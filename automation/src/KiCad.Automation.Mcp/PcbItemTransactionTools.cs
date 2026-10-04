using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// One native, lifecycle-checked transaction over the existing generic PCB
/// create/update/delete requests. The legacy primitive item tools remain
/// available for compatibility; this tool is the retained-receipt path.
/// </summary>
[McpServerToolType]
public sealed class PcbItemTransactionTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_items_transaction"),
     Description("Apply one ordered native PCB create/update/delete transaction. requestJson is a CheckedPcbItemTransaction carrying the exact Any-packed operations, full expected kicad_document_state, native process epoch, operation UUID and optional bounded receipt allowance. KiCad stages every operation in one undo transaction, retains exact per-operation responses, proves rollback when a pre-commit operation fails, and returns a post-commit lifecycle checkpoint. Reuse the identical request and operation UUID after a timeout; inspect kicad_pcb_items_transaction_receipt before retrying an indeterminate result."),
     KiCadCapability("pcb-item-transactions", "compiled-mcp plus-native-checked-controller", "ordered generic PCB create/update/delete operations, exact lifecycle state, process epoch, operation UUID, retained replay/rollback/post-commit receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio", "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> Apply(string instanceId, string requestJson,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = Parse(requestJson, "invalid_pcb_item_transaction");
        // Reject malformed protobufs and missing operation/state identity before
        // consulting an instance registry or opening an IPC connection.
        ValidateInput(request, "invalid_pcb_item_transaction");
        var client = registry.Client(instanceId);
        var session = await client.HandshakeAsync(cancellationToken);
        if (request.ProcessEpoch != session.Epoch)
            throw new AutomationException("stale_process_epoch", "The checked PCB item request belongs to another native process epoch.");

        CheckedPcbItemContract.ValidateRequest(request, session.Epoch);
        CheckedPcbItemTransactionReceipt receipt;
        try
        {
            receipt = await client.InvokeAsync<CheckedPcbItemTransaction, CheckedPcbItemTransactionReceipt>(
                request, cancellationToken);
        }
        catch (NngException error) when (error.RequestDelivered)
        {
            throw new AutomationException("operation_outcome_unknown",
                $"KiCad received checked PCB item operation {request.OperationId} but did not answer ({error.Message}). "
                + "Inspect kicad_pcb_items_transaction_receipt with the identical request before retrying.");
        }
        catch (NngException error)
        {
            throw new AutomationException("native_not_reached",
                $"KiCad did not receive checked PCB item operation {request.OperationId} ({error.Message}); no item transaction was started.");
        }

        try
        {
            CheckedPcbItemContract.ValidateResult(request, receipt, inspect: false);
        }
        catch (AutomationException error)
        {
            // A native flag is evidence about the reply, not proof that the
            // reply satisfies this managed contract. Keep malformed native
            // bytes nested as untrusted evidence and expose only an outer
            // indeterminate state to callers.
            return ReceiptData(instanceId, receipt, error: true,
                validationErrorCode: "invalid_checked_pcb_receipt",
                validationError: error.Code + ": " + error.Message, untrustedReceipt: true);
        }
        return ReceiptData(instanceId, receipt, receipt.State != CheckedPcbItemTransactionState.CpbtsCompleted);
    });

    [McpServerTool(Name = "kicad_pcb_items_transaction_receipt", ReadOnly = true),
     Description("Read the native process-owned receipt for one exact checked PCB item transaction after a lost reply. requestJson must be byte-for-byte equivalent to the original CheckedPcbItemTransaction, including operations, lifecycle checkpoint, process epoch and operation UUID. This never resubmits a mutation."),
     KiCadCapability("pcb-item-transactions", "native-checked-controller", "exact request replay, process epoch and operation receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Receipt(string instanceId, string requestJson,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = Parse(requestJson, "invalid_pcb_item_transaction_receipt");
        ValidateInput(request, "invalid_pcb_item_transaction_receipt");
        var client = registry.Client(instanceId);
        var session = await client.HandshakeAsync(cancellationToken);
        if (request.ProcessEpoch != session.Epoch)
            throw new AutomationException("stale_process_epoch", "The checked PCB item request belongs to another native process epoch.");

        CheckedPcbItemContract.ValidateRequest(request, session.Epoch, inspect: true);
        var query = new ReadCheckedPcbItemTransactionReceipt
        {
            Document = request.ExpectedState!.Document.Clone(),
            ProcessEpoch = session.Epoch,
            OperationId = request.OperationId,
            ExpectedRequest = request
        };
        var receipt = await client.InvokeAsync<ReadCheckedPcbItemTransactionReceipt, CheckedPcbItemTransactionReceipt>(
            query, cancellationToken);
        try
        {
            CheckedPcbItemContract.ValidateResult(request, receipt, inspect: true);
            return ReceiptData(instanceId, receipt, error: false, readOnly: true);
        }
        catch (AutomationException error)
        {
            return ReceiptData(instanceId, receipt, error: true, readOnly: true,
                validationErrorCode: "invalid_checked_pcb_receipt",
                validationError: error.Code + ": " + error.Message, untrustedReceipt: true);
        }
    });

    private static CheckedPcbItemTransaction Parse(string json, string code)
    {
        try { return SchematicJson.Parser.Parse<CheckedPcbItemTransaction>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException(code, "requestJson is not a valid CheckedPcbItemTransaction: " + error.Message); }
    }

    private static void ValidateInput(CheckedPcbItemTransaction request, string code)
    {
        try { CheckedPcbItemContract.ValidateRequest(request, request.ProcessEpoch, inspect: true); }
        catch (AutomationException error) { throw new AutomationException(code, error.Message); }
    }

    private static CallToolResult ReceiptData(string instanceId, CheckedPcbItemTransactionReceipt receipt,
        bool error, bool readOnly = false, string? validationErrorCode = null,
        string? validationError = null, bool untrustedReceipt = false)
    {
        var receiptJson = JsonDocument.Parse(SchematicJson.Formatter.Format(receipt)).RootElement.Clone();
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId,
            operationId = receipt.OperationId,
            processEpoch = receipt.ProcessEpoch,
            state = untrustedReceipt ? CheckedPcbItemTransactionState.CpbtsIndeterminate.ToString() : receipt.State.ToString(),
            receipt = untrustedReceipt ? (JsonElement?) null : receiptJson,
            untrustedReceipt = untrustedReceipt ? receiptJson : (JsonElement?) null,
            beforeState = untrustedReceipt || receipt.ObservedBefore is null ? null : SchematicJson.Formatter.Format(receipt.ObservedBefore),
            rollbackState = untrustedReceipt || receipt.ObservedRollback is null ? null : SchematicJson.Formatter.Format(receipt.ObservedRollback),
            afterState = untrustedReceipt || receipt.ObservedAfter is null ? null : SchematicJson.Formatter.Format(receipt.ObservedAfter),
            errorCode = validationErrorCode,
            validationError,
            readOnly
        });
        return new CallToolResult
        {
            IsError = error,
            StructuredContent = data,
            Content = [new TextContentBlock { Text = data.GetRawText() }]
        };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException
            or ArgumentException)
        {
            var data = JsonSerializer.SerializeToElement(new
            {
                errorCode = error is AutomationException known ? known.Code : "pcb_item_transaction_failed",
                errorMessage = error.Message
            });
            return new CallToolResult
            {
                IsError = true,
                StructuredContent = data,
                Content = [new TextContentBlock { Text = data.GetRawText() }]
            };
        }
    }
}
