using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass, DoNotParallelize]
public sealed class MacRetainedPackageTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeMacRetainedPackage")]
    public async Task RequalifiesExactRetainedArm64PayloadWithoutRebuildingIt()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("Requires native macOS execution.");
            return;
        }

        string Required(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new AssertFailedException("Missing frozen input: " + name);

        var frozen = MacRetainedPackageInput.Parse(Required("KICAD_RETAINED_MAC_INPUT"));
        string archive = Required("KICAD_RETAINED_MAC_ARCHIVE");
        string originalReceipt = Required("KICAD_RETAINED_MAC_RECEIPT");
        Assert.AreEqual(frozen.ArchiveName, Path.GetFileName(archive));
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
                ?? TestContext.TestResultsDirectory!, "mac-retained-package")).FullName;
        string scratch = Directory.CreateTempSubdirectory("kmretained-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        byte[] receiptBytes = await File.ReadAllBytesAsync(originalReceipt, deadline.Token);
        try
        {
            using JsonDocument receipt = JsonDocument.Parse(receiptBytes);
            Assert.AreEqual(frozen.Commit, receipt.RootElement.GetProperty("SourceCommit").GetString());
            Assert.AreEqual(frozen.RunId, receipt.RootElement.GetProperty("RunId").GetString());
            Assert.AreEqual("failed", receipt.RootElement.GetProperty("Status").GetString());
            Assert.AreEqual("macos", receipt.RootElement.GetProperty("Platform").GetString());
            Assert.AreEqual("arm64", receipt.RootElement.GetProperty("Architecture").GetString());
            foreach (string prerequisite in new[] { "source-commit", "pinned-ancestry", "official-dependencies" })
                Assert.IsTrue(receipt.RootElement.GetProperty("Steps").EnumerateArray().Any(step =>
                    step.GetProperty("Name").GetString() == prerequisite
                    && step.GetProperty("ExitCode").GetInt32() == 0), prerequisite);

            await using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, deadline.Token));
            Assert.AreEqual(frozen.Sha256, hash);
            var declared = receipt.RootElement.GetProperty("DiagnosticArtifacts").EnumerateArray()
                .Single(item => item.GetProperty("Path").GetString() == frozen.ArchiveName);
            Assert.AreEqual(hash, declared.GetProperty("Sha256").GetString());
            Assert.AreEqual(input.Length, declared.GetProperty("Bytes").GetInt64());

            input.Position = 0;
            MacArchivePlan plan = await MacArchivePreflight.InspectAsync(input, deadline.Token);
            string payload = Path.Combine(scratch, "payload");
            Directory.CreateDirectory(payload);
            await Run("tar", "/usr/bin/tar", ["-xpf", archive, "-C", payload, "--no-same-owner", "--mac-metadata", "--xattrs"], deadline.Token);
            await MacUpdateStager.VerifyExtractedAsync(payload, plan, deadline.Token);

            string bundle = Path.Combine(payload, "install/KiCad.app");
            string native = Path.Combine(bundle, "Contents/MacOS/kicad");
            string cli = Path.Combine(bundle, "Contents/MacOS/kicad-cli");
            string mcp = Path.Combine(payload, "kicad-mcp");
            foreach (string path in new[] { native, cli, mcp, Path.Combine(payload, "managed/kicad-mcp") })
                Assert.IsTrue(File.Exists(path), path);
            string nativeCommit = (await Run("native-commit", cli, ["version", "--format", "commit"], deadline.Token)).Trim();
            Assert.AreEqual(frozen.Commit, nativeCommit);
            string architectures = (await Run("native-architecture", "/usr/bin/lipo", ["-archs", native], deadline.Token)).Trim();
            StringAssert.Contains(architectures, "arm64");

            string zstd = Directory.GetFiles(Path.Combine(bundle, "Contents/Frameworks"), "libzstd*.dylib",
                SearchOption.TopDirectoryOnly).Single();
            string runtime = await Run("managed-runtime", mcp, ["--runtime-info"], deadline.Token,
                new Dictionary<string, string?>
                {
                    ["KICAD_AUTOMATION_NNG_LIBRARY"] = Path.Combine(payload, "managed/libnng.dylib"),
                    ["KICAD_AUTOMATION_ZSTD_LIBRARY"] = zstd
                });
            using (JsonDocument runtimeJson = JsonDocument.Parse(runtime))
            {
                Assert.AreEqual("runtime_available", runtimeJson.RootElement.GetProperty("status").GetString());
                Assert.IsFalse(runtimeJson.RootElement.GetProperty("nativeEditorContacted").GetBoolean());
            }

            string root = FindRepository();
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            string managedEvidence = Directory.CreateDirectory(Path.Combine(evidence, "managed-run")).FullName;
            ProcessResult managed = await RunProcess("managed-recovery", "dotnet", ["test", "KiCad.Automation.slnx",
                "--configuration", configuration, "--no-build", "--no-restore", "--filter",
                "FullyQualifiedName~PcbExportRecoveryTests", "--logger", "trx",
                "--results-directory", managedEvidence], deadline.Token, root);
            await File.WriteAllTextAsync(Path.Combine(evidence, "managed-run.stdout.log"), managed.Stdout, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "managed-run.stderr.log"), managed.Stderr, deadline.Token);
            Assert.AreEqual(0, managed.ExitCode, managed.Stderr + managed.Stdout);
            string[] managedTrx = Directory.GetFiles(managedEvidence, "*.trx");
            Assert.HasCount(1, managedTrx);
            MacRequalifiedPreview.RequirePassingTests(await File.ReadAllBytesAsync(managedTrx[0], deadline.Token),
                "PcbExportRecoveryTests");

            bool managedSourceUnchanged = (await RunProcess("managed-source-diff", "git",
                ["diff", "--quiet", frozen.Commit, "HEAD", "--", "automation/src"], deadline.Token, root)).ExitCode == 0;
            await File.WriteAllTextAsync(Path.Combine(evidence, "inputs.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, sourceCommit = frozen.Commit, archiveSha256 = hash, archiveBytes = input.Length,
                originalRunId = frozen.RunId, originalRunAttempt = frozen.Attempt,
                originalArtifactName = frozen.ArtifactName, rebuiltNativeCode = false,
                managedRuntimeSourceUnchanged = managedSourceUnchanged,
                nativeEditorJourneyAvailable = false, crashUndoJourneyAvailable = false
            }), deadline.Token);
            await File.WriteAllBytesAsync(Path.Combine(evidence, "original-receipt.json"), receiptBytes, deadline.Token);
            byte[] freshReceiptBytes = await File.ReadAllBytesAsync(originalReceipt, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "passed", sourceCommit = frozen.Commit, archiveSha256 = hash,
                exactRetainedPayload = true, nativeEditorJourneyPassed = false,
                nativeEditorJourneyAvailable = false, crashUndoJourneyAvailable = false,
                crashUndoClosed = false, rebuiltNativeCode = false, originalReceiptUnchanged =
                    receiptBytes.AsSpan().SequenceEqual(freshReceiptBytes),
                currentManagedRecoveryPassed = true, managedRuntimeSourceUnchanged = managedSourceUnchanged,
                publiclyPublishable = false, qualifyingDelivery = false,
                limitation = "No existing Mac package journey drives live PCB inline drag, undo or redo; the crash closure remains unavailable."
            }), deadline.Token);
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.txt"), error.ToString());
            throw;
        }
        finally
        {
            foreach (string file in Directory.GetFiles(evidence)) TestContext.AddResultFile(file);
            await RemoveTemporaryDirectory(scratch);
        }
    }

    private static string FindRepository()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KiCad.Automation.slnx"))) root = root.Parent;
        return root?.FullName ?? throw new AssertFailedException("The automation repository root was not found.");
    }

    private static async Task<string> Run(string name, string executable, string[] arguments,
        CancellationToken token, IDictionary<string, string?>? environment = null)
    {
        ProcessResult result = await RunProcess(name, executable, arguments, token,
            Directory.GetCurrentDirectory(), environment);
        Assert.AreEqual(0, result.ExitCode, name + ": " + result.Stderr);
        return result.Stdout;
    }

    private static async Task<ProcessResult> RunProcess(string name, string executable, string[] arguments,
        CancellationToken token, string? workingDirectory = null, IDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (string variable in new[] { "KICAD_RUN_FROM_BUILD_DIR", "APPDIR", "DYLD_LIBRARY_PATH",
            "DYLD_FRAMEWORK_PATH", "DYLD_FALLBACK_LIBRARY_PATH", "DYLD_FALLBACK_FRAMEWORK_PATH" })
            start.Environment.Remove(variable);
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + name);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return new(process.ExitCode, await stdout, await stderr);
    }

    private static async Task RemoveTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (IOException) { await Task.CompletedTask; }
        catch (UnauthorizedAccessException) { await Task.CompletedTask; }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
