using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Project;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class ProjectSettingsToolTests
{
    [TestMethod]
    public async Task NetClassesReturnsTypedSnapshotWithUnchangedLifecycleIdentity()
    {
        using var fixture = Fixture();
        var result = await fixture.Tool.NetClasses(fixture.Session.InstanceId,
            SchematicJson.Formatter.Format(fixture.Document),
            SchematicJson.Formatter.Format(fixture.State), default);

        Assert.IsFalse(result.IsError ?? false, Text(result));
        JsonElement data = result.StructuredContent!.Value;
        Assert.IsTrue(data.GetProperty("supported").GetBoolean(), Text(result));
        Assert.IsTrue(data.GetProperty("nativeQuery").GetBoolean(), Text(result));
        Assert.IsFalse(data.GetProperty("boardMutation").GetBoolean(), Text(result));
        Assert.AreEqual(fixture.Session.Epoch, data.GetProperty("processEpoch").GetString());
        Assert.AreEqual(2, data.GetProperty("response").GetProperty("netClasses").GetArrayLength());
        Assert.AreEqual(fixture.State, fixture.Transport.LastState);
    }

    [TestMethod]
    public async Task NetClassesRejectsLifecycleChangesAfterTheNativeQuery()
    {
        using var fixture = Fixture();
        fixture.Transport.ChangeAfterQuery = true;

        var result = await fixture.Tool.NetClasses(fixture.Session.InstanceId,
            SchematicJson.Formatter.Format(fixture.Document),
            SchematicJson.Formatter.Format(fixture.State), default);

        Assert.IsTrue(result.IsError ?? false, Text(result));
        Assert.AreEqual("project_settings_state_changed",
            result.StructuredContent!.Value.GetProperty("errorCode").GetString(), Text(result));
    }

    [TestMethod]
    public async Task NetClassesReportsUnsupportedNativeCommandWithoutInventingData()
    {
        using var fixture = Fixture();
        fixture.Transport.Unsupported = true;

        var result = await fixture.Tool.NetClasses(fixture.Session.InstanceId,
            SchematicJson.Formatter.Format(fixture.Document),
            SchematicJson.Formatter.Format(fixture.State), default);

        Assert.IsFalse(result.IsError ?? false, Text(result));
        JsonElement data = result.StructuredContent!.Value;
        Assert.IsFalse(data.GetProperty("supported").GetBoolean(), Text(result));
        Assert.IsFalse(data.GetProperty("nativeQuery").GetBoolean(), Text(result));
        Assert.AreEqual(JsonValueKind.Null, data.GetProperty("response").ValueKind);
        StringAssert.Contains(data.GetProperty("unsupportedReason").GetString()!, "does not provide");
    }

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Single().Text;

    private static Fixture Fixture()
    {
        string directory = Directory.CreateTempSubdirectory("project-netclasses-").FullName;
        var transport = new ProjectTransport();
        transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "fixture.kicad_pcb",
            Project = new() { Name = "fixture", Path = directory }
        };
        var state = new DocumentLifecycleState
        {
            Document = document.Clone(),
            Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 4 },
            NativeIdentity = Guid.NewGuid().ToString("D"),
            StateSha256 = new string('a', 64),
            Scope = DocumentLifecycleScope.DlsPcb,
            ProcessEpoch = transport.Session.Epoch,
            ProjectSettingsIncluded = true
        };
        transport.State = state.Clone();
        transport.NetClasses.NetClasses.Add(new NetClass { Name = "Default", Type = NetClassType.NctExplicit });
        transport.NetClasses.NetClasses.Add(new NetClass { Name = "HighSpeed", Type = NetClassType.NctExplicit });
        var registry = new InstanceRegistry(transport, directory);
        registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")),
            transport.Session.InstanceId).GetAwaiter().GetResult();
        return new Fixture(transport, registry, new ProjectSettingsTools(registry), document, state, directory);
    }

    private sealed record Fixture(ProjectTransport Transport, InstanceRegistry Registry, ProjectSettingsTools Tool,
        DocumentSpecifier Document, DocumentLifecycleState State, string Root) : IDisposable
    {
        public NativeClientTests.FixtureTransport Session => Transport.Session;
        public void Dispose()
        {
            System.IO.Directory.Delete(Root, true);
        }
    }

    private sealed class ProjectTransport : INativeTransport
    {
        public NativeClientTests.FixtureTransport Session { get; } = new() { Epoch = Guid.NewGuid().ToString("D") };
        public DocumentLifecycleState State { get; set; } = new();
        public DocumentLifecycleState? LastState { get; private set; }
        public NetClassesResponse NetClasses { get; } = new();
        public bool ChangeAfterQuery { get; set; }
        public bool Unsupported { get; set; }
        private int stateReads;

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ApiRequest envelope = ApiRequest.Parser.ParseFrom(request);
            if (envelope.Message.Is(ReadDocumentLifecycleState.Descriptor))
            {
                ++stateReads;
                DocumentLifecycleState observed = State.Clone();
                if (ChangeAfterQuery && stateReads > 1)
                    observed.Revision.Sequence++;
                LastState = observed.Clone();
                return Task.FromResult(Reply(observed));
            }
            if (envelope.Message.Is(GetNetClasses.Descriptor))
            {
                if (Unsupported)
                    return Task.FromResult(Reply(null, ApiStatusCode.AsUnhandled, "project netclasses are unavailable"));
                return Task.FromResult(Reply(NetClasses));
            }
            return Session.ExchangeAsync(endpoint, request, timeout, cancellationToken);
        }

        private byte[] Reply(IMessage? payload, ApiStatusCode status = ApiStatusCode.AsOk, string message = "")
        {
            var response = new ApiResponse
            {
                Header = new() { KicadToken = Session.Epoch },
                Status = new() { Status = status, ErrorMessage = message }
            };
            if (payload is not null)
                response.Message = Any.Pack(payload);
            return response.ToByteArray();
        }
    }
}
