using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RetainedXmlHistoryTests
{
    [TestMethod]
    public void HistoryMovesWithoutDeletionAndImmutableReceiptStillFindsIt()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Receipt;
        Assert.AreEqual("staged", RetainedXmlHistory.Inspect(receipt).Status);
        var archived = RetainedXmlHistory.Archive(receipt);
        Assert.AreEqual("archived", archived.Status); Assert.IsNull(archived.ErrorCode);
        Assert.IsFalse(File.Exists(receipt.PreviousXmlPath));
        CollectionAssert.AreEqual(fixture.Bytes, File.ReadAllBytes(archived.Path!));
        Assert.AreEqual(receipt.PreviousXmlPath, archived.OriginalPath);
        Assert.AreEqual(archived.Path, receipt.Result("current", true).PreviousXmlPath);
        DateTime time = File.GetLastWriteTimeUtc(archived.Path!);
        Assert.AreEqual(archived, RetainedXmlHistory.Archive(receipt));
        Assert.AreEqual(time, File.GetLastWriteTimeUtc(archived.Path!));
    }

    [TestMethod]
    public void ExistingArchiveIsNotOverwrittenOrUsedToDeleteTheSource()
    {
        using var fixture = new Fixture();
        var location = RetainedXmlHistory.Inspect(fixture.Receipt);
        Directory.CreateDirectory(Path.GetDirectoryName(location.ArchivePath!)!);
        File.WriteAllText(location.ArchivePath!, "Unrelated existing history");
        var result = RetainedXmlHistory.Archive(fixture.Receipt);
        Assert.AreEqual("conflict", result.Status);
        CollectionAssert.AreEqual(fixture.Bytes, File.ReadAllBytes(fixture.Receipt.PreviousXmlPath!));
        Assert.AreEqual("Unrelated existing history", File.ReadAllText(location.ArchivePath!));
    }

    [TestMethod]
    public void NativeMoveRefusesAnExistingDestination()
    {
        using var fixture = new Fixture();
        string destination = Path.Combine(fixture.Directory, "existing.xml");
        File.WriteAllText(destination, "Keep this file");
        Assert.ThrowsExactly<IOException>(() => PreservingFileReplacement.MoveNoReplace(fixture.Receipt.PreviousXmlPath!, destination));
        Assert.AreEqual("Keep this file", File.ReadAllText(destination));
        CollectionAssert.AreEqual(fixture.Bytes, File.ReadAllBytes(fixture.Receipt.PreviousXmlPath!));
    }

    [TestMethod]
    public void InterruptionAfterMoveIsResolvedFromEitherRecordedLocation()
    {
        using var fixture = new Fixture();
        var uncertain = RetainedXmlHistory.Archive(fixture.Receipt, () => throw new IOException("Interrupted after move"));
        Assert.AreEqual("archived", uncertain.Status);
        Assert.AreEqual("retained_xml_archive_failed", uncertain.ErrorCode);
        var recovered = RetainedXmlHistory.Archive(fixture.Receipt);
        Assert.AreEqual("archived", recovered.Status); Assert.IsNull(recovered.ErrorCode);
        CollectionAssert.AreEqual(fixture.Bytes, File.ReadAllBytes(recovered.Path!));
    }

    [TestMethod]
    public void UnrelatedTargetsDirectoriesAndLinksAreNotMoved()
    {
        using var fixture = new Fixture();
        string other = Path.Combine(fixture.Directory, "user-requirements.xml"); File.WriteAllText(other, "Keep requirements");
        var wrong = RetainedXmlHistory.Archive(fixture.Receipt with { PreviousXmlPath = other });
        Assert.AreEqual("invalid_retained_xml_target", wrong.ErrorCode);
        Assert.AreEqual("Keep requirements", File.ReadAllText(other));
        File.Delete(fixture.Receipt.PreviousXmlPath!);
        Directory.CreateDirectory(fixture.Receipt.PreviousXmlPath!);
        Assert.AreEqual("invalid_retained_xml_file", RetainedXmlHistory.Archive(fixture.Receipt).ErrorCode);
        Directory.Delete(fixture.Receipt.PreviousXmlPath!);
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(fixture.Receipt.PreviousXmlPath!, other);
            Assert.AreEqual("invalid_retained_xml_file", RetainedXmlHistory.Archive(fixture.Receipt).ErrorCode);
            Assert.AreEqual("Keep requirements", File.ReadAllText(other));
        }
    }

    [TestMethod]
    public void MissingHistoryIsExplicitAndExistingIgnoreRulesStayUnchanged()
    {
        using var fixture = new Fixture();
        var location = RetainedXmlHistory.Inspect(fixture.Receipt);
        string folder = Path.GetDirectoryName(location.ArchivePath!)!;
        Directory.CreateDirectory(folder);
        string ignore = Path.Combine(folder, ".gitignore"); File.WriteAllText(ignore, "user-rule\n");
        Assert.AreEqual("archived", RetainedXmlHistory.Archive(fixture.Receipt).Status);
        Assert.AreEqual("user-rule\n", File.ReadAllText(ignore));
        File.Delete(location.ArchivePath!);
        Assert.AreEqual("missing", RetainedXmlHistory.Inspect(fixture.Receipt).Status);
        Assert.IsNull(fixture.Receipt.Result("current", true).PreviousXmlPath);
        Assert.AreEqual("none", RetainedXmlHistory.Inspect(fixture.Receipt with { PreviousXmlPath = null }).Status);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("retained-xml-").FullName;
        internal byte[] Bytes { get; } = "<previous-design/>"u8.ToArray();
        internal DesignSynchronizationReceipt Receipt { get; }
        internal Fixture()
        {
            Guid operation = Guid.NewGuid(); string design = Path.Combine(Directory, "design.xml");
            string previous = PreservingFileReplacement.PreviousPath(design + ".sync-" + operation.ToString("N"));
            File.WriteAllBytes(previous, Bytes);
            Receipt = new(1, operation, Guid.NewGuid(), design, new string('a', 64), new string('b', 64),
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 4, false, true, null, previous);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
