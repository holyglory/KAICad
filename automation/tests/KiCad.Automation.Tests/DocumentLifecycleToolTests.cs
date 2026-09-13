using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DocumentLifecycleToolTests
{
    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content.Single()).Text;

    [TestMethod]
    public async Task InvalidInputAndCancellationDoNotDispatchSave()
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-input-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            var tool = new DocumentLifecycleTools(registry);
            foreach (string json in new[] { "{", "{}", "null" })
                Assert.IsTrue((await tool.Save("missing", json, Guid.NewGuid().ToString("D"), default)).IsError);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => tool.Save("missing", "{}", "bad", cancelled.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() => tool.Close("missing", "{}", "bad", cancelled.Token));
            Assert.IsTrue((await tool.Close("missing", "{}", Guid.NewGuid().ToString("D"), default)).IsError);
            Assert.AreEqual(0, transport.LifecycleCalls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task NativeResultsRetainIdentityFailureStatusAndRecoveryQueries()
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-results-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new DocumentLifecycleTools(registry);
            var state = new DocumentLifecycleState
            {
                Document = new() { Type = (DocumentType)3, BoardFilename = "fixture.kicad_pcb",
                    Project = new() { Name = "fixture", Path = directory } },
                ProcessEpoch = transport.Session.Epoch, NativeIdentity = Guid.NewGuid().ToString("D"),
                Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 5 }, StateSha256 = new string('a', 64)
            };
            foreach (var status in new[] { LifecycleOperationStatus.LosSaved, LifecycleOperationStatus.LosRejected,
                         LifecycleOperationStatus.LosFailed, LifecycleOperationStatus.LosIndeterminate })
            {
                string id = Guid.NewGuid().ToString("D");
                transport.Status = status;
                var response = await tool.Save(transport.Session.InstanceId, SchematicJson.Formatter.Format(state), id, default);
                Assert.AreEqual(status != LifecycleOperationStatus.LosSaved, response.IsError ?? false);
                var result = SchematicJson.Parser.Parse<LifecycleOperationResult>(Text(response));
                Assert.AreEqual(status, result.Status);
                Assert.AreEqual(id, result.OperationId);
                Assert.IsNotNull(response.StructuredContent);
                var observed = await tool.Operation(transport.Session.InstanceId, SchematicJson.Formatter.Format(state.Document),
                    id, state.ProcessEpoch, default);
                Assert.IsFalse(observed.IsError ?? false); // Reading a failed receipt itself succeeds.
                Assert.AreEqual(result, SchematicJson.Parser.Parse<LifecycleOperationResult>(Text(observed)));
            }
            int calls = transport.LifecycleCalls;
            var wrongEpoch = state.Clone(); wrongEpoch.ProcessEpoch = Guid.NewGuid().ToString("D");
            Assert.IsTrue((await tool.Save(transport.Session.InstanceId, SchematicJson.Formatter.Format(wrongEpoch),
                Guid.NewGuid().ToString("D"), default)).IsError);
            Assert.AreEqual(calls, transport.LifecycleCalls);
            transport.WrongTarget = true;
            Assert.IsTrue((await tool.Save(transport.Session.InstanceId, SchematicJson.Formatter.Format(state),
                Guid.NewGuid().ToString("D"), default)).IsError);
            transport.WrongTarget = false;
            transport.Status = LifecycleOperationStatus.LosClosed;
            var closed = await tool.Close(transport.Session.InstanceId, SchematicJson.Formatter.Format(state),
                Guid.NewGuid().ToString("D"), default);
            Assert.IsFalse(closed.IsError ?? false);
            Assert.AreEqual(LifecycleOperationStatus.LosClosed, SchematicJson.Parser.Parse<LifecycleOperationResult>(Text(closed)).Status);
            transport.Status = LifecycleOperationStatus.LosSaved;
            Assert.IsTrue((await tool.Close(transport.Session.InstanceId, SchematicJson.Formatter.Format(state),
                Guid.NewGuid().ToString("D"), default)).IsError, "A save result cannot be reported as a successful close.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class LifecycleTransport : INativeTransport
    {
        public NativeClientTests.FixtureTransport Session { get; } = new();
        public int LifecycleCalls { get; private set; }
        public LifecycleOperationStatus Status { get; set; } = LifecycleOperationStatus.LosSaved;
        public bool WrongTarget { get; set; }
        private LifecycleOperationResult? result;
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var envelope = ApiRequest.Parser.ParseFrom(request);
            if (envelope.Message.Is(CheckedSaveDocument.Descriptor))
            {
                ++LifecycleCalls;
                var save = envelope.Message.Unpack<CheckedSaveDocument>();
                result = new() { Document = save.Document.Clone(), OperationId = save.OperationId,
                    ProcessEpoch = Session.Epoch, Status = Status, ErrorCode = Status == LifecycleOperationStatus.LosSaved ? "" : "fixture_failure" };
                if (WrongTarget) result.Document.BoardFilename = "other.kicad_pcb";
            }
            else if (envelope.Message.Is(ReadLifecycleOperation.Descriptor)) ++LifecycleCalls;
            else if (envelope.Message.Is(CheckedCloseDocument.Descriptor))
            {
                ++LifecycleCalls;
                var close = envelope.Message.Unpack<CheckedCloseDocument>();
                result = new() { Document = close.Document.Clone(), OperationId = close.OperationId,
                    ProcessEpoch = Session.Epoch, Status = Status };
                if (WrongTarget) result.Document.BoardFilename = "other.kicad_pcb";
            }
            else return Session.ExchangeAsync(endpoint, request, timeout, cancellationToken);
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(result!) }.ToByteArray());
        }
    }
}
