using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
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

    private static void Reject(Action action, string expectedCode)
    {
        AutomationException error = Assert.ThrowsExactly<AutomationException>(action);
        Assert.AreEqual(expectedCode, error.Code);
    }
}
