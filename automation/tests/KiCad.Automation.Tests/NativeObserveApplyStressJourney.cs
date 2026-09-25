using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // When the harness, acting as a person at the keyboard of the same KiCad window, edits the design relative to one
    // agent iteration of observe (kicad_schematic_checked_view) and apply (kicad_schematic_apply_checked_batch).
    private enum StressTiming
    {
        // The batch applies.
        NoPersonEdit,
        // The person's rotation lands before the view, which shows it; the batch applies.
        BeforeObservation,
        // The rotation key is pressed while the view is being captured: the view shows the rotation or does not, and the
        // batch applies or is refused accordingly.
        DuringObservation,
        // The rotation lands after the view: the batch is stale.
        BeforeApply,
        // A rotation and the person's Ctrl+Z land after the view: the content matches the view again but the revision
        // does not, so the batch is still stale.
        RotateThenUndo,
        // The person saves (Ctrl+S) after the view: the batch is stale and the design stays saved.
        SaveBeforeApply,
        // The rotation key is pressed while the batch is on its way: whichever KiCad handles first decides.
        DuringApply,
        // The agent sends a second batch planned from the same view after its first one applied: the second is stale.
        ReusedObservation
    }

    // One batch the agent planned from one view, with exactly what it asks KiCad to create or change (null: remove).
    private sealed record StressBatch(int Iteration, CheckedSchematicView Observed, CheckedSchematicBatch Request,
        IReadOnlyDictionary<string, IMessage?> Expected, IReadOnlyList<string> Created, IReadOnlyList<string> Removed);

    // An agent observes and edits the live schematic only through the compiled MCP STDIO server, 50 times, while the
    // harness makes real keyboard edits in the same KiCad window (rotate the selected note, undo, save) at varying moments,
    // including while the agent's view is being captured and while its batch is on its way (item observe-apply-stress,
    // ledger p60776bb2239d1087). Every view's image and state describe one revision; every batch planned from an
    // out-of-date view is refused as stale_document_state and changes nothing; every accepted batch is exactly one undo
    // step holding exactly the requested change; nothing hangs; and undoing every step through the keyboard returns
    // KiCad to exactly where the run started, with nothing else left on the undo stack. The native reads here are the
    // harness's own independent view of KiCad; every agent observation and edit goes through the MCP tools.
    private static async Task VerifyObserveApplyStress(NativeClient client, DocumentSpecifier document, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        const int Iterations = 50;
        const string PersonNote = "Native automation fixture";
        var run = Stopwatch.StartNew();
        string documentJson = SchematicJson.Formatter.Format(document), epoch = "", personNoteId = "";
        // A seed tied to the instance, so a failing schedule can be replayed from the evidence.
        int seed = BitConverter.ToInt32(Guid.Parse(instanceId).ToByteArray(), 0);
        var random = new Random(seed);
        var timings = new SortedDictionary<string, List<double>>(StringComparer.Ordinal)
        {
            ["view"] = [], ["recapture"] = [], ["applyAccepted"] = [], ["applyRefused"] = [], ["personEdit"] = [],
            ["iteration"] = [], ["undoStep"] = []
        };
        var records = new List<Dictionary<string, object?>>();
        var accepted = new List<(StressBatch Batch, CheckedSchematicBatchReceipt Receipt)>();
        var refused = new List<(StressBatch Batch, CheckedSchematicBatchReceipt Receipt)>();
        int viewRetries = 0, rotations = 0, personUndos = 0, personSaves = 0, undoSteps = 0;
        bool completed = false;
        var schedule = new List<StressTiming>();
        foreach (var (timing, count) in new[] { (StressTiming.NoPersonEdit, 5), (StressTiming.BeforeObservation, 6),
                     (StressTiming.DuringObservation, 7), (StressTiming.BeforeApply, 8), (StressTiming.RotateThenUndo, 5),
                     (StressTiming.SaveBeforeApply, 4), (StressTiming.DuringApply, 10), (StressTiming.ReusedObservation, 4) })
            schedule.AddRange(Enumerable.Repeat(timing, count));
        var shuffled = schedule.ToArray();
        random.Shuffle(shuffled);
        // The first iteration edits the saved design, so every later refusal meets a design with unsaved work.
        schedule = [StressTiming.NoPersonEdit, .. shuffled];
        Assert.HasCount(Iterations, schedule);

        // Every native read has its own deadline: a stuck KiCad fails the step that waited, never the whole ceiling.
        async Task<T> Native<T>(Func<CancellationToken, Task<T>> call, string what, int seconds = 15)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            try { return await call(limit.Token); }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !token.IsCancellationRequested)
            {
                throw new AssertFailedException($"{what} did not answer within {seconds} s: KiCad is stuck.");
            }
        }
        Task<DocumentLifecycleState> State() => Native(t => client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, t), "Reading the document state");
        Task<SchematicSaveState> Flags() => Native(t => client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, t), "Reading the unsaved-change flags");
        Task<CheckedSchematicState> Checked() => Native(t => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = document, ProcessEpoch = client.Epoch }, t), "Reading the checked state");
        async Task<SchematicChangeJournal> ReadJournal(ulong after, CancellationToken limit)
        {
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = document, DocumentEpoch = epoch, AfterSequence = after }, limit);
            Assert.IsFalse(journal.ResetRequired, "KiCad's change history must still hold every step since the run started.");
            return journal;
        }
        Task<SchematicChangeJournal> Journal(ulong after) => Native(t => ReadJournal(after, t), "Reading the change history");
        Dictionary<string, Any> Root(CheckedSchematicState state) => ItemsByUuid(RootSheet(state).Items);
        SchematicScreenData RootSheet(CheckedSchematicState state) => state.Electrical.Hierarchy.Data.Instances
            .Single(screen => Equals(screen.Metadata.Document?.SheetPath, document.SheetPath));
        async Task Screenshot(string name)
        {
            try { await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-stress-{name}.png"), CancellationToken.None); }
            catch (Exception error) { Console.Error.WriteLine($"Stress failure capture {name} unavailable: {error.Message}"); }
        }
        async Task<Dictionary<string, byte[]>> ProjectFiles()
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(document.Project.Path)
                         .Where(path => Path.GetExtension(path) is ".kicad_sch" or ".kicad_pro").Order(StringComparer.Ordinal))
                files[path] = await File.ReadAllBytesAsync(path, token);
            return files;
        }

        // The person. A keyboard edit needs the canvas to have keyboard focus; the note to rotate is selected first,
        // as a person would click it, because a background click would clear the selection instead.
        async Task EnsureCanvasFocus()
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            bool clicked = false;
            while (true)
            {
                try
                {
                    var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                        new() { Document = document }, limit.Token);
                    if (observation.Preview.Viewport.CanvasHasKeyboardFocus) return;
                    if (!clicked) { NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false); clicked = true; }
                }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                await Task.Delay(100, limit.Token);
            }
        }
        async Task SelectPersonNote()
        {
            var header = new ItemHeader { Document = document };
            await Native(t => client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, t), "Clearing the selection");
            var select = new AddToSelection { Header = header };
            select.Items.Add(new KIID { Value = personNoteId });
            var selection = await Native(t => client.InvokeAsync<AddToSelection, SelectionResponse>(select, t), "Selecting the person's note");
            Assert.AreEqual(1, selection.Items.Count, "The person's note is selected before the keyboard edit.");
        }
        Stopwatch Press(string key, bool control)
        {
            var sent = Stopwatch.StartNew();
            NativeKeyboard.SchematicShortcut(display, processId, key, controlKey: control, focusCanvas: false);
            return sent;
        }
        // The person's own step in KiCad's history: an entry of this kind after the given revision, without an operation ID.
        async Task<SchematicChange> PersonChange(ulong after, SchematicChange.Types.Kind kind, Stopwatch sent, string what, bool timed = true)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    var journal = await ReadJournal(after, limit.Token);
                    if (journal.Changes.FirstOrDefault(entry => entry.Kind == kind && entry.OperationId.Length == 0) is { } change)
                    {
                        if (timed) timings["personEdit"].Add(sent.Elapsed.TotalMilliseconds);
                        return change;
                    }
                    await Task.Delay(20, limit.Token);
                }
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !token.IsCancellationRequested)
            {
                await Screenshot("missing-" + kind.ToString().ToLowerInvariant());
                throw new AssertFailedException($"{what}: the person's {kind} key did not reach KiCad's history within 5 s.");
            }
        }
        async Task<SchematicChange> Rotate(ulong after, string what)
        {
            await SelectPersonNote();
            var sent = Press("r", false);
            rotations++;
            return await PersonChange(after, SchematicChange.Types.Kind.Commit, sent, what);
        }
        async Task AwaitSaved(Stopwatch sent, string what)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    var state = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, limit.Token);
                    if (!state.NativeContentDirty && state.FileBaselines.All(file => file.Status == NativeFileBaselineStatus.NfbsUnchanged))
                    {
                        timings["personEdit"].Add(sent.Elapsed.TotalMilliseconds);
                        return;
                    }
                    await Task.Delay(20, limit.Token);
                }
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !token.IsCancellationRequested)
            {
                await Screenshot("missing-save");
                throw new AssertFailedException($"{what}: the person's Ctrl+S did not save the design within 5 s.");
            }
        }
        // With nothing on KiCad's undo stack Ctrl+Z changes nothing. KiCad handles keys in the order they arrive, so once a
        // rotation pressed after it reaches the history, the Ctrl+Z was handled first: the history then holds that rotation alone.
        async Task ProveUndoStackEmpty(string when, string digest)
        {
            await SelectPersonNote();
            var before = await State();
            Press("z", true);
            var rotation = await PersonChange(before.Revision.Sequence, SchematicChange.Types.Kind.Commit, Press("r", false), when, timed: false);
            var steps = (await Journal(before.Revision.Sequence)).Changes;
            Assert.HasCount(1, steps, $"{when}: Ctrl+Z found nothing to undo, so only the later rotation reached KiCad's history.");
            Assert.AreEqual(SchematicChange.Types.Kind.Commit, steps[0].Kind, when);
            // Undo the probe rotation, so the design is exactly as it was.
            await PersonChange(rotation.Sequence, SchematicChange.Types.Kind.Undo, Press("z", true), when, timed: false);
            Assert.AreEqual(digest, (await State()).StateSha256, $"{when}: undoing the probe rotation restores the design.");
        }
        // The person's rotation key and the agent's request race for KiCad's event loop. The key is pressed at an offset
        // from the moment the agent's request is sent (negative: the key first). After each race the offset moves one step
        // toward the other order (an up-and-down staircase with a little jitter), so the races keep landing where KiCad
        // handles the key and the request in either order.
        const int RaceStep = 20;
        var raceOffsets = new Dictionary<StressTiming, int> { [StressTiming.DuringObservation] = -60, [StressTiming.DuringApply] = -60 };
        async Task<(T Result, Stopwatch Sent, int Offset)> Race<T>(StressTiming timing, Func<Task<T>> agent)
        {
            int offset = raceOffsets[timing] + random.Next(-5, 6);
            Task<T> running;
            Stopwatch sent;
            if (offset < 0)
            {
                sent = Press("r", false);
                await Task.Delay(-offset, token);
                running = agent();
            }
            else
            {
                running = agent();
                if (offset > 0) await Task.Delay(offset, token); else await Task.Yield();
                sent = Press("r", false);
            }
            rotations++;
            return (await running, sent, offset);
        }
        void Raced(StressTiming timing, bool personFirst) => raceOffsets[timing] += personFirst ? RaceStep : -RaceStep;

        // The agent, only through the compiled MCP STDIO server. Every call has its own deadline.
        await using var mcp = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-stress-mcp"),
            Path.Combine(evidence, instanceId + "-stress-mcp.stderr.log"), token);
        async Task<JsonElement> Tool(string name, object arguments, string what)
        {
            try { return await mcp.Tool(name, arguments).WaitAsync(TimeSpan.FromSeconds(30), token); }
            catch (TimeoutException) { throw new AssertFailedException($"{what}: {name} did not answer within 30 s; the agent's call is stuck."); }
        }
        RequireToolSuccess(await Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }, "Attaching the agent"));
        // The agent's view: the rendered sheet and the checked state it plans from, returned together.
        async Task<(CheckedSchematicView View, byte[] Png)> View(string what)
        {
            for (int attempt = 1; ; attempt++)
            {
                var watch = Stopwatch.StartNew();
                var reply = await Tool("kicad_schematic_checked_view", new { instanceId, documentJson }, what);
                double elapsed = watch.Elapsed.TotalMilliseconds;
                if (reply.TryGetProperty("isError", out var failed) && failed.GetBoolean())
                {
                    // Only a change or a busy editor during the capture may refuse it; the agent then observes again.
                    string? code = reply.TryGetProperty("structuredContent", out var refusal) && refusal.TryGetProperty("code", out var value)
                        ? value.GetString() : null;
                    Assert.IsTrue(code is "native_status_4" or "native_status_7", $"{what}: the checked view failed: {reply.GetRawText()}");
                    Assert.IsLessThan(10, attempt, $"{what}: the checked view kept being refused: {reply.GetRawText()}");
                    viewRetries++;
                    await Task.Delay(50, token);
                    continue;
                }
                timings["view"].Add(elapsed);
                var image = reply.GetProperty("content")[0];
                Assert.AreEqual("image", image.GetProperty("type").GetString(), what + ": the view's first block is its image.");
                Assert.AreEqual("image/png", image.GetProperty("mimeType").GetString(), what);
                byte[] png = Convert.FromBase64String(image.GetProperty("data").GetString()!);
                var view = SchematicJson.Parser.Parse<CheckedSchematicView>(reply.GetProperty("structuredContent").GetRawText());
                // The image and the state describe one revision of exactly this document in exactly this KiCad process.
                var revision = view.Checked.State.Revision;
                Assert.AreEqual(revision, view.Checked.Electrical.Hierarchy.Revision, what + ": the objects and the checked state share one revision.");
                Assert.AreEqual(revision, view.View.Snapshot.Revision, what + ": the displayed objects belong to the checked revision.");
                Assert.AreEqual(revision, view.View.Preview.Revision, what + ": the image belongs to the checked revision.");
                Assert.AreEqual(document, view.Checked.State.Document, what);
                Assert.AreEqual(document, view.View.Preview.Document, what);
                Assert.AreEqual(client.Epoch, view.Checked.State.ProcessEpoch, what);
                CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray(), what + ": the image is a PNG.");
                Assert.AreEqual(view.View.Preview.WidthPixels, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)), what);
                Assert.AreEqual(view.View.Preview.HeightPixels, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)), what);
                Assert.IsTrue(view.View.Preview.Png.IsEmpty, what + ": the structured view leaves the image to its image block.");
                // The sheet on screen is exactly the root sheet of the checked state.
                Assert.AreEqual(RootSheet(view.Checked), view.View.Snapshot.Data, what + ": the displayed sheet's objects are the checked state's root sheet.");
                return (view, png);
            }
        }
        // Nothing ran since the view: KiCad still holds the viewed state, and its own capture now gives the same objects,
        // viewport and image bytes, so the image the agent received is KiCad's rendering of the state it received.
        async Task RecaptureMatches(CheckedSchematicView view, byte[] png, string what)
        {
            var watch = Stopwatch.StartNew();
            var direct = await Native(t => client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                new() { Document = document, SchemaVersion = 9 }, t), what + ": capturing the canvas directly");
            var now = await State();
            timings["recapture"].Add(watch.Elapsed.TotalMilliseconds);
            Assert.AreEqual(view.Checked.State, now, what + ": KiCad still holds exactly the viewed state.");
            Assert.AreEqual(view.View.Snapshot, direct.Snapshot, what + ": KiCad's own capture shows the same objects.");
            Assert.AreEqual(view.View.Preview.Revision, direct.Preview.Revision, what);
            Assert.AreEqual(view.View.Preview.Viewport, direct.Preview.Viewport, what);
            Assert.IsTrue(direct.Preview.Png.Span.SequenceEqual(png), what + ": the agent's image is exactly KiCad's rendering of the viewed state.");
        }

        // The agent's plan, made only from what it viewed: new notes, rewritten notes, new labels and removed notes, one to
        // two operations per batch, in a free area of the sheet the canvas shows.
        SchematicText? noteTemplate = null;
        LocalLabel? labelTemplate = null;
        var agentNotes = new List<string>();
        int planned = 0, slots = 0;
        Vector2 NextSlot()
        {
            int slot = slots++;
            return new() { XNm = (40 + 18 * (slot % 3)) * 1_270_000L, YNm = (96 + 3 * (slot / 3 % 14)) * 1_270_000L };
        }
        StressBatch Plan(int iteration, CheckedSchematicView observed, string description)
        {
            var items = Root(observed.Checked);
            var live = agentNotes.Where(items.ContainsKey).ToList();
            var operations = new List<SchematicItemOperation>();
            var expected = new Dictionary<string, IMessage?>(StringComparer.Ordinal);
            var created = new List<string>();
            var removed = new List<string>();
            int number = planned++;
            void Rewrite(string id)
            {
                var note = items[id].Unpack<SchematicText>();
                note.Text.Text_ = $"Agent rewrite {number}";
                operations.Add(new() { Update = Any.Pack(note) });
                expected[id] = note;
            }
            switch (number % 4)
            {
                case 1 when live.Count > 0:
                    Rewrite(live[^1]);
                    break;
                case 2 when live.Count > 0:
                    var label = labelTemplate!.Clone();
                    label.Id = new() { Value = Guid.NewGuid().ToString("D") };
                    label.Text.Text_ = $"AGENT_{number}";
                    label.Position = NextSlot();
                    label.Text.Position = label.Position.Clone();
                    operations.Add(new() { Create = Any.Pack(label) });
                    expected[label.Id.Value] = label;
                    created.Add(label.Id.Value);
                    Rewrite(live[0]);
                    break;
                case 3 when live.Count > 1:
                    operations.Add(new() { Remove = new KIID { Value = live[0] } });
                    expected[live[0]] = null;
                    removed.Add(live[0]);
                    break;
                default:
                    var fresh = noteTemplate!.Clone();
                    fresh.Id = new() { Value = Guid.NewGuid().ToString("D") };
                    fresh.Text.Text_ = $"Agent {number}";
                    fresh.Text.Position = NextSlot();
                    operations.Add(new() { Create = Any.Pack(fresh) });
                    expected[fresh.Id.Value] = fresh;
                    created.Add(fresh.Id.Value);
                    break;
            }
            var batch = new ApplySchematicItemBatch
            {
                Document = document.Clone(), ExpectedRevision = observed.Checked.State.Revision.Clone(),
                DocumentEpoch = observed.Checked.State.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = description
            };
            batch.Operations.Add(operations);
            return new(iteration, observed, new() { Batch = batch, ExpectedState = observed.Checked.State.Clone() }, expected, created, removed);
        }
        async Task<CheckedSchematicBatchReceipt> Apply(StressBatch batch, string what)
        {
            string requestJson = SchematicJson.Formatter.Format(batch.Request);
            var watch = Stopwatch.StartNew();
            var reply = await Tool("kicad_schematic_apply_checked_batch", new { instanceId, requestJson }, what);
            double elapsed = watch.Elapsed.TotalMilliseconds;
            var data = reply.GetProperty("structuredContent");
            Assert.IsTrue(data.TryGetProperty("receipt", out var json), $"{what}: the batch returned no receipt: {reply.GetRawText()}");
            var receipt = SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(json.GetRawText());
            bool completedBatch = receipt.Status == CheckedSchematicBatchStatus.CsbsCompleted;
            Assert.AreEqual(!completedBatch, reply.TryGetProperty("isError", out var failed) && failed.GetBoolean(),
                $"{what}: only a completed batch reports success: {reply.GetRawText()}");
            Assert.IsTrue(data.GetProperty("mutationSubmitted").GetBoolean(), what);
            Assert.IsFalse(data.GetProperty("designFilesSaved").GetBoolean(), what);
            Assert.AreEqual(batch.Request.Batch.OperationId, receipt.OperationId, what + ": the receipt belongs to exactly this batch.");
            Assert.AreEqual(document, receipt.Document, what);
            Assert.AreEqual(client.Epoch, receipt.ProcessEpoch, what);
            timings[completedBatch ? "applyAccepted" : "applyRefused"].Add(elapsed);
            return receipt;
        }
        // An accepted batch met exactly the viewed state, is one commit in KiCad's history right after the viewed
        // revision, and its receipt lists exactly the requested objects. Its exact content change is proven again,
        // object by object, when the run undoes it.
        async Task Accepted(StressBatch batch, CheckedSchematicBatchReceipt receipt, bool quiescent, string what)
        {
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, $"{what}: {receipt.ErrorCode} {receipt.ErrorMessage}");
            Assert.AreEqual(batch.Request.ExpectedState, receipt.ObservedBefore, what + ": KiCad applied the batch only to exactly the state the agent viewed.");
            var touched = batch.Expected.Where(entry => entry.Value is not null).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            AssertObjects(ItemsByUuid(receipt.Result.Items), touched, what + " receipt");
            CollectionAssert.AreEquivalent(touched.Keys.ToList(), receipt.Result.Items.Select(ItemUuid).ToList(),
                what + ": the receipt lists every created or changed object and nothing else.");
            CollectionAssert.AreEquivalent(batch.Removed.ToList(), receipt.Result.Removed.Select(id => id.Value).ToList(),
                what + ": the receipt lists every removed object and nothing else.");
            ulong viewed = batch.Request.ExpectedState.Revision.Sequence;
            var own = (await Journal(viewed)).Changes.Where(change => change.OperationId == batch.Request.Batch.OperationId).ToList();
            Assert.HasCount(1, own, what + ": the whole batch is one native commit.");
            Assert.AreEqual(SchematicChange.Types.Kind.Commit, own[0].Kind, what);
            Assert.AreEqual(viewed + 1, own[0].Sequence, what + ": nothing reached KiCad between the viewed revision and the batch.");
            Assert.AreEqual(own[0].Sequence, receipt.ObservedAfter.Revision.Sequence, what + ": the receipt's state is the state right after that commit.");
            if (quiescent) Assert.AreEqual(receipt.ObservedAfter, await State(), what + ": nothing but the batch changed KiCad.");
            accepted.Add((batch, receipt));
            agentNotes.AddRange(batch.Created.Where(id => batch.Expected[id] is SchematicText));
            foreach (string id in batch.Removed) agentNotes.Remove(id);
        }
        // A refused batch is refused as stale, leaves no step in KiCad's history and changes nothing: KiCad still holds
        // the state it refused against, with the same content digest, revision, unsaved flag and file baselines.
        async Task Refused(StressBatch batch, CheckedSchematicBatchReceipt receipt, DocumentLifecycleState? before,
            SchematicSaveState? flags, string what)
        {
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, receipt.Status, what + ": a batch planned from an out-of-date view must be refused.");
            Assert.AreEqual("stale_document_state", receipt.ErrorCode, $"{what}: {receipt.ErrorMessage}");
            Assert.IsNull(receipt.Result, what + ": a refused batch has no result.");
            Assert.AreNotEqual(batch.Request.ExpectedState, receipt.ObservedBefore, what + ": KiCad refused because its state differed from the view.");
            Assert.IsFalse((await Journal(batch.Request.ExpectedState.Revision.Sequence)).Changes
                .Any(change => change.OperationId == batch.Request.Batch.OperationId), what + ": a refused batch leaves no step in KiCad's history.");
            Assert.AreEqual(receipt.ObservedBefore, await State(), what + ": the refused batch changed nothing.");
            if (before is not null) Assert.AreEqual(before, receipt.ObservedBefore, what + ": KiCad refused against exactly the state the person left.");
            if (flags is not null) Assert.AreEqual(flags, await Flags(), what + ": the refused batch changed no unsaved-change flag.");
            refused.Add((batch, receipt));
        }

        try
        {
            // Start from the saved design with an empty undo stack.
            await Native(t => client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, t), "Saving the fixture");
            await Native(t => client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, t), "Reloading the fixture");
            var initial = await Checked();
            epoch = initial.State.Revision.Epoch;
            Assert.IsFalse(initial.State.NativeContentDirty, "The run starts from the saved design.");
            var initialItems = Root(initial);
            var person = initialItems.Values.Where(item => item.Is(SchematicText.Descriptor)).Select(item => item.Unpack<SchematicText>())
                .Single(text => text.Text.Text_ == PersonNote);
            personNoteId = person.Id.Value;
            noteTemplate = person.Clone();
            labelTemplate = initialItems.Values.Where(item => item.Is(LocalLabel.Descriptor)).Select(item => item.Unpack<LocalLabel>())
                .OrderBy(label => label.Id.Value, StringComparer.Ordinal).First();
            await EnsureCanvasFocus();
            await ProveUndoStackEmpty("Before the run", initial.State.StateSha256);
            var start = await Checked();
            Assert.AreEqual(initial.State.StateSha256, start.State.StateSha256);
            ulong startSequence = start.State.Revision.Sequence;

            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                var timing = schedule[iteration];
                string what = $"{instanceId} iteration {iteration} ({timing})";
                var clock = Stopwatch.StartNew();
                var trace = new Dictionary<string, object?> { ["iteration"] = iteration, ["timing"] = timing.ToString() };
                records.Add(trace);
                var idle = await State();
                if (timing == StressTiming.SaveBeforeApply && !idle.NativeContentDirty)
                {
                    // Saving needs unsaved work; the person makes some first.
                    trace["rotatedBeforeSaving"] = (await Rotate(idle.Revision.Sequence, what + " before saving")).Sequence;
                    idle = await State();
                }
                if (timing == StressTiming.BeforeObservation)
                    trace["personSequence"] = (await Rotate(idle.Revision.Sequence, what)).Sequence;

                CheckedSchematicView observed;
                byte[] png;
                if (timing == StressTiming.DuringObservation)
                {
                    await SelectPersonNote();
                    var (viewed, sent, offset) = await Race(timing, () => View(what));
                    (observed, png) = viewed;
                    var change = await PersonChange(idle.Revision.Sequence, SchematicChange.Types.Kind.Commit, sent, what);
                    bool personFirst = observed.Checked.State.Revision.Sequence >= change.Sequence;
                    Raced(timing, personFirst);
                    trace["keyOffsetMilliseconds"] = offset;
                    trace["personSequence"] = change.Sequence;
                    trace["viewShowedPersonEdit"] = personFirst;
                }
                else
                {
                    (observed, png) = await View(what);
                    await RecaptureMatches(observed, png, what);
                }
                ulong observedSequence = observed.Checked.State.Revision.Sequence;
                trace["observedSequence"] = observedSequence;
                trace["imageSha256"] = Convert.ToHexStringLower(SHA256.HashData(png));
                if (iteration % 10 == 0 || iteration == Iterations - 1)
                    await File.WriteAllBytesAsync(Path.Combine(evidence, $"{instanceId}-stress-view-{iteration:00}.png"), png, token);

                var batch = Plan(iteration, observed, $"Observe-apply stress iteration {iteration}");
                trace["operationId"] = batch.Request.Batch.OperationId;
                trace["operations"] = batch.Request.Batch.Operations.Select(operation => operation.OperationCase.ToString()).ToArray();
                Dictionary<string, byte[]>? savedFiles = null;
                switch (timing)
                {
                    case StressTiming.BeforeApply:
                        trace["personSequence"] = (await Rotate(observedSequence, what)).Sequence;
                        break;
                    case StressTiming.RotateThenUndo:
                    {
                        var rotation = await Rotate(observedSequence, what);
                        var sent = Press("z", true);
                        personUndos++;
                        var undo = await PersonChange(rotation.Sequence, SchematicChange.Types.Kind.Undo, sent, what);
                        var restored = await State();
                        Assert.AreEqual(observed.Checked.State.StateSha256, restored.StateSha256,
                            what + ": undoing the person's rotation restores exactly the viewed content.");
                        Assert.AreEqual(observedSequence + 2, restored.Revision.Sequence, what + ": the rotation and its undo are two steps after the view.");
                        trace["personSequence"] = rotation.Sequence;
                        trace["personUndoSequence"] = undo.Sequence;
                        break;
                    }
                    case StressTiming.SaveBeforeApply:
                        personSaves++;
                        await AwaitSaved(Press("s", true), what);
                        savedFiles = await ProjectFiles();
                        break;
                }

                bool concurrent = timing == StressTiming.DuringApply;
                DocumentLifecycleState? beforeApply = concurrent ? null : await State();
                SchematicSaveState? flagsBeforeApply = concurrent ? null : await Flags();
                CheckedSchematicBatchReceipt receipt;
                if (concurrent)
                {
                    await SelectPersonNote();
                    (receipt, var sent, int offset) = await Race(timing, () => Apply(batch, what));
                    var change = await PersonChange(observedSequence, SchematicChange.Types.Kind.Commit, sent, what);
                    bool batchFirst = receipt.Status == CheckedSchematicBatchStatus.CsbsCompleted;
                    Raced(timing, !batchFirst);
                    trace["keyOffsetMilliseconds"] = offset;
                    trace["personSequence"] = change.Sequence;
                    trace["personKeyHandledFirst"] = !batchFirst;
                    if (batchFirst)
                        Assert.IsGreaterThan(receipt.ObservedAfter.Revision.Sequence, change.Sequence, what + ": KiCad handled the batch, then the person's key.");
                    else
                        Assert.AreEqual(observedSequence + 1, change.Sequence, what + ": KiCad handled the person's key first, as the only step since the view.");
                }
                else receipt = await Apply(batch, what);
                trace["status"] = receipt.Status.ToString();
                trace["errorCode"] = receipt.ErrorCode;
                trace["receiptBeforeSequence"] = receipt.ObservedBefore?.Revision?.Sequence;
                trace["receiptAfterSequence"] = receipt.ObservedAfter?.Revision?.Sequence;

                bool expectAccepted = timing switch
                {
                    StressTiming.NoPersonEdit or StressTiming.BeforeObservation or StressTiming.ReusedObservation => true,
                    StressTiming.DuringObservation => (bool)trace["viewShowedPersonEdit"]!,
                    StressTiming.DuringApply => receipt.Status == CheckedSchematicBatchStatus.CsbsCompleted,
                    _ => false
                };
                if (expectAccepted) await Accepted(batch, receipt, !concurrent, what);
                else await Refused(batch, receipt, beforeApply, flagsBeforeApply, what);
                if (timing == StressTiming.RotateThenUndo)
                    Assert.AreEqual(batch.Request.ExpectedState.StateSha256, receipt.ObservedBefore!.StateSha256,
                        what + ": the content matched the view; only the revision showed the batch was stale.");
                if (timing == StressTiming.SaveBeforeApply)
                {
                    var now = await State();
                    var flags = await Flags();
                    Assert.IsFalse(now.NativeContentDirty, what + ": the design the person saved is still saved after the refused batch.");
                    Assert.IsFalse(flags.UnsavedSchematicChanges, what);
                    Assert.IsEmpty(flags.ModifiedSheetInstances, what);
                    foreach (var (path, bytes) in savedFiles!)
                        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token), $"{what}: {Path.GetFileName(path)} is as the person saved it.");
                }
                if (timing == StressTiming.ReusedObservation)
                {
                    var again = Plan(iteration, observed, $"Observe-apply stress iteration {iteration}, planned again from the same view");
                    var afterFirst = await State();
                    var flagsAfterFirst = await Flags();
                    var second = await Apply(again, what + " second batch");
                    await Refused(again, second, afterFirst, flagsAfterFirst, what + " second batch");
                    Assert.AreEqual(receipt.ObservedAfter, second.ObservedBefore, what + ": the second batch met the first batch's result.");
                    trace["secondOperationId"] = again.Request.Batch.OperationId;
                    trace["secondStatus"] = second.Status.ToString();
                    trace["secondErrorCode"] = second.ErrorCode;
                }
                trace["milliseconds"] = clock.Elapsed.TotalMilliseconds;
                timings["iteration"].Add(clock.Elapsed.TotalMilliseconds);
            }

            // KiCad's history since the start holds exactly the accepted batches and the person's edits, and never a
            // refused batch. Replaying it gives the undo stack the run must now unwind.
            var end = await Checked();
            var history = await Journal(startSequence);
            Assert.AreEqual(end.State.Revision.Sequence, history.Sequence);
            var stack = new List<SchematicChange>();
            foreach (var change in history.Changes)
            {
                if (change.Kind == SchematicChange.Types.Kind.Commit) { stack.Add(change); continue; }
                Assert.AreEqual(SchematicChange.Types.Kind.Undo, change.Kind, "The run never redoes.");
                Assert.IsNotEmpty(stack, "Every undo in the run has a step to undo.");
                Assert.AreEqual("", stack[^1].OperationId, "The person only ever undid their own rotation.");
                stack.RemoveAt(stack.Count - 1);
            }
            var byOperation = accepted.ToDictionary(entry => entry.Batch.Request.Batch.OperationId, StringComparer.Ordinal);
            CollectionAssert.AreEquivalent(byOperation.Keys.ToList(),
                history.Changes.Where(change => change.OperationId.Length > 0).Select(change => change.OperationId).ToList(),
                "Every accepted batch is exactly one step of KiCad's history, and no refused batch is one.");
            Assert.IsFalse(refused.Any(entry => byOperation.ContainsKey(entry.Batch.Request.Batch.OperationId)));
            Assert.AreEqual(rotations, history.Changes.Count(change => change.Kind == SchematicChange.Types.Kind.Commit && change.OperationId.Length == 0),
                "Every rotation the person made is exactly one step of KiCad's history.");
            Assert.AreEqual(personUndos, history.Changes.Count(change => change.Kind == SchematicChange.Types.Kind.Undo));

            // Undo every step through the keyboard, newest first. Each Ctrl+Z is one step: a batch's step takes KiCad from
            // exactly the state its receipt reported after it back to exactly the state the agent viewed, changing exactly
            // the objects the batch named; a person's step changes only the note they rotated.
            var current = end;
            for (int index = stack.Count - 1; index >= 0; index--)
            {
                var step = stack[index];
                string what = $"{instanceId} undo {stack.Count - index} of {stack.Count}";
                var watch = Stopwatch.StartNew();
                ulong at = current.State.Revision.Sequence;
                await PersonChange(at, SchematicChange.Types.Kind.Undo, Press("z", true), what, timed: false);
                var after = await Checked();
                Assert.HasCount(1, (await Journal(at)).Changes, what + ": one Ctrl+Z is one step.");
                var beforeItems = Root(current);
                var afterItems = Root(after);
                var changed = beforeItems.Keys.Union(afterItems.Keys)
                    .Where(id => !(beforeItems.TryGetValue(id, out var was) && afterItems.TryGetValue(id, out var now) && SameItem(was, now)))
                    .Order(StringComparer.Ordinal).ToList();
                if (byOperation.TryGetValue(step.OperationId, out var agent))
                {
                    Assert.AreEqual(agent.Receipt.ObservedAfter.StateSha256, current.State.StateSha256,
                        what + ": before undoing the batch, KiCad holds exactly the state the batch produced.");
                    Assert.AreEqual(agent.Receipt.ObservedBefore.StateSha256, after.State.StateSha256,
                        what + ": one undo removes exactly that batch and restores the state the agent viewed.");
                    AssertObjects(beforeItems, agent.Batch.Expected, what + " with the batch");
                    AssertSameItems(Root(agent.Batch.Observed.Checked), afterItems, what + ": after undoing the batch the sheet is exactly what the agent viewed");
                    CollectionAssert.AreEqual(agent.Batch.Expected.Keys.Order(StringComparer.Ordinal).ToList(), changed,
                        what + ": the batch changed exactly the objects it named.");
                }
                else
                {
                    Assert.AreEqual("", step.OperationId, what);
                    CollectionAssert.AreEqual(new List<string> { personNoteId }, changed, what + ": the person's keyboard edit changed only the note they rotated.");
                }
                current = after;
                undoSteps++;
                timings["undoStep"].Add(watch.Elapsed.TotalMilliseconds);
            }
            Assert.AreEqual(start.State.StateSha256, current.State.StateSha256,
                "Undoing every step returns KiCad to exactly where the run started, so the design held exactly the sum of the accepted edits.");
            AssertSameItems(Root(start), Root(current), "After undoing every step");
            await ProveUndoStackEmpty("After undoing every step", start.State.StateSha256);

            Assert.IsGreaterThanOrEqualTo(16, accepted.Count, "The schedule's undisturbed iterations are always accepted.");
            Assert.IsTrue(records.Any(entry => entry.GetValueOrDefault("personKeyHandledFirst") is true)
                && records.Any(entry => entry.GetValueOrDefault("personKeyHandledFirst") is false),
                "While the agent's batch was on its way, KiCad handled the person's key first at least once and the batch first at least once: "
                + string.Join(", ", records.Where(entry => entry.ContainsKey("personKeyHandledFirst"))
                    .Select(entry => $"{entry["keyOffsetMilliseconds"]} ms {((bool)entry["personKeyHandledFirst"]! ? "key" : "batch")} first")));
            Assert.IsGreaterThanOrEqualTo(21, refused.Count, "The schedule's stale iterations are always refused.");
            completed = true;
        }
        finally
        {
            object Percentiles(List<double> values)
            {
                if (values.Count == 0) return new { count = 0 };
                var sorted = values.Order().ToArray();
                double At(double share) => Math.Round(sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Length) - 1, 0, sorted.Length - 1)], 1);
                return new { count = sorted.Length, p50 = At(0.5), p90 = At(0.9), p99 = At(0.99), max = Math.Round(sorted[^1], 1) };
            }
            var summary = new
            {
                instanceId, seed, completed, iterations = records.Count, elapsedSeconds = Math.Round(run.Elapsed.TotalSeconds, 1),
                schedule = schedule.GroupBy(timing => timing).ToDictionary(group => group.Key.ToString(), group => group.Count()),
                applies = accepted.Count + refused.Count, accepted = accepted.Count, refused = refused.Count,
                refusedByCode = refused.GroupBy(entry => entry.Receipt.ErrorCode).ToDictionary(group => group.Key, group => group.Count()),
                duringApply = new
                {
                    batchFirst = records.Count(entry => entry.GetValueOrDefault("personKeyHandledFirst") is false),
                    personKeyFirst = records.Count(entry => entry.GetValueOrDefault("personKeyHandledFirst") is true)
                },
                duringObservation = new
                {
                    viewShowedEdit = records.Count(entry => entry.GetValueOrDefault("viewShowedPersonEdit") is true),
                    viewPrecededEdit = records.Count(entry => entry.GetValueOrDefault("viewShowedPersonEdit") is false)
                },
                person = new { rotations, undos = personUndos, saves = personSaves },
                viewRetries, undoSteps,
                timingMilliseconds = timings.ToDictionary(entry => entry.Key, entry => Percentiles(entry.Value)),
                records
            };
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-observe-apply-stress.json"),
                JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            Console.WriteLine($"Observe-apply stress {instanceId}: {(completed ? "completed" : "stopped")} after {records.Count} iterations in "
                + $"{run.Elapsed.TotalSeconds:F1}s; {accepted.Count + refused.Count} batches, {accepted.Count} accepted, {refused.Count} refused "
                + $"({string.Join(", ", refused.GroupBy(entry => entry.Receipt.ErrorCode).Select(group => $"{group.Key} {group.Count()}"))}); "
                + $"person: {rotations} rotations, {personUndos} undos, {personSaves} saves; {viewRetries} view retries; {undoSteps} undo steps; "
                + string.Join("; ", timings.Select(entry => $"{entry.Key} {JsonSerializer.Serialize(Percentiles(entry.Value))}")) + ".");
        }
    }

    // A native schematic object compared by content, so two readings of the same object are equal whatever their byte order.
    private static bool SameItem(Any left, Any right)
    {
        if (left.TypeUrl != right.TypeUrl) return false;
        var type = SchematicText.Descriptor.File.MessageTypes.SingleOrDefault(candidate => left.Is(candidate));
        return type is null ? left.Value.Equals(right.Value) : type.Parser.ParseFrom(left.Value).Equals(type.Parser.ParseFrom(right.Value));
    }

    // Exactly the same objects, each with exactly the same content.
    private static void AssertSameItems(IReadOnlyDictionary<string, Any> expected, IReadOnlyDictionary<string, Any> actual, string phase)
    {
        var differences = expected.Keys.Union(actual.Keys)
            .Where(id => !(expected.TryGetValue(id, out var wanted) && actual.TryGetValue(id, out var found) && SameItem(wanted, found)))
            .Order(StringComparer.Ordinal)
            .Select(id => $"{id} ({(expected.TryGetValue(id, out var item) ? item : actual[id]).TypeUrl.Split('/').Last()}"
                + $"{(expected.ContainsKey(id) ? actual.ContainsKey(id) ? ", changed" : ", missing" : ", added")})").ToList();
        Assert.IsEmpty(differences, $"{phase}; differing objects: {string.Join(", ", differences)}");
    }
}
