using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DocumentRevertReceiptStoreTests
{
    [TestMethod]
    public void AdmissionIsAtomicPerInstanceAndOperation()
    {
        string directory = Directory.CreateTempSubdirectory("revert-receipt-store-").FullName;
        try
        {
            string operation = Guid.NewGuid().ToString("D");
            string firstInstance = Guid.NewGuid().ToString("D");
            string secondInstance = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string otherEpoch = Guid.NewGuid().ToString("D");
            string firstFingerprint = DocumentRevertReceiptStore.Fingerprint(firstInstance, operation, "first");
            string secondFingerprint = DocumentRevertReceiptStore.Fingerprint(secondInstance, operation, "second");
            var first = new DocumentRevertReceiptStore(directory);
            var second = new DocumentRevertReceiptStore(directory);

            var created = first.Reserve(firstInstance, operation, epoch, firstFingerprint);
            Assert.IsTrue(created.Created);
            Assert.AreEqual(DocumentRevertReceiptOutcome.Admitted, created.Receipt.Outcome);

            var replay = second.Reserve(firstInstance, operation, epoch, firstFingerprint);
            Assert.IsFalse(replay.Created, "A second MCP process must observe the existing admission under the per-operation lock.");
            Assert.AreEqual(DocumentRevertReceiptOutcome.Admitted, replay.Receipt.Outcome);
            Assert.ThrowsExactly<AutomationException>(() => second.Reserve(firstInstance, operation, otherEpoch, firstFingerprint));

            var independent = second.Reserve(secondInstance, operation, epoch, secondFingerprint);
            Assert.IsTrue(independent.Created, "The same operation UUID is isolated by instance identity.");

            second.WriteTerminal(new DocumentRevertReceiptRecord(DocumentRevertReceiptRecord.CurrentVersion,
                firstInstance, operation, epoch, firstFingerprint, DocumentRevertReceiptOutcome.Unknown,
                null, "transport loss after delivery"));
            var unknown = first.Read(firstInstance, operation);
            Assert.IsNotNull(unknown);
            Assert.AreEqual(DocumentRevertReceiptOutcome.Unknown, unknown!.Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void TamperedEmbeddedResultIsRejectedFailClosed()
    {
        string directory = Directory.CreateTempSubdirectory("revert-receipt-corrupt-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string fingerprint = DocumentRevertReceiptStore.Fingerprint(instance, operation, "state");
            string path;
            var store = new DocumentRevertReceiptStore(directory);
            _ = store.Reserve(instance, operation, epoch, fingerprint);
            var result = System.Text.Json.JsonSerializer.SerializeToElement(new { instanceId = Guid.NewGuid().ToString("D") });
            store.WriteTerminal(new DocumentRevertReceiptRecord(DocumentRevertReceiptRecord.CurrentVersion,
                instance, operation, epoch, fingerprint, DocumentRevertReceiptOutcome.Completed, result, null));
            path = Directory.GetFiles(Path.Combine(directory, "document-revert-receipts"), "*.json").Single();
            string json = File.ReadAllText(path).Replace(instance, Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
            File.WriteAllText(path, json);
            Assert.ThrowsExactly<AutomationException>(() => store.Read(instance, operation));
        }
        finally { Directory.Delete(directory, true); }
    }
}
