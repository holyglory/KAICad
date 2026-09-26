using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record DiagramRequirementFileSnapshot(string Path, string ContentSha256,
    DiagramRequirementHistory History);

/// <summary>Repository-contained typed history persistence. This primitive
/// does not activate native designs; the owning edit transaction supplies its
/// operation journal and publishes the matching design reference separately.</summary>
public static class DiagramRequirementHistoryFiles
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<DiagramRequirementFileSnapshot> ReadAsync(string repositoryRoot, string path,
        DiagramRequirementScope expectedScope, CancellationToken token = default)
    {
        var loaded = await Load(repositoryRoot, path, expectedScope, token);
        return new(loaded.Path, Hash(loaded.Bytes), loaded.History);
    }

    public static async Task<DiagramRequirementFileSnapshot> CreateAsync(string repositoryRoot, string path,
        DiagramRequirementHistory initial, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(initial); token.ThrowIfCancellationRequested();
        Standalone(initial);
        path = Contained(repositoryRoot, path, requireFile: false);
        string xml = DiagramRequirementHistoryXml.Write(initial);
        if (File.Exists(path))
        {
            var existing = await ReadAsync(repositoryRoot, path, initial.Scope, token);
            if (DiagramRequirementHistoryXml.Write(existing.History) != xml)
                throw Invalid("requirement_file_exists", "Another history already exists at the selected path.");
            return existing;
        }

        byte[] bytes = Utf8.GetBytes(xml);
        string staged = path + ".initial-" + Guid.NewGuid().ToString("N");
        bool created = false, publicationAttempted = false;
        try
        {
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.SequentialScan))
            {
                created = true;
                await output.WriteAsync(bytes, token); await output.FlushAsync(token); output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            _ = Contained(repositoryRoot, path, requireFile: false);
            publicationAttempted = true;
            File.Move(staged, path, overwrite: false);
            var published = await ReadAsync(repositoryRoot, path, initial.Scope, CancellationToken.None);
            if (published.ContentSha256 != Hash(bytes))
                throw Invalid("requirement_file_changed_after_creation", "The history changed after creation. Keep the current file and inspect its revisions.");
            return published;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Invalid(publicationAttempted ? "requirement_file_creation_requires_recovery" : "requirement_file_io",
                publicationAttempted ? $"Creation is not confirmed. Preserve {path} and {staged} and inspect their contents. {error.Message}" : error.Message);
        }
        finally
        {
            if (created && !publicationAttempted)
            {
                try { File.Delete(staged); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static async Task<DiagramRequirementFileSnapshot> SaveAsync(string repositoryRoot, string path,
        string expectedContentSha256, Guid expectedSavedRevision, DiagramRequirementDraft draft,
        Guid newRevisionId, RequirementRevisionOrigin origin,
        IEnumerable<DiagramRequirementResolution>? resolutions = null, CancellationToken token = default)
    {
        if (draft?.Baseline is null) throw Invalid("invalid_requirement_draft", "Provide the draft with its exact baseline.");
        // Freeze any lazy/caller-owned resolution collection before async I/O.
        var choices = resolutions?.ToArray();
        var loaded = await Load(repositoryRoot, path, draft.Baseline.Scope, token);
        if (expectedContentSha256 != Hash(loaded.Bytes))
            throw Invalid("requirement_file_changed", "The history file changed. Preserve the draft and compare it with the latest saved revision.");
        var committed = loaded.History.Commit(expectedSavedRevision, draft, newRevisionId, origin, choices);
        if (!committed.Changed) return new(loaded.Path, Hash(loaded.Bytes), loaded.History);
        byte[] next = Utf8.GetBytes(DiagramRequirementHistoryXml.Write(committed.History));
        string hash = await DesignFilePublisher.WriteIfUnchangedAsync(loaded.Path, loaded.Bytes, next, token);
        return new(loaded.Path, hash, committed.History);
    }

    private static async Task<Loaded> Load(string repositoryRoot, string path,
        DiagramRequirementScope expectedScope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (expectedScope is null) throw Invalid("invalid_requirement_scope", "Provide an exact requirement owner.");
        expectedScope.Validate(); path = Contained(repositoryRoot, path, requireFile: true);
        byte[] bytes = await File.ReadAllBytesAsync(path, token);
        DiagramRequirementHistory history;
        try { history = DiagramRequirementHistoryXml.Read(Utf8.GetString(bytes)); }
        catch (DecoderFallbackException) { throw Invalid("invalid_requirement_history_xml", "History must contain valid UTF-8 text."); }
        if (history.Scope != expectedScope)
            throw Invalid("wrong_requirement_history", "The selected file belongs to another document, owner or design state.");
        Standalone(history);
        return new(path, bytes, history);
    }

    // A history that continues another implementation's history is resolved by the diagram that holds both;
    // a file holding one history alone cannot show or check the earlier texts, so it is refused, not truncated.
    private static void Standalone(DiagramRequirementHistory history)
    {
        if (history.DerivedFrom is not null)
            throw Invalid("invalid_requirement_history", "A history that continues another implementation's history belongs to its diagram file, not a separate history file.");
    }

    internal static string Contained(string root, string path, bool requireFile)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root) || !Path.IsPathFullyQualified(path))
            throw Invalid("invalid_requirement_path", "Specify an existing repository and an absolute history file path.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); path = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw Invalid("requirement_path_outside_repository", "History must belong to the selected repository.");
        for (string? current = path; current is not null && !current.Equals(root, comparison); current = Path.GetDirectoryName(current))
        {
            if (!requireFile && current == path && !File.Exists(current) && !Directory.Exists(current))
            {
                if (new FileInfo(current).LinkTarget is not null)
                    throw Invalid("linked_requirement_path", "Use the explicit history path, not a filesystem link.");
                continue;
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Invalid("linked_requirement_path", "Use the explicit repository path, not a filesystem link.");
        }
        if (Directory.Exists(path)) throw Invalid("invalid_requirement_path", "A history file cannot be a directory.");
        return path;
    }

    private sealed record Loaded(string Path, byte[] Bytes, DiagramRequirementHistory History);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static AutomationException Invalid(string code, string message) => new(code, message);
}
