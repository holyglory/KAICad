using System.Diagnostics;
using System.Text.Json;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SyncHarnessProcessTests
{
    internal static ProcessStartInfo StartInfo(string stage = "none", string? marker = null)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KiCad.Automation.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("The compiled source harness requires its automation checkout.");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add(Path.Combine(root.FullName, "tests", "KiCad.Automation.SyncHarness", "bin", configuration,
            "net10.0", "KiCad.Automation.SyncHarness.dll"));
        start.Environment["KICAD_SYNC_HARNESS_PAUSE_STAGE"] = stage;
        if (marker is null) start.Environment.Remove("KICAD_SYNC_HARNESS_PAUSE_MARKER");
        else start.Environment["KICAD_SYNC_HARNESS_PAUSE_MARKER"] = marker;
        return start;
    }

    [TestMethod]
    public async Task QualificationHostUsesRealStdioAndTheServiceValidationBoundary()
    {
        string root = Directory.CreateTempSubdirectory("sync-host-contract-").FullName;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var host = await StdioMcpFixture.StartAsync(StartInfo(), Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            var tools = await host.ListTools(null);
            Assert.IsTrue(tools.GetProperty("tools").EnumerateArray().Any(t => t.GetProperty("name").GetString() == "kicad_design_sync_apply"));
            var result = await host.Tool("kicad_design_sync_apply", new
            {
                instanceId = Guid.NewGuid().ToString("D"), recoveryPath = Path.Combine(root, "missing.json"),
                designPath = Path.Combine(root, "design.xml"), expectedRevisionToken = "missing", operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsTrue(result.GetProperty("isError").GetBoolean());
            Assert.AreEqual("missing_design_recovery", result.GetProperty("structuredContent").GetProperty("errorCode").GetString());
            Assert.IsFalse(File.Exists(Path.Combine(root, "design.xml")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "missing.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task WaitForMarkerAsync(string path, CancellationToken token)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        watcher.Created += (_, _) => { if (File.Exists(path)) ready.TrySetResult(); };
        watcher.Changed += (_, _) => { if (File.Exists(path)) ready.TrySetResult(); };
        watcher.Renamed += (_, _) => { if (File.Exists(path)) ready.TrySetResult(); };
        watcher.Error += (_, error) => ready.TrySetException(error.GetException());
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path)) ready.TrySetResult();
        await ready.Task.WaitAsync(token);
    }
}
