using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class CheckedDesignRecoveryTests
{
    private static DesignRecoveryState Fixture(string directory, string processEpoch, string instanceId)
    {
        var state = DesignRecoveryStoreTests.Fixture() with
            { NativeRevision = new(Guid.NewGuid().ToString("D"), 42), InstanceId = Guid.Parse(instanceId) };
        var design = state.Baseline with { Schematic = state.Baseline.Schematic.Clone() };
        design.Schematic.Document.Project.Path = directory;
        foreach (var screen in design.Schematic.Instances) screen.Metadata.Document.Project.Path = directory;
        state = state with { Baseline = design, Observed = design.Schematic.Clone(),
            DesiredFileBytes = System.Text.Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries)) };
        var batch = DesignRecoveryStoreTests.Mutation(state);
        batch.OperationId = Guid.NewGuid().ToString("D");
        var native = CheckedSchematicToolTests.Request(directory, processEpoch).ExpectedState;
        native.Document = batch.Document.Clone(); native.Revision = batch.ExpectedRevision.Clone();
        return state with { PendingMutation = batch, PendingNativeState = native };
    }

    [TestMethod]
    public void CheckedPendingStateRoundTripsAndLegacyRecordsDoNotAcquireNewFields()
    {
        string directory = Directory.CreateTempSubdirectory("checked-recovery-").FullName;
        try
        {
            string path = Path.Combine(directory, "recovery.json"); var store = new DesignRecoveryStore(path);
            var legacy = DesignRecoveryStoreTests.Fixture(); var before = store.Save(legacy, null);
            byte[] original = File.ReadAllBytes(path);
            Assert.IsFalse(JsonNode.Parse(original)!.AsObject().ContainsKey("PendingNativeState"));
            Assert.AreEqual(before.RevisionToken, store.Save(store.Read()!.State, before.RevisionToken).RevisionToken);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            var state = Fixture(directory, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
            var saved = store.Save(state, before.RevisionToken);
            Assert.AreEqual(4, JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>());
            var restored = new DesignRecoveryStore(path).Read()!;
            Assert.AreEqual(state.PendingMutation, restored.State.PendingMutation);
            Assert.AreEqual(state.PendingNativeState, restored.State.PendingNativeState);
            Assert.AreEqual(saved.RevisionToken, restored.RevisionToken);
            state.PendingNativeState!.StateSha256 = new string('f', 64);
            Assert.AreNotEqual(state.PendingNativeState, restored.State.PendingNativeState);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void MismatchedOrOrphanedPreconditionsCannotReplaceTheRecoveryRecord()
    {
        string directory = Directory.CreateTempSubdirectory("checked-recovery-invalid-").FullName;
        try
        {
            string path = Path.Combine(directory, "recovery.json"); var store = new DesignRecoveryStore(path);
            var state = Fixture(directory, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
            var saved = store.Save(state, null); byte[] original = File.ReadAllBytes(path);
            var wrong = state.PendingNativeState!.Clone(); wrong.Revision.Sequence++;
            foreach (var invalid in new[] { state with { PendingMutation = null }, state with { PendingNativeState = wrong } })
            {
                Assert.ThrowsExactly<AutomationException>(() => store.Save(invalid, saved.RevisionToken));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            }
            var missing = JsonNode.Parse(original)!; missing.AsObject().Remove("PendingNativeState");
            File.WriteAllText(path, missing.ToJsonString());
            Assert.ThrowsExactly<AutomationException>(() => store.Read());
            Assert.AreEqual(missing.ToJsonString(), File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void ElectricalRecoveryWithoutCheckedMutationRemainsVersionThreeAndByteStable()
    {
        string directory = Directory.CreateTempSubdirectory("checked-recovery-v3-").FullName;
        try
        {
            string path = Path.Combine(directory, "recovery.json"); var store = new DesignRecoveryStore(path);
            var state = SchematicNetReconciliationTests.Fixture(); var saved = store.Save(state, null);
            byte[] original = File.ReadAllBytes(path); var json = JsonNode.Parse(original)!;
            Assert.AreEqual(3, json["Version"]!.GetValue<int>());
            Assert.IsFalse(json.AsObject().ContainsKey("PendingNativeState"));
            var reopened = new DesignRecoveryStore(path).Read()!;
            Assert.AreEqual(saved.RevisionToken, store.Save(reopened.State, saved.RevisionToken).RevisionToken);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            Assert.AreEqual(state.BaselineElectrical, reopened.State.BaselineElectrical);
            Assert.AreEqual(state.ObservedElectrical, reopened.State.ObservedElectrical);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task InspectionUsesOnlyTheCheckedReceiptAndNeverClearsThePendingOperation()
    {
        string directory = Directory.CreateTempSubdirectory("checked-recovery-inspect-").FullName;
        try
        {
            var transport = new ReceiptTransport();
            var state = Fixture(directory, transport.Session.Epoch, transport.Session.InstanceId);
            var store = new DesignRecoveryStore(Path.Combine(directory, "recovery.json")); var saved = store.Save(state, null);
            var client = new NativeClient(transport, NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.Epoch);
            foreach (var status in new[] { CheckedSchematicBatchStatus.CsbsNotFound, CheckedSchematicBatchStatus.CsbsCompleted,
                         CheckedSchematicBatchStatus.CsbsRejected, CheckedSchematicBatchStatus.CsbsIndeterminate })
            {
                transport.Status = status;
                var observed = await DesignRecoveryInspector.InspectAsync(store, client);
                Assert.AreEqual(status switch
                {
                    CheckedSchematicBatchStatus.CsbsNotFound => DesignRecoveryDisposition.NotFound,
                    CheckedSchematicBatchStatus.CsbsCompleted => DesignRecoveryDisposition.CompletedNeedsReconciliation,
                    CheckedSchematicBatchStatus.CsbsRejected => DesignRecoveryDisposition.Rejected,
                    _ => DesignRecoveryDisposition.Indeterminate
                }, observed.Disposition);
                Assert.IsNull(observed.Receipt); Assert.IsNotNull(observed.CheckedReceipt);
                Assert.AreEqual(state.PendingMutation, transport.Last!.ExpectedRequest.Batch);
                var expectedGuard = state.PendingNativeState;
                Assert.AreEqual(expectedGuard, transport.Last.ExpectedRequest.ExpectedState);
                Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
            }
            Assert.AreEqual(4, transport.Inspections);
            transport.WrongReceipt = true;
            await Assert.ThrowsAsync<AutomationException>(() => DesignRecoveryInspector.InspectAsync(store, client));
            transport.WrongReceipt = false;
            transport.BeforeReply = () => store.Save(saved.State with { DesiredFileBytes = [0xff] }, saved.RevisionToken);
            await Assert.ThrowsAsync<AutomationException>(() => DesignRecoveryInspector.InspectAsync(store, client));
            CollectionAssert.AreEqual(new byte[] { 0xff }, store.Read()!.State.DesiredFileBytes);
            Assert.IsNotNull(store.Read()!.State.PendingNativeState);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => DesignRecoveryInspector.InspectAsync(store, client, cancellation.Token));
            Assert.AreEqual(0, transport.Mutations); Assert.AreEqual(0, transport.LegacyInspections);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ReceiptTransport : INativeTransport
    {
        internal NativeClientTests.FixtureTransport Session { get; } = new() { Epoch = Guid.NewGuid().ToString("D") };
        internal CheckedSchematicBatchStatus Status { get; set; }
        internal bool WrongReceipt { get; set; }
        internal Action? BeforeReply { get; set; }
        internal int Inspections { get; private set; }
        internal int LegacyInspections { get; private set; }
        internal int Mutations { get; private set; }
        internal ReadCheckedSchematicBatchReceipt? Last { get; private set; }
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] bytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var envelope = ApiRequest.Parser.ParseFrom(bytes);
            if (envelope.Message.Is(ApplySchematicItemBatch.Descriptor) || envelope.Message.Is(CheckedSchematicBatch.Descriptor)) Mutations++;
            if (envelope.Message.Is(InspectSchematicOperation.Descriptor)) LegacyInspections++;
            if (!envelope.Message.Is(ReadCheckedSchematicBatchReceipt.Descriptor)) return Session.ExchangeAsync(endpoint, bytes, timeout, cancellationToken);
            Inspections++; Last = envelope.Message.Unpack<ReadCheckedSchematicBatchReceipt>();
            var result = new CheckedSchematicBatchReceipt { Document = Last.Document.Clone(), ProcessEpoch = Last.ProcessEpoch,
                OperationId = Last.OperationId, Status = Status, ExpectedRequestVerified = Status != CheckedSchematicBatchStatus.CsbsNotFound };
            if (Status == CheckedSchematicBatchStatus.CsbsCompleted)
            {
                result.ObservedBefore = Last.ExpectedRequest.ExpectedState.Clone(); result.ObservedAfter = result.ObservedBefore.Clone();
                result.ObservedAfter.Revision.Sequence++; result.ObservedAfter.StateSha256 = new string('d', 64);
                result.Result = new() { Revision = result.ObservedAfter.Revision.Clone() };
            }
            else if (Status != CheckedSchematicBatchStatus.CsbsNotFound) result.ErrorCode = "fixture_failure";
            if (WrongReceipt) result.OperationId = Guid.NewGuid().ToString("D");
            BeforeReply?.Invoke();
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(result) }.ToByteArray());
        }
    }
}
