using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SymbolLibraryTransactionAdmissionTests
{
    [TestMethod]
    public void BuildRequestBindsExplicitDocumentProcessCheckpointAndPayload()
    {
        (DocumentSpecifier document, LibraryLifecycleState state) = Fixture();
        string operationId = Guid.NewGuid().ToString("D");

        LibraryTransactionRequest request = SymbolLibraryTransactionAdmission.BuildRequest(
            operationId, document, state.ProcessEpoch, state, "write",
            "{\"symbol\":\"Probe\",\"overwrite\":true}");

        Assert.AreEqual(operationId, request.OperationId);
        Assert.AreEqual(state.ProcessEpoch, request.ProcessEpoch);
        Assert.AreEqual("write", request.Operation);
        Assert.AreEqual("{\"symbol\":\"Probe\",\"overwrite\":true}", request.OperationPayload);
        Assert.AreEqual(document, request.Document);
        Assert.AreEqual(state.ProcessEpoch, request.ExpectedState.ProcessEpoch);
        Assert.AreEqual(state.Revision, request.ExpectedState.Revision);
        Assert.AreEqual(state.FullStateSha256, request.ExpectedState.FullStateSha256);
        CollectionAssert.AreEqual(state.FileBaselines.ToArray(), request.ExpectedState.FileBaselines.ToArray());
        LibraryTransactionContract.ValidateRequest(request);
    }

    [TestMethod]
    public void ValidAdmissionFailsClosedBeforeAnyLegacyWriterCanRun()
    {
        (DocumentSpecifier document, LibraryLifecycleState state) = Fixture();
        LibraryTransactionRequest request = SymbolLibraryTransactionAdmission.BuildRequest(
            Guid.NewGuid().ToString("D"), document, state.ProcessEpoch, state,
            "delete", "{\"symbol\":\"Probe\"}");

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryTransactionAdmission.Admit(request, state));

        Assert.AreEqual("library_mutation_unavailable", error.Code);
        StringAssert.Contains(error.Message, "no native writer was called");
    }

    [TestMethod]
    public void AdmissionRefusesStaleForeignAndPartialObservationsWithExactCodes()
    {
        (DocumentSpecifier document, LibraryLifecycleState state) = Fixture();
        LibraryTransactionRequest request = SymbolLibraryTransactionAdmission.BuildRequest(
            Guid.NewGuid().ToString("D"), document, state.ProcessEpoch, state,
            "clone", "{\"source\":\"Fixture:Probe\"}");

        Reject(() => SymbolLibraryTransactionAdmission.Admit(request,
            state with { Revision = state.Revision with { Sequence = state.Revision.Sequence + 1 } }),
            "stale_library_state");

        Reject(() => SymbolLibraryTransactionAdmission.Admit(request,
            state with { ProcessEpoch = Guid.NewGuid().ToString("D") }),
            "foreign_library_process");

        DocumentSpecifier foreign = document.Clone();
        foreign.LibId.EntryName = "Other";
        Reject(() => SymbolLibraryTransactionAdmission.Admit(request,
            state with { Document = foreign }), "foreign_library_document");

        Reject(() => SymbolLibraryTransactionAdmission.Admit(request,
            state with
            {
                FileBaselines = [state.FileBaselines[0] with { Cursor = "cursor:17" }, state.FileBaselines[1]]
            }), "partial_library_state");
    }

    [TestMethod]
    public void ReplayValidatesExactReceiptThenFailsClosedWithoutRetryingNativeOperation()
    {
        (DocumentSpecifier document, LibraryLifecycleState state) = Fixture();
        LibraryTransactionRequest request = SymbolLibraryTransactionAdmission.BuildRequest(
            Guid.NewGuid().ToString("D"), document, state.ProcessEpoch, state,
            "write", "{\"symbol\":\"Probe\",\"overwrite\":false}");
        var rejected = new LibraryTransactionReceipt(request, LibraryTransactionState.Rejected,
            RequestVerified: true, NativeWriteStarted: false, CancellationObservedAfterWrite: false,
            ObservedBefore: state, ObservedAfter: null, ObservedRollback: null,
            Postverified: false, RollbackAttempted: false, RollbackVerified: false,
            ErrorCode: "library_mutation_unavailable",
            ErrorMessage: "The checked native owner is not available.");

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryTransactionAdmission.Replay(request, rejected));
        Assert.AreEqual("library_mutation_unavailable", error.Code);

        LibraryTransactionRequest changed = request with { OperationPayload = "{\"symbol\":\"Other\"}" };
        changed = changed with { RequestFingerprintSha256 = LibraryTransactionContract.ComputeFingerprint(changed) };
        Reject(() => SymbolLibraryTransactionAdmission.Replay(changed, rejected), "operation_id_conflict");
    }

    [TestMethod]
    public void MissingCompleteCheckpointFailsClosedBeforeRequestConstruction()
    {
        (DocumentSpecifier document, LibraryLifecycleState state) = Fixture();

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryTransactionAdmission.BuildRequest(
                Guid.NewGuid().ToString("D"), document, state.ProcessEpoch, null!,
                "write", "{}"));

        Assert.AreEqual("library_mutation_unavailable", error.Code);
    }

    private static (DocumentSpecifier Document, LibraryLifecycleState State) Fixture()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "kicad-symbol-library-admission");
        string libraryPath = Path.Combine(projectPath, "libs", "fixture.kicad_sym");
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeSymbol,
            Project = new() { Name = "fixture", Path = projectPath },
            LibId = new() { LibraryNickname = "Fixture", EntryName = "Probe" }
        };
        var state = new LibraryLifecycleState(
            document,
            Guid.NewGuid().ToString("D"),
            new LibraryDocumentRevision(Guid.NewGuid().ToString("D"), 7),
            new string('a', 64),
            [
                new LibraryFileBaseline(Path.Combine(projectPath, "sym-lib-table"), true, new string('b', 64), 11),
                new LibraryFileBaseline(libraryPath, true, new string('c', 64), 7)
            ]);
        return (document, state);
    }

    private static void Reject(Action action, string expectedCode)
    {
        AutomationException error = Assert.ThrowsExactly<AutomationException>(action);
        Assert.AreEqual(expectedCode, error.Code);
    }
}
