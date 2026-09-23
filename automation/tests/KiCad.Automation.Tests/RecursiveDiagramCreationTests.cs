using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

/// <summary>Creating and discovering diagrams through the production helper process (<c>kicad-mcp --diagram-file</c>,
/// contract rbg-v2 section 7): the path the project manager uses, with no MCP session. Every refusal is checked to leave
/// all files byte-identical and no staged file behind. The MCP tools over the same helper actions are proven end to end
/// in NativeRecursiveEditorJourney (VerifyDiagramCreationOverMcp).</summary>
[TestClass]
public sealed class RecursiveDiagramCreationTests
{
    private static P.DiagramRevisionOriginData Origin(P.DiagramActorKind kind, Guid operation, string actor = "KiCad project manager")
    {
        var origin = new P.DiagramRevisionOriginData { Kind = kind, Actor = actor, Summary = "Project manager",
            RecordedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };
        origin.InputIds.Add(operation.ToString("D"));
        return origin;
    }

    private static P.RecursiveFileRequest Create(string root, string path, Guid operation, string rootName = "fixture") => new()
    {
        SchemaVersion = RecursiveBlockCodec.SchemaVersion, Action = P.RecursiveFileAction.RfaCreateDiagram, RepositoryRoot = root, SourcePath = path,
        Create = new() { OperationId = operation.ToString("D"), RootName = rootName, ImplementationName = "Initial",
            Fields = new(), Origin = Origin(P.DiagramActorKind.DakEditor, operation) }
    };

    private static P.RecursiveFileRequest Discover(string project) => new()
    { SchemaVersion = RecursiveBlockCodec.SchemaVersion, Action = P.RecursiveFileAction.RfaDiscoverDiagrams, Discover = new() { ProjectFile = project } };

    private static Task<P.RecursiveFileResult> Invoke(P.RecursiveFileRequest request) => RecursiveEditorFileCommandTests.Invoke(request);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Refused(P.RecursiveFileResult result, string code, string name)
    {
        Assert.IsFalse(result.Success, name); Assert.AreEqual(code, result.ErrorCode, name + ": " + result.ErrorMessage);
        Assert.IsNull(result.Document, name); Assert.IsFalse(result.Created, name);
    }

    /// <summary>Every regular file below the root with its bytes; filesystem links are checked separately.</summary>
    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(f => new FileInfo(f).LinkTarget is null).ToDictionary(f => f, File.ReadAllBytes);

    private static void Unchanged(Dictionary<string, byte[]> before, string root, string name)
    {
        var after = Snapshot(root);
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray(), name + ": no file may appear or disappear");
        foreach (var (path, bytes) in before) CollectionAssert.AreEqual(bytes, after[path], name + ": " + path);
    }

    [TestMethod]
    public async Task ProjectManagerCreatesACaptionOnlyDiagramOnceAndNeverOverwrites()
    {
        // Read-only folders and files are modelled with Unix file modes; this evidence is Linux evidence.
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Diagram file permission checks run on Linux."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-diagram-create-").FullName;
        string locked = Path.Combine(root, "locked");
        try
        {
            string project = Path.Combine(root, "board.kicad_pro");
            await File.WriteAllTextAsync(project, "{}\n");
            var empty = await Invoke(Discover(project));
            Assert.IsTrue(empty.Success, empty.ErrorMessage);
            string conventional = Path.Combine(root, "board.system-diagram.xml");
            Assert.AreEqual(root, empty.Discovery.RepositoryRoot); Assert.AreEqual(project, empty.Discovery.ProjectFile);
            Assert.AreEqual(conventional, empty.Discovery.SuggestedNewPath); Assert.IsFalse(empty.Discovery.SuggestedPathExists);
            Assert.IsEmpty(empty.Discovery.Diagrams); Assert.IsEmpty(empty.Discovery.FlatDiagrams); Assert.IsFalse(empty.Discovery.Truncated);

            Guid operation = Guid.NewGuid();
            var created = await Invoke(Create(root, conventional, operation));
            Assert.IsTrue(created.Success, created.ErrorMessage); Assert.IsTrue(created.Created);
            byte[] bytes = await File.ReadAllBytesAsync(conventional);
            Assert.AreEqual(Sha(bytes), created.SourceToken);
            Assert.AreEqual(2U, created.Document.SchemaVersion); Assert.AreEqual(2U, created.Document.StoredSchemaVersion);
            Assert.IsTrue(created.Document.SourceWritable); Assert.AreEqual(0U, created.UpgradedFromSchemaVersion);
            var (graph, version) = RecursiveBlockGraphXml.ReadVersioned(Encoding.UTF8.GetString(bytes));
            Assert.AreEqual(2, version, "R4: a new diagram is stored in format 2.");
            Assert.AreEqual(DiagramIdentity.Derive(operation, "document"), graph.DocumentId);
            Assert.AreEqual(created.Document.DocumentId, graph.DocumentId.ToString("D"));
            Assert.AreEqual(new BlockSelection(DiagramIdentity.Derive(operation, "root-block"), DiagramIdentity.Derive(operation, "state:root"),
                DiagramIdentity.Derive(operation, "revision:root")), graph.SelectedRoot);
            // The root is only its caption (owner decision n98a3f3c41084f0ed).
            var rootRevision = graph.Inspect(graph.SelectedRoot);
            Assert.AreEqual("fixture", rootRevision.Name); Assert.IsNull(rootRevision.ParentRevisionId); Assert.IsEmpty(rootRevision.Children);
            Assert.IsTrue(rootRevision.LocalDiagram.SameContents(BlockLocalDiagram.Empty));
            Assert.IsNull(rootRevision.Definition); Assert.IsNull(rootRevision.ComponentBindings); Assert.IsNull(rootRevision.PhysicalAllocation);
            Assert.AreEqual(DiagramRequirements.Empty, graph.Requirements(graph.SelectedRoot).Requirements);
            Assert.AreEqual(DiagramIdentity.Derive(operation, "requirements:root"), rootRevision.RequirementRevisionId);
            Assert.AreEqual("Initial", graph.States.Single().Name); Assert.AreEqual(RequirementRevisionActor.Editor, rootRevision.Origin.ActorKind);
            Assert.IsEmpty(Directory.EnumerateFiles(root, "*.initial-*"), "The staged file is moved into place, never left behind.");
            // The created diagram opens through the ordinary read.
            var read = await Invoke(new P.RecursiveFileRequest { SchemaVersion = 2, RepositoryRoot = root, SourcePath = conventional,
                DocumentId = created.Document.DocumentId });
            Assert.IsTrue(read.Success, read.ErrorMessage); Assert.AreEqual(created.SourceToken, read.SourceToken);

            var before = Snapshot(root);
            // Repeating the same operation observes its own diagram and writes nothing.
            var repeated = await Invoke(Create(root, conventional, operation));
            Assert.IsTrue(repeated.Success, repeated.ErrorMessage); Assert.IsFalse(repeated.Created);
            Assert.AreEqual(created.SourceToken, repeated.SourceToken); Assert.AreEqual(created.Document.DocumentId, repeated.Document.DocumentId);
            Unchanged(before, root, "repeated create");
            // Any other operation is refused; nothing is overwritten, and the refusal names the diagram already there.
            var other = await Invoke(Create(root, conventional, Guid.NewGuid()));
            Refused(other, "diagram_file_exists", "another operation");
            Assert.AreEqual((created.Document.DocumentId, conventional), (other.ErrorDetails.Single().ObjectId, other.ErrorDetails.Single().Message));
            Unchanged(before, root, "another operation");
            var listed = await Invoke(Discover(project));
            var diagram = listed.Discovery.Diagrams.Single();
            Assert.AreEqual((conventional, P.DiscoveredDiagramStatus.DdsReady, true, created.Document.DocumentId, 2U, "fixture", created.SourceToken),
                (diagram.Path, diagram.Status, diagram.Conventional, diagram.DocumentId, diagram.StoredSchemaVersion, diagram.RootName, diagram.SourceToken));
            Assert.IsTrue(listed.Discovery.SuggestedPathExists); Assert.IsFalse(diagram.HasMigratedFromStructureId);

            // Invalid targets and request shapes are refused before anything is written.
            Directory.CreateDirectory(locked);
            string folder = Path.Combine(root, "folder.xml"); Directory.CreateDirectory(folder);
            string linkTarget = Path.Combine(root, "elsewhere.xml"), link = Path.Combine(root, "linked.system-diagram.xml");
            File.CreateSymbolicLink(link, linkTarget);
            string present = Path.Combine(root, "present.xml"), presentLink = Path.Combine(root, "present-link.system-diagram.xml");
            await File.WriteAllTextAsync(present, "<unrelated/>\n"); File.CreateSymbolicLink(presentLink, present);
            var probes = new List<(string Name, P.RecursiveFileRequest Request, string Code)>
            {
                ("relative path", Create(root, "board2.system-diagram.xml", Guid.NewGuid()), "invalid_diagram_path"),
                ("not xml", Create(root, Path.Combine(root, "board2.system-diagram.json"), Guid.NewGuid()), "invalid_diagram_path"),
                ("outside the repository", Create(root, Path.Combine(Path.GetDirectoryName(root)!, "escaped.xml"), Guid.NewGuid()), "invalid_diagram_path"),
                ("escaping through ..", Create(root, root + "/../escaped.xml", Guid.NewGuid()), "invalid_diagram_path"),
                ("missing folder", Create(root, Path.Combine(root, "missing", "diagram.xml"), Guid.NewGuid()), "invalid_diagram_path"),
                ("a folder", Create(root, folder, Guid.NewGuid()), "invalid_diagram_path"),
                ("a dangling filesystem link", Create(root, link, Guid.NewGuid()), "invalid_diagram_path"),
                ("a filesystem link to a file", Create(root, presentLink, Guid.NewGuid()), "invalid_diagram_path"),
                ("relative repository", Create("relative", Path.Combine(root, "relative.xml"), Guid.NewGuid()), "invalid_diagram_path"),
                ("blank caption", Create(root, Path.Combine(root, "blank.xml"), Guid.NewGuid(), " "), "invalid_diagram_create_request"),
            };
            before = Snapshot(root);
            var identified = Create(root, Path.Combine(root, "identified.xml"), Guid.NewGuid()); identified.DocumentId = Guid.NewGuid().ToString("D");
            probes.Add(("document identity", identified, "ambiguous_diagram_file_request"));
            var tokened = Create(root, Path.Combine(root, "tokened.xml"), Guid.NewGuid()); tokened.ExpectedSourceToken = new string('a', 64);
            probes.Add(("file token", tokened, "ambiguous_diagram_file_request"));
            var foreignOrigin = Create(root, Path.Combine(root, "foreign.xml"), Guid.NewGuid()); foreignOrigin.Create.Origin.InputIds.Clear();
            probes.Add(("origin without the operation", foreignOrigin, "invalid_diagram_create_request"));
            var imported = Create(root, Path.Combine(root, "imported.xml"), Guid.NewGuid()); imported.Create.Origin.Kind = P.DiagramActorKind.DakImport;
            probes.Add(("import origin", imported, "invalid_diagram_create_request"));
            var unnamed = Create(root, Path.Combine(root, "unnamed.xml"), Guid.NewGuid()); unnamed.Create.ImplementationName = "";
            probes.Add(("no implementation name", unnamed, "invalid_diagram_create_request"));
            var noncanonical = Create(root, Path.Combine(root, "noncanonical.xml"), Guid.NewGuid());
            noncanonical.Create.OperationId = noncanonical.Create.OperationId.ToUpperInvariant();
            probes.Add(("noncanonical operation", noncanonical, "invalid_diagram_create_request"));
            // A new diagram starts as a caption only: requirement text is added once the diagram exists.
            foreach (var field in new[] { "general", "schematic", "routing" })
            {
                var detailed = Create(root, Path.Combine(root, field + ".xml"), Guid.NewGuid());
                detailed.Create.Fields = field switch { "general" => new() { General = "Supply the CPU." },
                    "schematic" => new() { Schematic = "One sheet." }, _ => new() { Routing = "Short loops." } };
                probes.Add((field + " text at creation", detailed, "invalid_diagram_create_request"));
            }
            var mixed = Create(root, Path.Combine(root, "mixed.xml"), Guid.NewGuid()); mixed.Discover = new() { ProjectFile = project };
            probes.Add(("two payloads", mixed, "ambiguous_diagram_file_request"));
            var schemaOne = Create(root, Path.Combine(root, "old.xml"), Guid.NewGuid()); schemaOne.SchemaVersion = 1;
            probes.Add(("schema 1 request", schemaOne, "unsupported_diagram_file_request"));
            foreach (var (name, request, code) in probes) Refused(await Invoke(request), code, name);
            Unchanged(before, root, "refused requests");
            Assert.IsFalse(File.Exists(linkTarget), "Nothing was created through the link.");

            // A folder that cannot be written: honest refusal, nothing created, nothing staged.
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            if (IsWritable(locked)) Assert.Inconclusive("The test account can write to a read-only folder (for example as root), so the refusal cannot be observed.");
            Refused(await Invoke(Create(root, Path.Combine(locked, "board.system-diagram.xml"), Guid.NewGuid())), "diagram_file_read_only", "read-only folder");
            Unchanged(before, root, "read-only folder");
            // A read-only existing diagram is never replaced; discovery reports it and reads say Save must stay disabled.
            File.SetUnixFileMode(conventional, UnixFileMode.UserRead);
            Refused(await Invoke(Create(root, conventional, Guid.NewGuid())), "diagram_file_exists", "read-only existing diagram");
            var retried = await Invoke(Create(root, conventional, operation));
            Assert.IsTrue(retried.Success, retried.ErrorMessage); Assert.IsFalse(retried.Created); Assert.IsFalse(retried.Document.SourceWritable);
            var readOnly = await Invoke(Discover(project));
            Assert.AreEqual(P.DiscoveredDiagramStatus.DdsReadOnly, readOnly.Discovery.Diagrams.Single().Status);
            var readOnlyRead = await Invoke(new P.RecursiveFileRequest { SchemaVersion = 2, RepositoryRoot = root, SourcePath = conventional,
                DocumentId = created.Document.DocumentId });
            Assert.IsTrue(readOnlyRead.Success, readOnlyRead.ErrorMessage); Assert.IsFalse(readOnlyRead.Document.SourceWritable);
            Unchanged(before, root, "read-only existing diagram");
            File.SetUnixFileMode(conventional, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (Directory.Exists(locked)) File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task DiscoveryListsRecursiveDiagramsByContentAndIgnoresLegacyFlatDiagrams()
    {
        string root = Directory.CreateTempSubdirectory("kicad-diagram-discover-").FullName;
        try
        {
            // The frozen PSU-CPU repository: hardware.xml, the version 1 block graph and the legacy flat structure.
            await PsuCpuFixture.WriteRepositoryFilesAsync(root, CancellationToken.None);
            string board = Path.Combine(root, "board"); Directory.CreateDirectory(board);
            string project = Path.Combine(board, "fixture.kicad_pro"); await File.WriteAllTextAsync(project, "{}\n");
            foreach (string name in new[] { "system.blocks.xml", "flat-structure.engineering.xml" })
                File.Copy(Path.Combine(root, name), Path.Combine(board, name));
            await File.WriteAllBytesAsync(Path.Combine(board, "design.engineering.xml"), PsuCpuFixture.ReadBytes("design.engineering.xml"));
            await File.WriteAllTextAsync(Path.Combine(board, "notes.xml"), "<unrelated/>\n");
            await File.WriteAllTextAsync(Path.Combine(board, ".hidden.xml"), "<unrelated/>\n");
            var before = Snapshot(root);
            var found = await Invoke(Discover(project));
            Assert.IsTrue(found.Success, found.ErrorMessage);
            // The repository root is the nearest folder holding hardware.xml; creating there keeps the diagram inside it.
            Assert.AreEqual((root, project, Path.Combine(board, "fixture.system-diagram.xml"), false),
                (found.Discovery.RepositoryRoot, found.Discovery.ProjectFile, found.Discovery.SuggestedNewPath, found.Discovery.SuggestedPathExists));
            // Only the recursive diagram is a diagram: the legacy flat structure, other models and unrelated XML are not listed or converted.
            var blocks = found.Discovery.Diagrams.Single();
            Assert.AreEqual((Path.Combine(board, "system.blocks.xml"), P.DiscoveredDiagramStatus.DdsReady, 1U, false, "System", PsuCpuIds.Id(0x10, 1).ToString("D")),
                (blocks.Path, blocks.Status, blocks.StoredSchemaVersion, blocks.Conventional, blocks.RootName, blocks.DocumentId));
            Assert.AreEqual(Sha(await File.ReadAllBytesAsync(blocks.Path)), blocks.SourceToken);
            Assert.IsEmpty(found.Discovery.FlatDiagrams); Assert.IsFalse(found.Discovery.HasManifestPath);
            Unchanged(before, root, "discovery");
            // Creating at the suggested path inside the repository root; the version 1 graph and the flat file stay as they were.
            Guid operation = Guid.NewGuid();
            var created = await Invoke(Create(found.Discovery.RepositoryRoot, found.Discovery.SuggestedNewPath, operation, "Fixture board"));
            Assert.IsTrue(created.Success, created.ErrorMessage); Assert.IsTrue(created.Created);
            foreach (var (path, bytes) in before) CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path), path);
            var listed = (await Invoke(Discover(project))).Discovery;
            CollectionAssert.AreEqual(new[] { Path.Combine(board, "fixture.system-diagram.xml"), Path.Combine(board, "system.blocks.xml") },
                listed.Diagrams.Select(d => d.Path).ToArray(), "Rows are in path order.");
            Assert.IsTrue(listed.Diagrams[0].Conventional); Assert.AreEqual("Fixture board", listed.Diagrams[0].RootName);

            // A newer or broken file at the conventional path is reported and never replaced.
            string newer = Path.Combine(root, "newer"); Directory.CreateDirectory(newer);
            string newerProject = Path.Combine(newer, "newer.kicad_pro"); await File.WriteAllTextAsync(newerProject, "{}\n");
            string newerDiagram = Path.Combine(newer, "newer.system-diagram.xml");
            await File.WriteAllTextAsync(newerDiagram, "<recursive-block-graph xmlns=\"urn:kicad:automation:recursive-block-graph:3\" version=\"3\"/>\n");
            var tooNew = await Invoke(Discover(newerProject));
            Assert.IsTrue(tooNew.Success, tooNew.ErrorMessage);
            var tooNewRow = tooNew.Discovery.Diagrams.Single();
            Assert.AreEqual((P.DiscoveredDiagramStatus.DdsTooNew, "diagram_schema_too_new", true), (tooNewRow.Status, tooNewRow.ErrorCode, tooNewRow.Conventional));
            var newerBefore = Snapshot(newer);
            Refused(await Invoke(Create(root, newerDiagram, Guid.NewGuid())), "diagram_file_exists", "too new at the conventional path");
            Unchanged(newerBefore, newer, "too new at the conventional path");
            await File.WriteAllTextAsync(newerDiagram, "<recursive-block-graph");
            Assert.AreEqual(P.DiscoveredDiagramStatus.DdsInvalid, (await Invoke(Discover(newerProject))).Discovery.Diagrams.Single().Status);
            await File.WriteAllTextAsync(newerDiagram, "<unrelated/>\n");
            var other = (await Invoke(Discover(newerProject))).Discovery.Diagrams.Single();
            Assert.AreEqual((P.DiscoveredDiagramStatus.DdsInvalid, "invalid_recursive_block_graph_xml", true), (other.Status, other.ErrorCode, other.Conventional));
            newerBefore = Snapshot(newer);
            Refused(await Invoke(Create(root, newerDiagram, Guid.NewGuid())), "diagram_file_exists", "unrelated XML at the conventional path");
            Unchanged(newerBefore, newer, "unrelated XML at the conventional path");
            // A filesystem link at the conventional path is reported, never followed, created through or replaced.
            File.Delete(newerDiagram); File.CreateSymbolicLink(newerDiagram, Path.Combine(board, "system.blocks.xml"));
            var linked = (await Invoke(Discover(newerProject))).Discovery;
            var linkedRow = linked.Diagrams.Single();
            Assert.AreEqual((P.DiscoveredDiagramStatus.DdsInvalid, "invalid_diagram_path", true, ""),
                (linkedRow.Status, linkedRow.ErrorCode, linkedRow.Conventional, linkedRow.DocumentId), "The linked file's content is not read.");
            Assert.IsTrue(linked.SuggestedPathExists);
            newerBefore = Snapshot(root);
            Refused(await Invoke(Create(root, newerDiagram, Guid.NewGuid())), "invalid_diagram_path", "a link at the conventional path");
            Unchanged(newerBefore, root, "a link at the conventional path");

            Refused(await Invoke(Discover(Path.Combine(root, "missing.kicad_pro"))), "invalid_discovery_request", "missing project");
            Refused(await Invoke(Discover("board.kicad_pro")), "invalid_discovery_request", "relative project");
            Refused(await Invoke(Discover(Path.Combine(root, "hardware.xml"))), "invalid_discovery_request", "not a project file");
            var located = Discover(project); located.RepositoryRoot = root;
            Refused(await Invoke(located), "ambiguous_diagram_file_request", "discovery with a repository root");
            var payload = Discover(project); payload.Create = new();
            Refused(await Invoke(payload), "ambiguous_diagram_file_request", "discovery with a create payload");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Unit test by design: identity derivation is an isolated algorithm with fixed contract vectors (rbg-v2 G7),
    /// which no file-level test can check beyond "the document id is derived from the operation".</summary>
    [TestMethod]
    public void DerivedIdentitiesFollowTheContractVectors()
    {
        var operation = Guid.ParseExact("6f1c2a4e-0b7d-4c55-9a51-3f0e8d2b7c10", "D");
        foreach (var (name, expected) in new[] { ("document", "74ec87fc-edcd-5b78-80e2-e903035a5282"), ("root-block", "2f5add7e-1b7c-52bc-95da-e7870273b2a2"),
            ("state:root", "cc2d2fdb-fc5b-593f-8ea6-62a9e18243fa"), ("revision:root", "54feb920-3bbd-5034-a144-6b7beaeb0d03"),
            ("requirements:root", "a0fcd4c6-1f62-54f9-a0b9-ae5d77eb35ad") })
            Assert.AreEqual(expected, DiagramIdentity.Derive(operation, name).ToString("D"), name);
        Assert.AreNotEqual(DiagramIdentity.Derive(operation, "document"), DiagramIdentity.Derive(Guid.NewGuid(), "document"));
        Assert.AreEqual("invalid_diagram_identity", Assert.ThrowsExactly<AutomationException>(() => DiagramIdentity.Derive(Guid.Empty, "document")).Code);
    }

    internal static bool IsWritable(string directory)
    {
        try { using var probe = File.Create(Path.Combine(directory, ".probe"), 1, FileOptions.DeleteOnClose); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
