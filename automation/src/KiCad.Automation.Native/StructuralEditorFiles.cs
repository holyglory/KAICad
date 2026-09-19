using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KiCad.Automation.Model;
using P = KiCad.Automation.Protocol.Structural;

namespace KiCad.Automation.Native;

/// <summary>The native view edits typed structure; this .NET owner preserves the
/// complete engineering/native envelope and publishes through the existing
/// conflict-preserving file replacement. It never edits a live schematic.</summary>
public static class StructuralEditorFiles
{
    public static async Task<P.StructuralEditorDocument> ExecuteAsync(P.StructuralFileRequest request,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (request.SchemaVersion != 1 || !Enum.IsDefined(request.Action))
            throw Invalid("unsupported_structural_file_request", "Use the supported structural file command version and action.");
        if (!Path.IsPathFullyQualified(request.RepositoryRoot) || !Directory.Exists(request.RepositoryRoot)
            || !Path.IsPathFullyQualified(request.SourcePath))
            throw Invalid("invalid_structural_path", "Specify the existing repository root and absolute design path.");
        string root = Path.GetFullPath(request.RepositoryRoot);
        string source = Contained(root, request.SourcePath);
        var loaded = await Load(root, source, token);
        if (request.Action == P.StructuralFileAction.SfaRead) return Describe(source, loaded);
        if (request.ExpectedSourceToken != loaded.Token)
            throw Invalid("structural_file_changed", "The design file changed. Reload or reconcile it without discarding the current draft.");
        if (request.Diagram is null || request.Diagram.Id != loaded.Engineering.Structure.Id.ToString("D"))
            throw Invalid("structural_identity_changed", "The edited structure must retain the exact document identity.");
        var structure = StructuralEditorCodec.Decode(request.Diagram, loaded.Engineering.Circuit);
        var desired = loaded.Engineering with { Structure = structure };
        desired.Validate(loaded.Libraries);
        if (StructuralDiagramXml.Write(structure, desired.Circuit)
            == StructuralDiagramXml.Write(loaded.Engineering.Structure, loaded.Engineering.Circuit))
            return Describe(source, loaded); // No whitespace or file churn for an unchanged save.
        byte[] bytes = Encoding.UTF8.GetBytes(loaded.Native is { } native
            ? SchematicDesignXml.Write(native with { Engineering = desired }, loaded.Libraries)
            : EngineeringDesignXml.Write(desired, loaded.Libraries));
        string hash = await DesignFilePublisher.WriteIfUnchangedAsync(source, loaded.Bytes, bytes, token);
        return Describe(source, loaded with { Bytes = bytes, Token = hash, Engineering = desired,
            Native = loaded.Native is { } document ? document with { Engineering = desired } : null });
    }

    private static async Task<Loaded> Load(string root, string source, CancellationToken token)
    {
        byte[] bytes = await File.ReadAllBytesAsync(source, token);
        string xml = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff');
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var parsed = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        XNamespace engineering = EngineeringDesignXml.Namespace;
        bool native = parsed.Root?.Name == XName.Get("design", SchematicDesignXml.Namespace);
        XElement? model = native ? parsed.Root!.Element(engineering + "engineering-design") : parsed.Root;
        if (model?.Name != engineering + "engineering-design")
            throw Invalid("unsupported_structural_document", "Select a complete engineering design, not an inferred or unrelated XML document.");
        var libraries = new List<ComponentKnowledgeLibrary>();
        foreach (var reference in model.Element(engineering + "knowledge-libraries")?.Elements(engineering + "library") ?? [])
        {
            string relative = reference.Attribute("path")?.Value
                ?? throw Invalid("invalid_knowledge_library", "A knowledge library requires its declared repository path.");
            HardwareRepository.ValidatePath(relative);
            string path = Contained(root, Path.Combine(root, relative));
            libraries.Add(ComponentKnowledgeXml.ReadLibrary(await File.ReadAllTextAsync(path, token)));
        }
        SchematicDesign? full = native ? SchematicDesignXml.Read(xml, libraries) : null;
        var design = full?.Engineering ?? EngineeringDesignXml.Read(xml, libraries);
        return new(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), design, full, libraries);
    }

    private static P.StructuralEditorDocument Describe(string source, Loaded loaded)
    {
        var result = new P.StructuralEditorDocument { SchemaVersion = 1, SourcePath = source,
            DocumentId = loaded.Engineering.Structure.Id.ToString("D"), SourceToken = loaded.Token,
            DisplayName = Path.GetFileNameWithoutExtension(source),
            Diagram = StructuralEditorCodec.Encode(loaded.Engineering.Structure, loaded.Engineering.Circuit) };
        var definitions = loaded.Engineering.Circuit.Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);
        var parts = loaded.Engineering.Circuit.Parts.ToDictionary(p => p.Id);
        result.Components.Add(loaded.Engineering.Circuit.Components.OrderBy(c => c.Id).Select(c => new P.StructuralComponentDisplay
        { ComponentId = c.Id.ToString("D"), Reference = c.Reference, PartName = parts[definitions[c.DefinitionId].PartId].Name }));
        return result;
    }

    private static string Contained(string root, string path)
    {
        path = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, comparison))
            throw Invalid("structural_path_outside_repository", "The design and declared libraries must belong to the selected repository.");
        for (string? current = path; current is not null && !current.Equals(root, comparison); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Invalid("linked_structural_path", "Use the explicit repository path, not a linked design or library.");
        return path;
    }
    private sealed record Loaded(byte[] Bytes, string Token, EngineeringDesign Engineering, SchematicDesign? Native,
        IReadOnlyList<ComponentKnowledgeLibrary> Libraries);
    private static AutomationException Invalid(string code, string message) => new(code, message);
}
