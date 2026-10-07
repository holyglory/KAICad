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
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbRoutingReceiptRecoveryTests
{
    [TestMethod]
    public async Task ReceiptReadsCompletedReplayFromNativeAfterNewMcpRegistry()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-completed-").FullName;
        try
        {
            var fixture = RouteReceiptFixture.Create(root);
            fixture.Transport.Receipt = fixture.CompletedReceipt(replayed: true);
            var registry = await fixture.Registry(root);
            var result = await new PcbRoutingTools(registry).Receipt(
                fixture.Transport.InstanceId, fixture.RequestJson, fixture.State.ProcessEpoch,
                fixture.Request.OperationId, default);

            JsonElement data = Success(result);
            Assert.AreEqual(fixture.Request.OperationId, data.GetProperty("operationId").GetString());
            Assert.AreEqual(fixture.State.ProcessEpoch, data.GetProperty("processEpoch").GetString());
            Assert.AreEqual(CheckedPcbRouteReceipt.Types.State.Completed.ToString(), data.GetProperty("state").GetString());
            Assert.IsTrue(data.GetProperty("replayed").GetBoolean());
            Assert.IsTrue(data.GetProperty("readOnly").GetBoolean());
            Assert.AreEqual(1, fixture.Transport.RouteReceiptReads);
            Assert.IsTrue(fixture.Transport.HandshakeSeen);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ReceiptAcceptsExplicitNativeNotFoundWithoutResubmitting()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-missing-").FullName;
        try
        {
            var fixture = RouteReceiptFixture.Create(root);
            fixture.Transport.Receipt = new CheckedPcbRouteReceipt
            {
                Document = fixture.Request.Candidate.Header.Document.Clone(),
                ProcessEpoch = fixture.State.ProcessEpoch,
                OperationId = fixture.Request.OperationId,
                State = CheckedPcbRouteReceipt.Types.State.NotFound
            };
            var registry = await fixture.Registry(root);
            JsonElement data = Success(await new PcbRoutingTools(registry).Receipt(
                fixture.Transport.InstanceId, fixture.RequestJson, fixture.State.ProcessEpoch,
                fixture.Request.OperationId, default));

            Assert.AreEqual(CheckedPcbRouteReceipt.Types.State.NotFound.ToString(), data.GetProperty("state").GetString());
            Assert.IsFalse(data.GetProperty("replayed").GetBoolean());
            Assert.AreEqual(1, fixture.Transport.RouteReceiptReads);
            Assert.AreEqual(0, fixture.Transport.RouteMutations);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ReceiptPreservesValidatedIndeterminateOutcome()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-indeterminate-").FullName;
        try
        {
            var fixture = RouteReceiptFixture.Create(root);
            fixture.Transport.Receipt = new CheckedPcbRouteReceipt
            {
                Document = fixture.Request.Candidate.Header.Document.Clone(),
                ProcessEpoch = fixture.State.ProcessEpoch,
                OperationId = fixture.Request.OperationId,
                State = CheckedPcbRouteReceipt.Types.State.Indeterminate,
                RequestVerified = true,
                ErrorCode = "commit_result_unknown",
                ErrorMessage = "native commit may have been applied",
                Before = fixture.State.Clone()
            };
            var registry = await fixture.Registry(root);
            JsonElement data = Success(await new PcbRoutingTools(registry).Receipt(
                fixture.Transport.InstanceId, fixture.RequestJson, fixture.State.ProcessEpoch,
                fixture.Request.OperationId, default));

            Assert.AreEqual(CheckedPcbRouteReceipt.Types.State.Indeterminate.ToString(), data.GetProperty("state").GetString());
            Assert.IsFalse(data.GetProperty("replayed").GetBoolean());
            Assert.AreEqual("commit_result_unknown", data.GetProperty("receipt").GetProperty("errorCode").GetString());
            Assert.AreEqual(0, fixture.Transport.RouteMutations);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ReceiptRejectsSeparateIdentityArgumentsBeforeNativeRead()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-identity-").FullName;
        try
        {
            var fixture = RouteReceiptFixture.Create(root);
            fixture.Transport.Receipt = fixture.CompletedReceipt(replayed: false);
            var registry = await fixture.Registry(root);

            AssertError(await new PcbRoutingTools(registry).Receipt(
                fixture.Transport.InstanceId, fixture.RequestJson, fixture.State.ProcessEpoch,
                Guid.NewGuid().ToString("D"), default), "route_receipt_failed");
            AssertError(await new PcbRoutingTools(registry).Receipt(
                fixture.Transport.InstanceId, fixture.RequestJson, Guid.NewGuid().ToString("D"),
                fixture.Request.OperationId, default), "route_receipt_failed");
            Assert.AreEqual(0, fixture.Transport.RouteReceiptReads);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ReceiptRejectsForeignOrMalformedNativeResult()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-receipt-invalid-").FullName;
        try
        {
            foreach (Func<RouteReceiptFixture, CheckedPcbRouteReceipt> make in new Func<RouteReceiptFixture, CheckedPcbRouteReceipt>[]
            {
                ForeignEpoch,
                ForeignOperation,
                MalformedState
            })
            {
                var fixture = RouteReceiptFixture.Create(root);
                fixture.Transport.Receipt = make(fixture);
                var registry = await fixture.Registry(root);
                AssertError(await new PcbRoutingTools(registry).Receipt(
                    fixture.Transport.InstanceId, fixture.RequestJson, fixture.State.ProcessEpoch,
                    fixture.Request.OperationId, default), "route_receipt_failed");
                Assert.AreEqual(1, fixture.Transport.RouteReceiptReads);
            }
        }
        finally { Directory.Delete(root, true); }
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

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "<no tool content>";

    private static CheckedPcbRouteReceipt ForeignEpoch(RouteReceiptFixture fixture)
    {
        var receipt = fixture.CompletedReceipt(replayed: false);
        receipt.ProcessEpoch = Guid.NewGuid().ToString("D");
        return receipt;
    }

    private static CheckedPcbRouteReceipt ForeignOperation(RouteReceiptFixture fixture)
    {
        var receipt = fixture.CompletedReceipt(replayed: false);
        receipt.OperationId = Guid.NewGuid().ToString("D");
        return receipt;
    }

    private static CheckedPcbRouteReceipt MalformedState(RouteReceiptFixture fixture)
    {
        var receipt = fixture.CompletedReceipt(replayed: false);
        receipt.State = CheckedPcbRouteReceipt.Types.State.Unknown;
        return receipt;
    }

    private sealed class RouteReceiptFixture
    {
        private RouteReceiptFixture(string root, RouteReceiptTransport transport,
            CheckedPcbRouteCommit request, DocumentLifecycleState state)
        {
            Root = root; Transport = transport; Request = request; State = state;
            RequestJson = SchematicJson.Formatter.Format(request);
        }

        internal string Root { get; }
        internal RouteReceiptTransport Transport { get; }
        internal CheckedPcbRouteCommit Request { get; }
        internal DocumentLifecycleState State { get; }
        internal string RequestJson { get; }

        internal static RouteReceiptFixture Create(string root)
        {
            var transport = new RouteReceiptTransport();
            string processEpoch = transport.Epoch;
            string nativeIdentity = Guid.NewGuid().ToString("D");
            var document = new DocumentSpecifier
            {
                Type = DocumentType.DoctypePcb,
                BoardFilename = "route.kicad_pcb",
                Project = new() { Name = "route", Path = root }
            };
            var state = new DocumentLifecycleState
            {
                Document = document.Clone(), ProcessEpoch = processEpoch, NativeIdentity = nativeIdentity,
                Scope = DocumentLifecycleScope.DlsPcb, ProjectSettingsIncluded = true, DiskBaselineChecked = true,
                StateSha256 = new string('a', 64), Revision = new() { Epoch = nativeIdentity, Sequence = 4 }
            };
            foreach (string path in new[] { Path.Combine(root, "route.kicad_pcb"), Path.Combine(root, "route.kicad_pro") })
            {
                state.NativeFiles.Add(path);
                state.FileBaselines.Add(new NativeFileBaselineState
                {
                    Path = path, BaselinePath = path, BaselineKnown = true, CurrentKnown = true,
                    Status = NativeFileBaselineStatus.NfbsUnchanged, BaselineExists = false, CurrentExists = false
                });
            }
            var track = new Track
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") },
                Start = new() { XNm = 1_000, YNm = 2_000 }, End = new() { XNm = 3_000, YNm = 2_000 },
                Width = new() { ValueNm = 200 }, Layer = BoardLayer.BlFCu, Net = new() { Name = "N1" }
            };
            var candidate = new CreateItems { Header = new() { Document = document }, Items = { Any.Pack(track) } };
            var request = new CheckedPcbRouteCommit
            {
                Candidate = candidate, ExpectedState = state.Clone(), DrcJobId = Guid.NewGuid().ToString("D"),
                OperationId = Guid.NewGuid().ToString("D"), MaximumResultBytes = 128 * 1024
            };
            return new(root, transport, request, state);
        }

        internal async Task<InstanceRegistry> Registry(string path)
        {
            var registry = new InstanceRegistry(Transport, path + "/mcp");
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(path + "/native.sock"), Transport.InstanceId);
            return registry;
        }

        internal CheckedPcbRouteReceipt CompletedReceipt(bool replayed)
        {
            var after = State.Clone(); after.Revision.Sequence++; after.StateSha256 = new string('b', 64);
            var created = new CreateItemsResponse { Header = new() { Document = State.Document }, Status = ItemRequestStatus.IrsOk };
            created.CreatedItems.Add(new ItemCreationResult
            {
                Status = new() { Code = ItemStatusCode.IscOk }, Item = Request.Candidate.Items[0].Clone()
            });
            return new CheckedPcbRouteReceipt
            {
                Document = State.Document.Clone(), ProcessEpoch = State.ProcessEpoch, OperationId = Request.OperationId,
                State = CheckedPcbRouteReceipt.Types.State.Completed, RequestVerified = true, Replayed = replayed,
                Before = State.Clone(), After = after, Created = created
            };
        }
    }

    private sealed class RouteReceiptTransport : INativeTransport
    {
        internal readonly string Epoch = Guid.NewGuid().ToString("D");
        internal readonly string InstanceId = Guid.NewGuid().ToString("D");
        internal CheckedPcbRouteReceipt Receipt { get; set; } = new();
        internal int RouteReceiptReads { get; private set; }
        internal int RouteMutations { get; private set; }
        internal bool HandshakeSeen { get; private set; }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] bytes, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var request = ApiRequest.Parser.ParseFrom(bytes);
            if (request.Message.Is(GetAutomationSession.Descriptor))
            {
                HandshakeSeen = true;
                return Reply(new AutomationSession { ProtocolVersion = 1, InstanceId = InstanceId,
                    ProjectPath = "/fixture/route.kicad_pro", Epoch = Epoch });
            }
            if (request.Message.Is(ReadCheckedPcbRouteReceipt.Descriptor))
            {
                RouteReceiptReads++;
                var query = request.Message.Unpack<ReadCheckedPcbRouteReceipt>();
                if (query.ProcessEpoch != Epoch || query.OperationId != Receipt.OperationId)
                    return Error("receipt query identity mismatch");
                return Reply(Receipt.Clone());
            }
            if (request.Message.Is(CheckedPcbRouteCommit.Descriptor))
            {
                RouteMutations++;
                return Reply(Receipt.Clone());
            }
            return Error("unexpected route receipt request");
        }

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
