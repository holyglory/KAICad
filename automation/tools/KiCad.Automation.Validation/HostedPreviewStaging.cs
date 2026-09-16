using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Validation;

public sealed record HostedPreviewRequest(string Candidate, string Commit, string Platform, string RunId,
    string Version, string Previous, string Output);
public sealed record HostedPreviewResult(string Status, string Directory, string Commit, string Platform,
    string RunId, int ArtifactCount, string CatalogueSha256, bool QualifyingDelivery = false);

/// <summary>Integrity-check trusted external build evidence and stage only public archives.
/// Does not execute native editors, authenticate CI, publish a site or advance an update feed.</summary>
public static partial class HostedPreviewStaging
{
    private sealed record CandidateReceipt(int SchemaVersion, string SourceCommit, string Platform,
        string Architecture, string RunId, string Status, IReadOnlyList<ValidationStep> Steps,
        IReadOnlyList<EvidenceFile> Artifacts, IReadOnlyList<EvidenceFile>? DiagnosticArtifacts,
        bool QualifyingDelivery, bool CrossPlatformReady);
    private static readonly JsonSerializerOptions CatalogueJson = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static async Task<HostedPreviewResult> RunAsync(HostedPreviewRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Evidence.RequireCommit(request.Commit);
        if (!ulong.TryParse(request.RunId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong run) || run == 0
            || request.Version is null || request.Version.Length is < 1 or > 120 || !VersionPattern().IsMatch(request.Version))
            throw new ArgumentException("An exact GitHub run ID and a bounded preview version are required.");
        (string platform, string architecture, string suffix) = request.Platform switch
        {
            "osx-arm64" => ("macos", "arm64", "macos-arm64.tar.gz"),
            "osx-x64" => ("macos", "x64", "macos-x64.tar.gz"),
            "win-x64" => ("windows", "x64", "windows-x64.zip"),
            _ => throw new ArgumentException("Select osx-arm64, osx-x64 or win-x64.")
        };
        string candidate = AbsoluteDirectory(request.Candidate), previous = AbsoluteDirectory(request.Previous);
        _ = AbsoluteDirectory(Path.Combine(candidate, "packages"));
        if (!Path.IsPathFullyQualified(request.Output)) throw new ArgumentException("Output must be absolute.");
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Output));
        if (Directory.Exists(output) || File.Exists(output) || Inside(output, previous) || Inside(output, candidate))
            throw new ArgumentException("Output must be new and outside both input trees.");

        CandidateReceipt receipt = JsonSerializer.Deserialize<CandidateReceipt>(await Metadata(Path.Combine(candidate, "receipt.json"), token))
            ?? throw new InvalidDataException("Missing hosted candidate receipt.");
        if (receipt.SchemaVersion != 1 || receipt.Status != "candidate_built" || receipt.SourceCommit != request.Commit
            || receipt.Platform != platform || receipt.Architecture != architecture || receipt.RunId != request.RunId
            || receipt.QualifyingDelivery || receipt.CrossPlatformReady || receipt.DiagnosticArtifacts is { Count: > 0 }
            || receipt.Steps is null || receipt.Steps.Count == 0 || receipt.Steps.Any(x => x is null || x.ExitCode != 0)
            || receipt.Artifacts is null || receipt.Artifacts.Count != 2)
            throw new InvalidDataException("The candidate is failed, ambiguous, diagnostic-only or belongs to another source/target/run.");
        string[] required = platform == "macos"
            ? ["source-commit", "pinned-ancestry", "official-dependencies", "package-native", "package-source", "source-still-clean"]
            : ["source-commit", "pinned-ancestry", "native-build", "native-tests", "native-install", "installed-native-commit", "managed-runtime", "managed-contracts", "installed-editor-journey", "package-source", "source-still-clean"];
        foreach (string step in required)
            if (receipt.Steps.Count(x => x.Name == step) != 1) throw new InvalidDataException("Missing or repeated successful build step: " + step);
        if (platform == "macos")
            await Evidence.VerifyAsync(Path.Combine(candidate, "mac/result.json"), Path.Combine(candidate, "mac/evidence.tar.gz"),
                request.Commit, token, architecture);
        else
            await VerifyWindowsEditorJourney(candidate, token);

        string prefix = "kicad-codex-" + request.Commit + "-";
        EvidenceFile app = SingleArtifact(prefix + suffix), source = SingleArtifact(prefix + "source.tar.gz");
        foreach (EvidenceFile file in receipt.Artifacts)
            await VerifyFile(Path.Combine(candidate, "packages", file.Path), file.Bytes, file.Sha256, token);
        var incoming = new[] { (app, request.Platform), (source, "source") }.Select(pair =>
            new PublicPreviewArtifact(new DownloadArtifact(pair.Item1.Path, pair.Item2, request.Version,
                request.Commit, source.Sha256, pair.Item1.Bytes, pair.Item1.Sha256),
                Path.Combine(candidate, "packages", pair.Item1.Path))).ToArray();
        var staged = await StagePublicAsync(previous, output, incoming, null, token);
        return new("staged", output, request.Commit, request.Platform, request.RunId, staged.Count, staged.Hash);

        EvidenceFile SingleArtifact(string name)
        {
            EvidenceFile[] matches = receipt.Artifacts.Where(x => x is not null && x.Path == name).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Missing or ambiguous native/source archive: " + name);
            return matches[0];
        }
    }

    private static Task VerifyWindowsEditorJourney(string candidate, CancellationToken token) =>
        VerifyWindowsEditorResult(Path.Combine(candidate, "evidence", "installed-editor-tests", "windows-installed-editor", "result.json"), token);

    internal static async Task VerifyWindowsEditorResult(string path, CancellationToken token)
    {
        const string failure = "The Windows candidate lacks a passing installed editor/MCP journey.";
        try
        {
            using var document = JsonDocument.Parse(await Metadata(path, token));
            var result = document.RootElement;
            if (result.GetProperty("schemaVersion").GetInt32() != 1 || result.GetProperty("status").GetString() != "passed")
                throw new InvalidDataException(failure);
            foreach (string field in new[] { "normalPackagedMcpLoading", "twoInstancesIsolated", "mcpRestartPreservedDirtyObjects", "nativeKeyboardSaveAndClose" })
                if (!result.GetProperty(field).GetBoolean()) throw new InvalidDataException(failure);
            var instances = result.GetProperty("instances").EnumerateArray().ToArray();
            if (instances.Length != 2) throw new InvalidDataException(failure);
            var ids = new HashSet<Guid>();
            foreach (var instance in instances)
                if (!Guid.TryParseExact(instance.GetProperty("instanceId").GetString(), "D", out var id) || id == Guid.Empty
                    || !ids.Add(id) || string.IsNullOrWhiteSpace(instance.GetProperty("processEpoch").GetString())
                    || !Guid.TryParseExact(instance.GetProperty("markerId").GetString(), "D", out var marker) || marker == Guid.Empty
                    || instance.GetProperty("nativeProcessExitCode").GetInt32() != 0)
                    throw new InvalidDataException(failure);
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException(failure, error); }
    }

    internal sealed record PublicPreviewArtifact(DownloadArtifact Artifact, string Source);
    internal sealed record PublicPreviewFeed(string RelativePath, byte[] Envelope, string? PreviousHash);

    internal static async Task<(int Count, string Hash)> StagePublicAsync(string previous, string output,
        IReadOnlyList<PublicPreviewArtifact> incoming, IReadOnlyList<PublicPreviewFeed>? replacements, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pendingFeeds = (replacements ?? []).ToDictionary(x => x.RelativePath, StringComparer.Ordinal);
        if (pendingFeeds.Any(x => !PlatformUpdateFeeds.IsFeedPath(x.Key)))
            throw new InvalidDataException("Invalid publication feed path.");
        var expectedFeeds = pendingFeeds.Values.Where(x => x.PreviousHash is not null).Select(x => x.RelativePath).ToHashSet(StringComparer.Ordinal);
        DownloadManifest old = JsonSerializer.Deserialize<DownloadManifest>(await Metadata(Path.Combine(previous, "downloads.json"), token), CatalogueJson)
            ?? throw new InvalidDataException("Missing previous public catalogue.");
        if (old.SchemaVersion != 1 || old.Artifacts is null || old.Artifacts.Count == 0)
            throw new InvalidDataException("The previous catalogue must contain declared public artifacts.");
        var merged = new Dictionary<string, DownloadArtifact>(StringComparer.Ordinal);
        var copies = new List<(string Source, string Relative, long Bytes, string Hash)>();
        foreach (DownloadArtifact item in old.Artifacts)
        {
            if (item is null || item.FileName is null || !ArtifactPattern().IsMatch(item.FileName)
                || item.Platform is not ("source" or "linux-x64" or "osx-arm64" or "osx-x64" or "win-x64")
                || string.IsNullOrWhiteSpace(item.Version) || !HashPattern().IsMatch(item.SourceSha256 ?? "")
                || !merged.TryAdd(item.FileName, item)) throw new InvalidDataException("Invalid or duplicated existing public artifact.");
            Evidence.RequireCommit(item.Commit);
            await VerifyFile(Path.Combine(previous, item.FileName), item.Bytes, item.Sha256, token);
            copies.Add((Path.Combine(previous, item.FileName), item.FileName, item.Bytes, item.Sha256));
        }
        foreach (var addition in incoming)
        {
            var item = addition.Artifact;
            if (item is null || item.FileName is null || !ArtifactPattern().IsMatch(item.FileName)
                || !HashPattern().IsMatch(item.SourceSha256 ?? ""))
                throw new InvalidDataException("Invalid incoming public artifact.");
            await VerifyFile(addition.Source, item.Bytes, item.Sha256, token);
            if (item.Platform == "source")
            {
                Evidence.RequireCommit(item.Commit);
                // Git archive encodings can differ between native builders.
                // Source links are byte-bound; never replace a published file
                // or claim that a different archive has the previous hash.
                if (merged.Values.Any(x => x.Platform == "source" && x.Commit == item.Commit
                        && x.Sha256 == item.Sha256 && x.Bytes == item.Bytes))
                    continue;
                if (merged.ContainsKey(item.FileName))
                    item = item with { FileName = "kicad-codex-" + item.Commit + "-source-" + item.Sha256 + ".tar.gz" };
            }
            if (merged.TryGetValue(item.FileName, out var existing))
            {
                if (existing != item) throw new InvalidDataException("An existing download has a conflicting identity: " + item.FileName);
            }
            else
            {
                merged.Add(item.FileName, item);
                copies.Add((addition.Source, item.FileName, item.Bytes, item.Sha256));
            }
        }
        string updates = Path.Combine(previous, "updates");
        if (Directory.Exists(updates))
        {
            _ = AbsoluteDirectory(updates);
            foreach (string path in Directory.GetFiles(updates, "*.json"))
            {
                if (!ChannelPattern().IsMatch(Path.GetFileName(path))) throw new InvalidDataException("Unexpected update-feed name.");
                byte[] bytes = await Metadata(path, token);
                QueueFeed(path, "updates/" + Path.GetFileName(path), bytes);
            }
            string platforms = Path.Combine(updates, "platforms");
            if (Directory.Exists(platforms))
            {
                _ = AbsoluteDirectory(platforms);
                foreach (string platform in PlatformUpdateFeeds.NativePlatforms)
                {
                    string directory = Path.Combine(platforms, platform);
                    if (!Directory.Exists(directory)) continue;
                    _ = AbsoluteDirectory(directory);
                    foreach (string path in Directory.GetFiles(directory, "*.json"))
                    {
                        string channel = Path.GetFileNameWithoutExtension(path);
                        string relative = PlatformUpdateFeeds.RelativeFeed(platform, channel);
                        byte[] bytes = await Metadata(path, token);
                        QueueFeed(path, relative, bytes);
                    }
                }
            }
        }
        if (expectedFeeds.Count != 0) throw new InvalidDataException("A previous feed disappeared during staging.");
        token.ThrowIfCancellationRequested();
        // Inputs were validated before creating output. No caller-owned tree is
        // deleted on failure; without downloads.json partial output cannot serve.
        Directory.CreateDirectory(output);
        foreach (var copy in copies)
        {
            token.ThrowIfCancellationRequested();
            string destination = Path.Combine(output, copy.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var from = File.OpenRead(copy.Source))
            await using (var to = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await from.CopyToAsync(to, token);
            await VerifyFile(destination, copy.Bytes, copy.Hash, token);
        }
        foreach (var replacement in pendingFeeds.Values)
        {
            string path = Path.Combine(output, replacement.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var feed = new FileStream(path,
                FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await feed.WriteAsync(replacement.Envelope, token);
        }
        byte[] catalogue = JsonSerializer.SerializeToUtf8Bytes(new DownloadManifest(1, merged.Values.ToArray()), CatalogueJson);
        await using (var stream = new FileStream(Path.Combine(output, "downloads.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await stream.WriteAsync(catalogue, token);
        return (merged.Count, Convert.ToHexStringLower(SHA256.HashData(catalogue)));

        void QueueFeed(string path, string relative, byte[] bytes)
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (pendingFeeds.TryGetValue(relative, out var replacement))
            {
                if (replacement.PreviousHash != hash) throw new InvalidDataException("A previous feed changed during staging.");
                expectedFeeds.Remove(relative);
            }
            else copies.Add((path, relative, bytes.Length, hash));
        }
    }

    internal static async Task VerifyFile(string path, long bytes, string hash, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || bytes <= 0 || info.Length != bytes || !HashPattern().IsMatch(hash ?? ""))
            throw new InvalidDataException("Missing, linked or invalid artifact: " + Path.GetFileName(path));
        await using var file = File.OpenRead(path);
        string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
        if (actual != hash) throw new InvalidDataException("Artifact checksum failed: " + Path.GetFileName(path));
    }

    internal static async Task<byte[]> Metadata(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length is <= 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("Missing, linked or oversized metadata: " + Path.GetFileName(path));
        return await File.ReadAllBytesAsync(path, token);
    }

    internal static string AbsoluteDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Input directories must be absolute.");
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget is not null) throw new ArgumentException("Use an existing ordinary input directory.");
        return Path.TrimEndingDirectorySeparator(info.FullName);
    }
    internal static bool Inside(string path, string root) => path == root || path.StartsWith(root + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._-]*\z")] private static partial Regex VersionPattern();
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._-]*\.(tar\.gz|zip|dmg|pkg|deb|sha256|sig)\z")] private static partial Regex ArtifactPattern();
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_-]*\.json\z")] private static partial Regex ChannelPattern();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z")] private static partial Regex HashPattern();
}
