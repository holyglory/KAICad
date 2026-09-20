using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class BlockProposalSelectionInterruptionTests
{
    [TestMethod]
    [DataRow("proposal-selection-prepared")]
    [DataRow("proposal-selection-replacing")]
    [DataRow("proposal-selection-replaced")]
    [DataRow("proposal-selection-published")]
    public async Task ProcessDeathRecoversConceptualSelectionWithoutDuplicateRootSnapshots(string stage)
    {
        string root = Directory.CreateTempSubdirectory("proposal-selection-death-").FullName;
        try
        {
            var f = RecursiveBlockProposalTests.Fixture(); var prepared = BlockProposalCompiler.Prepare(f.Graph, f.Proposal);
            string fingerprint = BlockProposalFiles.Fingerprint(f.Proposal);
            var record = new BlockProposalRecord(f.Proposal.Id, f.Proposal.InputId, fingerprint, f.Proposal.BasePath,
                f.Proposal.Candidate, f.Proposal.Issues, prepared.Graph.Inspect(f.Proposal.Candidate).Origin);
            var graph = prepared.Graph.WithProposal(record);
            string source = Path.Combine(root, "diagram.xml"), state = Path.Combine(root, "state");
            string xml = RecursiveBlockGraphXml.Write(graph); await File.WriteAllTextAsync(source, xml);
            Guid operation = Guid.NewGuid(); string request = Path.Combine(root, "selection.json"), marker = Path.Combine(root, "paused.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
            {
                repositoryRoot = root, designPath = source, documentId = graph.DocumentId, proposalId = f.Proposal.Id,
                sourceToken = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(xml))),
                expectedRoot = graph.SelectedRoot, currentPath = f.Proposal.BasePath, ancestorIds = new[] { Guid.NewGuid() },
                operationId = operation, origin = f.Proposal.Origin
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var start = SyncHarnessProcessTests.StartInfo(stage, marker); start.ArgumentList.Add("--block-proposal-select-file"); start.ArgumentList.Add(request);
            start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = state; start.UseShellExecute = false;
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using var process = Process.Start(start)!; Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                Task ready = SyncHarnessProcessTests.WaitForMarkerAsync(marker, timeout.Token); Task exit = process.WaitForExitAsync(timeout.Token);
                if (await Task.WhenAny(ready, exit) == exit) Assert.Fail("Selection helper exited early. stderr=" + await stderr);
                await ready; process.Kill(entireProcessTree: true); await process.WaitForExitAsync(timeout.Token);
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); }
            var observed = await BlockProposalRecovery.InspectAsync(root, source, graph.DocumentId, operation, state);
            var expected = stage switch
            {
                "proposal-selection-prepared" or "proposal-selection-replacing" => BlockProposalRecoveryDisposition.ResumePrepared,
                "proposal-selection-replaced" => BlockProposalRecoveryDisposition.ConfirmPublication,
                _ => BlockProposalRecoveryDisposition.CompletedPreviously
            };
            Assert.AreEqual(expected, observed.Disposition);
            var recovered = await BlockProposalRecovery.ResumeAsync(root, source, graph.DocumentId, operation, state);
            Assert.AreEqual(BlockProposalRecoveryDisposition.CompletedPreviously, recovered.Disposition);
            var selected = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source));
            Assert.AreNotEqual(graph.SelectedRoot, selected.SelectedRoot);
            Assert.HasCount(1, selected.Proposals); Assert.AreEqual(f.Proposal.Candidate, selected.Inspect(selected.SelectedRoot).Children[0]);
            var again = await BlockProposalRecovery.ResumeAsync(root, source, graph.DocumentId, operation, state);
            Assert.AreEqual(recovered.Receipt, again.Receipt); Assert.AreEqual(RecursiveBlockGraphXml.Write(selected), await File.ReadAllTextAsync(source));
        }
        finally { Directory.Delete(root, true); }
    }
}
