using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record ComponentSourceSnapshot(string RelativePath, string ContentSha256);
public sealed record ComponentDesignReadFailure(Guid DesignId, string RelativePath, string Code, string Message);
public sealed record BlockComponentFileInspection(Guid RepositoryId, ImmutableArray<ComponentSourceSnapshot> Sources,
    ImmutableArray<ComponentDesignReadFailure> Failures, ImmutableArray<ComponentRealizationInspection> Components);

/// <summary>Inspect declared saved design files; never launch an editor, search by name,
/// substitute another library revision or rewrite an unresolved binding.</summary>
public static class BlockComponentFiles
{
    public static async Task<BlockComponentFileInspection> InspectAsync(string repositoryRoot, string manifestPath,
        string expectedManifestToken, BlockComponentBindings bindings, CancellationToken token = default)
    {
        bindings.Validate();
        if (string.IsNullOrEmpty(expectedManifestToken))
            throw new AutomationException("missing_hardware_source_token", "Supply the observed hardware manifest hash.");
        var sources = new Dictionary<string, ComponentSourceSnapshot>(StringComparer.Ordinal);
        var manifest = await Read(manifestPath);
        if (sources[manifestPath].ContentSha256 != expectedManifestToken)
            throw new AutomationException("hardware_manifest_changed", "The repository manifest changed; inspect its new revision before resolving components.");
        var repository = HardwareRepositoryXml.Read(manifest);
        var libraries = await BlockDefinitionLibraries.ReadAsync(repositoryRoot, repository.Libraries.Select(l => l.Path).ToArray(), token);
        foreach (var declared in repository.Libraries)
        {
            var snapshot = libraries.Single(l => l.RelativePath == declared.Path);
            if (snapshot.Library.Id != declared.Id || snapshot.Library.Revision != declared.Revision)
                throw new AutomationException("realization_library_mismatch", "The declared knowledge library identity or revision does not match its file.");
            AddSource(new(snapshot.RelativePath, snapshot.ContentSha256));
        }
        var designs = new Dictionary<Guid, SchematicDesign>();
        var failures = ImmutableArray.CreateBuilder<ComponentDesignReadFailure>();
        foreach (var declared in repository.Designs.Where(d => bindings.Targets.Any(t => t.DesignId == d.Id)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string xml = await Read(declared.ModelPath);
                designs.Add(declared.Id, SchematicDesignXml.Read(xml, [.. libraries.Select(l => l.Library)]));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AutomationException)
            {
                failures.Add(new(declared.Id, declared.ModelPath,
                    error is AutomationException failure ? failure.Code : "design_file_unavailable", error.Message));
            }
        }
        var components = BlockComponentResolver.Inspect(bindings, repository, designs, [.. libraries.Select(l => l.Library)], token);
        // Optimistic read checkpoint: if any successfully read source changes while
        // the report is assembled, discard the whole observation, never a partial match.
        foreach (var source in sources.Values)
        {
            string path = Contained(source.RelativePath);
            byte[] current = await File.ReadAllBytesAsync(path, token);
            if (Hash(current) != source.ContentSha256)
                throw new AutomationException("realization_source_changed", "A declared source changed during inspection; read a fresh observation.");
        }
        return new(repository.Id, [.. sources.Values], failures.ToImmutable(), components);

        string Contained(string relative)
        {
            HardwareRepository.ValidatePath(relative);
            return DiagramRequirementHistoryFiles.Contained(repositoryRoot, Path.Combine(repositoryRoot, relative), requireFile: true);
        }
        async Task<string> Read(string relative)
        {
            byte[] bytes = await File.ReadAllBytesAsync(Contained(relative), token);
            AddSource(new(relative, Hash(bytes)));
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new AutomationException("invalid_realization_encoding", "A declared source is not valid UTF-8."); }
        }
        void AddSource(ComponentSourceSnapshot source)
        {
            if (sources.TryGetValue(source.RelativePath, out var previous) && previous.ContentSha256 != source.ContentSha256)
                throw new AutomationException("realization_source_changed", "A shared source changed between reads; inspect a fresh snapshot.");
            sources[source.RelativePath] = source;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
