using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class BlockProposalInterruptionTests
{
    [TestMethod]
    [DataRow("proposal-prepared")]
    [DataRow("proposal-replacing")]
    [DataRow("proposal-replaced")]
    [DataRow("proposal-published")]
    public async Task ProcessDeathRecoversProposalPublicationWithoutDuplicateCandidates(string stage)
    {
        string root = Directory.CreateTempSubdirectory("proposal-process-death-").FullName;
        try
        {
            var fixture = RecursiveBlockProposalTests.Fixture();
            var proposal = BlockProposalFiles.Normalize(fixture.Proposal);
            string source = Path.Combine(root, "diagram.xml"), state = Path.Combine(root, "state");
            string before = RecursiveBlockGraphXml.Write(fixture.Graph);
            await File.WriteAllTextAsync(source, before);
            string request = Path.Combine(root, "proposal.json"), marker = Path.Combine(root, "paused.json");
            Guid operation = Guid.NewGuid();
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
            {
                repositoryRoot = root, designPath = source, documentId = fixture.Graph.DocumentId,
                sourceToken = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(before))),
                operationId = operation, proposal
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var start = SyncHarnessProcessTests.StartInfo(stage, marker);
            start.ArgumentList.Add("--block-proposal-file"); start.ArgumentList.Add(request);
            start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = state;
            start.UseShellExecute = false; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using var process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                Task ready = SyncHarnessProcessTests.WaitForMarkerAsync(marker, timeout.Token);
                Task exit = process.WaitForExitAsync(timeout.Token);
                if (await Task.WhenAny(ready, exit) == exit)
                    Assert.Fail("Proposal helper exited before the requested checkpoint. stderr=" + await stderr + " stdout=" + await stdout);
                await ready; process.Kill(entireProcessTree: true); await process.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await Task.WhenAll(stdout, stderr);
            }
            var observed = await BlockProposalRecovery.InspectAsync(root, source, fixture.Graph.DocumentId, operation, state);
            var expected = stage switch
            {
                "proposal-prepared" or "proposal-replacing" => BlockProposalRecoveryDisposition.ResumePrepared,
                "proposal-replaced" => BlockProposalRecoveryDisposition.ConfirmPublication,
                _ => BlockProposalRecoveryDisposition.CompletedPreviously
            };
            Assert.AreEqual(expected, observed.Disposition);
            var recovered = await BlockProposalRecovery.ResumeAsync(root, source, fixture.Graph.DocumentId, operation, state);
            Assert.AreEqual(BlockProposalRecoveryDisposition.CompletedPreviously, recovered.Disposition);
            Assert.AreEqual(BlockProposalOperationStage.Published, recovered.Receipt.Stage);
            var graph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source));
            Assert.HasCount(1, graph.Proposals); Assert.IsTrue(graph.Proposal(proposal.Id).Issues.Length > 0);
            Assert.AreEqual(fixture.Graph.SelectedRoot, graph.SelectedRoot);
            var second = await BlockProposalRecovery.ResumeAsync(root, source, fixture.Graph.DocumentId, operation, state);
            Assert.AreEqual(recovered.Receipt, second.Receipt);
            Assert.AreEqual(RecursiveBlockGraphXml.Write(graph, 2), await File.ReadAllTextAsync(source), "R4: the publication stores schema 2.");
        }
        finally { Directory.Delete(root, true); }
    }
}
