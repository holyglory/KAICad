using System.Diagnostics;
using System.Security.Cryptography;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyFailedStartup(string executable, string temporary, string evidence,
        CancellationToken token)
    {
        string directory = Directory.CreateDirectory(Path.Combine(temporary, "failed-start")).FullName;
        string project = Path.Combine(directory, "failure.kicad_pro");
        await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}", token);
        string? runtime = null;
        var registry = new InstanceRegistry(new NngTransport(), Path.Combine(directory, "registry"), start =>
        {
            int socketArgument = start.ArgumentList.IndexOf("--api-socket");
            runtime = Path.GetDirectoryName(start.ArgumentList[socketArgument + 1]);
            // Empty DISPLAY cannot connect to the test's separate X server.
            start.Environment["DISPLAY"] = "";
            start.Environment.Remove("WAYLAND_DISPLAY");
            start.Environment["GDK_BACKEND"] = "x11";
            start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(directory, "config");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(directory, "cache");
        });
        var error = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.StartAsync(executable, project, token));
        Assert.AreEqual("start_failed", error.Code);
        Assert.AreEqual(0, registry.List().Count);
        Assert.AreEqual(0, (await registry.SavedSessionsAsync(token)).Count);
        Assert.IsEmpty(await registry.PendingLaunchesAsync(token), "The proven-exited child must not block a corrected retry.");
        Assert.IsNotNull(runtime);
        StringAssert.Contains(error.Message, runtime);
        foreach (string name in new[] { "native.log", "bootstrap.stdout.log", "bootstrap.stderr.log" })
            if (File.Exists(Path.Combine(runtime, name)))
                File.Copy(Path.Combine(runtime, name), Path.Combine(evidence, "failed-start-" + name), true);
        Assert.IsTrue(Directory.EnumerateFiles(evidence, "failed-start-*.log").Any(),
            "Failed startup must retain native or bootstrap diagnostics.");
    }

    [TestMethod, TestCategory("NativeStartupFailure")]
    public async Task NativeStartupRefusalsPreserveFilesAndExitNormally()
    {
        Assert.IsTrue(OperatingSystem.IsLinux());
        string executable = Path.Combine(FindRoot(), "automation", "artifacts", "native", "kicad", "kicad");
        string evidence = NativeEvidenceDirectory.Begin(Path.Combine(FindRoot(), "automation", "artifacts", "native-startup-failure"));
        string temporary = Directory.CreateTempSubdirectory("kicad-startup-refusals-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        using var display = new Process { StartInfo = new("Xvfb")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string argument in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" })
            display.StartInfo.ArgumentList.Add(argument);
        display.Start();
        Task displayLog = Capture(display.StandardError, Path.Combine(evidence, "xvfb.log"));
        string number = "";
        string? runtime = null;
        var registry = new InstanceRegistry(new NngTransport(), Path.Combine(temporary, "registry"), start =>
        {
            runtime = Path.GetDirectoryName(start.ArgumentList[start.ArgumentList.IndexOf("--api-socket") + 1]);
            start.Environment["DISPLAY"] = ":" + number;
            start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(temporary, "config");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(temporary, "cache");
        });
        try
        {
            number = (await display.StandardOutput.ReadLineAsync(token))!;
            Assert.IsTrue(int.TryParse(number, out _));
            await VerifyFailedStartup(executable, temporary, evidence, token);
            string projectDirectory = Directory.CreateDirectory(Path.Combine(temporary, "history-project")).FullName;
            string project = Path.Combine(projectDirectory, "fixture.kicad_pro");
            const string content = "{\"meta\":{\"version\":3}}";
            await File.WriteAllTextAsync(project, content, token);
            string history = Directory.CreateDirectory(Path.Combine(projectDirectory, ".history")).FullName;
            await File.WriteAllTextAsync(Path.Combine(history, "fixture.kicad_pro"), content, token);
            await Git("init", "--quiet");
            await Git("add", "fixture.kicad_pro");
            await Git("-c", "user.name=KiCad fixture", "-c", "user.email=fixture@localhost", "commit", "--quiet", "-m", "Unsaved local history fixture");
            var before = Directory.GetFiles(history, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f, f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);
            var refused = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.StartAsync(executable, project, token));
            Assert.AreEqual("start_failed", refused.Code);
            StringAssert.Contains(refused.Message, "unsaved local history");
            StringAssert.Contains(refused.Message, "exited with code 255", "A startup refusal must not crash during teardown.");
            Assert.AreEqual(content, await File.ReadAllTextAsync(project, token));
            Assert.IsEmpty(registry.List());
            Assert.IsEmpty(await registry.PendingLaunchesAsync(token));
            foreach (var (file, hash) in before)
                Assert.AreEqual(hash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file, token))), file);
            Assert.IsEmpty((await Git("tag", "--list", "Last_Save_*")).Trim());
            Assert.IsNotNull(runtime);
            File.Copy(Path.Combine(runtime, "native.log"), Path.Combine(evidence, "history-refusal.log"));

            // False-positive guard: a fixture history explicitly marked saved must open normally.
            await Git("tag", "Last_Save_project");
            var accepted = await registry.StartAsync(executable, project, token);
            Assert.AreEqual(project, accepted.ProjectPath);
            Assert.AreEqual(accepted.InstanceId, (await registry.Client(accepted.InstanceId).HandshakeAsync(token)).InstanceId);

            async Task<string> Git(params string[] arguments)
            {
                var info = new ProcessStartInfo("git") { WorkingDirectory = history, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in arguments) info.ArgumentList.Add(argument);
                using var command = Process.Start(info)!;
                Task<string> output = command.StandardOutput.ReadToEndAsync(token), error = command.StandardError.ReadToEndAsync(token);
                await command.WaitForExitAsync(token);
                Assert.AreEqual(0, command.ExitCode, await error);
                return await output;
            }
        }
        finally
        {
            // Every PID is from this test's verified registration or launch receipt.
            var pids = registry.List().Select(r => r.ProcessId)
                .Concat((await registry.PendingLaunchesAsync()).Select(r => r.ProcessId)).OfType<int>().Distinct();
            foreach (int pid in pids)
            {
                try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
                catch (ArgumentException) { }
            }
            if (!display.HasExited) display.Kill();
            await display.WaitForExitAsync();
            await displayLog;
            Directory.Delete(temporary, true);
        }
    }

}
