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
        using var fixture = CreateFixture();
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
        using var fixture = CreateFixture();
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
        using var fixture = CreateFixture();
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

    [TestMethod]
    public async Task SetTextVariablesPreservesOwnerIdentityDigestAndRollbackTruth()
    {
        using var fixture = CreateFixture();
        fixture.Transport.TextVariables.Variables.Add("BASE", "before");
        Guid operationId = Guid.NewGuid();
        var result = await fixture.Tool.SetTextVariables(fixture.Session.InstanceId,
            new NativeProjectInput { Name = "fixture", Path = fixture.Root },
            NativeDocument(fixture.Root),
            SchematicJson.Formatter.Format(fixture.State),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["BASE"] = "after", ["NEW"] = "value" },
            "MMM_REPLACE", operationId, default);

        Assert.IsFalse(result.IsError ?? false, Text(result));
        JsonElement data = result.StructuredContent!.Value;
        Assert.AreEqual(operationId.ToString("D"), data.GetProperty("operationId").GetString());
        Assert.AreEqual(fixture.Session.Epoch, data.GetProperty("processEpoch").GetString());
        Assert.AreEqual("fixture", data.GetProperty("project").GetProperty("project").GetProperty("name").GetString());
        Assert.AreEqual(fixture.Document.Project.Path,
            data.GetProperty("document").GetProperty("project").GetProperty("path").GetString());
        Assert.IsTrue(data.GetProperty("committed").GetBoolean(), Text(result));
        Assert.IsFalse(data.GetProperty("replayed").GetBoolean(), Text(result));
        Assert.IsFalse(data.GetProperty("rollbackCompleted").GetBoolean(), Text(result));
        Assert.AreEqual(data.GetProperty("beforeVariablesSha256").GetString(),
            data.GetProperty("ownerDigest").GetString());
        Assert.AreEqual(data.GetProperty("beforeVariablesSha256").GetString(),
            data.GetProperty("expectedVariablesSha256").GetString());
        Assert.AreEqual(operationId.ToString("D"), fixture.Transport.LastMutationRequest!.OperationId);
        Assert.AreEqual(fixture.Transport.LastMutationRequest.ExpectedVariablesSha256,
            data.GetProperty("ownerDigest").GetString());
        Assert.AreEqual("value", data.GetProperty("afterVariables").GetProperty("NEW").GetString());
    }

    [TestMethod]
    public async Task SetTextVariablesRejectsReceiptWhoseMapDoesNotMatchOwnerDigest()
    {
        using var fixture = CreateFixture();
        fixture.Transport.TextVariables.Variables.Add("BASE", "before");
        fixture.Transport.TamperBeforeMap = true;

        var result = await fixture.Tool.SetTextVariables(fixture.Session.InstanceId,
            new NativeProjectInput { Name = "fixture", Path = fixture.Root },
            NativeDocument(fixture.Root),
            SchematicJson.Formatter.Format(fixture.State),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["BASE"] = "after" },
            "MMM_REPLACE", Guid.NewGuid(), default);

        Assert.IsTrue(result.IsError ?? false, Text(result));
        Assert.AreEqual("invalid_project_text_variables_response",
            result.StructuredContent!.Value.GetProperty("errorCode").GetString(), Text(result));
    }

    [TestMethod]
    public async Task SetTextVariablesRejectsReceiptThatClaimsCommitAndRollback()
    {
        using var fixture = CreateFixture();
        fixture.Transport.TextVariables.Variables.Add("BASE", "before");
        fixture.Transport.ClaimCommitAndRollback = true;

        var result = await fixture.Tool.SetTextVariables(fixture.Session.InstanceId,
            new NativeProjectInput { Name = "fixture", Path = fixture.Root },
            NativeDocument(fixture.Root),
            SchematicJson.Formatter.Format(fixture.State),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["BASE"] = "after" },
            "MMM_REPLACE", Guid.NewGuid(), default);

        Assert.IsTrue(result.IsError ?? false, Text(result));
        Assert.AreEqual("invalid_project_text_variables_response",
            result.StructuredContent!.Value.GetProperty("errorCode").GetString(), Text(result));
    }

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Single().Text;

    private static NativeDocumentInput NativeDocument(string projectPath) => new()
    {
        Type = NativeDocumentType.Pcb,
        Project = new NativeProjectInput { Name = "fixture", Path = projectPath },
        BoardFilename = "fixture.kicad_pcb"
    };

    private static Fixture CreateFixture()
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
        public Kiapi.Common.Project.TextVariables TextVariables { get; } = new();
        public SetTextVariablesChecked? LastMutationRequest { get; private set; }
        public bool ChangeAfterQuery { get; set; }
        public bool Unsupported { get; set; }
        public bool TamperBeforeMap { get; set; }
        public bool ClaimCommitAndRollback { get; set; }
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
            if (envelope.Message.Is(GetTextVariables.Descriptor))
                return Task.FromResult(Reply(TextVariables));
            if (envelope.Message.Is(SetTextVariablesChecked.Descriptor))
            {
                var mutationRequest = envelope.Message.Unpack<SetTextVariablesChecked>();
                LastMutationRequest = mutationRequest.Clone();
                var before = TextVariables.Clone();
                var after = mutationRequest.MergeMode == MapMergeMode.MmmReplace ? mutationRequest.Variables.Clone() : before.Clone();
                if (mutationRequest.MergeMode == MapMergeMode.MmmMerge)
                    foreach (var pair in mutationRequest.Variables.Variables)
                        after.Variables[pair.Key] = pair.Value;
                var beforeForResponse = before.Clone();
                if (TamperBeforeMap)
                {
                    beforeForResponse.Variables.Clear();
                    beforeForResponse.Variables["TAMPERED"] = "map";
                }
                var response = new SetTextVariablesCheckedResponse
                {
                    Project = mutationRequest.Project.Clone(), Document = mutationRequest.Document.Clone(),
                    OperationId = mutationRequest.OperationId, ProcessEpoch = mutationRequest.ProcessEpoch,
                    BeforeVariables = beforeForResponse,
                    AfterVariables = after,
                    BeforeVariablesSha256 = mutationRequest.ExpectedVariablesSha256,
                    AfterVariablesSha256 = DigestForTest(after.Variables),
                    Committed = true, Replayed = false, RollbackCompleted = ClaimCommitAndRollback
                };
                TextVariables.Variables.Clear();
                foreach (var pair in after.Variables)
                    TextVariables.Variables[pair.Key] = pair.Value;
                return Task.FromResult(Reply(response));
            }
            return Session.ExchangeAsync(endpoint, request, timeout, cancellationToken);
        }

        private static string DigestForTest(IReadOnlyDictionary<string, string> variables)
        {
            var bytes = new List<byte>();
            foreach (var pair in variables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                byte[] key = System.Text.Encoding.UTF8.GetBytes(pair.Key);
                byte[] value = System.Text.Encoding.UTF8.GetBytes(pair.Value);
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(key.Length.ToString())); bytes.Add((byte)'\n'); bytes.AddRange(key);
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(value.Length.ToString())); bytes.Add((byte)'\n'); bytes.AddRange(value);
            }
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.ToArray()));
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
