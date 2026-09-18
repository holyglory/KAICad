using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Validation;

public sealed record ArchiveReplicaRoot(string Directory, string CatalogueSha256);
public sealed record IntermediateArchivePruneRequest(int SchemaVersion, ArchiveReplicaRoot Retained,
    IReadOnlyList<ArchiveReplicaRoot> CompletedImports);
public sealed record PrunedArchive(string Directory, string FileName, long Bytes, string Sha256, string RetainedCopy, bool Removed);
public sealed record ArchivePruneReceipt(int SchemaVersion, string Status, string RetainedDirectory,
    IReadOnlyList<PrunedArchive> Archives, long RemovedLogicalBytes, string? Error);

/// <summary>Exact-target removal of recoverable intermediate copies only. This
/// does not discover "old" directories, prune published roots, or delete metadata.
/// The caller must first prove the selected imports have no active consumers.</summary>
public static class IntermediateArchivePruning
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static async Task<IntermediateArchivePruneRequest> ReadAsync(string path, CancellationToken token) =>
        JsonSerializer.Deserialize<IntermediateArchivePruneRequest>(await HostedPreviewStaging.Metadata(path, token), Json)
        ?? throw new InvalidDataException("Missing explicit cleanup declaration.");

    public static Task<ArchivePruneReceipt> RunAsync(IntermediateArchivePruneRequest request, string receiptPath,
        CancellationToken token = default) => RunAsync(request, receiptPath, token, null);

    internal static async Task<ArchivePruneReceipt> RunAsync(IntermediateArchivePruneRequest request, string receiptPath,
        CancellationToken token, Action<int>? beforeArchive)
    {
        token.ThrowIfCancellationRequested();
        if (request.SchemaVersion != 1 || request.Retained is null || request.CompletedImports is not { Count: > 0 and <= 8 }
            || request.CompletedImports.Any(s => s is null))
            throw new ArgumentException("Select one to eight completed import roots using schema1.");
        string retained = DirectoryPath(request.Retained.Directory);
        var roots = request.CompletedImports.Select(s => DirectoryPath(s.Directory)).ToArray();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (roots.Distinct(comparer).Count() != roots.Length || roots.Any(r => HostedPreviewStaging.Inside(r, retained)
            || HostedPreviewStaging.Inside(retained, r)) || roots.Any(r => roots.Any(other => r != other && HostedPreviewStaging.Inside(r, other))))
            throw new ArgumentException("Import roots must be distinct, non-nested and separate from the retained root.");
        if (!Path.IsPathFullyQualified(receiptPath) || File.Exists(receiptPath) || Directory.Exists(receiptPath))
            throw new ArgumentException("Use a new absolute cleanup receipt path.");
        receiptPath = Path.GetFullPath(receiptPath);
        _ = DirectoryPath(Path.GetDirectoryName(receiptPath)!);
        if (roots.Append(retained).Any(root => HostedPreviewStaging.Inside(receiptPath, root)))
            throw new ArgumentException("Keep cleanup evidence outside the archive roots.");
        var available = await Catalogue(retained, request.Retained.CatalogueSha256, token);
        var planned = new List<(string Directory, DownloadArtifact Artifact, string Replica)>();
        // Validate every replacement and every present original before the
        // first removal, so ordinary missing/corrupt inputs cannot cause a
        // partial cleanup. New or unlisted files are never selected.
        var verified = new HashSet<string>(comparer);
        for (int i = 0; i < roots.Length; i++)
        {
            var catalogue = await Catalogue(roots[i], request.CompletedImports[i].CatalogueSha256, token);
            foreach (var item in catalogue.Values)
            {
                if (!available.TryGetValue(item.FileName, out var replica) || replica.Bytes != item.Bytes || replica.Sha256 != item.Sha256)
                    throw new InvalidDataException("No exact retained replica for " + item.FileName);
                string copy = Path.Combine(retained, replica.FileName);
                if (verified.Add(copy)) await HostedPreviewStaging.VerifyFile(copy, replica.Bytes, replica.Sha256, token);
                string source = Path.Combine(roots[i], item.FileName);
                if (new FileInfo(source).LinkTarget is not null) throw new InvalidDataException("Linked source artifacts cannot be pruned.");
                if (File.Exists(source)) await HostedPreviewStaging.VerifyFile(source, item.Bytes, item.Sha256, token);
                planned.Add((roots[i], item, copy));
            }
        }
        token.ThrowIfCancellationRequested();
        var removed = new List<PrunedArchive>();
        string status = "completed"; string? failure = null;
        // Reserve the receipt before mutation. Even cancellation/failure keeps
        // a compact list of completed removals and their retained recovery copy.
        await using var output = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            foreach (var item in planned)
            {
                beforeArchive?.Invoke(removed.Count);
                token.ThrowIfCancellationRequested();
                _ = DirectoryPath(item.Directory); _ = DirectoryPath(retained);
                await HostedPreviewStaging.VerifyFile(item.Replica, item.Artifact.Bytes, item.Artifact.Sha256, token);
                string source = Path.Combine(item.Directory, item.Artifact.FileName);
                bool exists = File.Exists(source);
                if (new FileInfo(source).LinkTarget is not null) throw new InvalidDataException("The selected source became a link.");
                if (exists)
                {
                    await HostedPreviewStaging.VerifyFile(source, item.Artifact.Bytes, item.Artifact.Sha256, token);
                    File.Delete(source);
                }
                removed.Add(new(item.Directory, item.Artifact.FileName, item.Artifact.Bytes, item.Artifact.Sha256, item.Replica, exists));
            }
        }
        catch (Exception error)
        {
            status = error is OperationCanceledException ? "cancelled" : "failed";
            failure = error.GetType().Name; throw;
        }
        finally
        {
            var receipt = new ArchivePruneReceipt(1, status, retained, removed,
                removed.Where(r => r.Removed).Sum(r => r.Bytes), failure);
            await JsonSerializer.SerializeAsync(output, receipt, Json, CancellationToken.None);
            await output.FlushAsync(CancellationToken.None);
        }
        return new(1, status, retained, removed, removed.Where(r => r.Removed).Sum(r => r.Bytes), null);
    }

    private static string DirectoryPath(string path)
    {
        string root = HostedPreviewStaging.AbsoluteDirectory(path);
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null) throw new ArgumentException("Archive roots cannot traverse linked directories.");
        return root;
    }

    private static async Task<Dictionary<string, DownloadArtifact>> Catalogue(string root, string expectedHash, CancellationToken token)
    {
        byte[] bytes = await HostedPreviewStaging.Metadata(Path.Combine(root, "downloads.json"), token);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedHash)
            throw new InvalidDataException("The selected catalogue changed.");
        var manifest = JsonSerializer.Deserialize<DownloadManifest>(bytes, Json) ?? throw new InvalidDataException("Missing catalogue.");
        if (manifest.SchemaVersion != 1 || manifest.Artifacts is not { Count: > 0 }) throw new InvalidDataException("Unsupported empty catalogue.");
        var result = new Dictionary<string, DownloadArtifact>(StringComparer.Ordinal);
        foreach (var item in manifest.Artifacts)
        {
            if (item is null) throw new InvalidDataException("A catalogue contains an absent artifact record.");
            string name = item.FileName;
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\')
                || !name.StartsWith("kicad-codex-", StringComparison.Ordinal)
                || !(name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".zip", StringComparison.Ordinal)
                    || name.EndsWith(".deb", StringComparison.Ordinal) || name.EndsWith(".dmg", StringComparison.Ordinal) || name.EndsWith(".pkg", StringComparison.Ordinal))
                || !result.TryAdd(name, item))
                throw new InvalidDataException("Only distinct, explicitly catalogued application/source archives may be pruned.");
        }
        return result;
    }
}
