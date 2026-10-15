using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using System.Text.Json.Nodes;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class FootprintLibraryCheckedReceiptStoreTests
{
    [TestMethod]
    public void AdmissionSurvivesReopenAndUnknownOutcomeBlocksRedispatch()
    {
        string root = Directory.CreateTempSubdirectory("footprint-checked-receipts-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = Request(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
            var first = new FootprintLibraryCheckedReceiptStore(root);
            var admission = first.Reserve(instance, request);
            Assert.IsTrue(admission.Created);

            var reopened = new FootprintLibraryCheckedReceiptStore(root);
            var duplicate = reopened.Reserve(instance, request);
            Assert.IsFalse(duplicate.Created);
            Assert.AreEqual(FootprintLibraryCheckedReceiptRecord.Admitted, duplicate.Receipt.Outcome);

            reopened.WriteUnknown(instance, request, "operation_outcome_unknown", "transport loss");
            var recovered = new FootprintLibraryCheckedReceiptStore(root);
            var unknown = recovered.ReadBound(instance, request);
            Assert.IsNotNull(unknown);
            Assert.AreEqual(FootprintLibraryCheckedReceiptRecord.Unknown, unknown!.Outcome);
            Assert.AreEqual("operation_outcome_unknown", unknown.ErrorCode);
            Assert.AreEqual(request.ProcessEpoch, unknown.ProcessEpoch);

            var replacedProcess = request.Clone();
            replacedProcess.ProcessEpoch = Guid.NewGuid().ToString("D");
            AutomationException epochConflict = Assert.ThrowsExactly<AutomationException>(() =>
                recovered.ReadBound(instance, replacedProcess));
            Assert.AreEqual("operation_id_conflict", epochConflict.Code);

            var blocked = new FootprintLibraryCheckedReceiptStore(root).Reserve(instance, request);
            Assert.IsFalse(blocked.Created);
            Assert.AreEqual(FootprintLibraryCheckedReceiptRecord.Unknown, blocked.Receipt.Outcome);

            var conflict = request.Clone();
            conflict.ExpectedSnapshotSha256 = new string('f', 64);
            Assert.ThrowsExactly<AutomationException>(() =>
                new FootprintLibraryCheckedReceiptStore(root).Reserve(instance, conflict));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("missing_document")]
    [DataRow("other_project")]
    [DataRow("other_entry")]
    [DataRow("other_process")]
    [DataRow("missing_revision")]
    [DataRow("other_revision_epoch")]
    public void ExpectedStateMustRetainTheRequestDocumentAndProcess(string mismatch)
    {
        string root = Directory.CreateTempSubdirectory("footprint-checked-binding-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = Request(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
            var mismatched = request.Clone();
            switch (mismatch)
            {
                case "missing_document": mismatched.ExpectedState.Document = null; break;
                case "other_project": mismatched.ExpectedState.Document.Project.Path = "/tmp/other-project"; break;
                case "other_entry": mismatched.ExpectedState.Document.LibId.EntryName = "Other"; break;
                case "other_process": mismatched.ExpectedState.ProcessEpoch = Guid.NewGuid().ToString("D"); break;
                case "missing_revision": mismatched.ExpectedState.Revision = null; break;
                case "other_revision_epoch": mismatched.ExpectedState.Revision.Epoch = Guid.NewGuid().ToString("D"); break;
                default: Assert.Fail("Unexpected expected-state mismatch."); break;
            }
            // Rehash every changed byte so these cases exercise lifecycle binding,
            // rather than merely rejecting a stale digest or request fingerprint.
            mismatched.ExpectedState.StateSha256 =
                FootprintLibraryCheckedContract.ComputeStateDigest(mismatched.ExpectedState);
            mismatched.ExpectedSnapshotSha256 = mismatched.ExpectedState.StateSha256;

            var store = new FootprintLibraryCheckedReceiptStore(root);
            AutomationException lookupFailure = Assert.ThrowsExactly<AutomationException>(() =>
                store.ReadBound(instance, mismatched));
            Assert.AreEqual("invalid_footprint_receipt", lookupFailure.Code);
            AutomationException admissionFailure = Assert.ThrowsExactly<AutomationException>(() =>
                store.Reserve(instance, mismatched));
            Assert.AreEqual("invalid_footprint_receipt", admissionFailure.Code);

            // A receipt written by an older process may have a self-consistent
            // fingerprint while its checkpoint names another native lifecycle.
            _ = store.Reserve(instance, request);
            string path = Directory.GetFiles(Path.Combine(root, "footprint-library-checked-receipts"), "*.json").Single();
            JsonObject journal = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            journal["requestJson"] = SchematicJson.Formatter.Format(mismatched);
            journal["requestFingerprintSha256"] = FootprintLibraryCheckedContract.ComputeFingerprint(mismatched);
            File.WriteAllText(path, journal.ToJsonString());

            var reopened = new FootprintLibraryCheckedReceiptStore(root);
            AutomationException recoveryFailure = Assert.ThrowsExactly<AutomationException>(() =>
                reopened.Read(instance, request.OperationId));
            Assert.AreEqual("invalid_footprint_receipt", recoveryFailure.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TerminalReceiptSurvivesReopenAndRejectsTampering()
    {
        string root = Directory.CreateTempSubdirectory("footprint-checked-terminal-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = Request(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
            var store = new FootprintLibraryCheckedReceiptStore(root);
            _ = store.Reserve(instance, request);
            var response = new NativeCapabilityFootprintLibraryCheckedWriteResponse
            {
                Document = request.Document.Clone(), ProcessEpoch = request.ProcessEpoch,
                OperationId = request.OperationId, Status = "unavailable",
                ErrorCode = "library_mutation_unavailable", ErrorMessage = "provider unavailable",
                ReceiptStatus = NativeCapabilityFootprintLibraryCheckedReceiptStatus.NcpFlcsUnavailable,
                Scope = "mcp_direct_footprint_file", ExpectedRequestVerified = true,
                Request = request.Clone(),
                RequestFingerprintSha256 = FootprintLibraryCheckedContract.ComputeFingerprint(request)
            };
            store.WriteTerminal(instance, request, response);

            var reopened = new FootprintLibraryCheckedReceiptStore(root);
            var record = reopened.Read(instance, request.OperationId);
            Assert.IsNotNull(record);
            Assert.AreEqual(FootprintLibraryCheckedReceiptRecord.Terminal, record!.Outcome);
            var restored = FootprintLibraryCheckedReceiptStore.ParseResponse(record.ResponseJson);
            FootprintLibraryCheckedContract.ValidateResponse(restored, request);
            Assert.AreEqual(response, restored);

            string path = Directory.GetFiles(Path.Combine(root, "footprint-library-checked-receipts"), "*.json").Single();
            File.WriteAllText(path, File.ReadAllText(path).Replace(request.OperationId,
                Guid.NewGuid().ToString("D"), StringComparison.Ordinal));
            Assert.ThrowsExactly<AutomationException>(() => reopened.Read(instance, request.OperationId));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PreDispatchAdmissionCanBeRemovedOnlyForTheExactRequest()
    {
        string root = Directory.CreateTempSubdirectory("footprint-checked-remove-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = Request(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
            var store = new FootprintLibraryCheckedReceiptStore(root);
            _ = store.Reserve(instance, request);
            store.RemoveAdmission(instance, request);
            var retry = new FootprintLibraryCheckedReceiptStore(root).Reserve(instance, request);
            Assert.IsTrue(retry.Created);
        }
        finally { Directory.Delete(root, true); }
    }

    private static NativeCapabilityFootprintLibraryCheckedWrite Request(string operationId,
        string processEpoch)
    {
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeFootprint,
            Project = new() { Name = "Fixture", Path = "/tmp/fixture" },
            LibId = new() { LibraryNickname = "Fixture", EntryName = "Probe" }
        };
        var state = new NativeCapabilityFootprintLibraryCheckedState
        {
            Document = document.Clone(), ProcessEpoch = processEpoch,
            Scope = "mcp_direct_footprint_file", StateComplete = true,
            DiskBaselineChecked = true,
            Revision = new() { Epoch = processEpoch, Sequence = 1 }
        };
        state.DependencyBaselines.Add(new NativeCapabilityLibrarySnapshotDependencyBaseline
        {
            Path = "/tmp/fixture/Fixture.pretty/Probe.kicad_mod", Known = true,
            Exists = true, Sha256 = new string('a', 64), Bytes = 1
        });
        state.DetachedDefinitionSha256 = new string('b', 64);
        state.StateSha256 = FootprintLibraryCheckedContract.ComputeStateDigest(state);
        return new()
        {
            Document = document, ProcessEpoch = processEpoch, OperationId = operationId,
            ExpectedSnapshotSha256 = state.StateSha256, ExpectedState = state,
            Footprint = new()
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") },
                Definition = new() { Id = document.LibId.Clone() }
            },
            Overwrite = true
        };
    }
}
