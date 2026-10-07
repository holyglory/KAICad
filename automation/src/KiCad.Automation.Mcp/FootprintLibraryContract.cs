using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Managed admission and readback checks for the legacy footprint-library API.
///
/// The native footprint editor predates document lifecycle revisions, so its
/// operation receipts are the only durable identity boundary available to this
/// surface. Keep the boundary fail-closed: a native response for another
/// operation or a non-completed receipt must never be exposed as a successful
/// MCP mutation.
/// </summary>
internal static class FootprintLibraryContract
{
    /// <summary>
    /// Reports whether either native receipt source actually contains the
    /// requested operation. A successful read transport with Found=false is
    /// still an honest receipt miss and must not be advertised as available
    /// native evidence.
    /// </summary>
    internal static bool HasNativeReceipt(
        NativeCapabilityFootprintReadOperationResponse? editorReceipt,
        NativeCapabilityFootprintLibraryReadOperationResponse? libraryReceipt) =>
        editorReceipt?.Found == true || libraryReceipt?.Found == true;

    internal static void ValidateDeleteResponse(
        NativeCapabilityFootprintLibraryDeleteResponse response,
        string operationId, string libraryNickname, string footprintName)
    {
        ValidateOperationId(operationId);
        if (response is null
            || response.OperationId != operationId
            || response.LibraryNickname != libraryNickname
            || response.FootprintName != footprintName)
            throw new AutomationException("footprint_operation_mismatch",
                "The native footprint-library response does not match the requested operation or library entry.");
        if (!response.Completed)
            throw new AutomationException("footprint_native_rejected",
                $"The native footprint-library delete for {libraryNickname}:{footprintName} was not completed; inspect the native editor before retrying.");
    }

    internal static void ValidateEditorReceiptIdentity(
        NativeCapabilityFootprintReadOperationResponse receipt, string operationId)
    {
        ValidateOperationId(operationId);
        if (receipt is null || receipt.OperationId != operationId)
            throw new AutomationException("footprint_receipt_mismatch",
                "The native footprint receipt does not match the requested operation ID.");
        if (!receipt.Found) return;
        if (!receipt.Completed || string.IsNullOrWhiteSpace(receipt.Operation))
            throw new AutomationException("footprint_native_rejected",
                "The native footprint receipt is present but does not report a completed operation.");
    }

    internal static void ValidateEditorReceipt(
        NativeCapabilityFootprintReadOperationResponse receipt, string operationId,
        string operation, DocumentSpecifier document)
    {
        ValidateEditorReceiptIdentity(receipt, operationId);
        if (!receipt.Found) return;
        if (!string.Equals(receipt.Operation, operation, StringComparison.Ordinal)
            || receipt.Document is null || !receipt.Document.Equals(document))
            throw new AutomationException("footprint_receipt_mismatch",
                $"The native footprint receipt does not match the requested {operation} document or operation.");
    }

    internal static void ValidateLibraryReceipt(
        NativeCapabilityFootprintLibraryReadOperationResponse receipt,
        string operationId, string libraryNickname, string footprintName)
    {
        ValidateLibraryReceiptIdentity(receipt, operationId);
        if (!receipt.Found) return;
        if (receipt.DeleteResponse is null)
            throw new AutomationException("footprint_receipt_mismatch",
                "The native footprint-library receipt is present without its delete result.");
        ValidateDeleteResponse(receipt.DeleteResponse, operationId, libraryNickname, footprintName);
    }

    internal static void ValidateLibraryReceiptIdentity(
        NativeCapabilityFootprintLibraryReadOperationResponse receipt, string operationId)
    {
        ValidateOperationId(operationId);
        if (receipt is null || receipt.OperationId != operationId)
            throw new AutomationException("footprint_receipt_mismatch",
                "The native footprint-library receipt does not match the requested operation ID.");
        if (!receipt.Found) return;
        if (receipt.DeleteResponse is null || receipt.DeleteResponse.OperationId != operationId)
            throw new AutomationException("footprint_receipt_mismatch",
                "The native footprint-library receipt contains a delete result for another operation.");
        if (!receipt.DeleteResponse.Completed)
            throw new AutomationException("footprint_native_rejected",
                "The native footprint-library receipt is present but does not report a completed delete operation.");
    }

    internal static void ValidateDeletePersistence(
        NativeCapabilityFootprintLibraryReadNamesResponse readback,
        string operationId, string libraryNickname, string footprintName, ulong expectedModifyHash)
    {
        if (readback is null
            || readback.LibraryNickname != libraryNickname
            || readback.FootprintNames.Contains(footprintName, StringComparer.Ordinal)
            || readback.ModifyHash != expectedModifyHash)
            throw new AutomationException("footprint_persistence_unverified",
                $"KiCad completed footprint-library delete operation {operationId}, but the native library readback did not prove that {libraryNickname}:{footprintName} is absent at modify hash {expectedModifyHash}.");
    }

    internal static void ValidateDefinitionReadbackIdentity(
        NativeCapabilityFootprintReadDefinitionResponse before,
        NativeCapabilityFootprintReadDefinitionResponse after,
        DocumentSpecifier expectedBeforeDocument,
        DocumentSpecifier expectedAfterDocument,
        string expectedProcessEpoch,
        string operation,
        string operationId)
    {
        if (before is null || after is null
            || before.Document is null || !before.Document.Equals(expectedBeforeDocument)
            || after.Document is null || !after.Document.Equals(expectedAfterDocument)
            || string.IsNullOrWhiteSpace(expectedProcessEpoch)
            || !string.Equals(before.ProcessEpoch, expectedProcessEpoch, StringComparison.Ordinal)
            || !string.Equals(after.ProcessEpoch, expectedProcessEpoch, StringComparison.Ordinal))
            throw new AutomationException("footprint_persistence_unverified",
                $"KiCad completed {operation} operation {operationId}, but the definition readback identity did not match the source, destination or native process.");
    }

    private static void ValidateOperationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid operation) || operation == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "Provide a nonempty operation UUID in canonical D format.");
    }
}
