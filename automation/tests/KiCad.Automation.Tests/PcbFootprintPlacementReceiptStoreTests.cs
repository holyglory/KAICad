using System.Text.Json;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using ModelContextProtocol.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
// Keep this name within the existing typed-mcp-contracts filter so durable
// placement replay is exercised with the rest of the placement contract.
public sealed class PcbFootprintPlacementTestsReceiptStore
{
    [TestMethod]
    public void TerminalPlacementReceiptSurvivesStoreReopenAndProjectsExactReplay()
    {
        string root = Directory.CreateTempSubdirectory("pcb-footprint-placement-receipt-").FullName;
        const string instanceId = "11111111-1111-1111-1111-111111111111";
        const string processEpoch = "22222222-2222-2222-2222-222222222222";
        const string operationId = "33333333-3333-3333-3333-333333333333";
        try
        {
            var result = Structured(instanceId, processEpoch, operationId);
            string fingerprint = PcbFootprintPlacementReceiptStore.Fingerprint("placement-request");
            new PcbFootprintPlacementReceiptStore(root).Write(
                instanceId, processEpoch, operationId, fingerprint, result);

            PcbFootprintPlacementReceiptStore reopened = new(root);
            PcbFootprintPlacementReceiptRecord? record = reopened.Read(instanceId, operationId);
            Assert.IsNotNull(record);
            Assert.AreEqual(fingerprint, record.RequestFingerprintSha256);
            Assert.IsFalse(record.IsError);

            CallToolResult replay = PcbFootprintPlacementReceiptStore.ReplayResult(record);
            Assert.IsFalse(replay.IsError ?? true);
            JsonElement payload = replay.StructuredContent!.Value;
            Assert.IsTrue(payload.GetProperty("replayed").GetBoolean());
            Assert.IsTrue(payload.GetProperty("receipt").GetProperty("replayed").GetBoolean());
            Assert.AreEqual(operationId, payload.GetProperty("operationId").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ReusingPlacementOperationWithDifferentRequestIsRejected()
    {
        string root = Directory.CreateTempSubdirectory("pcb-footprint-placement-receipt-conflict-").FullName;
        const string instanceId = "44444444-4444-4444-4444-444444444444";
        const string processEpoch = "55555555-5555-5555-5555-555555555555";
        const string operationId = "66666666-6666-6666-6666-666666666666";
        try
        {
            var store = new PcbFootprintPlacementReceiptStore(root);
            store.Write(instanceId, processEpoch, operationId,
                PcbFootprintPlacementReceiptStore.Fingerprint("first"),
                Structured(instanceId, processEpoch, operationId));

            AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
                store.Write(instanceId, processEpoch, operationId,
                    PcbFootprintPlacementReceiptStore.Fingerprint("second"),
                    Structured(instanceId, processEpoch, operationId)));
            Assert.AreEqual("operation_id_reused", error.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static CallToolResult Structured(string instanceId, string processEpoch, string operationId)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            instanceId,
            processEpoch,
            operationId,
            operation = "footprint-place",
            mutationConfirmed = true,
            nativeReceiptAvailable = true,
            receipt = new { state = "CPBTS_COMPLETED" },
            nativeReceipt = new { state = "CPBTS_COMPLETED" }
        });
        return new CallToolResult
        {
            StructuredContent = payload,
            Content = [new TextContentBlock { Text = payload.GetRawText() }]
        };
    }
}
