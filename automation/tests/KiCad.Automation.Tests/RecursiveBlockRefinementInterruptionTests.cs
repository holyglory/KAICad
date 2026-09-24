using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockRefinementInterruptionTests
{
    [TestMethod]
    [DataRow("input-prepared")]
    [DataRow("input-replacing")]
    [DataRow("input-replaced")]
    [DataRow("input-published")]
    public async Task ProcessDeathResumesOriginalPublicationWithoutDuplicateInput(string stage)
    {
        string root = Directory.CreateTempSubdirectory("refinement-process-death-").FullName;
        try
        {
            var graph = LinkedDiagramFixture.Create().Graph;
            // A version 1 file as an earlier build stored it, so the publication is its first changed write (R4).
            var input = RecursiveBlockRefinementInputTests.Input(graph, 1) with { Attachments = [] };
            string path = Path.Combine(root, "diagram.xml"), state = Path.Combine(root, "state");
            string before = RecursiveBlockGraphXml.Write(graph, 1); await File.WriteAllTextAsync(path, before);
            string request = Path.Combine(root, "request.json"), marker = Path.Combine(root, "paused.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { repositoryRoot = root, designPath = path,
                documentId = graph.DocumentId, sourceToken = input.SourceSha256, input }));
            var start = SyncHarnessProcessTests.StartInfo(stage, marker);
            start.ArgumentList.Add("--refinement-input-file"); start.ArgumentList.Add(request);
            start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = state;
            start.UseShellExecute = false; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using var process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                Task ready = SyncHarnessProcessTests.WaitForMarkerAsync(marker, timeout.Token);
                Task exit = process.WaitForExitAsync(timeout.Token);
                if (await Task.WhenAny(ready, exit) == exit) Assert.Fail("Owned helper exited before checkpoint: " + await stderr);
                await ready; process.Kill(entireProcessTree: true); await process.WaitForExitAsync(timeout.Token);
                Assert.AreNotEqual(0, process.ExitCode);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await Task.WhenAll(stdout, stderr);
            }
            var interrupted = await RefinementInputRecovery.InspectAsync(root, path, graph.DocumentId, input.Id, state);
            var expected = stage switch
            {
                "input-prepared" or "input-replacing" => RefinementRecoveryDisposition.ResumePrepared,
                "input-replaced" => RefinementRecoveryDisposition.ConfirmPublication,
                _ => RefinementRecoveryDisposition.CompletedPreviously
            };
            Assert.AreEqual(expected, interrupted.Disposition);
            Assert.AreEqual(0, interrupted.UpgradedFromSchemaVersion, "Inspection reports what exists; it writes and upgrades nothing.");
            // Process identity is not reused: the file service resumes from the
            // persisted receipt and paths after OS-enforced termination.
            var recovered = await RefinementInputRecovery.ResumeAsync(root, path, graph.DocumentId, input.Id, state);
            Assert.AreEqual(RefinementRecoveryDisposition.CompletedPreviously, recovered.Disposition);
            Assert.AreEqual(RefinementPublicationStage.Published, recovered.Receipt.Stage);
            string after = await File.ReadAllTextAsync(path);
            Assert.AreEqual(RecursiveBlockGraphXml.Write(graph.WithRefinementInput(input), 2), after, "R4: the publication stores schema 2.");
            Assert.HasCount(1, RecursiveBlockGraphXml.Read(after).RefinementInputs);
            Assert.IsNotNull(recovered.Receipt.RetainedPath);
            Assert.AreEqual(before, await File.ReadAllTextAsync(recovered.Receipt.RetainedPath));
            var again = await RefinementInputRecovery.ResumeAsync(root, path, graph.DocumentId, input.Id, state);
            Assert.AreEqual(recovered.Receipt, again.Receipt); Assert.AreEqual(after, await File.ReadAllTextAsync(path));
            // The completed publication reports the version 1 to 2 upgrade its retained preimage proves, on every resume.
            Assert.AreEqual(1, recovered.UpgradedFromSchemaVersion); Assert.AreEqual(1, again.UpgradedFromSchemaVersion);
            var repeat = await RefinementInputFiles.RecordAsync(root, path, graph.DocumentId, input.SourceSha256, input, stateDirectory: state);
            Assert.IsFalse(repeat.Added);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RacingSaveRetainsBothVersionsAndCannotBecomeAPublishedReceipt()
    {
        string root = Directory.CreateTempSubdirectory("refinement-racing-save-").FullName;
        try
        {
            var graph = LinkedDiagramFixture.Create().Graph;
            var input = RecursiveBlockRefinementInputTests.Input(graph) with { Attachments = [] };
            string path = Path.Combine(root, "diagram.xml"), state = Path.Combine(root, "state");
            string before = RecursiveBlockGraphXml.Write(graph); await File.WriteAllTextAsync(path, before);
            string concurrent = before + "\n<!-- concurrent user save -->\n";
            async Task Race(string checkpoint, CancellationToken token)
            { if (checkpoint == "input-replacing") await File.WriteAllTextAsync(path, concurrent, token); }
            var error = await Assert.ThrowsExactlyAsync<AutomationException>(() => RefinementInputFiles.RecordCoreAsync(root, path,
                graph.DocumentId, input.SourceSha256, input, CancellationToken.None, state, Race));
            Assert.AreEqual("design_file_conflict_preserved", error.Code);
            var receipt = new RefinementInputReceipts(state).Read(input.Id)!;
            Assert.AreEqual(RefinementPublicationStage.Replacing, receipt.Stage);
            Assert.AreEqual(concurrent, await File.ReadAllTextAsync(receipt.RetainedPath!));
            string candidate = await File.ReadAllTextAsync(path);
            var review = await RefinementInputRecovery.ResumeAsync(root, path, graph.DocumentId, input.Id, state);
            Assert.AreEqual(RefinementRecoveryDisposition.NeedsReview, review.Disposition);
            Assert.AreEqual(candidate, await File.ReadAllTextAsync(path));
            Assert.AreEqual(concurrent, await File.ReadAllTextAsync(receipt.RetainedPath!));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => RefinementInputFiles.RecordAsync(root, path, graph.DocumentId,
                input.SourceSha256, input, stateDirectory: state));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => RefinementInputRecovery.ResumeAsync(root, path, graph.DocumentId, input.Id, state, cancelled.Token));
            Assert.AreEqual(candidate, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
