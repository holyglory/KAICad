using System.Text.Json;
using System.Text.Json.Nodes;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignSynchronizationReceiptTests
{
    [TestMethod]
    public void ImmutableReceiptsReopenAndRejectReusedIdentity()
    {
        string directory = Directory.CreateTempSubdirectory("sync-receipts-").FullName;
        try
        {
            string record = Path.Combine(directory, "recovery.json");
            var store = new DesignSynchronizationReceipts(record);
            var receipt = Receipt(directory);
            store.Archive(receipt);
            var read = new DesignSynchronizationReceipts(record).Read(receipt.OperationId)!;
            Assert.AreEqual(receipt, read);
            string file = Path.Combine(record + ".operations", receipt.OperationId.ToString("N") + ".json");
            byte[] before = File.ReadAllBytes(file); DateTime timestamp = File.GetLastWriteTimeUtc(file);
            store.Archive(receipt);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(file)); Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(file));
            Assert.AreEqual("sync_receipt_conflict", Assert.ThrowsExactly<AutomationException>(() =>
                store.Archive(receipt with { DesignFileSha256 = new string('c', 64) })).Code);
            Assert.AreEqual("sync_operation_id_conflict", Assert.ThrowsExactly<AutomationException>(() =>
                read.RequireRequest(receipt.InstanceId, receipt.DesignPath, "different-token")).Code);
            Assert.AreEqual("sync_operation_id_conflict", Assert.ThrowsExactly<AutomationException>(() =>
                read.RequireRequest(Guid.NewGuid(), receipt.DesignPath, receipt.RequestedRecoveryRevisionToken)).Code);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(file));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void CorruptOrMismatchedReceiptFilesAreNeverOverwritten()
    {
        string directory = Directory.CreateTempSubdirectory("sync-receipt-corrupt-").FullName;
        try
        {
            string record = Path.Combine(directory, "recovery.json"); var store = new DesignSynchronizationReceipts(record);
            var receipt = Receipt(directory); store.Archive(receipt);
            string file = Path.Combine(record + ".operations", receipt.OperationId.ToString("N") + ".json");
            foreach (string corrupt in new[] { "{broken", JsonSerializer.Serialize(receipt with { OperationId = Guid.NewGuid() }),
                         JsonSerializer.Serialize(receipt)[..^1] + ",\"Version\":1}" })
            {
                File.WriteAllText(file, corrupt);
                Assert.ThrowsExactly<AutomationException>(() => store.Read(receipt.OperationId));
                Assert.ThrowsExactly<AutomationException>(() => store.Archive(receipt));
                Assert.AreEqual(corrupt, File.ReadAllText(file));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void LatestReceiptIsAtomicWithRecoveryAndOlderRecordsStayUnchanged()
    {
        string directory = Directory.CreateTempSubdirectory("sync-latest-receipt-").FullName;
        try
        {
            string path = Path.Combine(directory, "recovery.json"); var store = new DesignRecoveryStore(path);
            var baseline = DesignRecoveryStoreTests.Fixture(); var original = store.Save(baseline, null);
            byte[] legacy = File.ReadAllBytes(path);
            Assert.AreEqual(original.RevisionToken, store.Save(store.Read()!.State, original.RevisionToken).RevisionToken);
            CollectionAssert.AreEqual(legacy, File.ReadAllBytes(path));
            var receipt = Receipt(directory) with { InstanceId = baseline.InstanceId };
            var saved = store.Save(baseline with { LastSynchronization = receipt }, original.RevisionToken);
            Assert.AreEqual(7, JsonNode.Parse(File.ReadAllBytes(path))!["Version"]!.GetValue<int>());
            var reopened = new DesignRecoveryStore(path).Read()!;
            Assert.AreEqual(receipt, reopened.State.LastSynchronization);
            Assert.AreEqual(saved.RevisionToken, reopened.RevisionToken);
            new DesignSynchronizationReceipts(path).Archive(reopened.State.LastSynchronization!);
            Assert.AreEqual(receipt, new DesignSynchronizationReceipts(path).Read(receipt.OperationId));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void ActiveRequestInputsCannotChangeOrDisappearBeforeCompletion()
    {
        using var fixture = new DesignPublicationRecoveryTests.Fixture();
        var publication = fixture.Intent with { RequestedRecoveryRevisionToken = fixture.Saved.RevisionToken };
        var saved = fixture.Store.Save(fixture.Saved.State with { PendingPublication = publication }, fixture.Saved.RevisionToken);
        var changed = publication with { ExpectedFileBytes = publication.CandidateFileBytes.ToArray() };
        Assert.AreEqual("sync_intent_changed", Assert.ThrowsExactly<AutomationException>(() => fixture.Store.Save(
            saved.State with { PendingPublication = changed }, saved.RevisionToken)).Code);
        Assert.AreEqual("sync_phase_changed", Assert.ThrowsExactly<AutomationException>(() => fixture.Store.Save(
            saved.State with { PendingPublication = publication with { Phase = DesignPublicationPhase.Published } }, saved.RevisionToken)).Code);
        Assert.AreEqual("missing_sync_completion_receipt", Assert.ThrowsExactly<AutomationException>(() => fixture.Store.Save(
            saved.State with { PendingPublication = null, PendingNativeState = null, PendingNativeSave = null }, saved.RevisionToken)).Code);
        Assert.AreEqual(saved.RevisionToken, fixture.Store.Read()!.RevisionToken);
        var newer = fixture.Store.Save(saved.State with { DesiredFileBytes = [0xff] }, saved.RevisionToken);
        CollectionAssert.AreEqual(new byte[] { 0xff }, newer.State.DesiredFileBytes);
        Assert.AreEqual(publication.OperationId, newer.State.PendingPublication!.OperationId);
    }

    private static DesignSynchronizationReceipt Receipt(string directory) => new(1, Guid.NewGuid(), Guid.NewGuid(),
        Path.Combine(directory, "design.xml"), new string('a', 64), new string('b', 64),
        Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 4, false, false, null, null);
}
