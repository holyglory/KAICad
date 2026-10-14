using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed partial class PcbCheckedItemMigrationTests
{
    [TestMethod]
    public async Task CreateUpdateDeleteUseOneCheckedDispatchAndReplayBeforeStaleRead()
    {
        string root = Directory.CreateTempSubdirectory("pcb-checked-migration-").FullName;
        try
        {
            var transport = new MigrationTransport();
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            var tools = new PcbItemTools(registry);
            var state = State(root, transport.Epoch, sequence: 5, digest: 'a');
            var document = state.Document.Clone();
            var track = Track(Guid.NewGuid().ToString("D"), 1_000, 2_000);
            var create = new CreateItems { Header = new() { Document = document }, Items = { Any.Pack(track) } };
            string operationId = Guid.NewGuid().ToString("D");
            var created = await tools.Create(transport.InstanceId, BoardJson.Formatter.Format(create),
                SchematicJson.Formatter.Format(state), default, operationId);
            Assert.IsFalse(created.IsError ?? false, Text(created));
            JsonElement createdData = JsonSerializer.SerializeToElement(created.StructuredContent);
            Assert.AreEqual(operationId, createdData.GetProperty("operationId").GetString());
            Assert.IsTrue(createdData.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.IsTrue(createdData.GetProperty("mutationConfirmed").GetBoolean());
            Assert.AreEqual(CheckedPcbItemTransactionState.CpbtsCompleted.ToString(), createdData.GetProperty("state").GetString());
            Assert.AreEqual(ItemRequestStatus.IrsOk.ToString(),
                BoardJson.Parser.Parse<CreateItemsResponse>(createdData.GetProperty("response").GetRawText()).Status.ToString());

            // The transport deliberately reports a different live board for any
            // hypothetical reread. A same-operation retry must still reach the
            // native checked controller and obtain its retained replay.
            transport.LiveBoardChanged = true;
            var replay = await tools.Create(transport.InstanceId, BoardJson.Formatter.Format(create),
                SchematicJson.Formatter.Format(state), default, operationId);
            Assert.IsFalse(replay.IsError ?? false, Text(replay));
            JsonElement replayData = JsonSerializer.SerializeToElement(replay.StructuredContent);
            Assert.IsTrue(replayData.GetProperty("replayed").GetBoolean());
            Assert.AreEqual(2, transport.CheckedMutations);
            Assert.AreEqual(0, transport.LifecycleReads);
            Assert.AreEqual(0, transport.LegacyMutations);

            var after = SchematicJson.Parser.Parse<DocumentLifecycleState>(createdData.GetProperty("afterState").GetString()!);
            var updatedTrack = track.Clone(); updatedTrack.End.XNm = 9_000;
            var update = new UpdateItems { Header = new() { Document = document }, Items = { Any.Pack(updatedTrack) } };
            var updated = await tools.Update(transport.InstanceId, BoardJson.Formatter.Format(update),
                SchematicJson.Formatter.Format(after), default, Guid.NewGuid().ToString("D"));
            Assert.IsFalse(updated.IsError ?? false, Text(updated));
            JsonElement updatedData = JsonSerializer.SerializeToElement(updated.StructuredContent);
            Assert.IsTrue(updatedData.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.AreEqual(after, SchematicJson.Parser.Parse<DocumentLifecycleState>(updatedData.GetProperty("beforeState").GetString()!));
            var delete = new DeleteItems { Header = new() { Document = document }, ItemIds = { track.Id.Clone() } };
            var current = SchematicJson.Parser.Parse<DocumentLifecycleState>(updatedData.GetProperty("afterState").GetString()!);
            var deleted = await tools.Delete(transport.InstanceId, BoardJson.Formatter.Format(delete),
                SchematicJson.Formatter.Format(current), default, Guid.NewGuid().ToString("D"));
            Assert.IsFalse(deleted.IsError ?? false, Text(deleted));
            JsonElement deletedData = JsonSerializer.SerializeToElement(deleted.StructuredContent);
            var deleteResponse = BoardJson.Parser.Parse<DeleteItemsResponse>(deletedData.GetProperty("response").GetRawText());
            Assert.AreEqual(ItemRequestStatus.IrsOk, deleteResponse.Status);
            Assert.IsTrue(deletedData.GetProperty("nativeReceiptAvailable").GetBoolean());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task GroupDeleteRejectsDuplicateIdsBeforeDispatch()
    {
        string root = Directory.CreateTempSubdirectory("pcb-group-delete-guard-").FullName;
        try
        {
            var transport = new MigrationTransport();
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            var tools = new PcbItemTools(registry);
            var state = State(root, transport.Epoch, sequence: 5, digest: 'a');
            string groupId = Guid.NewGuid().ToString("D");
            var delete = new DeleteItems
            {
                Header = new() { Document = state.Document.Clone() },
                ItemIds = { new KIID { Value = groupId }, new KIID { Value = groupId } }
            };

            var result = await tools.Delete(transport.InstanceId, BoardJson.Formatter.Format(delete),
                SchematicJson.Formatter.Format(state), default, Guid.NewGuid().ToString("D"));

            Assert.IsTrue(result.IsError ?? false, Text(result));
            JsonElement data = JsonSerializer.SerializeToElement(result.StructuredContent);
            Assert.AreEqual("invalid_pcb_item_request", data.GetProperty("errorCode").GetString());
            Assert.AreEqual(0, transport.CheckedMutations,
                "Duplicate group/member identities must be rejected before native transaction dispatch.");
            Assert.AreEqual(0, transport.LegacyMutations);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RouteCleanupUsesCheckedDeleteDispatchAndReplayBeforeStaleRead()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-cleanup-migration-").FullName;
        try
        {
            var transport = new MigrationTransport();
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            var tools = new PcbRouteCleanupTools(registry);
            var state = State(root, transport.Epoch, sequence: 5, digest: 'a');
            var delete = new DeleteItems
            {
                Header = new() { Document = state.Document.Clone() },
                ItemIds = { new KIID { Value = Guid.NewGuid().ToString("D") } }
            };
            transport.ExistingItems.Items.Add(Any.Pack(Track(delete.ItemIds[0].Value, 1_000, 2_000)));
            string operationId = Guid.NewGuid().ToString("D");

            var applied = await tools.Apply(transport.InstanceId, BoardJson.Formatter.Format(delete),
                SchematicJson.Formatter.Format(state), operationId, default);
            Assert.IsFalse(applied.IsError ?? false, Text(applied));
            JsonElement appliedData = JsonSerializer.SerializeToElement(applied.StructuredContent);
            Assert.AreEqual(transport.InstanceId, appliedData.GetProperty("instanceId").GetString());
            Assert.IsTrue(appliedData.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.IsTrue(appliedData.GetProperty("mutationConfirmed").GetBoolean());
            Assert.AreEqual(CheckedPcbItemTransactionState.CpbtsCompleted.ToString(),
                appliedData.GetProperty("state").GetString());
            var response = BoardJson.Parser.Parse<DeleteItemsResponse>(appliedData.GetProperty("response").GetRawText());
            Assert.AreEqual(delete.ItemIds[0], response.DeletedItems[0].Id);
            await AssertReceiptIdentity((instance, epoch) => tools.Receipt(instance, epoch, operationId, default),
                transport, operationId);

            // A retry must use the native retained receipt without reopening a
            // legacy board transaction or rereading a changed board.
            transport.LiveBoardChanged = true;
            var replay = await tools.Apply(transport.InstanceId, BoardJson.Formatter.Format(delete),
                SchematicJson.Formatter.Format(state), operationId, default);
            Assert.IsFalse(replay.IsError ?? false, Text(replay));
            JsonElement replayData = JsonSerializer.SerializeToElement(replay.StructuredContent);
            Assert.IsTrue(replayData.GetProperty("replayed").GetBoolean());
            Assert.AreEqual(2, transport.CheckedMutations);
            var foreignState = state.Clone(); foreignState.ProcessEpoch = Guid.NewGuid().ToString("D");
            AssertError(await tools.Apply(transport.InstanceId, BoardJson.Formatter.Format(delete),
                SchematicJson.Formatter.Format(foreignState), Guid.NewGuid().ToString("D"), default), "pcb_instance_changed");
            transport.LiveBoardChanged = false;
            Assert.IsEmpty(await ReadNativeItems(registry, transport.InstanceId, state.Document, delete.ItemIds));
            Assert.AreEqual(0, transport.LifecycleReads);
            Assert.AreEqual(0, transport.LegacyMutations);
            var changed = delete.Clone(); changed.ItemIds[0].Value = Guid.NewGuid().ToString("D");
            AssertError(await tools.Apply(transport.InstanceId, BoardJson.Formatter.Format(changed),
                SchematicJson.Formatter.Format(state), operationId, default), "operation_id_reused");
            Assert.AreEqual(2, transport.CheckedMutations);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task GeneratedOperationIdIsReturnedAndRejectedUpdateKeepsBeforeSnapshot()
    {
        string root = Directory.CreateTempSubdirectory("pcb-checked-rejection-").FullName;
        try
        {
            var transport = new MigrationTransport { RejectUpdates = true };
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            var tools = new PcbItemTools(registry);
            var state = State(root, transport.Epoch, sequence: 7, digest: 'c');
            var track = Track(Guid.NewGuid().ToString("D"), 10_000, 20_000);
            var update = new UpdateItems { Header = new() { Document = state.Document.Clone() }, Items = { Any.Pack(track) } };
            var rejected = await tools.Update(transport.InstanceId, BoardJson.Formatter.Format(update),
                SchematicJson.Formatter.Format(state), default);
            Assert.IsTrue(rejected.IsError ?? false, Text(rejected));
            JsonElement data = JsonSerializer.SerializeToElement(rejected.StructuredContent);
            string generated = data.GetProperty("operationId").GetString()!;
            Assert.IsTrue(Guid.TryParseExact(generated, "D", out _));
            Assert.IsTrue(data.GetProperty("nativeReceiptAvailable").GetBoolean());
            Assert.IsFalse(data.GetProperty("mutationConfirmed").GetBoolean());
            Assert.AreEqual(state, SchematicJson.Parser.Parse<DocumentLifecycleState>(data.GetProperty("beforeState").GetString()!));
            Assert.AreEqual(CheckedPcbItemTransactionState.CpbtsRolledBack.ToString(), data.GetProperty("state").GetString());
            Assert.AreEqual("pcb_item_transaction_rejected", data.GetProperty("errorCode").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    private static DocumentLifecycleState State(string root, string epoch, long sequence, char digest)
    {
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb, BoardFilename = "fixture.kicad_pcb",
            Project = new() { Name = "fixture", Path = root }
        };
        string identity = Guid.NewGuid().ToString("D");
        var state = new DocumentLifecycleState
        {
            Document = document, ProcessEpoch = epoch, NativeIdentity = identity,
            Revision = new() { Epoch = identity, Sequence = (ulong)sequence }, Scope = DocumentLifecycleScope.DlsPcb,
            ProjectSettingsIncluded = true, CompleteChangeTracking = true, DiskBaselineChecked = true,
            StateSha256 = new string(digest, 64)
        };
        foreach (string path in new[] { Path.Combine(root, "fixture.kicad_pcb"), Path.Combine(root, "fixture.kicad_pro") })
        {
            state.NativeFiles.Add(path);
            state.FileBaselines.Add(new NativeFileBaselineState
            {
                Path = path, BaselinePath = path, BaselineKnown = true, CurrentKnown = true,
                Status = NativeFileBaselineStatus.NfbsUnchanged, BaselineExists = false, CurrentExists = false
            });
        }
        return state;
    }

    private static Track Track(string id, long start, long end) => new()
    {
        Id = new() { Value = id }, Start = new() { XNm = start, YNm = start },
        End = new() { XNm = end, YNm = start }, Width = new() { ValueNm = 200 }, Layer = BoardLayer.BlFCu
    };

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "<no tool content>";

    private sealed class MigrationTransport : INativeTransport
    {
        private readonly Dictionary<string, CheckedPcbItemTransactionReceipt> receipts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CheckedPcbItemTransaction> checkedRequests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (StartPcbDrcJob Request, PcbDrcJobState State)> drcJobs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (CheckedPcbRouteCommit Request, CheckedPcbRouteReceipt Receipt)> routeReceipts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Any> boardItems = new(StringComparer.Ordinal);
        private bool boardInitialized;
        internal string Epoch { get; } = Guid.NewGuid().ToString("D");
        internal string InstanceId { get; } = Guid.NewGuid().ToString("D");
        internal bool LiveBoardChanged { get; set; }
        internal bool RejectUpdates { get; init; }
        internal bool ForeignReceipt { get; init; }
        internal NngException? RouteFailure { get; set; }
        internal DocumentLifecycleState? LiveState { get; set; }
        internal GetItemsResponse ExistingItems { get; } = new();
        internal int CheckedMutations { get; private set; }
        internal int ReceiptReads { get; private set; }
        internal int ItemReads { get; private set; }
        internal int RouteMutations { get; private set; }
        internal int AppliedItemTransactions { get; private set; }
        internal int AppliedRouteTransactions { get; private set; }
        internal int Exchanges { get; private set; }
        internal int LifecycleReads { get; private set; }
        internal int LegacyMutations { get; private set; }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] bytes, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Exchanges++;
            var request = ApiRequest.Parser.ParseFrom(bytes);
            if (request.Message.Is(GetAutomationSession.Descriptor))
                return Reply(new AutomationSession { ProtocolVersion = 1, InstanceId = InstanceId,
                    ProjectPath = "/fixture/fixture.kicad_pro", Epoch = Epoch });
            if (request.Message.Is(ReadDocumentLifecycleState.Descriptor))
            {
                LifecycleReads++;
                return Reply(!LiveBoardChanged && LiveState is not null
                    ? LiveState.Clone() : new DocumentLifecycleState { ProcessEpoch = Epoch });
            }
            if (request.Message.Is(GetItemsById.Descriptor))
            {
                ItemReads++;
                if (LiveBoardChanged) return Error("live target read is unavailable");
                InitializeBoard();
                var query = request.Message.Unpack<GetItemsById>();
                var found = new GetItemsResponse();
                found.Items.Add(query.Items.Where(id => boardItems.ContainsKey(id.Value)).Select(id => boardItems[id.Value].Clone()));
                return Reply(found);
            }
            if (request.Message.Is(ReadCheckedPcbItemTransactionReceipt.Descriptor))
            {
                ReceiptReads++;
                var query = request.Message.Unpack<ReadCheckedPcbItemTransactionReceipt>();
                if (receipts.TryGetValue(query.OperationId, out var retained))
                {
                    if (!checkedRequests[query.OperationId].Equals(query.ExpectedRequest))
                        return Error("receipt request differs from the original transaction");
                    return Reply(retained.Clone());
                }
                return Reply(new CheckedPcbItemTransactionReceipt
                {
                    Document = query.Document.Clone(), ProcessEpoch = Epoch, OperationId = query.OperationId,
                    State = CheckedPcbItemTransactionState.CpbtsNotFound
                });
            }
            if (request.Message.Is(StartPcbDrcJob.Descriptor))
            {
                var start = request.Message.Unpack<StartPcbDrcJob>();
                var state = new PcbDrcJobState
                {
                    Document = start.Document.Clone(), ProcessEpoch = Epoch, OperationId = start.OperationId,
                    JobId = Guid.NewGuid().ToString("D"), CheckedRevision = start.ExpectedRevision.Clone(),
                    Status = PcbDrcJobStatus.PdrcjsCompleted, WorkerFinished = true, Progress = 1,
                    SnapshotComplete = true, ResultsFresh = true, CandidateDryRun = true
                };
                state.CandidateItemIds.Add(start.CandidateItems.Select(item => item.Unpack<Track>().Id.Value));
                drcJobs.Add(state.JobId, (start.Clone(), state));
                return Reply(state);
            }
            if (request.Message.Is(NativeCapabilityPcbDrcReplacementJob.Descriptor))
            {
                var query = request.Message.Unpack<NativeCapabilityPcbDrcReplacementJob>();
                var job = drcJobs[query.JobId];
                if (!job.Request.CandidateItems.Equals(query.ExpectedCandidateItems)
                    || !job.Request.DeleteItemIds.Equals(query.ExpectedDeleteItemIds))
                    return Error("DRC payload differs from the checked replacement");
                job.State.CandidatePayloadVerified = true;
                return Reply(job.State);
            }
            if (request.Message.Is(CheckedPcbRouteCommit.Descriptor))
            {
                RouteMutations++;
                var commit = request.Message.Unpack<CheckedPcbRouteCommit>();
                if (routeReceipts.TryGetValue(commit.OperationId, out var retained))
                {
                    if (!retained.Request.Equals(commit)) return Error("route replay payload differs");
                    var replay = retained.Receipt.Clone(); replay.Replayed = true;
                    return Reply(replay);
                }
                var job = drcJobs[commit.DrcJobId];
                if (job.State.Status != PcbDrcJobStatus.PdrcjsCompleted || !job.State.ResultsFresh
                    || !job.State.SnapshotComplete || job.State.Findings.Count != 0)
                    return Reply(new CheckedPcbRouteReceipt
                    {
                        Document = commit.ExpectedState.Document.Clone(), ProcessEpoch = Epoch, OperationId = commit.OperationId,
                        State = CheckedPcbRouteReceipt.Types.State.Rejected, RequestVerified = true,
                        ErrorCode = "native_drc_not_clean", ErrorMessage = "The retained native DRC job is not fresh and clean."
                    });
                if (!job.Request.Document.Equals(commit.ExpectedState.Document)
                    || !job.Request.ExpectedRevision.Equals(commit.ExpectedState.Revision)
                    || !job.Request.CandidateItems.Equals(commit.Candidate.Items)
                    || !job.Request.DeleteItemIds.Equals(commit.Removals.ItemIds))
                    return Error("route request differs from the checked DRC job");
                var after = commit.ExpectedState.Clone(); after.Revision.Sequence++; after.StateSha256 = new string('b', 64);
                var routeReceipt = new CheckedPcbRouteReceipt
                {
                    Document = commit.ExpectedState.Document.Clone(), ProcessEpoch = Epoch, OperationId = commit.OperationId,
                    State = CheckedPcbRouteReceipt.Types.State.Completed, RequestVerified = true,
                    Before = commit.ExpectedState.Clone(), After = after,
                    Created = CompleteOperation(new PcbItemOperation { Create = commit.Candidate }).Create,
                    Removals = commit.Removals.Clone(),
                    RemovedResult = CompleteOperation(new PcbItemOperation { Delete = commit.Removals }).Delete
                };
                routeReceipts.Add(commit.OperationId, (commit.Clone(), routeReceipt.Clone()));
                ApplyBoardOperation(new PcbItemOperation { Delete = commit.Removals });
                ApplyBoardOperation(new PcbItemOperation { Create = commit.Candidate });
                LiveState = after.Clone();
                AppliedRouteTransactions++;
                return Reply(routeReceipt);
            }
            if (request.Message.Is(BeginCommit.Descriptor) || request.Message.Is(EndCommit.Descriptor)
                || request.Message.Is(CreateItems.Descriptor) || request.Message.Is(UpdateItems.Descriptor)
                || request.Message.Is(DeleteItems.Descriptor))
            {
                LegacyMutations++;
                return Error("legacy primitive dispatch was used");
            }
            if (!request.Message.Is(CheckedPcbItemTransaction.Descriptor))
                return Error("unexpected test request");

            if (RouteFailure is { } routeFailure)
                throw routeFailure;

            CheckedMutations++;
            var checkedRequest = request.Message.Unpack<CheckedPcbItemTransaction>();
            if (receipts.TryGetValue(checkedRequest.OperationId, out var existing))
            {
                if (!checkedRequests[checkedRequest.OperationId].Equals(checkedRequest))
                    return Error("checked replay payload differs");
                var replay = existing.Clone(); replay.Replayed = true;
                return Reply(replay);
            }
            var receipt = checkedRequest.Operations[0].ActionCase == PcbItemOperation.ActionOneofCase.Update && RejectUpdates
                ? RolledBackUpdate(checkedRequest)
                : Completed(checkedRequest, checkedRequest.Operations.Select(CompleteOperation));
            if (receipt.State == CheckedPcbItemTransactionState.CpbtsCompleted)
            {
                foreach (var operation in checkedRequest.Operations) ApplyBoardOperation(operation);
                LiveState = receipt.ObservedAfter.Clone();
                AppliedItemTransactions++;
            }
            if (ForeignReceipt)
            {
                receipt.OperationId = Guid.NewGuid().ToString("D");
                receipt.ProcessEpoch = Guid.NewGuid().ToString("D");
                receipt.Document.BoardFilename = "foreign.kicad_pcb";
            }
            checkedRequests.Add(checkedRequest.OperationId, checkedRequest.Clone());
            receipts.Add(checkedRequest.OperationId, receipt.Clone());
            return Reply(receipt);
        }

        private void InitializeBoard()
        {
            if (boardInitialized) return;
            foreach (var item in ExistingItems.Items) boardItems.Add(ItemId(item), item.Clone());
            boardInitialized = true;
        }

        private void ApplyBoardOperation(PcbItemOperation operation)
        {
            InitializeBoard();
            if (operation.ActionCase == PcbItemOperation.ActionOneofCase.Delete)
            {
                foreach (var id in operation.Delete.ItemIds)
                    Assert.IsTrue(boardItems.Remove(id.Value), "A native deletion cannot run twice.");
            }
            else
            {
                var items = operation.ActionCase == PcbItemOperation.ActionOneofCase.Create
                    ? operation.Create.Items : operation.Update.Items;
                foreach (var item in items)
                {
                    if (operation.ActionCase == PcbItemOperation.ActionOneofCase.Create)
                        Assert.IsFalse(boardItems.ContainsKey(ItemId(item)), "A native creation cannot run twice.");
                    boardItems[ItemId(item)] = item.Clone();
                }
            }
        }

        private static string ItemId(Any item) => item.Is(Kiapi.Board.Types.Track.Descriptor)
            ? item.Unpack<Track>().Id.Value
            : item.Is(BoardGraphicShape.Descriptor)
                ? item.Unpack<BoardGraphicShape>().Id.Value
                : item.Unpack<FootprintInstance>().Id.Value;

        internal void InvalidateDrc(string jobId)
        {
            drcJobs[jobId].State.ResultsFresh = false;
        }

        private static PcbItemOperationResult CompleteOperation(PcbItemOperation operation)
        {
            switch (operation.ActionCase)
            {
                case PcbItemOperation.ActionOneofCase.Create:
                    var create = new CreateItemsResponse { Status = ItemRequestStatus.IrsOk };
                    create.CreatedItems.Add(operation.Create.Items.Select(item => new ItemCreationResult
                    { Status = new() { Code = ItemStatusCode.IscOk }, Item = item.Clone() }));
                    return new() { Create = create };
                case PcbItemOperation.ActionOneofCase.Update:
                    var update = new UpdateItemsResponse { Status = ItemRequestStatus.IrsOk };
                    update.UpdatedItems.Add(operation.Update.Items.Select(item => new ItemUpdateResult
                    { Status = new() { Code = ItemStatusCode.IscOk }, Item = item.Clone() }));
                    return new() { Update = update };
                case PcbItemOperation.ActionOneofCase.Delete:
                    var delete = new DeleteItemsResponse { Status = ItemRequestStatus.IrsOk };
                    delete.DeletedItems.Add(operation.Delete.ItemIds.Select(id => new ItemDeletionResult
                    { Id = id.Clone(), Status = ItemDeletionStatus.IdsOk }));
                    return new() { Delete = delete };
                default: throw new AssertFailedException("unsupported fixture operation");
            }
        }

        private static CheckedPcbItemTransactionReceipt Completed(CheckedPcbItemTransaction request,
            IEnumerable<PcbItemOperationResult> results)
        {
            var after = request.ExpectedState.Clone(); after.Revision.Sequence++; after.StateSha256 = new string('b', 64);
            var receipt = new CheckedPcbItemTransactionReceipt
            {
                Document = request.ExpectedState.Document.Clone(), ProcessEpoch = request.ProcessEpoch,
                OperationId = request.OperationId, State = CheckedPcbItemTransactionState.CpbtsCompleted,
                RequestVerified = true, Phase = CheckedPcbItemTransactionPhase.CpbtpComplete,
                RollbackState = CheckedPcbItemRollbackState.CpbrsNotAttempted,
                PostCommitState = CheckedPcbItemPostCommitState.CpbpsConfirmed,
                ObservedBefore = request.ExpectedState.Clone(), ObservedAfter = after
            };
            receipt.Results.Add(results);
            return receipt;
        }

        private static CheckedPcbItemTransactionReceipt RolledBackUpdate(CheckedPcbItemTransaction request) => new()
        {
            Document = request.ExpectedState.Document.Clone(), ProcessEpoch = request.ProcessEpoch,
            OperationId = request.OperationId, State = CheckedPcbItemTransactionState.CpbtsRolledBack,
            RequestVerified = true, Phase = CheckedPcbItemTransactionPhase.CpbtpRollbackConfirmed,
            RollbackState = CheckedPcbItemRollbackState.CpbrsConfirmed,
            PostCommitState = CheckedPcbItemPostCommitState.CpbpsNotAttempted,
            ErrorCode = "update_rejected", ErrorMessage = "fixture update rejected",
            ObservedBefore = request.ExpectedState.Clone(), ObservedRollback = request.ExpectedState.Clone()
        };

        private Task<byte[]> Reply(IMessage message) => Task.FromResult(new ApiResponse
        {
            Header = new() { KicadToken = Epoch }, Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(message)
        }.ToByteArray());

        private Task<byte[]> Error(string message) => Task.FromResult(new ApiResponse
        {
            Header = new() { KicadToken = Epoch }, Status = new() { Status = (ApiStatusCode)3, ErrorMessage = message }
        }.ToByteArray());
    }
}
