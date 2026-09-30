using System.Security.Cryptography;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>Draft XML publisher. Preserve the actually displaced file, including
/// on a racing external save; conflict does not imply rollback. Integrating the
/// retained filename with durable synchronization recovery is still required.</summary>
public static class DesignFilePublisher
{
    public static Task<string> WriteIfUnchangedAsync(string path, ReadOnlyMemory<byte> expected,
        ReadOnlyMemory<byte> replacement, CancellationToken cancellationToken = default) =>
        WriteCoreAsync(path, expected, replacement, null, null, cancellationToken);

    internal static async Task<string> WriteCoreAsync(string path, ReadOnlyMemory<byte> expected,
        ReadOnlyMemory<byte> replacement, Action? beforeReplace, Action<string, string>? replace,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new AutomationException("invalid_design_path", "The design XML path must be absolute.");
        path = Path.GetFullPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        RequireRegular(path);
        // Freeze caller-owned buffers before awaiting any file operation.
        byte[] expectedBytes = expected.ToArray(), replacementBytes = replacement.ToArray();
        byte[] current;
        try { current = await File.ReadAllBytesAsync(path, cancellationToken); }
        catch (FileNotFoundException) { throw new AutomationException("design_file_missing", "The design XML file no longer exists."); }
        catch (DirectoryNotFoundException) { throw new AutomationException("design_file_missing", "The design XML directory no longer exists."); }
        if (!current.AsSpan().SequenceEqual(expectedBytes))
            throw new AutomationException("design_file_changed", "The design XML changed while synchronization was being prepared.");
        if (current.AsSpan().SequenceEqual(replacementBytes))
            return Convert.ToHexStringLower(SHA256.HashData(current));

        string temporary = path + ".sync-" + Guid.NewGuid().ToString("N");
        string previous = PreservingFileReplacement.PreviousPath(temporary);
        bool replacementAttempted = false;
        try
        {
            RequireRegular(path);
            using var ownership = new FileStream(path + ".sync.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, options: FileOptions.SequentialScan))
            {
                await output.WriteAsync(replacementBytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            beforeReplace?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            // Once a native replacement was attempted, uncertain failures must
            // retain both paths: either may contain the only copy of newer work.
            replacementAttempted = true;
            (replace ?? PreservingFileReplacement.Replace)(path, temporary);
            RequireRegular(previous);
            byte[] displaced = await File.ReadAllBytesAsync(previous, CancellationToken.None);
            if (!displaced.AsSpan().SequenceEqual(expectedBytes))
                throw new AutomationException("design_file_conflict_preserved",
                    $"XML changed during replacement. The displaced version is retained at {previous}; the new candidate may already be visible. Reconcile both versions before continuing.");
            RequireRegular(path);
            byte[] published = await File.ReadAllBytesAsync(path, CancellationToken.None);
            if (!published.AsSpan().SequenceEqual(replacementBytes))
                throw new AutomationException("design_file_changed_after_publication",
                    $"XML changed after replacement. Prior bytes remain at {previous}; do not advance synchronization.");
            // Do not unlink displaced files here. An external writer may still
            // hold one open; retention/cleanup belongs to synchronized recovery.
            return Convert.ToHexStringLower(SHA256.HashData(replacementBytes));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or EntryPointNotFoundException or DllNotFoundException or PlatformNotSupportedException)
        {
            throw new AutomationException(replacementAttempted ? "design_publication_requires_recovery" : "design_file_io",
                replacementAttempted ? $"{error.Message} Retain and inspect {path}, {temporary} and {previous}; replacement is not confirmed." : error.Message);
        }
        finally
        {
            if (!replacementAttempted)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void RequireRegular(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new AutomationException("invalid_design_file", "XML publication requires a regular file, not a directory or link.");
    }
}
