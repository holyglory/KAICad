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
}
