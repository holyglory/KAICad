using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbZoneCheckedMigrationTests
{
    [TestMethod]
    public async Task CreateReplaysThroughCheckedReceiptWithoutLegacyMutation()
    {
        string root = Directory.CreateTempSubdirectory("pcb-zone-checked-replay-").FullName;
        try
        {
            var transport = new ZoneTransport();
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            var tools = new PcbZoneAuthoringTools(registry);
            var state = State(root, transport.Epoch, sequence: 5, digest: 'a');
            var input = Input(Guid.NewGuid().ToString("D"));
            string document = SchematicJson.Formatter.Format(state.Document);
            string expected = SchematicJson.Formatter.Format(state);
            string operationId = Guid.NewGuid().ToString("D");

            var created = await tools.Create(transport.InstanceId, document, input, expected, operationId, default);
            Assert.IsFalse(created.IsError ?? false, Text(created));
            JsonElement createdData = JsonSerializer.SerializeToElement(created.StructuredContent);
            Assert.IsTrue(createdData.GetProperty("nativeReceiptAvailable").GetBoolean(), Text(created));
            Assert.IsTrue(createdData.GetProperty("replaySafe").GetBoolean(), Text(created));
            Assert.IsTrue(createdData.GetProperty("mutationConfirmed").GetBoolean(), Text(created));
            Assert.AreEqual(CheckedPcbItemTransactionState.CpbtsCompleted.ToString(), createdData.GetProperty("state").GetString());

            var replay = await tools.Create(transport.InstanceId, document, input, expected, operationId, default);
            Assert.IsFalse(replay.IsError ?? false, Text(replay));
            JsonElement replayData = JsonSerializer.SerializeToElement(replay.StructuredContent);
            Assert.IsTrue(replayData.GetProperty("nativeReceiptAvailable").GetBoolean(), Text(replay));
            Assert.IsTrue(replayData.GetProperty("replayed").GetBoolean(), Text(replay));
            Assert.AreEqual(1, transport.CheckedMutations, "A retained replay is settled by receipt inspection without resubmitting the mutation.");
            Assert.AreEqual(0, transport.LegacyMutations);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UpdateRefusalReturnsCheckedRollbackReceipt()
    {
        string root = Directory.CreateTempSubdirectory("pcb-zone-checked-refusal-").FullName;
        try
        {
            var transport = new ZoneTransport { RejectUpdates = true };
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            var tools = new PcbZoneAuthoringTools(registry);
            var state = State(root, transport.Epoch, sequence: 7, digest: 'c');
            transport.LifecycleState = state;
            var input = Input(Guid.NewGuid().ToString("D"));
            transport.Existing = input.ToNative();
            var reply = await tools.Update(transport.InstanceId, SchematicJson.Formatter.Format(state.Document), input,
                SchematicJson.Formatter.Format(state), Guid.NewGuid().ToString("D"), default);
            Assert.IsTrue(reply.IsError ?? false, Text(reply));
            JsonElement data = JsonSerializer.SerializeToElement(reply.StructuredContent);
            Assert.IsTrue(data.GetProperty("nativeReceiptAvailable").GetBoolean(), Text(reply));
            Assert.AreEqual(CheckedPcbItemTransactionState.CpbtsRolledBack.ToString(), data.GetProperty("state").GetString());
            Assert.AreEqual("pcb_item_transaction_rejected", data.GetProperty("errorCode").GetString());
            Assert.IsFalse(data.GetProperty("mutationConfirmed").GetBoolean(), Text(reply));
            Assert.AreEqual(0, transport.LegacyMutations);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UpdateAndDeleteReplaySkipLiveTargetReadsAfterReceiptExists()
    {
        string root = Directory.CreateTempSubdirectory("pcb-zone-checked-target-replay-").FullName;
        try
        {
            var transport = new ZoneTransport();
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            var tools = new PcbZoneAuthoringTools(registry);
            var state = State(root, transport.Epoch, sequence: 9, digest: 'd');
            transport.LifecycleState = state;
            var input = Input(Guid.NewGuid().ToString("D"));
            transport.Existing = input.ToNative();
            transport.Existing.Parent = new KIID { Value = state.NativeIdentity };
            transport.Existing.Filled = true;
            transport.Existing.Locked = LockedState.LsLocked;
            transport.Existing.Priority = 17;
            transport.Existing.CustomProperties.Add(new CustomProperty
            {
                Key = "native.zone.metadata", Value = "preserve-on-update"
            });
            string document = SchematicJson.Formatter.Format(state.Document);
            string expected = SchematicJson.Formatter.Format(state);
            string updateOperation = Guid.NewGuid().ToString("D");

            var updated = await tools.Update(transport.InstanceId, document, input, expected, updateOperation, default);
            Assert.IsFalse(updated.IsError ?? false, Text(updated));
            var updatedResponse = BoardJson.Parser.Parse<UpdateItemsResponse>(
                JsonSerializer.SerializeToElement(updated.StructuredContent).GetProperty("response").GetRawText());
            var appliedUpdate = updatedResponse.UpdatedItems.Single().Item.Unpack<Zone>();
            Assert.AreEqual(LockedState.LsLocked, appliedUpdate.Locked);
            Assert.AreEqual(23u, appliedUpdate.Priority);
            Assert.AreEqual("preserve-on-update", appliedUpdate.CustomProperties.Single().Value);
            transport.LiveBoardChanged = true;
            var updateReplay = await tools.Update(transport.InstanceId, document, input, expected, updateOperation, default);
            Assert.IsFalse(updateReplay.IsError ?? false, Text(updateReplay));
            Assert.IsTrue(JsonSerializer.SerializeToElement(updateReplay.StructuredContent).GetProperty("replayed").GetBoolean(), Text(updateReplay));

            // A delete has the same target preflight requirement. Its first
            // call reads the copper zone; the exact operation ID then settles
            // from the receipt even after that live read is unavailable.
            transport.LiveBoardChanged = false;
            string deleteOperation = Guid.NewGuid().ToString("D");
            var deleted = await tools.Delete(transport.InstanceId, document, input.Id, expected, deleteOperation, default);
            Assert.IsFalse(deleted.IsError ?? false, Text(deleted));
            transport.LiveBoardChanged = true;
            var deleteReplay = await tools.Delete(transport.InstanceId, document, input.Id, expected, deleteOperation, default);
            Assert.IsFalse(deleteReplay.IsError ?? false, Text(deleteReplay));
            Assert.IsTrue(JsonSerializer.SerializeToElement(deleteReplay.StructuredContent).GetProperty("replayed").GetBoolean(), Text(deleteReplay));
            Assert.AreEqual(2, transport.ItemReads, "Only the first update and delete may read the live target.");
            Assert.AreEqual(2, transport.CheckedMutations, "Receipt replays must not resubmit checked mutations.");
            Assert.AreEqual(0, transport.LegacyMutations);
        }
        finally { Directory.Delete(root, true); }
    }

    private static PcbZoneAuthoringInput Input(string id) => new()
    {
        Id = id,
        Layers = ["F.Cu"],
        Polygons = [new PcbZonePolygonInput
        {
            Outline = [new() { XNm = 0, YNm = 0 }, new() { XNm = 20_000, YNm = 0 }, new() { XNm = 0, YNm = 20_000 }],
            Holes = []
        }],
        Name = "checked zone",
        Priority = 23,
        NetName = "GND",
        ClearanceNm = 200,
        MinThicknessNm = 200,
        FillMode = "ZFM_SOLID",
        Connection = "ZCS_INHERITED",
        IslandMode = "IRM_ALWAYS",
        BorderStyle = "ZBS_SOLID"
    };

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
            Revision = new() { Epoch = identity, Sequence = (ulong) sequence }, Scope = DocumentLifecycleScope.DlsPcb,
            ProjectSettingsIncluded = true, DiskBaselineChecked = true, StateSha256 = new string(digest, 64)
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

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault()?.Text ?? "<no tool content>";

    private sealed class ZoneTransport : INativeTransport
    {
        private readonly Dictionary<string, CheckedPcbItemTransactionReceipt> receipts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CheckedPcbItemTransaction> requests = new(StringComparer.Ordinal);
        internal string Epoch { get; } = Guid.NewGuid().ToString("D");
        internal string InstanceId { get; } = Guid.NewGuid().ToString("D");
        internal bool RejectUpdates { get; init; }
        internal bool LiveBoardChanged { get; set; }
        internal DocumentLifecycleState? LifecycleState { get; set; }
        internal Zone? Existing { get; set; }
        internal int CheckedMutations { get; private set; }
        internal int LegacyMutations { get; private set; }
        internal int ItemReads { get; private set; }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] bytes, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var request = ApiRequest.Parser.ParseFrom(bytes);
            if (request.Message.Is(GetAutomationSession.Descriptor))
                return Reply(new AutomationSession { ProtocolVersion = 1, InstanceId = InstanceId,
                    ProjectPath = "/fixture/fixture.kicad_pro", Epoch = Epoch });
            if (request.Message.Is(ReadDocumentLifecycleState.Descriptor))
            {
                if (LifecycleState is null)
                    return Error("lifecycle state was not configured");
                return Reply(LifecycleState.Clone());
            }
            if (request.Message.Is(GetItemsById.Descriptor))
            {
                ItemReads++;
                if (LiveBoardChanged) return Error("live zone target unavailable");
                var response = new GetItemsResponse { Status = ItemRequestStatus.IrsOk };
                if (Existing is not null) response.Items.Add(Any.Pack(Existing));
                return Reply(response);
            }
            if (request.Message.Is(ReadCheckedPcbItemTransactionReceipt.Descriptor))
            {
                var query = request.Message.Unpack<ReadCheckedPcbItemTransactionReceipt>();
                if (receipts.TryGetValue(query.OperationId, out var retained))
                {
                    if (!requests.TryGetValue(query.OperationId, out var stored)
                        || !stored.Equals(query.ExpectedRequest))
                        return Error("checked receipt inspection used a different request");
                    return Reply(retained.Clone());
                }
                return Reply(new CheckedPcbItemTransactionReceipt
                {
                    Document = query.Document.Clone(), ProcessEpoch = Epoch,
                    OperationId = query.OperationId, State = CheckedPcbItemTransactionState.CpbtsNotFound,
                    Phase = CheckedPcbItemTransactionPhase.CpbtpUnknown,
                    RollbackState = CheckedPcbItemRollbackState.CpbrsNotAttempted,
                    PostCommitState = CheckedPcbItemPostCommitState.CpbpsNotAttempted
                });
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

            CheckedMutations++;
            var checkedRequest = request.Message.Unpack<CheckedPcbItemTransaction>();
            if (receipts.TryGetValue(checkedRequest.OperationId, out var previous))
            {
                if (!requests[checkedRequest.OperationId].Equals(checkedRequest))
                    return Error("checked operation replay used a different request");
                var replay = previous.Clone();
                replay.Replayed = true;
                return Reply(replay);
            }
            CheckedPcbItemTransactionReceipt receipt = checkedRequest.Operations[0].ActionCase switch
            {
                PcbItemOperation.ActionOneofCase.Create => CompletedCreate(checkedRequest),
                PcbItemOperation.ActionOneofCase.Update when RejectUpdates => RolledBack(checkedRequest),
                PcbItemOperation.ActionOneofCase.Update => CompletedUpdate(checkedRequest),
                PcbItemOperation.ActionOneofCase.Delete => CompletedDelete(checkedRequest),
                _ => throw new AssertFailedException("unsupported zone fixture operation")
            };
            requests.Add(checkedRequest.OperationId, checkedRequest.Clone());
            receipts.Add(checkedRequest.OperationId, receipt.Clone());
            return Reply(receipt);
        }

        private static CheckedPcbItemTransactionReceipt CompletedCreate(CheckedPcbItemTransaction request) =>
            Completed(request, new PcbItemOperationResult
            {
                Create = new CreateItemsResponse
                {
                    Status = ItemRequestStatus.IrsOk,
                    CreatedItems = { new ItemCreationResult { Status = new() { Code = ItemStatusCode.IscOk }, Item = request.Operations[0].Create.Items[0].Clone() } }
                }
            });

        private static CheckedPcbItemTransactionReceipt CompletedUpdate(CheckedPcbItemTransaction request) =>
            Completed(request, new PcbItemOperationResult
            {
                Update = new UpdateItemsResponse
                {
                    Status = ItemRequestStatus.IrsOk,
                    UpdatedItems = { new ItemUpdateResult { Status = new() { Code = ItemStatusCode.IscOk }, Item = request.Operations[0].Update.Items[0].Clone() } }
                }
            });

        private static CheckedPcbItemTransactionReceipt CompletedDelete(CheckedPcbItemTransaction request) =>
            Completed(request, new PcbItemOperationResult
            {
                Delete = new DeleteItemsResponse
                {
                    Status = ItemRequestStatus.IrsOk,
                    DeletedItems = { new ItemDeletionResult { Id = request.Operations[0].Delete.ItemIds[0].Clone(), Status = ItemDeletionStatus.IdsOk } }
                }
            });

        private static CheckedPcbItemTransactionReceipt Completed(CheckedPcbItemTransaction request, PcbItemOperationResult result)
        {
            var after = request.ExpectedState.Clone();
            after.Revision.Sequence++;
            after.StateSha256 = new string('b', 64);
            var receipt = new CheckedPcbItemTransactionReceipt
            {
                Document = request.ExpectedState.Document.Clone(), ProcessEpoch = request.ProcessEpoch,
                OperationId = request.OperationId, State = CheckedPcbItemTransactionState.CpbtsCompleted,
                RequestVerified = true, Phase = CheckedPcbItemTransactionPhase.CpbtpComplete,
                RollbackState = CheckedPcbItemRollbackState.CpbrsNotAttempted,
                PostCommitState = CheckedPcbItemPostCommitState.CpbpsConfirmed,
                ObservedBefore = request.ExpectedState.Clone(), ObservedAfter = after
            };
            receipt.Results.Add(result);
            return receipt;
        }

        private static CheckedPcbItemTransactionReceipt RolledBack(CheckedPcbItemTransaction request) => new()
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
            Header = new() { KicadToken = Epoch }, Status = new() { Status = (ApiStatusCode) 1 }, Message = Any.Pack(message)
        }.ToByteArray());

        private Task<byte[]> Error(string message) => Task.FromResult(new ApiResponse
        {
            Header = new() { KicadToken = Epoch }, Status = new() { Status = (ApiStatusCode) 3, ErrorMessage = message }
        }.ToByteArray());
    }
}
