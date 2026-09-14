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
public sealed class PcbDrcToolTests
{
    [TestMethod]
    public async Task JobControlsRoundTripStructuredStateAndRejectStaleEpoch()
    {
        string directory = Directory.CreateTempSubdirectory("drc-job-contract-").FullName;
        try
        {
            var transport = new JobTransport();
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new PcbDrcTools(registry);
            var document = new DocumentSpecifier { Type = (DocumentType)3, BoardFilename = "fixture.kicad_pcb",
                Project = new() { Name = "fixture", Path = directory } };
            transport.State = new PcbDrcJobState
            {
                Document = document.Clone(),
                JobId = Guid.NewGuid().ToString("D"),
                OperationId = Guid.NewGuid().ToString("D"),
                ProcessEpoch = transport.Session.Epoch,
                CheckedRevision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 8 },
                Status = (PcbDrcJobStatus)3,
                Progress = 1,
                ResultsFresh = true, WorkerFinished = true, SnapshotComplete = true
            };
            string json = SchematicJson.Formatter.Format(document);
            string revisionJson = SchematicJson.Formatter.Format(transport.State.CheckedRevision);
            var started = await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, false, revisionJson, transport.Session.Epoch, default);
            Assert.IsFalse(started.IsError ?? false, started.ToString());
            Assert.IsTrue(started.StructuredContent.HasValue);
            Assert.AreEqual(transport.State,
                SchematicJson.Parser.Parse<PcbDrcJobState>(((TextContentBlock)started.Content.Single()).Text));
            var read = await tool.Job(transport.Session.InstanceId, json, transport.State.JobId,
                transport.Session.Epoch, default);
            Assert.IsFalse(read.IsError ?? false, read.ToString());
            var cancelled = await tool.Cancel(transport.Session.InstanceId, json, transport.State.JobId,
                transport.Session.Epoch, default);
            Assert.IsFalse(cancelled.IsError ?? false, cancelled.ToString());
            Assert.AreEqual(3, transport.Calls);
            var stale = await tool.Job(transport.Session.InstanceId, json, transport.State.JobId,
                Guid.NewGuid().ToString("D"), default);
            Assert.IsTrue(stale.IsError ?? false);
            Assert.AreEqual(3, transport.Calls);
            var badOperation = await tool.Start(transport.Session.InstanceId, json,
                Guid.Empty.ToString("D"), false, false, false, revisionJson, transport.Session.Epoch, default);
            Assert.IsTrue(badOperation.IsError ?? false);
            Assert.AreEqual(3, transport.Calls);
            var valid = transport.State.Clone();
            for (int variant = 0; variant < 7; ++variant)
            {
                transport.State = valid.Clone();
                switch (variant)
                {
                    case 0: transport.State.JobId = Guid.NewGuid().ToString("D"); break;
                    case 1: transport.State.WorkerFinished = false; break;
                    case 2: transport.State.SnapshotComplete = false; break;
                    case 3: transport.State.Progress = 0; break;
                    case 4: transport.State.Status = (PcbDrcJobStatus)4; break;
                    case 5: transport.State.OperationId = Guid.Empty.ToString("D"); break;
                    case 6: transport.State.CheckedRevision.Epoch = Guid.Empty.ToString("D"); break;
                }
                Assert.IsTrue((await tool.Job(transport.Session.InstanceId, json, valid.JobId,
                    transport.Session.Epoch, default)).IsError, $"Invalid native state variant {variant} was accepted");
            }
            transport.State = valid.Clone();
            transport.State.Status = (PcbDrcJobStatus)2;
            transport.State.WorkerFinished = false; transport.State.Progress = 0.2;
            transport.State.ResultsFresh = false; transport.State.CancellationRequested = true;
            Assert.IsFalse((await tool.Cancel(transport.Session.InstanceId, json, valid.JobId,
                transport.Session.Epoch, default)).IsError ?? false, "Cancellation acknowledgement may still be running.");
            transport.State.Status = (PcbDrcJobStatus)4; transport.State.WorkerFinished = true;
            Assert.IsFalse((await tool.Job(transport.Session.InstanceId, json, valid.JobId,
                transport.Session.Epoch, default)).IsError ?? false);
            transport.State = valid.Clone();
            var schematic = new DocumentLifecycleState
            {
                Document = new() { Type = (DocumentType)1, Project = document.Project.Clone(), SheetPath = new() },
                ProcessEpoch = transport.Session.Epoch,
                NativeIdentity = Guid.NewGuid().ToString("D"),
                Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 17 },
                StateSha256 = new string('a', 64), Scope = (DocumentLifecycleScope)1,
                ProjectSettingsIncluded = true
            };
            schematic.Document.SheetPath.Path.Add(new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") });
            transport.State.CheckedSchematicState = schematic.Clone();
            string schematicJson = SchematicJson.Formatter.Format(schematic);
            var parity = await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, true, revisionJson, transport.Session.Epoch, default, schematicJson, true);
            Assert.IsFalse(parity.IsError ?? false, parity.ToString());
            var transmittedSchematic = transport.LastStart!.ExpectedSchematicState;
            Assert.AreEqual(schematic, transmittedSchematic);
            Assert.IsTrue(transport.LastStart.AllowDuplicateSheetNames);
            int beforeInvalid = transport.Calls;
            Assert.IsTrue((await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, true, revisionJson, transport.Session.Epoch, default)).IsError ?? false);
            Assert.IsTrue((await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, false, revisionJson, transport.Session.Epoch, default, schematicJson)).IsError ?? false);
            var wrongSource = schematic.Clone(); wrongSource.ProcessEpoch = Guid.NewGuid().ToString("D");
            Assert.IsTrue((await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, true, revisionJson, transport.Session.Epoch, default,
                SchematicJson.Formatter.Format(wrongSource))).IsError ?? false);
            Assert.AreEqual(beforeInvalid, transport.Calls);
            transport.State.CheckedSchematicState.StateSha256 = new string('b', 64);
            Assert.IsTrue((await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, true, revisionJson, transport.Session.Epoch, default, schematicJson)).IsError ?? false,
                "A different checked schematic cannot be accepted as this operation's result.");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task MarkerInventoryPreservesExclusionsAndRejectsIncompleteOrWrongTargets()
    {
        string directory = Directory.CreateTempSubdirectory("drc-marker-contract-").FullName;
        try
        {
            var transport = new MarkerTransport();
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new PcbDrcTools(registry);
            var document = new DocumentSpecifier { Type = (DocumentType)3, BoardFilename = "fixture.kicad_pcb",
                Project = new() { Name = "fixture", Path = directory } };
            var state = new PcbDrcState { Document = document.Clone(), ProcessEpoch = transport.Session.Epoch,
                Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 4 }, MarkerSnapshotComplete = true };
            state.Findings.Add(new PcbDrcFinding { NativeId = Guid.NewGuid().ToString("D"),
                Marker = new() { ErrorType = (Kiapi.Board.DrcErrorType)1 }, Excluded = true, Comment = "explicit fixture exclusion" });
            transport.State = state;
            string json = SchematicJson.Formatter.Format(document);
            var result = await tool.Read(transport.Session.InstanceId, json, default);
            Assert.IsFalse(result.IsError ?? false);
            Assert.AreEqual(state, SchematicJson.Parser.Parse<PcbDrcState>(((TextContentBlock)result.Content.Single()).Text));
            Assert.IsFalse(state.ResultsFreshnessKnown);
            foreach (int variant in Enumerable.Range(0, 5))
            {
                transport.State = state.Clone();
                switch (variant)
                {
                    case 0: transport.State.Document.BoardFilename = "other.kicad_pcb"; break;
                    case 1: transport.State.MarkerSnapshotComplete = false; break;
                    case 2: transport.State.Running = true; break;
                    case 3: transport.State.Findings[0].NativeId = "bad"; break;
                    case 4: transport.State.Findings.Add(transport.State.Findings[0].Clone()); break;
                }
                Assert.IsTrue((await tool.Read(transport.Session.InstanceId, json, default)).IsError);
            }
            transport.State = state.Clone(); transport.State.Running = true;
            transport.State.MarkerSnapshotComplete = false; transport.State.Findings.Clear();
            Assert.IsFalse((await tool.Read(transport.Session.InstanceId, json, default)).IsError ?? false);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            int calls = transport.Calls;
            await Assert.ThrowsAsync<OperationCanceledException>(() => tool.Read(transport.Session.InstanceId, json, cancelled.Token));
            Assert.AreEqual(calls, transport.Calls);
            Assert.IsTrue((await tool.Read(transport.Session.InstanceId, "{", default)).IsError);
            Assert.AreEqual(calls, transport.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class MarkerTransport : INativeTransport
    {
        public NativeClientTests.FixtureTransport Session { get; } = new();
        public PcbDrcState State { get; set; } = new();
        public int Calls { get; private set; }
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (!ApiRequest.Parser.ParseFrom(request).Message.Is(ReadPcbDrcState.Descriptor))
                return Session.ExchangeAsync(endpoint, request, timeout, cancellationToken);
            ++Calls;
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(State) }.ToByteArray());
        }
    }

    private sealed class JobTransport : INativeTransport
    {
        public NativeClientTests.FixtureTransport Session { get; } = new();
        public PcbDrcJobState State { get; set; } = new();
        public int Calls { get; private set; }
        public StartPcbDrcJob? LastStart { get; private set; }
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var message = ApiRequest.Parser.ParseFrom(request).Message;
            if (!message.Is(StartPcbDrcJob.Descriptor) && !message.Is(ReadPcbDrcJob.Descriptor)
                && !message.Is(CancelPcbDrcJob.Descriptor))
                return Session.ExchangeAsync(endpoint, request, timeout, cancellationToken);
            ++Calls;
            if (message.Is(StartPcbDrcJob.Descriptor)) LastStart = message.Unpack<StartPcbDrcJob>();
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(State) }.ToByteArray());
        }
    }
}
