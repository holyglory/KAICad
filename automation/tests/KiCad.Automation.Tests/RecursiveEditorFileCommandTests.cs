using System.Collections.Immutable;
using System.Diagnostics;
using System.Xml.Linq;
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
    public async Task WholeHistoryComparisonAndRestorationPreparationAreReadOnlyAndRevisionBound()
    {
        string root = Directory.CreateTempSubdirectory("kicad-whole-history-").FullName;
        try
        {
            var original = LinkedDiagramFixture.Create().Graph;
            var graph = RecursiveBlockFixture.RefineRoot(original, DiagramRequirementField.General, 2);
            string path = Path.Combine(root, "design.xml"), xml = RecursiveBlockGraphXml.Write(graph);
            await File.WriteAllTextAsync(path, xml);
            var request = new P.RecursiveFileRequest { SchemaVersion = 1, RepositoryRoot = root,
                SourcePath = path, DocumentId = graph.DocumentId.ToString("D") };
            request.ExpectedSourceToken = (await Invoke(request)).SourceToken;
            request.Action = P.RecursiveFileAction.RfaDiagramHistory; request.Block = Selection(graph.SelectedRoot); request.Limit = 2;
            var history = await Invoke(request); Assert.IsTrue(history.Success); Assert.HasCount(2, history.DiagramHistory.Entries);
            Assert.AreEqual(3U, history.DiagramHistory.Total); Assert.IsNull(history.Document);
            request.Offset = 2; var older = await Invoke(request); Assert.IsTrue(older.Success);
            Assert.AreEqual(original.SelectedRoot.RevisionId.ToString("D"), older.DiagramHistory.Entries.Single().Selection.RevisionId);
            request.Action = P.RecursiveFileAction.RfaCompareDiagramHistory; request.Offset = request.Limit = 0; request.InspectedBlock = Selection(original.SelectedRoot);
            var compared = await Invoke(request); Assert.IsTrue(compared.Success); Assert.HasCount(1, compared.DiagramComparison.Changes);
            Assert.AreEqual(P.RequirementFieldKind.RfkGeneral, compared.DiagramComparison.Changes[0].Field);
            request.Action = P.RecursiveFileAction.RfaPrepareDiagramRestoration; request.Block = request.InspectedBlock = null;
            request.Restoration = new() { Draft = RecursiveBlockCodec.Encode(graph.StartDraft(graph.SelectedRoot)), Source = Selection(original.SelectedRoot) };
            var prepared = await Invoke(request); Assert.IsTrue(prepared.Success);
            Assert.AreEqual(original.SelectedRoot.RevisionId.ToString("D"), prepared.PreparedDraft.RestoredFrom.RevisionId);
            Assert.AreEqual(original.Requirements(original.SelectedRoot).Requirements.General, prepared.PreparedDraft.Fields.General);
            Assert.AreEqual(graph.SelectedRoot.RevisionId.ToString("D"), prepared.PreparedDraft.Baseline.RevisionId);
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path));
            var ambiguous = request.Clone(); ambiguous.Block = Selection(graph.SelectedRoot);
            Assert.IsFalse((await Invoke(ambiguous)).Success, "Restoration must not accept unrelated history paging targets.");
            ambiguous = request.Clone(); ambiguous.Offset = 1;
            Assert.IsFalse((await Invoke(ambiguous)).Success);
            ambiguous = request.Clone(); ambiguous.Action = P.RecursiveFileAction.RfaRead;
            Assert.IsFalse((await Invoke(ambiguous)).Success, "A read request cannot smuggle restoration data.");
            var dirty = request.Clone(); dirty.Restoration.Draft.Fields.Routing = "Retain my separate draft.";
            var rejected = await Invoke(dirty); Assert.IsFalse(rejected.Success); Assert.AreEqual("dirty_block_draft", rejected.ErrorCode);
            Assert.AreEqual("Retain my separate draft.", dirty.Restoration.Draft.Fields.Routing);
            await File.AppendAllTextAsync(path, "\n");
            var stale = await Invoke(request); Assert.IsFalse(stale.Success); Assert.IsNull(stale.PreparedDraft);
            Assert.AreEqual("recursive_block_file_changed", stale.ErrorCode); Assert.AreEqual(xml + "\n", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }

        static P.BlockSelectionData Selection(BlockSelection s) => new() { BlockId = s.BlockId.ToString("D"), StateId = s.StateId.ToString("D"), RevisionId = s.RevisionId.ToString("D") };
    }

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
            Assert.AreEqual(RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(restored.Document.Graph), 2), await File.ReadAllTextAsync(path));
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
            Assert.AreEqual(RecursiveBlockGraphXml.Write(loaded, 2), await File.ReadAllTextAsync(path));
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
            Assert.AreEqual(RecursiveBlockGraphXml.Write(updated, 2), await File.ReadAllTextAsync(path));
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
        // Schema 2 actions, payloads and nested fields are declared (contract rbg-v2) but not implemented yet:
        // each fails closed before any file access, exactly like the unknown value it was before the declaration.
        var level = await Invoke(new P.RecursiveFileRequest { SchemaVersion = 1, Action = P.RecursiveFileAction.RfaSaveLevel });
        Assert.IsFalse(level.Success); Assert.AreEqual("unsupported_diagram_file_request", level.ErrorCode);
        string root = Directory.CreateTempSubdirectory("kicad-schema-two-fields-").FullName;
        try
        {
            var graph = LinkedDiagramFixture.Create().Graph;
            string path = Path.Combine(root, "design.xml"), xml = RecursiveBlockGraphXml.Write(graph);
            await File.WriteAllTextAsync(path, xml);
            var read = new P.RecursiveFileRequest { SchemaVersion = 1, RepositoryRoot = root, SourcePath = path, DocumentId = graph.DocumentId.ToString("D") };
            var loaded = await Run(read); Assert.IsTrue(loaded.Success, loaded.ErrorMessage);
            var rebase = read.Clone(); rebase.Action = P.RecursiveFileAction.RfaRebaseRequirements; rebase.ExpectedSourceToken = loaded.SourceToken;
            rebase.Rebase = new() { Draft = RecursiveBlockCodec.Encode(graph.StartDraft(graph.SelectedRoot)) };
            var compared = await Run(rebase); Assert.IsTrue(compared.Success, compared.ErrorMessage); // Precision: the implemented request works.
            var probes = new List<(string Name, P.RecursiveFileRequest Request)>();
            foreach (var action in Enum.GetValues<P.RecursiveFileAction>().Where(a => a > P.RecursiveFileAction.RfaPrepareDiagramRestoration))
            { var probe = read.Clone(); probe.Action = action; probes.Add((action.ToString(), probe)); }
            foreach (var (name, attach) in new (string, Action<P.RecursiveFileRequest>)[]
            {
                ("level_edit", r => r.LevelEdit = new()), ("save_level", r => r.SaveLevel = new()), ("rebase_level", r => r.RebaseLevel = new()),
                ("reparent", r => r.Reparent = new()), ("create", r => r.Create = new()), ("migrate", r => r.Migrate = new()),
                ("discover", r => r.Discover = new() { ProjectFile = Path.Combine(root, "board.kicad_pro") }),
            })
            { var probe = read.Clone(); attach(probe); probes.Add((name, probe)); }
            var nested = rebase.Clone(); nested.Rebase.Draft.LocalDiagram.Presentation = new() { Units = "diagram-unit" };
            probes.Add(("nested presentation", nested));
            var connection = rebase.Clone(); connection.Rebase.Draft.LocalDiagram.Interfaces.Add(new P.DiagramBoundaryInterfaceData
                { Id = Guid.NewGuid().ToString("D"), Name = "Test-only port", Direction = P.DiagramInterfaceDirection.DidrOutput });
            probes.Add(("nested interface direction", connection));
            foreach (var (name, probe) in probes)
            {
                var rejected = await Run(probe);
                Assert.IsFalse(rejected.Success, name); Assert.AreEqual("unsupported_diagram_file_request", rejected.ErrorCode, name);
            }
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }

        static async Task<P.RecursiveFileResult> Run(P.RecursiveFileRequest request)
        {
            using var response = new StringWriter();
            await RecursiveFileCommand.RunAsync(new StringReader(JsonFormatter.Default.Format(request)), response, CancellationToken.None);
            return P.RecursiveFileResult.Parser.ParseJson(response.ToString());
        }
    }

    // ---- Schema 2 through the compiled diagram helper (contract rbg-v2 sections 2 and 7) ----

    private static P.BlockSelectionData Data(BlockSelection s) => new() { BlockId = s.BlockId.ToString("D"), StateId = s.StateId.ToString("D"), RevisionId = s.RevisionId.ToString("D") };
    private static P.DiagramRevisionOriginData EditorOrigin(string summary) => new() { Kind = P.DiagramActorKind.DakEditor, Actor = "Native editor",
        RecordedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), Summary = summary };
    private static P.RecursiveFileRequest ReadRequest(string root, string path, RecursiveBlockGraph graph, uint schema) =>
        new() { SchemaVersion = schema, RepositoryRoot = root, SourcePath = path, DocumentId = graph.DocumentId.ToString("D") };
    private static P.RecursiveFileRequest SaveBlockRequest(P.RecursiveFileRequest read, string token, BlockSelection root, RecursiveBlockDraft draft,
        ImmutableArray<BlockSelection> path, uint schema)
    {
        var request = read.Clone(); request.SchemaVersion = schema; request.Action = P.RecursiveFileAction.RfaSaveBlock; request.ExpectedSourceToken = token;
        request.Save = new() { ExpectedRoot = Data(root), Draft = RecursiveBlockCodec.Encode(draft), NewRevisionId = Guid.NewGuid().ToString("D"),
            NewRequirementRevisionId = Guid.NewGuid().ToString("D"), Origin = EditorOrigin("Edit level") };
        request.Save.BlockPath.Add(path.Select(Data)); request.Save.AncestorRevisionIds.Add(path.Skip(1).Select(_ => Guid.NewGuid().ToString("D")));
        return request;
    }
    private static XName RootName(string xml) => XElement.Parse(xml).Name;

    [TestMethod]
    public async Task SchemaTwoReadsReportTheStoredFormatAndWritabilityWithoutChangingTheFile()
    {
        string root = Directory.CreateTempSubdirectory("kicad-schema-two-read-").FullName;
        try
        {
            var v1 = LinkedDiagramFixture.Create().Graph; var v2 = SchemaTwoFixture.Create().Graph;
            string first = Path.Combine(root, "v1.xml"), second = Path.Combine(root, "v2.xml");
            string v1Xml = RecursiveBlockGraphXml.Write(v1), v2Xml = RecursiveBlockGraphXml.Write(v2);
            await File.WriteAllTextAsync(first, v1Xml); await File.WriteAllTextAsync(second, v2Xml);
            var modified = File.GetLastWriteTimeUtc(first);
            var old = await Invoke(ReadRequest(root, first, v1, 2));
            Assert.IsTrue(old.Success, old.ErrorMessage);
            Assert.AreEqual(2U, old.Document.SchemaVersion); Assert.AreEqual(2U, old.Document.Graph.SchemaVersion);
            Assert.AreEqual(1U, old.Document.StoredSchemaVersion); Assert.IsTrue(old.Document.SourceWritable); Assert.AreEqual(0U, old.UpgradedFromSchemaVersion);
            Assert.AreEqual(v1Xml, RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(old.Document.Graph)));
            var current = await Invoke(ReadRequest(root, second, v2, 2));
            Assert.IsTrue(current.Success, current.ErrorMessage); Assert.AreEqual(2U, current.Document.StoredSchemaVersion);
            Assert.AreEqual(v2Xml, RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(current.Document.Graph)), "Every schema 2 fact crosses the helper.");
            var psu = current.Document.Graph.Revisions.Single(r => r.Selection.RevisionId == SchemaTwoFixtureRevision(v2, "PSU"));
            Assert.AreEqual("diagram-unit", psu.LocalDiagram.Presentation.Units); Assert.AreEqual("1200", psu.LocalDiagram.Presentation.Frame.Width);
            Assert.AreEqual(P.DiagramRealizationState.DrsPartial, psu.LocalDiagram.InterfaceRealizations.Single().State);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(second, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                var readOnly = await Invoke(ReadRequest(root, second, v2, 2));
                Assert.IsTrue(readOnly.Success, readOnly.ErrorMessage); Assert.IsFalse(readOnly.Document.SourceWritable, "Save must be disabled for a read-only file.");
            }
            Assert.AreEqual(v1Xml, await File.ReadAllTextAsync(first)); Assert.AreEqual(v2Xml, await File.ReadAllTextAsync(second));
            Assert.AreEqual(modified, File.GetLastWriteTimeUtc(first), "The writability probe never touches the file.");
            var future = await Invoke(ReadRequest(root, first, v1, 3));
            Assert.IsFalse(future.Success); Assert.AreEqual("unsupported_diagram_file_request", future.ErrorCode);
            await File.WriteAllTextAsync(Path.Combine(root, "v3.xml"), v2Xml.Replace(RecursiveBlockGraphXml.Namespace,
                "urn:kicad:automation:recursive-block-graph:3", StringComparison.Ordinal));
            var tooNew = await Invoke(ReadRequest(root, Path.Combine(root, "v3.xml"), v2, 2));
            Assert.IsFalse(tooNew.Success); Assert.AreEqual("diagram_schema_too_new", tooNew.ErrorCode);
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                foreach (string file in Directory.EnumerateFiles(root)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(root, true);
        }

        static string SchemaTwoFixtureRevision(RecursiveBlockGraph graph, string name) =>
            graph.Walk(graph.SelectedRoot).Single(s => graph.Inspect(s).Name == name).RevisionId.ToString("D");
    }

    [TestMethod]
    public async Task TheFirstChangedSaveUpgradesAVersionOneFileAndUnchangedSavesKeepItsBytes()
    {
        string root = Directory.CreateTempSubdirectory("kicad-schema-two-save-").FullName;
        try
        {
            var f = LinkedDiagramFixture.Create(); var graph = f.Graph;
            string path = Path.Combine(root, "design.xml"), original = RecursiveBlockGraphXml.Write(graph);
            await File.WriteAllTextAsync(path, original);
            var read = ReadRequest(root, path, graph, 2); var loaded = await Invoke(read);
            var psu = f.Blocks["PSU"];
            // An unchanged save writes nothing and upgrades nothing.
            var unchanged = await Invoke(SaveBlockRequest(read, loaded.SourceToken, graph.SelectedRoot, graph.StartDraft(psu), [graph.SelectedRoot, psu], 2));
            Assert.IsTrue(unchanged.Success, unchanged.ErrorMessage); Assert.AreEqual(loaded.SourceToken, unchanged.SourceToken);
            Assert.IsFalse(unchanged.SaveSummary.Changed); Assert.AreEqual(0U, unchanged.UpgradedFromSchemaVersion);
            Assert.AreEqual(1U, unchanged.Document.StoredSchemaVersion);
            Assert.AreEqual(original, await File.ReadAllTextAsync(path), "R4: an unchanged save leaves a version 1 file byte-identical.");
            // R4: the first changed write stores schema 2, even for a change version 1 could hold, and reports the upgrade.
            var edit = graph.StartDraft(psu);
            edit = edit with { Requirements = edit.Requirements.Edit(DiagramRequirementField.General, "Supply the CPU.") };
            var textSaved = await Invoke(SaveBlockRequest(read, loaded.SourceToken, graph.SelectedRoot, edit, [graph.SelectedRoot, psu], 2));
            Assert.IsTrue(textSaved.Success, textSaved.ErrorMessage); Assert.AreEqual(1U, textSaved.UpgradedFromSchemaVersion);
            Assert.AreEqual(2U, textSaved.Document.StoredSchemaVersion);
            string textXml = await File.ReadAllTextAsync(path);
            Assert.AreEqual(XName.Get("recursive-block-graph", RecursiveBlockGraphXml.Namespace), RootName(textXml));
            Assert.IsFalse(XElement.Parse(textXml).Descendants().Any(e => e.Name.NamespaceName is RecursiveBlockGraphXml.NamespaceV1 or DiagramConnectionArchiveXml.NamespaceV1),
                "The embedded connection archives move to schema 2 with the document.");
            var afterText = RecursiveBlockCodec.Decode(textSaved.Document.Graph);
            Assert.AreEqual(textXml, RecursiveBlockGraphXml.Write(afterText, 2), "R5: the stored schema 2 text is canonical.");
            Assert.AreEqual(textXml, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(textXml), 2));
            // The upgrade itself adds no revision and no history row: only the edited PSU revision, its root snapshot and one requirement revision.
            Assert.IsTrue(textSaved.SaveSummary.Changed); Assert.HasCount(1, textSaved.SaveSummary.CreatedBlockRevisions);
            Assert.HasCount(1, textSaved.SaveSummary.CreatedAncestors);
            Assert.AreEqual(graph.Revisions.Length + 2, afterText.Revisions.Length);
            Assert.AreEqual(graph.RequirementHistories.Sum(h => h.Revisions.Length) + 1, afterText.RequirementHistories.Sum(h => h.Revisions.Length));
            Assert.IsTrue(graph.Revisions.All(r => afterText.Inspect(r.Selection).LocalDiagram.SameContents(r.LocalDiagram)), "Every version 1 fact is kept.");
            foreach (var archive in graph.ConnectionArchives) Assert.IsTrue(afterText.Connections(archive.OwnerBlockId).Retains(archive));
            // Later writes keep version 2 without a further upgrade report; a layout change also prunes dormant layout entries.
            var newPsu = afterText.Inspect(afterText.SelectedRoot).Children[0];
            var layout = afterText.StartDraft(newPsu);
            layout = layout with { Diagram = layout.LocalDiagram with { Presentation = new(
                [new(f.Blocks["Power stage"].BlockId, new(140, 110, 240, 145)), new(f.Blocks["Regulator"].BlockId, new(0, 0, 10, 10))],
                [new(f.Blocks["Power stage"].BlockId, f.Ports["Power stage/Output"], DiagramPortSide.Right, 72.5m)], []) } };
            var upgraded = await Invoke(SaveBlockRequest(read, textSaved.SourceToken, afterText.SelectedRoot, layout, [afterText.SelectedRoot, newPsu], 2));
            Assert.IsTrue(upgraded.Success, upgraded.ErrorMessage);
            Assert.AreEqual(0U, upgraded.UpgradedFromSchemaVersion); Assert.AreEqual(2U, upgraded.Document.StoredSchemaVersion);
            Assert.AreEqual(1U, upgraded.SaveSummary.PrunedPresentationEntries, "The Regulator is not a child of PSU, so its placement is dormant.");
            string upgradedXml = await File.ReadAllTextAsync(path);
            Assert.AreEqual(XName.Get("recursive-block-graph", RecursiveBlockGraphXml.Namespace), RootName(upgradedXml));
            var stored = RecursiveBlockGraphXml.Read(upgradedXml); var laidOut = stored.Inspect(stored.SelectedRoot).Children[0];
            Assert.AreEqual(1, stored.Inspect(laidOut).LocalDiagram.Layout.Blocks.Length);
            Assert.AreEqual(72.5m, stored.Inspect(laidOut).LocalDiagram.Layout.Ports.Single().Offset);
            Assert.IsTrue(afterText.Revisions.All(r => stored.Inspect(r.Selection).LocalDiagram.SameContents(r.LocalDiagram)), "Every earlier fact is kept.");
            var again = stored.StartDraft(laidOut);
            again = again with { Requirements = again.Requirements.Edit(DiagramRequirementField.Routing, "Short supply loops.") };
            var later = await Invoke(SaveBlockRequest(read, upgraded.SourceToken, stored.SelectedRoot, again, [stored.SelectedRoot, laidOut], 2));
            Assert.IsTrue(later.Success, later.ErrorMessage); Assert.AreEqual(0U, later.UpgradedFromSchemaVersion); Assert.AreEqual(2U, later.Document.StoredSchemaVersion);
            // A connection's direction and interconnect realization save through the same protocol.
            var latest = RecursiveBlockCodec.Decode(later.Document.Graph); var cpu = f.Blocks["CPU"]; var link = f.Links["CPU/Memory"];
            var connection = latest.Connections(cpu.BlockId).StartDraft(link) with { Direction = DiagramConnectionDirection.FromFirst,
                Realization = new(DiagramRealizationState.Unknown, [], [], "Memory bus wiring is not chosen.", []) };
            var save = new P.SaveConnectionDraftData { ExpectedRoot = Data(latest.SelectedRoot), Draft = RecursiveBlockCodec.Encode(connection),
                NewConnectionRevisionId = Guid.NewGuid().ToString("D"), NewRequirementRevisionId = Guid.NewGuid().ToString("D"),
                NewBlockRevisionId = Guid.NewGuid().ToString("D"), NewBlockRequirementRevisionId = Guid.NewGuid().ToString("D"), Origin = EditorOrigin("Direction") };
            save.BlockPath.Add(Data(latest.SelectedRoot)); save.BlockPath.Add(Data(cpu)); save.ConnectionPath.Add(save.Draft.Baseline.Clone());
            save.BlockAncestorRevisionIds.Add(Guid.NewGuid().ToString("D"));
            var connectionRequest = read.Clone(); connectionRequest.Action = P.RecursiveFileAction.RfaSaveConnection;
            connectionRequest.ExpectedSourceToken = later.SourceToken; connectionRequest.SaveConnection = save;
            var linked = await Invoke(connectionRequest); Assert.IsTrue(linked.Success, linked.ErrorMessage);
            var withLink = RecursiveBlockCodec.Decode(linked.Document.Graph);
            var newLink = withLink.Inspect(withLink.Inspect(withLink.SelectedRoot).Children[1]).LocalDiagram.Connections[2];
            var revision = withLink.Connections(cpu.BlockId).Inspect(newLink);
            Assert.AreEqual(DiagramConnectionDirection.FromFirst, revision.Direction);
            Assert.AreEqual(DiagramRealizationState.Unknown, revision.Realization!.State);
            Assert.AreEqual(RecursiveBlockGraphXml.Write(withLink, 2), await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SchemaOneExchangesRefuseSchemaTwoDocumentsAndFieldsWithoutWriting()
    {
        string root = Directory.CreateTempSubdirectory("kicad-schema-one-refusal-").FullName;
        try
        {
            var f = SchemaTwoFixture.Create(); var graph = f.Graph; var psu = f.Linked.Blocks["PSU"];
            string path = Path.Combine(root, "v2.xml"), xml = RecursiveBlockGraphXml.Write(graph); await File.WriteAllTextAsync(path, xml);
            var two = await Invoke(ReadRequest(root, path, graph, 2)); Assert.IsTrue(two.Success, two.ErrorMessage); // Precision.
            string token = two.SourceToken;
            var requests = new List<(string Name, P.RecursiveFileRequest Request)> { ("read", ReadRequest(root, path, graph, 1)) };
            var history = ReadRequest(root, path, graph, 1); history.Action = P.RecursiveFileAction.RfaDiagramHistory; history.Block = Data(graph.SelectedRoot);
            requests.Add(("history", history));
            var plain = LinkedDiagramFixture.Create();
            var draft = graph.StartDraft(psu); // What an old editor would send: its schema 1 view of the level.
            var v1Draft = RecursiveBlockCodec.Encode(draft with { Diagram = draft.LocalDiagram with { Presentation = null, InterfaceRealizations = default,
                Interfaces = [.. draft.LocalDiagram.Interfaces.Select(i => i with { Domain = DiagramDomain.Unspecified, Direction = DiagramInterfaceDirection.Unspecified })] } });
            var save = SaveBlockRequest(ReadRequest(root, path, graph, 1), token, graph.SelectedRoot, draft, [graph.SelectedRoot, psu], 1);
            save.Save.Draft = v1Draft; requests.Add(("save", save));
            var manage = ReadRequest(root, path, graph, 1); manage.Action = P.RecursiveFileAction.RfaManageImplementation; manage.ExpectedSourceToken = token;
            manage.Implementation = new() { Action = P.ImplementationActionKind.IakDuplicate, ExpectedRoot = Data(graph.SelectedRoot), Source = Data(psu),
                Name = "Copy", NewStateId = Guid.NewGuid().ToString("D"), NewRevisionId = Guid.NewGuid().ToString("D"),
                NewRequirementRevisionId = Guid.NewGuid().ToString("D"), Origin = EditorOrigin("Duplicate") };
            requests.Add(("manage", manage));
            foreach (var (name, request) in requests)
            {
                var refused = await Invoke(request);
                Assert.IsFalse(refused.Success, name); Assert.AreEqual("unsupported_diagram_file_request", refused.ErrorCode, name);
                Assert.IsNull(refused.Document, name);
            }
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path), "No schema 1 exchange changed the schema 2 file.");
            // On a version 1 file the schema 1 protocol still works, and still refuses schema 2 fields.
            string plainPath = Path.Combine(root, "v1.xml"), plainXml = RecursiveBlockGraphXml.Write(plain.Graph); await File.WriteAllTextAsync(plainPath, plainXml);
            var one = await Invoke(ReadRequest(root, plainPath, plain.Graph, 1));
            Assert.IsTrue(one.Success, one.ErrorMessage); Assert.AreEqual(1U, one.Document.SchemaVersion); Assert.AreEqual(1U, one.Document.Graph.SchemaVersion);
            Assert.AreEqual(0U, one.Document.StoredSchemaVersion, "A schema 1 document has no stored-format field.");
            var laidOut = plain.Graph.StartDraft(plain.Blocks["PSU"]);
            laidOut = laidOut with { Diagram = laidOut.LocalDiagram with { Presentation = new([new(plain.Blocks["Telemetry"].BlockId, new(1, 1, 10, 10))], [], []) } };
            var smuggled = await Invoke(SaveBlockRequest(ReadRequest(root, plainPath, plain.Graph, 1), one.SourceToken, plain.Graph.SelectedRoot, laidOut,
                [plain.Graph.SelectedRoot, plain.Blocks["PSU"]], 1));
            Assert.IsFalse(smuggled.Success); Assert.AreEqual("unsupported_diagram_file_request", smuggled.ErrorCode);
            var reparent = ReadRequest(root, plainPath, plain.Graph, 1); reparent.Action = P.RecursiveFileAction.RfaPrepareReparent;
            reparent.ExpectedSourceToken = one.SourceToken; reparent.Reparent = new() { ExpectedRoot = Data(plain.Graph.SelectedRoot) };
            var oldMove = await Invoke(reparent); Assert.IsFalse(oldMove.Success); Assert.AreEqual("unsupported_diagram_file_request", oldMove.ErrorCode);
            Assert.AreEqual(plainXml, await File.ReadAllTextAsync(plainPath));
            // The interim schema 1 bridge writes like every other writer: its first changed write stores schema 2 (R4), which its
            // schema 1 result cannot report, and the file stays usable over schema 1 because it still holds no schema 2 fact.
            var textEdit = plain.Graph.StartDraft(plain.Blocks["PSU"]);
            textEdit = textEdit with { Requirements = textEdit.Requirements.Edit(DiagramRequirementField.Schematic, "Keep the regulator sheet first.") };
            var oldSave = await Invoke(SaveBlockRequest(ReadRequest(root, plainPath, plain.Graph, 1), one.SourceToken, plain.Graph.SelectedRoot, textEdit,
                [plain.Graph.SelectedRoot, plain.Blocks["PSU"]], 1));
            Assert.IsTrue(oldSave.Success, oldSave.ErrorMessage); Assert.AreEqual(1U, oldSave.Document.SchemaVersion);
            Assert.AreEqual(0U, oldSave.UpgradedFromSchemaVersion); Assert.AreEqual(0U, oldSave.Document.StoredSchemaVersion);
            Assert.AreEqual(XName.Get("recursive-block-graph", RecursiveBlockGraphXml.Namespace), RootName(await File.ReadAllTextAsync(plainPath)));
            var reread = await Invoke(ReadRequest(root, plainPath, plain.Graph, 1));
            Assert.IsTrue(reread.Success, reread.ErrorMessage); Assert.AreEqual(oldSave.SourceToken, reread.SourceToken);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MovingABlockIsPreviewedThenSavedOnceWithEveryCheckFailingClosed()
    {
        string root = Directory.CreateTempSubdirectory("kicad-reparent-command-").FullName;
        try
        {
            var f = LinkedDiagramFixture.Create(); var graph = f.Graph;
            string path = Path.Combine(root, "design.xml"), xml = RecursiveBlockGraphXml.Write(graph); await File.WriteAllTextAsync(path, xml);
            var read = ReadRequest(root, path, graph, 2); var loaded = await Invoke(read);
            var system = graph.SelectedRoot; var psu = f.Blocks["PSU"]; var cpu = f.Blocks["CPU"]; var telemetry = f.Blocks["Telemetry"];
            var attached = f.Links["PSU/Telemetry"].ConnectionId;
            P.RecursiveFileRequest Move(P.RecursiveFileAction action, Guid block, ImmutableArray<BlockSelection> source, ImmutableArray<BlockSelection> target,
                IEnumerable<Guid>? detach = null, bool ids = true, string? token = null)
            {
                var request = read.Clone(); request.Action = action; request.ExpectedSourceToken = token ?? loaded.SourceToken;
                request.Reparent = new() { ExpectedRoot = Data(system), BlockId = block.ToString("D"),
                    TargetPlacement = new() { BlockId = block.ToString("D"), Rect = new() { X = "400", Y = "120.000000", Width = "240", Height = "145" } } };
                request.Reparent.SourceParentPath.Add(source.Select(Data)); request.Reparent.TargetParentPath.Add(target.Select(Data));
                request.Reparent.DetachConnectionIds.Add((detach ?? []).Select(id => id.ToString("D")));
                if (ids)
                {
                    foreach (var owner in source.Concat(target).Select(s => s.BlockId).Distinct())
                        request.Reparent.NewRevisions.Add(new P.RevisionIdAssignmentData { ObjectId = owner.ToString("D"), NewRevisionId = Guid.NewGuid().ToString("D") });
                    request.Reparent.Origin = EditorOrigin("Move to another block");
                }
                return request;
            }
            var preview = await Invoke(Move(P.RecursiveFileAction.RfaPrepareReparent, telemetry.BlockId, [system, psu], [system, cpu], ids: false));
            Assert.IsTrue(preview.Success, preview.ErrorMessage); Assert.IsNull(preview.Document);
            CollectionAssert.AreEqual(new[] { attached.ToString("D") }, preview.ReparentPreview.RequiredDetachConnectionIds.ToArray());
            CollectionAssert.AreEqual(new[] { P.LevelEditEffectKind.LeekChildRemoved, P.LevelEditEffectKind.LeekConnectionRemoved },
                preview.ReparentPreview.Effects.Select(e => e.Kind).ToArray());
            Assert.IsTrue(preview.ReparentPreview.Effects.All(e => e.ScopeBlockId == psu.BlockId.ToString("D")));
            CollectionAssert.AreEqual(new[] { system.BlockId, psu.BlockId, cpu.BlockId }.Select(id => id.ToString("D")).ToArray(),
                preview.ReparentPreview.SuccessorBlockIds.ToArray());
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path), "A preview never writes.");
            foreach (var (name, request, code) in new[]
            {
                ("connected", Move(P.RecursiveFileAction.RfaReparentBlock, telemetry.BlockId, [system, psu], [system, cpu]), "reparent_connected_block"),
                ("extra detach", Move(P.RecursiveFileAction.RfaReparentBlock, telemetry.BlockId, [system, psu], [system, cpu], [attached, f.Links["PSU/Supply"].ConnectionId]),
                    "reparent_disposition_mismatch"),
                ("cycle", Move(P.RecursiveFileAction.RfaReparentBlock, psu.BlockId, [system], [system, psu, telemetry]), "reparent_cycle"),
                ("root", Move(P.RecursiveFileAction.RfaReparentBlock, system.BlockId, [system], [system, cpu]), "reparent_root"),
                ("same parent", Move(P.RecursiveFileAction.RfaReparentBlock, telemetry.BlockId, [system, psu], [system, psu], [attached]), "reparent_same_parent"),
                ("not a child", Move(P.RecursiveFileAction.RfaReparentBlock, f.Blocks["Memory"].BlockId, [system, psu], [system, cpu]), "reparent_block_not_child"),
                ("wrong path", Move(P.RecursiveFileAction.RfaReparentBlock, telemetry.BlockId, [system, cpu, psu], [system, cpu], [attached]),
                    "reparent_not_in_selected_design"),
                ("no identities", Move(P.RecursiveFileAction.RfaReparentBlock, telemetry.BlockId, [system, psu], [system, cpu], [attached], ids: false),
                    "invalid_reparent_request"),
                ("stale", Move(P.RecursiveFileAction.RfaReparentBlock, telemetry.BlockId, [system, psu], [system, cpu], [attached], token: new string('0', 64)),
                    "recursive_block_file_changed"),
            })
            {
                var refused = await Invoke(request);
                Assert.IsFalse(refused.Success, name); Assert.AreEqual(code, refused.ErrorCode, name + ": " + refused.ErrorMessage);
            }
            var reused = Move(P.RecursiveFileAction.RfaReparentBlock, telemetry.BlockId, [system, psu], [system, cpu], [attached]);
            reused.Reparent.NewRevisions[0].NewRevisionId = psu.RevisionId.ToString("D");
            var reusedResult = await Invoke(reused); Assert.AreEqual("identity_reused", reusedResult.ErrorCode);
            var misplaced = Move(P.RecursiveFileAction.RfaReparentBlock, telemetry.BlockId, [system, psu], [system, cpu], [attached]);
            misplaced.Reparent.TargetPlacement.BlockId = psu.BlockId.ToString("D");
            Assert.AreEqual("invalid_presentation_view", (await Invoke(misplaced)).ErrorCode);
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path), "Refused moves never write.");
            var moved = await Invoke(Move(P.RecursiveFileAction.RfaReparentBlock, telemetry.BlockId, [system, psu], [system, cpu], [attached]));
            Assert.IsTrue(moved.Success, moved.ErrorMessage);
            Assert.AreEqual(1U, moved.UpgradedFromSchemaVersion, "R4: the move is the first changed write of this version 1 file.");
            Assert.HasCount(2, moved.SaveSummary.CreatedBlockRevisions); Assert.HasCount(1, moved.SaveSummary.CreatedAncestors);
            var after = RecursiveBlockCodec.Decode(moved.Document.Graph);
            var newPsu = after.Inspect(after.SelectedRoot).Children[0]; var newCpu = after.Inspect(after.SelectedRoot).Children[1];
            CollectionAssert.AreEqual(new[] { f.Blocks["Power stage"] }, after.Inspect(newPsu).Children.ToArray());
            CollectionAssert.AreEqual(new[] { f.Blocks["Processor"], f.Blocks["Memory"], telemetry }, after.Inspect(newCpu).Children.ToArray(),
                "The moved block keeps its exact identity and revision.");
            Assert.IsFalse(after.Inspect(newPsu).LocalDiagram.Connections.Any(c => c.ConnectionId == attached));
            Assert.IsNotNull(after.Connections(psu.BlockId).Inspect(f.Links["PSU/Telemetry"]), "The detached connection stays in PSU's archive history.");
            Assert.AreEqual(120m, after.Inspect(newCpu).LocalDiagram.Layout.Blocks.Single().Rect.Y, "Protocol decimals are canonicalized exactly.");
            Assert.IsTrue(after.Walk(system).Contains(telemetry) && graph.Inspect(psu).Children.Contains(telemetry), "The old root still reproduces the old hierarchy.");
            Assert.AreEqual(RecursiveBlockGraphXml.Write(after, 2), await File.ReadAllTextAsync(path));
            var retry = await Invoke(Move(P.RecursiveFileAction.RfaReparentBlock, telemetry.BlockId, [system, psu], [system, cpu], [attached]));
            Assert.AreEqual("recursive_block_file_changed", retry.ErrorCode, "A retry with the preview's token cannot move twice.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MovingABlockUpAndDownALevelPreviewsEveryEffectOnBothLevelsAndSavesOneSuccessorPerBlock()
    {
        string root = Directory.CreateTempSubdirectory("kicad-reparent-schema-two-").FullName;
        try
        {
            // Schema 2 fixture: PSU has a note on Power stage, a Partial realization of PSU/Power through Supply and Power stage,
            // and a layout with Power stage, its port and the Supply route. System keeps a dormant placement and port of Power
            // stage and a note whose target was removed earlier, as a copied older layout would.
            var f = SchemaTwoFixture.Create(); var g = f.Graph; var l = f.Linked;
            var system = g.SelectedRoot; var psu = l.Blocks["PSU"]; var cpu = l.Blocks["CPU"]; var power = l.Blocks["Power stage"];
            Guid output = l.Ports["Power stage/Output"], psuPower = l.Ports["PSU/Power"], supply = l.Links["PSU/Supply"].ConnectionId;
            var heatNote = new DiagramAnnotation(Guid.NewGuid(), DiagramAnnotationRole.Comment, "Check the power stage heat sink.",
                new(DiagramAnnotationTargetKind.Block, power.BlockId), new(120, 90), [], SchemaTwoFixture.Origin);
            var returnNote = new DiagramAnnotation(Guid.NewGuid(), DiagramAnnotationRole.Instruction, "Keep the power stage near the input connector.",
                new(DiagramAnnotationTargetKind.Block, power.BlockId, "Target removed from this diagram level."), null, [], SchemaTwoFixture.Origin);
            var graph = new RecursiveBlockGraph(g.DocumentId, g.SelectedRoot, g.States, g.Revisions.Select(r =>
                r.Selection == psu ? r with { Diagram = r.LocalDiagram with { Annotations = [heatNote] } }
                : r.Selection == system ? r with { Diagram = r.LocalDiagram with { Annotations = [returnNote], Presentation = r.LocalDiagram.Layout with {
                    Blocks = r.LocalDiagram.Layout.BlockPlacements.Add(new(power.BlockId, new(820, 80, 200, 120))),
                    Ports = r.LocalDiagram.Layout.PortPlacements.Add(new(power.BlockId, output, DiagramPortSide.Right, 30)) } } }
                : r), g.RequirementHistories, g.ConnectionArchives);
            string path = Path.Combine(root, "design.xml"), xml = RecursiveBlockGraphXml.Write(graph); await File.WriteAllTextAsync(path, xml);
            var read = ReadRequest(root, path, graph, 2); var loaded = await Invoke(read); Assert.IsTrue(loaded.Success, loaded.ErrorMessage);
            Assert.AreEqual(2U, loaded.Document.StoredSchemaVersion);
            static string Effect(P.LevelEditEffectKind kind, Guid objectId, Guid scope, string detail) => $"{kind} {objectId:D} {scope:D} {detail}";
            static string[] Ordered(IEnumerable<(P.LevelEditEffectKind Kind, Guid Object, Guid Scope, string Detail)> effects) => [.. effects
                .OrderBy(e => (int)e.Kind).ThenBy(e => e.Object.ToString("D"), StringComparer.Ordinal).ThenBy(e => e.Detail, StringComparer.Ordinal)
                .ThenBy(e => e.Scope.ToString("D"), StringComparer.Ordinal).Select(e => Effect(e.Kind, e.Object, e.Scope, e.Detail))];
            static string[] Actual(P.ReparentPreviewData preview) => [.. preview.Effects.Select(e => Effect(e.Kind, Guid.Parse(e.ObjectId), Guid.Parse(e.ScopeBlockId), e.Detail))];

            // Move 1: Power stage from PSU up to System (the target parent is an ancestor of the source parent).
            var up = MoveRequest(read, P.RecursiveFileAction.RfaPrepareReparent, system, power.BlockId, [system, psu], [system], [supply],
                new() { BlockId = power.BlockId.ToString("D"), Rect = new() { X = "760", Y = "300", Width = "203.2", Height = "152.4" } }, loaded.SourceToken, null);
            var preview = await Invoke(up); Assert.IsTrue(preview.Success, preview.ErrorMessage);
            CollectionAssert.AreEqual(new[] { supply.ToString("D") }, preview.ReparentPreview.RequiredDetachConnectionIds.ToArray());
            CollectionAssert.AreEqual(new[] { system.BlockId, psu.BlockId }.Select(id => id.ToString("D")).ToArray(), preview.ReparentPreview.SuccessorBlockIds.ToArray());
            CollectionAssert.AreEqual(Ordered(
            [
                (P.LevelEditEffectKind.LeekChildRemoved, power.BlockId, psu.BlockId, "Power stage"),
                (P.LevelEditEffectKind.LeekConnectionRemoved, supply, psu.BlockId, "Supply"),
                (P.LevelEditEffectKind.LeekAnnotationUnresolved, heatNote.Id, psu.BlockId, "Target removed from this diagram level."),
                (P.LevelEditEffectKind.LeekRealizationTargetRemoved, psuPower, psu.BlockId, $"ChildInterface {power.BlockId:D}/{output:D}"),
                (P.LevelEditEffectKind.LeekRealizationTargetRemoved, psuPower, psu.BlockId, $"LocalConnection {supply:D}"),
                (P.LevelEditEffectKind.LeekRealizationStateChanged, psuPower, psu.BlockId, "Partial -> Unknown"),
                (P.LevelEditEffectKind.LeekPresentationEntryRemoved, power.BlockId, psu.BlockId, $"block:{power.BlockId:D}"),
                (P.LevelEditEffectKind.LeekPresentationEntryRemoved, output, psu.BlockId, $"port:{power.BlockId:D}:{output:D}"),
                (P.LevelEditEffectKind.LeekPresentationEntryRemoved, supply, psu.BlockId, $"route:{supply:D}:1"),
                // The target level: the dormant placement and port of the arriving block are dropped and its note resolves again.
                (P.LevelEditEffectKind.LeekPresentationEntryRemoved, power.BlockId, system.BlockId, $"block:{power.BlockId:D}"),
                (P.LevelEditEffectKind.LeekPresentationEntryRemoved, output, system.BlockId, $"port:{power.BlockId:D}:{output:D}"),
                (P.LevelEditEffectKind.LeekAnnotationResolved, returnNote.Id, system.BlockId, "Target returned to this diagram level."),
            ]), Actual(preview.ReparentPreview));
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path), "A preview never writes.");
            var upIds = new Dictionary<Guid, Guid> { [system.BlockId] = Guid.NewGuid(), [psu.BlockId] = Guid.NewGuid() };
            var movedUp = await Invoke(MoveRequest(read, P.RecursiveFileAction.RfaReparentBlock, system, power.BlockId, [system, psu], [system], [supply],
                up.Reparent.TargetPlacement, loaded.SourceToken, upIds));
            Assert.IsTrue(movedUp.Success, movedUp.ErrorMessage);
            Assert.AreEqual(0U, movedUp.UpgradedFromSchemaVersion); Assert.AreEqual(2U, movedUp.Document.StoredSchemaVersion);
            Assert.IsTrue(movedUp.SaveSummary.Changed); Assert.HasCount(2, movedUp.SaveSummary.CreatedBlockRevisions);
            Assert.IsEmpty(movedUp.SaveSummary.CreatedAncestors, "The target parent is the root, so no other snapshot is needed.");
            Assert.AreEqual(2U, movedUp.SaveSummary.PrunedPresentationEntries, "The dormant placement and port of Power stage in System.");
            var afterUp = RecursiveBlockCodec.Decode(movedUp.Document.Graph);
            string upXml = await File.ReadAllTextAsync(path);
            Assert.AreEqual(RecursiveBlockGraphXml.Write(afterUp, 2), upXml); Assert.AreEqual(upXml, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(upXml), 2));
            var system1 = new BlockSelection(system.BlockId, system.StateId, upIds[system.BlockId]);
            var psu1 = new BlockSelection(psu.BlockId, psu.StateId, upIds[psu.BlockId]);
            Assert.AreEqual(system1, afterUp.SelectedRoot);
            Assert.AreEqual(graph.Revisions.Length + 2, afterUp.Revisions.Length, "Exactly one successor for System and one for PSU.");
            // The root's one successor holds both changes: its new PSU child and the arriving block with its placement.
            var rootAfterUp = afterUp.Inspect(system1);
            Assert.AreEqual(system.RevisionId, rootAfterUp.ParentRevisionId); Assert.AreEqual("Native editor", rootAfterUp.Origin.Actor);
            CollectionAssert.AreEqual(new[] { psu1, cpu, power }, rootAfterUp.Children.ToArray(), "The moved block keeps its exact identity and revision.");
            Assert.AreEqual(new DiagramRect(760, 300, 203.2m, 152.4m), rootAfterUp.LocalDiagram.Layout.BlockPlacements.Single(b => b.BlockId == power.BlockId).Rect);
            Assert.IsFalse(rootAfterUp.LocalDiagram.Layout.PortPlacements.Any(p => p.BlockId == power.BlockId));
            var resolved = rootAfterUp.LocalDiagram.Notes.Single(); Assert.AreEqual(returnNote.Id, resolved.Id);
            Assert.IsNull(resolved.Target.UnresolvedReason); Assert.AreEqual("Native editor", resolved.Origin.Actor);
            Assert.IsTrue(graph.Inspect(system).LocalDiagram.SameContents(rootAfterUp.LocalDiagram with
            {
                Annotations = [returnNote],
                Presentation = rootAfterUp.LocalDiagram.Layout with { Blocks = [.. rootAfterUp.LocalDiagram.Layout.BlockPlacements.Where(b => b.BlockId != power.BlockId)
                    .Append(new DiagramBlockPlacement(power.BlockId, new(820, 80, 200, 120)))],
                    Ports = rootAfterUp.LocalDiagram.Layout.PortPlacements.Add(new(power.BlockId, output, DiagramPortSide.Right, 30)) }
            }), "Nothing else changes at the target level.");
            // The source level loses the block, its attached connection, their layout and realization targets; its note is kept unresolved.
            var psuAfterUp = afterUp.Inspect(psu1);
            Assert.AreEqual(psu.RevisionId, psuAfterUp.ParentRevisionId);
            CollectionAssert.AreEqual(new[] { l.Blocks["Telemetry"] }, psuAfterUp.Children.ToArray());
            CollectionAssert.AreEqual(new[] { l.Links["PSU/Telemetry"] }, psuAfterUp.LocalDiagram.Connections.ToArray());
            var downgraded = psuAfterUp.LocalDiagram.Realizations.Single();
            Assert.AreEqual(psuPower, downgraded.InterfaceId); Assert.AreEqual(DiagramRealizationState.Unknown, downgraded.State);
            Assert.IsEmpty(downgraded.TargetList); Assert.AreEqual("Realizing element was removed.", downgraded.UnresolvedReason);
            var psuLayout = psuAfterUp.LocalDiagram.Layout;
            Assert.IsFalse(psuLayout.BlockPlacements.Any(b => b.BlockId == power.BlockId)); Assert.IsFalse(psuLayout.PortPlacements.Any(p => p.BlockId == power.BlockId));
            Assert.IsEmpty(psuLayout.ConnectionRoutes); Assert.AreEqual(new DiagramRect(0, 0, 1200, 800), psuLayout.Frame);
            CollectionAssert.AreEqual(new[] { l.Blocks["Telemetry"].BlockId, l.Blocks["Regulator"].BlockId }, psuLayout.BlockPlacements.Select(b => b.BlockId).ToArray(),
                "The other placements, including the dormant Regulator entry, are kept verbatim.");
            Assert.AreEqual("Target removed from this diagram level.", psuAfterUp.LocalDiagram.Notes.Single().Target.UnresolvedReason);
            Assert.IsNotNull(afterUp.Connections(psu.BlockId).Inspect(l.Links["PSU/Supply"]), "The detached connection stays in PSU's archive history.");
            CollectionAssert.AreEqual(graph.Inspect(system).Children.ToArray(), afterUp.Inspect(system).Children.ToArray(), "The old root still reproduces the old hierarchy.");
            Assert.IsTrue(afterUp.Inspect(psu).Children.Contains(power));

            // Move 2: Power stage from System down into CPU (the source parent is an ancestor of the target parent), unplaced there.
            var down = MoveRequest(read, P.RecursiveFileAction.RfaPrepareReparent, system1, power.BlockId, [system1], [system1, cpu], [], null, movedUp.SourceToken, null);
            var downPreview = await Invoke(down); Assert.IsTrue(downPreview.Success, downPreview.ErrorMessage);
            Assert.IsEmpty(downPreview.ReparentPreview.RequiredDetachConnectionIds);
            CollectionAssert.AreEqual(new[] { system.BlockId, cpu.BlockId }.Select(id => id.ToString("D")).ToArray(), downPreview.ReparentPreview.SuccessorBlockIds.ToArray());
            CollectionAssert.AreEqual(Ordered(
            [
                (P.LevelEditEffectKind.LeekChildRemoved, power.BlockId, system.BlockId, "Power stage"),
                (P.LevelEditEffectKind.LeekAnnotationUnresolved, returnNote.Id, system.BlockId, "Target removed from this diagram level."),
                (P.LevelEditEffectKind.LeekPresentationEntryRemoved, power.BlockId, system.BlockId, $"block:{power.BlockId:D}"),
            ]), Actual(downPreview.ReparentPreview));
            var downIds = new Dictionary<Guid, Guid> { [system.BlockId] = Guid.NewGuid(), [cpu.BlockId] = Guid.NewGuid() };
            var movedDown = await Invoke(MoveRequest(read, P.RecursiveFileAction.RfaReparentBlock, system1, power.BlockId, [system1], [system1, cpu], [], null,
                movedUp.SourceToken, downIds));
            Assert.IsTrue(movedDown.Success, movedDown.ErrorMessage);
            Assert.HasCount(2, movedDown.SaveSummary.CreatedBlockRevisions); Assert.IsEmpty(movedDown.SaveSummary.CreatedAncestors);
            Assert.AreEqual(0U, movedDown.SaveSummary.PrunedPresentationEntries);
            var afterDown = RecursiveBlockCodec.Decode(movedDown.Document.Graph);
            Assert.AreEqual(RecursiveBlockGraphXml.Write(afterDown, 2), await File.ReadAllTextAsync(path));
            var system2 = new BlockSelection(system.BlockId, system.StateId, downIds[system.BlockId]);
            var cpu1 = new BlockSelection(cpu.BlockId, cpu.StateId, downIds[cpu.BlockId]);
            Assert.AreEqual(system2, afterDown.SelectedRoot); Assert.AreEqual(afterUp.Revisions.Length + 2, afterDown.Revisions.Length);
            var rootAfterDown = afterDown.Inspect(system2);
            Assert.AreEqual(system1.RevisionId, rootAfterDown.ParentRevisionId);
            CollectionAssert.AreEqual(new[] { psu1, cpu1 }, rootAfterDown.Children.ToArray(), "One root successor removes the block and pins the new CPU.");
            Assert.IsFalse(rootAfterDown.LocalDiagram.Layout.BlockPlacements.Any(b => b.BlockId == power.BlockId));
            Assert.AreEqual("Target removed from this diagram level.", rootAfterDown.LocalDiagram.Notes.Single().Target.UnresolvedReason);
            var cpuAfterDown = afterDown.Inspect(cpu1);
            Assert.AreEqual(cpu.RevisionId, cpuAfterDown.ParentRevisionId);
            CollectionAssert.AreEqual(new[] { l.Blocks["Processor"], l.Blocks["Memory"], power }, cpuAfterDown.Children.ToArray());
            Assert.IsNull(cpuAfterDown.LocalDiagram.Presentation, "Without a target placement the moved block is unplaced.");
            Assert.IsTrue(graph.Inspect(cpu).LocalDiagram.SameContents(cpuAfterDown.LocalDiagram));

            // Stale requests are refused before anything is written.
            string current = await File.ReadAllTextAsync(path);
            var staleRoot = await Invoke(MoveRequest(read, P.RecursiveFileAction.RfaReparentBlock, system, l.Blocks["Telemetry"].BlockId, [system, psu], [system, cpu],
                [l.Links["PSU/Telemetry"].ConnectionId], null, movedDown.SourceToken, new() { [system.BlockId] = Guid.NewGuid(), [psu.BlockId] = Guid.NewGuid(), [cpu.BlockId] = Guid.NewGuid() }));
            Assert.IsFalse(staleRoot.Success); Assert.AreEqual("stale_root_revision", staleRoot.ErrorCode, staleRoot.ErrorMessage);
            Assert.AreEqual(current, await File.ReadAllTextAsync(path));
            // A newer saved CPU revision that the root does not pin yet makes CPU a stale parent.
            var candidate = graph.AppendRevision(cpu.RevisionId, graph.Inspect(cpu) with
                { Selection = cpu with { RevisionId = Guid.NewGuid() }, ParentRevisionId = cpu.RevisionId, Name = "CPU" });
            string stalePath = Path.Combine(root, "stale.xml"), staleXml = RecursiveBlockGraphXml.Write(candidate); await File.WriteAllTextAsync(stalePath, staleXml);
            var staleRead = ReadRequest(root, stalePath, candidate, 2); var staleLoaded = await Invoke(staleRead); Assert.IsTrue(staleLoaded.Success, staleLoaded.ErrorMessage);
            foreach (var action in new[] { P.RecursiveFileAction.RfaPrepareReparent, P.RecursiveFileAction.RfaReparentBlock })
            {
                var staleParent = await Invoke(MoveRequest(staleRead, action, system, l.Blocks["Telemetry"].BlockId, [system, psu], [system, cpu],
                    [l.Links["PSU/Telemetry"].ConnectionId], null, staleLoaded.SourceToken,
                    action == P.RecursiveFileAction.RfaPrepareReparent ? null
                        : new Dictionary<Guid, Guid> { [system.BlockId] = Guid.NewGuid(), [psu.BlockId] = Guid.NewGuid(), [cpu.BlockId] = Guid.NewGuid() }));
                Assert.IsFalse(staleParent.Success, action.ToString()); Assert.AreEqual("stale_parent_revision", staleParent.ErrorCode, staleParent.ErrorMessage);
            }
            Assert.AreEqual(staleXml, await File.ReadAllTextAsync(stalePath));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MovingABlockRefusesARevisionIdentityAlreadyUsedByAProposalIssue()
    {
        string root = Directory.CreateTempSubdirectory("kicad-reparent-identity-").FullName;
        try
        {
            var f = RecursiveBlockProposalTests.Fixture();
            var prepared = BlockProposalCompiler.Prepare(f.Graph, f.Proposal);
            var record = new BlockProposalRecord(f.Proposal.Id, f.Proposal.InputId, BlockProposalFiles.Fingerprint(f.Proposal), f.Proposal.BasePath,
                f.Proposal.Candidate, f.Proposal.Issues, prepared.Graph.Inspect(f.Proposal.Candidate).Origin);
            var graph = prepared.Graph.WithProposal(record); Guid issue = record.Issues.Single().Id;
            string path = Path.Combine(root, "design.xml"), xml = RecursiveBlockGraphXml.Write(graph); await File.WriteAllTextAsync(path, xml);
            var read = ReadRequest(root, path, graph, 2); var loaded = await Invoke(read); Assert.IsTrue(loaded.Success, loaded.ErrorMessage);
            var system = graph.SelectedRoot; var psu = graph.Inspect(system).Children[0]; var cpu = graph.Inspect(system).Children[1];
            var telemetry = graph.Inspect(psu).Children[1]; Guid attached = graph.Inspect(psu).LocalDiagram.Connections[1].ConnectionId;
            var reused = await Invoke(MoveRequest(read, P.RecursiveFileAction.RfaReparentBlock, system, telemetry.BlockId, [system, psu], [system, cpu], [attached], null,
                loaded.SourceToken, new() { [system.BlockId] = Guid.NewGuid(), [psu.BlockId] = issue, [cpu.BlockId] = Guid.NewGuid() }));
            Assert.IsFalse(reused.Success); Assert.AreEqual("identity_reused", reused.ErrorCode, "A proposal issue identity is already used by this document.");
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path));
            // Precision: the same move with fresh identities is saved.
            var moved = await Invoke(MoveRequest(read, P.RecursiveFileAction.RfaReparentBlock, system, telemetry.BlockId, [system, psu], [system, cpu], [attached], null,
                loaded.SourceToken, new() { [system.BlockId] = Guid.NewGuid(), [psu.BlockId] = Guid.NewGuid(), [cpu.BlockId] = Guid.NewGuid() }));
            Assert.IsTrue(moved.Success, moved.ErrorMessage); Assert.AreEqual(1U, moved.UpgradedFromSchemaVersion);
            Assert.AreEqual(issue, RecursiveBlockCodec.Decode(moved.Document.Graph).Proposals.Single().Issues.Single().Id);
        }
        finally { Directory.Delete(root, true); }
    }

    private static P.RecursiveFileRequest MoveRequest(P.RecursiveFileRequest read, P.RecursiveFileAction action, BlockSelection expectedRoot, Guid block,
        ImmutableArray<BlockSelection> source, ImmutableArray<BlockSelection> target, IEnumerable<Guid> detach, P.DiagramBlockPlacementData? placement,
        string token, Dictionary<Guid, Guid>? newRevisionIds)
    {
        var request = read.Clone(); request.Action = action; request.ExpectedSourceToken = token;
        request.Reparent = new() { ExpectedRoot = Data(expectedRoot), BlockId = block.ToString("D") };
        if (placement is not null) request.Reparent.TargetPlacement = placement.Clone();
        request.Reparent.SourceParentPath.Add(source.Select(Data)); request.Reparent.TargetParentPath.Add(target.Select(Data));
        request.Reparent.DetachConnectionIds.Add(detach.Select(id => id.ToString("D")));
        if (newRevisionIds is not null)
        {
            foreach (var (owner, revision) in newRevisionIds)
                request.Reparent.NewRevisions.Add(new P.RevisionIdAssignmentData { ObjectId = owner.ToString("D"), NewRevisionId = revision.ToString("D") });
            request.Reparent.Origin = EditorOrigin("Move to another block");
        }
        return request;
    }

    [TestMethod]
    public async Task RemovingAnInterfaceStillInUseReturnsEachUseAsErrorDetails()
    {
        string root = Directory.CreateTempSubdirectory("kicad-interface-in-use-").FullName;
        try
        {
            var f = LinkedDiagramFixture.Create(); var graph = f.Graph;
            string path = Path.Combine(root, "design.xml"), xml = RecursiveBlockGraphXml.Write(graph); await File.WriteAllTextAsync(path, xml);
            var read = ReadRequest(root, path, graph, 2); var loaded = await Invoke(read);
            var psu = f.Blocks["PSU"]; var power = f.Blocks["Power stage"];
            var draft = graph.StartDraft(power);
            draft = draft with { Diagram = draft.LocalDiagram with { Interfaces = [] } };
            var refused = await Invoke(SaveBlockRequest(read, loaded.SourceToken, graph.SelectedRoot, draft, [graph.SelectedRoot, psu, power], 2));
            Assert.IsFalse(refused.Success); Assert.AreEqual("boundary_interface_in_use", refused.ErrorCode);
            var detail = refused.ErrorDetails.Single();
            Assert.AreEqual("connection", detail.Kind); Assert.AreEqual(psu.BlockId.ToString("D"), detail.ScopeBlockId);
            Assert.AreEqual(f.Links["PSU/Supply"].ConnectionId.ToString("D"), detail.ObjectId);
            StringAssert.Contains(detail.Message, "Supply");
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UnimplementedSchemaTwoActionsAndPayloadsStayClosedInSchemaTwo()
    {
        string root = Directory.CreateTempSubdirectory("kicad-schema-two-closed-").FullName;
        try
        {
            var f = LinkedDiagramFixture.Create(); var graph = f.Graph;
            string path = Path.Combine(root, "design.xml"), xml = RecursiveBlockGraphXml.Write(graph); await File.WriteAllTextAsync(path, xml);
            var read = ReadRequest(root, path, graph, 2); var loaded = await Invoke(read); Assert.IsTrue(loaded.Success, loaded.ErrorMessage);
            var probes = new List<(string Name, P.RecursiveFileRequest Request, string Code)>();
            foreach (var action in new[] { P.RecursiveFileAction.RfaPrepareLevelEdit, P.RecursiveFileAction.RfaSaveLevel, P.RecursiveFileAction.RfaRebaseLevel,
                P.RecursiveFileAction.RfaCreateDiagram, P.RecursiveFileAction.RfaPrepareMigration, P.RecursiveFileAction.RfaMigrateFlatDiagram,
                P.RecursiveFileAction.RfaDiscoverDiagrams })
            { var probe = read.Clone(); probe.Action = action; probes.Add((action.ToString(), probe, "unsupported_diagram_file_request")); }
            foreach (var (name, attach) in new (string, Action<P.RecursiveFileRequest>)[]
            {
                ("level_edit", r => r.LevelEdit = new()), ("save_level", r => r.SaveLevel = new()), ("rebase_level", r => r.RebaseLevel = new()),
                ("create", r => r.Create = new()), ("migrate", r => r.Migrate = new()),
                ("discover", r => r.Discover = new() { ProjectFile = Path.Combine(root, "board.kicad_pro") }),
            })
            { var probe = read.Clone(); attach(probe); probes.Add((name, probe, "unsupported_diagram_file_request")); }
            var stray = read.Clone(); stray.Reparent = new(); probes.Add(("reparent payload on a read", stray, "ambiguous_diagram_file_request"));
            var paged = read.Clone(); paged.Action = P.RecursiveFileAction.RfaPrepareReparent; paged.ExpectedSourceToken = loaded.SourceToken;
            paged.Reparent = new(); paged.Limit = 5; probes.Add(("paged move", paged, "ambiguous_diagram_file_request"));
            foreach (var (name, request, code) in probes)
            {
                var rejected = await Invoke(request);
                Assert.IsFalse(rejected.Success, name); Assert.AreEqual(code, rejected.ErrorCode, name);
            }
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path));
            // Precision: schema 2 accepts and keeps the implemented nested fields a schema 1 exchange refuses.
            var draft = graph.StartDraft(f.Blocks["PSU"]);
            draft = draft with { Diagram = draft.LocalDiagram with { Interfaces = draft.LocalDiagram.Interfaces.SetItem(0,
                draft.LocalDiagram.Interfaces[0] with { Direction = DiagramInterfaceDirection.Output }) } };
            var accepted = await Invoke(SaveBlockRequest(read, loaded.SourceToken, graph.SelectedRoot, draft, [graph.SelectedRoot, f.Blocks["PSU"]], 2));
            Assert.IsTrue(accepted.Success, accepted.ErrorMessage); Assert.AreEqual(1U, accepted.UpgradedFromSchemaVersion);
            var saved = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(path));
            Assert.AreEqual(DiagramInterfaceDirection.Output, saved.Inspect(saved.Inspect(saved.SelectedRoot).Children[0]).LocalDiagram.Interfaces[0].Direction);
        }
        finally { Directory.Delete(root, true); }
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
