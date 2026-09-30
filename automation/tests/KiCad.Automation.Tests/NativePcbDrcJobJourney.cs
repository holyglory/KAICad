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
        Task<(StartPcbDrcJob Request, PcbDrcJobState State)> Run(DocumentLifecycleState at) => RunPcbDrcJob(client, board, at, token);
        Task<PcbDrcJobState> Read(PcbDrcJobState job, CancellationToken cancellation) => ReadPcbDrcJobState(client, board, job, cancellation);
        static void AssertStale(PcbDrcJobState job, string code, string because) => AssertPcbDrcJobStale(job, code, because);
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

        // A check of the edited board that is still current when the board is reloaded.
        // The editor detaches it before it frees the edited board, so the check becomes
        // stale for good instead of listening to a board that no longer exists.
        var (_, edit) = await Run(await ObserveLifecycleState(client, board, token));
        await Evidence("pcb-drc-job-edited-board.json", edit);
        Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, edit.Status, edit.ErrorCode + ": " + edit.ErrorMessage);
        Assert.AreEqual(edit, await Read(edit, token), "Nothing changed the edited board before it was reloaded.");

        // Reverting replaces the edited board with the saved one. The stale check
        // stays stale, and replaying its start request returns it instead of a new check.
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
        var detached = await Read(edit, token);
        await Evidence("pcb-drc-job-reload-detached.json", detached);
        AssertStale(detached, "input_events_lost",
            "Reloading the board must detach the check that was still current, not compare it with the new board.");
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

    // Window activation is the PCB editor's checkpoint for changes that reach it without any notification. Here the
    // board's custom rules file is a link to a file in another folder: KiCad watches the folder of the link, so it hears
    // nothing when the linked file changes, and the check says so in its input warnings. Only activating the PCB editor
    // (the person switches back to it) can then make the check stale before an agent reads it again.
    private static async Task VerifyPcbDrcJobActivation(NativeClient client, DocumentSpecifier board, int processId,
        string display, string evidence, CancellationToken token)
    {
        const string uncovered = "Some native input notifications are unavailable; status reads recheck content";
        const string original = "(version 1)\n(rule \"fixture_linked\" (constraint clearance (min 0.3mm)))\n";
        const string changed = "(version 1)\n(rule \"fixture_linked\" (constraint clearance (min 0.4mm)))\n";
        var schematic = (await client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
            new() { Type = DocumentType.DoctypeSchematic }, token)).Documents.Single();
        string rules = Path.Combine(board.Project.Path, Path.ChangeExtension(board.BoardFilename, ".kicad_dru"));
        Task Evidence(string name, PcbDrcJobState state) => File.WriteAllTextAsync(
            Path.Combine(evidence, name), SchematicJson.Formatter.Format(state), token);
        // KiCad handles notifications, activation and API requests on its UI thread. The pause only gives a busy UI thread
        // its turn; the assertions after it prove the result.
        async Task Settle()
        {
            await ObserveLifecycleState(client, board, token);
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            await ObserveLifecycleState(client, board, token);
        }
        // The schematic editor's canvas holds the keyboard focus exactly while its window is the active one.
        async Task SchematicFocus(bool focused, string because)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            int delay = 25;
            try
            {
                while (true)
                {
                    try
                    {
                        var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                            new() { Document = schematic }, limit.Token);
                        if (observation.Preview.Viewport.CanvasHasKeyboardFocus == focused) return;
                    }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(delay, limit.Token);
                    delay = Math.Min(delay * 2, 200);
                }
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !token.IsCancellationRequested)
            {
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-drc-job-activation-focus.png"), token);
                throw new AssertFailedException(because);
            }
        }

        byte[]? saved = File.Exists(rules) ? await File.ReadAllBytesAsync(rules, token) : null;
        string outside = Directory.CreateTempSubdirectory("kicad-drc-activation-").FullName;
        string target = Path.Combine(outside, "linked.kicad_dru");
        bool linked = false;
        try
        {
            if (saved is not null) File.Delete(rules);
            await File.WriteAllTextAsync(target, original, token);
            File.CreateSymbolicLink(rules, target);
            linked = true;
            await Settle(); // The link's own creation notification runs before the checks start.

            // Without an activation, a change that reached no notification and was put back before the read is not
            // seen: the check's inputs are again exactly what it checked.
            var (_, control) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token);
            await Evidence("pcb-drc-job-activation-control.json", control);
            Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, control.Status, control.ErrorCode + ": " + control.ErrorMessage);
            CollectionAssert.Contains(control.InputWarnings.ToArray(), uncovered,
                "A check whose rules file is a link must say that notifications do not cover all of its inputs.");
            await File.WriteAllTextAsync(target, changed, token);
            await Settle();
            await File.WriteAllTextAsync(target, original, token);
            var unobserved = await ReadPcbDrcJobState(client, board, control, token);
            Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, unobserved.Status,
                "Nothing but a read observed the linked rules, and they were put back first. " + unobserved.ErrorCode + ": " + unobserved.ErrorMessage);
            Assert.AreEqual(control.Findings.Count, unobserved.Findings.Count);

            // The same change, now with the PCB editor activated while the linked rules are changed: the person moves
            // to the schematic editor and back.
            var (_, check) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token);
            Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, check.Status, check.ErrorCode + ": " + check.ErrorMessage);
            CollectionAssert.Contains(check.InputWarnings.ToArray(), uncovered);
            await File.WriteAllTextAsync(target, changed, token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false); // The schematic canvas background.
            await SchematicFocus(true, "The schematic editor did not become the active window.");
            NativeKeyboard.SchematicShortcut(display, processId, "motion", "PCB Editor", false, false);
            await SchematicFocus(false, "The PCB editor did not become the active window again.");
            await Settle();
            await File.WriteAllTextAsync(target, original, token);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-drc-job-activation.png"), token);
            var activated = await ReadPcbDrcJobState(client, board, check, token);
            await Evidence("pcb-drc-job-activation-stale.json", activated);
            AssertPcbDrcJobStale(activated, "project_inputs_changed",
                "Activating the PCB editor after its linked rules changed must make the check stale before any read, and putting the rules back must not revive it.");
        }
        finally
        {
            if (linked) File.Delete(rules); // Only the link this step created.
            if (saved is not null) await File.WriteAllBytesAsync(rules, saved, CancellationToken.None);
            Directory.Delete(outside, true);
        }
    }

    // Starts an ordinary PCB DRC job at the given revision and reads it until its worker finished.
    private static async Task<(StartPcbDrcJob Request, PcbDrcJobState State)> RunPcbDrcJob(NativeClient client,
        DocumentSpecifier board, DocumentLifecycleState at, CancellationToken token)
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
            state = await ReadPcbDrcJobState(client, board, started, wait.Token);
            Assert.AreEqual(started.JobId, state.JobId);
        }
        return (request, state);
    }

    private static Task<PcbDrcJobState> ReadPcbDrcJobState(NativeClient client, DocumentSpecifier board, PcbDrcJobState job,
        CancellationToken token) => client.InvokeAsync<ReadPcbDrcJob, PcbDrcJobState>(new()
            { Document = board, JobId = job.JobId, ProcessEpoch = client.Epoch }, token);

    private static void AssertPcbDrcJobStale(PcbDrcJobState job, string code, string because)
    {
        Assert.AreEqual(PcbDrcJobStatus.PdrcjsStale, job.Status, because + " " + job.ErrorCode + ": " + job.ErrorMessage);
        Assert.AreEqual(code, job.ErrorCode, because);
        Assert.IsEmpty(job.Findings, because + " A stale check must not expose its old findings.");
        Assert.IsTrue(job.WorkerFinished, because);
        Assert.IsFalse(job.ResultsFresh, because);
        Assert.IsFalse(job.SnapshotComplete, because);
    }
}
