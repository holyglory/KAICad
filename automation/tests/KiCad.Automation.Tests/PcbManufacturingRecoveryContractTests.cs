using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbManufacturingRecoveryContractTests
{
    [TestMethod]
    public void TypedRecoveryInputsPreserveIdentityAndRejectContradictoryEpochs()
    {
        string root = Directory.CreateTempSubdirectory("pcb-manufacturing-recovery-contract-").FullName;
        try
        {
            const string epoch = "11111111-1111-4111-8111-111111111111";
            const string prior = "22222222-2222-4222-8222-222222222222";
            const string operation = "33333333-3333-4333-8333-333333333333";
            const string fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

            var cancel = new PcbManufacturingCancelInput
            {
                Document = Board(root), ProcessEpoch = epoch, OperationId = operation,
                BeforeNativeDispatchOnly = true
            };
            CancelPcbManufacturingExport cancelNative = cancel.ToNative();
            Assert.AreEqual(epoch, cancelNative.ProcessEpoch);
            Assert.AreEqual(operation, cancelNative.OperationId);
            Assert.IsTrue(cancelNative.BeforeNativeDispatchOnly);
            Assert.AreEqual(Kiapi.Common.Types.DocumentType.DoctypePcb, cancelNative.Document.Type);

            var reconcile = new PcbManufacturingReconcileInput
            {
                Document = Board(root), ProcessEpoch = epoch, OperationId = operation,
                RequestFingerprintSha256 = fingerprint, PriorProcessEpoch = prior,
                AllowPriorProcessEpoch = true
            };
            ReconcilePcbManufacturingExport reconcileNative = reconcile.ToNative();
            Assert.AreEqual(epoch, reconcileNative.ProcessEpoch);
            Assert.AreEqual(prior, reconcileNative.PriorProcessEpoch);
            Assert.AreEqual(fingerprint, reconcileNative.RequestFingerprintSha256);
            Assert.IsTrue(reconcileNative.AllowPriorProcessEpoch);

            var contradiction = new PcbManufacturingReconcileInput
            {
                Document = Board(root), ProcessEpoch = epoch, OperationId = operation,
                RequestFingerprintSha256 = fingerprint, PriorProcessEpoch = prior,
                AllowPriorProcessEpoch = false
            };
            AutomationException error = Assert.ThrowsExactly<AutomationException>(contradiction.ToNative);
            Assert.AreEqual("invalid_pcb_export_reconcile_input", error.Code);

            var invalidFingerprint = new PcbManufacturingReconcileInput
            {
                Document = Board(root), ProcessEpoch = epoch, OperationId = operation,
                RequestFingerprintSha256 = "not-a-digest"
            };
            error = Assert.ThrowsExactly<AutomationException>(invalidFingerprint.ToNative);
            Assert.AreEqual("invalid_pcb_export_reconcile_input", error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CompiledStdioPublishesTypedRecoveryToolsAndFailsClosedForIncompleteInput()
    {
        string root = Directory.CreateTempSubdirectory("pcb-manufacturing-recovery-mcp-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(), Path.Combine(root, "state"),
                Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement[] tools = listed.GetProperty("tools").EnumerateArray()
                .Where(tool => tool.GetProperty("name").GetString() is
                    "kicad_pcb_export_start_typed" or "kicad_pcb_export_cancel_typed" or "kicad_pcb_export_reconcile_typed")
                .ToArray();
            Assert.AreEqual(3, tools.Length);
            foreach (JsonElement tool in tools)
            {
                JsonElement input = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("input");
                Assert.IsTrue(SchemaIncludes(input, "object"),
                    tool.GetRawText());
                JsonElement properties = input.GetProperty("properties");
                foreach (string property in new[] { "document", "processEpoch", "operationId" })
                    Assert.IsTrue(properties.TryGetProperty(property, out _), tool.GetRawText());
            }

            JsonElement cancelRefused = await mcp.Tool("kicad_pcb_export_cancel_typed", new
            { instanceId = Guid.NewGuid().ToString("D"), input = new { } });
            Assert.IsTrue(cancelRefused.GetProperty("isError").GetBoolean(), cancelRefused.GetRawText());
            Assert.AreEqual("invalid_pcb_export_cancel_input",
                cancelRefused.GetProperty("structuredContent").GetProperty("errorCode").GetString(), cancelRefused.GetRawText());
            JsonElement startRefused = await mcp.Tool("kicad_pcb_export_start_typed", new
            { instanceId = Guid.NewGuid().ToString("D"), input = new { } });
            Assert.IsTrue(startRefused.GetProperty("isError").GetBoolean(), startRefused.GetRawText());
            Assert.AreEqual("invalid_pcb_export_input",
                startRefused.GetProperty("structuredContent").GetProperty("errorCode").GetString(), startRefused.GetRawText());
            JsonElement reconcileRefused = await mcp.Tool("kicad_pcb_export_reconcile_typed", new
            { instanceId = Guid.NewGuid().ToString("D"), input = new { } });
            Assert.IsTrue(reconcileRefused.GetProperty("isError").GetBoolean(), reconcileRefused.GetRawText());
            Assert.AreEqual("invalid_pcb_export_reconcile_input",
                reconcileRefused.GetProperty("structuredContent").GetProperty("errorCode").GetString(), reconcileRefused.GetRawText());
            JsonElement startRefused = await mcp.Tool("kicad_pcb_export_start_typed", new
            { instanceId = Guid.NewGuid().ToString("D"), input = new { } });
            Assert.IsTrue(startRefused.GetProperty("isError").GetBoolean(), startRefused.GetRawText());
            Assert.AreEqual("invalid_pcb_export_input",
                startRefused.GetProperty("structuredContent").GetProperty("errorCode").GetString(), startRefused.GetRawText());
        }
        finally { Directory.Delete(root, true); }
    }

    private static KiCad.Automation.Mcp.NativeDocumentInput Board(string root) => new()
    {
        Type = KiCad.Automation.Mcp.NativeDocumentType.Pcb,
        Project = new() { Name = "recovery-contract", Path = root },
        BoardFilename = "board.kicad_pcb"
    };

    private static bool SchemaIncludes(JsonElement element, string expected)
    {
        if (!element.TryGetProperty("type", out JsonElement type)) return false;
        return type.ValueKind == JsonValueKind.String
            ? type.GetString() == expected
            : type.ValueKind == JsonValueKind.Array
                && type.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
                    && item.GetString() == expected);
    }
}
