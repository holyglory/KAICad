using System.Security.Cryptography;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>Atomically publishes the user-visible engineering XML after checking
/// that no newer file save replaced the source observed by synchronization.</summary>
public static class DesignFilePublisher
{
    public static async Task<string> WriteIfUnchangedAsync(string path, ReadOnlyMemory<byte> expected,
        ReadOnlyMemory<byte> replacement, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new AutomationException("invalid_design_path", "The design XML path must be absolute.");
        path = Path.GetFullPath(path);
        byte[] current;
        try { current = await File.ReadAllBytesAsync(path, cancellationToken); }
        catch (FileNotFoundException) { throw new AutomationException("design_file_missing", "The design XML file no longer exists."); }
        catch (DirectoryNotFoundException) { throw new AutomationException("design_file_missing", "The design XML directory no longer exists."); }
        if (!current.AsSpan().SequenceEqual(expected.Span))
            throw new AutomationException("design_file_changed", "The design XML changed while synchronization was being prepared.");
        if (current.AsSpan().SequenceEqual(replacement.Span))
            return Convert.ToHexStringLower(SHA256.HashData(current));

        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = path + ".sync-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, options: FileOptions.SequentialScan))
            {
                await output.WriteAsync(replacement, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            temporary = string.Empty;
            return Convert.ToHexStringLower(SHA256.HashData(replacement.Span));
        }
        catch (IOException error) { throw new AutomationException("design_file_io", error.Message); }
        catch (UnauthorizedAccessException error) { throw new AutomationException("design_file_io", error.Message); }
        finally
        {
            if (temporary.Length != 0)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
