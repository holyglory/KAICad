using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
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
    public void DurableReceiptViewExposesUnknownMcpStateWithoutRedispatchPermission()
    {
        var record = Record(PcbExportRecoveryPolicy.Indeterminate, nativeDispatchKnown: true,
            nativeDispatch: true);
        JsonElement view = PcbJobTools.DurableReceiptView(record.InstanceId, record.ProcessEpoch,
            record.OperationId, record, receipt: null);

        Assert.IsFalse(view.GetProperty("found").GetBoolean());
        Assert.IsFalse(view.GetProperty("nativeReceiptFound").GetBoolean());
        Assert.AreEqual("mcp-state", view.GetProperty("receiptScope").GetString());
        Assert.AreEqual(PcbExportRecoveryPolicy.Indeterminate, view.GetProperty("status").GetString());
        Assert.AreEqual(PcbExportRecoveryPolicy.Unknown, view.GetProperty("outcome").GetString());
        Assert.IsFalse(view.GetProperty("retryAllowed").GetBoolean());
        Assert.IsTrue(view.GetProperty("publicationMayHaveHappened").GetBoolean());
        Assert.IsTrue(view.GetProperty("receipt").ValueKind == JsonValueKind.Null);
    }

    [TestMethod]
    public void DurableCompletedReceiptViewKeepsTerminalOutcomeAndReceiptScope()
    {
        var record = Record(PcbExportRecoveryPolicy.Completed, nativeDispatchKnown: true,
            nativeDispatch: true);
        var receipt = new PcbManufacturingExportReceipt
        {
            OperationId = record.OperationId,
            ProcessEpoch = record.ProcessEpoch,
            RequestFingerprintSha256 = record.RequestFingerprintSha256,
            Status = PcbManufacturingExportStatus.PcbMfgExportCompleted,
            NativeDispatch = true,
            Terminal = true
        };
        JsonElement view = PcbJobTools.DurableReceiptView(record.InstanceId, record.ProcessEpoch,
            record.OperationId, record, receipt);

        Assert.AreEqual(PcbExportRecoveryPolicy.Completed, view.GetProperty("status").GetString());
        Assert.AreEqual(PcbExportRecoveryPolicy.OutcomeCompleted, view.GetProperty("outcome").GetString());
        Assert.IsFalse(view.GetProperty("retryAllowed").GetBoolean());
        Assert.AreEqual(JsonValueKind.Object, view.GetProperty("receipt").ValueKind);
    }

    [TestMethod]
    public void TerminalFailureWithoutOwnedPathsIsRetryable()
    {
        var prior = Record(PcbExportRecoveryPolicy.Pending, nativeDispatchKnown: true, nativeDispatch: true);
        var receipt = new PcbManufacturingExportReceipt
        {
            OperationId = prior.OperationId,
            ProcessEpoch = prior.ProcessEpoch,
            RequestFingerprintSha256 = prior.RequestFingerprintSha256,
            Status = PcbManufacturingExportStatus.PcbMfgExportFailed,
            PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationFailed,
            Terminal = true,
            ErrorCode = "native_export_failed",
            ErrorMessage = "The exporter failed without creating output."
        };

        var decision = PcbExportRecoveryPolicy.Reconcile(prior, true, receipt,
            outputExists: false, outputVerified: false, stagingExists: false);

        Assert.AreEqual(PcbExportRecoveryPolicy.Retryable, decision.Status);
        Assert.IsTrue(decision.RetryAllowed);
        Assert.IsFalse(decision.PublicationMayHaveHappened);
    }

    [TestMethod]
    [DataRow("operation")]
    [DataRow("process")]
    [DataRow("fingerprint")]
    public void MismatchedTerminalReceiptIdentityNeverAuthorizesRetry(string changedIdentity)
    {
        var prior = Record(PcbExportRecoveryPolicy.Pending, nativeDispatchKnown: true, nativeDispatch: true);
        var receipt = new PcbManufacturingExportReceipt
        {
            OperationId = prior.OperationId,
            ProcessEpoch = prior.ProcessEpoch,
            RequestFingerprintSha256 = prior.RequestFingerprintSha256,
            Status = PcbManufacturingExportStatus.PcbMfgExportFailed,
            PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationFailed,
            Terminal = true,
            ErrorCode = "native_export_failed",
            ErrorMessage = "The exporter failed without creating output."
        };

        switch (changedIdentity)
        {
            case "operation": receipt.OperationId = "44444444-4444-4444-4444-444444444444"; break;
            case "process": receipt.ProcessEpoch = "55555555-5555-5555-5555-555555555555"; break;
            case "fingerprint": receipt.RequestFingerprintSha256 = new string('b', 64); break;
        }

        var decision = PcbExportRecoveryPolicy.Reconcile(prior, true, receipt,
            outputExists: false, outputVerified: false, stagingExists: false);

        Assert.AreEqual(PcbExportRecoveryPolicy.Indeterminate, decision.Status);
        Assert.IsFalse(decision.RetryAllowed);
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
    public void KnownCancellationIsRetainedAsCancelledAndRetryable()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-export-cancelled-").FullName;
        try
        {
            var record = Record(PcbExportRecoveryPolicy.Pending, nativeDispatchKnown: false,
                nativeDispatch: false, root: root);
            var store = new PcbExportRecoveryStore(root);
            store.Begin(record.InstanceId, record.OperationId, record.ProcessEpoch, record.DocumentJson,
                record.RequestFingerprintSha256, record.OutputDirectory);
            var cancelled = store.MarkCancelled(record.OperationId);
            Assert.AreEqual(PcbExportRecoveryPolicy.Cancelled, cancelled.Status);
            Assert.IsTrue(cancelled.NativeDispatchKnown);
            Assert.IsFalse(cancelled.NativeDispatch);

            var decision = PcbExportRecoveryPolicy.Reconcile(cancelled, false, null,
                outputExists: false, outputVerified: false, stagingExists: false);
            Assert.AreEqual(PcbExportRecoveryPolicy.Cancelled, decision.Status);
            Assert.IsTrue(decision.RetryAllowed);
            Assert.IsFalse(decision.PublicationMayHaveHappened);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AgentOutcomeNamesDistinguishNativeReceiptStates()
    {
        var (request, completed) = FixtureReceipt();
        Assert.AreEqual(PcbExportRecoveryPolicy.OutcomeCompleted,
            PcbExportRecoveryPolicy.Outcome(completed, nativeDispatch: true));

        var failed = completed.Clone();
        failed.Status = PcbManufacturingExportStatus.PcbMfgExportFailed;
        Assert.AreEqual(PcbExportRecoveryPolicy.Failed,
            PcbExportRecoveryPolicy.Outcome(failed, nativeDispatch: true));

        var unknown = completed.Clone();
        unknown.Status = PcbManufacturingExportStatus.PcbMfgExportIndeterminate;
        Assert.AreEqual(PcbExportRecoveryPolicy.Unknown,
            PcbExportRecoveryPolicy.Outcome(unknown, nativeDispatch: true));

        var running = completed.Clone();
        running.Status = PcbManufacturingExportStatus.PcbMfgExportRunning;
        Assert.AreEqual(PcbExportRecoveryPolicy.Submitted,
            PcbExportRecoveryPolicy.Outcome(running, nativeDispatch: true));

        Assert.AreEqual(PcbExportRecoveryPolicy.Cancelled,
            PcbExportRecoveryPolicy.Outcome(null, nativeDispatch: false, cancellationRequested: true));
        Assert.AreEqual(PcbExportRecoveryPolicy.Unknown,
            PcbExportRecoveryPolicy.Outcome(null, nativeDispatch: true));
        _ = request;
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
    public void CompletedRecoveryRequiresTerminalReceiptAndCompleteManifest()
    {
        var prior = Record(PcbExportRecoveryPolicy.Pending, nativeDispatchKnown: true, nativeDispatch: true);
        var receipt = new PcbManufacturingExportReceipt
        {
            OperationId = prior.OperationId,
            ProcessEpoch = prior.ProcessEpoch,
            RequestFingerprintSha256 = prior.RequestFingerprintSha256,
            Status = PcbManufacturingExportStatus.PcbMfgExportCompleted,
            PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationPublished,
            Terminal = false,
            OutputManifestComplete = false
        };

        var decision = PcbExportRecoveryPolicy.Reconcile(prior, true, receipt,
            outputExists: true, outputVerified: true, stagingExists: false);

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
                RequestFingerprintSha256 = record.RequestFingerprintSha256,
                Status = PcbManufacturingExportStatus.PcbMfgExportCompleted,
                PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationPublished,
                Terminal = true,
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

    [TestMethod]
    public void DurableReceiptMustRetainRequestFingerprint()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-export-fingerprint-").FullName;
        try
        {
            var record = Record(PcbExportRecoveryPolicy.Pending, nativeDispatchKnown: true,
                nativeDispatch: true, root: root);
            var store = new PcbExportRecoveryStore(root);
            store.Begin(record.InstanceId, record.OperationId, record.ProcessEpoch, record.DocumentJson,
                record.RequestFingerprintSha256, record.OutputDirectory);
            var receipt = new PcbManufacturingExportReceipt
            {
                OperationId = record.OperationId,
                ProcessEpoch = record.ProcessEpoch,
                RequestFingerprintSha256 = new string('b', 64),
                Status = PcbManufacturingExportStatus.PcbMfgExportFailed,
                DispatchStage = PcbManufacturingDispatchStage.PcbMfgDispatchTerminal,
                Terminal = true,
                ErrorCode = "native_export_failed",
                PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationFailed
            };
            var error = Assert.ThrowsExactly<AutomationException>(() =>
                store.MarkReceipt(record.OperationId, receipt, outputExists: false,
                    outputVerified: false, stagingExists: false));
            Assert.AreEqual("invalid_pcb_export_recovery", error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DurableReceiptMustRetainOperationOwnedOutputPaths()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-export-path-binding-").FullName;
        try
        {
            var record = Record(PcbExportRecoveryPolicy.Pending, nativeDispatchKnown: true,
                nativeDispatch: true, root: root);
            var store = new PcbExportRecoveryStore(root);
            store.Begin(record.InstanceId, record.OperationId, record.ProcessEpoch, record.DocumentJson,
                record.RequestFingerprintSha256, record.OutputDirectory);
            var receipt = new PcbManufacturingExportReceipt
            {
                OperationId = record.OperationId,
                ProcessEpoch = record.ProcessEpoch,
                RequestFingerprintSha256 = record.RequestFingerprintSha256,
                Status = PcbManufacturingExportStatus.PcbMfgExportFailed,
                DispatchStage = PcbManufacturingDispatchStage.PcbMfgDispatchTerminal,
                Terminal = true,
                ErrorCode = "native_export_failed",
                ErrorMessage = "fixture failure",
                PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationFailed,
                OutputDirectory = Path.Combine(root, "foreign-output"),
                StagingDirectory = PcbExportRecoveryPolicy.StagingDirectory(record.OutputDirectory, record.OperationId)
            };

            var outputError = Assert.ThrowsExactly<AutomationException>(() => store.MarkReceipt(
                record.OperationId, receipt, outputExists: false, outputVerified: false, stagingExists: false));
            Assert.AreEqual("export_recovery_identity_mismatch", outputError.Code);

            receipt.OutputDirectory = record.OutputDirectory;
            receipt.StagingDirectory = Path.Combine(root, "foreign-staging");
            var stagingError = Assert.ThrowsExactly<AutomationException>(() => store.MarkReceipt(
                record.OperationId, receipt, outputExists: false, outputVerified: false, stagingExists: false));
            Assert.AreEqual("export_recovery_identity_mismatch", stagingError.Code);

            Assert.AreEqual(PcbExportRecoveryPolicy.Pending, store.Read(record.OperationId)!.Status,
                "A receipt with foreign paths must not change durable recovery state.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancellationRefusalReconcilesLiveTerminalReceipt()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-export-cancel-reconcile-").FullName;
        try
        {
            string instanceId = "33333333-3333-3333-3333-333333333333";
            string processEpoch = "22222222-2222-2222-2222-222222222222";
            string operationId = "11111111-1111-1111-1111-111111111111";
            var document = new DocumentSpecifier
            {
                Type = DocumentType.DoctypePcb,
                BoardFilename = "fixture.kicad_pcb",
                Project = new() { Name = "fixture", Path = root }
            };
            var state = new DocumentLifecycleState
            {
                Document = document.Clone(),
                Scope = DocumentLifecycleScope.DlsPcb,
                ProcessEpoch = processEpoch,
                StateSha256 = new string('a', 64),
                DiskBaselineChecked = true
            };
            foreach (string path in new[]
            {
                Path.Combine(root, "fixture.kicad_pcb"),
                Path.Combine(root, "fixture.kicad_pro")
            })
            {
                state.NativeFiles.Add(path);
                state.FileBaselines.Add(new NativeFileBaselineState
                {
                    Path = path,
                    BaselinePath = path,
                    BaselineKnown = true,
                    CurrentKnown = true,
                    Status = NativeFileBaselineStatus.NfbsUnchanged,
                    BaselineExists = false,
                    CurrentExists = false
                });
            }
            string output = Path.Combine(root, "out");
            string fingerprint = new string('b', 64);
            var receipt = new PcbManufacturingExportReceipt
            {
                OperationId = operationId,
                ProcessEpoch = processEpoch,
                Document = document.Clone(),
                RequestFingerprintSha256 = fingerprint,
                JobKind = "svg",
                Status = PcbManufacturingExportStatus.PcbMfgExportFailed,
                DispatchStage = PcbManufacturingDispatchStage.PcbMfgDispatchTerminal,
                NativeDispatch = true,
                Terminal = true,
                ErrorCode = "native_export_failed",
                ErrorMessage = "fixture failure",
                ExpectedState = state.Clone(),
                ObservedBefore = state.Clone(),
                OutputDirectory = output,
                StagingDirectory = PcbExportRecoveryPolicy.StagingDirectory(output, operationId),
                PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationFailed
            };
            var peer = new ManufacturingReceiptPeer(instanceId, processEpoch, root, receipt);
            var registry = new InstanceRegistry(peer, Path.Combine(root, "state"));
            await registry.AttachAsync("ipc:///tmp/kicad-pcb-export-cancel-reconcile", instanceId);

            var store = new PcbExportRecoveryStore(registry.StateDirectory);
            store.Begin(instanceId, operationId, processEpoch,
                SchematicJson.Formatter.Format(document), fingerprint, output);

            var result = await new PcbJobTools(registry).ExportCancel(instanceId,
                new NativeDocumentInput
                {
                    Type = NativeDocumentType.Pcb,
                    Project = new NativeProjectInput { Name = "fixture", Path = root },
                    BoardFilename = "fixture.kicad_pcb"
                }, processEpoch, operationId, CancellationToken.None);

            Assert.IsTrue(result.IsError, result.StructuredContent?.GetRawText());
            JsonElement data = result.StructuredContent!.Value;
            Assert.IsTrue(data.GetProperty("nativeReceiptFound").GetBoolean(), data.GetRawText());
            Assert.AreEqual("native-process", data.GetProperty("receiptScope").GetString(), data.GetRawText());
            Assert.AreEqual(PcbExportRecoveryPolicy.Retryable, data.GetProperty("status").GetString(), data.GetRawText());
            Assert.AreEqual(PcbExportRecoveryPolicy.Failed, data.GetProperty("outcome").GetString(), data.GetRawText());
            Assert.AreEqual("pcb_export_cancellation_unsupported", data.GetProperty("errorCode").GetString(), data.GetRawText());
            Assert.AreEqual(1, peer.ReceiptReads);
            Assert.AreEqual(PcbExportRecoveryPolicy.Retryable, store.Read(operationId)!.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ReceiptBindingRejectsDifferentInstanceEpochOrDocument()
    {
        var record = Record(PcbExportRecoveryPolicy.Pending, nativeDispatchKnown: false,
            nativeDispatch: false);
        string document = record.DocumentJson;

        foreach (string instance in new[]
        {
            "44444444-4444-4444-4444-444444444444",
            record.InstanceId
        })
        {
            foreach (string epoch in new[]
            {
                record.ProcessEpoch,
                "55555555-5555-5555-5555-555555555555"
            })
            {
                foreach (string target in new[] { document, document + " " })
                {
                    bool matches = instance == record.InstanceId
                        && epoch == record.ProcessEpoch
                        && target == document;
                    if (matches)
                    {
                        PcbExportRecoveryPolicy.ValidateReceiptBinding(record, instance, epoch, target);
                        continue;
                    }

                    var error = Assert.ThrowsExactly<AutomationException>(() =>
                        PcbExportRecoveryPolicy.ValidateReceiptBinding(record, instance, epoch, target));
                    Assert.AreEqual("export_recovery_identity_mismatch", error.Code);
                }
            }
        }
    }

    private sealed class ManufacturingReceiptPeer(string instanceId, string epoch, string projectPath,
        PcbManufacturingExportReceipt receipt) : INativeTransport
    {
        internal int ReceiptReads { get; private set; }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] bytes, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApiRequest request = ApiRequest.Parser.ParseFrom(bytes);
            if (request.Message.Is(GetAutomationSession.Descriptor))
                return Task.FromResult(Response(new AutomationSession
                {
                    ProtocolVersion = 1,
                    InstanceId = instanceId,
                    ProjectPath = projectPath,
                    Epoch = epoch
                }));
            if (request.Message.Is(ReadPcbManufacturingExport.Descriptor))
            {
                ReceiptReads++;
                var query = request.Message.Unpack<ReadPcbManufacturingExport>();
                if (query.OperationId != receipt.OperationId || query.ProcessEpoch != epoch
                    || !query.Document.Equals(receipt.Document))
                    return Task.FromResult(Error("manufacturing receipt query identity mismatch"));
                return Task.FromResult(Response(new PcbManufacturingExportReceiptResponse
                {
                    Found = true,
                    Receipt = receipt.Clone()
                }));
            }
            throw new AssertFailedException("Unexpected manufacturing native request: " + request.Message.TypeUrl);
        }

        private byte[] Response(IMessage message) => new ApiResponse
        {
            Header = new() { KicadToken = epoch },
            Status = new() { Status = ApiStatusCode.AsOk },
            Message = Any.Pack(message)
        }.ToByteArray();

        private byte[] Error(string message) => new ApiResponse
        {
            Header = new() { KicadToken = epoch },
            Status = new() { Status = ApiStatusCode.AsBadRequest, ErrorMessage = message }
        }.ToByteArray();
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

    private static (RunPcbManufacturingExport Request, PcbManufacturingExportReceipt Receipt) FixtureReceipt()
    {
        string operationId = "11111111-1111-1111-1111-111111111111";
        string processEpoch = "22222222-2222-2222-2222-222222222222";
        var document = new Kiapi.Common.Types.DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "fixture.kicad_pcb",
            Project = new() { Name = "fixture", Path = "/tmp/kicad-pcb-export-outcomes" }
        };
        var state = new KiCad.Automation.Protocol.DocumentLifecycleState
        {
            Document = document.Clone(), Scope = DocumentLifecycleScope.DlsPcb,
            ProcessEpoch = processEpoch, StateSha256 = new string('a', 64)
        };
        var request = new RunPcbManufacturingExport
        {
            OperationId = operationId, ProcessEpoch = processEpoch, Document = document.Clone(),
            ExpectedState = state.Clone(), OutputDirectory = "/tmp/kicad-pcb-export-outcomes/out"
        };
        var receipt = new PcbManufacturingExportReceipt
        {
            OperationId = operationId, ProcessEpoch = processEpoch, Document = document.Clone(),
            RequestFingerprintSha256 = new string('b', 64), JobKind = "svg",
            Status = PcbManufacturingExportStatus.PcbMfgExportCompleted,
            DispatchStage = PcbManufacturingDispatchStage.PcbMfgDispatchTerminal,
            NativeDispatch = true, Terminal = true, ExpectedState = state.Clone(),
            ObservedBefore = state.Clone(), ObservedAfter = state.Clone(),
            OutputDirectory = request.OutputDirectory,
            StagingDirectory = PcbExportRecoveryPolicy.StagingDirectory(request.OutputDirectory, operationId),
            OutputManifestComplete = true, OutputManifestSha256 = new string('c', 64),
            PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationPublished
        };
        return (request, receipt);
    }
}
