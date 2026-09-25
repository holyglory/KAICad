using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyPcbDrcJobs(NativeClient client, DocumentSpecifier board,
        string evidence, CancellationToken token)
    {
        // The native job owner is registered (7717fc32a4), but complete project/rule
        // input capture is still open (p23deb822a36256a6). An ordinary job must run on
        // its detached snapshot, leave the live board untouched and never claim a
        // complete snapshot or fresh results.
        async Task<(StartPcbDrcJob Request, PcbDrcJobState State)> Run(DocumentLifecycleState at)
        {
            var request = new StartPcbDrcJob
            {
                Document = board, ProcessEpoch = client.Epoch,
                ExpectedRevision = at.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D")
            };
            var started = await client.InvokeAsync<StartPcbDrcJob, PcbDrcJobState>(request, token);
            Assert.AreEqual(board, started.Document);
            Assert.AreEqual(request.OperationId, started.OperationId);
            Assert.AreEqual(client.Epoch, started.ProcessEpoch);
            Assert.AreEqual(at.Revision, started.CheckedRevision);
            Assert.IsTrue(Guid.TryParseExact(started.JobId, "D", out _));
            Assert.IsFalse(started.CandidateDryRun);
            Assert.IsEmpty(started.CandidateItemIds);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromMinutes(2));
            int delay = 25;
            var state = started;
            while (!state.WorkerFinished)
            {
                await Task.Delay(delay, wait.Token);
                delay = Math.Min(delay * 2, 500);
                state = await Read(started, wait.Token);
                Assert.AreEqual(started.JobId, state.JobId);
            }
            return (request, state);
        }
        Task<PcbDrcJobState> Read(PcbDrcJobState job, CancellationToken cancellation) =>
            client.InvokeAsync<ReadPcbDrcJob, PcbDrcJobState>(new()
                { Document = board, JobId = job.JobId, ProcessEpoch = client.Epoch }, cancellation);
        static void AssertStale(PcbDrcJobState job, string code, string because)
        {
            Assert.AreEqual(PcbDrcJobStatus.PdrcjsStale, job.Status, because + " " + job.ErrorCode + ": " + job.ErrorMessage);
            Assert.AreEqual(code, job.ErrorCode, because);
            Assert.IsEmpty(job.Findings, because + " A stale check must not expose its old findings.");
            Assert.IsTrue(job.WorkerFinished, because);
            Assert.IsFalse(job.ResultsFresh, because);
            Assert.IsFalse(job.SnapshotComplete, because);
        }
        Task Evidence(string name, PcbDrcJobState state) => File.WriteAllTextAsync(
            Path.Combine(evidence, name), SchematicJson.Formatter.Format(state), token);

        var before = await ObserveLifecycleState(client, board, token);
        var (_, state) = await Run(before);
        await Evidence("pcb-drc-job.json", state);

        Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, state.Status, state.ErrorCode + ": " + state.ErrorMessage);
        Assert.AreEqual(1.0, state.Progress);
        Assert.IsFalse(state.SnapshotComplete, "Ordinary DRC must not claim complete input capture before p23deb822a36256a6.");
        Assert.IsFalse(state.ResultsFresh, "Results cannot be fresh while the input snapshot is incomplete.");
        Assert.IsFalse(state.CancellationRequested);
        Assert.AreEqual(state.Findings.Count, state.Findings.Select(finding => finding.NativeId).Distinct().Count());
        Assert.AreEqual(before, await ObserveLifecycleState(client, board, token),
            "A detached DRC job must not change the live board or its files.");

        // The check goes stale the moment its custom rules change on disk, even when
        // the file is put back before anyone reads the check again: KiCad's own file
        // notification made it stale, not a later comparison of the file contents.
        string rules = Path.Combine(board.Project.Path, Path.ChangeExtension(board.BoardFilename, ".kicad_dru"));
        byte[]? savedRules = File.Exists(rules) ? await File.ReadAllBytesAsync(rules, token) : null;
        try
        {
            await File.WriteAllTextAsync(rules,
                "(version 1)\n(rule \"fixture_event\" (constraint clearance (min 0.3mm)))\n", token);
            // KiCad handles file notifications and API requests on its UI thread and
            // runs ready file notifications before queued requests. The pause only
            // gives a busy UI thread its turn; the assertions below prove the result.
            await ObserveLifecycleState(client, board, token);
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            await ObserveLifecycleState(client, board, token);
        }
        finally
        {
            if (savedRules is null) File.Delete(rules); // Only the rules file this journey created.
            else await File.WriteAllBytesAsync(rules, savedRules, CancellationToken.None);
        }
        var rulesChanged = await Read(state, token);
        await Evidence("pcb-drc-job-rules-stale.json", rulesChanged);
        AssertStale(rulesChanged, "project_inputs_changed",
            "Changing the custom rules file must make the finished check stale, and restoring it must not revive it.");

        var (secondRequest, second) = await Run(await ObserveLifecycleState(client, board, token));
        Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, second.Status, second.ErrorCode + ": " + second.ErrorMessage);
        Assert.AreEqual(state.Findings.Count, second.Findings.Count,
            "A new check of the same board and restored rules must report the same findings.");

        // A committed edit through KiCad's own board transaction makes the check stale.
        var header = new ItemHeader { Document = board };
        var track = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = header, Items = { new KIID { Value = "33333333-3333-4333-8333-333333333333" } } }, token))
            .Items.Single().Unpack<Track>();
        var moved = track.Clone(); moved.End.XNm += 1_000_000;
        var begin = await client.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = header }, token);
        bool committed = false;
        try
        {
            var update = new UpdateItems { Header = header };
            update.Items.Add(Any.Pack(moved));
            var updated = await client.InvokeAsync<UpdateItems, UpdateItemsResponse>(update, token);
            Assert.IsTrue(updated.UpdatedItems.Count == 1
                && updated.UpdatedItems.All(item => item.Status.Code == ItemStatusCode.IscOk), updated.ToString());
            await client.InvokeAsync<EndCommit, EndCommitResponse>(new()
            {
                Id = begin.Id, Action = CommitAction.CmaCommit, Header = header,
                Message = "DRC freshness journey edit"
            }, token);
            committed = true;
        }
        finally
        {
            if (!committed)
                await client.InvokeAsync<EndCommit, EndCommitResponse>(new()
                    { Id = begin.Id, Action = CommitAction.CmaDrop, Header = header }, CancellationToken.None);
        }
        var edited = await Read(second, token);
        await Evidence("pcb-drc-job-edit-stale.json", edited);
        AssertStale(edited, "document_changed", "A committed board edit must make the finished check stale.");

        // An agent reading the same check through the STDIO MCP server sees it stale too.
        string statePath = Directory.CreateTempSubdirectory("kicad-drc-job-mcp-").FullName;
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(statePath,
                Path.Combine(evidence, "pcb-drc-job-mcp.stderr.log"), token);
            string instanceId = (await client.HandshakeAsync(token)).InstanceId;
            var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var attachError) && attachError.GetBoolean(), attached.GetRawText());
            var reply = await mcp.Tool("kicad_pcb_drc_job", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board),
                jobId = second.JobId, processEpoch = client.Epoch
            });
            Assert.IsFalse(reply.TryGetProperty("isError", out var replyError) && replyError.GetBoolean(), reply.GetRawText());
            var observed = SchematicJson.Parser.Parse<PcbDrcJobState>(reply.GetProperty("content").EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
            AssertStale(observed, "document_changed", "An agent reading the check over MCP must see it stale.");
            Assert.AreEqual(edited, observed);
        }
        finally { Directory.Delete(statePath, true); }

        // Reverting replaces the edited board with the saved one. The stale check
        // stays stale, and replaying its start request returns it instead of a new check.
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
        AssertStale(await Read(second, token), "document_changed", "Reloading the board must not revive an older check.");
        var replay = await client.InvokeAsync<StartPcbDrcJob, PcbDrcJobState>(secondRequest, token);
        Assert.AreEqual(second.JobId, replay.JobId);
        AssertStale(replay, "document_changed", "Replaying a start request must return its stale check.");
        var (_, third) = await Run(await ObserveLifecycleState(client, board, token));
        await Evidence("pcb-drc-job-reverted.json", third);
        Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, third.Status, third.ErrorCode + ": " + third.ErrorMessage);
        Assert.AreEqual(state.Findings.Count, third.Findings.Count,
            "A new check of the reloaded board must report the findings of the saved board.");
        Assert.IsFalse(third.SnapshotComplete);
        Assert.IsFalse(third.ResultsFresh);
    }
}
