using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private sealed record CrashKiCad(string Id, int ProcessId, string Epoch, NativeClient Client, DocumentSpecifier Document,
        Process? Owned = null);

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
    // Each time the MCP server reports the instance as exited with exit status 137 (signal 9, SIGKILL) and answers
    // instance_exited for it, and the synchronization that was applying pauses with instance_exited. The XML is
    // byte-identical to the version last published or, while an apply was pending, to the version the recovery record
    // names as that operation's starting point, with the operation and its save request stated exactly; the recovery
    // record survives unchanged. A fresh KiCad for the same project continues the instance ID with a new epoch (started
    // through the MCP server, or started like a user would and attached) and loads exactly what the killed KiCad had
    // saved, never an edit twice. After the idle kill the record is reattached and automatic synchronization resumes.
    // After the two mid-apply kills the record keeps the operation, and neither reattachment nor resuming the operation
    // replays it on the fresh process; releasing such an operation needs the recovery store's transition for a proven
    // exit, a parent seam (the service catalogue lists it as the exited-operation-release limitation).
    private static async Task VerifyPsuCpuNativeCrash(NativeClient client, PsuCpuNativeContext context, Process native, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux process signals, /proc and strace.");
        var clock = Stopwatch.StartNew();
        void Phase(string text) => Console.WriteLine($"Native crash {instanceId} at {clock.Elapsed.TotalSeconds:F1}s: {text}");
        string Evidence(string name) => Path.Combine(evidence, instanceId + "-crash-" + name);
        _ = context.Baseline ?? throw new AssertFailedException("The S1 seed must provide a native baseline.");

        // The other KiCad: this instance's fixture KiCad, which prepared the seed. Nothing may change it.
        var other = await client.HandshakeAsync(token);
        Assert.AreEqual((uint)processId, other.ProcessId, "KiCad names its own process in the handshake.");
        var otherFiles = DesignFileHashes(context.ProjectDirectory);

        string work = Path.Combine(Path.GetDirectoryName(context.ProjectDirectory)!, "crash-" + instanceId[..8]);
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
            var idleStop = await StopSynchronization(kicad, first.Session);
            kicad = await Start(first, "after the idle kill", kicad);
            RequirePlaced(await Capture(kicad), 2, "fresh KiCad after the idle kill");
            await Reattach(first, kicad);
            first.Session = await StartSynchronization(first, kicad, "after the idle kill");
            RequirePlaced(await Capture(kicad), 2, "resumed after the idle kill");
            RequirePublished(first, "resumed after the idle kill");
            await ApplyEdit(first, kicad, 4, "R1 and U2 after the idle kill");
            cases.Add(new { moment = "idle", exit = idleExit, recoveryRevision = idle.RevisionToken, xmlSha256 = Sha(published),
                automaticSynchronizationAtStop = idleStop, resumedEpoch = kicad.Epoch, resumedPlacements = Expected(4).Length });

            var commitDisk = Disk(first);
            var committing = await Capture(kicad);
            byte[] commitEdit;
            using (var events = new NativeEventSubscription(await kicad.Client.HandshakeAsync(token)))
            {
                // The first packet proves the subscription is connected before the edit is written.
                await NextEvent(events, _ => true, "the first native notification");
                commitEdit = await WriteEdit(first, 6);
                var commit = await NextEvent(events, delivery => delivery.Event.SchematicCommit is { } change
                    && change.Revision.Epoch == committing.State.Revision.Epoch && change.Revision.Sequence > committing.State.Revision.Sequence,
                    "KiCad to commit the XML edit");
                Assert.AreEqual(0, SignalProcess(kicad.ProcessId, SigStop), "KiCad could not be held.");
                await WaitUntil(() => ProcessState(kicad.ProcessId) == 'T', "KiCad to stop after its commit", token);
                Phase($"KiCad held right after commit {commit.Event.SchematicCommit.Revision.Sequence}");
            }
            var heldCommit = first.Store.Read()!;
            var commitPending = heldCommit.State.PendingPublication
                ?? throw new AssertFailedException("The apply journals its publication before KiCad commits the edit.");
            Assert.IsNotNull(heldCommit.State.PendingMutation, "The recovery record holds the native edit KiCad committed.");
            Assert.AreEqual(kicad.Epoch, heldCommit.State.PendingNativeState!.ProcessEpoch, "The operation names the process that holds it.");
            Assert.AreEqual(DesignPublicationPhase.Prepared, commitPending.Phase, "Nothing was published.");
            CollectionAssert.AreEqual(commitEdit, commitPending.ExpectedFileBytes, "The operation starts from the edited XML.");
            CollectionAssert.AreEqual(commitEdit, await File.ReadAllBytesAsync(first.Design, token));
            RequireDisk(first, commitDisk, "held after the commit");
            bool commitSaveJournaled = heldCommit.State.PendingNativeSave is not null;
            var commitExit = await Kill(kicad, "after-commit");
            await RequireKilledOperation(first, kicad, heldCommit, commitEdit, commitDisk, "after-commit");
            var commitStop = await StopSynchronization(kicad, first.Session);
            // A KiCad started again for the project like a user would, with the same instance ID, attached to the server.
            kicad = await StartAttached(first, "after the commit kill", kicad);
            // Only saved work survives a kill: the committed but unsaved U3 and U4 are gone; nothing is doubled.
            RequirePlaced(await Capture(kicad), 4, "fresh KiCad after the commit kill");
            await RequireNotReplayed(first, kicad, heldCommit, commitEdit, 4, "after-commit");
            var attachedExit = await KillAttached(kicad, "attached");
            cases.Add(new { moment = "after-native-commit", exit = commitExit, operationId = commitPending.OperationId,
                pendingProcessEpoch = heldCommit.State.PendingNativeState!.ProcessEpoch, recoveryRevision = heldCommit.RevisionToken,
                xmlSha256 = Sha(commitEdit), candidateSha256 = Sha(commitPending.CandidateFileBytes), saveJournaled = commitSaveJournaled,
                automaticSynchronizationAtStop = commitStop, restartedAttachedEpoch = kicad.Epoch, restartedPlacements = Expected(4).Length,
                attachedExit });

            // ---- Second copy: during the checked save ------------------------------------------------------------------
            var second = await Prepare("second");
            kicad = second.KiCad;
            await ApplyEdit(second, kicad, 4, "J1, U1, R1 and U2");
            var saveDisk = Disk(second);
            StoredDesignRecovery heldSave;
            byte[] saveEdit;
            string[] trace;
            string[] temporary;
            JsonElement saveExit;
            await using (var fault = await SyscallFault.AttachAsync(kicad.ProcessId, Evidence("save.strace"), token,
                "-e", "trace=fsync", "-e", "inject=fsync:signal=SIGSTOP:when=5"))
            {
                saveEdit = await WriteEdit(second, 8);
                await WaitLonger(() => IsPaused(kicad.ProcessId), "KiCad to stop inside the synchronization's save", 90);
                heldSave = second.Store.Read()!;
                temporary = Directory.GetFiles(second.Project, "*.kicad-save-*");
                saveExit = await Kill(kicad, "during-save");
                trace = await fault.DetachAsync();
            }
            var savePending = heldSave.State.PendingPublication
                ?? throw new AssertFailedException("The apply journals its publication before KiCad saves.");
            var saveRequest = heldSave.State.PendingNativeSave
                ?? throw new AssertFailedException("KiCad was saving, so the record holds the exact save request.");
            Assert.AreEqual(kicad.Epoch, saveRequest.ExpectedState.ProcessEpoch, "The save request names the process that ran it.");
            Assert.AreEqual(DesignPublicationPhase.Prepared, savePending.Phase, "Nothing was published.");
            CollectionAssert.AreEqual(saveEdit, savePending.ExpectedFileBytes);
            // The trace shows where KiCad stopped: flushing the CPU sheet's new version, not yet in place.
            string stoppedAt = trace.Last(line => line.StartsWith("fsync(", StringComparison.Ordinal));
            StringAssert.Contains(stoppedAt, "cpu.kicad_sch.kicad-save-" + kicad.ProcessId + "-",
                "KiCad must stop while writing the CPU sheet: " + string.Join('\n', trace));
            CollectionAssert.AreEqual(new[] { second.Cpu }, temporary.Select(path => path[..path.IndexOf(".kicad-save-", StringComparison.Ordinal)]).ToArray(),
                "KiCad was stopped with exactly the CPU sheet's new version not yet in place.");
            RequireDisk(second, saveDisk, "save stopped part way", replaced: [second.Root, second.Psu]);
            StringAssert.Contains(await File.ReadAllTextAsync(second.Psu, token), "\"U4\"", "The replaced PSU sheet holds U3 and U4.");
            Assert.IsFalse((await File.ReadAllTextAsync(second.Cpu, token)).Contains("\"U5\"", StringComparison.Ordinal),
                "The CPU sheet in place is the previous version, without U5.");
            await RequireKilledOperation(second, kicad, heldSave, saveEdit, null, "during-save");
            var saveStop = await StopSynchronization(kicad, second.Session);
            kicad = await Start(second, "after the save kill", kicad);
            // The fresh KiCad loads the half-written save: U3 and U4 once on the replaced PSU sheet, no U5 or U6.
            RequirePlaced(await Capture(kicad), 6, "fresh KiCad after the save kill");
            await RequireNotReplayed(second, kicad, heldSave, saveEdit, 6, "during-save");
            cases.Add(new { moment = "during-checked-save", exit = saveExit, operationId = savePending.OperationId,
                nativeSaveOperationId = saveRequest.OperationId, recoveryRevision = heldSave.RevisionToken, xmlSha256 = Sha(saveEdit),
                candidateSha256 = Sha(savePending.CandidateFileBytes), stoppedAt, replacedBeforeKill = new[] { "fixture.kicad_sch", "psu.kicad_sch" },
                leftBehind = temporary.Select(Path.GetFileName).ToArray(), automaticSynchronizationAtStop = saveStop,
                restartedEpoch = kicad.Epoch, restartedPlacements = Expected(6).Length });

            // ---- The other KiCad was never touched -------------------------------------------------------------------
            Assert.AreEqual(other.Epoch, (await client.HandshakeAsync(token)).Epoch, "The other KiCad kept its process.");
            Assert.IsFalse(native.HasExited, "The other KiCad is still running.");
            CollectionAssert.AreEquivalent(otherFiles.ToArray(), DesignFileHashes(context.ProjectDirectory).ToArray(),
                "The other KiCad's design files are unchanged.");
            await File.WriteAllTextAsync(Evidence("result.json"), JsonSerializer.Serialize(new
            {
                instanceId, otherEpoch = other.Epoch, otherProcess = processId, cases,
                releaseOfExitedOperations = "unfinished: needs the DesignRecoveryStore transition for a proven process exit (parent seam)",
                seconds = clock.Elapsed.TotalSeconds
            }, new JsonSerializerOptions { WriteIndented = true }), token);
            Phase("complete");
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

        // ---- Steps ------------------------------------------------------------------------------------------------------

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
                await LayoutComponents(copy, kicad, seed, seeded, recovery);
                recovery = copy.Store.Read()!;
                copy.Store.Save(recovery.State with { DesiredFileBytes = baselineBytes }, recovery.RevisionToken);
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
            else RequireContinues(view, previous, reply);
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
            var exit = await ReportedExit(target, moment);
            RequireKilled(exit, target);
            return exit;
        }

        // A KiCad this test started itself and the server only attached: the server proves the exit by the process it
        // recorded no longer existing, without an exit status.
        async Task<JsonElement> KillAttached(CrashKiCad target, string moment)
        {
            Assert.AreEqual(0, SignalProcess(target.ProcessId, 9), "KiCad could not be killed.");
            await target.Owned!.WaitForExitAsync(token);
            Assert.AreEqual(137, target.Owned.ExitCode, "The attached KiCad was killed by signal 9.");
            var exit = await ReportedExit(target, moment);
            Assert.AreEqual("process-absent", exit.GetProperty("evidence").GetString(), exit.GetRawText());
            // The instance list leaves out values it does not have.
            Assert.IsFalse(exit.TryGetProperty("exitCode", out var status) && status.ValueKind != JsonValueKind.Null,
                "Only the parent of a process learns its exit status: " + exit.GetRawText());
            Assert.AreEqual(target.ProcessId, exit.GetProperty("processId").GetInt32());
            return exit;
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
            string log = Path.Combine(NativeIpcEndpoint.RuntimeDirectory(target.Id), "native.log");
            if (File.Exists(log)) File.Copy(log, Evidence("native-" + moment + ".log"), true);
            Phase($"KiCad {target.ProcessId} killed {moment}; the MCP server reports it exited");
            return listed.GetProperty("exit").Clone();
        }

        // The worker that was applying when KiCad died pauses with instance_exited; the recovery record, the XML and the
        // native files are exactly as they were while KiCad was held.
        async Task RequireKilledOperation(CrashCopy copy, CrashKiCad target, StoredDesignRecovery held, byte[] xml,
            Dictionary<string, (byte[] Bytes, DateTime Written)>? disk, string moment)
        {
            var paused = await WaitSynchronization(target, copy.Session, await SynchronizationStatus(target, copy.Session), "the worker to pause",
                status => status.GetProperty("phase").GetString() == "Paused");
            Assert.AreEqual("instance_exited", paused.GetProperty("errorCode").GetString(), paused.GetRawText());
            RequireKept(copy, held, xml, moment);
            if (disk is not null) RequireDisk(copy, disk, moment);
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

        async Task<string> StartSynchronization(CrashCopy copy, CrashKiCad target, string label)
        {
            var reply = await mcp.Tool("kicad_design_automatic_sync_start", new { instanceId = target.Id, recoveryPath = copy.Store.StatePath,
                designPath = copy.Design, expectedRecoveryRevision = copy.Store.Read()!.RevisionToken });
            RequireToolSuccess(reply);
            var data = reply.GetProperty("structuredContent");
            string sessionId = data.GetProperty("sessionId").GetString()!;
            await WaitSynchronization(target, sessionId, data.GetProperty("status").Clone(), "synchronization to settle " + label,
                status => Settled(copy, status));
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

        async Task LayoutComponents(CrashCopy copy, CrashKiCad target, PsuCpuNativeContext seed, CheckedSchematicState seeded,
            StoredDesignRecovery saved)
        {
            // The layout tool reads the desired design from the recovery record: the Components stage, without coordinates.
            var free = PsuCpuFixture.Desired(seed, PsuCpuStage.Components);
            saved = copy.Store.Save(saved.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(free, [])) }, saved.RevisionToken);
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
        public string Design => Path.Combine(Project, "design.xml");
        public string[] NativeFiles => [Root, Psu, Cpu, Path.Combine(Project, "cpu_power.kicad_sch"), ProjectFile];
        public CrashKiCad KiCad { get; set; } = null!;
        public string Session { get; set; } = "";
    }

    private static void RequireKilled(JsonElement exit, CrashKiCad killed)
    {
        Assert.AreEqual(killed.Epoch, exit.GetProperty("epoch").GetString(), exit.GetRawText());
        Assert.AreEqual(killed.ProcessId, exit.GetProperty("processId").GetInt32(), exit.GetRawText());
        Assert.AreEqual(137, exit.GetProperty("exitCode").GetInt32(), exit.GetRawText());
        Assert.AreEqual(9, exit.GetProperty("signal").GetInt32(), exit.GetRawText());
        Assert.AreEqual("SIGKILL", exit.GetProperty("signalName").GetString(), exit.GetRawText());
        Assert.AreEqual("exit-status", exit.GetProperty("evidence").GetString(), exit.GetRawText());
        StringAssert.Contains(exit.GetProperty("description").GetString(), "exit status 137 (killed by signal 9, SIGKILL)");
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
