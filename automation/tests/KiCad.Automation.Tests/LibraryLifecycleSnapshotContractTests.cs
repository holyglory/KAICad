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
    public void ReadOnlyFoundationNeverAdmitsCheckedMutation()
    {
        (LibraryLifecycleSnapshot snapshot, DocumentSpecifier document, string projectPath) = Fixture();

        Reject(() => LibraryLifecycleSnapshotContract.AdmitCheckedMutation(
            snapshot, document, "fixture", projectPath), "library_mutation_unavailable");

        Reject(() => LibraryLifecycleSnapshotContract.ValidateReadOnly(
            snapshot with { CompleteChangeTracking = true }, document, "fixture", projectPath),
            "library_snapshot_incomplete");
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
            [new LibraryLifecycleDependencyBaseline(libraryPath, new string('a', 64), 7)],
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
