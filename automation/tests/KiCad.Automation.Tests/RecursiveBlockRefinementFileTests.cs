using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockRefinementFileTests
{
    [TestMethod]
    public async Task RecordingPreservesContextAndVerifiesAssetsWithoutSelectingANewDesign()
    {
        string root = Directory.CreateTempSubdirectory("kicad-refinement-input-").FullName;
        try
        {
            var graph = LinkedDiagramFixture.Create().Graph;
            string path = Path.Combine(root, "diagram.xml"); string before = RecursiveBlockGraphXml.Write(graph);
            await File.WriteAllTextAsync(path, before);
            byte[] source = Encoding.UTF8.GetBytes("Original requirement source.");
            await File.WriteAllBytesAsync(Path.Combine(root, "requirement.txt"), source);
            var asset = await RefinementAssetFiles.CaptureAsync(root, "requirement.txt", "assets/original", Guid.NewGuid(),
                Convert.ToHexStringLower(SHA256.HashData(source)), source.Length, "text/plain");
            var input = RecursiveBlockRefinementInputTests.Input(graph) with { Attachments = [asset] };
            var saved = await RefinementInputFiles.RecordAsync(root, path, graph.DocumentId, input.SourceSha256, input);
            Assert.IsTrue(saved.Added); Assert.AreEqual(graph.SelectedRoot, saved.Snapshot.Graph.SelectedRoot);
            Assert.IsTrue(input.SameContents(saved.Snapshot.Graph.RefinementInput(input.Id)));
            Assert.AreEqual(graph.Revisions.Length, saved.Snapshot.Graph.Revisions.Length);
            string savedXml = await File.ReadAllTextAsync(path);
            var retry = await RefinementInputFiles.RecordAsync(root, path, graph.DocumentId, input.SourceSha256, input);
            Assert.IsFalse(retry.Added); Assert.AreEqual(saved.Snapshot.ContentSha256, retry.Snapshot.ContentSha256);
            Assert.AreEqual(savedXml, await File.ReadAllTextAsync(path));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => RefinementInputFiles.RecordAsync(root, path, graph.DocumentId,
                input.SourceSha256, input with { Prompt = "Changed under an existing identity." }));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => RefinementInputFiles.RecordAsync(root, path, graph.DocumentId,
                input.SourceSha256, input with { Id = Guid.NewGuid() }));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => RefinementInputFiles.RecordAsync(root, path, graph.DocumentId,
                saved.Snapshot.ContentSha256, input, cancelled.Token));
            Assert.AreEqual(savedXml, await File.ReadAllTextAsync(path));
            File.Delete(Path.Combine(root, asset.AssetPath));
            var next = input with { Id = Guid.NewGuid(), SourceSha256 = saved.Snapshot.ContentSha256 };
            await Assert.ThrowsExactlyAsync<AutomationException>(() => RefinementInputFiles.RecordAsync(root, path, graph.DocumentId,
                saved.Snapshot.ContentSha256, next));
            Assert.AreEqual(savedXml, await File.ReadAllTextAsync(path));
            // A current-state lookup is still possible, without pretending missing
            // asset bytes or a prior uncertain publication were recovered.
            Assert.IsFalse((await RefinementInputFiles.RecordAsync(root, path, graph.DocumentId, input.SourceSha256, input)).Added);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AgentOriginsReferenceOnlyTheRetainedInputScopeAndKeepSourceProvenance()
    {
        var graph = LinkedDiagramFixture.Create().Graph; var root = graph.SelectedRoot;
        var child = graph.Inspect(root).Children[0]; var other = graph.Inspect(root).Children[1];
        var input = RecursiveBlockRefinementInputTests.Input(graph) with { BlockPath = [root, child] };
        graph = graph.WithRefinementInput(input);
        var origin = RecursiveBlockFixture.Origin() with { ActorKind = RequirementRevisionActor.Agent, InputIds = [Guid.NewGuid()] };
        var linked = RefinementInputFiles.AttachOrigin(graph, [root, child], input.Id, origin);
        Assert.IsTrue(linked.InputIds.Contains(input.Id)); Assert.IsTrue(linked.InputIds.Contains(origin.InputIds[0]));
        Assert.IsTrue(linked.Sources.Contains(input.Attachments[0].Source!));
        Assert.ThrowsExactly<AutomationException>(() => RefinementInputFiles.AttachOrigin(graph, [root, other], input.Id, origin));
        Assert.ThrowsExactly<AutomationException>(() => RefinementInputFiles.AttachOrigin(graph, [root], input.Id, origin));
        Assert.ThrowsExactly<AutomationException>(() => RefinementInputFiles.AttachOrigin(graph, [root, child], Guid.NewGuid(), origin));
        Assert.AreSame(origin, RefinementInputFiles.AttachOrigin(graph, [root, child], null, origin));
    }
}
