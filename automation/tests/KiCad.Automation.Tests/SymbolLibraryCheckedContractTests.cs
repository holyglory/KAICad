using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SymbolLibraryCheckedContractTests
{
    [TestMethod]
    public void UnavailableResponseIsAnHonestTerminalReceipt()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "unavailable", errorCode: "library_mutation_unavailable",
            errorMessage: "The native lifecycle owner is not available.", replayed: true);

        SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected);
    }

    [TestMethod]
    public void CommittedResponseRequiresCompleteSnapshotAndLifecycleEvidence()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "committed", committed: true,
            before: expected, after: new string('b', 64));

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected));

        Assert.AreEqual("invalid_symbol_library_response", error.Code);
    }

    [TestMethod]
    public void CommittedResponseBindsTheExpectedSnapshotAndIsAccepted()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "committed_postverified", committed: true, revisionTracked: true,
            completeLifecycle: true, before: expected, after: new string('b', 64));

        SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected);
    }

    [TestMethod]
    public void CommitFromAnotherSnapshotIsRejectedAsStale()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "committed", committed: true, revisionTracked: true, completeLifecycle: true,
            before: new string('c', 64), after: new string('b', 64));

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected));

        Assert.AreEqual("stale_library_state", error.Code);
    }

    [TestMethod]
    public void IdentityAndErrorShapeAreCheckedBeforeReceiptUse()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "unavailable", errorCode: "library_mutation_unavailable");

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected));

        Assert.AreEqual("invalid_symbol_library_response", error.Code);
    }

    [TestMethod]
    public void RollbackReceiptMustProveRollbackCompletion()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "rolled_back", errorCode: "symbol_library_write_failed", errorMessage: "write failed");

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected));

        Assert.AreEqual("invalid_symbol_library_response", error.Code);
    }

    [TestMethod]
    public void DirectFileReceiptRequiresTargetUnrelatedAndAdvancingRevisionEvidence()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        var before = State(document, epoch, sequence: 7, expected);
        string expectedSnapshot = before.StateSha256;
        var after = State(document, epoch, sequence: 8, new string('b', 64));
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "committed_postverified", committed: true, revisionTracked: true,
            completeLifecycle: false, before: expectedSnapshot, after: after.StateSha256);
        response.ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.SlcwsCommittedPostverified;
        response.Scope = "mcp_direct_symbol_file";
        response.RequestFingerprintSha256 = new string('c', 64);
        response.ExpectedRequestVerified = true;
        response.NativeWriteStarted = true;
        response.Postverified = true;
        response.TargetVerified = true;
        response.UnrelatedEntriesVerified = true;
        response.ObservedBefore = before;
        response.ObservedAfter = after;
        response.Request = new NativeCapabilitySymbolLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = epoch, OperationId = operation,
            ExpectedSnapshotSha256 = expectedSnapshot, ExpectedState = before
        };
        response.RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(response.Request);

        SymbolLibraryCheckedContract.ValidateResponse(response, response.Request);
    }

    [TestMethod]
    public void DirectFileReceiptRejectsCommitWithoutPostverificationProof()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        var response = Response(document, epoch, operation,
            status: "committed", committed: true, revisionTracked: true,
            completeLifecycle: true, before: expected, after: new string('b', 64));
        response.ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.SlcwsCommittedPostverified;
        response.Scope = "mcp_direct_symbol_file";
        response.RequestFingerprintSha256 = new string('c', 64);
        response.ExpectedRequestVerified = true;
        response.NativeWriteStarted = true;
        response.Request = new NativeCapabilitySymbolLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = epoch, OperationId = operation,
            ExpectedSnapshotSha256 = expected
        };
        response.RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(response.Request);

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, response.Request));

        Assert.AreEqual("invalid_symbol_library_response", error.Code);
    }

    [TestMethod]
    public void PreAdmissionRejectionMayUseLegacyUnknownReceiptStatus()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "rejected", errorCode: "invalid_library_document",
            errorMessage: "The project-bound symbol document is invalid.");

        // Identity was rejected before a request fingerprint existed.  The UNKNOWN enum is an
        // honest pre-admission terminal envelope and remains backward compatible with older peers.
        SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected);
    }

    private static NativeCapabilitySymbolLibraryCheckedWriteResponse Response(
        DocumentSpecifier document, string epoch, string operation, string status,
        bool committed = false, bool replayed = false, bool rollbackCompleted = false,
        bool revisionTracked = false, bool completeLifecycle = false,
        string? errorCode = null, string? errorMessage = null,
        string? before = null, string? after = null) => new()
        {
            Document = document.Clone(), ProcessEpoch = epoch, OperationId = operation,
            Status = status, Committed = committed, Replayed = replayed,
            RollbackCompleted = rollbackCompleted, RevisionTracked = revisionTracked,
            CompleteLifecycle = completeLifecycle, ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
            BeforeSnapshotSha256 = before ?? string.Empty, AfterSnapshotSha256 = after ?? string.Empty
        };

    private static (DocumentSpecifier Document, string Epoch, string Operation, string Expected) Fixture()
    {
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeSymbol,
            Project = new() { Name = "fixture", Path = "/tmp/kicad-symbol-checked" },
            LibId = new() { LibraryNickname = "Fixture", EntryName = "Probe" }
        };
        return (document, "epoch", "operation", new string('a', 64));
    }

    private static NativeCapabilitySymbolLibraryCheckedState State(DocumentSpecifier document,
        string epoch, ulong sequence, string stateSha)
    {
        var state = new NativeCapabilitySymbolLibraryCheckedState
        {
            Document = document.Clone(), ProcessEpoch = epoch,
            Revision = new() { Epoch = epoch, Sequence = sequence }, Scope = "mcp_direct_symbol_file",
            StateComplete = true, DiskBaselineChecked = true, CompleteChangeTracking = false,
            DependencyBaselines = { new NativeCapabilityLibrarySnapshotDependencyBaseline
            {
                Path = "/tmp/fixture.kicad_sym", Exists = true, Known = true,
                Sha256 = stateSha, Bytes = 7
            } }
        };
        state.StateSha256 = SymbolLibraryCheckedContract.ComputeStateDigest(state);
        return state;
    }
}
