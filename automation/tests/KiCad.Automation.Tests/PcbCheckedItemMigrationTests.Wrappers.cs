using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class PcbCheckedItemMigrationTests
{
    [TestMethod]
    public async Task FootprintPlacementPlanApplyReceiptAndRetryPreservePublicIdentity()
    {
        string root = Directory.CreateTempSubdirectory("pcb-placement-replay-").FullName;
        try
        {
            var transport = new MigrationTransport();
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            var tools = new PcbFootprintPlacementTools(registry);
            var expected = State(root, transport.Epoch, 5, 'a');
            string stateJson = SchematicJson.Formatter.Format(expected);
            var footprint = Footprint();
            var plan = await tools.Plan(SchematicJson.Formatter.Format(expected.Document), stateJson,
                BoardJson.Formatter.Format(footprint), default);
            JsonElement planData = Success(plan);
            string candidate = planData.GetProperty("candidateRequestJson").GetString()!;
            string operationId = Guid.NewGuid().ToString("D");

            JsonElement applied = Success(await tools.Apply(transport.InstanceId, candidate, stateJson, operationId, default));
            Assert.AreEqual(transport.InstanceId, applied.GetProperty("instanceId").GetString());
            Assert.AreEqual(footprint.Id, BoardJson.Parser.Parse<CreateItemsResponse>(
                applied.GetProperty("response").GetRawText()).CreatedItems.Single().Item.Unpack<FootprintInstance>().Id);
            await AssertReceiptIdentity((instance, epoch) => tools.Receipt(instance, epoch, operationId, default),
                transport, operationId);

            transport.LiveBoardChanged = true;
            JsonElement replay = Success(await tools.Apply(transport.InstanceId, candidate, stateJson, operationId, default));
            Assert.IsTrue(replay.GetProperty("replayed").GetBoolean());
            Assert.AreEqual(2, transport.CheckedMutations);
            Assert.AreEqual(1, transport.AppliedItemTransactions);
            Assert.AreEqual(0, transport.LifecycleReads);
            Assert.AreEqual(0, transport.LegacyMutations);

            var changed = BoardJson.Parser.Parse<CreateItems>(candidate);
            var moved = changed.Items[0].Unpack<FootprintInstance>(); moved.Position.XNm++;
            changed.Items[0] = Any.Pack(moved);
            AssertError(await tools.Apply(transport.InstanceId, BoardJson.Formatter.Format(changed),
                stateJson, operationId, default), "operation_id_reused");
            Assert.AreEqual(2, transport.CheckedMutations);
            var foreignState = expected.Clone(); foreignState.ProcessEpoch = Guid.NewGuid().ToString("D");
            AssertError(await tools.Apply(transport.InstanceId, candidate, SchematicJson.Formatter.Format(foreignState),
                Guid.NewGuid().ToString("D"), default), "pcb_instance_changed");
            transport.LiveBoardChanged = false;
            var nativeItems = await ReadNativeItems(registry, transport.InstanceId, expected.Document, [footprint.Id]);
            Assert.AreEqual(footprint, nativeItems.Single().Unpack<FootprintInstance>());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CornerApplyReceiptAndReplayAvoidChangedBoardPreflight()
    {
        string root = Directory.CreateTempSubdirectory("pcb-corner-replay-").FullName;
        try
        {
            var transport = new MigrationTransport();
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            var expected = State(root, transport.Epoch, 5, 'a');
            transport.LiveState = expected.Clone();
            var source = Track(Guid.NewGuid().ToString("D"), 1_000, 2_000); source.Net = new() { Name = "N1" };
            transport.ExistingItems.Items.Add(Any.Pack(source));
            var replacement = source.Clone(); replacement.Id.Value = Guid.NewGuid().ToString("D"); replacement.End.XNm++;
            var delete = new DeleteItems { Header = new() { Document = expected.Document }, ItemIds = { source.Id } };
            var create = new CreateItems { Header = new() { Document = expected.Document }, Items = { Any.Pack(replacement) } };
            var tools = new PcbRouteOptimizationTools(registry);
            string operationId = Guid.NewGuid().ToString("D");
            string stateJson = SchematicJson.Formatter.Format(expected);

            JsonElement applied = Success(await tools.Apply(transport.InstanceId, BoardJson.Formatter.Format(delete),
                BoardJson.Formatter.Format(create), stateJson, operationId, default));
            Assert.AreEqual(transport.InstanceId, applied.GetProperty("instanceId").GetString());
            Assert.IsTrue(applied.GetProperty("mutationConfirmed").GetBoolean());
            Assert.IsFalse(applied.GetProperty("drcValidated").GetBoolean());
            Assert.AreEqual(source.Id, BoardJson.Parser.Parse<DeleteItemsResponse>(applied.GetProperty("deleted").GetRawText()).DeletedItems.Single().Id);
            await AssertReceiptIdentity((instance, epoch) => tools.Receipt(instance, epoch, operationId, default),
                transport, operationId, "mcp-state");

            transport.LiveBoardChanged = true;
            JsonElement replay = Success(await tools.Apply(transport.InstanceId, BoardJson.Formatter.Format(delete),
                BoardJson.Formatter.Format(create), stateJson, operationId, default));
            // The durable MCP receipt returns the original successful result
            // without contacting the native controller a second time.
            Assert.AreEqual(applied.GetRawText(), replay.GetRawText());
            Assert.IsFalse(replay.GetProperty("replayed").GetBoolean());
            Assert.AreEqual(1, transport.CheckedMutations);
            Assert.AreEqual(1, transport.AppliedItemTransactions);
            Assert.AreEqual(2, transport.ReceiptReads);
            Assert.AreEqual(1, transport.LifecycleReads);
            Assert.AreEqual(1, transport.ItemReads);
            Assert.AreEqual(0, transport.LegacyMutations);
            var changed = create.Clone(); var moved = replacement.Clone(); moved.End.XNm++;
            changed.Items[0] = Any.Pack(moved);
            AssertError(await tools.Apply(transport.InstanceId, BoardJson.Formatter.Format(delete),
                BoardJson.Formatter.Format(changed), stateJson, operationId, default), "operation_id_reused");
            var foreignState = expected.Clone(); foreignState.ProcessEpoch = Guid.NewGuid().ToString("D");
            AssertError(await tools.Apply(transport.InstanceId, BoardJson.Formatter.Format(delete), BoardJson.Formatter.Format(create),
                SchematicJson.Formatter.Format(foreignState), Guid.NewGuid().ToString("D"), default), "pcb_instance_changed");
            transport.LiveBoardChanged = false;
            var nativeItems = await ReadNativeItems(registry, transport.InstanceId, expected.Document, [source.Id, replacement.Id]);
            Assert.AreEqual(replacement, nativeItems.Single().Unpack<Track>());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DrcReplacementApplyReceiptAndReplayUseFullJobJson(bool tuning)
    {
        string root = Directory.CreateTempSubdirectory("pcb-drc-replacement-").FullName;
        try
        {
            var transport = new MigrationTransport();
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            var expected = State(root, transport.Epoch, 5, 'a');
            var replacement = Track(Guid.NewGuid().ToString("D"), 1_000, 2_000); replacement.Net = new() { Name = "N1" };
            var create = new CreateItems { Header = new() { Document = expected.Document }, Items = { Any.Pack(replacement) } };
            var delete = new DeleteItems { Header = new() { Document = expected.Document }, ItemIds = { new KIID { Value = Guid.NewGuid().ToString("D") } } };
            transport.ExistingItems.Items.Add(Any.Pack(Track(delete.ItemIds[0].Value, 1_000, 2_000)));
            string createJson = BoardJson.Formatter.Format(create), deleteJson = BoardJson.Formatter.Format(delete);
            // The native peer owns the job. No MCP process-local DRC receipt
            // is populated, as after reconnecting to an existing native job.
            var start = new StartPcbDrcJob
            {
                Document = expected.Document.Clone(), ProcessEpoch = transport.Epoch,
                OperationId = Guid.NewGuid().ToString("D"), ExpectedRevision = expected.Revision.Clone(),
                CandidateItems = { create.Items }, DeleteItemIds = { delete.ItemIds }
            };
            var drc = await registry.Client(transport.InstanceId).InvokeAsync<StartPcbDrcJob, PcbDrcJobState>(start);
            Assert.IsFalse(PcbDrcTools.TryGetCandidateReceipt(drc.JobId, out _, out _, out _, out _, out _));
            string drcJson = SchematicJson.Formatter.Format(drc);
            var tools = new PcbRouteOptimizationTools(registry);
            string operationId = Guid.NewGuid().ToString("D"), stateJson = SchematicJson.Formatter.Format(expected);
            Task<CallToolResult> Apply() => tuning
                ? tools.TuneApply(transport.InstanceId, deleteJson, createJson, stateJson, operationId, default, drcJson)
                : tools.RerouteApply(transport.InstanceId, deleteJson, createJson, stateJson, operationId, default, drcJson);

            JsonElement applied = Success(await Apply());
            Assert.AreEqual(transport.InstanceId, applied.GetProperty("instanceId").GetString());
            Assert.IsTrue(applied.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.IsTrue(applied.GetProperty("drcValidated").GetBoolean());
            await AssertReceiptIdentity((instance, epoch) => tuning
                ? tools.TuneReceipt(instance, epoch, operationId, default)
                : tools.RerouteReceipt(instance, epoch, operationId, default), transport, operationId, "mcp-state");

            transport.LiveBoardChanged = true;
            registry = new InstanceRegistry(transport, Path.Combine(root, "reconnected"));
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            tools = new PcbRouteOptimizationTools(registry);
            // Caller status is not operation identity or admission proof.
            // Retry with only the same job locator and exact checked payload.
            drcJson = SchematicJson.Formatter.Format(new PcbDrcJobState { JobId = drc.JobId });
            Assert.IsFalse(PcbDrcTools.TryGetCandidateReceipt(drc.JobId, out _, out _, out _, out _, out _));
            JsonElement replay = Success(await Apply());
            Assert.IsTrue(replay.GetProperty("replayed").GetBoolean());
            Assert.IsTrue(SchematicJson.Parser.Parse<CheckedPcbRouteReceipt>(
                replay.GetProperty("receipt").GetRawText()).Replayed);
            Assert.AreEqual(2, transport.RouteMutations);
            Assert.AreEqual(1, transport.AppliedRouteTransactions);
            Assert.AreEqual(0, transport.LifecycleReads);
            Assert.AreEqual(0, transport.ItemReads);
            Assert.AreEqual(0, transport.LegacyMutations);
            transport.InvalidateDrc(drc.JobId);
            // Forged completed caller JSON cannot authorize a new edit when
            // the native job no longer proves fresh, finding-free geometry.
            drcJson = SchematicJson.Formatter.Format(drc);
            string originalOperation = operationId;
            operationId = Guid.NewGuid().ToString("D");
            AssertError(await Apply(), "native_drc_not_clean");
            Assert.AreEqual(1, transport.AppliedRouteTransactions);
            operationId = originalOperation;
            var changed = replacement.Clone(); changed.End.XNm++;
            create.Items[0] = Any.Pack(changed); createJson = BoardJson.Formatter.Format(create);
            AssertError(await Apply(), "operation_id_reused");
            transport.LiveBoardChanged = false;
            var nativeItems = await ReadNativeItems(registry, transport.InstanceId, expected.Document, [delete.ItemIds[0], replacement.Id]);
            Assert.AreEqual(replacement, nativeItems.Single().Unpack<Track>());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("placement")]
    [DataRow("cleanup")]
    [DataRow("corner")]
    public async Task ForeignNativeReceiptCannotClaimSafeRetry(string wrapper)
    {
        string root = Directory.CreateTempSubdirectory("pcb-foreign-receipt-").FullName;
        try
        {
            var transport = new MigrationTransport { ForeignReceipt = true };
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            var expected = State(root, transport.Epoch, 5, 'a'); transport.LiveState = expected.Clone();
            var source = Track(Guid.NewGuid().ToString("D"), 1_000, 2_000); source.Net = new() { Name = "N1" };
            transport.ExistingItems.Items.Add(Any.Pack(source));
            var delete = new DeleteItems { Header = new() { Document = expected.Document }, ItemIds = { source.Id } };
            var replacement = source.Clone(); replacement.Id.Value = Guid.NewGuid().ToString("D");
            var create = new CreateItems { Header = new() { Document = expected.Document }, Items = { Any.Pack(replacement) } };
            string stateJson = SchematicJson.Formatter.Format(expected), operationId = Guid.NewGuid().ToString("D");
            if (wrapper == "placement")
            {
                create.Items.Clear(); create.Items.Add(Any.Pack(Footprint()));
            }
            Task<CallToolResult> Apply() => wrapper switch
            {
                "placement" => new PcbFootprintPlacementTools(registry).Apply(transport.InstanceId,
                    BoardJson.Formatter.Format(create), stateJson, operationId, default),
                "cleanup" => new PcbRouteCleanupTools(registry).Apply(transport.InstanceId,
                    BoardJson.Formatter.Format(delete), stateJson, operationId, default),
                _ => new PcbRouteOptimizationTools(registry).Apply(transport.InstanceId,
                    BoardJson.Formatter.Format(delete), BoardJson.Formatter.Format(create), stateJson, operationId, default)
            };
            foreach (CallToolResult result in new[] { await Apply(), await Apply() })
            {
                AssertError(result, "invalid_checked_pcb_receipt");
                JsonElement data = result.StructuredContent!.Value;
                Assert.AreEqual(transport.InstanceId, data.GetProperty("instanceId").GetString());
                Assert.AreEqual(transport.Epoch, data.GetProperty("processEpoch").GetString());
                Assert.AreEqual(operationId, data.GetProperty("operationId").GetString());
                Assert.IsFalse(data.GetProperty("mutationConfirmed").GetBoolean());
                Assert.IsFalse(data.GetProperty("transportRetrySafe").GetBoolean());
                Assert.IsFalse(data.GetProperty("operationReceipt").GetBoolean());
                Assert.IsFalse(data.GetProperty("replayed").GetBoolean());
                Assert.AreEqual(CheckedPcbItemTransactionState.CpbtsIndeterminate.ToString(), data.GetProperty("state").GetString());
                Assert.AreEqual(CheckedPcbItemTransactionState.CpbtsIndeterminate.ToString(), data.GetProperty("operationOutcome").GetString());
                Assert.AreEqual(JsonValueKind.Null, data.GetProperty("receipt").ValueKind);
                var raw = SchematicJson.Parser.Parse<CheckedPcbItemTransactionReceipt>(data.GetProperty("untrustedReceipt").GetRawText());
                Assert.AreNotEqual(transport.Epoch, raw.ProcessEpoch);
                Assert.AreNotEqual(operationId, raw.OperationId);
                Assert.AreEqual("foreign.kicad_pcb", raw.Document.BoardFilename);
            }
            Assert.AreEqual(1, transport.AppliedItemTransactions);
            Assert.AreEqual(0, transport.LegacyMutations);
            var expectedId = wrapper == "placement" ? create.Items[0].Unpack<FootprintInstance>().Id : replacement.Id;
            var nativeItems = await ReadNativeItems(registry, transport.InstanceId, expected.Document,
                wrapper == "cleanup" ? [source.Id] : [expectedId]);
            if (wrapper == "cleanup") Assert.IsEmpty(nativeItems);
            else Assert.AreEqual(create.Items[0], nativeItems.Single());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SvgArtworkReconcilesAdmittedReceiptWithoutSecondNativeDispatch()
    {
        string root = Directory.CreateTempSubdirectory("pcb-artwork-reconcile-").FullName;
        try
        {
            var transport = new MigrationTransport();
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            DocumentLifecycleState expected = State(root, transport.Epoch, 5, 'a');
            string stateJson = SchematicJson.Formatter.Format(expected);
            const string svg = "<svg viewBox=\"0 0 10 10\"><line x1=\"1\" y1=\"2\" x2=\"8\" y2=\"2\"/></svg>";
            string sourceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(svg)));
            string operationId = Guid.NewGuid().ToString("D");
            var tools = new PcbArtworkTools(registry);

            CallToolResult first = await tools.Commit(transport.InstanceId,
                SchematicJson.Formatter.Format(expected.Document), stateJson, svg, sourceHash,
                "imagegen", Guid.NewGuid().ToString("D"), root, "artwork/source.svg", "Dwgs.User",
                "0", "0", "1", operationId, default);
            JsonElement firstData = Success(first);
            Assert.IsTrue(firstData.GetProperty("authoritativeVector").GetBoolean(), Text(first));
            Assert.IsFalse(firstData.GetProperty("durableReconciled").GetBoolean(), Text(first));
            int nativeDispatches = transport.CheckedMutations;
            Assert.AreEqual(1, nativeDispatches);

            // Simulate MCP loss after native completion but before terminal
            // receipt publication. The native process still retains the exact
            // checked receipt; the next MCP instance must inspect it rather
            // than dispatch the board graphic a second time.
            string receiptPath = Directory.GetFiles(Path.Combine(root, "pcb-artwork-receipts"), "*.json").Single();
            File.Delete(receiptPath);
            CallToolResult reconciled = await tools.Commit(transport.InstanceId,
                SchematicJson.Formatter.Format(expected.Document), stateJson, svg, sourceHash,
                "imagegen", firstData.GetProperty("assetId").GetString()!, root, "artwork/source.svg", "Dwgs.User",
                "0", "0", "1", operationId, default);
            JsonElement reconciledData = Success(reconciled);
            Assert.IsTrue(reconciledData.GetProperty("durableReconciled").GetBoolean(), Text(reconciled));
            Assert.IsTrue(reconciledData.GetProperty("replayed").GetBoolean(), Text(reconciled));
            Assert.AreEqual(nativeDispatches, transport.CheckedMutations,
                "Durable admission reconciliation must never submit a second native mutation.");
            Assert.IsTrue(transport.ReceiptReads > 0, "Reconciliation must inspect the native receipt.");

            JsonElement receipt = Success(await tools.Receipt(transport.InstanceId, transport.Epoch,
                operationId, default));
            Assert.IsTrue(receipt.GetProperty("found").GetBoolean(), receipt.GetRawText());
            Assert.IsTrue(receipt.GetProperty("durableRecovery").GetBoolean(), receipt.GetRawText());
            Assert.IsTrue(receipt.GetProperty("receipt").GetProperty("durableReconciled").GetBoolean(), receipt.GetRawText());
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task AssertReceiptIdentity(Func<string, string, Task<CallToolResult>> inspect,
        MigrationTransport transport, string operationId, string expectedScope = "mcp-process")
    {
        int exchanges = transport.Exchanges;
        JsonElement data = Success(await inspect(transport.InstanceId, transport.Epoch));
        Assert.IsTrue(data.GetProperty("found").GetBoolean());
        Assert.IsTrue(data.GetProperty("nativeReceiptAvailable").GetBoolean());
        Assert.AreEqual(expectedScope, data.GetProperty("receiptScope").GetString());
        Assert.AreEqual(transport.InstanceId, data.GetProperty("receipt").GetProperty("instanceId").GetString());
        Assert.AreEqual(operationId, data.GetProperty("receipt").GetProperty("operationId").GetString());
        Assert.IsFalse(data.GetProperty("receipt").GetProperty("replayed").GetBoolean());
        if (expectedScope == "mcp-state")
        {
            JsonElement foreign = Success(await inspect(Guid.NewGuid().ToString("D"), transport.Epoch));
            Assert.IsFalse(foreign.GetProperty("found").GetBoolean(),
                "Durable route receipts are keyed by both instance and operation.");
        }
        else
            AssertError(await inspect(Guid.NewGuid().ToString("D"), transport.Epoch), "wrong_instance_receipt");
        AssertError(await inspect(transport.InstanceId, Guid.NewGuid().ToString("D")), "stale_process_epoch");
        Assert.AreEqual(exchanges, transport.Exchanges, "A historical MCP receipt lookup does not claim a new native read.");
    }

    private static async Task<Any[]> ReadNativeItems(InstanceRegistry registry, string instanceId,
        DocumentSpecifier document, IEnumerable<KIID> ids)
    {
        var response = await registry.Client(instanceId).InvokeAsync<GetItemsById, GetItemsResponse>(new()
        { Header = new() { Document = document.Clone() }, Items = { ids } });
        return response.Items.ToArray();
    }

    private static JsonElement Success(CallToolResult result)
    {
        Assert.IsFalse(result.IsError ?? false, Text(result));
        Assert.IsNotNull(result.StructuredContent);
        return result.StructuredContent!.Value;
    }

    private static void AssertError(CallToolResult result, string code)
    {
        Assert.IsTrue(result.IsError ?? false, Text(result));
        Assert.AreEqual(code, result.StructuredContent!.Value.GetProperty("errorCode").GetString(), Text(result));
    }

    private static FootprintInstance Footprint() => new()
    {
        Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = new() { XNm = 1_000, YNm = 2_000 },
        Orientation = new() { ValueDegrees = 0 }, Layer = BoardLayer.BlFCu,
        Definition = new() { Id = new() { LibraryNickname = "Generated", EntryName = "Fixture" } }
    };
}
