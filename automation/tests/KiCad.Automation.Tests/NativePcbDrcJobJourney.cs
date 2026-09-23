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
        var before = await ObserveLifecycleState(client, board, token);
        string operation = Guid.NewGuid().ToString("D");
        var state = await client.InvokeAsync<StartPcbDrcJob, PcbDrcJobState>(new()
        {
            Document = board, ProcessEpoch = client.Epoch,
            ExpectedRevision = before.Revision.Clone(), OperationId = operation
        }, token);
        Assert.AreEqual(board, state.Document);
        Assert.AreEqual(operation, state.OperationId);
        Assert.AreEqual(client.Epoch, state.ProcessEpoch);
        Assert.AreEqual(before.Revision, state.CheckedRevision);
        Assert.IsTrue(Guid.TryParseExact(state.JobId, "D", out _));
        Assert.IsFalse(state.CandidateDryRun);
        Assert.IsEmpty(state.CandidateItemIds);
        string job = state.JobId;

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromMinutes(2));
        int delay = 25;
        while (!state.WorkerFinished)
        {
            await Task.Delay(delay, wait.Token);
            delay = Math.Min(delay * 2, 500);
            state = await client.InvokeAsync<ReadPcbDrcJob, PcbDrcJobState>(new()
                { Document = board, JobId = job, ProcessEpoch = client.Epoch }, wait.Token);
            Assert.AreEqual(job, state.JobId);
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "pcb-drc-job.json"),
            SchematicJson.Formatter.Format(state), token);

        Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, state.Status, state.ErrorCode + ": " + state.ErrorMessage);
        Assert.AreEqual(1.0, state.Progress);
        Assert.IsFalse(state.SnapshotComplete, "Ordinary DRC must not claim complete input capture before p23deb822a36256a6.");
        Assert.IsFalse(state.ResultsFresh, "Results cannot be fresh while the input snapshot is incomplete.");
        Assert.IsFalse(state.CancellationRequested);
        Assert.AreEqual(state.Findings.Count, state.Findings.Select(finding => finding.NativeId).Distinct().Count());
        Assert.AreEqual(before, await ObserveLifecycleState(client, board, token),
            "A detached DRC job must not change the live board or its files.");
    }
}
