using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass, TestCategory("UpstreamRelease")]
public sealed class UpstreamReleaseTests
{
    private static readonly string A = new('a', 40), B = new('b', 40), C = new('c', 40);

    [TestMethod]
    public void NativeWorkflowUsesTheCompiledProvenanceValidator()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".github/workflows/native-delivery.yml")))
            directory = directory.Parent;
        Assert.IsNotNull(directory, "The source-native workflow must be available to this repository test.");
        string workflow = File.ReadAllText(Path.Combine(directory.FullName, ".github/workflows/native-delivery.yml"));
        Assert.IsFalse(workflow.Contains("python", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(workflow, "upstream-verify --repository");
        StringAssert.Contains(workflow, "git merge-base --is-ancestor f638a860a05b3e48d1074314a656ad9b8f597466 HEAD");
        Assert.IsTrue(workflow.IndexOf("actions/setup-dotnet", StringComparison.Ordinal)
            < workflow.IndexOf("name: Verify pinned ancestry", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("10.0.7", true)]
    [DataRow("11.0.0", true)]
    [DataRow("9.0.9.1", true)]
    [DataRow("10.0.7-rc1", false)]
    [DataRow("10.99.0", false)]
    [DataRow("11.0.0-nightly", false)]
    [DataRow("v10.0.7", false)]
    [DataRow("10.00.7", false)]
    [DataRow("10.0.7/branch", false)]
    public void OnlyOfficialStableVersionShapesAreCandidates(string version, bool expected)
        => Assert.AreEqual(expected, UpstreamMaintenance.IsStable(version));

    [TestMethod]
    public void AnnotatedTagsUseTheirCommitAndIgnoreReleaseCandidates()
    {
        var tags = UpstreamMaintenance.ParseTags($"{A}\trefs/tags/10.0.7\n{B}\trefs/tags/10.0.7^{{}}\n{C}\trefs/tags/10.0.7-rc1\n");
        Assert.AreEqual(1, tags.Count);
        Assert.AreEqual(B, tags["10.0.7"]);
    }

    [TestMethod]
    public void InitializedTagsAndExistingCandidatesDoNotRepeatAndMissedVersionsAreOrdered()
    {
        var tracking = new UpstreamTracking(1, UpstreamProvenance.CanonicalRepository, new() { ["10.0.6"] = A });
        var result = UpstreamMaintenance.Detect(tracking, new Dictionary<string, string>
            { ["10.0.6"] = A, ["10.0.7"] = B, ["10.0.10"] = C, ["10.0.8"] = A, ["11.0.0"] = B },
            new Dictionary<string, string> { ["10.0.7"] = B });
        CollectionAssert.AreEqual(new[] { "10.0.8", "10.0.10", "11.0.0" }, result.Targets.Select(t => t.Tag).ToArray());
        Assert.AreEqual(1, result.ExistingCandidates);
    }

    [TestMethod]
    public void ChangedOrMissingTagsAndEmptyResponsesFailWithoutAdvancingState()
    {
        var tracking = new UpstreamTracking(1, UpstreamProvenance.CanonicalRepository, new() { ["10.0.6"] = A });
        Assert.ThrowsExactly<InvalidDataException>(() => UpstreamMaintenance.Detect(tracking,
            new Dictionary<string, string> { ["10.0.6"] = B }, new Dictionary<string, string>()));
        Assert.ThrowsExactly<InvalidDataException>(() => UpstreamMaintenance.Detect(tracking,
            new Dictionary<string, string> { ["10.0.7"] = B }, new Dictionary<string, string>()));
        Assert.ThrowsExactly<InvalidDataException>(() => UpstreamMaintenance.ParseTags(""));
        Assert.ThrowsExactly<InvalidDataException>(() => UpstreamMaintenance.Detect(tracking,
            new Dictionary<string, string> { ["10.0.6"] = A, ["10.0.7"] = C }, new Dictionary<string, string> { ["10.0.7"] = B }));
        Assert.AreEqual(A, tracking.InitialTags["10.0.6"]);
    }

    [TestMethod]
    public void StableProvenanceRequiresVersionAndExactFeatureSource()
    {
        new UpstreamProvenance(1, UpstreamProvenance.CanonicalRepository, null, A, "preview", 1).Validate();
        new UpstreamProvenance(1, UpstreamProvenance.CanonicalRepository, "10.0.7", A, "stable-candidate", 1, B).Validate();
        Assert.ThrowsExactly<InvalidDataException>(() => new UpstreamProvenance(1, UpstreamProvenance.CanonicalRepository,
            "10.99.0", A, "stable-candidate", 1, B).Validate());
        Assert.ThrowsExactly<InvalidDataException>(() => new UpstreamProvenance(1, UpstreamProvenance.CanonicalRepository,
            "10.0.7", A, "preview", 1).Validate());
        Assert.ThrowsExactly<InvalidDataException>(() => new UpstreamProvenance(1, "https://example.invalid/upstream",
            "10.0.7", A, "stable-candidate", 1, B).Validate());
    }

    [TestMethod]
    public async Task InitialPortStartsFromStableTagAndPreservesFeaturesAndDevelopment()
    {
        await using var fixture = await GitFixture.Create(false);
        var result = await UpstreamMaintenance.PrepareAsync(fixture.Source, "10.0.7", fixture.Upstream,
            Path.Combine(fixture.Root, "candidate-run"), CancellationToken.None, fixture.Remote);
        Assert.AreEqual("integrated", result.Status);
        Assert.AreEqual("KAICad feature\n", await File.ReadAllTextAsync(Path.Combine(result.Directory, "feature.txt")));
        Assert.AreEqual("upstream release\n", await File.ReadAllTextAsync(Path.Combine(result.Directory, "upstream.txt")));
        Assert.AreEqual(fixture.Feature, (await Git(fixture.Source, "rev-parse", "HEAD")).Trim());
        Assert.AreEqual("", (await Git(fixture.Source, "status", "--porcelain")).Trim());
        Assert.AreEqual("codex/upstream/10.0.7", result.CandidateBranch);
        Assert.AreEqual("release/10.0", result.MaintenanceBranch);
        Assert.AreEqual(fixture.Upstream, (await UpstreamProvenance.ReadAsync(result.Directory, result.CandidateCommit, CancellationToken.None)).Commit);
        await Git(result.Directory, "merge-base", "--is-ancestor", fixture.Upstream, result.CandidateCommit);
        Assert.IsFalse((await Git(result.Directory, "branch", "--show-current")).Trim().Length > 0);
        Assert.IsFalse((await Git(fixture.Remote, "show-ref")).Contains("codex/upstream", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ConflictCannotBePublishedAsAnIntegratedCandidateAndRetryPreservesOriginalEvidence()
    {
        await using var fixture = await GitFixture.Create(true);
        string first = Path.Combine(fixture.Root, "first");
        var failed = await UpstreamMaintenance.PrepareAsync(fixture.Source, "10.0.7", fixture.Upstream,
            first, CancellationToken.None, fixture.Remote);
        Assert.AreEqual("conflicts", failed.Status);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpstreamProvenance.RequireAsync(
            failed.Directory, failed.CandidateCommit, CancellationToken.None));
        Assert.IsTrue(File.Exists(Path.Combine(first, "kaicad.patch")));
        Assert.IsTrue(File.Exists(Path.Combine(first, "integration-error.txt")));
        Assert.AreEqual("upstream value\n", await File.ReadAllTextAsync(Path.Combine(failed.Directory, "shared.txt")));
        string originalReceipt = await File.ReadAllTextAsync(Path.Combine(first, "candidate.json"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => UpstreamMaintenance.PrepareAsync(fixture.Source,
            "10.0.7", fixture.Upstream, first, CancellationToken.None, fixture.Remote));
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "shared.txt"), "base value\n");
        await Git(fixture.Source, "add", "shared.txt");
        await Git(fixture.Source, "commit", "-m", "Keep upstream ownership; feature is independent");
        var repaired = await UpstreamMaintenance.PrepareAsync(fixture.Source, "10.0.7", fixture.Upstream,
            Path.Combine(fixture.Root, "retry"), CancellationToken.None, fixture.Remote);
        Assert.AreEqual("integrated", repaired.Status);
        Assert.AreEqual(originalReceipt, await File.ReadAllTextAsync(Path.Combine(first, "candidate.json")));
        Assert.AreEqual("KAICad feature\n", await File.ReadAllTextAsync(Path.Combine(repaired.Directory, "feature.txt")));
    }

    [TestMethod]
    public async Task WrongUpstreamCommitAndDirtyDevelopmentAreRejectedBeforeCreatingCandidate()
    {
        await using var fixture = await GitFixture.Create(false);
        string output = Path.Combine(fixture.Root, "not-created");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpstreamMaintenance.PrepareAsync(fixture.Source,
            "10.0.7", A, output, CancellationToken.None, fixture.Remote));
        Assert.IsFalse(Directory.Exists(output));
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "feature.txt"), "unsaved user work\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpstreamMaintenance.PrepareAsync(fixture.Source,
            "10.0.7", fixture.Upstream, output, CancellationToken.None, fixture.Remote));
        Assert.AreEqual("unsaved user work\n", await File.ReadAllTextAsync(Path.Combine(fixture.Source, "feature.txt")));
        Assert.IsFalse(Directory.Exists(output));
    }

    [TestMethod]
    public async Task LaterStableVersionPreservesMaintenanceAndNewKaicadFeatures()
    {
        await using var fixture = await GitFixture.Create(false);
        var first = await UpstreamMaintenance.PrepareAsync(fixture.Source, "10.0.7", fixture.Upstream,
            Path.Combine(fixture.Root, "first"), CancellationToken.None, fixture.Remote);
        await Git(fixture.Source, "push", "origin", first.CandidateCommit + ":refs/heads/release/10.0");
        await Git(fixture.Source, "switch", "upstream");
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "upstream.txt"), "next stable release\n");
        await Git(fixture.Source, "add", "."); await Git(fixture.Source, "commit", "-m", "Next synthetic stable release");
        await Git(fixture.Source, "tag", "10.0.8");
        string next = (await Git(fixture.Source, "rev-parse", "HEAD")).Trim();
        await Git(fixture.Source, "push", "origin", "refs/tags/10.0.8");
        await Git(fixture.Source, "switch", "main");
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "new-feature.txt"), "new KAICad feature\n");
        await Git(fixture.Source, "add", "."); await Git(fixture.Source, "commit", "-m", "Next KAICad functionality");
        var second = await UpstreamMaintenance.PrepareAsync(fixture.Source, "10.0.8", next,
            Path.Combine(fixture.Root, "second"), CancellationToken.None, fixture.Remote);
        Assert.AreEqual("integrated", second.Status);
        Assert.IsFalse(second.CreateMaintenanceBranch);
        Assert.AreEqual("KAICad feature\n", await File.ReadAllTextAsync(Path.Combine(second.Directory, "feature.txt")));
        Assert.AreEqual("new KAICad feature\n", await File.ReadAllTextAsync(Path.Combine(second.Directory, "new-feature.txt")));
        Assert.AreEqual("next stable release\n", await File.ReadAllTextAsync(Path.Combine(second.Directory, "upstream.txt")));
        await Git(second.Directory, "merge-base", "--is-ancestor", first.CandidateCommit, second.CandidateCommit);
    }

    [TestMethod]
    public async Task ExistingCandidateAndMalformedProvenanceAreNeverOverwrittenOrGivenLegacyFallback()
    {
        await using var fixture = await GitFixture.Create(false);
        var first = await UpstreamMaintenance.PrepareAsync(fixture.Source, "10.0.7", fixture.Upstream,
            Path.Combine(fixture.Root, "first"), CancellationToken.None, fixture.Remote);
        await Git(fixture.Source, "push", "origin", first.CandidateCommit + ":refs/heads/" + first.CandidateBranch);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpstreamMaintenance.PrepareAsync(fixture.Source,
            "10.0.7", fixture.Upstream, Path.Combine(fixture.Root, "rejected"), CancellationToken.None, fixture.Remote));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "rejected")));
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, UpstreamProvenance.ManifestPath), "{ malformed");
        await Git(fixture.Source, "add", "."); await Git(fixture.Source, "commit", "-m", "Explicit invalid-manifest fixture");
        string current = (await Git(fixture.Source, "rev-parse", "HEAD")).Trim();
        await Assert.ThrowsExactlyAsync<JsonException>(() => UpstreamProvenance.ReadAsync(fixture.Source, current, CancellationToken.None));
    }

    [TestMethod]
    public async Task ActualHistoricalPreviewBaseRemainsAdmissible()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        var legacy = await UpstreamProvenance.ReadAsync(directory.FullName, UpstreamProvenance.LegacyPreviewBase, CancellationToken.None);
        Assert.AreEqual("preview", legacy.Channel);
        Assert.IsNull(legacy.Tag);
        Assert.AreEqual(UpstreamProvenance.LegacyPreviewBase, legacy.Commit);
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await error);
        return await output;
    }

    private sealed class GitFixture : IAsyncDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("kaicad-upstream-fixture-").FullName;
        public string Source => Path.Combine(Root, "source");
        public string Remote => Path.Combine(Root, "remote.git");
        public string Feature { get; private set; } = "";
        public string Upstream { get; private set; } = "";

        public static async Task<GitFixture> Create(bool conflict)
        {
            var f = new GitFixture();
            Directory.CreateDirectory(f.Source);
            await Git(f.Source, "init", "-b", "main");
            await Git(f.Source, "config", "user.name", "Explicit synthetic fixture");
            await Git(f.Source, "config", "user.email", "fixture@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(f.Source, "shared.txt"), "base value\n");
            await Git(f.Source, "add", "."); await Git(f.Source, "commit", "-m", "Synthetic upstream base");
            string baseline = (await Git(f.Source, "rev-parse", "HEAD")).Trim();
            await Git(f.Source, "switch", "-c", "upstream");
            await File.WriteAllTextAsync(Path.Combine(f.Source, "upstream.txt"), "upstream release\n");
            if (conflict) await File.WriteAllTextAsync(Path.Combine(f.Source, "shared.txt"), "upstream value\n");
            await Git(f.Source, "add", "."); await Git(f.Source, "commit", "-m", "Synthetic stable release");
            f.Upstream = (await Git(f.Source, "rev-parse", "HEAD")).Trim();
            await Git(f.Source, "tag", "-a", "10.0.7", "-m", "Synthetic release tag");
            await Git(f.Source, "switch", "main");
            Directory.CreateDirectory(Path.Combine(f.Source, "automation/distribution"));
            await File.WriteAllTextAsync(Path.Combine(f.Source, UpstreamProvenance.ManifestPath),
                JsonSerializer.Serialize(new UpstreamProvenance(1, UpstreamProvenance.CanonicalRepository, null,
                    baseline, "preview", 1), UpstreamProvenance.Json));
            await File.WriteAllTextAsync(Path.Combine(f.Source, "feature.txt"), "KAICad feature\n");
            if (conflict) await File.WriteAllTextAsync(Path.Combine(f.Source, "shared.txt"), "KAICad value\n");
            await Git(f.Source, "add", "."); await Git(f.Source, "commit", "-m", "Synthetic KAICad functionality");
            f.Feature = (await Git(f.Source, "rev-parse", "HEAD")).Trim();
            await Git(f.Root, "clone", "--bare", f.Source, f.Remote);
            await Git(f.Source, "remote", "add", "origin", f.Remote);
            return f;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (string directory in Directory.GetDirectories(Root))
                if (Directory.Exists(Path.Combine(directory, "candidate")))
                    await Git(Source, "worktree", "remove", "--force", Path.Combine(directory, "candidate"));
            Directory.Delete(Root, true);
        }
    }
}
