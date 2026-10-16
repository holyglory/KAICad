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

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SymbolLibraryCheckedContractTests
{
    [TestMethod]
    public void UnavailableResponseIsAnHonestTerminalReceipt()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "unavailable", errorCode: "library_mutation_unavailable",
            errorMessage: "The native lifecycle owner is not available.", replayed: true);

        SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected);
    }

    [TestMethod]
    public void CommittedResponseRequiresCompleteSnapshotAndLifecycleEvidence()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "committed", committed: true,
            before: expected, after: new string('b', 64));

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected));

        Assert.AreEqual("invalid_symbol_library_response", error.Code);
    }

    [TestMethod]
    public void CommittedResponseBindsTheExpectedSnapshotAndIsAccepted()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "committed_postverified", committed: true, revisionTracked: true,
            completeLifecycle: true, before: expected, after: new string('b', 64));

        SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected);
    }

    [TestMethod]
    public void CommitFromAnotherSnapshotIsRejectedAsStale()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "committed", committed: true, revisionTracked: true, completeLifecycle: true,
            before: new string('c', 64), after: new string('b', 64));

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected));

        Assert.AreEqual("stale_library_state", error.Code);
    }

    [TestMethod]
    public void IdentityAndErrorShapeAreCheckedBeforeReceiptUse()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "unavailable", errorCode: "library_mutation_unavailable");

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected));

        Assert.AreEqual("invalid_symbol_library_response", error.Code);
    }

    [TestMethod]
    public void RollbackReceiptMustProveRollbackCompletion()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "rolled_back", errorCode: "symbol_library_write_failed", errorMessage: "write failed");

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected));

        Assert.AreEqual("invalid_symbol_library_response", error.Code);
    }

    [TestMethod]
    public void DirectFileReceiptRequiresTargetUnrelatedAndAdvancingRevisionEvidence()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        var before = State(document, epoch, sequence: 7, expected);
        string expectedSnapshot = before.StateSha256;
        var after = State(document, epoch, sequence: 8, new string('b', 64));
        after.EntryExists = true;
        after.DetachedDefinitionSha256 = new string('d', 64);
        after.StateSha256 = SymbolLibraryCheckedContract.ComputeStateDigest(after);
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "committed_postverified", committed: true, revisionTracked: true,
            completeLifecycle: false, before: expectedSnapshot, after: after.StateSha256);
        response.ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsCommittedPostverified;
        response.Scope = "mcp_direct_symbol_file";
        response.RequestFingerprintSha256 = new string('c', 64);
        response.ExpectedRequestVerified = true;
        response.NativeWriteStarted = true;
        response.Postverified = true;
        response.TargetVerified = true;
        response.UnrelatedEntriesVerified = true;
        response.ObservedBefore = before;
        response.ObservedAfter = after;
        response.Request = new NativeCapabilitySymbolLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = epoch, OperationId = operation,
            ExpectedSnapshotSha256 = expectedSnapshot, ExpectedState = before
        };
        response.RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(response.Request);

        SymbolLibraryCheckedContract.ValidateResponse(response, response.Request);
    }

    [TestMethod]
    public void DirectFileReceiptAcceptsTargetCreationWithChangedEntryCheckpoint()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        var before = State(document, epoch, sequence: 7, expected);
        var after = State(document, epoch, sequence: 8, new string('b', 64));
        after.EntryExists = true;
        after.DetachedDefinitionSha256 = new string('d', 64);
        after.DependencyBaselines[1].Sha256 = new string('e', 64);
        after.DependencyBaselines[1].Bytes = 19;
        after.StateSha256 = SymbolLibraryCheckedContract.ComputeStateDigest(after);
        var request = new NativeCapabilitySymbolLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = epoch, OperationId = operation,
            ExpectedSnapshotSha256 = before.StateSha256, ExpectedState = before
        };
        var response = Response(document, epoch, operation,
            status: "committed_postverified", committed: true, revisionTracked: true,
            completeLifecycle: false, before: before.StateSha256, after: after.StateSha256);
        response.ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsCommittedPostverified;
        response.Scope = "mcp_direct_symbol_file";
        response.ExpectedRequestVerified = true;
        response.NativeWriteStarted = true;
        response.Postverified = true;
        response.TargetVerified = true;
        response.UnrelatedEntriesVerified = true;
        response.ObservedBefore = before;
        response.ObservedAfter = after;
        response.Request = request;
        response.RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(request);

        SymbolLibraryCheckedContract.ValidateResponse(response, request);
    }

    [TestMethod]
    public void DirectFileReceiptRejectsAfterCheckpointWithInvalidEntryDigest()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        var before = State(document, epoch, sequence: 7, expected);
        var after = State(document, epoch, sequence: 8, new string('b', 64));
        after.EntryExists = true;
        after.DetachedDefinitionSha256 = "invalid-entry-digest";
        after.StateSha256 = SymbolLibraryCheckedContract.ComputeStateDigest(after);
        var request = new NativeCapabilitySymbolLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = epoch, OperationId = operation,
            ExpectedSnapshotSha256 = before.StateSha256, ExpectedState = before
        };
        var response = Response(document, epoch, operation,
            status: "committed_postverified", committed: true, revisionTracked: true,
            completeLifecycle: false, before: before.StateSha256, after: after.StateSha256);
        response.ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsCommittedPostverified;
        response.Scope = "mcp_direct_symbol_file";
        response.ExpectedRequestVerified = true;
        response.NativeWriteStarted = true;
        response.Postverified = true;
        response.TargetVerified = true;
        response.UnrelatedEntriesVerified = true;
        response.ObservedBefore = before;
        response.ObservedAfter = after;
        response.Request = request;
        response.RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(request);

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, request));

        Assert.AreEqual("invalid_symbol_library_response", error.Code);
    }

    [TestMethod]
    public void DirectFileReceiptRejectsACommitWithoutAnAdvancingRevision()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        var before = State(document, epoch, sequence: 7, expected);
        var after = State(document, epoch, sequence: 7, new string('b', 64));
        var request = new NativeCapabilitySymbolLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = epoch, OperationId = operation,
            ExpectedSnapshotSha256 = before.StateSha256, ExpectedState = before
        };
        var response = Response(document, epoch, operation,
            status: "committed_postverified", committed: true, revisionTracked: true,
            completeLifecycle: false, before: before.StateSha256, after: after.StateSha256);
        response.ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsCommittedPostverified;
        response.Scope = "mcp_direct_symbol_file";
        response.ExpectedRequestVerified = true;
        response.NativeWriteStarted = true;
        response.Postverified = true;
        response.TargetVerified = true;
        response.UnrelatedEntriesVerified = true;
        response.ObservedBefore = before;
        response.ObservedAfter = after;
        response.Request = request;
        response.RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(request);

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, request));

        Assert.AreEqual("invalid_symbol_library_response", error.Code);
    }

    [TestMethod]
    public void DirectFileReceiptAcceptsAnIdenticalNoOpAtTheSameRevision()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        var before = State(document, epoch, sequence: 7, expected);
        before.EntryExists = true;
        before.DetachedDefinitionSha256 = new string('d', 64);
        before.StateSha256 = SymbolLibraryCheckedContract.ComputeStateDigest(before);
        var request = new NativeCapabilitySymbolLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = epoch, OperationId = operation,
            ExpectedSnapshotSha256 = before.StateSha256, ExpectedState = before
        };
        var response = Response(document, epoch, operation,
            status: "committed_postverified", committed: true, revisionTracked: true,
            completeLifecycle: false, before: before.StateSha256, after: before.StateSha256);
        response.ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsCommittedPostverified;
        response.Scope = "mcp_direct_symbol_file";
        response.ExpectedRequestVerified = true;
        response.NativeWriteStarted = true;
        response.Postverified = true;
        response.TargetVerified = true;
        response.UnrelatedEntriesVerified = true;
        response.ObservedBefore = before;
        response.ObservedAfter = before.Clone();
        response.Request = request;
        response.RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(request);

        SymbolLibraryCheckedContract.ValidateResponse(response, request);
    }

    [TestMethod]
    public void DirectFileReceiptRejectsACommitWithARegressedRevision()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        var before = State(document, epoch, sequence: 7, expected);
        var after = State(document, epoch, sequence: 6, expected);
        var request = new NativeCapabilitySymbolLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = epoch, OperationId = operation,
            ExpectedSnapshotSha256 = before.StateSha256, ExpectedState = before
        };
        var response = Response(document, epoch, operation,
            status: "committed_postverified", committed: true, revisionTracked: true,
            completeLifecycle: false, before: before.StateSha256, after: after.StateSha256);
        response.ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsCommittedPostverified;
        response.Scope = "mcp_direct_symbol_file";
        response.ExpectedRequestVerified = true;
        response.NativeWriteStarted = true;
        response.Postverified = true;
        response.TargetVerified = true;
        response.UnrelatedEntriesVerified = true;
        response.ObservedBefore = before;
        response.ObservedAfter = after;
        response.Request = request;
        response.RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(request);

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, request));

        Assert.AreEqual("invalid_symbol_library_response", error.Code);
    }

    [TestMethod]
    public void DirectFileReceiptRejectsCommitWithoutPostverificationProof()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        var response = Response(document, epoch, operation,
            status: "committed", committed: true, revisionTracked: true,
            completeLifecycle: true, before: expected, after: new string('b', 64));
        response.ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsCommittedPostverified;
        response.Scope = "mcp_direct_symbol_file";
        response.RequestFingerprintSha256 = new string('c', 64);
        response.ExpectedRequestVerified = true;
        response.NativeWriteStarted = true;
        response.Request = new NativeCapabilitySymbolLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = epoch, OperationId = operation,
            ExpectedSnapshotSha256 = expected
        };
        response.RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(response.Request);

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            SymbolLibraryCheckedContract.ValidateResponse(response, response.Request));

        Assert.AreEqual("invalid_symbol_library_response", error.Code);
    }

    [TestMethod]
    public void PreAdmissionRejectionMayUseLegacyUnknownReceiptStatus()
    {
        (DocumentSpecifier document, string epoch, string operation, string expected) = Fixture();
        NativeCapabilitySymbolLibraryCheckedWriteResponse response = Response(document, epoch, operation,
            status: "rejected", errorCode: "invalid_library_document",
            errorMessage: "The project-bound symbol document is invalid.");

        // Identity was rejected before a request fingerprint existed.  The UNKNOWN enum is an
        // honest pre-admission terminal envelope and remains backward compatible with older peers.
        SymbolLibraryCheckedContract.ValidateResponse(response, document, epoch, operation, expected);
    }

    [TestMethod]
    public void DurableAdmissionSurvivesMcpReplacementAndUnknownOutcomeBlocksRedispatch()
    {
        string root = Directory.CreateTempSubdirectory("symbol-checked-receipts-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = ReceiptRequest();
            var first = new SymbolLibraryCheckedReceiptStore(root);
            Assert.IsTrue(first.Reserve(instance, request).Created);

            var reopened = new SymbolLibraryCheckedReceiptStore(root);
            var duplicate = reopened.Reserve(instance, request);
            Assert.IsFalse(duplicate.Created);
            Assert.AreEqual(SymbolLibraryCheckedReceiptRecord.Admitted, duplicate.Receipt.Outcome);

            reopened.WriteUnknown(instance, request, "operation_outcome_unknown", "transport loss");
            var unknown = new SymbolLibraryCheckedReceiptStore(root).Read(instance, request.OperationId);
            Assert.IsNotNull(unknown);
            Assert.AreEqual(SymbolLibraryCheckedReceiptRecord.Unknown, unknown!.Outcome);

            var blocked = new SymbolLibraryCheckedReceiptStore(root).Reserve(instance, request);
            Assert.IsFalse(blocked.Created);
            Assert.AreEqual(SymbolLibraryCheckedReceiptRecord.Unknown, blocked.Receipt.Outcome);
            Assert.IsNull(new SymbolLibraryCheckedReceiptStore(root).Read(
                Guid.NewGuid().ToString("D"), request.OperationId));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DurableTerminalReceiptSurvivesMcpReplacementAndRejectsTampering()
    {
        string root = Directory.CreateTempSubdirectory("symbol-checked-terminal-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = ReceiptRequest();
            var store = new SymbolLibraryCheckedReceiptStore(root);
            _ = store.Reserve(instance, request);
            var response = new NativeCapabilitySymbolLibraryCheckedWriteResponse
            {
                Document = request.Document.Clone(), ProcessEpoch = request.ProcessEpoch,
                OperationId = request.OperationId, Status = "unavailable",
                ErrorCode = "library_mutation_unavailable", ErrorMessage = "provider unavailable",
                ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsUnavailable,
                Scope = "mcp_direct_symbol_file", ExpectedRequestVerified = true,
                Request = request.Clone(),
                RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(request)
            };
            store.WriteTerminal(instance, request, response);

            var reopened = new SymbolLibraryCheckedReceiptStore(root);
            var record = reopened.Read(instance, request.OperationId);
            Assert.IsNotNull(record);
            Assert.AreEqual(SymbolLibraryCheckedReceiptRecord.Terminal, record!.Outcome);
            var restored = SymbolLibraryCheckedReceiptStore.ParseResponse(record.ResponseJson);
            SymbolLibraryCheckedContract.ValidateResponse(restored, request);
            Assert.AreEqual(response, restored);

            string path = Directory.GetFiles(Path.Combine(root, "symbol-library-checked-receipts"), "*.json").Single();
            File.WriteAllText(path, File.ReadAllText(path).Replace(request.OperationId,
                Guid.NewGuid().ToString("D"), StringComparison.Ordinal));
            Assert.ThrowsExactly<AutomationException>(() => reopened.Read(instance, request.OperationId));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DurableReceiptRecoveryBindsTheOriginalProcessEpoch()
    {
        string root = Directory.CreateTempSubdirectory("symbol-checked-epoch-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = ReceiptRequest();
            var first = new SymbolLibraryCheckedReceiptStore(root);
            _ = first.Reserve(instance, request);
            first.WriteUnknown(instance, request, "operation_outcome_unknown", "native process ended after dispatch");

            // Reopening the MCP store after a native replacement must recover the
            // exact old-epoch request without authorizing another dispatch.
            var reopened = new SymbolLibraryCheckedReceiptStore(root);
            var recovered = reopened.ReadBound(instance, request);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(SymbolLibraryCheckedReceiptRecord.Unknown, recovered!.Outcome);
            Assert.AreEqual(request.ProcessEpoch, recovered.ProcessEpoch);

            var changedEpoch = request.Clone();
            changedEpoch.ProcessEpoch = Guid.NewGuid().ToString("D");
            AutomationException conflict = Assert.ThrowsExactly<AutomationException>(() =>
                reopened.ReadBound(instance, changedEpoch));
            Assert.AreEqual("operation_id_conflict", conflict.Code);
            Assert.AreEqual(SymbolLibraryCheckedReceiptRecord.Unknown,
                reopened.Reserve(instance, request).Receipt.Outcome);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DurableUnknownReceiptIsRecoverableAcrossNativeRestartWithoutRedispatch()
    {
        string root = Directory.CreateTempSubdirectory("symbol-checked-restart-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = ReceiptRequest();
            var first = new SymbolLibraryCheckedReceiptStore(root);
            _ = first.Reserve(instance, request);
            first.WriteUnknown(instance, request, "operation_outcome_unknown", "native process ended after dispatch");

            string replacementEpoch = Guid.NewGuid().ToString("D");
            var reopened = new SymbolLibraryCheckedReceiptStore(root);
            SymbolLibraryCheckedReceiptRecord? recovered = reopened.ReadBoundAfterNativeRestart(
                instance, request, replacementEpoch);

            Assert.IsNotNull(recovered);
            Assert.AreEqual(SymbolLibraryCheckedReceiptRecord.Unknown, recovered!.Outcome);
            Assert.AreEqual(request.ProcessEpoch, recovered.ProcessEpoch);

            // A caller holding the replacement epoch cannot turn the old
            // operation into a new admission: the durable old request remains
            // bound to its original epoch and exact bytes.
            var replacementRequest = request.Clone();
            replacementRequest.ProcessEpoch = replacementEpoch;
            AutomationException conflict = Assert.ThrowsExactly<AutomationException>(() =>
                reopened.Reserve(instance, replacementRequest));
            Assert.AreEqual("operation_id_conflict", conflict.Code);
            Assert.AreEqual(SymbolLibraryCheckedReceiptRecord.Unknown,
                reopened.Read(instance, request.OperationId)!.Outcome);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CheckedReceiptReturnsTerminalDurableEvidenceAfterNativeRestartWithoutLookup()
    {
        string root = Directory.CreateTempSubdirectory("symbol-checked-tool-terminal-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = ReceiptRequest();
            var store = new SymbolLibraryCheckedReceiptStore(root);
            _ = store.Reserve(instance, request);
            store.WriteTerminal(instance, request, TerminalResponse(request));

            string replacementEpoch = Guid.NewGuid().ToString("D");
            var transport = new SymbolReceiptTransport(instance, replacementEpoch);
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), instance);

            var result = await new SchematicSymbolLibraryTools(registry).CheckedReceipt(
                instance, replacementEpoch, SchematicJson.Formatter.Format(request.Document),
                request.OperationId, SchematicJson.Formatter.Format(request), default);

            Assert.IsFalse(result.IsError ?? false, JsonSerializer.Serialize(result.StructuredContent));
            var data = JsonSerializer.SerializeToElement(result.StructuredContent);
            Assert.AreEqual(replacementEpoch, data.GetProperty("processEpoch").GetString());
            Assert.AreEqual(request.ProcessEpoch, data.GetProperty("durableProcessEpoch").GetString());
            Assert.IsTrue(data.GetProperty("durableRecovery").GetBoolean());
            Assert.IsFalse(data.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.IsFalse(data.GetProperty("retryDispatched").GetBoolean());
            Assert.AreEqual("terminal-receipt", data.GetProperty("retrySafety").GetString());
            Assert.AreEqual("unavailable", data.GetProperty("receipt").GetProperty("status").GetString());
            Assert.AreEqual(0, transport.NativeCalls,
                "A receipt-only recovery must not issue a native checked-read request.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CheckedReceiptReturnsFailClosedUnknownAfterNativeRestartWithoutRetry()
    {
        string root = Directory.CreateTempSubdirectory("symbol-checked-tool-unknown-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = ReceiptRequest();
            var store = new SymbolLibraryCheckedReceiptStore(root);
            _ = store.Reserve(instance, request);
            store.WriteUnknown(instance, request, "operation_outcome_unknown", "native process ended after dispatch");

            string replacementEpoch = Guid.NewGuid().ToString("D");
            var transport = new SymbolReceiptTransport(instance, replacementEpoch);
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), instance);

            var result = await new SchematicSymbolLibraryTools(registry).CheckedReceipt(
                instance, replacementEpoch, SchematicJson.Formatter.Format(request.Document),
                request.OperationId, SchematicJson.Formatter.Format(request), default);

            Assert.IsTrue(result.IsError ?? false, JsonSerializer.Serialize(result.StructuredContent));
            var data = JsonSerializer.SerializeToElement(result.StructuredContent);
            Assert.AreEqual("operation_outcome_unknown", data.GetProperty("errorCode").GetString());
            Assert.AreEqual(replacementEpoch, data.GetProperty("processEpoch").GetString());
            Assert.AreEqual(request.ProcessEpoch, data.GetProperty("durableProcessEpoch").GetString());
            Assert.AreEqual("unknown", data.GetProperty("outcome").GetString());
            Assert.AreEqual("unknown-native-outcome", data.GetProperty("retrySafety").GetString());
            Assert.IsFalse(data.GetProperty("retryDispatched").GetBoolean());
            Assert.AreEqual(0, transport.NativeCalls,
                "An unknown durable outcome must remain receipt-only after restart.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DurableTerminalReplayKeepsTheImmutableOriginalReceipt()
    {
        string root = Directory.CreateTempSubdirectory("symbol-checked-replay-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = ReceiptRequest();
            var response = new NativeCapabilitySymbolLibraryCheckedWriteResponse
            {
                Document = request.Document.Clone(), ProcessEpoch = request.ProcessEpoch,
                OperationId = request.OperationId, Status = "unavailable",
                ErrorCode = "library_mutation_unavailable", ErrorMessage = "provider unavailable",
                ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsUnavailable,
                Scope = "mcp_direct_symbol_file", ExpectedRequestVerified = true,
                Request = request.Clone(),
                RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(request)
            };
            var first = new SymbolLibraryCheckedReceiptStore(root);
            _ = first.Reserve(instance, request);
            first.WriteTerminal(instance, request, response);

            var reopened = new SymbolLibraryCheckedReceiptStore(root);
            var replay = response.Clone();
            replay.Replayed = true;
            reopened.WriteTerminal(instance, request, replay);

            var stored = reopened.Read(instance, request.OperationId);
            Assert.IsNotNull(stored);
            Assert.IsFalse(SymbolLibraryCheckedReceiptStore.ParseResponse(stored!.ResponseJson).Replayed);
            Assert.AreEqual(SymbolLibraryCheckedReceiptRecord.Terminal, stored.Outcome);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PreDispatchAdmissionCanBeRemovedOnlyForTheExactRequest()
    {
        string root = Directory.CreateTempSubdirectory("symbol-checked-remove-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            var request = ReceiptRequest();
            var store = new SymbolLibraryCheckedReceiptStore(root);
            _ = store.Reserve(instance, request);
            store.RemoveAdmission(instance, request);
            Assert.IsTrue(new SymbolLibraryCheckedReceiptStore(root).Reserve(instance, request).Created);
        }
        finally { Directory.Delete(root, true); }
    }

    private static NativeCapabilitySymbolLibraryCheckedWrite ReceiptRequest()
    {
        (DocumentSpecifier document, _, _, _) = Fixture();
        string epoch = Guid.NewGuid().ToString("D");
        string operation = Guid.NewGuid().ToString("D");
        var state = State(document, epoch, sequence: 1, new string('a', 64));
        return new()
        {
            Document = document, ProcessEpoch = epoch, OperationId = operation,
            ExpectedSnapshotSha256 = state.StateSha256, ExpectedState = state,
            Definition = new() { Id = document.LibId.Clone() }, Overwrite = true
        };
    }

    private static NativeCapabilitySymbolLibraryCheckedWriteResponse Response(
        DocumentSpecifier document, string epoch, string operation, string status,
        bool committed = false, bool replayed = false, bool rollbackCompleted = false,
        bool revisionTracked = false, bool completeLifecycle = false,
        string? errorCode = null, string? errorMessage = null,
        string? before = null, string? after = null) => new()
        {
            Document = document.Clone(), ProcessEpoch = epoch, OperationId = operation,
            Status = status, Committed = committed, Replayed = replayed,
            RollbackCompleted = rollbackCompleted, RevisionTracked = revisionTracked,
            CompleteLifecycle = completeLifecycle, ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
            BeforeSnapshotSha256 = before ?? string.Empty, AfterSnapshotSha256 = after ?? string.Empty
        };

    private static NativeCapabilitySymbolLibraryCheckedWriteResponse TerminalResponse(
        NativeCapabilitySymbolLibraryCheckedWrite request)
    {
        var response = Response(request.Document, request.ProcessEpoch, request.OperationId,
            status: "unavailable", errorCode: "library_mutation_unavailable",
            errorMessage: "The native lifecycle owner is not available.");
        response.ReceiptStatus = NativeCapabilitySymbolLibraryCheckedReceiptStatus.NcpSlcwsUnavailable;
        response.Scope = "mcp_direct_symbol_file";
        response.ExpectedRequestVerified = true;
        response.Request = request.Clone();
        response.RequestFingerprintSha256 = SymbolLibraryCheckedContract.ComputeFingerprint(request);
        return response;
    }

    private static (DocumentSpecifier Document, string Epoch, string Operation, string Expected) Fixture()
    {
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeSymbol,
            Project = new() { Name = "fixture", Path = "/tmp/kicad-symbol-checked" },
            LibId = new() { LibraryNickname = "Fixture", EntryName = "Probe" }
        };
        return (document, "epoch", "operation", new string('a', 64));
    }

    private static NativeCapabilitySymbolLibraryCheckedState State(DocumentSpecifier document,
        string epoch, ulong sequence, string stateSha)
    {
        var state = new NativeCapabilitySymbolLibraryCheckedState
        {
            Document = document.Clone(), ProcessEpoch = epoch,
            Revision = new() { Epoch = epoch, Sequence = sequence }, Scope = "mcp_direct_symbol_file",
            TablePath = "/tmp/sym-lib-table", TableScope = "PROJECT",
            RawUri = "${KIPRJMOD}/fixture.kicad_sym", ExpandedUri = "/tmp/fixture.kicad_sym",
            Provider = "KiCad",
            StateComplete = true, DiskBaselineChecked = true, CompleteChangeTracking = false,
            DependencyBaselines = { new NativeCapabilityLibrarySnapshotDependencyBaseline
            {
                Path = "/tmp/sym-lib-table", Exists = true, Known = true,
                Sha256 = stateSha, Bytes = 11
            }, new NativeCapabilityLibrarySnapshotDependencyBaseline
            {
                Path = "/tmp/fixture.kicad_sym", Exists = true, Known = true,
                Sha256 = stateSha, Bytes = 7
            } }
        };
        state.StateSha256 = SymbolLibraryCheckedContract.ComputeStateDigest(state);
        return state;
    }

    private sealed class SymbolReceiptTransport(string instanceId, string epoch) : INativeTransport
    {
        internal int NativeCalls { get; private set; }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var api = ApiRequest.Parser.ParseFrom(request);
            IMessage payload;
            if (api.Message.Is(GetAutomationSession.Descriptor))
            {
                payload = new AutomationSession
                {
                    ProtocolVersion = 1,
                    InstanceId = instanceId,
                    Epoch = epoch,
                    ProjectPath = "/tmp/symbol-receipt-fixture.kicad_pro"
                };
            }
            else
            {
                NativeCalls++;
                payload = new GetVersionResponse
                {
                    Version = new KiCadVersion { FullVersion = "symbol-receipt-fixture" }
                };
            }

            return Task.FromResult(new ApiResponse
            {
                Header = new ApiResponseHeader { KicadToken = epoch },
                Status = new ApiResponseStatus { Status = (ApiStatusCode)1 },
                Message = Any.Pack(payload)
            }.ToByteArray());
        }
    }
}
