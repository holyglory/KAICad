using System.Security.Cryptography;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record RetainedXmlLocation(string Status, string? Path, string? OriginalPath,
    string? ArchivePath, string? ErrorCode = null, string? ErrorMessage = null,
    string? ExpectedSha256 = null, bool? ContentVerified = null);

/// <summary>Relocate only a receipt-bound displaced XML file. Nothing is deleted
/// or overwritten, and immutable receipts remain resolvable after a process exit.</summary>
internal static class RetainedXmlHistory
{
    internal static RetainedXmlLocation Inspect(DesignSynchronizationReceipt receipt)
    {
        try { receipt.Validate(); }
        catch (AutomationException error)
        { return new("invalid", null, receipt.PreviousXmlPath, null, error.Code, error.Message); }
        if (receipt.PreviousXmlPath is null) return new("none", null, null, null);
        string expected = receipt.DesignPath + ".sync-" + receipt.OperationId.ToString("N");
        if (receipt.PreviousXmlPath != expected && receipt.PreviousXmlPath != expected + ".previous")
            return new("invalid", null, receipt.PreviousXmlPath, null, "invalid_retained_xml_target", "The retained path is not bound to this operation.");
        string folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(receipt.DesignPath)!, ".kicad-sync-history");
        string archived = System.IO.Path.Combine(folder, receipt.OperationId.ToString("N") + ".xml");
        try
        {
            int directoryKind = Kind(folder);
            if (directoryKind is not (0 or 2)) return Failure("invalid", "invalid_history_directory", "History requires an ordinary directory.");
            int original = Kind(receipt.PreviousXmlPath), history = Kind(archived);
            if (original > 1 || history > 1) return Failure("invalid", "invalid_retained_xml_file", "Retained history must be a regular file, not a link or directory.");
            if (original == 1 && history == 1) return Failure("conflict", "retained_xml_collision", "Both history locations exist; preserve both for inspection.");
            if (history == 1) return Present("archived", archived);
            if (original == 1) return Present("staged", receipt.PreviousXmlPath);
            return Failure("missing", "retained_xml_missing", "Neither recorded history location is available.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Failure("unavailable", "retained_xml_io", error.Message); }

        RetainedXmlLocation Failure(string status, string code, string message) =>
            new(status, null, receipt.PreviousXmlPath, archived, code, message, receipt.PreviousXmlSha256);

        RetainedXmlLocation Present(string status, string path)
        {
            if (receipt.PreviousXmlSha256 is not { } expected)
                return new(status, path, receipt.PreviousXmlPath, archived);
            using var input = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(input)) != expected)
                return new("changed", null, receipt.PreviousXmlPath, archived, "retained_xml_changed",
                    "The retained XML differs from the synchronized content; preserve it for reconciliation.", expected, false);
            return new(status, path, receipt.PreviousXmlPath, archived, ExpectedSha256: expected, ContentVerified: true);
        }
    }

    internal static async Task<byte[]> ReadVerifiedAsync(DesignSynchronizationReceipt receipt, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); receipt.Validate();
        if (receipt.PreviousXmlPath is null)
            throw new AutomationException("missing_retained_xml", "This synchronization did not retain a prior XML file.");
        if (receipt.PreviousXmlSha256 is null)
            throw new AutomationException("unverified_retained_xml", "This legacy receipt does not prove the retained content; inspect it before restoring identities.");
        var location = Inspect(receipt);
        if (location.ErrorCode is not null || location.Path is null || location.ContentVerified != true)
            throw new AutomationException(location.ErrorCode ?? "unverified_retained_xml", location.ErrorMessage ?? "Verified retained XML is unavailable.");
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(location.Path, token);
            token.ThrowIfCancellationRequested();
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != receipt.PreviousXmlSha256)
                throw new AutomationException("retained_xml_changed", "Retained XML changed while being read; no identities were restored.");
            return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AutomationException("retained_xml_io", error.Message); }
    }

    internal static RetainedXmlLocation Archive(DesignSynchronizationReceipt receipt, Action? afterMove = null)
    {
        var before = Inspect(receipt);
        if (before.Status != "staged") return before;
        try
        {
            string folder = System.IO.Path.GetDirectoryName(before.ArchivePath!)!;
            Directory.CreateDirectory(folder);
            if (Kind(folder) != 2) return before with { Status = "invalid", ErrorCode = "invalid_history_directory", ErrorMessage = "History directory changed before the move." };
            string ignore = System.IO.Path.Combine(folder, ".gitignore");
            try
            {
                using var output = new FileStream(ignore, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write("*\n"u8); output.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(ignore)) { } // Never replace an existing user file.
            PreservingFileReplacement.MoveNoReplace(before.OriginalPath!, before.ArchivePath!);
            afterMove?.Invoke();
            return Inspect(receipt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or EntryPointNotFoundException or DllNotFoundException or PlatformNotSupportedException)
        {
            var observed = Inspect(receipt);
            return observed with { ErrorCode = "retained_xml_archive_failed", ErrorMessage = error.Message };
        }
    }

    private static int Kind(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return 3;
            return (attributes & FileAttributes.Directory) != 0 ? 2 : 1;
        }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }
}
