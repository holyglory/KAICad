using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class LibraryLifecycleSnapshotContractTests
{
    [TestMethod]
    public void ReadSnapshotRequiresExactProjectDocumentAndCompleteEvidence()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = Fixture();

        LibraryLifecycleSnapshotContract.ValidateReadOnly(snapshot, document, "fixture", projectPath);

        var differentDocument = document.Clone();
        differentDocument.LibId.EntryName = "Other";
        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot, differentDocument, "fixture", projectPath), "library_document_mismatch");
    }

    [TestMethod]
    public void ForeignProjectAndProvidersAreRefusedBeforeMutation()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = Fixture();

        var foreignDocument = document.Clone();
        foreignDocument.Project.Name = "foreign";
        var foreignSnapshot = snapshot with { Document = foreignDocument };
        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            foreignSnapshot, foreignDocument, "fixture", projectPath), "library_project_mismatch");

        var unsupported = snapshot with { Provider = "http" };
        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            unsupported, document, "fixture", projectPath), "library_provider_unsupported");

        var unknown = snapshot with { Provider = "" };
        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            unknown, document, "fixture", projectPath), "library_provider_unknown");
    }

    [TestMethod]
    public void RelativeProjectPathsAreRejectedEvenWhenExpectedPathMatches()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, _) = Fixture();
        var relativeDocument = document.Clone();
        relativeDocument.Project.Path = "relative/project";
        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { Document = relativeDocument }, relativeDocument, "fixture", "relative/project"),
            "library_project_mismatch");
    }

    [TestMethod]
    public void AbsoluteDotAliasesRemainValid()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = Fixture();
        string tableAlias = Path.Combine(projectPath, "tables", "..", "sym-lib-table");
        string libraryAlias = Path.Combine(projectPath, "libs", "..", "libs", "fixture.kicad_sym");
        var aliased = snapshot with
        {
            TablePath = tableAlias,
            ExpandedUri = libraryAlias,
            DependencyBaselines =
            [
                new LibraryLifecycleDependencyBaseline(tableAlias, new string('a', 64), 7),
                new LibraryLifecycleDependencyBaseline(libraryAlias, new string('b', 64), 7)
            ]
        };

        LibraryLifecycleSnapshotContract.ValidateReadOnly(aliased, document, "fixture",
            Path.Combine(projectPath, "tables", ".."));
    }

    [TestMethod]
    public void RelativeTableAndExpandedUriAreRejected()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = Fixture();

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { TablePath = "relative/sym-lib-table" }, document, "fixture", projectPath),
            "library_table_row_mismatch");

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { ExpandedUri = "relative/fixture.kicad_sym" }, document, "fixture", projectPath),
            "library_table_row_mismatch");
    }

    [TestMethod]
    public void NulTableAndExpandedUriAreRejected()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = Fixture();

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { TablePath = snapshot.TablePath + "\0" }, document, "fixture", projectPath),
            "library_table_row_mismatch");

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { ExpandedUri = snapshot.ExpandedUri + "\0" }, document, "fixture", projectPath),
            "library_table_row_mismatch");
    }

    [TestMethod]
    public void IncompleteSnapshotAndPartialHashOrCursorCannotBeAdmitted()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = Fixture();

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { ForcedRefresh = false }, document, "fixture", projectPath), "library_snapshot_incomplete");

        var partialHash = snapshot with
        {
            DependencyBaselines = [new LibraryLifecycleDependencyBaseline(
                snapshot.DependencyBaselines[0].Path, "cursor", 7)]
        };
        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            partialHash, document, "fixture", projectPath), "library_baseline_incomplete");

        var cursor = snapshot with
        {
            DependencyBaselines = [snapshot.DependencyBaselines[0] with { Cursor = "line:17" }]
        };
        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            cursor, document, "fixture", projectPath), "library_baseline_incomplete");
    }

    [TestMethod]
    public void RequiresExistingTableAndLibraryBaselines()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = Fixture();

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { DependencyBaselines = [snapshot.DependencyBaselines[1]] },
            document, "fixture", projectPath), "library_baseline_incomplete");

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { DependencyBaselines = [snapshot.DependencyBaselines[0]] },
            document, "fixture", projectPath), "library_baseline_incomplete");

        var absentTable = snapshot with
        {
            DependencyBaselines =
            [
                snapshot.DependencyBaselines[0] with { Exists = false, Sha256 = "", Bytes = 0 },
                snapshot.DependencyBaselines[1]
            ]
        };
        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            absentTable, document, "fixture", projectPath), "library_baseline_incomplete");

        var unknownLibrary = snapshot with
        {
            DependencyBaselines =
            [
                snapshot.DependencyBaselines[0],
                snapshot.DependencyBaselines[1] with { Known = false, Sha256 = "", Bytes = 0 }
            ]
        };
        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            unknownLibrary, document, "fixture", projectPath), "library_baseline_incomplete");
    }

    [TestMethod]
    public void DuplicateNormalizedDependencyAliasesAreRejected()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = Fixture();
        string libraryAlias = Path.Combine(projectPath, "libs", "..", "libs", "fixture.kicad_sym");
        var duplicate = snapshot with
        {
            DependencyBaselines =
            [
                snapshot.DependencyBaselines[0],
                snapshot.DependencyBaselines[1],
                new LibraryLifecycleDependencyBaseline(libraryAlias, new string('c', 64), 7)
            ]
        };
        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            duplicate, document, "fixture", projectPath), "library_baseline_incomplete");
    }

    [TestMethod]
    public void DirectoryAndNestedSymbolFileShapesAreRejected()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = Fixture();
        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { ExpandedUri = Path.Combine(projectPath, "libs") },
            document, "fixture", projectPath), "library_table_row_mismatch");

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { ExpandedUri = Path.Combine(projectPath, "folder.kicad_sym", "fixture.kicad_sym") },
            document, "fixture", projectPath), "library_table_row_mismatch");
    }

    [TestMethod]
    public void ReadOnlyFoundationNeverAdmitsCheckedMutation()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = Fixture();

        Reject(() => LibraryLifecycleSnapshotContract.AdmitCheckedMutation(
            snapshot, document, "fixture", projectPath), "library_mutation_unavailable");

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { CompleteChangeTracking = true }, document, "fixture", projectPath),
            "library_snapshot_incomplete");
    }

    [TestMethod]
    public void FootprintSnapshotAcceptsDirectPrettyEntryAndRejectsNestedEntry()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = FootprintFixture();

        LibraryLifecycleSnapshotContract.ValidateReadOnly(snapshot, document, "fixture", projectPath);

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { ExpandedUri = Path.Combine(projectPath, "libs", "fixture.pretty.kicad_mod", "Probe.kicad_mod") },
            document, "fixture", projectPath), "library_table_row_mismatch");
    }

    [TestMethod]
    public void FootprintSnapshotRejectsNonPrettyRowsAndExternalProvider()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = FootprintFixture();

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { RawUri = "${KIPRJMOD}/libs/fixture.kicad_mod" },
            document, "fixture", projectPath), "library_table_row_mismatch");

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { Provider = "http" }, document, "fixture", projectPath),
            "library_provider_unsupported");
    }

    [TestMethod]
    public void LibraryTransactionAdmissionRefusesStaleForeignAndPartialObservations()
    {
        (LibraryTransactionRequest request, LibraryLifecycleState state, DocumentSpecifier document, string projectPath) = TransactionFixture();

        LibraryTransactionContract.ValidateAdmission(request, state);

        var stale = state with { Revision = state.Revision with { Sequence = state.Revision.Sequence + 1 } };
        Reject(() => LibraryTransactionContract.ValidateAdmission(request, stale), "stale_library_state");

        var foreignProcess = state with { ProcessEpoch = Guid.NewGuid().ToString("D") };
        Reject(() => LibraryTransactionContract.ValidateAdmission(request, foreignProcess), "foreign_library_process");

        var foreignDocument = document.Clone();
        foreignDocument.LibId.EntryName = "Other";
        Reject(() => LibraryTransactionContract.ValidateAdmission(request,
            state with { Document = foreignDocument }), "foreign_library_document");

        var partialCursor = state with
        {
            FileBaselines = [state.FileBaselines[0] with { Cursor = "cursor:17" }, state.FileBaselines[1]]
        };
        Reject(() => LibraryTransactionContract.ValidateAdmission(request, partialCursor), "partial_library_state");

        var partialHash = state with
        {
            FileBaselines = [state.FileBaselines[0] with { Sha256 = "partial" }, state.FileBaselines[1]]
        };
        Reject(() => LibraryTransactionContract.ValidateAdmission(request, partialHash), "partial_library_state");
    }

    [TestMethod]
    public void LibraryTransactionFingerprintBindsCanonicalRequestAndReplayIdentity()
    {
        (LibraryTransactionRequest request, LibraryLifecycleState state, _, _) = TransactionFixture();
        var rejected = new LibraryTransactionReceipt(request, LibraryTransactionState.Rejected,
            RequestVerified: true, NativeWriteStarted: false, CancellationObservedAfterWrite: false,
            ObservedBefore: state, ObservedAfter: null, ObservedRollback: null,
            Postverified: false, RollbackAttempted: false, RollbackVerified: false,
            ErrorCode: "admission_refused", ErrorMessage: "provider refused");
        LibraryTransactionContract.ValidateReceipt(request, rejected, inspect: true);
        LibraryTransactionContract.ValidateReplay(request, rejected);

        var reorderedState = state with
        {
            FileBaselines = [state.FileBaselines[1], state.FileBaselines[0]]
        };
        var reordered = request with { ExpectedState = reorderedState };
        reordered = reordered with
        {
            RequestFingerprintSha256 = LibraryTransactionContract.ComputeFingerprint(reordered)
        };
        Assert.AreEqual(request.RequestFingerprintSha256, reordered.RequestFingerprintSha256,
            "Dependency-set order must not change the canonical request identity.");
        LibraryTransactionContract.ValidateReplay(reordered, rejected);

        var changedPayload = request with { OperationPayload = "(library-edit v2)" };
        changedPayload = changedPayload with
        {
            RequestFingerprintSha256 = LibraryTransactionContract.ComputeFingerprint(changedPayload)
        };
        Reject(() => LibraryTransactionContract.ValidateReplay(changedPayload, rejected), "operation_id_conflict");

        var forged = request with { RequestFingerprintSha256 = new string('a', 64) };
        Reject(() => LibraryTransactionContract.ValidateRequest(forged), "invalid_library_transaction");
    }

    [TestMethod]
    public void LibraryTransactionReceiptsRequirePostverifyOrVerifiedRollback()
    {
        (LibraryTransactionRequest request, LibraryLifecycleState state, _, _) = TransactionFixture();
        var after = state with
        {
            Revision = state.Revision with { Sequence = state.Revision.Sequence + 1 },
            FullStateSha256 = new string('b', 64),
            FileBaselines = [
                state.FileBaselines[0],
                state.FileBaselines[1] with { Sha256 = new string('c', 64) }
            ]
        };
        var committed = new LibraryTransactionReceipt(request, LibraryTransactionState.CommittedPostverified,
            RequestVerified: true, NativeWriteStarted: true, CancellationObservedAfterWrite: false,
            ObservedBefore: state, ObservedAfter: after, ObservedRollback: null,
            Postverified: true, RollbackAttempted: false, RollbackVerified: false);
        LibraryTransactionContract.ValidateReceipt(request, committed);

        var missingAfter = committed with { ObservedAfter = null };
        Reject(() => LibraryTransactionContract.ValidateReceipt(request, missingAfter), "invalid_library_transaction");

        var rolledBack = new LibraryTransactionReceipt(request, LibraryTransactionState.RolledBackVerified,
            RequestVerified: true, NativeWriteStarted: true, CancellationObservedAfterWrite: false,
            ObservedBefore: state, ObservedAfter: null, ObservedRollback: state,
            Postverified: false, RollbackAttempted: true, RollbackVerified: true,
            ErrorCode: "native_rejected", ErrorMessage: "provider refused");
        LibraryTransactionContract.ValidateReceipt(request, rolledBack);

        var forgedRollback = rolledBack with { ObservedRollback = after };
        Reject(() => LibraryTransactionContract.ValidateReceipt(request, forgedRollback), "invalid_library_transaction");
    }

    [TestMethod]
    public void CancellationAfterWriteIsIndeterminateUntilInspected()
    {
        (LibraryTransactionRequest request, LibraryLifecycleState state, _, _) = TransactionFixture();
        var indeterminate = new LibraryTransactionReceipt(request, LibraryTransactionState.Indeterminate,
            RequestVerified: true, NativeWriteStarted: true, CancellationObservedAfterWrite: true,
            ObservedBefore: state, ObservedAfter: null, ObservedRollback: null,
            Postverified: false, RollbackAttempted: false, RollbackVerified: false,
            ErrorCode: "cancelled_after_native_write", ErrorMessage: "native outcome is unknown");
        LibraryTransactionContract.ValidateReceipt(request, indeterminate, inspect: true);
        LibraryTransactionContract.ValidateReceipt(request, indeterminate);

        var forgedRejected = new LibraryTransactionReceipt(request, LibraryTransactionState.Rejected,
            RequestVerified: true, NativeWriteStarted: true, CancellationObservedAfterWrite: true,
            ObservedBefore: state, ObservedAfter: null, ObservedRollback: null,
            Postverified: false, RollbackAttempted: false, RollbackVerified: false,
            ErrorCode: "cancelled_after_native_write", ErrorMessage: "native outcome is unknown");
        Reject(() => LibraryTransactionContract.ValidateReceipt(request, forgedRejected), "invalid_library_transaction");

        var rollbackUnknown = new LibraryTransactionReceipt(request, LibraryTransactionState.RollbackUnknown,
            RequestVerified: true, NativeWriteStarted: true, CancellationObservedAfterWrite: true,
            ObservedBefore: state, ObservedAfter: null, ObservedRollback: null,
            Postverified: false, RollbackAttempted: true, RollbackVerified: false,
            ErrorCode: "rollback_unknown", ErrorMessage: "rollback was requested but not verified");
        LibraryTransactionContract.ValidateReceipt(request, rollbackUnknown, inspect: true);
    }

    [TestMethod]
    [DataRow("symbol-clone->write")]
    [DataRow("symbol-write")]
    [DataRow("symbol-delete")]
    [DataRow("footprint-library-delete")]
    [DataRow("footprint-save")]
    [DataRow("footprint-save-copy")]
    [DataRow("footprint-revert")]
    public void LegacyLibraryMutationEvidenceCannotCrossCheckedAdmission(string mutationPath)
    {
        // These are the currently qualified managed/native entry points.  They
        // carry only a modify hash (symbol paths), an editor snapshot digest
        // (footprint paths), or neither.  None carries the complete process,
        // revision, full-state and dependency-baseline observation required by
        // LibraryTransactionContract.  Keep the test here as the integration
        // seam until each writer publishes that observation.
        (LibraryTransactionRequest request, _, _, _) = TransactionFixture();
        var legacyState = request.ExpectedState with
        {
            FullStateSha256 = new string('a', 64),
            FileBaselines = [],
            CompleteState = false
        };
        var legacyRequest = request with { ExpectedState = legacyState };
        legacyRequest = legacyRequest with
        {
            RequestFingerprintSha256 = LibraryTransactionContract.ComputeFingerprint(legacyRequest)
        };

        Reject(() => LibraryTransactionContract.ValidateAdmission(legacyRequest, legacyState),
            "partial_library_state");
        Assert.IsFalse(string.IsNullOrWhiteSpace(mutationPath));
    }

    private static (LibraryLifecycleSnapshot Snapshot, DocumentSpecifier Document, string ProjectPath) Fixture()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "kicad-library-lifecycle-fixture");
        string libraryPath = Path.Combine(projectPath, "libs", "fixture.kicad_sym");
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeSymbol,
            Project = new() { Name = "fixture", Path = projectPath },
            LibId = new() { LibraryNickname = "Fixture", EntryName = "Probe" }
        };
        var snapshot = new LibraryLifecycleSnapshot(
            document, Path.Combine(projectPath, "sym-lib-table"), "PROJECT", "Fixture",
            "${KIPRJMOD}/libs/fixture.kicad_sym", libraryPath, "kicad-symbol-file",
            "{\"id\":\"Fixture:Probe\"}",
            [
                new LibraryLifecycleDependencyBaseline(Path.Combine(projectPath, "sym-lib-table"), new string('b', 64), 11),
                new LibraryLifecycleDependencyBaseline(libraryPath, new string('a', 64), 7)
            ],
            ForcedRefresh: true, ProviderObservable: true, ContentSnapshotComplete: true,
            DiskBaselineChecked: true, CompleteChangeTracking: false, Postverify: false);
        return (snapshot, document, projectPath);
    }

    private static (LibraryLifecycleSnapshot Snapshot, DocumentSpecifier Document, string ProjectPath) FootprintFixture()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "kicad-library-lifecycle-footprint-fixture");
        string libraryPath = Path.Combine(projectPath, "libs", "fixture.pretty", "Probe.kicad_mod");
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeFootprint,
            Project = new() { Name = "fixture", Path = projectPath },
            LibId = new() { LibraryNickname = "Fixture", EntryName = "Probe" }
        };
        var snapshot = new LibraryLifecycleSnapshot(
            document, Path.Combine(projectPath, "fp-lib-table"), "PROJECT", "Fixture",
            "${KIPRJMOD}/libs/fixture.pretty", libraryPath, "kicad-footprint-file",
            "(footprint (version 20240108) (generator pcbnew))",
            [
                new LibraryLifecycleDependencyBaseline(Path.Combine(projectPath, "fp-lib-table"), new string('b', 64), 11),
                new LibraryLifecycleDependencyBaseline(libraryPath, new string('a', 64), 7)
            ],
            ForcedRefresh: true, ProviderObservable: true, ContentSnapshotComplete: true,
            DiskBaselineChecked: true, CompleteChangeTracking: false, Postverify: false);
        return (snapshot, document, projectPath);
    }

    private static (LibraryTransactionRequest Request, LibraryLifecycleState State,
        DocumentSpecifier Document, string ProjectPath) TransactionFixture()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "kicad-library-transaction-fixture");
        string libraryPath = Path.Combine(projectPath, "libs", "fixture.kicad_sym");
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeSymbol,
            Project = new() { Name = "fixture", Path = projectPath },
            LibId = new() { LibraryNickname = "Fixture", EntryName = "Probe" }
        };
        var state = new LibraryLifecycleState(document, Guid.NewGuid().ToString("D"),
            new LibraryDocumentRevision(Guid.NewGuid().ToString("D"), 4), new string('a', 64),
            [
                new LibraryFileBaseline(Path.Combine(projectPath, "sym-lib-table"), true, new string('b', 64), 11),
                new LibraryFileBaseline(libraryPath, true, new string('c', 64), 7)
            ]);
        var request = new LibraryTransactionRequest(Guid.NewGuid().ToString("D"), document,
            state.ProcessEpoch, state, "update", "(library-edit v1)", "");
        return (request with { RequestFingerprintSha256 = LibraryTransactionContract.ComputeFingerprint(request) },
            state, document, projectPath);
    }

    private static void Reject(Action action, string expectedCode)
    {
        AutomationException error = Assert.ThrowsExactly<AutomationException>(action);
        Assert.AreEqual(expectedCode, error.Code);
    }
}
