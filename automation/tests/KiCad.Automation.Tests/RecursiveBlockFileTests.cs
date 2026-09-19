using System.Collections.Immutable;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockFileTests
{
    [TestMethod]
    public async Task SaveReopenAndReconstructHistoricRootWithItsRequirementHistory()
    {
        string root = Directory.CreateTempSubdirectory("kicad-recursive-save-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml"); var f = RecursiveBlockFixture.Create();
            await File.WriteAllTextAsync(path, RecursiveBlockGraphXml.Write(f.Graph));
            var before = await RecursiveBlockFiles.ReadAsync(root, path, f.Graph.DocumentId);
            var draft = before.Graph.StartDraft(f.Selected["PSU"]);
            draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "User's revised power intent\r\n  preserved.") };
            var saved = await RecursiveBlockFiles.SaveDraftAsync(root, path, f.Graph.DocumentId, before.ContentSha256,
                before.Graph.SelectedRoot, [before.Graph.SelectedRoot, f.Selected["PSU"]], draft, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin());
            var reopened = await RecursiveBlockFiles.ReadAsync(root, path, f.Graph.DocumentId);
            Assert.AreEqual(saved.ContentSha256, reopened.ContentSha256);
            Assert.AreEqual(saved.Graph.SelectedRoot, reopened.Graph.SelectedRoot);
            var psu = reopened.Graph.Inspect(reopened.Graph.SelectedRoot).Children[0];
            Assert.AreEqual(draft.Requirements.Requirements.General, reopened.Graph.Requirements(psu).Requirements.General);
            Assert.AreEqual(f.Graph.Requirements(f.Selected["PSU"]).Requirements, reopened.Graph.Requirements(f.Selected["PSU"]).Requirements);
            CollectionAssert.AreEqual(f.Graph.Walk(f.Graph.SelectedRoot).ToArray(), reopened.Graph.Walk(f.Graph.SelectedRoot).ToArray());
            var bytes = await File.ReadAllBytesAsync(path);
            var unchanged = await RecursiveBlockFiles.SaveDraftAsync(root, path, f.Graph.DocumentId, saved.ContentSha256,
                saved.Graph.SelectedRoot, [saved.Graph.SelectedRoot], saved.Graph.StartDraft(saved.Graph.SelectedRoot),
                Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin());
            Assert.AreEqual(saved.ContentSha256, unchanged.ContentSha256);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ConcurrentSavesCannotLoseTheWinningHistoryOrOverwriteWithAStaleRetry()
    {
        string root = Directory.CreateTempSubdirectory("kicad-recursive-race-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml"); var graph = RecursiveBlockFixture.Create().Graph;
            await File.WriteAllTextAsync(path, RecursiveBlockGraphXml.Write(graph));
            var baseline = await RecursiveBlockFiles.ReadAsync(root, path, graph.DocumentId);
            async Task<bool> Save(string text)
            {
                var draft = graph.StartDraft(graph.SelectedRoot);
                draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.Routing, text) };
                try
                {
                    _ = await RecursiveBlockFiles.SaveDraftAsync(root, path, graph.DocumentId, baseline.ContentSha256,
                        graph.SelectedRoot, [graph.SelectedRoot], draft, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin());
                    return true;
                }
                catch (AutomationException) { return false; }
            }
            bool[] results = await Task.WhenAll(Save("Top edge"), Save("Bottom edge"));
            Assert.AreEqual(1, results.Count(x => x));
            var winner = await RecursiveBlockFiles.ReadAsync(root, path, graph.DocumentId);
            Assert.HasCount(2, winner.Graph.History(graph.SelectedRoot.StateId));
            Assert.AreEqual(results[0] ? "Top edge" : "Bottom edge", winner.Graph.Requirements(winner.Graph.SelectedRoot).Requirements.Routing);
            Assert.IsFalse(await Save("Stale retry"));
            Assert.AreEqual(winner.ContentSha256, (await RecursiveBlockFiles.ReadAsync(root, path, graph.DocumentId)).ContentSha256);
            Assert.AreEqual("", winner.Graph.Requirements(graph.SelectedRoot).Requirements.Routing);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WrongDocumentsPathsInvalidXmlAndCancellationPreserveSavedFile()
    {
        string root = Directory.CreateTempSubdirectory("kicad-recursive-reject-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml"); var graph = RecursiveBlockFixture.Create().Graph;
            string xml = RecursiveBlockGraphXml.Write(graph); await File.WriteAllTextAsync(path, xml);
            var loaded = await RecursiveBlockFiles.ReadAsync(root, path, graph.DocumentId);
            await Assert.ThrowsExactlyAsync<AutomationException>(() => RecursiveBlockFiles.ReadAsync(root, path, Guid.NewGuid()));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => RecursiveBlockFiles.ReadAsync(root, path, Guid.Empty));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => RecursiveBlockFiles.ReadAsync(root, Path.Combine(root, "..", "outside.xml"), graph.DocumentId));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => RecursiveBlockFiles.ReadAsync(root, root, graph.DocumentId));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var draft = graph.StartDraft(graph.SelectedRoot) with { Name = "Unsaved draft" };
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => RecursiveBlockFiles.SaveDraftAsync(root, path,
                graph.DocumentId, loaded.ContentSha256, graph.SelectedRoot, [graph.SelectedRoot], draft,
                Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin(), token: cancelled.Token));
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path));
            string invalid = xml.Replace("version=\"1\"", "version=\"999\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, invalid);
            await Assert.ThrowsExactlyAsync<AutomationException>(() => RecursiveBlockFiles.SaveDraftAsync(root, path,
                graph.DocumentId, loaded.ContentSha256, graph.SelectedRoot, [graph.SelectedRoot], draft,
                Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()));
            Assert.AreEqual(invalid, await File.ReadAllTextAsync(path));
            // Recovery restores the input explicitly, then the same retained draft can be saved.
            await File.WriteAllTextAsync(path, xml);
            var recovered = await RecursiveBlockFiles.SaveDraftAsync(root, path, graph.DocumentId, loaded.ContentSha256,
                graph.SelectedRoot, [graph.SelectedRoot], draft, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin());
            Assert.AreEqual("Unsaved draft", recovered.Graph.Inspect(recovered.Graph.SelectedRoot).Name);
        }
        finally { Directory.Delete(root, true); }
    }
}
