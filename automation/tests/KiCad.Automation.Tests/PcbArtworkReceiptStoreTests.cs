using System.Text.Json;
using System.Text.Json.Nodes;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbArtworkReceiptStoreTests
{
    [TestMethod]
    public void AdmissionSurvivesReopenAndUnknownOutcomeBlocksRedispatch()
    {
        string root = Directory.CreateTempSubdirectory("pcb-artwork-receipts-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            ArtworkSourceArchive.Receipt archive = Archive(root, "source.svg", "artwork-receipt-source");
            string fingerprint = PcbArtworkReceiptStore.Fingerprint("canonical-artwork-request");

            var first = new PcbArtworkReceiptStore(root);
            PcbArtworkReceiptReservation admission = first.Reserve(instance, epoch, operation,
                fingerprint, archive);
            Assert.IsTrue(admission.Created);

            var reopened = new PcbArtworkReceiptStore(root);
            PcbArtworkReceiptReservation duplicate = reopened.Reserve(instance, epoch, operation,
                fingerprint, archive);
            Assert.IsFalse(duplicate.Created);
            Assert.AreEqual(PcbArtworkReceiptRecord.Admitted, duplicate.Receipt.Outcome);

            reopened.WriteUnknown(instance, epoch, operation, fingerprint, archive,
                "operation_outcome_unknown", "transport loss after native dispatch");
            PcbArtworkReceiptRecord? unknown = new PcbArtworkReceiptStore(root).Read(instance, operation);
            Assert.IsNotNull(unknown);
            Assert.AreEqual(PcbArtworkReceiptRecord.Unknown, unknown!.Outcome);
            Assert.AreEqual("indeterminate", unknown.NativeOutcome);
            Assert.AreEqual("operation_outcome_unknown", unknown.ErrorCode);
            Assert.ThrowsExactly<AutomationException>(() => PcbArtworkReceiptStore.ToResult(unknown));

            PcbArtworkReceiptReservation blocked = new PcbArtworkReceiptStore(root).Reserve(
                instance, epoch, operation, fingerprint, archive);
            Assert.IsFalse(blocked.Created);
            Assert.AreEqual(PcbArtworkReceiptRecord.Unknown, blocked.Receipt.Outcome);

            string changedFingerprint = PcbArtworkReceiptStore.Fingerprint("different-request");
            Assert.ThrowsExactly<AutomationException>(() => new PcbArtworkReceiptStore(root)
                .Reserve(instance, epoch, operation, changedFingerprint, archive));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OnlyProvenPreNativeRejectionsCanReleaseAdmission()
    {
        string instance = Guid.NewGuid().ToString("D");
        string epoch = Guid.NewGuid().ToString("D");
        string operation = Guid.NewGuid().ToString("D");
        var request = new CheckedPcbItemTransaction
        {
            ProcessEpoch = epoch,
            OperationId = operation
        };

        Assert.IsTrue(PcbArtworkTools.ProvenPreNativeRejection(
            new PcbCheckedItemDispatch.Outcome(instance, request, null,
                "native_not_reached", "handshake failed")));
        Assert.IsTrue(PcbArtworkTools.ProvenPreNativeRejection(
            new PcbCheckedItemDispatch.Outcome(instance, request, null,
                "invalid_pcb_item_request", "invalid request")));
        Assert.IsFalse(PcbArtworkTools.ProvenPreNativeRejection(
            new PcbCheckedItemDispatch.Outcome(instance, request, null,
                "operation_outcome_unknown", "reply lost")));
        Assert.IsFalse(PcbArtworkTools.ProvenPreNativeRejection(
            new PcbCheckedItemDispatch.Outcome(instance, request, null,
                "native_status_3", "native rejected after dispatch")));
        Assert.IsFalse(PcbArtworkTools.ProvenPreNativeRejection(
            new PcbCheckedItemDispatch.Outcome(instance, request, null,
                "invalid_checked_pcb_receipt", "receipt is malformed",
                NativeReceiptAvailable: true, UntrustedReceipt: true)));
    }

    [TestMethod]
    public void TerminalReceiptSurvivesReopenAndSameTerminalIsIdempotent()
    {
        string root = Directory.CreateTempSubdirectory("pcb-artwork-terminal-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            ArtworkSourceArchive.Receipt archive = Archive(root, "terminal.svg", "terminal-artwork");
            string fingerprint = PcbArtworkReceiptStore.Fingerprint("terminal-request");
            var store = new PcbArtworkReceiptStore(root);
            _ = store.Reserve(instance, epoch, operation, fingerprint, archive);
            CallToolResult result = Result(new { operationId = operation, mutationConfirmed = true });

            store.WriteTerminal(instance, epoch, operation, fingerprint, archive,
                "completed", archiveReadbackVerified: true, result);
            // Repeating the exact terminal publication is safe after a caller
            // loses its reply while persisting the receipt.
            store.WriteTerminal(instance, epoch, operation, fingerprint, archive,
                "completed", archiveReadbackVerified: true, result);

            PcbArtworkReceiptRecord? record = new PcbArtworkReceiptStore(root).Read(instance, operation);
            Assert.IsNotNull(record);
            Assert.AreEqual(PcbArtworkReceiptRecord.Terminal, record!.Outcome);
            Assert.AreEqual("completed", record.NativeOutcome);
            Assert.IsTrue(record.ArchiveReadbackVerified);
            Assert.AreEqual(result.StructuredContent!.Value.GetRawText(), record.ResultJson);
            CallToolResult restored = PcbArtworkReceiptStore.ToResult(record);
            Assert.IsFalse(restored.IsError ?? false);
            Assert.AreEqual(record.ResultJson, restored.StructuredContent!.Value.GetRawText());

            CallToolResult conflicting = Result(new { operationId = operation, mutationConfirmed = false });
            Assert.ThrowsExactly<AutomationException>(() => store.WriteTerminal(instance, epoch,
                operation, fingerprint, archive, "completed", true, conflicting));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RemoveAdmissionAllowsRetryOnlyBeforeNativeReach()
    {
        string root = Directory.CreateTempSubdirectory("pcb-artwork-remove-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            ArtworkSourceArchive.Receipt archive = Archive(root, "retry.svg", "retry-artwork");
            string fingerprint = PcbArtworkReceiptStore.Fingerprint("retry-request");
            var store = new PcbArtworkReceiptStore(root);
            _ = store.Reserve(instance, epoch, operation, fingerprint, archive);
            store.RemoveAdmission(instance, epoch, operation, fingerprint, archive);
            Assert.IsNull(new PcbArtworkReceiptStore(root).Read(instance, operation));
            Assert.IsTrue(new PcbArtworkReceiptStore(root).Reserve(instance, epoch, operation,
                fingerprint, archive).Created);

            store.WriteUnknown(instance, epoch, operation, fingerprint, archive,
                "operation_outcome_unknown", "native outcome not observed");
            store.RemoveAdmission(instance, epoch, operation, fingerprint, archive);
            Assert.IsNotNull(new PcbArtworkReceiptStore(root).Read(instance, operation),
                "An indeterminate native outcome must never be deleted as a retry admission.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TamperedReceiptFailsClosedOnRead()
    {
        string root = Directory.CreateTempSubdirectory("pcb-artwork-tamper-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            ArtworkSourceArchive.Receipt archive = Archive(root, "tamper.svg", "tamper-artwork");
            string fingerprint = PcbArtworkReceiptStore.Fingerprint("tamper-request");
            var store = new PcbArtworkReceiptStore(root);
            _ = store.Reserve(instance, epoch, operation, fingerprint, archive);
            string path = Directory.GetFiles(Path.Combine(root, "pcb-artwork-receipts"), "*.json").Single();
            JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["operationId"] = Guid.NewGuid().ToString("D");
            File.WriteAllText(path, json.ToJsonString());
            Assert.ThrowsExactly<AutomationException>(() => store.Read(instance, operation));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ArtworkSourceArchive.Receipt Archive(string root, string path, string content)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        return ArtworkSourceArchive.Persist(root, path, bytes, hash, "invalid_archive", "archive_conflict");
    }

    private static CallToolResult Result(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value);
        return new CallToolResult
        {
            IsError = false,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }
}
