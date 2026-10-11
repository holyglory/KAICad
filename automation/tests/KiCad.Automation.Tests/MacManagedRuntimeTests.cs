using System.Text.Json;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacManagedRuntimeTests
{
    [TestMethod]
    public void RequestedArchitectureAndProbeClaimsCannotBeSubstituted()
    {
        Assert.AreEqual("osx-arm64", MacManagedRuntime.RuntimeIdentifier("arm64"));
        Assert.AreEqual("osx-x64", MacManagedRuntime.RuntimeIdentifier("x64"));
        Assert.ThrowsExactly<ArgumentException>(() => MacManagedRuntime.RuntimeIdentifier("universal"));
        string Probe(string architecture, bool contacted = false, bool ready = false) => JsonSerializer.Serialize(new
        { schemaVersion = 1, status = "runtime_available", processArchitecture = architecture,
            framework = ".NET 10.0.0", nngVersion = "1.10.0", nativeEditorContacted = contacted, crossPlatformReady = ready });
        Assert.AreEqual("1.10.0", MacManagedRuntime.ReadProbe(Probe("Arm64"), "arm64").NngVersion);
        Assert.ThrowsExactly<InvalidDataException>(() => MacManagedRuntime.ReadProbe(Probe("X64"), "arm64"));
        Assert.ThrowsExactly<InvalidDataException>(() => MacManagedRuntime.ReadProbe(Probe("Arm64", contacted: true), "arm64"));
        Assert.ThrowsExactly<InvalidDataException>(() => MacManagedRuntime.ReadProbe(Probe("Arm64", ready: true), "arm64"));
    }

    [TestMethod]
    public async Task BundleLibraryAliasesResolveOnceButMissingAmbiguousOrEscapingLibrariesFail()
    {
        string root = Directory.CreateTempSubdirectory("kicad-mac-nng-").FullName;
        try
        {
            string bundle = Path.Combine(root, "KiCad.app");
            string frameworks = Directory.CreateDirectory(Path.Combine(bundle, "Contents/Frameworks")).FullName;
            Assert.ThrowsExactly<InvalidDataException>(() => MacManagedRuntime.FindBundledNng(bundle));
            string real = Path.Combine(frameworks, "libnng.1.dylib");
            await File.WriteAllTextAsync(real, "Synthetic path-selection fixture, not a Mach-O binary.");
            File.CreateSymbolicLink(Path.Combine(frameworks, "libnng.dylib"), "libnng.1.dylib");
            Assert.AreEqual(real, MacManagedRuntime.FindBundledNng(bundle));
            string conflicting = Path.Combine(frameworks, "libnng.other.dylib");
            await File.WriteAllTextAsync(conflicting, "Synthetic second library.");
            Assert.ThrowsExactly<InvalidDataException>(() => MacManagedRuntime.FindBundledNng(bundle));
            File.Delete(conflicting);
            string outside = Path.Combine(root, "outside.dylib");
            await File.WriteAllTextAsync(outside, "Synthetic unbundled library.");
            File.CreateSymbolicLink(conflicting, outside);
            Assert.ThrowsExactly<InvalidDataException>(() => MacManagedRuntime.FindBundledNng(bundle));

            File.Delete(conflicting);
            string zstdReal = Path.Combine(frameworks, "libzstd.1.5.7.dylib");
            await File.WriteAllTextAsync(zstdReal, "Synthetic zstd path-selection fixture, not a Mach-O binary.");
            File.CreateSymbolicLink(Path.Combine(frameworks, "libzstd.1.dylib"), "libzstd.1.5.7.dylib");
            Assert.AreEqual(zstdReal, MacManagedRuntime.FindBundledZstd(bundle));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task SchemaThreeRequiresManagedRuntimeEvidenceWithoutClaimingMacExecutionOnLinux()
    {
        string root = Directory.CreateTempSubdirectory("kicad-mac-runtime-receipt-").FullName;
        const string commit = "1111111111111111111111111111111111111111";
        var runtime = new MacManagedRuntimeEvidence("arm64",
            new[] { "kicad-mcp", "libcoreclr.dylib", "libhostpolicy.dylib", "libnng.dylib" }
                .Select(name => new NativeBinaryEvidence(name, "arm64", new string('2', 64))).ToArray(),
            "Contents/Frameworks/libnng.1.dylib", ".NET 10.0.0", "1.10.0");
        try
        {
            MacManagedRuntime.ValidateEvidence(runtime, "arm64");
            foreach (var invalid in new[]
            {
                runtime with { TargetArchitecture = "x64" }, runtime with { Binaries = runtime.Binaries.Take(3).ToArray() },
                runtime with { NngRelativePath = "Contents/Frameworks/../../outside" }, runtime with { Framework = null! },
                runtime with { NngVersion = null! }, runtime with { Binaries = runtime.Binaries.Select(b => b with { Architectures = "x86_64" }).ToArray() }
            }) Assert.ThrowsExactly<InvalidDataException>(() => MacManagedRuntime.ValidateEvidence(invalid, "arm64"));
            string evidence = Directory.CreateDirectory(Path.Combine(root, "evidence")).FullName;
            string log = Path.Combine(evidence, "synthetic.log");
            await File.WriteAllTextAsync(log, "Synthetic receipt codec fixture; no Mac executed.");
            var receipt = new ValidationReceipt(3, "macos", "Arm64", "Arm64", commit, commit, commit, "", "synthetic",
                "synthetic", "synthetic", "checks_passed", null,
                [new("synthetic", 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "synthetic.log", "synthetic.log")],
                [new("synthetic.log", new FileInfo(log).Length, Evidence.Hash(log))], "arm64",
                [new("kicad", "arm64", new string('3', 64)), new("kicad-cli", "arm64", new string('4', 64))], runtime);
            var sealedResult = await Evidence.SealAsync(root, evidence, receipt);
            Assert.IsFalse(sealedResult.CrossPlatformReady);
            await Evidence.VerifyAsync(Path.Combine(root, "result.json"), sealedResult.Archive, commit, architecture: "arm64");
            string invalidRoot = Directory.CreateDirectory(Path.Combine(root, "invalid")).FullName;
            string invalidEvidence = Directory.CreateDirectory(Path.Combine(invalidRoot, "evidence")).FullName;
            File.Copy(log, Path.Combine(invalidEvidence, "synthetic.log"));
            var invalidResult = await Evidence.SealAsync(invalidRoot, invalidEvidence, receipt with { ManagedRuntime = null });
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence.VerifyAsync(
                Path.Combine(invalidRoot, "result.json"), invalidResult.Archive, commit, architecture: "arm64"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
