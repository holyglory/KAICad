using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record BlockDefinitionLibrarySnapshot(string RelativePath, string ContentSha256, ComponentKnowledgeLibrary Library);

/// <summary>Read explicitly supplied repository libraries as data, never upgrade a
/// declared revision or guess a library from the component's displayed name.</summary>
public static class BlockDefinitionLibraries
{
    public static async Task<ImmutableArray<BlockDefinitionLibrarySnapshot>> ReadAsync(string repositoryRoot,
        IReadOnlyList<string> relativePaths, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var paths = relativePaths.ToImmutableArray();
        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length)
            throw new AutomationException("duplicate_definition_library_path", "Supply each exact repository library path once.");
        var result = ImmutableArray.CreateBuilder<BlockDefinitionLibrarySnapshot>();
        foreach (string relative in paths)
        {
            token.ThrowIfCancellationRequested(); HardwareRepository.ValidatePath(relative);
            string path = DiagramRequirementHistoryFiles.Contained(repositoryRoot, Path.Combine(repositoryRoot, relative), requireFile: true);
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            string xml;
            try { xml = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            { throw new AutomationException("invalid_definition_library_encoding", "The declared knowledge library is not valid UTF-8."); }
            result.Add(new(relative, Convert.ToHexStringLower(SHA256.HashData(bytes)), ComponentKnowledgeXml.ReadLibrary(xml)));
        }
        return result.ToImmutable();
    }
}
