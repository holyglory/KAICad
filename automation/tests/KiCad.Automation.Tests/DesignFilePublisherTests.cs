using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignFilePublisherTests
{
    [TestMethod]
    public async Task UnchangedPublicationDoesNotRewriteXmlOrCreateBackupFiles()
    {
        string root = Directory.CreateTempSubdirectory("design-publisher-noop-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml");
            byte[] bytes = "<unchanged/>"u8.ToArray();
            await File.WriteAllBytesAsync(path, bytes);
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            for (int attempt = 0; attempt < 2; attempt++)
                Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    await DesignFilePublisher.WriteIfUnchangedAsync(path, bytes, bytes));
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(path));
            CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PublishesAtomicallyAndReturnsReplacementDigest()
    {
        string root = Directory.CreateTempSubdirectory("design-publisher-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml");
            byte[] before = Encoding.UTF8.GetBytes("<design version=\"1\" />\n");
            byte[] after = Encoding.UTF8.GetBytes("<design version=\"2\" />\n");
            await File.WriteAllBytesAsync(path, before);
            string digest = await DesignFilePublisher.WriteIfUnchangedAsync(path, before, after);
            CollectionAssert.AreEqual(after, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(after)), digest);
            string previous = Directory.GetFiles(root, "design.xml.sync-*").Single();
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(previous),
                "Retain the actually displaced file rather than unlinking an external writer's only remaining path.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ChangedSourceIsRejectedWithoutReplacingIt()
    {
        string root = Directory.CreateTempSubdirectory("design-publisher-conflict-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml");
            byte[] expected = Encoding.UTF8.GetBytes("<design />");
            byte[] changed = Encoding.UTF8.GetBytes("<design changed=\"true\" />");
            byte[] replacement = Encoding.UTF8.GetBytes("<design generated=\"true\" />");
            await File.WriteAllBytesAsync(path, changed);
            var error = await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                DesignFilePublisher.WriteIfUnchangedAsync(path, expected, replacement));
            Assert.AreEqual("design_file_changed", error.Code);
            CollectionAssert.AreEqual(changed, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancellationBeforeCommitLeavesTheOriginalFile()
    {
        string root = Directory.CreateTempSubdirectory("design-publisher-cancel-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml");
            byte[] before = Encoding.UTF8.GetBytes("<design />");
            byte[] after = Encoding.UTF8.GetBytes("<design generated=\"true\" />");
            await File.WriteAllBytesAsync(path, before);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                DesignFilePublisher.WriteIfUnchangedAsync(path, before, after, cancellation.Token));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RacingReplacementPreservesTheNewerUserVersionAndReportsConflict()
    {
        string root = Directory.CreateTempSubdirectory("design-publisher-race-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml");
            byte[] before = "<before/>"u8.ToArray(), candidate = "<candidate/>"u8.ToArray(), newer = "<newer/>"u8.ToArray();
            await File.WriteAllBytesAsync(path, before);
            var failure = await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignFilePublisher.WriteCoreAsync(
                path, before, candidate, () => File.WriteAllBytes(path, newer), null));
            Assert.AreEqual("design_file_conflict_preserved", failure.Code);
            CollectionAssert.AreEqual(candidate, await File.ReadAllBytesAsync(path), "A conflict does not claim rollback after replacement.");
            string previous = Directory.GetFiles(root, "design.xml.sync-*").Single();
            CollectionAssert.AreEqual(newer, await File.ReadAllBytesAsync(previous), "The raced save must remain recoverable.");
            StringAssert.Contains(failure.Message, previous);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FailedPostReplacementObservationDoesNotDeleteTheDisplacedFile()
    {
        string root = Directory.CreateTempSubdirectory("design-publisher-recovery-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml");
            byte[] before = "<before/>"u8.ToArray(), candidate = "<candidate/>"u8.ToArray();
            await File.WriteAllBytesAsync(path, before);
            var failure = await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignFilePublisher.WriteCoreAsync(
                path, before, candidate, null, (target, staged) =>
                {
                    PreservingFileReplacement.Replace(target, staged);
                    throw new IOException("Injected failure after native replacement");
                }));
            Assert.AreEqual("design_publication_requires_recovery", failure.Code);
            CollectionAssert.AreEqual(candidate, await File.ReadAllBytesAsync(path));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(Directory.GetFiles(root, "design.xml.sync-*").Single()));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UnsupportedReplacementNeverFallsBackToOverwritingTheTarget()
    {
        string root = Directory.CreateTempSubdirectory("design-publisher-unsupported-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml");
            byte[] before = "<before/>"u8.ToArray(), candidate = "<candidate/>"u8.ToArray();
            await File.WriteAllBytesAsync(path, before);
            var failure = await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignFilePublisher.WriteCoreAsync(
                path, before, candidate, null, (_, _) => throw new EntryPointNotFoundException("Unsupported swap")));
            Assert.AreEqual("design_publication_requires_recovery", failure.Code);
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
            CollectionAssert.AreEqual(candidate, await File.ReadAllBytesAsync(Directory.GetFiles(root, "design.xml.sync-*").Single()));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LateCancellationDoesNotMisreportACompletedReplacementAsRollback()
    {
        string root = Directory.CreateTempSubdirectory("design-publisher-late-cancel-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml");
            byte[] before = "<before/>"u8.ToArray(), candidate = "<candidate/>"u8.ToArray();
            await File.WriteAllBytesAsync(path, before);
            using var cancellation = new CancellationTokenSource();
            string digest = await DesignFilePublisher.WriteCoreAsync(path, before, candidate, null, (target, staged) =>
            {
                PreservingFileReplacement.Replace(target, staged); cancellation.Cancel();
            }, cancellation.Token);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(candidate)), digest);
            CollectionAssert.AreEqual(candidate, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
