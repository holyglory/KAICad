using System.Security;
using System.Security.Cryptography;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Publishes a generated-artwork source before a board mutation and proves that
/// the exact bytes can be read back. The source archive is deliberately a
/// separate persistence boundary from KiCad's checked item transaction; the
/// receipt therefore proves archive preflight, not a joint board/archive
/// commit or power-loss durability of the parent directory entry.
/// </summary>
internal static class ArtworkSourceArchive
{
    internal sealed record Receipt(string RelativePath, string Sha256, long Bytes,
        bool Persisted, bool ExactBytesVerified, bool AtomicWrite);

    internal static Receipt Persist(string repositoryRoot, string relativePath, byte[] bytes,
        string expectedSha256, string invalidCode, string conflictCode)
    {
        if (!Path.IsPathFullyQualified(repositoryRoot) || string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.Split('/', '\\').Any(part => part is "" or "." or ".."))
            throw new AutomationException(invalidCode,
                "repositoryRoot must be absolute and the source archive path must be repository-relative.");

        string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new AutomationException(conflictCode,
                "The source bytes do not match the declared source SHA-256.");

        string root = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, pathComparison))
            throw new AutomationException(invalidCode, "The source archive escapes repositoryRoot.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            if (File.Exists(full))
            {
                byte[] existing = File.ReadAllBytes(full);
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(existing)), actualSha256,
                        StringComparison.Ordinal))
                    throw new AutomationException(conflictCode,
                        "The source archive already contains different bytes.");

                return new(relativePath.Replace('\\', '/'), actualSha256, existing.LongLength,
                    Persisted: true, ExactBytesVerified: true, AtomicWrite: false);
            }

            string temporary = Path.Combine(Path.GetDirectoryName(full)!,
                "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Write and flush a sibling file first, then rename it into
                // place.  A failed archive preflight exits before any native
                // board mutation is submitted.
                using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                try
                {
                    File.Move(temporary, full);
                }
                catch (IOException) when (File.Exists(full))
                {
                    // Another writer may have published the same archive
                    // between our existence check and rename. Reuse it only
                    // after hashing its complete bytes; a different payload
                    // remains a source conflict.
                    byte[] existing = File.ReadAllBytes(full);
                    if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(existing)), actualSha256,
                            StringComparison.Ordinal))
                        throw new AutomationException(conflictCode,
                            "The source archive already contains different bytes.");

                    return new(relativePath.Replace('\\', '/'), actualSha256, existing.LongLength,
                        Persisted: true, ExactBytesVerified: true, AtomicWrite: false);
                }
            }
            finally
            {
                TryDelete(temporary);
            }

            byte[] observed = File.ReadAllBytes(full);
            if (observed.LongLength != bytes.LongLength
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(observed)), actualSha256,
                    StringComparison.Ordinal))
                throw new AutomationException(invalidCode,
                    "The source archive could not be verified after its atomic write.");

            return new(relativePath.Replace('\\', '/'), actualSha256, observed.LongLength,
                Persisted: true, ExactBytesVerified: true, AtomicWrite: true);
        }
        catch (AutomationException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or SecurityException)
        {
            throw new AutomationException(invalidCode,
                "The source archive preflight could not persist and verify the exact bytes: "
                + error.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The primary archive failure remains the authoritative result;
            // a best-effort temporary-file cleanup cannot make it a success.
        }
    }
}
