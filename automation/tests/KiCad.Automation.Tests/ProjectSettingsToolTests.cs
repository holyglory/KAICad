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
    public async Task SetNetClassesRefusesPcbOnlyWithoutCheckedProjectTransaction()
    {
        using var fixture = CreateFixture();

        // The common SetNetClasses command is a legacy project-global setter.  A PCB
        // lifecycle observation cannot be used as its revision/receipt precondition,
        // so both wrappers must refuse before contacting the native peer until a
        // checked project-settings transaction exists.
        int exchanges = fixture.Transport.ExchangeCount;
        var legacy = await fixture.Tool.SetNetClasses(fixture.Session.InstanceId,
            SchematicJson.Formatter.Format(fixture.Document), "{}",
            SchematicJson.Formatter.Format(fixture.Transport.NetClasses), "MMM_MERGE",
            Guid.NewGuid(), new string('a', 64), default);

        var typed = await fixture.Tool.SetNetClassesStructured(fixture.Session.InstanceId, "{}",
            "MMM_MERGE", Guid.NewGuid(), new string('a', 64), default,
            document: NativeDocument(fixture.Root), netClasses: new() { NetClasses = [] });

        foreach (CallToolResult result in new[] { legacy, typed })
        {
            Assert.IsTrue(result.IsError ?? false, Text(result));
            JsonElement data = result.StructuredContent!.Value;
            Assert.AreEqual("project_net_classes_schematic_required",
                data.GetProperty("errorCode").GetString(), Text(result));
            StringAssert.Contains(data.GetProperty("errorMessage").GetString()!,
                "PCB-only project settings have no checked mutation path", Text(result));
        }

        Assert.AreEqual(exchanges, fixture.Transport.ExchangeCount,
            "PCB-only refusal must happen before handshake, lifecycle reads, or a legacy native setter.");
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

    [TestMethod]
    public async Task SetTextVariablesMapsNativeStaleDigestAndPreservesOtherBadRequests()
    {
        using var fixture = CreateFixture();
        fixture.Transport.TextVariables.Variables.Add("BASE", "before");
        fixture.Transport.TextVariablesNativeErrorMessage =
            "project_text_variables_stale: expected digest no longer matches the project";

        var stale = await fixture.Tool.SetTextVariables(fixture.Session.InstanceId,
            new NativeProjectInput { Name = "fixture", Path = fixture.Root },
            NativeDocument(fixture.Root), SchematicJson.Formatter.Format(fixture.State),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["BASE"] = "after" },
            "MMM_REPLACE", Guid.NewGuid(), default);

        Assert.IsTrue(stale.IsError ?? false, Text(stale));
        Assert.AreEqual("project_text_variables_stale",
            stale.StructuredContent!.Value.GetProperty("errorCode").GetString(), Text(stale));
        StringAssert.Contains(stale.StructuredContent.Value.GetProperty("errorMessage").GetString()!,
            "project_text_variables_stale:");
        Assert.IsNull(fixture.Transport.LastMutationRequest);

        fixture.Transport.TextVariablesNativeErrorMessage = "a different bad request";
        var other = await fixture.Tool.SetTextVariables(fixture.Session.InstanceId,
            new NativeProjectInput { Name = "fixture", Path = fixture.Root },
            NativeDocument(fixture.Root), SchematicJson.Formatter.Format(fixture.State),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["BASE"] = "after" },
            "MMM_REPLACE", Guid.NewGuid(), default);

        Assert.IsTrue(other.IsError ?? false, Text(other));
        Assert.AreEqual("native_status_3",
            other.StructuredContent!.Value.GetProperty("errorCode").GetString(), Text(other));
    }

    [TestMethod]
    public async Task StructuredNetClassesReadMatchesLegacyAndChecksTheCompleteLifecycle()
    {
        using var fixture = CreateFixture();
        var legacy = await fixture.Tool.NetClasses(fixture.Session.InstanceId,
            SchematicJson.Formatter.Format(fixture.Document), SchematicJson.Formatter.Format(fixture.State), default);
        var structured = await fixture.Tool.NetClassesStructured(fixture.Session.InstanceId, default,
            document: NativeDocument(fixture.Root), expectedState: TypedState(fixture));
        Assert.IsFalse(structured.IsError ?? false, Text(structured));
        Assert.AreEqual(Text(legacy), Text(structured));
        Assert.AreEqual(fixture.State, fixture.Transport.LastState);

        fixture.Transport.ChangeAfterQuery = true;
        var stale = await fixture.Tool.NetClassesStructured(fixture.Session.InstanceId, default,
            document: NativeDocument(fixture.Root), expectedState: TypedState(fixture));
        Assert.IsTrue(stale.IsError ?? false, Text(stale));
        Assert.AreEqual("project_settings_state_changed", stale.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }

    [TestMethod]
    public async Task StructuredTextVariablesRetainNativeIdentityDigestAndRequestedValues()
    {
        using var fixture = CreateFixture();
        Guid operation = Guid.NewGuid();
        var result = await fixture.Tool.SetTextVariablesStructured(fixture.Session.InstanceId,
            new NativeProjectInput { Name = "fixture", Path = fixture.Root }, NativeDocument(fixture.Root),
            new Dictionary<string, string> { ["PROJECT"] = "typed value", ["EMPTY"] = "" },
            "MMM_REPLACE", operation, default, expectedState: TypedState(fixture));
        Assert.IsFalse(result.IsError ?? false, Text(result));
        SetTextVariablesChecked native = fixture.Transport.LastMutationRequest!;
        Assert.AreEqual(fixture.Document, native.Document);
        Assert.AreEqual(fixture.Session.Epoch, native.ProcessEpoch);
        Assert.AreEqual(operation.ToString("D"), native.OperationId);
        Assert.AreEqual(MapMergeMode.MmmReplace, native.MergeMode);
        Assert.AreEqual("typed value", native.Variables.Variables["PROJECT"]);
        Assert.AreEqual("", native.Variables.Variables["EMPTY"]);
        Assert.AreEqual(native.ExpectedVariablesSha256, result.StructuredContent!.Value.GetProperty("ownerDigest").GetString());

        fixture.Transport.TamperBeforeMap = true;
        var forged = await fixture.Tool.SetTextVariablesStructured(fixture.Session.InstanceId,
            new NativeProjectInput { Name = "fixture", Path = fixture.Root }, NativeDocument(fixture.Root),
            new Dictionary<string, string> { ["PROJECT"] = "later" }, "MMM_REPLACE", Guid.NewGuid(), default,
            expectedState: TypedState(fixture));
        Assert.IsTrue(forged.IsError ?? false, Text(forged));
        Assert.AreEqual("invalid_project_text_variables_response", forged.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }

    [TestMethod]
    public async Task StructuredProjectSettingsRejectConflictsBeforeAnyNativeContact()
    {
        using var fixture = CreateFixture();
        int exchanges = fixture.Transport.ExchangeCount;
        var documentConflict = await fixture.Tool.NetClassesStructured(fixture.Session.InstanceId, default,
            documentJson: "{}", document: NativeDocument(fixture.Root), expectedState: TypedState(fixture));
        var stateConflict = await fixture.Tool.TextVariablesStructured(fixture.Session.InstanceId,
            new NativeProjectInput { Name = "fixture", Path = fixture.Root }, NativeDocument(fixture.Root), default,
            expectedStateJson: "", expectedState: TypedState(fixture));
        var requestConflict = await fixture.Tool.SetNetClassesStructured(fixture.Session.InstanceId,
            "{}", "MMM_REPLACE", Guid.NewGuid(), new string('a', 64), default,
            document: NativeDocument(fixture.Root), netClassesJson: "{}", netClasses: new() { NetClasses = [] });
        foreach (CallToolResult result in new[] { documentConflict, stateConflict, requestConflict })
        {
            Assert.IsTrue(result.IsError ?? false, Text(result));
            Assert.AreEqual("invalid_project_settings_input", result.StructuredContent!.Value.GetProperty("errorCode").GetString());
        }
        Assert.AreEqual(exchanges, fixture.Transport.ExchangeCount);
        Assert.IsNull(fixture.Transport.LastMutationRequest);
    }

    [TestMethod]
    public void StructuredNetClassesPreserveAllNativeSettingsAndPresence()
    {
        ProjectNetClassesInput input = JsonSerializer.Deserialize<ProjectNetClassesInput>("""
            {"netClasses":[
              {"name":"Default","priority":0,"type":"NCT_EXPLICIT","constituents":[],
               "board":{"clearanceNm":0,"trackWidthNm":200000,"diffPairTrackWidthNm":150000,
                 "diffPairGapNm":180000,"diffPairViaGapNm":220000,
                 "viaStack":{"diameterNm":600000,"drillDiameterNm":300000},
                 "microviaStack":{"diameterNm":200000,"drillDiameterNm":100000},
                 "color":{"r":0.1,"g":0.2,"b":0.3,"a":1},"tuningProfile":"fast"},
               "schematic":{"wireWidthNm":50000,"busWidthNm":100000,
                 "color":{"r":0.5,"g":0.6,"b":0.7,"a":0.8},"lineStyle":"SLS_DASHDOT"}},
              {"name":"Inherited"}
            ]}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        NetClassesResponse expected = SchematicJson.Parser.Parse<NetClassesResponse>("""
            {"netClasses":[
              {"name":"Default","priority":0,"type":"NCT_EXPLICIT",
               "board":{"clearance":{"valueNm":"0"},"trackWidth":{"valueNm":"200000"},
                 "diffPairTrackWidth":{"valueNm":"150000"},"diffPairGap":{"valueNm":"180000"},
                 "diffPairViaGap":{"valueNm":"220000"},
                 "viaStack":{"copperLayers":[{"layer":"BL_F_Cu","shape":"PSS_CIRCLE","size":{"xNm":"600000","yNm":"600000"}}],"drill":{"diameter":{"xNm":"300000","yNm":"300000"}}},
                 "microviaStack":{"copperLayers":[{"layer":"BL_F_Cu","shape":"PSS_CIRCLE","size":{"xNm":"200000","yNm":"200000"}}],"drill":{"diameter":{"xNm":"100000","yNm":"100000"}}},
                 "color":{"r":0.1,"g":0.2,"b":0.3,"a":1},"tuningProfile":"fast"},
               "schematic":{"wireWidth":{"valueNm":"50000"},"busWidth":{"valueNm":"100000"},
                 "color":{"r":0.5,"g":0.6,"b":0.7,"a":0.8},"lineStyle":"SLS_DASHDOT"}},
              {"name":"Inherited","type":"NCT_EXPLICIT"}
            ]}
            """);
        NetClassesResponse actual = ProjectNetClassesInput.ToNative(input);
        CollectionAssert.AreEqual(expected.ToByteArray(), actual.ToByteArray());
        Assert.IsTrue(actual.NetClasses[0].HasPriority);
        Assert.IsFalse(actual.NetClasses[1].HasPriority);
        Assert.IsNull(actual.NetClasses[1].Board);
        Assert.IsNull(actual.NetClasses[1].Schematic);
        Assert.AreEqual(0, ProjectNetClassesInput.ToNative(new() { NetClasses = [] }).NetClasses.Count);
    }

    [TestMethod]
    public void StructuredNetClassesRefuseMalformedOrUnsupportedValuesWithoutLosingCollections()
    {
        string[] malformed =
        [
            "{\"netClasses\":[{\"name\":\"A\"},{\"name\":\"A\"}]}",
            "{\"netClasses\":[null]}",
            "{\"netClasses\":[{\"name\":\"A\",\"type\":\"NCT_IMPLICIT\"}]}",
            "{\"netClasses\":[{\"name\":\"A\",\"constituents\":[\"Default\"]}]}",
            "{\"netClasses\":[{\"name\":\"A\",\"board\":{\"trackWidthNm\":2147483648}}]}",
            "{\"netClasses\":[{\"name\":\"A\",\"board\":{\"clearanceNm\":-1}}]}",
            "{\"netClasses\":[{\"name\":\"A\",\"board\":{\"viaStack\":{\"diameterNm\":10,\"drillDiameterNm\":11}}}]}",
            "{\"netClasses\":[{\"name\":\"A\",\"board\":{\"color\":{\"r\":2}}}]}",
            "{\"netClasses\":[{\"name\":\"A\",\"schematic\":{\"lineStyle\":\"SLS_UNKNOWN\"}}]}"
        ];
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (string json in malformed)
        {
            var input = JsonSerializer.Deserialize<ProjectNetClassesInput>(json, options)!;
            var error = Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => ProjectNetClassesInput.ToNative(input), json);
            Assert.AreEqual("invalid_project_net_classes_request", error.Code);
        }
        foreach (string json in new[]
        {
            "{\"netClasses\":[{\"name\":\"A\",\"board\":{\"unknown\":1}}]}",
            "{\"netClasses\":[{\"name\":\"A\",\"name\":\"B\"}]}",
            "{\"netClasses\":[],\"netClasses\":[]}"
        })
            Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<ProjectNetClassesInput>(json, options), json);
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => ProjectNetClassesInput.ToNative(new()
        {
            NetClasses = [new() { Name = "Nonfinite", Board = new() { Color = new() { R = double.NaN } } }]
        }));
    }

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Single().Text;

    private static NativeDocumentInput NativeDocument(string projectPath) => new()
    {
        Type = NativeDocumentType.Pcb,
        Project = new NativeProjectInput { Name = "fixture", Path = projectPath },
        BoardFilename = "fixture.kicad_pcb"
    };

    private static DocumentLifecycleInput TypedState(Fixture fixture) =>
        JsonSerializer.Deserialize<DocumentLifecycleInput>(SchematicJson.Formatter.Format(fixture.State),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

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
        string file = Path.Combine(directory, "fixture.kicad_pcb");
        state.NativeFiles.Add(file);
        state.FileBaselines.Add(new NativeFileBaselineState { Path = file, BaselinePath = file });
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
        public string? TextVariablesNativeErrorMessage { get; set; }
        public int ExchangeCount { get; private set; }
        private int stateReads;

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ++ExchangeCount;
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
                if (TextVariablesNativeErrorMessage is { } nativeError)
                    return Task.FromResult(Reply(null, ApiStatusCode.AsBadRequest, nativeError));
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
