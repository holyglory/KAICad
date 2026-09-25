using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KiCad.Automation.Model;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Native;

/// <summary>Creating a new system diagram and finding a project's diagrams (contract rbg-v2 section 7,
/// actions 16 and 19). These are helper actions the project manager can call directly, so neither
/// depends on MCP. Create only ever creates a new file: an existing file is never overwritten, a
/// retried operation recognises its own result and every refusal leaves all files as they were.
/// Discovery never writes. Legacy flat structural diagrams are not diagrams here: they are neither
/// listed nor converted (owner decision n9af098253fec71da).</summary>
public static class RecursiveDiagramFiles
{
    public const string ConventionalSuffix = ".system-diagram.xml";
    public const int DiscoveryFileLimit = 256;
    public const int RepositoryWalkLimit = 8;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Validates the target shape of one action and runs it. Called by
    /// <see cref="RecursiveEditorFiles.ExecuteAsync"/> before any document identity is read.</summary>
    internal static Task<P.RecursiveFileResult> ExecuteAsync(P.RecursiveFileRequest request, CancellationToken token)
    {
        bool create = request.Action == P.RecursiveFileAction.RfaCreateDiagram;
        if (request.DocumentId.Length != 0 || request.ExpectedSourceToken.Length != 0 || request.Block is not null || request.Connection is not null
            || request.Field != P.RequirementFieldKind.RfkUnknown || request.Offset != 0 || request.Limit != 0 || request.Save is not null
            || request.Rebase is not null || request.SaveConnection is not null || request.Implementation is not null || request.InspectedBlock is not null
            || request.Restoration is not null || request.LevelEdit is not null || request.SaveLevel is not null || request.RebaseLevel is not null
            || request.Reparent is not null || !create && request.Create is not null || create && request.Discover is not null
            || !create && (request.RepositoryRoot.Length != 0 || request.SourcePath.Length != 0))
            throw Refused("ambiguous_diagram_file_request",
                "Creating and discovering diagrams take only their own target and payload; no document identity or file token.");
        return create ? CreateAsync(request, token) : DiscoverAsync(request.Discover, token);
    }

    // ---- Create (action 16) ----------------------------------------------------------------------

    /// <summary>A new diagram whose root block is only its caption (owner decision n98a3f3c41084f0ed):
    /// no requirement text, interfaces, children or definition. Detail is added later, when it is known.</summary>
    private static async Task<P.RecursiveFileResult> CreateAsync(P.RecursiveFileRequest request, CancellationToken token)
    {
        var data = request.Create ?? throw Refused("invalid_diagram_create_request", "Provide the diagram creation payload.");
        if (!Guid.TryParseExact(data.OperationId, "D", out Guid operation) || operation == Guid.Empty || operation.ToString("D") != data.OperationId
            || data.Origin is null)
            throw Refused("invalid_diagram_create_request", "Creating a diagram needs a canonical operation identity and its origin.");
        if (data.Fields is { } fields && (fields.General.Length != 0 || fields.Schematic.Length != 0 || fields.Routing.Length != 0))
            throw Refused("invalid_diagram_create_request",
                "A new diagram starts with only its root block's caption; add requirement text once the diagram exists. Nothing was written.");
        RequirementRevisionOrigin origin;
        try { origin = RecursiveBlockCodec.DecodeOrigin(data.Origin); }
        catch (AutomationException error) { throw Refused("invalid_diagram_create_request", error.Message); }
        if (!Enum.IsDefined(origin.ActorKind) || origin.ActorKind == RequirementRevisionActor.Import)
            throw Refused("invalid_diagram_create_request", "A new diagram is created by a person, the editor or an agent, not imported.");
        var (root, target) = Target(request);
        RecursiveBlockGraph graph;
        try { graph = RecursiveBlockGraph.CreateEmpty(operation, data.RootName, data.ImplementationName, DiagramRequirements.Empty, origin); }
        catch (AutomationException error) when (error.Code is "invalid_requirement_origin" or "invalid_diagram_requirements" or "invalid_recursive_block_graph")
        { throw Refused("invalid_diagram_create_request", error.Message); }
        var (snapshot, created) = await CreateNewAsync(root, target, graph, token);
        var result = RecursiveEditorFiles.Describe(snapshot, RecursiveBlockCodec.SchemaVersion);
        result.Created = created;
        return result;
    }

    /// <summary>Publishes a new diagram file without ever replacing one: the bytes go to a staged
    /// <c>&lt;target&gt;.initial-&lt;random&gt;</c> file, containment is checked again and the staged file is
    /// moved into place with overwrite disabled, then read back. An existing target created by the same
    /// operation is reported with created = false (an observation, not a receipt); any other existing
    /// target is refused. If the move itself fails for another reason, both paths are kept for recovery.</summary>
    private static async Task<(RecursiveBlockFileSnapshot Snapshot, bool Created)> CreateNewAsync(string root, string target,
        RecursiveBlockGraph graph, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(target)) return await ExistingAsync(target, graph, token);
        var (bytes, version) = RecursiveBlockFiles.Serialize(graph);
        string staged = target + ".initial-" + RandomNumberGenerator.GetHexString(16, lowercase: true);
        bool stagedCreated = false, moveAttempted = false, keepStaged = false, concurrent = false;
        try
        {
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.None))
            {
                stagedCreated = true;
                await output.WriteAsync(bytes, token); await output.FlushAsync(token); output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            try { _ = DiagramRequirementHistoryFiles.Contained(root, target, requireFile: false); }
            catch (AutomationException) { throw PathRefused(); }
            moveAttempted = true;
            File.Move(staged, target, overwrite: false);
            keepStaged = true; // Moved: the staged name no longer exists.
        }
        catch (UnauthorizedAccessException) when (!moveAttempted)
        {
            throw Refused("diagram_file_read_only",
                $"The diagram cannot be created in {Path.GetDirectoryName(target)} because that folder is not writable. Nothing was written.");
        }
        catch (IOException error) when (!moveAttempted)
        { throw Refused("diagram_file_error", $"The diagram could not be written: {error.Message} Nothing was created."); }
        catch (IOException) when (File.Exists(target) && File.Exists(staged))
        {
            // Another writer created the target first; this operation's staged file was never moved.
            concurrent = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            keepStaged = true;
            throw Refused("diagram_creation_requires_recovery",
                $"Creating {target} is not confirmed: {error.Message} Both {target} and {staged} are kept; inspect them before trying again.");
        }
        finally
        {
            if (stagedCreated && !keepStaged)
            {
                try { File.Delete(staged); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        if (concurrent) return await ExistingAsync(target, graph, token);
        string expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var published = await TryReadRecursiveAsync(target, CancellationToken.None);
        if (published.Token != expected || published.Graph is null)
            throw Refused("diagram_creation_requires_recovery", $"{target} changed right after it was created; keep it and inspect its contents.");
        return (new(target, expected, published.Graph, version), true);
    }

    /// <summary>The target already exists: the same operation's own diagram is observed, never rewritten;
    /// anything else is refused and left exactly as it is.</summary>
    private static async Task<(RecursiveBlockFileSnapshot Snapshot, bool Created)> ExistingAsync(string target, RecursiveBlockGraph graph,
        CancellationToken token)
    {
        var present = await TryReadRecursiveAsync(target, token);
        if (present.Graph is { } existing && existing.DocumentId == graph.DocumentId)
            return (new(target, present.Token!, existing, present.Version), false);
        throw new AutomationException("diagram_file_exists",
            "A file already exists at the chosen diagram path; it is never overwritten. Choose another path or open that diagram.",
            [new AutomationErrorDetail("existing_file", null, present.Graph?.DocumentId, target)]);
    }

    // ---- Discovery (action 19) -------------------------------------------------------------------

    private static async Task<P.RecursiveFileResult> DiscoverAsync(P.DiscoverDiagramsData? data, CancellationToken token)
    {
        string project = data?.ProjectFile ?? "";
        if (!Path.IsPathFullyQualified(project) || !project.EndsWith(".kicad_pro", StringComparison.Ordinal) || !File.Exists(project) || IsLink(project))
            throw Refused("invalid_discovery_request", "Discovery needs the absolute path of an existing .kicad_pro project file.");
        project = Path.GetFullPath(project);
        string directory = Path.GetDirectoryName(project)!, stem = Path.GetFileNameWithoutExtension(project);
        var result = new P.DiagramDiscoveryData { RepositoryRoot = RepositoryRoot(directory), ProjectFile = project };
        string conventional = Path.Combine(directory, stem + ConventionalSuffix);
        result.SuggestedNewPath = conventional;
        result.SuggestedPathExists = File.Exists(conventional) || Directory.Exists(conventional);

        // Candidates: the conventional path first, then every other top-level XML file of the project
        // directory. Diagrams are recognised by content, never by file name.
        var candidates = new List<string>();
        var diagrams = new List<P.DiscoveredDiagramData>();
        // A filesystem link at the conventional path is never followed, created over or opened.
        if (File.Exists(conventional) && IsLink(conventional))
            diagrams.Add(Diagram(conventional, P.DiscoveredDiagramStatus.DdsInvalid, "invalid_diagram_path",
                "The conventional diagram path is a filesystem link; it is not followed.", true));
        else if (File.Exists(conventional)) candidates.Add(conventional);
        candidates.AddRange(TopLevelXml(directory).Where(f => f != conventional));
        result.Truncated = candidates.Count > DiscoveryFileLimit;
        foreach (string file in candidates.Take(DiscoveryFileLimit))
        {
            bool isConventional = file == conventional;
            XName? rootName;
            try { rootName = Sniff(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (isConventional) diagrams.Add(Diagram(file, P.DiscoveredDiagramStatus.DdsUnreadable, "diagram_file_unreadable", error.Message, true));
                continue;
            }
            catch (XmlException error)
            {
                if (isConventional) diagrams.Add(Diagram(file, P.DiscoveredDiagramStatus.DdsInvalid, "invalid_recursive_block_graph_xml", error.Message, true));
                continue;
            }
            if (IsRecursive(rootName))
            {
                var read = await TryReadRecursiveAsync(file, token);
                var row = Diagram(file, read.Status, read.Code ?? "", read.Message ?? "", isConventional);
                if (read.Graph is { } graph)
                {
                    row.DocumentId = graph.DocumentId.ToString("D"); row.StoredSchemaVersion = checked((uint)read.Version);
                    row.SourceToken = read.Token!; row.RootName = graph.Inspect(graph.SelectedRoot).Name;
                }
                diagrams.Add(row);
            }
            else if (isConventional)
                diagrams.Add(Diagram(file, P.DiscoveredDiagramStatus.DdsInvalid, "invalid_recursive_block_graph_xml",
                    "The conventional diagram path holds another kind of XML document.", true));
        }
        result.Diagrams.Add(diagrams.OrderBy(d => d.Path, StringComparer.Ordinal));
        return new P.RecursiveFileResult { Success = true, Discovery = result };

        static P.DiscoveredDiagramData Diagram(string path, P.DiscoveredDiagramStatus status, string code, string message, bool conventionalPath) =>
            new() { Path = path, Status = status, ErrorCode = code, ErrorMessage = message, Conventional = conventionalPath };
    }

    private static bool IsRecursive(XName? name) => name is { LocalName: "recursive-block-graph" }
        && name.NamespaceName.StartsWith("urn:kicad:automation:recursive-block-graph:", StringComparison.Ordinal);

    /// <summary>The nearest ancestor-or-self of the project directory holding hardware.xml, at most
    /// eight levels up and never past the first directory holding .git; the project directory otherwise.</summary>
    private static string RepositoryRoot(string directory)
    {
        string? current = directory;
        for (int level = 0; current is not null && level <= RepositoryWalkLimit; ++level, current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Refused("invalid_discovery_request", "The project path runs through a filesystem link; open the project from its real location.");
            if (File.Exists(Path.Combine(current, "hardware.xml"))) return Path.TrimEndingDirectorySeparator(current);
            if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git"))) break;
        }
        return directory;
    }

    // ---- Shared helpers --------------------------------------------------------------------------

    private readonly record struct RecursiveRead(RecursiveBlockGraph? Graph, int Version, string? Token,
        P.DiscoveredDiagramStatus Status, string? Code, string? Message);

    /// <summary>Loads any recursive diagram file without assuming its identity. Never writes.</summary>
    private static async Task<RecursiveRead> TryReadRecursiveAsync(string path, CancellationToken token)
    {
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(null, 0, null, P.DiscoveredDiagramStatus.DdsUnreadable, "diagram_file_unreadable", error.Message); }
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        try
        {
            var (graph, version) = RecursiveBlockGraphXml.ReadVersioned(Utf8.GetString(bytes));
            return new(graph, version, hash, RecursiveEditorFiles.Writable(path) ? P.DiscoveredDiagramStatus.DdsReady : P.DiscoveredDiagramStatus.DdsReadOnly, null, null);
        }
        catch (AutomationException error)
        {
            return new(null, 0, hash, error.Code == "diagram_schema_too_new" ? P.DiscoveredDiagramStatus.DdsTooNew : P.DiscoveredDiagramStatus.DdsInvalid,
                error.Code, error.Message);
        }
        catch (DecoderFallbackException error)
        { return new(null, 0, hash, P.DiscoveredDiagramStatus.DdsInvalid, "invalid_recursive_block_encoding", error.Message); }
    }

    /// <summary>Top-level regular XML files of one directory in ordinal order, without dot-files,
    /// hardware.xml or links.</summary>
    private static IEnumerable<string> TopLevelXml(string directory) => Directory.EnumerateFiles(directory, "*.xml", SearchOption.TopDirectoryOnly)
        .Where(f => Path.GetFileName(f) is var name && !name.StartsWith('.') && name != "hardware.xml" && !IsLink(f))
        .Order(StringComparer.Ordinal);

    /// <summary>True for a filesystem link, including one whose target is missing.</summary>
    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>The name of a document's root element, read as a stream with DTDs prohibited.</summary>
    private static XName? Sniff(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            IgnoreComments = true, IgnoreProcessingInstructions = true, IgnoreWhitespace = true });
        return reader.MoveToContent() == XmlNodeType.Element ? XName.Get(reader.LocalName, reader.NamespaceURI) : null;
    }

    /// <summary>The repository root and the absolute target of a create: an .xml file inside the
    /// repository whose folder exists, reached without filesystem links, and not a folder itself.</summary>
    private static (string Root, string Target) Target(P.RecursiveFileRequest request)
    {
        string root = request.RepositoryRoot, path = request.SourcePath;
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root) || !Path.IsPathFullyQualified(path)
            || !path.EndsWith(".xml", StringComparison.Ordinal))
            throw PathRefused();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string target = Path.GetFullPath(path);
        if (!Inside(root, target) || !Directory.Exists(Path.GetDirectoryName(target)))
            throw PathRefused();
        try { target = DiagramRequirementHistoryFiles.Contained(root, target, requireFile: false); }
        catch (AutomationException) { throw PathRefused(); }
        return (root, target);
    }

    private static AutomationException PathRefused() => Refused("invalid_diagram_path",
        "Choose an absolute .xml path for the new diagram inside the repository, in an existing folder, without filesystem links. Nothing was written.");

    private static bool Inside(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static AutomationException Refused(string code, string message) => new(code, message);
}
