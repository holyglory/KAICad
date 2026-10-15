using System.Security.Cryptography;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbDrcReceiptStoreTests
{
    [TestMethod]
    public void DurableReceiptSurvivesStoreRecreation()
    {
        string root = Directory.CreateTempSubdirectory("pcb-drc-receipt-store-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string job = Guid.NewGuid().ToString("D");
            string document = "{\"type\":\"PCB\"}";
            string revision = "{\"epoch\":\"" + Guid.NewGuid().ToString("D") + "\",\"sequence\":4}";
            string candidate = "{\"items\":[\"track\"]}";
            string candidateHash = Digest(candidate);
            string fingerprint = Digest(instance + operation + epoch + candidate);
            var store = new PcbDrcReceiptStore(root);
            var reservation = store.Reserve(instance, operation, epoch, document, revision, candidate,
                "", "", fingerprint, candidateHash);
            Assert.IsTrue(reservation.Created);
            var completed = store.MarkCompleted(instance, operation, epoch, job,
                "{\"jobId\":\"" + job + "\",\"status\":\"PDRCJS_COMPLETED\"}");
            Assert.AreEqual(PcbDrcReceiptOutcome.Completed, completed.Outcome);

            var reopened = new PcbDrcReceiptStore(root);
            var persisted = reopened.Read(instance, operation);
            Assert.IsNotNull(persisted);
            Assert.AreEqual(PcbDrcReceiptOutcome.Completed, persisted!.Outcome);
            Assert.AreEqual(job, persisted.JobId);
            Assert.AreEqual(candidateHash, persisted.CandidateHash);

            string replacementEpoch = Guid.NewGuid().ToString("D");
            var stale = Assert.ThrowsExactly<AutomationException>(() => reopened.Reserve(instance, operation,
                replacementEpoch, document, revision, candidate, "", "", fingerprint, candidateHash));
            Assert.AreEqual("stale_process_epoch", stale.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnknownReceiptSurvivesRestartAndBlocksRedispatch()
    {
        string root = Directory.CreateTempSubdirectory("pcb-drc-receipt-unknown-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string fingerprint = Digest(operation);
            var store = new PcbDrcReceiptStore(root);
            store.Reserve(instance, operation, epoch, "{}", "{}", "", "", "", fingerprint, "");
            store.MarkUnknown(instance, operation, true, "operation_outcome_unknown", "native reply was lost");

            var reopened = new PcbDrcReceiptStore(root);
            Assert.AreEqual(PcbDrcReceiptOutcome.Unknown, reopened.Read(instance, operation)!.Outcome);
            var refused = Assert.ThrowsExactly<AutomationException>(() => reopened.Reserve(instance, operation,
                epoch, "{}", "{}", "", "", "", fingerprint, ""));
            Assert.AreEqual("operation_outcome_unknown", refused.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ProvenNativeNonDispatchCanBeRetriedExactly()
    {
        string root = Directory.CreateTempSubdirectory("pcb-drc-receipt-retry-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string fingerprint = Digest(operation);
            var store = new PcbDrcReceiptStore(root);
            store.Reserve(instance, operation, epoch, "{}", "{}", "", "", "", fingerprint, "");
            store.MarkUnknown(instance, operation, false, "native_not_reached", "transport failed before dispatch");
            var retry = store.Reserve(instance, operation, epoch, "{}", "{}", "", "", "", fingerprint, "");
            Assert.IsTrue(retry.Created);
            Assert.AreEqual(PcbDrcReceiptOutcome.Prepared, retry.Receipt.Outcome);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ProvenNativeNonDispatchCanRetryWithFreshRequestBytes()
    {
        string root = Directory.CreateTempSubdirectory("pcb-drc-receipt-fresh-retry-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string firstRevision = "{\"epoch\":\"" + Guid.NewGuid().ToString("D") + "\",\"sequence\":1}";
            string secondRevision = "{\"epoch\":\"" + Guid.NewGuid().ToString("D") + "\",\"sequence\":2}";
            string firstFingerprint = Digest(operation + firstRevision);
            string secondFingerprint = Digest(operation + secondRevision);
            var store = new PcbDrcReceiptStore(root);
            store.Reserve(instance, operation, epoch, "{}", firstRevision, "", "", "", firstFingerprint, "");
            store.MarkUnknown(instance, operation, false, "native_not_reached", "the stale revision was rejected before dispatch");

            var retry = store.Reserve(instance, operation, epoch, "{}", secondRevision, "", "", "", secondFingerprint, "");
            Assert.IsTrue(retry.Created);
            Assert.AreEqual(PcbDrcReceiptOutcome.Prepared, retry.Receipt.Outcome);
            Assert.AreEqual(secondRevision, retry.Receipt.RevisionJson);
            Assert.AreEqual(secondFingerprint, retry.Receipt.RequestFingerprintSha256);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Digest(string value) => Convert.ToHexStringLower(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
