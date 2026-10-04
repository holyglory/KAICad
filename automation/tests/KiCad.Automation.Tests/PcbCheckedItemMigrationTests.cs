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
public sealed class PcbCheckedItemMigrationTests
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
            ProjectSettingsIncluded = true, DiskBaselineChecked = true,
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
        internal string Epoch { get; } = Guid.NewGuid().ToString("D");
        internal string InstanceId { get; } = Guid.NewGuid().ToString("D");
        internal bool LiveBoardChanged { get; set; }
        internal bool RejectUpdates { get; init; }
        internal int CheckedMutations { get; private set; }
        internal int LifecycleReads { get; private set; }
        internal int LegacyMutations { get; private set; }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] bytes, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var request = ApiRequest.Parser.ParseFrom(bytes);
            if (request.Message.Is(GetAutomationSession.Descriptor))
                return Reply(new AutomationSession { ProtocolVersion = 1, InstanceId = InstanceId,
                    ProjectPath = "/fixture/fixture.kicad_pro", Epoch = Epoch });
            if (request.Message.Is(ReadDocumentLifecycleState.Descriptor))
            {
                LifecycleReads++;
                return Reply(new DocumentLifecycleState { ProcessEpoch = Epoch });
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
            if (receipts.TryGetValue(checkedRequest.OperationId, out var existing))
            {
                var replay = existing.Clone(); replay.Replayed = true;
                return Reply(replay);
            }
            var receipt = checkedRequest.Operations[0].ActionCase switch
            {
                PcbItemOperation.ActionOneofCase.Create => CompletedCreate(checkedRequest),
                PcbItemOperation.ActionOneofCase.Update when RejectUpdates => RolledBackUpdate(checkedRequest),
                PcbItemOperation.ActionOneofCase.Update => CompletedUpdate(checkedRequest),
                PcbItemOperation.ActionOneofCase.Delete => CompletedDelete(checkedRequest),
                _ => throw new AssertFailedException("unsupported fixture operation")
            };
            receipts.Add(checkedRequest.OperationId, receipt.Clone());
            return Reply(receipt);
        }

        private CheckedPcbItemTransactionReceipt CompletedCreate(CheckedPcbItemTransaction request) =>
            Completed(request, new PcbItemOperationResult
            {
                Create = new CreateItemsResponse
                {
                    Status = ItemRequestStatus.IrsOk,
                    CreatedItems = { new ItemCreationResult { Status = new() { Code = ItemStatusCode.IscOk }, Item = request.Operations[0].Create.Items[0].Clone() } }
                }
            });

        private CheckedPcbItemTransactionReceipt CompletedUpdate(CheckedPcbItemTransaction request) =>
            Completed(request, new PcbItemOperationResult
            {
                Update = new UpdateItemsResponse
                {
                    Status = ItemRequestStatus.IrsOk,
                    UpdatedItems = { new ItemUpdateResult { Status = new() { Code = ItemStatusCode.IscOk }, Item = request.Operations[0].Update.Items[0].Clone() } }
                }
            });

        private CheckedPcbItemTransactionReceipt CompletedDelete(CheckedPcbItemTransaction request) =>
            Completed(request, new PcbItemOperationResult
            {
                Delete = new DeleteItemsResponse
                {
                    Status = ItemRequestStatus.IrsOk,
                    DeletedItems = { new ItemDeletionResult { Id = request.Operations[0].Delete.ItemIds[0].Clone(), Status = ItemDeletionStatus.IdsOk } }
                }
            });

        private static CheckedPcbItemTransactionReceipt Completed(CheckedPcbItemTransaction request,
            PcbItemOperationResult result)
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
            receipt.Results.Add(result);
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
