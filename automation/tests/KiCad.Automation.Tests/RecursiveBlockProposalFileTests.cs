using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockProposalFileTests
{
    [TestMethod]
    public async Task PublishedProposalKeepsIssuesHistoryAndIndependentActiveSelection()
    {
        string root = Directory.CreateTempSubdirectory("kicad-block-proposal-").FullName;
        try
        {
            var f = RecursiveBlockProposalTests.Fixture(); string path = Path.Combine(root, "diagram.xml"), state = Path.Combine(root, "state");
            await File.WriteAllTextAsync(path, RecursiveBlockGraphXml.Write(f.Graph));
            var before = await RecursiveBlockFiles.ReadAsync(root, path, f.Graph.DocumentId);
            var saved = await BlockProposalFiles.PublishAsync(root, path, f.Graph.DocumentId, before.ContentSha256, f.Proposal, state);
            Assert.IsTrue(saved.Added); Assert.AreEqual(f.Graph.SelectedRoot, saved.Snapshot.Graph.SelectedRoot);
            Assert.HasCount(1, saved.Snapshot.Graph.Proposals); Assert.HasCount(1, saved.Proposal.Issues);
            Assert.AreEqual(f.Proposal.Issues[0].Message, saved.Proposal.Issues[0].Message);
            string xml = await File.ReadAllTextAsync(path);
            Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(xml)));
            Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(saved.Snapshot.Graph))));
            var wire = RecursiveBlockCodec.Encode(saved.Proposal); wire.MergeFrom(new byte[] { 0x98, 0x06, 1 });
            Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(wire));
            var retry = await BlockProposalFiles.PublishAsync(root, path, f.Graph.DocumentId, before.ContentSha256, f.Proposal, state);
            Assert.IsFalse(retry.Added); Assert.AreEqual(xml, await File.ReadAllTextAsync(path));
            var retained = BlockProposalFiles.ReadRetained(state, f.Proposal.Id);
            Assert.AreEqual(BlockProposalFiles.Fingerprint(f.Proposal), retained.RequestSha256);
            Assert.AreEqual(retained.RequestSha256, BlockProposalFiles.Fingerprint(retained.Proposal));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => BlockProposalFiles.PublishAsync(root, path, f.Graph.DocumentId,
                saved.Snapshot.ContentSha256, f.Proposal with { Issues = [] }, state));
            var chosen = await BlockProposalFiles.SelectAsync(root, path, f.Graph.DocumentId, f.Proposal.Id, saved.Snapshot.ContentSha256,
                saved.Snapshot.Graph.SelectedRoot, f.Proposal.BasePath, [Guid.NewGuid()], f.Proposal.Origin);
            Assert.AreEqual(f.Sibling, chosen.Graph.Inspect(chosen.Graph.SelectedRoot).Children[1]);
            Assert.AreEqual(f.Proposal.Candidate, chosen.Graph.Inspect(chosen.Graph.SelectedRoot).Children[0]);
            Assert.IsTrue(saved.Proposal.SameContents(chosen.Graph.Proposal(f.Proposal.Id)));
            var draft = chosen.Graph.StartDraft(f.Proposal.Candidate);
            var edited = chosen.Graph.SaveDraft(chosen.Graph.SelectedRoot, [chosen.Graph.SelectedRoot, f.Proposal.Candidate], draft with
                { Requirements = draft.Requirements.Edit(DiagramRequirementField.Routing, "Later manual guidance.") },
                Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
            Assert.IsTrue(saved.Proposal.SameContents(edited.Proposal(f.Proposal.Id)), "The recorded candidate must remain historical, not follow its newer head.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task StaleAndInvalidProposalsRemainRetrievableWithoutPartialPublication()
    {
        string root = Directory.CreateTempSubdirectory("kicad-stale-proposal-").FullName;
        try
        {
            var f = RecursiveBlockProposalTests.Fixture(); string path = Path.Combine(root, "diagram.xml"), state = Path.Combine(root, "state");
            var newer = RecursiveBlockFixture.RefineRoot(f.Graph, DiagramRequirementField.General, 1);
            string xml = RecursiveBlockGraphXml.Write(newer); await File.WriteAllTextAsync(path, xml);
            await Assert.ThrowsExactlyAsync<AutomationException>(() => BlockProposalFiles.PublishAsync(root, path, f.Graph.DocumentId, "stale", f.Proposal, state));
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path));
            var retained = BlockProposalFiles.ReadRetained(state, f.Proposal.Id); Assert.AreEqual(f.Proposal.Id, retained.Proposal.Id);
            var current = await RecursiveBlockFiles.ReadAsync(root, path, f.Graph.DocumentId);
            var saved = await BlockProposalFiles.PublishAsync(root, path, f.Graph.DocumentId, current.ContentSha256, retained.Proposal, state);
            Assert.IsFalse(saved.ContextStillSelected); Assert.AreEqual(newer.SelectedRoot, saved.Snapshot.Graph.SelectedRoot);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => BlockProposalFiles.SelectAsync(root, path, f.Graph.DocumentId,
                f.Proposal.Id, saved.Snapshot.ContentSha256, newer.SelectedRoot, [newer.SelectedRoot, f.Proposal.BasePath[^1]],
                [Guid.NewGuid()], f.Proposal.Origin, cancel.Token));
            Assert.AreEqual(saved.Snapshot.ContentSha256, (await RecursiveBlockFiles.ReadAsync(root, path, f.Graph.DocumentId)).ContentSha256);
            var draft = saved.Snapshot.Graph.StartDraft(f.Proposal.BasePath[^1]);
            var changedTarget = saved.Snapshot.Graph.SaveDraft(newer.SelectedRoot, [newer.SelectedRoot, f.Proposal.BasePath[^1]], draft with
                { Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "User changed the target while the agent was working.") },
                Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
            Assert.ThrowsExactly<AutomationException>(() => BlockProposalCompiler.Select(changedTarget, f.Proposal.Id, changedTarget.SelectedRoot,
                [changedTarget.SelectedRoot, changedTarget.Inspect(changedTarget.SelectedRoot).Children[0]], [Guid.NewGuid()], f.Proposal.Origin));
            Assert.AreEqual(f.Proposal.Candidate, changedTarget.Proposal(f.Proposal.Id).Candidate);
            // Malformed/newer input fields are not silently dropped by typed decoding.
            string json = JsonSerializer.Serialize(BlockProposalFiles.Normalize(f.Proposal)).Replace("\"Blocks\":", "\"Future\":true,\"Blocks\":", StringComparison.Ordinal);
            Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<BlockProposal>(json));
        }
        finally { Directory.Delete(root, true); }
    }
}
