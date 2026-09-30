using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeIntakeRegistryTests
{
    [TestMethod]
    public async Task OwnershipIsReservedBeforeDiscoveryAndIndependentStartsDoNotSerialize()
    {
        int started = 0, cancelled = 0;
        await using var registry = new NativeIntakeRegistry(async (_, _, token) =>
        {
            Interlocked.Increment(ref started);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new AssertFailedException("Only cancellation can finish this discovery."); }
            finally { Interlocked.Increment(ref cancelled); }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string first = Guid.NewGuid().ToString("D"), second = Guid.NewGuid().ToString("D");
        string root = Path.Combine(Path.GetTempPath(), "native-intake-registry-" + Guid.NewGuid().ToString("N"));
        var a = registry.StartAsync(first, Path.Combine(root, "a.json"), deadline.Token);
        var b = registry.StartAsync(second, Path.Combine(root, "b.json"), deadline.Token);
        Assert.AreEqual(2, started);
        string aId = registry.List(first).Single().IntakeId;
        string bId = registry.List(second).Single().IntakeId;
        Assert.AreEqual(DesignNativeIntakePhase.Starting, registry.List(first).Single().Status.Phase);
        Assert.AreEqual("native_intake_ownership_conflict", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            registry.StartAsync(second, Path.Combine(root, "a.json"), deadline.Token))).Code);
        Assert.AreEqual("unknown_native_intake", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            registry.GetAsync(second, aId, deadline.Token))).Code);
        Assert.AreEqual(DesignNativeIntakePhase.Stopped, (await registry.StopAsync(first, aId)).Phase);
        await Assert.ThrowsAsync<OperationCanceledException>(() => a);
        Assert.AreEqual(1, cancelled);
        Assert.IsFalse(b.IsCompleted);
        Assert.IsEmpty(registry.List(first));
        Assert.AreEqual(bId, registry.List(second).Single().IntakeId);
        await registry.DisposeAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => b);
        Assert.AreEqual(2, cancelled);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => registry.StartAsync(first, Path.Combine(root, "c.json"), deadline.Token));
    }

    [TestMethod]
    public async Task FailedDiscoveryReleasesOwnershipAndToolsReturnActionableErrors()
    {
        int attempts = 0;
        await using var registry = new NativeIntakeRegistry((_, _, _) =>
        { ++attempts; return Task.FromException<DesignNativeIntakeSession>(new AutomationException("incompatible_native", "Fixture peer cannot attach.")); });
        var tools = new NativeIntakeTools(registry);
        string instance = Guid.NewGuid().ToString("D");
        string path = Path.Combine(Path.GetTempPath(), "intake-failure-" + Guid.NewGuid().ToString("N") + ".json");
        for (int attempt = 0; attempt < 2; ++attempt)
        {
            var result = await tools.Start(instance, path, CancellationToken.None);
            Assert.IsTrue(result.IsError == true);
            Assert.AreEqual("incompatible_native", result.StructuredContent!.Value.GetProperty("errorCode").GetString());
            Assert.IsEmpty(registry.List(instance));
        }
        Assert.AreEqual(2, attempts);
        Assert.IsTrue((await tools.List("", CancellationToken.None)).IsError == true);
        Assert.IsTrue((await tools.Stop(instance, "unknown", CancellationToken.None)).IsError == true);
        Assert.IsTrue((await tools.Wait(instance, "unknown", CancellationToken.None)).IsError == true);
        Assert.IsTrue((await tools.Resume(instance, "unknown", 0, CancellationToken.None)).IsError == true);
    }
}
