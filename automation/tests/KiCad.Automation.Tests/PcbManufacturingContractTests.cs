using Kiapi.Board.Jobs;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbManufacturingContractTests
{
    [TestMethod]
    public void CompletedReceiptRequiresMatchingPublishedManifest()
    {
        var (request, receipt) = Fixture();

        PcbManufacturingContract.ValidateRequest(request);
        PcbManufacturingContract.ValidateReceipt(receipt, request.Document, request.ExpectedState,
            request.OperationId, request.ProcessEpoch);

        var terminalMismatch = receipt.Clone();
        terminalMismatch.Terminal = false;
        Reject(terminalMismatch, request, "invalid_pcb_export_receipt");

        var incompleteManifest = receipt.Clone();
        incompleteManifest.OutputManifestComplete = false;
        Reject(incompleteManifest, request, "invalid_pcb_export_receipt");

        var escapedArtifact = receipt.Clone();
        escapedArtifact.Artifacts[0].RelativePath = "../outside.svg";
        Reject(escapedArtifact, request, "invalid_pcb_export_receipt");
    }

    [TestMethod]
    public void ReceiptReadRejectsStaleIdentityAndAcceptsAdmittedState()
    {
        var (request, receipt) = Fixture();
        var stale = receipt.Clone();
        stale.ProcessEpoch = Guid.NewGuid().ToString("D");
        Reject(stale, request, "invalid_pcb_export_receipt");

        var admitted = receipt.Clone();
        admitted.Status = PcbManufacturingExportStatus.PcbMfgExportAdmitted;
        admitted.DispatchStage = PcbManufacturingDispatchStage.PcbMfgDispatchAdmitted;
        admitted.Terminal = false;
        admitted.PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationStaged;
        admitted.Artifacts.Clear();
        admitted.OutputManifestComplete = false;
        admitted.OutputManifestSha256 = "";
        admitted.ErrorCode = "";
        admitted.ErrorMessage = "";
        PcbManufacturingContract.ValidateReceipt(admitted, request.Document, null,
            request.OperationId, request.ProcessEpoch);

        var failedWithStagedArtifact = receipt.Clone();
        failedWithStagedArtifact.Status = PcbManufacturingExportStatus.PcbMfgExportIndeterminate;
        failedWithStagedArtifact.DispatchStage = PcbManufacturingDispatchStage.PcbMfgDispatchTerminal;
        failedWithStagedArtifact.PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationFailed;
        failedWithStagedArtifact.ErrorCode = "native_export_failed";
        failedWithStagedArtifact.ErrorMessage = "fixture failure";
        failedWithStagedArtifact.Artifacts[0].Path = failedWithStagedArtifact.StagingDirectory + "/export.svg";
        PcbManufacturingContract.ValidateReceipt(failedWithStagedArtifact, request.Document, null,
            request.OperationId, request.ProcessEpoch);
    }

    [TestMethod]
    public void RequestRequiresOnePcbJobAndCompleteLifecycleIdentity()
    {
        var (request, _) = Fixture();

        var noJob = request.Clone();
        noJob.ClearJob();
        RejectRequest(noJob);

        var wrongEpoch = request.Clone();
        wrongEpoch.ExpectedState.ProcessEpoch = Guid.NewGuid().ToString("D");
        RejectRequest(wrongEpoch);

        var relativeOutput = request.Clone();
        relativeOutput.OutputDirectory = "relative-output";
        RejectRequest(relativeOutput);
    }

    private static void Reject(PcbManufacturingExportReceipt receipt,
        RunPcbManufacturingExport request, string code)
    {
        var error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbManufacturingContract.ValidateReceipt(receipt, request.Document, request.ExpectedState,
                request.OperationId, request.ProcessEpoch));
        Assert.AreEqual(code, error.Code);
    }

    private static void RejectRequest(RunPcbManufacturingExport request)
    {
        var error = Assert.ThrowsExactly<AutomationException>(() => PcbManufacturingContract.ValidateRequest(request));
        Assert.AreEqual("invalid_pcb_export_receipt", error.Code);
    }

    private static (RunPcbManufacturingExport Request, PcbManufacturingExportReceipt Receipt) Fixture()
    {
        string operationId = "11111111-1111-1111-1111-111111111111";
        string processEpoch = "22222222-2222-2222-2222-222222222222";
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "fixture.kicad_pcb",
            Project = new() { Name = "fixture", Path = "/tmp/kicad-manufacturing-contract" }
        };
        var state = new DocumentLifecycleState
        {
            Document = document.Clone(),
            Scope = DocumentLifecycleScope.DlsPcb,
            ProcessEpoch = processEpoch,
            StateSha256 = new string('a', 64)
        };
        var request = new RunPcbManufacturingExport
        {
            OperationId = operationId,
            ProcessEpoch = processEpoch,
            Document = document.Clone(),
            ExpectedState = state.Clone(),
            OutputDirectory = "/tmp/kicad-manufacturing-contract/out",
            ExportSvg = new RunBoardJobExportSvg
            {
                JobSettings = new() { Document = document.Clone() }
            }
        };
        var receipt = new PcbManufacturingExportReceipt
        {
            OperationId = operationId,
            ProcessEpoch = processEpoch,
            Document = document.Clone(),
            RequestFingerprintSha256 = new string('b', 64),
            JobKind = "svg",
            Status = PcbManufacturingExportStatus.PcbMfgExportCompleted,
            DispatchStage = PcbManufacturingDispatchStage.PcbMfgDispatchTerminal,
            NativeDispatch = true,
            Terminal = true,
            ExpectedState = state.Clone(),
            ObservedBefore = state.Clone(),
            ObservedAfter = state.Clone(),
            OutputDirectory = "/tmp/kicad-manufacturing-contract/out",
            StagingDirectory = "/tmp/kicad-manufacturing-contract/out.kicad-staging-11111111-1111-1111-1111-111111111111",
            OutputManifestComplete = true,
            OutputManifestSha256 = new string('c', 64),
            PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationPublished
        };
        receipt.Artifacts.Add(new PcbManufacturingArtifact
        {
            Path = "/tmp/kicad-manufacturing-contract/out/export.svg",
            RelativePath = "export.svg",
            Bytes = 12,
            Sha256 = new string('d', 64),
            Ownership = PcbManufacturingArtifactOwnership.PcbMfgArtifactCreated
        });
        return (request, receipt);
    }
}
