using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Protocol;

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
            var unknown = new FootprintLibraryCheckedReceiptStore(root).Read(instance, request.OperationId);
            Assert.IsNotNull(unknown);
            Assert.AreEqual(FootprintLibraryCheckedReceiptRecord.Unknown, unknown!.Outcome);
            Assert.AreEqual("operation_outcome_unknown", unknown.ErrorCode);

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
