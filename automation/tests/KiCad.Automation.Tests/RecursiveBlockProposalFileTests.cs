using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockProposalFileTests
{
    [TestMethod]
    public async Task AgentProposalsCarrySchemaTwoFactsAndUpgradeTheFileWhileVersionOneRequestsKeepTheirFingerprint()
    {
        string root = Directory.CreateTempSubdirectory("kicad-block-proposal-v2-").FullName;
        try
        {
            var f = RecursiveBlockProposalTests.Fixture(); string path = Path.Combine(root, "diagram.xml"), state = Path.Combine(root, "state");
            // A version 1 shaped request serializes exactly as before schema 2: its recorded fingerprint cannot move.
            string plain = JsonSerializer.Serialize(BlockProposalFiles.Normalize(f.Proposal));
            foreach (string member in new[] { "\"Domain\"", "\"Direction\"", "\"Presentation\"", "\"InterfaceRealizations\"", "\"Realization\"", "\"Layout\"" })
                Assert.IsFalse(plain.Contains(member, StringComparison.Ordinal), member);
            var target = f.Proposal.Blocks[0]; var link = f.Proposal.Connections[0]; var member0 = f.Proposal.Connections[1];
            var layout = new DiagramPresentationView([new(target.Children[0].BlockId, new(140, 110, 240, 145)), new(target.Children[1].BlockId, new(510, 110, 240, 145))],
                [], [new(link.Selection.ConnectionId, 1, [new(420, 180)], null)]);
            var telemetryPort = target.Diagram.Interfaces[1].Id;
            var proposal = f.Proposal with
            {
                Blocks = f.Proposal.Blocks.SetItem(0, target with { Diagram = target.Diagram with { Presentation = layout,
                    InterfaceRealizations = [new(telemetryPort, DiagramRealizationState.Partial,
                        [InterfaceRealizationTarget.LocalConnection(link.Selection.ConnectionId)], "Member signals are not mapped yet.", [])] } }),
                Connections = [link with { Domain = DiagramDomain.Data }, member0 with { Direction = DiagramConnectionDirection.FromFirst,
                    Realization = new(DiagramRealizationState.Unknown, [], [], "Wiring is not chosen.", []) }]
            };
            // Agents may omit empty collections; the request still validates and publishes.
            var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(proposal, web))!;
            json["blocks"]![0]!["diagram"]!["interfaceRealizations"]![0]!.AsObject().Remove("sources");
            json["connections"]![1]!["realization"]!.AsObject().Remove("segments");
            json["connections"]![1]!["realization"]!.AsObject().Remove("joins");
            var sent = BlockProposalFiles.Normalize(JsonSerializer.Deserialize<BlockProposal>(json.ToJsonString(), web)!);
            await File.WriteAllTextAsync(path, RecursiveBlockGraphXml.Write(f.Graph));
            var before = await RecursiveBlockFiles.ReadAsync(root, path, f.Graph.DocumentId);
            Assert.AreEqual(1, before.StoredSchemaVersion);
            var saved = await BlockProposalFiles.PublishAsync(root, path, f.Graph.DocumentId, before.ContentSha256, sent, state);
            Assert.IsTrue(saved.Added); Assert.AreEqual(1, saved.Snapshot.UpgradedFromSchemaVersion); Assert.AreEqual(2, saved.Snapshot.StoredSchemaVersion);
            var stored = RecursiveBlockGraphXml.ReadVersioned(await File.ReadAllTextAsync(path));
            Assert.AreEqual(2, stored.StoredSchemaVersion);
            var candidate = stored.Graph.Inspect(proposal.Candidate).LocalDiagram;
            Assert.IsTrue(layout.SameContents(candidate.Presentation)); Assert.AreEqual(DiagramRealizationState.Partial, candidate.Realizations.Single().State);
            var archive = stored.Graph.Connections(target.Selection.BlockId);
            Assert.AreEqual(DiagramDomain.Data, archive.Inspect(link.Selection).Domain);
            Assert.AreEqual(DiagramConnectionDirection.FromFirst, archive.Inspect(member0.Selection).Direction);
            Assert.AreEqual(DiagramRealizationState.Unknown, archive.Inspect(member0.Selection).Realization!.State);
            var retry = await BlockProposalFiles.PublishAsync(root, path, f.Graph.DocumentId, before.ContentSha256, sent, state);
            Assert.IsFalse(retry.Added, "An identical schema 2 request observes the published candidate.");
        }
        finally { Directory.Delete(root, true); }
    }

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
            // R4: publishing into a version 1 file is its first changed write, which stores schema 2 and reports the upgrade.
            Assert.AreEqual(1, before.StoredSchemaVersion); Assert.AreEqual(2, saved.Snapshot.StoredSchemaVersion);
            Assert.AreEqual(1, saved.Snapshot.UpgradedFromSchemaVersion);
            Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(xml), 2));
            Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(saved.Snapshot.Graph)), 2));
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
