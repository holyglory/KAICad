using System.Text.Json;
using Google.Protobuf;
using Kiapi.Board.Jobs;
using Kiapi.Common.Commands;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbManufacturingRecoveryContractTests
{
    [TestMethod]
    public void ManagerCapabilitiesExposeSynchronousOwnerThreadSupportSeparatelyFromDetachedRecovery()
    {
        var capabilities = new PcbManufacturingManagerCapabilities
        {
            SynchronousSvgExportSupported = true,
            SynchronousGerberExportSupported = true,
            SynchronousOdbExportSupported = true,
            SynchronousIpc2581ExportSupported = true,
            PublishedOutputsVerificationSupported = true,
            DetachedExportSupported = false,
            PublicationSupported = false,
            CompleteSnapshotSupported = false
        };

        Assert.IsTrue(capabilities.SynchronousSvgExportSupported);
        Assert.IsTrue(capabilities.SynchronousGerberExportSupported);
        Assert.IsTrue(capabilities.SynchronousOdbExportSupported);
        Assert.IsTrue(capabilities.SynchronousIpc2581ExportSupported);
        Assert.IsTrue(capabilities.PublishedOutputsVerificationSupported);
        Assert.IsFalse(capabilities.DetachedExportSupported);
        Assert.IsFalse(capabilities.PublicationSupported);
        Assert.IsFalse(capabilities.CompleteSnapshotSupported);
    }

    [TestMethod]
    public void TypedRecoveryRefusesStatsWithoutDetachedSnapshotAdapter()
    {
        var request = new RunPcbManufacturingExport
        {
            ExportStats = new RunBoardJobExportStats()
        };

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbManufacturingContract.ValidateTypedRecoveryJob(request));

        Assert.AreEqual("pcb_export_detached_unsupported", error.Code);
        StringAssert.Contains(error.Message, "PCBNEW_JOBS_HANDLER");
        StringAssert.Contains(error.Message, "serialized-snapshot");

        var unsupported = new RunPcbManufacturingExport
        {
            Export3D = new RunBoardJobExport3D()
        };
        error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbManufacturingContract.ValidateTypedRecoveryJob(unsupported));
        Assert.AreEqual("pcb_export_detached_unsupported", error.Code);
        StringAssert.Contains(error.Message, "serialized-snapshot");
    }

    [TestMethod]
    public void TypedRecoveryAllowsOnlyTheExistingOwnerThreadAdapters()
    {
        foreach (RunPcbManufacturingExport.JobOneofCase job in new[]
        {
            RunPcbManufacturingExport.JobOneofCase.ExportSvg,
            RunPcbManufacturingExport.JobOneofCase.ExportGerbers,
            RunPcbManufacturingExport.JobOneofCase.ExportOdb,
            RunPcbManufacturingExport.JobOneofCase.ExportIpc2581
        })
        {
            var request = new RunPcbManufacturingExport();
            switch (job)
            {
                case RunPcbManufacturingExport.JobOneofCase.ExportSvg:
                    request.ExportSvg = new();
                    break;
                case RunPcbManufacturingExport.JobOneofCase.ExportGerbers:
                    request.ExportGerbers = new();
                    break;
                case RunPcbManufacturingExport.JobOneofCase.ExportOdb:
                    request.ExportOdb = new();
                    break;
                case RunPcbManufacturingExport.JobOneofCase.ExportIpc2581:
                    request.ExportIpc2581 = new();
                    break;
            }

            PcbManufacturingContract.ValidateTypedRecoveryJob(request);
        }
    }

    [TestMethod]
    public void ReconcileRefusesUnverifiedNotFoundAndAcceptsVerifiedAbsence()
    {
        var unsupported = new PcbManufacturingExportReceiptResponse { Found = false };
        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbJobTools.ValidateReconcileResponseSupport(unsupported, allowPriorProcessEpoch: false,
                "11111111-1111-4111-8111-111111111111"));
        Assert.AreEqual("pcb_export_recovery_unavailable", error.Code);

        var verifiedAbsence = new PcbManufacturingExportReceiptResponse
        {
            Found = false,
            ReconciliationSupported = true,
            ObservingProcessEpoch = "11111111-1111-4111-8111-111111111111",
            RequestFingerprintVerified = true,
            JournalVerified = true
        };
        PcbJobTools.ValidateReconcileResponseSupport(verifiedAbsence, allowPriorProcessEpoch: false,
            "11111111-1111-4111-8111-111111111111");

        var unverifiedAbsence = verifiedAbsence.Clone();
        unverifiedAbsence.JournalVerified = false;
        error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbJobTools.ValidateReconcileResponseSupport(unverifiedAbsence, allowPriorProcessEpoch: false,
                "11111111-1111-4111-8111-111111111111"));
        Assert.AreEqual("pcb_export_recovery_unavailable", error.Code);

        var unverifiedPriorAbsence = verifiedAbsence.Clone();
        error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbJobTools.ValidateReconcileResponseSupport(unverifiedPriorAbsence, allowPriorProcessEpoch: true,
                "11111111-1111-4111-8111-111111111111"));
        Assert.AreEqual("pcb_export_recovery_unavailable", error.Code);

        var verifiedPriorAbsence = verifiedAbsence.Clone();
        verifiedPriorAbsence.PriorProcessEpochVerified = true;
        PcbJobTools.ValidateReconcileResponseSupport(verifiedPriorAbsence, allowPriorProcessEpoch: true,
            "11111111-1111-4111-8111-111111111111");

        var foreignObserver = verifiedAbsence.Clone();
        foreignObserver.ObservingProcessEpoch = "22222222-2222-4222-8222-222222222222";
        error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbJobTools.ValidateReconcileResponseSupport(foreignObserver, allowPriorProcessEpoch: false,
                "11111111-1111-4111-8111-111111111111"));
        Assert.AreEqual("pcb_export_recovery_unavailable", error.Code);

        var incompleteReceipt = new PcbManufacturingExportReceiptResponse
        {
            Found = true,
            ReconciliationSupported = true,
            ObservingProcessEpoch = "11111111-1111-4111-8111-111111111111",
            RequestFingerprintVerified = true
        };
        error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbJobTools.ValidateReconcileResponseSupport(incompleteReceipt, allowPriorProcessEpoch: false,
                "11111111-1111-4111-8111-111111111111"));
        Assert.AreEqual("pcb_export_recovery_unavailable", error.Code);

        var completedUnverified = verifiedAbsence.Clone();
        completedUnverified.Found = true;
        completedUnverified.Receipt = new PcbManufacturingExportReceipt
        {
            Status = PcbManufacturingExportStatus.PcbMfgExportCompleted
        };
        error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbJobTools.ValidateReconcileResponseSupport(completedUnverified, allowPriorProcessEpoch: false,
                "11111111-1111-4111-8111-111111111111"));
        Assert.AreEqual("pcb_export_recovery_unavailable", error.Code);

        var completedVerified = completedUnverified.Clone();
        completedVerified.PublishedOutputsVerified = true;
        PcbJobTools.ValidateReconcileResponseSupport(completedVerified, allowPriorProcessEpoch: false,
            "11111111-1111-4111-8111-111111111111");
    }

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
                    "kicad_pcb_export_start_typed" or "kicad_pcb_export_cancel_typed" or "kicad_pcb_export_reconcile_typed" or "kicad_pcb_export_read_typed" or "kicad_pcb_export_manager_capabilities_typed")
                .ToArray();
            Assert.AreEqual(5, tools.Length);
            foreach (JsonElement tool in tools)
            {
                JsonElement input = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("input");
                Assert.IsTrue(SchemaIncludes(input, "object"),
                    tool.GetRawText());
                JsonElement properties = input.GetProperty("properties");
                string[] required = tool.GetProperty("name").GetString() == "kicad_pcb_export_start_typed"
                    ? ["document", "expectedState", "operationId", "outputDirectory", "job"]
                    : tool.GetProperty("name").GetString() == "kicad_pcb_export_manager_capabilities_typed"
                        ? ["document", "processEpoch"]
                    : tool.GetProperty("name").GetString() == "kicad_pcb_export_read_typed"
                        ? ["document", "processEpoch", "operationId", "requestFingerprintSha256"]
                    : ["document", "processEpoch", "operationId"];
                foreach (string property in required)
                    Assert.IsTrue(properties.TryGetProperty(property, out _), tool.GetRawText());
            }

            JsonElement cancelRefused = await mcp.Tool("kicad_pcb_export_cancel_typed", new
            { instanceId = Guid.NewGuid().ToString("D"), input = new { } });
            Assert.IsTrue(cancelRefused.GetProperty("isError").GetBoolean(), cancelRefused.GetRawText());
            Assert.AreEqual("invalid_pcb_export_cancel_input",
                cancelRefused.GetProperty("structuredContent").GetProperty("errorCode").GetString(), cancelRefused.GetRawText());
            JsonElement reconcileRefused = await mcp.Tool("kicad_pcb_export_reconcile_typed", new
            { instanceId = Guid.NewGuid().ToString("D"), input = new { } });
            Assert.IsTrue(reconcileRefused.GetProperty("isError").GetBoolean(), reconcileRefused.GetRawText());
            Assert.AreEqual("invalid_pcb_export_reconcile_input",
                reconcileRefused.GetProperty("structuredContent").GetProperty("errorCode").GetString(), reconcileRefused.GetRawText());
            JsonElement startRefused = await mcp.Tool("kicad_pcb_export_start_typed", new
            { instanceId = Guid.NewGuid().ToString("D"), input = new { } });
            Assert.IsTrue(startRefused.GetProperty("isError").GetBoolean(), startRefused.GetRawText());
            Assert.AreEqual("invalid_operation_id",
                startRefused.GetProperty("structuredContent").GetProperty("errorCode").GetString(), startRefused.GetRawText());
            JsonElement capabilitiesRefused = await mcp.Tool("kicad_pcb_export_manager_capabilities_typed", new
            { instanceId = Guid.NewGuid().ToString("D"), input = new { } });
            Assert.IsTrue(capabilitiesRefused.GetProperty("isError").GetBoolean(), capabilitiesRefused.GetRawText());
            Assert.AreEqual("invalid_pcb_export_capabilities_input",
                capabilitiesRefused.GetProperty("structuredContent").GetProperty("errorCode").GetString(), capabilitiesRefused.GetRawText());
            JsonElement readRefused = await mcp.Tool("kicad_pcb_export_read_typed", new
            { instanceId = Guid.NewGuid().ToString("D"), input = new { } });
            Assert.IsTrue(readRefused.GetProperty("isError").GetBoolean(), readRefused.GetRawText());
            Assert.AreEqual("invalid_pcb_export_read_input",
                readRefused.GetProperty("structuredContent").GetProperty("errorCode").GetString(), readRefused.GetRawText());
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
