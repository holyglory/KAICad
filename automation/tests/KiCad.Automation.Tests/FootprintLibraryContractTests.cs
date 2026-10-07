using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class FootprintLibraryContractTests
{
    [TestMethod]
    public void NativeReceiptAvailabilityRequiresAFoundReceipt()
    {
        const string operationId = "00000000-0000-0000-0000-000000000001";
        var editorReceipt = new NativeCapabilityFootprintReadOperationResponse
        {
            OperationId = operationId,
            Found = false
        };
        var libraryReceipt = new NativeCapabilityFootprintLibraryReadOperationResponse
        {
            OperationId = operationId,
            Found = false
        };

        Assert.IsFalse(FootprintLibraryContract.HasNativeReceipt(editorReceipt, libraryReceipt));

        editorReceipt.Found = true;
        Assert.IsTrue(FootprintLibraryContract.HasNativeReceipt(editorReceipt, libraryReceipt));

        editorReceipt.Found = false;
        libraryReceipt.Found = true;
        Assert.IsTrue(FootprintLibraryContract.HasNativeReceipt(editorReceipt, libraryReceipt));
    }

    [TestMethod]
    public void DeleteResponseAndReceiptRequireExactIdentityAndCompletion()
    {
        const string operationId = "11111111-1111-1111-1111-111111111111";
        const string libraryNickname = "Fixture";
        const string footprintName = "Probe";
        var response = new NativeCapabilityFootprintLibraryDeleteResponse
        {
            LibraryNickname = libraryNickname,
            FootprintName = footprintName,
            OperationId = operationId,
            AfterModifyHash = 42,
            Completed = true
        };

        FootprintLibraryContract.ValidateDeleteResponse(response, operationId, libraryNickname, footprintName);

        var wrongOperation = response.Clone();
        wrongOperation.OperationId = "22222222-2222-2222-2222-222222222222";
        Reject(() => FootprintLibraryContract.ValidateDeleteResponse(
            wrongOperation, operationId, libraryNickname, footprintName), "footprint_operation_mismatch");

        var rejected = response.Clone();
        rejected.Completed = false;
        Reject(() => FootprintLibraryContract.ValidateDeleteResponse(
            rejected, operationId, libraryNickname, footprintName), "footprint_native_rejected");

        var receipt = new NativeCapabilityFootprintLibraryReadOperationResponse
        {
            OperationId = operationId,
            Found = true,
            DeleteResponse = response.Clone()
        };
        FootprintLibraryContract.ValidateLibraryReceipt(receipt, operationId, libraryNickname, footprintName);

        var wrongNestedOperation = receipt.Clone();
        wrongNestedOperation.DeleteResponse.OperationId = "33333333-3333-3333-3333-333333333333";
        Reject(() => FootprintLibraryContract.ValidateLibraryReceipt(
            wrongNestedOperation, operationId, libraryNickname, footprintName), "footprint_receipt_mismatch");
    }

    [TestMethod]
    public void EditorReceiptRequiresOperationAndDocumentIdentity()
    {
        const string operationId = "44444444-4444-4444-4444-444444444444";
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeFootprint,
            LibId = new() { LibraryNickname = "Fixture", EntryName = "Probe" }
        };
        var receipt = new NativeCapabilityFootprintReadOperationResponse
        {
            OperationId = operationId,
            Found = true,
            Operation = "save",
            Completed = true,
            Document = document.Clone()
        };

        FootprintLibraryContract.ValidateEditorReceipt(receipt, operationId, "save", document);

        var wrongOperation = receipt.Clone();
        wrongOperation.OperationId = "55555555-5555-5555-5555-555555555555";
        Reject(() => FootprintLibraryContract.ValidateEditorReceipt(
            wrongOperation, operationId, "save", document), "footprint_receipt_mismatch");

        var incomplete = receipt.Clone();
        incomplete.Completed = false;
        Reject(() => FootprintLibraryContract.ValidateEditorReceipt(
            incomplete, operationId, "save", document), "footprint_native_rejected");

        var wrongDocument = receipt.Clone();
        wrongDocument.Document.LibId.EntryName = "Other";
        Reject(() => FootprintLibraryContract.ValidateEditorReceipt(
            wrongDocument, operationId, "save", document), "footprint_receipt_mismatch");
    }

    [TestMethod]
    public void DeletePersistenceRequiresAbsentEntryAndMatchingModifyHash()
    {
        const string operationId = "66666666-6666-6666-6666-666666666666";
        var readback = new NativeCapabilityFootprintLibraryReadNamesResponse
        {
            LibraryNickname = "Fixture",
            ModifyHash = 73
        };
        readback.FootprintNames.Add("Other");

        FootprintLibraryContract.ValidateDeletePersistence(
            readback, operationId, "Fixture", "Probe", 73);

        var stillPresent = readback.Clone();
        stillPresent.FootprintNames.Add("Probe");
        Reject(() => FootprintLibraryContract.ValidateDeletePersistence(
            stillPresent, operationId, "Fixture", "Probe", 73), "footprint_persistence_unverified");

        var staleHash = readback.Clone();
        staleHash.ModifyHash = 74;
        Reject(() => FootprintLibraryContract.ValidateDeletePersistence(
            staleHash, operationId, "Fixture", "Probe", 73), "footprint_persistence_unverified");
    }

    [TestMethod]
    public void DefinitionReadbackRequiresExactSourceDestinationAndProcessIdentity()
    {
        string processEpoch = "11111111-1111-1111-1111-111111111111";
        var source = Document("Source");
        var destination = Document("Destination");
        var before = new NativeCapabilityFootprintReadDefinitionResponse
        {
            Document = source.Clone(), ProcessEpoch = processEpoch
        };
        var after = new NativeCapabilityFootprintReadDefinitionResponse
        {
            Document = destination.Clone(), ProcessEpoch = processEpoch
        };

        FootprintLibraryContract.ValidateDefinitionReadbackIdentity(
            before, after, source, destination, processEpoch, "footprint save-copy", "22222222-2222-2222-2222-222222222222");

        var wrongDestination = after.Clone();
        wrongDestination.Document.LibId.EntryName = "Other";
        Reject(() => FootprintLibraryContract.ValidateDefinitionReadbackIdentity(
            before, wrongDestination, source, destination, processEpoch, "footprint save-copy", "22222222-2222-2222-2222-222222222222"),
            "footprint_persistence_unverified");

        var wrongProcess = after.Clone();
        wrongProcess.ProcessEpoch = "33333333-3333-3333-3333-333333333333";
        Reject(() => FootprintLibraryContract.ValidateDefinitionReadbackIdentity(
            before, wrongProcess, source, destination, processEpoch, "footprint save-copy", "22222222-2222-2222-2222-222222222222"),
            "footprint_persistence_unverified");
    }

    private static DocumentSpecifier Document(string entryName) => new()
    {
        Type = DocumentType.DoctypeFootprint,
        LibId = new() { LibraryNickname = "Fixture", EntryName = entryName }
    };

    private static void Reject(Action action, string expectedCode)
    {
        AutomationException error = Assert.ThrowsExactly<AutomationException>(action);
        Assert.AreEqual(expectedCode, error.Code);
    }
}
