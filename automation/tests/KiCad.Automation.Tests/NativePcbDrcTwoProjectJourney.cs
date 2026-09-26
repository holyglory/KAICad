using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    // KiCad's copper clearance step, which finds this board's clearance violations.
    private const string PcbDrcClearancePhase = "Checking track & via clearances...";

    // KiCad runs its copper sliver check after the copper clearance checks, and on this board it is the longest step.
    // A check seen in this step has already produced its clearance violations.
    private const string PcbDrcLateCopperPhase = "Running sliver detection on copper layers...";

    // Custom rules for project A: a real rule that matches no net on the board, so every finding stays the same.
    // The broken copy adds an item KiCad does not know on line 3, from its second character.
    private const string PcbDrcHarmlessRules =
        "(version 1)\n(rule \"journey_unmatched\" (condition \"A.NetName == 'NO_SUCH_NET'\") (constraint track_width (min 0.1mm)))\n";
    private const string PcbDrcBrokenRules = PcbDrcHarmlessRules + "(not_a_rule)\n";

    // A generated board: 60 closed copper loops on their own nets (12,120 segments) that break no rule but give KiCad's
    // checker seconds of real work, and two real copper clearance violations between exactly known items: two parallel
    // B.Cu tracks of different nets 0.05 mm apart, and a via 0.075 mm from a track of another net. Those three tracks and
    // the via connect to nothing, so KiCad also reports each of them as dangling. Item identities are new for each
    // project, so each project's findings can only name its own copper.
    private sealed record TwoProjectDrcBoard(string Text, string PairA, string PairB, string Track, string Via, string Edited);

    // One project as the agent addresses it, and what the journey must put back afterwards.
    private sealed record TwoProjectDrcTarget(string Name, string InstanceId, string Epoch, NativeClient Native,
        DocumentSpecifier Board, string BoardPath, string RulesPath, TwoProjectDrcBoard Fixture);

    private static TwoProjectDrcBoard MakeTwoProjectDrcBoard()
    {
        const int loops = 60, segments = 100;
        const double pitch = 0.65;
        var text = new StringBuilder(3_000_000);
        static string Mm(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        string Segment(double x0, double y0, double x1, double y1, string net, string layer, double width)
        {
            string id = Guid.NewGuid().ToString("D");
            text.Append($"  (segment (start {Mm(x0)} {Mm(y0)}) (end {Mm(x1)} {Mm(y1)}) (width {Mm(width)}) (layer \"{layer}\") (net \"{net}\") (uuid \"{id}\"))\n");
            return id;
        }
        text.Append("(kicad_pcb (version 20260206) (generator \"pcbnew\")\n  (general (thickness 1.6)) (paper \"A4\")\n")
            .Append("  (layers (0 \"F.Cu\" signal) (2 \"B.Cu\" signal) (25 \"Edge.Cuts\" user))\n  (setup (pad_to_mask_clearance 0))\n")
            .Append($"  (gr_rect (start 0 0) (end {Mm(20 + segments)} {Mm(30 + loops * pitch)}) (stroke (width 0.05) (type default)) (fill no) (layer \"Edge.Cuts\") (uuid \"{Guid.NewGuid():D}\"))\n");
        string edited = "";
        for (int loop = 0; loop < loops; loop++)
        {
            // A closed loop: 0.2 mm tracks 0.2 mm apart, so no end dangles; neighbouring loops keep 0.25 mm of clearance.
            double y = 10 + loop * pitch;
            string net = "LOAD_" + loop.ToString(CultureInfo.InvariantCulture);
            for (int s = 0; s < segments; s++)
            {
                string id = Segment(10 + s, y, 11 + s, y, net, "F.Cu", 0.2);
                if (loop == 0 && s == 0) edited = id;
            }
            Segment(10 + segments, y, 10 + segments, y + 0.2, net, "F.Cu", 0.2);
            for (int s = 0; s < segments; s++) Segment(10 + segments - s, y + 0.2, 9 + segments - s, y + 0.2, net, "F.Cu", 0.2);
            Segment(10, y + 0.2, 10, y, net, "F.Cu", 0.2);
        }
        double pairs = 15 + loops * pitch;
        string pairA = Segment(10, pairs, 20, pairs, "PAIR_A", "B.Cu", 0.25);
        string pairB = Segment(10, pairs + 0.3, 20, pairs + 0.3, "PAIR_B", "B.Cu", 0.25);
        string track = Segment(30, pairs, 40, pairs, "TRACK_T", "B.Cu", 0.25);
        string via = Guid.NewGuid().ToString("D");
        text.Append($"  (via (at 35 {Mm(pairs + 0.5)}) (size 0.6) (drill 0.3) (layers \"F.Cu\" \"B.Cu\") (net \"VIA_V\") (uuid \"{via}\"))\n)\n");
        return new(text.ToString(), pairA, pairB, track, via, edited);
    }

    // Item pcb-two-project-drc (ledger p4aa00ee192dd3acd). One agent, through one compiled MCP STDIO server, runs real
    // native design-rule checks in the rendered PCB editors of two KiCad instances at once: it watches both progress,
    // gets real copper violations of the exact checked revision, cancels one check mid-run, forces a failure with rules
    // that do not compile and recovers, and is refused for wrong-instance and stale targets without either board
    // changing. After each terminal outcome (completed, cancelled, failed, recovered) the editor it happened in accepts
    // a normal edit and saves it, and a person's undo and redo in project B's rendered editor still work. Ordinary jobs
    // never claim a complete snapshot or fresh results: KiCad does not yet capture every project and rules input a check
    // depends on (decision n39a51a3a2ff84c55, ledger p23deb822a36256a6).
    private static async Task VerifyPcbDrcTwoProjects(NativeClient client, DocumentSpecifier board, int processId,
        string display, string evidence, CancellationToken token)
    {
        var sibling = await SiblingInstance(client, token);
        var siblingNative = new NativeClient(new NngTransport(), sibling.Endpoint, sibling.Epoch);
        var siblingSession = await siblingNative.HandshakeAsync(token);
        string siblingBoardPath = Path.ChangeExtension(siblingSession.ProjectPath, ".kicad_pcb");
        // The harness keeps its two projects in the numbered folders 0 and 1 under one root and runs this step in
        // project 0, then in project 1. Both boards are open only in project 1's run, so the journey runs there, and
        // only project 0's run may skip it: in project 1 a missing sibling board fails instead of passing unproven.
        string? own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(board.Project.Path));
        string? other = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(siblingSession.ProjectPath)!));
        while (own is not null && other is not null && Path.GetDirectoryName(own) != Path.GetDirectoryName(other))
        {
            own = Path.GetDirectoryName(own);
            other = Path.GetDirectoryName(other);
        }
        string slot = Path.GetFileName(own) ?? "", siblingSlot = Path.GetFileName(other) ?? "";
        Assert.IsTrue((slot, siblingSlot) is ("0", "1") or ("1", "0"),
            $"The two projects must be the harness's folders 0 and 1 under one root: {board.Project.Path} and {siblingSession.ProjectPath}.");
        if (slot == "0")
        {
            Console.WriteLine("PCB checks in two projects at once run from project 1, once both boards are open.");
            return;
        }
        Assert.IsTrue(File.Exists(siblingBoardPath),
            $"Project 1 runs the two-project PCB checks, so project 0's board {siblingBoardPath} must already exist.");
        var clock = Stopwatch.StartNew();
        var timeline = new StringBuilder();
        string statePath = Directory.CreateTempSubdirectory("kicad-drc-two-projects-").FullName;
        var restore = new List<(NativeClient Native, DocumentSpecifier Board, string Path, byte[] Saved)>();
        TwoProjectDrcTarget? a = null, b = null;
        bool rulesWritten = false;
        Exception? restoreFailure = null;
        static string Json(Google.Protobuf.IMessage message) => SchematicJson.Formatter.Format(message);
        static string Text(JsonElement result) => result.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
        static bool Failed(JsonElement result) => result.TryGetProperty("isError", out var error) && error.GetBoolean();
        Task Evidence(string name, Google.Protobuf.IMessage state) =>
            File.WriteAllTextAsync(Path.Combine(evidence, "pcb-drc-two-projects-" + name + ".json"), Json(state), token);
        try
        {
            await using var mcp = await CancellableMcpClient.StartAsync(statePath,
                Path.Combine(evidence, "pcb-drc-two-projects-mcp.stderr.log"), token);
            string instanceId = (await client.HandshakeAsync(token)).InstanceId;
            RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = sibling.Endpoint, expectedInstanceId = sibling.InstanceId }));
            var listed = await mcp.Tool("kicad_documents_list", new { instanceId = sibling.InstanceId, kind = "pcb" });
            RequireToolSuccess(listed);
            var siblingBoard = SchematicJson.Parser.Parse<GetOpenDocumentsResponse>(Text(listed)).Documents.Single();
            Assert.AreEqual(Path.GetFileName(siblingBoardPath), siblingBoard.BoardFilename);
            Assert.AreNotEqual(board.Project, siblingBoard.Project, "The two instances must hold independent projects.");

            async Task<DocumentLifecycleState> State(TwoProjectDrcTarget target)
            {
                var reply = await mcp.Tool("kicad_document_state", new { instanceId = target.InstanceId, documentJson = Json(target.Board) });
                RequireToolSuccess(reply);
                return SchematicJson.Parser.Parse<DocumentLifecycleState>(Text(reply));
            }
            async Task<TwoProjectDrcTarget> Load(string name, string id, string epoch, NativeClient native, DocumentSpecifier target)
            {
                string boardPath = Path.Combine(target.Project.Path, target.BoardFilename);
                string rules = Path.ChangeExtension(boardPath, ".kicad_dru");
                Assert.IsFalse(File.Exists(rules), $"Project {name} must start without custom rules; only this journey writes them.");
                var fixture = MakeTwoProjectDrcBoard();
                var project = new TwoProjectDrcTarget(name, id, epoch, native, target, boardPath, rules, fixture);
                Assert.IsFalse((await State(project)).NativeContentDirty, $"Project {name}'s board must be saved before the journey replaces it.");
                restore.Add((native, target, boardPath, await File.ReadAllBytesAsync(boardPath, token)));
                // Test data only: the generated board goes through KiCad's own loader into the open editor.
                await File.WriteAllTextAsync(boardPath, fixture.Text, token);
                await native.InvokeAsync<RevertDocument, Empty>(new() { Document = target }, token);
                var loaded = await native.InvokeAsync<GetItemsById, GetItemsResponse>(new()
                {
                    Header = new() { Document = target },
                    Items = { new[] { fixture.PairA, fixture.PairB, fixture.Track, fixture.Via, fixture.Edited }.Select(item => new KIID { Value = item }) }
                }, token);
                Assert.HasCount(5, loaded.Items, $"Project {name}'s editor must hold the generated copper.");
                Assert.IsFalse((await State(project)).NativeContentDirty);
                return project;
            }
            a = await Load("A", sibling.InstanceId, sibling.Epoch, siblingNative, siblingBoard);
            b = await Load("B", instanceId, client.Epoch, client, board);
            Console.WriteLine($"Two-project PCB checks: boards loaded at {clock.Elapsed.TotalSeconds:F1}s.");

            void Record(string step, TwoProjectDrcTarget target, PcbDrcJobState job) => timeline.AppendLine(
                $"{clock.Elapsed.TotalSeconds:F3}s {step} {target.Name} {job.Status} progress={job.Progress:F3} " +
                $"phase=\"{job.Phase}\" workerFinished={job.WorkerFinished} cancellationRequested={job.CancellationRequested} findings={job.Findings.Count}");
            PcbDrcJobState Parse(JsonElement reply)
            {
                RequireToolSuccess(reply);
                return SchematicJson.Parser.Parse<PcbDrcJobState>(Text(reply));
            }
            async Task<JsonElement> Result(Task<JsonElement> call)
            {
                var reply = await call.WaitAsync(TimeSpan.FromSeconds(60), token);
                if (reply.TryGetProperty("error", out var error)) throw new IOException(error.GetRawText());
                return reply.GetProperty("result").Clone();
            }
            object StartArguments(TwoProjectDrcTarget target, DocumentRevision revision, string operation, string? epoch = null,
                DocumentSpecifier? document = null) => new
            {
                instanceId = target.InstanceId, documentJson = Json(document ?? target.Board), operationId = operation,
                refillZones = false, reportAllTrackErrors = false, testFootprints = false,
                expectedRevisionJson = Json(revision), processEpoch = epoch ?? target.Epoch
            };
            // Both checks are requested together, as an agent does for two projects; the MCP server serves both calls at once.
            async Task<(PcbDrcJobState A, PcbDrcJobState B)> StartBoth(string step, DocumentRevision atA, DocumentRevision atB,
                string? operationA = null)
            {
                operationA ??= Guid.NewGuid().ToString("D");
                string operationB = Guid.NewGuid().ToString("D");
                var callA = mcp.StartTool("kicad_pcb_drc_start", StartArguments(a!, atA, operationA)).Reply;
                var callB = mcp.StartTool("kicad_pcb_drc_start", StartArguments(b!, atB, operationB)).Reply;
                var startedA = Parse(await Result(callA));
                var startedB = Parse(await Result(callB));
                foreach (var (target, started, at, operation) in new[] { (a!, startedA, atA, operationA), (b!, startedB, atB, operationB) })
                {
                    Record(step + "-start", target, started);
                    Assert.AreEqual(target.Board, started.Document);
                    Assert.AreEqual(operation, started.OperationId);
                    Assert.AreEqual(target.Epoch, started.ProcessEpoch);
                    Assert.AreEqual(at, started.CheckedRevision, $"Project {target.Name}'s check must be bound to the revision it was asked for.");
                    Assert.IsEmpty(started.Findings);
                    Assert.IsFalse(started.CandidateDryRun);
                }
                return (startedA, startedB);
            }
            async Task<PcbDrcJobState> Read(TwoProjectDrcTarget target, PcbDrcJobState job) => Parse(await mcp.Tool("kicad_pcb_drc_job", new
                { instanceId = target.InstanceId, documentJson = Json(target.Board), jobId = job.JobId, processEpoch = target.Epoch }));
            // A running check shows no findings, stays below 1, and its progress never goes back.
            void Progressing(TwoProjectDrcTarget target, PcbDrcJobState before, PcbDrcJobState now)
            {
                Assert.AreEqual(before.JobId, now.JobId);
                Assert.AreEqual(before.CheckedRevision, now.CheckedRevision);
                Assert.IsGreaterThanOrEqualTo(before.Progress, now.Progress, $"Project {target.Name}'s progress went back.");
                if (now.WorkerFinished) return;
                Assert.IsTrue(now.Status is PcbDrcJobStatus.PdrcjsQueued or PcbDrcJobStatus.PdrcjsRunning, now.Status.ToString());
                Assert.IsEmpty(now.Findings, $"Project {target.Name}'s running check must not expose findings.");
                Assert.IsLessThan(1.0, now.Progress);
            }
            // Reads every unfinished check until each worker has finished; returns the terminal states and the phases seen.
            // The projects' checks are read together, as an agent watching both does, so each is read about every 40 ms
            // and a step as short as KiCad's copper clearance step (100-250 ms here) is seen.
            async Task<(PcbDrcJobState[] States, HashSet<string>[] Phases)> Watch(string step, params (TwoProjectDrcTarget Target, PcbDrcJobState Job)[] jobs)
            {
                var states = jobs.Select(job => job.Job).ToArray();
                var phases = jobs.Select(job => new HashSet<string>(StringComparer.Ordinal)).ToArray();
                for (int index = 0; index < jobs.Length; index++)
                    if (!states[index].WorkerFinished && states[index].Phase.Length != 0) phases[index].Add(states[index].Phase);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromMinutes(2));
                while (states.Any(state => !state.WorkerFinished))
                {
                    await Task.Delay(10, limit.Token);
                    int[] watched = Enumerable.Range(0, jobs.Length).Where(index => !states[index].WorkerFinished).ToArray();
                    var reads = watched.Select(index => Read(jobs[index].Target, states[index])).ToArray();
                    await Task.WhenAll(reads);
                    for (int read = 0; read < watched.Length; read++)
                    {
                        int index = watched[read];
                        var now = reads[read].Result;
                        Record(step, jobs[index].Target, now);
                        Progressing(jobs[index].Target, states[index], now);
                        if (!now.WorkerFinished && now.Phase.Length != 0) phases[index].Add(now.Phase);
                        states[index] = now;
                    }
                }
                return (states, phases);
            }
            static string Finding(PcbDrcFinding finding) =>
                $"{finding.Marker.ErrorType}|{string.Join(",", finding.Marker.Items.Select(item => item.Value).Order(StringComparer.Ordinal))}" +
                $"|{finding.Marker.Layer}|{finding.Marker.Position?.XNm},{finding.Marker.Position?.YNm}|{finding.Excluded}|{finding.Comment}";
            static string[] Findings(PcbDrcJobState job) => job.Findings.Select(Finding).Order(StringComparer.Ordinal).ToArray();
            static string Named(Kiapi.Board.DrcErrorType type, IEnumerable<string> items) =>
                $"{type}|" + string.Join(",", items.Order(StringComparer.Ordinal));
            // A completed check of the exact revision it was asked for, with exactly the six real findings of this
            // project's own generated copper and nothing else: the two copper clearance violations (the parallel pair,
            // and the track and via), the three tracks dangling and the via dangling. No finding names the 12,120 load
            // segments or the other project's copper.
            void CheckedCopper(TwoProjectDrcTarget target, PcbDrcJobState job, DocumentRevision at, string because)
            {
                Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, job.Status, because + " " + job.ErrorCode + ": " + job.ErrorMessage);
                Assert.AreEqual(1.0, job.Progress);
                Assert.IsTrue(job.WorkerFinished);
                Assert.IsFalse(job.CancellationRequested, because + " Nothing asked this check to stop.");
                Assert.IsFalse(job.SnapshotComplete,
                    "An ordinary check never claims a complete input snapshot (n39a51a3a2ff84c55, ledger p23deb822a36256a6).");
                Assert.IsFalse(job.ResultsFresh,
                    "An ordinary check never claims fresh results (n39a51a3a2ff84c55, ledger p23deb822a36256a6).");
                Assert.AreEqual(at, job.CheckedRevision, because);
                var fixture = target.Fixture;
                CollectionAssert.AreEqual(new[]
                    {
                        Named(Kiapi.Board.DrcErrorType.DrcetClearance, [fixture.PairA, fixture.PairB]),
                        Named(Kiapi.Board.DrcErrorType.DrcetClearance, [fixture.Track, fixture.Via]),
                        Named(Kiapi.Board.DrcErrorType.DrcetDanglingTrack, [fixture.PairA]),
                        Named(Kiapi.Board.DrcErrorType.DrcetDanglingTrack, [fixture.PairB]),
                        Named(Kiapi.Board.DrcErrorType.DrcetDanglingTrack, [fixture.Track]),
                        Named(Kiapi.Board.DrcErrorType.DrcetDanglingVia, [fixture.Via])
                    }.Order(StringComparer.Ordinal).ToArray(),
                    job.Findings.Select(finding => Named(finding.Marker.ErrorType, finding.Marker.Items.Select(item => item.Value)))
                        .Order(StringComparer.Ordinal).ToArray(),
                    because + " The findings must be exactly this project's two clearance violations and its dangling test copper.");
            }
            async Task<string> Picture(string name)
            {
                string path = Path.Combine(evidence, processId + "-pcb-drc-two-projects-" + name + ".png");
                await NativeKeyboard.CaptureAsync(display, path, token);
                return path;
            }
            // The board as a person or agent last saved it: the width KiCad wrote for the edited load segment.
            async Task<long> SavedWidth(TwoProjectDrcTarget target)
            {
                string saved = await File.ReadAllTextAsync(target.BoardPath, token);
                int identity = saved.IndexOf($"(uuid \"{target.Fixture.Edited}\")", StringComparison.Ordinal);
                Assert.IsGreaterThanOrEqualTo(0, identity, $"Project {target.Name}'s saved board lost the edited segment.");
                int start = saved.LastIndexOf("(segment", identity, StringComparison.Ordinal);
                var width = Regex.Match(saved[start..identity], @"\(width ([0-9.]+)\)");
                Assert.IsTrue(width.Success, $"Project {target.Name}'s saved segment has no width.");
                return (long)Math.Round(double.Parse(width.Groups[1].Value, CultureInfo.InvariantCulture) * 1_000_000);
            }
            async Task<Track> Edited(TwoProjectDrcTarget target) => (await target.Native.InvokeAsync<GetItemsById, GetItemsResponse>(new()
                { Header = new() { Document = target.Board }, Items = { new KIID { Value = target.Fixture.Edited } } }, token))
                .Items.Single().Unpack<Track>();
            // After a check ends, the agent makes a normal edit in the same editor (a load track's width, which keeps every
            // clearance) through KiCad's own undoable commit and saves it. Returns the saved state.
            async Task<DocumentLifecycleState> EditAndSave(TwoProjectDrcTarget target, string after)
            {
                var track = await Edited(target);
                long width = track.Width.ValueNm == 200_000 ? 220_000 : 200_000;
                var changed = track.Clone(); changed.Width.ValueNm = width;
                var update = new UpdateItems { Header = new() { Document = target.Board } };
                update.Items.Add(Any.Pack(changed));
                var before = await State(target);
                var updated = await mcp.Tool("kicad_pcb_items_update", new
                    { instanceId = target.InstanceId, requestJson = BoardJson.Formatter.Format(update), expectedStateJson = Json(before) });
                RequireToolSuccess(updated);
                string editedJson = updated.GetProperty("structuredContent").GetProperty("afterState").GetString()!;
                var edited = SchematicJson.Parser.Parse<DocumentLifecycleState>(editedJson);
                Assert.IsTrue(edited.NativeContentDirty, $"After {after}, project {target.Name}'s editor must accept an edit.");
                Assert.AreNotEqual(before.Revision, edited.Revision);
                Assert.AreEqual(width, (await Edited(target)).Width.ValueNm);
                var saved = await mcp.Tool("kicad_document_save", new
                    { instanceId = target.InstanceId, expectedStateJson = editedJson, operationId = Guid.NewGuid().ToString("D") });
                RequireToolSuccess(saved);
                var result = SchematicJson.Parser.Parse<LifecycleOperationResult>(Text(saved));
                Assert.AreEqual(LifecycleOperationStatus.LosSaved, result.Status, $"After {after}, project {target.Name} must save. {result.ErrorCode}: {result.ErrorMessage}");
                Assert.IsFalse(result.ObservedState.NativeContentDirty);
                Assert.AreEqual(width, await SavedWidth(target), $"After {after}, project {target.Name}'s edit must reach its file.");
                Console.WriteLine($"Two-project PCB checks: {target.Name} edited and saved after {after} at {clock.Elapsed.TotalSeconds:F1}s.");
                return result.ObservedState;
            }
            // Everything an agent can observe about both boards, and the bytes of their files.
            async Task<(DocumentLifecycleState A, DocumentLifecycleState B, byte[] FileA, byte[] FileB)> Untouched() =>
                (await State(a!), await State(b!), await File.ReadAllBytesAsync(a!.BoardPath, token), await File.ReadAllBytesAsync(b!.BoardPath, token));
            async Task Unchanged((DocumentLifecycleState A, DocumentLifecycleState B, byte[] FileA, byte[] FileB) before, string because)
            {
                var after = await Untouched();
                Assert.AreEqual(before.A, after.A, because + " Project A's board changed.");
                Assert.AreEqual(before.B, after.B, because + " Project B's board changed.");
                CollectionAssert.AreEqual(before.FileA, after.FileA, because + " Project A's file changed.");
                CollectionAssert.AreEqual(before.FileB, after.FileB, because + " Project B's file changed.");
            }
            void Refused(JsonElement reply, string because, string? code = null, string? containing = null)
            {
                Assert.IsTrue(Failed(reply), because + " " + reply.GetRawText());
                var error = reply.GetProperty("structuredContent");
                if (code is not null) Assert.AreEqual(code, error.GetProperty("code").GetString(), because + " " + reply.GetRawText());
                if (containing is not null) StringAssert.Contains(error.GetProperty("message").GetString()!, containing, because);
            }

            // The project instance B reports as open, as an agent sees it.
            var listedB = await mcp.Tool("kicad_documents_list", new { instanceId = b.InstanceId, kind = "pcb" });
            RequireToolSuccess(listedB);
            var openInB = SchematicJson.Parser.Parse<GetOpenDocumentsResponse>(Text(listedB)).Documents.Single().Project;

            // 1. Both projects are checked at once. The agent watches both, and a cancel or read sent to the wrong instance
            //    or with the other process's epoch is refused and does not stop the running check.
            var stateA1 = await State(a); var stateB1 = await State(b);
            var boards1 = await Untouched();
            var (a1, b1) = await StartBoth("both", stateA1.Revision, stateB1.Revision);
            var runningA = await Read(a, a1); Record("both-overlap", a, runningA);
            var runningB = await Read(b, b1); Record("both-overlap", b, runningB);
            Assert.IsTrue(runningA.Status == PcbDrcJobStatus.PdrcjsRunning && runningB.Status == PcbDrcJobStatus.PdrcjsRunning
                    && !runningA.WorkerFinished && !runningB.WorkerFinished,
                $"Both projects' checks must be running at the same time: A {runningA.Status}, then B {runningB.Status}.");
            // KiCad's document check finds project A's board is not project B's (same file name, other project folder).
            // The refusal names both projects, so the agent knows it reached the wrong instance, not a closed board
            // (ledger p98cb405e55270a80).
            string WrongProject = $"the requested document {a.Board.BoardFilename} of project '{a.Board.Project.Name}' at " +
                $"'{a.Board.Project.Path}' is not open in this KiCad instance, which has project '{openInB.Name}' at " +
                $"'{openInB.Path}' open; send the request to the KiCad instance that has that project open";
            Assert.AreEqual(a.Board.BoardFilename, b.Board.BoardFilename, "Only the project folder tells the two boards apart.");
            Assert.AreNotEqual(a.Board.Project.Path, openInB.Path, "The refusal must be able to tell the two projects apart.");
            Refused(await mcp.Tool("kicad_pcb_drc_cancel", new { instanceId = b.InstanceId, documentJson = Json(a.Board), jobId = a1.JobId, processEpoch = b.Epoch }),
                "Instance B does not hold project A's board.", code: "native_status_3", containing: WrongProject);
            Refused(await mcp.Tool("kicad_pcb_drc_cancel", new { instanceId = b.InstanceId, documentJson = Json(b.Board), jobId = a1.JobId, processEpoch = b.Epoch }),
                "Instance B does not own project A's check.", code: "native_status_3", containing: "Unknown PCB DRC job");
            Refused(await mcp.Tool("kicad_pcb_drc_cancel", new { instanceId = a.InstanceId, documentJson = Json(a.Board), jobId = a1.JobId, processEpoch = b.Epoch }),
                "A cancel with instance B's process epoch must not reach instance A.", code: "stale_process_epoch");
            Refused(await mcp.Tool("kicad_pcb_drc_job", new { instanceId = b.InstanceId, documentJson = Json(b.Board), jobId = a1.JobId, processEpoch = b.Epoch }),
                "Instance B cannot read project A's check.", code: "native_status_3", containing: "Unknown PCB DRC job");
            var (both, bothPhases) = await Watch("both", (a, runningA), (b, runningB));
            await Evidence("A-completed", both[0]); await Evidence("B-completed", both[1]);
            CheckedCopper(a, both[0], stateA1.Revision, "Project A's check, run beside project B's and after refused cancels,");
            CheckedCopper(b, both[1], stateB1.Revision, "Project B's check, run beside project A's,");
            for (int index = 0; index < 2; index++)
                foreach (string phase in new[] { PcbDrcClearancePhase, PcbDrcLateCopperPhase })
                    Assert.Contains(phase, bothPhases[index],
                        $"The agent must see project {(index == 0 ? "A" : "B")}'s check reach KiCad's step \"{phase}\"; it saw: {string.Join(" / ", bothPhases[index])}");
            await Unchanged(boards1, "Running two checks and refusing wrong-instance requests must leave both boards alone.");
            Assert.AreEqual(both[0], await Read(a, a1), "Nothing changed project A's board: its check stays current.");
            Assert.AreEqual(both[1], await Read(b, b1), "Nothing changed project B's board: its check stays current.");
            await Picture("completed");
            string[] baselineA = Findings(both[0]), baselineB = Findings(both[1]);
            var savedA = await EditAndSave(a, "a completed check");
            var savedB = await EditAndSave(b, "a completed check");
            foreach (var (target, job) in new[] { (a, a1), (b, b1) })
            {
                var old = await Read(target, job);
                AssertPcbDrcJobStale(old, "document_changed", $"Project {target.Name}'s edit must make its finished check stale, not current.");
            }

            // 2. Stale and wrong targets are refused before any check starts, and nothing is left behind: the operation
            //    refused for its stale revision later starts a real check.
            var boards2 = await Untouched();
            string reused = Guid.NewGuid().ToString("D");
            Refused(await mcp.Tool("kicad_pcb_drc_start", StartArguments(a, stateA1.Revision, reused)),
                "A check of a revision older than the saved edit must not start.", code: "native_status_3",
                containing: "PCB changed since the requested DRC revision; no job was started");
            Refused(await mcp.Tool("kicad_pcb_drc_start", StartArguments(a, savedA.Revision, Guid.NewGuid().ToString("D"), epoch: b.Epoch)),
                "A check with instance B's process epoch must not start in instance A.", code: "stale_process_epoch");
            Refused(await mcp.Tool("kicad_pcb_drc_start", StartArguments(b, savedA.Revision, Guid.NewGuid().ToString("D"), document: a.Board)),
                "Instance B must not check project A's board.", code: "native_status_3", containing: WrongProject);
            Refused(await mcp.Tool("kicad_pcb_drc_job", new { instanceId = a.InstanceId, documentJson = Json(a.Board), jobId = a1.JobId, processEpoch = Guid.NewGuid().ToString("D") }),
                "A read with an epoch no process has must be refused.", code: "stale_process_epoch");
            await Unchanged(boards2, "Refused stale and wrong-target requests must leave both boards alone.");

            // 3. Project B's check is cancelled after its copper clearance checks, while project A's check runs beside it and
            //    completes with its first findings. The cancelled check ends cancelled, never completed, only once its
            //    worker has stopped.
            var (a2, b2) = await StartBoth("cancel", savedA.Revision, savedB.Revision, operationA: reused);
            var watchedB = b2;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(TimeSpan.FromMinutes(2));
                while (watchedB.Phase != PcbDrcLateCopperPhase)
                {
                    Assert.IsFalse(watchedB.WorkerFinished,
                        $"Project B's check finished ({watchedB.Status}) before the agent saw its late copper step; see the timeline.");
                    await Task.Delay(20, limit.Token);
                    var now = await Read(b, watchedB); Record("cancel-wait", b, now);
                    Progressing(b, watchedB, now);
                    watchedB = now;
                }
            }
            var runningA2 = await Read(a, a2); Record("cancel-beside", a, runningA2);
            var cancelReply = await mcp.Tool("kicad_pcb_drc_cancel", new { instanceId = b.InstanceId, documentJson = Json(b.Board), jobId = b2.JobId, processEpoch = b.Epoch });
            var acknowledged = Parse(cancelReply); Record("cancel-request", b, acknowledged);
            Assert.IsTrue(acknowledged.CancellationRequested, "The cancel must reach the running check.");
            // Asking a check to stop is not the check stopping: until its worker has finished, the check still reads as
            // running (n39a51a3a2ff84c55).
            Assert.IsTrue(acknowledged.Status == PcbDrcJobStatus.PdrcjsRunning && !acknowledged.WorkerFinished,
                $"The cancel request must find project B's check still running: {acknowledged.Status}, workerFinished={acknowledged.WorkerFinished}.");
            Assert.AreNotEqual(PcbDrcJobStatus.PdrcjsCompleted, acknowledged.Status);
            Assert.IsEmpty(acknowledged.Findings);
            await Picture("cancelling");
            var (cancelled, _) = await Watch("cancel", (a, runningA2), (b, acknowledged));
            await Evidence("A-beside-cancel", cancelled[0]); await Evidence("B-cancelled", cancelled[1]);
            CheckedCopper(a, cancelled[0], savedA.Revision, "Project A's check beside project B's cancelled one");
            CollectionAssert.AreEqual(baselineA, Findings(cancelled[0]), "Project A's findings must not depend on project B's cancellation.");
            var terminalB = cancelled[1];
            Assert.AreEqual(PcbDrcJobStatus.PdrcjsCancelled, terminalB.Status, terminalB.ErrorCode + ": " + terminalB.ErrorMessage);
            Assert.IsTrue(terminalB.WorkerFinished, "Cancelled is reported only after the worker stopped.");
            Assert.IsTrue(terminalB.CancellationRequested);
            Assert.IsEmpty(terminalB.Findings, "A cancelled check never exposes the violations it had already found.");
            Assert.IsLessThan(1.0, terminalB.Progress);
            Assert.IsFalse(terminalB.ResultsFresh || terminalB.SnapshotComplete);
            Assert.AreEqual(savedB.Revision, terminalB.CheckedRevision);
            Assert.AreEqual(terminalB, await Read(b, b2), "A cancelled check stays cancelled.");
            Assert.AreEqual(terminalB, Parse(await mcp.Tool("kicad_pcb_drc_cancel", new
                { instanceId = b.InstanceId, documentJson = Json(b.Board), jobId = b2.JobId, processEpoch = b.Epoch })),
                "Cancelling again returns the same terminal state.");
            Assert.AreEqual(savedB, await State(b), "Cancelling a check must not change project B's board.");
            // Project A's check completed again; its editor was already proven after a completed check.
            savedB = await EditAndSave(b, "a cancelled check");

            // 4. Project A's custom rules do not compile: its check fails with a code that says so and names the file, the
            //    item and its exact line, while project B's first check after its cancellation completes with its first
            //    findings. Project A's editor still takes an edit and a save with the broken rules in place.
            rulesWritten = true;
            await File.WriteAllTextAsync(a.RulesPath, PcbDrcBrokenRules, token);
            var (a3, b3) = await StartBoth("failure", savedA.Revision, savedB.Revision);
            var (failure, _) = await Watch("failure", (a, a3), (b, b3));
            await Evidence("A-failed", failure[0]); await Evidence("B-after-cancel", failure[1]);
            var failedA = failure[0];
            Assert.AreEqual(PcbDrcJobStatus.PdrcjsFailed, failedA.Status, failedA.ErrorCode + ": " + failedA.ErrorMessage);
            Assert.AreEqual("design_rules_invalid", failedA.ErrorCode, failedA.ErrorMessage);
            StringAssert.Contains(failedA.ErrorMessage, "'not_a_rule'", "The failure must name the item that does not compile.");
            StringAssert.Contains(failedA.ErrorMessage, $"{a.RulesPath}', line 3, offset 2.",
                "The failure must name the rules file and the item's own line and offset in it.");
            Assert.IsEmpty(failedA.Findings, "A failed check never exposes findings.");
            Assert.IsTrue(failedA.WorkerFinished);
            Assert.IsFalse(failedA.CancellationRequested || failedA.ResultsFresh || failedA.SnapshotComplete);
            Assert.IsLessThan(1.0, failedA.Progress);
            Assert.AreEqual(failedA, await Read(a, a3), "A failed check stays failed.");
            // A check cancelled after its findings started leaves nothing behind: the next complete check of the same
            // board reports exactly the first run's real copper findings (n87c71e6665971bd2).
            CheckedCopper(b, failure[1], savedB.Revision, "Project B's first check after its cancellation");
            CollectionAssert.AreEqual(baselineB, Findings(failure[1]),
                "After the cancellation, a complete check of project B must report exactly its first findings (n87c71e6665971bd2).");
            savedA = await EditAndSave(a, "a failed check");

            // 5. The person corrects the rules by removing the broken item and keeps the rule that matches nothing, and
            //    project A recovers: a new check compiles the corrected rules and completes with its first findings.
            await File.WriteAllTextAsync(a.RulesPath, PcbDrcHarmlessRules, token);
            var a4 = Parse(await mcp.Tool("kicad_pcb_drc_start", StartArguments(a, savedA.Revision, Guid.NewGuid().ToString("D"))));
            Record("recovery-start", a, a4);
            var (recovered, _) = await Watch("recovery", (a, a4));
            await Evidence("A-recovered", recovered[0]);
            CheckedCopper(a, recovered[0], savedA.Revision, "Project A's check after its failure");
            CollectionAssert.AreEqual(baselineA, Findings(recovered[0]), "After recovery, project A's findings must be its first findings.");
            savedA = await EditAndSave(a, "a recovered check");
            Assert.AreEqual(savedB, await State(b), "Project A's failure and recovery must not change project B's board.");

            // 6. A person at project B's rendered PCB editor undoes and redoes the agent's last edit with the keyboard, and
            //    the agent saves the result.
            long agentWidth = (await Edited(b)).Width.ValueNm;
            async Task Keyboard(string key, long expected)
            {
                NativeKeyboard.SchematicShortcut(display, processId, key, "PCB Editor", true, true, clickFromLeft: 500, clickFromTop: 500);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(8));
                int delay = 25;
                try
                {
                    while ((await Edited(b)).Width.ValueNm != expected)
                    { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
                }
                catch (OperationCanceledException) when (limit.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    await Picture("keyboard-" + key + "-failed");
                    throw new AssertFailedException($"Ctrl+{key.ToUpperInvariant()} in project B's PCB editor did not change the edited track.");
                }
            }
            await Keyboard("z", agentWidth == 200_000 ? 220_000 : 200_000);
            await Keyboard("y", agentWidth);
            await Picture("keyboard");
            var keyboardState = await State(b);
            var keyboardSave = await mcp.Tool("kicad_document_save", new
                { instanceId = b.InstanceId, expectedStateJson = Json(keyboardState), operationId = Guid.NewGuid().ToString("D") });
            RequireToolSuccess(keyboardSave);
            Assert.AreEqual(LifecycleOperationStatus.LosSaved, SchematicJson.Parser.Parse<LifecycleOperationResult>(Text(keyboardSave)).Status);
            Assert.AreEqual(agentWidth, await SavedWidth(b));
            Console.WriteLine($"Two-project PCB checks: all outcomes proven at {clock.Elapsed.TotalSeconds:F1}s.");
        }
        finally
        {
            if (rulesWritten && a is not null) File.Delete(a.RulesPath); // Only the rules file this journey wrote.
            // Put each project's saved board back and reload it, so the steps after this one see their own fixture.
            foreach (var (native, target, path, saved) in restore)
            {
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await File.WriteAllBytesAsync(path, saved, limit.Token);
                    await native.InvokeAsync<RevertDocument, Empty>(new() { Document = target }, limit.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or NativeApiException)
                {
                    restoreFailure ??= error;
                }
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "pcb-drc-two-projects-timeline.txt"), timeline.ToString(), CancellationToken.None);
            Directory.Delete(statePath, true);
        }
        if (restoreFailure is not null) throw new AssertFailedException("Restoring the projects' saved boards failed.", restoreFailure);
        foreach (var (native, target, path, saved) in restore)
        {
            var state = await native.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = target }, token);
            Assert.IsFalse(state.NativeContentDirty, $"{path} must be reloaded from its saved file.");
            CollectionAssert.AreEqual(saved, await File.ReadAllBytesAsync(path, token));
            var fixtureTrack = await native.InvokeAsync<GetItemsById, GetItemsResponse>(new()
                { Header = new() { Document = target }, Items = { new KIID { Value = "33333333-3333-4333-8333-333333333333" } } }, token);
            Assert.HasCount(1, fixtureTrack.Items, $"{path} must hold its own fixture again.");
        }
    }
}
