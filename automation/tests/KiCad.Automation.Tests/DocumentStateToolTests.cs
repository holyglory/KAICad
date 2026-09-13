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
public sealed class DocumentStateToolTests
{
    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content.Single()).Text;

    [TestMethod]
    public async Task InvalidDescriptorsAndCancellationDoNotReachNativeTransport()
    {
        string directory = Directory.CreateTempSubdirectory("document-state-input-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport();
            var tool = new DocumentStateTools(new InstanceRegistry(transport, directory));
            var valid = Target(directory);
            foreach (string input in new[] { "{}", "not-json", SchematicJson.Formatter.Format(new DocumentSpecifier
                { Type = (DocumentType)1, Project = valid.Project.Clone() }),
                SchematicJson.Formatter.Format(new DocumentSpecifier
                { Type = (DocumentType)3, Project = valid.Project.Clone(), BoardFilename = "../other.kicad_pcb" }) })
                Assert.IsTrue((await tool.Read("unattached", input, default)).IsError);
            Assert.IsNull(transport.LastRequest);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => tool.Read("unattached",
                SchematicJson.Formatter.Format(valid), cancelled.Token));
            Assert.IsNull(transport.LastRequest);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task MatchingStatePreservesCoverageAndRejectsMalformedNativeReplies()
    {
        string directory = Directory.CreateTempSubdirectory("document-state-reply-").FullName;
        try
        {
            var target = Target(directory);
            var state = new DocumentLifecycleState { Document = target.Clone(), Scope = DocumentLifecycleScope.DlsPcb,
                NativeIdentity = Guid.NewGuid().ToString("D"), Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 3 },
                StateSha256 = new string('a', 64), ProjectSettingsIncluded = true };
            state.NativeFiles.Add(Path.Combine(directory, "fixture.kicad_pcb"));
            state.FileBaselines.Add(new NativeFileBaselineState
            {
                Path = state.NativeFiles[0], BaselinePath = state.NativeFiles[0],
                BaselineKnown = true, BaselineExists = true, BaselineSha256 = new string('b', 64), BaselineBytes = 42,
                CurrentKnown = true, CurrentExists = true, CurrentSha256 = new string('c', 64), CurrentBytes = 43,
                Status = NativeFileBaselineStatus.NfbsChanged
            });
            var transport = new StateTransport { State = state.Clone() };
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "state.sock")), transport.Session.InstanceId);
            var tool = new DocumentStateTools(registry);
            string json = SchematicJson.Formatter.Format(target);
            var success = await tool.Read(transport.Session.InstanceId, json, default);
            Assert.IsFalse(success.IsError ?? false);
            Assert.AreEqual(state, SchematicJson.Parser.Parse<DocumentLifecycleState>(Text(success)));
            foreach (int kind in Enumerable.Range(0, 5))
            {
                transport.State = state.Clone();
                switch (kind)
                {
                    case 0: transport.State.Document.BoardFilename = "other.kicad_pcb"; break;
                    case 1: transport.State.StateSha256 = "bad"; break;
                    case 2: transport.State.Scope = DocumentLifecycleScope.DlsSchematicHierarchy; break;
                    case 3: transport.State.NativeIdentity = "bad"; break;
                    case 4: transport.State.Revision = null; break;
                }
                Assert.IsTrue((await tool.Read(transport.Session.InstanceId, json, default)).IsError);
            }
            transport.State = state.Clone();
            Assert.IsFalse((await tool.Read(transport.Session.InstanceId, json, default)).IsError ?? false);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static DocumentSpecifier Target(string directory) => new()
    { Type = (DocumentType)3, BoardFilename = "fixture.kicad_pcb", Project = new() { Name = "fixture", Path = directory } };

    private sealed class StateTransport : INativeTransport
    {
        public NativeClientTests.FixtureTransport Session { get; } = new();
        public DocumentLifecycleState State { get; set; } = new();
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (!ApiRequest.Parser.ParseFrom(request).Message.Is(ReadDocumentLifecycleState.Descriptor))
                return Session.ExchangeAsync(endpoint, request, timeout, cancellationToken);
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(State) }.ToByteArray());
        }
    }
}
