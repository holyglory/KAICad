using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Identity checks for native PCB operation receipts. A found receipt is
/// usable only when it belongs to the requested operation and board; otherwise
/// exposing its outcome could attribute another operation's result to this
/// request.
/// </summary>
internal static class PcbOperationsContract
{
    internal static void ValidateNativeReceiptIdentity(
        NativeCapabilityPcbReadOperationResponse receipt,
        string operationId,
        string expectedProcessEpoch,
        DocumentSpecifier expectedDocument,
        string expectedOperation)
    {
        if (receipt is null || !receipt.Found)
            return;

        if (!string.Equals(receipt.OperationId, operationId, StringComparison.Ordinal)
            || !string.Equals(receipt.Operation, expectedOperation, StringComparison.Ordinal))
            throw new AutomationException("wrong_operation_receipt",
                "The native PCB receipt belongs to another operation or operation kind.");

        if (!string.IsNullOrEmpty(receipt.ProcessEpoch)
            && !string.Equals(receipt.ProcessEpoch, expectedProcessEpoch, StringComparison.Ordinal))
            throw new AutomationException("stale_process_epoch",
                "The native PCB receipt belongs to another process epoch.");

        if (receipt.Document is null || !receipt.Document.Equals(expectedDocument))
            throw new AutomationException("wrong_operation_document",
                "The native PCB receipt belongs to another board document.");
    }
}
