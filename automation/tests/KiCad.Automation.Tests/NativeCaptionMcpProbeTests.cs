using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeCaptionMcpProbeTests
{
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
