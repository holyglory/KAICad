using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class HostedPreviewStagingTests
{
    [TestMethod]
    public async Task DifferentPlatformSourceBytesKeepBothImmutableSourcesAndExactLinks()
    {
        using var f = await Fixture.Create();
        byte[] macSource = "Synthetic source bytes from another native builder."u8.ToArray();
        string macHash = Convert.ToHexStringLower(SHA256.HashData(macSource));
        await File.WriteAllBytesAsync(Path.Combine(f.Previous, f.Source.Path), macSource);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var previous = JsonSerializer.Deserialize<DownloadManifest>(f.OriginalCatalogue, json)!;
        var mac = previous.Artifacts[0] with { FileName = "macos-existing.tar.gz", Platform = "osx-arm64",
            Commit = Fixture.Commit, SourceSha256 = macHash };
        File.Copy(Path.Combine(f.Previous, "existing.zip"), Path.Combine(f.Previous, mac.FileName));
        var source = new DownloadArtifact(f.Source.Path, "source", "earlier-preview", Fixture.Commit, macHash, macSource.Length, macHash);
        await File.WriteAllTextAsync(Path.Combine(f.Previous, "downloads.json"),
            JsonSerializer.Serialize(new DownloadManifest(1, [.. previous.Artifacts, mac, source]), json));
        var result = await HostedPreviewStaging.RunAsync(f.Request, default);
        var manifest = JsonSerializer.Deserialize<DownloadManifest>(await File.ReadAllTextAsync(Path.Combine(result.Directory, "downloads.json")), json)!;
        Assert.AreEqual(5, manifest.Artifacts.Count);
        var sources = manifest.Artifacts.Where(x => x.Platform == "source").ToArray();
        Assert.AreEqual(2, sources.Length);
        var windowsSource = sources.Single(x => x.Sha256 == f.Source.Sha256);
        Assert.AreEqual("kicad-codex-" + Fixture.Commit + "-source-" + f.Source.Sha256 + ".tar.gz", windowsSource.FileName);
        CollectionAssert.AreEqual(macSource, await File.ReadAllBytesAsync(Path.Combine(result.Directory, f.Source.Path)));
        Assert.AreEqual(f.Source.Sha256, Evidence.Hash(Path.Combine(result.Directory, windowsSource.FileName)));
        Assert.AreEqual(macHash, manifest.Artifacts.Single(x => x.Platform == "osx-arm64").SourceSha256);
        Assert.AreEqual(windowsSource.Sha256, manifest.Artifacts.Single(x => x.Platform == "win-x64").SourceSha256);
        var repeated = await HostedPreviewStaging.RunAsync(f.Request with
            { Previous = result.Directory, Output = f.Request.Output + "-repeated" }, default);
        Assert.AreEqual(result.ArtifactCount, repeated.ArtifactCount); Assert.AreEqual(result.CatalogueSha256, repeated.CatalogueSha256);
        var reused = await HostedPreviewStaging.StagePublicAsync(result.Directory, f.Request.Output + "-reuse",
            [new(windowsSource with { FileName = f.Source.Path, Version = "later-source-label" }, Path.Combine(f.Candidate, "packages", f.Source.Path))], null, default);
        Assert.AreEqual(result.ArtifactCount, reused.Count); Assert.AreEqual(result.CatalogueSha256, reused.Hash);
    }

    [TestMethod]
    public async Task SourceAliasCannotReplaceAnExistingDifferentArtifact()
    {
        using var f = await Fixture.Create();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var old = JsonSerializer.Deserialize<DownloadManifest>(f.OriginalCatalogue, json)!;
        var generic = old.Artifacts[0] with { FileName = f.Source.Path };
        var alias = generic with { FileName = "kicad-codex-" + Fixture.Commit + "-source-" + f.Source.Sha256 + ".tar.gz" };
        File.Copy(Path.Combine(f.Previous, "existing.zip"), Path.Combine(f.Previous, generic.FileName));
        File.Copy(Path.Combine(f.Previous, "existing.zip"), Path.Combine(f.Previous, alias.FileName));
        await File.WriteAllTextAsync(Path.Combine(f.Previous, "downloads.json"), JsonSerializer.Serialize(new DownloadManifest(1,
            [.. old.Artifacts, generic, alias]), json));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => HostedPreviewStaging.RunAsync(f.Request, default));
        Assert.IsFalse(Directory.Exists(f.Request.Output));
        Assert.AreEqual(alias.Sha256, Evidence.Hash(Path.Combine(f.Previous, alias.FileName)));
    }

    [TestMethod]
    public async Task CopiesOnlyVerifiedArchivesAndExistingFeedsAndNeverClaimsNativeExecution()
    {
        using var fixture = await Fixture.Create();
        await File.WriteAllTextAsync(Path.Combine(fixture.Previous, "private.txt"), "Synthetic private fixture.");
        await File.WriteAllTextAsync(Path.Combine(fixture.Candidate, "diagnostic.txt"), "Synthetic diagnostic fixture.");
        string nativeFeed = Path.Combine(fixture.Previous, PlatformUpdateFeeds.RelativeFeed("osx-arm64", "preview"));
        Directory.CreateDirectory(Path.GetDirectoryName(nativeFeed)!);
        await File.WriteAllTextAsync(nativeFeed, "Synthetic retained feed bytes; server authentication is checked separately.");
        var result = await HostedPreviewStaging.RunAsync(fixture.Request, CancellationToken.None);
        Assert.AreEqual("staged", result.Status);
        Assert.IsFalse(result.QualifyingDelivery);
        Assert.AreEqual(3, result.ArtifactCount);
        Assert.AreEqual(Evidence.Hash(Path.Combine(fixture.Request.Output, "downloads.json")), result.CatalogueSha256);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Request.Output, "private.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Request.Output, "diagnostic.txt")));
        Assert.AreEqual(await File.ReadAllTextAsync(Path.Combine(fixture.Previous, "updates/preview.json")),
            await File.ReadAllTextAsync(Path.Combine(fixture.Request.Output, "updates/preview.json")));
        Assert.AreEqual(fixture.OriginalCatalogue, await File.ReadAllTextAsync(Path.Combine(fixture.Previous, "downloads.json")));
        Assert.AreEqual(await File.ReadAllTextAsync(nativeFeed), await File.ReadAllTextAsync(Path.Combine(fixture.Request.Output,
            PlatformUpdateFeeds.RelativeFeed("osx-arm64", "preview"))));
        var manifest = JsonSerializer.Deserialize<DownloadManifest>(await File.ReadAllTextAsync(Path.Combine(fixture.Request.Output, "downloads.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.AreEqual("win-x64", manifest.Artifacts.Single(x => x.FileName.EndsWith("windows-x64.zip", StringComparison.Ordinal)).Platform);
        Assert.AreEqual(fixture.Source.Sha256, manifest.Artifacts.Last().SourceSha256);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => HostedPreviewStaging.RunAsync(fixture.Request, CancellationToken.None));
    }

    [TestMethod]
    public async Task CorrectingAFailedReceiptRecoversWithoutReplacingExistingDownloads()
    {
        using var fixture = await Fixture.Create();
        await fixture.WriteReceipt("failed");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => HostedPreviewStaging.RunAsync(fixture.Request, CancellationToken.None));
        Assert.IsFalse(Directory.Exists(fixture.Request.Output));
        await fixture.WriteReceipt("");
        var result = await HostedPreviewStaging.RunAsync(fixture.Request, CancellationToken.None);
        Assert.AreEqual("staged", result.Status);
        Assert.AreEqual(fixture.OriginalCatalogue, await File.ReadAllTextAsync(Path.Combine(fixture.Previous, "downloads.json")));
    }

    [TestMethod]
    [DataRow("failed")]
    [DataRow("prepared")]
    [DataRow("wrong-run")]
    [DataRow("wrong-commit")]
    [DataRow("wrong-platform")]
    [DataRow("failed-step")]
    [DataRow("missing-step")]
    [DataRow("missing-editor-step")]
    [DataRow("missing-editor-proof")]
    [DataRow("failed-editor-proof")]
    [DataRow("no-editor-input")]
    [DataRow("duplicate-editor")]
    [DataRow("failed-editor-exit")]
    [DataRow("duplicate-step")]
    [DataRow("changed-package")]
    [DataRow("diagnostic")]
    [DataRow("name-collision")]
    [DataRow("cancelled")]
    public async Task RejectsUnprovenOrChangedCandidatesBeforeCreatingPublicOutput(string mode)
    {
        using var fixture = await Fixture.Create();
        if (mode == "missing-editor-proof") File.Delete(fixture.EditorProof);
        else if (mode is "failed-editor-proof" or "no-editor-input" or "duplicate-editor" or "failed-editor-exit") await fixture.WriteEditorProof(mode);
        if (mode == "changed-package") await File.AppendAllTextAsync(Path.Combine(fixture.Candidate, "packages", fixture.App.Path), "changed");
        else if (mode == "name-collision")
        {
            var collision = new DownloadArtifact(fixture.App.Path, "win-x64", "other-version", Fixture.Commit,
                fixture.Source.Sha256, fixture.App.Bytes, fixture.App.Sha256);
            File.Copy(Path.Combine(fixture.Candidate, "packages", fixture.App.Path), Path.Combine(fixture.Previous, fixture.App.Path));
            await File.WriteAllTextAsync(Path.Combine(fixture.Previous, "downloads.json"),
                JsonSerializer.Serialize(new DownloadManifest(1, [collision]), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        else await fixture.WriteReceipt(mode);
        if (mode == "cancelled")
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => HostedPreviewStaging.RunAsync(fixture.Request, new CancellationToken(true)));
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => HostedPreviewStaging.RunAsync(fixture.Request, CancellationToken.None));
        Assert.IsFalse(Directory.Exists(fixture.Request.Output));
    }

    private sealed class Fixture : IDisposable
    {
        public const string Commit = "1111111111111111111111111111111111111111";
        private readonly string root = Directory.CreateTempSubdirectory("kicad-hosted-import-").FullName;
        public string Candidate => Path.Combine(root, "candidate");
        public string Previous => Path.Combine(root, "previous");
        public string EditorProof => Path.Combine(Candidate, "evidence/installed-editor-tests/windows-installed-editor/result.json");
        public HostedPreviewRequest Request => new(Candidate, Commit, "win-x64", "123", "synthetic-preview", Previous, Path.Combine(root, "output"));
        public EvidenceFile App { get; private set; } = null!;
        public EvidenceFile Source { get; private set; } = null!;
        public string OriginalCatalogue { get; private set; } = "";
        private static readonly string[] Names = ["source-commit", "pinned-ancestry", "native-build", "native-tests", "native-install",
            "installed-native-commit", "managed-runtime", "managed-contracts", "installed-editor-journey", "package-source", "source-still-clean"];

        public static async Task<Fixture> Create()
        {
            var value = new Fixture();
            Directory.CreateDirectory(Path.Combine(value.Candidate, "packages"));
            Directory.CreateDirectory(Path.Combine(value.Previous, "updates"));
            byte[] bytes = "Synthetic archive bytes for staging-code tests; not a native application."u8.ToArray();
            byte[] sourceBytes = "Distinct synthetic source archive bytes; no native source execution evidence."u8.ToArray();
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            value.App = new("kicad-codex-" + Commit + "-windows-x64.zip", bytes.Length, hash);
            value.Source = new("kicad-codex-" + Commit + "-source.tar.gz", sourceBytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(sourceBytes)));
            await File.WriteAllBytesAsync(Path.Combine(value.Candidate, "packages", value.App.Path), bytes);
            await File.WriteAllBytesAsync(Path.Combine(value.Candidate, "packages", value.Source.Path), sourceBytes);
            await File.WriteAllBytesAsync(Path.Combine(value.Previous, "existing.zip"), bytes);
            await File.WriteAllTextAsync(Path.Combine(value.Previous, "updates/preview.json"), "{\"fixture\":\"unchanged bytes, not a signed feed\"}");
            value.OriginalCatalogue = JsonSerializer.Serialize(new DownloadManifest(1,
                [new("existing.zip", "linux-x64", "synthetic-existing", new string('2', 40), hash, bytes.Length, hash)]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllTextAsync(Path.Combine(value.Previous, "downloads.json"), value.OriginalCatalogue);
            await value.WriteReceipt("");
            Directory.CreateDirectory(Path.GetDirectoryName(value.EditorProof)!);
            await value.WriteEditorProof("");
            return value;
        }

        public Task WriteEditorProof(string mode) => File.WriteAllTextAsync(EditorProof, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, status = mode == "failed-editor-proof" ? "failed" : "passed",
            normalPackagedMcpLoading = true, twoInstancesIsolated = true, mcpRestartPreservedDirtyObjects = true,
            nativeKeyboardSaveAndClose = mode != "no-editor-input",
            instances = Enumerable.Range(1, 2).Select(index => new
            {
                instanceId = mode == "duplicate-editor" ? "11111111-1111-1111-1111-111111111111"
                    : index == 1 ? "11111111-1111-1111-1111-111111111111" : "22222222-2222-2222-2222-222222222222",
                processEpoch = "synthetic-epoch-" + index, markerId = "33333333-3333-3333-3333-333333333333",
                nativeProcessExitCode = mode == "failed-editor-exit" ? 1 : 0
            })
        }));

        public async Task WriteReceipt(string mode)
        {
            ValidationStep[] steps = Names.Select(name => new ValidationStep(name,
                mode == "failed-step" && name == "native-build" ? 1 : 0,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "synthetic.stdout", "synthetic.stderr")).ToArray();
            if (mode == "missing-step") steps = steps.Skip(1).ToArray();
            if (mode == "missing-editor-step") steps = steps.Where(step => step.Name != "installed-editor-journey").ToArray();
            if (mode == "duplicate-step") steps = [.. steps, steps[0]];
            await File.WriteAllTextAsync(Path.Combine(Candidate, "receipt.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, SourceCommit = mode == "wrong-commit" ? new string('3', 40) : Commit,
                Platform = mode == "wrong-platform" ? "macos" : "windows", Architecture = "x64",
                RunId = mode == "wrong-run" ? "456" : "123", Status = mode is "failed" or "prepared" ? mode : "candidate_built",
                Steps = steps, Artifacts = new[] { App, Source },
                DiagnosticArtifacts = mode == "diagnostic" ? new[] { App } : [],
                QualifyingDelivery = false, CrossPlatformReady = false
            }));
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
