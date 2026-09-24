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
        var checkedNames = new List<string>();
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
            checkedNames.Add(name);
        }
        CollectionAssert.IsSubsetOf(DeclaredToolNames(), checkedNames, "Every lane 2D tool claim must be checked.");
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
                    [TestMethod]
                    public Task Foundation() => RunNativeSessions(NativeJourney.Foundation);

                    [TestMethod]
                    public Task Stubbed() => RunNativeSessions(NativeJourney.Stubbed);

                    [TestMethod]
                    public Task Defaulted(string theme) => RunNativeSessions(NativeJourney.Defaulted, theme);

                    [TestMethod]
                    public Task Delivered() => RunNativeSessions(NativeJourney.Delivered);

                    [TestMethod]
                    public async Task BlockBodied()
                    {
                        await RunNativeSessions(NativeJourney.Foundation);
                    }

                    [TestMethod]
                    public Task Direct(bool restore) => VerifyDirect(restore);

                    async Task Journey()
                    {
                        await Call("sample_attach", new { });
                        await mcp!.Tool("sample_close", new { });
                        var opened = await Open(endpoint, openTool: "sample_open");
                        string endpoint = "ipc:///tmp/sample.sock"; await Call("sample_after_url", new { });
                        // await Call("sample_commented", new { });
                        /* await mcp!.Tool("sample_block_commented", new { }); */
                        string toolName = "sample_local";
                    }

                    private async Task VerifyDirect(bool restore) => await Call("sample_direct", new { });

                    private static Task Create(string endpoint, CancellationToken token,
                        string toolName = "sample_default") => Task.CompletedTask;

                    private static async Task RunLaneJourney(NativeJourney journey)
                    {
                        var seed = journey switch
                        {
                            NativeJourney.Stubbed or NativeJourney.Delivered or NativeJourney.Defaulted => Seed.Sheets,
                            _ => throw new ArgumentOutOfRangeException(nameof(journey))
                        };
                        await (journey switch
                        {
                            NativeJourney.Stubbed => VerifyStubbed(seed),
                            NativeJourney.Delivered => VerifyDelivered(seed),
                            _ => VerifyDefaulted(seed)
                        });
                    }

                    // Lane 2X replaces this body when it delivers the journey.
                    private static Task VerifyStubbed(Seed seed)
                        => throw new AssertInconclusiveException("Phase 2 lane 2X has not delivered this journey");

                    private static Task VerifyDefaulted(Seed seed)
                        => throw new AssertInconclusiveException("Phase 2 lane 2X has not delivered this journey");

                    private static async Task VerifyDelivered(Seed seed) => await Call("sample_delivered", new { });
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
                        string expectedTool = "sample_assigned";
                        var described = new { tool = "sample_member", toolName = "sample_member" };
                        // var commented = await mcp.Tool("sample_stdio_commented", new { });
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
        string[] tests = [native + ".Foundation", native + ".Stubbed", native + ".Defaulted", native + ".Delivered",
            native + ".BlockBodied", native + ".Direct", "SampleStdioTests.Run", "SampleUnitTests.Run"];
        IReadOnlyList<string> Check(string tool, KiCadVerificationLevel level, params string[] evidence) =>
            VerificationEvidenceRules.Check(tool, level, evidence, sources, (type, method) => tests.Contains(type + "." + method));
        string foundation = native + ".Foundation";

        // Must not flag: real calls where the claim puts them.
        Assert.IsEmpty(Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, foundation));
        Assert.IsEmpty(Check("sample_open", KiCadVerificationLevel.McpNativeJourney, foundation), "A named tool argument of a journey helper is a call site.");
        Assert.IsEmpty(Check("sample_default", KiCadVerificationLevel.McpNativeJourney, foundation), "A helper's default tool parameter is a call site.");
        Assert.IsEmpty(Check("sample_after_url", KiCadVerificationLevel.McpNativeJourney, foundation), "A // inside a string is not a comment.");
        Assert.IsEmpty(Check("sample_close", KiCadVerificationLevel.NativeJourney, foundation));
        Assert.IsEmpty(Check("sample_list", KiCadVerificationLevel.McpProcess, "SampleStdioTests.Run"));
        Assert.IsEmpty(Check("sample_inspect", KiCadVerificationLevel.McpProcess, "SampleStdioTests.Run"));
        Assert.IsEmpty(Check("sample_unit", KiCadVerificationLevel.InProcess, "SampleUnitTests.Run"));
        // A delivered journey of the same dispatch switch is real evidence.
        Assert.IsEmpty(Check("sample_delivered", KiCadVerificationLevel.McpNativeJourney, native + ".Delivered"));
        // A test that hands over to a helper naming no journey and no stub is real evidence.
        Assert.IsEmpty(Check("sample_direct", KiCadVerificationLevel.McpNativeJourney, native + ".Direct"));

        // Must catch: an STDIO-only call backing a native claim (the removed-journey case).
        StringAssert.Contains(Check("sample_list", KiCadVerificationLevel.McpNativeJourney, foundation, "SampleStdioTests.Run").Single(), "no NativeSessionTests journey calls it");
        StringAssert.Contains(Check("sample_inspect", KiCadVerificationLevel.McpNativeJourney, foundation, "SampleStdioTests.Run").Single(), "no NativeSessionTests journey calls it");
        // A native claim that cites no native journey.
        Assert.IsTrue(Check("sample_list", KiCadVerificationLevel.McpNativeJourney, "SampleStdioTests.Run").Any(p => p.Contains("without citing")));
        Assert.IsTrue(Check("sample_close", KiCadVerificationLevel.NativeJourney, "SampleUnitTests.Run").Any(p => p.Contains("without citing")));
        // A journey that is still an Inconclusive lane stub proves nothing, whether its switch arm
        // names it or the default arm reaches it, and it does not count as citing a journey.
        foreach (string stub in new[] { native + ".Stubbed", native + ".Defaulted" })
        {
            var stubbed = Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, stub);
            Assert.IsTrue(stubbed.Any(p => p.Contains("Inconclusive lane stub") && p.Contains(stub)), string.Join(Environment.NewLine, stubbed));
            Assert.IsTrue(stubbed.Any(p => p.Contains("without citing")), string.Join(Environment.NewLine, stubbed));
            Assert.IsTrue(Check("sample_close", KiCadVerificationLevel.NativeJourney, stub).Any(p => p.Contains("Inconclusive lane stub")));
            // Citing a real journey beside the stub still reports the stub.
            StringAssert.Contains(Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, foundation, stub).Single(), "Inconclusive lane stub");
        }
        // A test whose body this check cannot read is rejected, even though it runs a real journey.
        var blockBodied = Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, native + ".BlockBodied");
        Assert.IsTrue(blockBodied.Any(p => p.Contains(native + ".BlockBodied") && p.Contains("cannot be read")), string.Join(Environment.NewLine, blockBodied));
        Assert.IsTrue(blockBodied.Any(p => p.Contains("without citing")), string.Join(Environment.NewLine, blockBodied));

        // Dispatch this check cannot read makes a journey unprovable instead of delivered. Each case
        // is its own source set, because an unreadable dispatch rejects every journey it may reach.
        static VerificationEvidenceRules.Source[] NativeOnly(string members) =>
        [
            new("SampleNativeJourney.cs", $$"""
                public sealed partial class {{native}}
                {
                    [TestMethod]
                    public Task Foundation() => RunNativeSessions(NativeJourney.Foundation);

                    async Task Journey() => await Call("sample_attach", new { });

                {{members}}

                    private static Task VerifyStubbed(int seed)
                        => throw new AssertInconclusiveException("Phase 2 lane 2X has not delivered this journey");
                }
                """)
        ];
        static IReadOnlyList<string> CheckIn(VerificationEvidenceRules.Source[] set, params string[] evidence) =>
            VerificationEvidenceRules.Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, evidence, set, (type, _) => type == native);
        // A default arm leading to a stub, where no earlier switch throws for the journeys it does not
        // name: every journey its switch does not name may reach the stub.
        var unlimited = NativeOnly("""
                    [TestMethod]
                    public Task Named() => RunNativeSessions(NativeJourney.Named);

                    private static async Task RunLaneJourney(NativeJourney journey)
                    {
                        int seed = journey switch { NativeJourney.Named => 1, _ => 0 };
                        await (journey switch
                        {
                            NativeJourney.Named => VerifyNamed(seed),
                            _ => VerifyStubbed(seed)
                        });
                    }

                    private static Task VerifyNamed(int seed) => Task.CompletedTask;
            """);
        var unlimitedFoundation = CheckIn(unlimited, foundation);
        Assert.IsTrue(unlimitedFoundation.Any(p => p.Contains("may reach an Inconclusive lane stub") && p.Contains("default arm in RunLaneJourney")),
            string.Join(Environment.NewLine, unlimitedFoundation));
        Assert.IsTrue(unlimitedFoundation.Any(p => p.Contains("without citing")), string.Join(Environment.NewLine, unlimitedFoundation));
        Assert.IsEmpty(CheckIn(unlimited, native + ".Named"), "A journey its own switch arm sends elsewhere cannot reach the default arm.");
        // An if/else dispatch to a stub.
        var ifElse = NativeOnly("""
                    private static async Task RunLaneJourney(NativeJourney journey)
                    {
                        if (journey == NativeJourney.Stubbed) await VerifyStubbed(0);
                    }
            """);
        StringAssert.Contains(CheckIn(ifElse, foundation)[0], "VerifyStubbed is reached other than through a NativeJourney switch arm");
        // A test that calls a stub itself.
        var direct = NativeOnly("""
                    [TestMethod]
                    public Task DirectStub() => VerifyStubbed(0);
            """);
        StringAssert.Contains(CheckIn(direct, native + ".DirectStub")[0], "calls the Inconclusive lane stub VerifyStubbed");
        StringAssert.Contains(CheckIn(direct, foundation)[0], "reached other than through a NativeJourney switch arm");
        // The same stub without any unreadable dispatch leaves the journey provable.
        Assert.IsEmpty(CheckIn(NativeOnly(""), foundation));
        // A name that is only listed, asserted, assigned or used as an object member is not a call.
        StringAssert.Contains(Check("sample_listed", KiCadVerificationLevel.McpProcess, "SampleStdioTests.Run")[0], "never calls sample_listed");
        StringAssert.Contains(Check("sample_other", KiCadVerificationLevel.McpProcess, "SampleStdioTests.Run")[0], "never calls sample_other");
        StringAssert.Contains(Check("sample_assigned", KiCadVerificationLevel.McpProcess, "SampleStdioTests.Run")[0], "never calls sample_assigned");
        StringAssert.Contains(Check("sample_member", KiCadVerificationLevel.McpProcess, "SampleStdioTests.Run")[0], "never calls sample_member");
        StringAssert.Contains(Check("sample_local", KiCadVerificationLevel.McpNativeJourney, foundation).Single(), "no NativeSessionTests journey calls it");
        // A commented-out call is not a call.
        StringAssert.Contains(Check("sample_stdio_commented", KiCadVerificationLevel.McpProcess, "SampleStdioTests.Run")[0], "never calls sample_stdio_commented");
        StringAssert.Contains(Check("sample_commented", KiCadVerificationLevel.McpNativeJourney, foundation).Single(), "no NativeSessionTests journey calls it");
        StringAssert.Contains(Check("sample_block_commented", KiCadVerificationLevel.McpNativeJourney, foundation).Single(), "no NativeSessionTests journey calls it");
        // A cited class that does not start the compiled server, or never calls the tool.
        StringAssert.Contains(Check("sample_unit", KiCadVerificationLevel.McpProcess, "SampleUnitTests.Run")[0], "does not start the compiled MCP STDIO server");
        StringAssert.Contains(Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, foundation, "SampleStdioTests.Run").Single(), "never calls sample_attach");
        // A name that is not a test, and a claim with no evidence.
        StringAssert.Contains(Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, foundation, "Missing.Test").Single(), "not a test method");
        StringAssert.Contains(Check("sample_attach", KiCadVerificationLevel.McpNativeJourney).Single(), "without evidence");
    }

    // Isolated mapping rule over every handshake shape, including ones this build's KiCad never
    // sends: request type names in capabilities (an unreleased interim lane build) and an entry that
    // is neither. The tool-level result for a released older build is checked through
    // kicad_instance_inspect in InstanceToolBoundaryTests, and the live half runs in the Foundation
    // journey (McpReattachmentJourney); neither can produce these other shapes.
    [TestMethod]
    public void OlderKiCadBuildsReportUnknownRequestCoverageNeverHandlers()
    {
        string[] features = ["session.info", "version.read"];
        string[] requests = [GetAutomationSession.Descriptor.FullName, Kiapi.Common.Commands.GetVersion.Descriptor.FullName];
        var current = CapabilityCatalog.Native(new AutomationSession { Capabilities = { features }, HandledRequests = { requests } });
        CollectionAssert.AreEqual(features, current.Features.ToArray());
        Assert.AreEqual(CapabilityCatalog.HandledRequestCoverage, current.RequestCoverage);
        CollectionAssert.AreEqual(requests, current.Requests!.Select(r => r.Name).ToArray());
        Assert.IsTrue(current.Requests!.All(r => r.Availability == "handler-registered"));

        // Without handled_requests nothing says which requests the build dispatches, so none is
        // reported as a handler, and only feature contract names are published as features: a
        // request type name or a label that is not a dotted lower-case name never is.
        string[][] shapes = [features, requests, [.. features, .. requests], ["session.info", "Session.Info", "versionread", "session..info", "version.read."]];
        foreach (string[] labels in shapes)
        {
            var older = CapabilityCatalog.Native(new AutomationSession { Capabilities = { labels } });
            CollectionAssert.AreEqual(labels.Where(label => features.Contains(label)).ToArray(), older.Features.ToArray(), string.Join(", ", labels));
            Assert.AreEqual(CapabilityCatalog.UnknownRequestCoverage, older.RequestCoverage);
            Assert.IsNull(older.Requests, "Unknown request coverage is not an empty handler list.");
            var published = JsonSerializer.SerializeToElement(older, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.AreEqual(JsonValueKind.Null, published.GetProperty("requests").ValueKind, published.GetRawText());
        }
        // The same rule applies with handled_requests: a request name in capabilities is not a feature.
        var mixed = CapabilityCatalog.Native(new AutomationSession { Capabilities = { features, requests }, HandledRequests = { requests } });
        CollectionAssert.AreEqual(features, mixed.Features.ToArray());
        // No message type of the whole protocol can pass for a feature contract.
        var protocol = NativeCapabilityProbe.KnownMessageTypes();
        Assert.IsGreaterThan(300, protocol.Count, "The protocol assembly must describe every KiCad API message.");
        string[] featureLike = protocol.Where(CapabilityCatalog.IsFeatureContract).ToArray();
        Assert.IsEmpty(featureLike, "A protocol message type would be published as a feature: " + string.Join(", ", featureLike));
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
/// given to a journey helper as a named tool argument (openTool: "name"), or the default value of a
/// helper's tool parameter (string toolName = "name"). A listed, asserted or assigned name is not a
/// call, and comments are removed before matching. NativeSessionTests is the Linux native session
/// fixture: its partial sources are the journeys that drive a real KiCad. A cited NativeSessionTests
/// method counts only when this check can read that it does not reach an Inconclusive lane stub: its
/// body is one call, either RunNativeSessions(NativeJourney.X) with a journey that no dispatch switch
/// sends to a stub, or a helper that names no journey and no stub. What it cannot read is rejected,
/// never accepted: a block body, a stub mentioned anywhere but its declaration and NativeJourney
/// switch arms (an if/else dispatch, for example), and a default arm leading to a stub when no
/// earlier switch of the same member limits the journeys that reach it. The call check is per
/// class, not per method.
/// </summary>
internal static class VerificationEvidenceRules
{
    internal const string NativeJourneyClass = "NativeSessionTests";

    internal sealed record Source(string File, string Text);

    private static readonly Regex ClassDeclaration = new(
        @"^[ \t]*(?:(?:public|internal|private|protected|sealed|static|abstract|partial|file)\s+)*class\s+(\w+)", RegexOptions.Multiline);
    private static readonly Regex StartsCompiledServer = new(@"StdioMcpFixture\.StartAsync\(|""kicad-mcp\.dll""");
    // A Task or void method declaration, and whether its body is an expression.
    private static readonly Regex TaskMethod = new(@"\b(?:Task|void)\s+(?<method>\w+)\s*\([^()]*\)\s*(?<arrow>=>)?");
    // The one call of an expression body: [await] Callee(, with the journey when it is named first.
    private static readonly Regex DelegatedCall = new(@"\G\s*(?:await\s+)?(?<callee>\w+)\s*\(\s*(?:NativeJourney\.(?<journey>\w+)\b)?");
    // A journey body that only ends Inconclusive: the lane stubs of the shared fixture.
    private static readonly Regex InconclusiveStub = new(
        @"\b(?:Task(?:<[^<>()]*>)?|void)\s+(?<method>\w+)\s*\([^()]*\)\s*(?:=>|\{)\s*(?:throw\s+new\s+AssertInconclusiveException|Assert\.Inconclusive)\s*\(");
    // Members start with an access modifier; local functions and lambdas never do.
    private static readonly Regex MemberStart = new(@"^[ \t]*(?:public|private|internal|protected)\b", RegexOptions.Multiline);
    // The name a member declares: its first identifier that is followed by a parameter list.
    private static readonly Regex MemberName = new(@"(?<![.\w])(?<name>[A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\(");
    private static readonly Regex SwitchStart = new(@"\bswitch\s*\{");
    private static readonly Regex SwitchArm = new(
        @"(?<pattern>NativeJourney\.\w+(?:\s+or\s+NativeJourney\.\w+)*|(?<![\w.])_)\s*=>\s*(?:(?<throws>throw)\b|(?:\w+\.)*(?<target>\w+)\s*\()?");
    private static readonly Regex JourneyName = new(@"NativeJourney\.(\w+)");
    // The sources are analysed once per source set, not once per checked claim.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<Source>, Analysis> Analyses = new();

    // Classes by name with their comment-free sources, and for each Task or void method declared in
    // NativeSessionTests: null when it is readable journey evidence, otherwise why it is not.
    private sealed record Analysis(IReadOnlyDictionary<string, Source[]> Classes, IReadOnlyDictionary<string, string?> NativeTests);

    private sealed record Member(string Name, string Text);

    // Why the dispatch to an Inconclusive stub cannot be read; a journey in Excluded cannot reach it.
    private sealed record Unresolved(string Reason, IReadOnlySet<string> Excluded);

    internal static IReadOnlyList<string> Check(string tool, KiCadVerificationLevel level, IReadOnlyList<string> evidence,
        IReadOnlyList<Source> sources, Func<string, string, bool> isTestMethod)
    {
        var problems = new List<string>();
        if (evidence.Count == 0) return [$"{tool} declares verification without evidence."];
        var (classes, nativeTests) = Analyses.GetValue(sources, Analyse);
        var nativeSources = classes.GetValueOrDefault(NativeJourneyClass) ?? [];
        string name = Regex.Escape(tool);
        var call = new Regex($@"(?:\bTool|\bCall|\bCallToolAsync)\(\s*""{name}""|""tools/call""\s*,\s*new\s*\{{\s*name\s*=\s*""{name}""" +
            $@"|\b\w*[Tt]ool\w*\s*:\s*""{name}""|[(,]\s*string\??\s+\w*[Tt]ool\w*\s*=\s*""{name}""\s*(?=[,)])");
        bool citesNative = false, stdioCall = false;
        foreach (string item in evidence)
        {
            string[] parts = item.Split('.');
            if (parts.Length != 2 || !isTestMethod(parts[0], parts[1]))
            {
                problems.Add($"{tool} cites {item}, which is not a test method in KiCad.Automation.Tests.");
                continue;
            }
            if (parts[0] == NativeJourneyClass)
            {
                if (!nativeTests.TryGetValue(parts[1], out string? refusal))
                    refusal = "whose declaration this check cannot find, so whether it reaches an Inconclusive lane stub cannot be read.";
                if (refusal is null) citesNative = true;
                else problems.Add($"{tool} cites {item}, {refusal}");
                continue;
            }
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
        if (level == KiCadVerificationLevel.McpNativeJourney && !nativeSources.Any(file => call.IsMatch(file.Text)))
            problems.Add($"{tool} claims {claim}, but no {NativeJourneyClass} journey calls it through MCP.");
        if (level == KiCadVerificationLevel.McpProcess && !stdioCall)
            problems.Add($"{tool} claims {claim}, but no cited compiled STDIO test calls it.");
        return problems;
    }

    private static Analysis Analyse(IReadOnlyList<Source> sources)
    {
        var code = sources.Select(source => source with { Text = StripComments(source.Text) }).ToArray();
        var classes = code.SelectMany(source => ClassDeclaration.Matches(source.Text).Select(match => (Name: match.Groups[1].Value, Source: source)))
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Source).Distinct().ToArray(), StringComparer.Ordinal);
        var nativeSources = classes.GetValueOrDefault(NativeJourneyClass) ?? [];
        string[] stubDeclarations = nativeSources.SelectMany(source => InconclusiveStub.Matches(source.Text))
            .Select(match => match.Groups["method"].Value).ToArray();
        var stubs = stubDeclarations.ToHashSet(StringComparer.Ordinal);
        var members = nativeSources.SelectMany(Members).ToArray();
        var (stubJourneys, unresolved, armTargets) = ReadDispatch(members, stubs);
        // A stub is reached only through NativeJourney switch arms. Any other mention, such as an
        // if/else dispatch, a direct call or a delegate, could reach it from any journey.
        foreach (string stub in stubs.Order(StringComparer.Ordinal))
        {
            var mention = new Regex($@"\b{Regex.Escape(stub)}\b");
            int mentions = nativeSources.Sum(source => mention.Matches(source.Text).Count);
            if (mentions > stubDeclarations.Count(declared => declared == stub) + armTargets.GetValueOrDefault(stub))
                unresolved.Add(new($"the Inconclusive lane stub {stub} is reached other than through a NativeJourney switch arm",
                    new HashSet<string>(StringComparer.Ordinal)));
        }
        var byName = members.ToLookup(member => member.Name, StringComparer.Ordinal);
        var tests = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var source in nativeSources)
            foreach (Match declaration in TaskMethod.Matches(source.Text))
            {
                string method = declaration.Groups["method"].Value;
                string? verdict = Verdict(source.Text, declaration, byName, stubs, stubJourneys, unresolved);
                // Overloads and local functions share the name: any unreadable one makes it unreadable.
                tests[method] = tests.TryGetValue(method, out string? earlier) ? earlier ?? verdict : verdict;
            }
        return new(classes, tests);
    }

    private static string? Verdict(string text, Match declaration, ILookup<string, Member> members, IReadOnlySet<string> stubs,
        IReadOnlySet<string> stubJourneys, IReadOnlyList<Unresolved> unresolved)
    {
        const string unreadable = "whose body is not one call to a journey, so whether it reaches an Inconclusive lane stub cannot be read.";
        if (!declaration.Groups["arrow"].Success) return unreadable;
        var call = DelegatedCall.Match(text, declaration.Index + declaration.Length);
        if (!call.Success) return unreadable;
        string callee = call.Groups["callee"].Value;
        if (callee == "RunNativeSessions")
        {
            if (!call.Groups["journey"].Success) return unreadable;
            string journey = call.Groups["journey"].Value;
            if (stubJourneys.Contains(journey)) return $"whose journey NativeJourney.{journey} is still an Inconclusive lane stub.";
            return unresolved.FirstOrDefault(entry => !entry.Excluded.Contains(journey)) is { } open
                ? $"whose journey NativeJourney.{journey} may reach an Inconclusive lane stub: {open.Reason}."
                : null;
        }
        if (stubs.Contains(callee)) return $"which calls the Inconclusive lane stub {callee}.";
        var helpers = members[callee].ToArray();
        if (helpers.Length == 0 || helpers.Any(helper => helper.Text.Contains("NativeJourney.", StringComparison.Ordinal)
                || helper.Text.Contains("RunNativeSessions", StringComparison.Ordinal)
                || stubs.Any(stub => Regex.IsMatch(helper.Text, $@"\b{Regex.Escape(stub)}\b"))))
            return $"whose helper {callee} this check cannot read as free of journeys and Inconclusive lane stubs.";
        return unresolved.Count > 0 ? $"which may reach an Inconclusive lane stub: {unresolved[0].Reason}." : null;
    }

    private static IEnumerable<Member> Members(Source source)
    {
        var starts = MemberStart.Matches(source.Text).Select(match => match.Index).Append(source.Text.Length).ToArray();
        for (int member = 0; member + 1 < starts.Length; member++)
        {
            string text = source.Text[starts[member]..starts[member + 1]];
            yield return new(MemberName.Match(text) is { Success: true } declared ? declared.Groups["name"].Value : "", text);
        }
    }

    // Reads the dispatch switches member by member. An arm naming a journey sends that journey to its
    // target. A default arm is reached only by the journeys that every earlier switch of the member
    // lets through (it names them and throws for any other) and that its own switch does not name;
    // with no such earlier switch, every journey its switch does not name may reach it.
    private static (HashSet<string> StubJourneys, List<Unresolved> Unresolved, Dictionary<string, int> ArmTargets) ReadDispatch(
        IReadOnlyList<Member> members, IReadOnlySet<string> stubs)
    {
        var journeys = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<Unresolved>();
        var targets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var switches = Switches(member.Text);
            for (int index = 0; index < switches.Count; index++)
            {
                var arms = switches[index];
                var named = arms.SelectMany(arm => arm.Journeys).ToHashSet(StringComparer.Ordinal);
                foreach (var arm in arms)
                {
                    if (arm.Target is null) continue;
                    targets[arm.Target] = targets.GetValueOrDefault(arm.Target) + 1;
                    if (!stubs.Contains(arm.Target)) continue;
                    if (arm.Journeys.Count > 0) { journeys.UnionWith(arm.Journeys); continue; }
                    var filters = switches.Take(index)
                        .Where(earlier => earlier.Any(other => other.Journeys.Count > 0) && earlier.Any(other => other.Journeys.Count == 0 && other.Throws))
                        .Select(earlier => earlier.SelectMany(other => other.Journeys).ToHashSet(StringComparer.Ordinal)).ToArray();
                    if (filters.Length == 0)
                    {
                        unresolved.Add(new($"a default arm in {member.Name} leads to the stub {arm.Target}, and no earlier switch there limits the journeys that reach it", named));
                        continue;
                    }
                    var reaching = filters.Skip(1).Aggregate(filters[0], (all, next) => { all.IntersectWith(next); return all; });
                    reaching.ExceptWith(named);
                    journeys.UnionWith(reaching);
                }
            }
        }
        return (journeys, unresolved, targets);
    }

    private sealed record SwitchArmEntry(IReadOnlyList<string> Journeys, string? Target, bool Throws);

    // Arms of each switch expression in one member, in source order; nested braces inside an arm are skipped over.
    private static List<List<SwitchArmEntry>> Switches(string member)
    {
        var result = new List<List<SwitchArmEntry>>();
        foreach (Match start in SwitchStart.Matches(member))
        {
            int open = start.Index + start.Length - 1, depth = 0, close = open;
            for (; close < member.Length; close++)
            {
                if (member[close] == '{') depth++;
                else if (member[close] == '}' && --depth == 0) break;
            }
            string body = member[(open + 1)..Math.Min(close, member.Length)];
            result.Add(SwitchArm.Matches(body).Select(arm => new SwitchArmEntry(
                JourneyName.Matches(arm.Groups["pattern"].Value).Select(j => j.Groups[1].Value).ToArray(),
                arm.Groups["target"].Success ? arm.Groups["target"].Value : null, arm.Groups["throws"].Success)).ToList());
        }
        return result;
    }

    // Removes // and /* */ comments while keeping string and character literals intact, so a "//"
    // inside a string (an ipc:/// endpoint) is not a comment and a commented-out call is not a call.
    // Interpolation holes are read as part of their string.
    internal static string StripComments(string text)
    {
        var output = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                output.Append('\n', text.AsSpan(i, end - i).Count('\n'));
                i = end;
                continue;
            }
            int quote = i;
            while (quote < text.Length && text[quote] is '$' or '@') quote++;
            if (quote < text.Length && text[quote] == '"')
            {
                int end = StringEnd(text, quote, text.AsSpan(i, quote - i).Contains('@'));
                output.Append(text, i, end - i);
                i = end;
                continue;
            }
            if (c == '\'')
            {
                int end = i + 1;
                while (end < text.Length && text[end] != '\'' && text[end] != '\n') end += text[end] == '\\' ? 2 : 1;
                end = Math.Min(end + 1, text.Length);
                output.Append(text, i, end - i);
                i = end;
                continue;
            }
            output.Append(c);
            i++;
        }
        return output.ToString();
    }

    // The index just past the string literal whose opening quote run starts at quote.
    private static int StringEnd(string text, int quote, bool verbatim)
    {
        int run = 0;
        while (quote + run < text.Length && text[quote + run] == '"') run++;
        if (verbatim)
        {
            // Verbatim string: "" is an escaped quote.
            for (int i = quote + 1; i < text.Length; i++)
            {
                if (text[i] != '"') continue;
                if (i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                return i + 1;
            }
            return text.Length;
        }
        if (run >= 3)
        {
            // Raw string: it ends at the next run of at least as many quotes.
            for (int i = quote + run; i < text.Length; i++)
            {
                if (text[i] != '"') continue;
                int closing = 0;
                while (i + closing < text.Length && text[i + closing] == '"') closing++;
                if (closing >= run) return i + closing;
                i += closing - 1;
            }
            return text.Length;
        }
        if (run == 2) return quote + 2;
        for (int i = quote + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '"' || text[i] == '\n') return i + 1;
        }
        return text.Length;
    }
}

/// <summary>
/// The handshake's capabilities field names feature contracts (session.info, version.read and, when a
/// whole feature works, labels such as schematic.connection-realization.v1), never request types.
/// </summary>
internal static class NativeFeatureContracts
{
    private static readonly Regex FeatureName = new(@"^[a-z][a-z0-9-]*(\.[a-z0-9-]+)+$");

    internal static string[] Verify(AutomationSession session)
    {
        string[] features = session.Capabilities.ToArray();
        CollectionAssert.IsSubsetOf(new[] { "session.info", "version.read" }, features, "Every automation handshake names its base features.");
        CollectionAssert.AllItemsAreUnique(features);
        Assert.IsTrue(features.All(FeatureName.IsMatch), "Feature contracts are lower-case dotted names: " + string.Join(", ", features));
        string[] requestNames = features.Intersect(NativeCapabilityProbe.KnownMessageTypes().Concat(session.HandledRequests)).ToArray();
        Assert.IsEmpty(requestNames, "A request type is not a feature contract: " + string.Join(", ", requestNames));
        return features;
    }
}

/// <summary>
/// Proves that a native handshake's handled_requests lists exactly the request types its process
/// dispatches. Every message type the protocol defines, plus anything listed, is sent with a payload
/// that cannot be decoded: a registered handler rejects it before running anything, and an
/// unregistered type comes back unhandled. Nothing is executed and no document changes. The named
/// feature contracts in capabilities are not request types and are checked by NativeFeatureContracts.
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
            string[] advertised = before.HandledRequests.ToArray();
            Assert.IsNotEmpty(advertised, "This build's handshake must list the requests it handles.");
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
            if (!after.HandledRequests.SequenceEqual(advertised))
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
                    epoch = client.Epoch, features = before.Capabilities.ToArray(), advertised, probed = statuses.Count,
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
