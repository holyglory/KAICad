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

        // The check's reading of the real fixture must agree with a plain text search of its files, so
        // a regression in the member or dispatch reader cannot hide a stub and let its journeys count.
        var reading = VerificationEvidenceRules.ReadStubs(sources);
        var nativeFiles = sources.Where(source => Regex.IsMatch(source.Text, $@"\bpartial\s+class\s+{VerificationEvidenceRules.NativeJourneyClass}\b")).ToArray();
        string[] searched = nativeFiles
            .Select(source => (source.File, Sites: Regex.Matches(VerificationEvidenceRules.StripComments(source.Text), @"AssertInconclusiveException\(|Assert\.Inconclusive\(").Count))
            .Where(file => file.Sites > 0).Select(file => $"{file.File}: {file.Sites}").ToArray();
        CollectionAssert.AreEquivalent(searched, reading.RaiseSites.Select(file => $"{file.Key}: {file.Value}").ToArray(),
            "Every Inconclusive raise in the native journey files must lie in a member the check reads as a stub.");
        foreach (var source in nativeFiles)
            foreach (Match arm in Regex.Matches(VerificationEvidenceRules.StripComments(source.Text), @"NativeJourney\.(\w+)\s*=>\s*(\w+)\s*\("))
                if (reading.Stubs.Contains(arm.Groups[2].Value))
                    Assert.IsTrue(reading.StubJourneys.Contains(arm.Groups[1].Value),
                        $"{source.File}: NativeJourney.{arm.Groups[1].Value} leads to the stub {arm.Groups[2].Value}, which the check must read as a stub journey.");

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
                    public Task Partial() => RunNativeSessions(NativeJourney.Partial);

                    [TestMethod]
                    public async Task BlockBodied()
                    {
                        await RunNativeSessions(NativeJourney.Foundation);
                    }

                    [TestMethod]
                    public Task Direct(bool restore) => VerifyDirect(restore);

                    [TestMethod]
                    public Task Chained() => VerifyChainFirst();

                    [TestMethod]
                    public Task ChainedToStub() => VerifyStubChainFirst();

                    [TestMethod]
                    public Task ChainedToUnknown() => VerifyUnknownChainFirst();

                    [TestMethod]
                    public async Task SelfStubbed()
                    {
                        await Call("sample_attach", new { });
                        throw new AssertInconclusiveException("Phase 2 lane 2X has not delivered the rest of this journey");
                    }

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

                    // Two helper levels, a local function and a delegate parameter, and a type test
                    // that only lets an Inconclusive result pass: none of them is a stub.
                    private static Task VerifyChainFirst() => VerifyChainSecond(attempts: 2);

                    private static async Task VerifyChainSecond(int attempts)
                    {
                        async Task Attempt(Func<Task> action) => await action();
                        for (int attempt = 0; attempt < attempts; attempt++)
                        {
                            try { await Attempt(() => Call("sample_chained", new { attempt })); return; }
                            catch (Exception error) when (error is not AssertInconclusiveException && attempt + 1 < attempts) { }
                        }
                    }

                    private static Task VerifyStubChainFirst() => VerifyStubChainSecond();

                    private Task VerifyStubChainSecond() => RunNativeSessions(NativeJourney.Stubbed);

                    private static Task VerifyUnknownChainFirst() => VerifyUnknownChainSecond();

                    private static Task VerifyUnknownChainSecond() => ImportedHelper("sample_attach");

                    private static Task<int> Call(string tool, object arguments) => Task.FromResult(0);

                    private static Task<int> Open(string endpoint, string openTool) => Task.FromResult(0);

                    private static Task Create(string endpoint, CancellationToken token,
                        string toolName = "sample_default") => Task.CompletedTask;

                    private static async Task RunLaneJourney(NativeJourney journey)
                    {
                        var seed = journey switch
                        {
                            NativeJourney.Stubbed or NativeJourney.Delivered or NativeJourney.Defaulted or NativeJourney.Partial => Seed.Sheets,
                            _ => throw new ArgumentOutOfRangeException(nameof(journey))
                        };
                        await (journey switch
                        {
                            NativeJourney.Stubbed => VerifyStubbed(seed),
                            NativeJourney.Delivered => VerifyDelivered(seed),
                            NativeJourney.Partial => VerifyPartial(seed),
                            _ => VerifyDefaulted(seed)
                        });
                    }

                    // Lane 2X replaces this body when it delivers the journey.
                    private static Task VerifyStubbed(Seed seed)
                        => throw new AssertInconclusiveException("Phase 2 lane 2X has not delivered this journey");

                    private static Task VerifyDefaulted(Seed seed)
                        => throw new AssertInconclusiveException("Phase 2 lane 2X has not delivered this journey");

                    private static async Task VerifyDelivered(Seed seed) => await Call("sample_delivered", new { });

                    // A journey that proves its first half and then ends Inconclusive is still a stub.
                    private static async Task VerifyPartial(Seed seed)
                    {
                        await Call("sample_partial", new { });
                        Assert.Inconclusive("Phase 2 lane 2X has delivered only the first half of this journey");
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
        string[] tests = [.. new[] { "Foundation", "Stubbed", "Defaulted", "Delivered", "Partial", "BlockBodied", "Direct", "Chained",
            "ChainedToStub", "ChainedToUnknown", "SelfStubbed" }.Select(test => native + "." + test), "SampleStdioTests.Run", "SampleUnitTests.Run"];
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
        // So is a clean two-level chain through a local function and a delegate, whose only mention of
        // AssertInconclusiveException is a type test that lets such a result pass.
        Assert.IsEmpty(Check("sample_chained", KiCadVerificationLevel.McpNativeJourney, native + ".Chained"));

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
        // A journey body that ends Inconclusive after real calls is a stub too, and so is a test
        // method that ends Inconclusive itself.
        var partial = Check("sample_partial", KiCadVerificationLevel.McpNativeJourney, native + ".Partial");
        Assert.IsTrue(partial.Any(p => p.Contains("NativeJourney.Partial is still an Inconclusive lane stub")), string.Join(Environment.NewLine, partial));
        Assert.IsTrue(partial.Any(p => p.Contains("without citing")), string.Join(Environment.NewLine, partial));
        StringAssert.Contains(Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, foundation, native + ".SelfStubbed").Single(),
            "which ends Inconclusive itself");
        // A two-level helper chain that ends in a journey, whether a stub or not, cannot be read.
        var chainedToStub = Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, native + ".ChainedToStub");
        Assert.IsTrue(chainedToStub.Any(p => p.Contains("ChainedToStub -> VerifyStubChainFirst -> VerifyStubChainSecond")
            && p.Contains("names a journey or RunNativeSessions")), string.Join(Environment.NewLine, chainedToStub));
        Assert.IsTrue(chainedToStub.Any(p => p.Contains("without citing")), string.Join(Environment.NewLine, chainedToStub));
        // A helper chain whose second level calls something this check cannot resolve.
        StringAssert.Contains(Check("sample_attach", KiCadVerificationLevel.McpNativeJourney, foundation, native + ".ChainedToUnknown").Single(),
            "ChainedToUnknown -> VerifyUnknownChainFirst -> VerifyUnknownChainSecond, where ImportedHelper(...) is neither");
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
        static string Lane(string limit) => $$"""
                    [TestMethod]
                    public Task Named() => RunNativeSessions(NativeJourney.Named);

                    private static async Task RunLaneJourney(NativeJourney journey, NativeJourney other, bool ready)
                    {
                {{limit}}
                        await (journey switch
                        {
                            NativeJourney.Named => VerifyNamed(seed),
                            _ => VerifyStubbed(seed)
                        });
                    }

                    private static Task VerifyNamed(int seed) => Task.CompletedTask;
            """;
        // A default arm leading to a stub, where no earlier switch throws for the journeys it does not
        // name: every journey its switch does not name may reach the stub.
        var unlimited = NativeOnly(Lane("        int seed = journey switch { NativeJourney.Named => 1, _ => 0 };"));
        var unlimitedFoundation = CheckIn(unlimited, foundation);
        Assert.IsTrue(unlimitedFoundation.Any(p => p.Contains("may reach an Inconclusive lane stub") && p.Contains("default arm in RunLaneJourney")),
            string.Join(Environment.NewLine, unlimitedFoundation));
        Assert.IsTrue(unlimitedFoundation.Any(p => p.Contains("without citing")), string.Join(Environment.NewLine, unlimitedFoundation));
        Assert.IsEmpty(CheckIn(unlimited, native + ".Named"), "A journey its own switch arm sends elsewhere cannot reach the default arm.");
        // An earlier statement switching over the same parameter and throwing for any other journey
        // limits the default arm to the journeys it names, also after an unconditional block.
        string limit = "int seed = journey switch { NativeJourney.Named => 1, _ => throw new ArgumentOutOfRangeException(nameof(journey)) };";
        Assert.IsEmpty(CheckIn(NativeOnly(Lane("        " + limit)), foundation), "A limit over the same value keeps other journeys away from the stub.");
        Assert.IsEmpty(CheckIn(NativeOnly(Lane("        if (ready) { }\n        " + limit)), foundation), "A limit after a finished if block always runs.");
        // Must catch: only a limit over the same value counts; a reassigned value, or a limit that
        // runs only under a condition (an if, or the else of one), limits nothing.
        string conditional = limit.Replace("int seed = ", "seed = ", StringComparison.Ordinal);
        foreach (string unreadable in new[]
        {
            "        " + limit.Replace("journey switch", "other switch", StringComparison.Ordinal),
            "        int seed = 0;\n        if (ready) " + conditional,
            "        int seed;\n        if (ready) seed = 0; else " + conditional,
            "        int seed;\n        if (ready) { seed = 0; } else\n            " + conditional
        })
        {
            var unlimitedByValue = CheckIn(NativeOnly(Lane(unreadable)), foundation);
            Assert.IsTrue(unlimitedByValue.Any(p => p.Contains("no earlier switch over journey there limits the journeys that reach it")),
                unreadable + Environment.NewLine + string.Join(Environment.NewLine, unlimitedByValue));
        }
        var reassigned = NativeOnly(Lane("        " + limit + "\n        journey = other;"));
        foreach (string test in new[] { foundation, native + ".Named" })
            StringAssert.Contains(CheckIn(reassigned, test)[0], "can be read: RunLaneJourney reassigns or redeclares journey");
        // Must catch: a limit written earlier in the text than a switch in a local function, which the
        // member may call before the limit runs, limits nothing; the switch's own arms still count.
        foreach (string dispatch in new[]
        {
            "async Task Dispatch() => await (journey switch { NativeJourney.Named => VerifyNamed(0), _ => VerifyStubbed(0) });",
            "async Task Dispatch()\n        {\n            await (journey switch { NativeJourney.Named => VerifyNamed(0), _ => VerifyStubbed(0) });\n        }"
        })
        {
            var late = NativeOnly($$"""
                    [TestMethod]
                    public Task Named() => RunNativeSessions(NativeJourney.Named);

                    private static async Task RunLaneJourney(NativeJourney journey)
                    {
                        await Dispatch();
                        {{limit}}
                        {{dispatch}}
                    }

                    private static Task VerifyNamed(int seed) => Task.CompletedTask;
            """);
            var beforeLimit = CheckIn(late, foundation);
            Assert.IsTrue(beforeLimit.Any(p => p.Contains("leads to the stub VerifyStubbed from inside a lambda, a local function or a nested block")
                && p.Contains("no earlier switch over journey there limits the journeys that reach it")), string.Join(Environment.NewLine, beforeLimit));
            Assert.IsEmpty(CheckIn(late, native + ".Named"), "A journey the switch's own arm sends elsewhere cannot reach its default arm.");
        }

        // Only a parameter that always holds the journey being run is read as dispatch: the one
        // RunNativeSessions receives from tests, passed on unchanged. Must not flag: a clean two-level
        // chain passing it on by position and then by name.
        static string Runner(string call) => $$"""
                    private async Task RunNativeSessions(NativeJourney journey, string theme = "light")
                    {
                        await {{call}};
                    }

                    [TestMethod]
                    public Task Stubbed() => RunNativeSessions(NativeJourney.Stubbed);

                    [TestMethod]
                    public Task Named() => RunNativeSessions(NativeJourney.Named);

            """;
        const string dispatchOverJourney = """
                    private static async Task Dispatch(string theme, NativeJourney journey)
                    {
                        await (journey switch { NativeJourney.Stubbed => VerifyStubbed(0), _ => Task.CompletedTask });
                    }
            """;
        var carried = NativeOnly(Runner("RunLaneJourney(journey, theme)") + """
                    private static Task RunLaneJourney(NativeJourney journey, string theme) => Dispatch(theme, journey: journey);

            """ + dispatchOverJourney);
        Assert.IsEmpty(CheckIn(carried, foundation), "The journey passed on unchanged through two members is read exactly.");
        StringAssert.Contains(CheckIn(carried, native + ".Stubbed")[0], "whose journey NativeJourney.Stubbed is still an Inconclusive lane stub.");
        // Must catch: a switch over a second NativeJourney parameter that a caller sets to a fixed
        // journey, a fixed or default journey passed instead of the running one, a member also used as
        // a delegate, and a run of another journey started from inside a journey.
        var second = NativeOnly(Runner("RunLaneJourney(journey, NativeJourney.Named)") + """
                    private static async Task RunLaneJourney(NativeJourney journey, NativeJourney other)
                    {
                        await (other switch { NativeJourney.Named => VerifyStubbed(0), _ => Task.CompletedTask });
                    }
            """);
        StringAssert.Contains(CheckIn(second, foundation)[0], "can be read: RunNativeSessions calls RunLaneJourney with NativeJourney.Named for other");
        foreach (string passed in new[] { "NativeJourney.Stubbed", "default" })
        {
            var fixedJourney = NativeOnly(Runner($"Dispatch(theme, {passed})") + dispatchOverJourney);
            StringAssert.Contains(CheckIn(fixedJourney, foundation)[0], $"can be read: RunNativeSessions calls Dispatch with {passed} for journey");
        }
        var delegated = NativeOnly(Runner("RunLaneJourney(journey)") + """
                    private static async Task RunLaneJourney(NativeJourney journey)
                    {
                        Func<string, NativeJourney, Task> dispatch = Dispatch;
                        await dispatch("", journey);
                    }

            """ + dispatchOverJourney);
        StringAssert.Contains(CheckIn(delegated, foundation)[0], "can be read: Dispatch is named other than in a call");
        var nested = NativeOnly(Runner("RunLaneJourney(journey, theme)") + """
                    private async Task RunLaneJourney(NativeJourney journey, string theme)
                    {
                        await Nested();
                        await Dispatch(theme, journey);
                    }

                    private Task Nested() => RunNativeSessions(NativeJourney.Stubbed);

            """ + dispatchOverJourney);
        StringAssert.Contains(CheckIn(nested, foundation)[0], "and Nested calls RunNativeSessions with NativeJourney.Stubbed for journey");
        // An if/else dispatch to a stub.
        var ifElse = NativeOnly("""
                    private static async Task RunLaneJourney(NativeJourney journey)
                    {
                        if (journey == NativeJourney.Stubbed) await VerifyStubbed(0);
                    }
            """);
        StringAssert.Contains(CheckIn(ifElse, foundation)[0], "VerifyStubbed is reached other than through a NativeJourney switch arm");
        // A test that calls a stub itself, or through two helper levels.
        var direct = NativeOnly("""
                    [TestMethod]
                    public Task DirectStub() => VerifyStubbed(0);

                    [TestMethod]
                    public Task ChainedStub() => VerifyFirst();

                    private static Task VerifyFirst() => VerifySecond(0);

                    private static Task VerifySecond(int seed) => VerifyStubbed(seed);
            """);
        StringAssert.Contains(CheckIn(direct, native + ".DirectStub")[0], "reaches the Inconclusive lane stub VerifyStubbed through DirectStub.");
        StringAssert.Contains(CheckIn(direct, native + ".ChainedStub")[0],
            "reaches the Inconclusive lane stub VerifyStubbed through ChainedStub -> VerifyFirst -> VerifySecond.");
        StringAssert.Contains(CheckIn(direct, foundation)[0], "reached other than through a NativeJourney switch arm");
        // The same stub without any unreadable dispatch leaves the journey provable.
        Assert.IsEmpty(CheckIn(NativeOnly(""), foundation));
        // Must catch: a helper that catches Inconclusive, or whose filter can still let one be caught,
        // is a stub, because it can turn an unfinished journey into a pass.
        var handled = NativeOnly("""
                    [TestMethod]
                    public Task Swallowed() => VerifySwallowed();

                    [TestMethod]
                    public Task Tested() => VerifyTested();

                    [TestMethod]
                    public Task Either() => VerifyEither(true);

                    private static async Task VerifySwallowed()
                    {
                        try { await Task.Yield(); } catch (AssertInconclusiveException) { }
                    }

                    private static async Task VerifyTested()
                    {
                        try { await Task.Yield(); } catch (Exception error) when (error is AssertInconclusiveException) { }
                    }

                    private static async Task VerifyEither(bool retry)
                    {
                        try { await Task.Yield(); } catch (Exception error) when (error is not AssertInconclusiveException || retry) { }
                    }
            """);
        foreach (string test in new[] { "Swallowed", "Tested", "Either" })
            StringAssert.Contains(CheckIn(handled, native + "." + test)[0], $"reaches the Inconclusive lane stub Verify{test} through {test}.");
        // A call into another test class is followed member by member. Must catch: one that reaches
        // Inconclusive two levels down, or a name the check cannot find in a class that can end
        // Inconclusive; any journey may make such a call. Must not flag: a member of the same class
        // that reaches nothing Inconclusive.
        VerificationEvidenceRules.Source[] external =
        [
            .. NativeOnly("""
                    [TestMethod]
                    public Task ExternalStub() => SampleFixture.Prepare(0);

                    [TestMethod]
                    public Task ExternalUnknown() => SampleFixture.Missing();

                    [TestMethod]
                    public Task ExternalClean() => SampleFixture.Clean();
            """),
            new("SampleFixture.cs", """
                internal static class SampleFixture
                {
                    internal static Task Prepare(int seed) => Step(seed);

                    private static Task Step(int seed) => throw new AssertInconclusiveException("Phase 2 lane 2X has not delivered this step");

                    internal static Task Clean() => Task.CompletedTask;
                }
                """)
        ];
        StringAssert.Contains(CheckIn(external, native + ".ExternalStub")[0],
            "where ExternalStub calls SampleFixture.Prepare -> SampleFixture.Step, where SampleFixture.Step raises, catches or tests for Inconclusive.");
        StringAssert.Contains(CheckIn(external, native + ".ExternalUnknown")[0],
            "SampleFixture.Missing, which this check cannot find, so any SampleFixture.Step, where SampleFixture.Step raises");
        Assert.IsEmpty(CheckIn(external, native + ".ExternalClean"));
        StringAssert.Contains(CheckIn(external, foundation)[0], "may reach an Inconclusive lane stub: ExternalStub calls SampleFixture.Prepare");
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
/// call, and comments are removed before matching. The call check is per class, not per method.
/// <para>
/// NativeSessionTests is the Linux native session fixture: its partial sources are the journeys that
/// drive a real KiCad. Its members, and those of every other class in the test sources, are read with
/// their braces, strings and comments, so every member is found, with or without an access modifier.
/// An Inconclusive lane stub is any member that names AssertInconclusiveException or
/// Assert.Inconclusive, whether it raises, catches or tests for it, except in the one catch filter
/// that lets such a result through: catch (T e) when (e is not AssertInconclusiveException), alone or
/// followed by &amp;&amp; with no top-level | or ?. A cited NativeSessionTests method counts only when this
/// check can read that it cannot reach a stub, and what it cannot read is rejected, never accepted:
/// </para>
/// <list type="bullet">
/// <item>An expression body that is exactly RunNativeSessions(NativeJourney.X[, parameters]) runs
/// journey X. A switch is read as dispatch only over a parameter that always holds the journey being
/// run: the NativeJourney parameter of RunNativeSessions when every call passes it a named journey
/// from a test that nothing else names, or the NativeJourney parameter of a member named only in
/// calls that each pass it such a parameter of the caller, unchanged. Neither may be reassigned or
/// redeclared, and an overloaded member is not read. X is rejected when a dispatch arm sends it to a
/// stub, or when a default arm leading to a stub may be reached by X. A default arm is limited only
/// when its switch runs directly in the member's block, outside any lambda, local function or nested
/// block, and then only by earlier switches over the same parameter, each a whole unconditional
/// assignment statement of that block whose arms name journeys and throw for any other. Every journey
/// may reach a stub that is named anywhere except its declaration and switch arms (an if/else
/// dispatch, a direct call, a delegate), a stub that a switch over any other value leads to, and a
/// call into another test class that can end Inconclusive.</item>
/// <item>Any other test is followed through every NativeSessionTests member it names, to any depth.
/// It is rejected when a reachable member is or names a stub, names NativeJourney or
/// RunNativeSessions, makes an unqualified call that is neither a NativeSessionTests member nor a
/// local function, delegate or parameter declared where it is called, or calls into another test
/// class a member that can end Inconclusive.</item>
/// </list>
/// A call into another test class (Class.Member(...) or new Class(...)) is followed member by member
/// through that class and the test classes it calls the same way; a name this check cannot find there
/// stands for every member of its class. Members reached through an instance, and code outside the
/// test sources, are not examined.
/// </summary>
internal static class VerificationEvidenceRules
{
    internal const string NativeJourneyClass = "NativeSessionTests";
    private const string JourneyRunner = "RunNativeSessions";

    internal sealed record Source(string File, string Text);

    // What this check reads about the fixture's Inconclusive lane stubs, for comparison with a plain
    // text search: the raise sites inside stub members per file, the stub members, and the journeys
    // that a dispatch arm or a limited default arm sends to one.
    internal sealed record StubReading(IReadOnlyDictionary<string, int> RaiseSites, IReadOnlySet<string> Stubs, IReadOnlySet<string> StubJourneys);

    private static readonly Regex ClassDeclaration = new(
        @"^[ \t]*(?:(?:public|internal|private|protected|sealed|static|abstract|partial|file)\s+)*class\s+(\w+)", RegexOptions.Multiline);
    private static readonly Regex StartsCompiledServer = new(@"StdioMcpFixture\.StartAsync\(|""kicad-mcp\.dll""");
    // A whole expression body that runs one named journey and passes on only parameters or constants.
    private static readonly Regex JourneyRun = new(
        @"^\s*(?:await\s+)?RunNativeSessions\s*\(\s*NativeJourney\s*\.\s*(?<journey>[A-Za-z_]\w*)\s*(?:,\s*(?:[A-Za-z_]\w*|`+)\s*)*\)\s*;?\s*$");
    // Naming Inconclusive at all, raising it, and the one catch filter that only lets it through
    // (RunNativeSessions has one).
    private static readonly Regex InconclusiveMention = new(@"\bAssert\s*\.\s*Inconclusive\b|\bAssertInconclusiveException\b");
    private static readonly Regex RaiseSite = new(@"\bAssertInconclusiveException\s*\(|\bAssert\s*\.\s*Inconclusive\s*\(");
    private static readonly Regex LetThroughFilter = new(
        @"\bcatch\s*\(\s*[A-Za-z_][\w.]*\s+(?<name>[A-Za-z_]\w*)\s*\)\s*when\s*(?<open>\()\s*\k<name>\s+is\s+not\s+(?:[A-Za-z_]\w*\s*\.\s*)*(?<type>AssertInconclusiveException)\b");
    private static readonly Regex JourneyMention = new(@"\b(?:NativeJourney|RunNativeSessions)\b");
    private static readonly Regex JourneyEnum = new(@"\benum\s+NativeJourney\b");
    private static readonly Regex JourneyLiteral = new(@"^NativeJourney\s*\.\s*[A-Za-z_]\w*$");
    private static readonly Regex Identifier = new(@"^[A-Za-z_]\w*$");
    private static readonly Regex NamedArgument = new(@"^(?<name>[A-Za-z_]\w*)\s*:(?!:)\s*(?<value>[\s\S]*)$");
    private static readonly Regex ParameterDeclaration = new(
        @"^(?:\[[^\]]*\]\s*)*(?:(?<modifier>this|params|in|ref|out|scoped|readonly)\s+)*(?<type>[A-Za-z_][\w.]*(?:\s*<[^=]*>)?(?:\s*\[[\s,]*\])*\??|\([^=]*\))\s+(?<name>[A-Za-z_]\w*)\s*(?:=[\s\S]*)?$");
    // A name another member may refer to: unqualified, or through this, base or the fixture class.
    private static readonly Regex Reference = new(
        @"(?<![\w@$]|\.\s*)(?<name>[A-Za-z_]\w*)|\b(?:this|base|NativeSessionTests)\s*\.\s*(?<name>[A-Za-z_]\w*)");
    // An unqualified call, optionally generic; constructors (new T(...)) are not calls of a member.
    private static readonly Regex UnqualifiedCall = new(
        @"(?<![\w@$]|\.\s*|\bnew\s+|::)(?<name>[A-Za-z_]\w*)\s*(?:<[\w\s,.?\[\]]*(?:<[\w\s,.?\[\]]*>[\w\s,.?\[\]]*)*>)?\s*\(");
    // A call of another class's static member or constructor: Class.Member(...) or new Class(...).
    private static readonly Regex QualifiedCall = new(
        @"(?<![\w@$]|\.\s*)(?<class>[A-Za-z_]\w*)\s*\.\s*(?<name>[A-Za-z_]\w*)\s*(?:<[\w\s,.?\[\]]*(?:<[\w\s,.?\[\]]*>[\w\s,.?\[\]]*)*>)?\s*\(" +
        @"|\bnew\s+(?<class>[A-Za-z_]\w*)\s*\(");
    // Declarations inside a member: local functions (type name(...) { or =>), typed variables and
    // parameters (type name followed by = , ; ) : or in), deconstructed variables and lambda parameters.
    private const string Generic = @"<(?:[^<>;{}]|<(?:[^<>;{}]|<[^<>;{}]*>)*>)*>";
    private const string TypeToken = @"(?<type>[A-Za-z_][\w.]*(?:\s*" + Generic + @")?(?:\s*\[[\s,]*\])*\??|\([^;{}()]*\))";
    private static readonly Regex LocalFunction = new(
        @"(?<![\w.])" + TypeToken + @"\s+(?<name>[A-Za-z_]\w*)\s*(?:" + Generic + @")?\s*\([^;{}]*?\)\s*(?:\{|=>|where\b)");
    private static readonly Regex LocalVariable = new(
        @"(?<![\w.])" + TypeToken + @"\s+(?<name>[A-Za-z_]\w*)\s*(?=[=,;):]|\bin\b)");
    private static readonly Regex LambdaParameter = new(@"(?<![\w.])(?<name>[A-Za-z_]\w*)\s*=>");
    private static readonly Regex LambdaParameters = new(@"\((?<list>[^()]*)\)\s*=>");
    private static readonly Regex Deconstruction = new(@"\((?<list>[^()]*)\)\s*(?:=(?![=>])|\bin\b)");
    private static readonly Regex LastIdentifier = new(@"(?<name>[A-Za-z_]\w*)\s*$");
    private static readonly Regex TrailingName = new(@"(?<name>[A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*$");
    private static readonly Regex TypeDeclaration = new(@"\b(?:class|struct|interface|enum|record)\s+(?<name>[A-Za-z_]\w*)");
    private static readonly Regex SwitchStart = new(
        @"(?:(?<![\w.])(?<value>[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)\s*)?\bswitch\s*\{");
    private static readonly Regex JourneyArm = new(
        @"^(?<pattern>NativeJourney\s*\.\s*\w+(?:\s+or\s+NativeJourney\s*\.\s*\w+)*)\s*=>");
    private static readonly Regex DiscardArm = new(@"^_\s*=>");
    private static readonly Regex ArmTarget = new(
        @"^\s*(?:await\s+)?(?:[A-Za-z_]\w*\s*\.\s*)*(?<target>[A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*\(");
    private static readonly Regex JourneyName = new(@"NativeJourney\s*\.\s*(\w+)");
    // A whole statement that assigns the switch to a variable: [type] name = <value> switch. A
    // statement keyword in the type's place (else, do) makes it conditional.
    private static readonly Regex AssigningStatement = new(
        @"^\s*(?:(?<type>[A-Za-z_][\w.]*)(?:\s*<[\w\s,.?\[\]<>]*>)?\??\s+)?[A-Za-z_]\w*\s*=\s*$");

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "while", "for", "foreach", "switch", "catch", "using", "lock", "fixed", "nameof", "typeof", "sizeof", "default",
        "checked", "unchecked", "when", "return", "await", "throw", "new", "is", "as", "and", "or", "not", "base", "this", "var",
        "in", "out", "ref", "yield", "else", "case", "do", "stackalloc", "where", "select", "from", "let", "join", "orderby",
        "group", "into", "on", "equals", "by", "get", "set", "init", "add", "remove", "params", "operator", "implicit",
        "explicit", "delegate", "static", "async", "unsafe", "extern", "public", "private", "protected", "internal", "override",
        "virtual", "abstract", "sealed", "readonly", "partial", "required", "volatile", "const", "file",
        // Members every object has; none of them can reach a journey.
        "GetType", "ToString", "Equals", "GetHashCode", "ReferenceEquals", "MemberwiseClone"
    };
    // Words that start an expression or statement, so the name after them is not being declared.
    private static readonly HashSet<string> NotTypes = new(StringComparer.Ordinal)
    {
        "return", "await", "throw", "new", "else", "case", "is", "as", "in", "out", "ref", "yield", "goto", "when", "and", "or",
        "not", "using", "do", "static", "async", "unsafe", "extern", "readonly", "const", "volatile", "public", "private",
        "protected", "internal", "override", "virtual", "abstract", "sealed", "partial", "required", "file"
    };
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "async", "unsafe", "extern", "override", "virtual", "abstract",
        "sealed", "new", "readonly", "partial", "file", "required", "volatile"
    };

    // The sources are analysed once per source set, not once per checked claim.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<Source>, Analysis> Analyses = new();

    internal static StubReading ReadStubs(IReadOnlyList<Source> sources) => Analyses.GetValue(sources, Analyse).Reading();

    internal static IReadOnlyList<string> Check(string tool, KiCadVerificationLevel level, IReadOnlyList<string> evidence,
        IReadOnlyList<Source> sources, Func<string, string, bool> isTestMethod)
    {
        var problems = new List<string>();
        if (evidence.Count == 0) return [$"{tool} declares verification without evidence."];
        var analysis = Analyses.GetValue(sources, Analyse);
        var nativeSources = analysis.Classes.GetValueOrDefault(NativeJourneyClass) ?? [];
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
                string? refusal = analysis.Verdict(parts[1]);
                if (refusal is null) citesNative = true;
                else problems.Add($"{tool} cites {item}, {refusal}");
                continue;
            }
            if (level == KiCadVerificationLevel.InProcess) continue;
            var files = analysis.Classes.GetValueOrDefault(parts[0]) ?? [];
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

    // One member of a class in the test sources, attributes removed. Text keeps its string literals;
    // Masked is the same range with every literal blanked (interpolation holes stay code). Body is the
    // offset of the body's '{', '=>' or '=' in both, or -1 for a declaration that has none.
    private sealed record Member(string Class, string File, string Name, string Text, string Masked, int Body)
    {
        public bool Expression => Body >= 0 && Body + 1 < Masked.Length && Masked[Body] == '=' && Masked[Body + 1] == '>';
        public string Header => Masked[..(Body < 0 ? Masked.Length : Body)];
        public string MaskedBody => Body < 0 ? "" : Masked[Body..];
    }

    // One declared parameter: its position, name and type, and whether it is passed by value.
    private sealed record Parameter(int Index, string Name, string Type, bool Plain);

    // Why the dispatch to an Inconclusive stub cannot be read; a journey in Excluded cannot reach it.
    private sealed record Unresolved(string Reason, IReadOnlySet<string> Excluded);

    private sealed record Arm(IReadOnlyList<string> Journeys, bool Discard, bool Throws, string? Target, bool Readable);

    // A switch expression: where its switched value starts in the member, the value, and its arms.
    private sealed record SwitchExpression(int Index, string? Value, IReadOnlyList<Arm> Arms);

    private sealed class Analysis
    {
        private readonly Dictionary<string, string?> verdicts = new(StringComparer.Ordinal);
        private readonly Dictionary<Member, Facts> facts = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, string?> external = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> mentions = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, ILookup<string, Member>> classMembers;
        private readonly IReadOnlyList<Member> native;
        private readonly IReadOnlyList<Source> nativeCode;
        private IReadOnlySet<string> stubJourneys = new HashSet<string>();
        private IReadOnlyList<Unresolved> unresolved = [];
        private Regex? stubMention;

        public Analysis(IReadOnlyDictionary<string, Source[]> classes, IReadOnlyDictionary<string, List<Member>> members, IReadOnlyList<Source> nativeCode)
        {
            Classes = classes;
            classMembers = members.ToDictionary(entry => entry.Key, entry => entry.Value.ToLookup(member => member.Name, StringComparer.Ordinal),
                StringComparer.Ordinal);
            native = members.GetValueOrDefault(NativeJourneyClass) ?? [];
            this.nativeCode = nativeCode;
            Members = native.ToLookup(member => member.Name, StringComparer.Ordinal);
            Stubs = native.Where(IsStub).Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, Source[]> Classes { get; }
        public ILookup<string, Member> Members { get; }
        public IReadOnlySet<string> Stubs { get; }

        public StubReading Reading() => new(
            native.Where(IsStub).GroupBy(member => member.File, StringComparer.Ordinal)
                .ToDictionary(file => file.Key, file => file.Sum(member => RaiseSite.Matches(member.Text).Count), StringComparer.Ordinal),
            Stubs, stubJourneys);

        public void ReadDispatch()
        {
            var none = new HashSet<string>(StringComparer.Ordinal);
            var (journeys, open, targets) = VerificationEvidenceRules.ReadDispatch(native, Stubs, RunningJourneyParameters());
            // A stub is reached only through the switch arms read above. Any other mention, such as an
            // if/else dispatch, a direct call or a delegate, could reach it from any journey.
            foreach (string stub in Stubs.Order(StringComparer.Ordinal))
                if (Mentions(stub) > Members[stub].Count() + targets.GetValueOrDefault(stub))
                    open.Add(new($"the Inconclusive lane stub {stub} is reached other than through a NativeJourney switch arm", none));
            // Which fixture members a journey runs is not read, so a call into another test class that
            // can end Inconclusive may be made by any journey.
            foreach (var member in native)
                if (ExternalReach(member) is { } path)
                    open.Add(new($"{member.Name} calls {path}", none));
            stubJourneys = journeys;
            unresolved = open;
            if (Stubs.Count > 0)
                stubMention = new Regex(@"\b(?:" + string.Join("|", Stubs.Order(StringComparer.Ordinal).Select(Regex.Escape)) + @")\b");
        }

        // Null when the cited fixture method is readable evidence, otherwise why it is not.
        public string? Verdict(string method)
        {
            lock (verdicts)
            {
                if (verdicts.TryGetValue(method, out string? known)) return known;
                var declared = Members[method].ToArray();
                // Overloads share the name: any one that cannot be read makes the name unreadable.
                string? verdict = declared.Length == 0
                    ? "whose declaration this check cannot find, so whether it reaches an Inconclusive lane stub cannot be read."
                    : declared.Select(TestVerdict).FirstOrDefault(refusal => refusal is not null);
                verdicts[method] = verdict;
                return verdict;
            }
        }

        private string? TestVerdict(Member test)
        {
            if (IsStub(test)) return "which ends Inconclusive itself: its body raises, catches or tests for AssertInconclusiveException or calls Assert.Inconclusive.";
            if (test.Expression && JourneyRun.Match(test.Masked[(test.Body + 2)..]) is { Success: true } run)
            {
                string journey = run.Groups["journey"].Value;
                if (stubJourneys.Contains(journey)) return $"whose journey NativeJourney.{journey} is still an Inconclusive lane stub.";
                return unresolved.FirstOrDefault(entry => !entry.Excluded.Contains(journey)) is { } open
                    ? $"whose journey NativeJourney.{journey} may reach an Inconclusive lane stub: {open.Reason}."
                    : null;
            }
            return Reach(test);
        }

        // Follows every fixture member the test names, to any depth.
        private string? Reach(Member root)
        {
            var chains = new Dictionary<Member, string>(ReferenceEqualityComparer.Instance) { [root] = root.Name };
            var queue = new Queue<Member>([root]);
            while (queue.TryDequeue(out var member))
            {
                string chain = chains[member];
                var known = FactsOf(member);
                if (!ReferenceEquals(member, root) && known.Stub)
                    return $"which reaches the Inconclusive lane stub {member.Name} through {chain}.";
                if (known.NamesJourney)
                    return ReferenceEquals(member, root)
                        ? "whose body is not one RunNativeSessions call with a named journey, so which journey it runs cannot be read."
                        : $"which reaches {chain}, where {member.Name} names a journey or RunNativeSessions, so which journey it runs cannot be read.";
                if (known.NamedStub is { } stub)
                    return $"which reaches the Inconclusive lane stub {stub} through {chain}.";
                if (known.UnresolvedCall is { } unknown)
                    return $"which reaches {chain}, where {unknown}(...) is neither a {NativeJourneyClass} member nor a local this check can read, so where it leads cannot be read.";
                if (known.External is { } path)
                    return $"which reaches {chain}, where {member.Name} calls {path}.";
                foreach (string name in known.References)
                    foreach (var next in Members[name])
                        if (chains.TryAdd(next, chain + " -> " + next.Name)) queue.Enqueue(next);
            }
            return null;
        }

        private sealed record Facts(bool Stub, bool NamesJourney, string? NamedStub, string? UnresolvedCall, string? External,
            IReadOnlyList<string> References);

        private Facts FactsOf(Member member)
        {
            lock (facts)
            {
                if (facts.TryGetValue(member, out var known)) return known;
                var locals = LocalNames(member.Masked);
                string? unknown = UnqualifiedCall.Matches(member.Masked).Where(call => !IsAttribute(member.Masked, call.Index))
                    .Select(call => call.Groups["name"].Value)
                    .FirstOrDefault(name => !Keywords.Contains(name) && !locals.Contains(name) && !Members.Contains(name));
                string[] references = Reference.Matches(member.Text).Select(reference => reference.Groups["name"].Value)
                    .Where(Members.Contains).Distinct(StringComparer.Ordinal).ToArray();
                known = new(IsStub(member), JourneyMention.IsMatch(member.Text),
                    stubMention?.Matches(member.Text).Select(match => match.Value).FirstOrDefault(name => name != member.Name),
                    unknown, ExternalReach(member), references);
                facts[member] = known;
                return known;
            }
        }

        // Mentions of a fixture member's name in the fixture's code, leaving out journey names:
        // NativeJourney.X and the NativeJourney declaration itself.
        private int Mentions(string name)
        {
            lock (mentions)
            {
                if (mentions.TryGetValue(name, out int known)) return known;
                var mention = new Regex($@"(?<!\bNativeJourney\s*\.\s*)\b{Regex.Escape(name)}\b");
                int count = nativeCode.Sum(source => mention.Matches(source.Text).Count)
                    - native.Where(member => JourneyEnum.IsMatch(member.Header)).Sum(member => mention.Matches(member.Text).Count);
                mentions[name] = count;
                return count;
            }
        }

        // For each fixture member and NativeJourney parameter: null when the parameter always holds the
        // journey being run, otherwise why not. The parameter of RunNativeSessions does when every call
        // of it passes a named journey from a test that nothing else names, or such a parameter of the
        // caller; the parameter of any other member does when every call passes it such a parameter.
        // Read as the greatest fixed point, so a parameter passed on unchanged, even recursively, keeps
        // holding the journey, and one that any call can set to something else does not.
        private Func<Member, string, string?> RunningJourneyParameters()
        {
            var reasons = new Dictionary<Member, Dictionary<string, string?>>(ReferenceEqualityComparer.Instance);
            var parameters = new Dictionary<Member, Parameter[]>(ReferenceEqualityComparer.Instance);
            var calls = new Dictionary<string, List<(Member Caller, IReadOnlyList<string>? Arguments)>>(StringComparer.Ordinal);
            foreach (var member in native)
            {
                var journeyParameters = Parameters(member).Where(parameter => parameter.Type == "NativeJourney").ToArray();
                if (journeyParameters.Length == 0) continue;
                parameters[member] = journeyParameters;
                reasons[member] = journeyParameters.ToDictionary(parameter => parameter.Name, parameter =>
                    !parameter.Plain ? $"{member.Name} does not take {parameter.Name} by value"
                    : !IsStableJourneyParameter(member, parameter.Name) ? $"{member.Name} reassigns or redeclares {parameter.Name}, or jumps with goto"
                    : Members[member.Name].Count() > 1 ? $"{member.Name} is overloaded, so which declaration a call reaches cannot be read"
                    : (string?)null, StringComparer.Ordinal);
                if (!calls.ContainsKey(member.Name))
                    calls[member.Name] = native.SelectMany(caller => CallsOf(caller, member.Name).Select(arguments => (caller, arguments))).ToList();
            }
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var (member, own) in reasons)
                    foreach (var parameter in parameters[member])
                    {
                        if (own[parameter.Name] is not null) continue;
                        var found = calls[member.Name];
                        string? reason = Mentions(member.Name) > Members[member.Name].Count() + found.Count
                            ? $"{member.Name} is named other than in a call, so what it is passed cannot be read" : null;
                        foreach (var (caller, arguments) in found)
                        {
                            if (reason is not null) break;
                            string? argument = arguments is null ? null : ArgumentFor(arguments, parameter);
                            if (argument is null)
                                reason = $"{caller.Name} calls {member.Name} without a readable argument for {parameter.Name}";
                            else if (member.Name == JourneyRunner && JourneyLiteral.IsMatch(argument) && Mentions(caller.Name) == Members[caller.Name].Count())
                                continue;
                            else if (Identifier.IsMatch(argument) && reasons.TryGetValue(caller, out var passed) && passed.TryGetValue(argument, out string? inherited))
                                reason = inherited is null ? null : $"{caller.Name} calls {member.Name} with {argument} for {parameter.Name}, and {inherited}";
                            else
                                reason = $"{caller.Name} calls {member.Name} with {Regex.Replace(argument, @"\s+", " ")} for {parameter.Name}";
                        }
                        if (reason is null) continue;
                        own[parameter.Name] = reason;
                        changed = true;
                    }
            }
            return (member, value) => reasons.TryGetValue(member, out var own) && own.TryGetValue(value, out string? reason)
                ? reason : $"{value} is not a NativeJourney parameter of {member.Name}";
        }

        // The first call member makes into another test class that can end Inconclusive, as the path to
        // where it does, or null.
        private string? ExternalReach(Member member)
        {
            foreach (Match call in QualifiedCall.Matches(member.MaskedBody))
                if (ExternalTarget(call) is { } target && External(target.Class, target.Name) is { } path)
                    return path;
            return null;
        }

        private (string Class, string Name)? ExternalTarget(Match call)
        {
            string owner = call.Groups["class"].Value;
            if (owner == NativeJourneyClass || !classMembers.ContainsKey(owner)) return null;
            return (owner, call.Groups["name"].Success ? call.Groups["name"].Value : owner);
        }

        // Whether calling owner.name can reach a member that raises or catches Inconclusive, followed
        // member by member through its class and the test classes it calls by name; a name this check
        // cannot find stands for every member of its class. Null when it cannot, otherwise the path.
        private string? External(string owner, string name)
        {
            lock (external)
            {
                string key = owner + "." + name;
                if (external.TryGetValue(key, out string? known)) return known;
                var chains = new Dictionary<Member, string>(ReferenceEqualityComparer.Instance);
                var queue = new Queue<Member>();
                void Enqueue(Member next, string chain)
                {
                    if (chains.TryAdd(next, chain)) queue.Enqueue(next);
                }
                void Visit(string type, string called, string? from)
                {
                    var lookup = classMembers[type];
                    string prefix = from is null ? "" : from + " -> ";
                    if (lookup.Contains(called))
                        foreach (var next in lookup[called]) Enqueue(next, prefix + type + "." + called);
                    else
                        foreach (var next in lookup.SelectMany(group => group))
                            Enqueue(next, $"{prefix}{type}.{called}, which this check cannot find, so any {type}.{next.Name}");
                }
                Visit(owner, name, null);
                string? result = null;
                while (result is null && queue.TryDequeue(out var member))
                {
                    string chain = chains[member];
                    if (IsStub(member))
                    {
                        result = $"{chain}, where {member.Class}.{member.Name} raises, catches or tests for Inconclusive";
                        continue;
                    }
                    var own = classMembers[member.Class];
                    foreach (Match reference in Reference.Matches(member.Masked))
                        foreach (var next in own[reference.Groups["name"].Value])
                            Enqueue(next, chain + " -> " + member.Class + "." + next.Name);
                    foreach (Match call in QualifiedCall.Matches(member.MaskedBody))
                        if (ExternalTarget(call) is { } target)
                            Visit(target.Class, target.Name, chain);
                }
                external[key] = result;
                return result;
            }
        }
    }

    private static Analysis Analyse(IReadOnlyList<Source> sources)
    {
        var code = sources.Select(source => source with { Text = StripComments(source.Text) }).ToArray();
        var masked = code.Select(source => Mask(source.Text)).ToArray();
        var classes = new Dictionary<string, List<Source>>(StringComparer.Ordinal);
        var members = new Dictionary<string, List<Member>>(StringComparer.Ordinal);
        var nativeCode = new List<Source>();
        for (int index = 0; index < code.Length; index++)
            foreach (Match declaration in ClassDeclaration.Matches(masked[index]))
            {
                string name = declaration.Groups[1].Value;
                var list = classes.TryGetValue(name, out var existing) ? existing : classes[name] = [];
                if (!list.Contains(code[index])) list.Add(code[index]);
                if (name == NativeJourneyClass && !nativeCode.Contains(code[index])) nativeCode.Add(code[index]);
                int open = masked[index].IndexOf('{', declaration.Index + declaration.Length);
                if (open < 0) continue;
                var own = members.TryGetValue(name, out var found) ? found : members[name] = [];
                own.AddRange(ParseMembers(name, code[index].File, code[index].Text, masked[index], open));
            }
        var analysis = new Analysis(classes.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal), members, nativeCode);
        analysis.ReadDispatch();
        return analysis;
    }

    // A member that names Inconclusive other than in the catch filter that only lets it through.
    private static bool IsStub(Member member)
    {
        var through = new HashSet<int>();
        foreach (Match filter in LetThroughFilter.Matches(member.Masked))
        {
            int close = Math.Min(Matching(member.Masked, filter.Groups["open"].Index), member.Masked.Length);
            string rest = member.Masked[(filter.Index + filter.Length)..close].Trim();
            if (rest.Length == 0 || rest.StartsWith("&&", StringComparison.Ordinal) && TopLevel(rest).IndexOfAny(['|', '?']) < 0)
                through.Add(filter.Groups["type"].Index);
        }
        return InconclusiveMention.Matches(member.Text).Any(mention => !through.Contains(mention.Index));
    }

    // The characters of text outside any bracket.
    private static string TopLevel(string text)
    {
        var output = new System.Text.StringBuilder(text.Length);
        int depth = 0;
        foreach (char c in text)
        {
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0) output.Append(c);
        }
        return output.ToString();
    }

    // The members of the class body that opens at open: each ends at its own ';' or at the '}' of its
    // block, whichever comes first outside brackets. Attributes are left out of the member.
    private static IEnumerable<Member> ParseMembers(string owner, string file, string code, string masked, int open)
    {
        int i = open + 1;
        while (true)
        {
            while (i < masked.Length && char.IsWhiteSpace(masked[i])) i++;
            if (i >= masked.Length || masked[i] == '}') yield break;
            int start = i;
            while (start < masked.Length && masked[start] == '[')
            {
                start = Matching(masked, start) + 1;
                while (start < masked.Length && char.IsWhiteSpace(masked[start])) start++;
            }
            int body = -1, end = -1, depth = 0;
            for (i = start; i < masked.Length && end < 0; i++)
            {
                char c = masked[i];
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']') depth--;
                else if (depth != 0) continue;
                else if (c == ';') end = i + 1;
                else if (c == '}') end = i;
                else if (c == '{')
                {
                    body = i;
                    int after = Matching(masked, i) + 1, next = after;
                    while (next < masked.Length && char.IsWhiteSpace(masked[next])) next++;
                    // A property initializer follows its accessors: { get; } = value;
                    end = next < masked.Length && masked[next] == '=' && (next + 1 >= masked.Length || masked[next + 1] != '=')
                        ? StatementEnd(masked, next) : after;
                }
                else if (c == '=' && i + 1 < masked.Length && masked[i + 1] != '=' && (i == 0 || masked[i - 1] is not ('!' or '<' or '>' or '=')))
                {
                    body = i;
                    end = StatementEnd(masked, i);
                }
            }
            if (end < 0) end = masked.Length;
            if (end <= start) yield break;
            string header = masked[start..(body < 0 ? end : body)];
            yield return new(owner, file, MemberName(header), code[start..end], masked[start..end], body < 0 ? -1 : body - start);
            i = end;
        }
    }

    // A method's name precedes its parameter list. Types and fields name themselves.
    private static string MemberName(string header)
    {
        if (ParameterList(header) is { } method) return method.Name;
        if (TypeDeclaration.Match(header) is { Success: true } type) return type.Groups["name"].Value;
        return LastIdentifier.Match(header) is { Success: true } last ? last.Groups["name"].Value : "";
    }

    // The first top-level '(' of a header that follows a name which is not a modifier (a tuple return
    // type follows a modifier): the method's name and where its parameter list opens.
    private static (string Name, int Open)? ParameterList(string header)
    {
        int angle = 0, depth = 0;
        for (int i = 0; i < header.Length; i++)
        {
            char c = header[i];
            if (c == '<') angle++;
            else if (c == '>') angle--;
            else if (c == ')') depth--;
            else if (c == '(' && depth++ == 0 && angle == 0
                && TrailingName.Match(header[..i]) is { Success: true } name && !Modifiers.Contains(name.Groups["name"].Value))
                return (name.Groups["name"].Value, i);
        }
        return null;
    }

    // The declared parameters of a method member, in order; empty for any other member.
    private static Parameter[] Parameters(Member member)
    {
        string header = member.Header;
        if (ParameterList(header) is not { } list) return [];
        int close = Math.Min(Matching(header, list.Open), header.Length), depth = 0, start = list.Open + 1, index = 0;
        var result = new List<Parameter>();
        void Add(string part)
        {
            part = part.Trim();
            if (part.Length == 0) return;
            if (ParameterDeclaration.Match(part) is { Success: true } parameter)
                result.Add(new(index, parameter.Groups["name"].Value, Regex.Replace(parameter.Groups["type"].Value, @"\s+", ""),
                    !parameter.Groups["modifier"].Success));
            index++;
        }
        for (int i = list.Open + 1; i < close; i++)
        {
            char c = header[i];
            if (c is '(' or '[' or '{' or '<') depth++;
            else if (c is ')' or ']' or '}' or '>') depth--;
            else if (c == ',' && depth == 0)
            {
                Add(header[start..i]);
                start = i + 1;
            }
        }
        Add(header[start..close]);
        return [.. result];
    }

    // The argument lists of the caller's calls of the fixture member name, unqualified or through this
    // or the fixture class, in its body; null for a call whose arguments cannot be split with
    // certainty (a '<' or '>' may be a generic type argument list spanning a comma).
    private static IEnumerable<IReadOnlyList<string>?> CallsOf(Member caller, string name)
    {
        if (caller.Body < 0) yield break;
        foreach (Match match in Regex.Matches(caller.Masked, $@"\b{Regex.Escape(name)}\s*\("))
        {
            if (match.Index < caller.Body) continue;
            int before = match.Index - 1;
            while (before >= 0 && char.IsWhiteSpace(caller.Masked[before])) before--;
            if (before >= 0 && caller.Masked[before] == '.')
            {
                var qualifier = LastIdentifier.Match(caller.Masked[..before]);
                if (!qualifier.Success || qualifier.Groups["name"].Value is not ("this" or NativeJourneyClass)) continue;
            }
            int open = match.Index + match.Length - 1;
            string[] arguments = [.. TopLevelParts(caller.Masked, open + 1, Matching(caller.Masked, open))];
            yield return arguments.Any(argument => argument.IndexOfAny(['<', '>']) >= 0) ? null : arguments;
        }
    }

    // The argument a call passes for a parameter, by name or by position; null when it passes none.
    private static string? ArgumentFor(IReadOnlyList<string> arguments, Parameter parameter)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            if (NamedArgument.Match(arguments[i]) is { Success: true } named)
            {
                if (named.Groups["name"].Value == parameter.Name) return named.Groups["value"].Value.Trim();
            }
            else if (i == parameter.Index) return arguments[i];
        }
        return null;
    }

    // Whether the name at index opens an attribute ([Name(...)] or [target: Name(...)]) that decorates
    // a declaration, rather than a call inside a collection expression or an indexer.
    private static bool IsAttribute(string masked, int index)
    {
        int i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(masked[i])) i--;
        if (i >= 0 && masked[i] == ':')
        {
            i--;
            while (i >= 0 && char.IsWhiteSpace(masked[i])) i--;
            while (i >= 0 && (char.IsLetterOrDigit(masked[i]) || masked[i] == '_')) i--;
            while (i >= 0 && char.IsWhiteSpace(masked[i])) i--;
        }
        if (i < 0 || masked[i] != '[') return false;
        int open = i--;
        while (i >= 0 && char.IsWhiteSpace(masked[i])) i--;
        if (i >= 0 && masked[i] is not ('(' or ',' or ';' or '{' or '}' or ']')) return false;
        int after = Matching(masked, open) + 1;
        while (after < masked.Length && char.IsWhiteSpace(masked[after])) after++;
        return after < masked.Length && (char.IsLetter(masked[after]) || masked[after] is '_' or '[');
    }

    // Names declared inside a member: local functions, typed variables and parameters, deconstructed
    // variables, lambda parameters.
    private static HashSet<string> LocalNames(string masked)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in new[] { LocalFunction, LocalVariable })
            foreach (Match match in declaration.Matches(masked))
                if (!NotTypes.Contains(match.Groups["type"].Value)) names.Add(match.Groups["name"].Value);
        foreach (Match match in LambdaParameter.Matches(masked)) names.Add(match.Groups["name"].Value);
        foreach (Match match in LambdaParameters.Matches(masked).Concat(Deconstruction.Matches(masked)))
            foreach (string parameter in match.Groups["list"].Value.Split(','))
                if (LastIdentifier.Match(parameter.Trim()) is { Success: true } last) names.Add(last.Groups["name"].Value);
        return names;
    }

    // Reads the dispatch switches member by member. An arm naming a journey sends that journey to its
    // target. A default arm is reached only by the journeys that every earlier limiting switch lets
    // through and that its own switch does not name; with no limiting switch, every journey its switch
    // does not name may reach it. Both need the switched value to be a parameter that always holds the
    // journey being run (notRunning says why one does not); otherwise any journey may reach the stub.
    private static (HashSet<string> StubJourneys, List<Unresolved> Unresolved, Dictionary<string, int> ArmTargets) ReadDispatch(
        IReadOnlyList<Member> members, IReadOnlySet<string> stubs, Func<Member, string, string?> notRunning)
    {
        var journeys = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<Unresolved>();
        var targets = new Dictionary<string, int>(StringComparer.Ordinal);
        var none = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var switches = Switches(member.Masked);
            for (int index = 0; index < switches.Count; index++)
            {
                var current = switches[index];
                var named = current.Arms.Where(arm => arm.Readable).SelectMany(arm => arm.Journeys).ToHashSet(StringComparer.Ordinal);
                foreach (var arm in current.Arms)
                {
                    if (!arm.Readable || arm.Target is null) continue;
                    targets[arm.Target] = targets.GetValueOrDefault(arm.Target) + 1;
                    if (!stubs.Contains(arm.Target)) continue;
                    string? notDispatch = current.Value is { } value ? notRunning(member, value) : "it switches over an expression";
                    if (notDispatch is not null)
                    {
                        unresolved.Add(new($"a switch in {member.Name} over {current.Value ?? "an expression"} leads to the stub {arm.Target}, " +
                            $"and only a switch over a parameter that always holds the journey being run can be read: {notDispatch}", none));
                        continue;
                    }
                    if (!arm.Discard) { journeys.UnionWith(arm.Journeys); continue; }
                    // Earlier limits have run only when this switch runs directly in the member's block.
                    var filters = RunsInMemberBlock(member, current.Index)
                        ? switches.Take(index).Where(earlier => IsLimit(member, earlier, current.Value!))
                            .Select(earlier => earlier.Arms.SelectMany(other => other.Journeys).ToHashSet(StringComparer.Ordinal)).ToArray()
                        : null;
                    if (filters is null or { Length: 0 })
                    {
                        string where = filters is null ? " from inside a lambda, a local function or a nested block" : "";
                        unresolved.Add(new($"a default arm in {member.Name} leads to the stub {arm.Target}{where}, " +
                            $"and no earlier switch over {current.Value} there limits the journeys that reach it", named));
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

    // The member's own NativeJourney parameter, which its body never assigns, redeclares or jumps around.
    private static bool IsStableJourneyParameter(Member member, string value)
    {
        if (!Regex.IsMatch(value, @"^[A-Za-z_]\w*$") || member.Body < 0) return false;
        string name = Regex.Escape(value), body = member.MaskedBody;
        if (!Regex.IsMatch(member.Header, $@"\bNativeJourney\s+{name}\s*[,)=]")) return false;
        bool assigned = Regex.IsMatch(body, $@"(?<![\w.]){name}\s*(?:[-+*/%&|^]|\?\?|<<|>>)?=(?![=>])|\b(?:ref|out)\s+{name}\b" +
            $@"|(?<![\w.]){name}\s*(?:\+\+|--)|(?:\+\+|--)\s*{name}\b");
        return !assigned && !LocalNames(body).Contains(value) && !Regex.IsMatch(body, @"\bgoto\b");
    }

    // An earlier switch limits a default arm only when it switches over the same value, is a whole
    // assignment statement of the member's block body (never conditional, as after else or do, and
    // never nested), names journeys in every arm it has, and throws for any other.
    private static bool IsLimit(Member member, SwitchExpression earlier, string value)
    {
        if (earlier.Value != value || member.Expression || member.Body < 0) return false;
        if (!earlier.Arms.All(arm => arm.Readable) || !earlier.Arms.Any(arm => arm.Journeys.Count > 0)
            || !earlier.Arms.Any(arm => arm.Discard) || !earlier.Arms.Where(arm => arm.Discard).All(arm => arm.Throws))
            return false;
        string before = member.Masked[..earlier.Index];
        if (before.Count(c => c == '{') - before.Count(c => c == '}') != 1) return false;
        int boundary = before.LastIndexOfAny([';', '{', '}']);
        return boundary >= member.Body && AssigningStatement.Match(before[(boundary + 1)..]) is { Success: true } statement
            && !NotTypes.Contains(statement.Groups["type"].Value);
    }

    // Whether the switch whose value starts at index runs directly in the member's block: at the
    // block's own level, in a statement that declares no lambda or local function before it. Only
    // then has every whole statement before it in that block run first.
    private static bool RunsInMemberBlock(Member member, int index)
    {
        if (member.Body < 0 || member.Masked[member.Body] != '{') return false;
        int braces = 0, brackets = 0, start = member.Body + 1;
        for (int i = member.Body; i < index; i++)
        {
            char c = member.Masked[i];
            if (c == '{') braces++;
            else if (c == '}') braces--;
            else if (braces != 1) continue;
            else if (c is '(' or '[') brackets++;
            else if (c is ')' or ']') brackets--;
            else if (c == ';' && brackets == 0) start = i + 1;
        }
        if (braces != 1) return false;
        // Nested blocks, initializers and earlier switch bodies of the statement are left out, so a
        // remaining => is a lambda or an expression-bodied local function that encloses the switch.
        var statement = new System.Text.StringBuilder();
        int depth = 0;
        for (int i = start; i < index; i++)
        {
            char c = member.Masked[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (depth == 0) statement.Append(c);
        }
        return !statement.ToString().Contains("=>", StringComparison.Ordinal);
    }

    // The switch expressions of one member in source order, read from its masked text.
    private static List<SwitchExpression> Switches(string masked)
    {
        var result = new List<SwitchExpression>();
        foreach (Match start in SwitchStart.Matches(masked))
        {
            int open = start.Index + start.Length - 1, close = Matching(masked, open);
            var arms = TopLevelParts(masked, open + 1, close).Select(ReadArm).ToArray();
            var value = start.Groups["value"];
            result.Add(new(value.Success ? value.Index : start.Index, value.Success ? Regex.Replace(value.Value, @"\s+", "") : null, arms));
        }
        return result;
    }

    private static Arm ReadArm(string arm)
    {
        var journey = JourneyArm.Match(arm);
        var discard = journey.Success ? Match.Empty : DiscardArm.Match(arm);
        if (!journey.Success && !discard.Success) return new([], false, false, null, false);
        string result = arm[(journey.Success ? journey.Length : discard.Length)..];
        string[] journeys = journey.Success
            ? JourneyName.Matches(journey.Groups["pattern"].Value).Select(name => name.Groups[1].Value).ToArray()
            : [];
        return new(journeys, !journey.Success, Regex.IsMatch(result, @"^\s*throw\b"),
            ArmTarget.Match(result) is { Success: true } target ? target.Groups["target"].Value : null, true);
    }

    // The comma-separated parts between from and to that are outside any bracket, trimmed.
    private static IEnumerable<string> TopLevelParts(string masked, int from, int to)
    {
        int depth = 0, start = from;
        to = Math.Min(to, masked.Length);
        for (int i = from; i <= to; i++)
        {
            if (i < to && masked[i] is '(' or '[' or '{') depth++;
            else if (i < to && masked[i] is ')' or ']' or '}') depth--;
            else if (i == to || (masked[i] == ',' && depth == 0))
            {
                string part = masked[start..i].Trim();
                if (part.Length > 0) yield return part;
                start = i + 1;
            }
        }
    }

    // The index of the bracket that closes the one at open, in masked text; the end when unclosed.
    private static int Matching(string masked, int open)
    {
        int depth = 0;
        for (int i = open; i < masked.Length; i++)
        {
            if (masked[i] is '(' or '[' or '{') depth++;
            else if (masked[i] is ')' or ']' or '}' && --depth == 0) return i;
        }
        return masked.Length;
    }

    // The index just past the ';' that ends the statement or declaration running from start.
    private static int StatementEnd(string masked, int start)
    {
        int depth = 0;
        for (int i = start; i < masked.Length; i++)
        {
            if (masked[i] is '(' or '[' or '{') depth++;
            else if (masked[i] is ')' or ']' or '}' && --depth < 0) return i;
            else if (masked[i] == ';' && depth == 0) return i + 1;
        }
        return masked.Length;
    }

    private const byte CodeCharacter = 0, LiteralCharacter = 1, CommentCharacter = 2;

    // Removes // and /* */ comments while keeping string and character literals intact, so a "//"
    // inside a string (an ipc:/// endpoint) is not a comment and a commented-out call is not a call.
    internal static string StripComments(string text)
    {
        var kinds = Classify(text);
        var output = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
            if (kinds[i] != CommentCharacter || text[i] == '\n') output.Append(text[i]);
        return output.ToString();
    }

    // The same text with every string and character literal, quotes and prefixes included, replaced
    // by a character that C# code never uses (a backtick), so text between two interpolation holes
    // cannot join their code; the code inside a hole stays. Line breaks are kept.
    internal const char Blank = '`';

    internal static string Mask(string text)
    {
        var kinds = Classify(text);
        var output = text.ToCharArray();
        for (int i = 0; i < output.Length; i++)
            if (kinds[i] != CodeCharacter && output[i] != '\n') output[i] = Blank;
        return new string(output);
    }

    private static byte[] Classify(string text)
    {
        var kinds = new byte[text.Length];
        ClassifyCode(text, 0, kinds, hole: false);
        return kinds;
    }

    // Classifies code from start to the end, or inside an interpolation hole up to the unmatched '}'
    // that closes it, whose index is returned without being classified.
    private static int ClassifyCode(string text, int start, byte[] kinds, bool hole)
    {
        int depth = 0, i = start;
        while (i < text.Length)
        {
            char c = text[i], next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') kinds[i++] = CommentCharacter;
                continue;
            }
            if (c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                while (i < end) kinds[i++] = CommentCharacter;
                continue;
            }
            if (c == '\'')
            {
                int end = i + 1;
                while (end < text.Length && text[end] != '\'' && text[end] != '\n') end += text[end] == '\\' ? 2 : 1;
                end = Math.Min(end + 1, text.Length);
                while (i < end) kinds[i++] = LiteralCharacter;
                continue;
            }
            int quote = i, dollars = 0;
            bool verbatim = false;
            while (quote < text.Length && text[quote] is '$' or '@')
            {
                if (text[quote] == '$') dollars++; else verbatim = true;
                quote++;
            }
            if (quote < text.Length && text[quote] == '"')
            {
                i = ClassifyString(text, i, quote, dollars, verbatim, kinds);
                continue;
            }
            if (hole)
            {
                if (c == '{') depth++;
                else if (c == '}' && depth-- == 0) return i;
            }
            kinds[i++] = CodeCharacter;
        }
        return i;
    }

    // Classifies the string literal whose prefix starts at start and whose quotes start at quote;
    // returns the index just past it. Interpolation holes are classified as code.
    private static int ClassifyString(string text, int start, int quote, int dollars, bool verbatim, byte[] kinds)
    {
        int i = start;
        void Literal(int end) { end = Math.Min(end, text.Length); while (i < end) kinds[i++] = LiteralCharacter; }
        void Hole() { i = ClassifyCode(text, i, kinds, hole: true); }
        int run = 0;
        while (quote + run < text.Length && text[quote + run] == '"') run++;
        if (!verbatim && run >= 3)
        {
            // Raw string: it ends at the next run of at least as many quotes; with n dollars, a run of
            // at least n braces opens a hole and n braces close it.
            Literal(quote + run);
            while (i < text.Length)
            {
                if (text[i] == '"')
                {
                    int closing = 0;
                    while (i + closing < text.Length && text[i + closing] == '"') closing++;
                    Literal(i + closing);
                    if (closing >= run) return i;
                    continue;
                }
                if (dollars > 0 && text[i] == '{')
                {
                    int braces = 0;
                    while (i + braces < text.Length && text[i + braces] == '{') braces++;
                    Literal(i + braces);
                    if (braces < dollars) continue;
                    Hole();
                    Literal(i + dollars);
                    continue;
                }
                Literal(i + 1);
            }
            return i;
        }
        Literal(quote + 1);
        while (i < text.Length)
        {
            char c = text[i], next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (verbatim && c == '"')
            {
                if (next == '"') { Literal(i + 2); continue; }
                Literal(i + 1);
                return i;
            }
            if (!verbatim)
            {
                if (c == '\\') { Literal(i + 2); continue; }
                if (c == '"') { Literal(i + 1); return i; }
                if (c == '\n' && dollars == 0) return i;
            }
            if (dollars > 0 && c == '{')
            {
                if (next == '{') { Literal(i + 2); continue; }
                Literal(i + 1);
                Hole();
                Literal(i + 1);
                continue;
            }
            if (dollars > 0 && c == '}' && next == '}') { Literal(i + 2); continue; }
            Literal(i + 1);
        }
        return i;
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
