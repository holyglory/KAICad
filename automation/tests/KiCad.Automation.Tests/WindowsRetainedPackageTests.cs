using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass, DoNotParallelize]
public sealed class WindowsRetainedPackageTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeWindowsRetainedPackage")]
    public async Task OperateTheExactRetainedNativePackageWithoutRebuildingIt()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows execution."); return; }
        string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new AssertFailedException("Missing frozen input: " + name);
        var frozen = WindowsRetainedPackageInput.Parse(Required("KICAD_RETAINED_WINDOWS_INPUT"));
        string archive = Required("KICAD_RETAINED_WINDOWS_ARCHIVE"), expectedHash = frozen.Sha256;
        string commit = frozen.Commit, originalReceipt = Required("KICAD_RETAINED_WINDOWS_RECEIPT");
        Assert.AreEqual(frozen.ArchiveName, Path.GetFileName(archive));
        string scratch = Directory.CreateTempSubdirectory("kwretained-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "windows-retained-package")).FullName;
        string? prior = Environment.GetEnvironmentVariable("KICAD_TEST_WINDOWS_INSTALL");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            byte[] receiptBytes = await File.ReadAllBytesAsync(originalReceipt, deadline.Token);
            using var receipt = JsonDocument.Parse(receiptBytes);
            Assert.AreEqual(commit, receipt.RootElement.GetProperty("SourceCommit").GetString());
            Assert.AreEqual(frozen.RunId, receipt.RootElement.GetProperty("RunId").GetString());
            Assert.AreEqual("failed", receipt.RootElement.GetProperty("Status").GetString());
            foreach (string prerequisite in new[] { "native-build", "native-tests", "native-install", "installed-native-commit", "managed-runtime" })
                Assert.IsTrue(receipt.RootElement.GetProperty("Steps").EnumerateArray().Any(step =>
                    step.GetProperty("Name").GetString() == prerequisite && step.GetProperty("ExitCode").GetInt32() == 0), prerequisite);
            await using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, deadline.Token));
            Assert.AreEqual(expectedHash, hash);
            var declared = receipt.RootElement.GetProperty("DiagnosticArtifacts").EnumerateArray()
                .Single(item => item.GetProperty("Path").GetString() == Path.GetFileName(archive));
            Assert.AreEqual(hash, declared.GetProperty("Sha256").GetString());
            Assert.AreEqual(input.Length, declared.GetProperty("Bytes").GetInt64());
            input.Position = 0;
            var plan = await WindowsArchivePreflight.InspectAsync(input, deadline.Token);
            string install = Path.Combine(scratch, "install");
            input.Position = 0;
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true)) zip.ExtractToDirectory(install);
            foreach (var file in plan.Entries.Where(item => !item.Directory))
            {
                string path = Path.Combine(install, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Assert.AreEqual(file.Bytes, new FileInfo(path).Length);
                await using var bytes = File.OpenRead(path);
                Assert.AreEqual(file.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(bytes, deadline.Token)), file.Path);
            }
            var nativeCommit = await WindowsLauncherTests.Invoke(Path.Combine(install, "bin/kicad-cli.exe"),
                ["version", "--format", "commit"], install, deadline.Token, input: null);
            Assert.AreEqual(0, nativeCommit.ExitCode, nativeCommit.Error);
            Assert.AreEqual(commit, nativeCommit.Output.Trim());
            await File.WriteAllTextAsync(Path.Combine(evidence, "inputs.json"), JsonSerializer.Serialize(new
            { sourceCommit = commit, archiveSha256 = hash, archiveBytes = input.Length, entries = plan.Entries.Count,
                originalRunId = receipt.RootElement.GetProperty("RunId").GetString(),
                originalRunAttempt = frozen.Attempt, originalArtifactName = frozen.ArtifactName,
                harnessCommit = Environment.GetEnvironmentVariable("SOURCE_COMMIT"), rebuiltNativeCode = false }), deadline.Token);
            Environment.SetEnvironmentVariable("KICAD_TEST_WINDOWS_INSTALL", install);
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "KiCad.Automation.slnx"))) repository = repository.Parent;
            Assert.IsNotNull(repository);
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var unchanged = await WindowsLauncherTests.Invoke("git", ["diff", "--quiet", commit, "HEAD", "--", "automation/src"],
                repository.Parent!.FullName, deadline.Token, input: null);
            Assert.AreEqual(0, unchanged.ExitCode, "Retained payload qualification requires unchanged managed runtime source.");
            var managed = await WindowsLauncherTests.Invoke("dotnet", ["test", "KiCad.Automation.slnx", "--configuration", configuration,
                "--no-build", "--no-restore", "--filter",
                "FullyQualifiedName~HostedDeliveryTests|FullyQualifiedName~RuntimeInfoTests|FullyQualifiedName~NngTransportTests|FullyQualifiedName~NativeIpcEndpointTests",
                "--logger", "trx", "--results-directory", Path.Combine(evidence, "managed-run")], repository.FullName,
                deadline.Token, input: null, environment: new Dictionary<string, string?>
                { ["KICAD_AUTOMATION_NNG_LIBRARY"] = Path.Combine(install, "bin/nng.dll") });
            await File.WriteAllTextAsync(Path.Combine(evidence, "managed-run.stdout.log"), managed.Output, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "managed-run.stderr.log"), managed.Error, deadline.Token);
            Assert.AreEqual(0, managed.ExitCode, "The retained managed contracts failed; keep the candidate unqualified.");
            string[] managedTrx = Directory.GetFiles(Path.Combine(evidence, "managed-run"), "*.trx");
            Assert.HasCount(1, managedTrx);
            WindowsRequalifiedPreview.RequirePassingManagedTests(await File.ReadAllBytesAsync(managedTrx[0], deadline.Token));
            // The native NNG library must unload before deleting the extracted
            // installation. Run the editor journey in its own test process.
            var journey = await WindowsLauncherTests.Invoke("dotnet", ["test", "KiCad.Automation.slnx", "--configuration", configuration,
                "--no-build", "--no-restore", "--filter", "TestCategory=NativeWindowsPackage", "--logger", "trx",
                "--results-directory", Path.Combine(evidence, "installed-run")], repository.FullName, deadline.Token, input: null);
            await File.WriteAllTextAsync(Path.Combine(evidence, "installed-run.stdout.log"), journey.Output, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "installed-run.stderr.log"), journey.Error, deadline.Token);
            Assert.AreEqual(0, journey.ExitCode, "The retained editor journey failed; see its native result and cleanup evidence.");
            CollectionAssert.AreEqual(receiptBytes, await File.ReadAllBytesAsync(originalReceipt, deadline.Token));
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            { schemaVersion = 1, status = "passed", sourceCommit = commit, archiveSha256 = hash,
                exactRetainedPayload = true, nativeEditorJourneyPassed = true, rebuiltNativeCode = false,
                originalReceiptUnchanged = true, managedContractsPassed = true, managedRuntimeSourceUnchanged = true,
                publiclyPublishable = false, crossPlatformReady = false }), deadline.Token);
        }
        catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(evidence, "failure.txt"), error.ToString()); throw; }
        finally
        {
            Environment.SetEnvironmentVariable("KICAD_TEST_WINDOWS_INSTALL", prior);
            foreach (string file in Directory.GetFiles(evidence)) TestContext.AddResultFile(file);
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(scratch);
        }
    }
}
