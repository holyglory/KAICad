using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsJsonLinkTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("WindowsFileMode")]
    public async Task NativeDescriptorModesMatchTheBytesRecordedByFileBaselines()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native MSVC and the Windows CRT."); return; }
        string root = Directory.CreateTempSubdirectory("kwfile-mode-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!,
            "windows-file-mode")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await WindowsFixtureCleanup.PreserveFailuresAsync(async () =>
        {
            var build = await Run("build", "cl.exe", "/nologo", "/std:c++20", "/EHsc", "/MD", "/O2",
                "/I" + Path.Combine(Repository(), "libs/kiplatform/os/windows"),
                Path.Combine(Repository(), "automation/tests/fixtures/windows-file-mode/probe.cpp"),
                "/Fefile-mode.exe");
            Assert.AreEqual(0, build.ExitCode, build.Error);
            var executed = await Run("execute", Path.Combine(root, "file-mode.exe"), root);
            Assert.AreEqual(0, executed.ExitCode, executed.Error);
            using var result = System.Text.Json.JsonDocument.Parse(executed.Output);
            Assert.IsTrue(result.RootElement.GetProperty("legacyMismatchReproduced").GetBoolean());
            Assert.IsTrue(result.RootElement.GetProperty("textBytesMatched").GetBoolean());
            Assert.IsTrue(result.RootElement.GetProperty("binaryBytesMatched").GetBoolean());
            Assert.IsTrue(result.RootElement.GetProperty("embeddedNulPreserved").GetBoolean());
            Assert.AreEqual(5, result.RootElement.GetProperty("cases").GetInt32());
        }, async () =>
        {
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(root);
            Assert.IsFalse(Directory.Exists(root));
        });

        async Task<(int ExitCode, string Output, string Error)> Run(string name, string executable, params string[] arguments)
        {
            var result = await WindowsLauncherTests.Invoke(executable, arguments, root, deadline.Token, input: null);
            string stdout = Path.Combine(evidence, name + ".stdout.log"), stderr = Path.Combine(evidence, name + ".stderr.log");
            await File.WriteAllTextAsync(stdout, result.Output, deadline.Token);
            await File.WriteAllTextAsync(stderr, result.Error, deadline.Token);
            TestContext.AddResultFile(stdout); TestContext.AddResultFile(stderr);
            return (result.ExitCode, result.Output, result.Error);
        }
    }

    [TestMethod, TestCategory("WriteObserverBoundary")]
    public async Task NativeWriteObserverDllPreservesThreadIsolationNestingAndFailureRecovery()
    {
        string root = Directory.CreateTempSubdirectory("kicad-observer-link-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!,
            "write-observer-link")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            Assert.AreEqual(0, (await Run("configure", "cmake", "-S",
                Path.Combine(Repository(), "automation/tests/fixtures/write-observer-link"),
                "-B", root, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release")).ExitCode);
            Assert.AreEqual(0, (await Run("build", "cmake", "--build", root,
                "--target", "observer_consumer")).ExitCode);
            Assert.AreEqual(0, (await Run("execute", Path.Combine(root,
                OperatingSystem.IsWindows() ? "observer_consumer.exe" : "observer_consumer"))).ExitCode);
            if (OperatingSystem.IsWindows())
            {
                var rejected = await Run("exported-tls-negative", "cmake", "--build", root,
                    "--target", "rejected_tls_interface");
                Assert.AreNotEqual(0, rejected.ExitCode);
                StringAssert.Contains(rejected.Diagnostic, "C2492", "The original MSVC rejection must be reproduced.");
                var missing = await Run("missing-provider-negative", "cmake", "--build", root,
                    "--target", "observer_missing_provider");
                Assert.AreNotEqual(0, missing.ExitCode);
                StringAssert.Contains(missing.Diagnostic, "LNK2019");
                StringAssert.Contains(missing.Diagnostic, "FILE_WRITE_OBSERVER");
            }
        }
        finally { Directory.Delete(root, recursive: true); }

        async Task<(int ExitCode, string Diagnostic)> Run(string name, string executable, params string[] arguments)
        {
            var result = await WindowsLauncherTests.Invoke(executable, arguments, root, deadline.Token, input: null);
            string stdout = Path.Combine(evidence, name + ".stdout.log");
            string stderr = Path.Combine(evidence, name + ".stderr.log");
            await File.WriteAllTextAsync(stdout, result.Output, deadline.Token);
            await File.WriteAllTextAsync(stderr, result.Error, deadline.Token);
            TestContext.AddResultFile(stdout); TestContext.AddResultFile(stderr);
            return (result.ExitCode, result.Output + result.Error);
        }
    }

    [TestMethod]
    public void UpdaterHeaderUsesTheExistingSharedJsonImports()
    {
        foreach (string file in new[] { "automation_update_client.h", "automation_update_windows.h" })
        {
            string header = File.ReadAllText(Path.Combine(Repository(), "kicad", file));
            StringAssert.Contains(header, "#include <json_common.h>", file);
            Assert.IsFalse(header.Contains("#include <nlohmann/json.hpp>", StringComparison.Ordinal), file);
        }
        foreach (string file in Directory.GetFiles(Path.Combine(Repository(), "kicad"), "automation_update*.*"))
            Assert.IsFalse(File.ReadAllText(file).Contains("#include <nlohmann/json.hpp>", StringComparison.Ordinal),
                "Every production updater translation unit must keep the shared JSON boundary: " + Path.GetFileName(file));
        string declaration = File.ReadAllText(Path.Combine(Repository(), "qa/tests/common/CMakeLists.txt"));
        var link = System.Text.RegularExpressions.Regex.Match(declaration,
            @"target_link_libraries\(\s*qa_automation_update_client\s+(?<libraries>[^)]*)\)");
        Assert.IsTrue(link.Success);
        StringAssert.Contains(link.Groups["libraries"].Value, "kicommon");
    }

    [TestMethod]
    public async Task WindowsDllConsumerRejectsDuplicateDefinitionsAndRunsWithKiCadImports()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This check requires the native MSVC compiler and Windows DLL loader.");
            return;
        }
        string root = Directory.CreateTempSubdirectory("kicad-json-link-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!,
            "windows-json-link")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            Assert.AreEqual(0, await Run("configure", "cmake", ["-S", Path.Combine(Repository(), "automation/tests/fixtures/windows-json-link"),
                "-B", root, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release"]));
            Assert.AreEqual(0, await Run("wrapped-build", "cmake", ["--build", root, "--target", "wrapped"]));
            Assert.AreEqual(0, await Run("wrapped-execution", Path.Combine(root, "wrapped.exe"), []));
            Assert.AreNotEqual(0, await Run("raw-build", "cmake", ["--build", root, "--target", "raw"]),
                "The negative fixture did not reproduce the duplicate JSON definition failure.");
            string diagnostic = await File.ReadAllTextAsync(Path.Combine(evidence, "raw-build.stdout.log"));
            StringAssert.Contains(diagnostic, "LNK2005", "A different build failure cannot prove this regression.");
            Assert.AreNotEqual(0, await Run("missing-provider", "cmake", ["--build", root, "--target", "missing_provider"]));
            diagnostic = await File.ReadAllTextAsync(Path.Combine(evidence, "missing-provider.stdout.log"));
            StringAssert.Contains(diagnostic, "LNK2019");
            StringAssert.Contains(diagnostic, "basic_json", "The missing JSON provider must actually be exercised.");
            Assert.AreEqual(0, await Run("native-request-build", "cmake", ["--build", root, "--target", "native_request"]));
            Assert.AreNotEqual(0, await Run("raw-native-request-build", "cmake", ["--build", root, "--target", "raw_native_request"]));
            diagnostic = await File.ReadAllTextAsync(Path.Combine(evidence, "raw-native-request-build.stdout.log"));
            StringAssert.Contains(diagnostic, "LNK2005", "The actual request-builder translation unit must reproduce the regression.");
            StringAssert.Contains(diagnostic, "automation_update_windows.cpp.obj");
        }
        finally { Directory.Delete(root, recursive: true); }

        async Task<int> Run(string name, string executable, string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not start " + name);
            await using var stdout = File.Create(Path.Combine(evidence, name + ".stdout.log"));
            await using var stderr = File.Create(Path.Combine(evidence, name + ".stderr.log"));
            Task output = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            Task error = process.StandardError.BaseStream.CopyToAsync(stderr);
            try { await process.WaitForExitAsync(deadline.Token); }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(output, error);
            }
            await stdout.FlushAsync();
            await stderr.FlushAsync();
            TestContext.AddResultFile(Path.Combine(evidence, name + ".stdout.log"));
            TestContext.AddResultFile(Path.Combine(evidence, name + ".stderr.log"));
            return process.ExitCode;
        }
    }

    private static string Repository()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "kicad/automation_update_client.h")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The KiCad source fixture is unavailable.");
    }
}
