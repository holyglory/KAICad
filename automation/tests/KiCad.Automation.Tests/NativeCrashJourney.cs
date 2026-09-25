using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private sealed record CrashKiCad(string Id, int ProcessId, string Epoch, NativeClient Client, DocumentSpecifier Document,
        Process? Owned = null)
    {
        // When it was killed, which names its log copied to the evidence.
        public string? Moment { get; set; }
    }

    // What the record, the XML and the native files held when KiCad was killed right after committing an XML edit.
    private sealed record CrashCommitKill(StoredDesignRecovery Held, byte[] Edit, SchematicCommitNotification Committed,
        Dictionary<string, (byte[] Bytes, DateTime Written)> Disk, JsonElement Exit, bool SaveJournaled);

    // What the record held when KiCad was killed inside the checked save, where strace stopped it, and what it left behind.
    private sealed record CrashSaveKill(StoredDesignRecovery Held, byte[] Edit, SchematicCommitNotification Committed,
        CheckedSaveDocument Save, JsonElement Exit, string StoppedAt, string[] LeftBehind);

    // Shared PSU/CPU acceptance journey (psu-cpu-fixture-and-ownership.md §1.9; ledger pcb5513dd69eda714, Linux part):
    // a KiCad that dies mid-session leaves the XML, the recovery record and the instance list truthful.
    //
    // The compiled MCP server starts KiCad on copies of this instance's S1 "Sheets" project and synchronizes each copy
    // automatically with PSU/CPU XML. This instance's own fixture KiCad is attached to the same server as the other KiCad:
    // it is never killed and must stay untouched. KiCad is killed (SIGKILL) at three moments:
    //   1. idle, with everything synchronized (first copy);
    //   2. during an XML apply, right after KiCad committed the edit and before anything was saved or published: KiCad is
    //      held (SIGSTOP) as soon as its commit notification arrives (first copy);
    //   3. during the apply's checked save, after the root and PSU sheets were replaced and before the CPU sheet was: strace
    //      holds KiCad at the flush of the CPU sheet's new version (second copy).
    // Each time the MCP server reports the instance as exited with exit status 137 (the status reported for signal 9,
    // SIGKILL) and answers instance_exited for it, and the synchronization that was applying pauses with instance_exited.
    // The XML is byte-identical to the version last published or, while an apply was pending, to the version the recovery
    // record names as that operation's starting point, with the operation stated exactly: the native edit KiCad committed
    // (its operation ID), the XML it publishes, and the save KiCad was cut off in. The recovery record survives unchanged.
    // A fresh KiCad for the same project continues the instance ID with a new epoch (started through the MCP server, or
    // started like a user would and attached) and loads exactly what the killed KiCad had saved, never an edit twice; the
    // killed process keeps its own logs. After the idle kill the record is reattached and automatic synchronization
    // resumes. After the two mid-apply kills the record keeps the operation, and neither reattachment nor resuming the
    // operation replays it on the fresh process. A request waiting on an attached KiCad fails soon after it is killed.
    private static Task VerifyPsuCpuNativeCrash(NativeClient client, PsuCpuNativeContext context, Process native, int processId,
        string display, string evidence, string instanceId, CancellationToken token) =>
        RunPsuCpuNativeCrash(client, context, native, processId, evidence, instanceId, release: false, token);

    // The same crash fixture, for the operations the mid-apply kills leave pending (decision nd2e75380e7f8aa7f). While the
    // killed KiCad is only held, kicad_design_recovery_release_exited refuses: its exit is not proven. After the kill it
    // releases the operation, keeping a receipt of the whole operation. Once KiCad runs again it recognises what that
    // KiCad loaded sheet by sheet, and in one step attaches the record to it and journals the continuation, which then
    // completes like any pending synchronization:
    //   1. killed right after the commit: released before KiCad runs again (nothing continues yet), then resumed once it
    //      runs; automatic synchronization completes the released operation itself, and every unit is placed exactly once;
    //   2. killed during the save after the root and PSU sheets were replaced: released and resumed in one call; the agent
    //      completes it with kicad_design_sync_apply; only the CPU sheet's part is applied, so U3 and U4, which KiCad loaded
    //      from the replaced PSU sheet, are not doubled;
    //   3. killed the same way and released before KiCad runs again: while the KiCad started again holds U3 and U4 from
    //      the replaced PSU sheet, an ordinary kicad_design_recovery_reattach is refused and changes nothing, so the
    //      partial result can never be synchronized as a user edit; then rolled back: U3 and U4 are removed, the XML is
    //      the synchronized baseline the record holds and the same design as the XML published before the edit, the edit
    //      is kept as the previous XML, and making the edit again applies it once.
    // The baseline, desired XML and last completed synchronization are unchanged by every release itself.
    private static Task VerifyPsuCpuExitedOperationRelease(NativeClient client, PsuCpuNativeContext context, Process native,
        int processId, string display, string evidence, string instanceId, CancellationToken token) =>
        RunPsuCpuNativeCrash(client, context, native, processId, evidence, instanceId, release: true, token);

    private static async Task RunPsuCpuNativeCrash(NativeClient client, PsuCpuNativeContext context, Process native, int processId,
        string evidence, string instanceId, bool release, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux process signals, /proc and strace.");
        var clock = Stopwatch.StartNew();
        string mode = release ? "release" : "crash";
        void Phase(string text) => Console.WriteLine($"Native {mode} {instanceId} at {clock.Elapsed.TotalSeconds:F1}s: {text}");
        string Evidence(string name) => Path.Combine(evidence, instanceId + "-" + mode + "-" + name);
        _ = context.Baseline ?? throw new AssertFailedException("The S1 seed must provide a native baseline.");

        // The other KiCad: this instance's fixture KiCad, which prepared the seed. Nothing may change it.
        var other = await client.HandshakeAsync(token);
        Assert.AreEqual((uint)processId, other.ProcessId, "KiCad names its own process in the handshake.");
        var otherFiles = DesignFileHashes(context.ProjectDirectory);

        string work = Path.Combine(Path.GetDirectoryName(context.ProjectDirectory)!, mode + "-" + instanceId[..8]);
        var (executable, environment) = await FixtureKiCadLaunch(processId, Path.Combine(work, "settings"), token);
        var cases = new List<object>();
        SchematicDesign? placed = null;
        var started = new HashSet<string>(StringComparer.Ordinal);
        var owned = new List<Process>();
        var failure = false;
        await using var mcp = await CancellableMcpClient.StartAsync(Path.Combine(work, "mcp-state"), Evidence("mcp.stderr.log"), token, environment);
        try
        {
            var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
            RequireToolSuccess(attached);
            Assert.AreEqual(processId, attached.GetProperty("structuredContent").GetProperty("processId").GetInt32());
            Assert.AreEqual("running", attached.GetProperty("structuredContent").GetProperty("processState").GetString(),
                "The server observes the attached KiCad by the process it names in its handshake.");
            if (release) await ReleaseJourney();
            else await CrashJourney();

            // ---- The other KiCad was never touched -------------------------------------------------------------------
            Assert.AreEqual(other.Epoch, (await client.HandshakeAsync(token)).Epoch, "The other KiCad kept its process.");
            Assert.IsFalse(native.HasExited, "The other KiCad is still running.");
            CollectionAssert.AreEquivalent(otherFiles.ToArray(), DesignFileHashes(context.ProjectDirectory).ToArray(),
                "The other KiCad's design files are unchanged.");
            await File.WriteAllTextAsync(Evidence("result.json"), JsonSerializer.Serialize(new
            {
                instanceId, otherEpoch = other.Epoch, otherProcess = processId, cases, seconds = clock.Elapsed.TotalSeconds
            }, new JsonSerializerOptions { WriteIndented = true }), token);
            Phase("every assertion passed");
        }
        catch { failure = true; throw; }
        finally
        {
            foreach (var process in owned)
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync(CancellationToken.None);
                process.Dispose();
            }
            foreach (string id in started)
            {
                try { await StopStartedKiCad(id); }
                catch (Exception) when (failure) { }
                string runtime = NativeIpcEndpoint.RuntimeDirectory(id);
                if (Directory.Exists(runtime)) Directory.Delete(runtime, true);
            }
        }

        // ---- The three kills ----------------------------------------------------------------------------------------------

        async Task CrashJourney()
        {
            // ---- First copy: idle, then after the native commit of an XML apply ---------------------------------------
            var first = await Prepare("first");
            var kicad = first.KiCad;
            await ApplyEdit(first, kicad, 2, "J1 and U1");
            var idle = first.Store.Read()!;
            byte[] published = await File.ReadAllBytesAsync(first.Design, token);
            Assert.IsFalse(idle.State.HasPendingWork);
            Assert.AreEqual(Sha(published), idle.State.LastSynchronization!.DesignFileSha256, "The XML is the version last published.");
            CollectionAssert.AreEqual(published, idle.State.DesiredFileBytes);
            var idleDisk = Disk(first);
            var idleExit = await Kill(kicad, "idle");
            CollectionAssert.AreEqual(published, await File.ReadAllBytesAsync(first.Design, token),
                "After KiCad died idle the XML is byte-identical to the version last published.");
            Assert.AreEqual(idle.RevisionToken, first.Store.Read()!.RevisionToken, "The recovery record survives unchanged.");
            RequireDisk(first, idleDisk, "idle kill");
            // Nothing was applying: the worker learns of the exit only when it next needs KiCad or its events fall silent.
            var idleBeforeStop = await SynchronizationStatus(kicad, first.Session);
            var idleStop = await StopSynchronization(kicad, first.Session);
            kicad = await Start(first, "after the idle kill", kicad);
            RequirePlaced(await Capture(kicad), 2, "fresh KiCad after the idle kill");
            await Reattach(first, kicad);
            first.Session = await StartSynchronization(first, kicad, "after the idle kill");
            RequirePlaced(await Capture(kicad), 2, "resumed after the idle kill");
            RequirePublished(first, "resumed after the idle kill");
            await ApplyEdit(first, kicad, 4, "R1 and U2 after the idle kill");
            cases.Add(new { moment = "idle", exit = idleExit, recoveryRevision = idle.RevisionToken, xmlSha256 = Sha(published),
                automaticSynchronizationBeforeStop = idleBeforeStop, automaticSynchronizationAtStop = idleStop, resumedEpoch = kicad.Epoch,
                resumedPlacements = Expected(4).Length });

            var commit = await KillAfterCommit(first, kicad, 6, "after-commit");
            var commitPending = commit.Held.State.PendingPublication!;
            var commitPaused = await RequireKilledOperation(first, kicad, commit.Held, commit.Edit, commit.Disk, "after-commit");
            var commitStop = await StopSynchronization(kicad, first.Session);
            // A KiCad started again for the project like a user would, with the same instance ID, attached to the server.
            kicad = await StartAttached(first, "after the commit kill", kicad);
            // Only saved work survives a kill: the committed but unsaved U3 and U4 are gone; nothing is doubled.
            RequirePlaced(await Capture(kicad), 4, "fresh KiCad after the commit kill");
            await RequireNotReplayed(first, kicad, commit.Held, commit.Edit, 4, "after-commit");
            var attachedExit = await KillAttached(kicad, "attached");
            cases.Add(new { moment = "after-native-commit", exit = commit.Exit, operationId = commitPending.OperationId,
                nativeOperationId = commit.Committed.Change.OperationId, committedRevision = commit.Committed.Revision.Sequence,
                pendingProcessEpoch = commit.Held.State.PendingNativeState!.ProcessEpoch, recoveryRevision = commit.Held.RevisionToken,
                xmlSha256 = Sha(commit.Edit), candidateSha256 = Sha(commitPending.CandidateFileBytes), candidatePlacements = Expected(6),
                saveJournaled = commit.SaveJournaled, automaticSynchronizationPaused = commitPaused, automaticSynchronizationAtStop = commitStop,
                restartedAttachedEpoch = kicad.Epoch, restartedPlacements = Expected(4).Length, attachedExit });

            // ---- Second copy: during the checked save ------------------------------------------------------------------
            var second = await Prepare("second");
            kicad = second.KiCad;
            await ApplyEdit(second, kicad, 4, "J1, U1, R1 and U2");
            var save = await KillDuringSave(second, kicad, 8, "during-save");
            var savePending = save.Held.State.PendingPublication!;
            var savePaused = await RequireKilledOperation(second, kicad, save.Held, save.Edit, null, "during-save");
            var saveStop = await StopSynchronization(kicad, second.Session);
            kicad = await Start(second, "after the save kill", kicad);
            // The fresh KiCad loads the half-written save: U3 and U4 once on the replaced PSU sheet, no U5 or U6.
            RequirePlaced(await Capture(kicad), 6, "fresh KiCad after the save kill");
            await RequireNotReplayed(second, kicad, save.Held, save.Edit, 6, "during-save");
            cases.Add(new { moment = "during-checked-save", exit = save.Exit, operationId = savePending.OperationId,
                nativeOperationId = save.Committed.Change.OperationId, committedRevision = save.Committed.Revision.Sequence,
                nativeSaveOperationId = save.Save.OperationId, nativeSaveRevision = save.Save.ExpectedState.Revision.Sequence,
                recoveryRevision = save.Held.RevisionToken, xmlSha256 = Sha(save.Edit), candidateSha256 = Sha(savePending.CandidateFileBytes),
                candidatePlacements = Expected(8), stoppedAt = save.StoppedAt, replacedBeforeKill = new[] { "fixture.kicad_sch", "psu.kicad_sch" },
                leftBehind = save.LeftBehind, automaticSynchronizationPaused = savePaused,
                automaticSynchronizationAtStop = saveStop, restartedEpoch = kicad.Epoch, restartedPlacements = Expected(6).Length });
        }

        // ---- Releasing the operations the mid-apply kills left pending ------------------------------------------------

        async Task ReleaseJourney()
        {
            // ---- First copy: killed right after the commit; released before KiCad runs again, then resumed ----------------
            // In the KiCad started again the user draws a wire and writes a note on CPU_POWER, a sheet the operation (U3 and
            // U4 on PSU) does not change. The wire is refused with the way out; deleted again, the resume keeps the note,
            // saves and publishes it, and a later edit of the note is synchronized like any user edit.
            var first = await Prepare("first");
            var kicad = first.KiCad;
            await ApplyEdit(first, kicad, 4, "J1, U1, R1 and U2");
            var commit = await KillAfterCommit(first, kicad, 6, "after-commit");
            var killed = kicad;
            await RequireKilledOperation(first, kicad, commit.Held, commit.Edit, commit.Disk, "after-commit");
            await StopSynchronization(kicad, first.Session);
            var wrong = await mcp.Tool("kicad_design_recovery_release_exited", new { instanceId = killed.Id, recoveryPath = first.Store.StatePath,
                expectedRevisionToken = commit.Held.RevisionToken, operationId = Guid.NewGuid().ToString("D"), continuation = "resume" });
            Assert.AreEqual("released_operation_mismatch", Error(wrong), wrong.GetRawText());
            RequireKept(first, commit.Held, commit.Edit, "after-commit, another operation ID refused");
            var releasedOnly = await Release(first, killed, commit.Held, "resume", null, "after-commit, before KiCad runs again");
            kicad = await Start(first, "after the commit kill", killed);
            RequirePlaced(await Capture(kicad), 4, "fresh KiCad after the commit kill");
            string power = SheetPath(first, 4);
            string noteId = Guid.NewGuid().ToString("D"), wireId = Guid.NewGuid().ToString("D");
            string noteText = "User note on CPU_POWER " + noteId[..8];
            await UserEdit(first, kicad, 4, "a note and a wire on CPU_POWER",
                UserNote(noteId, noteText, 25_400_000, 25_400_000), UserWire(wireId, 25_400_000, 50_800_000, 76_200_000, 50_800_000));
            var notCarried = await RequireRefusedAfterRelease(first, killed, kicad, commit.Held, "resume", "released_operation_edits_not_carried",
                false, "after-commit, a wire on CPU_POWER", "added wire " + wireId, "sheet CPU_POWER (sheet path " + power + ")", "undo those edits");
            await UserEdit(first, kicad, 4, "the wire deleted again, the way out the refusal names", UserRemoval(wireId));
            var resumedCommit = await Release(first, killed, commit.Held, "resume", kicad, "after-commit");
            Assert.AreEqual(0, resumedCommit.GetProperty("sheetsWithOperationResult").GetArrayLength(), "Nothing of the operation was saved.");
            CollectionAssert.AreEqual(new[] { SheetPath(first, 2) }, ViewPaths(resumedCommit, "operationSheets"),
                $"The operation changes only PSU (U3 and U4), not CPU_POWER. {resumedCommit.GetRawText()}");
            CollectionAssert.AreEqual(new[] { power }, ViewPaths(resumedCommit, "sheetsWithOtherEdits"), resumedCommit.GetRawText());
            var commitCandidate = RequireResumed(first, kicad, commit.Held, resumedCommit, "after-commit", noteId);
            RequirePlaced(await Capture(kicad), 4, "after-commit: the release itself applies nothing");
            RequireNote(await Capture(kicad), power, noteId, noteText, "after-commit: the release itself keeps the note in KiCad");
            first.Session = await StartSynchronization(first, kicad, "resuming the operation left by the commit kill");
            RequireCompleted(first, commit.Held.State.PendingPublication!.OperationId, commitCandidate, await Capture(kicad), 6, "after-commit resumed");
            await RequireKeptNote(first, kicad, power, first.PowerFile, noteId, noteText, "after-commit resumed");
            Assert.AreEqual("completed", (await Repeat(first, commit.Held, "resume")).GetProperty("outcome").GetString());
            // After the continuation the note is an ordinary part of the design: the user's next edit of it is synchronized.
            string editedText = noteText + " (edited after the continuation)";
            var resumedOperation = first.Store.Read()!.State.LastSynchronization!.OperationId;
            var current = NoteIn(await Capture(kicad), power, noteId);
            current.Text.Text_ = editedText;
            await UserEdit(first, kicad, 4, "the note on CPU_POWER edited", new SchematicItemOperation { Update = Any.Pack(current) });
            await WaitSynchronization(kicad, first.Session, await SynchronizationStatus(kicad, first.Session), "the note edit to be synchronized",
                status => Settled(first, status) && first.Store.Read()!.State.LastSynchronization?.OperationId is { } done && done != resumedOperation);
            RequirePublished(first, "the note edited after the continuation");
            RequirePlaced(await Capture(kicad), 6, "the note edited after the continuation");
            await RequireKeptNote(first, kicad, power, first.PowerFile, noteId, editedText, "the note edited after the continuation");
            cases.Add(new { moment = "after-native-commit", operationId = commit.Held.State.PendingPublication.OperationId,
                killedEpoch = killed.Epoch, exit = commit.Exit, releasedBeforeRestart = releasedOnly, wireOnUntouchedSheetRefused = notCarried,
                resumed = resumedCommit, keptNote = new { sheet = power, noteId, noteText, editedText },
                completedOperation = resumedOperation, noteEditSynchronizedBy = first.Store.Read()!.State.LastSynchronization!.OperationId,
                placements = Expected(6).Length });

            // ---- Second copy: killed during the save after the root and PSU sheets were replaced; resumed ------------------
            // Before the release a note is written on the CPU sheet, which the operation changes: the release happens, both
            // continuations are refused with the way out, the reply names the new revision token and the receipt, and
            // deleting the note lets the resume go ahead.
            var second = await Prepare("second");
            kicad = second.KiCad;
            await ApplyEdit(second, kicad, 4, "J1, U1, R1 and U2");
            var save = await KillDuringSave(second, kicad, 8, "during-save");
            killed = kicad;
            await RequireKilledOperation(second, kicad, save.Held, save.Edit, null, "during-save");
            await StopSynchronization(kicad, second.Session);
            kicad = await Start(second, "after the save kill", killed);
            RequirePlaced(await Capture(kicad), 6, "fresh KiCad after the save kill");
            string cpu = SheetPath(second, 3), cpuNote = Guid.NewGuid().ToString("D");
            await UserEdit(second, kicad, 3, "a note on CPU, which the operation changes", UserNote(cpuNote, "User note on CPU " + cpuNote[..8], 25_400_000, 25_400_000));
            var editedResume = await RequireRefusedAfterRelease(second, killed, kicad, save.Held, "resume", "released_operation_sheets_edited", true,
                "during-save, a note on CPU", "sheet CPU (sheet path " + cpu + ")", "undo those edits", "close the schematic without saving");
            var editedRollback = await RequireRefusedAfterRelease(second, killed, kicad, save.Held, "roll-back", "released_operation_sheets_edited", false,
                "during-save, a note on CPU, roll-back", "sheet CPU (sheet path " + cpu + ")", "undo those edits");
            await UserEdit(second, kicad, 3, "the note on CPU deleted again, the way out the refusal names", UserRemoval(cpuNote));
            var resumedSave = await Release(second, killed, save.Held, "resume", kicad, "during-save");
            RequirePartial(second, resumedSave, "during-save");
            Assert.AreEqual(0, resumedSave.GetProperty("sheetsWithOtherEdits").GetArrayLength(), resumedSave.GetRawText());
            var saveCandidate = RequireResumed(second, kicad, save.Held, resumedSave, "during-save", null);
            RequirePlaced(await Capture(kicad), 6, "during-save: the release itself applies nothing");
            // The agent completes the continuation itself, with the operation ID and request token the release returned.
            var applied = await mcp.Tool("kicad_design_sync_apply", new { instanceId = kicad.Id, recoveryPath = second.Store.StatePath,
                designPath = second.Design, expectedRevisionToken = resumedSave.GetProperty("requestedRecoveryRevisionToken").GetString(),
                operationId = resumedSave.GetProperty("continuationOperationId").GetString() });
            RequireToolSuccess(applied);
            var appliedView = applied.GetProperty("structuredContent").Clone();
            Assert.AreEqual(save.Held.State.PendingPublication!.OperationId.ToString("D"), appliedView.GetProperty("operationId").GetString(), appliedView.GetRawText());
            Assert.IsTrue(appliedView.GetProperty("synchronizationCommitted").GetBoolean(), appliedView.GetRawText());
            Assert.IsTrue(appliedView.GetProperty("nativeMutationCommitted").GetBoolean(), "U5 and U6 were applied on the running KiCad.");
            Assert.IsTrue(appliedView.GetProperty("nativeFilesSaved").GetBoolean(), appliedView.GetRawText());
            Assert.IsFalse(appliedView.GetProperty("replayed").GetBoolean(), appliedView.GetRawText());
            Assert.AreEqual(Sha(saveCandidate), appliedView.GetProperty("designFileSha256").GetString(), "It published the continued candidate.");
            // U3 and U4 came from the replaced PSU sheet; only U5 and U6 were added: every unit exactly once.
            RequireCompleted(second, save.Held.State.PendingPublication.OperationId, saveCandidate, await Capture(kicad), 8, "during-save resumed");
            Assert.IsFalse((await Capture(kicad)).State.NativeContentDirty, "during-save: the completed sheets are saved.");
            Assert.AreEqual("completed", (await Repeat(second, save.Held, "resume")).GetProperty("outcome").GetString());
            cases.Add(new { moment = "during-checked-save", operationId = save.Held.State.PendingPublication.OperationId,
                killedEpoch = killed.Epoch, exit = save.Exit, stoppedAt = save.StoppedAt, operationSheetEditRefused = editedResume,
                operationSheetEditRollbackRefused = editedRollback, resumed = resumedSave, completedBy = appliedView,
                completedOperation = second.Store.Read()!.State.LastSynchronization!.OperationId, placements = Expected(8).Length });

            // ---- Third copy: killed during the save the same way; released, reattachment refused, rolled back ----------
            // In the KiCad started again the user writes a note on the root sheet, which the operation does not change: the
            // roll-back removes only the operation's partial result and keeps the note.
            var third = await Prepare("third");
            kicad = third.KiCad;
            await ApplyEdit(third, kicad, 4, "J1, U1, R1 and U2");
            // The XML published by the last synchronization before the edit, and the synchronized design the record holds.
            byte[] synchronizedXml = await File.ReadAllBytesAsync(third.Design, token);
            var rolled = await KillDuringSave(third, kicad, 8, "roll-back-during-save");
            killed = kicad;
            await RequireKilledOperation(third, kicad, rolled.Held, rolled.Edit, null, "roll-back-during-save");
            await StopSynchronization(kicad, third.Session);
            var releasedRollback = await Release(third, killed, rolled.Held, "roll-back", null, "roll-back, before KiCad runs again");
            kicad = await Start(third, "after the save kill to roll back", killed);
            RequirePlaced(await Capture(kicad), 6, "fresh KiCad after the save kill to roll back");
            var refusedReattach = await RequireReattachRefused(third, kicad, "roll-back");
            string rootSheet = SheetPath(third, 1), rootNote = Guid.NewGuid().ToString("D");
            string rootText = "User note on the root sheet " + rootNote[..8];
            await UserEdit(third, kicad, 1, "a note on the root sheet", UserNote(rootNote, rootText, 25_400_000, 25_400_000));
            var rollback = await Release(third, killed, rolled.Held, "roll-back", kicad, "roll-back");
            Assert.AreEqual("roll-back-pending", rollback.GetProperty("outcome").GetString(), rollback.GetRawText());
            RequirePartial(third, rollback, "roll-back");
            CollectionAssert.DoesNotContain(ViewPaths(rollback, "operationSheets"), rootSheet, "The operation does not change the root sheet.");
            CollectionAssert.AreEqual(new[] { rootSheet }, ViewPaths(rollback, "sheetsWithOtherEdits"), rollback.GetRawText());
            var rollbackId = Guid.Parse(rollback.GetProperty("continuationOperationId").GetString()!);
            Assert.AreNotEqual(rolled.Held.State.PendingPublication!.OperationId, rollbackId, "The roll-back is its own operation.");
            var journaled = third.Store.Read()!;
            var restore = journaled.State.PendingPublication!;
            Assert.AreEqual(rollbackId, restore.OperationId);
            Assert.AreEqual(kicad.Epoch, journaled.State.PendingNativeState!.ProcessEpoch, "The roll-back runs on the KiCad started again.");
            CollectionAssert.AreEqual(rolled.Edit, restore.ExpectedFileBytes, "The roll-back replaces the XML that holds the edit.");
            CollectionAssert.AreEqual(Expected(4), Placements(restore.CandidateFileBytes), "It publishes the last synchronized design.");
            Assert.AreEqual(journaled.State.PendingMutation!.Operations.Count, rollback.GetProperty("nativeOperations").GetInt32());
            Assert.IsGreaterThan(0, rollback.GetProperty("nativeOperations").GetInt32(), "U3 and U4 are removed from the PSU sheet.");
            Assert.IsTrue(journaled.State.PendingMutation.Operations.All(operation => operation.TargetDocument is { } target
                && SheetKey(target) != rootSheet), "The roll-back changes nothing on the root sheet, which holds the user's note.");
            RequirePlaced(await Capture(kicad), 6, "roll-back: the release itself changes nothing in KiCad");
            third.Session = await StartSynchronization(third, kicad, "rolling back the operation left by the save kill");
            var back = await Capture(kicad);
            RequirePlaced(back, 4, "rolled back: U3 and U4 removed, everything else once");
            Assert.IsFalse(back.State.NativeContentDirty, "The rolled-back sheets are saved.");
            string psu = await File.ReadAllTextAsync(third.Psu, token);
            Assert.IsFalse(psu.Contains("\"U3\"", StringComparison.Ordinal) || psu.Contains("\"U4\"", StringComparison.Ordinal),
                "The saved PSU sheet no longer holds the operation's U3 and U4.");
            var restored = third.Store.Read()!;
            Assert.AreEqual(rollbackId, restored.State.LastSynchronization!.OperationId, "The roll-back completed.");
            byte[] rolledXml = await File.ReadAllBytesAsync(third.Design, token);
            CollectionAssert.AreEqual(restore.CandidateFileBytes, rolledXml, "The XML is the roll-back's candidate.");
            await RequireKeptNote(third, kicad, rootSheet, third.Root, rootNote, rootText, "rolled back");
            // The last synchronized design: exactly the baseline the record held when KiCad was killed (apart from the sheet
            // files' load-format version, which the KiCad started again read anew, and the root sheet, which is as the KiCad
            // started again holds it: the same objects plus the user's note), and the same design as the XML that
            // synchronization published: every part but the native sheets byte for byte, and no native object differs.
            // That XML is not byte-identical: it kept the synchronization's candidate sheets, while the baseline committed
            // with it records the sheets as KiCad reported them after saving (the evidence names the first difference).
            byte[] baselineXml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(rolled.Held.State.Baseline, []));
            Assert.AreEqual(WithoutLoadProvenance(baselineXml), WithoutLoadProvenance(WithSheet(rolledXml, rootSheet, baselineXml)),
                "Apart from the root sheet, the XML is exactly the synchronized baseline the record held.");
            byte[] rolledWithoutNote = WithoutItems(rolledXml, rootNote);
            RequireSameDesign(baselineXml, rolledWithoutNote, "rolled back, apart from the user's note: the synchronized baseline");
            RequireSameDesign(synchronizedXml, rolledWithoutNote, "rolled back, apart from the user's note: the XML published before the edit");
            RequirePublished(third, "rolled back");
            // The XML the roll-back replaced, with the edit that started the operation, is kept in the design's sync history.
            var kept = RetainedXmlHistory.Inspect(restored.State.LastSynchronization);
            Assert.IsTrue(kept.ContentVerified == true && kept.Path is not null, $"The replaced XML is kept: {kept}");
            CollectionAssert.AreEqual(rolled.Edit, await File.ReadAllBytesAsync(kept.Path!, token), "The edit that started the operation is kept.");
            string previous = kept.Path!;
            Assert.AreEqual("rolled-back", (await Repeat(third, rolled.Held, "roll-back")).GetProperty("outcome").GetString());
            // Making the edit again applies it once, from the restored design, and the note stays.
            await ApplyEdit(third, kicad, 8, "all eight components again after the roll-back");
            await RequireKeptNote(third, kicad, rootSheet, third.Root, rootNote, rootText, "all eight components again after the roll-back");
            cases.Add(new { moment = "roll-back-during-checked-save", operationId = rolled.Held.State.PendingPublication.OperationId,
                killedEpoch = killed.Epoch, exit = rolled.Exit, releasedBeforeRestart = releasedRollback, ordinaryReattach = refusedReattach,
                rolledBack = rollback, rollbackOperation = rollbackId, previousXml = previous, keptNote = new { sheet = rootSheet, rootNote, rootText },
                rolledBackXmlSha256 = Sha(rolledXml), synchronizedXmlSha256 = Sha(synchronizedXml),
                byteIdenticalApartFromLoadFormat = WithoutLoadProvenance(synchronizedXml) == WithoutLoadProvenance(rolledWithoutNote),
                firstDifferenceFromPublished = FirstDifference(WithoutLoadProvenance(synchronizedXml), WithoutLoadProvenance(rolledWithoutNote)),
                appliedAgainBy = third.Store.Read()!.State.LastSynchronization!.OperationId, placements = Expected(8).Length });
        }

        // ---- Steps ------------------------------------------------------------------------------------------------------

        // Holds KiCad (SIGSTOP) as soon as it notifies the commit of the next XML edit, checks what the record states, and
        // kills it: nothing was saved or published.
        async Task<CrashCommitKill> KillAfterCommit(CrashCopy copy, CrashKiCad target, int components, string moment)
        {
            var disk = Disk(copy);
            var committing = await Capture(target);
            byte[] edit;
            SchematicCommitNotification committed;
            using (var events = new NativeEventSubscription(await target.Client.HandshakeAsync(token)))
            {
                // The first packet proves the subscription is connected before the edit is written.
                await NextEvent(events, _ => true, "the first native notification");
                edit = await WriteEdit(copy, components);
                committed = await NextCommit(events, committing, "KiCad to commit the XML edit");
                Assert.AreEqual(0, SignalProcess(target.ProcessId, SigStop), "KiCad could not be held.");
                await WaitUntil(() => ProcessState(target.ProcessId) == 'T', "KiCad to stop after its commit", token);
                Phase($"KiCad held right after commit {committed.Revision.Sequence}");
            }
            var held = copy.Store.Read()!;
            var pending = held.State.PendingPublication
                ?? throw new AssertFailedException("The apply journals its publication before KiCad commits the edit.");
            RequireCommitted(held, committed, components, moment);
            Assert.AreEqual(target.Epoch, held.State.PendingNativeState!.ProcessEpoch, "The operation names the process that holds it.");
            Assert.AreEqual(DesignPublicationPhase.Prepared, pending.Phase, "Nothing was published.");
            CollectionAssert.AreEqual(edit, pending.ExpectedFileBytes, "The operation starts from the edited XML.");
            CollectionAssert.AreEqual(edit, await File.ReadAllBytesAsync(copy.Design, token));
            RequireDisk(copy, disk, "held after the commit");
            // The apply may already have journaled its save request; KiCad, held, had not started it (no file was rewritten).
            bool saveJournaled = held.State.PendingNativeSave is not null;
            if (held.State.PendingNativeSave is { } journaledSave)
                RequireSaveOf(journaledSave, held, committed, target, disk, copy, moment);
            if (release) await RequireReleaseRefused(copy, target, held, moment);
            var exit = await Kill(target, moment);
            return new(held, edit, committed, disk, exit, saveJournaled);
        }

        // strace holds KiCad at the flush of the CPU sheet's new version inside the apply's checked save, after the root and
        // PSU sheets were replaced; the record is checked and KiCad killed there.
        async Task<CrashSaveKill> KillDuringSave(CrashCopy copy, CrashKiCad target, int components, string moment)
        {
            var disk = Disk(copy);
            StoredDesignRecovery held;
            byte[] edit;
            string[] trace;
            string[] temporary;
            JsonElement exit;
            SchematicCommitNotification committed;
            var saving = await Capture(target);
            using (var events = new NativeEventSubscription(await target.Client.HandshakeAsync(token)))
            await using (var fault = await SyscallFault.AttachAsync(target.ProcessId, Evidence(moment + ".strace"), token,
                "-e", "trace=fsync", "-e", "inject=fsync:signal=SIGSTOP:when=5"))
            {
                await NextEvent(events, _ => true, "the first native notification");
                edit = await WriteEdit(copy, components);
                // KiCad commits the edit before it saves, so the notification arrives before strace holds the save.
                committed = await NextCommit(events, saving, "KiCad to commit the XML edit before saving it");
                await WaitLonger(() => IsPaused(target.ProcessId), "KiCad to stop inside the synchronization's save", 90);
                held = copy.Store.Read()!;
                temporary = Directory.GetFiles(copy.Project, "*.kicad-save-*");
                if (release) await RequireReleaseRefused(copy, target, held, moment);
                exit = await Kill(target, moment);
                trace = await fault.DetachAsync();
            }
            var pending = held.State.PendingPublication
                ?? throw new AssertFailedException("The apply journals its publication before KiCad saves.");
            var save = held.State.PendingNativeSave
                ?? throw new AssertFailedException("KiCad was saving, so the record holds the exact save request.");
            RequireCommitted(held, committed, components, moment);
            RequireSaveOf(save, held, committed, target, disk, copy, moment);
            Assert.AreEqual(DesignPublicationPhase.Prepared, pending.Phase, "Nothing was published.");
            CollectionAssert.AreEqual(edit, pending.ExpectedFileBytes);
            // The trace shows where KiCad stopped: flushing the CPU sheet's new version, not yet in place.
            string stoppedAt = trace.Last(line => line.StartsWith("fsync(", StringComparison.Ordinal));
            StringAssert.Contains(stoppedAt, "cpu.kicad_sch.kicad-save-" + target.ProcessId + "-",
                "KiCad must stop while writing the CPU sheet: " + string.Join('\n', trace));
            CollectionAssert.AreEqual(new[] { copy.Cpu }, temporary.Select(path => path[..path.IndexOf(".kicad-save-", StringComparison.Ordinal)]).ToArray(),
                "KiCad was stopped with exactly the CPU sheet's new version not yet in place.");
            RequireDisk(copy, disk, "save stopped part way", replaced: [copy.Root, copy.Psu]);
            StringAssert.Contains(await File.ReadAllTextAsync(copy.Psu, token), "\"U4\"", "The replaced PSU sheet holds U3 and U4.");
            Assert.IsFalse((await File.ReadAllTextAsync(copy.Cpu, token)).Contains("\"U5\"", StringComparison.Ordinal),
                "The CPU sheet in place is the previous version, without U5.");
            return new(held, edit, committed, save, exit, stoppedAt, temporary.Select(path => Path.GetFileName(path)).ToArray());
        }

        // While the KiCad holding the operation is only stopped, its exit is not proven: the release changes nothing and keeps
        // no receipt, and needs no reply from the stopped KiCad to say so.
        async Task RequireReleaseRefused(CrashCopy copy, CrashKiCad target, StoredDesignRecovery held, string moment)
        {
            var refused = await mcp.Tool("kicad_design_recovery_release_exited", new { instanceId = target.Id, recoveryPath = copy.Store.StatePath,
                expectedRevisionToken = held.RevisionToken, operationId = held.State.PendingPublication!.OperationId.ToString("D"), continuation = "resume" });
            Assert.AreEqual("operation_exit_unproven", Error(refused), $"{moment}: {refused.GetRawText()}");
            StringAssert.Contains(refused.GetProperty("structuredContent").GetProperty("errorMessage").GetString(), target.Epoch, refused.GetRawText());
            Assert.AreEqual(held.RevisionToken, copy.Store.Read()!.RevisionToken, $"{moment}: a refused release changes nothing.");
            Assert.IsFalse(Directory.Exists(DesignReleasedOperations.Directory(copy.Store.StatePath)), $"{moment}: a refused release keeps no receipt.");
            Phase($"{moment}: releasing the operation of the held KiCad {target.ProcessId} is refused");
        }

        // Releases the operation the killed KiCad left pending (when the record still holds it) and continues it on the
        // running KiCad, if any. The receipt keeps the whole operation exactly as the record held it when KiCad was killed;
        // the baseline, desired XML and last completed synchronization are unchanged.
        async Task<JsonElement> Release(CrashCopy copy, CrashKiCad killed, StoredDesignRecovery held, string continuation, CrashKiCad? running,
            string moment)
        {
            var before = copy.Store.Read()!;
            var pending = held.State.PendingPublication!;
            var reply = await mcp.Tool("kicad_design_recovery_release_exited", new { instanceId = killed.Id, recoveryPath = copy.Store.StatePath,
                expectedRevisionToken = before.RevisionToken, operationId = pending.OperationId.ToString("D"), continuation });
            RequireToolSuccess(reply);
            var view = reply.GetProperty("structuredContent").Clone();
            Assert.AreEqual(before.State.HasPendingWork, view.GetProperty("releasedNow").GetBoolean(), $"{moment}: {view.GetRawText()}");
            Assert.AreEqual(killed.Epoch, view.GetProperty("releasedEpoch").GetString(), view.GetRawText());
            RequireKilled(view.GetProperty("exit"), killed);
            var receipt = DesignReleasedOperations.Read(view.GetProperty("receiptPath").GetString()!);
            Assert.AreEqual(pending.OperationId, receipt.OperationId);
            Assert.AreEqual(held.RevisionToken, receipt.ReleasedFromRevisionToken, $"{moment}: released from exactly the record KiCad left.");
            Assert.AreEqual(held.State.PendingMutation!.OperationId, receipt.NativeOperationId);
            Assert.AreEqual(held.State.PendingMutation, receipt.Mutation(), $"{moment}: the receipt keeps the native edit KiCad committed.");
            Assert.AreEqual(held.State.PendingNativeState, receipt.NativeState(), $"{moment}: the receipt keeps the operation's native state.");
            Assert.AreEqual(held.State.PendingNativeSave, receipt.NativeSave(), $"{moment}: the receipt keeps the save KiCad was cut off in.");
            CollectionAssert.AreEqual(pending.ExpectedFileBytes, receipt.PendingPublication!.ExpectedFileBytes);
            CollectionAssert.AreEqual(pending.CandidateFileBytes, receipt.PendingPublication.CandidateFileBytes);
            Assert.AreEqual(killed.Epoch, receipt.Exit.Epoch);
            var after = copy.Store.Read()!;
            Assert.AreEqual(view.GetProperty("recoveryRevisionToken").GetString(), after.RevisionToken);
            Assert.AreEqual(SchematicDesignXml.Write(held.State.Baseline, []), SchematicDesignXml.Write(after.State.Baseline, []),
                $"{moment}: releasing never advances the baseline.");
            CollectionAssert.AreEqual(held.State.DesiredFileBytes, after.State.DesiredFileBytes, $"{moment}: the desired XML is unchanged.");
            Assert.AreEqual(held.State.LastSynchronization!.OperationId, after.State.LastSynchronization!.OperationId, moment);
            Assert.AreEqual(held.State.LastSynchronization.DesignFileSha256, after.State.LastSynchronization.DesignFileSha256, moment);
            if (running is null)
            {
                Assert.AreEqual("released", view.GetProperty("outcome").GetString(), view.GetRawText());
                Assert.AreEqual(JsonValueKind.Null, view.GetProperty("continuedEpoch").ValueKind, view.GetRawText());
                Assert.IsFalse(after.State.HasPendingWork, $"{moment}: the operation is released.");
                StringAssert.Contains(view.GetProperty("nextStep").GetString(), "kicad_instance_start");
            }
            else Assert.AreEqual(running.Epoch, view.GetProperty("continuedEpoch").GetString(), view.GetRawText());
            Phase($"{moment}: the operation of killed KiCad {killed.ProcessId} released, outcome {view.GetProperty("outcome").GetString()}");
            return view;
        }

        // The rest of the same operation now waits on the running KiCad, as a new native edit of that process; the killed
        // process's request is never sent again. Its candidate is the operation's candidate with the sheet files' load-format
        // version as the running KiCad read them, and with the sheet that holds a kept note exactly as the running KiCad
        // holds it: the same objects plus the note. Returns that candidate.
        byte[] RequireResumed(CrashCopy copy, CrashKiCad running, StoredDesignRecovery held, JsonElement view, string moment, string? keptNote)
        {
            Assert.AreEqual("resume-pending", view.GetProperty("outcome").GetString(), view.GetRawText());
            var journaled = copy.Store.Read()!;
            var pending = held.State.PendingPublication!;
            Assert.AreEqual(pending.OperationId.ToString("D"), view.GetProperty("continuationOperationId").GetString(), $"{moment}: the same operation continues.");
            Assert.IsTrue(view.GetProperty("continuationPending").GetBoolean(), view.GetRawText());
            Assert.AreEqual(pending.OperationId, journaled.State.PendingPublication!.OperationId, moment);
            CollectionAssert.AreEqual(pending.ExpectedFileBytes, journaled.State.PendingPublication.ExpectedFileBytes, moment);
            byte[] continued = journaled.State.PendingPublication.CandidateFileBytes;
            if (keptNote is null)
                Assert.AreEqual(WithoutLoadProvenance(pending.CandidateFileBytes), WithoutLoadProvenance(continued),
                    $"{moment}: the candidate is the operation's own, apart from the files' load-format version.");
            else
            {
                string sheet = ViewPaths(view, "sheetsWithOtherEdits").Single();
                Assert.AreEqual(WithoutLoadProvenance(pending.CandidateFileBytes), WithoutLoadProvenance(WithSheet(continued, sheet, pending.CandidateFileBytes)),
                    $"{moment}: apart from the sheet with the kept note, the candidate is the operation's own.");
                RequireSameDesign(pending.CandidateFileBytes, WithoutItems(continued, keptNote), $"{moment}: apart from the kept note, the candidate is the operation's own");
                _ = NoteIn(SchematicDesignXml.Read(Encoding.UTF8.GetString(continued), []).Schematic, sheet, keptNote);
            }
            Assert.AreEqual(running.Epoch, journaled.State.PendingNativeState!.ProcessEpoch, $"{moment}: it continues on the running KiCad.");
            Assert.IsNull(journaled.State.PendingNativeSave, $"{moment}: the killed process's save is not reused.");
            Assert.AreEqual(pending.RequestedRecoveryRevisionToken, view.GetProperty("requestedRecoveryRevisionToken").GetString());
            var rest = journaled.State.PendingMutation ?? throw new AssertFailedException($"{moment}: part of the operation is still missing in KiCad.");
            Assert.AreEqual(rest.Operations.Count, view.GetProperty("nativeOperations").GetInt32());
            Assert.AreNotEqual(held.State.PendingMutation!.OperationId, rest.OperationId,
                $"{moment}: the rest is a new native edit of the running process, never the killed process's request replayed.");
            return journaled.State.PendingPublication.CandidateFileBytes;
        }

        // Automatic synchronization completed the continued operation on the running KiCad and published its candidate.
        void RequireCompleted(CrashCopy copy, Guid operation, byte[] candidate, CheckedSchematicState state, int components, string moment)
        {
            var done = copy.Store.Read()!;
            Assert.AreEqual(operation, done.State.LastSynchronization!.OperationId, $"{moment}: the released operation itself completed.");
            CollectionAssert.AreEqual(candidate, File.ReadAllBytes(copy.Design), $"{moment}: the XML is exactly the continued candidate.");
            RequirePublished(copy, moment);
            RequirePlaced(state, components, moment + ": every unit exactly once");
        }

        // Calling the release again reports what became of the operation instead of repeating anything.
        async Task<JsonElement> Repeat(CrashCopy copy, StoredDesignRecovery held, string continuation)
        {
            var before = copy.Store.Read()!;
            var reply = await mcp.Tool("kicad_design_recovery_release_exited", new { instanceId = copy.KiCad.Id, recoveryPath = copy.Store.StatePath,
                expectedRevisionToken = before.RevisionToken, operationId = held.State.PendingPublication!.OperationId.ToString("D"), continuation });
            RequireToolSuccess(reply);
            var view = reply.GetProperty("structuredContent").Clone();
            Assert.IsFalse(view.GetProperty("releasedNow").GetBoolean(), view.GetRawText());
            Assert.AreEqual(before.RevisionToken, copy.Store.Read()!.RevisionToken, "A repeated release changes nothing.");
            return view;
        }

        // The PSU sheet the interrupted save had replaced holds the operation's result; the CPU sheet it had not reached does
        // not. The save also rewrote the root sheet, with the same bytes (its content did not change), so the receipt
        // counts it as unchanged: a replaced file is one whose content differs from the version the save started from.
        void RequirePartial(CrashCopy copy, JsonElement view, string moment)
        {
            CollectionAssert.AreEquivalent(new[] { "psu.kicad_sch" },
                view.GetProperty("replacedFiles").EnumerateArray().Select(file => Path.GetFileName(file.GetString())).ToArray(), $"{moment}: {view.GetRawText()}");
            CollectionAssert.IsSubsetOf(new[] { "fixture.kicad_sch", "cpu.kicad_sch", "cpu_power.kicad_sch" },
                view.GetProperty("unchangedFiles").EnumerateArray().Select(file => Path.GetFileName(file.GetString())).ToArray(), view.GetRawText());
            var sheets = ViewPaths(view, "sheetsWithOperationResult");
            CollectionAssert.Contains(sheets, SheetPath(copy, 2), $"{moment}: the replaced PSU sheet holds the operation's U3 and U4. {view.GetRawText()}");
            CollectionAssert.DoesNotContain(sheets, SheetPath(copy, 3), $"{moment}: the CPU sheet was not reached. {view.GetRawText()}");
            // The operation changes PSU, CPU and CPU_POWER (U3 and U4, U5 and U6, U5's power unit), not the root sheet.
            CollectionAssert.AreEquivalent(new[] { SheetPath(copy, 2), SheetPath(copy, 3), SheetPath(copy, 4) }, ViewPaths(view, "operationSheets"), view.GetRawText());
        }

        // A sheet of a copy by its fixture sheet instance ordinal (1 root, 2 PSU, 3 CPU, 4 CPU_POWER), as a native sheet path.
        string SheetPath(CrashCopy copy, int ordinal) => string.Join('/', copy.Store.Read()!.State.Baseline.SheetBindings
            .Single(binding => binding.SheetInstanceId == PsuCpuIds.Id(0x05, ordinal)).NativePath.Select(id => id.ToString("D")));

        // An edit made in the KiCad started again, as a user makes one: a native commit of that KiCad that is not the recovery
        // record's own, sent through the public checked-batch tool against the exact state KiCad holds.
        async Task UserEdit(CrashCopy copy, CrashKiCad target, int sheet, string what, params SchematicItemOperation[] operations)
        {
            var state = await Capture(target);
            string path = SheetPath(copy, sheet);
            var document = state.Electrical.Hierarchy.Data.Instances.Single(screen => SheetKey(screen.Metadata.Document) == path).Metadata.Document;
            var batch = new ApplySchematicItemBatch { Document = state.State.Document.Clone(), DocumentEpoch = state.State.Revision.Epoch,
                ExpectedRevision = state.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), OriginId = UserOrigin,
                Description = "User edit: " + what };
            foreach (var operation in operations)
            {
                var targeted = operation.Clone();
                targeted.TargetDocument = document.Clone();
                batch.Operations.Add(targeted);
            }
            var reply = await mcp.Tool("kicad_schematic_apply_checked_batch", new { instanceId = target.Id,
                requestJson = SchematicJson.Formatter.Format(new CheckedSchematicBatch { Batch = batch, ExpectedState = state.State.Clone() }) });
            RequireToolSuccess(reply);
            Phase($"user edit in KiCad {target.ProcessId}: {what}");
        }

        // A continuation refused once the operation was released: by this call (releasedNow, when the record still held the
        // operation) or an earlier one. The reply names the way out, the release receipt and the record's current revision
        // token to call again with, and never claims that nothing changed when this call released the operation. The record
        // stays on the ended KiCad's session; KiCad and the XML file are untouched.
        async Task<JsonElement> RequireRefusedAfterRelease(CrashCopy copy, CrashKiCad killed, CrashKiCad running, StoredDesignRecovery held,
            string continuation, string code, bool releasedNow, string moment, params string[] mentions)
        {
            var before = copy.Store.Read()!;
            Assert.AreEqual(releasedNow, before.State.HasPendingWork, $"{moment}: the record still holds the operation only when this call releases it.");
            var native = await Capture(running);
            var reply = await mcp.Tool("kicad_design_recovery_release_exited", new { instanceId = killed.Id, recoveryPath = copy.Store.StatePath,
                expectedRevisionToken = before.RevisionToken, operationId = held.State.PendingPublication!.OperationId.ToString("D"), continuation });
            Assert.AreEqual(code, Error(reply), $"{moment}: {reply.GetRawText()}");
            var view = reply.GetProperty("structuredContent").Clone();
            var after = copy.Store.Read()!;
            Assert.AreEqual(releasedNow, view.GetProperty("releasedNow").GetBoolean(), view.GetRawText());
            Assert.AreEqual(killed.Epoch, view.GetProperty("releasedEpoch").GetString(), view.GetRawText());
            Assert.AreEqual(after.RevisionToken, view.GetProperty("recoveryRevisionToken").GetString(), $"{moment}: the reply names the record's current revision token.");
            if (releasedNow) Assert.AreNotEqual(before.RevisionToken, after.RevisionToken, $"{moment}: this call released the operation.");
            else Assert.AreEqual(before.RevisionToken, after.RevisionToken, $"{moment}: a refusal after an earlier release changes nothing.");
            Assert.IsFalse(after.State.HasPendingWork, $"{moment}: the operation stays released.");
            Assert.AreEqual(held.State.NativeRevision.Epoch, after.State.NativeRevision.Epoch, $"{moment}: the record stays on the ended KiCad's session.");
            string receiptPath = view.GetProperty("receiptPath").GetString()!;
            var receipt = DesignReleasedOperations.Read(receiptPath);
            Assert.AreEqual(held.RevisionToken, receipt.ReleasedFromRevisionToken, $"{moment}: the receipt is of exactly the record KiCad left.");
            Assert.AreEqual(killed.Epoch, receipt.Exit.Epoch);
            string message = view.GetProperty("errorMessage").GetString()!;
            foreach (string text in mentions.Concat([receiptPath, after.RevisionToken, releasedNow ? "was released by this call" : "stays released"]))
                StringAssert.Contains(message, text, $"{moment}: {message}");
            Assert.IsFalse(message.Contains("othing was changed", StringComparison.Ordinal), $"{moment}: {message}");
            if (releasedNow) Assert.IsFalse(message.Contains("This call changed nothing:", StringComparison.Ordinal), $"{moment}: {message}");
            CollectionAssert.AreEqual(held.State.DesiredFileBytes, await File.ReadAllBytesAsync(copy.Design, token), $"{moment}: the XML file is untouched.");
            var unchanged = await Capture(running);
            Assert.AreEqual(native.State.Revision, unchanged.State.Revision, $"{moment}: KiCad is untouched.");
            Assert.AreEqual(native.Electrical.Hierarchy.Data, unchanged.Electrical.Hierarchy.Data, $"{moment}: KiCad is untouched.");
            Phase($"{moment}: {continuation} refused with {code}, the operation released {(releasedNow ? "by this call" : "earlier")}");
            return view;
        }

        // KiCad, the XML published last and the saved sheet file hold the user's note exactly; KiCad has saved it.
        async Task RequireKeptNote(CrashCopy copy, CrashKiCad target, string sheet, string file, string id, string text, string when)
        {
            var state = await Capture(target);
            RequireNote(state, sheet, id, text, when + ": KiCad keeps the note");
            Assert.IsFalse(state.State.NativeContentDirty, $"{when}: KiCad saved the note with the continuation's sheets.");
            var published = SchematicDesignXml.Read(await File.ReadAllTextAsync(copy.Design, token), []);
            Assert.AreEqual(text, NoteIn(published.Schematic, sheet, id).Text.Text_, $"{when}: the published XML holds the note on its sheet.");
            Assert.AreEqual(NoteIn(state.Electrical.Hierarchy.Data, sheet, id), NoteIn(published.Schematic, sheet, id),
                $"{when}: the XML holds the note exactly as KiCad does.");
            StringAssert.Contains(await File.ReadAllTextAsync(file, token), text, $"{when}: the saved sheet file holds the note.");
        }

        // A copy of the S1 project, opened by a KiCad the MCP server starts, saved once in KiCad's own form, with recovery at
        // the S1 baseline and automatic synchronization watching it.
        async Task<CrashCopy> Prepare(string name)
        {
            string project = Directory.CreateDirectory(Path.Combine(work, name)).FullName;
            foreach (string file in new[] { "fixture.kicad_pro", "fixture.kicad_sch", "psu.kicad_sch", "cpu.kicad_sch", "cpu_power.kicad_sch" })
                File.Copy(Path.Combine(context.ProjectDirectory, file), Path.Combine(project, file));
            var copy = new CrashCopy(project, new DesignRecoveryStore(Path.Combine(work, name + ".recovery.json")));
            var kicad = await Start(copy, name + " copy", null);
            var lifecycle = await ObserveLifecycleState(kicad.Client, kicad.Document, token);
            RequireToolSuccess(await mcp.Tool("kicad_document_save", new { instanceId = kicad.Id,
                expectedStateJson = SchematicJson.Formatter.Format(lifecycle), operationId = Guid.NewGuid().ToString("D") }));
            var seeded = await Capture(kicad);
            Assert.IsFalse(seeded.State.NativeContentDirty, "The copied S1 seed is saved by KiCad before synchronization.");
            PsuCpuFixture.RequireSeedHierarchy(seeded.Electrical.Hierarchy.Data, PsuCpuSeed.Sheets, context.RootInstanceId);
            var baseline = PsuCpuFixture.Baseline(seeded.Electrical.Hierarchy.Data, PsuCpuSeed.Sheets, context.RootInstanceId, token);
            var seed = context with { ProjectDirectory = project, Root = kicad.Document, Baseline = baseline, DesignPath = copy.Design };
            byte[] baselineBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, []));
            await File.WriteAllBytesAsync(copy.Design, baselineBytes, token);
            var recovery = await PsuCpuFixture.InitializeRecoveryAsync(kicad.Client, seed, copy.Store.StatePath, token);
            // Placements for all eight components, proposed once by the public layout tool (both copies have the same
            // sheets). Each edit adds the next components with these placements to the XML KiCad last published.
            if (placed is null)
            {
                await LayoutComponents(copy, kicad, seed, seeded);
                await CommitCandidate(copy, kicad, baselineBytes);
            }
            copy.KiCad = kicad;
            copy.Session = await StartSynchronization(copy, kicad, name + " copy");
            Phase($"automatic synchronization watches the {name} copy");
            return copy;
        }

        async Task<CrashKiCad> Start(CrashCopy copy, string label, CrashKiCad? previous)
        {
            var reply = await mcp.Tool("kicad_instance_start", new { executable, projectPath = copy.ProjectFile });
            RequireToolSuccess(reply);
            var view = reply.GetProperty("structuredContent");
            string id = view.GetProperty("instanceId").GetString()!;
            started.Add(id);
            Assert.AreEqual("running", view.GetProperty("processState").GetString(), reply.GetRawText());
            if (previous is null) Assert.AreEqual(JsonValueKind.Null, view.GetProperty("replaces").ValueKind, reply.GetRawText());
            else
            {
                RequireContinues(view, previous, reply);
                // The same instance ID reuses its runtime folder; the killed process's log moved aside unchanged, and the
                // fresh process writes its own.
                string runtime = NativeIpcEndpoint.RuntimeDirectory(id);
                string kept = Path.Combine(runtime, "epochs", previous.Epoch, "native.log");
                Assert.IsTrue(File.Exists(kept), $"The killed KiCad's log is kept under its epoch: {kept}");
                CollectionAssert.AreEqual(await File.ReadAllBytesAsync(Evidence("native-" + previous.Moment + ".log"), token),
                    await File.ReadAllBytesAsync(kept, token), "The fresh KiCad neither appended to nor replaced the killed KiCad's log.");
                Assert.AreEqual(view.GetProperty("epoch").GetString(), (await File.ReadAllTextAsync(Path.Combine(runtime, "logs.epoch"), token)).Trim(),
                    "The runtime folder's current logs are the fresh process's.");
            }
            return await Open(copy, id, view.GetProperty("processId").GetInt32(), view.GetProperty("epoch").GetString()!, label, null);
        }

        // Like a user starting KiCad again for the project with the same automation identity, then attaching it.
        async Task<CrashKiCad> StartAttached(CrashCopy copy, string label, CrashKiCad previous)
        {
            string socket = Path.Combine(NativeIpcEndpoint.RuntimeDirectory(previous.Id), "api.sock");
            var start = new ProcessStartInfo(executable) { WorkingDirectory = copy.Project, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var (name, value) in environment) start.Environment[name] = value;
            foreach (string argument in new[] { "--new", "--automation", previous.Id, "--api-socket", socket,
                         "--automation-log", Evidence("attached-native.log"), "--software-rendering", copy.ProjectFile })
                start.ArgumentList.Add(argument);
            var process = Process.Start(start)!;
            owned.Add(process);
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
            string endpoint = NativeIpcEndpoint.FromSocketPath(socket);
            JsonElement reply;
            using (var ready = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                ready.CancelAfter(TimeSpan.FromSeconds(45));
                while (true)
                {
                    Assert.IsFalse(process.HasExited, "The KiCad started again exited; see " + Evidence("attached-native.log"));
                    reply = await mcp.Tool("kicad_instance_attach", new { endpoint, expectedInstanceId = previous.Id });
                    if (Error(reply) is not ("native_transport_unavailable" or "native_status_4" or "native_status_7")) break;
                    await Task.Delay(200, ready.Token);
                }
            }
            RequireToolSuccess(reply);
            var view = reply.GetProperty("structuredContent");
            Assert.AreEqual(previous.Id, view.GetProperty("instanceId").GetString());
            Assert.AreEqual(process.Id, view.GetProperty("processId").GetInt32(), "The server records the process KiCad names in its handshake.");
            Assert.AreEqual("running", view.GetProperty("processState").GetString(), reply.GetRawText());
            RequireContinues(view, previous, reply);
            return await Open(copy, previous.Id, process.Id, view.GetProperty("epoch").GetString()!, label, process);
        }

        void RequireContinues(JsonElement view, CrashKiCad previous, JsonElement reply)
        {
            Assert.AreEqual(previous.Id, view.GetProperty("instanceId").GetString(), "KiCad started again continues the killed instance's ID.");
            Assert.AreNotEqual(previous.Epoch, view.GetProperty("epoch").GetString(), "The fresh KiCad is a new process epoch.");
            var replaces = view.GetProperty("replaces");
            Assert.AreEqual(previous.Epoch, replaces.GetProperty("epoch").GetString(), reply.GetRawText());
            Assert.AreEqual(previous.ProcessId, replaces.GetProperty("processId").GetInt32(), reply.GetRawText());
            RequireKilled(replaces.GetProperty("exit"), previous);
        }

        async Task<CrashKiCad> Open(CrashCopy copy, string id, int pid, string epoch, string label, Process? process)
        {
            var endpoint = NativeIpcEndpoint.FromSocketPath(Path.Combine(NativeIpcEndpoint.RuntimeDirectory(id), "api.sock"));
            var direct = new NativeClient(new NngTransport(), endpoint, epoch);
            var session = await direct.HandshakeAsync(token);
            Assert.AreEqual(id, session.InstanceId);
            Assert.AreEqual((uint)pid, session.ProcessId);
            JsonElement opened;
            using (var ready = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                ready.CancelAfter(TimeSpan.FromSeconds(30));
                while (true)
                {
                    opened = await mcp.Tool("kicad_schematic_open", new { instanceId = id, path = copy.Root });
                    if (Error(opened) is not ("native_status_4" or "native_status_7")) break;
                    await Task.Delay(200, ready.Token);
                }
            }
            RequireToolSuccess(opened);
            var document = SchematicJson.Parser.Parse<DocumentSpecifier>(Text(opened));
            Assert.AreEqual(context.RootInstanceId.ToString("D"), document.SheetPath.Path.Single().Value);
            Phase($"KiCad {label}: process {pid}, epoch {epoch}");
            return new(id, pid, epoch, direct, document, process);
        }

        async Task<JsonElement> Kill(CrashKiCad target, string moment)
        {
            Assert.AreEqual(0, SignalProcess(target.ProcessId, 9), "KiCad could not be killed.");
            target.Moment = moment;
            var exit = await ReportedExit(target, moment);
            RequireKilled(exit, target);
            return exit;
        }

        // A KiCad this test started itself and the server only attached: the server proves the exit by the process it
        // recorded no longer existing, without an exit status.
        async Task<JsonElement> KillAttached(CrashKiCad target, string moment)
        {
            // KiCad is held first, so a request to it waits for a reply that cannot come. The operating system tells
            // only KiCad's parent (this test) when it ends; the server checks the process it recorded while the
            // request waits, so the request fails soon after the kill instead of after its 15 s reply timeout.
            Assert.AreEqual(0, SignalProcess(target.ProcessId, SigStop), "KiCad could not be held.");
            await WaitUntil(() => ProcessState(target.ProcessId) == 'T', "the attached KiCad to stop", token);
            var (_, waiting) = mcp.StartTool("kicad_instance_inspect", new { instanceId = target.Id });
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            Assert.IsFalse(waiting.IsCompleted, "A request to the held KiCad waits for its reply.");
            var sinceKill = Stopwatch.StartNew();
            Assert.AreEqual(0, SignalProcess(target.ProcessId, 9), "KiCad could not be killed.");
            var answer = await waiting.WaitAsync(TimeSpan.FromSeconds(60), token);
            double waited = sinceKill.Elapsed.TotalSeconds;
            Assert.IsFalse(answer.TryGetProperty("error", out var protocolError), protocolError.ToString());
            var failed = answer.GetProperty("result");
            Assert.AreEqual("instance_exited", Error(failed), failed.GetRawText());
            Assert.IsLessThan(5.0, waited, $"The waiting request failed {waited:F1}s after the kill, not after its reply timeout.");
            Phase($"a request waiting on the attached KiCad failed {waited:F2}s after the kill");
            await target.Owned!.WaitForExitAsync(token);
            Assert.AreEqual(137, target.Owned.ExitCode, "The attached KiCad was killed by signal 9.");
            var exit = await ReportedExit(target, moment);
            Assert.AreEqual("process-absent", exit.GetProperty("evidence").GetString(), exit.GetRawText());
            // The instance list leaves out values it does not have.
            Assert.IsFalse(exit.TryGetProperty("exitCode", out var status) && status.ValueKind != JsonValueKind.Null,
                "Only the parent of a process learns its exit status: " + exit.GetRawText());
            Assert.AreEqual(target.ProcessId, exit.GetProperty("processId").GetInt32());
            return JsonSerializer.SerializeToElement(new { exit, waitingRequestFailedAfterSeconds = waited });
        }

        // What follows is the MCP server's own proof of the exit, never an answer from KiCad.
        async Task<JsonElement> ReportedExit(CrashKiCad target, string moment)
        {
            JsonElement listed, otherListed;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(20));
                while (true)
                {
                    using var list = JsonDocument.Parse(Text(await mcp.Tool("kicad_instances_list", new { })));
                    var views = list.RootElement.EnumerateArray().ToArray();
                    listed = views.Single(view => view.GetProperty("instanceId").GetString() == target.Id).Clone();
                    otherListed = views.Single(view => view.GetProperty("instanceId").GetString() == instanceId).Clone();
                    if (listed.GetProperty("processState").GetString() == "exited") break;
                    await Task.Delay(100, limit.Token);
                }
            }
            Assert.AreEqual(target.Epoch, listed.GetProperty("epoch").GetString());
            Assert.AreEqual(target.ProcessId, listed.GetProperty("processId").GetInt32());
            Assert.AreEqual(target.Epoch, listed.GetProperty("exit").GetProperty("epoch").GetString());
            Assert.AreEqual("running", otherListed.GetProperty("processState").GetString(), "The other KiCad is observed still running.");
            Assert.AreEqual(other.Epoch, otherListed.GetProperty("epoch").GetString());
            var refused = await mcp.Tool("kicad_instance_inspect", new { instanceId = target.Id });
            Assert.AreEqual("instance_exited", Error(refused), refused.GetRawText());
            string message = refused.GetProperty("structuredContent").GetProperty("message").GetString()!;
            foreach (string text in new[] { target.Id, target.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "changes it had not saved are gone", "kicad_instance_start" })
                StringAssert.Contains(message, text, message);
            RequireToolSuccess(await mcp.Tool("kicad_instance_inspect", new { instanceId }));
            Assert.AreEqual(other.Epoch, (await client.HandshakeAsync(token)).Epoch, "The other KiCad kept its process epoch.");
            Assert.IsFalse(native.HasExited, "The other KiCad is still running.");
            // A KiCad the server started logs into its runtime folder; one this test started logs to the evidence already.
            string log = Path.Combine(NativeIpcEndpoint.RuntimeDirectory(target.Id), "native.log");
            if (target.Owned is null) File.Copy(log, Evidence("native-" + moment + ".log"), true);
            Phase($"KiCad {target.ProcessId} killed {moment}; the MCP server reports it exited");
            return listed.GetProperty("exit").Clone();
        }

        // The worker that was applying when KiCad died pauses with instance_exited; the recovery record, the XML and the
        // native files are exactly as they were while KiCad was held.
        async Task<JsonElement> RequireKilledOperation(CrashCopy copy, CrashKiCad target, StoredDesignRecovery held, byte[] xml,
            Dictionary<string, (byte[] Bytes, DateTime Written)>? disk, string moment)
        {
            var paused = await WaitSynchronization(target, copy.Session, await SynchronizationStatus(target, copy.Session), "the worker to pause",
                status => status.GetProperty("phase").GetString() == "Paused");
            Assert.AreEqual("instance_exited", paused.GetProperty("errorCode").GetString(), paused.GetRawText());
            Assert.AreEqual(held.State.PendingPublication!.OperationId.ToString("D"), paused.GetProperty("operationId").GetString(),
                $"{moment}: the worker paused on exactly the operation the recovery record keeps. {paused.GetRawText()}");
            StringAssert.Contains(paused.GetProperty("errorMessage").GetString(), target.Id, paused.GetRawText());
            RequireKept(copy, held, xml, moment);
            if (disk is not null) RequireDisk(copy, disk, moment);
            return paused;
        }

        // The next commit KiCad notifies after a captured state: the edit the synchronization applied.
        async Task<SchematicCommitNotification> NextCommit(NativeEventSubscription events, CheckedSchematicState before, string what) =>
            (await NextEvent(events, delivery => delivery.Event.SchematicCommit is { } change
                && change.Revision.Epoch == before.State.Revision.Epoch && change.Revision.Sequence > before.State.Revision.Sequence, what))
                .Event.SchematicCommit;

        // The recovery record names exactly the edit KiCad committed (the native batch's own operation ID, which the
        // commit notification carries, and this record's origin), and the XML the pending operation publishes adds
        // exactly the next components.
        void RequireCommitted(StoredDesignRecovery held, SchematicCommitNotification committed, int components, string moment)
        {
            var mutation = held.State.PendingMutation
                ?? throw new AssertFailedException($"{moment}: the recovery record holds the native edit KiCad committed.");
            Assert.AreEqual(mutation.OperationId, committed.Change.OperationId, $"{moment}: the record's native edit is the one KiCad committed.");
            Assert.AreEqual(held.State.OriginId.ToString("D"), committed.Change.OriginId, $"{moment}: KiCad attributes the commit to this design.");
            CollectionAssert.AreEqual(Expected(components), Placements(held.State.PendingPublication!.CandidateFileBytes),
                $"{moment}: the operation publishes exactly the components of the edit, each unit once.");
        }

        // The save request in the record is the save of exactly that commit, by the killed process, of the files as
        // they were on disk before it: the save KiCad was cut off in.
        void RequireSaveOf(CheckedSaveDocument save, StoredDesignRecovery held, SchematicCommitNotification committed, CrashKiCad killed,
            Dictionary<string, (byte[] Bytes, DateTime Written)> disk, CrashCopy copy, string moment)
        {
            Assert.IsTrue(Guid.TryParseExact(save.OperationId, "D", out _), $"{moment}: the save has its own operation ID: {save}");
            Assert.AreEqual(held.State.PendingMutation!.Document, save.Document, $"{moment}: the save is of the edited design.");
            Assert.AreEqual(killed.Epoch, save.ExpectedState.ProcessEpoch, $"{moment}: the save request names the process that ran it.");
            Assert.AreEqual(committed.Revision, save.ExpectedState.Revision, $"{moment}: the save is of exactly the revision KiCad committed.");
            Assert.IsTrue(save.ExpectedState.NativeContentDirty, $"{moment}: the save is of the unsaved edit.");
            foreach (string file in new[] { copy.Root, copy.Psu, copy.Cpu })
            {
                var baseline = save.ExpectedState.FileBaselines.Single(b => Path.GetFileName(b.Path) == Path.GetFileName(file));
                Assert.AreEqual(Sha(disk[file].Bytes), baseline.BaselineSha256.ToLowerInvariant(),
                    $"{moment}: the save expected {Path.GetFileName(file)} as it was on disk before the save.");
            }
        }

        void RequireKept(CrashCopy copy, StoredDesignRecovery held, byte[] xml, string moment)
        {
            var now = copy.Store.Read()!;
            Assert.AreEqual(held.RevisionToken, now.RevisionToken, $"{moment}: the recovery record survives unchanged.");
            Assert.AreEqual(held.State.PendingPublication!.OperationId, now.State.PendingPublication!.OperationId,
                $"{moment}: the record still states the pending operation.");
            CollectionAssert.AreEqual(xml, File.ReadAllBytes(copy.Design),
                $"{moment}: the XML is still the version the pending operation started from; nothing was published.");
        }

        // The fresh process never receives the killed process's operation: the record cannot be reattached while it is
        // pending, and resuming the operation is refused because it belongs to the process that ended. Neither writes
        // anything or changes KiCad.
        async Task RequireNotReplayed(CrashCopy copy, CrashKiCad target, StoredDesignRecovery held, byte[] xml, int placements, string moment)
        {
            var state = await mcp.Tool("kicad_schematic_checked_state", new { instanceId = target.Id,
                documentJson = SchematicJson.Formatter.Format(target.Document) });
            RequireToolSuccess(state);
            string documentEpoch = state.GetProperty("structuredContent").GetProperty("state").GetProperty("revision").GetProperty("epoch").GetString()!;
            var reattach = await mcp.Tool("kicad_design_recovery_reattach", new { instanceId = target.Id, recoveryPath = copy.Store.StatePath,
                expectedRevisionToken = held.RevisionToken, expectedDocumentEpoch = documentEpoch });
            Assert.AreEqual("pending_recovery_requires_reconciliation", Error(reattach), $"{moment}: {reattach.GetRawText()}");
            var pending = held.State.PendingPublication!;
            var resume = await mcp.Tool("kicad_design_sync_apply", new { instanceId = target.Id, recoveryPath = copy.Store.StatePath,
                designPath = copy.Design, expectedRevisionToken = pending.RequestedRecoveryRevisionToken, operationId = pending.OperationId.ToString("D") });
            Assert.AreEqual("instance_changed", Error(resume), $"{moment}: {resume.GetRawText()}");
            RequireKept(copy, held, xml, moment + " after the refused reattachment and resume");
            RequirePlaced(await Capture(target), placements, moment + ": the refused resume changed nothing");
        }

        // After a release and before its continuation, the record stays on the killed KiCad's document session: the ordinary
        // reattachment to the KiCad started again is refused and changes nothing, because that KiCad holds part of the
        // released operation's result, which the next synchronization would otherwise take for a user edit.
        async Task<JsonElement> RequireReattachRefused(CrashCopy copy, CrashKiCad target, string moment)
        {
            var before = copy.Store.Read()!;
            Assert.IsFalse(before.State.HasPendingWork, $"{moment}: the operation was released.");
            var state = await mcp.Tool("kicad_schematic_checked_state", new { instanceId = target.Id,
                documentJson = SchematicJson.Formatter.Format(target.Document) });
            RequireToolSuccess(state);
            string documentEpoch = state.GetProperty("structuredContent").GetProperty("state").GetProperty("revision").GetProperty("epoch").GetString()!;
            Assert.AreNotEqual(before.State.NativeRevision.Epoch, documentEpoch, $"{moment}: the KiCad started again is a new document session.");
            var reattach = await mcp.Tool("kicad_design_recovery_reattach", new { instanceId = target.Id, recoveryPath = copy.Store.StatePath,
                expectedRevisionToken = before.RevisionToken, expectedDocumentEpoch = documentEpoch });
            Assert.AreEqual("released_operation_requires_continuation", Error(reattach), $"{moment}: {reattach.GetRawText()}");
            using var refusal = JsonDocument.Parse(Text(reattach));
            string message = refusal.RootElement.GetProperty("message").GetString()!;
            StringAssert.Contains(message, "kicad_design_recovery_release_exited", message);
            Assert.AreEqual(before.RevisionToken, copy.Store.Read()!.RevisionToken, $"{moment}: the refused reattachment changes nothing.");
            RequirePlaced(await Capture(target), 6, $"{moment}: the refused reattachment changes nothing in KiCad");
            Phase($"{moment}: the ordinary reattachment to KiCad {target.ProcessId} is refused until the released operation continues");
            return refusal.RootElement.Clone();
        }

        async Task Reattach(CrashCopy copy, CrashKiCad target)
        {
            var state = await mcp.Tool("kicad_schematic_checked_state", new { instanceId = target.Id,
                documentJson = SchematicJson.Formatter.Format(target.Document) });
            RequireToolSuccess(state);
            string documentEpoch = state.GetProperty("structuredContent").GetProperty("state").GetProperty("revision").GetProperty("epoch").GetString()!;
            RequireToolSuccess(await mcp.Tool("kicad_design_recovery_reattach", new { instanceId = target.Id, recoveryPath = copy.Store.StatePath,
                expectedRevisionToken = copy.Store.Read()!.RevisionToken, expectedDocumentEpoch = documentEpoch }));
            Assert.AreEqual(documentEpoch, copy.Store.Read()!.State.NativeRevision.Epoch);
        }

        async Task<string> StartSynchronization(CrashCopy copy, CrashKiCad target, string label, Func<StoredDesignRecovery, bool>? until = null)
        {
            var reply = await mcp.Tool("kicad_design_automatic_sync_start", new { instanceId = target.Id, recoveryPath = copy.Store.StatePath,
                designPath = copy.Design, expectedRecoveryRevision = copy.Store.Read()!.RevisionToken });
            RequireToolSuccess(reply);
            var data = reply.GetProperty("structuredContent");
            string sessionId = data.GetProperty("sessionId").GetString()!;
            await WaitSynchronization(target, sessionId, data.GetProperty("status").Clone(), "synchronization to settle " + label,
                status => Settled(copy, status) && (until is null || until(copy.Store.Read()!)));
            return sessionId;
        }

        async Task ApplyEdit(CrashCopy copy, CrashKiCad target, int components, string what)
        {
            var before = copy.Store.Read()!.State.LastSynchronization?.OperationId;
            await WriteEdit(copy, components);
            await WaitSynchronization(target, copy.Session, await SynchronizationStatus(target, copy.Session), "the XML edit adding " + what,
                status => Settled(copy, status) && copy.Store.Read()!.State.LastSynchronization?.OperationId is { } done && done != before);
            RequirePlaced(await Capture(target), components, "applied " + what);
            RequirePublished(copy, "applied " + what);
            Phase("automatic synchronization applied and published " + what);
        }

        bool Settled(CrashCopy copy, JsonElement status) => status.GetProperty("phase").GetString() == "Watching"
            && status.GetProperty("errorCode").ValueKind == JsonValueKind.Null
            && copy.Store.Read() is { } record && !record.State.HasPendingWork
            && status.GetProperty("recoveryRevisionToken").GetString() == record.RevisionToken;

        async Task<JsonElement> SynchronizationStatus(CrashKiCad target, string sessionId)
        {
            var listed = await mcp.Tool("kicad_design_automatic_sync_list", new { instanceId = target.Id });
            RequireToolSuccess(listed);
            return listed.GetProperty("structuredContent").GetProperty("sessions").EnumerateArray()
                .Single(s => s.GetProperty("sessionId").GetString() == sessionId).GetProperty("status").Clone();
        }

        async Task<JsonElement> WaitSynchronization(CrashKiCad target, string sessionId, JsonElement status, string what,
            Func<JsonElement, bool> done)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(120));
            while (!done(status))
            {
                if (status.GetProperty("phase").GetString() is "Paused" or "InvalidDesign" or "Stopped")
                    throw new AssertFailedException($"Automatic synchronization stopped before {what}: {status.GetRawText()}");
                limit.Token.ThrowIfCancellationRequested();
                var next = await mcp.Tool("kicad_design_automatic_sync_wait", new { instanceId = target.Id, sessionId,
                    afterSequence = status.GetProperty("sequence").GetUInt64() });
                RequireToolSuccess(next);
                status = next.GetProperty("structuredContent").GetProperty("status").Clone();
            }
            return status;
        }

        async Task<JsonElement> StopSynchronization(CrashKiCad target, string sessionId)
        {
            var stopped = await mcp.Tool("kicad_design_automatic_sync_stop", new { instanceId = target.Id, sessionId });
            RequireToolSuccess(stopped);
            return stopped.GetProperty("structuredContent").GetProperty("status").Clone();
        }

        // Adds components 1..count (J1, U1, R1, U2, U3, U4, U5, U6) with their proposed placements to the XML KiCad last
        // published, and replaces the design file in one step.
        async Task<byte[]> WriteEdit(CrashCopy copy, int count)
        {
            var current = SchematicDesignXml.Read(await File.ReadAllTextAsync(copy.Design, token), []);
            var source = placed!.Engineering.Circuit;
            var circuit = current.Engineering.Circuit;
            HashSet<Guid> Ids(int kind) => Enumerable.Range(1, count).Select(n => PsuCpuIds.Id(kind, n)).ToHashSet();
            var instances = Ids(0x07);
            var definitions = Ids(0x06);
            var parts = Enumerable.Range(1, count).Select(n => PsuCpuIds.Id(0x03, PsuCpuFixtureBuilder.Components[n - 1].Part)).ToHashSet();
            var edited = circuit with
            {
                Parts = [.. circuit.Parts, .. source.Parts.Where(p => parts.Contains(p.Id) && circuit.Parts.All(x => x.Id != p.Id))],
                Sheets = [.. circuit.Sheets.Select(sheet => sheet with { Components = [.. sheet.Components,
                    .. source.Sheets.Single(s => s.Id == sheet.Id).Components.Where(c => definitions.Contains(c.Id) && sheet.Components.All(x => x.Id != c.Id))] })],
                Components = [.. circuit.Components, .. source.Components.Where(c => instances.Contains(c.Id) && circuit.Components.All(x => x.Id != c.Id))],
                Symbols = [.. circuit.Symbols, .. source.Symbols.Where(o => instances.Contains(o.ComponentId) && circuit.Symbols.All(x => x.Id != o.Id))]
            };
            var engineering = current.Engineering with { Circuit = edited };
            _ = engineering.Validate([]);
            var present = edited.Parts.Select(p => p.Id).ToHashSet();
            var design = current with { Engineering = engineering, PartSymbols = [.. placed.PartSymbols!.Where(s => present.Contains(s.PartId))] };
            byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, []));
            string temporary = Path.Combine(copy.Project, ".design.xml.edit");
            await File.WriteAllBytesAsync(temporary, bytes, token);
            File.Move(temporary, copy.Design, true);
            return bytes;
        }

        async Task LayoutComponents(CrashCopy copy, CrashKiCad target, PsuCpuNativeContext seed, CheckedSchematicState seeded)
        {
            // The layout tool reads the desired design from the recovery record: the Components stage, without coordinates.
            var free = PsuCpuFixture.Desired(seed, PsuCpuStage.Components);
            var saved = await CommitCandidate(copy, target, Encoding.UTF8.GetBytes(SchematicDesignXml.Write(free, [])));
            var expected = PsuCpuFixture.ExpectedNative(PsuCpuStage.Components);
            string PathKey(IEnumerable<string> ids) => string.Join('/', ids);
            var regions = new List<SchematicLayoutRegion>();
            foreach (var sheet in expected.Sheets.Where(s => expected.Symbols.Any(x => x.Sheet == s.Key)))
            {
                string path = PathKey(seed.Baseline!.SheetBindings.Single(b => b.SheetInstanceId == sheet.ModelSheetInstance).NativePath.Select(id => id.ToString("D")));
                var screen = seed.Baseline.Schematic.Instances.Single(s => PathKey(s.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == path);
                var page = await target.Client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
                    { Document = screen.Metadata.Document.Clone(), ExpectedRevision = seeded.State.Revision.Clone() }, token);
                Assert.AreEqual(sheet.NativeScreen.ToString("D"), page.ScreenId.Value, sheet.Key);
                long inset = expected.Presentation.PageInsetMm * 1_000_000L, reserve = expected.Presentation.ReservedBottomMm * 1_000_000L;
                regions.Add(new(sheet.NativeScreen, new(page.PageBounds.Position.XNm + inset, page.PageBounds.Position.YNm + inset,
                    page.PageBounds.Position.XNm + page.PageBounds.Size.XNm - inset, page.PageBounds.Position.YNm + page.PageBounds.Size.YNm - reserve), []));
            }
            var layout = await mcp.Tool("kicad_design_propose_initial_layout", new { instanceId = target.Id, recoveryPath = copy.Store.StatePath,
                expectedRevisionToken = saved.RevisionToken, gridNm = 1_270_000L, clearanceNm = 2_540_000L, pageInsetNm = 0L, regions,
                userInstructions = "Place each fixture component on its own sheet and keep the processor's power unit on CPU_POWER." });
            RequireToolSuccess(layout);
            var proposal = layout.GetProperty("structuredContent");
            Assert.IsTrue(proposal.GetProperty("canPropose").GetBoolean(), proposal.GetRawText());
            placed = SchematicDesignXml.Read(proposal.GetProperty("desiredXml").GetString()!, []);
            Assert.IsTrue(placed.Engineering.Circuit.Symbols.All(s => s.Placement is not null));
            Assert.HasCount(8, placed.Engineering.Circuit.Components);
        }

        // Stores complete XML as the recovery record's desired design through the public candidate tool.
        async Task<StoredDesignRecovery> CommitCandidate(CrashCopy copy, CrashKiCad target, byte[] xml)
        {
            RequireToolSuccess(await mcp.Tool("kicad_design_candidate_commit", new { instanceId = target.Id, recoveryPath = copy.Store.StatePath,
                expectedRevisionToken = copy.Store.Read()!.RevisionToken, candidateXml = Encoding.UTF8.GetString(xml),
                expectedCandidateSha256 = Sha(xml), operationId = Guid.NewGuid().ToString("D") }));
            var committed = copy.Store.Read()!;
            CollectionAssert.AreEqual(xml, committed.State.DesiredFileBytes, "The candidate tool stored exactly this XML.");
            return committed;
        }

        void RequirePublished(CrashCopy copy, string when)
        {
            var record = copy.Store.Read()!;
            byte[] xml = File.ReadAllBytes(copy.Design);
            Assert.IsFalse(record.State.HasPendingWork, when);
            Assert.AreEqual(record.State.LastSynchronization!.DesignFileSha256, Sha(xml), $"{when}: the XML is the version last published.");
            CollectionAssert.AreEqual(xml, record.State.DesiredFileBytes, when);
        }

        Dictionary<string, (byte[] Bytes, DateTime Written)> Disk(CrashCopy copy) =>
            copy.NativeFiles.ToDictionary(path => path, path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));

        void RequireDisk(CrashCopy copy, Dictionary<string, (byte[] Bytes, DateTime Written)> before, string when, string[]? replaced = null)
        {
            foreach (var (path, (bytes, written)) in before)
            {
                bool rewritten = !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes) || File.GetLastWriteTimeUtc(path) != written;
                Assert.AreEqual(replaced?.Contains(path) ?? false, rewritten, $"{when}: {Path.GetFileName(path)} "
                    + (rewritten ? "was rewritten." : "was not rewritten."));
            }
            if (replaced is null)
                Assert.IsEmpty(Directory.GetFiles(copy.Project, "*.kicad-save-*"), $"{when}: KiCad started no save.");
        }

        Task<CheckedSchematicState> Capture(CrashKiCad target) => target.Client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = target.Document.Clone(), ProcessEpoch = target.Client.Epoch }, token);

        async Task<NativeEventDelivery> NextEvent(NativeEventSubscription subscription, Func<NativeEventDelivery, bool> wanted, string what)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                NativeEventDelivery delivery;
                try { delivery = await subscription.ReceiveAsync(limit.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new AssertFailedException($"Timed out waiting for {what}."); }
                if (wanted(delivery)) return delivery;
            }
        }

        async Task WaitLonger(Func<bool> condition, string what, int seconds)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            while (!condition())
            {
                try { await Task.Delay(20, limit.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new AssertFailedException($"Timed out waiting for {what}."); }
            }
        }
    }

    // One copy of the S1 project with its recovery record and the automatic synchronization watching it.
    private sealed class CrashCopy(string project, DesignRecoveryStore store)
    {
        public string Project { get; } = project;
        public DesignRecoveryStore Store { get; } = store;
        public string ProjectFile => Path.Combine(Project, "fixture.kicad_pro");
        public string Root => Path.Combine(Project, "fixture.kicad_sch");
        public string Psu => Path.Combine(Project, "psu.kicad_sch");
        public string Cpu => Path.Combine(Project, "cpu.kicad_sch");
        public string PowerFile => Path.Combine(Project, "cpu_power.kicad_sch");
        public string Design => Path.Combine(Project, "design.xml");
        public string[] NativeFiles => [Root, Psu, Cpu, Path.Combine(Project, "cpu_power.kicad_sch"), ProjectFile];
        public CrashKiCad KiCad { get; set; } = null!;
        public string Session { get; set; } = "";
    }

    // The origin of the edits a user makes in the KiCad started again: never the recovery record's own.
    private static readonly string UserOrigin = Guid.NewGuid().ToString("D");

    private static SchematicItemOperation UserNote(string id, string text, long x, long y) => new()
    {
        Create = Any.Pack(new SchematicText { Id = new() { Value = id }, Text = new() { Text_ = text, Position = new() { XNm = x, YNm = y },
            Attributes = new() { Size = new() { XNm = 1_270_000, YNm = 1_270_000 } } } })
    };

    private static SchematicItemOperation UserWire(string id, long x1, long y1, long x2, long y2) => new()
    {
        Create = Any.Pack(new SchematicLine { Id = new() { Value = id }, Start = new() { XNm = x1, YNm = y1 }, End = new() { XNm = x2, YNm = y2 },
            Type = SchematicLineType.SltWire })
    };

    private static SchematicItemOperation UserRemoval(string id) => new() { Remove = new() { Value = id } };

    private static string SheetKey(DocumentSpecifier document) => string.Join('/', document.SheetPath.Path.Select(id => id.Value));

    private static string[] ViewPaths(JsonElement view, string name) =>
        view.GetProperty(name).EnumerateArray().Select(path => path.GetString()!).ToArray();

    private static SchematicText NoteIn(SchematicHierarchyData hierarchy, string sheet, string id) =>
        hierarchy.Instances.Single(screen => SheetKey(screen.Metadata.Document) == sheet).Items.Where(item => item.Is(SchematicText.Descriptor))
            .Select(item => item.Unpack<SchematicText>()).SingleOrDefault(note => note.Id.Value == id)
        ?? throw new AssertFailedException($"The sheet {sheet} holds no note {id}.");

    private static SchematicText NoteIn(CheckedSchematicState state, string sheet, string id) => NoteIn(state.Electrical.Hierarchy.Data, sheet, id);

    private static void RequireNote(CheckedSchematicState state, string sheet, string id, string text, string when) =>
        Assert.AreEqual(text, NoteIn(state, sheet, id).Text.Text_, when);

    // A design XML without the given native objects, wherever they are.
    private static byte[] WithoutItems(byte[] xml, params string[] ids)
    {
        var design = SchematicDesignXml.Read(Encoding.UTF8.GetString(xml), []);
        var schematic = design.Schematic.Clone();
        foreach (var screen in schematic.Instances)
        {
            var kept = screen.Items.Where(item => !ids.Contains(NativeItemId(item))).ToArray();
            screen.Items.Clear();
            screen.Items.Add(kept);
        }
        return Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design with { Schematic = schematic }, []));
    }

    private static string? NativeItemId(Any item)
    {
        var decoded = SchematicItemDelta.Index([item]).Values.Single();
        return (decoded.Descriptor.FindFieldByName("id")?.Accessor.GetValue(decoded) as KIID)?.Value;
    }

    // A design XML whose sheet at path is taken from another design XML, to compare everything else exactly.
    private static byte[] WithSheet(byte[] xml, string path, byte[] from)
    {
        var design = SchematicDesignXml.Read(Encoding.UTF8.GetString(xml), []);
        var source = SchematicDesignXml.Read(Encoding.UTF8.GetString(from), []).Schematic.Instances.Single(screen => SheetKey(screen.Metadata.Document) == path);
        var schematic = design.Schematic.Clone();
        for (int index = 0; index < schematic.Instances.Count; index++)
            if (SheetKey(schematic.Instances[index].Metadata.Document) == path) schematic.Instances[index] = source.Clone();
        return Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design with { Schematic = schematic }, []));
    }

    private static void RequireKilled(JsonElement exit, CrashKiCad killed)
    {
        Assert.AreEqual(killed.Epoch, exit.GetProperty("epoch").GetString(), exit.GetRawText());
        Assert.AreEqual(killed.ProcessId, exit.GetProperty("processId").GetInt32(), exit.GetRawText());
        Assert.AreEqual(137, exit.GetProperty("exitCode").GetInt32(), exit.GetRawText());
        Assert.AreEqual(9, exit.GetProperty("signal").GetInt32(), exit.GetRawText());
        Assert.AreEqual("SIGKILL", exit.GetProperty("signalName").GetString(), exit.GetRawText());
        Assert.AreEqual("exit-status", exit.GetProperty("evidence").GetString(), exit.GetRawText());
        StringAssert.Contains(exit.GetProperty("description").GetString(), "exit status 137, the status reported for a process ended by signal 9 (SIGKILL)");
    }

    // Every expected unit placement exactly once and nothing else: the check that no edit is ever applied twice.
    private static void RequirePlaced(CheckedSchematicState state, int components, string when)
    {
        var placed = state.Electrical.Hierarchy.Data.Instances.SelectMany(screen => screen.Items)
            .Where(item => item.Is(SchematicSymbolInstance.Descriptor)).Select(item => item.Unpack<SchematicSymbolInstance>())
            .GroupBy(symbol => symbol.Id.Value).Select(group => group.First())
            .SelectMany(symbol => symbol.InstanceRecords.Records.Select(record => $"{record.Reference}/{record.Unit}"))
            .Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(Expected(components), placed, $"{when}: every unit exactly once. Placed: {string.Join(", ", placed)}");
    }

    // A design XML with the sheet files' load-format version cleared: the provenance a KiCad started again reads anew.
    private static string WithoutLoadProvenance(byte[] xml)
    {
        var design = SchematicDesignXml.Read(Encoding.UTF8.GetString(xml), []);
        var schematic = design.Schematic.Clone();
        foreach (var screen in schematic.Instances)
        {
            screen.Metadata.LoadedNativeFormatVersion = 0;
            screen.Metadata.WriterNativeFormatVersion = 0;
        }
        return SchematicDesignXml.Write(design with { Schematic = schematic }, []);
    }

    // The same design: every part but the native sheets byte for byte, and no native object differs (the sheets' load-format
    // version aside). The native sheet records themselves may differ in form, for example as KiCad reported them after saving.
    private static void RequireSameDesign(byte[] expected, byte[] actual, string when)
    {
        SchematicDesign Normalized(byte[] xml) => SchematicDesignXml.Read(WithoutLoadProvenance(xml), []);
        var before = Normalized(expected);
        var after = Normalized(actual);
        Assert.AreEqual(SchematicDesignXml.Write(before with { Schematic = after.Schematic }, []), SchematicDesignXml.Write(after, []),
            $"{when}: every part but the native sheets is identical.");
        var differences = SchematicHierarchyDelta.Plan(after.Schematic, before.Schematic, CancellationToken.None);
        Assert.IsEmpty(differences, $"{when}: no native object differs: {string.Join("; ", differences.Select(operation => operation.ToString()))}");
    }

    // The first line where two texts differ, with a little of each side, for evidence; null when they are equal.
    private static object? FirstDifference(string expected, string actual)
    {
        string[] a = expected.Split('\n'), b = actual.Split('\n');
        for (int line = 0; line < Math.Max(a.Length, b.Length); line++)
        {
            string left = line < a.Length ? a[line] : "", right = line < b.Length ? b[line] : "";
            if (left == right) continue;
            int column = 0;
            while (column < left.Length && column < right.Length && left[column] == right[column]) column++;
            int from = Math.Max(0, column - 80);
            string Excerpt(string text) => text.Length <= from ? "" : text.Substring(from, Math.Min(240, text.Length - from));
            return new { line = line + 1, column = column + 1, published = Excerpt(left), rolledBack = Excerpt(right),
                publishedLines = a.Length, rolledBackLines = b.Length };
        }
        return null;
    }

    // The unit placements a design XML declares, as "reference/unit".
    private static string[] Placements(byte[] xml)
    {
        var circuit = SchematicDesignXml.Read(Encoding.UTF8.GetString(xml), []).Engineering.Circuit;
        var references = circuit.Components.ToDictionary(component => component.Id, component => component.Reference);
        return circuit.Symbols.Select(symbol => $"{references[symbol.ComponentId]}/{symbol.Unit}").Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] Expected(int components) => PsuCpuFixtureBuilder.Occurrences.Where(o => o.Component <= components)
        .Select(o => $"{PsuCpuFixtureBuilder.Components[o.Component - 1].Reference}/{o.Unit}").Order(StringComparer.Ordinal).ToArray();

    // The error code of a failed tool result: structured {code} or {errorCode}, or the JSON text a few tools return.
    private static string? Error(JsonElement result)
    {
        if (!(result.TryGetProperty("isError", out var error) && error.GetBoolean())) return null;
        if (!result.TryGetProperty("structuredContent", out var content) || content.ValueKind != JsonValueKind.Object)
        {
            using var text = JsonDocument.Parse(Text(result));
            content = text.RootElement.Clone();
        }
        return (content.TryGetProperty("code", out var code) ? code : content.GetProperty("errorCode")).GetString();
    }

    private static string Text(JsonElement result) => result.GetProperty("content").EnumerateArray()
        .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Dictionary<string, string> DesignFileHashes(string directory) => Directory.EnumerateFiles(directory)
        .Where(path => Path.GetExtension(path) is ".kicad_sch" or ".kicad_pro" or ".xml")
        .ToDictionary(path => Path.GetFileName(path), path => Sha(File.ReadAllBytes(path)));
}
