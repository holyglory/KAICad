using System.Security.Cryptography;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public enum RefinementAssetStatus { Available, Missing, Changed, Unavailable }
public sealed record RefinementAssetObservation(DiagramRefinementAttachment Attachment, RefinementAssetStatus Status,
    string? ObservedSha256, long? ObservedByteCount, string? ErrorCode, string? ErrorMessage);

/// <summary>Preserve declared original bytes as ordinary repository assets. No renderer,
/// extractor, agent or executable is invoked based on a file's contents or media type.</summary>
public static class RefinementAssetFiles
{
    public static async Task<DiagramRefinementAttachment> CaptureAsync(string repositoryRoot, string sourceRelativePath,
        string archiveDirectory, Guid attachmentId, string expectedSha256, long expectedByteCount, string mediaType,
        SourceReference? source = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); HardwareRepository.ValidatePath(sourceRelativePath);
        HardwareRepository.ValidatePath(archiveDirectory);
        string relative = archiveDirectory + "/" + expectedSha256;
        var attachment = new DiagramRefinementAttachment(attachmentId, Path.GetFileName(sourceRelativePath), relative,
            expectedSha256, expectedByteCount, mediaType, source);
        attachment.Validate();
        string directory = EnsureDirectory(repositoryRoot, archiveDirectory);
        string destination = Path.Combine(directory, expectedSha256);
        if (File.Exists(destination)) { await RequireAvailable(repositoryRoot, attachment, token); return attachment; }
        string original = Contained(repositoryRoot, sourceRelativePath);
        string temporary = Path.Combine(directory, ".capture-" + Guid.NewGuid().ToString("N"));
        bool created = false;
        try
        {
            await using (var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                var actual = await Digest(input, output, token);
                if (actual.Hash != expectedSha256 || actual.Bytes != expectedByteCount)
                    throw new AutomationException("refinement_source_changed", "The source bytes do not match the observed attachment; no archive was published.");
                await output.FlushAsync(token); output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            _ = EnsureDirectory(repositoryRoot, archiveDirectory);
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination))
            {
                // A concurrent capture may have published the same content. Verify
                // it, never overwrite a corrupt or different existing asset.
                await RequireAvailable(repositoryRoot, attachment, token);
            }
            await RequireAvailable(repositoryRoot, attachment, token);
            return attachment;
        }
        finally
        {
            // Only our unique, successfully created staging file is disposable.
            if (created && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<RefinementAssetObservation> InspectAsync(string repositoryRoot,
        DiagramRefinementAttachment attachment, CancellationToken token = default)
    {
        attachment.Validate(); token.ThrowIfCancellationRequested();
        try
        {
            string path = Contained(repositoryRoot, attachment.AssetPath);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = await Digest(input, null, token);
            return new(attachment, actual.Hash == attachment.ContentSha256 && actual.Bytes == attachment.ByteCount
                ? RefinementAssetStatus.Available : RefinementAssetStatus.Changed, actual.Hash, actual.Bytes, null, null);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new(attachment, RefinementAssetStatus.Missing, null, null, "refinement_asset_missing", error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AutomationException)
        { return new(attachment, RefinementAssetStatus.Unavailable, null, null, error is AutomationException a ? a.Code : "refinement_asset_io", error.Message); }
    }

    public static async Task RequireAvailable(string repositoryRoot, DiagramRefinementAttachment attachment, CancellationToken token = default)
    {
        var observed = await InspectAsync(repositoryRoot, attachment, token);
        if (observed.Status != RefinementAssetStatus.Available)
            throw new AutomationException("refinement_asset_unavailable", "The preserved asset is " + observed.Status + ": " + attachment.AssetPath);
    }

    private static async Task<(string Hash, long Bytes)> Digest(Stream input, Stream? output, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024]; long count = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            hash.AppendData(buffer, 0, read); count = checked(count + read);
            if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), count);
    }

    private static string Contained(string root, string relative)
    {
        HardwareRepository.ValidatePath(relative);
        return DiagramRequirementHistoryFiles.Contained(root, Path.Combine(root, relative), requireFile: true);
    }

    private static string EnsureDirectory(string root, string relative)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new AutomationException("invalid_refinement_repository", "Supply the existing absolute engineering repository.");
        HardwareRepository.ValidatePath(relative); string path = Path.GetFullPath(root);
        foreach (string segment in relative.Split('/'))
        {
            path = Path.Combine(path, segment);
            // CreateDirectory is idempotent, but a pre-existing link/file is not an
            // archive container. Retain the same ordinary-path rules as design I/O.
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { Directory.CreateDirectory(path); attributes = File.GetAttributes(path); }
            catch (DirectoryNotFoundException) { Directory.CreateDirectory(path); attributes = File.GetAttributes(path); }
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new AutomationException("invalid_refinement_asset_directory", "The asset directory must be an ordinary repository directory, not a file or link.");
        }
        return path;
    }
}
