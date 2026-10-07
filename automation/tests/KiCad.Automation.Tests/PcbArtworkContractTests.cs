using System.Text.Json;
using System.Text;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbArtworkContractTests
{
    [TestMethod]
    public void ReceiptIdentityRequiresStoredInstanceAndProcessEpoch()
    {
        const string instanceId = "instance-a";
        const string processEpoch = "11111111-1111-1111-1111-111111111111";
        JsonElement payload = JsonSerializer.SerializeToElement(new { instanceId, processEpoch });

        PcbArtworkContract.ValidateReceiptIdentity(payload, instanceId, processEpoch);

        Reject(() => PcbArtworkContract.ValidateReceiptIdentity(payload, instanceId,
            "22222222-2222-2222-2222-222222222222"), "stale_process_epoch");
        Reject(() => PcbArtworkContract.ValidateReceiptIdentity(payload, "instance-b", processEpoch),
            "wrong_instance_receipt");
    }

    [TestMethod]
    public void MissingReceiptIdentityFailsClosed()
    {
        const string instanceId = "instance-a";
        const string processEpoch = "11111111-1111-1111-1111-111111111111";

        JsonElement missingEpoch = JsonSerializer.SerializeToElement(new { instanceId });
        Reject(() => PcbArtworkContract.ValidateReceiptIdentity(missingEpoch, instanceId, processEpoch),
            "stale_process_epoch");

        JsonElement missingInstance = JsonSerializer.SerializeToElement(new { processEpoch });
        Reject(() => PcbArtworkContract.ValidateReceiptIdentity(missingInstance, instanceId, processEpoch),
            "wrong_instance_receipt");
    }

    [TestMethod]
    public void SourceArchivePreflightAtomicallyPersistsAndReusesExactBytes()
    {
        string root = Directory.CreateTempSubdirectory("pcb-artwork-archive-").FullName;
        byte[] bytes = Encoding.UTF8.GetBytes("<svg viewBox=\"0 0 1 1\"/>");
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        try
        {
            ArtworkSourceArchive.Receipt first = ArtworkSourceArchive.Persist(root, "artwork/source.svg",
                bytes, hash, "invalid_archive", "archive_conflict");
            Assert.IsTrue(first.Persisted);
            Assert.IsTrue(first.AtomicWrite);
            Assert.AreEqual("artwork/source.svg", first.RelativePath);
            Assert.AreEqual(hash, first.Sha256);
            Assert.AreEqual(bytes.LongLength, first.Bytes);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(root, "artwork/source.svg")));

            ArtworkSourceArchive.Receipt replay = ArtworkSourceArchive.Persist(root, "artwork/source.svg",
                bytes, hash, "invalid_archive", "archive_conflict");
            Assert.AreEqual(first, replay);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SourceArchivePreflightRejectsConflictAndTraversalBeforeMutation()
    {
        string root = Directory.CreateTempSubdirectory("pcb-artwork-archive-").FullName;
        byte[] expected = Encoding.UTF8.GetBytes("expected");
        string expectedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(expected));
        try
        {
            string existing = Path.Combine(root, "artwork", "source.svg");
            Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
            File.WriteAllText(existing, "different");
            Reject(() => ArtworkSourceArchive.Persist(root, "artwork/source.svg", expected,
                expectedHash, "invalid_archive", "archive_conflict"), "archive_conflict");

            string outsideName = "outside-" + Guid.NewGuid().ToString("N") + ".svg";
            Reject(() => ArtworkSourceArchive.Persist(root, "../" + outsideName, expected,
                expectedHash, "invalid_archive", "archive_conflict"), "invalid_archive");
            Assert.IsFalse(File.Exists(Path.Combine(Directory.GetParent(root)!.FullName, outsideName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Reject(Action action, string expectedCode)
    {
        AutomationException error = Assert.ThrowsExactly<AutomationException>(action);
        Assert.AreEqual(expectedCode, error.Code);
    }
}
