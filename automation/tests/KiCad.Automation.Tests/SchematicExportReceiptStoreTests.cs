using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicExportReceiptStoreTests
{
    [TestMethod]
    public void ReceiptSurvivesStoreReopenAndRejectsRebinding()
    {
        string root = Directory.CreateTempSubdirectory("schematic-export-receipts-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            using JsonDocument document = JsonDocument.Parse("{\"outcome\":\"completed\",\"outputs\":[{\"bytes\":3}]}");
            var receipt = new SchematicExportReceiptRecord(1, instance, operation, new string('a', 64), epoch,
                document.RootElement.Clone());
            var store = new SchematicExportReceiptStore(root);

            store.Write(receipt);
            store.Write(receipt);
            AssertEquivalent(receipt, store.Read(instance, operation));
            AssertEquivalent(receipt, new SchematicExportReceiptStore(root).Read(instance, operation));
            Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(root, "schematic-export-receipts"), "*.json").Count());

            Assert.ThrowsExactly<AutomationException>(() => store.Write(receipt with { Fingerprint = new string('b', 64) }));
            Assert.ThrowsExactly<AutomationException>(() => store.Write(receipt with { ProcessEpoch = Guid.NewGuid().ToString("D") }));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AssertEquivalent(SchematicExportReceiptRecord expected, SchematicExportReceiptRecord? actual)
    {
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.Version, actual.Version);
        Assert.AreEqual(expected.InstanceId, actual.InstanceId);
        Assert.AreEqual(expected.OperationId, actual.OperationId);
        Assert.AreEqual(expected.Fingerprint, actual.Fingerprint);
        Assert.AreEqual(expected.ProcessEpoch, actual.ProcessEpoch);
        Assert.IsTrue(expected.Result.GetRawText() == actual.Result.GetRawText());
    }

    [TestMethod]
    public void LegacySingleFileReceiptRemainsReadableAfterStoreUpgrade()
    {
        string root = Directory.CreateTempSubdirectory("schematic-export-legacy-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string key = instance + "\n" + operation;
            using JsonDocument document = JsonDocument.Parse("{\"outcome\":\"completed\"}");
            var legacy = new Dictionary<string, object>
            {
                [key] = new { Fingerprint = new string('c', 64), ProcessEpoch = epoch, Result = document.RootElement.Clone() }
            };
            File.WriteAllText(Path.Combine(root, "schematic-export-receipts.json"), JsonSerializer.Serialize(legacy));

            var receipt = new SchematicExportReceiptStore(root).Read(instance, operation);
            Assert.IsNotNull(receipt);
            Assert.AreEqual(instance, receipt.InstanceId);
            Assert.AreEqual(operation, receipt.OperationId);
            Assert.AreEqual(epoch, receipt.ProcessEpoch);
            Assert.AreEqual("completed", receipt.Result.GetProperty("outcome").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CorruptReceiptFailsClosedInsteadOfLookingMissing()
    {
        string root = Directory.CreateTempSubdirectory("schematic-export-corrupt-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string directory = Path.Combine(root, "schematic-export-receipts");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(instance)))
                + "-" + operation + ".json");
            File.WriteAllText(file, "{\"Version\":1,\"Version\":1}");

            var error = Assert.ThrowsExactly<AutomationException>(() => new SchematicExportReceiptStore(root).Read(instance, operation));
            Assert.AreEqual("invalid_schematic_export_receipt", error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void BoundTerminalReceiptRetainsExactRequestAcrossStoreReopen()
    {
        string root = Directory.CreateTempSubdirectory("schematic-export-bound-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            var request = new SchematicExportReceiptRequest(instance, epoch, operation, "svg",
                "{\"request\":true}", "{\"state\":true}");
            using JsonDocument result = JsonDocument.Parse($"{{\"instanceId\":\"{instance}\",\"processEpoch\":\"{epoch}\",\"operationId\":\"{operation}\",\"outcome\":\"completed\"}}");
            var store = new SchematicExportReceiptStore(root);

            store.WriteAdmitted(request);
            store.WriteTerminal(request, result.RootElement);

            var retained = new SchematicExportReceiptStore(root).ReadBound(instance, request);
            Assert.IsNotNull(retained);
            Assert.AreEqual(SchematicExportReceiptRecord.Completed, retained.Outcome);
            Assert.AreEqual(request.RequestJson, retained.RequestJson);
            Assert.AreEqual(request.ExpectedStateJson, retained.ExpectedStateJson);
            Assert.AreEqual(request.Fingerprint, retained.Fingerprint);

            var changed = request with { RequestJson = "{\"request\":false}" };
            var error = Assert.ThrowsExactly<AutomationException>(() =>
                new SchematicExportReceiptStore(root).ReadBound(instance, changed));
            Assert.AreEqual("operation_id_reused", error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void BoundAdmittedAndUnknownReceiptsRemainFailClosed()
    {
        string root = Directory.CreateTempSubdirectory("schematic-export-unknown-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            var request = new SchematicExportReceiptRequest(instance, epoch, operation, "pdf",
                "{\"request\":true}", "{\"state\":true}");
            var store = new SchematicExportReceiptStore(root);

            store.WriteAdmitted(request);
            var admitted = store.ReadBound(instance, request);
            Assert.IsNotNull(admitted);
            Assert.AreEqual(SchematicExportReceiptRecord.Admitted, admitted.Outcome);
            Assert.AreEqual("operation_admitted", admitted.ErrorCode);

            store.WriteUnknown(request, "transport_status_timeout", "The native reply was lost.");
            var unknown = new SchematicExportReceiptStore(root).ReadBound(instance, request);
            Assert.IsNotNull(unknown);
            Assert.AreEqual(SchematicExportReceiptRecord.Unknown, unknown.Outcome);
            Assert.AreEqual("transport_status_timeout", unknown.ErrorCode);
            Assert.AreEqual("The native reply was lost.", unknown.ErrorMessage);
            Assert.IsFalse(unknown.Result.GetProperty("retryDispatched").GetBoolean());

            // An indeterminate operation cannot be replaced by a terminal
            // result under the same UUID, even if a caller presents the old
            // request again.
            using JsonDocument result = JsonDocument.Parse("{\"outcome\":\"completed\"}");
            store.WriteTerminal(request, result.RootElement);
            Assert.AreEqual(SchematicExportReceiptRecord.Unknown,
                new SchematicExportReceiptStore(root).ReadBound(instance, request)!.Outcome);
        }
        finally { Directory.Delete(root, true); }
    }
}
