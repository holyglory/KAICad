using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Tests;

// The capability catalogue is derived from what is registered: the MCP server's tool collection and
// the native handshake. These checks prove the derivation over the compiled STDIO server; the native
// halves run in the Foundation journey (NativeStartupRecoveryJourney, McpReattachmentJourney).
[TestClass]
public sealed class CapabilityCatalogTests
{
    public TestContext TestContext { get; set; } = null!;

    // Lane 2D tool types: each of their tools declares its capability and its verification.
    private static readonly System.Type[] DeclaredToolTypes =
        [typeof(InstanceTools), typeof(InstanceUpdateTools), typeof(DocumentLifecycleTools), typeof(DocumentStateTools), typeof(CheckedSchematicTools)];

    [TestMethod]
    public async Task CompiledServerAdvertisesExactlyItsRegisteredTools()
    {
        string root = Directory.CreateTempSubdirectory("kicad-capability-catalog-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            JsonElement structured;
            await using (var mcp = await StdioMcpFixture.StartAsync(Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), deadline.Token))
            {
                var listed = await CapabilityCatalogAssertions.ListToolsAsync(mcp);
                var result = await mcp.Tool("kicad_service_capabilities", new { });
                Assert.IsFalse(result.TryGetProperty("isError", out var failed) && failed.GetBoolean(), result.GetRawText());
                structured = result.GetProperty("structuredContent");
                var names = CapabilityCatalogAssertions.VerifyServiceCatalogue(structured, listed);

                // The hand-kept transitional rows may only describe tools that are really registered.
                foreach (var row in ServiceCapabilities.Registered)
                    CollectionAssert.Contains(names, row.Name, "A transitional capability row names a tool this server does not register.");
                var entries = structured.GetProperty("serviceCapabilities").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!);
                foreach (string name in DeclaredToolNames())
                {
                    Assert.AreEqual("attribute", entries[name].GetProperty("declaration").GetString(), name);
                    Assert.AreNotEqual("undeclared", entries[name].GetProperty("verification").GetProperty("level").GetString(), name);
                }
                // A tool with no declaration is listed as registered without invented metadata.
                var undeclared = entries.Values.Where(e => e.GetProperty("declaration").GetString() == "undeclared").ToList();
                Assert.IsTrue(undeclared.All(e => e.GetProperty("scope").ValueKind == JsonValueKind.Null
                    && e.GetProperty("source").ValueKind == JsonValueKind.Null && e.GetProperty("revisionContract").ValueKind == JsonValueKind.Null));

                // An unknown instance is refused instead of returning a service-only catalogue as if it were live.
                var unknown = await mcp.Tool("kicad_instance_capabilities", new { instanceId = Guid.NewGuid().ToString("D") });
                Assert.IsTrue(unknown.GetProperty("isError").GetBoolean());
                Assert.AreEqual("unknown_instance", unknown.GetProperty("structuredContent").GetProperty("code").GetString());
            }
            string retained = Path.Combine(TestContext.TestResultsDirectory!, "service-capabilities.json");
            await File.WriteAllTextAsync(retained, JsonSerializer.Serialize(structured, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
            TestContext.AddResultFile(retained);
        }
        finally
        {
            string log = Path.Combine(root, "stderr.log");
            if (File.Exists(log))
            {
                string retained = Path.Combine(TestContext.TestResultsDirectory!, "service-capabilities-mcp.log");
                File.Copy(log, retained, true); TestContext.AddResultFile(retained);
            }
            Directory.Delete(root, true);
        }
    }

    // Isolated metadata check: evidence names are test-assembly identifiers, which no end-to-end
    // path can resolve. It keeps the published verification tied to tests that exist and call the
    // tool where the claim says they do (VerificationEvidenceRules).
    [TestMethod]
    public void DeclaredVerificationNamesExistingTestsThatUseTheTool()
    {
        string directory = Path.Combine(AutomationRoot(), "tests", "KiCad.Automation.Tests");
        var sources = Directory.EnumerateFiles(directory, "*.cs").Order(StringComparer.Ordinal)
            .Select(file => new VerificationEvidenceRules.Source(Path.GetFileName(file), File.ReadAllText(file))).ToArray();
        var problems = new List<string>();
        var declared = new Dictionary<string, KiCadCapabilityAttribute>(StringComparer.Ordinal);
        int checkedClaims = 0;
        foreach (var method in typeof(InstanceTools).Assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is not { Name: { } name }) continue;
            var capability = method.GetCustomAttribute<KiCadCapabilityAttribute>();
            var verification = method.GetCustomAttribute<KiCadVerificationAttribute>();
            if (capability is not null)
            {
                declared.Add(name, capability);
                if (new[] { capability.Scope, capability.Source, capability.RevisionContract }.Any(string.IsNullOrWhiteSpace))
                    problems.Add($"{name} declares an empty capability field.");
            }
            if (DeclaredToolTypes.Contains(method.DeclaringType) && (capability is null || verification is null))
                problems.Add($"{name} is a lane 2D tool without both [KiCadCapability] and [KiCadVerification].");
            if (verification is null) continue;
            if (capability is null) problems.Add($"{name} declares verification without its capability.");
            problems.AddRange(VerificationEvidenceRules.Check(name, verification.Level, verification.Evidence, sources, IsTestMethod));
            checkedClaims++;
        }
        Assert.IsGreaterThanOrEqualTo(DeclaredToolNames().Length, checkedClaims, "Every lane 2D tool claim must be checked.");
        var rows = ServiceCapabilities.Registered;
        foreach (var duplicate in rows.GroupBy(r => r.Name).Where(g => g.Count() > 1))
            problems.Add($"Transitional capability row {duplicate.Key} is repeated.");
        foreach (var row in rows.Where(r => declared.ContainsKey(r.Name)))
        {
            var attribute = declared[row.Name];
            if ((attribute.Scope, attribute.Source, attribute.RevisionContract) != (row.Scope, row.Source, row.RevisionContract))
                problems.Add($"{row.Name}: its [KiCadCapability] and transitional row disagree; the row must be removed or match.");
        }
        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));

        static bool IsTestMethod(string type, string method) =>
            typeof(CapabilityCatalogTests).Assembly.GetType("KiCad.Automation.Tests." + type)?
                .GetMethods(BindingFlags.Public | BindingFlags.Instance).SingleOrDefault(m => m.Name == method)?
                .GetCustomAttributes<TestMethodAttribute>(inherit: true).Any() == true;
    }

    // Isolated detector check: must-catch and must-not-flag cases for the rules above, over
    // synthetic sources, because the real test tree has no failing claim to observe. The native
    // class name is interpolated so this file is never read as a native journey source.
    [TestMethod]
    public void VerificationRulesOnlyCountCallsWhereTheClaimSaysTheyHappen()
    {
        const string native = VerificationEvidenceRules.NativeJourneyClass;
        VerificationEvidenceRules.Source[] sources =
        [
            new("SampleNativeJourney.cs", $$"""
                public sealed partial class {{native}}
                {
                    async Task Journey()
                    {
                        await Call("sample_attach", new { });
                        await mcp!.Tool("sample_close", new { });
                        var opened = await Open(endpoint, openTool: "sample_open");
                    }
                }
                """),
            new("SampleStdioTests.cs", """
                public sealed class SampleStdioTests
                {
                    public async Task Run()
                    {
                        await using var mcp = await StdioMcpFixture.StartAsync(state, log, token);
                        CollectionAssert.Contains(names, "sample_listed");
                        string[] required = ["sample_listed", "sample_other", "sample_third"];
                        var listed = await Request(3, "tools/call", new { name = "sample_list", arguments = new { } });
                        var inspected = await mcp.Tool("sample_inspect", new { });
                    }
                }
                """),
            new("SampleUnitTests.cs", """
                public sealed class SampleUnitTests
                {
                    public void Run() => tools.Tool("sample_unit", new { });
                }
                """)
        ];
        string[] tests = [native + ".Foundation", "SampleStdioTests.Run", "SampleUnitTests.Run"];
        IReadOnlyList<string> Check(string tool, KiCadVerificationLevel level, params string[] evidence) =>
            VerificationEvidenceRules.Check(tool, level, evidence, sources, (type, method) => tests.Contains(type + "." + method));
        string foundation = native + ".Foundation";

        // Must not flag: real calls where the claim puts them.
        Assert.IsEmpty(Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, foundation));
        Assert.IsEmpty(Check("sample_open", KiCadVerificationLevel.McpNativeJourney, foundation), "A named tool argument of a journey helper is a call site.");
        Assert.IsEmpty(Check("sample_close", KiCadVerificationLevel.NativeJourney, foundation));
        Assert.IsEmpty(Check("sample_list", KiCadVerificationLevel.McpProcess, "SampleStdioTests.Run"));
        Assert.IsEmpty(Check("sample_inspect", KiCadVerificationLevel.McpProcess, "SampleStdioTests.Run"));
        Assert.IsEmpty(Check("sample_unit", KiCadVerificationLevel.InProcess, "SampleUnitTests.Run"));

        // Must catch: an STDIO-only call backing a native claim (the removed-journey case).
        StringAssert.Contains(Check("sample_list", KiCadVerificationLevel.McpNativeJourney, foundation, "SampleStdioTests.Run").Single(), "no NativeSessionTests journey calls it");
        StringAssert.Contains(Check("sample_inspect", KiCadVerificationLevel.McpNativeJourney, foundation, "SampleStdioTests.Run").Single(), "no NativeSessionTests journey calls it");
        // A native claim that cites no native journey.
        Assert.IsTrue(Check("sample_list", KiCadVerificationLevel.McpNativeJourney, "SampleStdioTests.Run").Any(p => p.Contains("without citing")));
        Assert.IsTrue(Check("sample_close", KiCadVerificationLevel.NativeJourney, "SampleUnitTests.Run").Any(p => p.Contains("without citing")));
        // A name that is only listed or asserted is not a call.
        StringAssert.Contains(Check("sample_listed", KiCadVerificationLevel.McpProcess, "SampleStdioTests.Run")[0], "never calls sample_listed");
        StringAssert.Contains(Check("sample_other", KiCadVerificationLevel.McpProcess, "SampleStdioTests.Run")[0], "never calls sample_other");
        // A cited class that does not start the compiled server, or never calls the tool.
        StringAssert.Contains(Check("sample_unit", KiCadVerificationLevel.McpProcess, "SampleUnitTests.Run")[0], "does not start the compiled MCP STDIO server");
        StringAssert.Contains(Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, foundation, "SampleStdioTests.Run").Single(), "never calls sample_attach");
        // A name that is not a test, and a claim with no evidence.
        StringAssert.Contains(Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, foundation, "Missing.Test").Single(), "not a test method");
        StringAssert.Contains(Check("sample_attach", KiCadVerificationLevel.McpNativeJourney).Single(), "without evidence");
    }

    // Isolated format rule: older or newer native peers cannot be produced by this build, so the
    // fail-closed mapping of their handshake is checked directly.
    [TestMethod]
    public void OnlyTheRegisteredRequestFormatIsReportedAsHandlers()
    {
        var registered = CapabilityCatalog.Native(new AutomationSession
        {
            CapabilityFormat = NativeCapabilityFormat.NcpRegisteredRequestTypes,
            Capabilities = { "kiapi.automation.v1.GetAutomationSession", "kiapi.common.commands.GetVersion" }
        });
        Assert.AreEqual("registered-request-types", registered.Format);
        Assert.IsTrue(registered.Requests.All(r => r.Availability == "handler-registered"));
        var legacy = CapabilityCatalog.Native(new AutomationSession { Capabilities = { "session.info", "version.read" } });
        Assert.AreEqual("legacy-labels", legacy.Format);
        CollectionAssert.AreEqual(new[] { "legacy-label", "legacy-label" }, legacy.Requests.Select(r => r.Availability).ToArray());
        var future = CapabilityCatalog.Native(new AutomationSession { CapabilityFormat = (NativeCapabilityFormat)7, Capabilities = { "x" } });
        Assert.AreEqual("unrecognized", future.Format);
        Assert.AreEqual("unrecognized", future.Requests.Single().Availability);
    }

    private static string[] DeclaredToolNames() => DeclaredToolTypes
        .SelectMany(t => t.GetMethods()).Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name).OfType<string>().ToArray();

    private static string AutomationRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KiCad.Automation.slnx"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Run this source test from the automation checkout.");
    }
}

internal static class CapabilityCatalogAssertions
{
    internal static async Task<IReadOnlyList<JsonElement>> ListToolsAsync(IMcpToolClient mcp)
    {
        var tools = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await mcp.ListTools(cursor);
            tools.AddRange(page.GetProperty("tools").EnumerateArray().Select(t => t.Clone()));
            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        } while (cursor is not null);
        return tools;
    }

    // The service half of a catalogue lists exactly the tools/list catalogue, once each, in ordinal
    // order, with the listed read-only hint; limitations only point at listed tools of their scope.
    internal static string[] VerifyServiceCatalogue(JsonElement structured, IReadOnlyList<JsonElement> listed)
    {
        Assert.AreEqual(CapabilityCatalog.SchemaVersion, structured.GetProperty("schemaVersion").GetInt32());
        string[] listedNames = listed.Select(t => t.GetProperty("name").GetString()!).ToArray();
        var entries = structured.GetProperty("serviceCapabilities").EnumerateArray().ToArray();
        string[] names = entries.Select(e => e.GetProperty("name").GetString()!).ToArray();
        CollectionAssert.AreEqual(listedNames.Order(StringComparer.Ordinal).ToArray(), names,
            "The service catalogue must list exactly the registered tools, once each, in ordinal order.");
        Assert.AreEqual(listedNames.Length, listedNames.Distinct(StringComparer.Ordinal).Count());
        var hints = listed.ToDictionary(t => t.GetProperty("name").GetString()!, t =>
            t.TryGetProperty("annotations", out var a) && a.TryGetProperty("readOnlyHint", out var h) ? h.GetBoolean() : (bool?)null);
        string[] levels = ["mcp-native-journey", "native-journey", "mcp-process", "in-process", "undeclared"];
        foreach (var entry in entries)
        {
            string name = entry.GetProperty("name").GetString()!;
            Assert.AreEqual("registered", entry.GetProperty("availability").GetString(), name);
            CollectionAssert.Contains(new[] { "attribute", "legacy-service-list", "undeclared" }, entry.GetProperty("declaration").GetString(), name);
            var readOnly = entry.GetProperty("readOnly");
            Assert.AreEqual(hints[name], readOnly.ValueKind == JsonValueKind.Null ? (bool?)null : readOnly.GetBoolean(), name);
            var verification = entry.GetProperty("verification");
            string level = verification.GetProperty("level").GetString()!;
            CollectionAssert.Contains(levels, level, name);
            Assert.AreEqual(level == "undeclared", verification.GetProperty("evidence").GetArrayLength() == 0, name);
        }
        var limitations = structured.GetProperty("limitations").EnumerateArray().ToArray();
        Assert.AreEqual(limitations.Length, limitations.Select(l => l.GetProperty("id").GetString()).Distinct().Count());
        foreach (var limitation in limitations)
        {
            string scope = limitation.GetProperty("scope").GetString()!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(limitation.GetProperty("summary").GetString()));
            string[] expected = entries.Where(e => e.GetProperty("scope").ValueKind == JsonValueKind.String && e.GetProperty("scope").GetString() == scope)
                .Select(e => e.GetProperty("name").GetString()!).ToArray();
            CollectionAssert.AreEqual(expected, limitation.GetProperty("registeredToolsInScope").EnumerateArray().Select(n => n.GetString()!).ToArray(), scope);
            // Each limitation cites the ledger outcomes (p + 16 hex digits) or decision refs that track it.
            string[] tracking = limitation.GetProperty("trackedBy").EnumerateArray().Select(n => n.GetString()!).ToArray();
            Assert.IsNotEmpty(tracking, scope);
            Assert.IsTrue(tracking.All(reference => Regex.IsMatch(reference, "^(p[0-9a-f]{16}|[a-z0-9]+(-[a-z0-9]+)+)$")), string.Join(", ", tracking));
        }
        Assert.IsGreaterThan(0, structured.GetProperty("notes").GetArrayLength());
        return names;
    }
}

/// <summary>
/// Ties a tool's declared verification to the test sources of the classes it cites. A call is the
/// tool's name passed to an MCP client (Tool, Call, CallToolAsync), sent as a raw tools/call request,
/// or given to a journey helper as a named tool argument; a listed or asserted name is not a call.
/// NativeSessionTests is the Linux native session fixture: its partial sources are the journeys that
/// drive a real KiCad. The check is per class, not per method.
/// </summary>
internal static class VerificationEvidenceRules
{
    internal const string NativeJourneyClass = "NativeSessionTests";

    internal sealed record Source(string File, string Text);

    private static readonly Regex ClassDeclaration = new(
        @"^[ \t]*(?:(?:public|internal|private|protected|sealed|static|abstract|partial|file)\s+)*class\s+(\w+)", RegexOptions.Multiline);
    private static readonly Regex StartsCompiledServer = new(@"StdioMcpFixture\.StartAsync\(|""kicad-mcp\.dll""");

    internal static IReadOnlyList<string> Check(string tool, KiCadVerificationLevel level, IReadOnlyList<string> evidence,
        IReadOnlyList<Source> sources, Func<string, string, bool> isTestMethod)
    {
        var problems = new List<string>();
        if (evidence.Count == 0) return [$"{tool} declares verification without evidence."];
        var classes = sources.SelectMany(source => ClassDeclaration.Matches(source.Text).Select(match => (Name: match.Groups[1].Value, Source: source)))
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Source).Distinct().ToArray(), StringComparer.Ordinal);
        string name = Regex.Escape(tool);
        var call = new Regex($@"(?:\bTool|\bCall|\bCallToolAsync)\(\s*""{name}""|""tools/call""\s*,\s*new\s*\{{\s*name\s*=\s*""{name}""|\b\w*[Tt]ool\w*\s*[:=]\s*""{name}""");
        bool citesNative = false, stdioCall = false;
        foreach (string item in evidence)
        {
            string[] parts = item.Split('.');
            if (parts.Length != 2 || !isTestMethod(parts[0], parts[1]))
            {
                problems.Add($"{tool} cites {item}, which is not a test method in KiCad.Automation.Tests.");
                continue;
            }
            if (parts[0] == NativeJourneyClass) { citesNative = true; continue; }
            if (level == KiCadVerificationLevel.InProcess) continue;
            var files = classes.GetValueOrDefault(parts[0]) ?? [];
            if (!files.Any(file => StartsCompiledServer.IsMatch(file.Text)))
                problems.Add($"{tool} cites {item}, whose class does not start the compiled MCP STDIO server.");
            else if (!files.Any(file => call.IsMatch(file.Text)))
                problems.Add($"{tool} cites {item}, whose class never calls {tool}.");
            else stdioCall = true;
        }
        string claim = CapabilityCatalog.Level(level);
        if (level is KiCadVerificationLevel.McpNativeJourney or KiCadVerificationLevel.NativeJourney && !citesNative)
            problems.Add($"{tool} claims {claim} without citing a {NativeJourneyClass} journey.");
        if (level == KiCadVerificationLevel.McpNativeJourney
            && !(classes.GetValueOrDefault(NativeJourneyClass) ?? []).Any(file => call.IsMatch(file.Text)))
            problems.Add($"{tool} claims {claim}, but no {NativeJourneyClass} journey calls it through MCP.");
        if (level == KiCadVerificationLevel.McpProcess && !stdioCall)
            problems.Add($"{tool} claims {claim}, but no cited compiled STDIO test calls it.");
        return problems;
    }
}

/// <summary>
/// Proves that a native handshake advertises exactly the request types its process dispatches. Every
/// message type the protocol defines, plus anything advertised, is sent with a payload that cannot be
/// decoded: a registered handler rejects it before running anything, and an unregistered type comes
/// back unhandled. Nothing is executed and no document changes.
/// </summary>
internal static class NativeCapabilityProbe
{
    private const int AsOk = 1, AsBadRequest = 3, AsUnhandled = 5;
    // A truncated varint: protobuf parsing fails for every message type.
    private static readonly ByteString Undecodable = ByteString.CopyFrom(new byte[] { 0x08, 0x80 });

    internal static IReadOnlyList<string> KnownMessageTypes() => typeof(ApiRequest).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMessage).IsAssignableFrom(t))
        .Select(t => t.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)?.GetValue(null))
        .OfType<MessageDescriptor>().Select(d => d.FullName).Distinct().Order(StringComparer.Ordinal).ToArray();

    internal static async Task<string[]> VerifyHandshakeAsync(NativeClient client, string? evidenceFile, CancellationToken token)
    {
        var known = KnownMessageTypes();
        Assert.IsGreaterThan(300, known.Count, "The protocol assembly must describe every KiCad API message.");
        for (int attempt = 1; ; attempt++)
        {
            // A stable snapshot: the same list before and after probing, so no editor opened or closed meanwhile.
            var before = await client.HandshakeAsync(token);
            Assert.AreEqual(NativeCapabilityFormat.NcpRegisteredRequestTypes, before.CapabilityFormat);
            string[] advertised = before.Capabilities.ToArray();
            CollectionAssert.AreEqual(advertised.Distinct().Order(StringComparer.Ordinal).ToArray(), advertised,
                "The handshake lists each request type once, in ordinal order.");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var statuses = await Task.Run(() =>
            {
                // One connection for the whole pass keeps it to one editor round trip per type.
                using var socket = new ProbeSocket(client.Endpoint);
                var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (string type in known.Union(advertised))
                {
                    token.ThrowIfCancellationRequested();
                    var request = new ApiRequest
                    {
                        Header = new ApiRequestHeader { KicadToken = client.Epoch, ClientName = "kicad-capability-probe" },
                        Message = new Any { TypeUrl = "type.googleapis.com/" + type, Value = Undecodable }
                    };
                    var response = ApiResponse.Parser.ParseFrom(socket.Exchange(request.ToByteArray()));
                    Assert.AreEqual(client.Epoch, response.Header?.KicadToken, "The probed native process changed.");
                    result[type] = (int)(response.Status?.Status ?? 0);
                }
                return result;
            }, token);
            Console.WriteLine($"Probed {statuses.Count} request types of {client.Endpoint} in {elapsed.Elapsed.TotalSeconds:F2}s.");
            var after = await client.HandshakeAsync(token);
            if (!after.Capabilities.SequenceEqual(advertised))
            {
                Assert.IsLessThan(3, attempt, "The native request handlers kept changing while they were probed.");
                continue;
            }
            string[] handled = statuses.Where(s => s.Value != AsUnhandled).Select(s => s.Key).ToArray();
            string[] unadvertised = handled.Except(advertised).ToArray(), unhandled = advertised.Except(handled).ToArray();
            string[] executed = statuses.Where(s => s.Value != AsUnhandled
                && s.Value != (s.Key == GetAutomationSession.Descriptor.FullName ? AsOk : AsBadRequest)).Select(s => $"{s.Key}={s.Value}").ToArray();
            if (evidenceFile is not null)
                await File.WriteAllTextAsync(evidenceFile, JsonSerializer.Serialize(new
                {
                    epoch = client.Epoch, format = before.CapabilityFormat.ToString(), advertised, probed = statuses.Count,
                    probeSeconds = elapsed.Elapsed.TotalSeconds,
                    statusCounts = statuses.GroupBy(s => s.Value).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                    unadvertised, unhandled, executed
                }, new JsonSerializerOptions { WriteIndented = true }), token);
            Assert.IsEmpty(unadvertised, "Handled but not advertised: " + string.Join(", ", unadvertised));
            Assert.IsEmpty(unhandled, "Advertised but not handled: " + string.Join(", ", unhandled));
            Assert.IsEmpty(executed, "An undecodable request must be rejected before it runs: " + string.Join(", ", executed));
            return advertised;
        }
    }

    // A test-owned REQ connection to the explicit endpoint; KiCad replies to each request in turn.
    private sealed class ProbeSocket : IDisposable
    {
        private readonly Nng.Socket socket;

        public ProbeSocket(string endpoint)
        {
            NngTransport.ValidateEndpoint(endpoint);
            Nng.Check(Nng.nng_req0_open(out socket));
            try
            {
                Nng.Check(Nng.nng_setopt_ms(socket, "send-timeout", 15000));
                Nng.Check(Nng.nng_setopt_ms(socket, "recv-timeout", 15000));
                Nng.Check(Nng.nng_dial(socket, endpoint, IntPtr.Zero, 2));
            }
            catch { _ = Nng.nng_close(socket); throw; }
        }

        public byte[] Exchange(byte[] request)
        {
            Nng.Check(Nng.nng_send(socket, request, (nuint)request.Length, 0));
            nuint length = 0;
            Nng.Check(Nng.nng_recv(socket, out IntPtr data, ref length, 1));
            try
            {
                byte[] reply = new byte[checked((int)length)];
                System.Runtime.InteropServices.Marshal.Copy(data, reply, 0, reply.Length);
                return reply;
            }
            finally { Nng.nng_free(data, length); }
        }

        public void Dispose() => _ = Nng.nng_close(socket);
    }
}
