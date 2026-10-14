using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
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
            using JsonDocument payload = JsonDocument.Parse($"{{\"instanceId\":\"{instance}\",\"processEpoch\":\"{epoch}\",\"operationId\":\"{operation}\",\"outcome\":\"completed\",\"replayed\":false}}");

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

            CallToolResult restored = RouteOptimizationReceiptStore.Result(reopened);
            Assert.AreEqual(reopened.ResultJson, restored.StructuredContent!.Value.GetRawText());
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
            Assert.AreEqual(first.GetRawText(), replay.GetRawText());
            Assert.AreEqual(routeMutations, transport.RouteMutations);
            Assert.AreEqual(exchanges, transport.Exchanges);

            JsonElement receipt = Success(await replayTools.Receipt(
                transport.InstanceId, transport.Epoch, operationId, default));
            Assert.IsTrue(receipt.GetProperty("found").GetBoolean());
            Assert.AreEqual("mcp-state", receipt.GetProperty("receiptScope").GetString());
            Assert.AreEqual(first.GetRawText(), receipt.GetProperty("receipt").GetRawText());
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class OfflineTransport : INativeTransport
    {
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A durable route receipt replay must not contact native.");
    }
}
