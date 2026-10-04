using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbExportRecoveryTests
{
    [TestMethod]
    public void TerminalFailureWithoutOwnedPathsIsRetryable()
    {
        var prior = Record(PcbExportRecoveryPolicy.Pending, nativeDispatchKnown: true, nativeDispatch: true);
        var receipt = new PcbManufacturingExportReceipt
        {
            OperationId = prior.OperationId,
            ProcessEpoch = prior.ProcessEpoch,
            Status = PcbManufacturingExportStatus.PcbMfgExportFailed,
            PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationFailed
        };

        var decision = PcbExportRecoveryPolicy.Reconcile(prior, true, receipt,
            outputExists: false, outputVerified: false, stagingExists: false);

        Assert.AreEqual(PcbExportRecoveryPolicy.Retryable, decision.Status);
        Assert.IsTrue(decision.RetryAllowed);
        Assert.IsFalse(decision.PublicationMayHaveHappened);
    }

    [TestMethod]
    public void IndeterminateNeverAuthorizesRetry()
    {
        var prior = Record(PcbExportRecoveryPolicy.Indeterminate, nativeDispatchKnown: true, nativeDispatch: true);

        var decision = PcbExportRecoveryPolicy.Reconcile(prior, false, null,
            outputExists: false, outputVerified: false, stagingExists: false);

        Assert.AreEqual(PcbExportRecoveryPolicy.Indeterminate, decision.Status);
        Assert.IsFalse(decision.RetryAllowed);
    }

    [TestMethod]
    public void KnownUnsubmittedCancellationIsRetryable()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-export-cancel-").FullName;
        try
        {
            var record = Record(PcbExportRecoveryPolicy.Pending, nativeDispatchKnown: false,
                nativeDispatch: false, root: root);
            var store = new PcbExportRecoveryStore(root);
            store.Begin(record.InstanceId, record.OperationId, record.ProcessEpoch, record.DocumentJson,
                record.RequestFingerprintSha256, record.OutputDirectory);
            var after = store.MarkUnknown(record.OperationId, nativeDispatch: false);
            Assert.AreEqual(PcbExportRecoveryPolicy.Retryable, after.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PublishedReceiptRequiresVerifiedOutput()
    {
        var prior = Record(PcbExportRecoveryPolicy.Pending, nativeDispatchKnown: true, nativeDispatch: true);
        var receipt = new PcbManufacturingExportReceipt
        {
            OperationId = prior.OperationId,
            ProcessEpoch = prior.ProcessEpoch,
            Status = PcbManufacturingExportStatus.PcbMfgExportCompleted,
            PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationPublished,
            OutputManifestComplete = true
        };

        var decision = PcbExportRecoveryPolicy.Reconcile(prior, true, receipt,
            outputExists: true, outputVerified: false, stagingExists: false);

        Assert.AreEqual(PcbExportRecoveryPolicy.Indeterminate, decision.Status);
        Assert.IsFalse(decision.RetryAllowed);
    }

    [TestMethod]
    public void DurableStoreBindsIdentityAndRefusesIndeterminateResubmit()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-export-recovery-").FullName;
        try
        {
            var record = Record(PcbExportRecoveryPolicy.Pending, nativeDispatchKnown: false, nativeDispatch: false,
                root: root);
            var store = new PcbExportRecoveryStore(root);
            store.Begin(record.InstanceId, record.OperationId, record.ProcessEpoch, record.DocumentJson,
                record.RequestFingerprintSha256, record.OutputDirectory);
            store.MarkUnknown(record.OperationId, nativeDispatch: true);

            var error = Assert.ThrowsExactly<AutomationException>(() =>
                store.Begin(record.InstanceId, record.OperationId, record.ProcessEpoch, record.DocumentJson,
                    record.RequestFingerprintSha256, record.OutputDirectory));
            Assert.AreEqual("export_status_required", error.Code);

            var mismatch = Assert.ThrowsExactly<AutomationException>(() =>
                store.Begin(record.InstanceId, record.OperationId, record.ProcessEpoch, record.DocumentJson,
                    new string('f', 64), record.OutputDirectory));
            Assert.AreEqual("export_operation_reused", mismatch.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LostReplayReplyDoesNotInvalidateCompletedReceipt()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-export-replay-").FullName;
        try
        {
            var record = Record(PcbExportRecoveryPolicy.Completed, nativeDispatchKnown: true,
                nativeDispatch: true, root: root);
            Directory.CreateDirectory(record.OutputDirectory);
            string artifactPath = Path.Combine(record.OutputDirectory, "export.svg");
            File.WriteAllText(artifactPath, "svg");
            byte[] artifactBytes = File.ReadAllBytes(artifactPath);
            var receipt = new PcbManufacturingExportReceipt
            {
                OperationId = record.OperationId,
                ProcessEpoch = record.ProcessEpoch,
                Status = PcbManufacturingExportStatus.PcbMfgExportCompleted,
                PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationPublished,
                OutputDirectory = record.OutputDirectory,
                OutputManifestComplete = true,
                OutputManifestSha256 = new string('a', 64)
            };
            receipt.Artifacts.Add(new PcbManufacturingArtifact
            {
                Path = artifactPath,
                RelativePath = "export.svg",
                Bytes = (ulong)artifactBytes.Length,
                Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(artifactBytes)),
                Ownership = PcbManufacturingArtifactOwnership.PcbMfgArtifactCreated
            });
            var store = new PcbExportRecoveryStore(root);
            store.Begin(record.InstanceId, record.OperationId, record.ProcessEpoch, record.DocumentJson,
                record.RequestFingerprintSha256, record.OutputDirectory);
            store.MarkReceipt(record.OperationId, receipt, outputExists: true,
                outputVerified: true, stagingExists: false);
            var after = store.MarkUnknown(record.OperationId, nativeDispatch: true);
            Assert.AreEqual(PcbExportRecoveryPolicy.Completed, after.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    private static PcbExportRecoveryRecord Record(string status, bool nativeDispatchKnown,
        bool nativeDispatch, string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "kicad-pcb-export-recovery-fixture");
        string operationId = "11111111-1111-1111-1111-111111111111";
        string processEpoch = "22222222-2222-2222-2222-222222222222";
        string instanceId = "33333333-3333-3333-3333-333333333333";
        string output = Path.GetFullPath(Path.Combine(root, "out"));
        string document = SchematicJson.Formatter.Format(new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "fixture.kicad_pcb",
            Project = new() { Name = "fixture", Path = root }
        });
        return new PcbExportRecoveryRecord(1, instanceId, operationId, processEpoch, document,
            new string('a', 64), output,
            PcbExportRecoveryPolicy.StagingDirectory(output, operationId), status,
            nativeDispatchKnown, nativeDispatch, nativeDispatch, null, DateTimeOffset.UtcNow);
    }
}
