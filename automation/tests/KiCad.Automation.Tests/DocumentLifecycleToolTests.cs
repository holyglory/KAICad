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

    // Timeouts need KiCad to stay silent for the full 15 s native deadline per case, so the
    // mapping from the transport's delivery evidence to the tool error is checked here in
    // isolation; the native journey proves the cancelled, never-received case end to end.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransportFailuresSayWhetherKiCadReceivedTheRequest(bool delivered)
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-transport-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new DocumentLifecycleTools(registry);
            var state = State(directory, transport.Session.Epoch);
            transport.Failure = new NngException(5, "Timed out", requestDelivered: delivered);
            foreach (bool close in new[] { false, true })
            {
                string id = Guid.NewGuid().ToString("D");
                var response = close
                    ? await tool.Close(transport.Session.InstanceId, SchematicJson.Formatter.Format(state), id, default)
                    : await tool.Save(transport.Session.InstanceId, SchematicJson.Formatter.Format(state), id, default);
                Assert.IsTrue(response.IsError, "A transport failure is never a lifecycle success.");
                var error = response.StructuredContent!.Value;
                string message = error.GetProperty("message").GetString()!;
                Assert.AreEqual(delivered ? "operation_outcome_unknown" : "native_not_reached", error.GetProperty("code").GetString(), message);
                StringAssert.Contains(message, id, "The error names the operation to query or repeat.");
                StringAssert.Contains(message, delivered ? "kicad_document_operation" : "never received it");
                StringAssert.Contains(message, "same operation ID");
                if (!delivered && !close) StringAssert.Contains(message, "nothing was saved");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task UnknownOperationExplainsThatKiCadNeverStartedIt()
    {
        string directory = Directory.CreateTempSubdirectory("lifecycle-unknown-").FullName;
        try
        {
            var transport = new LifecycleTransport();
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new DocumentLifecycleTools(registry);
            var state = State(directory, transport.Session.Epoch);
            string id = Guid.NewGuid().ToString("D");
            // The native refusal starts with a fixed marker; the explanation after it may change freely.
            transport.ReceiptError = DocumentLifecycleTools.NativeUnknownOperation + ": any explanation";
            var unknown = await tool.Operation(transport.Session.InstanceId, SchematicJson.Formatter.Format(state.Document), id, state.ProcessEpoch, default);
            Assert.IsTrue(unknown.IsError);
            Assert.AreEqual("operation_not_started", unknown.StructuredContent!.Value.GetProperty("code").GetString());
            string message = unknown.StructuredContent!.Value.GetProperty("message").GetString()!;
            StringAssert.Contains(message, id);
            StringAssert.Contains(message, "has no record of starting");
            StringAssert.Contains(message, "saved or closed nothing");
            // Any other native refusal keeps its own status, including prose that merely resembles the marker.
            foreach (string other in new[] { "Lifecycle operation belongs to another document",
                         "Lifecycle operation is not known in this process", DocumentLifecycleTools.NativeUnknownOperation + "_later" })
            {
                transport.ReceiptError = other;
                var refused = await tool.Operation(transport.Session.InstanceId, SchematicJson.Formatter.Format(state.Document), id, state.ProcessEpoch, default);
                Assert.AreEqual("native_status_3", refused.StructuredContent!.Value.GetProperty("code").GetString(), other);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static DocumentLifecycleState State(string directory, string epoch) => new()
    {
        Document = new() { Type = (DocumentType)3, BoardFilename = "fixture.kicad_pcb",
            Project = new() { Name = "fixture", Path = directory } },
        ProcessEpoch = epoch, NativeIdentity = Guid.NewGuid().ToString("D"),
        Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 5 }, StateSha256 = new string('a', 64)
    };

    private sealed class LifecycleTransport : INativeTransport
    {
        public NativeClientTests.FixtureTransport Session { get; } = new();
        public int LifecycleCalls { get; private set; }
        public LifecycleOperationStatus Status { get; set; } = LifecycleOperationStatus.LosSaved;
        public bool WrongTarget { get; set; }
        public NngException? Failure { get; set; }
        public string? ReceiptError { get; set; }
        private LifecycleOperationResult? result;
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var envelope = ApiRequest.Parser.ParseFrom(request);
            if (Failure is not null && (envelope.Message.Is(CheckedSaveDocument.Descriptor) || envelope.Message.Is(CheckedCloseDocument.Descriptor)))
                return Task.FromException<byte[]>(Failure);
            if (ReceiptError is not null && envelope.Message.Is(ReadLifecycleOperation.Descriptor))
                return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                    Status = new() { Status = (ApiStatusCode)3, ErrorMessage = ReceiptError } }.ToByteArray());
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
