using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record SchematicProjectSheet(Guid Id, string Name);

public sealed record SchematicProjectSkeleton(string ProjectFile, Guid RootSheetId,
    string ProjectName, IReadOnlyList<SchematicProjectSheet> Sheets)
{
    public static SchematicProjectSkeleton FromHierarchy(SchematicHierarchyData hierarchy,
        string? expectedProjectFile = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        var topology = SchematicHierarchyTopology.Inspect(hierarchy, cancellationToken);
        if (!topology.IsValid)
            throw new AutomationException("invalid_project_skeleton", "The schematic hierarchy is not valid enough to reconstruct its project file.");
        var declaredDocument = hierarchy.Document ?? throw new AutomationException("invalid_project_skeleton", "The hierarchy must declare a project document.");
        var project = declaredDocument.Project;
        if (project is null || string.IsNullOrWhiteSpace(project.Name) || project.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || project.Name.Contains('/') || project.Name.Contains('\\') || project.Name.Contains('\0')
            || !Path.IsPathFullyQualified(project.Path) || !Directory.Exists(project.Path))
            throw new AutomationException("invalid_project_skeleton", "The XML must identify one absolute existing project directory and a safe project name.");
        string projectFile = Path.GetFullPath(Path.Combine(project.Path, project.Name + ".kicad_pro"));
        if (expectedProjectFile is not null && !PathEquals(projectFile, expectedProjectFile))
            throw new AutomationException("project_identity_mismatch", "The XML project identity does not match the authoritative project file path.");
        if (declaredDocument.SheetPath?.Path is not { Count: 1 } rootPath
            || !Guid.TryParseExact(rootPath[0].Value, "D", out Guid rootId))
            throw new AutomationException("invalid_project_skeleton", "The XML must declare one canonical root sheet identity.");

        var sheets = new Dictionary<Guid, SchematicProjectSheet>();
        foreach (var screen in hierarchy.Instances)
        foreach (var sheet in screen.Items.Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(sheet.Id?.Value, "D", out Guid id)
                || string.IsNullOrWhiteSpace(sheet.NameField?.Text?.Text_)
                || sheet.NameField.Text.Text_.Contains('\0')
                || string.IsNullOrWhiteSpace(sheet.FilenameField?.Text?.Text_))
                throw new AutomationException("invalid_project_skeleton", "Every sheet symbol must carry a canonical ID, name and filename.");
            var entry = new SchematicProjectSheet(id, sheet.NameField.Text.Text_);
            if (sheets.TryGetValue(id, out var existing) && existing != entry)
                throw new AutomationException("conflicting_project_skeleton", "Repeated sheet instances disagree about a sheet entry.");
            sheets[id] = entry;
        }

        var ordered = new List<SchematicProjectSheet> { new(rootId, project.Name) };
        ordered.AddRange(sheets.Values.Where(s => s.Id != rootId).OrderBy(s => s.Id));
        return new(projectFile, rootId, project.Name, ordered);
    }

    public byte[] JsonBytes()
    {
        var root = new JsonObject
        {
            ["meta"] = new JsonObject { ["filename"] = ProjectName + ".kicad_pro", ["version"] = 3 },
            ["schematic"] = new JsonObject
            {
                ["meta"] = new JsonObject { ["version"] = 1 },
                ["top_level_sheets"] = new JsonArray(new JsonObject
                {
                    ["filename"] = ProjectName + ".kicad_sch", ["name"] = ProjectName, ["uuid"] = RootSheetId.ToString("D")
                })
            },
            ["sheets"] = new JsonArray(Sheets.Select(s => new JsonArray(s.Id.ToString("D"), s.Name)).ToArray())
        };
        return Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    public bool CreateIfMissing(string? authoritativeProjectFile = null)
    {
        string path = Path.GetFullPath(authoritativeProjectFile ?? ProjectFile);
        if (!PathEquals(path, ProjectFile))
            throw new AutomationException("project_identity_mismatch", "The requested project file does not match the XML project identity.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) return false;
        string temporary = path + ".codex-skeleton-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonBytes());
            File.Move(temporary, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path)) { return false; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    public string JsonSha256() => Convert.ToHexStringLower(SHA256.HashData(JsonBytes()));

    private static bool PathEquals(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
