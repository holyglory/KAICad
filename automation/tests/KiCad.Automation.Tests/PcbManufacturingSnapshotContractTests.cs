using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbManufacturingSnapshotContractTests
{
    [TestMethod]
    public void DetachedSnapshotRequiresSerializedBoardProjectAndDependencySet()
    {
        (PcbManufacturingDetachedSnapshot snapshot, DocumentSpecifier document) = Fixture();

        PcbManufacturingSnapshotContract.Validate(snapshot, document);

        Reject(snapshot with { BoardSerializationComplete = false });
        Reject(snapshot with { ProjectSerializationComplete = false });
        Reject(snapshot with { DependencyCaptureComplete = false });
        Reject(snapshot with { DiskBaselineChecked = false });
    }

    [TestMethod]
    public void DetachedSnapshotRequiresBoardAndProjectBaselines()
    {
        (PcbManufacturingDetachedSnapshot snapshot, DocumentSpecifier document) = Fixture();

        string boardPath = snapshot.DependencyBaselines[0].Path;
        Reject(snapshot with
        {
            DependencyBaselines = snapshot.DependencyBaselines
                .Where(baseline => !string.Equals(baseline.Path, boardPath, StringComparison.Ordinal))
                .ToArray()
        });

        var absentProject = snapshot with
        {
            DependencyBaselines = snapshot.DependencyBaselines
                .Select(baseline => baseline.Path.EndsWith(".kicad_pro", StringComparison.Ordinal)
                    ? baseline with { Exists = false, Sha256 = "", Bytes = 0 } : baseline)
                .ToArray()
        };
        Reject(absentProject);
    }

    [TestMethod]
    public void DetachedSnapshotRejectsDuplicateOrPartialDependencyBaselines()
    {
        (PcbManufacturingDetachedSnapshot snapshot, DocumentSpecifier document) = Fixture();
        var duplicate = snapshot with
        {
            DependencyBaselines = [.. snapshot.DependencyBaselines, snapshot.DependencyBaselines[0]]
        };
        Reject(duplicate);

        var partial = snapshot with
        {
            DependencyBaselines = snapshot.DependencyBaselines
                .Select(baseline => baseline.Path.EndsWith(".kicad_pro", StringComparison.Ordinal)
                    ? baseline with { Sha256 = "partial" } : baseline)
                .ToArray()
        };
        Reject(partial);
    }

    [TestMethod]
    public void DetachedSnapshotRejectsMalformedSerializedInputsAndDigestDrift()
    {
        (PcbManufacturingDetachedSnapshot snapshot, DocumentSpecifier document) = Fixture();
        Reject(snapshot with { SerializedBoard = "(not-a-board)" });
        Reject(snapshot with { SerializedProject = "not-json" });
        Reject(snapshot with { SnapshotSha256 = new string('f', 64) });
    }

    [TestMethod]
    public void DetachedSnapshotDigestIsOrderIndependentForDependencies()
    {
        (PcbManufacturingDetachedSnapshot snapshot, DocumentSpecifier document) = Fixture();
        PcbManufacturingDetachedSnapshot reordered = snapshot with
        {
            DependencyBaselines = snapshot.DependencyBaselines.Reverse().ToArray()
        };
        reordered = reordered with
        {
            SnapshotSha256 = PcbManufacturingSnapshotContract.ComputeSnapshotSha256(reordered)
        };
        PcbManufacturingSnapshotContract.Validate(reordered, document);
        Assert.AreEqual(snapshot.SnapshotSha256, reordered.SnapshotSha256);
    }

    private static void Reject(PcbManufacturingDetachedSnapshot snapshot)
    {
        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbManufacturingSnapshotContract.Validate(snapshot, snapshot.Document));
        Assert.AreEqual("pcb_export_snapshot_incomplete", error.Code);
    }

    private static (PcbManufacturingDetachedSnapshot Snapshot, DocumentSpecifier Document) Fixture()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "kicad-manufacturing-snapshot-fixture");
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "fixture.kicad_pcb",
            Project = new() { Name = "fixture", Path = projectPath }
        };
        string boardPath = Path.Combine(projectPath, document.BoardFilename);
        string projectFile = Path.Combine(projectPath, "fixture.kicad_pro");
        var baselines = new[]
        {
            new PcbManufacturingDependencyBaseline(boardPath, new string('a', 64), 1024),
            new PcbManufacturingDependencyBaseline(projectFile, new string('b', 64), 512)
        };
        var snapshot = new PcbManufacturingDetachedSnapshot(
            document, "(kicad_pcb (version 20260101) (generator pcbnew))", "{\"board\":{}}", "",
            baselines, BoardSerializationComplete: true, ProjectSerializationComplete: true,
            DependencyCaptureComplete: true, DiskBaselineChecked: true);
        snapshot = snapshot with { SnapshotSha256 = PcbManufacturingSnapshotContract.ComputeSnapshotSha256(snapshot) };
        return (snapshot, document);
    }
}
