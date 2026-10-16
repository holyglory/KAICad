using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class PcbCheckedItemMigrationTests
{
    [TestMethod]
    public void RouteOptimizationReceiptSurvivesStoreReopen()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-store-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string fingerprint = new string('a', 64);
            using JsonDocument payload = JsonDocument.Parse($"{{\"instanceId\":\"{instance}\",\"processEpoch\":\"{epoch}\",\"operationId\":\"{operation}\",\"outcome\":\"completed\",\"replayed\":false,\"receipt\":{{\"replayed\":false}}}}");

            new RouteOptimizationReceiptStore(root).Write(instance, epoch, operation, fingerprint,
                payload.RootElement);

            RouteOptimizationReceiptRecord? reopened = new RouteOptimizationReceiptStore(root)
                .Read(instance, operation);
            Assert.IsNotNull(reopened);
            Assert.AreEqual(instance, reopened!.InstanceId);
            Assert.AreEqual(epoch, reopened.ProcessEpoch);
            Assert.AreEqual(operation, reopened.OperationId);
            Assert.AreEqual(fingerprint, reopened.Fingerprint);
            Assert.AreEqual(payload.RootElement.GetRawText(), reopened.ResultJson);
            Assert.IsFalse(reopened.ReplayObserved);

            CallToolResult restored = RouteOptimizationReceiptStore.Result(reopened);
            Assert.AreEqual(reopened.ResultJson, restored.StructuredContent!.Value.GetRawText());

            RouteOptimizationReceiptRecord replayedRecord = new RouteOptimizationReceiptStore(root)
                .MarkReplayed(reopened);
            Assert.IsTrue(replayedRecord.ReplayObserved);
            Assert.AreEqual(reopened.ResultJson, replayedRecord.ResultJson,
                "The immutable terminal result must remain unchanged when replay is observed.");
            JsonElement replay = RouteOptimizationReceiptStore.ReplayResult(replayedRecord)
                .StructuredContent!.Value;
            Assert.IsTrue(replay.GetProperty("replayed").GetBoolean());
            Assert.IsTrue(replay.GetProperty("receipt").GetProperty("replayed").GetBoolean());
            Assert.AreEqual(reopened.ResultJson,
                RouteOptimizationReceiptStore.Result(replayedRecord).StructuredContent!.Value.GetRawText(),
                "A durable read still exposes the original terminal payload.");
            Assert.AreEqual(1, Directory.EnumerateFiles(
                Path.Combine(root, "pcb-route-receipts"), "*.json").Count());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RouteOptimizationReceiptRejectsPayloadIdentityConflict()
    {
        string instance = Guid.NewGuid().ToString("D");
        string epoch = Guid.NewGuid().ToString("D");
        string operation = Guid.NewGuid().ToString("D");
        string fingerprint = new string('b', 64);
        using JsonDocument payload = JsonDocument.Parse($"{{\"instanceId\":\"{instance}\",\"processEpoch\":\"{epoch}\",\"operationId\":\"{Guid.NewGuid():D}\"}}");

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            new RouteOptimizationReceiptRecord(instance, epoch, operation, fingerprint,
                payload.RootElement.GetRawText()).Validate());

        Assert.AreEqual("invalid_route_receipt", error.Code);
    }

    [TestMethod]
    public void RouteOptimizationUnknownOutcomeSurvivesStoreReopenWithoutRetryPermission()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-unknown-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            string fingerprint = new string('c', 64);
            var store = new RouteOptimizationReceiptStore(root);
            store.WriteUnknown(instance, epoch, operation, fingerprint,
                "operation_outcome_unknown", "KiCad may have committed the route before transport loss.");

            RouteOptimizationReceiptRecord? reopened = new RouteOptimizationReceiptStore(root)
                .Read(instance, operation);
            Assert.IsNotNull(reopened);
            JsonElement payload = JsonDocument.Parse(reopened!.ResultJson).RootElement;
            Assert.AreEqual("unknown", payload.GetProperty("outcome").GetString());
            Assert.AreEqual("unknown-native-outcome", payload.GetProperty("retrySafety").GetString());
            Assert.AreEqual("operation_outcome_unknown", payload.GetProperty("errorCode").GetString());
            Assert.IsFalse(payload.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.IsFalse(payload.GetProperty("replayed").GetBoolean());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RouteOptimizationReceiptLeaseIsExclusiveAcrossStoreInstances()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-lock-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            using FileStream held = new RouteOptimizationReceiptStore(root).Acquire(instance, operation);

            AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            {
                using FileStream _ = new RouteOptimizationReceiptStore(root).Acquire(instance, operation);
            });

            Assert.AreEqual("route_receipt_busy", error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CornerApplyReceiptReplaysFromSharedMcpStateAfterRegistryRestart()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-restart-").FullName;
        try
        {
            var transport = new MigrationTransport();
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")),
                transport.InstanceId);
            var expected = State(root, transport.Epoch, 5, 'a');
            transport.LiveState = expected.Clone();
            var source = Track(Guid.NewGuid().ToString("D"), 1_000, 2_000);
            source.Net = new() { Name = "N1" };
            transport.ExistingItems.Items.Add(Any.Pack(source));
            var replacement = source.Clone();
            replacement.Id.Value = Guid.NewGuid().ToString("D");
            replacement.End.XNm++;
            var delete = new DeleteItems { Header = new() { Document = expected.Document }, ItemIds = { source.Id } };
            var create = new CreateItems { Header = new() { Document = expected.Document }, Items = { Any.Pack(replacement) } };
            string stateJson = SchematicJson.Formatter.Format(expected);
            string deleteJson = BoardJson.Formatter.Format(delete);
            string createJson = BoardJson.Formatter.Format(create);
            string operationId = Guid.NewGuid().ToString("D");

            JsonElement first = Success(await new PcbRouteOptimizationTools(registry).Apply(
                transport.InstanceId, deleteJson, createJson, stateJson, operationId, default));
            int routeMutations = transport.RouteMutations;
            int exchanges = transport.Exchanges;

            // A new MCP registry has no native attachment. A successful replay
            // therefore proves that the shared durable state is sufficient.
            var restarted = new InstanceRegistry(new OfflineTransport(), root);
            var replayTools = new PcbRouteOptimizationTools(restarted);
            JsonElement replay = Success(await replayTools.Apply(
                transport.InstanceId, deleteJson, createJson, stateJson, operationId, default));
            Assert.IsTrue(replay.GetProperty("replayed").GetBoolean());
            Assert.IsTrue(replay.GetProperty("receipt").GetProperty("replayed").GetBoolean());
            Assert.IsFalse(first.GetProperty("replayed").GetBoolean());
            Assert.AreEqual(routeMutations, transport.RouteMutations);
            Assert.AreEqual(exchanges, transport.Exchanges);

            JsonElement receipt = Success(await replayTools.Receipt(
                transport.InstanceId, transport.Epoch, operationId, default));
            Assert.IsTrue(receipt.GetProperty("found").GetBoolean());
            Assert.AreEqual("mcp-state", receipt.GetProperty("receiptScope").GetString());
            Assert.AreEqual(replay.GetRawText(), receipt.GetProperty("receipt").GetRawText());

            // A native process restart changes the epoch.  The durable MCP
            // receipt remains readable, but must fail closed rather than
            // presenting an old process-owned outcome as current.
            AssertError(await replayTools.Receipt(
                transport.InstanceId, Guid.NewGuid().ToString("D"), operationId, default),
                "stale_process_epoch");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CornerApplyPersistsUnknownTransportOutcomeAndBlocksRedispatch()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-unknown-apply-").FullName;
        try
        {
            var transport = new MigrationTransport { RouteFailure = new NngException(5, "lost reply", requestDelivered: true) };
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")),
                transport.InstanceId);
            var expected = State(root, transport.Epoch, 7, 'a');
            transport.LiveState = expected.Clone();
            var source = Track(Guid.NewGuid().ToString("D"), 1_000, 2_000);
            source.Net = new() { Name = "N1" };
            transport.ExistingItems.Items.Add(Any.Pack(source));
            var replacement = source.Clone();
            replacement.Id.Value = Guid.NewGuid().ToString("D");
            replacement.End.XNm++;
            var delete = new DeleteItems { Header = new() { Document = expected.Document }, ItemIds = { source.Id } };
            var create = new CreateItems { Header = new() { Document = expected.Document }, Items = { Any.Pack(replacement) } };
            string operationId = Guid.NewGuid().ToString("D");

            CallToolResult first = await new PcbRouteOptimizationTools(registry).Apply(
                transport.InstanceId, BoardJson.Formatter.Format(delete), BoardJson.Formatter.Format(create),
                SchematicJson.Formatter.Format(expected), operationId, default);
            Assert.IsTrue(first.IsError ?? false);
            Assert.AreEqual("operation_outcome_unknown", first.StructuredContent!.Value.GetProperty("errorCode").GetString());
            int exchangesAfterLoss = transport.Exchanges;

            JsonElement receipt = (await new PcbRouteOptimizationTools(registry).Receipt(
                transport.InstanceId, transport.Epoch, operationId, default)).StructuredContent!.Value;
            Assert.IsTrue(receipt.GetProperty("found").GetBoolean());
            Assert.AreEqual("unknown", receipt.GetProperty("outcome").GetString());
            Assert.AreEqual("unknown-native-outcome", receipt.GetProperty("retrySafety").GetString());

            CallToolResult retry = await new PcbRouteOptimizationTools(registry).Apply(
                transport.InstanceId, BoardJson.Formatter.Format(delete), BoardJson.Formatter.Format(create),
                SchematicJson.Formatter.Format(expected), operationId, default);
            Assert.IsTrue(retry.IsError ?? false);
            Assert.AreEqual("operation_outcome_unknown", retry.StructuredContent!.Value.GetProperty("errorCode").GetString());
            Assert.AreEqual(exchangesAfterLoss, transport.Exchanges,
                "An unknown native outcome must be reconciled before another native dispatch.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RouteReceiptRecoversTerminalResultAfterNativeReplacementWithoutReceiptLookup()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-native-restart-terminal-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string originalEpoch = Guid.NewGuid().ToString("D");
            string replacementEpoch = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            RouteOptimizationReceiptRequest request = RecoveryRequest(root, instance, originalEpoch, operation);
            using JsonDocument payload = JsonDocument.Parse($"{{\"instanceId\":\"{instance}\",\"processEpoch\":\"{originalEpoch}\",\"operationId\":\"{operation}\",\"outcome\":\"completed\",\"nativeReceiptAvailable\":true,\"replayed\":false,\"receipt\":{{\"replayed\":false}}}}");
            new RouteOptimizationReceiptStore(root).Write(instance, originalEpoch, operation,
                request.FingerprintSha256, payload.RootElement, request.Serialize());

            var replacement = new RouteReplacementTransport(instance, replacementEpoch);
            var registry = new InstanceRegistry(replacement, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), instance);

            CallToolResult result = await new PcbRouteOptimizationTools(registry).Receipt(
                instance, replacementEpoch, operation, default, request.Serialize());

            JsonElement data = Success(result);
            Assert.AreEqual(replacementEpoch, data.GetProperty("processEpoch").GetString());
            Assert.AreEqual(originalEpoch, data.GetProperty("durableProcessEpoch").GetString());
            Assert.IsTrue(data.GetProperty("durableRecovery").GetBoolean());
            Assert.IsFalse(data.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.IsFalse(data.GetProperty("retryDispatched").GetBoolean());
            Assert.AreEqual("terminal-receipt", data.GetProperty("retrySafety").GetString());
            Assert.AreEqual(2, replacement.Handshakes,
                "Attachment and recovery each verify the replacement process identity.");
            Assert.AreEqual(0, replacement.UnexpectedCalls,
                "Durable terminal recovery must not query or mutate the replacement native process.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RouteReceiptRecoversUnknownAndAdmittedAfterNativeReplacementWithoutRetry()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-native-restart-unknown-").FullName;
        try
        {
            string instance = Guid.NewGuid().ToString("D");
            string originalEpoch = Guid.NewGuid().ToString("D");
            string replacementEpoch = Guid.NewGuid().ToString("D");
            string operation = Guid.NewGuid().ToString("D");
            RouteOptimizationReceiptRequest request = RecoveryRequest(root, instance, originalEpoch, operation);
            var store = new RouteOptimizationReceiptStore(root);
            store.WriteUnknown(instance, originalEpoch, operation, request.FingerprintSha256,
                "operation_outcome_unknown", "The native route result was lost.", request.Serialize());

            var replacement = new RouteReplacementTransport(instance, replacementEpoch);
            var registry = new InstanceRegistry(replacement, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), instance);
            CallToolResult unknown = await new PcbRouteOptimizationTools(registry).Receipt(
                instance, replacementEpoch, operation, default, request.Serialize());

            Assert.IsTrue(unknown.IsError ?? false, Text(unknown));
            JsonElement unknownData = unknown.StructuredContent!.Value;
            Assert.AreEqual("operation_outcome_unknown", unknownData.GetProperty("errorCode").GetString());
            Assert.AreEqual(replacementEpoch, unknownData.GetProperty("processEpoch").GetString());
            Assert.AreEqual(originalEpoch, unknownData.GetProperty("durableProcessEpoch").GetString());
            Assert.IsFalse(unknownData.GetProperty("retryDispatched").GetBoolean());
            Assert.IsFalse(unknownData.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.AreEqual(0, replacement.UnexpectedCalls);

            string admittedOperation = Guid.NewGuid().ToString("D");
            RouteOptimizationReceiptRequest admittedRequest = RecoveryRequest(root, instance, originalEpoch, admittedOperation);
            store.WriteAdmitted(instance, originalEpoch, admittedOperation,
                admittedRequest.FingerprintSha256, admittedRequest.Serialize());
            CallToolResult admitted = await new PcbRouteOptimizationTools(registry).Receipt(
                instance, replacementEpoch, admittedOperation, default, admittedRequest.Serialize());

            Assert.IsTrue(admitted.IsError ?? false, Text(admitted));
            JsonElement admittedData = admitted.StructuredContent!.Value;
            Assert.AreEqual("operation_outcome_unknown", admittedData.GetProperty("errorCode").GetString());
            Assert.AreEqual("admitted", admittedData.GetProperty("outcome").GetString());
            Assert.IsFalse(admittedData.GetProperty("retryDispatched").GetBoolean());
            Assert.AreEqual(3, replacement.Handshakes,
                "Attachment and each recovery read verify the replacement process identity; neither outcome may perform a native receipt lookup.");
            Assert.AreEqual(0, replacement.UnexpectedCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    private static RouteOptimizationReceiptRequest RecoveryRequest(
        string root, string instance, string processEpoch, string operationId)
    {
        DocumentLifecycleState state = State(root, processEpoch, 9, 'a');
        var delete = new DeleteItems { Header = new() { Document = state.Document } };
        delete.ItemIds.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
        var track = Track(Guid.NewGuid().ToString("D"), 1_000, 2_000);
        track.Net = new() { Name = "N1" };
        var create = new CreateItems
        {
            Header = new() { Document = state.Document },
            Items = { Any.Pack(track) }
        };
        return new RouteOptimizationReceiptRequest(instance, processEpoch, operationId,
            BoardJson.Formatter.Format(delete), BoardJson.Formatter.Format(create),
            SchematicJson.Formatter.Format(state), "");
    }

    private sealed class RouteReplacementTransport(string instanceId, string epoch) : INativeTransport
    {
        internal int Handshakes { get; private set; }
        internal int UnexpectedCalls { get; private set; }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] bytes, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var request = ApiRequest.Parser.ParseFrom(bytes);
            if (request.Message.Is(GetAutomationSession.Descriptor))
            {
                Handshakes++;
                return Task.FromResult(new ApiResponse
                {
                    Header = new() { KicadToken = epoch },
                    Status = new() { Status = (ApiStatusCode)1 },
                    Message = Any.Pack(new AutomationSession
                    {
                        ProtocolVersion = 1, InstanceId = instanceId,
                        ProjectPath = "/fixture/route.kicad_pro", Epoch = epoch
                    })
                }.ToByteArray());
            }

            UnexpectedCalls++;
            throw new InvalidOperationException("The replacement native process must not receive route lookup or mutation during durable recovery.");
        }
    }

    private sealed class OfflineTransport : INativeTransport
    {
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A durable route receipt replay must not contact native.");
    }
}
