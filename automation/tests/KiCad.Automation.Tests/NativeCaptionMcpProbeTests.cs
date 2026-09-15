using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeCaptionMcpProbeTests
{
    public TestContext TestContext { get; set; } = null!;

    private static readonly string[] RequiredTools = ["kicad_instance_attach", "kicad_instance_reattach",
        "kicad_schematic_observe", "kicad_instance_reconnect_after_update"];

    [TestMethod]
    public async Task CapabilityCheckAcceptsPaginatedToolsWithoutCallingThem()
    {
        var client = new CatalogClient(cursor => Task.FromResult(cursor switch
        {
            null => Catalog(RequiredTools[..2], "remaining"),
            "remaining" => Catalog([.. RequiredTools[2..], "unrelated_tool"]),
            _ => throw new AssertFailedException("Unexpected cursor")
        }));
        await NativeCaptionMcpProbe.RequireCaptionToolsAsync(client, CancellationToken.None);
        CollectionAssert.AreEqual(new string?[] { null, "remaining" }, client.Cursors.ToArray());
        Assert.AreEqual(0, client.ToolCalls);
    }

    [TestMethod]
    [DataRow("kicad_instance_attach")]
    [DataRow("kicad_instance_reattach")]
    [DataRow("kicad_schematic_observe")]
    [DataRow("kicad_instance_reconnect_after_update")]
    public async Task MissingCapabilityFailsBeforeAnyDesignToolAndDisposesTheClient(string missing)
    {
        var client = new CatalogClient(_ => Task.FromResult(Catalog(RequiredTools.Where(x => x != missing))));
        await using var probe = Probe(client);
        var error = await Assert.ThrowsAsync<AssertFailedException>(() => probe.StartAsync(typeof(NativeCaptionMcpProbeTests).Assembly.Location));
        StringAssert.Contains(error.Message, missing);
        Assert.AreEqual(0, client.ToolCalls);
        Assert.AreEqual(1, client.Disposals);
        Assert.AreEqual(0, probe.ReconnectedDesigns);
        Assert.IsFalse(probe.ServerRestartVerified);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"tools\":{}}")]
    [DataRow("{\"tools\":[{}]}")]
    [DataRow("{\"tools\":[{\"name\":7}]}")]
    [DataRow("{\"tools\":[{\"name\":\" \"}]}")]
    [DataRow("{\"tools\":[],\"nextCursor\":7}")]
    [DataRow("{\"tools\":[],\"nextCursor\":\"\"}")]
    public async Task MalformedCatalogDoesNotBecomeSuccessfulPreflight(string json)
    {
        var client = new CatalogClient(_ => Task.FromResult(JsonSerializer.Deserialize<JsonElement>(json)));
        await Assert.ThrowsAsync<AssertFailedException>(() => NativeCaptionMcpProbe.RequireCaptionToolsAsync(client, CancellationToken.None));
        Assert.AreEqual(0, client.ToolCalls);
    }

    [TestMethod]
    public async Task DuplicateNamesAndCursorLoopsFailRatherThanMaskMissingTools()
    {
        var duplicates = new CatalogClient(_ => Task.FromResult(Catalog([.. RequiredTools, RequiredTools[0]])));
        await Assert.ThrowsAsync<AssertFailedException>(() => NativeCaptionMcpProbe.RequireCaptionToolsAsync(duplicates, CancellationToken.None));
        var loop = new CatalogClient(_ => Task.FromResult(Catalog([], "again")));
        await Assert.ThrowsAsync<AssertFailedException>(() => NativeCaptionMcpProbe.RequireCaptionToolsAsync(loop, CancellationToken.None));
        Assert.AreEqual(2, loop.Cursors.Count);
    }

    [TestMethod]
    public async Task FailedDiscoveryCanRecoverWithANewClientWithoutLeakingEither()
    {
        var failed = new CatalogClient(_ => throw new IOException("Catalog transport failed"));
        var healthy = new CatalogClient(_ => Task.FromResult(Catalog(RequiredTools)));
        int starts = 0;
        await using (var probe = new NativeCaptionMcpProbe("unused", "unused", CancellationToken.None,
            (_, _) => Task.FromResult<IMcpToolClient>(starts++ == 0 ? failed : healthy)))
        {
            await Assert.ThrowsAsync<IOException>(() => probe.StartAsync(typeof(NativeCaptionMcpProbeTests).Assembly.Location));
            Assert.AreEqual(1, failed.Disposals);
            await probe.StartAsync(typeof(NativeCaptionMcpProbeTests).Assembly.Location);
            Assert.AreEqual(0, healthy.Disposals);
        }
        Assert.AreEqual(1, healthy.Disposals);
        Assert.AreEqual(0, failed.ToolCalls + healthy.ToolCalls);
    }

    [TestMethod]
    public async Task CancellationDisposesTheNewClientBeforeAnyMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new CatalogClient(_ => Task.FromResult(Catalog(RequiredTools)));
        await using var probe = new NativeCaptionMcpProbe("unused", "unused", cancellation.Token, (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<IMcpToolClient>(client);
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.StartAsync(typeof(NativeCaptionMcpProbeTests).Assembly.Location));
        Assert.AreEqual(0, client.Cursors.Count);
        Assert.AreEqual(0, client.ToolCalls);
        Assert.AreEqual(1, client.Disposals);
    }

    [TestMethod]
    public async Task CleanupFailureRetainsTheOriginalCapabilityFailure()
    {
        var client = new CatalogClient(_ => Task.FromResult(Catalog([]))) { FailDisposal = true };
        await using var probe = Probe(client);
        var error = await Assert.ThrowsAsync<AggregateException>(() => probe.StartAsync(typeof(NativeCaptionMcpProbeTests).Assembly.Location));
        Assert.AreEqual(2, error.InnerExceptions.Count);
        Assert.IsInstanceOfType<AssertFailedException>(error.InnerExceptions[0]);
        Assert.IsInstanceOfType<IOException>(error.InnerExceptions[1]);
        Assert.AreEqual(1, client.Disposals);
    }

    [TestMethod]
    public async Task ActualCompiledStdioServerPassesCapabilityPreflightAndShutsDownNormally()
    {
        string root = Directory.CreateTempSubdirectory("kicad-caption-catalog-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        StdioMcpFixture? child = null;
        try
        {
            await using (var probe = new NativeCaptionMcpProbe(root, root, deadline.Token, async (_, _) =>
                child = await StdioMcpFixture.StartAsync(root, Path.Combine(root, "mcp.stderr.log"), deadline.Token)))
                await probe.StartAsync(typeof(NativeCaptionMcpProbeTests).Assembly.Location);
            Assert.IsNotNull(child);
            Assert.IsFalse(child.ForcedTermination);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeWindowsMcpCapabilities")]
    public async Task ActualWindowsClientChecksLiveCapabilitiesAndClosesItsOwnedProcess()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires the native Windows MCP client."); return; }
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "KiCad.Automation.slnx")))
            repository = repository.Parent;
        Assert.IsNotNull(repository);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string executable = Path.Combine(repository.FullName, "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.exe");
        Assert.IsTrue(File.Exists(executable), "Build the actual Windows MCP apphost before this check.");
        string scratch = Directory.CreateTempSubdirectory("kicad-win-catalog-").FullName;
        string state = Path.Combine(scratch, "registry");
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "windows-mcp-capabilities")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var job = new WindowsProcessJob();
        try
        {
            await WindowsFixtureCleanup.PreserveFailuresAsync(async () =>
            {
                await using (var probe = new NativeCaptionMcpProbe(state, evidence, deadline.Token, async (path, name) =>
                    await WindowsInstalledPackageTests.Mcp.Start(path, scratch, state, evidence, name, job, deadline.Token)))
                    await probe.StartAsync(executable);
                using var request = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "caption-mcp-0-mcp-request.json"), deadline.Token));
                Assert.AreEqual("tools/list", request.RootElement.GetProperty("method").GetString());
                Assert.AreEqual("received", request.RootElement.GetProperty("phase").GetString());
                using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "caption-mcp-0-mcp-capture.json"), deadline.Token));
                // Reader cancellation is intentional when an inherited writer remains;
                // require completed capture and released handles, not an EOF scheduling race.
                await using var log = new FileStream(Path.Combine(evidence, "caption-mcp-0-mcp.stderr.log"),
                    FileMode.Open, FileAccess.Read, FileShare.None);
                Assert.AreEqual(capture.RootElement.GetProperty("bytes").GetInt64(), log.Length);
            }, async () =>
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await job.StopAndWaitAsync(cleanup.Token);
                await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(scratch);
            });
        }
        finally
        {
            foreach (string file in Directory.GetFiles(evidence)) TestContext.AddResultFile(file);
        }
    }

    private static NativeCaptionMcpProbe Probe(CatalogClient client) => new("unused", "unused", CancellationToken.None,
        (_, _) => Task.FromResult<IMcpToolClient>(client));

    private static JsonElement Catalog(IEnumerable<string> names, string? nextCursor = null) =>
        JsonSerializer.SerializeToElement(new { tools = names.Select(name => new { name }), nextCursor });

    private sealed class CatalogClient(Func<string?, Task<JsonElement>> list) : IMcpToolClient
    {
        public List<string?> Cursors { get; } = [];
        public int ToolCalls { get; private set; }
        public int Disposals { get; private set; }
        public bool FailDisposal { get; init; }
        public Task<JsonElement> ListTools(string? cursor) { Cursors.Add(cursor); return list(cursor); }
        public Task<JsonElement> Tool(string name, object arguments) { ToolCalls++; throw new AssertFailedException("Capability preflight must not call design tools."); }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return FailDisposal ? ValueTask.FromException(new IOException("Fixture cleanup failed")) : ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    [DataRow("native_status_4")]
    [DataRow("native_status_7")]
    public void NativeReadinessErrorsWorkInBothPublishedResponseShapes(string code)
    {
        var structured = JsonSerializer.SerializeToElement(new { isError = true, structuredContent = new { code } });
        var content = JsonSerializer.SerializeToElement(new { isError = true, content = new[]
        { new { type = "text", text = JsonSerializer.Serialize(new { code, message = "The canvas has no completed render to capture" }) } } });
        Assert.IsTrue(NativeCaptionMcpProbe.RenderPending(structured));
        Assert.IsTrue(NativeCaptionMcpProbe.RenderPending(content));
    }

    [TestMethod]
    [DataRow("{\"isError\":true,\"structuredContent\":{\"code\":\"native_status_3\"}}")]
    [DataRow("{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"native_status_4 is mentioned but not a typed result\"}]}")]
    [DataRow("{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"{}\"},{\"type\":\"text\",\"text\":\"{}\"}]}")]
    [DataRow("{\"isError\":false,\"structuredContent\":{\"code\":\"native_status_4\"}}")]
    [DataRow("{\"isError\":true,\"structuredContent\":{\"code\":4}}")]
    public void OtherErrorsAndAmbiguousContentDoNotRetry(string json)
    {
        using var result = JsonDocument.Parse(json);
        Assert.IsFalse(NativeCaptionMcpProbe.RenderPending(result.RootElement));
    }
}
