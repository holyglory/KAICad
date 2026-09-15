using System.Runtime.InteropServices;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class HostedDeliveryTests
{
    [TestMethod, TestCategory("WindowsDependencyCacheContracts")]
    public void DependencyCacheIsScopedOptionalAndSavedBeforeNativeCompilation()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, ".github/workflows/native-delivery.yml"))) root = root.Parent;
        Assert.IsNotNull(root);
        string workflow = File.ReadAllText(Path.Combine(root.FullName, ".github/workflows/native-delivery.yml")).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string action = "0400d5f644dc74513175e3cd8d07132dd4860809";
        StringAssert.Contains(workflow, "actions/cache/restore@" + action);
        StringAssert.Contains(workflow, "actions/cache/save@" + action);
        StringAssert.Contains(workflow, "windows_dependency_cache:");
        StringAssert.Contains(workflow, "Join-Path $env:RUNNER_TEMP 'kicad-vcpkg-binary-cache'");
        StringAssert.Contains(workflow, "\"VCPKG_DEFAULT_BINARY_CACHE=$cacheDirectory\" >> $env:GITHUB_ENV");
        Assert.IsFalse(workflow.Contains("VCPKG_DEFAULT_BINARY_CACHE: ${{ runner.", StringComparison.Ordinal),
            "The runner context is unavailable in job-level environment expressions.");
        StringAssert.Contains(workflow, "if: '!inputs.checks_only'\n        id: dependency-cache-key");
        StringAssert.Contains(workflow, "steps.prepare.outcome == 'success' && inputs.windows_dependency_cache");
        StringAssert.Contains(workflow, "VersionInfo.FileVersion");
        StringAssert.Contains(workflow, "hashFiles('vcpkg.json', 'vcpkg-configuration.json', 'tools/custom_vcpkg_triplets/**')");
        StringAssert.Contains(workflow, "enableCrossOsArchive: false");
        StringAssert.Contains(workflow, "nuget,https://gitlab.com/api/v4/projects/27426693/packages/nuget/index.json,read");
        int save = workflow.IndexOf("name: Retain compiled Windows dependencies before native tests", StringComparison.Ordinal);
        int build = workflow.IndexOf("name: Build, inspect and package Windows x64", StringComparison.Ordinal);
        int seedJob = workflow.IndexOf("\n  windows-cache-seed:", StringComparison.Ordinal);
        int windowsJob = workflow.IndexOf("\n  windows:", StringComparison.Ordinal);
        int nativeReceipt = workflow.IndexOf("name: native-windows-x64-", StringComparison.Ordinal);
        Assert.IsTrue(windowsJob >= 0 && windowsJob < save && build < seedJob && nativeReceipt < seedJob,
            "The native build and its retained evidence must remain in the Windows delivery job.");
        Assert.IsFalse(workflow[seedJob..].Contains("name: Build, inspect and package Windows x64", StringComparison.Ordinal));
        Assert.IsTrue(save >= 0 && save < build, "Dependency cache must survive a later native test failure.");
        string cacheSteps = workflow[workflow.IndexOf("name: Identify the Windows dependency cache", StringComparison.Ordinal)..build];
        Assert.IsFalse(cacheSteps.Contains("signing/", StringComparison.Ordinal));
        Assert.IsFalse(cacheSteps.Contains("github.run_id", StringComparison.Ordinal), "Real dependency cache keys must survive reruns.");
        Assert.IsFalse(cacheSteps.Contains("continue-on-error: false", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InstalledWindowsProbeCannotBorrowTheSourceTestLibraryOverride()
    {
        var environment = new Dictionary<string, string?>
        { ["KICAD_AUTOMATION_NNG_LIBRARY"] = "build-only-library", ["PATH"] = "preserved-runner-path" };
        HostedDelivery.ConfigureWindowsTransportCheck(environment, "managed-runtime", "packaged-library");
        Assert.IsFalse(environment.ContainsKey("KICAD_AUTOMATION_NNG_LIBRARY"));
        Assert.AreEqual("preserved-runner-path", environment["PATH"]);
        HostedDelivery.ConfigureWindowsTransportCheck(environment, "managed-contracts", "packaged-library");
        Assert.AreEqual("packaged-library", environment["KICAD_AUTOMATION_NNG_LIBRARY"]);
        HostedDelivery.ConfigureWindowsTransportCheck(environment, "unrelated-check", "different-library");
        Assert.AreEqual("packaged-library", environment["KICAD_AUTOMATION_NNG_LIBRARY"]);
    }

    [TestMethod]
    public void NativeTargetsDoNotTurnLinuxOrRosettaIntoAnotherArchitecture()
    {
        HostedDelivery.ValidateTarget("arm64", true, false, Architecture.Arm64);
        HostedDelivery.ValidateTarget("x64", true, false, Architecture.X64);
        HostedDelivery.ValidateTarget("x64", false, true, Architecture.X64);
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => HostedDelivery.ValidateTarget("x64", false, false, Architecture.X64));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => HostedDelivery.ValidateTarget("arm64", false, true, Architecture.Arm64));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => HostedDelivery.ValidateTarget("x64", false, true, Architecture.Arm64));
        Assert.ThrowsExactly<InvalidDataException>(() => HostedDelivery.ValidateTarget("arm64", true, false, Architecture.X64));
    }

    [TestMethod]
    public async Task InvalidSourceCannotCreateOrBootstrapOutput()
    {
        string root = Directory.CreateTempSubdirectory("kicad-hosted-contract-").FullName;
        try
        {
            string output = Path.Combine(root, "must-not-exist");
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => HostedDelivery.RunAsync(
                new(root, "master", "x64", output, new string('a', 40)), CancellationToken.None));
            Assert.IsFalse(Directory.Exists(output));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task RandomBytesAreNotWindowsExecutableEvidence()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pe-contract-").FullName;
        try
        {
            string path = Path.Combine(root, "not-a-binary.exe");
            await File.WriteAllTextAsync(path, "Synthetic malformed PE fixture.");
            Assert.ThrowsExactly<BadImageFormatException>(() => HostedDelivery.RequireWindowsX64(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
