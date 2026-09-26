using System.Diagnostics;
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
    // Lane 2C, ledgers p0aa59a1dfc8701ea, p6728215278183167 and pec2f1b53d4024a17: a public way out of a synchronization that
    // KiCad applied but whose follow-up check refused it, and an automatic worker that pauses as soon as its KiCad ends.
    //
    // The PSU/CPU XML rebuild journey (NativeXmlRebuildJourney) creates the Components stage from XML and draws the Complete
    // stage's nets through the production MCP server. Before each of those applies, the same apply is first made through the
    // compiled test host with a scripted refusal (KICAD_SYNC_HARNESS_REFUSE_STAGE): KiCad really commits the edit, and every
    // checked capture that host reads afterwards joins KiCad's two largest nets, so the synchronization's own check refuses the
    // operation after the commit (native_sync_connectivity_mismatch for the creation, realization_resolution_mismatch for the
    // realization) and refuses it again on every retry, as a real mismatch does. The scripted join only forces that condition:
    // KiCad, the recovery record, the XML and the production server always see KiCad's own state, and the way out is taken
    // through the production server's public tool kicad_design_recovery_resolve_pending.
    //  - First project: the stuck creation is undone (KiCad shows the arranged sheets again, the record observes them, the XML
    //    is unchanged) and synchronization resumes with the journey's ordinary creation apply. The stuck realization is taken
    //    back in KiCad with Edit > Undo, discard verifies KiCad no longer shows it, and the ordinary realization apply resumes.
    //    Discard is refused while KiCad still shows either result. A KiCad started for a copy of the realized project is watched
    //    by an automatic worker and killed: the worker pauses with instance_exited within a second.
    //  - Second project: both stuck operations are kept (keep-and-replan); each continuation is completed with
    //    kicad_design_sync_apply, which saves KiCad's result and publishes the XML re-planned from it. On a KiCad started for a
    //    copy of the realized project, a stuck creation is kept after the person joined two XML nets with a wire and saved and
    //    reloaded the sheets (VerifyKeptConnectionsWin): discard and undo are refused and name keep-and-replan, KiCad's joined
    //    net is published with the requirements on the two nets waiting for resolution, and the kept result's publication is
    //    refused undo and discard while KiCad shows it.
    // Every path ends with KiCad, the recovery record and the XML consistent, and the next synchronization proceeds.

    private sealed record StuckSynchronization(Guid OperationId, string NativeOperationId, StoredDesignRecovery Held, CheckedSchematicState Before,
        CheckedSchematicState Committed, CheckedSchematicBatchReceipt NativeReceipt, byte[] Xml, string DesignPath, object Evidence);

    // Apply the record's plan through the test host with a scripted refusal of the given stage and require the half-applied
    // state: KiCad committed the edit exactly once, the record keeps the operation pending, the XML and baseline are unchanged,
    // and retrying, planning or reattaching through the public tools cannot leave it.
    private static async Task<StuckSynchronization> ForceStuckSynchronization(NativeClient client, DocumentSpecifier document,
        DesignRecoveryStore store, string designPath, string instanceId, string stage, string code, string evidence, CancellationToken token)
    {
        string Evidence(string name) => Path.Combine(evidence, instanceId + "-stuck-" + stage + "-" + name);
        string marker = Evidence("refusal.json");
        var start = SyncHarnessProcessTests.StartInfo();
        start.Environment["KICAD_SYNC_HARNESS_REFUSE_STAGE"] = stage;
        start.Environment["KICAD_SYNC_HARNESS_REFUSE_MARKER"] = marker;
        await using var harness = await StdioMcpFixture.StartAsync(start, Evidence("host"), Evidence("host.log"), token);
        RequireToolSuccess(await harness.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var record = store.Read()!;
        Assert.IsFalse(record.State.HasPendingWork, stage + ": nothing is pending before the apply.");
        byte[] xml = await File.ReadAllBytesAsync(designPath, token);
        var before = await Capture();
        var operation = Guid.NewGuid();
        var args = new { instanceId, recoveryPath = store.StatePath, designPath, expectedRevisionToken = record.RevisionToken, operationId = operation.ToString("D") };
        var refused = await harness.Tool("kicad_design_sync_apply", args);
        await File.WriteAllTextAsync(Evidence("apply.json"), RetainedToolEvidence(refused), token);
        Assert.AreEqual(code, Error(refused), refused.GetRawText());
        string message = refused.GetProperty("structuredContent").GetProperty("errorMessage").GetString()!;
        StringAssert.Contains(message, "kicad_design_recovery_resolve_pending", "The refusal names the way out.");
        StringAssert.Contains(message, operation.ToString("D"), "The refusal names the operation to leave.");
        Assert.IsTrue(File.Exists(marker), stage + ": the refusal is the scripted one.");
        using var scripted = JsonDocument.Parse(await File.ReadAllBytesAsync(marker, token));

        var held = store.Read()!;
        Assert.IsTrue(held.State.HasPendingWork, stage + ": the operation stays pending.");
        Assert.AreEqual(operation, held.State.PendingPublication?.OperationId ?? held.State.PendingLayout?.OperationId);
        string nativeOperation = held.State.PendingMutation?.OperationId ?? throw new AssertFailedException(stage + ": the record holds the native edit.");
        Assert.AreEqual(nativeOperation, scripted.RootElement.GetProperty("nativeOperationId").GetString(), "The scripted refusal followed exactly this edit.");
        Assert.AreEqual(SchematicDesignXml.Write(record.State.Baseline, []), SchematicDesignXml.Write(held.State.Baseline, []), "The baseline did not move.");
        CollectionAssert.AreEqual(xml, await File.ReadAllBytesAsync(designPath, token), stage + ": no XML was published.");
        var committed = await Capture();
        Assert.AreNotEqual(before.State.StateSha256, committed.State.StateSha256, stage + ": KiCad holds the operation's change.");
        Assert.IsTrue(committed.State.NativeContentDirty, stage + ": KiCad's change is not saved.");
        // KiCad's retained receipt, read without running the edit: committed once, and it is what KiCad shows.
        var expected = new CheckedSchematicBatch { Batch = held.State.PendingMutation.Clone(), ExpectedState = held.State.PendingNativeState!.Clone() };
        var receipt = await client.InvokeAsync<ReadCheckedSchematicBatchReceipt, CheckedSchematicBatchReceipt>(new()
            { Document = expected.Batch.Document.Clone(), ProcessEpoch = client.Epoch, OperationId = nativeOperation, ExpectedRequest = expected }, token);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, receipt.ToString());
        Assert.AreEqual(committed.State, receipt.ObservedAfter, stage + ": KiCad shows exactly what the operation's edit left.");

        // Retrying the same operation through the same host replays KiCad's receipt and repeats the refusal; planning and
        // reattaching are refused while it is pending. None of them changes anything.
        var retried = await harness.Tool("kicad_design_sync_apply", args);
        Assert.AreEqual(code, Error(retried), retried.GetRawText());
        var plan = await harness.Tool("kicad_design_sync_plan", new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = held.RevisionToken });
        Assert.AreEqual("pending_recovery_requires_reconciliation", Error(plan), plan.GetRawText());
        var reattach = await harness.Tool("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = held.RevisionToken, expectedDocumentEpoch = committed.State.Revision.Epoch });
        Assert.AreEqual("pending_recovery_requires_reconciliation", Error(reattach), reattach.GetRawText());
        Assert.AreEqual(held.RevisionToken, store.Read()!.RevisionToken, stage + ": retrying changed nothing in the record.");
        Assert.AreEqual(committed.State, (await Capture()).State, stage + ": retrying never ran the edit again.");
        CollectionAssert.AreEqual(xml, await File.ReadAllBytesAsync(designPath, token));
        return new(operation, nativeOperation, held, before, committed, receipt, xml, designPath, new
        {
            stage, operationId = operation, nativeOperationId = nativeOperation, refusal = code,
            joinedNets = scripted.RootElement.GetProperty("joinedNets").EnumerateArray().Select(n => n.GetString()).ToArray(),
            retryRefused = Error(retried), planRefused = Error(plan), reattachRefused = Error(reattach),
            committedRevision = committed.State.Revision.Sequence,
            connectivityAssertionVerified = receipt.Result?.ConnectivityAssertionVerified ?? false
        });

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
    }

    // kicad_design_recovery_resolve_pending through the given (production) server, for the stuck operation's revision.
    private static async Task<JsonElement> ResolvePending(StdioMcpFixture host, string instanceId, DesignRecoveryStore store,
        StuckSynchronization stuck, string choice, string evidence, CancellationToken token, string? revisionToken = null, string label = "")
    {
        var reply = await host.Tool("kicad_design_recovery_resolve_pending", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = revisionToken ?? stuck.Held.RevisionToken, operationId = stuck.OperationId.ToString("D"), choice });
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-resolve-" + stuck.OperationId.ToString("N")[..8] + "-" + choice
            + (revisionToken is null ? "" : "-repeat") + (label.Length == 0 ? "" : "-" + label) + ".json"), RetainedToolEvidence(reply), token);
        return reply;
    }

    // Undo: KiCad shows the design as the operation found it, the record observes that state with nothing pending, and the
    // baseline, desired XML and XML file are unchanged. Discard is refused first, while KiCad still shows the result.
    private static async Task<object> UndoStuckSynchronization(StdioMcpFixture host, NativeClient client, DocumentSpecifier document,
        DesignRecoveryStore store, StuckSynchronization stuck, string instanceId, string evidence, CancellationToken token)
    {
        var early = await ResolvePending(host, instanceId, store, stuck, "discard", evidence, token, label: "refused");
        Assert.AreEqual("operation_result_in_kicad", Error(early), early.GetRawText());
        Assert.AreEqual(stuck.Held.RevisionToken, store.Read()!.RevisionToken, "A refused discard changes nothing.");
        Assert.AreEqual(stuck.Committed.State, (await Capture()).State, "A refused discard sends nothing to KiCad.");
        var mismatch = await host.Tool("kicad_design_recovery_resolve_pending", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = stuck.Held.RevisionToken, operationId = Guid.NewGuid().ToString("D"), choice = "undo" });
        Assert.AreEqual("pending_operation_mismatch", Error(mismatch), mismatch.GetRawText());

        var reply = await ResolvePending(host, instanceId, store, stuck, "undo", evidence, token);
        RequireToolSuccess(reply);
        var view = reply.GetProperty("structuredContent").Clone();
        Assert.AreEqual("undone", view.GetProperty("outcome").GetString(), view.GetRawText());
        Assert.AreEqual("completed", view.GetProperty("nativeStatus").GetString());
        Assert.IsTrue(view.GetProperty("undoOperations").GetInt32() > 0, "One checked native edit took the operation's change back.");
        Assert.IsFalse(view.GetProperty("pendingWork").GetBoolean());
        var undone = await Capture();
        RequireSameDesign(stuck.Before, undone, "undo");
        Assert.AreEqual(view.GetProperty("undoOperationId").GetString(), await LastCommitOperation(undone), "KiCad's last commit is the undo.");
        var record = store.Read()!;
        RequireSettledOn(record, stuck, undone, view, "undo");
        // A repeated call reports the same resolution and changes nothing.
        var again = await ResolvePending(host, instanceId, store, stuck, "undo", evidence, token, stuck.Held.RevisionToken);
        RequireToolSuccess(again);
        Assert.IsFalse(again.GetProperty("structuredContent").GetProperty("resolvedNow").GetBoolean(), again.GetRawText());
        Assert.AreEqual(record.RevisionToken, store.Read()!.RevisionToken);
        Assert.AreEqual(undone.State, (await Capture()).State);
        return new { stuck.Evidence, discardRefused = Error(early), mismatchRefused = Error(mismatch), outcome = "undone",
            undoOperations = view.GetProperty("undoOperations").GetInt32(), undoOperationId = view.GetProperty("undoOperationId").GetString(),
            revisionAfter = undone.State.Revision.Sequence, receiptPath = view.GetProperty("receiptPath").GetString(), repeatedCallNoOp = true };

        async Task<string> LastCommitOperation(CheckedSchematicState at)
        {
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            { Document = document.Clone(), DocumentEpoch = at.State.Revision.Epoch, AfterSequence = stuck.Committed.State.Revision.Sequence }, token);
            return journal.Changes.Last(c => c.Kind == SchematicChange.Types.Kind.Commit).OperationId;
        }
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
    }

    // Discard after the person took the operation's change back in KiCad (Edit > Undo): refused while KiCad shows it, then
    // accepted once KiCad shows the design as the operation found it; nothing is sent to KiCad.
    private static async Task<object> DiscardStuckSynchronization(StdioMcpFixture host, NativeClient client, DocumentSpecifier document,
        DesignRecoveryStore store, StuckSynchronization stuck, int processId, string display, string instanceId, string evidence, CancellationToken token)
    {
        var early = await ResolvePending(host, instanceId, store, stuck, "discard", evidence, token, label: "refused");
        Assert.AreEqual("operation_result_in_kicad", Error(early), early.GetRawText());
        Assert.AreEqual(stuck.Held.RevisionToken, store.Read()!.RevisionToken, "A refused discard changes nothing.");
        await FocusedSchematicShortcut(client, document, processId, display, "z", token);
        CheckedSchematicState undone;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                undone = await Capture();
                if (undone.State.Revision.Sequence > stuck.Committed.State.Revision.Sequence) break;
                await Task.Delay(250, wait.Token);
            }
        }
        RequireSameDesign(stuck.Before, undone, "KiCad's own undo");
        var reply = await ResolvePending(host, instanceId, store, stuck, "discard", evidence, token);
        RequireToolSuccess(reply);
        var view = reply.GetProperty("structuredContent").Clone();
        Assert.AreEqual("discarded", view.GetProperty("outcome").GetString(), view.GetRawText());
        Assert.AreEqual("completed", view.GetProperty("nativeStatus").GetString(), "KiCad had committed the edit the person took back.");
        Assert.AreEqual(0, view.GetProperty("undoOperations").GetInt32(), "Discard sends nothing to KiCad.");
        Assert.AreEqual(undone.State, (await Capture()).State, "Discard sends nothing to KiCad.");
        RequireSettledOn(store.Read()!, stuck, undone, view, "discard");
        return new { stuck.Evidence, discardRefusedWhileShown = Error(early), undoneInKiCadAt = undone.State.Revision.Sequence, outcome = "discarded",
            receiptPath = view.GetProperty("receiptPath").GetString() };

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
    }

    // Keep-and-replan: KiCad keeps the operation's result, the continuation is journaled for the running KiCad, and completing
    // it with kicad_design_sync_apply saves KiCad's sheets and publishes the XML re-planned from what KiCad shows.
    private static async Task<object> KeepStuckSynchronization(StdioMcpFixture host, NativeClient client, DocumentSpecifier document,
        DesignRecoveryStore store, StuckSynchronization stuck, string instanceId, string evidence, CancellationToken token)
    {
        var reply = await ResolvePending(host, instanceId, store, stuck, "keep-and-replan", evidence, token);
        RequireToolSuccess(reply);
        var view = reply.GetProperty("structuredContent").Clone();
        Assert.AreEqual("keep-pending", view.GetProperty("outcome").GetString(), view.GetRawText());
        Assert.AreEqual("completed", view.GetProperty("nativeStatus").GetString());
        Assert.IsTrue(view.GetProperty("continuationPending").GetBoolean(), view.GetRawText());
        Assert.IsFalse(view.GetProperty("nativeChangedSinceOperation").GetBoolean());
        Assert.AreEqual(0, view.GetProperty("netChanges").GetArrayLength(), "KiCad connects the pins as the XML says, so no net changes.");
        string continuation = view.GetProperty("continuationOperationId").GetString()!;
        string requested = view.GetProperty("requestedRecoveryRevisionToken").GetString()!;
        Assert.AreEqual(stuck.Committed.State, (await Capture()).State, "Keeping sends nothing to KiCad.");
        CollectionAssert.AreEqual(stuck.Xml, await File.ReadAllBytesAsync(stuck.DesignPath, token), "The XML waits for the continuation.");
        var kept = store.Read()!;
        Assert.AreEqual(Guid.Parse(continuation), kept.State.PendingPublication?.OperationId);
        Assert.IsNull(kept.State.PendingMutation, "The continuation sends no native edit.");
        Assert.AreEqual(stuck.Committed.State, kept.State.PendingNativeState, "The continuation is guarded by exactly what KiCad shows.");
        Assert.AreEqual(SchematicDesignXml.Write(stuck.Held.State.Baseline, []), SchematicDesignXml.Write(kept.State.Baseline, []), "The baseline did not move.");

        var completed = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath = stuck.DesignPath,
            expectedRevisionToken = requested, operationId = continuation });
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-resolve-" + stuck.OperationId.ToString("N")[..8] + "-continuation.json"),
            RetainedToolEvidence(completed), token);
        RequireToolSuccess(completed);
        var result = completed.GetProperty("structuredContent");
        Assert.IsFalse(result.GetProperty("nativeMutationCommitted").GetBoolean(), completed.GetRawText());
        Assert.IsTrue(result.GetProperty("nativeFilesSaved").GetBoolean(), completed.GetRawText());
        Assert.IsTrue(result.GetProperty("synchronizationCommitted").GetBoolean(), completed.GetRawText());
        var shown = await Capture();
        Assert.IsFalse(shown.State.NativeContentDirty, "KiCad's kept result is saved.");
        var published = store.Read()!;
        Assert.IsFalse(published.State.HasPendingWork);
        Assert.AreEqual(Guid.Parse(continuation), published.State.LastSynchronization?.OperationId);
        var design = SchematicDesignXml.Read(await File.ReadAllTextAsync(stuck.DesignPath, token), []);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(shown.Electrical.Hierarchy.Data, design.Schematic, token), "The XML describes what KiCad shows.");
        var comparison = SchematicElectricalComparison.Compare(design, shown.Electrical, []);
        Assert.IsTrue(comparison.PinBindingsComplete && comparison.ConnectivityEquivalent, "KiCad's connections are the XML's.");
        Assert.AreEqual(SchematicDesignXml.Write(design, []), SchematicDesignXml.Write(published.State.Baseline, []), "The baseline is the published XML.");
        return new { stuck.Evidence, outcome = "keep-pending", continuationOperationId = continuation, published = true,
            receiptPath = view.GetProperty("receiptPath").GetString() };

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
    }

    // KiCad shows the same objects and connections as before the operation.
    private static void RequireSameDesign(CheckedSchematicState before, CheckedSchematicState now, string what)
    {
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(now.Electrical.Hierarchy.Data, before.Electrical.Hierarchy.Data), what + ": KiCad shows the design as the operation found it.");
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(before.Electrical.Hierarchy.Data, now.Electrical.Hierarchy.Data), what + ": KiCad shows nothing more.");
        CollectionAssert.AreEqual(RebuildPartition(before.Electrical), RebuildPartition(now.Electrical), what + ": the same connections.");
    }

    // The record observes the verified KiCad state with nothing pending; baseline, desired XML and XML file are unchanged.
    private static void RequireSettledOn(StoredDesignRecovery record, StuckSynchronization stuck, CheckedSchematicState shown, JsonElement view, string what)
    {
        Assert.IsFalse(record.State.HasPendingWork, what + ": nothing is pending.");
        Assert.AreEqual(view.GetProperty("recoveryRevisionToken").GetString(), record.RevisionToken);
        Assert.AreEqual(new KiCad.Automation.Model.DocumentRevision(shown.State.Revision.Epoch, shown.State.Revision.Sequence), record.State.NativeRevision, what + ": the record observes KiCad.");
        Assert.AreEqual(shown.Electrical.Hierarchy.Data, record.State.Observed);
        Assert.AreEqual(SchematicDesignXml.Write(stuck.Held.State.Baseline, []), SchematicDesignXml.Write(record.State.Baseline, []), what + ": the baseline did not move.");
        CollectionAssert.AreEqual(stuck.Held.State.DesiredFileBytes, record.State.DesiredFileBytes, what + ": the desired XML is unchanged.");
        CollectionAssert.AreEqual(stuck.Xml, File.ReadAllBytes(stuck.DesignPath), what + ": the XML file is unchanged.");
        var receipt = DesignPendingResolutions.Read(view.GetProperty("receiptPath").GetString()!);
        Assert.AreEqual(stuck.OperationId, receipt.OperationId);
        Assert.AreEqual(stuck.Held.RevisionToken, receipt.ResolvedFromRevisionToken);
        Assert.AreEqual(stuck.NativeOperationId, receipt.NativeOperationId, what + ": the receipt keeps the operation's native edit.");
    }

    // A KiCad started for a copy of the project in projectDirectory like a person would start it, attached to the given
    // production server with its schematic open, and a direct client of it. Disposing it kills that KiCad and removes the copy.
    private sealed class CopiedKiCad(Process process, string scratch, string copy, string id, DocumentSpecifier root, NativeClient client,
        Task<string> output, Task<string> errors) : IAsyncDisposable
    {
        public Process Process => process;
        public string Scratch => scratch;
        public string Copy => copy;
        public string Id => id;
        public DocumentSpecifier Root => root;
        public NativeClient Client => client;

        public async ValueTask DisposeAsync() => await Stop(process, scratch, output, errors);

        internal static async Task Stop(Process process, string scratch, Task<string> output, Task<string> errors)
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, errors);
            process.Dispose();
            try { Directory.Delete(scratch, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<CopiedKiCad> StartKiCadForCopy(StdioMcpFixture host, string projectDirectory, string display, string evidence,
        string instanceId, string label, CancellationToken token)
    {
        string scratch = Directory.CreateTempSubdirectory("kicad-" + label + "-").FullName;
        string copy = Directory.CreateDirectory(Path.Combine(scratch, "project")).FullName;
        foreach (string file in Directory.EnumerateFiles(projectDirectory).Where(f => Path.GetExtension(f) is ".kicad_sch" or ".kicad_pro"))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        string project = Path.Combine(copy, "fixture.kicad_pro");
        string id = Guid.NewGuid().ToString("D"), socket = Path.Combine(scratch, "api.sock");
        string executable = Path.Combine(FindRoot(), "automation", "artifacts", "native", "kicad", "kicad");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = copy, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["DISPLAY"] = display;
        start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(scratch, "config");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(scratch, "cache");
        foreach (string argument in new[] { "--new", "--automation", id, "--api-socket", socket,
                     "--automation-log", Path.Combine(evidence, instanceId + "-" + label + "-native.log"), "--software-rendering", project })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            string endpoint = NativeIpcEndpoint.FromSocketPath(socket);
            JsonElement attached;
            using (var ready = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                ready.CancelAfter(TimeSpan.FromSeconds(60));
                while (true)
                {
                    Assert.IsFalse(process.HasExited, "The KiCad for the copy exited before serving requests.");
                    attached = await host.Tool("kicad_instance_attach", new { endpoint, expectedInstanceId = id });
                    if (Error(attached) is not ("native_transport_unavailable" or "native_status_4" or "native_status_7")) break;
                    await Task.Delay(200, ready.Token);
                }
            }
            RequireToolSuccess(attached);
            Assert.AreEqual(process.Id, attached.GetProperty("structuredContent").GetProperty("processId").GetInt32());
            Assert.AreEqual("running", attached.GetProperty("structuredContent").GetProperty("processState").GetString(), attached.GetRawText());
            string epoch = attached.GetProperty("structuredContent").GetProperty("epoch").GetString()!;
            var root = await OpenCopiedSchematic(host, id, copy, token);
            var direct = new NativeClient(new NngTransport(), endpoint, epoch);
            Assert.AreEqual(id, (await direct.HandshakeAsync(token)).InstanceId);
            return new(process, scratch, copy, id, root, direct, output, errors);
        }
        catch
        {
            await CopiedKiCad.Stop(process, scratch, output, errors);
            throw;
        }
    }

    // Opens the copy's root schematic through the production server, waiting while KiCad is still starting or busy.
    private static async Task<DocumentSpecifier> OpenCopiedSchematic(StdioMcpFixture host, string id, string copy, CancellationToken token)
    {
        JsonElement opened;
        using (var ready = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            ready.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                opened = await host.Tool("kicad_schematic_open", new { instanceId = id, path = Path.Combine(copy, "fixture.kicad_sch") });
                if (Error(opened) is not ("native_status_4" or "native_status_7")) break;
                await Task.Delay(200, ready.Token);
            }
        }
        RequireToolSuccess(opened);
        return SchematicJson.Parser.Parse<DocumentSpecifier>(Text(opened));
    }

    // An automatic worker watching a KiCad that ends pauses at once with instance_exited (ledger pec2f1b53d4024a17). A KiCad is
    // started for a copy of the realized project like a person would start it, attached to the production server, and watched by
    // an automatic worker; once the worker watches, KiCad is killed. The server finds the exit by its process observer (it did
    // not start this KiCad) and the worker's status is paused with instance_exited within a second, while nothing needs KiCad.
    private static async Task<object> VerifyAutomaticPauseOnExit(StdioMcpFixture host, SchematicDesign design, string projectDirectory,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        await using var kicad = await StartKiCadForCopy(host, projectDirectory, display, evidence, instanceId, "exit-pause", token);
        var process = kicad.Process;
        string id = kicad.Id, copy = kicad.Copy, scratch = kicad.Scratch;
        var root = kicad.Root;
        var direct = kicad.Client;
        string? session = null;
        try
        {
            var shown = await direct.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new() { Document = root.Clone(), ProcessEpoch = direct.Epoch }, token);
            // The copy's record: the realized design as KiCad shows it for the copy, synchronized.
            var baseline = design with { Schematic = shown.Electrical.Hierarchy.Data.Clone() };
            string designPath = Path.Combine(copy, "design.xml");
            byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, []));
            await File.WriteAllBytesAsync(designPath, bytes, token);
            var store = new DesignRecoveryStore(Path.Combine(scratch, "recovery.json"));
            var record = store.Save(new(Guid.NewGuid(), Guid.Parse(id), new(shown.State.Revision.Epoch, shown.State.Revision.Sequence),
                shown.Electrical.Hierarchy.TrackingComplete, baseline, bytes, baseline.Schematic.Clone(), [],
                BaselineElectrical: shown.Electrical.Clone(), ObservedElectrical: shown.Electrical.Clone()), null);
            var started = await host.Tool("kicad_design_automatic_sync_start", new { instanceId = id, recoveryPath = store.StatePath, designPath,
                expectedRecoveryRevision = record.RevisionToken });
            RequireToolSuccess(started);
            session = started.GetProperty("structuredContent").GetProperty("sessionId").GetString()!;
            var status = started.GetProperty("structuredContent").GetProperty("status").Clone();
            using (var settle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                settle.CancelAfter(TimeSpan.FromSeconds(90));
                while (status.GetProperty("phase").GetString() != "Watching")
                {
                    Assert.IsFalse(status.GetProperty("phase").GetString() is "Paused" or "Stopped" or "InvalidDesign", status.GetRawText());
                    settle.Token.ThrowIfCancellationRequested();
                    status = await Wait(status);
                }
            }
            // KiCad dies while the worker is idle: after it first watches, it may still take in the events of its own first
            // synchronization (KiCad's save, the XML it wrote) without a status change. Three quiet seconds with the same
            // status and record let those settle, so the kill finds the worker waiting, not planning.
            for (int quiet = 0; ; quiet++)
            {
                Assert.IsTrue(quiet < 10, "The automatic worker never became idle: " + status.GetRawText());
                var record0 = store.Read()!.RevisionToken;
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                var listed = await host.Tool("kicad_design_automatic_sync_list", new { instanceId = id });
                RequireToolSuccess(listed);
                var now = listed.GetProperty("structuredContent").GetProperty("sessions").EnumerateArray()
                    .Single(s => s.GetProperty("sessionId").GetString() == session).GetProperty("status").Clone();
                Assert.AreEqual("Watching", now.GetProperty("phase").GetString(), now.GetRawText());
                if (now.GetProperty("sequence").GetUInt64() == status.GetProperty("sequence").GetUInt64()
                    && store.Read()!.RevisionToken == record0) break;
                status = now;
            }
            var watching = status;
            var settled = store.Read()!;
            Assert.IsFalse(settled.State.HasPendingWork);
            byte[] settledXml = await File.ReadAllBytesAsync(designPath, token);

            // Timed from the moment KiCad has ended, as its parent (this test) sees it: how long the kernel takes to tear a
            // killed KiCad down is not the server's to shorten. The kill itself is timed too, for the evidence.
            var killedAt = DateTimeOffset.UtcNow;
            var sinceKill = Stopwatch.StartNew();
            process.Kill();
            await process.WaitForExitAsync(token);
            double ended = sinceKill.Elapsed.TotalSeconds;
            var endedAt = DateTimeOffset.UtcNow;
            var sinceExit = Stopwatch.StartNew();
            var seen = new List<JsonElement>();
            while (!(status.GetProperty("phase").GetString() == "Paused" && status.GetProperty("errorCode").GetString() == "instance_exited"))
            {
                Assert.IsTrue(sinceExit.Elapsed < TimeSpan.FromSeconds(10), "The worker did not pause with instance_exited after its KiCad ended: "
                    + string.Join(", ", seen.Select(s => s.GetRawText())));
                status = await Wait(status);
                seen.Add(status);
            }
            double paused = sinceExit.Elapsed.TotalSeconds;
            var pausedAt = DateTimeOffset.UtcNow;
            var evidenceFile = Path.Combine(evidence, instanceId + "-exit-pause.json");
            await File.WriteAllTextAsync(evidenceFile, JsonSerializer.Serialize(new { instanceId = id, processId = process.Id, exitCode = process.ExitCode,
                killedAt, endedAt, pausedAt, endedAfterKillSeconds = Math.Round(ended, 3), pausedAfterExitSeconds = Math.Round(paused, 3),
                watchingSequence = watching.GetProperty("sequence").GetUInt64(),
                statusesAfterExit = seen.Select(s => JsonSerializer.Deserialize<JsonElement>(s.GetRawText())).ToArray() }), token);
            Assert.AreEqual("instance_exited", status.GetProperty("errorCode").GetString(), status.GetRawText());
            StringAssert.Contains(status.GetProperty("errorMessage").GetString(), id, status.GetRawText());
            Assert.IsTrue(status.GetProperty("reattachRequired").GetBoolean(), "Resuming this worker cannot work: KiCad must be started again and the record reattached.");
            Assert.IsTrue(paused < 1.0, $"The worker paused {paused:F2} s after KiCad ended ({ended:F2} s after it was killed); within a second is required.");
            Assert.AreEqual(settled.RevisionToken, store.Read()!.RevisionToken, "The recovery record is unchanged.");
            CollectionAssert.AreEqual(settledXml, await File.ReadAllBytesAsync(designPath, token), "The XML is unchanged.");
            var resume = await host.Tool("kicad_design_automatic_sync_resume", new { instanceId = id, sessionId = session,
                expectedSequence = status.GetProperty("sequence").GetUInt64() });
            Assert.AreEqual("automatic_sync_reattach_required", Error(resume), resume.GetRawText());
            var stopped = await host.Tool("kicad_design_automatic_sync_stop", new { instanceId = id, sessionId = session });
            RequireToolSuccess(stopped);
            session = null;
            var result = new { instanceId = id, processId = process.Id, exitCode = process.ExitCode, watchingSequence = watching.GetProperty("sequence").GetUInt64(),
                killedAt, endedAt, pausedAt, endedAfterKillSeconds = Math.Round(ended, 3), pausedAfterExitSeconds = Math.Round(paused, 3),
                statusesAfterExit = seen.Select(s => JsonSerializer.Deserialize<JsonElement>(s.GetRawText())).ToArray(),
                resumeRefused = Error(resume), recordUnchanged = true, xmlUnchanged = true };
            await File.WriteAllTextAsync(evidenceFile, JsonSerializer.Serialize(result), token);
            return result;

            async Task<JsonElement> Wait(JsonElement current)
            {
                var next = await host.Tool("kicad_design_automatic_sync_wait", new { instanceId = id, sessionId = session,
                    afterSequence = current.GetProperty("sequence").GetUInt64() });
                RequireToolSuccess(next);
                return next.GetProperty("structuredContent").GetProperty("status").Clone();
            }
        }
        finally
        {
            if (session is not null)
                try { await host.Tool("kicad_design_automatic_sync_stop", new { instanceId = id, sessionId = session }); }
                catch (IOException) { }
        }
    }

    // Keep-and-replan when KiCad's connections really differ from the XML (ledger p0aa59a1dfc8701ea), after KiCad saved and
    // reloaded its sheets, and the publication of a kept result that can be neither undone nor discarded while KiCad shows it.
    // A KiCad is started for a copy of the realized project, and its record holds that design with a requirement on each of two
    // nets whose labels one straight wire can join on a sheet below the root. The XML then adds a resistor on the root sheet,
    // and that creation is forced stuck as in ForceStuckSynchronization: KiCad commits it and the scripted check refuses it.
    // The person draws the wire in KiCad, so KiCad joins the two nets the XML keeps apart, saves the sheets (Ctrl+S) and
    // reloads them (File > Revert), so KiCad's receipt of the creation is gone (session-ended). Discard and undo are refused
    // and name keep-and-replan; keep-and-replan keeps what KiCad shows: both nets are reported merged into one net named as
    // KiCad names it, and their
    // requirements become unresolved net bindings. While that result waits for its publication, discard and undo of the
    // publication are refused (kept_result_pending). Completing it publishes the XML with KiCad's joined net and the resistor;
    // KiCad, the record and the XML then agree, with nothing pending.
    private static async Task<object> VerifyKeptConnectionsWin(StdioMcpFixture host, SchematicDesign design, string projectDirectory,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        await using var kicad = await StartKiCadForCopy(host, projectDirectory, display, evidence, instanceId, "kept-connections", token);
        var client = kicad.Client;
        var root = kicad.Root;
        string id = kicad.Id;
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        async Task<CheckedSchematicState> Until(string what, Func<CheckedSchematicState, bool> done)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                try
                {
                    var state = await Capture();
                    if (done(state)) return state;
                }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                try { await Task.Delay(250, wait.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new AssertFailedException("KiCad never " + what + "."); }
            }
        }
        var shown = await Capture();
        var circuit = design.Engineering.Circuit;

        // The wire the person will draw, and the copy's record: the realized design as KiCad shows it for the copy, with a
        // requirement on each of the two nets it joins.
        var wire = await ChooseJoiningWire(client, shown, circuit, token);
        var (first, second) = (circuit.Nets.Single(n => n.Name == wire.First), circuit.Nets.Single(n => n.Name == wire.Second));
        EngineeringStatement Requirement(CircuitNet net) => new(Guid.NewGuid(), net.Id, EngineeringStatementRole.Intent, GuidanceStrength.Requirement,
            net.Name + " stays its own net.", null, [], []);
        var requirements = new[] { Requirement(first), Requirement(second) };
        var structure = design.Engineering.Structure;
        var baseline = design with { Schematic = shown.Electrical.Hierarchy.Data.Clone(), Engineering = design.Engineering with
            { Structure = structure with { Statements = [.. structure.Statements, .. requirements] } } };
        string designPath = Path.Combine(kicad.Copy, "design.xml");
        byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, []));
        await File.WriteAllBytesAsync(designPath, bytes, token);
        var store = new DesignRecoveryStore(Path.Combine(kicad.Scratch, "recovery.json"));
        var record = store.Save(new(Guid.NewGuid(), Guid.Parse(id), new(shown.State.Revision.Epoch, shown.State.Revision.Sequence),
            shown.Electrical.Hierarchy.TrackingComplete, baseline, bytes, baseline.Schematic.Clone(), [],
            BaselineElectrical: shown.Electrical.Clone(), ObservedElectrical: shown.Electrical.Clone()), null);

        // The XML adds a resistor like R1 on a free spot of the root sheet; its creation is forced stuck.
        var spot = await FreeSymbolSpot(client, shown, token);
        var r1 = circuit.Components.Single(c => c.Reference == "R1");
        var r1Definition = circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == r1.DefinitionId);
        var rootSheet = circuit.SheetInstances.Single(s => s.ParentId is null);
        Guid definitionId = Guid.NewGuid(), componentId = Guid.NewGuid(), occurrenceId = Guid.NewGuid();
        var withResistor = baseline with { Engineering = baseline.Engineering with { Circuit = circuit with
        {
            Sheets = [.. circuit.Sheets.Select(s => s.Id != rootSheet.DefinitionId ? s
                : s with { Components = [.. s.Components, new ComponentDefinition(definitionId, r1Definition.PartId, r1Definition.Value)] })],
            Components = [.. circuit.Components, new ComponentInstance(componentId, definitionId, rootSheet.Id, "R2")],
            Symbols = [.. circuit.Symbols, new SymbolOccurrence(occurrenceId, componentId, 1,
                new SymbolPlacement(spot.XNm / 1_000_000m, spot.YNm / 1_000_000m, 0, false, false, false))]
        } } };
        byte[] desired = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(withResistor, []));
        await File.WriteAllBytesAsync(designPath, desired, token);
        store.Save(record.State with { DesiredFileBytes = desired }, record.RevisionToken);
        var stuck = await ForceStuckSynchronization(client, root, store, designPath, id, "creation-check", "native_sync_connectivity_mismatch",
            evidence, token);

        // The person draws the wire: KiCad joins exactly the two nets.
        var committed = await Capture();
        Assert.AreEqual(stuck.Committed.State, committed.State);
        var line = new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") }, Type = SchematicLineType.SltWire,
            Start = wire.Start.Clone(), End = wire.End.Clone(), Locked = LockedState.LsUnlocked };
        var edit = new ApplySchematicItemBatch { Document = committed.State.Document.Clone(), DocumentEpoch = committed.State.Revision.Epoch,
            ExpectedRevision = committed.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), Description = "Draw wire" };
        edit.Operations.Add(new SchematicItemOperation { TargetDocument = wire.Sheet.Clone(), Create = Google.Protobuf.WellKnownTypes.Any.Pack(line) });
        var drawn = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(new() { Batch = edit, ExpectedState = committed.State.Clone() }, token);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, drawn.Status, drawn.ToString());
        var wired = await Capture();
        string[] apart = RebuildPartition(committed.Electrical), together = RebuildPartition(wired.Electrical);
        var joinedGroups = apart.Except(together).ToArray();
        var joinedGroup = together.Except(apart).ToArray();
        Assert.HasCount(2, joinedGroups, "The wire joins exactly two nets: " + string.Join(" | ", joinedGroups));
        Assert.HasCount(1, joinedGroup, "The wire makes exactly one new net.");
        var joinedItems = joinedGroups.SelectMany(g => g.Split(' ')).ToHashSet(StringComparer.Ordinal);
        var newItems = joinedGroup[0].Split(' ').ToHashSet(StringComparer.Ordinal);
        var earlierItems = apart.SelectMany(g => g.Split(' ')).ToHashSet(StringComparer.Ordinal);
        var added = newItems.Where(item => !joinedItems.Contains(item)).ToArray();
        Assert.IsTrue(joinedItems.All(item => newItems.Contains(item)), "The new net holds both nets.");
        Assert.IsFalse(added.Any(item => earlierItems.Contains(item)), "The wire joins nothing else: " + string.Join(", ", added));

        // The person saves KiCad's sheets (Ctrl+S) and reloads them from disk (File > Revert): a new document session, whose
        // receipts hold nothing of the creation.
        await FocusedSchematicShortcut(client, root, kicad.Process.Id, display, "s", token);
        await Until("saved its sheets after Ctrl+S", s => !s.State.NativeContentDirty && s.State.Revision.Epoch == wired.State.Revision.Epoch);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await client.InvokeAsync<Kiapi.Common.Commands.RevertDocument, Google.Protobuf.WellKnownTypes.Empty>(new() { Document = root.Clone() }, token);
                break;
            }
            catch (NativeApiException error) when (error.Status is 4 or 7 && attempt < 40) { await Task.Delay(250, token); }
        }
        var reloaded = await Until("reloaded its sheets", s => s.State.Revision.Epoch != wired.State.Revision.Epoch);
        Assert.IsFalse(reloaded.State.NativeContentDirty, "KiCad shows the sheets it saved.");
        CollectionAssert.AreEqual(together, RebuildPartition(reloaded.Electrical), "KiCad reloaded the sheets it saved, with the wire.");
        CollectionAssert.AreEqual(stuck.Xml, await File.ReadAllBytesAsync(designPath, token), "The XML still waits.");

        // Discard and undo cannot tell whether KiCad holds the creation's result, and name keep-and-replan.
        string Message(JsonElement reply) => reply.GetProperty("structuredContent").GetProperty("errorMessage").GetString()!;
        var discard = await ResolvePending(host, id, store, stuck, "discard", evidence, token, label: "session-ended");
        Assert.AreEqual("operation_outcome_unknown", Error(discard), discard.GetRawText());
        StringAssert.Contains(Message(discard), "keep-and-replan");
        var undo = await ResolvePending(host, id, store, stuck, "undo", evidence, token, label: "session-ended");
        Assert.AreEqual("native_operation_not_committed", Error(undo), undo.GetRawText());
        StringAssert.Contains(Message(undo), "keep-and-replan");
        Assert.AreEqual(stuck.Held.RevisionToken, store.Read()!.RevisionToken, "The refusals change nothing in the record.");
        Assert.AreEqual(reloaded.State, (await Capture()).State, "The refusals send nothing to KiCad.");

        // Keep-and-replan: KiCad's connections win.
        var reply = await ResolvePending(host, id, store, stuck, "keep-and-replan", evidence, token);
        RequireToolSuccess(reply);
        var view = reply.GetProperty("structuredContent").Clone();
        Assert.AreEqual("keep-pending", view.GetProperty("outcome").GetString(), view.GetRawText());
        Assert.AreEqual("session-ended", view.GetProperty("nativeStatus").GetString(), view.GetRawText());
        Assert.IsTrue(view.GetProperty("continuationPending").GetBoolean(), view.GetRawText());
        var changes = view.GetProperty("netChanges").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, changes.Select(c => c.GetProperty("formerNetId").GetGuid()).ToArray(),
            "Both joined nets are reported: " + view.GetRawText());
        Assert.IsTrue(changes.All(c => c.GetProperty("change").GetString() == "Merged"), view.GetRawText());
        var joinedNet = changes.SelectMany(c => c.GetProperty("candidateNetIds").EnumerateArray().Select(n => n.GetGuid())).Distinct().Single();
        string continuation = view.GetProperty("continuationOperationId").GetString()!;
        string requested = view.GetProperty("requestedRecoveryRevisionToken").GetString()!;
        Assert.AreEqual(reloaded.State, (await Capture()).State, "Keeping sends nothing to KiCad.");
        CollectionAssert.AreEqual(stuck.Xml, await File.ReadAllBytesAsync(designPath, token), "The XML waits for the continuation.");
        var kept = store.Read()!;
        Assert.AreEqual(Guid.Parse(continuation), kept.State.PendingPublication?.OperationId);
        Assert.IsNull(kept.State.PendingMutation, "The continuation sends no native edit.");
        Assert.AreEqual(reloaded.State, kept.State.PendingNativeState, "The continuation is guarded by exactly what KiCad shows.");
        Assert.AreEqual(view.GetProperty("resultRevisionToken").GetString(), kept.RevisionToken, "The receipt names the record revision it produced.");

        // While the kept result waits for its publication, it can be neither undone nor discarded: KiCad still shows it.
        var publication = stuck with { OperationId = Guid.Parse(continuation), Held = kept };
        var undoKept = await ResolvePending(host, id, store, publication, "undo", evidence, token, label: "refused");
        Assert.AreEqual("kept_result_pending", Error(undoKept), undoKept.GetRawText());
        var discardKept = await ResolvePending(host, id, store, publication, "discard", evidence, token, label: "refused");
        Assert.AreEqual("kept_result_pending", Error(discardKept), discardKept.GetRawText());
        StringAssert.Contains(Message(discardKept), "kicad_design_sync_apply");
        Assert.AreEqual(kept.RevisionToken, store.Read()!.RevisionToken, "The refusals change nothing in the record.");
        Assert.AreEqual(reloaded.State, (await Capture()).State, "The refusals send nothing to KiCad.");

        // Completing the publication saves KiCad's sheets and publishes the XML with KiCad's joined net.
        var completed = await host.Tool("kicad_design_sync_apply", new { instanceId = id, recoveryPath = store.StatePath, designPath,
            expectedRevisionToken = requested, operationId = continuation });
        await File.WriteAllTextAsync(Path.Combine(evidence, id + "-kept-connections-continuation.json"), RetainedToolEvidence(completed), token);
        RequireToolSuccess(completed);
        var result = completed.GetProperty("structuredContent");
        Assert.IsFalse(result.GetProperty("nativeMutationCommitted").GetBoolean(), completed.GetRawText());
        Assert.IsTrue(result.GetProperty("nativeFilesSaved").GetBoolean(), completed.GetRawText());
        Assert.IsTrue(result.GetProperty("synchronizationCommitted").GetBoolean(), completed.GetRawText());
        var final = await Capture();
        Assert.IsFalse(final.State.NativeContentDirty, "KiCad's kept result is saved.");
        var published = store.Read()!;
        Assert.IsFalse(published.State.HasPendingWork);
        Assert.AreEqual(Guid.Parse(continuation), published.State.LastSynchronization?.OperationId);
        var xml = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
        var nets = xml.Engineering.Circuit.Nets;
        var joined = nets.Single(n => n.Id == joinedNet);
        CollectionAssert.IsSubsetOf(first.Pins.Concat(second.Pins).ToArray(), joined.Pins.ToArray(), "The XML holds KiCad's joined net.");
        Assert.IsFalse(nets.Any(n => n.Id == first.Id || n.Id == second.Id), "The two nets KiCad joined are gone from the XML.");
        foreach (var other in circuit.Nets.Where(n => n.Id != first.Id && n.Id != second.Id))
            CollectionAssert.AreEquivalent(other.Pins.ToArray(), nets.Single(n => n.Id == other.Id).Pins.ToArray(), other.Name + " is unchanged.");
        Assert.IsTrue(xml.Engineering.Circuit.Components.Any(c => c.Id == componentId && c.Reference == "R2"), "The XML holds the created resistor.");
        Assert.IsTrue(xml.Engineering.Structure.HasUnresolvedNetBindings, "The joined nets' requirements wait for resolution.");
        var bindings = xml.Engineering.Structure.UnresolvedNetBindings!;
        CollectionAssert.AreEquivalent(requirements.Select(r => (r.Id, r.TargetId)).ToArray(), bindings.Select(b => (b.OwnerId, b.FormerNetId)).ToArray());
        Assert.IsTrue(bindings.All(b => b.CandidateNetIds.SequenceEqual([joinedNet])), "Each requirement names the joined net as its candidate.");
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(final.Electrical.Hierarchy.Data, xml.Schematic, token), "The XML describes what KiCad shows.");
        var comparison = SchematicElectricalComparison.Compare(xml, final.Electrical, []);
        Assert.IsTrue(comparison.PinBindingsComplete && comparison.ConnectivityEquivalent, "KiCad's connections are the XML's.");
        Assert.AreEqual(SchematicDesignXml.Write(xml, []), SchematicDesignXml.Write(published.State.Baseline, []), "The baseline is the published XML.");

        var proof = new
        {
            instanceId = id, sheet = string.Join('/', wire.Sheet.SheetPath.Path.Select(p => p.Value)), joinedNets = new[] { first.Name, second.Name },
            wire = new { startNm = new[] { wire.Start.XNm, wire.Start.YNm }, endNm = new[] { wire.End.XNm, wire.End.YNm } },
            resistorAtNm = new[] { spot.XNm, spot.YNm }, stuck.Evidence, reloadedEpoch = reloaded.State.Revision.Epoch,
            refusedAfterReload = new { discard = Error(discard), undo = Error(undo) }, keptStatus = "session-ended",
            netChanges = changes.Select(c => JsonSerializer.Deserialize<JsonElement>(c.GetRawText())).ToArray(), joinedNetId = joinedNet,
            joinedNetName = joined.Name, refusedWhileKept = new { undo = Error(undoKept), discard = Error(discardKept) },
            continuationOperationId = continuation, unresolvedNetBindings = bindings.Count, receiptPath = view.GetProperty("receiptPath").GetString()
        };
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-kept-connections.json"), JsonSerializer.Serialize(proof), token);
        return proof;
    }

    private sealed record JoiningWire(DocumentSpecifier Sheet, string First, string Second, Vector2 Start, Vector2 End);

    // The shortest straight wire between labels of two different XML nets on one sheet below the root that touches no other
    // connection point: no wire end, label, junction, no-connect marker, sheet pin or symbol pin lies on it, so drawing it joins
    // exactly those two nets.
    private static async Task<JoiningWire> ChooseJoiningWire(NativeClient client, CheckedSchematicState shown, Circuit circuit, CancellationToken token)
    {
        var names = circuit.Nets.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        (JoiningWire Wire, long Length)? best = null;
        foreach (var screen in shown.Electrical.Hierarchy.Data.Instances.Where(s => s.Metadata.Document.SheetPath.Path.Count > 1))
        {
            var labels = new List<(string Text, Vector2 At)>();
            var points = new List<Vector2>();
            foreach (var item in screen.Items)
            {
                if (item.Is(SchematicLine.Descriptor)) { var wire = item.Unpack<SchematicLine>(); points.Add(wire.Start); points.Add(wire.End); }
                else if (item.Is(LocalLabel.Descriptor)) { var label = item.Unpack<LocalLabel>(); points.Add(label.Position); labels.Add((label.Text.Text_, label.Position)); }
                else if (item.Is(HierarchicalLabel.Descriptor)) { var label = item.Unpack<HierarchicalLabel>(); points.Add(label.Position); labels.Add((label.Text.Text_, label.Position)); }
                else if (item.Is(GlobalLabel.Descriptor)) points.Add(item.Unpack<GlobalLabel>().Position);
                else if (item.Is(Junction.Descriptor)) points.Add(item.Unpack<Junction>().Position);
                else if (item.Is(NoConnectMarker.Descriptor)) points.Add(item.Unpack<NoConnectMarker>().Position);
                else if (item.Is(SheetSymbol.Descriptor)) points.AddRange(item.Unpack<SheetSymbol>().Pins.Select(p => p.Position));
            }
            var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
                { Document = screen.Metadata.Document.Clone(), ExpectedRevision = shown.State.Revision.Clone() }, token);
            Assert.IsTrue(measured.PinGeometryAvailable, "KiCad measures the symbol pins of every sheet.");
            points.AddRange(measured.Obstacles.Where(o => o.SymbolPins is not null).SelectMany(o => o.SymbolPins.Pins).Select(p => p.Position));
            foreach (var a in labels.Where(l => names.Contains(l.Text)))
                foreach (var b in labels.Where(l => names.Contains(l.Text) && string.CompareOrdinal(l.Text, a.Text) > 0))
                {
                    long dx = b.At.XNm - a.At.XNm, dy = b.At.YNm - a.At.YNm, length = dx * dx / 1000 + dy * dy / 1000;
                    if (length == 0 || best is { } shortest && length >= shortest.Length) continue;
                    bool At(Vector2 p, Vector2 q) => p.XNm == q.XNm && p.YNm == q.YNm;
                    bool On(Vector2 p) => (p.XNm - a.At.XNm) * dy == (p.YNm - a.At.YNm) * dx
                        && p.XNm >= Math.Min(a.At.XNm, b.At.XNm) && p.XNm <= Math.Max(a.At.XNm, b.At.XNm)
                        && p.YNm >= Math.Min(a.At.YNm, b.At.YNm) && p.YNm <= Math.Max(a.At.YNm, b.At.YNm);
                    if (points.Any(p => !At(p, a.At) && !At(p, b.At) && On(p))) continue;
                    best = (new JoiningWire(screen.Metadata.Document.Clone(), a.Text, b.Text, a.At.Clone(), b.At.Clone()), length);
                }
        }
        return best?.Wire ?? throw new AssertFailedException("No two XML nets have labels on one sheet that a straight wire joins without touching anything else.");
    }

    // A spot on the root sheet with room for a resistor and its fields, clear of everything KiCad measured there, inside the
    // page (10 mm inset, the bottom 50 mm kept for the title block), on the connection grid.
    private static async Task<Vector2> FreeSymbolSpot(NativeClient client, CheckedSchematicState shown, CancellationToken token)
    {
        var rootScreen = shown.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.SheetPath.Path.Count == 1);
        var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
            { Document = rootScreen.Metadata.Document.Clone(), ExpectedRevision = shown.State.Revision.Clone() }, token);
        const long Grid = 2_540_000, Clearance = 2_540_000;
        long left = measured.PageBounds.Position.XNm, top = measured.PageBounds.Position.YNm;
        long right = left + measured.PageBounds.Size.XNm - 10_000_000, bottom = top + measured.PageBounds.Size.YNm - 50_000_000;
        static (long L, long T, long R, long B) Rect(Box2 b) => (b.Position.XNm, b.Position.YNm, b.Position.XNm + b.Size.XNm, b.Position.YNm + b.Size.YNm);
        for (long y = top + 8 * Grid; y + 5 * Grid <= bottom; y += 2 * Grid)
            for (long x = left + 8 * Grid; x + 8 * Grid <= right; x += 2 * Grid)
            {
                var area = (L: x - 4 * Grid, T: y - 5 * Grid, R: x + 8 * Grid, B: y + 5 * Grid);
                if (measured.Obstacles.All(o =>
                    {
                        var box = Rect(o.Bounds);
                        return area.R + Clearance < box.L || area.L - Clearance > box.R || area.B + Clearance < box.T || area.T - Clearance > box.B;
                    }))
                    return new() { XNm = x, YNm = y };
            }
        throw new AssertFailedException("The root sheet has no free room for a resistor.");
    }
}
