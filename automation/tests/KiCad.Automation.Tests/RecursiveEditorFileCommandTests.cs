using System.Diagnostics;
using Google.Protobuf;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveEditorFileCommandTests
{
    [TestMethod]
    public async Task OlderHistoryPagesKeepTheirExactContextAndRejectChangedFiles()
    {
        string root = Directory.CreateTempSubdirectory("kicad-history-pages-").FullName;
        try
        {
            var initial = RecursiveBlockFixture.Create().Graph;
            var graph = RecursiveBlockFixture.RefineRoot(initial, DiagramRequirementField.General, 205);
            string path = Path.Combine(root, "design.xml"), xml = RecursiveBlockGraphXml.Write(graph);
            await File.WriteAllTextAsync(path, xml);
            var request = new P.RecursiveFileRequest { SchemaVersion = 1, RepositoryRoot = root,
                SourcePath = path, DocumentId = graph.DocumentId.ToString("D") };
            var read = await Invoke(request); Assert.IsTrue(read.Success);
            request.ExpectedSourceToken = read.SourceToken; request.Action = P.RecursiveFileAction.RfaBlockFieldHistory;
            request.Block = new() { BlockId = graph.SelectedRoot.BlockId.ToString("D"), StateId = graph.SelectedRoot.StateId.ToString("D"), RevisionId = graph.SelectedRoot.RevisionId.ToString("D") };
            request.Field = P.RequirementFieldKind.RfkGeneral; request.Limit = 200;
            var first = await Invoke(request); Assert.IsTrue(first.Success); Assert.HasCount(200, first.History.Entries);
            request.Offset = 200;
            var older = await Invoke(request); Assert.IsTrue(older.Success); Assert.HasCount(6, older.History.Entries);
            Assert.AreEqual(first.History.ContextRevisionId, older.History.ContextRevisionId);
            Assert.AreEqual(first.History.SavedText, older.History.SavedText);
            Assert.AreEqual(initial.Requirements(initial.SelectedRoot).RevisionId.ToString("D"), older.History.Entries[^1].RequirementRevisionId);
            var changed = RecursiveBlockFixture.RefineRoot(graph, DiagramRequirementField.Routing, 1);
            string changedXml = RecursiveBlockGraphXml.Write(changed); await File.WriteAllTextAsync(path, changedXml);
            var stale = await Invoke(request); Assert.IsFalse(stale.Success); Assert.AreEqual("recursive_block_file_changed", stale.ErrorCode);
            Assert.IsNull(stale.History); Assert.AreEqual(changedXml, await File.ReadAllTextAsync(path));
            var currentRead = request.Clone(); currentRead.Action = P.RecursiveFileAction.RfaRead;
            currentRead.Block = null; currentRead.Field = P.RequirementFieldKind.RfkUnknown; currentRead.Offset = currentRead.Limit = 0; currentRead.ExpectedSourceToken = "";
            request.ExpectedSourceToken = (await Invoke(currentRead)).SourceToken;
            var recovered = await Invoke(request); Assert.IsTrue(recovered.Success);
            Assert.AreEqual(older.History, recovered.History, "A refreshed file still permits inspecting the exact old context, not today's head.");
            Assert.AreEqual(changedXml, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CompiledReadAndScopedHistoryCommandsPreserveTheSavedDesign()
    {
        string root = Directory.CreateTempSubdirectory("kicad-recursive-command-").FullName;
        try
        {
            var fixture = LinkedDiagramFixture.Create(); string path = Path.Combine(root, "design.xml");
            string xml = RecursiveBlockGraphXml.Write(fixture.Graph); await File.WriteAllTextAsync(path, xml);
            var request = new P.RecursiveFileRequest { SchemaVersion = 1, RepositoryRoot = root,
                SourcePath = path, DocumentId = fixture.Graph.DocumentId.ToString("D") };
            var read = await Invoke(request);
            Assert.IsTrue(read.Success); Assert.AreEqual(request.DocumentId, read.Document.DocumentId);
            Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(KiCad.Automation.Native.RecursiveBlockCodec.Decode(read.Document.Graph)));
            var cpu = fixture.Blocks["CPU"];
            request.Action = P.RecursiveFileAction.RfaBlockFieldHistory;
            request.Block = new() { BlockId = cpu.BlockId.ToString("D"), StateId = cpu.StateId.ToString("D"), RevisionId = cpu.RevisionId.ToString("D") };
            request.Field = P.RequirementFieldKind.RfkGeneral; request.Limit = 50; request.ExpectedSourceToken = read.SourceToken;
            var history = await Invoke(request);
            Assert.IsTrue(history.Success); Assert.IsNull(history.Document);
            Assert.AreEqual(cpu.BlockId.ToString("D"), history.History.OwnerId);
            Assert.AreEqual(fixture.Graph.Requirements(cpu).Requirements.General, history.History.SavedText);
            request.Action = P.RecursiveFileAction.RfaConnectionFieldHistory;
            var link = fixture.Links["CPU/Memory"];
            request.Connection = new() { ConnectionId = link.ConnectionId.ToString("D"), StateId = link.StateId.ToString("D"), RevisionId = link.RevisionId.ToString("D") };
            var connection = await Invoke(request);
            Assert.IsTrue(connection.Success); Assert.AreEqual(link.ConnectionId.ToString("D"), connection.History.OwnerId);
            var wrongScope = request.Clone(); var psu = fixture.Blocks["PSU"];
            wrongScope.Block = new() { BlockId = psu.BlockId.ToString("D"), StateId = psu.StateId.ToString("D"), RevisionId = psu.RevisionId.ToString("D") };
            Assert.IsFalse((await Invoke(wrongScope)).Success);
            request.ExpectedSourceToken = new string('0', 64);
            var stale = await Invoke(request);
            Assert.IsFalse(stale.Success); Assert.AreEqual("recursive_block_file_changed", stale.ErrorCode);
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CompiledSavePublishesOneSuccessorAndRejectsStaleRetriesWithoutLosingHistory()
    {
        string root = Directory.CreateTempSubdirectory("kicad-recursive-save-command-").FullName;
        try
        {
            var fixture = LinkedDiagramFixture.Create(); var graph = fixture.Graph;
            string path = Path.Combine(root, "design.xml"); await File.WriteAllTextAsync(path, RecursiveBlockGraphXml.Write(graph));
            var request = new P.RecursiveFileRequest { SchemaVersion = 1, RepositoryRoot = root, SourcePath = path, DocumentId = graph.DocumentId.ToString("D") };
            var read = await Invoke(request);
            var block = fixture.Blocks["PSU"]; var draft = graph.StartDraft(block);
            draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "A native editor requirement.") };
            var save = new P.SaveBlockDraftData
            {
                ExpectedRoot = read.Document.Graph.SelectedRoot.Clone(), Draft = KiCad.Automation.Native.RecursiveBlockCodec.Encode(draft),
                NewRevisionId = Guid.NewGuid().ToString("D"), NewRequirementRevisionId = Guid.NewGuid().ToString("D"),
                Origin = new() { Kind = P.DiagramActorKind.DakEditor, Actor = "Native editor",
                    RecordedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), Summary = "Edit requirements" }
            };
            save.BlockPath.Add(save.ExpectedRoot.Clone()); save.BlockPath.Add(save.Draft.Baseline.Clone());
            save.AncestorRevisionIds.Add(Guid.NewGuid().ToString("D"));
            request.Action = P.RecursiveFileAction.RfaSaveBlock; request.ExpectedSourceToken = read.SourceToken; request.Save = save;
            var saved = await Invoke(request);
            Assert.IsTrue(saved.Success); Assert.AreNotEqual(read.SourceToken, saved.SourceToken);
            var loaded = KiCad.Automation.Native.RecursiveBlockCodec.Decode(saved.Document.Graph);
            var psu = loaded.Inspect(loaded.SelectedRoot).Children[0];
            Assert.AreEqual("A native editor requirement.", loaded.Requirements(psu).Requirements.General);
            Assert.AreEqual(graph.Requirements(block).Requirements.General, loaded.Requirements(block).Requirements.General);
            Assert.AreEqual(RequirementRevisionActor.Editor, loaded.Inspect(psu).Origin.ActorKind);
            var stale = await Invoke(request);
            Assert.IsFalse(stale.Success); Assert.AreEqual("recursive_block_file_changed", stale.ErrorCode);
            var reread = request.Clone(); reread.Action = P.RecursiveFileAction.RfaRead; reread.Save = null; reread.ExpectedSourceToken = "";
            var after = await Invoke(reread);
            Assert.AreEqual(saved.SourceToken, after.SourceToken);
            Assert.AreEqual(loaded.Revisions.Length, KiCad.Automation.Native.RecursiveBlockCodec.Decode(after.Document.Graph).Revisions.Length);
            var unchanged = KiCad.Automation.Native.RecursiveBlockCodec.Encode(loaded.StartDraft(psu));
            request.ExpectedSourceToken = saved.SourceToken; request.Save.Draft = unchanged;
            request.Save.ExpectedRoot = saved.Document.Graph.SelectedRoot.Clone(); request.Save.BlockPath.Clear();
            request.Save.BlockPath.Add(request.Save.ExpectedRoot.Clone()); request.Save.BlockPath.Add(unchanged.Baseline.Clone());
            request.Save.NewRevisionId = Guid.NewGuid().ToString("D"); request.Save.NewRequirementRevisionId = Guid.NewGuid().ToString("D");
            request.Save.AncestorRevisionIds[0] = Guid.NewGuid().ToString("D");
            var noOp = await Invoke(request);
            Assert.IsTrue(noOp.Success); Assert.AreEqual(saved.SourceToken, noOp.SourceToken);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CompiledImplementationManagementCreatesRenamesRemovesAndRestoresWithoutDeletingHistory()
    {
        string root = Directory.CreateTempSubdirectory("kicad-implementation-management-").FullName;
        try
        {
            var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var cpu = f.Blocks["CPU"];
            string path = Path.Combine(root, "design.xml"); await File.WriteAllTextAsync(path, RecursiveBlockGraphXml.Write(graph));
            var request = new P.RecursiveFileRequest { SchemaVersion = 1, RepositoryRoot = root, SourcePath = path, DocumentId = graph.DocumentId.ToString("D") };
            var read = await Invoke(request);
            var source = new P.BlockSelectionData { BlockId = cpu.BlockId.ToString("D"), StateId = cpu.StateId.ToString("D"), RevisionId = cpu.RevisionId.ToString("D") };
            var management = new P.ManageImplementationData { Action = P.ImplementationActionKind.IakDuplicate,
                ExpectedRoot = read.Document.Graph.SelectedRoot.Clone(), Source = source, Name = "Serviceable alternative",
                NewStateId = Guid.NewGuid().ToString("D"), NewRevisionId = Guid.NewGuid().ToString("D"), NewRequirementRevisionId = Guid.NewGuid().ToString("D"),
                Origin = new() { Kind = P.DiagramActorKind.DakEditor, Actor = "Native editor", Summary = "Implementation management fixture",
                    RecordedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) } };
            request.Action = P.RecursiveFileAction.RfaManageImplementation; request.Implementation = management; request.ExpectedSourceToken = read.SourceToken;
            var created = await Invoke(request); Assert.IsTrue(created.Success, created.ErrorMessage);
            Assert.AreEqual(management.NewStateId, created.ImplementationId);
            var loaded = RecursiveBlockCodec.Decode(created.Document.Graph);
            Assert.AreEqual(graph.SelectedRoot, loaded.SelectedRoot); Assert.AreEqual(graph.States.Length + 1, loaded.States.Length);
            Assert.AreEqual(cpu, loaded.States.Single(s => s.Id.ToString("D") == created.ImplementationId).ForkedFrom);
            var stale = await Invoke(request); Assert.IsFalse(stale.Success); Assert.AreEqual("recursive_block_file_changed", stale.ErrorCode);
            management.Source = new() { BlockId = cpu.BlockId.ToString("D"), StateId = created.ImplementationId, RevisionId = management.NewRevisionId };
            management.NewStateId = ""; management.NewRevisionId = ""; management.NewRequirementRevisionId = "";
            management.Action = P.ImplementationActionKind.IakRename; management.Name = "Thermal alternative"; management.ChangeId = Guid.NewGuid().ToString("D");
            request.ExpectedSourceToken = created.SourceToken;
            var renamed = await Invoke(request); Assert.IsTrue(renamed.Success, renamed.ErrorMessage);
            Assert.AreEqual("Thermal alternative", renamed.Document.Graph.States.Single(s => s.Id == created.ImplementationId).Name);
            management.Action = P.ImplementationActionKind.IakArchive; management.Name = ""; management.ChangeId = Guid.NewGuid().ToString("D"); request.ExpectedSourceToken = renamed.SourceToken;
            var removed = await Invoke(request); Assert.IsTrue(removed.Success, removed.ErrorMessage);
            Assert.IsTrue(removed.Document.Graph.States.Single(s => s.Id == created.ImplementationId).Archived);
            Assert.AreEqual(renamed.Document.Graph.Revisions.Count, removed.Document.Graph.Revisions.Count);
            management.Action = P.ImplementationActionKind.IakRestore; management.ChangeId = Guid.NewGuid().ToString("D"); request.ExpectedSourceToken = removed.SourceToken;
            var restored = await Invoke(request); Assert.IsTrue(restored.Success, restored.ErrorMessage);
            Assert.IsFalse(restored.Document.Graph.States.Single(s => s.Id == created.ImplementationId).Archived);
            Assert.AreEqual(3, restored.Document.Graph.ImplementationChanges.Count);
            request.ExpectedSourceToken = restored.SourceToken; management.Action = P.ImplementationActionKind.IakArchive;
            management.Source = source; management.ChangeId = Guid.NewGuid().ToString("D");
            var activeRemoval = await Invoke(request); Assert.IsFalse(activeRemoval.Success); Assert.AreEqual("implementation_is_selected", activeRemoval.ErrorCode);
            Assert.AreEqual(RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(restored.Document.Graph)), await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CompiledImplementationSaveKeepsPreviewInactiveUntilGuardedPublication()
    {
        string root = Directory.CreateTempSubdirectory("kicad-implementation-command-").FullName;
        try
        {
            var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var cpu = f.Blocks["CPU"];
            var state = graph.States.Single(s => s.BlockId == cpu.BlockId && s.Id != cpu.StateId);
            var selection = new BlockSelection(cpu.BlockId, state.Id, state.HeadRevisionId);
            string path = Path.Combine(root, "design.xml"); string original = RecursiveBlockGraphXml.Write(graph);
            await File.WriteAllTextAsync(path, original);
            var request = new P.RecursiveFileRequest { SchemaVersion = 1, RepositoryRoot = root, SourcePath = path, DocumentId = graph.DocumentId.ToString("D") };
            var read = await Invoke(request); var draft = graph.StartDraft(selection);
            draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "A different implementation choice") };
            Assert.AreEqual(original, await File.ReadAllTextAsync(path));
            request.Action = P.RecursiveFileAction.RfaSaveImplementation; request.ExpectedSourceToken = read.SourceToken;
            request.Save = new() { ExpectedRoot = read.Document.Graph.SelectedRoot.Clone(), Draft = KiCad.Automation.Native.RecursiveBlockCodec.Encode(draft),
                NewRevisionId = Guid.NewGuid().ToString("D"), NewRequirementRevisionId = Guid.NewGuid().ToString("D"), Origin = new()
                    { Kind = P.DiagramActorKind.DakEditor, Actor = "Native editor", Summary = "Choose implementation", RecordedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) } };
            request.Save.BlockPath.Add(request.Save.ExpectedRoot.Clone());
            request.Save.BlockPath.Add(new P.BlockSelectionData { BlockId = cpu.BlockId.ToString("D"), StateId = cpu.StateId.ToString("D"), RevisionId = cpu.RevisionId.ToString("D") });
            request.Save.AncestorRevisionIds.Add(Guid.NewGuid().ToString("D"));
            var saved = await Invoke(request); Assert.IsTrue(saved.Success, saved.ErrorMessage);
            var loaded = KiCad.Automation.Native.RecursiveBlockCodec.Decode(saved.Document.Graph);
            var chosen = loaded.Inspect(loaded.SelectedRoot).Children[1]; Assert.AreEqual(state.Id, chosen.StateId);
            Assert.AreEqual("A different implementation choice", loaded.Requirements(chosen).Requirements.General);
            Assert.AreEqual(cpu, loaded.Inspect(graph.SelectedRoot).Children[1]);
            var stale = await Invoke(request); Assert.IsFalse(stale.Success); Assert.AreEqual("recursive_block_file_changed", stale.ErrorCode);
            Assert.AreEqual(RecursiveBlockGraphXml.Write(loaded), await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CompiledConnectionSavePreservesTheOldInterfaceAndContainingDiagram()
    {
        string root = Directory.CreateTempSubdirectory("kicad-connection-command-").FullName;
        try
        {
            var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var cpu = f.Blocks["CPU"]; var link = f.Links["CPU/Memory"];
            string path = Path.Combine(root, "design.xml"); await File.WriteAllTextAsync(path, RecursiveBlockGraphXml.Write(graph));
            var request = new P.RecursiveFileRequest { SchemaVersion = 1, RepositoryRoot = root, SourcePath = path, DocumentId = graph.DocumentId.ToString("D") };
            var read = await Invoke(request);
            var draft = graph.Connections(cpu.BlockId).StartDraft(link);
            draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.Routing, "Keep memory routes away from noisy power.") };
            var data = KiCad.Automation.Native.RecursiveBlockCodec.Encode(draft);
            var save = new P.SaveConnectionDraftData { ExpectedRoot = read.Document.Graph.SelectedRoot.Clone(), Draft = data,
                NewConnectionRevisionId = Guid.NewGuid().ToString("D"), NewRequirementRevisionId = Guid.NewGuid().ToString("D"),
                NewBlockRevisionId = Guid.NewGuid().ToString("D"), NewBlockRequirementRevisionId = Guid.NewGuid().ToString("D"),
                Origin = new() { Kind = P.DiagramActorKind.DakEditor, Actor = "Native editor", Summary = "Edit connection requirements",
                    RecordedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) } };
            save.BlockPath.Add(save.ExpectedRoot.Clone());
            save.BlockPath.Add(new P.BlockSelectionData { BlockId = cpu.BlockId.ToString("D"), StateId = cpu.StateId.ToString("D"), RevisionId = cpu.RevisionId.ToString("D") });
            save.ConnectionPath.Add(data.Baseline.Clone()); save.BlockAncestorRevisionIds.Add(Guid.NewGuid().ToString("D"));
            request.Action = P.RecursiveFileAction.RfaSaveConnection; request.SaveConnection = save; request.ExpectedSourceToken = read.SourceToken;
            var saved = await Invoke(request); Assert.IsTrue(saved.Success, saved.ErrorMessage);
            var updated = KiCad.Automation.Native.RecursiveBlockCodec.Decode(saved.Document.Graph);
            var newCpu = updated.Inspect(updated.SelectedRoot).Children[1]; var newLink = updated.Inspect(newCpu).LocalDiagram.Connections[2];
            Assert.AreEqual("Keep memory routes away from noisy power.", updated.Connections(cpu.BlockId).Requirements(newLink).Requirements.Routing);
            Assert.AreEqual("", updated.Connections(cpu.BlockId).Requirements(link).Requirements.Routing);
            Assert.AreEqual(link, updated.Inspect(cpu).LocalDiagram.Connections[2]);
            var stale = await Invoke(request); Assert.IsFalse(stale.Success); Assert.AreEqual("recursive_block_file_changed", stale.ErrorCode);
            Assert.AreEqual(RecursiveBlockGraphXml.Write(updated), await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CompiledConflictComparisonPreservesVersionsAndBindsChoicesToExactSavedBytes()
    {
        string root = Directory.CreateTempSubdirectory("kicad-recursive-merge-command-").FullName;
        try
        {
            var graph = RecursiveBlockFixture.Create().Graph; string path = Path.Combine(root, "design.xml");
            await File.WriteAllTextAsync(path, RecursiveBlockGraphXml.Write(graph));
            var readRequest = new P.RecursiveFileRequest { SchemaVersion = 1, RepositoryRoot = root, SourcePath = path, DocumentId = graph.DocumentId.ToString("D") };
            var first = await Invoke(readRequest);
            var local = graph.StartDraft(graph.SelectedRoot);
            local = local with { Requirements = local.Requirements.Edit(DiagramRequirementField.Routing, "Top edge") };
            var remote = graph.StartDraft(graph.SelectedRoot);
            remote = remote with { Requirements = remote.Requirements.Edit(DiagramRequirementField.Routing, "Bottom edge") };
            var latest = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], remote, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
            string latestXml = RecursiveBlockGraphXml.Write(latest); await File.WriteAllTextAsync(path, latestXml);
            var request = readRequest.Clone(); request.Action = P.RecursiveFileAction.RfaRebaseRequirements;
            request.ExpectedSourceToken = first.SourceToken;
            request.Rebase = new() { Draft = KiCad.Automation.Native.RecursiveBlockCodec.Encode(local) };
            var compared = await Invoke(request);
            Assert.IsTrue(compared.Success); Assert.IsNull(compared.Merge.Candidate); Assert.HasCount(1, compared.Merge.Conflicts);
            Assert.AreEqual("Top edge", compared.Merge.Conflicts[0].Draft); Assert.AreEqual("Bottom edge", compared.Merge.Conflicts[0].Saved);
            Assert.AreEqual(latestXml, await File.ReadAllTextAsync(path));
            request.ExpectedSourceToken = compared.SourceToken;
            request.Rebase.Resolutions.Add(new P.RequirementResolutionData
            {
                DocumentId = graph.DocumentId.ToString("D"), OwnerId = graph.SelectedRoot.BlockId.ToString("D"), StateId = graph.SelectedRoot.StateId.ToString("D"),
                BaselineRevisionId = compared.Merge.OriginalDraft.BaselineRequirementRevisionId,
                SavedRevisionId = compared.Merge.SavedDraft.BaselineRequirementRevisionId,
                Baseline = compared.Merge.OriginalDraft.BaselineFields.Clone(), Draft = compared.Merge.OriginalDraft.Fields.Clone(),
                Saved = compared.Merge.SavedDraft.Fields.Clone(), Field = P.RequirementFieldKind.RfkRouting, Text = "Chosen combined text"
            });
            var resolved = await Invoke(request);
            Assert.IsTrue(resolved.Success); Assert.IsNotNull(resolved.Merge.Candidate);
            Assert.AreEqual("Chosen combined text", resolved.Merge.Candidate.Fields.Routing);
            Assert.AreEqual(latestXml, await File.ReadAllTextAsync(path)); // Compare is not save.
            await File.AppendAllTextAsync(path, "\n");
            var stale = await Invoke(request);
            Assert.IsFalse(stale.Success); Assert.AreEqual("stale_requirement_resolution", stale.ErrorCode);
            Assert.AreEqual("Chosen combined text", request.Rebase.Resolutions[0].Text);
            Assert.AreEqual(latestXml + "\n", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MalformedAndCancelledCommandsReturnFailureWithoutWritingOrStartingAnMcpSession()
    {
        using var output = new StringWriter();
        Assert.AreEqual(1, await RecursiveFileCommand.RunAsync(new StringReader("{not json"), output, CancellationToken.None));
        Assert.IsFalse(P.RecursiveFileResult.Parser.ParseJson(output.ToString()).Success);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var cancelledOutput = new StringWriter();
        Assert.AreEqual(1, await RecursiveFileCommand.RunAsync(new StringReader("{}"), cancelledOutput, cancellation.Token));
        Assert.AreEqual("cancelled", P.RecursiveFileResult.Parser.ParseJson(cancelledOutput.ToString()).ErrorCode);
        var wrongVersion = await Invoke(new P.RecursiveFileRequest { SchemaVersion = 999 });
        Assert.IsFalse(wrongVersion.Success); Assert.AreEqual("unsupported_diagram_file_request", wrongVersion.ErrorCode);
    }

    private static async Task<P.RecursiveFileResult> Invoke(P.RecursiveFileRequest request)
    {
        string? root = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "automation", "KiCad.Automation.slnx"))) { root = dir.FullName; break; }
        Assert.IsNotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "automation", "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll"));
        start.ArgumentList.Add("--diagram-file");
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var diagnostics = process.StandardError.ReadToEndAsync(timeout.Token);
            var response = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(JsonFormatter.Default.Format(request)); process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var result = P.RecursiveFileResult.Parser.ParseJson(await response);
            Assert.AreEqual(result.Success ? 0 : 1, process.ExitCode, await diagnostics);
            return result;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
