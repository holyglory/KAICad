using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
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

        // A present receipt is not completion proof when the native provider
        // failed to delete the file, even if a separate name lookup is empty.
        var failedDelete = receipt.Clone();
        failedDelete.DeleteResponse.Completed = false;
        FootprintLibraryContract.ValidateDeletePersistence(new()
        { LibraryNickname = libraryNickname, ModifyHash = 42 }, operationId, libraryNickname, footprintName, 42);
        Reject(() => FootprintLibraryContract.ValidateLibraryReceipt(
            failedDelete, operationId, libraryNickname, footprintName), "footprint_native_rejected");

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
    public void CheckedFootprintContractRejectsForgedCommitAndAcceptsUnavailableReceipt()
    {
        var document = Document("Probe");
        var state = new NativeCapabilityFootprintLibraryCheckedState
        {
            Document = document.Clone(), ProcessEpoch = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            Scope = "mcp_direct_footprint_file", StateComplete = true, DiskBaselineChecked = true,
            Revision = new() { Epoch = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Sequence = 1 },
            StateSha256 = new string('a', 64)
        };
        var request = new NativeCapabilityFootprintLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = state.ProcessEpoch,
            OperationId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", ExpectedSnapshotSha256 = state.StateSha256,
            ExpectedState = state.Clone(), Footprint = new() { Definition = new() { Id = document.LibId.Clone() } }
        };
        var unavailable = new NativeCapabilityFootprintLibraryCheckedWriteResponse
        {
            Document = document.Clone(), ProcessEpoch = state.ProcessEpoch, OperationId = request.OperationId,
            Status = "unavailable", ErrorCode = "library_mutation_unavailable", ErrorMessage = "provider unavailable",
            ReceiptStatus = NativeCapabilityFootprintLibraryCheckedReceiptStatus.NcpFlcsUnavailable,
            Scope = "mcp_direct_footprint_file", ExpectedRequestVerified = true, Request = request.Clone(),
            RequestFingerprintSha256 = new string('c', 64)
        };
        FootprintLibraryCheckedContract.ValidateResponse(unavailable, request);

        var forged = unavailable.Clone();
        forged.Status = "committed_postverified";
        forged.Committed = true;
        Assert.ThrowsExactly<AutomationException>(() => FootprintLibraryCheckedContract.ValidateResponse(forged, request));
    }

    [TestMethod]
    public void CheckedFootprintContractRejectsOperationOrDocumentMismatch()
    {
        var document = Document("Probe");
        var request = new NativeCapabilityFootprintLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            OperationId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", ExpectedSnapshotSha256 = new string('a', 64),
            ExpectedState = new() { Document = document.Clone(), ProcessEpoch = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                Scope = "mcp_direct_footprint_file", StateComplete = true, DiskBaselineChecked = true,
                StateSha256 = new string('a', 64), Revision = new() { Epoch = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" } }
        };
        var response = new NativeCapabilityFootprintLibraryCheckedWriteResponse
        {
            Document = document.Clone(), ProcessEpoch = request.ProcessEpoch, OperationId = request.OperationId,
            Status = "unavailable", ErrorCode = "library_mutation_unavailable", ErrorMessage = "provider unavailable",
            ReceiptStatus = NativeCapabilityFootprintLibraryCheckedReceiptStatus.NcpFlcsUnavailable,
            Scope = "mcp_direct_footprint_file", ExpectedRequestVerified = true, Request = request.Clone(),
            RequestFingerprintSha256 = new string('c', 64)
        };
        var wrong = response.Clone();
        wrong.OperationId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
        Assert.ThrowsExactly<AutomationException>(() => FootprintLibraryCheckedContract.ValidateResponse(wrong, request));
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

        Reject(() => FootprintLibraryContract.ValidateDefinitionReadbackIdentity(
            before, after, source, destination, processEpoch, "footprint save-copy", "malformed-operation"),
            "invalid_operation_id");
    }

    [TestMethod]
    public void CompleteDefinitionCoverageProvesNestedPadsGeometryAndProperties()
    {
        const string epoch = "77777777-7777-7777-7777-777777777777";
        DocumentSpecifier document = Document("Probe");
        NativeCapabilityFootprintReadDefinitionResponse response = CompleteDefinition(document, epoch);

        FootprintLibraryContract.FootprintDefinitionCoverage coverage =
            FootprintLibraryContract.ValidateDefinitionResponse(response, document, epoch,
                (NativeFootprintDefinitionSource)2, document.LibId);

        Assert.IsTrue(coverage.Complete);
        Assert.IsTrue(coverage.CanonicalWriterHashValid);
        Assert.IsTrue(coverage.ProviderIdentityHashValid);
        Assert.IsTrue(coverage.NestedItemsDecoded);
        Assert.IsTrue(coverage.PadsDecoded);
        Assert.IsTrue(coverage.PropertiesPresent);
        Assert.IsTrue(coverage.ItemsKnownAndDecoded);
        Assert.AreEqual(6, coverage.ItemCount);
        Assert.AreEqual(6, coverage.GeometryItemCount);
        Assert.AreEqual(2, coverage.PadCount);
        Assert.IsGreaterThan(0, coverage.PropertyCount);
        Assert.AreEqual("complete", coverage.Status);

        FootprintLibraryContract.FootprintDefinitionCoverage persisted =
            coverage.WithPersistence(persistedReadbackVerified: true, canonicalHashMatched: true);
        Assert.IsTrue(persisted.PersistedReadbackVerified);
        Assert.IsTrue(persisted.CanonicalHashMatched);
        Assert.IsTrue(persisted.Complete);
    }

    [TestMethod]
    public void NativeNpthAndNonPadItemPayloadsRemainComplete()
    {
        const string epoch = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        DocumentSpecifier document = Document("NativeItems");
        NativeCapabilityFootprintReadDefinitionResponse response = CompleteDefinition(document, epoch);

        FootprintLibraryContract.FootprintDefinitionCoverage coverage =
            FootprintLibraryContract.ValidateDefinitionResponse(response, document, epoch,
                (NativeFootprintDefinitionSource)2, document.LibId);

        Assert.IsTrue(coverage.Complete);
        Assert.AreEqual(2, coverage.PadCount);
        Assert.IsTrue(coverage.NestedItemsDecoded);
    }

    [TestMethod]
    public void CompleteClaimRejectsMalformedNestedPayloadAndDigest()
    {
        const string epoch = "88888888-8888-8888-8888-888888888888";
        DocumentSpecifier document = Document("Probe");
        NativeCapabilityFootprintReadDefinitionResponse malformed = CompleteDefinition(document, epoch);
        malformed.Footprint.Definition.Items[0] = new()
        {
            TypeUrl = "type.googleapis.com/kiapi.board.types.FutureFootprintItem",
            Value = Google.Protobuf.ByteString.CopyFromUtf8("not-a-pad")
        };
        Reject(() => FootprintLibraryContract.ValidateDefinitionResponse(malformed, document, epoch,
            (NativeFootprintDefinitionSource)2, document.LibId), "footprint_definition_incomplete");

        NativeCapabilityFootprintReadDefinitionResponse badHash = CompleteDefinition(document, epoch);
        badHash.CanonicalSha256 = "not-a-sha";
        Reject(() => FootprintLibraryContract.ValidateDefinitionResponse(badHash, document, epoch,
            (NativeFootprintDefinitionSource)2, document.LibId), "footprint_definition_incomplete");
    }

    [TestMethod]
    public void DefinitionCoverageKeepsIncompleteNativeResponsesTruthful()
    {
        const string epoch = "99999999-9999-9999-9999-999999999999";
        DocumentSpecifier document = Document("Incomplete");
        NativeCapabilityFootprintReadDefinitionResponse response = CompleteDefinition(document, epoch);
        response.DefinitionComplete = false;
        response.Limitations.Add("native provider omitted a property");

        FootprintLibraryContract.FootprintDefinitionCoverage coverage =
            FootprintLibraryContract.ValidateDefinitionResponse(response, document, epoch,
                (NativeFootprintDefinitionSource)2, document.LibId);

        Assert.IsFalse(coverage.Complete);
        Assert.AreEqual("incomplete", coverage.Status);
        CollectionAssert.Contains(coverage.Limitations, "native provider omitted a property");
    }

    private static DocumentSpecifier Document(string entryName) => new()
    {
        Type = DocumentType.DoctypeFootprint,
        LibId = new() { LibraryNickname = "Fixture", EntryName = entryName }
    };

    private static NativeCapabilityFootprintReadDefinitionResponse CompleteDefinition(
        DocumentSpecifier document, string processEpoch)
    {
        var definition = new Footprint
        {
            Id = document.LibId.Clone(),
            Attributes = new(),
            Overrides = new(),
            ReferenceField = new(),
            ValueField = new(),
            DatasheetField = new(),
            DescriptionField = new(),
            Jumpers = new()
        };
        definition.Items.Add(Any.Pack(new Pad
        {
            Id = new() { Value = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" },
            Number = "1",
            PadStack = new()
            {
                Layers = { BoardLayer.BlFCu },
                CopperLayers = { new PadStackLayer
                {
                    Layer = BoardLayer.BlFCu,
                    Shape = PadStackShape.PssCircle,
                    Size = new() { XNm = 1_000, YNm = 1_000 }
                } }
            }
        }));
        definition.Items.Add(Any.Pack(new Pad
        {
            Id = new() { Value = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" },
            Type = PadType.PtNpth,
            Number = "",
            PadStack = new()
            {
                Layers = { BoardLayer.BlFCu },
                CopperLayers = { new PadStackLayer
                {
                    Layer = BoardLayer.BlFCu,
                    Shape = PadStackShape.PssCircle,
                    Size = new() { XNm = 1_000, YNm = 1_000 }
                } }
            }
        }));
        definition.Items.Add(Any.Pack(new Kiapi.Board.Types.Field { Text = new() }));
        definition.Items.Add(Any.Pack(new ReferencePoint
        {
            Id = new() { Value = "cccccccc-cccc-cccc-cccc-cccccccccccc" }
        }));
        definition.Items.Add(Any.Pack(new Zone
        {
            Id = new() { Value = "dddddddd-dddd-dddd-dddd-dddddddddddd" }
        }));
        definition.Items.Add(Any.Pack(new Footprint3DModel { Filename = "probe.step" }));
        var response = new NativeCapabilityFootprintReadDefinitionResponse
        {
            Document = document.Clone(),
            ProcessEpoch = processEpoch,
            Footprint = new() { Definition = definition },
            CanonicalSha256 = new string('a', 64),
            ProviderSha256 = new string('b', 64),
            DefinitionComplete = true,
            CanonicalTarget = document.LibId.Clone(),
            FileBaseline = new()
            {
                BaselineKnown = true, BaselineExists = true,
                CurrentKnown = true, CurrentExists = true,
                Status = NativeFileBaselineStatus.NfbsUnchanged
            }
        };
        return response;
    }

    private static void Reject(Action action, string expectedCode)
    {
        AutomationException error = Assert.ThrowsExactly<AutomationException>(action);
        Assert.AreEqual(expectedCode, error.Code);
    }
}
