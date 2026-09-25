using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Client;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    /// <summary>Shared PSU/CPU acceptance journey (psu-cpu-fixture-and-ownership.md §1.5 and §1.9) through the rendered per-level
    /// editor. An agent opens the fixture's schema 1 system.blocks.xml through the production MCP server. The person walks System,
    /// PSU and CPU; each level draws the legacy fallback grid (contract rbg-v2 section 9.2 F1) and writes nothing. On the CPU level a
    /// first drawn block is declined and the file stays the fixture's bytes. A move is undone and redone; the first layout edit stores
    /// every unplaced block where it was drawn in the same undo step (F1a). The Processor is resized with its handle, a caption-only
    /// block and a caption-only connection are drawn with the Round A1 tools (owner decisions n9f7cf92f32090daf, n98a3f3c41084f0ed),
    /// and a component choice chip is set on the fixture's Memory block (Round A4, n0b2a908b00e78823). Save stores schema 2 with
    /// every fixture fact and identity kept and exactly the drawn layout; a later edit is declined; and the level reopens from its
    /// stored presentation view exactly while the other levels keep drawing their fallback grid.</summary>
    private static async Task VerifyPsuCpuDiagramCanvas(NativeClient native, PsuCpuNativeContext context, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        const string title = "Structural diagram";
        static string S(Guid id) => id.ToString("D");
        // Fixture identities (contract section 1.2): blocks K11, states K12, revisions K13.
        var fixture = PsuCpuFixture.Graph();
        var system = fixture.SelectedRoot; var systemRevision = fixture.Inspect(system);
        var psu = systemRevision.Children[0]; var cpu = systemRevision.Children[1];
        var psuRevision = fixture.Inspect(psu); var cpuRevision = fixture.Inspect(cpu);
        var processor = cpuRevision.Children[0]; var memory = cpuRevision.Children[1];
        Assert.AreEqual((PsuCpuIds.Id(0x11, 1), PsuCpuIds.Id(0x12, 1), PsuCpuIds.Id(0x13, 1)), (system.BlockId, system.StateId, system.RevisionId));
        Assert.AreEqual((PsuCpuIds.Id(0x11, 2), PsuCpuIds.Id(0x13, 2)), (psu.BlockId, psu.RevisionId));
        Assert.AreEqual((PsuCpuIds.Id(0x11, 3), PsuCpuIds.Id(0x13, 3)), (cpu.BlockId, cpu.RevisionId));
        Assert.AreEqual((PsuCpuIds.Id(0x11, 8), PsuCpuIds.Id(0x13, 8)), (processor.BlockId, processor.RevisionId));
        Assert.AreEqual((PsuCpuIds.Id(0x11, 9), PsuCpuIds.Id(0x13, 9)), (memory.BlockId, memory.RevisionId));
        string documentId = S(fixture.DocumentId), processorId = S(processor.BlockId), memoryId = S(memory.BlockId);
        byte[] fixtureBytes = await File.ReadAllBytesAsync(Path.Combine(PsuCpuFixture.Directory, "system.blocks.xml"), token);
        CollectionAssert.AreEqual(fixtureBytes, await File.ReadAllBytesAsync(context.BlocksPath, token), "The journey starts from the frozen schema 1 fixture file.");

        var document = new P.ReadRecursiveDiagramEditor { DocumentId = documentId };
        Task<P.RecursiveDiagramEditorState> Read() => native.InvokeAsync<P.ReadRecursiveDiagramEditor, P.RecursiveDiagramEditorState>(document, token);
        async Task<P.RecursiveDiagramEditorState> Wait(string step, Func<P.RecursiveDiagramEditorState, bool> condition)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            P.RecursiveDiagramEditorState current = new();
            try
            {
                while (true)
                {
                    current = await Read();
                    // A step is complete once the editor is idle and any pointer drag has been released.
                    if (!current.Busy && !current.Dragging && condition(current)) return current;
                    await Task.Delay(50, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-canvas-timeout-" + step + ".json"), SchematicJson.Formatter.Format(current), token);
                await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-canvas-timeout-" + step + ".png"), token);
                throw new AssertFailedException("The PSU/CPU canvas step '" + step + "' did not reach its expected state; its state and screen are retained.");
            }
        }
        void Key(string key, bool control = false, bool alt = false) => NativeKeyboard.SchematicShortcut(display, processId, key, title, control, false, altKey: alt);
        void Type(string value) { foreach (char character in value) Key(character.ToString()); }
        void Click(int x, int y) => NativeKeyboard.SchematicShortcut(display, processId, "click", title, false, true, clickFromLeft: x, clickFromTop: y);
        P.DiagramControlRect Find(P.RecursiveDiagramEditorState at, string name) => at.Controls.Single(c => c.Name == name);
        // A diagram point as a window pixel. The point must be drawn inside the canvas and right of the canvas-edge palette, so the
        // press lands on the diagram element under it and nowhere else.
        (int X, int Y) Pixel(P.RecursiveDiagramEditorState at, double x, double y)
        {
            int px = (int)Math.Round(at.CanvasWindowX + (x - at.CanvasOriginX) * at.CanvasScale);
            int py = (int)Math.Round(at.CanvasWindowY + (y - at.CanvasOriginY) * at.CanvasScale);
            int paletteRight = at.Controls.Where(c => c.Name.StartsWith("DiagramPalette", StringComparison.Ordinal) && c.Shown)
                .Select(c => c.X + c.Width).DefaultIfEmpty(at.CanvasWindowX).Max();
            Assert.IsTrue(px > paletteRight && px < at.CanvasWindowX + (int)at.CanvasPixelWidth && py > at.CanvasWindowY
                && py < at.CanvasWindowY + (int)at.CanvasPixelHeight, $"Diagram point ({x}, {y}) must be drawn inside the canvas, right of the palette.");
            return (px, py);
        }
        async Task At(double x, double y) { var (px, py) = Pixel(await Read(), x, y); Click(px, py); }
        async Task Drag(double x, double y, double toX, double toY)
        {
            var at = await Read(); var from = Pixel(at, x, y); var to = Pixel(at, toX, toY);
            NativeKeyboard.SchematicShortcut(display, processId, "drag", title, false, true, clickFromLeft: from.X, clickFromTop: from.Y, dragToLeft: to.X, dragToTop: to.Y);
        }
        async Task Press(string name)
        {
            // The inspector re-lays out after a selection or a facet detail changes; press a control only where two readings agree.
            var at = await Read(); var control = Find(at, name);
            using (var settle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                settle.CancelAfter(TimeSpan.FromSeconds(5));
                while (true)
                {
                    await Task.Delay(120, settle.Token);
                    var moved = Find(await Read(), name);
                    if (moved.Equals(control)) break;
                    control = moved;
                }
            }
            Assert.IsTrue(control.Shown && control.Enabled, name + " must be shown and enabled before it is pressed.");
            Click(control.X + control.Width / 2, control.Y + control.Height / 2);
        }
        async Task Popup()
        {
            using var menu = CancellationTokenSource.CreateLinkedTokenSource(token); menu.CancelAfter(TimeSpan.FromSeconds(15)); int count = 0;
            do { NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, observePopupCount: value => count = value); if (count == 0) await Task.Delay(50, menu.Token); }
            while (count == 0);
        }
        bool Tool(P.RecursiveDiagramEditorState at, string tool, string strip, string palette) => at.CanvasTool == tool && Find(at, strip).Active && Find(at, palette).Active;
        static P.DiagramPresentationViewData? View(P.RecursiveDiagramEditorState at) => at.LevelDraft?.Scope?.LocalDiagram?.Presentation;
        static string[] Placed(P.RecursiveDiagramEditorState at) => View(at)?.Blocks.Select(b => b.BlockId).ToArray() ?? [];
        static (string X, string Y, string W, string H) Rect(P.RecursiveDiagramEditorState at, string block) =>
            View(at)?.Blocks.SingleOrDefault(b => b.BlockId == block)?.Rect is { } r ? (r.X, r.Y, r.Width, r.Height) : ("", "", "", "");
        Task Capture(string name) => CaptureRecursive(display, Path.Combine(evidence, instanceId + "-canvas-" + name + ".png"), token);
        Task Retain(string name, P.RecursiveDiagramEditorState at) =>
            File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-canvas-" + name + "-state.json"), SchematicJson.Formatter.Format(at), token);
        async Task Closed()
        {
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(token); closing.CancelAfter(TimeSpan.FromSeconds(15));
            while (NativeKeyboard.HasWindow(display, processId, title)) await Task.Delay(50, closing.Token);
        }
        // The level as the fixture stores it: its exact revision, children, connections, ports and notes, no stored layout, nothing new.
        void Level(P.RecursiveDiagramEditorState at, string step, BlockSelection scope, RecursiveBlockRevision revision)
        {
            Assert.AreEqual("", at.ErrorCode, step + ": the level opens without an error.");
            Assert.AreEqual(S(scope.RevisionId), at.DiagramPath[^1].RevisionId, step + ": the level shown is the fixture's exact revision.");
            Assert.AreEqual(S(scope.RevisionId), at.LevelDraft.Scope.Baseline.RevisionId);
            CollectionAssert.AreEqual(revision.Children.Select(c => S(c.BlockId) + "/" + S(c.RevisionId)).ToArray(),
                at.LevelDraft.Scope.Children.Select(c => c.BlockId + "/" + c.RevisionId).ToArray(), step + ": the children are the fixture's exact revisions.");
            CollectionAssert.AreEqual(revision.LocalDiagram.Connections.Select(c => S(c.ConnectionId) + "/" + S(c.RevisionId)).ToArray(),
                at.LevelDraft.Scope.LocalDiagram.Connections.Select(c => c.ConnectionId + "/" + c.RevisionId).ToArray(), step + ": the connections are the fixture's.");
            CollectionAssert.AreEqual(revision.LocalDiagram.Interfaces.Select(i => S(i.Id) + " " + i.Name).ToArray(),
                at.LevelDraft.Scope.LocalDiagram.Interfaces.Select(i => i.Id + " " + i.Name).ToArray(), step + ": the boundary ports are the fixture's.");
            CollectionAssert.AreEqual(revision.LocalDiagram.Notes.Select(n => S(n.Id)).ToArray(),
                at.LevelDraft.Scope.LocalDiagram.Annotations.Select(n => n.Id).ToArray(), step + ": the notes are the fixture's.");
            Assert.IsEmpty(Placed(at), step + ": a schema 1 level has no stored layout.");
            Assert.IsFalse(at.Dirty, step + ": viewing and navigating change nothing.");
            Assert.IsEmpty(at.LevelDraft.NewChildren); Assert.IsEmpty(at.LevelDraft.NewConnections); Assert.IsEmpty(at.LevelDraft.ChildDrafts);
            // Each canvas note is drawn with its lines broken between words, so its shown lines read back as its text
            // ("Keep sensing away from sw" / "itching nodes." once split a word that fitted the next line).
            var canvasNotes = revision.LocalDiagram.Notes.Where(n => n.Target.Kind == DiagramAnnotationTargetKind.Canvas).ToArray();
            CollectionAssert.AreEqual(canvasNotes.Select(n => S(n.Id)).ToArray(), at.CanvasNotes.Select(n => n.AnnotationId).ToArray(),
                step + ": every canvas note is drawn.");
            foreach (var (note, drawn) in canvasNotes.Zip(at.CanvasNotes))
            {
                Assert.IsTrue(drawn.Rect.Shown, step + ": the note \"" + note.Text + "\" is drawn inside the canvas.");
                Assert.AreEqual(note.Text, string.Join(" ", drawn.Lines), step + ": the note's lines break between words.");
            }
        }

        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string stateRoot = Directory.CreateTempSubdirectory("kicad-psu-cpu-canvas-mcp-").FullName;
        try
        {
            await using var mcp = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet", Arguments = [Path.Combine(FindRoot(), "automation", "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll")],
                EnvironmentVariables = new Dictionary<string, string?> { ["KICAD_AUTOMATION_STATE_DIRECTORY"] = stateRoot }
            }), cancellationToken: token);
            var attach = await mcp.CallToolAsync("kicad_instance_attach", new Dictionary<string, object?>
                { ["endpoint"] = native.Endpoint, ["expectedInstanceId"] = instanceId }, cancellationToken: token);
            Assert.IsFalse(attach.IsError == true, "The MCP server attaches to this project's KiCad instance.");
            async Task<P.RecursiveDiagramEditorState> Open(string step)
            {
                var opened = await mcp.CallToolAsync("kicad_diagram_open", new Dictionary<string, object?> { ["instanceId"] = instanceId,
                    ["repositoryRoot"] = context.ProjectDirectory, ["sourcePath"] = context.BlocksPath, ["documentId"] = documentId }, cancellationToken: token);
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-canvas-" + step + "-open.json"), JsonSerializer.Serialize(opened), token);
                Assert.IsFalse(opened.IsError == true, "The PSU/CPU diagram opens in the native per-level editor; the response is retained.");
                return await Wait(step, s => s.Ready && s.Rendered && s.DiagramPath.Count == 1);
            }
            // The layout the editor actually drew for the viewed level (contract rbg-v2 section 9.2), as an agent observes it.
            async Task<JsonElement> Observe(string label)
            {
                var at = await Read();
                var observed = await mcp.CallToolAsync("kicad_diagram_observe", new Dictionary<string, object?>
                {
                    ["instanceId"] = instanceId, ["documentId"] = documentId, ["expectedSourceToken"] = at.SourceToken,
                    ["expectedViewRevision"] = at.ViewRevision, ["views"] = new[] { new { viewId = "level", pixelWidth = 800, pixelHeight = 600 } }
                }, cancellationToken: token);
                if (observed.IsError == true)
                    await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-canvas-" + label + "-observation-error.json"), JsonSerializer.Serialize(observed), token);
                Assert.IsFalse(observed.IsError == true, "The " + label + " level can be observed; the response is retained.");
                var view = JsonSerializer.SerializeToElement(observed).GetProperty("structuredContent").GetProperty("observation").GetProperty("views")[0];
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-canvas-" + label + "-observation.json"), view.GetRawText(), token);
                return view.GetProperty("resolvedLayout");
            }
            static string Point(JsonElement point) => point.GetProperty("x").GetString() + "," + point.GetProperty("y").GetString();
            static string Box(JsonElement rect) => string.Join(",", new[] { "x", "y", "width", "height" }.Select(p => rect.GetProperty(p).GetString()));
            // Every block drawn where it is expected, in the level's child order, with where its position came from.
            void Drawn(JsonElement layout, string step, params (Guid Block, string Rect, string Source)[] expected)
            {
                CollectionAssert.AreEqual(expected.Select(e => S(e.Block) + " " + e.Rect + " " + e.Source).ToArray(), layout.GetProperty("blocks").EnumerateArray()
                    .Select(b => b.GetProperty("blockId").GetString() + " " + Box(b.GetProperty("rect")) + " " + b.GetProperty("source").GetString()).ToArray(),
                    step + ": the blocks are drawn exactly where expected.");
                Assert.AreEqual(0U, layout.GetProperty("dormantEntries").GetUInt32(), step + ": nothing stored is left undrawn.");
            }
            // Unplaced boundary ports sit at (40, 90 + 85k) on the left (rule F3), and the frame is computed, not stored.
            void FallbackBoundary(JsonElement layout, string step, BlockSelection scope, RecursiveBlockRevision revision)
            {
                CollectionAssert.AreEqual(revision.LocalDiagram.Interfaces.Select((p, k) => S(p.Id) + " DPS_LEFT 40," + (90 + 85 * k) + " RPS_FALLBACK").ToArray(),
                    layout.GetProperty("ports").EnumerateArray().Where(p => p.GetProperty("blockId").GetString() == S(scope.BlockId))
                        .Select(p => p.GetProperty("interfaceId").GetString() + " " + p.GetProperty("side").GetString() + " " + Point(p.GetProperty("anchor"))
                            + " " + p.GetProperty("source").GetString()).ToArray(), step + ": the boundary ports use the fallback column.");
                Assert.AreEqual("RPS_FALLBACK", layout.GetProperty("frameSource").GetString(), step + ": no frame is stored.");
            }
            string[] Routes(JsonElement layout) => layout.GetProperty("routes").EnumerateArray()
                .Select(r => r.GetProperty("connectionId").GetString() + " " + r.GetProperty("source").GetString()).ToArray();

            // An agent opens the fixture's schema 1 file. The System level draws its two blocks on the legacy grid; opening writes nothing.
            var opened = await Open("open");
            Level(opened, "system", system, systemRevision);
            Assert.AreEqual(1U, opened.StoredSchemaVersion, "The fixture file is schema 1."); Assert.IsTrue(opened.SourceWritable);
            var systemLayout = await Observe("system");
            Drawn(systemLayout, "system", (psu.BlockId, "140,110,240,145", "RPS_FALLBACK"), (cpu.BlockId, "510,110,240,145", "RPS_FALLBACK"));
            FallbackBoundary(systemLayout, "system", system, systemRevision);
            CollectionAssert.AreEqual(systemRevision.LocalDiagram.Connections.Select(c => S(c.ConnectionId) + " RPS_FALLBACK").ToArray(), Routes(systemLayout));
            VerifyRoutesClear(systemLayout, "system");
            await Capture("system");

            // System -> PSU: the four PSU blocks on the two-column grid, every connection on its computed path.
            Key("Escape"); Key("Right");
            await Wait("psu-selected", s => s.Draft.Baseline.BlockId == S(psu.BlockId));
            Key("Return");
            var psuLevel = await Wait("psu-level", s => s.Rendered && s.DiagramPath.Count == 2 && s.DiagramPath[^1].BlockId == S(psu.BlockId));
            Level(psuLevel, "psu", psu, psuRevision);
            // Design QA P2-7 and round 2 (R2-P2-1) at the default window size: every connection caption of the crowded PSU level is
            // drawn, the four that end at a boundary port as well (the approved sketch A3 captions such a connection beside the
            // port's own name), each clear of blocks, handles, ports, wires and the other captions, beside its own connection or in
            // the free space around it. A caption whose whole text has no clear place is shortened with "…", as a block caption is,
            // and shows its whole text on hover; none is left out. Each caption is matched to its own connection by identity, not
            // by its text ("Rail…" could otherwise stand for either rail's sense connection).
            var psuConnections = psuRevision.LocalDiagram.Connections.ToDictionary(c => S(c.ConnectionId),
                c => fixture.Connections(psu.BlockId).Inspect(c).Name, StringComparer.Ordinal);
            await Capture("psu-captions");
            var psuShot = await CapturedWindow.LoadAsync(Path.Combine(evidence, instanceId + "-canvas-psu-captions.png"), WindowOrigin(display, processId, title), token);
            VerifyCaptions(psuShot, psuLevel, "psu", psuConnections);
            Assert.IsTrue(psuLevel.ConnectionCaptions.All(c => psuConnections.ContainsKey(c.ObjectId)), "psu: every caption belongs to a connection of the level.");
            var psuLayout = await Observe("psu");
            var psuChildren = psuRevision.Children;
            Drawn(psuLayout, "psu", (psuChildren[0].BlockId, "140,110,240,145", "RPS_FALLBACK"), (psuChildren[1].BlockId, "510,110,240,145", "RPS_FALLBACK"),
                (psuChildren[2].BlockId, "140,360,240,145", "RPS_FALLBACK"), (psuChildren[3].BlockId, "510,360,240,145", "RPS_FALLBACK"));
            FallbackBoundary(psuLayout, "psu", psu, psuRevision);
            CollectionAssert.AreEqual(psuRevision.LocalDiagram.Connections.Select(c => S(c.ConnectionId) + " RPS_FALLBACK").ToArray(), Routes(psuLayout));
            // Design QA P2-5 on the shared design (rules F2, F4a and F4c as revised; cn2 erratum "connection layout", decision
            // n03892aa8cecf933f, and its review). Each computed leg leaves its blocks along their edges before turning: Rail A sense
            // and Fault, whose ends face the same way on two blocks of one column, run out 20 units and back instead of along the
            // blocks' edges. Connections keep apart: LDO supply and Rail B sense no longer meet end to end at (445, 182.5), and
            // Telemetry no longer runs along Measurements' leg at 468.75 (they turn up 10 units apart, at x 445 and 455). Only
            // connections from one port (Rail B and Rail B sense from the LDO's output, Rail A and Rail B into the Power port) share
            // the leg at that port. Level runs 7.5 units apart count as running beside each other too (the formal QA asks for 8
            // pixels): Rail A's and LDO supply's runs at 182.5 lie beside Rail B's run into the Power port at 175, so both turn
            // at their 20-unit limit (Rail A at x 120, LDO supply at x 400) and Rail B turns at x 415, which keeps each of those
            // runs beside it to the 20 units the end itself needs.
            CollectionAssert.AreEqual(new[] {
                    "40,90 90,90 90,146.25 140,146.25", "140,182.5 120,182.5 120,175 40,175", "380,182.5 400,182.5 400,146.25 510,146.25",
                    "510,182.5 415,182.5 415,175 40,175", "140,218.75 120,218.75 120,396.25 140,396.25", "510,182.5 465,182.5 465,432.5 380,432.5",
                    "380,468.75 445,468.75 445,396.25 510,396.25", "510,218.75 490,218.75 490,432.5 510,432.5", "510,468.75 455,468.75 455,260 40,260" },
                psuLayout.GetProperty("routes").EnumerateArray().Select(r => string.Join(" ", r.GetProperty("points").EnumerateArray().Select(Point))).ToArray(),
                "psu: every connection runs on its computed path.");
            // Those two are the only runs beside another, and neither can be avoided with three-segment paths: Rail B's run into the
            // Power port at 175 must reach the port, and Rail A's and LDO supply's runs at 182.5 must first leave their ends by 20
            // units. Each is held to exactly those 20 units.
            string Link(string name) => psuConnections.Single(c => c.Value == name).Key;
            VerifyRoutesClear(psuLayout, "psu", (Link("Rail A"), Link("Rail B")), (Link("LDO supply"), Link("Rail B")));
            await Retain("psu", psuLevel); await Capture("psu");

            // PSU -> System (Backspace keeps the PSU selected, as the level was left) -> CPU.
            Key("BackSpace");
            await Wait("system-again", s => s.Rendered && s.DiagramPath.Count == 1 && s.Draft.Baseline.BlockId == S(psu.BlockId));
            Key("Right");
            await Wait("cpu-selected", s => s.Draft.Baseline.BlockId == S(cpu.BlockId));
            Key("Return");
            var cpuLevel = await Wait("cpu-level", s => s.Rendered && s.DiagramPath.Count == 2 && s.DiagramPath[^1].BlockId == S(cpu.BlockId));
            Level(cpuLevel, "cpu", cpu, cpuRevision);
            Assert.AreEqual(S(system.RevisionId), cpuLevel.DiagramPath[0].RevisionId);
            var cpuLayout = await Observe("cpu");
            Drawn(cpuLayout, "cpu", (processor.BlockId, "140,110,240,145", "RPS_FALLBACK"), (memory.BlockId, "510,110,240,145", "RPS_FALLBACK"));
            FallbackBoundary(cpuLayout, "cpu", cpu, cpuRevision);
            CollectionAssert.AreEqual(cpuRevision.LocalDiagram.Connections.Select(c => S(c.ConnectionId) + " RPS_FALLBACK").ToArray(), Routes(cpuLayout));
            VerifyRoutesClear(cpuLayout, "cpu");
            CollectionAssert.AreEqual(fixtureBytes, await File.ReadAllBytesAsync(context.BlocksPath, token), "Opening and navigating never write.");
            await Retain("cpu", cpuLevel); await Capture("cpu");

            // Decline: a first drawn block stores the two unplaced blocks where they are drawn (F1a) and itself where it was put;
            // Decline discards all of it and the file stays the fixture's schema 1 bytes.
            await Press("DiagramPaletteAddBlock");
            await Wait("palette-add-block", s => Tool(s, "add-block", "RecursiveToolAddBlock", "DiagramPaletteAddBlock"));
            await At(630, 430); await Wait("declined-caption", s => s.CaptionEditor == "block");
            Type("Clock"); Key("Return");
            var drafted = await Wait("declined-block-added", s => s.LevelDraft.NewChildren.Count == 1 && s.CaptionEditor == "" && s.Dirty);
            string draftedId = drafted.LevelDraft.NewChildren[0].Selection.BlockId;
            CollectionAssert.AreEqual(new[] { processorId, memoryId, draftedId }, Placed(drafted), "The first layout edit keeps every block where it was drawn.");
            Assert.AreEqual(("140", "110", "240", "145"), Rect(drafted, processorId)); Assert.AreEqual(("510", "110", "240", "145"), Rect(drafted, memoryId));
            Assert.AreEqual(("510", "360", "240", "140"), Rect(drafted, draftedId), "A new block is placed where the person clicked.");
            await Capture("before-decline");
            ulong saves = drafted.CompletedSaveCount;
            await Press("RecursiveDecline");
            var declined = await Wait("declined", s => !s.Dirty && s.LevelDraft.NewChildren.Count == 0);
            Assert.IsEmpty(Placed(declined), "Decline removes the stored positions with the drawn block.");
            Assert.AreEqual(saves, declined.CompletedSaveCount, "Decline sends nothing to save.");
            Assert.AreEqual(1U, declined.StoredSchemaVersion);
            CollectionAssert.AreEqual(fixtureBytes, await File.ReadAllBytesAsync(context.BlocksPath, token), "Decline writes nothing.");

            // Undo: moving the Memory stores both unplaced blocks in the same step, so one Undo returns the level to no stored layout.
            await Drag(630, 182.5, 710, 222.5);
            var moved = await Wait("memory-moved", s => s.Dirty && Rect(s, memoryId) == ("590", "150", "240", "145"));
            Assert.AreEqual(memoryId, moved.Draft.Baseline.BlockId, "Dragging a block selects and moves it.");
            CollectionAssert.AreEqual(new[] { processorId, memoryId }, Placed(moved));
            Assert.AreEqual(("140", "110", "240", "145"), Rect(moved, processorId), "The Processor stays where it was drawn.");
            Key("z", control: true);
            var undone = await Wait("move-undone", s => !s.Dirty && Placed(s).Length == 0);
            Assert.AreEqual("", undone.ErrorCode);
            Key("y", control: true);
            await Wait("move-redone", s => s.Dirty && Rect(s, memoryId) == ("590", "150", "240", "145") && Rect(s, processorId) == ("140", "110", "240", "145"));
            await Capture("moved");

            // Resize the Processor with its lower-right handle; the palette's Undo and Ctrl+Y undo and redo it.
            await At(260, 182.5);
            await Wait("processor-selected", s => s.Draft.Baseline.BlockId == processorId && s.ConnectionDraft is null);
            await Drag(380, 255, 420, 285);
            await Wait("processor-resized", s => Rect(s, processorId) == ("140", "110", "280", "175"));
            await Press("DiagramPaletteUndo");
            await Wait("resize-undone", s => Rect(s, processorId) == ("140", "110", "240", "145") && Rect(s, memoryId) == ("590", "150", "240", "145"));
            Key("y", control: true);
            var resized = await Wait("resize-redone", s => Rect(s, processorId) == ("140", "110", "280", "175"));
            Assert.AreEqual(("590", "150", "240", "145"), Rect(resized, memoryId), "Resizing moves nothing else.");
            await Capture("resized");

            // The toolbar strip's Add block draws a caption-only block where the person clicks.
            await Press("RecursiveToolAddBlock");
            await Wait("strip-add-block", s => Tool(s, "add-block", "RecursiveToolAddBlock", "DiagramPaletteAddBlock"));
            await At(630, 430); await Wait("clock-caption", s => s.CaptionEditor == "block");
            Type("Clock"); Key("Return");
            var clockAdded = await Wait("clock-added", s => s.LevelDraft.NewChildren.Count == 1 && s.CaptionEditor == "" && s.Dirty);
            var clockDraft = clockAdded.LevelDraft.NewChildren[0]; string clockId = clockDraft.Selection.BlockId;
            Assert.AreEqual("Clock", clockDraft.Name); Assert.IsEmpty(clockDraft.Interfaces);
            Assert.AreEqual(("", "", ""), (clockDraft.Fields?.General ?? "", clockDraft.Fields?.Schematic ?? "", clockDraft.Fields?.Routing ?? ""));
            Assert.AreEqual("Clock", clockAdded.Draft.Name, "The new block is selected in the inspector.");
            Assert.IsEmpty(clockAdded.ShownRequirementFields, "A new block shows only its caption."); Assert.IsEmpty(clockAdded.ShownFacets);
            CollectionAssert.AreEqual(new[] { processorId, memoryId, clockId }, Placed(clockAdded));
            Assert.AreEqual(("510", "360", "240", "140"), Rect(clockAdded, clockId));
            CollectionAssert.AreEqual(new[] { S(processor.BlockId), S(memory.BlockId), clockId }, clockAdded.LevelDraft.Scope.Children.Select(c => c.BlockId).ToArray());

            // The palette's Connect draws a caption-only connection from the Clock to the Processor.
            await Press("DiagramPaletteConnect");
            await Wait("palette-connect", s => Tool(s, "connect", "RecursiveToolConnect", "DiagramPaletteConnect"));
            await At(630, 430); await Wait("connect-started", s => s.CanvasHint == "Click a port to finish connection");
            await At(280, 197.5); await Wait("connection-caption", s => s.CaptionEditor == "connection");
            Type("Clock feed"); Key("Return");
            var connected = await Wait("connected", s => s.LevelDraft.NewConnections.Count == 1 && s.CaptionEditor == "");
            var feedDraft = connected.LevelDraft.NewConnections[0]; string feedId = feedDraft.Selection.ConnectionId;
            Assert.AreEqual("Clock feed", feedDraft.Name); Assert.AreEqual("Clock feed", connected.ConnectionDraft.Name, "The new connection is selected.");
            CollectionAssert.AreEqual(new[] { (P.DiagramEndpointKind.DekUnresolved, clockId), (P.DiagramEndpointKind.DekUnresolved, processorId) },
                feedDraft.Endpoints.Select(e => (e.Kind, e.BlockId)).ToArray());
            Assert.IsEmpty(connected.ShownRequirementFields, "A new connection shows only its caption.");
            Assert.IsEmpty(connected.ShownConnectionDetails, "A new connection shows no details yet.");
            Assert.IsEmpty(View(connected)!.Routes, "The Clock feed's computed path runs along no other connection, so no route is stored.");
            await Capture("connected");

            // Round A4: a component choice chip on the fixture's Memory block. Add detail > Type gives it a first chosen value.
            var (memoryX, memoryY) = (590 + 30, 150 + 36);
            await Press("DiagramPaletteSelect"); await Wait("palette-select", s => Tool(s, "select", "RecursiveToolSelect", "DiagramPaletteSelect"));
            await At(memoryX, memoryY);
            var memorySelected = await Wait("memory-selected", s => s.Draft.Baseline.BlockId == memoryId && s.ConnectionDraft is null && s.SelectedInterfaceId == "");
            Assert.IsEmpty(memorySelected.ShownFacets, "The fixture's Memory has no component choices yet.");
            Assert.IsNull(memorySelected.BlockChips.SingleOrDefault(b => b.BlockId == memoryId), "A block without choices shows no chips.");
            await Press("RecursiveAddDetail"); await Popup(); Key("Home"); Key("Down"); Key("Return");
            await Wait("type-open", s => s.FacetEditor == "type" && s.FocusedControl == "RecursiveFacetValue");
            Type("EEPROM");
            await Wait("type-typed", s => s.Draft.Definition?.Type is { } type && type.Values.SequenceEqual(["EEPROM"]));
            Key("Return");
            var chosen = await Wait("type-kept", s => s.FacetEditor == "" && s.ShownFacets.SequenceEqual(["type"]));
            var chips = chosen.BlockChips.Single(b => b.BlockId == memoryId);
            CollectionAssert.AreEqual(new[] { "Type: EEPROM" }, chips.Chips.Select(c => c.Text).ToArray(), "The Memory shows its chosen type as a chip.");
            Assert.AreEqual(P.DefinitionChoiceStateData.DcsdSelected, chips.Chips[0].State);
            Assert.IsTrue(chips.Chips[0].Rect.Shown, "The chip is drawn inside the canvas.");
            Assert.IsNull(chosen.BlockChips.SingleOrDefault(b => b.BlockId == clockId), "The Clock is still only its caption.");
            CollectionAssert.AreEqual(new[] { memoryId }, chosen.LevelDraft.ChildDrafts.Select(d => d.Baseline.BlockId).ToArray(), "Only the Memory's own definition changed.");
            await Retain("before-save", chosen); await Capture("chip");

            // Save stores everything in one save: schema 2, the drawn layout on the CPU level, and the Memory's choice.
            saves = chosen.CompletedSaveCount;
            await Press("RecursiveSave");
            var saved = await Wait("saved", s => s.CompletedSaveCount > saves && !s.Dirty);
            Assert.AreEqual("", saved.ErrorCode); Assert.AreEqual("", saved.ErrorMessage);
            Assert.AreEqual(2U, saved.StoredSchemaVersion, "The first changed save stores schema 2 (contract rbg-v2 R4).");
            await Retain("saved", saved); await Capture("saved");
            string savedXml = await File.ReadAllTextAsync(context.BlocksPath, token);
            var (stored, storedVersion) = RecursiveBlockGraphXml.ReadVersioned(savedXml);
            Assert.AreEqual(2, storedVersion);
            Assert.AreEqual(fixture.DocumentId, stored.DocumentId);

            // Identities: System and CPU gain one revision each on top of the fixture's; the PSU and the Processor keep theirs.
            var storedSystemSelection = stored.SelectedRoot; var storedSystem = stored.Inspect(storedSystemSelection);
            Assert.AreEqual((system.BlockId, system.StateId), (storedSystemSelection.BlockId, storedSystemSelection.StateId));
            Assert.AreEqual(system.RevisionId, storedSystem.ParentRevisionId, "System's new revision follows the fixture's.");
            Assert.AreEqual(systemRevision.RequirementRevisionId, storedSystem.RequirementRevisionId);
            Assert.HasCount(2, storedSystem.Children); Assert.AreEqual(psu, storedSystem.Children[0], "The PSU keeps its exact fixture revision.");
            Assert.IsTrue(storedSystem.LocalDiagram.Layout.IsEmpty, "The System level still has no stored layout.");
            var storedCpuSelection = storedSystem.Children[1]; var storedCpu = stored.Inspect(storedCpuSelection);
            Assert.AreEqual((cpu.BlockId, cpu.StateId), (storedCpuSelection.BlockId, storedCpuSelection.StateId));
            Assert.AreEqual(cpu.RevisionId, storedCpu.ParentRevisionId, "The CPU's new revision follows the fixture's.");
            Assert.AreEqual(cpuRevision.Name, storedCpu.Name);
            Assert.AreEqual(cpuRevision.RequirementRevisionId, storedCpu.RequirementRevisionId, "Unchanged requirements keep their fixture revision.");
            CollectionAssert.AreEqual(new[] { S(system.BlockId) + "/" + S(storedSystemSelection.RevisionId), S(cpu.BlockId) + "/" + S(storedCpuSelection.RevisionId) },
                saved.DiagramPath.Select(p => p.BlockId + "/" + p.RevisionId).ToArray(), "The editor now shows the saved revisions.");
            Assert.HasCount(3, storedCpu.Children);
            Assert.AreEqual(processor, storedCpu.Children[0], "The Processor keeps its exact fixture revision; only its position is the CPU level's.");
            var storedMemorySelection = storedCpu.Children[1]; var storedMemory = stored.Inspect(storedMemorySelection); var memoryRevision = fixture.Inspect(memory);
            Assert.AreEqual((memory.BlockId, memory.StateId, memory.RevisionId), (storedMemorySelection.BlockId, storedMemorySelection.StateId, storedMemory.ParentRevisionId));
            Assert.AreEqual(memoryRevision.Name, storedMemory.Name);
            Assert.AreEqual(memoryRevision.RequirementRevisionId, storedMemory.RequirementRevisionId);
            Assert.IsTrue(memoryRevision.LocalDiagram.SameContents(storedMemory.LocalDiagram), "The Memory keeps its ports.");
            Assert.IsTrue(memoryRevision.EffectiveComponentBindings.SameContents(storedMemory.EffectiveComponentBindings), "The Memory keeps its U6 binding.");
            Assert.IsNull(memoryRevision.PhysicalAllocation); Assert.IsNull(storedMemory.PhysicalAllocation, "A chosen type adds no physical allocation.");
            var memoryType = storedMemory.EffectiveDefinition.Get(BlockDefinitionFacet.Type);
            Assert.AreEqual(DefinitionChoiceState.Selected, memoryType.State, "The chip's choice is saved as chosen.");
            CollectionAssert.AreEqual(new[] { "EEPROM" }, memoryType.Values.ToArray());
            foreach (var facet in Enum.GetValues<BlockDefinitionFacet>().Where(f => f != BlockDefinitionFacet.Type))
                Assert.AreEqual(DefinitionChoiceState.Unspecified, storedMemory.EffectiveDefinition.Get(facet).State, facet + " stays unstated.");
            var storedClockSelection = storedCpu.Children[2]; var storedClock = stored.Inspect(storedClockSelection);
            Assert.AreEqual(clockId, S(storedClockSelection.BlockId), "The drawn block keeps the identity it was drawn with.");
            Assert.IsFalse(fixture.States.Any(s => s.BlockId == storedClockSelection.BlockId), "The drawn block has a new identity.");
            Assert.AreEqual("Clock", storedClock.Name); Assert.IsNull(storedClock.ParentRevisionId); Assert.IsEmpty(storedClock.Children);
            Assert.IsNull(storedClock.Diagram, "A caption-only block stores no diagram.");
            Assert.IsTrue(storedClock.EffectiveDefinition.SameContents(BlockDefinition.Empty), "A caption-only block stores no component choices.");
            Assert.IsNull(storedClock.ComponentBindings); Assert.IsNull(storedClock.PhysicalAllocation);
            Assert.AreEqual(DiagramRequirements.Empty, stored.Requirements(storedClockSelection).Requirements, "A caption-only block stores no requirements.");
            Assert.AreEqual(fixture.Requirements(cpu).Requirements, stored.Requirements(storedCpuSelection).Requirements);
            var archive = stored.Connections(cpu.BlockId);
            CollectionAssert.AreEqual(cpuRevision.LocalDiagram.Connections.ToArray(), storedCpu.LocalDiagram.Connections.Take(4).ToArray(),
                "The CPU level's fixture connections keep their exact revisions.");
            Assert.HasCount(5, storedCpu.LocalDiagram.Connections);
            var storedFeed = archive.Inspect(storedCpu.LocalDiagram.Connections[4]);
            Assert.AreEqual(feedId, S(storedFeed.Selection.ConnectionId), "The drawn connection keeps the identity it was drawn with.");
            Assert.AreEqual(("Clock feed", DiagramConnectionKind.Abstract), (storedFeed.Name, storedFeed.Kind));
            Assert.IsNull(storedFeed.ParentRevisionId); Assert.IsEmpty(storedFeed.Members);
            CollectionAssert.AreEqual(new[] { (DiagramEndpointKind.Unresolved, storedClockSelection.BlockId, (Guid?)null), (DiagramEndpointKind.Unresolved, processor.BlockId, (Guid?)null) },
                storedFeed.Endpoints.Select(e => (e.Kind, e.BlockId, e.InterfaceId)).ToArray(), "The connection joins the Clock and the Processor, both still unresolved.");
            Assert.AreEqual(DiagramRequirements.Empty, archive.Requirements(storedFeed.Selection).Requirements, "A caption-only connection stores no requirements.");

            // The CPU level's layout is exactly what was drawn, in drawing order; no port, route or frame was placed.
            var layout = storedCpu.LocalDiagram.Layout;
            CollectionAssert.AreEqual(new[] {
                    new DiagramBlockPlacement(processor.BlockId, new DiagramRect(140, 110, 280, 175)),
                    new DiagramBlockPlacement(memory.BlockId, new DiagramRect(590, 150, 240, 145)),
                    new DiagramBlockPlacement(storedClockSelection.BlockId, new DiagramRect(510, 360, 240, 140)) },
                layout.BlockPlacements.ToArray(), "The saved presentation view holds exactly the drawn positions and sizes.");
            Assert.IsEmpty(layout.PortPlacements); Assert.IsEmpty(layout.ConnectionRoutes); Assert.IsNull(layout.Frame);
            Assert.IsTrue(stored.Inspect(psu).LocalDiagram.Layout.IsEmpty, "The PSU level still has no stored layout.");

            // Every other fixture fact is kept exactly: each fixture revision (block, connection and requirement) is written unchanged,
            // and each fixture state is unchanged except that System, CPU and Memory now point at their new revisions.
            var before = XDocument.Parse(RecursiveBlockGraphXml.Write(fixture)); var after = XDocument.Parse(savedXml);
            static Dictionary<string, XElement> Index(XDocument xml, string name) => xml.Descendants()
                .Where(e => e.Name.LocalName == name && e.Attribute("id") is not null).ToDictionary(e => e.Attribute("id")!.Value, StringComparer.Ordinal);
            var afterRevisions = Index(after, "revision"); var afterStates = Index(after, "state");
            foreach (var (id, element) in Index(before, "revision"))
                Assert.IsTrue(afterRevisions.TryGetValue(id, out var kept) && XNode.DeepEquals(element, kept), "Fixture revision " + id + " is kept exactly.");
            var heads = new Dictionary<string, Guid> { [S(system.StateId)] = storedSystemSelection.RevisionId,
                [S(cpu.StateId)] = storedCpuSelection.RevisionId, [S(memory.StateId)] = storedMemorySelection.RevisionId };
            foreach (var (id, element) in Index(before, "state"))
            {
                var expected = new XElement(element);
                if (heads.TryGetValue(id, out var head)) expected.SetAttributeValue("head", S(head));
                Assert.IsTrue(afterStates.TryGetValue(id, out var kept) && XNode.DeepEquals(expected, kept), "Fixture state " + id + " is kept.");
            }
            Assert.HasCount(fixture.Revisions.Length + 4, stored.Revisions, "Only System, CPU, Memory and the Clock gained a revision.");
            Assert.HasCount(fixture.States.Length + 1, stored.States);
            Assert.HasCount(fixture.Connections(cpu.BlockId).Revisions.Length + 1, archive.Revisions, "Only the Clock feed was added to the CPU level.");
            XElement Local(XDocument xml, Guid revision, string part) => xml.Descendants().Single(e => e.Name.LocalName == "revision" && e.Attribute("id")?.Value == S(revision))
                .Descendants().Single(e => e.Name.LocalName == part && e.Parent?.Name.LocalName == "local-diagram");
            foreach (string part in new[] { "interfaces", "annotations" })
                Assert.IsTrue(XNode.DeepEquals(Local(before, cpu.RevisionId, part), Local(after, storedCpuSelection.RevisionId, part)),
                    "The CPU level keeps its fixture " + part + ".");

            // A later move is declined: nothing is written.
            await Drag(630, 430, 630, 470);
            await Wait("clock-moved", s => s.Dirty && Rect(s, clockId) == ("510", "400", "240", "140"));
            Key("d", alt: true);
            var declinedLater = await Wait("later-declined", s => !s.Dirty && Rect(s, clockId) == ("510", "360", "240", "140"));
            Assert.AreEqual(saved.CompletedSaveCount, declinedLater.CompletedSaveCount);
            Assert.AreEqual(savedXml, await File.ReadAllTextAsync(context.BlocksPath, token), "Decline after a save writes nothing.");

            // Close and reopen from the file. The System level still draws its fallback grid; the CPU level draws exactly the stored
            // presentation view; the Clock feed follows its ends on the computed path (rule F4); the chip is back.
            Key("w", control: true); await Closed();
            Assert.AreEqual(savedXml, await File.ReadAllTextAsync(context.BlocksPath, token), "Closing a clean window writes nothing.");
            var reopened = await Open("reopen");
            Assert.IsFalse(reopened.Dirty); Assert.AreEqual(2U, reopened.StoredSchemaVersion); Assert.AreEqual(saved.SourceToken, reopened.SourceToken);
            Assert.AreEqual(S(storedSystemSelection.RevisionId), reopened.DiagramPath.Single().RevisionId);
            Assert.IsEmpty(Placed(reopened));
            var reopenedSystem = await Observe("reopened-system");
            Drawn(reopenedSystem, "reopened system", (psu.BlockId, "140,110,240,145", "RPS_FALLBACK"), (cpu.BlockId, "510,110,240,145", "RPS_FALLBACK"));
            Key("Escape"); Key("Right"); await Wait("reopened-psu-selected", s => s.Draft.Baseline.BlockId == S(psu.BlockId));
            Key("Right"); await Wait("reopened-cpu-selected", s => s.Draft.Baseline.BlockId == S(cpu.BlockId));
            Key("Return");
            var reopenedCpu = await Wait("reopened-cpu", s => s.Rendered && s.DiagramPath.Count == 2 && s.DiagramPath[^1].RevisionId == S(storedCpuSelection.RevisionId));
            Assert.IsFalse(reopenedCpu.Dirty);
            CollectionAssert.AreEqual(new[] { processorId, memoryId, clockId }, Placed(reopenedCpu), "The stored presentation view is the level's layout.");
            Assert.AreEqual(("140", "110", "280", "175"), Rect(reopenedCpu, processorId));
            Assert.AreEqual(("590", "150", "240", "145"), Rect(reopenedCpu, memoryId));
            Assert.AreEqual(("510", "360", "240", "140"), Rect(reopenedCpu, clockId));
            var reopenedLayout = await Observe("reopened-cpu");
            Drawn(reopenedLayout, "reopened cpu", (processor.BlockId, "140,110,280,175", "RPS_PLACED"), (memory.BlockId, "590,150,240,145", "RPS_PLACED"),
                (storedClockSelection.BlockId, "510,360,240,140", "RPS_PLACED"));
            FallbackBoundary(reopenedLayout, "reopened cpu", cpu, cpuRevision);
            CollectionAssert.AreEqual(storedCpu.LocalDiagram.Connections.Select(c => S(c.ConnectionId) + " RPS_FALLBACK").ToArray(), Routes(reopenedLayout));
            // The Clock's end faces the Processor from its left edge at mid-height. The Processor's end, with no port named, faces the
            // Clock on its right edge at its own point between the Processor's three unplaced ports (rule F2 as revised for design QA
            // P2-5; the legacy rule put it on the bottom corner, 285): the edge (height 175) has step points 35, 70, 105 and 140, the
            // ports at 43.75, 87.5 and 131.25 take 35, 70 and 140, and the end takes 105. The middle leg runs half-way between (F4).
            var feedPath = reopenedLayout.GetProperty("routes").EnumerateArray().Single(r => r.GetProperty("connectionId").GetString() == feedId);
            CollectionAssert.AreEqual(new[] { "510,430", "465,430", "465,215", "420,215" }, feedPath.GetProperty("points").EnumerateArray().Select(Point).ToArray(),
                "The Clock feed is drawn from the stored positions.");
            VerifyRoutesClear(reopenedLayout, "reopened cpu");
            var reopenedChips = reopenedCpu.BlockChips.Single(b => b.BlockId == memoryId);
            CollectionAssert.AreEqual(new[] { "Type: EEPROM" }, reopenedChips.Chips.Select(c => c.Text).ToArray(), "The Memory's chip is back.");
            Assert.IsNull(reopenedCpu.BlockChips.SingleOrDefault(b => b.BlockId == clockId));
            await Retain("reopened", reopenedCpu); await Capture("reopened");

            // Ports on a top and a bottom edge (review of design QA P2-5, rule F4d): Place port puts "Status" on the Memory's top
            // edge and "Reset" on the Processor's bottom edge, and Connect joins them. The wire leaves each of those ports straight
            // up or down for 20 units or more, never along the block's outline, and every other rule of the level still holds.
            // Decline then discards all of it.
            await Press("DiagramPalettePlacePort");
            await Wait("palette-place-port", s => Tool(s, "add-port", "RecursiveToolPlacePort", "DiagramPalettePlacePort"));
            await At(700, 150); await Wait("status-caption", s => s.CaptionEditor == "port");
            Type("Status"); Key("Return");
            var statusPlaced = await Wait("status-placed", s => s.CaptionEditor == "" && (View(s)?.Ports.Any(p => p.BlockId == memoryId && p.Side == P.DiagramPortSide.DpsTop) ?? false));
            static decimal Offset(P.DiagramPortPlacementData port) => decimal.Parse(port.Offset, System.Globalization.CultureInfo.InvariantCulture);
            var statusPort = View(statusPlaced)!.Ports.Single(p => p.BlockId == memoryId && p.Side == P.DiagramPortSide.DpsTop);
            Assert.IsTrue(Math.Abs(Offset(statusPort) - 110) <= 2, $"Status sits on the Memory's top edge where it was placed ({statusPort.Offset} along it).");
            await Press("DiagramPalettePlacePort");
            await Wait("palette-place-port-again", s => Tool(s, "add-port", "RecursiveToolPlacePort", "DiagramPalettePlacePort"));
            await At(280, 285); await Wait("reset-caption", s => s.CaptionEditor == "port");
            Type("Reset"); Key("Return");
            var resetPlaced = await Wait("reset-placed", s => s.CaptionEditor == "" && (View(s)?.Ports.Any(p => p.BlockId == processorId && p.Side == P.DiagramPortSide.DpsBottom) ?? false));
            var resetPort = View(resetPlaced)!.Ports.Single(p => p.BlockId == processorId && p.Side == P.DiagramPortSide.DpsBottom);
            Assert.IsTrue(Math.Abs(Offset(resetPort) - 140) <= 2, $"Reset sits on the Processor's bottom edge where it was placed ({resetPort.Offset} along it).");
            decimal statusX = 590 + Offset(statusPort), resetX = 140 + Offset(resetPort);
            await Press("DiagramPaletteConnect");
            await Wait("palette-connect-ports", s => Tool(s, "connect", "RecursiveToolConnect", "DiagramPaletteConnect"));
            await At(700, 150); await Wait("reset-line-started", s => s.CanvasHint == "Click a port to finish connection");
            await At(280, 285); await Wait("reset-line-caption", s => s.CaptionEditor == "connection");
            Type("Reset line"); Key("Return");
            var resetLine = await Wait("reset-line-added", s => s.LevelDraft.NewConnections.Count == 1 && s.CaptionEditor == "");
            var resetLink = resetLine.LevelDraft.NewConnections[0];
            CollectionAssert.AreEqual(new[] { (memoryId, statusPort.InterfaceId), (processorId, resetPort.InterfaceId) },
                resetLink.Endpoints.Select(e => (e.BlockId, e.InterfaceId)).ToArray(), "Reset line joins the two new ports.");
            var portsLayout = await Observe("top-bottom-ports");
            var resetRoute = portsLayout.GetProperty("routes").EnumerateArray().Single(r => r.GetProperty("connectionId").GetString() == resetLink.Selection.ConnectionId)
                .GetProperty("points").EnumerateArray().Select(p => (X: decimal.Parse(p.GetProperty("x").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                    Y: decimal.Parse(p.GetProperty("y").GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            Assert.AreEqual((statusX, 150m), resetRoute[0], "Reset line starts at the Status port on the Memory's top edge.");
            Assert.IsTrue(resetRoute[1].X == statusX && resetRoute[1].Y <= 130m, $"Reset line leaves the top edge straight up for 20 units or more, to ({resetRoute[1].X}, {resetRoute[1].Y}).");
            Assert.AreEqual((resetX, 285m), resetRoute[^1], "Reset line ends at the Reset port on the Processor's bottom edge.");
            Assert.IsTrue(resetRoute[^2].X == resetX && resetRoute[^2].Y >= 305m, $"Reset line reaches the bottom edge straight from below, from ({resetRoute[^2].X}, {resetRoute[^2].Y}).");
            VerifyRoutesClear(portsLayout, "top-bottom-ports");
            await Retain("top-bottom-ports", await Read()); await Capture("top-bottom-ports");
            // A port on the level's own boundary, on the frame's top side (rule F4d for boundary ports; review of the routing
            // follow-ups, finding 5): Place port just inside the top of the level frame, between the Processor and the Memory,
            // stores the frame and puts "Wake" on its top side, and Connect joins it to the Processor. The wire enters the level
            // from the port straight down for 20 units or more, and every other rule of the level still holds.
            static decimal Number(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            decimal frameTop = Number(portsLayout.GetProperty("frame").GetProperty("y").GetString()!);
            string cpuScope = S(cpu.BlockId);
            await Press("DiagramPalettePlacePort");
            await Wait("palette-place-boundary-port", s => Tool(s, "add-port", "RecursiveToolPlacePort", "DiagramPalettePlacePort"));
            await At(445, (double)frameTop + 8); await Wait("wake-caption", s => s.CaptionEditor == "port");
            Type("Wake"); Key("Return");
            var wakePlaced = await Wait("wake-placed", s => s.CaptionEditor == "" && (View(s)?.Ports.Any(p => p.BlockId == cpuScope && p.Side == P.DiagramPortSide.DpsTop) ?? false));
            var wakePort = View(wakePlaced)!.Ports.Single(p => p.BlockId == cpuScope && p.Side == P.DiagramPortSide.DpsTop);
            var storedFrame = View(wakePlaced)!.Frame;
            Assert.AreEqual(frameTop, Number(storedFrame.Y), "The first placed boundary port stores the frame drawn around the level.");
            decimal wakeX = Number(storedFrame.X) + Offset(wakePort), wakeY = Number(storedFrame.Y);
            Assert.IsTrue(Math.Abs(wakeX - 445) <= 2, $"Wake sits on the frame's top side where it was placed (x {wakeX}).");
            await Press("DiagramPaletteConnect");
            await Wait("palette-connect-wake", s => Tool(s, "connect", "RecursiveToolConnect", "DiagramPaletteConnect"));
            await At((double)wakeX, (double)wakeY); await Wait("wake-line-started", s => s.CanvasHint == "Click a port to finish connection");
            await At(260, 180); await Wait("wake-line-caption", s => s.CaptionEditor == "connection");
            Type("Wake line"); Key("Return");
            var wakeLine = await Wait("wake-line-added", s => s.LevelDraft.NewConnections.Count == 2 && s.CaptionEditor == "");
            var wakeLink = wakeLine.LevelDraft.NewConnections.Single(c => c.Name == "Wake line");
            Assert.AreEqual((cpuScope, wakePort.InterfaceId, processorId), (wakeLink.Endpoints[0].BlockId, wakeLink.Endpoints[0].InterfaceId, wakeLink.Endpoints[1].BlockId),
                "Wake line runs from the Wake port on the level's boundary to the Processor.");
            var boundaryLayout = await Observe("top-boundary-port");
            var wakeRoute = boundaryLayout.GetProperty("routes").EnumerateArray().Single(r => r.GetProperty("connectionId").GetString() == wakeLink.Selection.ConnectionId)
                .GetProperty("points").EnumerateArray().Select(p => (X: Number(p.GetProperty("x").GetString()!), Y: Number(p.GetProperty("y").GetString()!))).ToArray();
            Assert.AreEqual((wakeX, wakeY), wakeRoute[0], "Wake line starts at the Wake port on the frame's top side.");
            Assert.IsTrue(wakeRoute[1].X == wakeX && wakeRoute[1].Y >= wakeY + 20,
                $"Wake line enters the level from the top side straight down for 20 units or more, to ({wakeRoute[1].X}, {wakeRoute[1].Y}).");
            VerifyRoutesClear(boundaryLayout, "top-boundary-port");
            await Retain("top-boundary-port", await Read()); await Capture("top-boundary-port");
            Key("d", alt: true);
            await Wait("top-bottom-declined", s => !s.Dirty && s.LevelDraft.NewConnections.Count == 0
                && !(View(s)?.Ports.Any(p => p.Side is P.DiagramPortSide.DpsTop or P.DiagramPortSide.DpsBottom) ?? false));
            Key("w", control: true); await Closed();
            Assert.AreEqual(savedXml, await File.ReadAllTextAsync(context.BlocksPath, token), "Reopening, drawing, declining and closing write nothing.");

            // An agent refines the shared design's connections with the agent tools (ledger pf92d0ecdec8805b4) while no editor is
            // open. On the PSU level, Rail B's end on the PSU's own Power port is left explicitly unresolved and then bound to that
            // port again; the tool reports how the port maps through the levels, to the System's Power connection and its three rails.
            // On the System level, Power's Rail A and Rail B become one group beside Return, each keeping its exact revision.
            static Guid K(int kind, long n) => PsuCpuIds.Id(kind, n);
            var agentBase = RecursiveBlockGraphXml.Read(savedXml); var agentPsu = agentBase.Inspect(agentBase.SelectedRoot).Children[0];
            var railB = agentBase.Inspect(agentPsu).LocalDiagram.Connections.Single(c => c.ConnectionId == K(0x16, 0x0c));
            var agentCall = new Dictionary<string, object?> { ["instanceId"] = instanceId, ["expectedInstanceEpoch"] = native.Epoch,
                ["repositoryRoot"] = context.ProjectDirectory, ["sourcePath"] = context.BlocksPath, ["documentId"] = documentId,
                ["expectedSourceToken"] = saved.SourceToken, ["expectedRoot"] = agentBase.SelectedRoot, ["blockPath"] = new[] { agentBase.SelectedRoot, agentPsu },
                ["connectionPath"] = new[] { railB }, ["endpointIndex"] = 1, ["operationId"] = Guid.NewGuid(), ["actor"] = "PSU/CPU agent" };
            async Task<JsonElement> Edited(string tool, Dictionary<string, object?> call, params (string Key, object? Value)[] values)
            {
                var next = new Dictionary<string, object?>(call);
                foreach (var (key, value) in values) next[key] = value;
                var result = await mcp.CallToolAsync(tool, next, cancellationToken: token);
                var data = JsonSerializer.SerializeToElement(result).GetProperty("structuredContent");
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-canvas-agent-" + tool + "-" + Guid.NewGuid().ToString("N")[..8] + ".json"),
                    data.GetRawText(), token);
                Assert.IsFalse(result.IsError == true, tool + ": " + data.GetRawText());
                Assert.IsTrue(data.GetProperty("changed").GetBoolean(), tool + " saves a change.");
                return data;
            }
            var railBUnbound = await Edited("kicad_diagram_connection_endpoint_set", agentCall, ("action", "unbind"), ("intent", "Which output carries Rail B is open."));
            Assert.AreEqual(("Unresolved", S(agentPsu.BlockId)), (railBUnbound.GetProperty("endpoint").GetProperty("kind").GetString(),
                railBUnbound.GetProperty("endpoint").GetProperty("blockId").GetString()));
            var railBBound = await Edited("kicad_diagram_connection_endpoint_set", agentCall, ("expectedSourceToken", railBUnbound.GetProperty("sourceToken").GetString()),
                ("expectedRoot", railBUnbound.GetProperty("selectedRoot")), ("blockPath", railBUnbound.GetProperty("blockPath")),
                ("connectionPath", railBUnbound.GetProperty("connectionPath")), ("operationId", Guid.NewGuid()), ("action", "bind"), ("blockId", agentPsu.BlockId),
                ("interfaceId", K(0x15, 3)), ("intent", "Rail B leaves the PSU through its Power port."));
            var railBMapping = railBBound.GetProperty("boundaryMapping");
            Assert.AreEqual(("Power", railBBound.GetProperty("blockPath")[0].GetProperty("revisionId").GetString()),
                (railBMapping.GetProperty("interfaceName").GetString(), railBMapping.GetProperty("parentLevel").GetProperty("revisionId").GetString()));
            CollectionAssert.AreEquivalent(new[] { K(0x16, 2), K(0x16, 3), K(0x16, 4), K(0x16, 5) }.Select(S).ToArray(),
                railBMapping.GetProperty("parentConnections").EnumerateArray().Select(c => c.GetProperty("connectionId").GetString()).ToArray(),
                "Through the PSU's Power port Rail B reaches the System's Power connection and its three rails.");
            var boundGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(context.BlocksPath, token));
            var agentPower = boundGraph.Inspect(boundGraph.SelectedRoot).LocalDiagram.Connections.Single(c => c.ConnectionId == K(0x16, 2));
            Guid supplyRails = Guid.NewGuid();
            var railsGrouped = await Edited("kicad_diagram_connection_members_refine", new Dictionary<string, object?> { ["instanceId"] = instanceId,
                ["expectedInstanceEpoch"] = native.Epoch, ["repositoryRoot"] = context.ProjectDirectory, ["sourcePath"] = context.BlocksPath, ["documentId"] = documentId,
                ["expectedSourceToken"] = railBBound.GetProperty("sourceToken").GetString(), ["expectedRoot"] = boundGraph.SelectedRoot,
                ["blockPath"] = new[] { boundGraph.SelectedRoot }, ["connectionPath"] = new[] { agentPower }, ["memberIds"] = new[] { supplyRails, K(0x16, 5) },
                ["newMembers"] = JsonSerializer.SerializeToElement(new[] { new ConnectionMemberDefinition(supplyRails, "Supply rails", [K(0x16, 3), K(0x16, 4)],
                    DiagramConnectionKind.SignalGroup, "Deliver both regulated rails to the CPU.") },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }),
                ["operationId"] = Guid.NewGuid(), ["actor"] = "PSU/CPU agent" });
            CollectionAssert.AreEqual(new[] { "Supply rails", "Rail A", "Rail B", "Return" }, railsGrouped.GetProperty("members").EnumerateArray()
                .Select(m => m.GetProperty("revision").GetProperty("name").GetString()).ToArray());
            var agentStored = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(context.BlocksPath, token));
            var agentSystem = agentStored.Inspect(agentStored.SelectedRoot); var systemLinks = agentStored.Connections(system.BlockId);
            var powerGrouped = systemLinks.Inspect(agentSystem.LocalDiagram.Connections.Single(c => c.ConnectionId == K(0x16, 2)));
            CollectionAssert.AreEqual(fixture.Connections(system.BlockId).Inspect(systemRevision.LocalDiagram.Connections.Single(c => c.ConnectionId == K(0x16, 2)))
                .Members.Take(2).ToArray(), systemLinks.Inspect(powerGrouped.Members[0]).Members.ToArray(), "Rail A and Rail B keep their fixture revisions.");
            var railBStored = agentStored.Connections(psu.BlockId).Inspect(agentStored.Inspect(agentSystem.Children[0]).LocalDiagram.Connections
                .Single(c => c.ConnectionId == K(0x16, 0x0c)));
            Assert.AreEqual((DiagramEndpointKind.Interface, psu.BlockId, (Guid?)K(0x15, 3)), (railBStored.Endpoints[1].Kind, railBStored.Endpoints[1].BlockId,
                railBStored.Endpoints[1].InterfaceId), "Rail B ends on the PSU's Power port, which the System's Power connection uses from outside.");
            savedXml = await File.ReadAllTextAsync(context.BlocksPath, token);

            // A dense level stays responsive (review of design QA P2-5): another agent draws twelve blocks with eight ports each and
            // fifty connections between them on the System level. The editor lays a level's connection paths out once for each
            // change of its geometry and reuses that layout for every repaint and every state read; a drag of one block lays the
            // level out at most once for each position the block reaches (GTK may merge the fixture's eight pointer motions into fewer
            // positions, which the editor reports as drag_positions), and no single layout takes a second even in this
            // unoptimized build.
            var denseBase = RecursiveBlockGraphXml.Read(savedXml);
            var denseLevel = denseBase.StartLevelDraft(denseBase.SelectedRoot);
            var denseBlocks = new List<NewBlockOccurrence>(); var densePorts = new List<Guid[]>();
            for (int b = 0; b < 12; ++b)
            {
                var ports = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray(); densePorts.Add(ports);
                denseBlocks.Add(new NewBlockOccurrence(new BlockSelection(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), "Initial",
                    "Dense " + (b + 1), DiagramRequirements.Empty, [.. ports.Select((id, k) => new DiagramBoundaryInterface(id, "P" + (k + 1), ""))], null));
            }
            var denseLinks = new List<NewConnectionOccurrence>();
            for (int i = 0; i < 50; ++i)
            {
                int first = i % 12, second = (first + 1 + i * 7 % 11) % 12;
                denseLinks.Add(new NewConnectionOccurrence(new ConnectionSelection(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), "Initial",
                    "Link " + (i + 1), DiagramConnectionKind.Abstract, DiagramDomain.Unspecified, DiagramConnectionDirection.Unspecified,
                    [new(DiagramEndpointKind.Interface, denseBlocks[first].Selection.BlockId, densePorts[first][i / 12 % 8], "", null, [], null),
                     new(DiagramEndpointKind.Interface, denseBlocks[second].Selection.BlockId, densePorts[second][(i / 12 + 4) % 8], "", null, [], null)],
                    DiagramRequirements.Empty, null));
            }
            denseLevel = denseLevel with
            {
                Scope = denseLevel.Scope with { Children = denseLevel.Scope.Children.AddRange(denseBlocks.Select(n => n.Selection)),
                    Diagram = denseLevel.Scope.LocalDiagram with { Connections = denseLevel.Scope.LocalDiagram.Connections.AddRange(denseLinks.Select(l => l.Selection)) } },
                NewChildren = [.. denseBlocks], NewConnections = [.. denseLinks]
            };
            var noRevisions = System.Collections.Immutable.ImmutableDictionary<Guid, LevelRevisionId>.Empty;
            var denseGraph = denseBase.SaveLevelDraft(denseBase.SelectedRoot, [denseBase.SelectedRoot], denseLevel,
                new LevelRevisionIds(Guid.NewGuid(), Guid.NewGuid(), [], noRevisions, noRevisions), RecursiveBlockFixture.Origin("Another agent")).Graph;
            await File.WriteAllTextAsync(context.BlocksPath, RecursiveBlockGraphXml.Write(denseGraph), token);
            var denseOpen = await Open("dense");
            Assert.HasCount(14, denseOpen.LevelDraft.Scope.Children, "The System level shows the PSU, the CPU and the twelve drawn blocks.");
            var denseLayout = await Observe("dense");
            Assert.AreEqual(systemRevision.LocalDiagram.Connections.Length + 50, denseLayout.GetProperty("routes").GetArrayLength(),
                "Every connection of the dense level is drawn: the System level's own and the fifty drawn ones.");
            var settled = await Read(); var readAgain = await Read();
            Assert.IsTrue(settled.RouteLayouts > 0, "The editor reports how often it laid connection paths out.");
            Assert.AreEqual(settled.RouteLayouts, readAgain.RouteLayouts, "Reading the state again lays nothing out again: an unchanged level reuses its layout.");
            string denseId = S(denseBlocks[0].Selection.BlockId);
            var denseRect = denseLayout.GetProperty("blocks").EnumerateArray().Single(b => b.GetProperty("blockId").GetString() == denseId).GetProperty("rect");
            double Units(JsonElement value) => double.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            double denseX = Units(denseRect.GetProperty("x")) + Units(denseRect.GetProperty("width")) / 2, denseY = Units(denseRect.GetProperty("y")) + 30;
            var watch = Stopwatch.StartNew();
            await Drag(denseX, denseY, denseX + 40, denseY + 40);
            var denseDragged = await Wait("dense-dragged", s => s.Dirty && Placed(s).Contains(denseId));
            watch.Stop();
            ulong layoutsDuringDrag = denseDragged.RouteLayouts - readAgain.RouteLayouts;
            ulong dragPositions = denseDragged.DragPositions - readAgain.DragPositions;
            Console.WriteLine($"Dense level: {layoutsDuringDrag} layouts for {dragPositions} drag positions, slowest {denseDragged.SlowestRouteLayoutMicros} µs, "
                + $"latest {denseDragged.LatestRouteLayoutMicros} µs, drag settled after {watch.ElapsedMilliseconds} ms.");
            await Retain("dense-dragged", denseDragged); await Capture("dense-dragged");
            Assert.IsTrue(dragPositions >= 1, "The drag moved the block to at least one new position.");
            // Once per position, and at most once more for the stored placements the drag's first motion writes: laying each
            // position out again for the repaint and the state read (three layouts per position before the fix) fails.
            Assert.IsTrue(layoutsDuringDrag >= 1 && layoutsDuringDrag <= dragPositions + 1,
                $"A drag that reached {dragPositions} positions lays the dense level out at most once per position and once more, not {layoutsDuringDrag} times.");
            Assert.IsTrue(denseDragged.SlowestRouteLayoutMicros <= 1_000_000,
                $"The slowest layout of the dense level took {denseDragged.SlowestRouteLayoutMicros} µs, at most a second.");
            Key("d", alt: true); var denseDeclined = await Wait("dense-declined", s => !s.Dirty && Placed(s).Length == 0);
            // Design QA round 2, R2-P2-1: on the dense level every connection still shows its caption (none is left out, as the
            // earlier editor left out the ones without a clear place); a caption shortened for want of room shows its whole text on
            // hover, and the tooltip goes when the pointer leaves it. Where not even a shortened caption has a clear place, it is
            // drawn shortened where it covers least, never over a block.
            await Retain("dense-declined", denseDeclined);
            Assert.AreEqual(systemRevision.LocalDiagram.Connections.Length + 50, denseDeclined.ConnectionCaptions.Count(c => c.Shown),
                "dense: every connection of the dense level shows its caption.");
            // Where it covers least means over wires only here: a crowded caption covers no block, no port, no boundary port's name
            // and no other caption (review of design QA round 2).
            var denseShown = denseDeclined.ConnectionCaptions.Where(c => c.Shown).ToArray();
            foreach (var crowded in denseDeclined.ConnectionCaptions.Where(c => !c.Enabled))
            {
                Assert.IsTrue(denseDeclined.BlockTexts.All(b => RectsApart(crowded, b.Block)),
                    $"dense: the crowded caption '{crowded.Label}' covers no block.");
                Assert.IsTrue(denseDeclined.PortMarks.All(p => RectsApart(crowded, p)), $"dense: the crowded caption '{crowded.Label}' covers no port.");
                Assert.IsTrue(denseDeclined.BoundaryPortNames.All(n => RectsApart(crowded, n)), $"dense: the crowded caption '{crowded.Label}' covers no port's name.");
                Assert.IsTrue(denseShown.Where(o => !ReferenceEquals(o, crowded)).All(o => RectsApart(crowded, o)),
                    $"dense: the crowded caption '{crowded.Label}' covers no other caption.");
            }
            var shortenedCaption = denseDeclined.ConnectionCaptions.FirstOrDefault(c => c.Shown && c.Tooltip != "" && c.X > denseDeclined.CanvasWindowX + 120);
            Assert.IsNotNull(shortenedCaption, "dense: the crowded level shortens a caption for want of room.");
            Assert.IsTrue(shortenedCaption.Label.EndsWith('…') && shortenedCaption.Tooltip.StartsWith(shortenedCaption.Label[..^1].TrimEnd(), StringComparison.Ordinal),
                $"dense: the shortened caption '{shortenedCaption.Label}' names '{shortenedCaption.Tooltip}' in full.");
            NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: denseDeclined.CanvasWindowX + (int)denseDeclined.CanvasPixelWidth - 4,
                clickFromTop: denseDeclined.CanvasWindowY + (int)denseDeclined.CanvasPixelHeight - 4);
            ulong denseMotions = (await Read()).CanvasMotions;
            foreach (int dx in new[] { -3, 0, 2 })
                NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: shortenedCaption.X + shortenedCaption.Width / 2 + dx,
                    clickFromTop: shortenedCaption.Y + shortenedCaption.Height / 2);
            await Wait("dense-caption-hover", s => s.CanvasMotions > denseMotions && s.CanvasTooltip == shortenedCaption.Tooltip);
            // GTK shows a tooltip after the pointer rests for its delay; the capture waits past it. Only the reported tooltip, which
            // is read back from the canvas window, is asserted: the capture is not measured for the tooltip's popup.
            await Task.Delay(1500, token);
            await Capture("dense-caption-hover");
            // Every motion has arrived by now: the pointer rests here from this reading on.
            var hovered = await Wait("dense-caption-rested", s => s.CanvasTooltip == shortenedCaption.Tooltip);
            // The tooltip follows the canvas while the pointer stays still (review of design QA round 2): it is what lies under the
            // pointer on the level shown, found again after the canvas is drawn anew, not the text of the last hover.
            static bool Under(P.DiagramControlRect r, P.DiagramCanvasPoint p) => p.X >= r.X && p.X < r.X + r.Width && p.Y >= r.Y && p.Y < r.Y + r.Height;
            static string TipUnderPointer(P.RecursiveDiagramEditorState s) =>
                s.ConnectionCaptions.FirstOrDefault(c => c.Tooltip != "" && Under(c, s.CanvasPointer))?.Tooltip
                ?? s.BlockChips.Select(b => b.More).FirstOrDefault(m => m is not null && Under(m, s.CanvasPointer))?.Tooltip ?? "";
            Assert.AreEqual(shortenedCaption.Tooltip, TipUnderPointer(hovered), "dense: the pointer rests on the shortened caption.");
            // The keyboard selects the PSU (the arrows step through the level's blocks) and Enter opens its level.
            Key("Escape");
            for (int i = 0; i <= denseOpen.LevelDraft.Scope.Children.Count; ++i)
            {
                string selectedNow = (await Read()).Draft.Baseline.BlockId;
                if (selectedNow == S(psu.BlockId)) break;
                Key("Right"); await Wait($"dense-select-{i}", s => s.Draft.Baseline.BlockId != selectedNow);
            }
            await Wait("dense-psu-selected", s => s.Draft.Baseline.BlockId == S(psu.BlockId));
            Key("Return");
            var psuUnderPointer = await Wait("dense-tip-psu-level", s => s.Rendered && s.DiagramPath.Count == 2 && s.DiagramPath[^1].BlockId == S(psu.BlockId)
                && s.CanvasTooltip == TipUnderPointer(s));
            Assert.AreEqual(hovered.CanvasMotions, psuUnderPointer.CanvasMotions, "dense: the pointer did not move while the level changed.");
            Assert.AreNotEqual(shortenedCaption.Tooltip, psuUnderPointer.CanvasTooltip, "dense: on the PSU level the canvas no longer shows the System level's caption on hover.");
            Key("BackSpace");
            var backUnderPointer = await Wait("dense-tip-system-again", s => s.Rendered && s.DiagramPath.Count == 1 && s.CanvasTooltip == TipUnderPointer(s)
                && s.CanvasTooltip == shortenedCaption.Tooltip);
            Assert.AreEqual(hovered.CanvasMotions, backUnderPointer.CanvasMotions, "dense: back on the System level, the pointer still had not moved.");
            // The tooltip goes when the pointer leaves the canvas, although the last place the canvas saw the pointer is still on the
            // shortened caption.
            var inspector = Find(backUnderPointer, "RecursiveInspector");
            NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: inspector.X + inspector.Width / 2,
                clickFromTop: inspector.Y + 40);
            var left = await Wait("dense-caption-hover-left-canvas", s => s.CanvasTooltip == "");
            Assert.AreEqual(shortenedCaption.Tooltip, TipUnderPointer(left), "dense: the canvas last saw the pointer on the shortened caption.");
            NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: denseDeclined.CanvasWindowX + (int)denseDeclined.CanvasPixelWidth - 4,
                clickFromTop: denseDeclined.CanvasWindowY + (int)denseDeclined.CanvasPixelHeight - 4);
            await Wait("dense-caption-hover-left", s => s.CanvasTooltip == "" && s.CanvasMotions > left.CanvasMotions);
            Key("w", control: true); await Closed();
        }
        finally { Directory.Delete(stateRoot, true); }
    }

    private static async Task VerifyRecursiveEditor(NativeClient native, int processId, string display,
        string evidence, string instanceId, CancellationToken token)
    {
        var fixture = LinkedDiagramFixture.Create(); var graph = fixture.Graph;
        var initialBindings = RecursiveBlockComponentTests.UnresolvedFixture();
        var definitionDraft = graph.StartDraft(graph.SelectedRoot) with { Definition = RecursiveBlockDefinitionTests.Partial(), ComponentBindings = initialBindings };
        graph = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], definitionDraft, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
        string project = Path.GetDirectoryName((await native.HandshakeAsync(token)).ProjectPath)!;
        string source = Path.Combine(project, "system.design.xml");
        // A version 1 file as an earlier build stored it (the explicit version 1 writer); the first agent write upgrades it (R4).
        await File.WriteAllTextAsync(source, RecursiveBlockGraphXml.Write(graph, 1), token);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string stateRoot = Directory.CreateTempSubdirectory("kicad-recursive-mcp-").FullName;
        var interactionFailures = new List<Exception>();
        try
        {
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet", Arguments = [Path.Combine(FindRoot(), "automation", "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll")],
                EnvironmentVariables = new Dictionary<string, string?> { ["KICAD_AUTOMATION_STATE_DIRECTORY"] = stateRoot }
            }), cancellationToken: token);
            var attach = await client.CallToolAsync("kicad_instance_attach", new Dictionary<string, object?>
                { ["endpoint"] = native.Endpoint, ["expectedInstanceId"] = instanceId }, cancellationToken: token);
            Assert.IsFalse(attach.IsError == true);
            // Session-wide checks (tool list, diagram creation, schema 2 opening, the drawing tools and component choices) run in
            // the first project of each themed session; the second project repeats the editor journey up to the compact window
            // to prove instance isolation.
            bool sessionWide = FirstInSession(evidence, "recursive-editor-session-checks");
            if (sessionWide) await VerifyFlatEditorRetired(client, native, instanceId, evidence, token);
            // Creating a new system diagram next to this session's project through the same production MCP server and
            // live instance (contract rbg-v2 section 8); the created diagram is opened in the real editor at the end.
            var createdDiagram = sessionWide ? await VerifyDiagramCreationOverMcp(client, native, source, graph, instanceId, evidence, token) : null;
            var arguments = new Dictionary<string, object?> { ["instanceId"] = instanceId, ["repositoryRoot"] = project,
                ["sourcePath"] = source, ["documentId"] = graph.DocumentId.ToString("D") };
            var savedRead = await client.CallToolAsync("kicad_diagram_read", arguments, cancellationToken: token);
            Assert.IsFalse(savedRead.IsError == true);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-saved-diagram.json"), JsonSerializer.Serialize(savedRead), token);
            var savedReadData = JsonSerializer.SerializeToElement(savedRead).GetProperty("structuredContent");
            Assert.AreEqual(graph.DocumentId.ToString("D"), savedReadData.GetProperty("documentId").GetString());
            Assert.AreEqual(2, savedReadData.GetProperty("children").GetArrayLength());
            // Schema 2 agent reads (contract rbg-v2 section 8) report the exchange and the stored file format.
            Assert.AreEqual(2, savedReadData.GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual(1, savedReadData.GetProperty("storedSchemaVersion").GetInt32());
            Assert.IsTrue(savedReadData.GetProperty("sourceWritable").GetBoolean());
            Assert.AreEqual(2, savedReadData.GetProperty("connections").GetArrayLength());
            Assert.AreEqual("DCSD_UNKNOWN", savedReadData.GetProperty("block").GetProperty("definition").GetProperty("model").GetProperty("state").GetString());
            Assert.AreEqual("SGS_REQUIREMENT", savedReadData.GetProperty("block").GetProperty("definition").GetProperty("package").GetProperty("strength").GetString());
            Assert.AreEqual(initialBindings.Targets[0].ComponentId.ToString("D"), savedReadData.GetProperty("block")
                .GetProperty("componentBindings").GetProperty("targets")[0].GetProperty("componentId").GetString());
            byte[] originalSource = System.Text.Encoding.UTF8.GetBytes("Original requirements and user drawing description.\n");
            await File.WriteAllBytesAsync(Path.Combine(project, "original-requirements.txt"), originalSource, token);
            var assetArguments = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = savedReadData.GetProperty("sourceToken").GetString(),
                ["attachmentPath"] = "original-requirements.txt", ["archiveDirectory"] = "assets/refinement",
                ["attachmentId"] = Guid.NewGuid(), ["expectedSha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(originalSource)),
                ["expectedByteCount"] = originalSource.LongLength, ["mediaType"] = "text/plain"
            };
            var assetResult = await client.CallToolAsync("kicad_diagram_refinement_asset_capture", assetArguments, cancellationToken: token);
            Assert.IsFalse(assetResult.IsError == true);
            var capturedAsset = JsonSerializer.SerializeToElement(assetResult).GetProperty("structuredContent").GetProperty("attachment")
                .Deserialize<DiagramRefinementAttachment>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var originalInput = RecursiveBlockRefinementInputTests.Input(graph, 1) with { Attachments = [capturedAsset] };
            var inputArguments = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = savedReadData.GetProperty("sourceToken").GetString(),
                ["input"] = JsonSerializer.SerializeToElement(originalInput, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
            var inputResult = await client.CallToolAsync("kicad_diagram_refinement_input_record", inputArguments, cancellationToken: token);
            if (inputResult.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-refinement-input-error.json"), JsonSerializer.Serialize(inputResult), token);
            Assert.IsFalse(inputResult.IsError == true);
            Assert.IsTrue(JsonSerializer.SerializeToElement(inputResult).GetProperty("structuredContent").GetProperty("added").GetBoolean());
            // R4: recording the input is this version 1 file's first changed write; it stores schema 2 and reports the upgrade.
            Assert.AreEqual(1, JsonSerializer.SerializeToElement(inputResult).GetProperty("structuredContent").GetProperty("upgradedFromSchemaVersion").GetInt32());
            var repeatedInput = await client.CallToolAsync("kicad_diagram_refinement_input_record", inputArguments, cancellationToken: token);
            Assert.IsFalse(repeatedInput.IsError == true);
            Assert.IsFalse(JsonSerializer.SerializeToElement(repeatedInput).GetProperty("structuredContent").GetProperty("added").GetBoolean());
            Assert.IsFalse(JsonSerializer.SerializeToElement(repeatedInput).GetProperty("structuredContent").TryGetProperty("upgradedFromSchemaVersion", out _),
                "An observation writes nothing, so it upgrades nothing.");
            graph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.IsTrue(originalInput.SameContents(graph.RefinementInput(originalInput.Id)));
            savedRead = await client.CallToolAsync("kicad_diagram_read", arguments, cancellationToken: token);
            Assert.IsFalse(savedRead.IsError == true); savedReadData = JsonSerializer.SerializeToElement(savedRead).GetProperty("structuredContent");
            // Ledger pa48933d0fe0a5c2f: the agent's revision-bound context of the recorded input, its level by exact revision with the
            // three fields of every element, and the original prompt and attachment. Its fingerprint is compared after many later edits.
            var inputContext = await AgentContextOverMcp(client, arguments, savedReadData.GetProperty("sourceToken").GetString()!, token, inputId: originalInput.Id);
            AssertAgentContext(inputContext, graph, originalInput.BlockPath, originalInput, current: true, "recorded input");
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-agent-context-input.json"), inputContext.GetRawText(), token);
            string inputContextSha = inputContext.GetProperty("contextSha256").GetString()!;
            await File.WriteAllTextAsync(Path.Combine(project, "original-requirements.txt"), "Later replacement, not the original.", token);
            var readInputArguments = new Dictionary<string, object?>(arguments)
            { ["expectedSourceToken"] = savedReadData.GetProperty("sourceToken").GetString(), ["inputId"] = originalInput.Id };
            var readInput = await client.CallToolAsync("kicad_diagram_refinement_input_read", readInputArguments, cancellationToken: token);
            Assert.IsFalse(readInput.IsError == true);
            var readInputData = JsonSerializer.SerializeToElement(readInput).GetProperty("structuredContent");
            Assert.AreEqual(originalInput.Prompt, readInputData.GetProperty("input").GetProperty("prompt").GetString());
            Assert.AreEqual("Available", readInputData.GetProperty("assets")[0].GetProperty("status").GetString());
            Assert.AreEqual("Published", readInputData.GetProperty("publication").GetProperty("stage").GetString());
            var publicationArguments = new Dictionary<string, object?>(arguments)
            { ["expectedInstanceEpoch"] = native.Epoch, ["inputId"] = originalInput.Id };
            var publicationState = await client.CallToolAsync("kicad_diagram_refinement_publication", publicationArguments, cancellationToken: token);
            Assert.IsFalse(publicationState.IsError == true);
            Assert.AreEqual("CompletedPreviously", JsonSerializer.SerializeToElement(publicationState).GetProperty("structuredContent")
                .GetProperty("inspection").GetProperty("disposition").GetString());
            Assert.IsFalse(JsonSerializer.SerializeToElement(publicationState).GetProperty("structuredContent").TryGetProperty("upgradedFromSchemaVersion", out _),
                "Inspection never writes, so it reports no upgrade.");
            publicationArguments["resume"] = true;
            var resumedPublication = await client.CallToolAsync("kicad_diagram_refinement_publication", publicationArguments, cancellationToken: token);
            Assert.IsFalse(resumedPublication.IsError == true);
            Assert.AreEqual("CompletedPreviously", JsonSerializer.SerializeToElement(resumedPublication).GetProperty("structuredContent")
                .GetProperty("inspection").GetProperty("disposition").GetString());
            // The resumed publication reports the version 1 to 2 upgrade its retained preimage proves.
            Assert.AreEqual(1, JsonSerializer.SerializeToElement(resumedPublication).GetProperty("structuredContent").GetProperty("upgradedFromSchemaVersion").GetInt32());
            publicationArguments["inputId"] = Guid.NewGuid();
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_refinement_publication", publicationArguments, cancellationToken: token)).IsError == true);
            publicationArguments["inputId"] = originalInput.Id; publicationArguments["expectedInstanceEpoch"] = Guid.NewGuid().ToString("D");
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_refinement_publication", publicationArguments, cancellationToken: token)).IsError == true);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-refinement-input.json"), JsonSerializer.Serialize(readInput), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-refinement-publication.json"), JsonSerializer.Serialize(resumedPublication), token);
            var wholeHistoryArguments = new Dictionary<string, object?>(arguments)
            {
                ["context"] = new { blockId = graph.SelectedRoot.BlockId, stateId = graph.SelectedRoot.StateId, revisionId = graph.SelectedRoot.RevisionId },
                ["limit"] = 1, ["expectedSourceToken"] = savedReadData.GetProperty("sourceToken").GetString()
            };
            var wholeHistory = await client.CallToolAsync("kicad_diagram_history", wholeHistoryArguments, cancellationToken: token);
            Assert.IsFalse(wholeHistory.IsError == true);
            var firstWholeHistory = JsonSerializer.SerializeToElement(wholeHistory).GetProperty("structuredContent").GetProperty("history");
            Assert.AreEqual(1, firstWholeHistory.GetProperty("entries").GetArrayLength());
            Assert.AreEqual(graph.SelectedRoot.RevisionId.ToString("D"), firstWholeHistory.GetProperty("context").GetProperty("revisionId").GetString());
            var invalidTargetArguments = new Dictionary<string, object?>(arguments) { ["blockId"] = graph.SelectedRoot.BlockId.ToString("D") };
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_read", invalidTargetArguments, cancellationToken: token)).IsError == true);
            if (sessionWide) await VerifySchemaTwoDiagramTools(client, native, processId, display, project, instanceId, evidence, token);
            string openingBytes = await File.ReadAllTextAsync(source, token);
            var opened = await client.CallToolAsync("kicad_diagram_open", arguments, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-recursive-open.json"), JsonSerializer.Serialize(opened), token);
            Assert.IsFalse(opened.IsError == true);
            Task<P.RecursiveDiagramEditorState> Read() => native.InvokeAsync<P.ReadRecursiveDiagramEditor, P.RecursiveDiagramEditorState>(
                new() { DocumentId = graph.DocumentId.ToString("D") }, token);
            async Task<P.RecursiveDiagramEditorState> Wait(Func<P.RecursiveDiagramEditorState, bool> condition)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
                P.RecursiveDiagramEditorState current = new();
                try
                {
                    while (true)
                    {
                        current = await Read();
                        if (condition(current)) return current;
                        await Task.Delay(50, deadline.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-recursive-timeout.json"), SchematicJson.Formatter.Format(current), token);
                    await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-timeout.png"), token);
                    throw;
                }
            }
            void Key(string key, bool control = false, bool alt = false, string title = "Structural diagram") =>
                NativeKeyboard.SchematicShortcut(display, processId, key, title, control, false, altKey: alt);
            void Type(string value) { foreach (char character in value) Key(character.ToString()); }
            int saveRequests = 0;
            async Task<P.RecursiveDiagramEditorState> Save()
            {
                ulong previous = (await Read()).CompletedSaveCount;
                if (++saveRequests % 2 == 0)
                    NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromRight: 55, clickFromBottom: 52);
                else Key("s", control: true);
                var saved = await Wait(s => s.CompletedSaveCount > previous && !s.Busy);
                Assert.AreEqual("", saved.ErrorMessage); Assert.IsFalse(saved.Dirty); return saved;
            }
            async Task ImplementationMenu()
            {
                Key("i", control: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                int count = 0;
                do
                {
                    NativeKeyboard.SchematicShortcut(display, processId, "", "Structural diagram", false, false, observePopupCount: value => count = value);
                    if (count == 0) await Task.Delay(50, timeout.Token);
                } while (count == 0);
            }
            async Task Preview(bool last) { await ImplementationMenu(); Key("Home"); if (last) Key("Down"); Key("Return"); }
            async Task PreviewState(Guid id)
            {
                var saved = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
                Guid owner = saved.States.Single(s => s.Id == id).BlockId;
                var states = saved.States.Where(s => s.BlockId == owner && !s.Archived).ToArray();
                int index = Array.FindIndex(states, s => s.Id == id); Assert.IsTrue(index >= 0);
                await ImplementationMenu(); Key("Home"); for (int i = 0; i < index; ++i) Key("Down"); Key("Return");
            }
            async Task Window(string title, bool visible = true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                while (NativeKeyboard.HasWindow(display, processId, title) != visible) await Task.Delay(50, timeout.Token);
            }
            void Name(string value, string title)
            { Key("a", control: true, title: title); foreach (char character in value) Key(character.ToString(), title: title); Key("Return", title: title); }
            // Canvas clicks target diagram points through the editor's reported canvas geometry, so the canvas-edge
            // drawing palette and the fitted view never shift them onto another element.
            async Task ClickDiagram(double x, double y)
            {
                var at = await Read();
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true,
                    clickFromLeft: (int)Math.Round(at.CanvasWindowX + (x - at.CanvasOriginX) * at.CanvasScale),
                    clickFromTop: (int)Math.Round(at.CanvasWindowY + (y - at.CanvasOriginY) * at.CanvasScale));
            }
            async Task ClickBlankCanvas()
            {
                var at = await Read();
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true,
                    clickFromLeft: at.CanvasWindowX + (int)at.CanvasPixelWidth - 40, clickFromTop: at.CanvasWindowY + (int)at.CanvasPixelHeight - 40);
            }
            var initial = await Wait(s => s.Ready && !s.Busy && s.Rendered);
            Assert.AreEqual(graph.SelectedRoot.BlockId.ToString("D"), initial.DiagramPath.Single().BlockId);
            // Round A4: the root's partial definition an agent recorded is listed by facet, only where a facet has a value.
            CollectionAssert.AreEqual(new[] { "purpose", "type", "model", "package" }, initial.ShownFacets.ToArray());
            Assert.AreEqual(openingBytes, await File.ReadAllTextAsync(source, token));
            async Task ObserveViews(string label, bool dirty, BlockSelection? history = null)
            {
                var before = await Read();
                var psuView = fixture.Blocks["PSU"];
                object Selection(BlockSelection selected) => new { blockId = selected.BlockId, stateId = selected.StateId, revisionId = selected.RevisionId };
                var viewArguments = new Dictionary<string, object?>
                {
                    ["instanceId"] = instanceId, ["documentId"] = graph.DocumentId.ToString("D"),
                    ["expectedSourceToken"] = before.SourceToken, ["expectedViewRevision"] = before.ViewRevision,
                    ["views"] = new object[] {
                        new { viewId = "current", pixelWidth = 1024, pixelHeight = 768 },
                        new { viewId = "saved-psu", pixelWidth = 800, pixelHeight = 600, selection = Selection(history ?? psuView) },
                        new { viewId = "detail", pixelWidth = 640, pixelHeight = 480, viewport = new { x = 100.0, y = 80.0, width = 400.0, height = 300.0 } }
                    }
                };
                var observation = await client.CallToolAsync("kicad_diagram_observe", viewArguments, cancellationToken: token);
                if (observation.IsError == true)
                    await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-" + label + "-observation-error.json"), JsonSerializer.Serialize(observation), token);
                Assert.IsFalse(observation.IsError == true, "Native multi-view observation failed; its exact response is retained.");
                var payload = JsonSerializer.SerializeToElement(observation);
                var state = payload.GetProperty("structuredContent").GetProperty("observation");
                var imageReferences = payload.GetProperty("structuredContent").GetProperty("imageReferences");
                Assert.AreEqual(before.ViewRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), state.GetProperty("viewRevision").GetString());
                Assert.AreEqual(before.SourceToken, state.GetProperty("sourceToken").GetString());
                Assert.AreEqual(3, state.GetProperty("views").GetArrayLength());
                // The schema 2 per-level editor (contract rbg-v2 section 9.1) reports its stored format, writability, level draft,
                // tool and viewports, and each view reports the layout it actually rendered (section 9.2).
                var editorState = state.GetProperty("editor");
                Assert.AreEqual(2, editorState.GetProperty("storedSchemaVersion").GetInt32(), "The journey's saves stored schema 2.");
                Assert.IsTrue(editorState.GetProperty("sourceWritable").GetBoolean());
                Assert.AreEqual("select", editorState.GetProperty("canvasTool").GetString());
                Assert.IsTrue(editorState.GetProperty("levelDraft").TryGetProperty("scope", out _));
                Assert.IsTrue(editorState.GetProperty("levelViewports").GetArrayLength() >= 1);
                var renderedLayout = state.GetProperty("views")[0].GetProperty("resolvedLayout");
                Assert.AreEqual(state.GetProperty("views")[0].GetProperty("children").GetArrayLength(), renderedLayout.GetProperty("blocks").GetArrayLength());
                Assert.IsTrue(renderedLayout.GetProperty("blocks").EnumerateArray().All(b => b.GetProperty("source").GetString() == "RPS_FALLBACK"),
                    "This level has no stored layout, so every block uses the legacy grid.");
                Assert.AreEqual("RPS_FALLBACK", renderedLayout.GetProperty("frameSource").GetString());
                var images = payload.GetProperty("content").EnumerateArray().Where(c => c.GetProperty("type").GetString() == "image").ToArray();
                Assert.AreEqual(3, images.Length);
                for (int i = 0; i < images.Length; ++i)
                {
                    byte[] png = Convert.FromBase64String(images[i].GetProperty("data").GetString()!);
                    Assert.IsTrue(png.Length > 1000); Assert.AreEqual("image/png", images[i].GetProperty("mimeType").GetString());
                    Assert.AreEqual(2 + i * 2, imageReferences[i].GetProperty("contentIndex").GetInt32());
                    Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png)), imageReferences[i].GetProperty("sha256").GetString());
                    Assert.AreEqual(state.GetProperty("views")[i].GetProperty("viewId").GetString(), imageReferences[i].GetProperty("viewId").GetString());
                    Assert.IsTrue(state.GetProperty("views")[i].GetProperty("viewport").TryGetProperty("x", out _));
                    Assert.IsTrue(state.GetProperty("views")[i].GetProperty("viewport").TryGetProperty("y", out _));
                    await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-" + label + "-view-" + i + ".png"), png, token);
                    Assert.AreEqual("diagram-unit", state.GetProperty("views")[i].GetProperty("units").GetString());
                    Assert.AreEqual("x-right/y-down", state.GetProperty("views")[i].GetProperty("coordinateSystem").GetString());
                }
                var currentView = state.GetProperty("views")[0];
                Assert.AreEqual(dirty, currentView.GetProperty("containsUnsavedDraft").GetBoolean());
                Assert.AreEqual((history ?? psuView).RevisionId.ToString("D"), state.GetProperty("views")[1].GetProperty("diagram").GetProperty("selection").GetProperty("revisionId").GetString());
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-" + label + "-observation.json"), state.GetRawText(), token);
                Assert.AreEqual(WithoutLayoutStatistics(before), WithoutLayoutStatistics(await Read()),
                    "Offscreen views must not alter draft, selection, source, view revision or viewport.");
                viewArguments["expectedViewRevision"] = before.ViewRevision + 1;
                Assert.IsTrue((await client.CallToolAsync("kicad_diagram_observe", viewArguments, cancellationToken: token)).IsError == true);
                viewArguments["expectedViewRevision"] = before.ViewRevision; viewArguments["documentId"] = Guid.NewGuid().ToString("D");
                Assert.IsTrue((await client.CallToolAsync("kicad_diagram_observe", viewArguments, cancellationToken: token)).IsError == true);
                viewArguments["documentId"] = graph.DocumentId.ToString("D");
                viewArguments["views"] = new[] { new { viewId = "invalid", pixelWidth = 1, pixelHeight = 600 } };
                Assert.IsTrue((await client.CallToolAsync("kicad_diagram_observe", viewArguments, cancellationToken: token)).IsError == true);
                viewArguments["views"] = new[] { new { viewId = "invalid", pixelWidth = 640, pixelHeight = 480,
                    viewport = new { x = 0.0, y = 0.0, width = -1.0, height = 300.0 } } };
                Assert.IsTrue((await client.CallToolAsync("kicad_diagram_observe", viewArguments, cancellationToken: token)).IsError == true);
                viewArguments["views"] = new[] { new { viewId = "duplicate", pixelWidth = 640, pixelHeight = 480 }, new { viewId = "duplicate", pixelWidth = 640, pixelHeight = 480 } };
                Assert.IsTrue((await client.CallToolAsync("kicad_diagram_observe", viewArguments, cancellationToken: token)).IsError == true);
                using (var cancelledView = new CancellationTokenSource())
                {
                    cancelledView.Cancel();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => native.InvokeAsync<P.ObserveRecursiveDiagramEditor, P.RecursiveDiagramObservation>(
                        new() { DocumentId = graph.DocumentId.ToString("D"), ExpectedSourceToken = before.SourceToken, ExpectedViewRevision = before.ViewRevision,
                            Views = { new P.RecursiveDiagramViewRequest { ViewId = "cancelled", PixelWidth = 640, PixelHeight = 480 } } }, cancelledView.Token));
                }
                Assert.AreEqual(WithoutLayoutStatistics(before), WithoutLayoutStatistics(await Read()));
                viewArguments["views"] = new[] { new { viewId = "recovered", pixelWidth = 640, pixelHeight = 480 } };
                var recoveredObservation = await client.CallToolAsync("kicad_diagram_observe", viewArguments, cancellationToken: token);
                Assert.IsFalse(recoveredObservation.IsError == true, "A valid native observation must work after rejected and cancelled requests.");
                Assert.AreEqual(WithoutLayoutStatistics(before), WithoutLayoutStatistics(await Read()));
            }
            await ObserveViews("saved-root", false);
            Key("Escape"); Key("Right");
            await Wait(s => s.Draft.Baseline.BlockId == fixture.Blocks["PSU"].BlockId.ToString("D"));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-system.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromRight: 300, clickFromTop: 185);
            await Wait(s => s.DiagramPath.Count == 2 && s.DiagramPath[^1].BlockId == fixture.Blocks["PSU"].BlockId.ToString("D"));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-psu.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromLeft: 118, clickFromTop: 45);
            var afterUp = await Wait(s => s.DiagramPath.Count == 1 && s.FocusedControl == "RecursiveDiagramCanvas"
                && s.Draft.Baseline.BlockId == fixture.Blocks["PSU"].BlockId.ToString("D"));
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-after-toolbar-up.json"), SchematicJson.Formatter.Format(afterUp), token);
            Key("Right");
            try
            {
                var acknowledged = await Wait(s => s.NavigationInputRevision > afterUp.NavigationInputRevision);
                Assert.AreEqual(afterUp.NavigationInputRevision + 1, acknowledged.NavigationInputRevision, "One arrow must be handled exactly once.");
                Assert.AreEqual(fixture.Blocks["CPU"].BlockId.ToString("D"), acknowledged.Draft.Baseline.BlockId, "The next peer must be selected after the acknowledged arrow.");
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                interactionFailures.Add(error);
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-arrow-failure.json"), SchematicJson.Formatter.Format(await Read()), token);
                await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-arrow-failure.png"), token);
                // Explicit rendered recovery preserves the original failed assertion,
                // then permits independent editing/save/recovery observations.
                // The CPU is the second block of the root's legacy grid: (510, 110, 240, 145).
                await ClickDiagram(630, 182);
                await Wait(s => s.Draft.Baseline.BlockId == fixture.Blocks["CPU"].BlockId.ToString("D"));
            }
            Key("Return"); await Wait(s => s.DiagramPath.Count == 2 && s.DiagramPath[^1].BlockId == fixture.Blocks["CPU"].BlockId.ToString("D"));
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromLeft: 42, clickFromTop: 45);
            await Wait(s => s.DiagramPath.Count == 1 && s.FocusedControl == "RecursiveDiagramCanvas"); Key("Return");
            await Wait(s => s.DiagramPath.Count == 2 && s.DiagramPath[^1].BlockId == fixture.Blocks["CPU"].BlockId.ToString("D"));
            ulong beforeFit = (await Read()).ViewRevision;
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromLeft: 377, clickFromTop: 45);
            await Wait(s => s.ViewRevision > beforeFit && s.Rendered);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-cpu.png"), token);
            Key("Escape"); Key("l"); Key("l"); Key("l");
            await Wait(s => s.ConnectionDraft?.Baseline.ConnectionId == fixture.Links["CPU/Memory"].ConnectionId.ToString("D"));
            Key("3", control: true); Key("a", control: true); Type("Keep memory away from noisy power.");
            await Wait(s => s.Dirty && s.ConnectionDraft.Fields.Routing == "Keep memory away from noisy power.");
            var connectionSave = await Save();
            Assert.AreEqual("Keep memory away from noisy power.", connectionSave.ConnectionDraft.Fields.Routing);
            var connectionFile = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var connectionCpu = connectionFile.Inspect(connectionFile.SelectedRoot).Children[1];
            var memoryLink = connectionFile.Inspect(connectionCpu).LocalDiagram.Connections[2];
            Assert.AreEqual("Keep memory away from noisy power.", connectionFile.Connections(connectionCpu.BlockId).Requirements(memoryLink).Requirements.Routing);
            Assert.AreEqual("", connectionFile.Connections(connectionCpu.BlockId).Requirements(fixture.Links["CPU/Memory"]).Requirements.Routing);
            Key("3", control: true); Key("a", control: true); Type("Unsaved connection preference."); await Wait(s => s.Dirty);
            Key("d", alt: true); await Wait(s => !s.Busy && !s.Dirty && s.ConnectionDraft.Fields.Routing == "Keep memory away from noisy power.");
            Key("3", control: true); Key("a", control: true); Type("A later memory routing preference."); await Wait(s => s.Dirty); await Save();
            Key("3", control: true); Key("h", alt: true);
            using (var modal = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                modal.CancelAfter(TimeSpan.FromSeconds(20));
                while (!NativeKeyboard.HasWindow(display, processId, "Requirement history")) await Task.Delay(50, modal.Token);
            }
            Key("Down", title: "Requirement history");
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Requirement history", false, true,
                clickFromRight: 70, clickFromBottom: 30);
            await Wait(s => s.Dirty && s.ConnectionDraft.Fields.Routing == "Keep memory away from noisy power.");
            await Save();
            var restoredConnection = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var restoredCpu = restoredConnection.Inspect(restoredConnection.SelectedRoot).Children[1];
            var restoredLink = restoredConnection.Inspect(restoredCpu).LocalDiagram.Connections[2];
            var linkHistory = restoredConnection.Connections(restoredCpu.BlockId).RequirementHistories.Single(h => h.Scope.DesignStateId == restoredLink.StateId);
            Assert.AreEqual(DiagramRequirementField.Routing, linkHistory.Current.Restorations.Single().Field);
            Assert.AreEqual("A later memory routing preference.", linkHistory.Revisions[^2].Requirements.Routing);
            var fieldArguments = new Dictionary<string, object?>(arguments)
            {
                ["blockId"] = restoredCpu.BlockId.ToString("D"), ["stateId"] = restoredCpu.StateId.ToString("D"), ["revisionId"] = restoredCpu.RevisionId.ToString("D"),
                ["field"] = "Routing", ["connectionId"] = restoredLink.ConnectionId.ToString("D"), ["connectionStateId"] = restoredLink.StateId.ToString("D"),
                ["connectionRevisionId"] = restoredLink.RevisionId.ToString("D"), ["offset"] = 0, ["limit"] = 1
            };
            var fieldRead = await client.CallToolAsync("kicad_diagram_field_history", fieldArguments, cancellationToken: token);
            Assert.IsFalse(fieldRead.IsError == true);
            var fieldData = JsonSerializer.SerializeToElement(fieldRead).GetProperty("structuredContent").GetProperty("history");
            Assert.AreEqual(restoredLink.ConnectionId.ToString("D"), fieldData.GetProperty("ownerId").GetString());
            Assert.AreEqual("Keep memory away from noisy power.", fieldData.GetProperty("savedText").GetString());
            Assert.AreEqual(1, fieldData.GetProperty("entries").GetArrayLength()); Assert.IsTrue(fieldData.GetProperty("total").GetUInt32() > 1);
            fieldArguments["offset"] = 1;
            var olderField = await client.CallToolAsync("kicad_diagram_field_history", fieldArguments, cancellationToken: token);
            Assert.IsFalse(olderField.IsError == true);
            Assert.AreEqual("A later memory routing preference.", JsonSerializer.SerializeToElement(olderField).GetProperty("structuredContent").GetProperty("history")
                .GetProperty("entries")[0].GetProperty("text").GetString());
            Key("4", control: true); Type("Leave pin choices open until placement."); await Wait(s => s.Dirty); await Save();
            var annotatedConnection = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var annotatedCpu = annotatedConnection.Inspect(annotatedConnection.SelectedRoot).Children[1];
            var connectionComment = annotatedConnection.Inspect(annotatedCpu).LocalDiagram.Notes.Single();
            Assert.AreEqual("Leave pin choices open until placement.", connectionComment.Text);
            Assert.AreEqual(DiagramAnnotationTargetKind.Connection, connectionComment.Target.Kind);
            Assert.AreEqual(fixture.Links["CPU/Memory"].ConnectionId, connectionComment.Target.TargetId);
            Assert.AreEqual(restoredLink, annotatedConnection.Inspect(annotatedCpu).LocalDiagram.Connections[2]);
            Key("4", control: true); Key("a", control: true); Type("An unsaved note."); await Wait(s => s.Dirty);
            Key("d", alt: true); await Wait(s => !s.Busy && !s.Dirty);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-connection.png"), token);
            // Select blank canvas space to return the inspector to the current
            // diagram, rather than accidentally editing a similarly named block.
            await ClickBlankCanvas();
            await Wait(s => s.ConnectionDraft is null && s.Draft.Baseline.BlockId == fixture.Blocks["CPU"].BlockId.ToString("D"));
            Key("4", control: true); Type("Prefer the cooler enclosure side."); await Wait(s => s.Dirty); await Save();
            var annotatedBlock = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var latestCpu = annotatedBlock.Inspect(annotatedBlock.SelectedRoot).Children[1];
            Assert.AreEqual(2, annotatedBlock.Inspect(latestCpu).LocalDiagram.Notes.Length);
            Assert.IsTrue(annotatedBlock.Inspect(latestCpu).LocalDiagram.Notes.Any(n => n.Target.Kind == DiagramAnnotationTargetKind.Block
                && n.Target.TargetId == fixture.Blocks["CPU"].BlockId && n.Text == "Prefer the cooler enclosure side."));
            Assert.IsTrue(annotatedBlock.Inspect(latestCpu).LocalDiagram.Notes.Any(n => n.Id == connectionComment.Id && n.Text == connectionComment.Text));
            Key("Escape");
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromLeft: 455, clickFromTop: 45);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromLeft: 260, clickFromTop: 740);
            Key("4", control: true); Type("Keep this region accessible.");
            await Wait(s => s.Dirty && s.Draft.LocalDiagram.Annotations.Any(n => n.TargetKind == P.DiagramAnnotationTargetKind.DatCanvas && n.Text == "Keep this region accessible."));
            var placed = await Save();
            var canvasNote = placed.Draft.LocalDiagram.Annotations.Single(n => n.TargetKind == P.DiagramAnnotationTargetKind.DatCanvas);
            string originalX = canvasNote.Position.X;
            NativeKeyboard.SchematicShortcut(display, processId, "drag", "Structural diagram", false, true,
                clickFromLeft: 280, clickFromTop: 760, dragToLeft: 360, dragToTop: 820);
            var moved = await Wait(s => s.Dirty && s.Draft.LocalDiagram.Annotations.Single(n => n.Id == canvasNote.Id).Position.X != originalX);
            string movedX = moved.Draft.LocalDiagram.Annotations.Single(n => n.Id == canvasNote.Id).Position.X;
            Key("z", control: true); await Wait(s => !s.Dirty && s.Draft.LocalDiagram.Annotations.Single(n => n.Id == canvasNote.Id).Position.X == originalX);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromLeft: 285, clickFromTop: 45);
            await Wait(s => s.Dirty && s.Draft.LocalDiagram.Annotations.Single(n => n.Id == canvasNote.Id).Position.X == movedX);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromLeft: 210, clickFromTop: 45);
            await Wait(s => !s.Dirty && s.Draft.LocalDiagram.Annotations.Single(n => n.Id == canvasNote.Id).Position.X == originalX);
            Key("y", control: true); await Wait(s => s.Dirty && s.Draft.LocalDiagram.Annotations.Single(n => n.Id == canvasNote.Id).Position.X == movedX);
            await Save();
            var noteFile = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var noteCpu = noteFile.Inspect(noteFile.SelectedRoot).Children[1];
            var savedNote = noteFile.Inspect(noteCpu).LocalDiagram.Notes.Single(n => n.Id.ToString("D") == canvasNote.Id);
            Assert.AreEqual("Keep this region accessible.", savedNote.Text);
            Assert.AreEqual(decimal.Parse(movedX, System.Globalization.CultureInfo.InvariantCulture), savedNote.Position!.X);
            Key("5", control: true); Key("End"); await Wait(s => s.SelectedAnnotationId == "");
            Key("4", control: true); Type("Second independent block comment."); await Wait(s => s.Dirty); await Save();
            string multipleXml = await File.ReadAllTextAsync(source, token); var multiple = RecursiveBlockGraphXml.Read(multipleXml);
            var multipleCpu = multiple.Inspect(multiple.SelectedRoot).Children[1];
            Assert.AreEqual(4, multiple.Inspect(multipleCpu).LocalDiagram.Notes.Length);
            Assert.IsTrue(multiple.Inspect(multipleCpu).LocalDiagram.Notes.Any(n => n.Id == savedNote.Id && n.Text == savedNote.Text));
            // R4: the native editor's changed saves stored the file as schema 2, and the schema 1 editor keeps working on it.
            Assert.AreEqual(2, RecursiveBlockGraphXml.ReadVersioned(multipleXml).StoredSchemaVersion);
            Key("5", control: true); Key("Home"); Key("4", control: true); Key("a", control: true); Type("Unsaved first-comment edit.");
            await Wait(s => s.Dirty); Key("d", alt: true); await Wait(s => !s.Busy && !s.Dirty);
            Assert.AreEqual(multipleXml, await File.ReadAllTextAsync(source, token));
            // Import original sketch points through the same typed source model;
            // this verifies rendering/preservation, not a still-missing sketch tool.
            var sketchDraft = multiple.StartDraft(multipleCpu);
            var sketch = new DiagramAnnotation(Guid.NewGuid(), DiagramAnnotationRole.Comment, "Original sketch region",
                new(DiagramAnnotationTargetKind.Canvas, null), new(460m, 400m),
                [new([new(450m, 360m), new(610m, 360m), new(610m, 480m), new(450m, 480m), new(450m, 360m)])], RecursiveBlockFixture.Origin());
            sketchDraft = sketchDraft with { Diagram = sketchDraft.LocalDiagram with { Annotations = sketchDraft.LocalDiagram.Notes.Add(sketch) } };
            var sketched = multiple.SaveDraft(multiple.SelectedRoot, [multiple.SelectedRoot, multipleCpu], sketchDraft,
                Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
            await File.WriteAllTextAsync(source, RecursiveBlockGraphXml.Write(sketched), token);
            Key("1", control: true); Key("a", control: true); Type("Reload the saved sketch without losing requirements."); await Wait(s => s.Dirty);
            Key("d", alt: true); await Wait(s => !s.Busy && !s.Dirty && s.Draft.LocalDiagram.Annotations.Any(n => n.Id == sketch.Id.ToString("D")));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-canvas-note.png"), token);
            // The same level as an agent's context (ledger pa48933d0fe0a5c2f): the comments on its elements and in its free space, the
            // original sketch strokes included, each with the exact revisions they were saved in.
            var sketchedFile = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            BlockSelection[] sketchPath = [sketchedFile.SelectedRoot, sketchedFile.Inspect(sketchedFile.SelectedRoot).Children.Single(c => c.BlockId == fixture.Blocks["CPU"].BlockId)];
            var sketchContext = await AgentContextOverMcp(client, arguments, await FileToken(source, token), token, blockPath: sketchPath);
            AssertAgentContext(sketchContext, sketchedFile, sketchPath, null, current: true, "commented level");
            var sketchComments = sketchContext.GetProperty("context").GetProperty("comments").EnumerateArray().ToArray();
            Assert.IsTrue(sketchComments.Any(c => c.GetProperty("placement").GetString() == "Element" && c.GetProperty("comment").GetProperty("text").GetString() == "Leave pin choices open until placement."
                && c.GetProperty("comment").GetProperty("target").GetProperty("kind").GetString() == "Connection"), "A comment on a connection is an element comment.");
            Assert.IsTrue(sketchComments.Any(c => c.GetProperty("placement").GetString() == "Element" && c.GetProperty("comment").GetProperty("text").GetString() == "Prefer the cooler enclosure side."
                && c.GetProperty("comment").GetProperty("target").GetProperty("kind").GetString() == "Block"), "A comment on a block is an element comment.");
            Assert.IsTrue(sketchComments.Any(c => c.GetProperty("placement").GetString() == "FreeSpace" && c.GetProperty("comment").GetProperty("text").GetString() == "Keep this region accessible."),
                "A comment placed on the canvas is a free-space comment.");
            var sketchStrokes = sketchComments.Single(c => c.GetProperty("comment").GetProperty("id").GetGuid() == sketch.Id);
            Assert.AreEqual("FreeSpace", sketchStrokes.GetProperty("placement").GetString());
            CollectionAssert.AreEqual(sketch.Strokes[0].Points.Select(p => (p.X, p.Y)).ToArray(), sketchStrokes.GetProperty("comment").GetProperty("strokes")[0].GetProperty("points")
                .EnumerateArray().Select(p => (p.GetProperty("x").GetDecimal(), p.GetProperty("y").GetDecimal())).ToArray(), "The original sketch points reach the agent exactly.");
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-agent-context-comments.json"), sketchContext.GetRawText(), token);
            string sketchContextSha = sketchContext.GetProperty("contextSha256").GetString()!;
            string original = graph.Requirements(fixture.Blocks["CPU"]).Requirements.General;
            Key("1", control: true); Key("a", control: true); Type("Cool near the enclosure edge.");
            await Wait(s => s.Dirty && s.Draft.Fields.General == "Cool near the enclosure edge.");
            var savedFirst = await Save();
            var file = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var cpu = file.Inspect(file.SelectedRoot).Children[1];
            Assert.AreEqual("Cool near the enclosure edge.", file.Requirements(cpu).Requirements.General);
            Assert.AreEqual(original, file.Requirements(fixture.Blocks["CPU"]).Requirements.General);
            Key("1", control: true); Key("a", control: true); Type("Unsaved alternative.");
            await Wait(s => s.Dirty); Key("d", alt: true);
            var declined = await Wait(s => !s.Busy && !s.Dirty && s.Draft.Fields.General == "Cool near the enclosure edge.");
            Assert.AreEqual(savedFirst.SourceToken, declined.SourceToken);
            Key("1", control: true); Key("a", control: true); Type("A second saved requirement."); await Wait(s => s.Dirty); await Save();
            Key("1", control: true); Key("h", alt: true);
            using (var modal = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                modal.CancelAfter(TimeSpan.FromSeconds(20));
                while (!NativeKeyboard.HasWindow(display, processId, "Requirement history")) await Task.Delay(50, modal.Token);
            }
            Key("Down", title: "Requirement history");
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Requirement history", false, true,
                clickFromRight: 70, clickFromBottom: 30);
            await Wait(s => s.Dirty && s.Draft.Fields.General == "Cool near the enclosure edge.");
            var restored = await Save();
            Assert.AreNotEqual(savedFirst.SourceToken, restored.SourceToken);
            var oldRoot = graph.SelectedRoot;
            file = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            CollectionAssert.AreEqual(graph.Walk(oldRoot).ToArray(), file.Walk(oldRoot).ToArray());
            Key("1", control: true); Key("a", control: true); Type("Retain this draft on conflict."); await Wait(s => s.Dirty);
            var remoteCpu = file.Inspect(file.SelectedRoot).Children[1];
            var remoteDraft = file.StartDraft(remoteCpu);
            remoteDraft = remoteDraft with { Requirements = remoteDraft.Requirements.Edit(DiagramRequirementField.General, "A competing saved requirement.") };
            var competing = file.SaveDraft(file.SelectedRoot, [file.SelectedRoot, remoteCpu], remoteDraft, Guid.NewGuid(), Guid.NewGuid(),
                [Guid.NewGuid()], RecursiveBlockFixture.Origin("Another agent")).Graph;
            await File.WriteAllTextAsync(source, RecursiveBlockGraphXml.Write(competing), token);
            ulong beforeConflict = (await Read()).CompletedSaveCount; Key("s", control: true);
            using (var modal = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                modal.CancelAfter(TimeSpan.FromSeconds(20));
                while (!NativeKeyboard.HasWindow(display, processId, "Resolve changes before saving")) await Task.Delay(50, modal.Token);
            }
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-conflict.png"), token);
            Key("Escape", title: "Resolve changes before saving");
            var conflict = await Wait(s => s.CompletedSaveCount > beforeConflict && !s.Busy && s.ErrorCode == "requirement_conflict");
            Assert.IsTrue(conflict.Dirty); Assert.AreEqual("requirement_conflict", conflict.ErrorCode);
            Assert.AreEqual("Retain this draft on conflict.", conflict.Draft.Fields.General);
            Assert.AreEqual(RecursiveBlockGraphXml.Write(competing), await File.ReadAllTextAsync(source, token));
            Key("s", control: true);
            using (var modal = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                modal.CancelAfter(TimeSpan.FromSeconds(20));
                while (!NativeKeyboard.HasWindow(display, processId, "Resolve changes before saving")) await Task.Delay(50, modal.Token);
            }
            Key("u", alt: true, title: "Resolve changes before saving");
            // A resolution is tied to the exact comparison bytes. Even a further
            // saved-file change while the dialog is open must not reuse its token.
            await File.AppendAllTextAsync(source, "\n", token);
            Key("v", alt: true, title: "Resolve changes before saving");
            var changedAgain = await Wait(s => !s.Busy && s.Dirty && s.ErrorCode == "stale_requirement_resolution");
            Assert.AreEqual("Retain this draft on conflict.", changedAgain.Draft.Fields.General);
            Assert.AreEqual(RecursiveBlockGraphXml.Write(competing) + "\n", await File.ReadAllTextAsync(source, token));
            Key("s", control: true);
            using (var modal = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                modal.CancelAfter(TimeSpan.FromSeconds(20));
                while (!NativeKeyboard.HasWindow(display, processId, "Resolve changes before saving")) await Task.Delay(50, modal.Token);
            }
            Key("u", alt: true, title: "Resolve changes before saving");
            Key("v", alt: true, title: "Resolve changes before saving");
            var resolved = await Wait(s => !s.Busy && !s.Dirty && s.Draft.Fields.General == "Retain this draft on conflict.");
            Assert.AreEqual("", resolved.ErrorCode);
            var reconciled = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var chosenCpu = reconciled.Inspect(reconciled.SelectedRoot).Children[1];
            Assert.AreEqual("Retain this draft on conflict.", reconciled.Requirements(chosenCpu).Requirements.General);
            Assert.AreEqual("A competing saved requirement.", reconciled.Requirements(competing.Inspect(competing.SelectedRoot).Children[1]).Requirements.General);
            // Independent field edits should compose without asking the user to
            // choose a winner or losing the remote schematic requirement.
            Key("1", control: true); Key("a", control: true); Type("My independent general requirement."); await Wait(s => s.Dirty);
            var independentRemote = reconciled.StartDraft(chosenCpu);
            independentRemote = independentRemote with { Requirements = independentRemote.Requirements.Edit(DiagramRequirementField.Schematic, "Show the telemetry path clearly.") };
            var independentGraph = reconciled.SaveDraft(reconciled.SelectedRoot, [reconciled.SelectedRoot, chosenCpu], independentRemote,
                Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin("Another agent")).Graph;
            await File.WriteAllTextAsync(source, RecursiveBlockGraphXml.Write(independentGraph), token);
            var composed = await Save();
            Assert.AreEqual("My independent general requirement.", composed.Draft.Fields.General);
            Assert.AreEqual("Show the telemetry path clearly.", composed.Draft.Fields.Schematic);
            Assert.IsFalse(NativeKeyboard.HasWindow(display, processId, "Resolve changes before saving"));
            string beforeSwitch = await File.ReadAllTextAsync(source, token);
            var beforeSwitchGraph = RecursiveBlockGraphXml.Read(beforeSwitch);
            var savedCpu = beforeSwitchGraph.Inspect(beforeSwitchGraph.SelectedRoot).Children[1];
            var alternative = beforeSwitchGraph.States.Single(s => s.BlockId == savedCpu.BlockId && s.Id != savedCpu.StateId);
            await Preview(last: true);
            var preview = await Wait(s => s.ImplementationPreview && s.Draft.Baseline.StateId == alternative.Id.ToString("D"));
            Assert.IsTrue(preview.Dirty); Assert.AreEqual(beforeSwitch, await File.ReadAllTextAsync(source, token));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-implementation-preview.png"), token);
            Key("d", alt: true);
            await Wait(s => !s.Busy && !s.Dirty && !s.ImplementationPreview && s.Draft.Baseline.StateId == savedCpu.StateId.ToString("D"));
            Assert.AreEqual(beforeSwitch, await File.ReadAllTextAsync(source, token));
            await Preview(last: true); await Wait(s => s.ImplementationPreview);
            Key("1", control: true); Key("a", control: true); Type("Alternative implementation requirements."); await Wait(s => s.Dirty);
            var chosen = await Save(); Assert.IsFalse(chosen.ImplementationPreview);
            var chosenGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var choice = chosenGraph.Inspect(chosenGraph.SelectedRoot).Children[1];
            Assert.AreEqual(alternative.Id, choice.StateId);
            Assert.AreEqual("Alternative implementation requirements.", chosenGraph.Requirements(choice).Requirements.General);
            Assert.AreEqual(savedCpu, chosenGraph.Inspect(beforeSwitchGraph.SelectedRoot).Children[1]);
            Assert.AreEqual(beforeSwitchGraph.Requirements(savedCpu).Requirements, chosenGraph.Requirements(savedCpu).Requirements);
            await Preview(last: false); await Wait(s => s.ImplementationPreview); await Save();
            var returned = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual(savedCpu, returned.Inspect(returned.SelectedRoot).Children[1]);
            Assert.AreEqual("Alternative implementation requirements.", returned.Requirements(choice).Requirements.General);
            string selectionBytes = await File.ReadAllTextAsync(source, token);
            await Preview(last: true); await Wait(s => s.ImplementationPreview);
            await File.AppendAllTextAsync(source, "\n", token);
            ulong selectionAttempts = (await Read()).CompletedSaveCount;
            Key("s", control: true);
            var staleSelection = await Wait(s => !s.Busy && s.CompletedSaveCount > selectionAttempts && s.ErrorCode == "recursive_block_file_changed");
            Assert.IsTrue(staleSelection.Dirty); Assert.IsTrue(staleSelection.ImplementationPreview);
            Assert.AreEqual(alternative.Id.ToString("D"), staleSelection.Draft.Baseline.StateId);
            Assert.AreEqual(selectionBytes + "\n", await File.ReadAllTextAsync(source, token));
            Key("d", alt: true); await Wait(s => !s.Busy && !s.ImplementationPreview && !s.Dirty);
            Assert.AreEqual(savedCpu, RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token)).Inspect(returned.SelectedRoot).Children[1]);
            var beforeResize = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, "", "Structural diagram", false, false, resizeWidth: 1100, resizeHeight: 760,
                observeGeometry: bounds => { Assert.AreEqual(1100, bounds.Width); Assert.AreEqual(760, bounds.Height); });
            await Wait(s => s.Rendered && s.ViewRevision > beforeResize.ViewRevision);
            ulong compactView = (await Read()).ViewRevision;
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromLeft: 377, clickFromTop: 45);
            await Wait(s => s.Rendered && s.ViewRevision > compactView);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-compact.png"), token);
            Key("3", control: true); Key("a", control: true); Type("A compact-window routing edit."); await Wait(s => s.Dirty);
            await Save();
            Key("4", control: true);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-compact-comments.png"), token);
            ulong narrowView = (await Read()).ViewRevision;
            NativeKeyboard.SchematicShortcut(display, processId, "", "Structural diagram", false, false, resizeWidth: 1536, resizeHeight: 1024,
                observeGeometry: bounds => { Assert.AreEqual(1536, bounds.Width); Assert.AreEqual(1024, bounds.Height); });
            await Wait(s => s.Rendered && !s.Busy && s.ViewRevision > narrowView);
            string expandedCapture = Path.Combine(evidence, instanceId + "-recursive-expanded.png");
            await CaptureRecursive(display, expandedCapture, token);
            await VerifyExpandedCanvasContent(expandedCapture, token);
            string beforeManagement = await File.ReadAllTextAsync(source, token);
            var beforeManagementGraph = RecursiveBlockGraphXml.Read(beforeManagement);
            var sourceCpu = beforeManagementGraph.Inspect(beforeManagementGraph.SelectedRoot).Children[1];
            await ImplementationMenu(); Key("d"); await Window("Duplicate implementation");
            Key("Escape", title: "Duplicate implementation"); await Window("Duplicate implementation", false);
            Assert.AreEqual(beforeManagement, await File.ReadAllTextAsync(source, token));
            await ImplementationMenu(); Key("d"); await Window("Duplicate implementation");
            Name("Serviceable copy", "Duplicate implementation");
            var duplicated = await Wait(s => !s.Busy && s.ImplementationPreview && s.Draft.Baseline.StateId != sourceCpu.StateId.ToString("D"));
            Guid copyState = Guid.Parse(duplicated.Draft.Baseline.StateId);
            string copiedGraphXml = await File.ReadAllTextAsync(source, token); var copiedGraph = RecursiveBlockGraphXml.Read(copiedGraphXml);
            Assert.AreEqual(beforeManagementGraph.SelectedRoot, copiedGraph.SelectedRoot);
            Assert.AreEqual(sourceCpu, copiedGraph.States.Single(s => s.Id == copyState).ForkedFrom);
            Assert.AreEqual(beforeManagementGraph.Requirements(sourceCpu).Requirements,
                copiedGraph.Requirements(new(sourceCpu.BlockId, copyState, Guid.Parse(duplicated.Draft.Baseline.RevisionId))).Requirements);
            // The duplicate continues the source's field history from the exact source revision (ledger p390b40bed99e0ab2).
            Assert.AreEqual(beforeManagementGraph.Requirements(sourceCpu).RevisionId,
                copiedGraph.RequirementHistories.Single(h => h.Scope.DesignStateId == copyState).DerivedFrom);
            await ImplementationMenu(); Key("r"); await Window("Rename implementation");
            Name("Initial approach", "Rename implementation"); await Window("Invalid implementation name");
            Assert.AreEqual(copiedGraphXml, await File.ReadAllTextAsync(source, token));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-implementation-name-error.png"), token);
            Key("Return", title: "Invalid implementation name"); await Window("Invalid implementation name", false); await Window("Rename implementation");
            Key("a", control: true, title: "Rename implementation"); Key("BackSpace", title: "Rename implementation"); Key("Return", title: "Rename implementation");
            await Window("Invalid implementation name");
            Assert.AreEqual(copiedGraphXml, await File.ReadAllTextAsync(source, token));
            Key("Return", title: "Invalid implementation name"); await Window("Invalid implementation name", false); await Window("Rename implementation");
            Name("Thermal copy", "Rename implementation");
            await Wait(s => !s.Busy && s.ImplementationPreview && s.SourceToken != duplicated.SourceToken);
            var renamedGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual("Thermal copy", renamedGraph.States.Single(s => s.Id == copyState).Name);
            Assert.AreEqual("Serviceable copy", renamedGraph.ImplementationChanges.Single(c => c.StateId == copyState).BeforeName);
            await Save();
            var selectedCopyGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual(copyState, selectedCopyGraph.Inspect(selectedCopyGraph.SelectedRoot).Children[1].StateId);
            string selectedCopyBytes = await File.ReadAllTextAsync(source, token);
            await ImplementationMenu(); Key("v");
            Assert.IsFalse(NativeKeyboard.HasWindow(display, processId, "Remove implementation")); Key("Escape");
            Assert.AreEqual(selectedCopyBytes, await File.ReadAllTextAsync(source, token));
            await PreviewState(sourceCpu.StateId); await Wait(s => s.ImplementationPreview); await Save();
            await PreviewState(copyState); await Wait(s => s.ImplementationPreview);
            renamedGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            await ImplementationMenu(); Key("v"); await Window("Remove implementation");
            Key("Escape", title: "Remove implementation"); await Window("Remove implementation", false);
            Assert.IsFalse(RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token)).States.Single(s => s.Id == copyState).Archived);
            await ImplementationMenu(); Key("v"); await Window("Remove implementation");
            Key("r", alt: true, title: "Remove implementation");
            await Wait(s => !s.Busy && !s.ImplementationPreview && !s.Dirty);
            var removedGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.IsTrue(removedGraph.States.Single(s => s.Id == copyState).Archived);
            Assert.AreEqual(renamedGraph.Revisions.Length, removedGraph.Revisions.Length);
            await ImplementationMenu(); Key("m"); Key("Right"); Key("Home"); Key("Return");
            await Wait(s => !s.Busy && s.ImplementationPreview && s.Draft.Baseline.StateId == copyState.ToString("D"));
            Assert.IsFalse(RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token)).States.Single(s => s.Id == copyState).Archived);
            Key("d", alt: true); await Wait(s => !s.Busy && !s.Dirty && !s.ImplementationPreview);
            await ImplementationMenu(); Key("n"); await Window("New implementation");
            Name("New topology", "New implementation");
            var created = await Wait(s => !s.Busy && s.ImplementationPreview && s.Draft.Children.Count == 0);
            var newGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var newSelection = new BlockSelection(Guid.Parse(created.Draft.Baseline.BlockId), Guid.Parse(created.Draft.Baseline.StateId), Guid.Parse(created.Draft.Baseline.RevisionId));
            Assert.AreEqual(sourceCpu, newGraph.States.Single(s => s.Id == newSelection.StateId).ForkedFrom);
            Assert.AreEqual(beforeManagementGraph.Requirements(sourceCpu).Requirements, newGraph.Requirements(newSelection).Requirements);
            Assert.HasCount(2, newGraph.Inspect(newSelection).LocalDiagram.Interfaces);
            Assert.IsEmpty(newGraph.Inspect(newSelection).Children);
            await ObserveViews("empty-implementation", true, newSelection);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-new-implementation.png"), token);
            Key("d", alt: true); await Wait(s => !s.Busy && !s.Dirty && !s.ImplementationPreview);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromLeft: 118, clickFromTop: 45);
            await Wait(s => s.DiagramPath.Count == 1 && !s.Busy);
            await ImplementationMenu(); Key("d"); await Window("Duplicate implementation"); Name("Whole-system exploration", "Duplicate implementation");
            var rootPreview = await Wait(s => !s.Busy && s.ImplementationPreview && s.Draft.Baseline.BlockId == newGraph.SelectedRoot.BlockId.ToString("D"));
            var rootCopyGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual(newGraph.SelectedRoot, rootCopyGraph.SelectedRoot);
            Assert.AreEqual(newGraph.SelectedRoot, rootCopyGraph.States.Single(s => s.Id.ToString("D") == rootPreview.Draft.Baseline.StateId).ForkedFrom);
            Assert.HasCount(2, rootPreview.Draft.Children);
            Key("d", alt: true); await Wait(s => !s.Busy && !s.Dirty && !s.ImplementationPreview);
            var managementRead = await client.CallToolAsync("kicad_diagram_read", arguments, cancellationToken: token);
            Assert.IsFalse(managementRead.IsError == true);
            var managementData = JsonSerializer.SerializeToElement(managementRead).GetProperty("structuredContent");
            Guid agentStateId = Guid.NewGuid();
            var agentArguments = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = managementData.GetProperty("instanceEpoch").GetString(),
                ["expectedSourceToken"] = managementData.GetProperty("sourceToken").GetString(),
                ["expectedRoot"] = new { blockId = newGraph.SelectedRoot.BlockId, stateId = newGraph.SelectedRoot.StateId, revisionId = newGraph.SelectedRoot.RevisionId },
                ["source"] = new { blockId = sourceCpu.BlockId, stateId = sourceCpu.StateId, revisionId = sourceCpu.RevisionId },
                ["action"] = "duplicate", ["operationId"] = agentStateId, ["actor"] = "Compatible agent fixture", ["name"] = "Agent alternative"
            };
            var agentCreated = await client.CallToolAsync("kicad_diagram_manage_implementation", agentArguments, cancellationToken: token);
            Assert.IsFalse(agentCreated.IsError == true);
            var agentCreatedData = JsonSerializer.SerializeToElement(agentCreated).GetProperty("structuredContent");
            Assert.AreEqual(agentStateId.ToString("D"), agentCreatedData.GetProperty("implementation").GetProperty("id").GetString());
            var agentGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual(newGraph.SelectedRoot, agentGraph.SelectedRoot);
            Assert.AreEqual(sourceCpu, agentGraph.States.Single(s => s.Id == agentStateId).ForkedFrom);
            // Switching to another implementation keeps the field history: the agent's duplicate lists every General text of
            // the CPU implementation it was made from, each with its revision, version and provenance (ledger p390b40bed99e0ab2).
            var agentAlternative = new BlockSelection(sourceCpu.BlockId, agentStateId, agentGraph.States.Single(s => s.Id == agentStateId).HeadRevisionId);
            var sourceGeneral = AllFieldEntries((offset, limit) => DiagramFieldHistoryQuery.Block(beforeManagementGraph, sourceCpu, DiagramRequirementField.General, offset, limit));
            AssertFieldHistory(await FieldHistoryOverMcp(client, arguments, agentAlternative, "General", null, token), sourceGeneral,
                "agent duplicate of the CPU implementation", Contexts(agentGraph));
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_manage_implementation", agentArguments, cancellationToken: token)).IsError == true);
            var agentState = agentGraph.States.Single(s => s.Id == agentStateId);
            agentArguments["source"] = new { blockId = agentState.BlockId, stateId = agentState.Id, revisionId = agentState.HeadRevisionId };
            agentArguments["expectedSourceToken"] = agentCreatedData.GetProperty("sourceToken").GetString();
            agentArguments["action"] = "remove"; agentArguments["operationId"] = Guid.NewGuid(); agentArguments.Remove("name");
            var agentRemoved = await client.CallToolAsync("kicad_diagram_manage_implementation", agentArguments, cancellationToken: token);
            Assert.IsFalse(agentRemoved.IsError == true);
            Assert.IsTrue(RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token)).States.Single(s => s.Id == agentStateId).Archived);
            agentArguments["expectedSourceToken"] = JsonSerializer.SerializeToElement(agentRemoved).GetProperty("structuredContent").GetProperty("sourceToken").GetString();
            agentArguments["action"] = "restore"; agentArguments["operationId"] = Guid.NewGuid();
            var agentRestored = await client.CallToolAsync("kicad_diagram_manage_implementation", agentArguments, cancellationToken: token);
            Assert.IsFalse(agentRestored.IsError == true);
            Assert.IsFalse(RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token)).States.Single(s => s.Id == agentStateId).Archived);
            agentArguments["expectedSourceToken"] = JsonSerializer.SerializeToElement(agentRestored).GetProperty("structuredContent").GetProperty("sourceToken").GetString();
            agentArguments["action"] = "rename"; agentArguments["operationId"] = Guid.NewGuid(); agentArguments["name"] = "Agent-renamed alternative";
            var agentRenamed = await client.CallToolAsync("kicad_diagram_manage_implementation", agentArguments, cancellationToken: token);
            Assert.IsFalse(agentRenamed.IsError == true);
            Assert.AreEqual("Agent-renamed alternative", RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token)).States.Single(s => s.Id == agentStateId).Name);
            agentArguments["expectedSourceToken"] = JsonSerializer.SerializeToElement(agentRenamed).GetProperty("structuredContent").GetProperty("sourceToken").GetString();
            Guid agentNewId = Guid.NewGuid(); agentArguments["action"] = "new"; agentArguments["operationId"] = agentNewId; agentArguments["name"] = "Agent new topology";
            var agentNew = await client.CallToolAsync("kicad_diagram_manage_implementation", agentArguments, cancellationToken: token);
            Assert.IsFalse(agentNew.IsError == true);
            var agentNewGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var agentNewState = agentNewGraph.States.Single(s => s.Id == agentNewId);
            Assert.IsEmpty(agentNewGraph.Inspect(new(agentNewState.BlockId, agentNewState.Id, agentNewState.HeadRevisionId)).Children);
            Assert.AreEqual(newGraph.SelectedRoot, agentNewGraph.SelectedRoot);
            agentArguments["expectedInstanceEpoch"] = "wrong-epoch";
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_manage_implementation", agentArguments, cancellationToken: token)).IsError == true);
            Key("r", control: true); await Wait(s => !s.Busy && !s.Dirty && s.SourceToken == JsonSerializer.SerializeToElement(agentNew).GetProperty("structuredContent").GetProperty("sourceToken").GetString());
            Key("w", control: true);
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(token); closing.CancelAfter(TimeSpan.FromSeconds(15));
            while (NativeKeyboard.HasWindow(display, processId, "Structural diagram")) await Task.Delay(50, closing.Token);
            var reopened = await client.CallToolAsync("kicad_diagram_open", arguments, cancellationToken: token);
            Assert.IsFalse(reopened.IsError == true);
            await Wait(s => s.Ready && !s.Busy && s.Draft.Baseline.BlockId == file.SelectedRoot.BlockId.ToString("D"));
            // A real native dialog must reach history older than the first bounded
            // page, while retaining an unrelated draft and exact saved context.
            var longGraph = RecursiveBlockFixture.RefineRoot(RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token)),
                DiagramRequirementField.General, 205);
            await File.WriteAllTextAsync(source, RecursiveBlockGraphXml.Write(longGraph), token);
            Key("r", control: true);
            await Wait(s => !s.Busy && !s.Dirty && s.Draft.Baseline.RevisionId == longGraph.SelectedRoot.RevisionId.ToString("D"));
            Key("3", control: true); Key("a", control: true); Type("Independent routing draft.");
            await Wait(s => s.Dirty && s.Draft.Fields.Routing == "Independent routing draft.");
            Key("1", control: true); Key("h", alt: true);
            var firstHistory = await Wait(s => s.FieldHistory is { LoadedCount: 200, Loading: false });
            Key("Down", title: "Requirement history");
            var inspectedHistory = await Wait(s => s.FieldHistory is not null
                && s.FieldHistory.InspectedRevisionId != firstHistory.FieldHistory.InspectedRevisionId);
            var externalHistory = RecursiveBlockFixture.RefineRoot(longGraph, DiagramRequirementField.Schematic, 1);
            string externalHistoryXml = RecursiveBlockGraphXml.Write(externalHistory);
            await File.WriteAllTextAsync(source, externalHistoryXml, token);
            Key("o", alt: true, title: "Requirement history");
            var pageFailure = await Wait(s => !s.Busy && s.ErrorCode == "recursive_block_file_changed" && s.FieldHistory is { Loading: false });
            Assert.AreEqual(200U, pageFailure.FieldHistory.LoadedCount);
            Assert.AreEqual(inspectedHistory.FieldHistory.InspectedRevisionId, pageFailure.FieldHistory.InspectedRevisionId);
            Assert.AreEqual(longGraph.SelectedRoot.RevisionId.ToString("D"), pageFailure.FieldHistory.ContextRevisionId);
            Assert.AreEqual("Independent routing draft.", pageFailure.Draft.Fields.Routing);
            Assert.IsFalse(string.IsNullOrEmpty(pageFailure.FieldHistory.ErrorMessage));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-history-page-stale.png"), token);
            Key("Escape", title: "Requirement history"); await Wait(s => s.FieldHistory is null && !s.Busy);
            Assert.AreEqual(externalHistoryXml, await File.ReadAllTextAsync(source, token));
            Key("d", alt: true);
            await Wait(s => !s.Busy && !s.Dirty && s.Draft.Baseline.RevisionId == externalHistory.SelectedRoot.RevisionId.ToString("D"));
            Key("1", control: true); Key("h", alt: true); await Wait(s => s.FieldHistory is { LoadedCount: 200, Loading: false });
            Key("o", alt: true, title: "Requirement history"); Key("Escape", title: "Requirement history");
            await Wait(s => !s.Busy && s.FieldHistory is null);
            Assert.IsFalse(NativeKeyboard.HasWindow(display, processId, "Requirement history"), "A cancelled page read must not reopen history.");
            Assert.AreEqual(externalHistoryXml, await File.ReadAllTextAsync(source, token));
            Key("3", control: true); Key("a", control: true); Type("Independent routing draft."); await Wait(s => s.Dirty);
            Key("1", control: true); Key("h", alt: true);
            var beforeOlder = await Wait(s => s.FieldHistory is { LoadedCount: 200, Loading: false });
            Key("o", alt: true, title: "Requirement history");
            var allHistory = await Wait(s => s.FieldHistory is { Loading: false } h && h.LoadedCount == h.TotalCount);
            Assert.AreEqual(beforeOlder.FieldHistory.InspectedRevisionId, allHistory.FieldHistory.InspectedRevisionId);
            var lastHistory = DiagramFieldHistoryQuery.Block(externalHistory, externalHistory.SelectedRoot,
                DiagramRequirementField.General, 200, 200).Entries[^1];
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Requirement history", false, true,
                clickFromLeft: 70, clickFromTop: 150);
            Key("End", title: "Requirement history");
            await Wait(s => s.FieldHistory?.InspectedRevisionId == lastHistory.RequirementRevisionId.ToString("D"));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-history-oldest.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Requirement history", false, true,
                clickFromRight: 70, clickFromBottom: 30);
            var oldestDraft = await Wait(s => s.FieldHistory is null && s.Dirty && s.Draft.Fields.General == lastHistory.Text);
            Assert.AreEqual("Independent routing draft.", oldestDraft.Draft.Fields.Routing);
            Assert.AreEqual(externalHistoryXml, await File.ReadAllTextAsync(source, token));
            await Save();
            var historySaved = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual(lastHistory.Text, historySaved.Requirements(historySaved.SelectedRoot).Requirements.General);
            Assert.AreEqual("Independent routing draft.", historySaved.Requirements(historySaved.SelectedRoot).Requirements.Routing);
            Assert.AreEqual(externalHistory.Requirements(externalHistory.SelectedRoot).Requirements.Schematic,
                historySaved.Requirements(historySaved.SelectedRoot).Requirements.Schematic);
            var restoredHistory = historySaved.RequirementHistories.Single(h => h.Scope.DesignStateId == historySaved.SelectedRoot.StateId);
            Assert.AreEqual(lastHistory.RequirementRevisionId, restoredHistory.Current.Restorations.Single().SourceRevisionId);
            string beforeWholeRestore = await File.ReadAllTextAsync(source, token);
            var unchangedWindow = await Read();
            var comparisonArguments = new Dictionary<string, object?>(arguments)
            {
                ["context"] = new { blockId = historySaved.SelectedRoot.BlockId, stateId = historySaved.SelectedRoot.StateId, revisionId = historySaved.SelectedRoot.RevisionId },
                ["inspected"] = new { blockId = graph.SelectedRoot.BlockId, stateId = graph.SelectedRoot.StateId, revisionId = graph.SelectedRoot.RevisionId },
                ["expectedSourceToken"] = unchangedWindow.SourceToken
            };
            var wholeComparison = await client.CallToolAsync("kicad_diagram_history_compare", comparisonArguments, cancellationToken: token);
            Assert.IsFalse(wholeComparison.IsError == true);
            Assert.IsTrue(JsonSerializer.SerializeToElement(wholeComparison).GetProperty("structuredContent").GetProperty("comparison").GetProperty("changes").GetArrayLength() > 0);
            comparisonArguments["source"] = comparisonArguments["inspected"]; comparisonArguments.Remove("inspected");
            var preparedWhole = await client.CallToolAsync("kicad_diagram_prepare_restoration", comparisonArguments, cancellationToken: token);
            Assert.IsFalse(preparedWhole.IsError == true);
            var preparedWholeDraft = JsonSerializer.SerializeToElement(preparedWhole).GetProperty("structuredContent").GetProperty("draft");
            Assert.AreEqual(graph.SelectedRoot.RevisionId.ToString("D"), preparedWholeDraft.GetProperty("restoredFrom").GetProperty("revisionId").GetString());
            Assert.AreEqual(historySaved.SelectedRoot.RevisionId.ToString("D"), preparedWholeDraft.GetProperty("baseline").GetProperty("revisionId").GetString());
            Assert.AreEqual(beforeWholeRestore, await File.ReadAllTextAsync(source, token));
            Assert.AreEqual(unchangedWindow.Draft, (await Read()).Draft);
            comparisonArguments["expectedSourceToken"] = new string('0', 64);
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_prepare_restoration", comparisonArguments, cancellationToken: token)).IsError == true);
            Assert.AreEqual(beforeWholeRestore, await File.ReadAllTextAsync(source, token));
            // Whole-diagram history has its own read-only inspector and explicit
            // preview; browsing never replaces the user's current editing draft.
            var oldDiagram = historySaved.SelectedRoot;
            var topologyDraft = historySaved.StartDraft(oldDiagram);
            topologyDraft = topologyDraft with { Children = [topologyDraft.Children[0]],
                Diagram = topologyDraft.LocalDiagram with { Connections = [] } };
            var topologyGraph = historySaved.SaveDraft(oldDiagram, [oldDiagram], topologyDraft,
                Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin("Another agent")).Graph;
            string topologyXml = RecursiveBlockGraphXml.Write(topologyGraph);
            await File.WriteAllTextAsync(source, topologyXml, token); Key("r", control: true);
            await Wait(s => !s.Busy && !s.Dirty && s.Draft.Baseline.RevisionId == topologyGraph.SelectedRoot.RevisionId.ToString("D"));
            Key("3", control: true); Key("a", control: true); Type("Keep this uncommitted routing text."); await Wait(s => s.Dirty);
            var historyBeforeDraft = (await Read()).Draft.Clone();
            await ObserveViews("current-draft", true, oldDiagram);
            Key("h", control: true);
            var wholeOpened = await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false, LoadedCount: 50 });
            Assert.AreEqual(historyBeforeDraft, wholeOpened.Draft); Assert.IsNull(wholeOpened.DiagramHistory.Preview);
            Key("Down");
            await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false } h && h.Inspected.RevisionId == oldDiagram.RevisionId.ToString("D"));
            Assert.AreEqual(historyBeforeDraft, (await Read()).Draft); Assert.AreEqual(topologyXml, await File.ReadAllTextAsync(source, token));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-diagram-history-inspection.png"), token);
            ulong historySizeRevision = (await Read()).ViewRevision;
            NativeKeyboard.SchematicShortcut(display, processId, "", "Structural diagram", false, false, resizeWidth: 1100, resizeHeight: 760);
            await Wait(s => s.ViewRevision > historySizeRevision && s.Rendered);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-diagram-history-compact.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "", "Structural diagram", false, false, resizeWidth: 1536, resizeHeight: 1024);
            await Wait(s => s.Rendered);
            Key("p", alt: true);
            await Wait(s => !s.Busy && s.DiagramHistory?.Preview?.RevisionId == oldDiagram.RevisionId.ToString("D"));
            await ObserveViews("historical-preview", false, oldDiagram);
            Assert.AreEqual(historyBeforeDraft, (await Read()).Draft);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-diagram-history-preview.png"), token);
            Key("c", alt: true); await Wait(s => s.DiagramHistory is { Preview: null });
            Key("d", alt: true); await Window("Unsaved changes"); Key("Escape", title: "Unsaved changes");
            await Wait(s => !s.Busy && s.DiagramHistory is not null && s.Dirty);
            Assert.AreEqual(historyBeforeDraft, (await Read()).Draft); Assert.AreEqual(topologyXml, await File.ReadAllTextAsync(source, token));
            Key("Escape"); await Wait(s => s.DiagramHistory is null); Assert.AreEqual(historyBeforeDraft, (await Read()).Draft);
            Key("d", alt: true); await Wait(s => !s.Busy && !s.Dirty);
            Key("h", control: true); await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false, LoadedCount: 50 });
            Key("Down"); await Wait(s => !s.Busy && s.DiagramHistory?.Inspected?.RevisionId == oldDiagram.RevisionId.ToString("D"));
            Key("d", alt: true);
            var restoredDiagram = await Wait(s => !s.Busy && s.DiagramHistory is null && s.Dirty && s.Draft.RestoredFrom?.RevisionId == oldDiagram.RevisionId.ToString("D"));
            Assert.HasCount(2, restoredDiagram.Draft.Children); Assert.HasCount(2, restoredDiagram.Draft.LocalDiagram.Connections);
            Assert.AreEqual(topologyXml, await File.ReadAllTextAsync(source, token));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-diagram-history-restored-draft.png"), token);
            await Save();
            string wholeSavedXml = await File.ReadAllTextAsync(source, token); var wholeSaved = RecursiveBlockGraphXml.Read(wholeSavedXml);
            Assert.AreEqual(topologyGraph.SelectedRoot.RevisionId, wholeSaved.Inspect(wholeSaved.SelectedRoot).ParentRevisionId);
            Assert.AreEqual(oldDiagram, wholeSaved.Inspect(wholeSaved.SelectedRoot).RestoredFrom);
            Assert.HasCount(1, wholeSaved.Inspect(topologyGraph.SelectedRoot).Children);
            Assert.HasCount(2, wholeSaved.Inspect(wholeSaved.SelectedRoot).Children);
            Key("h", control: true);
            var cancellingHistory = await Wait(s => s.DiagramHistory is not null);
            Key("Escape");
            var afterHistoryCancel = await Wait(s => !s.Busy && s.DiagramHistory is null && s.ViewRevision > cancellingHistory.ViewRevision);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-history-cancelled-state.json"), SchematicJson.Formatter.Format(afterHistoryCancel), token);
            string cancelledCanvas = Path.Combine(evidence, instanceId + "-history-cancelled-canvas.png");
            await CaptureRecursive(display, cancelledCanvas, token);
            try { await VerifyExpandedCanvasContent(cancelledCanvas, token); }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                interactionFailures.Add(error);
                ulong cancelledCanvasBeforeFit = (await Read()).ViewRevision;
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true,
                    clickFromLeft: 378, clickFromTop: 45);
                await Wait(s => s.Rendered && s.ViewRevision > cancelledCanvasBeforeFit);
                await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-history-canvas-fit-recovery.png"), token);
            }
            Assert.IsFalse((await Read()).Dirty, "Closing while history loads cannot create a draft.");
            // Continue safe observations after a failed shortcut without erasing
            // that failure. The real History button distinguishes input dispatch
            // from a broken model or an unavailable history source.
            try
            {
                Key("h", control: true); await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false, LoadedCount: 50 });
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                interactionFailures.Add(error);
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true,
                    clickFromRight: 440, clickFromTop: 115);
                var recoveredHistory = await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false, LoadedCount: 50 });
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-history-pointer-recovery.json"), SchematicJson.Formatter.Format(recoveredHistory), token);
                await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-history-pointer-recovery.png"), token);
            }
            var inspectedBeforeMore = (await Read()).DiagramHistory.Inspected;
            Key("o", alt: true); await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false, LoadedCount: 100 });
            Assert.AreEqual(inspectedBeforeMore, (await Read()).DiagramHistory.Inspected);
            await File.AppendAllTextAsync(source, "\n", token); Key("Down");
            await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false } h && !string.IsNullOrEmpty(h.ErrorMessage));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-diagram-history-stale.png"), token);
            Key("r", alt: true); await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false } h && !string.IsNullOrEmpty(h.ErrorMessage));
            Assert.AreEqual(wholeSavedXml + "\n", await File.ReadAllTextAsync(source, token));
            // Reload reads the changed file, so it is finished only once the editor holds the new file's source token; an idle
            // state read before the reload starts would let the next keys race with the reload's selection reset.
            string staleToken = (await Read()).SourceToken;
            Key("Escape"); await Wait(s => s.DiagramHistory is null); Key("r", control: true);
            await Wait(s => !s.Busy && !s.Dirty && s.Ready && s.SourceToken != staleToken);
            Key("Escape"); Key("Right"); await Wait(s => s.Draft.Baseline.BlockId == fixture.Blocks["PSU"].BlockId.ToString("D"));
            Key("Return"); await Wait(s => s.DiagramPath.Count == 2 && !s.Busy);
            var childGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var oldPsu = childGraph.Inspect(childGraph.SelectedRoot).Children[0];
            var unchangedSibling = childGraph.Inspect(childGraph.SelectedRoot).Children[1];
            var smallerPsu = childGraph.StartDraft(oldPsu);
            smallerPsu = smallerPsu with { Children = [smallerPsu.Children[1]], Diagram = smallerPsu.LocalDiagram with { Connections = [] } };
            var smallerGraph = childGraph.SaveDraft(childGraph.SelectedRoot, [childGraph.SelectedRoot, oldPsu], smallerPsu,
                Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
            await File.WriteAllTextAsync(source, RecursiveBlockGraphXml.Write(smallerGraph), token);
            Key("r", control: true); await Wait(s => !s.Busy && s.Draft.Children.Count == 1);
            Key("3", control: true); Key("a", control: true); Type("Save my work before restoration."); await Wait(s => s.Dirty);
            Key("h", control: true); await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false });
            Key("End"); await Wait(s => !s.Busy && s.DiagramHistory?.Inspected?.RevisionId == oldPsu.RevisionId.ToString("D"));
            Key("p", alt: true); await Wait(s => s.DiagramHistory?.Preview?.RevisionId == oldPsu.RevisionId.ToString("D"));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-diagram-history-child-preview.png"), token);
            Key("d", alt: true); await Window("Unsaved changes"); Key("s", alt: true, title: "Unsaved changes");
            await Wait(s => !s.Busy && s.DiagramHistory is null && s.Draft.RestoredFrom?.RevisionId == oldPsu.RevisionId.ToString("D"));
            string savedBeforeRestoreXml = await File.ReadAllTextAsync(source, token); var savedBeforeRestore = RecursiveBlockGraphXml.Read(savedBeforeRestoreXml);
            var savedPsuBeforeRestore = savedBeforeRestore.Inspect(savedBeforeRestore.SelectedRoot).Children[0];
            Assert.AreEqual("Save my work before restoration.", savedBeforeRestore.Requirements(savedPsuBeforeRestore).Requirements.Routing);
            Assert.HasCount(1, savedBeforeRestore.Inspect(savedPsuBeforeRestore).Children);
            Key("d", alt: true); await Wait(s => !s.Busy && !s.Dirty && s.Draft.Fields.Routing == "Save my work before restoration.");
            Key("3", control: true); Key("a", control: true); Type("Explicitly decline this draft."); await Wait(s => s.Dirty);
            Key("h", control: true); await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false });
            Key("End"); await Wait(s => !s.Busy && s.DiagramHistory?.Inspected?.RevisionId == oldPsu.RevisionId.ToString("D"));
            Key("d", alt: true); await Window("Unsaved changes"); Key("d", alt: true, title: "Unsaved changes");
            var childRestoredDraft = await Wait(s => !s.Busy && s.DiagramHistory is null && s.Draft.RestoredFrom?.RevisionId == oldPsu.RevisionId.ToString("D"));
            Assert.HasCount(2, childRestoredDraft.Draft.Children); Assert.HasCount(2, childRestoredDraft.Draft.LocalDiagram.Connections);
            Assert.AreEqual(savedBeforeRestoreXml, await File.ReadAllTextAsync(source, token));
            await Save();
            var restoredChildGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual(unchangedSibling, restoredChildGraph.Inspect(restoredChildGraph.SelectedRoot).Children[1]);
            Assert.AreEqual(oldPsu, restoredChildGraph.Inspect(restoredChildGraph.Inspect(restoredChildGraph.SelectedRoot).Children[0]).RestoredFrom);
            Assert.IsTrue(RecursiveBlockDefinitionTests.Partial().SameContents(restoredChildGraph.Inspect(restoredChildGraph.SelectedRoot).EffectiveDefinition),
                "Native requirement edits, history restoration and child saves must retain independent definition choices.");
            Assert.IsTrue(initialBindings.SameContents(restoredChildGraph.Inspect(restoredChildGraph.SelectedRoot).EffectiveComponentBindings),
                "Native editing, undo and restoration must preserve exact component identities, including unresolved targets.");
            Assert.IsTrue(originalInput.SameContents(restoredChildGraph.RefinementInput(originalInput.Id)),
                "Native edits and history restoration must preserve original inputs without following later heads.");
            var currentDefinitionState = await Read();
            var selectedDefinition = RecursiveBlockDefinitionTests.Partial().With(BlockDefinitionFacet.Model,
                RecursiveBlockDefinitionTests.Choice(DefinitionChoiceState.Selected, ["Fixture selected model"]));
            var definitionArguments = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = (await native.HandshakeAsync(token)).Epoch,
                ["expectedSourceToken"] = currentDefinitionState.SourceToken,
                ["expectedRoot"] = restoredChildGraph.SelectedRoot,
                ["blockPath"] = new[] { restoredChildGraph.SelectedRoot },
                ["definition"] = JsonSerializer.SerializeToElement(selectedDefinition, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ["refinementInputId"] = originalInput.Id,
                ["operationId"] = Guid.NewGuid(), ["actor"] = "Compatible agent fixture"
            };
            var definitionResult = await client.CallToolAsync("kicad_diagram_definition_set", definitionArguments, cancellationToken: token);
            if (definitionResult.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-definition-error.json"), JsonSerializer.Serialize(definitionResult), token);
            Assert.IsFalse(definitionResult.IsError == true);
            string definedGraphXml = await File.ReadAllTextAsync(source, token); var definedGraph = RecursiveBlockGraphXml.Read(definedGraphXml);
            Assert.IsTrue(selectedDefinition.SameContents(definedGraph.Inspect(definedGraph.SelectedRoot).EffectiveDefinition));
            Assert.IsTrue(definedGraph.Inspect(definedGraph.SelectedRoot).Origin.InputIds.Contains(originalInput.Id));
            Assert.AreEqual(restoredChildGraph.Requirements(restoredChildGraph.SelectedRoot).Requirements, definedGraph.Requirements(definedGraph.SelectedRoot).Requirements);
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_definition_set", definitionArguments, cancellationToken: token)).IsError == true);
            var currentDefinitionToken = JsonSerializer.SerializeToElement(definitionResult).GetProperty("structuredContent").GetProperty("sourceToken").GetString();
            definitionArguments["expectedSourceToken"] = currentDefinitionToken; definitionArguments["expectedRoot"] = definedGraph.SelectedRoot;
            definitionArguments["blockPath"] = new[] { definedGraph.SelectedRoot }; definitionArguments["operationId"] = Guid.NewGuid();
            var definitionNoOp = await client.CallToolAsync("kicad_diagram_definition_set", definitionArguments, cancellationToken: token);
            Assert.IsFalse(definitionNoOp.IsError == true);
            Assert.IsFalse(JsonSerializer.SerializeToElement(definitionNoOp).GetProperty("structuredContent").GetProperty("changed").GetBoolean());
            Assert.AreEqual(definedGraphXml, await File.ReadAllTextAsync(source, token));
            var classReference = selectedDefinition.KnowledgeClass!.Values.Single();
            var statement = new GuidanceStatement(Guid.NewGuid(), "routing", "routing", "Keep the sensing region accessible.", GuidanceStrength.Preference, "", []);
            var knowledge = new ComponentKnowledgeLibrary(classReference.LibraryId, classReference.LibraryRevision,
                [new(classReference.ClassId, "Fixture reusable class", null, [statement])]);
            await File.WriteAllTextAsync(Path.Combine(project, "fixture-knowledge.xml"), ComponentKnowledgeXml.WriteLibrary(knowledge), token);
            var guidanceArguments = new Dictionary<string, object?>(arguments)
            {
                ["expectedSourceToken"] = currentDefinitionToken, ["selection"] = definedGraph.SelectedRoot,
                ["libraryPaths"] = new[] { "fixture-knowledge.xml" }
            };
            var guidanceResult = await client.CallToolAsync("kicad_diagram_definition_guidance", guidanceArguments, cancellationToken: token);
            Assert.IsFalse(guidanceResult.IsError == true);
            var guidanceData = JsonSerializer.SerializeToElement(guidanceResult).GetProperty("structuredContent");
            Assert.AreEqual("Available", guidanceData.GetProperty("resolution").GetProperty("selected").GetProperty("availability").GetString());
            Assert.AreEqual("Keep the sensing region accessible.", guidanceData.GetProperty("resolution").GetProperty("selected").GetProperty("guidance").GetProperty("effective")[0].GetProperty("statement").GetProperty("text").GetString());
            Assert.AreEqual(64, guidanceData.GetProperty("libraries")[0].GetProperty("contentSha256").GetString()!.Length);
            guidanceArguments["libraryPaths"] = Array.Empty<string>();
            var missingClass = await client.CallToolAsync("kicad_diagram_definition_guidance", guidanceArguments, cancellationToken: token);
            Assert.IsFalse(missingClass.IsError == true);
            Assert.AreEqual("MissingLibrary", JsonSerializer.SerializeToElement(missingClass).GetProperty("structuredContent").GetProperty("resolution").GetProperty("selected").GetProperty("availability").GetString());
            Assert.AreEqual(definedGraphXml, await File.ReadAllTextAsync(source, token));
            var electrical = await RecursiveBlockComponentTests.WriteRepository(project, token);
            var componentArguments = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = currentDefinitionToken,
                ["expectedRoot"] = definedGraph.SelectedRoot, ["blockPath"] = new[] { definedGraph.SelectedRoot },
                ["bindings"] = JsonSerializer.SerializeToElement(electrical.Bindings, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ["refinementInputId"] = originalInput.Id,
                ["operationId"] = Guid.NewGuid(), ["actor"] = "Compatible component agent fixture"
            };
            var componentResult = await client.CallToolAsync("kicad_diagram_components_set", componentArguments, cancellationToken: token);
            if (componentResult.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-components-error.json"), JsonSerializer.Serialize(componentResult), token);
            Assert.IsFalse(componentResult.IsError == true);
            var componentData = JsonSerializer.SerializeToElement(componentResult).GetProperty("structuredContent");
            string componentToken = componentData.GetProperty("sourceToken").GetString()!;
            string mappedGraphXml = await File.ReadAllTextAsync(source, token); var mappedGraph = RecursiveBlockGraphXml.Read(mappedGraphXml);
            Assert.IsTrue(electrical.Bindings.SameContents(mappedGraph.Inspect(mappedGraph.SelectedRoot).EffectiveComponentBindings));
            Assert.IsTrue(initialBindings.SameContents(mappedGraph.Inspect(definedGraph.SelectedRoot).EffectiveComponentBindings));
            Assert.AreEqual(definedGraph.Requirements(definedGraph.SelectedRoot).Requirements, mappedGraph.Requirements(mappedGraph.SelectedRoot).Requirements);
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_components_set", componentArguments, cancellationToken: token)).IsError == true);
            componentArguments["expectedSourceToken"] = componentToken; componentArguments["expectedRoot"] = mappedGraph.SelectedRoot;
            componentArguments["blockPath"] = new[] { mappedGraph.SelectedRoot }; componentArguments["operationId"] = Guid.NewGuid();
            var componentNoOp = await client.CallToolAsync("kicad_diagram_components_set", componentArguments, cancellationToken: token);
            Assert.IsFalse(componentNoOp.IsError == true);
            Assert.IsFalse(JsonSerializer.SerializeToElement(componentNoOp).GetProperty("structuredContent").GetProperty("changed").GetBoolean());
            componentArguments["expectedInstanceEpoch"] = Guid.NewGuid().ToString("D");
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_components_set", componentArguments, cancellationToken: token)).IsError == true);
            var inspectComponents = new Dictionary<string, object?>(arguments)
            {
                ["expectedSourceToken"] = componentToken, ["selection"] = mappedGraph.SelectedRoot,
                ["manifestPath"] = "hardware.xml", ["expectedManifestToken"] = electrical.ManifestHash
            };
            var componentObservation = await client.CallToolAsync("kicad_diagram_components", inspectComponents, cancellationToken: token);
            if (componentObservation.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-component-observation-error.json"), JsonSerializer.Serialize(componentObservation), token);
            Assert.IsFalse(componentObservation.IsError == true);
            var inspected = JsonSerializer.SerializeToElement(componentObservation).GetProperty("structuredContent").GetProperty("inspection");
            Assert.AreEqual(0, inspected.GetProperty("failures").GetArrayLength());
            Assert.AreEqual(2, inspected.GetProperty("components").GetArrayLength());
            foreach (var resolvedComponent in inspected.GetProperty("components").EnumerateArray())
            {
                Assert.AreEqual("ComponentResolved", resolvedComponent.GetProperty("status").GetString());
                Assert.AreEqual(2, resolvedComponent.GetProperty("nativeLocations").GetArrayLength());
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-component-observation.json"), JsonSerializer.Serialize(componentObservation), token);
            inspectComponents["expectedManifestToken"] = "stale";
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_components", inspectComponents, cancellationToken: token)).IsError == true);
            Assert.AreEqual(mappedGraphXml, await File.ReadAllTextAsync(source, token));
            var proposal = RecursiveBlockProposalTests.CreateFor(mappedGraph, originalInput);
            var proposalArguments = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = componentToken,
                ["proposalJson"] = JsonSerializer.SerializeToElement(proposal, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ["operationId"] = Guid.NewGuid()
            };
            var proposalResult = await client.CallToolAsync("kicad_diagram_proposal_publish", proposalArguments, cancellationToken: token);
            if (proposalResult.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-proposal-error.json"), JsonSerializer.Serialize(proposalResult), token);
            Assert.IsFalse(proposalResult.IsError == true);
            var proposalData = JsonSerializer.SerializeToElement(proposalResult).GetProperty("structuredContent");
            Assert.IsTrue(proposalData.GetProperty("added").GetBoolean());
            Assert.IsFalse(proposalData.GetProperty("contextStillSelected").GetBoolean(), "The proposal was based on the original input while later edits had advanced the active root.");
            // Publishing a proposal built on an older revision keeps it for comparison and says so (ledger pa48933d0fe0a5c2f).
            Assert.IsTrue(proposalData.GetProperty("comparison").GetProperty("stale").GetBoolean(), "The published proposal is compared with the advanced root.");
            Assert.AreNotEqual(0, proposalData.GetProperty("comparison").GetProperty("currentChanges").GetArrayLength());
            Assert.AreEqual("Published", proposalData.GetProperty("publication").GetProperty("stage").GetString());
            var proposalPublication = await client.CallToolAsync("kicad_diagram_proposal_publication", new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId, ["expectedInstanceEpoch"] = native.Epoch, ["operationId"] = proposalArguments["operationId"]
            }, cancellationToken: token);
            if (proposalPublication.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-proposal-publication-error.json"), JsonSerializer.Serialize(proposalPublication), token);
            Assert.IsFalse(proposalPublication.IsError == true);
            Assert.AreEqual("Published", JsonSerializer.SerializeToElement(proposalPublication).GetProperty("structuredContent")
                .GetProperty("receipt").GetProperty("stage").GetString());
            string proposalGraphXml = await File.ReadAllTextAsync(source, token); var proposalGraph = RecursiveBlockGraphXml.Read(proposalGraphXml);
            Assert.IsTrue(proposalGraph.Proposal(proposal.Id).Issues.Any());
            // The proposed root implementation, still unselected, continues the root's field history from the revision the
            // original input captured: the agent's rewrite first, then every earlier text with its own provenance.
            AssertFieldHistory(await FieldHistoryOverMcp(client, arguments, proposal.Candidate, "General", null, token),
                [ProposedFieldEntry(proposal, DiagramRequirementField.General),
                 .. AllFieldEntries((offset, limit) => DiagramFieldHistoryQuery.Block(mappedGraph, originalInput.BlockPath[^1], DiagramRequirementField.General, offset, limit))
                    .Select(e => e with { IsSavedText = false })],
                "unselected root proposal", Contexts(proposalGraph));
            var proposalReadArguments = new Dictionary<string, object?>(arguments)
            {
                ["expectedSourceToken"] = proposalData.GetProperty("sourceToken").GetString(), ["proposalId"] = proposal.Id
            };
            var proposalRead = await client.CallToolAsync("kicad_diagram_proposal_read", proposalReadArguments, cancellationToken: token);
            Assert.IsFalse(proposalRead.IsError == true);
            Assert.AreEqual(proposal.Id.ToString("D"), JsonSerializer.SerializeToElement(proposalRead).GetProperty("structuredContent")
                .GetProperty("proposal").GetProperty("id").GetString());
            proposalReadArguments["proposalId"] = Guid.NewGuid();
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_proposal_read", proposalReadArguments, cancellationToken: token)).IsError == true);
            componentToken = proposalData.GetProperty("sourceToken").GetString()!;
            mappedGraph = proposalGraph; mappedGraphXml = proposalGraphXml;
            Key("r", control: true); await Wait(s => !s.Busy && !s.Dirty && s.SourceToken == componentToken);
            // Reload preserves the PSU location used above; root mutations made
            // through MCP must not silently change that native editing scope.
            Assert.AreEqual(fixture.Blocks["PSU"].BlockId.ToString("D"), (await Read()).DiagramPath[^1].BlockId);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true,
                clickFromLeft: 118, clickFromTop: 45);
            await Wait(s => !s.Busy && s.DiagramPath.Count == 1);
            await ClickBlankCanvas();
            await Wait(s => !s.Busy && s.Draft.Baseline.RevisionId == mappedGraph.SelectedRoot.RevisionId.ToString("D"));
            Key("h", control: true); await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false });
            Key("Down"); await Wait(s => !s.Busy && s.DiagramHistory is { Busy: false } h
                && h.Inspected.RevisionId == definedGraph.SelectedRoot.RevisionId.ToString("D"));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-component-history.png"), token);
            Key("d", alt: true);
            var restoredComponents = await Wait(s => !s.Busy && s.DiagramHistory is null && s.Dirty);
            Assert.IsTrue(initialBindings.SameContents(RecursiveBlockCodec.Decode(restoredComponents.Draft.ComponentBindings)));
            Assert.AreEqual(mappedGraphXml, await File.ReadAllTextAsync(source, token));
            Key("d", alt: true); var declinedComponents = await Wait(s => !s.Busy && !s.Dirty);
            Assert.IsTrue(electrical.Bindings.SameContents(RecursiveBlockCodec.Decode(declinedComponents.Draft.ComponentBindings)));
            Key("3", control: true); Key("a", control: true); Type("Keep mapped components reachable."); await Wait(s => s.Dirty);
            await Save();
            var nativeSavedComponents = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.IsTrue(electrical.Bindings.SameContents(nativeSavedComponents.Inspect(nativeSavedComponents.SelectedRoot).EffectiveComponentBindings));
            Assert.AreEqual("Keep mapped components reachable.", nativeSavedComponents.Requirements(nativeSavedComponents.SelectedRoot).Requirements.Routing);
            Assert.AreEqual(mappedGraph.Requirements(mappedGraph.SelectedRoot).Requirements.General,
                nativeSavedComponents.Requirements(nativeSavedComponents.SelectedRoot).Requirements.General);
            // Ledger p390b40bed99e0ab2 for members (review finding 2): a supply agent groups the PSU level's Supply connection's
            // signals into a member with kicad_diagram_connection_members_refine: a "Converted rails" group of the new signals VOUT
            // and RTN, whose own General text cites the regulator datasheet by page, table and part variant. The PSU proposal below
            // refines that member too, and its field history is read back over MCP across the choice.
            var groupBase = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var groupPsu = groupBase.Inspect(groupBase.SelectedRoot).Children.Single(c => c.BlockId == fixture.Blocks["PSU"].BlockId);
            var groupSupply = groupBase.Inspect(groupPsu).LocalDiagram.Connections.Single(c => c.ConnectionId == fixture.Links["PSU/Supply"].ConnectionId);
            Guid convertedRails = Guid.NewGuid(), vout = Guid.NewGuid(), rtn = Guid.NewGuid(), groupOperation = Guid.NewGuid();
            var datasheet = new SourceReference("regulator-datasheet", "rev-c", 7, "Table 3", "TPS62A0-Q1");
            var memberEnums = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            var groupCall = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = await FileToken(source, token), ["expectedRoot"] = groupBase.SelectedRoot,
                ["blockPath"] = new[] { groupBase.SelectedRoot, groupPsu }, ["connectionPath"] = new[] { groupSupply }, ["memberIds"] = new[] { convertedRails },
                ["newMembers"] = JsonSerializer.SerializeToElement(new[]
                {
                    new ConnectionMemberDefinition(convertedRails, "Converted rails", [vout, rtn], DiagramConnectionKind.SignalGroup,
                        "Carry the converted rail and its return to the boundary."),
                    new ConnectionMemberDefinition(vout, "VOUT", []), new ConnectionMemberDefinition(rtn, "RTN", [])
                }, memberEnums),
                ["operationId"] = groupOperation, ["actor"] = "Supply agent", ["sources"] = new[] { datasheet }
            };
            var grouped = await client.CallToolAsync("kicad_diagram_connection_members_refine", groupCall, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-supply-members-grouped.json"), JsonSerializer.Serialize(grouped), token);
            Assert.IsFalse(grouped.IsError == true, JsonSerializer.Serialize(grouped));
            Assert.IsTrue(JsonSerializer.SerializeToElement(grouped).GetProperty("structuredContent").GetProperty("changed").GetBoolean());
            var groupedGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var groupedPsu = groupedGraph.Inspect(groupedGraph.SelectedRoot).Children.Single(c => c.BlockId == groupPsu.BlockId);
            var groupedSupply = groupedGraph.Inspect(groupedPsu).LocalDiagram.Connections.Single(c => c.ConnectionId == groupSupply.ConnectionId);
            // The connection edit's new revision is the operation's identity, so an agent whose call was cut off can see it landed.
            Assert.AreEqual(groupOperation, groupedSupply.RevisionId, "The grouped Supply revision is the operation's identity.");
            CollectionAssert.AreEqual(new[] { convertedRails }, groupedGraph.Connections(groupPsu.BlockId).Inspect(groupedSupply).Members.Select(m => m.ConnectionId).ToArray());
            // Repeating the landed grouping on the file it produced (its token and paths) cannot save it twice: the new members it
            // declares now exist, so it is refused as identity_reused and writes nothing. (A repeated end binding instead finds
            // nothing to change; the connection-details journey proves that.)
            var groupedData = JsonSerializer.SerializeToElement(grouped).GetProperty("structuredContent");
            string groupedXml = await File.ReadAllTextAsync(source, token);
            var regrouped = await client.CallToolAsync("kicad_diagram_connection_members_refine", new Dictionary<string, object?>(groupCall)
            {
                ["expectedSourceToken"] = groupedData.GetProperty("sourceToken").GetString(), ["expectedRoot"] = groupedData.GetProperty("selectedRoot"),
                ["blockPath"] = groupedData.GetProperty("blockPath"), ["connectionPath"] = groupedData.GetProperty("connectionPath")
            }, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-supply-members-regrouped.json"), JsonSerializer.Serialize(regrouped), token);
            Assert.IsTrue(regrouped.IsError == true, "Repeating the landed grouping is refused: " + JsonSerializer.Serialize(regrouped));
            Assert.AreEqual("identity_reused", JsonSerializer.SerializeToElement(regrouped).GetProperty("structuredContent").GetProperty("code").GetString());
            Assert.AreEqual(groupedXml, await File.ReadAllTextAsync(source, token), "The refused repeat writes nothing.");
            nativeSavedComponents = groupedGraph;
            var beforePhysical = await client.CallToolAsync("kicad_diagram_read", arguments, cancellationToken: token);
            Assert.IsFalse(beforePhysical.IsError == true);
            var beforePhysicalData = JsonSerializer.SerializeToElement(beforePhysical).GetProperty("structuredContent");
            var physical = new BlockPhysicalAllocation(PhysicalAllocationState.Partial,
                [new PhysicalAllocationTarget(Guid.NewGuid(), PhysicalAllocationKind.Board, "Main controller PCB", "board:main", "boards/main.kicad_pcb")],
                "Power and telemetry boards remain to be allocated.");
            var physicalArguments = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = beforePhysicalData.GetProperty("sourceToken").GetString(),
                ["expectedRoot"] = nativeSavedComponents.SelectedRoot, ["blockPath"] = new[] { nativeSavedComponents.SelectedRoot },
                ["allocation"] = JsonSerializer.SerializeToElement(physical, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { Converters = { new JsonStringEnumConverter() } }), ["operationId"] = Guid.NewGuid(), ["actor"] = "Compatible allocation agent fixture"
            };
            var physicalResult = await client.CallToolAsync("kicad_diagram_physical_allocation_set", physicalArguments, cancellationToken: token);
            if (physicalResult.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-physical-allocation-error.json"), JsonSerializer.Serialize(physicalResult), token);
            Assert.IsFalse(physicalResult.IsError == true);
            var physicalData = JsonSerializer.SerializeToElement(physicalResult).GetProperty("structuredContent");
            Assert.IsTrue(physicalData.GetProperty("changed").GetBoolean());
            string physicalToken = physicalData.GetProperty("sourceToken").GetString()!;
            var physicalSelection = physicalData.GetProperty("selection").Deserialize<BlockSelection>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var physicalReadArguments = new Dictionary<string, object?>(arguments)
            { ["expectedSourceToken"] = physicalToken, ["selection"] = physicalSelection };
            var physicalRead = await client.CallToolAsync("kicad_diagram_physical_allocation", physicalReadArguments, cancellationToken: token);
            Assert.IsFalse(physicalRead.IsError == true);
            var physicalReadData = JsonSerializer.SerializeToElement(physicalRead).GetProperty("structuredContent");
            Assert.AreEqual("Partial", physicalReadData.GetProperty("allocation").GetProperty("state").GetString());
            Assert.AreEqual("Main controller PCB", physicalReadData.GetProperty("allocation").GetProperty("targets")[0].GetProperty("name").GetString());
            physicalArguments["expectedSourceToken"] = physicalToken; physicalArguments["expectedRoot"] = physicalSelection;
            physicalArguments["blockPath"] = new[] { physicalSelection };
            physicalArguments["operationId"] = Guid.NewGuid();
            var physicalNoOp = await client.CallToolAsync("kicad_diagram_physical_allocation_set", physicalArguments, cancellationToken: token);
            Assert.IsFalse(physicalNoOp.IsError == true);
            Assert.IsFalse(JsonSerializer.SerializeToElement(physicalNoOp).GetProperty("structuredContent").GetProperty("changed").GetBoolean());
            var savedPhysical = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual("Main controller PCB", savedPhysical.Inspect(physicalSelection).PhysicalAllocation!.Targets[0].Name);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-physical-allocation.json"), JsonSerializer.Serialize(physicalRead), token);
            // Choosing a published whole-block proposal applies it only to the
            // exact target it refined. Competing edits that advanced the target
            // keep both versions for comparison; siblings and history stay intact.
            var beforeSelectionRead = await client.CallToolAsync("kicad_diagram_read", arguments, cancellationToken: token);
            Assert.IsFalse(beforeSelectionRead.IsError == true);
            string selectionToken = JsonSerializer.SerializeToElement(beforeSelectionRead).GetProperty("structuredContent").GetProperty("sourceToken").GetString()!;
            var selectionBase = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var selectArguments = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = selectionToken, ["proposalId"] = proposal.Id,
                ["expectedRoot"] = selectionBase.SelectedRoot, ["currentPath"] = new[] { selectionBase.SelectedRoot },
                ["ancestorRevisionIds"] = Array.Empty<Guid>(), ["actor"] = "Compatible agent fixture"
            };
            async Task<JsonElement> RejectedSelection(string label, string code)
            {
                string unchanged = await File.ReadAllTextAsync(source, token); string unchangedToken = await FileToken(source, token); var nativeBefore = await Read();
                Guid rejectedOperation = Guid.NewGuid(); selectArguments["operationId"] = rejectedOperation;
                var rejected = await client.CallToolAsync("kicad_diagram_proposal_select", selectArguments, cancellationToken: token);
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-proposal-select-" + label + ".json"), JsonSerializer.Serialize(rejected), token);
                Assert.IsTrue(rejected.IsError == true, "A " + label + " proposal selection must be rejected, not applied over newer work.");
                var refusal = JsonSerializer.SerializeToElement(rejected).GetProperty("structuredContent");
                Assert.AreEqual(code, refusal.GetProperty("code").GetString());
                Assert.AreEqual(unchanged, await File.ReadAllTextAsync(source, token), "A rejected " + label + " selection must leave the saved design unchanged.");
                var nativeAfter = await Read();
                Assert.AreEqual(nativeBefore.SourceToken, nativeAfter.SourceToken); Assert.AreEqual(nativeBefore.DiagramPath, nativeAfter.DiagramPath);
                Assert.AreEqual(nativeBefore.Draft, nativeAfter.Draft);
                var noReceipt = await client.CallToolAsync("kicad_diagram_proposal_publication", new Dictionary<string, object?>
                    { ["instanceId"] = instanceId, ["expectedInstanceEpoch"] = native.Epoch, ["operationId"] = rejectedOperation }, cancellationToken: token);
                Assert.AreEqual("missing_block_proposal_receipt", JsonSerializer.SerializeToElement(noReceipt).GetProperty("structuredContent").GetProperty("code").GetString(),
                    "A rejected selection must not leave a prepared write behind.");
                // The refusal names every element changed on each side, from today's file (ledger pa48933d0fe0a5c2f).
                AssertRefusalComparison(refusal, label, unchangedToken);
                return refusal;
            }
            // Ledger pa48933d0fe0a5c2f: the input's context is bound to its revisions, so many edits later it is the same context with
            // the same fingerprint, now marked as no longer on today's design; the commented level's context likewise.
            var laterInputContext = await AgentContextOverMcp(client, arguments, selectionToken, token, inputId: originalInput.Id);
            AssertAgentContext(laterInputContext, selectionBase, originalInput.BlockPath, originalInput, current: false, "recorded input after later edits");
            Assert.AreEqual(inputContextSha, laterInputContext.GetProperty("contextSha256").GetString(), "Later edits do not change a revision-bound context.");
            Assert.AreEqual(inputContext.GetProperty("context").GetRawText(), laterInputContext.GetProperty("context").GetRawText());
            var laterSketchContext = await AgentContextOverMcp(client, arguments, selectionToken, token, blockPath: sketchPath);
            AssertAgentContext(laterSketchContext, selectionBase, sketchPath, null, current: false, "commented level after later edits");
            Assert.AreEqual(sketchContextSha, laterSketchContext.GetProperty("contextSha256").GetString());
            var todayContext = await AgentContextOverMcp(client, arguments, selectionToken, token, blockPath: [selectionBase.SelectedRoot]);
            AssertAgentContext(todayContext, selectionBase, [selectionBase.SelectedRoot], null, current: true, "today's root");
            Assert.AreNotEqual(inputContextSha, todayContext.GetProperty("contextSha256").GetString());
            // A context that names no level, an outdated file, a path that does not start at the root or is not pinned revision by revision,
            // or a level outside the input's revisions is refused.
            await AgentContextOverMcp(client, arguments, selectionToken, token, expectError: "ambiguous_agent_context");
            await AgentContextOverMcp(client, arguments, componentToken, token, blockPath: [selectionBase.SelectedRoot], expectError: "recursive_block_file_changed");
            var todayPsu = selectionBase.Inspect(selectionBase.SelectedRoot).Children.Single(c => c.BlockId == fixture.Blocks["PSU"].BlockId);
            await AgentContextOverMcp(client, arguments, selectionToken, token, blockPath: [todayPsu], expectError: "invalid_agent_context_scope");
            await AgentContextOverMcp(client, arguments, selectionToken, token, blockPath: [originalInput.BlockPath[0], sketchPath[1]], expectError: "invalid_agent_context_scope");
            await AgentContextOverMcp(client, arguments, selectionToken, token, inputId: originalInput.Id, blockPath: [selectionBase.SelectedRoot],
                expectError: "invalid_agent_context_scope");
            // A revision the diagram does not have is a wrong scope too, and an input it does not have is unknown.
            await AgentContextOverMcp(client, arguments, selectionToken, token,
                blockPath: [originalInput.BlockPath[0] with { RevisionId = Guid.NewGuid() }], expectError: "invalid_agent_context_scope");
            await AgentContextOverMcp(client, arguments, selectionToken, token, inputId: Guid.NewGuid(), expectError: "unknown_refinement_input");
            // A level below the input's own, inside the input's revisions: the supply the input's root pinned. The input's prompt and
            // attachments come with it, its focus does not, and that supply revision is no longer on today's design.
            BlockSelection[] belowInput = [.. originalInput.BlockPath,
                selectionBase.Inspect(originalInput.BlockPath[^1]).Children.Single(c => c.BlockId == fixture.Blocks["PSU"].BlockId)];
            var belowInputContext = await AgentContextOverMcp(client, arguments, selectionToken, token, inputId: originalInput.Id, blockPath: belowInput);
            AssertAgentContext(belowInputContext, selectionBase, belowInput, originalInput, current: false, "a level below the input");
            Assert.AreEqual(0, belowInputContext.GetProperty("context").GetProperty("focus").GetArrayLength(), "Only the input's own level has its focus.");
            Assert.AreEqual(originalInput.Id, belowInputContext.GetProperty("context").GetProperty("input").GetProperty("id").GetGuid());
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-agent-context-below-input.json"), belowInputContext.GetRawText(), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-agent-context-later.json"), laterInputContext.GetRawText(), token);
            // The first proposal refined the original root revision; the native and agent edits above have since advanced that exact
            // target. Comparing it with today's design names what changed on each side by exact identity.
            var compared = await client.CallToolAsync("kicad_diagram_proposal_compare", new Dictionary<string, object?>(arguments)
                { ["expectedSourceToken"] = selectionToken, ["proposalId"] = proposal.Id }, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-proposal-compare.json"), JsonSerializer.Serialize(compared), token);
            Assert.IsFalse(compared.IsError == true, JsonSerializer.Serialize(compared));
            var staleComparison = JsonSerializer.SerializeToElement(compared).GetProperty("structuredContent").GetProperty("comparison");
            await AssertStaleRootComparison(client, arguments, selectionToken, staleComparison, selectionBase, originalInput, proposal, published: true, token);
            // A second proposal on the same original input, sent with a token read before those edits, is refused with the same
            // comparison against today's file; it writes nothing, and the retained request compares the same way afterwards.
            var staleRequest = RecursiveBlockProposalTests.CreateFor(selectionBase, originalInput);
            staleRequest = staleRequest with { Blocks = staleRequest.Blocks.SetItem(0, staleRequest.Blocks[0] with { ImplementationName = "Second agent proposal" }) };
            string beforeStaleRequest = await File.ReadAllTextAsync(source, token);
            var refusedPublish = await client.CallToolAsync("kicad_diagram_proposal_publish", new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = componentToken, ["operationId"] = Guid.NewGuid(),
                ["proposalJson"] = JsonSerializer.SerializeToElement(staleRequest, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            }, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-proposal-publish-stale.json"), JsonSerializer.Serialize(refusedPublish), token);
            Assert.IsTrue(refusedPublish.IsError == true, "A proposal sent with an outdated token must not be published silently.");
            var publishRefusal = JsonSerializer.SerializeToElement(refusedPublish).GetProperty("structuredContent");
            Assert.AreEqual("block_proposal_source_changed", publishRefusal.GetProperty("code").GetString());
            Assert.AreEqual(beforeStaleRequest, await File.ReadAllTextAsync(source, token), "A refused publication writes nothing.");
            AssertRefusalComparison(publishRefusal, "publish-outdated-token", selectionToken);
            await AssertStaleRootComparison(client, arguments, selectionToken, publishRefusal.GetProperty("comparison"), selectionBase, originalInput, staleRequest,
                published: false, token);
            Assert.AreEqual(staleComparison.GetProperty("currentChanges").GetRawText(), publishRefusal.GetProperty("comparison").GetProperty("currentChanges").GetRawText(),
                "Both proposals share the base revision, so today's side is the same.");
            var retainedCompare = await client.CallToolAsync("kicad_diagram_proposal_compare", new Dictionary<string, object?>(arguments)
                { ["expectedSourceToken"] = selectionToken, ["proposalId"] = staleRequest.Id }, cancellationToken: token);
            Assert.IsFalse(retainedCompare.IsError == true, JsonSerializer.Serialize(retainedCompare));
            Assert.AreEqual(publishRefusal.GetProperty("comparison").GetRawText(),
                JsonSerializer.SerializeToElement(retainedCompare).GetProperty("structuredContent").GetProperty("comparison").GetRawText());
            // A third agent sends, with the same outdated token, a proposal that no longer prepares against today's file: its
            // implementation name is now taken by the first proposal, published since. The refusal keeps its own code and, having
            // no comparison, says why (comparisonUnavailable: invalid_block_proposal, as its publication would be refused); nothing
            // is written, and comparing the retained request directly is refused with that code.
            var invalidRequest = RecursiveBlockProposalTests.CreateFor(selectionBase, originalInput);
            Assert.AreEqual(selectionBase.States.Single(st => st.Id == proposal.Candidate.StateId).Name, invalidRequest.Blocks[0].ImplementationName,
                "The request reuses the implementation name the first proposal took.");
            var refusedInvalid = await client.CallToolAsync("kicad_diagram_proposal_publish", new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = componentToken, ["operationId"] = Guid.NewGuid(),
                ["proposalJson"] = JsonSerializer.SerializeToElement(invalidRequest, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            }, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-proposal-publish-stale-invalid.json"), JsonSerializer.Serialize(refusedInvalid), token);
            Assert.IsTrue(refusedInvalid.IsError == true, "An outdated proposal that no longer prepares is refused.");
            var invalidRefusal = JsonSerializer.SerializeToElement(refusedInvalid).GetProperty("structuredContent");
            Assert.AreEqual("block_proposal_source_changed", invalidRefusal.GetProperty("code").GetString(), "The refusal keeps its own code.");
            Assert.AreEqual(JsonValueKind.Null, invalidRefusal.GetProperty("comparison").ValueKind, "No comparison can be made.");
            Assert.AreEqual("invalid_block_proposal", invalidRefusal.GetProperty("comparisonUnavailable").GetProperty("code").GetString(),
                invalidRefusal.GetRawText());
            Assert.AreEqual(beforeStaleRequest, await File.ReadAllTextAsync(source, token), "A refused publication writes nothing.");
            var invalidCompare = await client.CallToolAsync("kicad_diagram_proposal_compare", new Dictionary<string, object?>(arguments)
                { ["expectedSourceToken"] = selectionToken, ["proposalId"] = invalidRequest.Id }, cancellationToken: token);
            Assert.IsTrue(invalidCompare.IsError == true, JsonSerializer.Serialize(invalidCompare));
            Assert.AreEqual("invalid_block_proposal", JsonSerializer.SerializeToElement(invalidCompare).GetProperty("structuredContent").GetProperty("code").GetString());
            // A proposal whose block list holds an empty entry is refused as malformed before anything is read, retained or written.
            var nullBlock = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(staleRequest with { Id = Guid.NewGuid() },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
            nullBlock["blocks"] = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null);
            var refusedNull = await client.CallToolAsync("kicad_diagram_proposal_publish", new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = selectionToken, ["operationId"] = Guid.NewGuid(),
                ["proposalJson"] = JsonSerializer.SerializeToElement(nullBlock)
            }, cancellationToken: token);
            Assert.IsTrue(refusedNull.IsError == true, JsonSerializer.Serialize(refusedNull));
            Assert.AreEqual("invalid_block_proposal", JsonSerializer.SerializeToElement(refusedNull).GetProperty("structuredContent").GetProperty("code").GetString());
            Assert.AreEqual(beforeStaleRequest, await File.ReadAllTextAsync(source, token), "A malformed proposal writes nothing.");
            var unknownCompare = await client.CallToolAsync("kicad_diagram_proposal_compare", new Dictionary<string, object?>(arguments)
                { ["expectedSourceToken"] = selectionToken, ["proposalId"] = Guid.NewGuid() }, cancellationToken: token);
            Assert.AreEqual("unknown_block_proposal", JsonSerializer.SerializeToElement(unknownCompare).GetProperty("structuredContent").GetProperty("code").GetString());
            var staleCompareToken = await client.CallToolAsync("kicad_diagram_proposal_compare", new Dictionary<string, object?>(arguments)
                { ["expectedSourceToken"] = componentToken, ["proposalId"] = proposal.Id }, cancellationToken: token);
            Assert.AreEqual("recursive_block_file_changed", JsonSerializer.SerializeToElement(staleCompareToken).GetProperty("structuredContent").GetProperty("code").GetString());
            // Choosing the stale proposal is refused with that comparison instead of being applied over the newer root.
            var changedTarget = await RejectedSelection("changed-target", "proposal_target_changed");
            Assert.AreEqual(staleComparison.GetRawText(), changedTarget.GetProperty("comparison").GetRawText(), "The refusal carries the same comparison.");
            var psu = selectionBase.Inspect(selectionBase.SelectedRoot).Children.Single(c => c.BlockId == fixture.Blocks["PSU"].BlockId);
            var psuInput = RecursiveBlockRefinementInputTests.Input(selectionBase) with
                { SourceSha256 = selectionToken, BlockPath = [selectionBase.SelectedRoot, psu], Attachments = [] };
            var psuInputResult = await client.CallToolAsync("kicad_diagram_refinement_input_record", new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = selectionToken,
                ["input"] = JsonSerializer.SerializeToElement(psuInput, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            }, cancellationToken: token);
            if (psuInputResult.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-psu-input-error.json"), JsonSerializer.Serialize(psuInputResult), token);
            Assert.IsFalse(psuInputResult.IsError == true);
            string inputToken = JsonSerializer.SerializeToElement(psuInputResult).GetProperty("structuredContent").GetProperty("sourceToken").GetString()!;
            var psuSupply = selectionBase.Inspect(psu).LocalDiagram.Connections.Single(c => c.ConnectionId == fixture.Links["PSU/Supply"].ConnectionId);
            var groupedMember = selectionBase.Connections(psu.BlockId).Inspect(psuSupply).Members.Single(m => m.ConnectionId == convertedRails);
            var psuProposal = RecursiveBlockProposalTests.CreateFor(selectionBase, psuInput, psuSupply, groupedMember);
            psuProposal = psuProposal with { Origin = psuProposal.Origin with { Sources = [new("original-requirements.txt", "captured-2026-09", 1, null, null)] } };
            var psuPublished = await client.CallToolAsync("kicad_diagram_proposal_publish", new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = inputToken, ["operationId"] = Guid.NewGuid(),
                ["proposalJson"] = JsonSerializer.SerializeToElement(psuProposal, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            }, cancellationToken: token);
            if (psuPublished.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-psu-proposal-error.json"), JsonSerializer.Serialize(psuPublished), token);
            Assert.IsFalse(psuPublished.IsError == true);
            var psuPublishedData = JsonSerializer.SerializeToElement(psuPublished).GetProperty("structuredContent");
            Assert.IsTrue(psuPublishedData.GetProperty("added").GetBoolean());
            Assert.IsTrue(psuPublishedData.GetProperty("contextStillSelected").GetBoolean(), "No competing edit has advanced the refined supply block yet.");
            string proposalToken = psuPublishedData.GetProperty("sourceToken").GetString()!;
            var beforeChoiceGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual(selectionBase.SelectedRoot, beforeChoiceGraph.SelectedRoot, "Publishing a proposal must not activate it.");
            var psuRead = await client.CallToolAsync("kicad_diagram_proposal_read", new Dictionary<string, object?>(arguments)
                { ["expectedSourceToken"] = proposalToken, ["proposalId"] = psuProposal.Id }, cancellationToken: token);
            Assert.IsFalse(psuRead.IsError == true);
            Assert.AreEqual(psuProposal.Candidate.RevisionId.ToString("D"), JsonSerializer.SerializeToElement(psuRead).GetProperty("structuredContent")
                .GetProperty("proposal").GetProperty("candidate").GetProperty("revisionId").GetString());
            selectArguments["proposalId"] = psuProposal.Id; selectArguments["currentPath"] = new[] { selectionBase.SelectedRoot, psu };
            selectArguments["ancestorRevisionIds"] = new[] { Guid.NewGuid() }; selectArguments["expectedSourceToken"] = inputToken;
            // A token observed before the proposal was saved is outdated, even
            // though the refined block itself has not changed.
            var outdatedToken = (await RejectedSelection("outdated-token", "block_proposal_source_changed")).GetProperty("comparison");
            // The supply block itself has not changed since the proposal's input: nothing on today's side, only the proposal's changes.
            Assert.IsFalse(outdatedToken.GetProperty("stale").GetBoolean());
            Assert.AreEqual(0, outdatedToken.GetProperty("currentChanges").GetArrayLength());
            Assert.AreNotEqual(0, outdatedToken.GetProperty("proposalChanges").GetArrayLength());
            // A path through an older root revision is outdated even though that revision pins the same unchanged supply: the choice is
            // refused as stale, not as a damaged file, and the comparison gives today's path to the supply.
            var olderRoot = selectionBase.History(selectionBase.SelectedRoot.StateId).Select(r => r.Selection)
                .First(r => r != selectionBase.SelectedRoot && selectionBase.Inspect(r).Children.Contains(psu));
            selectArguments["expectedSourceToken"] = proposalToken; selectArguments["currentPath"] = new[] { olderRoot, psu };
            var outdatedPath = (await RejectedSelection("outdated-path", "stale_root_revision")).GetProperty("comparison");
            Assert.IsFalse(outdatedPath.GetProperty("stale").GetBoolean());
            Assert.AreEqual(0, outdatedPath.GetProperty("currentChanges").GetArrayLength());
            CollectionAssert.AreEqual(new[] { selectionBase.SelectedRoot, psu }, outdatedPath.GetProperty("currentPath").Deserialize<BlockSelection[]>(AgentJson),
                "The refusal names today's path to the target.");
            selectArguments["currentPath"] = new[] { selectionBase.SelectedRoot, psu };
            Guid appliedRootRevision = Guid.NewGuid(), applyOperation = Guid.NewGuid();
            selectArguments["expectedSourceToken"] = proposalToken; selectArguments["ancestorRevisionIds"] = new[] { appliedRootRevision };
            selectArguments["operationId"] = applyOperation;
            var applyResult = await client.CallToolAsync("kicad_diagram_proposal_select", selectArguments, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-proposal-selection.json"), JsonSerializer.Serialize(applyResult), token);
            Assert.IsFalse(applyResult.IsError == true, "Choosing a current whole-block proposal must succeed; its exact response is retained.");
            var applyData = JsonSerializer.SerializeToElement(applyResult).GetProperty("structuredContent");
            string appliedToken = applyData.GetProperty("sourceToken").GetString()!;
            Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source, token))), appliedToken);
            Assert.AreEqual(appliedRootRevision.ToString("D"), applyData.GetProperty("selectedRoot").GetProperty("revisionId").GetString());
            var applied = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var appliedRoot = new BlockSelection(beforeChoiceGraph.SelectedRoot.BlockId, beforeChoiceGraph.SelectedRoot.StateId, appliedRootRevision);
            Assert.AreEqual(appliedRoot, applied.SelectedRoot);
            Assert.AreEqual(beforeChoiceGraph.SelectedRoot.RevisionId, applied.Inspect(appliedRoot).ParentRevisionId);
            CollectionAssert.AreEqual(beforeChoiceGraph.Inspect(beforeChoiceGraph.SelectedRoot).Children.Select(c => c == psu ? psuProposal.Candidate : c).ToArray(),
                applied.Inspect(appliedRoot).Children.ToArray(), "Only the refined supply occurrence changes; its sibling keeps its exact revision.");
            CollectionAssert.IsSubsetOf(psuProposal.Blocks.Select(b => b.Selection).ToArray(), applied.Walk(appliedRoot).ToArray());
            Assert.HasCount(beforeChoiceGraph.Revisions.Length + 1, applied.Revisions, "Selection adds exactly one containing root snapshot.");
            CollectionAssert.IsSubsetOf(beforeChoiceGraph.Revisions.Select(r => r.Selection).ToArray(), applied.Revisions.Select(r => r.Selection).ToArray());
            CollectionAssert.AreEqual(beforeChoiceGraph.Walk(beforeChoiceGraph.SelectedRoot).ToArray(), applied.Walk(beforeChoiceGraph.SelectedRoot).ToArray());
            CollectionAssert.AreEquivalent(beforeChoiceGraph.States.Where(s => s.Id != appliedRoot.StateId).ToArray(), applied.States.Where(s => s.Id != appliedRoot.StateId).ToArray(),
                "The replaced supply implementation and every other implementation head remain available.");
            Assert.HasCount(beforeChoiceGraph.RequirementHistories.Length, applied.RequirementHistories);
            Assert.AreEqual(beforeChoiceGraph.Requirements(beforeChoiceGraph.SelectedRoot).Requirements, applied.Requirements(appliedRoot).Requirements);
            Assert.AreEqual(beforeChoiceGraph.Requirements(psu).Requirements, applied.Requirements(psu).Requirements);
            Assert.AreEqual("Main controller PCB", applied.Inspect(appliedRoot).PhysicalAllocation!.Targets[0].Name);
            Assert.IsTrue(electrical.Bindings.SameContents(applied.Inspect(appliedRoot).EffectiveComponentBindings));
            Assert.IsTrue(beforeChoiceGraph.Proposal(proposal.Id).SameContents(applied.Proposal(proposal.Id)), "The stale proposal stays available for comparison.");
            Assert.IsTrue(originalInput.SameContents(applied.RefinementInput(originalInput.Id)));
            Assert.IsTrue(psuInput.SameContents(applied.RefinementInput(psuInput.Id)));
            var appliedOrigin = applied.Inspect(appliedRoot).Origin;
            Assert.AreEqual(RequirementRevisionActor.Agent, appliedOrigin.ActorKind);
            CollectionAssert.IsSubsetOf(new[] { psuInput.Id, psuProposal.Id }, appliedOrigin.InputIds.ToArray());
            // Choosing the proposal keeps every field history (ledger p390b40bed99e0ab2), read back over MCP. The chosen supply
            // lists the agent's rewrite (with its source and the input and proposal it came from) and then every General text
            // the replaced implementation had; its refined supply connection does the same; the root keeps its own history.
            var psuGeneral = AllFieldEntries((offset, limit) => DiagramFieldHistoryQuery.Block(selectionBase, psu, DiagramRequirementField.General, offset, limit));
            var supplyGeneral = AllFieldEntries((offset, limit) => DiagramFieldHistoryQuery.Connection(selectionBase.Connections(psu.BlockId), psuSupply,
                DiagramRequirementField.General, offset, limit));
            var refinedSupply = psuProposal.Connections.Single(c => c.BasedOn == psuSupply);
            AssertFieldHistory(await FieldHistoryOverMcp(client, arguments, psuProposal.Candidate, "General", null, token),
                [ProposedFieldEntry(psuProposal, DiagramRequirementField.General), .. psuGeneral.Select(e => e with { IsSavedText = false })],
                "chosen supply proposal", Contexts(applied));
            AssertFieldHistory(await FieldHistoryOverMcp(client, arguments, psuProposal.Candidate, "General", refinedSupply.Selection, token),
                [ProposedFieldEntry(psuProposal, DiagramRequirementField.General, refinedSupply), .. supplyGeneral.Select(e => e with { IsSavedText = false })],
                "refined supply connection", Contexts(applied.Connections(psu.BlockId)));
            // The member the proposal refined inside that connection does the same: its rewrite, then the supply agent's text with its
            // datasheet source (page, table and part variant), saved in the member's earlier implementation.
            var refinedMember = psuProposal.Connections.Single(c => c.BasedOn == groupedMember);
            var memberGeneral = AllFieldEntries((offset, limit) => DiagramFieldHistoryQuery.Connection(selectionBase.Connections(psu.BlockId), groupedMember,
                DiagramRequirementField.General, offset, limit));
            Assert.AreEqual(("Supply agent", "Carry the converted rail and its return to the boundary."), (memberGeneral.Single().Origin.Actor, memberGeneral.Single().Text));
            Assert.AreEqual(datasheet, memberGeneral.Single().Origin.Sources.Single(), "The member's first text cites the datasheet the agent named.");
            AssertFieldHistory(await FieldHistoryOverMcp(client, arguments, psuProposal.Candidate, "General", refinedMember.Selection, token),
                [ProposedFieldEntry(psuProposal, DiagramRequirementField.General, refinedMember), .. memberGeneral.Select(e => e with { IsSavedText = false })],
                "refined supply member", Contexts(applied.Connections(psu.BlockId)));
            AssertFieldHistory(await FieldHistoryOverMcp(client, arguments, appliedRoot, "General", null, token),
                AllFieldEntries((offset, limit) => DiagramFieldHistoryQuery.Block(beforeChoiceGraph, beforeChoiceGraph.SelectedRoot,
                    DiagramRequirementField.General, offset, limit)), "root after the choice", Contexts(applied));
            Assert.AreEqual(selectionBase.Requirements(psu).RevisionId, applied.RequirementHistories.Single(h => h.Scope.DesignStateId == psuProposal.Candidate.StateId).DerivedFrom);
            var selectionReceipt = await client.CallToolAsync("kicad_diagram_proposal_publication", new Dictionary<string, object?>
                { ["instanceId"] = instanceId, ["expectedInstanceEpoch"] = native.Epoch, ["operationId"] = applyOperation }, cancellationToken: token);
            Assert.IsFalse(selectionReceipt.IsError == true);
            var receiptData = JsonSerializer.SerializeToElement(selectionReceipt).GetProperty("structuredContent").GetProperty("receipt");
            Assert.AreEqual("Select", receiptData.GetProperty("kind").GetString()); Assert.AreEqual("Published", receiptData.GetProperty("stage").GetString());
            Assert.AreEqual(proposalToken, receiptData.GetProperty("beforeSha256").GetString()); Assert.AreEqual(appliedToken, receiptData.GetProperty("afterSha256").GetString());
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-proposal-selection-receipt.json"), JsonSerializer.Serialize(selectionReceipt), token);
            Key("r", control: true);
            var nativeChoice = await Wait(s => !s.Busy && !s.Dirty && s.SourceToken == appliedToken);
            Assert.AreEqual(appliedRootRevision.ToString("D"), nativeChoice.DiagramPath.Single().RevisionId);
            Assert.AreEqual(appliedRootRevision.ToString("D"), nativeChoice.CanvasDiagram.Selection.RevisionId);
            Assert.AreEqual(psuProposal.Candidate.RevisionId.ToString("D"), nativeChoice.CanvasDiagram.Children.Single(c => c.BlockId == psu.BlockId.ToString("D")).RevisionId);
            Assert.AreEqual((uint)nativeChoice.CanvasDiagram.Children.Count, nativeChoice.ResolvedCanvasChildren, "The editor must load every chosen child revision.");
            Key("Escape"); Key("Right");
            var chosenSupply = await Wait(s => s.Draft.Baseline.BlockId == psu.BlockId.ToString("D"));
            Assert.AreEqual(psuProposal.Candidate.StateId.ToString("D"), chosenSupply.Draft.Baseline.StateId, "The native editor must show the chosen proposed implementation.");
            Assert.AreEqual(psuProposal.Candidate.RevisionId.ToString("D"), chosenSupply.Draft.Baseline.RevisionId);
            Key("Return");
            var insideChoice = await Wait(s => !s.Busy && s.Rendered && s.DiagramPath.Count == 2
                && s.DiagramPath[^1].RevisionId == psuProposal.Candidate.RevisionId.ToString("D"));
            Assert.HasCount(applied.Inspect(psuProposal.Candidate).Children.Length, insideChoice.CanvasDiagram.Children);
            Assert.AreEqual((uint)insideChoice.CanvasDiagram.Children.Count, insideChoice.ResolvedCanvasChildren);
            Assert.HasCount(applied.Inspect(psuProposal.Candidate).LocalDiagram.Connections.Length, insideChoice.CanvasDiagram.LocalDiagram.Connections);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-proposal-selected-state.json"), SchematicJson.Formatter.Format(insideChoice), token);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-proposal-selected.png"), token);
            // After selection the target is the candidate itself; choosing the
            // proposal again must not re-apply it over the now-current target.
            selectArguments["expectedSourceToken"] = appliedToken; selectArguments["expectedRoot"] = appliedRoot;
            selectArguments["currentPath"] = new[] { appliedRoot, psuProposal.Candidate }; selectArguments["ancestorRevisionIds"] = new[] { Guid.NewGuid() };
            var reselected = (await RejectedSelection("reselected-target", "proposal_target_changed")).GetProperty("comparison");
            Assert.IsTrue(reselected.GetProperty("candidateSelected").GetBoolean(), "The refusal says the candidate already is today's supply.");
            // The chosen proposal is adopted: its own changes are not reported again as today's changes or as conflicts.
            Assert.IsTrue(reselected.GetProperty("candidateAdopted").GetBoolean());
            Assert.IsFalse(reselected.GetProperty("stale").GetBoolean(), "An adopted proposal is not outdated.");
            Assert.AreEqual(0, reselected.GetProperty("currentChanges").GetArrayLength(), "Nothing changed after the choice.");
            Assert.AreEqual(0, reselected.GetProperty("changedOnBothSides").GetArrayLength(), "An adopted proposal conflicts with nothing.");
            Assert.AreNotEqual(0, reselected.GetProperty("proposalChanges").GetArrayLength(), "What the proposal changed stays listed.");
            // In the editor the chosen supply's General history continues across the switch: the row below the saved text is
            // the replaced implementation's text (labelled with that implementation's name and version). Using it and saving
            // makes a new revision of the chosen implementation that names the earlier revision the text came from.
            var earlierGeneral = psuGeneral[0];
            Assert.AreNotEqual(ProposedFieldEntry(psuProposal, DiagramRequirementField.General).Text, earlierGeneral.Text);
            await Wait(s => !s.Busy && s.ConnectionDraft is null && s.Draft.Baseline.RevisionId == psuProposal.Candidate.RevisionId.ToString("D"));
            Key("1", control: true); Key("h", alt: true);
            using (var modal = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                modal.CancelAfter(TimeSpan.FromSeconds(20));
                while (!NativeKeyboard.HasWindow(display, processId, "Requirement history")) await Task.Delay(50, modal.Token);
            }
            Key("Down", title: "Requirement history");
            // The list shows the rewrite as this implementation's saved version and every earlier text with the name of the
            // implementation it was saved in, in the implementation selector's "name · version" form, and its author.
            var psuHistoryDialog = await Wait(s => s.FieldHistory is { Loading: false }
                && s.FieldHistory.InspectedRevisionId == earlierGeneral.RequirementRevisionId.ToString("D"));
            Assert.AreEqual(psu.BlockId.ToString("D"), psuHistoryDialog.FieldHistory.OwnerId);
            CollectionAssert.AreEqual(new[] { $"v2 · Saved · {psuProposal.Origin.Actor}" }
                    .Concat(psuGeneral.Select(e => HistoryRowLabel(Contexts(selectionBase), e, psuProposal.Candidate.StateId))).ToArray(),
                psuHistoryDialog.FieldHistory.RowLabels.ToArray(), "The chosen supply's General history as the editor lists it.");
            var earlierPsuImplementation = Contexts(selectionBase)(earlierGeneral.ContextRevisionId);
            Assert.AreNotEqual(psuProposal.Candidate.StateId, earlierPsuImplementation.State);
            StringAssert.StartsWith(psuHistoryDialog.FieldHistory.RowLabels[1], earlierPsuImplementation.Name + " · v");
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-continued-field-history.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Requirement history", false, true, clickFromRight: 70, clickFromBottom: 30);
            await Wait(s => s.Dirty && s.Draft.Fields.General == earlierGeneral.Text
                && s.Draft.RestoredFields.Any(r => r.SourceRevisionId == earlierGeneral.RequirementRevisionId.ToString("D")));
            await Save();
            var continuedFile = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var continuedPsu = continuedFile.Inspect(continuedFile.SelectedRoot).Children.Single(c => c.BlockId == psu.BlockId);
            Assert.AreEqual(psuProposal.Candidate.StateId, continuedPsu.StateId, "The restore is saved in the chosen implementation.");
            var continuedHistory = continuedFile.RequirementHistories.Single(h => h.Scope.DesignStateId == continuedPsu.StateId);
            Assert.AreEqual(psuProposal.Blocks[0].RequirementRevisionId, continuedHistory.Current.ParentId);
            Assert.AreEqual(new RequirementFieldRestoration(DiagramRequirementField.General, earlierGeneral.RequirementRevisionId),
                continuedHistory.Current.Restorations.Single());
            Assert.AreEqual(psuProposal.Blocks[0].Requirements with { General = earlierGeneral.Text }, continuedHistory.Current.Requirements);
            // Edited after the choice, the proposal is still adopted: today's side is exactly what this save changed after the candidate
            // (its saved history comparison), and nothing is changed on both sides.
            var afterEdit = await client.CallToolAsync("kicad_diagram_proposal_compare", new Dictionary<string, object?>(arguments)
                { ["expectedSourceToken"] = await FileToken(source, token), ["proposalId"] = psuProposal.Id }, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-proposal-compare-after-edit.json"), JsonSerializer.Serialize(afterEdit), token);
            Assert.IsFalse(afterEdit.IsError == true, JsonSerializer.Serialize(afterEdit));
            var editedComparison = JsonSerializer.SerializeToElement(afterEdit).GetProperty("structuredContent").GetProperty("comparison");
            Assert.IsTrue(editedComparison.GetProperty("candidateAdopted").GetBoolean());
            Assert.IsFalse(editedComparison.GetProperty("candidateSelected").GetBoolean(), "Today's supply is a later revision of the candidate.");
            Assert.IsFalse(editedComparison.GetProperty("stale").GetBoolean());
            string[] editedKeys = [.. editedComparison.GetProperty("currentChanges").EnumerateArray().Select(c => ChangeKey(c) + "=" + c.GetProperty("kind").GetString())];
            CollectionAssert.AreEquivalent(DiagramHistoryQuery.Changes(continuedFile, psuProposal.Candidate, continuedPsu).Select(c => LevelChange([psu.BlockId], c))
                .Select(c => c.Key + "=" + c.Kind).ToArray(), editedKeys, "Only what changed after the choice.");
            CollectionAssert.Contains(editedKeys, ChangeKey([psu.BlockId], [], DiagramHistoryChangeCategory.Requirement, psu.BlockId, DiagramRequirementField.General, null) + "=Changed");
            Assert.AreEqual(0, editedComparison.GetProperty("changedOnBothSides").GetArrayLength());
            AssertFieldHistory(await FieldHistoryOverMcp(client, arguments, continuedPsu, "General", null, token),
                [new(continuedHistory.Current.Id, continuedPsu.RevisionId, 3, "PSU", earlierGeneral.Text, continuedHistory.Current.Origin, true),
                 ProposedFieldEntry(psuProposal, DiagramRequirementField.General) with { IsSavedText = false },
                 .. psuGeneral.Select(e => e with { IsSavedText = false })], "chosen supply after restoring the earlier text", Contexts(continuedFile));
            // The refined supply connection keeps its field history across the switch in the editor too. Selecting it in the
            // chosen supply's level and opening its General history lists the agent's rewrite and then every text the replaced
            // connection implementation had, each named after that implementation. Using the earlier text and saving makes a
            // new revision of the chosen connection implementation that names the revision the text came from.
            var earlierSupply = supplyGeneral[0];
            Assert.AreNotEqual(refinedSupply.Requirements.General, earlierSupply.Text);
            // Escape returns the keyboard to the canvas, where L selects the level's next connection.
            Key("Escape");
            await Wait(s => !s.Busy && s.ConnectionDraft is null && s.Draft.Baseline.BlockId == psu.BlockId.ToString("D")
                && s.FocusedControl == "RecursiveDiagramCanvas");
            var levelLinks = applied.Inspect(psuProposal.Candidate).LocalDiagram.Connections;
            Assert.IsTrue(levelLinks.Any(c => c.ConnectionId == psuSupply.ConnectionId));
            for (string? selectedLink = null; selectedLink != psuSupply.ConnectionId.ToString("D");)
            {
                string? previous = selectedLink; Key("l");
                selectedLink = (await Wait(s => s.ConnectionDraft is not null && s.ConnectionDraft.Baseline.ConnectionId != previous)).ConnectionDraft.Baseline.ConnectionId;
                Assert.IsTrue(levelLinks.Any(c => c.ConnectionId.ToString("D") == selectedLink), "L selects a connection of the viewed level.");
            }
            var supplySelected = await Wait(s => s.ConnectionDraft?.Baseline.ConnectionId == psuSupply.ConnectionId.ToString("D"));
            Assert.AreEqual(refinedSupply.Selection.RevisionId.ToString("D"), supplySelected.ConnectionDraft.Baseline.RevisionId,
                "The editor shows the chosen connection implementation.");
            Key("1", control: true); Key("h", alt: true);
            using (var modal = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                modal.CancelAfter(TimeSpan.FromSeconds(20));
                while (!NativeKeyboard.HasWindow(display, processId, "Requirement history")) await Task.Delay(50, modal.Token);
            }
            Key("Down", title: "Requirement history");
            var supplyHistoryDialog = await Wait(s => s.FieldHistory is { Loading: false }
                && s.FieldHistory.InspectedRevisionId == earlierSupply.RequirementRevisionId.ToString("D"));
            Assert.AreEqual(psuSupply.ConnectionId.ToString("D"), supplyHistoryDialog.FieldHistory.OwnerId);
            var replacedSupply = selectionBase.Connections(psu.BlockId);
            CollectionAssert.AreEqual(new[] { $"v2 · Saved · {psuProposal.Origin.Actor}" }
                    .Concat(supplyGeneral.Select(e => HistoryRowLabel(Contexts(replacedSupply), e, refinedSupply.Selection.StateId))).ToArray(),
                supplyHistoryDialog.FieldHistory.RowLabels.ToArray(), "The refined supply connection's General history as the editor lists it.");
            var earlierSupplyImplementation = Contexts(replacedSupply)(earlierSupply.ContextRevisionId);
            Assert.AreNotEqual(refinedSupply.Selection.StateId, earlierSupplyImplementation.State);
            StringAssert.StartsWith(supplyHistoryDialog.FieldHistory.RowLabels[1], earlierSupplyImplementation.Name + " · v");
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-continued-connection-field-history.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Requirement history", false, true, clickFromRight: 70, clickFromBottom: 30);
            await Wait(s => s.Dirty && s.ConnectionDraft?.Fields.General == earlierSupply.Text
                && s.ConnectionDraft.RestoredFields.Any(r => r.SourceRevisionId == earlierSupply.RequirementRevisionId.ToString("D")));
            await Save();
            var connectionRestoreFile = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var connectionRestorePsu = connectionRestoreFile.Inspect(connectionRestoreFile.SelectedRoot).Children.Single(c => c.BlockId == psu.BlockId);
            Assert.AreEqual(psuProposal.Candidate.StateId, connectionRestorePsu.StateId);
            var restoredSupply = connectionRestoreFile.Inspect(connectionRestorePsu).LocalDiagram.Connections.Single(c => c.ConnectionId == psuSupply.ConnectionId);
            Assert.AreEqual(refinedSupply.Selection.StateId, restoredSupply.StateId, "The restore is saved in the chosen connection implementation.");
            var supplyArchive = connectionRestoreFile.Connections(psu.BlockId);
            var restoredSupplyHistory = supplyArchive.RequirementHistories.Single(h => h.Scope.DesignStateId == restoredSupply.StateId);
            Assert.AreEqual(supplyArchive.Inspect(restoredSupply).RequirementRevisionId, restoredSupplyHistory.Current.Id);
            Assert.AreEqual(refinedSupply.RequirementRevisionId, restoredSupplyHistory.Current.ParentId);
            Assert.AreEqual(new RequirementFieldRestoration(DiagramRequirementField.General, earlierSupply.RequirementRevisionId),
                restoredSupplyHistory.Current.Restorations.Single());
            Assert.AreEqual(refinedSupply.Requirements with { General = earlierSupply.Text }, restoredSupplyHistory.Current.Requirements);
            Assert.AreEqual(replacedSupply.Requirements(psuSupply).RevisionId, restoredSupplyHistory.DerivedFrom,
                "The chosen connection implementation still continues the exact revision it was refined from.");
            AssertFieldHistory(await FieldHistoryOverMcp(client, arguments, connectionRestorePsu, "General", restoredSupply, token),
                [new(restoredSupplyHistory.Current.Id, restoredSupply.RevisionId, 3, refinedSupply.Name, earlierSupply.Text, restoredSupplyHistory.Current.Origin, true),
                 ProposedFieldEntry(psuProposal, DiagramRequirementField.General, refinedSupply) with { IsSavedText = false },
                 .. supplyGeneral.Select(e => e with { IsSavedText = false })], "refined supply connection after restoring the earlier text",
                Contexts(supplyArchive));
            // The refined member's text from before the switch is restored once. The editor lists a connection's members only in its
            // Signals row, so the restore is saved through the diagram companion (the process the editor saves through), as a
            // member draft: a new revision of the chosen member implementation naming the revision the text came from, read back
            // over MCP first in the member's history, followed by everything it continues.
            var memberFile = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var memberLevel = memberFile.Inspect(memberFile.SelectedRoot).Children.Single(c => c.BlockId == psu.BlockId);
            var memberLinks = memberFile.Connections(psu.BlockId);
            var supplyOfMember = memberFile.Inspect(memberLevel).LocalDiagram.Connections.Single(c => c.ConnectionId == psuSupply.ConnectionId);
            var memberToday = memberLinks.Inspect(supplyOfMember).Members.Single(m => m.ConnectionId == convertedRails);
            Assert.AreEqual(refinedMember.Selection, memberToday, "The chosen supply connection pins the proposal's member implementation.");
            var earlierMember = memberGeneral.Single();
            var memberDraft = memberLinks.StartDraft(memberToday);
            memberDraft = memberDraft with { Requirements = memberLinks.RequirementHistories.Single(h => h.Scope.DesignStateId == memberToday.StateId)
                .RestoreField(memberDraft.Requirements, earlierMember.RequirementRevisionId, DiagramRequirementField.General) };
            var memberSave = new P.SaveConnectionDraftData { ExpectedRoot = RecursiveBlockCodec.EncodeSelection(memberFile.SelectedRoot),
                Draft = RecursiveBlockCodec.Encode(memberDraft), NewConnectionRevisionId = Guid.NewGuid().ToString("D"),
                NewRequirementRevisionId = Guid.NewGuid().ToString("D"), NewBlockRevisionId = Guid.NewGuid().ToString("D"),
                NewBlockRequirementRevisionId = Guid.NewGuid().ToString("D"), Origin = RecursiveBlockCodec.EncodeOrigin(RecursiveBlockFixture.Origin("Fixture user")) };
            memberSave.BlockPath.Add(new[] { memberFile.SelectedRoot, memberLevel }.Select(RecursiveBlockCodec.EncodeSelection));
            memberSave.ConnectionPath.Add(new[] { supplyOfMember, memberToday }.Select(RecursiveBlockCodec.EncodeSelection));
            memberSave.BlockAncestorRevisionIds.Add(Guid.NewGuid().ToString("D")); memberSave.ConnectionAncestorRevisionIds.Add(Guid.NewGuid().ToString("D"));
            var memberRestored = await RecursiveEditorFileCommandTests.Invoke(new P.RecursiveFileRequest { SchemaVersion = RecursiveBlockCodec.SchemaVersion,
                Action = P.RecursiveFileAction.RfaSaveConnection, RepositoryRoot = project, SourcePath = source, DocumentId = graph.DocumentId.ToString("D"),
                ExpectedSourceToken = await FileToken(source, token), SaveConnection = memberSave });
            Assert.IsTrue(memberRestored.Success, memberRestored.ErrorCode + ": " + memberRestored.ErrorMessage);
            var memberRestoreFile = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var memberRestoreLevel = memberRestoreFile.Inspect(memberRestoreFile.SelectedRoot).Children.Single(c => c.BlockId == psu.BlockId);
            var memberRestoreLinks = memberRestoreFile.Connections(psu.BlockId);
            var restoredMember = memberRestoreLinks.Inspect(memberRestoreFile.Inspect(memberRestoreLevel).LocalDiagram.Connections
                .Single(c => c.ConnectionId == psuSupply.ConnectionId)).Members.Single(m => m.ConnectionId == convertedRails);
            Assert.AreEqual(refinedMember.Selection.StateId, restoredMember.StateId, "The restore is saved in the chosen member implementation.");
            var restoredMemberHistory = memberRestoreLinks.RequirementHistories.Single(h => h.Scope.DesignStateId == restoredMember.StateId);
            Assert.AreEqual(refinedMember.RequirementRevisionId, restoredMemberHistory.Current.ParentId);
            Assert.AreEqual(new RequirementFieldRestoration(DiagramRequirementField.General, earlierMember.RequirementRevisionId),
                restoredMemberHistory.Current.Restorations.Single());
            Assert.AreEqual(refinedMember.Requirements with { General = earlierMember.Text }, restoredMemberHistory.Current.Requirements);
            AssertFieldHistory(await FieldHistoryOverMcp(client, arguments, memberRestoreLevel, "General", restoredMember, token),
                [new(restoredMemberHistory.Current.Id, restoredMember.RevisionId, 3, refinedMember.Name, earlierMember.Text, restoredMemberHistory.Current.Origin, true),
                 ProposedFieldEntry(psuProposal, DiagramRequirementField.General, refinedMember) with { IsSavedText = false },
                 earlierMember with { IsSavedText = false }], "refined supply member after restoring the earlier text", Contexts(memberRestoreLinks));
            // Ledger pa48933d0fe0a5c2f: the user keeps an unsaved edit open while an agent's calls are cancelled and the agent comes back
            // through a reattached server. Each operation keeps one record, and the open draft is neither saved, replaced nor lost.
            // It runs once per themed session; the second project only proves instance isolation.
            if (sessionWide)
            {
                const string unsavedText = "Unsaved text kept while an agent reconnects.";
                Key("1", control: true); Key("a", control: true); Type(unsavedText);
                var dirtyBefore = await Wait(s => s.Dirty && s.ConnectionDraft?.Fields.General == unsavedText);
                await VerifyAgentReattachment(native, stateRoot, source, project, graph.DocumentId, fixture.Blocks["CPU"].BlockId, instanceId, evidence, token);
                var dirtyAfter = await Read();
                Assert.IsTrue(dirtyAfter.Dirty, "The user's unsaved edit is still open.");
                Assert.AreEqual(dirtyBefore.SourceToken, dirtyAfter.SourceToken, "The editor was not reloaded under the unsaved edit.");
                Assert.AreEqual(dirtyBefore.Draft, dirtyAfter.Draft); Assert.AreEqual(dirtyBefore.ConnectionDraft, dirtyAfter.ConnectionDraft);
                Assert.AreEqual(dirtyBefore.DiagramPath, dirtyAfter.DiagramPath);
                Key("d", alt: true); await Wait(s => !s.Busy && !s.Dirty);
            }
            Key("w", control: true);
            if (createdDiagram is not null)
                await VerifyCreatedDiagramOpensInTheEditor(client, native, processId, display, createdDiagram, instanceId, evidence, token);
            else
            {
                using var closed = CancellationTokenSource.CreateLinkedTokenSource(token); closed.CancelAfter(TimeSpan.FromSeconds(15));
                while (NativeKeyboard.HasWindow(display, processId, "Structural diagram")) await Task.Delay(50, closed.Token);
            }
        }
        finally { Directory.Delete(stateRoot, true); }
        if (interactionFailures.Count != 0) throw new AggregateException("Native input failures were preserved; the remaining safe editor journey was exercised.", interactionFailures);
    }

    /// <summary>Schema 2 through the real MCP server and native instance (contract rbg-v2 sections 2.4, 8 and 12):
    /// an agent reads a schema 2 level's layout, realizations, domains and directions; a changed write
    /// upgrades a version 1 file and reports it (R4); and the native editor, which speaks schema 2, opens
    /// such a document with every fact of the viewed level in its draft and writes nothing until a save.</summary>
    private static async Task VerifySchemaTwoDiagramTools(McpClient client, NativeClient native, int processId, string display, string project,
        string instanceId, string evidence, CancellationToken token)
    {
        var f = SchemaTwoFixture.Create(); var psu = f.Linked.Blocks["PSU"];
        string layered = Path.Combine(project, "system.schema-two.design.xml"), layeredXml = RecursiveBlockGraphXml.Write(f.Graph);
        await File.WriteAllTextAsync(layered, layeredXml, token);
        var target = new Dictionary<string, object?> { ["instanceId"] = instanceId, ["repositoryRoot"] = project, ["sourcePath"] = layered,
            ["documentId"] = f.Graph.DocumentId.ToString("D") };
        var levelRead = await client.CallToolAsync("kicad_diagram_read", new Dictionary<string, object?>(target)
            { ["blockId"] = psu.BlockId.ToString("D"), ["stateId"] = psu.StateId.ToString("D"), ["revisionId"] = psu.RevisionId.ToString("D") }, cancellationToken: token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-schema-two-level-read.json"), JsonSerializer.Serialize(levelRead), token);
        Assert.IsFalse(levelRead.IsError == true, "A schema 2 level must be readable by agents.");
        var level = JsonSerializer.SerializeToElement(levelRead).GetProperty("structuredContent");
        Assert.AreEqual(2, level.GetProperty("storedSchemaVersion").GetInt32());
        var local = level.GetProperty("block").GetProperty("localDiagram");
        Assert.AreEqual("diagram-unit", local.GetProperty("presentation").GetProperty("units").GetString());
        Assert.AreEqual("1200", local.GetProperty("presentation").GetProperty("frame").GetProperty("width").GetString());
        Assert.AreEqual("101.6", local.GetProperty("presentation").GetProperty("blocks")[0].GetProperty("rect").GetProperty("x").GetString());
        var power = local.GetProperty("interfaces").EnumerateArray().Single(i => i.GetProperty("id").GetString() == f.Linked.Ports["PSU/Power"].ToString("D"));
        Assert.AreEqual("DD_POWER", power.GetProperty("domain").GetString()); Assert.AreEqual("DIDR_OUTPUT", power.GetProperty("direction").GetString());
        Assert.AreEqual("DRS_PARTIAL", local.GetProperty("interfaceRealizations")[0].GetProperty("state").GetString());
        Assert.AreEqual(2, local.GetProperty("interfaceRealizations")[0].GetProperty("targets").GetArrayLength());
        var rootRead = await client.CallToolAsync("kicad_diagram_read", target, cancellationToken: token);
        Assert.IsFalse(rootRead.IsError == true);
        var link = JsonSerializer.SerializeToElement(rootRead).GetProperty("structuredContent").GetProperty("connections").EnumerateArray()
            .Select(c => c.GetProperty("revision")).Single(r => r.GetProperty("selection").GetProperty("connectionId").GetString() == f.Linked.Links["System/Power"].ConnectionId.ToString("D"));
        Assert.AreEqual("DD_POWER", link.GetProperty("domain").GetString()); Assert.AreEqual("DCDR_FROM_FIRST", link.GetProperty("direction").GetString());
        Assert.AreEqual("DRS_RESOLVED", link.GetProperty("realization").GetProperty("state").GetString());
        Assert.AreEqual(5, link.GetProperty("realization").GetProperty("segments").GetArrayLength());
        Assert.AreEqual(4, link.GetProperty("realization").GetProperty("joins").GetArrayLength());
        // A changed write (here a harness target, which only schema 2 can hold) upgrades a version 1 file and reports the upgrade.
        var plain = LinkedDiagramFixture.Create().Graph;
        string upgradedPath = Path.Combine(project, "system.upgrade.design.xml");
        await File.WriteAllTextAsync(upgradedPath, RecursiveBlockGraphXml.Write(plain, 1), token); // A version 1 file as an earlier build stored it.
        var plainTarget = new Dictionary<string, object?>(target) { ["sourcePath"] = upgradedPath, ["documentId"] = plain.DocumentId.ToString("D") };
        var before = JsonSerializer.SerializeToElement(await client.CallToolAsync("kicad_diagram_read", plainTarget, cancellationToken: token)).GetProperty("structuredContent");
        Assert.AreEqual(1, before.GetProperty("storedSchemaVersion").GetInt32());
        var harness = new BlockPhysicalAllocation(PhysicalAllocationState.Partial,
            [new PhysicalAllocationTarget(Guid.NewGuid(), PhysicalAllocationKind.Harness, "Test-only supply harness")], "Connector pins are not chosen yet.");
        var allocation = await client.CallToolAsync("kicad_diagram_physical_allocation_set", new Dictionary<string, object?>(plainTarget)
        {
            ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = before.GetProperty("sourceToken").GetString(),
            ["expectedRoot"] = plain.SelectedRoot, ["blockPath"] = new[] { plain.SelectedRoot },
            ["allocation"] = JsonSerializer.SerializeToElement(harness, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }),
            ["operationId"] = Guid.NewGuid(), ["actor"] = "Schema 2 allocation agent fixture"
        }, cancellationToken: token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-schema-two-upgrade.json"), JsonSerializer.Serialize(allocation), token);
        Assert.IsFalse(allocation.IsError == true);
        var upgraded = JsonSerializer.SerializeToElement(allocation).GetProperty("structuredContent");
        Assert.IsTrue(upgraded.GetProperty("changed").GetBoolean());
        Assert.AreEqual(1, upgraded.GetProperty("upgradedFromSchemaVersion").GetInt32(), "The silent version 1 to 2 upgrade is reported.");
        string upgradedXml = await File.ReadAllTextAsync(upgradedPath, token);
        Assert.AreEqual(2, RecursiveBlockGraphXml.ReadVersioned(upgradedXml).StoredSchemaVersion);
        var after = JsonSerializer.SerializeToElement(await client.CallToolAsync("kicad_diagram_read", plainTarget, cancellationToken: token)).GetProperty("structuredContent");
        Assert.AreEqual(2, after.GetProperty("storedSchemaVersion").GetInt32());
        Assert.AreEqual("PAK_HARNESS", after.GetProperty("block").GetProperty("physicalAllocation").GetProperty("targets")[0].GetProperty("kind").GetString());
        // R4 also for a write version 1 could hold: a definition choice on a fresh version 1 copy stores schema 2 and reports the upgrade.
        string definedPath = Path.Combine(project, "system.upgrade-definition.design.xml"), definedV1 = RecursiveBlockGraphXml.Write(plain, 1);
        await File.WriteAllTextAsync(definedPath, definedV1, token);
        Assert.AreEqual(1, RecursiveBlockGraphXml.RequiredSchemaVersion(plain), "The fixture holds no schema 2 fact.");
        var definedTarget = new Dictionary<string, object?>(target) { ["sourcePath"] = definedPath, ["documentId"] = plain.DocumentId.ToString("D") };
        var definedBefore = JsonSerializer.SerializeToElement(await client.CallToolAsync("kicad_diagram_read", definedTarget, cancellationToken: token)).GetProperty("structuredContent");
        Assert.AreEqual(1, definedBefore.GetProperty("storedSchemaVersion").GetInt32());
        var definition = await client.CallToolAsync("kicad_diagram_definition_set", new Dictionary<string, object?>(definedTarget)
        {
            ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = definedBefore.GetProperty("sourceToken").GetString(),
            ["expectedRoot"] = plain.SelectedRoot, ["blockPath"] = new[] { plain.SelectedRoot },
            ["definition"] = JsonSerializer.SerializeToElement(RecursiveBlockDefinitionTests.Partial(), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ["operationId"] = Guid.NewGuid(), ["actor"] = "Version 1 definition agent fixture"
        }, cancellationToken: token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-schema-two-definition-upgrade.json"), JsonSerializer.Serialize(definition), token);
        Assert.IsFalse(definition.IsError == true);
        var definedResult = JsonSerializer.SerializeToElement(definition).GetProperty("structuredContent");
        Assert.IsTrue(definedResult.GetProperty("changed").GetBoolean());
        Assert.AreEqual(1, definedResult.GetProperty("upgradedFromSchemaVersion").GetInt32(), "A change version 1 could hold still upgrades the file.");
        string definedXml = await File.ReadAllTextAsync(definedPath, token);
        var (definedGraph, definedVersion) = RecursiveBlockGraphXml.ReadVersioned(definedXml);
        Assert.AreEqual(2, definedVersion);
        Assert.AreEqual(1, RecursiveBlockGraphXml.RequiredSchemaVersion(definedGraph), "The stored content itself still needs no schema 2 fact.");
        Assert.IsTrue(RecursiveBlockDefinitionTests.Partial().SameContents(definedGraph.Inspect(definedGraph.SelectedRoot).EffectiveDefinition));
        var definedAfter = JsonSerializer.SerializeToElement(await client.CallToolAsync("kicad_diagram_read", definedTarget, cancellationToken: token)).GetProperty("structuredContent");
        Assert.AreEqual(2, definedAfter.GetProperty("storedSchemaVersion").GetInt32());
        Assert.AreEqual(definedResult.GetProperty("sourceToken").GetString(), definedAfter.GetProperty("sourceToken").GetString());
        // The native editor speaks schema 2 (contract rbg-v2 section 12): it opens a schema 2 document with its layout and
        // realizations kept, and opening, observing and closing a clean window write nothing.
        foreach (var (path, documentId, bytes) in new[] { (layered, f.Graph.DocumentId, layeredXml), (upgradedPath, plain.DocumentId, upgradedXml) })
        {
            var open = await client.CallToolAsync("kicad_diagram_open", new Dictionary<string, object?>(target) { ["sourcePath"] = path,
                ["documentId"] = documentId.ToString("D") }, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-schema-two-open-" + Path.GetFileNameWithoutExtension(path) + ".json"),
                JsonSerializer.Serialize(open), token);
            Assert.IsFalse(open.IsError == true, "The schema 2 editor opens a schema 2 document.");
            P.RecursiveDiagramEditorState state = new();
            using (var ready = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                ready.CancelAfter(TimeSpan.FromSeconds(20));
                while (true)
                {
                    state = await native.InvokeAsync<P.ReadRecursiveDiagramEditor, P.RecursiveDiagramEditorState>(new() { DocumentId = documentId.ToString("D") }, token);
                    if (state.Ready && !state.Busy && state.Rendered) break;
                    await Task.Delay(50, ready.Token);
                }
            }
            Assert.AreEqual("", state.ErrorMessage); Assert.IsFalse(state.Dirty);
            Assert.AreEqual(2U, state.StoredSchemaVersion); Assert.IsTrue(state.SourceWritable);
            Assert.AreEqual(state.DiagramPath.Single().RevisionId, state.LevelDraft.Scope.Baseline.RevisionId);
            var saved = RecursiveBlockGraphXml.Read(bytes);
            // Every schema 2 fact of the viewed level reaches the editor's level draft unchanged.
            var draftLocal = state.LevelDraft.Scope.LocalDiagram ?? new P.BlockLocalDiagramData();
            var savedLocal = RecursiveBlockCodec.Encode(saved).Revisions.Single(r => r.Selection.RevisionId == saved.SelectedRoot.RevisionId.ToString("D")).LocalDiagram
                ?? new P.BlockLocalDiagramData();
            Assert.AreEqual(savedLocal, draftLocal);
            NativeKeyboard.SchematicShortcut(display, processId, "w", "Structural diagram", true, false);
            using (var closing = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                closing.CancelAfter(TimeSpan.FromSeconds(15));
                while (NativeKeyboard.HasWindow(display, processId, "Structural diagram")) await Task.Delay(50, closing.Token);
            }
            Assert.AreEqual(bytes, await File.ReadAllTextAsync(path, token), "Opening and closing a clean schema 2 window writes nothing.");
        }
    }

    /// <summary>Flat structural diagrams are discarded, not converted (owner decision n9af098253fec71da), so the per-level
    /// editor is the only diagram editor. The production MCP server attached to this live session advertises no flat editor
    /// tool and no conversion tool, and the live project manager has no handler for the flat editor's native commands. The
    /// same raw probe reaches the per-level editor's handler, so the refusal is the missing flat editor, not the probe.</summary>
    private static async Task VerifyFlatEditorRetired(McpClient client, NativeClient native, string instanceId, string evidence,
        CancellationToken token)
    {
        string[] tools = [.. (await client.ListToolsAsync(cancellationToken: token)).Select(tool => tool.Name).Order(StringComparer.Ordinal)];
        await File.WriteAllLinesAsync(Path.Combine(evidence, instanceId + "-mcp-tools.txt"), tools, token);
        Assert.IsFalse(tools.Any(name => name.StartsWith("kicad_structure_", StringComparison.Ordinal)), string.Join(", ", tools));
        CollectionAssert.DoesNotContain(tools, "kicad_diagram_migrate");
        foreach (string tool in new[] { "kicad_diagram_create", "kicad_diagram_discover", "kicad_diagram_open", "kicad_diagram_state" })
            CollectionAssert.Contains(tools, tool);
        var transport = new NngTransport();
        async Task<Kiapi.Common.ApiResponse> Probe(string type)
        {
            var request = new Kiapi.Common.ApiRequest
            {
                Header = new Kiapi.Common.ApiRequestHeader { KicadToken = native.Epoch, ClientName = "kicad-automation-flat-editor-probe" },
                Message = new Google.Protobuf.WellKnownTypes.Any { TypeUrl = "type.googleapis.com/" + type }
            };
            var reply = Kiapi.Common.ApiResponse.Parser.ParseFrom(await transport.ExchangeAsync(native.Endpoint,
                Google.Protobuf.MessageExtensions.ToByteArray(request), TimeSpan.FromSeconds(15), token));
            await File.AppendAllTextAsync(Path.Combine(evidence, instanceId + "-flat-editor-probe.txt"),
                $"{type}: {reply.Status?.Status} {reply.Status?.ErrorMessage}{Environment.NewLine}", token);
            Assert.AreEqual(native.Epoch, reply.Header?.KicadToken, type + ": the reply must come from the same live manager");
            return reply;
        }
        foreach (string type in new[] { "kiapi.automation.structure.v1.OpenStructuralEditor", "kiapi.automation.structure.v1.ReadStructuralEditor" })
        {
            var reply = await Probe(type);
            Assert.AreEqual(Kiapi.Common.ApiStatusCode.AsUnhandled, reply.Status.Status, type + ": " + reply.Status.ErrorMessage);
            Assert.AreEqual("no handler available for request of type " + type, reply.Status.ErrorMessage);
        }
        var perLevel = await Probe(P.ReadRecursiveDiagramEditor.Descriptor.FullName);
        Assert.AreEqual(Kiapi.Common.ApiStatusCode.AsBadRequest, perLevel.Status.Status, perLevel.Status.ErrorMessage);
        Assert.AreEqual("The explicitly identified recursive diagram is not open", perLevel.Status.ErrorMessage);
    }

    /// <summary>The diagram <see cref="VerifyDiagramCreationOverMcp"/> created, for opening it in the editor.</summary>
    private sealed record CreatedDiagram(string RepositoryRoot, string Path, string DocumentId, string SourceToken, BlockSelection Root, string Caption);

    /// <summary>Creating a new system diagram next to the live session's project through the production MCP server
    /// (contract rbg-v2 section 8, owner decision n98a3f3c41084f0ed): discovery finds the project's existing diagram and the
    /// conventional new path; create writes a root that is only its caption, once; a retry observes it; and an existing file,
    /// bad paths, a blank caption, a changed epoch and an unwritable folder are refused without writing any diagram file.</summary>
    private static async Task<CreatedDiagram> VerifyDiagramCreationOverMcp(McpClient client, NativeClient native, string existingSource,
        RecursiveBlockGraph existing, string instanceId, string evidence, CancellationToken token)
    {
        var session = await native.HandshakeAsync(token);
        string projectFile = session.ProjectPath, directory = Path.GetDirectoryName(projectFile)!, epoch = session.Epoch;
        int step = 0;
        async Task<JsonElement> Call(string tool, Dictionary<string, object?> arguments, bool expectError = false)
        {
            var result = await client.CallToolAsync(tool, arguments, cancellationToken: token);
            string text = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
            await File.WriteAllTextAsync(Path.Combine(evidence, $"{instanceId}-diagram-create-{++step:D2}-{tool}.json"), text, token);
            Assert.AreEqual(expectError, result.IsError == true, tool + ": " + text);
            return JsonSerializer.SerializeToElement(result).GetProperty("structuredContent");
        }
        // Diagram files and any staged or refused creation file; the native session's own project, settings and lock files are not diagrams.
        string locked = Path.Combine(directory, "locked-diagrams");
        Dictionary<string, byte[]> Diagrams() => Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetFileName(f).Contains(".xml", StringComparison.Ordinal) || Path.GetFileName(f).Contains("system-diagram", StringComparison.Ordinal))
            .Concat(Directory.Exists(locked) ? Directory.EnumerateFiles(locked, "*", SearchOption.AllDirectories) : [])
            .ToDictionary(f => f, File.ReadAllBytes);
        void Unchanged(Dictionary<string, byte[]> before, string name)
        {
            var after = Diagrams();
            CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray(), name + ": no diagram file may appear or disappear");
            foreach (var (path, bytes) in before) CollectionAssert.AreEqual(bytes, after[path], name + ": " + path);
        }
        async Task Refused(string name, Dictionary<string, object?> arguments, string code)
        {
            var before = Diagrams();
            var error = await Call("kicad_diagram_create", arguments, expectError: true);
            Assert.AreEqual(code, error.GetProperty("code").GetString(), name);
            Unchanged(before, name);
        }

        // Opening the project finds its existing diagram by content and suggests the conventional new path.
        var discovered = (await Call("kicad_diagram_discover", new() { ["instanceId"] = instanceId, ["projectFile"] = projectFile })).GetProperty("discovery");
        string target = Path.Combine(directory, Path.GetFileNameWithoutExtension(projectFile) + ".system-diagram.xml");
        Assert.AreEqual(target, discovered.GetProperty("suggestedNewPath").GetString());
        Assert.IsFalse(discovered.TryGetProperty("suggestedPathExists", out _), "Protobuf JSON omits false: the conventional path is free.");
        string root = discovered.GetProperty("repositoryRoot").GetString()!;
        Assert.IsTrue(directory == root || directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal), root);
        var present = discovered.GetProperty("diagrams").EnumerateArray().Single(d => d.GetProperty("path").GetString() == existingSource);
        Assert.AreEqual("DDS_READY", present.GetProperty("status").GetString());
        Assert.AreEqual(existing.DocumentId.ToString("D"), present.GetProperty("documentId").GetString());
        Assert.AreEqual(existing.Inspect(existing.SelectedRoot).Name, present.GetProperty("rootName").GetString());
        Assert.IsFalse(present.TryGetProperty("conventional", out _));
        Assert.IsFalse(discovered.TryGetProperty("flatDiagrams", out _), "Legacy flat diagrams are neither listed nor converted.");

        // Create: the new diagram's root is only its caption, stored in format 2.
        const string caption = "Fixture board";
        Guid operation = Guid.NewGuid();
        Dictionary<string, object?> Creation(Guid op, string path, string rootName = caption) => new()
        {
            ["instanceId"] = instanceId, ["expectedInstanceEpoch"] = epoch, ["repositoryRoot"] = root, ["sourcePath"] = path,
            ["rootName"] = rootName, ["operationId"] = op, ["actor"] = "Diagram creation journey"
        };
        var created = await Call("kicad_diagram_create", Creation(operation, target));
        Assert.IsTrue(created.GetProperty("created").GetBoolean());
        Assert.AreEqual(target, created.GetProperty("sourcePath").GetString());
        Assert.AreEqual(2, created.GetProperty("storedSchemaVersion").GetInt32());
        string documentId = created.GetProperty("documentId").GetString()!, createdToken = created.GetProperty("sourceToken").GetString()!;
        Assert.AreEqual(DiagramIdentity.Derive(operation, "document").ToString("D"), documentId);
        byte[] stored = await File.ReadAllBytesAsync(target, token);
        Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stored)), createdToken);
        Assert.IsFalse(Directory.EnumerateFiles(directory, "*.initial-*").Any(), "The staged file is moved into place, never left behind.");
        var (graph, version) = RecursiveBlockGraphXml.ReadVersioned(System.Text.Encoding.UTF8.GetString(stored));
        Assert.AreEqual(2, version);
        var rootRevision = graph.Inspect(graph.SelectedRoot);
        Assert.AreEqual(DiagramIdentity.Derive(operation, "root-block"), graph.SelectedRoot.BlockId);
        Assert.AreEqual(graph.SelectedRoot.BlockId.ToString("D"), created.GetProperty("selectedRoot").GetProperty("blockId").GetString());
        Assert.AreEqual("Initial", graph.States.Single().Name);
        Assert.AreEqual(caption, rootRevision.Name); Assert.IsNull(rootRevision.ParentRevisionId); Assert.IsEmpty(rootRevision.Children);
        Assert.IsTrue(rootRevision.LocalDiagram.SameContents(BlockLocalDiagram.Empty), "No interfaces, connections, notes or layout.");
        Assert.IsNull(rootRevision.Definition); Assert.IsNull(rootRevision.ComponentBindings); Assert.IsNull(rootRevision.PhysicalAllocation);
        Assert.AreEqual(DiagramRequirements.Empty, graph.Requirements(graph.SelectedRoot).Requirements, "The caption is the only definition so far.");
        Assert.HasCount(1, graph.Revisions); Assert.HasCount(1, graph.RequirementHistories);
        Assert.AreEqual(RequirementRevisionActor.Agent, rootRevision.Origin.ActorKind);
        Assert.AreEqual("Diagram creation journey", rootRevision.Origin.Actor);
        CollectionAssert.Contains(rootRevision.Origin.InputIds.ToArray(), operation);

        // Agents read it like any other diagram: a caption with nothing else defined yet.
        var read = await Call("kicad_diagram_read", new() { ["instanceId"] = instanceId, ["repositoryRoot"] = root, ["sourcePath"] = target, ["documentId"] = documentId });
        Assert.AreEqual(2, read.GetProperty("storedSchemaVersion").GetInt32()); Assert.IsTrue(read.GetProperty("sourceWritable").GetBoolean());
        Assert.AreEqual(caption, read.GetProperty("block").GetProperty("name").GetString());
        Assert.AreEqual(0, read.GetProperty("children").GetArrayLength()); Assert.AreEqual(0, read.GetProperty("connections").GetArrayLength());
        Assert.IsFalse(read.GetProperty("block").TryGetProperty("definition", out _));
        if (read.GetProperty("requirements").TryGetProperty("fields", out var fields))
            Assert.IsFalse(fields.EnumerateObject().Any(), "No requirement text is stated: " + fields.GetRawText());

        // Repeating the operation observes the same diagram; nothing else is ever overwritten or created.
        var before = Diagrams();
        var repeated = await Call("kicad_diagram_create", Creation(operation, target));
        Assert.IsFalse(repeated.GetProperty("created").GetBoolean());
        Assert.AreEqual(createdToken, repeated.GetProperty("sourceToken").GetString()); Assert.AreEqual(documentId, repeated.GetProperty("documentId").GetString());
        Unchanged(before, "repeated create");
        var existingFile = await Call("kicad_diagram_create", Creation(Guid.NewGuid(), target), expectError: true);
        Assert.AreEqual("diagram_file_exists", existingFile.GetProperty("code").GetString());
        Assert.AreEqual(documentId, existingFile.GetProperty("details")[0].GetProperty("objectId").GetString());
        Unchanged(before, "create over an existing diagram");
        await Refused("create over another existing diagram", Creation(Guid.NewGuid(), existingSource), "diagram_file_exists");
        await Refused("relative path", Creation(Guid.NewGuid(), "second.system-diagram.xml"), "invalid_diagram_path");
        await Refused("not an XML path", Creation(Guid.NewGuid(), Path.Combine(directory, "second.system-diagram.json")), "invalid_diagram_path");
        await Refused("outside the repository", Creation(Guid.NewGuid(), Path.Combine(Path.GetDirectoryName(root)!, "escaped.system-diagram.xml")), "invalid_diagram_path");
        await Refused("missing folder", Creation(Guid.NewGuid(), Path.Combine(directory, "missing", "second.system-diagram.xml")), "invalid_diagram_path");
        await Refused("blank caption", Creation(Guid.NewGuid(), Path.Combine(directory, "blank.system-diagram.xml"), " "), "invalid_diagram_create_request");
        var changedEpoch = Creation(Guid.NewGuid(), Path.Combine(directory, "second.system-diagram.xml")); changedEpoch["expectedInstanceEpoch"] = Guid.NewGuid().ToString("D");
        await Refused("changed instance epoch", changedEpoch, "recursive_instance_changed");
        if (OperatingSystem.IsLinux()) // Native sessions are Linux evidence; read-only folders are Unix file modes.
        {
            Directory.CreateDirectory(locked);
            try
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                if (RecursiveDiagramCreationTests.IsWritable(locked))
                    await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-diagram-create-locked-skipped.txt"),
                        "The test account can write to a read-only folder (for example as root); the helper-process test covers this refusal.", token);
                else await Refused("unwritable folder", Creation(Guid.NewGuid(), Path.Combine(locked, "board.system-diagram.xml")), "diagram_file_read_only");
            }
            finally
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(locked, true);
            }
        }

        // The next project open lists the new diagram at the conventional path.
        var reopened = (await Call("kicad_diagram_discover", new() { ["instanceId"] = instanceId, ["projectFile"] = projectFile })).GetProperty("discovery");
        Assert.IsTrue(reopened.GetProperty("suggestedPathExists").GetBoolean());
        var listed = reopened.GetProperty("diagrams").EnumerateArray().Single(d => d.GetProperty("path").GetString() == target);
        Assert.AreEqual(("DDS_READY", documentId, 2, caption, createdToken, true), (listed.GetProperty("status").GetString(), listed.GetProperty("documentId").GetString(),
            listed.GetProperty("storedSchemaVersion").GetInt32(), listed.GetProperty("rootName").GetString(), listed.GetProperty("sourceToken").GetString(),
            listed.GetProperty("conventional").GetBoolean()));
        return new(root, target, documentId, createdToken, graph.SelectedRoot, caption);
    }

    /// <summary>The created diagram opens in the real per-level editor as one level whose root is only its caption; opening
    /// writes nothing, and the clean window closes without a prompt.</summary>
    private static async Task VerifyCreatedDiagramOpensInTheEditor(McpClient client, NativeClient native, int processId, string display,
        CreatedDiagram created, string instanceId, string evidence, CancellationToken token)
    {
        const string title = "Structural diagram";
        async Task WindowState(bool visible)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (NativeKeyboard.HasWindow(display, processId, title) != visible) await Task.Delay(50, timeout.Token);
        }
        await WindowState(false); // The journey's own editor window has closed.
        byte[] bytes = await File.ReadAllBytesAsync(created.Path, token);
        var opened = await client.CallToolAsync("kicad_diagram_open", new Dictionary<string, object?> { ["instanceId"] = instanceId,
            ["repositoryRoot"] = created.RepositoryRoot, ["sourcePath"] = created.Path, ["documentId"] = created.DocumentId }, cancellationToken: token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-created-diagram-open.json"), JsonSerializer.Serialize(opened), token);
        Assert.IsFalse(opened.IsError == true, "A newly created diagram opens in the native editor of this build.");
        P.RecursiveDiagramEditorState state = new();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                state = await native.InvokeAsync<P.ReadRecursiveDiagramEditor, P.RecursiveDiagramEditorState>(new() { DocumentId = created.DocumentId }, token);
                if (state.Ready && !state.Busy && state.Rendered) break;
                await Task.Delay(50, timeout.Token);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-created-diagram-state.json"), SchematicJson.Formatter.Format(state), token);
        await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-created-diagram.png"), token);
        Assert.AreEqual("", state.ErrorMessage); Assert.IsFalse(state.Dirty);
        Assert.AreEqual(created.SourceToken, state.SourceToken);
        Assert.AreEqual(created.Root.RevisionId.ToString("D"), state.DiagramPath.Single().RevisionId);
        Assert.AreEqual(created.Caption, state.Draft.Name);
        var draftFields = state.Draft.Fields ?? new P.RequirementFieldsData();
        Assert.AreEqual(("", "", ""), (draftFields.General, draftFields.Schematic, draftFields.Routing), "The caption is the only definition so far.");
        Assert.IsNotNull(state.CanvasDiagram, "The rendered level is reported.");
        Assert.IsEmpty(state.CanvasDiagram.Children, "A new diagram level has no blocks yet.");
        Assert.AreEqual(0U, state.ResolvedCanvasChildren);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(created.Path, token), "Opening never writes.");
        Assert.IsEmpty(state.ShownRequirementFields, "A caption-only root shows no empty requirement boxes.");
        Assert.IsEmpty(state.ShownFacets, "A caption-only root lists no component choices.");
        await VerifyDrawingTools(client, native, processId, display, created, instanceId, evidence, token);
        await VerifyBlockChoices(client, native, processId, display, created, instanceId, evidence, token);
        await VerifyConnectionDetails(client, native, processId, display, created, instanceId, evidence, token);
    }

    /// <summary>The first project of a native session runs the session-wide checks; later projects skip them.</summary>
    private static bool FirstInSession(string evidence, string name)
    {
        try { using var marker = new FileStream(Path.Combine(evidence, name + ".first"), FileMode.CreateNew); return true; }
        catch (IOException) { return false; }
    }

    /// <summary>Round A1 (owner decision n9f7cf92f32090daf) through the rendered editor, on the caption-only diagram an agent
    /// just created: blocks, connections and ports are drawn with both the toolbar strip and the canvas-edge palette, which
    /// always highlight the same tool; drawn elements start as their caption (owner decision n98a3f3c41084f0ed); moving,
    /// resizing, removing, undo and redo change only the level draft; Save stores the layout in the level's format 2
    /// presentation, Decline and cancelled edits write nothing, and the saved level reopens exactly.</summary>
    private static async Task VerifyDrawingTools(McpClient client, NativeClient native, int processId, string display, CreatedDiagram created,
        string instanceId, string evidence, CancellationToken token)
    {
        const string title = "Structural diagram";
        var document = new P.ReadRecursiveDiagramEditor { DocumentId = created.DocumentId };
        Task<P.RecursiveDiagramEditorState> Read() => native.InvokeAsync<P.ReadRecursiveDiagramEditor, P.RecursiveDiagramEditorState>(document, token);
        async Task<P.RecursiveDiagramEditorState> Wait(string step, Func<P.RecursiveDiagramEditorState, bool> condition)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            P.RecursiveDiagramEditorState current = new();
            try
            {
                while (true)
                {
                    current = await Read();
                    // A drawn step is complete once the editor is idle and any pointer drag has been released.
                    if (!current.Busy && !current.Dragging && condition(current)) return current;
                    await Task.Delay(50, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-drawing-timeout-" + step + ".json"), SchematicJson.Formatter.Format(current), token);
                await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-drawing-timeout-" + step + ".png"), token);
                throw new AssertFailedException("The drawing step '" + step + "' did not reach its expected state; its state and screen are retained.");
            }
        }
        void Key(string key, bool control = false, bool alt = false, string target = title) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, target, control, false, altKey: alt);
        void Type(string value) { foreach (char character in value) Key(character.ToString()); }
        void Click(int x, int y, string target = title) => NativeKeyboard.SchematicShortcut(display, processId, "click", target, false, true, clickFromLeft: x, clickFromTop: y);
        (int X, int Y) Screen(P.RecursiveDiagramEditorState at, double x, double y) =>
            ((int)Math.Round(at.CanvasWindowX + (x - at.CanvasOriginX) * at.CanvasScale), (int)Math.Round(at.CanvasWindowY + (y - at.CanvasOriginY) * at.CanvasScale));
        async Task At(double x, double y) { var (px, py) = Screen(await Read(), x, y); Click(px, py); }
        async Task Drag(double x, double y, double toX, double toY)
        {
            var at = await Read(); var from = Screen(at, x, y); var to = Screen(at, toX, toY);
            NativeKeyboard.SchematicShortcut(display, processId, "drag", title, false, true, clickFromLeft: from.X, clickFromTop: from.Y, dragToLeft: to.X, dragToTop: to.Y);
        }
        P.DiagramControlRect Find(P.RecursiveDiagramEditorState at, string name) => at.Controls.Single(c => c.Name == name);
        async Task Press(string name)
        {
            var control = Find(await Read(), name);
            Assert.IsTrue(control.Shown && control.Enabled, name + " must be shown and enabled before it is pressed.");
            Click(control.X + control.Width / 2, control.Y + control.Height / 2);
        }
        // Both entry points drive one tool: exactly the chosen strip button and palette button are highlighted.
        bool Tool(P.RecursiveDiagramEditorState at, string tool, string strip, string palette) => at.CanvasTool == tool
            && Find(at, strip).Active && Find(at, palette).Active
            && at.Controls.Count(c => (c.Name.StartsWith("RecursiveTool", StringComparison.Ordinal) || c.Name.StartsWith("DiagramPalette", StringComparison.Ordinal)) && c.Active) == 2;
        P.DiagramRectData Placement(P.RecursiveDiagramEditorState at, string block) =>
            at.LevelDraft.Scope.LocalDiagram.Presentation.Blocks.Single(b => b.BlockId == block).Rect;
        (double X, double Y) Centre(P.RecursiveDiagramEditorState at, string block)
        {
            var rect = Placement(at, block);
            return (double.Parse(rect.X, System.Globalization.CultureInfo.InvariantCulture) + double.Parse(rect.Width, System.Globalization.CultureInfo.InvariantCulture) / 2,
                double.Parse(rect.Y, System.Globalization.CultureInfo.InvariantCulture) + double.Parse(rect.Height, System.Globalization.CultureInfo.InvariantCulture) / 2);
        }
        // Design QA round 2: a capture waits until GTK's short fade of a button that just became available has finished (the
        // round's refuted "washed-out Save" was one frame of it).
        async Task Capture(string name)
        {
            await Task.Delay(FadeSettleMilliseconds, token);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-drawing-" + name + ".png"), token);
        }
        // The captures double as measured design evidence (design QA of the drawing tools): pixels are read back from them.
        async Task<CapturedWindow> Shot(string name)
        {
            await Capture(name);
            return await CapturedWindow.LoadAsync(Path.Combine(evidence, instanceId + "-drawing-" + name + ".png"), WindowOrigin(display, processId, title), token);
        }
        (int X, int Y) Canvas(P.RecursiveDiagramEditorState at, double x, double y) =>
            (at.CanvasWindowX + (int)Math.Round((x - at.CanvasOriginX) * at.CanvasScale), at.CanvasWindowY + (int)Math.Round((y - at.CanvasOriginY) * at.CanvasScale));
        async Task<JsonElement> Route(P.RecursiveDiagramEditorState at, string connection)
        {
            var observed = await client.CallToolAsync("kicad_diagram_observe", new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId, ["documentId"] = created.DocumentId, ["expectedSourceToken"] = at.SourceToken,
                ["expectedViewRevision"] = at.ViewRevision, ["views"] = new[] { new { viewId = "canvas", pixelWidth = 800, pixelHeight = 600 } }
            }, cancellationToken: token);
            Assert.IsFalse(observed.IsError == true, "The drawn level can be observed.");
            var observedView = JsonSerializer.SerializeToElement(observed).GetProperty("structuredContent").GetProperty("observation").GetProperty("views")[0];
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-drawing-observation.json"), observedView.GetRawText(), token);
            return observedView.GetProperty("resolvedLayout").GetProperty("routes").EnumerateArray().Single(r => r.GetProperty("connectionId").GetString() == connection);
        }
        async Task ClickRoute(string connection)
        {
            var route = await Route(await Read(), connection);
            var points = route.GetProperty("points").EnumerateArray().Select(p => (double.Parse(p.GetProperty("x").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p.GetProperty("y").GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            var (a, b) = (points[0], points[1]);
            await At((a.Item1 + b.Item1) / 2, (a.Item2 + b.Item2) / 2);
        }
        string root = created.Root.BlockId.ToString("D");

        // The empty level: Select is the one active tool in both entry points; nothing is defined but the caption.
        var start = await Wait("start", s => s.Ready && s.Rendered);
        Assert.IsTrue(Tool(start, "select", "RecursiveToolSelect", "DiagramPaletteSelect"));
        Assert.IsTrue(start.PaletteShown); Assert.AreEqual(2U, start.StoredSchemaVersion); Assert.IsTrue(start.SourceWritable);
        Assert.AreEqual(root, start.LevelDraft.Scope.Baseline.BlockId);
        Assert.IsTrue(Find(start, "RecursiveAddRequirement").Shown, "One quiet way to add a requirement.");
        Assert.IsFalse(Find(start, "RecursiveToolDelete").Enabled, "Nothing is selected that can be removed.");
        VerifyToolStyling(await Shot("empty"), start, "empty", "Select");

        // The plain letters B, P and C choose the same tools as both entry points. A connection cannot start on empty
        // space; Escape returns to Select. With Ctrl held the letters belong to other commands and never switch tools.
        Key("b"); await Wait("key-add-block", s => Tool(s, "add-block", "RecursiveToolAddBlock", "DiagramPaletteAddBlock"));
        Key("p"); await Wait("key-place-port", s => Tool(s, "add-port", "RecursiveToolPlacePort", "DiagramPalettePlacePort"));
        Key("c"); await Wait("key-connect", s => Tool(s, "connect", "RecursiveToolConnect", "DiagramPaletteConnect"));
        await At(430, 420);
        var emptyStart = await Wait("connect-start-empty", s => s.Notice == "Start the connection on a block or port.");
        Assert.AreEqual("", emptyStart.CanvasHint, "Nothing was started."); Assert.IsFalse(emptyStart.Dirty);
        Key("Escape"); await Wait("key-escape", s => Tool(s, "select", "RecursiveToolSelect", "DiagramPaletteSelect"));
        ulong navigation = (await Read()).NavigationInputRevision;
        Key("c", control: true); Key("b", control: true); Key("p", control: true); Key("Right");
        var modified = await Wait("modified-letters", s => s.NavigationInputRevision > navigation);
        Assert.IsTrue(Tool(modified, "select", "RecursiveToolSelect", "DiagramPaletteSelect"), "Ctrl+C, Ctrl+B and Ctrl+P never switch tools.");

        // Toolbar strip: Add block. Cancel first (Escape), then a blank caption is refused, then a caption is kept.
        await Press("RecursiveToolAddBlock");
        await Wait("strip-add-block", s => Tool(s, "add-block", "RecursiveToolAddBlock", "DiagramPaletteAddBlock"));
        await At(260, 200); var captionOpen = await Wait("caption-open", s => s.CaptionEditor == "block");
        {
            // Design QA P1-1: the dashed outline of the block being added and the caption field's focus ring are drawn in the
            // accent, 3:1 or more on the canvas in both themes (the dark theme's own blue reached only 1.8:1). The field shows
            // what to type (P3 2).
            var shot = await Shot("block-caption");
            VerifyToolStyling(shot, captionOpen, "block-caption", "AddBlock");
            var canvasColour = shot.At(captionOpen.CanvasWindowX + (int)captionOpen.CanvasPixelWidth - 12, captionOpen.CanvasWindowY + (int)captionOpen.CanvasPixelHeight - 12);
            var (outlineLeft, outlineTop) = Canvas(captionOpen, 140, 130); var (outlineRight, _) = Canvas(captionOpen, 380, 270);
            var outline = shot.MostContrasting(outlineLeft + 20, outlineTop - 1, (outlineRight - outlineLeft) / 2, 4, canvasColour);
            Assert.IsTrue(shot.Contrast("P1-1 new-block outline on the canvas", outline, canvasColour) >= 3.0,
                $"block-caption: the new block's outline {CapturedWindow.Describe(outline)} stands 3:1 from the canvas {CapturedWindow.Describe(canvasColour)}.");
            var editor = Find(captionOpen, "DiagramCaptionEditor");
            var ring = shot.MostContrasting(editor.X - 6, editor.Y + editor.Height / 2 - 2, 5, 5, canvasColour);
            Assert.IsTrue(shot.Contrast("P1-1 caption focus ring on the canvas", ring, canvasColour) >= 3.0,
                $"block-caption: the caption field's focus ring {CapturedWindow.Describe(ring)} stands 3:1 from the canvas {CapturedWindow.Describe(canvasColour)}.");
            Assert.AreEqual("Type a name and press Enter, or press Escape to cancel.", captionOpen.StatusText, "The status bar says what to do with the caption.");
        }
        Key("Escape"); var cancelled = await Wait("caption-cancelled", s => s.CaptionEditor == "");
        Assert.IsEmpty(cancelled.LevelDraft.NewChildren); Assert.IsFalse(cancelled.Dirty);
        await At(260, 200); await Wait("caption-reopen", s => s.CaptionEditor == "block");
        Key("Return"); var blank = await Wait("caption-blank", s => s.CaptionEditor == "block" && s.Notice.Contains("caption", StringComparison.Ordinal));
        Assert.IsEmpty(blank.LevelDraft.NewChildren, "A blank caption adds nothing.");
        Type("PSU"); Key("Return");
        var psuAdded = await Wait("psu-added", s => s.LevelDraft.NewChildren.Count == 1 && s.CaptionEditor == "" && s.Dirty);
        string psu = psuAdded.LevelDraft.NewChildren[0].Selection.BlockId;
        Assert.AreEqual("PSU", psuAdded.Draft.Name, "The new block is selected in the inspector.");
        Assert.AreEqual(("140", "130", "240", "140"), (Placement(psuAdded, psu).X, Placement(psuAdded, psu).Y, Placement(psuAdded, psu).Width, Placement(psuAdded, psu).Height),
            "A new block is placed where the user clicked.");
        Assert.AreEqual("select", psuAdded.CanvasTool);
        Assert.IsEmpty(psuAdded.ShownRequirementFields, "A new block shows only its caption.");
        Assert.IsFalse(Find(psuAdded, "RecursiveOpenDiagram").Shown); Assert.IsFalse(Find(psuAdded, "RecursiveFieldHistory0").Shown);
        await Capture("block-added");

        // Canvas-edge palette: Add block.
        await Press("DiagramPaletteAddBlock");
        await Wait("palette-add-block", s => Tool(s, "add-block", "RecursiveToolAddBlock", "DiagramPaletteAddBlock"));
        await At(620, 200); await Wait("cpu-caption", s => s.CaptionEditor == "block");
        Type("CPU"); Key("Return");
        var cpuAdded = await Wait("cpu-added", s => s.LevelDraft.NewChildren.Count == 2 && s.CaptionEditor == "");
        string cpu = cpuAdded.LevelDraft.NewChildren[1].Selection.BlockId;
        CollectionAssert.AreEqual(new[] { psu, cpu }, cpuAdded.LevelDraft.Scope.Children.Select(c => c.BlockId).ToArray());

        // Palette Connect: the hint is shared; clicking empty space is refused; finishing on the CPU asks for a caption.
        await Press("DiagramPaletteConnect");
        await Wait("palette-connect", s => Tool(s, "connect", "RecursiveToolConnect", "DiagramPaletteConnect"));
        var (psuX, psuY) = Centre(cpuAdded, psu); var (cpuX, cpuY) = Centre(cpuAdded, cpu);
        await At(psuX, psuY);
        // Design QA round 2, R2-P2-3: the connection's first end becomes the selection and fills the inspector; the CPU, selected
        // before, is no longer selected.
        var connectStarted = await Wait("connect-started", s => s.CanvasHint == "Click a port to finish connection");
        Assert.AreEqual(psu, connectStarted.Draft.Baseline.BlockId, "connect-started: the connection's first end, the PSU, is the selection.");
        Assert.IsNull(connectStarted.ConnectionDraft, "connect-started: no connection is selected.");
        Assert.AreEqual("PSU", Find(connectStarted, "RecursiveOwnerCaption").Label, "connect-started: the inspector shows the PSU.");
        await At(psuX + 60, psuY + 30);
        await Wait("connect-same-block", s => s.Notice == "Connect two different blocks or ports." && s.CanvasHint == "Click a port to finish connection"
            && s.CaptionEditor == "");
        await At(430, 420); var connectEmpty = await Wait("connect-empty", s => s.Notice.Contains("Finish the connection", StringComparison.Ordinal)
            && s.CanvasHint == "Click a port to finish connection");
        {
            var shot = await Shot("connect-hint");
            VerifyToolStyling(shot, connectEmpty, "connect-hint", "Connect");
            // Design QA P1-1 and P3 4: the connection in progress runs at right angles in the accent from the PSU's right edge
            // (380, 200) to the pointer (430, 420); its vertical leg at x 405 stands 3:1 or more from the canvas.
            var canvasColour = shot.At(connectEmpty.CanvasWindowX + (int)connectEmpty.CanvasPixelWidth - 12, connectEmpty.CanvasWindowY + (int)connectEmpty.CanvasPixelHeight - 12);
            var (legX, legY) = Canvas(connectEmpty, 405, 310);
            var preview = shot.MostContrasting(legX - 3, legY - 15, 7, 30, canvasColour);
            Assert.IsTrue(shot.Contrast("P1-1 connection preview on the canvas", preview, canvasColour) >= 3.0,
                $"connect-hint: the connection preview {CapturedWindow.Describe(preview)} stands 3:1 from the canvas {CapturedWindow.Describe(canvasColour)}.");
            // Design QA P2-8: with unsaved changes, Save is the primary action: an accent fill with a 4.5:1 label that stands apart
            // from Decline, and the two share the inspector's width.
            var save = Find(connectEmpty, "RecursiveSave"); var decline = Find(connectEmpty, "RecursiveDecline");
            Assert.IsTrue(save.Enabled && decline.Enabled, "connect-hint: Save and Decline are available with unsaved changes.");
            var saveFill = shot.At(save.X + 8, save.Y + save.Height / 2); var declineFill = shot.At(decline.X + 8, decline.Y + decline.Height / 2);
            var saveLabel = shot.MostContrasting(save.X + save.Width / 4, save.Y + 6, save.Width / 2, save.Height - 12, saveFill);
            Assert.IsTrue(shot.Contrast("P2-8 Save label on its fill", saveLabel, saveFill) >= 4.5,
                $"connect-hint: the Save label {CapturedWindow.Describe(saveLabel)} reads 4.5:1 on its fill {CapturedWindow.Describe(saveFill)}.");
            Assert.IsTrue(shot.Contrast("P2-8 Save fill against Decline", saveFill, declineFill) >= 2.0,
                $"connect-hint: Save {CapturedWindow.Describe(saveFill)} stands apart from Decline {CapturedWindow.Describe(declineFill)}.");
            Assert.IsTrue(Math.Abs(save.Width - decline.Width) <= 2 && save.Width >= 120 && save.X > decline.X,
                "connect-hint: Decline and Save share the inspector's width, Save on the right.");
            // Design QA round 2, R2-P2-3, measured: the PSU, where the connection starts, has the selection's accent outline and
            // tinted fill; the CPU, selected before Connect, is drawn plain again (its outline is not the accent and its fill is
            // not the selection's tint).
            var psuBox = connectEmpty.BlockTexts.Single(b => b.BlockId == psu).Block; var cpuBox = connectEmpty.BlockTexts.Single(b => b.BlockId == cpu).Block;
            var psuFill = shot.At(psuBox.X + psuBox.Width - 18, psuBox.Y + psuBox.Height - 14); var cpuFill = shot.At(cpuBox.X + cpuBox.Width - 18, cpuBox.Y + cpuBox.Height - 14);
            var psuBorder = shot.MostContrasting(psuBox.X + psuBox.Width / 4 - 2, psuBox.Y - 1, 5, 4, psuFill);
            var cpuBorder = shot.MostContrasting(cpuBox.X + cpuBox.Width / 4 - 2, cpuBox.Y - 1, 5, 4, cpuFill);
            Assert.IsTrue(shot.Contrast("R2-P2-3 connection source's outline on the canvas", psuBorder, canvasColour) >= 3.0,
                $"connect-hint: the PSU, where the connection starts, is outlined in the accent {CapturedWindow.Describe(psuBorder)}.");
            Assert.IsTrue(CapturedWindow.Distance(psuBorder, cpuBorder) >= 60,
                $"connect-hint: the CPU's outline {CapturedWindow.Describe(cpuBorder)} is not the selection's accent {CapturedWindow.Describe(psuBorder)}.");
            Assert.IsTrue(CapturedWindow.Distance(psuFill, cpuFill) >= 12,
                $"connect-hint: the CPU's fill {CapturedWindow.Describe(cpuFill)} is not the selection's tint {CapturedWindow.Describe(psuFill)}.");
            Assert.AreEqual("PSU", Find(connectEmpty, "RecursiveOwnerCaption").Label, "connect-hint: the inspector shows the connection's source, not the CPU.");
            // The hint is placed clear of every block (design QA round 2, P3 1).
            Assert.IsNotNull(connectEmpty.ConnectHint, "connect-hint: the hint is drawn.");
            Assert.IsTrue(connectEmpty.BlockTexts.All(b => RectsApart(connectEmpty.ConnectHint, b.Block)), "connect-hint: the hint covers no block.");
        }
        await At(cpuX, cpuY); await Wait("connection-caption", s => s.CaptionEditor == "connection");
        Type("Power"); Key("Return");
        var powerAdded = await Wait("power-added", s => s.LevelDraft.NewConnections.Count == 1 && s.CaptionEditor == "");
        var power = powerAdded.LevelDraft.NewConnections[0];
        Assert.AreEqual("Power", power.Name); Assert.AreEqual("Power", powerAdded.ConnectionDraft.Name, "The new connection is selected.");
        CollectionAssert.AreEqual(new[] { (P.DiagramEndpointKind.DekUnresolved, psu), (P.DiagramEndpointKind.DekUnresolved, cpu) },
            power.Endpoints.Select(e => (e.Kind, e.BlockId)).ToArray());
        Assert.IsEmpty(powerAdded.ShownRequirementFields, "A new connection shows only its caption.");
        Assert.IsFalse(Find(powerAdded, "RecursiveConnectionEndpoints").Shown);

        // Toolbar Place port: on the level boundary, then on the PSU's right edge.
        await Press("RecursiveToolPlacePort");
        await Wait("strip-port", s => Tool(s, "add-port", "RecursiveToolPlacePort", "DiagramPalettePlacePort"));
        await At(80, 200); await Wait("boundary-caption", s => s.CaptionEditor == "port");
        Type("DC input"); Key("Return");
        var boundary = await Wait("boundary-added", s => s.LevelDraft.Scope.LocalDiagram.Interfaces.Count == 1 && s.CaptionEditor == "");
        string dcInput = boundary.LevelDraft.Scope.LocalDiagram.Interfaces[0].Id;
        var view = boundary.LevelDraft.Scope.LocalDiagram.Presentation;
        Assert.AreEqual(("100", "90", "680", "220"), (view.Frame.X, view.Frame.Y, view.Frame.Width, view.Frame.Height), "The first boundary port stores the frame around the drawing.");
        var dcPort = view.Ports.Single(p => p.BlockId == root && p.InterfaceId == dcInput);
        Assert.AreEqual((P.DiagramPortSide.DpsLeft, "110"), (dcPort.Side, dcPort.Offset));
        Assert.AreEqual(root, boundary.SelectedInterfaceOwnerId); Assert.AreEqual(dcInput, boundary.SelectedInterfaceId);
        await Press("RecursiveToolPlacePort"); await At(380, 180); await Wait("child-port-caption", s => s.CaptionEditor == "port");
        Type("Rail"); Key("Return");
        var railAdded = await Wait("child-port-added", s => s.LevelDraft.NewChildren[0].Interfaces.Count == 1 && s.CaptionEditor == "");
        string rail = railAdded.LevelDraft.NewChildren[0].Interfaces[0].Id;
        var railPort = railAdded.LevelDraft.Scope.LocalDiagram.Presentation.Ports.Single(p => p.BlockId == psu && p.InterfaceId == rail);
        Assert.AreEqual((P.DiagramPortSide.DpsRight, "50"), (railPort.Side, railPort.Offset));

        // Toolbar Connect from the new port to the CPU block.
        await Press("RecursiveToolConnect");
        await Wait("strip-connect", s => Tool(s, "connect", "RecursiveToolConnect", "DiagramPaletteConnect"));
        await At(380, 180); var portStarted = await Wait("port-connect-started", s => s.CanvasHint == "Click a port to finish connection");
        Assert.AreEqual((psu, rail), (portStarted.SelectedInterfaceOwnerId, portStarted.SelectedInterfaceId),
            "port-connect-started: the Rail port, where the connection starts, is the selection (design QA round 2, R2-P2-3).");
        {
            // Design QA P2-6: ports are 12-pixel squares at every zoom, and while Connect is active the port under the pointer gets
            // a ring in the accent (the sketch's snap circle), 3:1 or more on the canvas; a block under the pointer gets an outline.
            Assert.IsTrue(portStarted.PortMarks.Count >= 2 && portStarted.PortMarks.All(p => p.Width >= 12 && p.Height >= 12),
                "port-connect-started: every port is drawn as a square of at least 12 pixels.");
            var dcMark = portStarted.PortMarks.Single(p => p.Label == "DC input");
            NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: dcMark.X + dcMark.Width / 2, clickFromTop: dcMark.Y + dcMark.Height / 2);
            var aimed = await Wait("connect-target-port", s => s.ConnectTarget?.Label == "DC input" && s.PortMarks.Any(p => p.Label == "DC input" && p.Active)
                && s.ConnectPreview.Count >= 2);
            var shot = await Shot("connect-port-target");
            VerifyConnectPreview(shot, aimed, "connect-port-target", psu, "Rail", "DC input");
            var canvasColour = shot.At(aimed.CanvasWindowX + (int)aimed.CanvasPixelWidth - 12, aimed.CanvasWindowY + (int)aimed.CanvasPixelHeight - 12);
            var ring = aimed.ConnectTarget;
            int cx = ring.X + ring.Width / 2, cy = ring.Y + ring.Height / 2;
            var ringInk = shot.MostContrasting(cx + 4, cy - ring.Height / 2, 6, 5, canvasColour);
            Assert.IsTrue(ring.Width >= 16 && shot.Contrast("P2-6 Connect ring on the canvas", ringInk, canvasColour) >= 3.0,
                $"connect-port-target: the ring around the port under the pointer {CapturedWindow.Describe(ringInk)} stands 3:1 from the canvas.");
            var (blockX, blockY) = Canvas(aimed, cpuX, cpuY);
            NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: blockX, clickFromTop: blockY);
            await Wait("connect-target-block", s => s.ConnectTarget?.Label == "CPU" && !s.PortMarks.Any(p => p.Active));
        }
        await At(cpuX, cpuY); await Wait("feed-caption", s => s.CaptionEditor == "connection");
        Type("Rail feed"); Key("Return");
        var feedAdded = await Wait("feed-added", s => s.LevelDraft.NewConnections.Count == 2 && s.CaptionEditor == "");
        var feed = feedAdded.LevelDraft.NewConnections[1];
        Assert.AreEqual((P.DiagramEndpointKind.DekInterface, psu, rail), (feed.Endpoints[0].Kind, feed.Endpoints[0].BlockId, feed.Endpoints[0].InterfaceId));
        Assert.AreEqual("Rail feed", feedAdded.ConnectionDraft?.Name, "The finished connection is selected.");

        // Palette Select, then move and resize the CPU; Escape also returns to Select.
        await Press("DiagramPaletteSelect"); await Wait("palette-select", s => Tool(s, "select", "RecursiveToolSelect", "DiagramPaletteSelect"));
        await Drag(cpuX, cpuY + 30, cpuX + 60, cpuY + 90);
        var moved = await Wait("moved", s => Placement(s, cpu).X == "560" && Placement(s, cpu).Y == "190");
        Assert.AreEqual(cpu, moved.Draft.Baseline.BlockId, "Dragging a block selects and moves it.");
        (string, string, string, string) Frame(P.RecursiveDiagramEditorState at)
        {
            var frame = at.LevelDraft.Scope.LocalDiagram.Presentation.Frame;
            return (frame.X, frame.Y, frame.Width, frame.Height);
        }
        Assert.AreEqual(("100", "90", "740", "280"), Frame(moved), "The level frame grows to keep the moved block inside it.");
        Assert.AreEqual("110", moved.LevelDraft.Scope.LocalDiagram.Presentation.Ports.Single(p => p.InterfaceId == dcInput).Offset,
            "The boundary port stays where it was drawn.");
        // Fit brings the whole drawing into view before the resize handle is used.
        ulong beforeFit = moved.ViewRevision;
        NativeKeyboard.SchematicShortcut(display, processId, "click", title, false, true, clickFromLeft: 377, clickFromTop: 45);
        var fitted = await Wait("fitted", s => s.ViewRevision > beforeFit && s.Rendered);
        VerifySelectionContrast(await Shot("selected-handles"), fitted, "selected-handles", cpu);
        await Drag(800, 330, 840, 360);
        await Wait("resized", s => Placement(s, cpu).Width == "280" && Placement(s, cpu).Height == "170" && Frame(s) == ("100", "90", "780", "310"));
        Key("z", control: true); await Wait("resize-undone", s => Placement(s, cpu).Width == "240" && Frame(s) == ("100", "90", "740", "280"));
        Key("y", control: true); await Wait("resize-redone", s => Placement(s, cpu).Width == "280" && Frame(s) == ("100", "90", "780", "310"));
        // Moving a port along its block's edge.
        await Drag(380, 180, 380, 230);
        var portMoved = await Wait("port-moved", s => s.LevelDraft.Scope.LocalDiagram.Presentation.Ports.Single(p => p.InterfaceId == rail).Offset == "100");
        Assert.AreEqual(rail, portMoved.SelectedInterfaceId);
        // Design QA P2-5: Rail feed and Power both end on the CPU itself, and Power also on the PSU. Each end on a block gets its
        // own point on the block's edge facing its peer (rule F2 as revised), never a corner and never another connection's point:
        // on the PSU, whose Rail port (offset 100) takes the nearer of the two step points 47 and 93, Power's end takes 47; on the
        // CPU (height 170), Power (its peer is the PSU, centred at 200) takes 57 and Rail feed (its peer is the Rail port at 230)
        // takes 113. Computed paths keep their vertical legs at least 10 units apart (rule F4a): Power runs at the middle, x 470,
        // and Rail feed, which would share that channel from 230 to 247, moves to x 460. No route is stored.
        Assert.IsEmpty(portMoved.LevelDraft.Scope.LocalDiagram.Presentation.Routes, "No route is stored: the computed paths keep apart by themselves.");
        async Task<(double X, double Y)[]> Drawn(string connection) => (await Route(await Read(), connection)).GetProperty("points").EnumerateArray()
            .Select(p => (double.Parse(p.GetProperty("x").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p.GetProperty("y").GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        (double X, double Y, double W, double H)[] Blocks(P.RecursiveDiagramEditorState at) => [.. at.LevelDraft.Scope.LocalDiagram.Presentation.Blocks.Select(b =>
            (double.Parse(b.Rect.X, System.Globalization.CultureInfo.InvariantCulture), double.Parse(b.Rect.Y, System.Globalization.CultureInfo.InvariantCulture),
             double.Parse(b.Rect.Width, System.Globalization.CultureInfo.InvariantCulture), double.Parse(b.Rect.Height, System.Globalization.CultureInfo.InvariantCulture)))];
        var powerPath = await Drawn(power.Selection.ConnectionId); var feedPath = await Drawn(feed.Selection.ConnectionId);
        CollectionAssert.AreEqual(new[] { (380.0, 177.0), (470.0, 177.0), (470.0, 247.0), (560.0, 247.0) }, powerPath, "Power runs from its own points on the PSU and the CPU.");
        CollectionAssert.AreEqual(new[] { (380.0, 230.0), (460.0, 230.0), (460.0, 303.0), (560.0, 303.0) }, feedPath, "Rail feed runs from the Rail port to its own point on the CPU.");
        VerifyPathsApart("port-moved", Blocks(portMoved), powerPath, feedPath);
        // A click near Rail feed's CPU end selects Rail feed.
        await At(510, 303);
        await Wait("feed-near-cpu", s => s.ConnectionDraft?.Baseline.ConnectionId == feed.Selection.ConnectionId && s.SelectedInterfaceId == "");
        // The nearest connection on screen wins. The two channels run side by side from y 230 to 247, 10 units apart (design QA
        // P2-5 asks for 8 pixels or more): a press 3 pixels from one and 7 from the other selects the nearer. A press midway,
        // equally near both, keeps whichever is selected; the editor counts every canvas press, so an unchanged selection is a
        // handled press, not one still on its way.
        (int X, int Y) Pixel(P.RecursiveDiagramEditorState at, double x, double y) =>
            (at.CanvasWindowX + (int)Math.Round((x - at.CanvasOriginX) * at.CanvasScale, MidpointRounding.AwayFromZero),
             at.CanvasWindowY + (int)Math.Round((y - at.CanvasOriginY) * at.CanvasScale, MidpointRounding.AwayFromZero));
        var probe = await Read();
        var feedChannel = Pixel(probe, 460, 240); var powerChannel = Pixel(probe, 470, 240);
        Assert.IsTrue(powerChannel.X - feedChannel.X >= 8, $"The two channels are {powerChannel.X - feedChannel.X} pixels apart on screen, at least 8.");
        Assert.AreEqual(0, (powerChannel.X - feedChannel.X) % 2, "A press can land exactly midway between the channels.");
        Click(powerChannel.X - 3, powerChannel.Y);
        await Wait("nearest-power", s => s.ConnectionDraft?.Baseline.ConnectionId == power.Selection.ConnectionId);
        Click(feedChannel.X + 3, feedChannel.Y);
        await Wait("nearest-feed", s => s.ConnectionDraft?.Baseline.ConnectionId == feed.Selection.ConnectionId);
        int midway = (feedChannel.X + powerChannel.X) / 2;
        foreach (var (stays, near, step) in new[] { (feed.Selection.ConnectionId, feedChannel.X + 3, "midway-keeps-feed"),
                                                     (power.Selection.ConnectionId, powerChannel.X - 3, "midway-keeps-power") })
        {
            if ((await Read()).ConnectionDraft?.Baseline.ConnectionId != stays)
            {
                Click(near, feedChannel.Y);
                await Wait(step + "-selected", s => s.ConnectionDraft?.Baseline.ConnectionId == stays);
            }
            ulong presses = (await Read()).CanvasPresses;
            Click(midway, feedChannel.Y);
            var tie = await Wait(step, s => s.CanvasPresses > presses);
            Assert.AreEqual(stays, tie.ConnectionDraft?.Baseline.ConnectionId, "A press equally near both connections keeps the selected one selected.");
        }

        // Removing: the connection (Delete key), the CPU block (toolbar Delete), the used port (palette Delete, asked first).
        await ClickRoute(power.Selection.ConnectionId); await Wait("power-selected", s => s.ConnectionDraft?.Baseline.ConnectionId == power.Selection.ConnectionId);
        Key("Delete");
        var powerRemoved = await Wait("power-removed", s => s.LevelDraft.NewConnections.Count == 1 && s.LastEffects.Count > 0);
        Assert.AreEqual(P.LevelEditEffectKind.LeekConnectionRemoved, powerRemoved.LastEffects.Single().Kind);
        StringAssert.Contains(powerRemoved.Notice, "Power");
        await Press("DiagramPaletteUndo"); await Wait("power-restored", s => s.LevelDraft.NewConnections.Count == 2);
        Key("y", control: true); await Wait("power-removed-again", s => s.LevelDraft.NewConnections.Count == 1);
        Key("z", control: true); var nothing = await Wait("power-restored-again", s => s.LevelDraft.NewConnections.Count == 2);
        await At(Centre(nothing, cpu).X, Centre(nothing, cpu).Y); await Wait("cpu-selected", s => s.Draft.Baseline.BlockId == cpu);
        await Press("RecursiveToolDelete");
        var cpuRemoved = await Wait("cpu-removed", s => s.LevelDraft.NewChildren.Count == 1 && s.LastEffects.Count > 0);
        Assert.AreEqual(cpu, cpuRemoved.LastEffects.Single(e => e.Kind == P.LevelEditEffectKind.LeekChildRemoved).ObjectId);
        Assert.AreEqual(2, cpuRemoved.LastEffects.Count(e => e.Kind == P.LevelEditEffectKind.LeekConnectionRemoved), "Both connections end on the CPU.");
        Assert.IsEmpty(cpuRemoved.LevelDraft.NewConnections);
        Key("z", control: true); await Wait("cpu-restored", s => s.LevelDraft.NewChildren.Count == 2 && s.LevelDraft.NewConnections.Count == 2);
        var restored = await Read();
        var (railX, railY) = (380.0, 230.0);
        await At(railX, railY); await Wait("port-selected", s => s.SelectedInterfaceId == rail);
        await Press("DiagramPalettePlacePort"); Key("Escape"); await Wait("escape-select", s => s.CanvasTool == "select");
        await At(railX, railY); await Wait("port-reselected", s => s.SelectedInterfaceId == rail);
        await Press("DiagramPaletteDelete");
        using (var modal = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            modal.CancelAfter(TimeSpan.FromSeconds(20));
            while (!NativeKeyboard.HasWindow(display, processId, "Remove port")) await Task.Delay(50, modal.Token);
        }
        await Capture("remove-port-question");
        Key("Escape", target: "Remove port");
        var kept = await Wait("port-kept", s => !NativeKeyboard.HasWindow(display, processId, "Remove port"));
        Assert.AreEqual(1, kept.LevelDraft.NewChildren[0].Interfaces.Count); Assert.AreEqual("", kept.ErrorCode);
        await Press("DiagramPaletteDelete");
        using (var modal = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            modal.CancelAfter(TimeSpan.FromSeconds(20));
            while (!NativeKeyboard.HasWindow(display, processId, "Remove port")) await Task.Delay(50, modal.Token);
        }
        Key("r", alt: true, target: "Remove port");
        var portRemoved = await Wait("port-removed", s => s.LevelDraft.NewChildren[0].Interfaces.Count == 0 && s.LastEffects.Count > 0);
        CollectionAssert.IsSubsetOf(new[] { P.LevelEditEffectKind.LeekInterfaceRemoved, P.LevelEditEffectKind.LeekConnectionRemoved },
            portRemoved.LastEffects.Select(e => e.Kind).ToArray());
        Key("z", control: true); await Wait("port-restored", s => s.LevelDraft.NewChildren[0].Interfaces.Count == 1 && s.LevelDraft.NewConnections.Count == 2);

        // Renaming in place (F2), with undo.
        await At(Centre(restored, cpu).X, Centre(restored, cpu).Y); await Wait("cpu-reselected", s => s.Draft.Baseline.BlockId == cpu);
        Key("F2"); await Wait("rename-open", s => s.CaptionEditor == "rename");
        Key("End"); Type(" module"); Key("Return");
        await Wait("renamed", s => s.LevelDraft.NewChildren[1].Name == "CPU module");
        Key("z", control: true); await Wait("rename-undone", s => s.LevelDraft.NewChildren[1].Name == "CPU");

        // The inspector grows with definition: one quiet Add requirement, then the chosen box only. A block also offers Add
        // detail for its component choices (VerifyBlockChoices drives it).
        await At(psuX, psuY); await Wait("psu-reselected", s => s.Draft.Baseline.BlockId == psu && s.ShownRequirementFields.Count == 0);
        Assert.IsTrue(Find(await Read(), "RecursiveAddDetail").Shown, "A block offers Add detail beside Add requirement.");
        await Press("RecursiveAddRequirement");
        using (var menu = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            menu.CancelAfter(TimeSpan.FromSeconds(15)); int count = 0;
            do { NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, observePopupCount: value => count = value); if (count == 0) await Task.Delay(50, menu.Token); }
            while (count == 0);
        }
        Key("Home"); Key("Return");
        await Wait("general-revealed", s => s.ShownRequirementFields.SequenceEqual(new[] { P.RequirementFieldKind.RfkGeneral }) && s.FocusedControl == "RecursiveRequirements0");
        Type("Supply the CPU.");
        var generalTyped = await Wait("general-typed", s => s.Draft.Fields.General == "Supply the CPU.");
        VerifyTextInset(await Shot("inspector-requirement"), Find(generalTyped, "RecursiveRequirements0"), "inspector-requirement", "the General requirements box");
        await ClickRoute(feed.Selection.ConnectionId);
        var feedSelected = await Wait("feed-selected", s => s.ConnectionDraft?.Name == "Rail feed" && s.ShownRequirementFields.Count == 0);
        Assert.IsTrue(Find(feedSelected, "RecursiveAddRequirement").Shown, "A connection offers Add requirement.");
        // Owner decision nf53af9d74841b7d3 (Round A3) pairs + Add detail with + Add requirement on a connection too;
        // VerifyConnectionDetails drives it.
        Assert.IsTrue(Find(feedSelected, "RecursiveAddDetail").Shown, "A connection offers + Add detail beside + Add requirement.");
        Key("4", control: true); Type("Feed the CPU from the rail.");
        await Wait("feed-comment", s => s.LevelDraft.Scope.LocalDiagram.Annotations.Any(n => n.TargetKind == P.DiagramAnnotationTargetKind.DatConnection
            && n.TargetId == feed.Selection.ConnectionId && n.Text == "Feed the CPU from the rail."));

        // A blank caption cannot be saved: Ctrl+S keeps its editor open with the notice and writes nothing. Moving focus into
        // the inspector then cancels the blank caption and leaves focus where the person put it.
        ulong saves = (await Read()).CompletedSaveCount;
        string unsavedBytes = await File.ReadAllTextAsync(created.Path, token);
        await Press("RecursiveToolAddBlock"); await At(260, 480); await Wait("save-blank-caption", s => s.CaptionEditor == "block");
        Key("s", control: true);
        var refusedSave = await Wait("save-refused", s => s.CaptionEditor == "block" && s.Notice == "Type a caption, or press Escape to cancel.");
        Assert.AreEqual(saves, refusedSave.CompletedSaveCount, "Nothing was sent to save."); Assert.IsTrue(refusedSave.Dirty);
        Assert.AreEqual(unsavedBytes, await File.ReadAllTextAsync(created.Path, token));
        await Press("RecursiveComments");
        var focusLeft = await Wait("caption-focus-left", s => s.CaptionEditor == "" && s.FocusedControl == "RecursiveComments");
        Assert.HasCount(2, focusLeft.LevelDraft.NewChildren, "The blank caption added nothing.");
        Assert.AreEqual("", focusLeft.Notice);
        await Press("RecursiveToolSelect"); await Wait("save-select", s => Tool(s, "select", "RecursiveToolSelect", "DiagramPaletteSelect"));

        // Save stores everything in one level revision with the layout in the level's format 2 presentation.
        Key("s", control: true);
        var saved = await Wait("saved", s => s.CompletedSaveCount > saves && !s.Dirty);
        Assert.AreEqual("", saved.ErrorMessage);
        string savedXml = await File.ReadAllTextAsync(created.Path, token);
        var (graph, version) = RecursiveBlockGraphXml.ReadVersioned(savedXml);
        Assert.AreEqual(2, version);
        var top = graph.Inspect(graph.SelectedRoot);
        CollectionAssert.AreEqual(new[] { "PSU", "CPU" }, top.Children.Select(c => graph.Inspect(c).Name).ToArray());
        var psuSaved = graph.Inspect(top.Children[0]); var cpuSaved = graph.Inspect(top.Children[1]);
        Assert.AreEqual("Supply the CPU.", graph.Requirements(psuSaved.Selection).Requirements.General);
        Assert.AreEqual(DiagramRequirements.Empty, graph.Requirements(cpuSaved.Selection).Requirements, "The CPU is still only its caption.");
        Assert.IsNull(cpuSaved.Diagram, "A caption-only block stores no diagram.");
        Assert.AreEqual("Rail", psuSaved.LocalDiagram.Interfaces.Single().Name);
        Assert.AreEqual("DC input", top.LocalDiagram.Interfaces.Single().Name);
        var layout = top.LocalDiagram.Layout;
        Assert.AreEqual(new DiagramRect(140, 130, 240, 140), layout.Blocks.Single(b => b.BlockId.ToString("D") == psu).Rect);
        Assert.AreEqual(new DiagramRect(560, 190, 280, 170), layout.Blocks.Single(b => b.BlockId.ToString("D") == cpu).Rect);
        Assert.AreEqual(new DiagramRect(100, 90, 780, 310), layout.Frame);
        Assert.AreEqual(new DiagramPortPlacement(Guid.Parse(psu), Guid.Parse(rail), DiagramPortSide.Right, 100), layout.Ports.Single(p => p.InterfaceId.ToString("D") == rail));
        Assert.IsEmpty(layout.ConnectionRoutes, "No route is stored: the computed paths keep apart by themselves (design QA P2-5).");
        var archive = graph.Connections(graph.SelectedRoot.BlockId);
        CollectionAssert.AreEqual(new[] { "Power", "Rail feed" }, top.LocalDiagram.Connections.Select(c => archive.Inspect(c).Name).ToArray());
        Assert.IsTrue(top.LocalDiagram.Connections.All(c => archive.Requirements(c).Requirements == DiagramRequirements.Empty));
        Assert.AreEqual("Feed the CPU from the rail.", top.LocalDiagram.Notes.Single().Text);
        {
            // Design QA P1-1: the selected Rail feed is drawn in the accent, 3:1 or more on the canvas and never dimmer than the
            // unselected Power. P2-7: both captions are drawn, each clear of blocks, handles, ports and the other. P2-8: after
            // Save, Save is unavailable and looks like Decline again.
            var shot = await Shot("saved");
            var canvasColour = shot.At(saved.CanvasWindowX + (int)saved.CanvasPixelWidth - 12, saved.CanvasWindowY + (int)saved.CanvasPixelHeight - 12);
            var (feedX, feedY) = Canvas(saved, 510, 303); var (powerX, powerY) = Canvas(saved, 425, 177);
            var selectedWire = shot.MostContrasting(feedX - 10, feedY - 2, 20, 5, canvasColour);
            var otherWire = shot.MostContrasting(powerX - 10, powerY - 2, 20, 5, canvasColour);
            double selectedContrast = shot.Contrast("P1-1 selected wire on the canvas", selectedWire, canvasColour);
            Assert.IsTrue(selectedContrast >= 3.0 && selectedContrast >= shot.Contrast("P1-1 unselected wire on the canvas", otherWire, canvasColour),
                $"saved: the selected wire {CapturedWindow.Describe(selectedWire)} stands 3:1 from the canvas and out from the unselected one {CapturedWindow.Describe(otherWire)}.");
            CollectionAssert.AreEquivalent(new[] { "Power", "Rail feed" }, saved.ConnectionCaptions.Where(c => c.Shown).Select(c => c.Label).ToArray(),
                "saved: both connection captions are drawn.");
            VerifyCaptions(shot, saved, "saved", new Dictionary<string, string> { [power.Selection.ConnectionId] = "Power", [feed.Selection.ConnectionId] = "Rail feed" },
                "Power", "Rail feed");
            VerifyCanvasText(saved, "saved");
            var save = Find(saved, "RecursiveSave"); var decline = Find(saved, "RecursiveDecline");
            Assert.IsFalse(save.Enabled || decline.Enabled, "saved: nothing is left to save or decline.");
            Assert.IsTrue(CapturedWindow.Distance(shot.At(save.X + 8, save.Y + save.Height / 2), shot.At(decline.X + 8, decline.Y + decline.Height / 2)) <= 30,
                "saved: an unavailable Save keeps the theme's own disabled look, like Decline.");
        }

        // An agent stores Rail feed's route (the editor stores none of its own): an unlocked channel at x 500, 30 units right of
        // the middle between its ends, level with them. The computed Power keeps apart from it (rule F4a): at the middle, x 470,
        // its last leg would cross the channel, so it runs at x 510, 10 units beside it.
        async Task<P.RecursiveDiagramEditorState> AgentRoute(string step, bool locked, params DiagramPoint[] waypoints)
        {
            var current = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(created.Path, token));
            var agentDraft = current.StartDraft(current.SelectedRoot); var agentView = agentDraft.LocalDiagram.Layout;
            agentDraft = agentDraft with { Diagram = agentDraft.LocalDiagram with { Presentation = agentView with { Routes =
                [new DiagramConnectionRoute(Guid.Parse(feed.Selection.ConnectionId), 1, [.. waypoints], null, locked)] } } };
            var written = current.SaveDraft(current.SelectedRoot, [current.SelectedRoot], agentDraft, Guid.NewGuid(), Guid.NewGuid(), [],
                RecursiveBlockFixture.Origin("Another agent")).Graph;
            await File.WriteAllTextAsync(created.Path, RecursiveBlockGraphXml.Write(written), token);
            Key("r", control: true);
            return await Wait(step, s => !s.Dirty && s.LevelDraft.Scope.Baseline.RevisionId == written.SelectedRoot.RevisionId.ToString("D"));
        }
        await AgentRoute("route-agent", false, new DiagramPoint(500, 230), new DiagramPoint(500, 303));
        var agentFeed = await Drawn(feed.Selection.ConnectionId); var agentPower = await Drawn(power.Selection.ConnectionId);
        CollectionAssert.AreEqual(new[] { (380.0, 230.0), (500.0, 230.0), (500.0, 303.0), (560.0, 303.0) }, agentFeed, "Rail feed runs on the agent's route.");
        CollectionAssert.AreEqual(new[] { (380.0, 177.0), (510.0, 177.0), (510.0, 247.0), (560.0, 247.0) }, agentPower, "Power keeps clear of the stored route.");
        VerifyPathsApart("route-agent", Blocks(await Read()), agentPower, agentFeed);
        savedXml = await File.ReadAllTextAsync(created.Path, token); graph = RecursiveBlockGraphXml.Read(savedXml);

        // A save made against an older file rebases (contract rbg-v2 section 9.1): another editor moved the PSU up and the CPU up
        // and left while this draft moved the PSU down and right. The draft's PSU position is kept with a non-modal notice, the
        // other editor's CPU position is merged in, and the automatic save stores both.
        await Drag(psuX, psuY + 30, psuX + 20, psuY + 50);
        await Wait("psu-moved-here", s => s.Dirty && Placement(s, psu).X == "160");
        var elsewhere = graph.StartDraft(graph.SelectedRoot); var elsewhereView = elsewhere.LocalDiagram.Layout;
        elsewhere = elsewhere with { Diagram = elsewhere.LocalDiagram with { Presentation = elsewhereView with { Blocks = [.. elsewhereView.Blocks.Select(b =>
            b.BlockId.ToString("D") == psu ? b with { Rect = b.Rect with { Y = 110 } }
            : b.BlockId.ToString("D") == cpu ? b with { Rect = b.Rect with { X = 540, Y = 150 } } : b)] } } };
        var movedElsewhere = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], elsewhere, Guid.NewGuid(), Guid.NewGuid(), [],
            RecursiveBlockFixture.Origin("Another agent")).Graph;
        await File.WriteAllTextAsync(created.Path, RecursiveBlockGraphXml.Write(movedElsewhere), token);
        ulong beforeRebase = (await Read()).CompletedSaveCount;
        Key("s", control: true);
        const string keptNotice = "Your layout kept a position that was also moved in the saved design.";
        var rebased = await Wait("layout-rebased", s => s.CompletedSaveCount >= beforeRebase + 2 && !s.Dirty && s.Notice == keptNotice);
        Assert.AreEqual("", rebased.ErrorCode); Assert.AreEqual(keptNotice, rebased.StatusText, "The notice is shown without a dialog.");
        Assert.IsFalse(NativeKeyboard.HasWindow(display, processId, "Resolve changes before saving"), "A layout-only overlap never asks.");
        savedXml = await File.ReadAllTextAsync(created.Path, token); saved = rebased;
        var rebasedGraph = RecursiveBlockGraphXml.Read(savedXml); var rebasedLayout = rebasedGraph.Inspect(rebasedGraph.SelectedRoot).LocalDiagram.Layout;
        Assert.AreEqual(new DiagramRect(160, 150, 240, 140), rebasedLayout.Blocks.Single(b => b.BlockId.ToString("D") == psu).Rect, "The draft's position was saved.");
        Assert.AreEqual(new DiagramRect(540, 150, 280, 170), rebasedLayout.Blocks.Single(b => b.BlockId.ToString("D") == cpu).Rect,
            "The other editor's CPU position was merged in.");
        // The agent's route followed both moves before the automatic save (the regression the A1/A4 re-review fixed: a merged
        // route could keep an end's old height and stay slanted). Moving the PSU here put Rail feed's end at the Rail port (400,
        // 250) and kept the channel 30 right of the middle, x 510; the other editor's CPU move put its end at (540, 263) (on the
        // CPU at (540, 150, 280, 170) Power's end takes 57 and Rail feed's 113, rule F2), and the channel again keeps 30 right of
        // the new middle, x 500 = (400 + 540) / 2 + 30. The saved heights are the resolved heights of the ends, so every leg is
        // level or upright; Power follows the merged positions on its computed path (its end on the PSU at (160, 150) takes 47).
        CollectionAssert.AreEqual(new[] { new DiagramPoint(500, 250), new DiagramPoint(500, 263) }, rebasedLayout.ConnectionRoutes.Single().Points.ToArray(),
            "Rail feed's route followed the PSU moved here and the CPU moved elsewhere before the automatic save.");
        async Task<DiagramPoint[]> Resolved(string connection) => (await Route(await Read(), connection)).GetProperty("points").EnumerateArray()
            .Select(p => new DiagramPoint(decimal.Parse(p.GetProperty("x").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(p.GetProperty("y").GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var rebasedPath = await Resolved(feed.Selection.ConnectionId); var rebasedRoute = rebasedLayout.ConnectionRoutes.Single().Points;
        Assert.AreEqual((rebasedPath[0].Y, rebasedPath[^1].Y), (rebasedRoute[0].Y, rebasedRoute[1].Y),
            "The saved route's heights are the heights of the ends the editor resolves, so every leg is level or upright.");
        var rebasedPower = await Drawn(power.Selection.ConnectionId); var rebasedFeed = await Drawn(feed.Selection.ConnectionId);
        CollectionAssert.AreEqual(new[] { (400.0, 197.0), (470.0, 197.0), (470.0, 207.0), (540.0, 207.0) }, rebasedPower, "Power follows the merged positions.");
        CollectionAssert.AreEqual(new[] { (400.0, 250.0), (500.0, 250.0), (500.0, 263.0), (540.0, 263.0) }, rebasedFeed, "Rail feed follows the merged positions.");
        VerifyPathsApart("layout-rebased", Blocks(rebased), rebasedPower, rebasedFeed);

        // A route an agent locked stays exactly as stored when its ends move. An unlocked channel route stored with its ends' old
        // heights (an older file, or a writer that did not follow the ends) is drawn level with its ends at once, before any
        // edit and without changing anything (rule F4b); moving one of its ends then stores it in line.
        (string, string, string, string) Waypoints(P.RecursiveDiagramEditorState at)
        {
            var route = at.LevelDraft.Scope.LocalDiagram.Presentation.Routes.Single();
            return (route.Waypoints[0].X + "," + route.Waypoints[0].Y, route.Waypoints[1].X + "," + route.Waypoints[1].Y, route.Locked ? "locked" : "unlocked", route.ConnectionId);
        }
        var (cpuNowX, cpuNowY) = (680.0, 235.0);
        await AgentRoute("route-locked", true, new DiagramPoint(500, 250), new DiagramPoint(500, 235));
        await Drag(cpuNowX, cpuNowY, cpuNowX, cpuNowY + 20);
        var lockedMoved = await Wait("locked-route-kept", s => s.Dirty && Placement(s, cpu).Y == "170");
        Assert.AreEqual(("500,250", "500,235", "locked", feed.Selection.ConnectionId), Waypoints(lockedMoved), "A locked route is kept exactly as stored.");
        Key("d", alt: true); await Wait("locked-declined", s => !s.Dirty && Placement(s, cpu).Y == "150");
        var stale = await AgentRoute("route-stale", false, new DiagramPoint(500, 230), new DiagramPoint(500, 275));
        Assert.AreEqual(("500,230", "500,275", "unlocked", feed.Selection.ConnectionId), Waypoints(stale), "Opening the stored route changes nothing.");
        var stalePath = await Resolved(feed.Selection.ConnectionId);
        CollectionAssert.AreEqual(new[] { new DiagramPoint(400, 250), new DiagramPoint(500, 250), new DiagramPoint(500, 263), new DiagramPoint(540, 263) }, stalePath,
            "The stored route is drawn level with both of its ends, the Rail port at 250 and Rail feed's own point on the CPU at 263.");
        await Drag(cpuNowX, cpuNowY, cpuNowX, cpuNowY + 20);
        var repaired = await Wait("stale-route-repaired", s => s.Dirty && Placement(s, cpu).Y == "170");
        Assert.AreEqual(("500,250", "500,283", "unlocked", feed.Selection.ConnectionId), Waypoints(repaired),
            "Moving the CPU puts the route back in line with both ends: the PSU's Rail port at 250 and Rail feed's own point on the CPU at 170 + 113.");
        ulong beforeRepairSave = repaired.CompletedSaveCount;
        Key("s", control: true);
        saved = await Wait("repaired-saved", s => s.CompletedSaveCount > beforeRepairSave && !s.Dirty);
        Assert.AreEqual("", saved.ErrorCode);
        savedXml = await File.ReadAllTextAsync(created.Path, token);
        var repairedGraph = RecursiveBlockGraphXml.Read(savedXml); var repairedLayout = repairedGraph.Inspect(repairedGraph.SelectedRoot).LocalDiagram.Layout;
        CollectionAssert.AreEqual(new[] { new DiagramPoint(500, 250), new DiagramPoint(500, 283) }, repairedLayout.ConnectionRoutes.Single().Points.ToArray());
        var repairedPath = await Resolved(feed.Selection.ConnectionId);
        Assert.AreEqual((repairedPath[0].Y, repairedPath[^1].Y), (repairedLayout.ConnectionRoutes.Single().Points[0].Y, repairedLayout.ConnectionRoutes.Single().Points[1].Y),
            "The repaired route's heights are the resolved heights of its ends.");

        // A rebase keeps another writer's route as that writer drew it (contract rbg-v2 section 4.8: only one side changed it, so
        // take that side). This draft only comments on the CPU. Meanwhile another editor moved the CPU 40 right and, as this editor
        // does on a move, moved Rail feed's channel with it: from x 500, 30 right of the middle (400 + 540) / 2, to x 520, 30 right
        // of the new middle (400 + 580) / 2. The automatic save keeps x 520; shifting that channel again by the move would store x 540.
        await At(cpuNowX, cpuNowY + 20);
        await Wait("cpu-selected-for-comment", s => s.Draft.Baseline.BlockId == cpu && s.ConnectionDraft is null && !s.Dirty);
        Key("4", control: true); Type("Keep the CPU cool.");
        await Wait("cpu-comment", s => s.Dirty && s.LevelDraft.Scope.LocalDiagram.Annotations.Any(n => n.TargetId == cpu && n.Text == "Keep the CPU cool."));
        var routeWriter = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(created.Path, token));
        var routeDraft = routeWriter.StartDraft(routeWriter.SelectedRoot); var routeView = routeDraft.LocalDiagram.Layout;
        routeDraft = routeDraft with { Diagram = routeDraft.LocalDiagram with { Presentation = routeView with {
            Blocks = [.. routeView.Blocks.Select(b => b.BlockId.ToString("D") == cpu ? b with { Rect = b.Rect with { X = 580 } } : b)],
            Routes = [.. routeView.ConnectionRoutes.Select(r => r with { Waypoints = [new DiagramPoint(520, 250), new DiagramPoint(520, 283)] })] } } };
        var routeWritten = routeWriter.SaveDraft(routeWriter.SelectedRoot, [routeWriter.SelectedRoot], routeDraft, Guid.NewGuid(), Guid.NewGuid(), [],
            RecursiveBlockFixture.Origin("Another editor")).Graph;
        await File.WriteAllTextAsync(created.Path, RecursiveBlockGraphXml.Write(routeWritten), token);
        ulong beforeRouteRebase = (await Read()).CompletedSaveCount;
        Key("s", control: true);
        var routeRebased = await Wait("route-rebased", s => s.CompletedSaveCount >= beforeRouteRebase + 2 && !s.Dirty);
        Assert.AreEqual("", routeRebased.ErrorCode);
        Assert.AreNotEqual(keptNotice, routeRebased.Notice, "Nothing both sides moved, so no position was kept over the saved design.");
        Assert.IsFalse(NativeKeyboard.HasWindow(display, processId, "Resolve changes before saving"), "A change on each side never asks.");
        savedXml = await File.ReadAllTextAsync(created.Path, token); saved = routeRebased;
        var routeGraph = RecursiveBlockGraphXml.Read(savedXml); var routeTop = routeGraph.Inspect(routeGraph.SelectedRoot);
        Assert.AreEqual(new DiagramRect(580, 170, 280, 170), routeTop.LocalDiagram.Layout.Blocks.Single(b => b.BlockId.ToString("D") == cpu).Rect,
            "The other editor's CPU position was merged in.");
        CollectionAssert.AreEqual(new[] { new DiagramPoint(520, 250), new DiagramPoint(520, 283) }, routeTop.LocalDiagram.Layout.ConnectionRoutes.Single().Points.ToArray(),
            "Rail feed keeps the channel the other editor drew; only a route this draft drew keeps its offset when its ends move.");
        Assert.AreEqual("Keep the CPU cool.", routeTop.LocalDiagram.Notes.Single(n => n.Target.TargetId?.ToString("D") == cpu).Text, "This draft's comment was saved.");
        var mergedPath = await Resolved(feed.Selection.ConnectionId);
        Assert.AreEqual((mergedPath[0].Y, mergedPath[^1].Y), (routeTop.LocalDiagram.Layout.ConnectionRoutes.Single().Points[0].Y,
            routeTop.LocalDiagram.Layout.ConnectionRoutes.Single().Points[1].Y), "The kept route still runs level from each of its ends.");

        // Decline discards a later layout edit and writes nothing.
        await Drag(psuX, psuY + 30, psuX + 20, psuY + 50);
        await Wait("psu-moved", s => s.Dirty && Placement(s, psu).X == "180");
        Key("d", alt: true); await Wait("declined", s => !s.Dirty && Placement(s, psu).X == "160");
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token));

        // A compact window keeps the toolbar strip and the palette usable, and re-fits so every block stays in view beside the
        // palette (the canvas cannot scroll); View hides and restores the palette.
        bool BlocksInView(P.RecursiveDiagramEditorState at)
        {
            int paletteRight = at.Controls.Where(c => c.Name.StartsWith("DiagramPalette", StringComparison.Ordinal) && c.Shown).Select(c => c.X + c.Width)
                .DefaultIfEmpty(at.CanvasWindowX).Max() - at.CanvasWindowX;
            return at.LevelDraft.Scope.LocalDiagram.Presentation.Blocks.All(b =>
            {
                double Units(string value) => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                double left = (Units(b.Rect.X) - at.CanvasOriginX) * at.CanvasScale, top = (Units(b.Rect.Y) - at.CanvasOriginY) * at.CanvasScale;
                return left >= paletteRight && top >= 0 && left + Units(b.Rect.Width) * at.CanvasScale <= at.CanvasPixelWidth
                    && top + Units(b.Rect.Height) * at.CanvasScale <= at.CanvasPixelHeight;
            });
        }
        // The level frame and the names of its boundary ports, as the editor reports them drawn, are right of the palette and
        // inside the canvas too ("DC input" once sat half under the palette while every block was in view).
        bool FrameInView(P.RecursiveDiagramEditorState at)
        {
            int paletteRight = at.Controls.Where(c => c.Name.StartsWith("DiagramPalette", StringComparison.Ordinal) && c.Shown).Select(c => c.X + c.Width)
                .DefaultIfEmpty(at.CanvasWindowX).Max();
            bool Clear(P.DiagramControlRect? rect) => rect is { Shown: true } && rect.X >= paletteRight && rect.Width > 0;
            return Clear(at.LevelFrame) && at.BoundaryPortNames.Count == at.LevelDraft.Scope.LocalDiagram.Interfaces.Count
                && at.BoundaryPortNames.All(Clear);
        }
        void AssertFrameInView(P.RecursiveDiagramEditorState at, string step)
        {
            Assert.IsNotNull(at.LevelFrame, step + ": the level's stored frame is reported as drawn.");
            CollectionAssert.AreEqual(at.LevelDraft.Scope.LocalDiagram.Interfaces.Select(i => i.Name).ToArray(), at.BoundaryPortNames.Select(n => n.Label).ToArray(),
                step + ": every boundary port's name is reported as drawn.");
            Assert.IsTrue(FrameInView(at), step + ": the frame and every boundary port name lie inside the canvas, right of the palette.");
        }
        ulong beforeCompact = (await Read()).ViewRevision;
        NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, resizeWidth: 1100, resizeHeight: 760);
        var compact = await Wait("compact", s => s.Rendered && s.ViewRevision > beforeCompact && s.CanvasPixelWidth < 800 && BlocksInView(s) && FrameInView(s));
        AssertFrameInView(compact, "compact");
        Assert.HasCount(2, compact.LevelDraft.Scope.LocalDiagram.Presentation.Blocks);
        var palette = Find(compact, "DiagramPaletteUndo");
        Assert.IsTrue(palette.Shown && palette.Y + palette.Height <= compact.CanvasWindowY + (int)compact.CanvasPixelHeight, "The palette fits the compact canvas.");
        Assert.IsTrue(Find(compact, "RecursiveToolDelete").Shown, "The toolbar strip stays visible in a compact window.");
        {
            // The compact window keeps the same measured design: the active tool, the selection's contrast, and text drawn whole
            // with captions clear of blocks, handles and ports (design QA P1-1, P2-1 to P2-4 and P2-7).
            var shot = await Shot("compact");
            VerifyToolStyling(shot, compact, "compact", "Select");
            VerifySelectionContrast(shot, compact, "compact", compact.Draft.Baseline.BlockId);
            VerifyCanvasText(compact, "compact");
            // Design QA round 2, R2-P2-8: a caption with room is drawn whole in the compact window too ("Rail feed" was cut to
            // "Rail f…" beside its short first leg although the canvas around it was empty).
            VerifyCaptions(shot, compact, "compact", new Dictionary<string, string> { [power.Selection.ConnectionId] = "Power", [feed.Selection.ConnectionId] = "Rail feed" },
                "Power", "Rail feed");
        }
        await Press("RecursiveToolConnect"); await Wait("compact-connect", s => Tool(s, "connect", "RecursiveToolConnect", "DiagramPaletteConnect"));
        await Press("RecursiveToolSelect"); await Wait("compact-select", s => Tool(s, "select", "RecursiveToolSelect", "DiagramPaletteSelect"));
        Key("v", alt: true);
        using (var menu = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            menu.CancelAfter(TimeSpan.FromSeconds(15)); int count = 0;
            do { NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, observePopupCount: value => count = value); if (count == 0) await Task.Delay(50, menu.Token); }
            while (count == 0);
        }
        Key("p");
        var hidden = await Wait("palette-hidden", s => !s.PaletteShown && !Find(s, "DiagramPaletteSelect").Shown);
        await Capture("palette-hidden");
        Key("v", alt: true);
        using (var menu = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            menu.CancelAfter(TimeSpan.FromSeconds(15)); int count = 0;
            do { NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, observePopupCount: value => count = value); if (count == 0) await Task.Delay(50, menu.Token); }
            while (count == 0);
        }
        Key("p"); await Wait("palette-shown", s => s.PaletteShown && Find(s, "DiagramPaletteSelect").Shown);
        _ = hidden;
        NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, resizeWidth: 1536, resizeHeight: 1024);
        var expanded = await Wait("expanded", s => s.Rendered && s.CanvasPixelWidth > 900 && BlocksInView(s) && FrameInView(s));
        AssertFrameInView(expanded, "expanded");

        // Close, make the saved file read-only and reopen it. The level comes back exactly as saved, from its stored layout, and
        // a read-only file opens for viewing and editing but not saving (contract rbg-v2 section 9.1): Save stays unavailable
        // with the reason in the status bar, Ctrl+S reports diagram_file_read_only, and the file is never written.
        async Task Closed()
        {
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(token); closing.CancelAfter(TimeSpan.FromSeconds(15));
            while (NativeKeyboard.HasWindow(display, processId, title)) await Task.Delay(50, closing.Token);
        }
        Key("w", control: true); await Closed();
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token), "Closing a clean window writes nothing.");
        UnixFileMode? writableMode = null;
        if (!OperatingSystem.IsWindows())
        {
            writableMode = File.GetUnixFileMode(created.Path);
            File.SetUnixFileMode(created.Path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        try
        {
            var reopened = await client.CallToolAsync("kicad_diagram_open", new Dictionary<string, object?> { ["instanceId"] = instanceId,
                ["repositoryRoot"] = created.RepositoryRoot, ["sourcePath"] = created.Path, ["documentId"] = created.DocumentId }, cancellationToken: token);
            Assert.IsFalse(reopened.IsError == true, "A read-only diagram still opens.");
            var again = await Wait("reopened", s => s.Ready && s.Rendered);
            Assert.AreEqual(saved.SourceToken, again.SourceToken);
            var storedPath = await Route(again, top.LocalDiagram.Connections[1].ConnectionId.ToString("D"));
            Assert.AreEqual("RPS_PLACED", storedPath.GetProperty("source").GetString(), "Rail feed comes back on its stored route.");
            CollectionAssert.AreEqual(new[] { "400,250", "520,250", "520,283", "580,283" }, storedPath.GetProperty("points").EnumerateArray()
                .Select(p => p.GetProperty("x").GetString() + "," + p.GetProperty("y").GetString()).ToArray());
            var reopenedView = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, instanceId + "-drawing-observation.json"), token)).RootElement
                .GetProperty("resolvedLayout");
            string powerId = top.LocalDiagram.Connections[0].ConnectionId.ToString("D");
            Assert.AreEqual("RPS_FALLBACK", reopenedView.GetProperty("routes").EnumerateArray().Single(r => r.GetProperty("connectionId").GetString() == powerId)
                .GetProperty("source").GetString(), "Power keeps its computed path.");
            Assert.IsTrue(reopenedView.GetProperty("blocks").EnumerateArray().All(b => b.GetProperty("source").GetString() == "RPS_PLACED"));
            Assert.AreEqual("RPS_PLACED", reopenedView.GetProperty("frameSource").GetString());
            Assert.AreEqual(0U, reopenedView.GetProperty("dormantEntries").GetUInt32());
            await Capture("reopened");
            if (writableMode is not null)
            {
                const string readOnlyStatus = "This diagram file is read-only; changes cannot be saved.";
                Assert.IsFalse(again.SourceWritable); Assert.AreEqual(readOnlyStatus, again.StatusText);
                await Drag(psuX, psuY + 30, psuX + 20, psuY + 50);
                var lockedDraft = await Wait("read-only-draft", s => s.Dirty && Placement(s, psu).X == "180");
                Assert.IsFalse(Find(lockedDraft, "RecursiveSave").Enabled, "Save stays unavailable for a read-only file.");
                Assert.IsTrue(Find(lockedDraft, "RecursiveDecline").Enabled);
                Assert.AreEqual(readOnlyStatus, lockedDraft.StatusText, "The status bar explains why Save is unavailable.");
                // Typing keeps that explanation: a comment typed on the moved PSU changes the draft, and the status bar still says why
                // Save is unavailable (every change to the draft shows the same status line).
                Key("4", control: true); Type("Check the rail current.");
                var typedReadOnly = await Wait("read-only-typed", s => s.LevelDraft.Scope.LocalDiagram.Annotations.Any(n => n.TargetId == psu
                    && n.Text == "Check the rail current."));
                Assert.AreEqual(readOnlyStatus, typedReadOnly.StatusText, "Typing keeps the read-only explanation in the status bar.");
                Assert.IsFalse(Find(typedReadOnly, "RecursiveSave").Enabled, "Save stays unavailable after typing.");
                // The same holds for the other typing paths: a requirement, a connection caption and a component choice.
                void StillReadOnly(P.RecursiveDiagramEditorState at, string what)
                {
                    Assert.AreEqual(readOnlyStatus, at.StatusText, "Typing " + what + " keeps the read-only explanation in the status bar.");
                    Assert.IsFalse(Find(at, "RecursiveSave").Enabled, "Save stays unavailable after typing " + what + ".");
                }
                await Press("RecursiveRequirements0"); await Wait("read-only-requirement-focused", s => s.FocusedControl == "RecursiveRequirements0");
                Key("End"); Type(" Now");
                StillReadOnly(await Wait("read-only-requirement-typed", s => s.Draft.Fields.General == "Supply the CPU. Now"), "a requirement");
                await ClickRoute(feed.Selection.ConnectionId); await Wait("read-only-feed-selected", s => s.ConnectionDraft?.Name == "Rail feed");
                await Press("RecursiveConnectionCaption"); await Wait("read-only-caption-focused", s => s.FocusedControl == "RecursiveConnectionCaption");
                Key("End"); Type("s");
                StillReadOnly(await Wait("read-only-caption-typed", s => s.ConnectionDraft?.Name == "Rail feeds"), "a connection caption");
                var (movedX, movedY) = Centre(await Read(), psu);
                await At(movedX, movedY); await Wait("read-only-psu-selected", s => s.Draft.Baseline.BlockId == psu && s.ConnectionDraft is null);
                await Press("RecursiveAddDetail");
                using (var menu = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    menu.CancelAfter(TimeSpan.FromSeconds(15)); int count = 0;
                    do { NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, observePopupCount: value => count = value); if (count == 0) await Task.Delay(50, menu.Token); }
                    while (count == 0);
                }
                Key("Home"); Key("Return");
                await Wait("read-only-purpose-open", s => s.FacetEditor == "purpose" && s.FocusedControl == "RecursiveFacetValue");
                Type("Power");
                StillReadOnly(await Wait("read-only-purpose-typed", s => s.Draft.Definition?.Purpose is { } purpose && purpose.Values.SequenceEqual(["Power"])),
                    "a component choice");
                Key("s", control: true);
                var lockedSave = await Wait("read-only-save", s => s.ErrorCode == "diagram_file_read_only");
                Assert.IsTrue(lockedSave.Dirty); Assert.AreEqual(lockedDraft.CompletedSaveCount, lockedSave.CompletedSaveCount, "Nothing was sent to save.");
                Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token), "A read-only file is never written.");
                await Capture("read-only");
                Key("d", alt: true); await Wait("read-only-declined", s => !s.Dirty && Placement(s, psu).X == "160" && s.ErrorCode == "");
            }
            Key("w", control: true); await Closed();
            Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token));
        }
        finally { if (writableMode is { } mode && !OperatingSystem.IsWindows()) File.SetUnixFileMode(created.Path, mode); }
    }

    /// <summary>Round A4 option 3 (owner decision n0b2a908b00e78823) through the rendered editor, on the level the drawing journey
    /// saved. A caption-only block shows no chips. The one quiet Add detail action gives a facet its first value; each chosen or
    /// candidate facet becomes a chip on its block with a Review facets link; the inspector's facet overview lists only facets
    /// that have a value (owner decision n98a3f3c41084f0ed); one facet's detail edits its state, value, reason and strength,
    /// returns it to unknown or clears it; an entry that cannot be kept is refused with a notice and never saved. Save, Decline,
    /// undo, a compact window and a reopen keep the same choices, and a chosen name never adds ports, component bindings or a
    /// physical allocation (decision na7aa99408263431e).</summary>
    private static async Task VerifyBlockChoices(McpClient client, NativeClient native, int processId, string display, CreatedDiagram created,
        string instanceId, string evidence, CancellationToken token)
    {
        const string title = "Structural diagram";
        var document = new P.ReadRecursiveDiagramEditor { DocumentId = created.DocumentId };
        Task<P.RecursiveDiagramEditorState> Read() => native.InvokeAsync<P.ReadRecursiveDiagramEditor, P.RecursiveDiagramEditorState>(document, token);
        async Task<P.RecursiveDiagramEditorState> Wait(string step, Func<P.RecursiveDiagramEditorState, bool> condition)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            P.RecursiveDiagramEditorState current = new();
            try
            {
                while (true)
                {
                    current = await Read();
                    if (!current.Busy && !current.Dragging && condition(current)) return current;
                    await Task.Delay(50, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-choices-timeout-" + step + ".json"), SchematicJson.Formatter.Format(current), token);
                await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-choices-timeout-" + step + ".png"), token);
                throw new AssertFailedException("The component-choice step '" + step + "' did not reach its expected state; its state and screen are retained.");
            }
        }
        void Key(string key, bool control = false, bool alt = false) => NativeKeyboard.SchematicShortcut(display, processId, key, title, control, false, altKey: alt);
        void Type(string value) { foreach (char character in value) Key(character.ToString()); }
        void Click(int x, int y) => NativeKeyboard.SchematicShortcut(display, processId, "click", title, false, true, clickFromLeft: x, clickFromTop: y);
        P.DiagramControlRect Find(P.RecursiveDiagramEditorState at, string name) => at.Controls.Single(c => c.Name == name);
        async Task Press(string name)
        {
            // The inspector re-lays out after the detail opens or closes; press a control only where two readings agree.
            var at = await Read(); var control = Find(at, name);
            using (var settle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                settle.CancelAfter(TimeSpan.FromSeconds(5));
                while (true)
                {
                    await Task.Delay(120, settle.Token);
                    var again = await Read(); var moved = Find(again, name);
                    if (moved.Equals(control) && Find(again, "RecursiveInspector").Equals(Find(at, "RecursiveInspector"))) break;
                    at = again; control = moved;
                }
            }
            Assert.IsTrue(control.Shown && control.Enabled, name + " must be shown and enabled before it is pressed.");
            if (name.StartsWith("RecursiveFacet", StringComparison.Ordinal) || name == "RecursiveAddDetail")
            {
                var inspector = Find(at, "RecursiveInspector");
                int x = control.X + control.Width / 2, y = control.Y + control.Height / 2;
                Assert.IsTrue(x >= inspector.X && x < inspector.X + inspector.Width && y >= inspector.Y && y < inspector.Y + inspector.Height,
                    name + " must be scrolled into the inspector's view before it is pressed.");
            }
            Click(control.X + control.Width / 2, control.Y + control.Height / 2);
        }
        async Task Popup()
        {
            using var menu = CancellationTokenSource.CreateLinkedTokenSource(token); menu.CancelAfter(TimeSpan.FromSeconds(15)); int count = 0;
            try
            {
                do { NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, observePopupCount: value => count = value); if (count == 0) await Task.Delay(50, menu.Token); }
                while (count == 0);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-choices-timeout-popup.json"), SchematicJson.Formatter.Format(await Read()), token);
                await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-choices-timeout-popup.png"), token);
                throw new AssertFailedException("The pressed control did not open its list; the state and screen are retained.");
            }
        }
        // The Add detail menu lists the block's facets without a value, in facet order (requirement boxes are under Add requirement).
        async Task AddDetail(params string[] keys) { await Press("RecursiveAddDetail"); await Popup(); foreach (string key in keys) Key(key); Key("Return"); }
        // The strength choices stay in one row, unclipped and without overlap, at every inspector width; brief tells which labels
        // show. Whenever the full labels show, the row fits the detail's column with the rule's 4 DIP to spare (the fixture display
        // is at 96 DPI, so 4 pixels): the build that failed in clear-facet-overlap-11e548 showed its 369-pixel row of full labels
        // in a 365-pixel column, and a build that kept only 2 pixels spare would show them in a 371-pixel column.
        string[] StrengthLabels(P.RecursiveDiagramEditorState at) =>
            new[] { "Information", "Preference", "Requirement" }.Select(n => Find(at, "RecursiveFacetStrength" + n).Label).ToArray();
        void StrengthRow(P.RecursiveDiagramEditorState at, string step, bool? brief)
        {
            var inspector = Find(at, "RecursiveInspector");
            var row = new[] { "Information", "Preference", "Requirement" }.Select(n => Find(at, "RecursiveFacetStrength" + n)).ToArray();
            Assert.IsTrue(row.All(c => c.Shown), step + ": every strength choice is shown.");
            Assert.IsTrue(row.All(c => c.Y == row[0].Y), step + ": the strength choices stay in one row; their labels collapse before the row wraps.");
            Assert.IsTrue(row[0].X + row[0].Width <= row[1].X && row[1].X + row[1].Width <= row[2].X, step + ": the strength choices do not overlap.");
            Assert.IsTrue(row[0].X >= inspector.X && row[2].X + row[2].Width <= inspector.X + inspector.Width, step + ": no strength choice is clipped.");
            string[] full = ["Information", "Preference", "Requirement"], shortLabels = ["Info", "Pref.", "Req."];
            var labels = StrengthLabels(at);
            if (brief is bool collapsed) CollectionAssert.AreEqual(collapsed ? shortLabels : full, labels, step + ": the strength labels.");
            else Assert.IsTrue(labels.SequenceEqual(full) || labels.SequenceEqual(shortLabels), step + ": the strength labels are all full or all short.");
            if (labels.SequenceEqual(full) && at.Controls.Where(c => c.Shown && c.Name is "RecursiveFacetValue" or "RecursiveFacetCandidates" or "RecursiveFacetReason")
                .ToArray() is [var column])
            {
                int rowWidth = row[2].X + row[2].Width - row[0].X;
                Assert.IsTrue(rowWidth + 4 <= column.Width,
                    $"{step}: the full strength labels ({rowWidth} pixels) show only with 4 pixels to spare in the detail's {column.Width}-pixel column.");
            }
        }
        // The interactive controls of a facet's detail, where the editor drew them.
        string[] detailNames = ["RecursiveFacetBack", "RecursiveFacetStateChosen", "RecursiveFacetStateCandidate", "RecursiveFacetStateUnknown",
            "RecursiveFacetValue", "RecursiveFacetCandidates", "RecursiveFacetReason", "RecursiveFacetStrengthInformation",
            "RecursiveFacetStrengthPreference", "RecursiveFacetStrengthRequirement", "RecursiveFacetClear"];
        P.DiagramControlRect[] DetailControls(P.RecursiveDiagramEditorState at) => detailNames.Select(n => Find(at, n)).Where(c => c.Shown).ToArray();
        async Task<P.RecursiveDiagramEditorState> DetailSettled(string step)
        {
            // The inspector lays out again once a label collapses or its scroll bar shows; measure once two readings agree.
            var at = await Read();
            using var settle = CancellationTokenSource.CreateLinkedTokenSource(token); settle.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    await Task.Delay(120, settle.Token);
                    var again = await Read();
                    if (DetailControls(again).SequenceEqual(DetailControls(at)) && Find(again, "RecursiveInspector").Equals(Find(at, "RecursiveInspector"))) return again;
                    at = again;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-choices-timeout-" + step + ".json"), SchematicJson.Formatter.Format(at), token);
                await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-choices-timeout-" + step + ".png"), token);
                throw new AssertFailedException("The facet detail at '" + step + "' was still moving after 5 seconds; its last state and screen are retained.");
            }
        }
        // Measured from the rectangles the editor reports: every control of the detail keeps a size, Clear facet is as tall as Back to
        // facet overview, no two controls intersect, none is clipped at the inspector's sides and every choice lies within the
        // detail's column. The entry lies below the state choices, the strength choices below the entry, and Clear facet below the
        // strength choices, however those rows wrap or collapse their labels. Returns the closest two controls' distance and
        // Clear facet's distance below the lowest strength choice.
        (int Closest, int ClearBelow) VerifyDetailGeometry(P.RecursiveDiagramEditorState at, string step)
        {
            static string Box(P.DiagramControlRect c) => $"{c.Name} ({c.X}, {c.Y}, {c.Width} x {c.Height})";
            // The distance between two rectangles along the axis that separates them; negative when they intersect.
            static int Gap(P.DiagramControlRect a, P.DiagramControlRect b) =>
                Math.Max(Math.Max(b.X - (a.X + a.Width), a.X - (b.X + b.Width)), Math.Max(b.Y - (a.Y + a.Height), a.Y - (b.Y + b.Height)));
            var inspector = Find(at, "RecursiveInspector");
            var shown = DetailControls(at);
            // A control squeezed to nothing cannot be seen or pressed: in the failing capture clear-facet-overlap-11e548 the editor
            // reported Clear facet 0 pixels high below the wrapped strength row, and the click on its centre met empty space.
            foreach (var control in shown)
                Assert.IsTrue(control.Width > 0 && control.Height > 0, $"{step}: {Box(control)} keeps a size.");
            var back = Find(at, "RecursiveFacetBack"); var clear = Find(at, "RecursiveFacetClear");
            Assert.IsTrue(back.Shown, step + ": Back to facet overview is shown while the overview lists facets.");
            Assert.IsTrue(clear.Shown, step + ": Clear facet is shown for a facet with a value.");
            // Clear facet keeps at least a whole line of text: it is as tall as the inspector's one-line text (the saved version line),
            // whichever font draws the glyph of Back to facet overview.
            var textLine = Find(at, "RecursiveSavedVersion");
            Assert.IsTrue(textLine.Height > 0 && clear.Height >= textLine.Height,
                $"{step}: {Box(clear)} is at least as tall as a line of the inspector's text ({textLine.Height} pixels).");
            int closest = int.MaxValue;
            for (int i = 0; i < shown.Length; ++i)
                for (int j = i + 1; j < shown.Length; ++j)
                {
                    int gap = Gap(shown[i], shown[j]); closest = Math.Min(closest, gap);
                    Assert.IsTrue(gap >= 0, $"{step}: {Box(shown[i])} and {Box(shown[j])} intersect.");
                }
            foreach (var control in shown)
                Assert.IsTrue(control.X >= inspector.X && control.X + control.Width <= inspector.X + inspector.Width,
                    $"{step}: {Box(control)} is not clipped at the inspector's sides ({inspector.X} to {inspector.X + inspector.Width}).");
            var states = shown.Where(c => c.Name.StartsWith("RecursiveFacetState", StringComparison.Ordinal)).ToArray();
            var strengths = shown.Where(c => c.Name.StartsWith("RecursiveFacetStrength", StringComparison.Ordinal)).ToArray();
            var entries = shown.Where(c => c.Name is "RecursiveFacetValue" or "RecursiveFacetCandidates" or "RecursiveFacetReason").ToArray();
            Assert.HasCount(3, states, step + ": the three state choices are shown.");
            Assert.HasCount(3, strengths, step + ": the three strength choices are shown.");
            Assert.HasCount(1, entries, step + ": the one entry the state asks for is shown.");
            // The entry fills the detail's column, so the choices wrap within its edges.
            var entry = entries[0];
            foreach (var choice in states.Concat(strengths))
                Assert.IsTrue(choice.X >= entry.X && choice.X + choice.Width <= entry.X + entry.Width,
                    $"{step}: {Box(choice)} lies within the detail's column ({entry.X} to {entry.X + entry.Width}).");
            Assert.IsTrue(entry.Y >= states.Max(c => c.Y + c.Height), $"{step}: {Box(entry)} lies below every state choice.");
            Assert.IsTrue(strengths.Min(c => c.Y) >= entry.Y + entry.Height, $"{step}: the strength choices lie below {Box(entry)}.");
            int below = clear.Y - strengths.Max(c => c.Y + c.Height);
            Assert.IsTrue(below >= 4, $"{step}: {Box(clear)} starts {below} pixels below the lowest strength choice, at least 4.");
            return (closest, below);
        }
        // The same, measured in one capture as well: each choice shows its label where it is reported, and Clear facet looks like an
        // action (design QA P2-10).
        void VerifyDetailLayout(CapturedWindow shot, P.RecursiveDiagramEditorState at, string step)
        {
            var (closest, below) = VerifyDetailGeometry(at, step);
            shot.Record("Facet detail: closest two interactive controls (px)", closest);
            shot.Record("Clear facet below the lowest strength choice (px)", below);
            var inspector = Find(at, "RecursiveInspector");
            // Each choice is drawn where the editor reports it: its label's ink lies in the right two thirds of its rectangle (the
            // indicator takes the left), measured against the inspector beside it. The label's letters are separate runs of ink over
            // 12 pixels or more, which the indicator alone, a ring at most a few pixels into that part, cannot make.
            foreach (var choice in DetailControls(at).Where(c => c.Name.StartsWith("RecursiveFacetState", StringComparison.Ordinal)
                || c.Name.StartsWith("RecursiveFacetStrength", StringComparison.Ordinal)))
            {
                var page = shot.At(inspector.X + 3, choice.Y + choice.Height / 2);
                int textFrom = choice.X + choice.Width / 3;
                var ink = shot.InkRuns(textFrom, choice.Y, choice.X + choice.Width - textFrom, choice.Height, page, rows: false, minimum: 2.0);
                Assert.IsTrue(ink.Count >= 2 && ink[^1].Last - ink[0].First + 1 >= 12,
                    $"{step}: {choice.Name} ({choice.X}, {choice.Y}, {choice.Width} x {choice.Height}) shows its label where it is reported "
                    + $"({ink.Count} runs of ink over {(ink.Count == 0 ? 0 : ink[^1].Last - ink[0].First + 1)} pixels).");
            }
            VerifyLink(shot, Find(at, "RecursiveFacetClear"), Find(at, "RecursiveSavedVersion"), step, "Clear facet");
        }
        async Task<P.RecursiveDiagramEditorState> MoveSash(string step, int toX, Func<P.RecursiveDiagramEditorState, bool> reached)
        {
            var sash = Find(await Read(), "RecursiveInspectorSash");
            Assert.IsTrue(sash.Shown, "The splitter between the canvas and the inspector is shown.");
            int y = sash.Y + sash.Height / 2;
            NativeKeyboard.SchematicShortcut(display, processId, "drag", title, false, true, clickFromLeft: sash.X + sash.Width / 2, clickFromTop: y,
                dragToLeft: toX, dragToTop: y);
            // The display plays the drag's motions and release with delays after the call returns, so the step is complete only once
            // the sash has stopped moving; a next drag started earlier would press where the sash no longer is.
            int? lastX = null; var since = DateTime.UtcNow;
            return await Wait(step, s =>
            {
                int x = Find(s, "RecursiveInspectorSash").X;
                if (lastX != x) { lastX = x; since = DateTime.UtcNow; return false; }
                return DateTime.UtcNow - since >= TimeSpan.FromMilliseconds(400) && reached(s);
            });
        }
        async Task Capture(string name)
        {
            await Task.Delay(FadeSettleMilliseconds, token);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-choices-" + name + ".png"), token);
        }
        async Task<CapturedWindow> Shot(string name)
        {
            await Capture(name);
            return await CapturedWindow.LoadAsync(Path.Combine(evidence, instanceId + "-choices-" + name + ".png"), WindowOrigin(display, processId, title), token);
        }
        async Task Closed()
        {
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(token); closing.CancelAfter(TimeSpan.FromSeconds(15));
            while (NativeKeyboard.HasWindow(display, processId, title)) await Task.Delay(50, closing.Token);
        }
        async Task<P.RecursiveDiagramEditorState> Open(string step)
        {
            var opened = await client.CallToolAsync("kicad_diagram_open", new Dictionary<string, object?> { ["instanceId"] = instanceId,
                ["repositoryRoot"] = created.RepositoryRoot, ["sourcePath"] = created.Path, ["documentId"] = created.DocumentId }, cancellationToken: token);
            Assert.IsFalse(opened.IsError == true, "The saved diagram opens again.");
            return await Wait(step, s => s.Ready && s.Rendered);
        }
        async Task SelectBlock(string step, string block)
        {
            var at = await Read();
            var rect = at.LevelDraft.Scope.LocalDiagram.Presentation.Blocks.Single(b => b.BlockId == block).Rect;
            double Units(string value) => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            // Just below the caption on the left: inside the block, clear of its chips, link, ports and handles.
            double x = Units(rect.X) + 30, y = Units(rect.Y) + Math.Min(Units(rect.Height) / 3, 36);
            Click((int)Math.Round(at.CanvasWindowX + (x - at.CanvasOriginX) * at.CanvasScale), (int)Math.Round(at.CanvasWindowY + (y - at.CanvasOriginY) * at.CanvasScale));
            await Wait(step, s => s.Draft.Baseline.BlockId == block && s.ConnectionDraft is null && s.SelectedInterfaceId == "");
        }
        P.DiagramCanvasBlockChips? Chips(P.RecursiveDiagramEditorState at, string block) => at.BlockChips.SingleOrDefault(b => b.BlockId == block);
        string[] ChipTexts(P.RecursiveDiagramEditorState at, string block) => Chips(at, block)?.Chips.Select(c => c.Text).ToArray() ?? [];
        P.DefinitionTextChoiceData? Facet(P.RecursiveDiagramEditorState at, Func<P.BlockDefinitionData, P.DefinitionTextChoiceData?> facet) =>
            at.Draft.Definition is { } definition ? facet(definition) : null;
        // Each chosen or candidate facet of a block is visible: a chip inside the canvas, or (when it has no chip) a state mark
        // inside the canvas right of and beside the caption, or a shown "+N more" chip. The hidden count is exactly the facets
        // without a chip, and marks appear only when no chip and no "+N more" chip fits.
        void VerifyChoicesVisible(P.RecursiveDiagramEditorState at, string step, string blockId, params (string Facet, P.DefinitionChoiceStateData State)[] expected)
        {
            var block = Chips(at, blockId) ?? throw new AssertFailedException(step + ": the block reports its component choices.");
            var caption = block.Caption;
            Assert.IsTrue(caption.Shown && caption.Width > 0, step + ": the block's caption is drawn inside the canvas.");
            Assert.IsTrue(block.Chips.All(c => c.Rect.Shown), step + ": drawn chips lie inside the canvas.");
            // A chip is never squeezed to nothing: it keeps its state mark (chip height - 9 pixels, with 6-pixel margins) and at least
            // 40 pixels of text with an 8-pixel margin, so it is at least its height + 51 pixels wide. A row the port names make
            // narrower than that holds no chip, and its facet goes behind "+N more".
            foreach (var chip in block.Chips)
                Assert.IsTrue(chip.Rect.Width >= chip.Rect.Height + 51 && chip.Text.Length > 0,
                    step + ": the " + chip.Facet + " chip is " + chip.Rect.Width + " pixels wide, too narrow to show its text.");
            var withoutChip = expected.Where(e => !block.Chips.Any(c => c.Facet == e.Facet && c.State == e.State)).ToArray();
            Assert.AreEqual((uint)withoutChip.Length, block.HiddenChips, step + ": the hidden count is exactly the choices without a chip.");
            Assert.AreEqual(expected.Length - withoutChip.Length, block.Chips.Count, step + ": no chip is drawn for anything else.");
            bool more = block.More is { Shown: true };
            // The names of the block's ports drawn inside its edge stay readable: no chip, "+N more", mark or link covers one.
            static bool Apart(P.DiagramControlRect a, P.DiagramControlRect b) =>
                a.X + a.Width <= b.X || b.X + b.Width <= a.X || a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y;
            var drawnParts = block.Chips.Select(c => c.Rect).Concat(block.Marks.Select(m => m.Rect))
                .Concat(new[] { block.More, block.ReviewFacets }.Where(r => r is not null)).ToArray();
            foreach (var name in block.PortNames)
                Assert.IsTrue(drawnParts.All(part => Apart(part, name)), step + ": the chips keep clear of a port name drawn inside the block.");
            if (block.Marks.Count != 0)
                Assert.IsTrue(block.Chips.Count == 0 && block.More is null, step + ": marks stand in for chips only when no chip fits.");
            foreach (var (facet, state) in withoutChip)
            {
                var mark = block.Marks.SingleOrDefault(m => m.Facet == facet);
                if (mark is null) { Assert.IsTrue(more, step + ": " + facet + " is behind a shown \"+N more\" chip."); continue; }
                Assert.AreEqual(state, mark.State, step + ": " + facet + "'s mark shows its state.");
                Assert.IsTrue(mark.Rect.Shown, step + ": " + facet + "'s mark lies inside the canvas.");
                Assert.IsTrue(mark.Rect.X >= caption.X + caption.Width, step + ": " + facet + "'s mark is right of the caption.");
                int middle = mark.Rect.Y + mark.Rect.Height / 2;
                Assert.IsTrue(middle >= caption.Y && middle <= caption.Y + caption.Height, step + ": " + facet + "'s mark is beside the caption.");
            }
        }

        var start = await Open("start");
        var level = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(created.Path, token));
        var children = level.Inspect(level.SelectedRoot).Children;
        string psu = children[0].BlockId.ToString("D"), cpu = children[1].BlockId.ToString("D");
        Assert.IsEmpty(start.BlockChips, "Caption-only blocks show no chips and no Review facets link.");
        await SelectBlock("psu-selected", psu);
        var psuSelected = await Read();
        Assert.IsEmpty(psuSelected.ShownFacets, "A block without component choices lists none.");
        Assert.IsFalse(Find(psuSelected, "RecursiveFacetRowType").Shown); Assert.IsFalse(Find(psuSelected, "RecursiveFacetStateChosen").Shown);
        Assert.IsTrue(Find(psuSelected, "RecursiveAddDetail").Shown, "A quiet Add detail gives a component choice its first value.");
        Assert.IsTrue(Find(psuSelected, "RecursiveAddRequirement").Shown, "Add requirement stays beside it for the hidden requirement boxes.");

        // Add detail > Type opens the detail ready for a first chosen value. Escape cancels without a change. The menu lists only
        // the facets without a value: Purpose, Type, ...
        await AddDetail("Home", "Down");
        var typeOpen = await Wait("type-open", s => s.FacetEditor == "type" && s.FocusedControl == "RecursiveFacetValue");
        Assert.IsFalse(typeOpen.Dirty); Assert.IsEmpty(typeOpen.ShownFacets);
        Assert.IsFalse(Find(typeOpen, "RecursiveFacetClear").Shown, "A facet without a value has nothing to clear.");
        await Capture("add-facet");
        Key("Escape");
        var cancelled = await Wait("type-cancelled", s => s.FacetEditor == "");
        Assert.IsFalse(cancelled.Dirty); Assert.IsNull(cancelled.Draft.Definition, "A cancelled first value stores nothing.");
        await AddDetail("Home", "Down");
        await Wait("type-reopened", s => s.FacetEditor == "type" && s.FocusedControl == "RecursiveFacetValue");
        Type("linear regulator");
        var typed = await Wait("type-typed", s => Facet(s, d => d.Type)?.Values.SequenceEqual(["linear regulator"]) == true && s.Dirty);
        Assert.AreEqual(P.DefinitionChoiceStateData.DcsdSelected, typed.Draft.Definition.Type.State);
        Assert.AreEqual(KiCad.Automation.Protocol.Structural.StructuralGuidanceStrength.SgsInformation, typed.Draft.Definition.Type.Strength);
        // Enter keeps the value and returns to the overview; the block now shows a chosen chip and the Review facets link.
        Key("Return");
        var typeKept = await Wait("type-kept", s => s.FacetEditor == "" && s.FocusedControl == "RecursiveFacetRowType");
        CollectionAssert.AreEqual(new[] { "type" }, typeKept.ShownFacets.ToArray());
        CollectionAssert.AreEqual(new[] { "Type: linear regulator" }, ChipTexts(typeKept, psu));
        Assert.AreEqual(P.DefinitionChoiceStateData.DcsdSelected, Chips(typeKept, psu)!.Chips[0].State);
        Assert.IsTrue(Chips(typeKept, psu)!.ReviewFacets.Shown, "A block with choices offers Review facets.");
        Assert.IsNull(Chips(typeKept, cpu), "The CPU is still only its caption.");
        // Enter on an overview row opens that facet's detail; Escape returns to the overview.
        Key("Return"); await Wait("type-row-enter", s => s.FacetEditor == "type" && s.FocusedControl == "RecursiveFacetStateChosen");
        Key("Escape"); await Wait("type-row-escape", s => s.FacetEditor == "" && s.FocusedControl == "RecursiveFacetRowType");

        // Package: a chosen value with a preference, then a candidate. Switching carries the value into the candidate list. An
        // emptied list cannot be kept: the draft keeps the last entry that could, and Save refuses with the notice.
        await AddDetail("End");
        await Wait("package-open", s => s.FacetEditor == "package" && s.FocusedControl == "RecursiveFacetValue");
        Type("SOT-23-5");
        await Wait("package-chosen", s => Facet(s, d => d.Package) is { State: P.DefinitionChoiceStateData.DcsdSelected } c && c.Values.SequenceEqual(["SOT-23-5"]));
        await Press("RecursiveFacetStrengthPreference");
        await Wait("package-preference", s => Facet(s, d => d.Package)?.Strength == KiCad.Automation.Protocol.Structural.StructuralGuidanceStrength.SgsPreference);
        await Press("RecursiveFacetStateCandidate");
        await Wait("package-candidate", s => Facet(s, d => d.Package) is { State: P.DefinitionChoiceStateData.DcsdCandidates } c
            && c.Values.SequenceEqual(["SOT-23-5"]) && c.Strength == KiCad.Automation.Protocol.Structural.StructuralGuidanceStrength.SgsPreference
            && s.FocusedControl == "RecursiveFacetCandidates");
        Key("a", control: true); Key("BackSpace");
        const string listCandidates = "List the candidates, one per line.";
        var emptyList = await Wait("package-candidates-empty", s => s.FacetNotice == listCandidates);
        CollectionAssert.AreEqual(new[] { "SOT-23-5" }, emptyList.Draft.Definition.Package.Values.ToArray(), "Nothing is stored until the entry can be kept.");
        ulong saves = emptyList.CompletedSaveCount; string unsaved = await File.ReadAllTextAsync(created.Path, token);
        Key("s", control: true);
        var refused = await Wait("package-save-refused", s => s.Notice == listCandidates && s.FacetEditor == "package");
        Assert.AreEqual(saves, refused.CompletedSaveCount, "Nothing was sent to save."); Assert.IsTrue(refused.Dirty);
        Assert.AreEqual(unsaved, await File.ReadAllTextAsync(created.Path, token));
        Type("SOT-23-5");
        await Wait("package-candidate-again", s => Facet(s, d => d.Package) is { State: P.DefinitionChoiceStateData.DcsdCandidates } c
            && c.Values.SequenceEqual(["SOT-23-5"]) && s.FacetNotice == "" && s.Notice == "");

        // Manufacturer returns to unknown with its reason; an unknown facet is listed but has no chip. With Type and Package set, the
        // Add detail menu reads Purpose, Manufacturer, ...
        await AddDetail("Home", "Down");
        await Wait("manufacturer-open", s => s.FacetEditor == "manufacturer" && s.FocusedControl == "RecursiveFacetValue");
        await Press("RecursiveFacetStateUnknown");
        await Wait("manufacturer-needs-reason", s => s.FacetNotice == "Say why this is unknown." && s.FocusedControl == "RecursiveFacetReason");
        Type("No preference recorded.");
        var unknown = await Wait("manufacturer-unknown", s => Facet(s, d => d.Manufacturer) is { State: P.DefinitionChoiceStateData.DcsdUnknown } c
            && c.UnknownReason == "No preference recorded.");
        CollectionAssert.AreEqual(new[] { "type", "manufacturer", "package" }, unknown.ShownFacets.ToArray(), "Only facets with a value are listed.");
        CollectionAssert.AreEqual(new[] { "Type: linear regulator", "Package: SOT-23-5" }, ChipTexts(unknown, psu));
        CollectionAssert.AreEqual(new[] { P.DefinitionChoiceStateData.DcsdSelected, P.DefinitionChoiceStateData.DcsdCandidates },
            Chips(unknown, psu)!.Chips.Select(c => c.State).ToArray(), "Chosen and candidate chips are told apart.");
        Assert.AreEqual(0U, Chips(unknown, psu)!.HiddenChips);
        Assert.IsEmpty(Chips(unknown, psu)!.Marks, "Marks stand in for chips only when no chip fits.");
        Assert.IsNull(Chips(unknown, psu)!.More, "No \"+N more\" chip while every chip is drawn.");
        // Design QA round 2, R2-P2-7: at the default window size a facet's detail shows the strength choices' full labels; the
        // inspector's default width counts its scroll bar, so they show whether or not the inspector scrolls.
        StrengthRow(unknown, "default-width", false);
        {
            var settled = await DetailSettled("detail-settled");
            var shot = await Shot("detail");
            VerifyDetailLayout(shot, unknown, "choices-detail");
            var informationChoice = Find(unknown, "RecursiveFacetStrengthInformation");
            var informationInk = shot.InkRuns(informationChoice.X, informationChoice.Y, informationChoice.Width, informationChoice.Height,
                shot.At(Find(unknown, "RecursiveInspector").X + 3, informationChoice.Y + informationChoice.Height / 2), rows: false, minimum: 2.0);
            int informationWidth = informationInk.Count == 0 ? 0 : informationInk[^1].Last - informationInk[0].First + 1;
            Assert.IsTrue(shot.Record("R2-P2-7 Information choice drawn width at the default size (px)", informationWidth) >= 70,
                $"choices-detail: the Information choice is drawn {informationWidth} pixels wide, its whole word, not \"Info\".");
            VerifySelectionContrast(shot, unknown, "choices-detail", psu);
            // Design QA P2-10: Back to facet overview and Clear facet look like the canvas's link: underlined, in the link colour,
            // 4.5:1 on the inspector, and coloured apart from the inspector's static text ("Selected design: v1").
            var staticText = Find(unknown, "RecursiveSavedVersion");
            VerifyLink(shot, Find(unknown, "RecursiveFacetBack"), staticText, "choices-detail", "Back to facet overview");
            VerifyLink(shot, Find(unknown, "RecursiveFacetClear"), staticText, "choices-detail", "Clear facet");
            // P2-11: every facet row ends in a text-size chevron at 3:1 or more, the open row included.
            VerifyChevrons(shot, unknown, "choices-detail");
            // P2-9: the reason sits inside its box, not against the border.
            VerifyTextInset(shot, Find(unknown, "RecursiveFacetReason"), "choices-detail", "the Reason box");
            // P2-13: Comments is whole in the inspector's view, or a visible scroll bar shows that the inspector holds more.
            VerifyInspectorScroll(shot, settled, "choices-detail");
            // Design QA round 2, P3 10: while a facet's detail is open the requirement box and Comments are two lines high, as
            // sketch A4 draws them, so at the default window size Comments lies whole in the inspector's view with its 12-pixel
            // margin below it (it was cut off at the view's lower edge), and its bottom border is drawn.
            var inspector = Find(settled, "RecursiveInspector"); var comments = Find(settled, "RecursiveComments");
            Assert.IsTrue(comments.Shown && comments.Y >= inspector.Y, "choices-detail: Comments is in the inspector's view.");
            int commentsMargin = inspector.Y + inspector.Height - (comments.Y + comments.Height);
            Assert.IsTrue(shot.Record("P3 10 Comments bottom to the inspector's lower edge (px)", commentsMargin) >= 12,
                $"choices-detail: Comments ends {commentsMargin} pixels above the inspector's lower edge, at least 12.");
            var belowComments = shot.At(inspector.X + 3, comments.Y + comments.Height + 6);
            var commentsBorder = shot.MostContrasting(comments.X + 12, comments.Y + comments.Height - 3, comments.Width - 24, 3, belowComments);
            Assert.IsTrue(shot.Contrast("P3 10 Comments bottom border on the inspector", commentsBorder, belowComments) >= 1.3,
                $"choices-detail: Comments' bottom border {CapturedWindow.Describe(commentsBorder)} is drawn on {CapturedWindow.Describe(belowComments)}.");
            var general = Find(settled, "RecursiveRequirements0");
            Assert.IsTrue(general.Height < 90 && comments.Height < 72,
                $"choices-detail: the requirement box ({general.Height} pixels) and Comments ({comments.Height} pixels) are two lines high beside the detail.");
        }

        // The strength labels collapse before the row would wrap and return when the inspector is wide enough, and the chosen
        // strength stays selected. The narrowest inspector (its minimum width) shows the short labels; a wider one the full labels.
        var defaultSash = Find(unknown, "RecursiveInspectorSash");
        var narrow = await MoveSash("inspector-narrowest", defaultSash.X + 160, s => Find(s, "RecursiveInspectorSash").X > defaultSash.X
            && StrengthLabels(s)[0] == "Info");
        StrengthRow(narrow, "narrowest-inspector", true);
        Assert.IsTrue(Find(narrow, "RecursiveFacetStrengthInformation").Active, "The chosen strength stays selected.");
        await Capture("strength-narrow");
        var wide = await MoveSash("inspector-wide", defaultSash.X - 160, s => Find(s, "RecursiveInspectorSash").X < defaultSash.X - 100
            && StrengthLabels(s)[0] == "Information");
        StrengthRow(wide, "wide-inspector", false);
        Assert.IsTrue(Find(wide, "RecursiveFacetStrengthInformation").Active, "The chosen strength stays selected.");
        Assert.AreEqual(unknown.FacetEditor, wide.FacetEditor, "The facet's detail stays open.");
        Assert.IsTrue(Facet(wide, d => d.Manufacturer) is { State: P.DefinitionChoiceStateData.DcsdUnknown } kept && kept.UnknownReason == "No preference recorded."
            && kept.Strength == KiCad.Automation.Protocol.Structural.StructuralGuidanceStrength.SgsInformation, "Resizing the inspector changes nothing in the draft.");
        var restored = await MoveSash("inspector-default", defaultSash.X + defaultSash.Width / 2,
            s => Math.Abs(Find(s, "RecursiveInspectorSash").X - defaultSash.X) <= 2);
        StrengthRow(restored, "restored-width", null);
        await Press("RecursiveFacetBack");
        await Wait("manufacturer-back", s => s.FacetEditor == "" && s.FocusedControl == "RecursiveFacetRowManufacturer");

        // The CPU stays caption-only. Review facets on the PSU selects it and opens its first facet's detail.
        await SelectBlock("cpu-selected", cpu);
        var cpuSelected = await Read();
        Assert.IsEmpty(cpuSelected.ShownFacets); Assert.IsNull(Chips(cpuSelected, cpu));
        var link = Chips(cpuSelected, psu)!.ReviewFacets;
        Assert.IsTrue(link.Shown && link.Enabled);
        Click(link.X + link.Width / 2, link.Y + link.Height / 2);
        await Wait("review-facets", s => s.Draft.Baseline.BlockId == psu && s.FacetEditor == "type" && s.FocusedControl == "RecursiveFacetStateChosen");

        // Type returns to unknown (its chip goes), then back to chosen: a chosen state needs its value typed again.
        await Press("RecursiveFacetStateUnknown");
        await Wait("type-needs-reason", s => s.FacetNotice == "Say why this is unknown." && s.FocusedControl == "RecursiveFacetReason");
        Type("Compare regulator types first.");
        var typeUnknown = await Wait("type-unknown", s => Facet(s, d => d.Type) is { State: P.DefinitionChoiceStateData.DcsdUnknown } c
            && c.UnknownReason == "Compare regulator types first.");
        CollectionAssert.AreEqual(new[] { "Package: SOT-23-5" }, ChipTexts(typeUnknown, psu));
        CollectionAssert.AreEqual(new[] { "type", "manufacturer", "package" }, typeUnknown.ShownFacets.ToArray());
        await Press("RecursiveFacetBack"); await Wait("type-back", s => s.FacetEditor == "" && s.FocusedControl == "RecursiveFacetRowType");
        Key("Return"); await Wait("type-reopened-unknown", s => s.FacetEditor == "type" && s.FocusedControl == "RecursiveFacetStateUnknown");
        await Press("RecursiveFacetStateChosen");
        await Wait("type-needs-value", s => s.FacetNotice == "Type the chosen value, or choose another state." && s.FocusedControl == "RecursiveFacetValue");
        Type("linear regulator");
        var chosenAgain = await Wait("type-chosen-again", s => Facet(s, d => d.Type) is { State: P.DefinitionChoiceStateData.DcsdSelected } c
            && c.Values.SequenceEqual(["linear regulator"]) && s.FacetNotice == "");
        CollectionAssert.AreEqual(new[] { "Type: linear regulator", "Package: SOT-23-5" }, ChipTexts(chosenAgain, psu));

        // Clear facet returns Package to unspecified: its row and chip go. Undo brings it back with its strength. The Package
        // detail is opened from the facet overview, measured, and cleared through the rendered Clear facet at the default width,
        // at the inspector's minimum width, at the first width that shows the full strength labels, and in the compact window
        // (below), in each theme's session.
        async Task<(P.RecursiveDiagramEditorState Detail, CapturedWindow Shot)> MeasurePackage(string step, bool? brief)
        {
            var detail = await DetailSettled(step);
            var shot = await Shot(step + "-package-detail");
            VerifyDetailLayout(shot, detail, step);
            StrengthRow(detail, step, brief);
            return (detail, shot);
        }
        async Task<(P.RecursiveDiagramEditorState Detail, CapturedWindow Shot)> OpenPackage(string step, bool? brief)
        {
            await Press("RecursiveFacetRowPackage");
            await Wait(step + "-package-row", s => s.FacetEditor == "package" && s.FocusedControl == "RecursiveFacetStateCandidate");
            return await MeasurePackage(step, brief);
        }
        async Task<P.RecursiveDiagramEditorState> ClearOpenPackage(string step, string[] facetsLeft,
            params (string Facet, P.DefinitionChoiceStateData State)[] choicesLeft)
        {
            bool dirty = (await Read()).Dirty;
            await Press("RecursiveFacetClear");
            var gone = await Wait(step + "-package-cleared", s => s.FacetEditor == "" && Facet(s, d => d.Package) is null);
            CollectionAssert.AreEqual(facetsLeft, gone.ShownFacets.ToArray(), step + ": the Package row leaves the facet overview.");
            VerifyChoicesVisible(gone, step + "-cleared", psu, choicesLeft);
            Assert.IsTrue(gone.Dirty, step + ": the cleared facet is a change to save.");
            Key("z", control: true);
            var back = await Wait(step + "-package-restored", s => Facet(s, d => d.Package) is { State: P.DefinitionChoiceStateData.DcsdCandidates } c
                && c.Values.SequenceEqual(["SOT-23-5"]) && c.Strength == KiCad.Automation.Protocol.Structural.StructuralGuidanceStrength.SgsPreference);
            Assert.AreEqual(dirty, back.Dirty, step + ": undo returns the draft to what it was before Clear facet.");
            return gone;
        }
        async Task<P.RecursiveDiagramEditorState> ClearPackage(string step, bool? brief, string[] facetsLeft,
            params (string Facet, P.DefinitionChoiceStateData State)[] choicesLeft)
        {
            await OpenPackage(step, brief);
            return await ClearOpenPackage(step, facetsLeft, choicesLeft);
        }
        (string, P.DefinitionChoiceStateData) typeChosen = ("type", P.DefinitionChoiceStateData.DcsdSelected);
        string[] fullStrengths = ["Information", "Preference", "Requirement"], briefStrengths = ["Info", "Pref.", "Req."];
        var clearSash = Find(await Read(), "RecursiveInspectorSash");
        var cleared = await ClearPackage("default-width", false, ["type", "manufacturer"], typeChosen);
        CollectionAssert.AreEqual(new[] { "Type: linear regulator" }, ChipTexts(cleared, psu));
        // The labels switch at one width, and the integration run clear-facet-overlap-11e548 failed near such a width: the full
        // labels fitted the facet overview, which has no scroll bar, but not the Package detail once its scroll bar showed, and
        // Clear facet was squeezed to nothing below the wrapped row. Since design QA round 2 (R2-P2-7) the default inspector shows
        // the full labels, so the inspector is first narrowed by 30 pixels, below the switch, and then widened in 3-pixel steps
        // until the full labels show with the Package detail open. At each width the detail is measured as the sash left it, and
        // again, with a capture, after it is reopened from the facet overview: the order in which the integration run met it.
        var sweepFrom = Find(await Read(), "RecursiveInspectorSash");
        await MoveSash("sweep-narrowed", sweepFrom.X + sweepFrom.Width / 2 + 30, s => Find(s, "RecursiveInspectorSash").X >= sweepFrom.X + 20);
        // The facet overview's column where the sweep starts, as drawn: each overview row fills the width inside the 12 DIP margins.
        int overviewColumn = Find(await Read(), "RecursiveFacetRowPackage").Width;
        var sweepStart = (await OpenPackage("sweep-00", true)).Detail;
        P.RecursiveDiagramEditorState lastShort = sweepStart, firstFull;
        for (int i = 1; ; ++i)
        {
            Assert.IsTrue(i <= 20, "The full strength labels show within 60 pixels of where the sweep started.");
            string step = $"sweep-{i:00}";
            var sash = Find(await Read(), "RecursiveInspectorSash");
            await MoveSash(step, sash.X + sash.Width / 2 - 3, s => Find(s, "RecursiveInspectorSash").X < sash.X);
            var dragged = await DetailSettled(step + "-resized");
            _ = VerifyDetailGeometry(dragged, step + "-resized"); StrengthRow(dragged, step + "-resized", null);
            await Press("RecursiveFacetBack");
            await Wait(step + "-overview", s => s.FacetEditor == "" && s.ShownFacets.Contains("package"));
            var reopenedDetail = (await OpenPackage(step + "-reopened", null)).Detail;
            CollectionAssert.AreEqual(StrengthLabels(dragged), StrengthLabels(reopenedDetail),
                step + ": the strength labels follow the inspector's width, however the detail was reached.");
            // Every width is visited: each step widens the detail's column by 1 to 3 pixels, so no width where the labels could
            // switch is skipped.
            int widened = Find(reopenedDetail, "RecursiveFacetCandidates").Width - Find(lastShort, "RecursiveFacetCandidates").Width;
            Assert.IsTrue(widened >= 1 && widened <= 3, $"{step}: the detail's column widened by {widened} pixels, 1 to 3.");
            if (StrengthLabels(reopenedDetail).SequenceEqual(fullStrengths)) { firstFull = reopenedDetail; break; }
            lastShort = reopenedDetail;
        }
        // They switch exactly where the rule says: the full labels show once they fit the detail's column with 4 DIP to spare (the
        // fixture display is at 96 DPI, where the detail's 12 DIP margin is 12 pixels), and one step narrower they did not. The
        // editor adds up the same label widths the journey measures here, so the wide side is exact; on the narrow side the editor
        // estimates the full labels from the short ones it shows, which each may round by a pixel, so that side allows 3 pixels.
        var (fullDetail, fullShot) = await MeasurePackage("first-full-width", false);
        var information = Find(fullDetail, "RecursiveFacetStrengthInformation"); var requirement = Find(fullDetail, "RecursiveFacetStrengthRequirement");
        Assert.AreEqual(12, Find(fullDetail, "RecursiveFacetCandidates").X - Find(fullDetail, "RecursiveInspector").X, "The detail's margin is 12 pixels.");
        int fullRow = fullShot.Record("Strength row with the full labels (px)", requirement.X + requirement.Width - information.X);
        int wideColumn = fullShot.Record("Detail column where the full labels first show (px)", Find(fullDetail, "RecursiveFacetCandidates").Width);
        int narrowColumn = fullShot.Record("Detail column one step narrower, short labels (px)", Find(lastShort, "RecursiveFacetCandidates").Width);
        Assert.AreEqual(Find(firstFull, "RecursiveFacetCandidates").Width, wideColumn, "The detail keeps its width while it is measured again.");
        Assert.IsTrue(fullRow + 4 <= wideColumn, $"The full strength labels ({fullRow} pixels) show from a {wideColumn}-pixel column, where they have 4 pixels to spare.");
        Assert.IsTrue(fullRow + 4 > narrowColumn - 3,
            $"The full strength labels ({fullRow} pixels) did not show from {narrowColumn} pixels, where they would need {fullRow + 4}.");
        // The sweep covered every width where opening the detail switches the labels: where it started, even the facet overview's
        // column (measured from its rows, as the overview has no scroll bar) was too narrow for them.
        fullShot.Record("Facet overview column where the sweep started (px)", overviewColumn);
        Assert.IsTrue(overviewColumn < fullRow + 4,
            $"The sweep starts below the widths where opening the detail switches the labels: the overview's {overviewColumn}-pixel column there is under {fullRow + 4} pixels.");
        await ClearOpenPackage("first-full-width", ["type", "manufacturer"], typeChosen);
        // The narrowest inspector (its minimum width) collapses the strength labels.
        await MoveSash("clear-inspector-narrowest", clearSash.X + 160, s => Find(s, "RecursiveInspectorSash").X > clearSash.X
            && StrengthLabels(s).SequenceEqual(briefStrengths));
        await ClearPackage("narrowest-inspector", true, ["type", "manufacturer"], typeChosen);
        await MoveSash("clear-inspector-default", clearSash.X + clearSash.Width / 2, s => Math.Abs(Find(s, "RecursiveInspectorSash").X - clearSash.X) <= 2);

        // A third choice: Family takes the candidate "TLV755P" (Add detail now reads Purpose, Family, ...). The block has room for
        // two chips, so as many chips as fit are drawn and the rest is one "+N more" chip; the facet overview still lists every facet.
        await AddDetail("Home", "Down");
        await Wait("family-open", s => s.FacetEditor == "family" && s.FocusedControl == "RecursiveFacetValue");
        Type("TLV755P");
        await Wait("family-chosen", s => Facet(s, d => d.Family) is { State: P.DefinitionChoiceStateData.DcsdSelected } c && c.Values.SequenceEqual(["TLV755P"]));
        await Press("RecursiveFacetStateCandidate");
        await Wait("family-candidate", s => Facet(s, d => d.Family) is { State: P.DefinitionChoiceStateData.DcsdCandidates } c
            && c.Values.SequenceEqual(["TLV755P"]) && s.FocusedControl == "RecursiveFacetCandidates");
        await Press("RecursiveFacetBack");
        var familyAdded = await Wait("family-back", s => s.FacetEditor == "" && s.FocusedControl == "RecursiveFacetRowFamily");
        (string, P.DefinitionChoiceStateData)[] three = [("type", P.DefinitionChoiceStateData.DcsdSelected), ("family", P.DefinitionChoiceStateData.DcsdCandidates),
            ("package", P.DefinitionChoiceStateData.DcsdCandidates)];
        const string moreTip = "Family: TLV755P (candidate)\nPackage: SOT-23-5 (candidate)";
        void VerifyMore(P.RecursiveDiagramEditorState at, string step)
        {
            var chips = Chips(at, psu) ?? throw new AssertFailedException(step + ": the PSU reports its chips.");
            // Design QA round 2, R2-P2-6: a chip always shows its whole "Facet: value". Family's "TLV755P" does not fit the PSU's
            // second chip row beside "+1 more" (the Rail port's name ends that row), so Family goes behind "+N more" with Package
            // instead of being cut to "Family: T…"; the chip's tooltip names both with their values and states.
            CollectionAssert.AreEqual(new[] { "type" }, chips.Chips.Select(c => c.Facet).ToArray(), step + ": the chips that fit whole, in facet order.");
            Assert.AreEqual("Type: linear regulator", chips.Chips[0].Text, step + ": the chip keeps its whole text.");
            Assert.IsFalse(chips.Chips.Any(c => c.Text.Contains('…')), step + ": no chip is shortened.");
            Assert.IsTrue(chips.More is { Shown: true }, step + ": the rest is shown as one \"+N more\" chip.");
            Assert.AreEqual(2U, chips.HiddenChips, step + ": Family and Package are the choices behind \"+2 more\".");
            Assert.AreEqual("+2 more", chips.More.Label, step + ": the chip counts the choices behind it.");
            Assert.AreEqual(moreTip, chips.More.Tooltip, step + ": \"+2 more\" names the choices behind it with their values and states.");
            Assert.IsEmpty(chips.Marks, step + ": no state marks while chips fit.");
            Assert.HasCount(1, chips.PortNames, step + ": the PSU names its Rail port inside its edge, and the chips keep clear of it.");
            VerifyChoicesVisible(at, step, psu, three);
        }
        CollectionAssert.AreEqual(new[] { "type", "manufacturer", "family", "package" }, familyAdded.ShownFacets.ToArray(),
            "The overview lists every facet with a value, including Family and Package behind \"+2 more\".");
        VerifyMore(familyAdded, "more");
        await Capture("more");
        // On hover, "+2 more" shows the choices behind it (design QA round 2, R2-P2-6), and the tooltip goes when the pointer leaves.
        var moreChip = Chips(familyAdded, psu)!.More;
        ulong motionsBefore = (await Read()).CanvasMotions;
        // The pointer first enters the canvas away from the chip, then moves onto it.
        NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: moreChip.X + moreChip.Width / 2, clickFromTop: moreChip.Y + moreChip.Height + 40);
        foreach (int dx in new[] { -3, 0, 2 })
            NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: moreChip.X + moreChip.Width / 2 + dx, clickFromTop: moreChip.Y + moreChip.Height / 2);
        await Wait("more-hover", s => s.CanvasMotions > motionsBefore && s.CanvasTooltip == moreTip);
        NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: familyAdded.CanvasWindowX + (int)familyAdded.CanvasPixelWidth - 30,
            clickFromTop: familyAdded.CanvasWindowY + (int)familyAdded.CanvasPixelHeight - 30);
        await Wait("more-hover-left", s => s.CanvasTooltip == "");

        // Save stores the choices in exactly one new revision of the PSU and nothing else about it; the CPU keeps its revision.
        var beforeChoices = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(created.Path, token));
        var psuBefore = beforeChoices.Inspect(beforeChoices.SelectedRoot).Children[0]; var cpuBefore = beforeChoices.Inspect(beforeChoices.SelectedRoot).Children[1];
        ulong beforeSave = (await Read()).CompletedSaveCount;
        Key("s", control: true);
        var saved = await Wait("saved", s => s.CompletedSaveCount > beforeSave && !s.Dirty);
        Assert.AreEqual("", saved.ErrorMessage);
        string savedXml = await File.ReadAllTextAsync(created.Path, token);
        var graph = RecursiveBlockGraphXml.Read(savedXml);
        var top = graph.Inspect(graph.SelectedRoot);
        var psuSaved = graph.Inspect(top.Children[0]); var cpuSaved = graph.Inspect(top.Children[1]);
        Assert.AreEqual(cpuBefore, top.Children[1], "The CPU keeps its saved revision.");
        CollectionAssert.AreEqual(beforeChoices.History(psuBefore.StateId).Select(r => r.Selection.RevisionId).ToArray(),
            graph.History(psuBefore.StateId).Select(r => r.Selection.RevisionId).Where(id => id != top.Children[0].RevisionId).ToArray(),
            "Every earlier PSU revision is kept.");
        Assert.AreEqual(beforeChoices.History(psuBefore.StateId).Length + 1, graph.History(psuBefore.StateId).Length, "Save adds exactly one PSU revision.");
        Assert.AreEqual((psuBefore.BlockId, psuBefore.StateId), (top.Children[0].BlockId, top.Children[0].StateId));
        Assert.AreEqual((Guid?)psuBefore.RevisionId, psuSaved.ParentRevisionId, "The new PSU revision follows the one the choices were made on.");
        var definition = psuSaved.Definition!;
        Assert.IsTrue(definition.SameContents(new BlockDefinition(
            Type: new(DefinitionChoiceState.Selected, ["linear regulator"], GuidanceStrength.Information, "", [], VerificationState.Unverified),
            Manufacturer: new(DefinitionChoiceState.Unknown, [], GuidanceStrength.Information, "", [], VerificationState.Unverified, "No preference recorded."),
            Family: new(DefinitionChoiceState.Candidates, ["TLV755P"], GuidanceStrength.Information, "", [], VerificationState.Unverified),
            Package: new(DefinitionChoiceState.Candidates, ["SOT-23-5"], GuidanceStrength.Preference, "", [], VerificationState.Unverified))),
            "The saved choices are exactly the chosen type, the unknown manufacturer, the candidate family and the candidate package.");
        Assert.AreEqual("Rail", psuSaved.LocalDiagram.Interfaces.Single().Name, "A chosen name adds no ports.");
        Assert.IsNull(psuSaved.ComponentBindings, "A chosen name realizes no component.");
        Assert.IsNull(psuSaved.PhysicalAllocation, "A chosen name allocates no footprint or board.");
        Assert.AreEqual("Supply the CPU.", graph.Requirements(psuSaved.Selection).Requirements.General);
        Assert.IsNull(cpuSaved.Definition, "The CPU is still only its caption.");
        VerifyMore(saved, "saved");
        {
            var shot = await Shot("saved");
            VerifyChevrons(shot, saved, "choices-saved");
            // Design QA P2-12: a clear gap after the facet table before the next section.
            var lastRow = saved.Controls.Where(c => c.Name.StartsWith("RecursiveFacetRow", StringComparison.Ordinal) && c.Shown).MaxBy(c => c.Y)!;
            var next = Find(saved, "RecursiveAddDetail");
            Assert.IsTrue(next.Shown && shot.Record("P2-12 gap between the facet table and Add detail (px)", next.Y - (lastRow.Y + lastRow.Height)) >= 12,
                $"choices-saved: {next.Y - (lastRow.Y + lastRow.Height)} pixels separate the facet table from Add detail, at least 12.");
        }

        // While the whole-diagram history is open the canvas takes no presses, and a previewed past revision is read only: the PSU
        // keeps its chips, but the Review facets link, which would do nothing, is not drawn, before Preview as well as during it. The
        // history opens on the saved revision; Down and Up inspect it again before Preview.
        Key("h", control: true);
        var historyOpen = await Wait("history-open", s => s.DiagramHistory is { Busy: false } h && h.Inspected?.RevisionId == graph.SelectedRoot.RevisionId.ToString("D")
            && s.Rendered);
        var historyOpenChips = Chips(historyOpen, psu) ?? throw new AssertFailedException("history-open: the PSU still shows its chips.");
        CollectionAssert.AreEqual(new[] { "type" }, historyOpenChips.Chips.Select(c => c.Facet).ToArray(), "history-open: the same chips.");
        Assert.IsNull(historyOpenChips.ReviewFacets, "history-open: no Review facets link while the history is open.");
        Key("Down"); await Wait("history-older", s => s.DiagramHistory is { Busy: false } h && h.Inspected?.RevisionId != graph.SelectedRoot.RevisionId.ToString("D"));
        Key("Up"); await Wait("history-saved", s => s.DiagramHistory is { Busy: false } h && h.Inspected?.RevisionId == graph.SelectedRoot.RevisionId.ToString("D"));
        Key("p", alt: true);
        var preview = await Wait("history-preview", s => s.DiagramHistory?.Preview?.RevisionId == graph.SelectedRoot.RevisionId.ToString("D") && s.Rendered);
        var previewChips = Chips(preview, psu) ?? throw new AssertFailedException("history-preview: the previewed PSU still shows its chips.");
        CollectionAssert.AreEqual(new[] { "type" }, previewChips.Chips.Select(c => c.Facet).ToArray(), "history-preview: the same chips.");
        Assert.IsNull(previewChips.ReviewFacets, "history-preview: no Review facets link in a read-only preview.");
        await Capture("history-preview");
        Key("c", alt: true); await Wait("history-preview-closed", s => s.DiagramHistory is { Preview: null });
        Key("Escape"); var historyClosed = await Wait("history-closed", s => s.DiagramHistory is null);
        Assert.IsTrue(Chips(historyClosed, psu)!.ReviewFacets is { Shown: true, Enabled: true }, "Back in the level, Review facets is offered again.");
        Assert.IsFalse(historyClosed.Dirty); Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token), "Browsing history writes nothing.");

        // Decline discards a later change and writes nothing.
        await Press("RecursiveFacetRowType");
        await Wait("type-detail", s => s.FacetEditor == "type" && s.FocusedControl == "RecursiveFacetStateChosen");
        await Press("RecursiveFacetStrengthRequirement");
        await Wait("type-requirement", s => s.Dirty && Facet(s, d => d.Type)?.Strength == KiCad.Automation.Protocol.Structural.StructuralGuidanceStrength.SgsRequirement);
        Key("d", alt: true);
        await Wait("declined", s => !s.Dirty && s.FacetEditor == ""
            && Facet(s, d => d.Type)?.Strength == KiCad.Automation.Protocol.Structural.StructuralGuidanceStrength.SgsInformation);
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token));

        // A compact window re-fits the level. Every chosen or candidate facet stays visible on its block: as a chip, behind a
        // "+N more" chip, or as its state mark beside the caption when not even one chip fits. Nothing is drawn clipped.
        ulong beforeCompact = (await Read()).ViewRevision;
        NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, resizeWidth: 1100, resizeHeight: 760);
        var compact = await Wait("compact", s => s.Rendered && s.ViewRevision > beforeCompact && s.CanvasPixelWidth < 800);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-choices-compact.json"), SchematicJson.Formatter.Format(compact), token);
        VerifyChoicesVisible(compact, "compact", psu, three);
        // Design QA P2-7: what stands for the PSU's choices on a small block (its chips, "+N more", or its state marks when not even
        // "+N more" fits) keeps 6 pixels from the port names and from the selected block's handles. In these windows the PSU's
        // caption sits high enough for one chip row, so "+3 more" stands for the three choices; the check covers whatever is drawn
        // and requires that something is.
        static bool ApartBy(P.DiagramControlRect a, P.DiagramControlRect b, int gap) =>
            a.X + a.Width + gap <= b.X || b.X + b.Width + gap <= a.X || a.Y + a.Height + gap <= b.Y || b.Y + b.Height + gap <= a.Y;
        void ChoicesClear(P.RecursiveDiagramEditorState at, string step)
        {
            var chips = Chips(at, psu)!;
            var drawn = chips.Chips.Select(c => c.Rect).Concat(chips.Marks.Select(m => m.Rect)).Append(chips.More).Where(r => r is { Shown: true }).ToArray();
            Assert.IsNotEmpty(drawn, step + ": the PSU shows its choices.");
            Assert.IsNotEmpty(at.SelectionHandles, step + ": the PSU is selected, with its handles drawn.");
            foreach (var rect in drawn)
            {
                Assert.IsTrue(chips.PortNames.All(name => ApartBy(rect, name, 6)), $"{step}: {rect.Name} keeps 6 pixels from the port names.");
                Assert.IsTrue(at.SelectionHandles.All(handle => ApartBy(rect, handle, 6)), $"{step}: {rect.Name} keeps 6 pixels from the handles.");
            }
        }
        {
            // Design QA P2-7: in the compact window text is drawn whole and captions keep clear; P2-13: the inspector shows that it
            // scrolls.
            var shot = await Shot("compact");
            VerifyCanvasText(compact, "choices-compact");
            ChoicesClear(compact, "choices-compact");
            VerifyInspectorScroll(shot, compact, "choices-compact");
            // Design QA round 2, R2-P2-6 in the compact window: a chip is drawn whole or not at all; the choices that do not fit
            // whole are behind "+N more", which counts them and names each with its value and state on hover.
            var wholeChips = new Dictionary<string, (string Chip, string Tip)>
            {
                ["type"] = ("Type: linear regulator", "Type: linear regulator (chosen)"),
                ["family"] = ("Family: TLV755P", "Family: TLV755P (candidate)"),
                ["package"] = ("Package: SOT-23-5", "Package: SOT-23-5 (candidate)")
            };
            var compactChips = Chips(compact, psu)!;
            foreach (var chip in compactChips.Chips)
                Assert.AreEqual(wholeChips[chip.Facet].Chip, chip.Text, "choices-compact: the " + chip.Facet + " chip shows its whole facet and value.");
            string[] behind = [.. wholeChips.Keys.Where(f => compactChips.Chips.All(c => c.Facet != f))];
            Assert.AreEqual((uint)behind.Length, compactChips.HiddenChips, "choices-compact: every choice not drawn as a chip is counted behind \"+N more\".");
            if (behind.Length > 0 && compactChips.More is { Shown: true } more)
            {
                Assert.AreEqual($"+{behind.Length} more", more.Label, "choices-compact: \"+N more\" counts the choices behind it.");
                Assert.AreEqual(string.Join("\n", behind.Select(f => wholeChips[f].Tip)), more.Tooltip,
                    "choices-compact: \"+N more\" names the choices behind it with their values and states.");
            }
        }
        // The compact window keeps the Package detail's measured layout, and Clear facet still clears Package; undo leaves the
        // declined draft clean again.
        // The compact window keeps the default inspector's width, so the Package detail shows the full strength labels there too;
        // the short labels are the narrow inspector's fallback only (design QA round 2, R2-P2-7).
        await ClearPackage("compact", false, ["type", "manufacturer", "family"], ("type", P.DefinitionChoiceStateData.DcsdSelected),
            ("family", P.DefinitionChoiceStateData.DcsdCandidates));
        // Between the compact and the full window the PSU is re-fitted at a third scale, where the Rail port's name narrows the chip
        // rows differently: the same rules hold there (no chip narrower than its minimum, the rest behind "+N more" or as marks).
        // Read just before the resize: clearing Package above advanced the view revision, and the wait below must see a new frame.
        ulong beforeBetween = (await Read()).ViewRevision;
        NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, resizeWidth: 1300, resizeHeight: 880);
        var between = await Wait("between", s => s.Rendered && s.ViewRevision > beforeBetween && s.CanvasPixelWidth > compact.CanvasPixelWidth);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-choices-between.json"), SchematicJson.Formatter.Format(between), token);
        VerifyChoicesVisible(between, "between", psu, three);
        ChoicesClear(between, "choices-between");
        await Capture("between");
        NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, resizeWidth: 1536, resizeHeight: 1024);
        await Wait("expanded", s => s.Rendered && s.CanvasPixelWidth > 900);

        // Close and reopen: the saved choices come back on the canvas and in the inspector.
        Key("w", control: true); await Closed();
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token), "Closing a clean window writes nothing.");
        var reopened = await Open("reopened");
        VerifyMore(reopened, "reopened");
        Assert.IsNull(Chips(reopened, cpu));
        await SelectBlock("psu-reopened", psu);
        CollectionAssert.AreEqual(new[] { "type", "manufacturer", "family", "package" }, (await Read()).ShownFacets.ToArray());
        await Press("RecursiveFacetRowManufacturer");
        var manufacturer = await Wait("manufacturer-reopened", s => s.FacetEditor == "manufacturer");
        Assert.IsFalse(manufacturer.Dirty, "Opening a facet's detail changes nothing.");
        Assert.AreEqual("No preference recorded.", manufacturer.Draft.Definition.Manufacturer.UnknownReason);
        await Capture("reopened");
        // A value with an "&" (a manufacturer such as C&K) is shown and read out as written: the facet row, a platform toggle
        // button whose label GTK would otherwise take as a keyboard mnemonic, is named "Manufacturer: C&K" by its visible label
        // and by assistive technology (review of the routing follow-ups, finding 3). Decline then discards the value.
        await Press("RecursiveFacetStateChosen");
        await Wait("ampersand-needs-value", s => s.FacetEditor == "manufacturer" && s.FocusedControl == "RecursiveFacetValue");
        Type("C&K");
        var ampersand = await Wait("ampersand-value", s => Facet(s, d => d.Manufacturer) is { State: P.DefinitionChoiceStateData.DcsdSelected } c
            && c.Values.SequenceEqual(["C&K"]) && Find(s, "RecursiveFacetRowManufacturer").Label == "Manufacturer: C&K");
        VerifyAccessible(Find(ampersand, "RecursiveFacetRowManufacturer"), "toggle button", "ampersand-value");
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-choices-ampersand.json"), SchematicJson.Formatter.Format(ampersand), token);
        await Capture("ampersand");
        Key("d", alt: true);
        await Wait("ampersand-declined", s => !s.Dirty && Facet(s, d => d.Manufacturer) is { State: P.DefinitionChoiceStateData.DcsdUnknown });
        Key("w", control: true); await Closed();
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token));

        // Sources, a condition and a verification recorded with a facet describe that exact choice. Another agent records them for
        // the PSU's Type. A strength change keeps them; a new value drops them, because the inspector does not show them and the
        // person could not see that they no longer fit. The saved revision says exactly that.
        var recorded = RecursiveBlockGraphXml.Read(savedXml); var recordedPsu = recorded.Inspect(recorded.SelectedRoot).Children[0];
        var agentDraft = recorded.StartDraft(recordedPsu);
        var sourced = agentDraft.Definition! with { Type = agentDraft.Definition!.Type! with { Applicability = "For the 3.3 V rail",
            Sources = [new SourceReference("regulator-datasheet", "r3", 12, null, null)], Verification = VerificationState.Verified } };
        recorded = recorded.SaveDraft(recorded.SelectedRoot, [recorded.SelectedRoot, recordedPsu], agentDraft with { Definition = sourced },
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin("Another agent")).Graph;
        await File.WriteAllTextAsync(created.Path, RecursiveBlockGraphXml.Write(recorded), token);
        (string, int, KiCad.Automation.Protocol.Structural.StructuralVerification) Provenance(P.RecursiveDiagramEditorState at) =>
            Facet(at, d => d.Type) is { } type ? (type.Applicability, type.Sources.Count, type.Verification) : ("(no type)", -1, default);
        await Open("sourced-opened");
        await SelectBlock("psu-sourced", psu);
        await Press("RecursiveFacetRowType");
        var sourcedType = await Wait("type-sourced", s => s.FacetEditor == "type" && s.FocusedControl == "RecursiveFacetStateChosen");
        Assert.AreEqual(("For the 3.3 V rail", 1, KiCad.Automation.Protocol.Structural.StructuralVerification.SvVerified), Provenance(sourcedType),
            "The agent's source, condition and verification arrive with the facet.");
        // A keystroke that is taken back is no new statement: typing one letter drops the provenance, and Backspace, which
        // returns the value to the saved one, brings the saved source, condition and verification back and leaves nothing to save.
        await Press("RecursiveFacetValue"); await Wait("type-sourced-value-focused", s => s.FocusedControl == "RecursiveFacetValue");
        Key("End"); Type("s");
        var oneLetter = await Wait("type-sourced-letter", s => s.Dirty && Facet(s, d => d.Type)?.Values.SequenceEqual(["linear regulators"]) == true);
        Assert.AreEqual(("", 0, KiCad.Automation.Protocol.Structural.StructuralVerification.SvUnverified), Provenance(oneLetter),
            "A new value keeps none of the old value's source, condition or verification.");
        Key("BackSpace");
        var takenBack = await Wait("type-sourced-taken-back", s => Facet(s, d => d.Type)?.Values.SequenceEqual(["linear regulator"]) == true);
        Assert.AreEqual(("For the 3.3 V rail", 1, KiCad.Automation.Protocol.Structural.StructuralVerification.SvVerified), Provenance(takenBack),
            "Typing the saved value back restores the saved source, condition and verification.");
        Assert.IsFalse(takenBack.Dirty, "A keystroke taken back leaves nothing to save.");
        Assert.IsFalse(Find(takenBack, "RecursiveSave").Enabled, "Save is unavailable again.");
        await Press("RecursiveFacetStrengthPreference");
        var strengthOnly = await Wait("type-sourced-preference", s => s.Dirty
            && Facet(s, d => d.Type)?.Strength == KiCad.Automation.Protocol.Structural.StructuralGuidanceStrength.SgsPreference);
        Assert.AreEqual(("For the 3.3 V rail", 1, KiCad.Automation.Protocol.Structural.StructuralVerification.SvVerified), Provenance(strengthOnly),
            "A strength change keeps the facet's source, condition and verification.");
        await Press("RecursiveFacetValue"); await Wait("type-value-focused", s => s.FocusedControl == "RecursiveFacetValue");
        Key("a", control: true); Type("LDO regulator");
        var newValue = await Wait("type-new-value", s => Facet(s, d => d.Type)?.Values.SequenceEqual(["LDO regulator"]) == true);
        Assert.AreEqual(("", 0, KiCad.Automation.Protocol.Structural.StructuralVerification.SvUnverified), Provenance(newValue),
            "A new value keeps none of the source, condition or verification recorded for the old one.");
        ulong beforeSourcedSave = newValue.CompletedSaveCount;
        Key("s", control: true);
        await Wait("sourced-saved", s => s.CompletedSaveCount > beforeSourcedSave && !s.Dirty);
        var edited = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(created.Path, token));
        var editedPsu = edited.Inspect(edited.Inspect(edited.SelectedRoot).Children[0]);
        Assert.IsTrue(editedPsu.Definition!.Type!.SameContents(new(DefinitionChoiceState.Selected, ["LDO regulator"], GuidanceStrength.Preference, "", [],
            VerificationState.Unverified)), "The saved Type is the new value with its strength and nothing recorded for the old value.");
        Assert.IsTrue(editedPsu.Definition.Package!.SameContents(definition.Package!), "The other facets are unchanged.");
        Key("w", control: true); await Closed();
    }

    /// <summary>Round A3 option 1 (owner decision nf53af9d74841b7d3, kept sketch sd44465992aa73168) through the rendered editor, on the
    /// level the drawing and component-choice journeys saved. Selecting a connection shows its title, an editable Caption field,
    /// + Add detail, + Add requirement and Comments, and nothing else until more is defined (owner decision n98a3f3c41084f0ed).
    /// + Add detail offers only the details the connection does not have yet (its signals, direction, domain and type, which the
    /// format-2 model stores); choosing one adds exactly that detail as its own row, and removing the row returns the connection to
    /// how it was. Signals become the connection's members. Details an agent writes through MCP (a proposal it publishes and
    /// chooses) show the same way once the editor reads the changed file. Save, Decline, undo and redo, a compact window and a
    /// reopen keep them; a blank caption and a repeated signal are refused and nothing is written.</summary>
    private static async Task VerifyConnectionDetails(McpClient client, NativeClient native, int processId, string display, CreatedDiagram created,
        string instanceId, string evidence, CancellationToken token)
    {
        const string title = "Structural diagram";
        var document = new P.ReadRecursiveDiagramEditor { DocumentId = created.DocumentId };
        Task<P.RecursiveDiagramEditorState> Read() => native.InvokeAsync<P.ReadRecursiveDiagramEditor, P.RecursiveDiagramEditorState>(document, token);
        async Task<P.RecursiveDiagramEditorState> Wait(string step, Func<P.RecursiveDiagramEditorState, bool> condition)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            P.RecursiveDiagramEditorState current = new();
            try
            {
                while (true)
                {
                    current = await Read();
                    if (!current.Busy && !current.Dragging && condition(current)) return current;
                    await Task.Delay(50, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-details-timeout-" + step + ".json"), SchematicJson.Formatter.Format(current), token);
                await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-details-timeout-" + step + ".png"), token);
                throw new AssertFailedException("The connection-detail step '" + step + "' did not reach its expected state; its state and screen are retained.");
            }
        }
        void Key(string key, bool control = false, bool alt = false) => NativeKeyboard.SchematicShortcut(display, processId, key, title, control, false, altKey: alt);
        void Type(string value) { foreach (char character in value) Key(character.ToString()); }
        void Click(int x, int y) => NativeKeyboard.SchematicShortcut(display, processId, "click", title, false, true, clickFromLeft: x, clickFromTop: y);
        P.DiagramControlRect Find(P.RecursiveDiagramEditorState at, string name) => at.Controls.Single(c => c.Name == name);
        bool Inside(P.DiagramControlRect inner, P.DiagramControlRect outer) => inner.X >= outer.X && inner.X + inner.Width <= outer.X + outer.Width
            && inner.Y >= outer.Y && inner.Y + inner.Height <= outer.Y + outer.Height;
        async Task Press(string name, bool inInspector = true)
        {
            // The inspector re-lays out when a row appears or goes; press a control only where two readings agree, and only
            // once it is inside the inspector's visible area.
            var at = await Read(); var control = Find(at, name);
            using (var settle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                settle.CancelAfter(TimeSpan.FromSeconds(5));
                while (true)
                {
                    await Task.Delay(120, settle.Token);
                    var again = await Read(); var moved = Find(again, name);
                    if (moved.Equals(control) && Find(again, "RecursiveInspector").Equals(Find(at, "RecursiveInspector"))) break;
                    at = again; control = moved;
                }
            }
            Assert.IsTrue(control.Shown && control.Enabled, name + " must be shown and enabled before it is pressed.");
            var inspector = Find(at, "RecursiveInspector");
            int x = control.X + control.Width / 2, y = control.Y + control.Height / 2;
            Assert.IsTrue(!inInspector || x >= inspector.X && x < inspector.X + inspector.Width && y >= inspector.Y && y < inspector.Y + inspector.Height,
                name + " must be inside the inspector's view before it is pressed.");
            Click(x, y);
        }
        async Task Popup()
        {
            using var menu = CancellationTokenSource.CreateLinkedTokenSource(token); menu.CancelAfter(TimeSpan.FromSeconds(15)); int count = 0;
            try
            {
                do { NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, observePopupCount: value => count = value); if (count == 0) await Task.Delay(50, menu.Token); }
                while (count == 0);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-details-timeout-popup.json"), SchematicJson.Formatter.Format(await Read()), token);
                await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-details-timeout-popup.png"), token);
                throw new AssertFailedException("The pressed control did not open its list; the state and screen are retained.");
            }
        }
        async Task PopupClosed()
        {
            using var menu = CancellationTokenSource.CreateLinkedTokenSource(token); menu.CancelAfter(TimeSpan.FromSeconds(15)); int count = 1;
            try
            {
                do { NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, observePopupCount: value => count = value); if (count != 0) await Task.Delay(50, menu.Token); }
                while (count != 0);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-details-timeout-popup-closed.png"), token);
                throw new AssertFailedException("The open list did not close; the screen is retained.");
            }
        }
        // + Add detail lists only the details the connection does not have yet, in the order signals, direction, domain, type.
        async Task AddDetail(params string[] keys) { await Press("RecursiveAddDetail"); await Popup(); foreach (string key in keys) Key(key); Key("Return"); }
        async Task Capture(string name)
        {
            await Task.Delay(FadeSettleMilliseconds, token);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-details-" + name + ".png"), token);
        }
        async Task<CapturedWindow> Shot(string name)
        {
            await Capture(name);
            return await CapturedWindow.LoadAsync(Path.Combine(evidence, instanceId + "-details-" + name + ".png"), WindowOrigin(display, processId, title), token);
        }
        // Parks the pointer on an empty corner of the canvas, so a capture measures the controls rather than the pointer drawn
        // over the one pressed last.
        async Task ParkPointer()
        {
            var at = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: at.CanvasWindowX + (int)at.CanvasPixelWidth - 6,
                clickFromTop: at.CanvasWindowY + (int)at.CanvasPixelHeight - 6);
        }
        // The route the editor reports it drew for a connection, in window pixels.
        async Task<(int X, int Y)[]> RoutePixels(P.RecursiveDiagramEditorState at, string connection)
        {
            var observed = await client.CallToolAsync("kicad_diagram_observe", new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId, ["documentId"] = created.DocumentId, ["expectedSourceToken"] = at.SourceToken,
                ["expectedViewRevision"] = at.ViewRevision, ["views"] = new[] { new { viewId = "canvas", pixelWidth = 800, pixelHeight = 600 } }
            }, cancellationToken: token);
            Assert.IsFalse(observed.IsError == true, "The level can be observed.");
            var route = JsonSerializer.SerializeToElement(observed).GetProperty("structuredContent").GetProperty("observation").GetProperty("views")[0]
                .GetProperty("resolvedLayout").GetProperty("routes").EnumerateArray().First(r => r.GetProperty("connectionId").GetString() == connection);
            return [.. route.GetProperty("points").EnumerateArray().Select(p => Screen(at,
                double.Parse(p.GetProperty("x").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p.GetProperty("y").GetString()!, System.Globalization.CultureInfo.InvariantCulture)))];
        }
        async Task Closed()
        {
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(token); closing.CancelAfter(TimeSpan.FromSeconds(15));
            while (NativeKeyboard.HasWindow(display, processId, title)) await Task.Delay(50, closing.Token);
        }
        async Task<P.RecursiveDiagramEditorState> Open(string step)
        {
            var opened = await client.CallToolAsync("kicad_diagram_open", new Dictionary<string, object?> { ["instanceId"] = instanceId,
                ["repositoryRoot"] = created.RepositoryRoot, ["sourcePath"] = created.Path, ["documentId"] = created.DocumentId }, cancellationToken: token);
            Assert.IsFalse(opened.IsError == true, "The saved diagram opens again.");
            return await Wait(step, s => s.Ready && s.Rendered);
        }
        (int X, int Y) Screen(P.RecursiveDiagramEditorState at, double x, double y) =>
            ((int)Math.Round(at.CanvasWindowX + (x - at.CanvasOriginX) * at.CanvasScale), (int)Math.Round(at.CanvasWindowY + (y - at.CanvasOriginY) * at.CanvasScale));
        async Task At(double x, double y) { var (px, py) = Screen(await Read(), x, y); Click(px, py); }
        // A connection is selected by a click on the middle of the longest leg the editor reports it drew.
        async Task SelectConnection(string step, string connection)
        {
            var at = await Wait(step + "-idle", s => s.Rendered);
            var observed = await client.CallToolAsync("kicad_diagram_observe", new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId, ["documentId"] = created.DocumentId, ["expectedSourceToken"] = at.SourceToken,
                ["expectedViewRevision"] = at.ViewRevision, ["views"] = new[] { new { viewId = "canvas", pixelWidth = 800, pixelHeight = 600 } }
            }, cancellationToken: token);
            Assert.IsFalse(observed.IsError == true, "The level can be observed.");
            var route = JsonSerializer.SerializeToElement(observed).GetProperty("structuredContent").GetProperty("observation").GetProperty("views")[0]
                .GetProperty("resolvedLayout").GetProperty("routes").EnumerateArray().First(r => r.GetProperty("connectionId").GetString() == connection);
            var points = route.GetProperty("points").EnumerateArray().Select(p => (double.Parse(p.GetProperty("x").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p.GetProperty("y").GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            int longest = 1;
            for (int i = 2; i < points.Length; ++i)
                if (Math.Abs(points[i].Item1 - points[i - 1].Item1) + Math.Abs(points[i].Item2 - points[i - 1].Item2)
                    > Math.Abs(points[longest].Item1 - points[longest - 1].Item1) + Math.Abs(points[longest].Item2 - points[longest - 1].Item2)) longest = i;
            var (px, py) = Screen(at, (points[longest - 1].Item1 + points[longest].Item1) / 2, (points[longest - 1].Item2 + points[longest].Item2) / 2);
            Click(px, py);
            await Wait(step, s => s.ConnectionDraft?.Baseline.ConnectionId == connection && s.SelectedInterfaceId == "");
        }
        async Task ClickBlankCanvas(string step, string scope)
        {
            var at = await Read(); Click(at.CanvasWindowX + (int)at.CanvasPixelWidth - 30, at.CanvasWindowY + (int)at.CanvasPixelHeight - 30);
            await Wait(step, s => s.ConnectionDraft is null && s.Draft.Baseline.BlockId == scope);
        }
        string[] Details(P.RecursiveDiagramEditorState at) => [.. at.ShownConnectionDetails];
        string[] Signals(P.RecursiveDiagramEditorState at) => [.. at.ShownSignals];
        string[] Active(P.RecursiveDiagramEditorState at, string prefix) =>
            [.. at.Controls.Where(c => c.Name.StartsWith(prefix, StringComparison.Ordinal) && c.Active).Select(c => c.Name[prefix.Length..])];
        uint[] Arrows(P.RecursiveDiagramEditorState at, string connection) =>
            [.. at.ConnectionMarks.SingleOrDefault(m => m.ConnectionId == connection)?.ArrowEndpoints ?? []];
        // Only the caption, + Add detail, + Add requirement and Comments; no detail row, requirement box, end list or field history.
        void CaptionOnly(P.RecursiveDiagramEditorState at, string step)
        {
            Assert.AreEqual("Connection", Find(at, "RecursiveOwnerCaption").Label, step + ": the inspector's title names what is selected.");
            Assert.IsTrue(Find(at, "RecursiveConnectionCaption").Shown && Find(at, "RecursiveConnectionCaption").Enabled, step + ": the caption is editable.");
            Assert.AreEqual(at.ConnectionDraft.Name, Find(at, "RecursiveConnectionCaption").Label, step + ": the caption field shows the connection's caption.");
            Assert.IsEmpty(at.ShownConnectionDetails, step + ": no detail row until one is defined.");
            Assert.IsEmpty(at.ShownRequirementFields, step + ": no empty requirement box.");
            Assert.IsTrue(Find(at, "RecursiveAddDetail").Shown && Find(at, "RecursiveAddRequirement").Shown, step + ": + Add detail and + Add requirement.");
            Assert.IsTrue(Find(at, "RecursiveComments").Shown, step + ": Comments.");
            Assert.IsFalse(Find(at, "RecursiveConnectionEndpoints").Shown, step + ": a drawn end is on the canvas, not repeated in the inspector.");
            foreach (var name in new[] { "RecursiveDetailRemoveSignals", "RecursiveDetailRemoveDirection", "RecursiveDetailRemoveDomain", "RecursiveDetailRemoveType",
                         "RecursiveDetailRemoveEndpoints", "RecursiveSignalEntry", "RecursiveFieldHistory0", "RecursiveOpenDiagram" })
                Assert.IsFalse(Find(at, name).Shown, step + ": " + name + " is not shown.");
            Assert.IsFalse(at.Controls.Any(c => (c.Name.StartsWith("RecursiveDirection", StringComparison.Ordinal) || c.Name.StartsWith("RecursiveDomain", StringComparison.Ordinal)
                || c.Name.StartsWith("RecursiveType", StringComparison.Ordinal)) && c.Shown), step + ": no choice is offered up front.");
        }
        // A signal drawn in this draft is removed by the editor itself, as the inverse of its addition (contract rbg-v2 erratum
        // "connection signals in the level draft", section 4.7): the level draft is exactly what it was without that signal, no
        // removal effect is reported, the status line names no removal (a saved signal's cascade says "Removed …"), and nothing
        // is written.
        async Task DrawnRemoval(P.RecursiveDiagramEditorState at, P.LevelDraftData expected, string file, string step)
        {
            Assert.AreEqual(expected, at.LevelDraft, step + ": the level draft is exactly what it was without the drawn signal.");
            Assert.IsEmpty(at.LastEffects, step + ": removing a drawn signal reports no removal effect.");
            Assert.AreEqual(at.Dirty ? "Unsaved changes" : "", at.StatusText, step + ": the status line names no removal.");
            Assert.AreEqual("", at.ErrorMessage, step + ": no error.");
            Assert.AreEqual(file, await File.ReadAllTextAsync(created.Path, token), step + ": nothing is written.");
        }
        // The draft without one drawn signal: its own new connection and the member line its addition gave a saved connection's draft.
        static P.LevelDraftData Without(P.LevelDraftData draft, string signal)
        {
            var expected = draft.Clone();
            expected.NewConnections.Remove(expected.NewConnections.Single(c => c.Selection.ConnectionId == signal));
            foreach (var link in expected.ConnectionDrafts)
                foreach (var member in link.Members.Where(m => m.ConnectionId == signal).ToArray()) link.Members.Remove(member);
            return expected;
        }

        var start = await Open("start");
        string startXml = await File.ReadAllTextAsync(created.Path, token);
        var level = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(created.Path, token));
        var top = level.Inspect(level.SelectedRoot);
        string root = level.SelectedRoot.BlockId.ToString("D");
        var levelLinks = level.Connections(level.SelectedRoot.BlockId);
        string power = top.LocalDiagram.Connections.Single(c => levelLinks.Inspect(c).Name == "Power").ConnectionId.ToString("D");
        string feed = top.LocalDiagram.Connections.Single(c => levelLinks.Inspect(c).Name == "Rail feed").ConnectionId.ToString("D");
        string psuName = level.Inspect(top.Children[0]).Name, cpuName = level.Inspect(top.Children[1]).Name;

        // The kept sketch's state: a connection just drawn from a block to a port on the level's boundary and captioned is
        // selected, and the inspector shows only its title, caption, + Add detail, + Add requirement and Comments.
        var layout = top.LocalDiagram.Layout; var frame = layout.Frame!;
        var boundary = top.LocalDiagram.Interfaces.Single(); var port = layout.Ports.Single(p => p.InterfaceId == boundary.Id);
        (double X, double Y) anchor = port.Side switch
        {
            DiagramPortSide.Right => ((double)(frame.X + frame.Width), (double)(frame.Y + port.Offset)),
            DiagramPortSide.Top => ((double)(frame.X + port.Offset), (double)frame.Y),
            DiagramPortSide.Bottom => ((double)(frame.X + port.Offset), (double)(frame.Y + frame.Height)),
            _ => ((double)frame.X, (double)(frame.Y + port.Offset))
        };
        var psuRect = layout.Blocks.Single(b => b.BlockId == top.Children[0].BlockId).Rect;
        await Press("RecursiveToolConnect", inInspector: false);
        await Wait("connect-tool", s => s.CanvasTool == "connect");
        await At((double)(psuRect.X + psuRect.Width / 2), (double)(psuRect.Y + psuRect.Height / 2));
        await Wait("connect-from-psu", s => s.CanvasHint == "Click a port to finish connection");
        await At(anchor.X, anchor.Y); await Wait("new-caption", s => s.CaptionEditor == "connection");
        Type("Supply input"); Key("Return");
        var drawn = await Wait("new-connection", s => s.CaptionEditor == "" && s.LevelDraft.NewConnections.Count == 1 && s.ConnectionDraft?.Name == "Supply input");
        string supply = drawn.LevelDraft.NewConnections[0].Selection.ConnectionId;
        CaptionOnly(drawn, "new-connection");
        Assert.IsFalse(Find(drawn, "RecursiveSavedVersion").Shown, "A connection drawn in this draft has no saved version yet.");
        {
            // Design QA round 2, R2-P2-1: the new connection's caption is drawn on the canvas, whole, in a clear place near its short
            // route to the port on the level's boundary (whose own name stays beside the port), as every other caption is.
            var shot = await Shot("new-connection");
            VerifyCaptions(shot, drawn, "new-connection", new Dictionary<string, string> { [supply] = "Supply input", [power] = "Power", [feed] = "Rail feed" },
                "Supply input");
            var caption = drawn.ConnectionCaptions.Single(c => c.ObjectId == supply);
            var route = await RoutePixels(drawn, supply);
            double Distance((int X, int Y) a, (int X, int Y) b)
            {
                // From the caption's box to the segment a-b (both axis-aligned boxes): the gap between them, 0 when they touch.
                int dx = Math.Max(0, Math.Max(Math.Min(a.X, b.X) - (caption.X + caption.Width), caption.X - Math.Max(a.X, b.X)));
                int dy = Math.Max(0, Math.Max(Math.Min(a.Y, b.Y) - (caption.Y + caption.Height), caption.Y - Math.Max(a.Y, b.Y)));
                return Math.Sqrt(dx * dx + dy * dy);
            }
            double nearest = route.Zip(route.Skip(1)).Min(pair => Distance(pair.First, pair.Second));
            Assert.IsTrue(shot.Record("R2-P2-1 Supply input caption from its connection (px)", (int)Math.Round(nearest)) <= 64,
                $"new-connection: the caption 'Supply input' lies {nearest:F0} pixels from its connection, beside it.");
        }
        // Escape closes + Add detail without adding anything.
        await Press("RecursiveAddDetail"); await Popup(); Key("Escape"); await PopupClosed();
        var menuCancelled = await Wait("add-detail-cancelled", s => Details(s).Length == 0);
        CaptionOnly(menuCancelled, "add-detail-cancelled");
        Assert.AreEqual(drawn.LevelDraft, menuCancelled.LevelDraft, "Escape in + Add detail changes nothing.");
        // An empty Signals row, with a name typed but not entered, goes when the connection becomes a single signal: a single signal
        // has no signals, so + Add detail stops offering them. Removing the Type row makes it abstract again, and removing the
        // Direction row returns it to its caption.
        await AddDetail("Home");
        await Wait("empty-signals-row", s => Details(s).SequenceEqual(["signals"]) && s.FocusedControl == "RecursiveSignalEntry");
        Type("SENSE");
        await Wait("signal-name-typed", s => Find(s, "RecursiveSignalEntry").Label == "SENSE");
        await AddDetail("Home", "Down", "Down");
        var newType = await Wait("new-type-row", s => Details(s).SequenceEqual(["signals", "type"]));
        Assert.IsEmpty(Active(newType, "RecursiveType"), "No type is chosen for the person.");
        Assert.IsTrue(Find(newType, "RecursiveTypeSignal").Enabled, "A connection without signals may be a single signal.");
        Assert.IsFalse(Find(newType, "RecursiveTypeDifferentialPair").Enabled, "A pair needs exactly two signals.");
        await Press("RecursiveTypeSignal");
        var single = await Wait("new-type-signal", s => s.ConnectionDraft.Kind == P.DiagramConnectionKind.DckSignal && Details(s).SequenceEqual(["type"]));
        Assert.IsFalse(Find(single, "RecursiveSignalEntry").Shown, "A single signal shows no entry for signals of its own.");
        Assert.AreEqual("", Find(single, "RecursiveSignalEntry").Label, "The name that was typed but not entered is gone.");
        Assert.IsFalse(single.LevelDraft.NewConnections.Any(c => c.HasMemberOf), "The typed name never became a signal.");
        await AddDetail("Home");
        await Wait("single-signal-direction", s => Details(s).SequenceEqual(["direction", "type"]));
        await Press("RecursiveDetailRemoveType");
        var typeRemoved = await Wait("new-type-removed", s => s.ConnectionDraft.Kind == P.DiagramConnectionKind.DckAbstract && Details(s).SequenceEqual(["direction"]));
        Assert.IsFalse(Find(typeRemoved, "RecursiveSignalEntry").Shown, "The empty Signals row does not come back by itself.");
        await Press("RecursiveDetailRemoveDirection");
        CaptionOnly(await Wait("new-back-to-caption", s => Details(s).Length == 0), "new-back-to-caption");
        // + Add detail > Signals gives the new connection its first signals: members drawn for it in this draft.
        await AddDetail("Home");
        await Wait("new-signals-row", s => Details(s).SequenceEqual(["signals"]) && s.FocusedControl == "RecursiveSignalEntry");
        Type("VIN"); Key("Return"); Type("RTN"); Key("Return");
        var newSignals = await Wait("new-signals", s => Signals(s).SequenceEqual(["VIN", "RTN"]));
        var drawnForSupply = newSignals.LevelDraft.NewConnections.Where(c => c.HasMemberOf && c.MemberOf == supply).ToArray();
        CollectionAssert.AreEqual(new[] { "VIN", "RTN" }, drawnForSupply.Select(c => c.Name).ToArray());
        CollectionAssert.AreEqual(drawnForSupply.Select(c => c.Selection.ConnectionId).ToArray(), newSignals.ConnectionDraft.Members.Select(m => m.ConnectionId).ToArray());
        Assert.AreEqual(psuName + " \u2192 " + boundary.Name, Find(newSignals, "RecursiveDirectionFromFirst").Label, "A direction would read from the block to the port.");
        // A third signal drawn for the new connection and removed again with its own remove button leaves the draft exactly as it
        // was before Enter added it; Undo brings it back exactly, and Redo removes it again.
        Type("AUX"); Key("Return");
        var withAux = await Wait("new-signal-aux", s => Signals(s).SequenceEqual(["VIN", "RTN", "AUX"]));
        Assert.AreEqual(supply, withAux.LevelDraft.NewConnections.Single(c => c.Name == "AUX").MemberOf, "AUX is drawn for Supply input.");
        await Press("RecursiveSignalRemove2");
        await DrawnRemoval(await Wait("new-signal-aux-removed", s => Signals(s).SequenceEqual(["VIN", "RTN"])), newSignals.LevelDraft, startXml,
            "new-signal-aux-removed");
        await Press("RecursiveSignalEntry");
        Key("z", control: true);
        var auxBack = await Wait("new-signal-aux-undone", s => Signals(s).SequenceEqual(["VIN", "RTN", "AUX"]));
        Assert.AreEqual(withAux.LevelDraft, auxBack.LevelDraft, "Undo restores the drawn signal exactly.");
        Key("y", control: true);
        await DrawnRemoval(await Wait("new-signal-aux-redone", s => Signals(s).SequenceEqual(["VIN", "RTN"])), newSignals.LevelDraft, startXml,
            "new-signal-aux-redone");
        // With exactly two single signals the connection may be a differential pair; a pair keeps its two signals, so their remove
        // buttons, the Signals row's remove button and the entry are unavailable until another type is chosen.
        await AddDetail("Home", "Down", "Down");
        var pairRow = await Wait("new-pair-row", s => Details(s).SequenceEqual(["signals", "type"]));
        Assert.IsFalse(Find(pairRow, "RecursiveTypeSignal").Enabled, "A connection with signals cannot be a single signal.");
        await Press("RecursiveTypeDifferentialPair");
        var pair = await Wait("new-pair", s => s.ConnectionDraft.Kind == P.DiagramConnectionKind.DckDifferentialPair
            && Active(s, "RecursiveType").SequenceEqual(["DifferentialPair"]));
        foreach (var name in new[] { "RecursiveSignalRemove0", "RecursiveSignalRemove1", "RecursiveSignalEntry", "RecursiveDetailRemoveSignals" })
            Assert.IsTrue(Find(pair, name).Shown && !Find(pair, name).Enabled, name + " is shown but unavailable for a differential pair.");
        Assert.IsTrue(Signals(pair).SequenceEqual(["VIN", "RTN"]));
        await Capture("pair");

        // A saved connection likewise shows only what it has: here, its caption and its saved version.
        await SelectConnection("power-selected", power);
        var selected = await Read();
        CaptionOnly(selected, "power-selected");
        Assert.AreEqual("Selected connection: v1", Find(selected, "RecursiveSavedVersion").Label);
        Assert.IsEmpty(Arrows(selected, power), "A connection without a direction has no arrowhead.");
        await Capture("selected");

        // + Add requirement adds one requirement box, here General, with focus.
        await Press("RecursiveAddRequirement"); await Popup(); Key("Home"); Key("Return");
        await Wait("general-revealed", s => s.ShownRequirementFields.SequenceEqual(new[] { P.RequirementFieldKind.RfkGeneral }) && s.FocusedControl == "RecursiveRequirements0");
        Type("Keep the return path short.");
        await Wait("general-typed", s => s.ConnectionDraft.Fields.General == "Keep the return path short." && s.Dirty);

        // The caption is edited in place. A blank caption is refused: the draft keeps the last caption, Save refuses with the notice
        // and writes nothing, and Escape brings the caption back.
        const string blankCaption = "Type a caption for this connection.";
        await Press("RecursiveConnectionCaption"); Key("a", control: true); Type("Supply");
        await Wait("caption-typed", s => s.ConnectionDraft.Name == "Supply" && s.FocusedControl == "RecursiveConnectionCaption");
        Key("a", control: true); Key("BackSpace");
        var blank = await Wait("caption-blank", s => s.ConnectionNotice == blankCaption);
        Assert.AreEqual("Supply", blank.ConnectionDraft.Name, "A blank caption is not stored.");
        ulong saves = blank.CompletedSaveCount; string unsaved = await File.ReadAllTextAsync(created.Path, token);
        Key("s", control: true);
        var refused = await Wait("caption-save-refused", s => s.Notice == blankCaption && s.FocusedControl == "RecursiveConnectionCaption");
        Assert.AreEqual(saves, refused.CompletedSaveCount, "Nothing was sent to save."); Assert.IsTrue(refused.Dirty);
        Assert.AreEqual(unsaved, await File.ReadAllTextAsync(created.Path, token));
        Key("Escape");
        await Wait("caption-restored", s => s.ConnectionNotice == "" && s.Notice == "" && Find(s, "RecursiveConnectionCaption").Label == "Supply");
        Key("a", control: true); Type("Power");
        await Wait("caption-power", s => s.ConnectionDraft.Name == "Power" && Find(s, "RecursiveConnectionCaption").Label == "Power");

        // + Add detail > Direction adds exactly a direction row, with no choice made yet; removing it returns to the caption-only
        // connection. Then the direction is chosen: the canvas shows an arrowhead into the end it points to. Undo and redo follow.
        await AddDetail("Home", "Down");
        var direction = await Wait("direction-row", s => Details(s).SequenceEqual(["direction"]) && s.FocusedControl == "RecursiveDirectionFromFirst");
        Assert.IsEmpty(Active(direction, "RecursiveDirection"), "No direction is chosen for the person.");
        Assert.AreEqual(P.DiagramConnectionDirection.DcdrUnspecified, direction.ConnectionDraft.Direction);
        Assert.AreEqual(psuName + " → " + cpuName, Find(direction, "RecursiveDirectionFromFirst").Label, "The direction names the connection's own ends.");
        Assert.AreEqual(cpuName + " → " + psuName, Find(direction, "RecursiveDirectionToFirst").Label);
        Assert.AreEqual("Both ways", Find(direction, "RecursiveDirectionBoth").Label);
        Assert.IsTrue(new[] { "FromFirst", "ToFirst", "Both" }.All(n => Find(direction, "RecursiveDirection" + n).Tooltip == ""),
            "No direction choice has a tooltip that only repeats its label (design QA round 2, P3 19).");
        await Capture("direction-row");
        await Press("RecursiveDetailRemoveDirection");
        var directionGone = await Wait("direction-row-removed", s => Details(s).Length == 0 && s.FocusedControl == "RecursiveAddDetail");
        Assert.AreEqual(P.DiagramConnectionDirection.DcdrUnspecified, directionGone.ConnectionDraft.Direction);
        await AddDetail("Home", "Down");
        await Wait("direction-row-again", s => Details(s).SequenceEqual(["direction"]));
        await Press("RecursiveDirectionFromFirst");
        var fromFirst = await Wait("direction-from-first", s => s.ConnectionDraft.Direction == P.DiagramConnectionDirection.DcdrFromFirst
            && Active(s, "RecursiveDirection").SequenceEqual(["FromFirst"]));
        CollectionAssert.AreEqual(new uint[] { 1 }, Arrows(fromFirst, power), "One arrowhead points into the CPU end.");
        Assert.IsTrue(fromFirst.ConnectionMarks.Single(m => m.ConnectionId == power).Arrows.All(a => a.Shown), "The arrowhead is drawn inside the canvas.");
        await Press("RecursiveDirectionToFirst");
        var toFirst = await Wait("direction-to-first", s => s.ConnectionDraft.Direction == P.DiagramConnectionDirection.DcdrToFirst
            && Active(s, "RecursiveDirection").SequenceEqual(["ToFirst"]));
        CollectionAssert.AreEqual(new uint[] { 0 }, Arrows(toFirst, power), "The reverse direction shows one arrowhead, into the PSU end only.");
        Assert.IsTrue(toFirst.ConnectionMarks.Single(m => m.ConnectionId == power).Arrows.All(a => a.Shown));
        await Press("RecursiveDirectionBoth");
        var both = await Wait("direction-both", s => s.ConnectionDraft.Direction == P.DiagramConnectionDirection.DcdrBidirectional);
        CollectionAssert.AreEqual(new uint[] { 0, 1 }, Arrows(both, power), "Both ways shows an arrowhead at each end.");
        Key("z", control: true); await Wait("direction-undone", s => s.ConnectionDraft.Direction == P.DiagramConnectionDirection.DcdrToFirst);
        Key("y", control: true); await Wait("direction-redone", s => s.ConnectionDraft.Direction == P.DiagramConnectionDirection.DcdrBidirectional);

        // + Add detail > Signals: each typed name and Enter adds one signal; a repeated name is refused with a notice; Escape clears
        // the entry; a signal's own remove button takes only that signal away, and Undo brings it back.
        await AddDetail("Home");
        var signalsRow = await Wait("signals-row", s => Details(s).SequenceEqual(["signals", "direction"]) && s.FocusedControl == "RecursiveSignalEntry");
        Type("VBUS"); Key("Return");
        await Wait("signal-vbus", s => Signals(s).SequenceEqual(["VBUS"]) && Find(s, "RecursiveSignalEntry").Label == "");
        Type("GND"); Key("Return");
        var twoSignals = await Wait("signal-gnd", s => Signals(s).SequenceEqual(["VBUS", "GND"]));
        var drawnSignals = twoSignals.LevelDraft.NewConnections.Where(c => c.HasMemberOf && c.MemberOf == power).ToArray();
        CollectionAssert.AreEqual(new[] { "VBUS", "GND" }, drawnSignals.Select(c => c.Name).ToArray(), "Each signal is a member drawn for this connection.");
        Assert.IsTrue(drawnSignals.All(c => c.Kind == P.DiagramConnectionKind.DckSignal));
        CollectionAssert.AreEqual(drawnSignals.Select(c => c.Selection.ConnectionId).ToArray(), twoSignals.ConnectionDraft.Members.Select(m => m.ConnectionId).ToArray());
        Type("GND"); Key("Return");
        await Wait("signal-repeated", s => s.ConnectionNotice == "“GND” is already a signal of this connection." && Signals(s).Length == 2);
        Key("Escape");
        await Wait("signal-entry-cleared", s => s.ConnectionNotice == "" && Find(s, "RecursiveSignalEntry").Label == "" && s.FocusedControl == "RecursiveSignalEntry");
        await Press("RecursiveSignalRemove0");
        var vbusGone = await Wait("signal-removed", s => Signals(s).SequenceEqual(["GND"]));
        await DrawnRemoval(vbusGone, Without(twoSignals.LevelDraft, drawnSignals[0].Selection.ConnectionId), startXml, "signal-removed");
        Key("z", control: true);
        var restored = await Wait("signal-restored", s => Signals(s).SequenceEqual(["VBUS", "GND"]));
        Assert.AreEqual(twoSignals.LevelDraft, restored.LevelDraft, "Undo restores the drawn signal exactly.");
        // The Signals row's remove button, while every signal was drawn in this draft, is their removal too: the draft is again
        // exactly what it was before + Add detail > Signals, and Undo brings both back.
        await Press("RecursiveDetailRemoveSignals");
        await DrawnRemoval(await Wait("drawn-signals-removed", s => Signals(s).Length == 0 && Details(s).SequenceEqual(["direction"])),
            signalsRow.LevelDraft, startXml, "drawn-signals-removed");
        Key("z", control: true);
        var bothBack = await Wait("drawn-signals-restored", s => Signals(s).SequenceEqual(["VBUS", "GND"]) && Details(s).SequenceEqual(["signals", "direction"]));
        Assert.AreEqual(twoSignals.LevelDraft, bothBack.LevelDraft, "Undo brings both drawn signals back exactly.");
        {
            // Design QA round 2, R2-P2-5: the action that removes the whole Signals detail is a link that says so ("Remove signals"),
            // visibly unlike each signal's own "×", which is named after the signal it removes on hover and for assistive technology.
            await ParkPointer();
            VerifySignalRemoves(await Shot("signals"), bothBack, "details-signals", "VBUS", "GND");
            VerifyCanvasText(bothBack, "details-signals");
        }

        // + Add detail > Type: a connection with signals cannot be a single signal; with exactly two signals it may be a pair.
        await AddDetail("Home", "Down");
        var type = await Wait("type-row", s => Details(s).SequenceEqual(["signals", "direction", "type"]));
        Assert.IsFalse(Find(type, "RecursiveTypeSignal").Enabled, "A connection with signals cannot be a single signal.");
        Assert.AreEqual("A single signal has no signals of its own; remove its signals first.", Find(type, "RecursiveTypeSignal").Tooltip,
            "The unavailable Signal says why on hover (design QA round 2, P3 20).");
        Assert.AreEqual("", Find(type, "RecursiveTypeInterface").Tooltip, "An available type has no tooltip that repeats its label.");
        Assert.IsTrue(Find(type, "RecursiveTypeDifferentialPair").Enabled, "Two signals may form a differential pair.");
        await Press("RecursiveTypeInterface");
        await Wait("type-interface", s => s.ConnectionDraft.Kind == P.DiagramConnectionKind.DckInterface && Active(s, "RecursiveType").SequenceEqual(["Interface"]));
        await Press("RecursiveTypeSignalGroup");
        await Wait("type-group", s => s.ConnectionDraft.Kind == P.DiagramConnectionKind.DckSignalGroup && Active(s, "RecursiveType").SequenceEqual(["SignalGroup"]));
        // + Add detail > Domain, the last detail it does not have; + Add detail then has nothing left to offer.
        await AddDetail("Home");
        var domainRow = await Wait("domain-row", s => Details(s).SequenceEqual(["signals", "direction", "domain", "type"]));
        Assert.IsEmpty(Active(domainRow, "RecursiveDomain"), "No domain is chosen for the person.");
        // Each domain choice stores its domain; one choice is shown as chosen at a time.
        foreach (var (domainName, domainValue) in new[] { ("Data", P.DiagramDomain.DdData), ("Control", P.DiagramDomain.DdControl),
                     ("Analog", P.DiagramDomain.DdAnalog), ("Mechanical", P.DiagramDomain.DdMechanical) })
        {
            await Press("RecursiveDomain" + domainName);
            await Wait("domain-" + domainName.ToLowerInvariant(), s => s.ConnectionDraft.Domain == domainValue
                && Active(s, "RecursiveDomain").SequenceEqual([domainName]));
        }
        await Press("RecursiveDomainPower");
        var all = await Wait("domain-power", s => s.ConnectionDraft.Domain == P.DiagramDomain.DdPower && !Find(s, "RecursiveAddDetail").Shown);
        Assert.IsTrue(Find(all, "RecursiveAddRequirement").Shown, "+ Add requirement stays for the two requirement boxes not shown yet.");
        {
            // Design QA round 2, R2-P2-4: a chosen option has the drawing tools' accent style, told apart from the others by more
            // than its fill; and under the pointer an option that is not chosen turns a neutral grey, never the accent.
            await ParkPointer();
            var shot = await Shot("all-details");
            VerifyChoiceStyling(shot, all, "details-all-details", ["DirectionBoth", "DomainPower", "TypeSignalGroup"],
                ["DirectionFromFirst", "DirectionToFirst", "DomainData", "DomainControl", "TypeInterface"]);
            var hovered = Find(all, "RecursiveDomainData");
            NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: hovered.X + 6, clickFromTop: hovered.Y + hovered.Height / 2);
            await Task.Delay(300, token);
            var hoverShot = await Shot("all-details-hover");
            var hoverFill = hoverShot.At(hovered.X + 4, hovered.Y + hovered.Height / 2);
            var restingFill = hoverShot.At(Find(all, "RecursiveDomainControl").X + 4, hovered.Y + hovered.Height / 2);
            var chosenFill = hoverShot.At(Find(all, "RecursiveDomainPower").X + 4, hovered.Y + hovered.Height / 2);
            int spread = Math.Max(hoverFill.R, Math.Max(hoverFill.G, hoverFill.B)) - Math.Min(hoverFill.R, Math.Min(hoverFill.G, hoverFill.B));
            Assert.IsTrue(CapturedWindow.Distance(hoverFill, restingFill) >= 6 && spread <= 12,
                $"details-all-details: under the pointer Data turns a neutral grey {CapturedWindow.Describe(hoverFill)} (at rest {CapturedWindow.Describe(restingFill)}).");
            Assert.IsTrue(CapturedWindow.Distance(hoverFill, chosenFill) >= 12,
                $"details-all-details: hover {CapturedWindow.Describe(hoverFill)} does not look like the chosen Power {CapturedWindow.Describe(chosenFill)}.");
            var caption = Find(all, "RecursiveConnectionCaption");
            NativeKeyboard.SchematicShortcut(display, processId, "motion", title, false, true, clickFromLeft: caption.X + caption.Width / 2, clickFromTop: caption.Y + caption.Height / 2);
        }
        // Removing a row takes exactly that detail away; Undo brings it back.
        await Press("RecursiveDetailRemoveDomain");
        await Wait("domain-removed", s => s.ConnectionDraft.Domain == P.DiagramDomain.DdUnspecified && Details(s).SequenceEqual(["signals", "direction", "type"])
            && Find(s, "RecursiveAddDetail").Shown);
        Key("z", control: true);
        await Wait("domain-restored", s => s.ConnectionDraft.Domain == P.DiagramDomain.DdPower && Details(s).SequenceEqual(["signals", "direction", "domain", "type"]));

        // Save writes one level revision with Power's successor holding exactly these details and its two signals as members.
        ulong beforeSave = (await Read()).CompletedSaveCount;
        Key("s", control: true);
        var saved = await Wait("saved", s => s.CompletedSaveCount > beforeSave && !s.Dirty);
        Assert.AreEqual("", saved.ErrorMessage);
        string savedXml = await File.ReadAllTextAsync(created.Path, token);
        var graph = RecursiveBlockGraphXml.Read(savedXml); var savedTop = graph.Inspect(graph.SelectedRoot); var links = graph.Connections(graph.SelectedRoot.BlockId);
        var powerSaved = links.Inspect(savedTop.LocalDiagram.Connections.Single(c => c.ConnectionId.ToString("D") == power));
        Assert.AreEqual(("Power", DiagramConnectionKind.SignalGroup, DiagramDomain.Power, DiagramConnectionDirection.Bidirectional),
            (powerSaved.Name, powerSaved.Kind, powerSaved.Domain, powerSaved.Direction));
        CollectionAssert.AreEqual(new[] { "VBUS", "GND" }, powerSaved.Members.Select(m => links.Inspect(m).Name).ToArray());
        Assert.IsTrue(powerSaved.Members.All(m => links.Inspect(m).Kind == DiagramConnectionKind.Signal && links.Inspect(m).Endpoints.Length == powerSaved.Endpoints.Length
            && links.Inspect(m).Endpoints.Zip(powerSaved.Endpoints).All(e => e.First.SameDefinition(e.Second))), "A signal runs between its connection's ends.");
        Assert.AreEqual("Keep the return path short.", links.Requirements(powerSaved.Selection).Requirements.General);
        var feedSaved = links.Inspect(savedTop.LocalDiagram.Connections.Single(c => c.ConnectionId.ToString("D") == feed));
        Assert.AreEqual((DiagramConnectionKind.Abstract, DiagramDomain.Unspecified, DiagramConnectionDirection.Unspecified, 0),
            (feedSaved.Kind, feedSaved.Domain, feedSaved.Direction, feedSaved.Members.Length), "Rail feed is still only its caption.");
        var supplySaved = links.Inspect(savedTop.LocalDiagram.Connections.Single(c => c.ConnectionId.ToString("D") == supply));
        Assert.AreEqual(("Supply input", DiagramConnectionKind.DifferentialPair), (supplySaved.Name, supplySaved.Kind),
            "The drawn connection is its caption, its signals and its type.");
        CollectionAssert.AreEqual(new[] { "VIN", "RTN" }, supplySaved.Members.Select(m => links.Inspect(m).Name).ToArray());
        Assert.IsTrue(supplySaved.Members.All(m => links.Inspect(m).Kind == DiagramConnectionKind.Signal && links.Inspect(m).Endpoints.Length == 2
            && links.Inspect(m).Endpoints.Zip(supplySaved.Endpoints).All(e => e.First.SameDefinition(e.Second))), "The pair's signals run between its drawn ends.");
        Assert.AreEqual((DiagramEndpointKind.Interface, boundary.Id), (supplySaved.Endpoints[1].Kind, supplySaved.Endpoints[1].InterfaceId));
        Assert.AreEqual(3, savedTop.LocalDiagram.Connections.Length, "Signals are members, never connections of the level itself.");
        await Capture("saved");

        // Decline discards a later change and writes nothing.
        await Press("RecursiveDirectionFromFirst");
        await Wait("direction-changed", s => s.Dirty && s.ConnectionDraft.Direction == P.DiagramConnectionDirection.DcdrFromFirst);
        Key("d", alt: true);
        var declined = await Wait("declined", s => !s.Dirty && s.ConnectionDraft?.Direction == P.DiagramConnectionDirection.DcdrBidirectional);
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token));
        // On a clean draft, a signal drawn for the saved Power and removed again leaves nothing to save and no trace: Power's
        // connection draft, which the addition opened, goes with it, so the level draft is exactly the declined one.
        await Press("RecursiveSignalEntry"); Type("VREF"); Key("Return");
        var vrefAdded = await Wait("clean-signal-added", s => Signals(s).SequenceEqual(["VBUS", "GND", "VREF"]) && s.Dirty);
        Assert.IsTrue(vrefAdded.LevelDraft.ConnectionDrafts.Any(d => d.Baseline.ConnectionId == power), "Adding the signal opened Power's connection draft.");
        await Press("RecursiveSignalRemove2");
        var vrefGone = await Wait("clean-signal-removed", s => Signals(s).SequenceEqual(["VBUS", "GND"]) && !s.Dirty);
        await DrawnRemoval(vrefGone, declined.LevelDraft, savedXml, "clean-signal-removed");
        Assert.AreEqual(declined.ConnectionDraft, vrefGone.ConnectionDraft, "The inspector shows Power as saved.");
        Assert.IsFalse(Find(vrefGone, "RecursiveSave").Enabled, "Save is unavailable: nothing is left to save.");
        // A saved signal leaves through the companion's removal cascade and the status bar names it; Undo restores it.
        await Press("RecursiveSignalRemove0");
        var vbusRemoved = await Wait("saved-signal-removed", s => Signals(s).SequenceEqual(["GND"]) && s.Dirty);
        Assert.AreEqual(("VBUS", P.LevelEditEffectKind.LeekConnectionRemoved), (vbusRemoved.LastEffects.Single().Detail, vbusRemoved.LastEffects.Single().Kind));
        StringAssert.Contains(vbusRemoved.StatusText, "VBUS");
        Assert.AreEqual(power, vbusRemoved.ConnectionDraft.Baseline.ConnectionId, "Power stays selected with its other details.");
        Key("z", control: true); await Wait("saved-signal-restored", s => Signals(s).SequenceEqual(["VBUS", "GND"]) && !s.Dirty);
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token));

        // An agent refines Rail feed through MCP: it records its input, publishes a proposal giving Rail feed a direction, the power
        // domain, a VIN signal and what its first end carries, and chooses it. The editor shows those details the same way once it
        // reads the changed file.
        string savedToken = saved.SourceToken;
        var feedSelection = savedTop.LocalDiagram.Connections.Single(c => c.ConnectionId.ToString("D") == feed);
        var arguments = new Dictionary<string, object?> { ["instanceId"] = instanceId, ["repositoryRoot"] = created.RepositoryRoot,
            ["sourcePath"] = created.Path, ["documentId"] = created.DocumentId, ["expectedInstanceEpoch"] = native.Epoch };
        var input = new DiagramRefinementInput(Guid.NewGuid(), graph.DocumentId, savedToken, [graph.SelectedRoot], [feedSelection],
            "Refine Rail feed into its supply signal.", RecursiveBlockFixture.Origin("Agent console"), []);
        var recorded = await client.CallToolAsync("kicad_diagram_refinement_input_record", new Dictionary<string, object?>(arguments)
        { ["expectedSourceToken"] = savedToken, ["input"] = JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(JsonSerializerDefaults.Web)) },
            cancellationToken: token);
        if (recorded.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-details-input-error.json"), JsonSerializer.Serialize(recorded), token);
        Assert.IsFalse(recorded.IsError == true, "The agent's input is recorded.");
        string inputToken = JsonSerializer.SerializeToElement(recorded).GetProperty("structuredContent").GetProperty("sourceToken").GetString()!;
        var candidate = new BlockSelection(graph.SelectedRoot.BlockId, Guid.NewGuid(), Guid.NewGuid());
        var refined = new ConnectionSelection(feedSelection.ConnectionId, Guid.NewGuid(), Guid.NewGuid());
        var vin = new ConnectionSelection(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var proposal = new BlockProposal(Guid.NewGuid(), input.Id, [graph.SelectedRoot], candidate,
            [new ProposedBlock(candidate, graph.SelectedRoot, "Agent proposal", savedTop.Name, Guid.NewGuid(), graph.Requirements(graph.SelectedRoot).Requirements,
                savedTop.Children, savedTop.LocalDiagram with { Connections = [.. savedTop.LocalDiagram.Connections.Select(c => c == feedSelection ? refined : c)] },
                Definition: savedTop.Definition, ComponentBindings: savedTop.ComponentBindings, ForkRevisionId: Guid.NewGuid(),
                ForkRequirementRevisionId: Guid.NewGuid(), PhysicalAllocation: savedTop.PhysicalAllocation)],
            [new ProposedConnection(graph.SelectedRoot.BlockId, refined, feedSelection, "Agent proposal", feedSaved.Name, feedSaved.Kind, Guid.NewGuid(),
                links.Requirements(feedSelection).Requirements, feedSaved.Endpoints.SetItem(0, feedSaved.Endpoints[0] with { Intent = "Regulated 3.3 V" }),
                [vin], Guid.NewGuid(), Guid.NewGuid(), DiagramDomain.Power, DiagramConnectionDirection.FromFirst),
             new ProposedConnection(graph.SelectedRoot.BlockId, vin, null, "Agent proposal", "VIN", DiagramConnectionKind.Signal, Guid.NewGuid(),
                DiagramRequirements.Empty, feedSaved.Endpoints, [])],
            [], RecursiveBlockFixture.Origin("Agent console"));
        var published = await client.CallToolAsync("kicad_diagram_proposal_publish", new Dictionary<string, object?>(arguments)
        {
            ["expectedSourceToken"] = inputToken, ["operationId"] = Guid.NewGuid(),
            ["proposalJson"] = JsonSerializer.SerializeToElement(proposal, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        }, cancellationToken: token);
        if (published.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-details-proposal-error.json"), JsonSerializer.Serialize(published), token);
        Assert.IsFalse(published.IsError == true, "The agent's proposal is published.");
        string publishedToken = JsonSerializer.SerializeToElement(published).GetProperty("structuredContent").GetProperty("sourceToken").GetString()!;
        var chosen = await client.CallToolAsync("kicad_diagram_proposal_select", new Dictionary<string, object?>(arguments)
        {
            ["expectedSourceToken"] = publishedToken, ["proposalId"] = proposal.Id, ["expectedRoot"] = graph.SelectedRoot,
            ["currentPath"] = new[] { graph.SelectedRoot }, ["ancestorRevisionIds"] = Array.Empty<Guid>(), ["operationId"] = Guid.NewGuid(),
            ["actor"] = "Agent console"
        }, cancellationToken: token);
        if (chosen.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-details-select-error.json"), JsonSerializer.Serialize(chosen), token);
        Assert.IsFalse(chosen.IsError == true, "The agent chooses its proposal.");
        string agentToken = JsonSerializer.SerializeToElement(chosen).GetProperty("structuredContent").GetProperty("sourceToken").GetString()!;
        Key("r", control: true);
        await Wait("agent-reloaded", s => s.Ready && !s.Dirty && s.SourceToken == agentToken);
        await SelectConnection("agent-feed-selected", feed);
        var agent = await Wait("agent-details", s => Details(s).SequenceEqual(["signals", "direction", "domain", "endpoints"]) && Signals(s).SequenceEqual(["VIN"]));
        Assert.IsTrue(Active(agent, "RecursiveDirection").SequenceEqual(["FromFirst"]) && Active(agent, "RecursiveDomain").SequenceEqual(["Power"]),
            "The agent's direction and domain show as chosen, like the person's own.");
        CollectionAssert.AreEqual(new uint[] { 1 }, Arrows(agent, feed));
        Assert.AreEqual(psuName + " \u00b7 Rail \u2014 Regulated 3.3 V", Find(agent, "RecursiveConnectionEndpoints").Label,
            "What the agent stated about the first end is shown beside that end's block and port.");
        Assert.IsTrue(Find(agent, "RecursiveAddDetail").Shown, "Rail feed can still gain a type.");
        await Capture("agent-details");
        // A signal the person adds to Rail feed runs between the block and port each end is drawn on: what the agent stated about
        // Rail feed's first end is not copied into it.
        await Press("RecursiveSignalEntry"); Type("SENSE"); Key("Return");
        var sense = await Wait("agent-signal-added", s => Signals(s).SequenceEqual(["VIN", "SENSE"]));
        var senseDrawn = sense.LevelDraft.NewConnections.Single(c => c.HasMemberOf && c.MemberOf == feed);
        Assert.AreEqual("Regulated 3.3 V", sense.ConnectionDraft.Endpoints[0].Intent, "Rail feed's own end still says what the agent stated.");
        static bool Plain(P.DiagramEndpointBindingData end) => end.Intent == "" && end.Pin is null && end.Selector is null && end.Candidates.Count == 0
            && end.Kind == (end.HasInterfaceId ? P.DiagramEndpointKind.DekInterface : P.DiagramEndpointKind.DekUnresolved);
        static (string, string) DrawnOn(P.DiagramEndpointBindingData end) => (end.BlockId, end.HasInterfaceId ? end.InterfaceId : "");
        Assert.IsTrue(senseDrawn.Endpoints.All(Plain), "The new signal's ends carry no pin, selector, candidates or intent.");
        CollectionAssert.AreEqual(sense.ConnectionDraft.Endpoints.Select(DrawnOn).ToArray(), senseDrawn.Endpoints.Select(DrawnOn).ToArray(),
            "The new signal is drawn on its connection's blocks and ports.");
        // The agent's details are removed like the person's own: its end detail (the drawn signal's ends stay as drawn), then its
        // domain. Save keeps both signals; then the Signals row's remove button takes both saved signals away through the removal
        // cascade.
        await Press("RecursiveDetailRemoveEndpoints");
        var endsRemoved = await Wait("agent-endpoints-removed", s => Details(s).SequenceEqual(["signals", "direction", "domain"])
            && s.ConnectionDraft.Endpoints[0].Intent == "" && s.ConnectionDraft.Endpoints[0].Kind == P.DiagramEndpointKind.DekInterface && s.Dirty);
        Assert.IsTrue(endsRemoved.LevelDraft.NewConnections.Single(c => c.HasMemberOf && c.MemberOf == feed).Endpoints.All(Plain));
        await Press("RecursiveDetailRemoveDomain");
        await Wait("agent-domain-removed", s => Details(s).SequenceEqual(["signals", "direction"]) && s.ConnectionDraft.Domain == P.DiagramDomain.DdUnspecified);
        beforeSave = (await Read()).CompletedSaveCount;
        Key("s", control: true);
        var agentSaved = await Wait("agent-saved", s => s.CompletedSaveCount > beforeSave && !s.Dirty);
        Assert.AreEqual("", agentSaved.ErrorMessage);
        savedXml = await File.ReadAllTextAsync(created.Path, token);
        graph = RecursiveBlockGraphXml.Read(savedXml); savedTop = graph.Inspect(graph.SelectedRoot); links = graph.Connections(graph.SelectedRoot.BlockId);
        Assert.AreEqual(candidate.StateId, graph.SelectedRoot.StateId, "The saved level is the implementation the agent's proposal became.");
        var feedNow = links.Inspect(savedTop.LocalDiagram.Connections.Single(c => c.ConnectionId.ToString("D") == feed));
        Assert.AreEqual((DiagramConnectionDirection.FromFirst, DiagramDomain.Unspecified), (feedNow.Direction, feedNow.Domain));
        CollectionAssert.AreEqual(new[] { "VIN", "SENSE" }, feedNow.Members.Select(m => links.Inspect(m).Name).ToArray());
        var senseSaved = links.Inspect(feedNow.Members[1]);
        Assert.IsTrue(senseSaved.Endpoints.All(e => e.Intent == "" && e.Pin is null && e.Selector is null && e.Candidates.IsEmpty)
            && senseSaved.Endpoints.Zip(feedNow.Endpoints).All(e => e.First.SameDefinition(e.Second)), "The saved signal runs between Rail feed's drawn ends only.");
        Assert.IsTrue(feedNow.Endpoints[0].SameDefinition(feedSaved.Endpoints[0]), "Rail feed's first end is its drawn port again.");
        await Press("RecursiveDetailRemoveSignals");
        var signalsRemoved = await Wait("agent-signals-removed", s => Details(s).SequenceEqual(["direction"]) && s.Dirty);
        CollectionAssert.AreEquivalent(new[] { (P.LevelEditEffectKind.LeekConnectionRemoved, "VIN"), (P.LevelEditEffectKind.LeekConnectionRemoved, "SENSE") },
            signalsRemoved.LastEffects.Select(e => (e.Kind, e.Detail)).ToArray(), "Both saved signals leave through the removal cascade.");
        beforeSave = signalsRemoved.CompletedSaveCount;
        Key("s", control: true);
        agentSaved = await Wait("agent-signals-saved", s => s.CompletedSaveCount > beforeSave && !s.Dirty);
        Assert.AreEqual("", agentSaved.ErrorMessage);
        savedXml = await File.ReadAllTextAsync(created.Path, token);
        graph = RecursiveBlockGraphXml.Read(savedXml); savedTop = graph.Inspect(graph.SelectedRoot); links = graph.Connections(graph.SelectedRoot.BlockId);
        feedNow = links.Inspect(savedTop.LocalDiagram.Connections.Single(c => c.ConnectionId.ToString("D") == feed));
        Assert.AreEqual((DiagramConnectionDirection.FromFirst, DiagramDomain.Unspecified, 0), (feedNow.Direction, feedNow.Domain, feedNow.Members.Length),
            "Only the agent's direction stays.");
        Assert.AreEqual("VIN", links.Inspect(vin).Name, "The agent's revision with its signal stays in history.");
        Assert.AreEqual("Feed the CPU from the rail.", savedTop.LocalDiagram.Notes.Single(n => n.Target.TargetId?.ToString("D") == feed).Text,
            "The comment on Rail feed is untouched.");
        Assert.AreEqual(2, links.Inspect(savedTop.LocalDiagram.Connections.Single(c => c.ConnectionId.ToString("D") == power)).Members.Length);

        // A compact window keeps every detail row usable: no choice is clipped or overlaps another, and one still works.
        ulong beforeCompact = (await Read()).ViewRevision;
        NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, resizeWidth: 1100, resizeHeight: 760);
        await Wait("compact", s => s.Rendered && s.ViewRevision > beforeCompact && s.CanvasPixelWidth < 800);
        await ClickBlankCanvas("compact-level", savedTop.Selection.BlockId.ToString("D"));
        await SelectConnection("compact-power", power);
        var compact = await Wait("compact-details", s => Details(s).SequenceEqual(["signals", "direction", "domain", "type"]));
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-details-compact.json"), SchematicJson.Formatter.Format(compact), token);
        var compactInspector = Find(compact, "RecursiveInspector");
        var choices = compact.Controls.Where(c => c.Shown && (c.Name.StartsWith("RecursiveDirection", StringComparison.Ordinal)
            || c.Name.StartsWith("RecursiveDomain", StringComparison.Ordinal) || c.Name.StartsWith("RecursiveType", StringComparison.Ordinal))).ToArray();
        Assert.HasCount(12, choices, "Every choice of the four rows is shown.");
        Assert.IsTrue(choices.All(c => c.X >= compactInspector.X && c.X + c.Width <= compactInspector.X + compactInspector.Width), "No choice is clipped at the side.");
        for (int i = 0; i < choices.Length; ++i)
            for (int j = i + 1; j < choices.Length; ++j)
                Assert.IsFalse(choices[i].X < choices[j].X + choices[j].Width && choices[j].X < choices[i].X + choices[i].Width
                    && choices[i].Y < choices[j].Y + choices[j].Height && choices[j].Y < choices[i].Y + choices[i].Height, choices[i].Name + " overlaps " + choices[j].Name);
        Assert.IsTrue(Inside(Find(compact, "RecursiveConnectionCaption"), compactInspector), "The caption is in view.");
        await Press("RecursiveDirectionFromFirst");
        var compactChosen = await Wait("compact-direction", s => s.Dirty && s.ConnectionDraft.Direction == P.DiagramConnectionDirection.DcdrFromFirst);
        {
            // Design QA round 2 in the compact window, measured: the chosen direction, domain and type have the drawing tools'
            // accent style and the others do not (R2-P2-4); "Remove signals" is a link unlike each signal's own "×" (R2-P2-5); and
            // every connection shows its whole caption on the canvas, the new "Supply input" beside its short route as well
            // (R2-P2-1 and R2-P2-8).
            await ParkPointer();
            var shot = await Shot("compact");
            string[] OneClick(bool chosen) => [.. compactChosen.Controls.Where(c => c.Shown && c.Enabled && c.Active == chosen
                && (c.Name.StartsWith("RecursiveDirection", StringComparison.Ordinal) || c.Name.StartsWith("RecursiveDomain", StringComparison.Ordinal)
                    || c.Name.StartsWith("RecursiveType", StringComparison.Ordinal))).Select(c => c.Name["Recursive".Length..])];
            CollectionAssert.IsSubsetOf(new[] { "DirectionFromFirst", "DomainPower" }, OneClick(true), "details-compact: PSU → CPU and Power are chosen.");
            VerifyChoiceStyling(shot, compactChosen, "details-compact", OneClick(true), OneClick(false));
            VerifySignalRemoves(shot, compactChosen, "details-compact", "VBUS", "GND");
            var compactNames = new[] { supply, power, feed }.ToDictionary(id => id,
                id => links.Inspect(savedTop.LocalDiagram.Connections.Single(c => c.ConnectionId.ToString("D") == id)).Name);
            VerifyCaptions(shot, compactChosen, "details-compact", compactNames, [.. compactNames.Values]);
        }
        Key("z", control: true); await Wait("compact-undone", s => !s.Dirty && s.ConnectionDraft.Direction == P.DiagramConnectionDirection.DcdrBidirectional);
        NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, resizeWidth: 1536, resizeHeight: 1024);
        await Wait("expanded", s => s.Rendered && s.CanvasPixelWidth > 900);

        // Close and reopen: both connections come back from the file with the same rows.
        Key("w", control: true); await Closed();
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token), "Closing a clean window writes nothing.");
        await Open("reopened");
        await SelectConnection("reopened-power", power);
        var reopened = await Wait("reopened-details", s => Details(s).SequenceEqual(["signals", "direction", "domain", "type"])
            && Signals(s).SequenceEqual(["VBUS", "GND"]) && s.ShownRequirementFields.SequenceEqual(new[] { P.RequirementFieldKind.RfkGeneral }));
        Assert.IsTrue(Active(reopened, "RecursiveDirection").SequenceEqual(["Both"]) && Active(reopened, "RecursiveDomain").SequenceEqual(["Power"])
            && Active(reopened, "RecursiveType").SequenceEqual(["SignalGroup"]));
        Assert.AreEqual("Connection", Find(reopened, "RecursiveOwnerCaption").Label);
        await Capture("reopened");
        await SelectConnection("reopened-feed", feed);
        var feedReopened = await Wait("reopened-feed-details", s => Details(s).SequenceEqual(["direction"]));
        await SelectConnection("reopened-supply", supply);
        var supplyReopened = await Wait("reopened-supply-details", s => Details(s).SequenceEqual(["signals", "type"]) && Signals(s).SequenceEqual(["VIN", "RTN"])
            && Active(s, "RecursiveType").SequenceEqual(["DifferentialPair"]));
        Assert.IsFalse(Find(supplyReopened, "RecursiveSignalRemove0").Enabled || Find(supplyReopened, "RecursiveSignalEntry").Enabled,
            "The saved pair keeps its two signals.");
        Assert.IsFalse(feedReopened.Dirty || supplyReopened.Dirty, "Selecting connections changes nothing.");

        // An agent binds and unbinds connection ends and refines a connection's members with the agent tools (ledger
        // pf92d0ecdec8805b4), through the production MCP server while the clean editor stays open. Each call names the exact file,
        // root, level and connection it edits; a stale, ambiguous or missing target is refused and writes nothing. Once the editor
        // reloads the file, an end the agent left unresolved or bound, with what it says about that end, shows in the connection's
        // existing Endpoints row, and the refined members in its Signals row.
        var agentGraph = RecursiveBlockGraphXml.Read(savedXml); var agentTop = agentGraph.Inspect(agentGraph.SelectedRoot);
        var agentLinks = agentGraph.Connections(agentGraph.SelectedRoot.BlockId);
        ConnectionSelection AgentLink(RecursiveBlockGraph at, string id) =>
            at.Inspect(at.SelectedRoot).LocalDiagram.Connections.Single(c => c.ConnectionId.ToString("D") == id);
        var railFeed = agentLinks.Inspect(AgentLink(agentGraph, feed));
        Guid psuBlock = railFeed.Endpoints[0].BlockId, railPort = railFeed.Endpoints[0].InterfaceId!.Value, cpuBlock = railFeed.Endpoints[1].BlockId;
        Assert.AreEqual((psuName, "Rail"), (agentGraph.Inspect(agentTop.Children.Single(c => c.BlockId == psuBlock)).Name,
            agentGraph.Inspect(agentTop.Children.Single(c => c.BlockId == psuBlock)).LocalDiagram.Interfaces.Single(i => i.Id == railPort).Name),
            "Rail feed starts on the PSU's Rail port.");
        var agentEdit = new Dictionary<string, object?>(arguments) { ["expectedSourceToken"] = supplyReopened.SourceToken,
            ["expectedRoot"] = agentGraph.SelectedRoot, ["blockPath"] = new[] { agentGraph.SelectedRoot }, ["connectionPath"] = new[] { AgentLink(agentGraph, feed) },
            ["endpointIndex"] = 0, ["operationId"] = Guid.NewGuid(), ["actor"] = "Agent console" };
        static Dictionary<string, object?> With(Dictionary<string, object?> call, params (string Key, object? Value)[] values)
        {
            var next = new Dictionary<string, object?>(call);
            foreach (var (key, value) in values) next[key] = value;
            return next;
        }
        async Task<JsonElement> AgentTool(string tool, Dictionary<string, object?> call, string step, string? refusedWith = null)
        {
            var result = await client.CallToolAsync(tool, call, cancellationToken: token);
            var data = JsonSerializer.SerializeToElement(result).GetProperty("structuredContent");
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-details-" + step + ".json"), data.GetRawText(), token);
            if (refusedWith is null) Assert.IsFalse(result.IsError == true, step + ": " + data.GetRawText());
            else
            {
                Assert.IsTrue(result.IsError == true, step + " is refused: " + data.GetRawText());
                Assert.AreEqual(refusedWith, data.GetProperty("code").GetString(), step + ": " + data.GetRawText());
                Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token), step + ": a refused edit writes nothing.");
            }
            return data;
        }
        // The tool's next call names the revisions this one saved.
        static Dictionary<string, object?> After(Dictionary<string, object?> call, JsonElement saved) => With(call,
            ("expectedSourceToken", saved.GetProperty("sourceToken").GetString()), ("expectedRoot", saved.GetProperty("selectedRoot")),
            ("blockPath", saved.GetProperty("blockPath")), ("operationId", Guid.NewGuid()));
        async Task<P.RecursiveDiagramEditorState> Reloaded(string step, JsonElement saved, string connection, Func<P.RecursiveDiagramEditorState, bool> shown)
        {
            string written = saved.GetProperty("sourceToken").GetString()!;
            Key("r", control: true);
            await Wait(step + "-reloaded", s => s.Ready && !s.Dirty && s.SourceToken == written);
            await SelectConnection(step + "-selected", connection);
            var at = await Wait(step, shown);
            await Capture(step);
            return at;
        }
        // The row wraps a long line between words to the inspector's width; read back, each line break is the space it replaced.
        string EndpointsRow(P.RecursiveDiagramEditorState at) => Find(at, "RecursiveConnectionEndpoints").Label.Replace('\n', ' ');
        const string feedIntent = "Choose which PSU output feeds the CPU.";
        await AgentTool("kicad_diagram_connection_endpoint_set", With(agentEdit, ("action", "unbind"), ("expectedSourceToken", savedToken)),
            "agent-edit-stale-file", "recursive_block_file_changed");
        await AgentTool("kicad_diagram_connection_endpoint_set", With(agentEdit, ("action", "unbind"), ("expectedRoot", created.Root)),
            "agent-edit-stale-root", "stale_root_revision");
        await AgentTool("kicad_diagram_connection_endpoint_set", With(agentEdit, ("action", "bind"), ("blockId", psuBlock)),
            "agent-edit-bind-without-port", "ambiguous_connection_edit");
        await AgentTool("kicad_diagram_connection_endpoint_set", With(agentEdit, ("action", "bind"), ("blockId", cpuBlock), ("interfaceId", railPort)),
            "agent-edit-port-of-another-block", "connection_edit_target_missing");
        await AgentTool("kicad_diagram_connection_endpoint_set", With(agentEdit, ("action", "unbind"), ("endpointIndex", 2)),
            "agent-edit-missing-end", "connection_edit_target_missing");

        // Rail feed's first end is left explicitly unresolved on the PSU. The earlier binding stays in history.
        var unbound = await AgentTool("kicad_diagram_connection_endpoint_set", With(agentEdit, ("action", "unbind"), ("intent", feedIntent)), "agent-unbind");
        Assert.IsTrue(unbound.GetProperty("changed").GetBoolean());
        Assert.AreEqual(("Interface", railPort.ToString("D")), (unbound.GetProperty("previousEndpoint").GetProperty("kind").GetString(),
            unbound.GetProperty("previousEndpoint").GetProperty("interfaceId").GetString()));
        var unboundEnd = unbound.GetProperty("endpoint");
        Assert.AreEqual(("Unresolved", psuBlock.ToString("D"), JsonValueKind.Null, feedIntent), (unboundEnd.GetProperty("kind").GetString(),
            unboundEnd.GetProperty("blockId").GetString(), unboundEnd.GetProperty("interfaceId").ValueKind, unboundEnd.GetProperty("intent").GetString()));
        Assert.AreEqual(JsonValueKind.Null, unbound.GetProperty("boundaryMapping").ValueKind, "An end on a block of the level maps through no boundary port.");
        savedXml = await File.ReadAllTextAsync(created.Path, token);
        var unboundGraph = RecursiveBlockGraphXml.Read(savedXml); var unboundLinks = unboundGraph.Connections(unboundGraph.SelectedRoot.BlockId);
        var feedUnbound = unboundLinks.Inspect(AgentLink(unboundGraph, feed));
        Assert.IsTrue(feedUnbound.Endpoints[0].SameDefinition(DiagramEndpointBinding.Unknown(psuBlock, feedIntent))
            && feedUnbound.Endpoints[1].SameDefinition(railFeed.Endpoints[1]), "Only Rail feed's first end changed, and it is Unresolved on the PSU.");
        Assert.AreEqual((railFeed.Selection.RevisionId, "Agent console"), (feedUnbound.ParentRevisionId!.Value, feedUnbound.Origin.Actor));
        // The new revision is the operation's identity (review of pf92d, finding 4): after a cut-off call the agent reads the
        // connection and sees whether its operation landed. Repeating the landed operation on the file it produced writes nothing.
        Assert.AreEqual((Guid)agentEdit["operationId"]!, feedUnbound.Selection.RevisionId, "Rail feed's new revision is the unbind operation's identity.");
        var repeatedUnbind = await AgentTool("kicad_diagram_connection_endpoint_set", With(agentEdit, ("action", "unbind"), ("intent", feedIntent),
            ("expectedSourceToken", unbound.GetProperty("sourceToken").GetString()), ("expectedRoot", unbound.GetProperty("selectedRoot")),
            ("blockPath", unbound.GetProperty("blockPath")), ("connectionPath", unbound.GetProperty("connectionPath"))), "agent-unbind-repeated");
        Assert.IsFalse(repeatedUnbind.GetProperty("changed").GetBoolean(), "The repeated operation finds its end already unbound and saves nothing.");
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token), "Repeating a landed operation writes nothing.");
        Assert.AreEqual(agentLinks.Requirements(railFeed.Selection).Requirements, unboundLinks.Requirements(feedUnbound.Selection).Requirements,
            "Rail feed's requirement fields are unchanged.");
        Assert.AreEqual(unbound.GetProperty("connectionPath")[0].GetProperty("revisionId").GetString(), feedUnbound.Selection.RevisionId.ToString("D"));
        await Reloaded("agent-unbound", unbound, feed, s => Details(s).SequenceEqual(["direction", "endpoints"])
            && EndpointsRow(s) == psuName + " \u2014 " + feedIntent);

        // Bound to the PSU's Rail port again, with what it carries: the Endpoints row names the block and the port.
        const string railIntent = "Regulated supply from the Rail output.";
        var bound = await AgentTool("kicad_diagram_connection_endpoint_set", With(After(agentEdit, unbound), ("connectionPath", unbound.GetProperty("connectionPath")),
            ("action", "bind"), ("blockId", psuBlock), ("interfaceId", railPort), ("intent", railIntent)), "agent-bind");
        var boundEnd = bound.GetProperty("endpoint");
        Assert.AreEqual(("Interface", railPort.ToString("D"), railIntent), (boundEnd.GetProperty("kind").GetString(),
            boundEnd.GetProperty("interfaceId").GetString(), boundEnd.GetProperty("intent").GetString()));
        savedXml = await File.ReadAllTextAsync(created.Path, token);
        await Reloaded("agent-bound", bound, feed, s => Details(s).SequenceEqual(["direction", "endpoints"])
            && EndpointsRow(s) == psuName + " \u00b7 Rail \u2014 " + railIntent);
        var sameAgain = await AgentTool("kicad_diagram_connection_endpoint_set", With(After(agentEdit, bound), ("connectionPath", bound.GetProperty("connectionPath")),
            ("action", "bind"), ("blockId", psuBlock), ("interfaceId", railPort)), "agent-bind-again");
        Assert.IsFalse(sameAgain.GetProperty("changed").GetBoolean(), "Binding the end the way it already is writes nothing.");
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token));

        // Supply input's end on the level's own boundary port is bound with what enters there; the tool reports the port and, on
        // this root level, no level above it that uses the port.
        const string entryIntent = "The system's supply enters here.";
        var boundGraph = RecursiveBlockGraphXml.Read(savedXml);
        var entry = await AgentTool("kicad_diagram_connection_endpoint_set", With(After(agentEdit, bound), ("connectionPath", new[] { AgentLink(boundGraph, supply) }),
            ("endpointIndex", 1), ("action", "bind"), ("blockId", boundGraph.SelectedRoot.BlockId), ("interfaceId", boundary.Id), ("intent", entryIntent)),
            "agent-bind-boundary-port");
        var entryMapping = entry.GetProperty("boundaryMapping");
        Assert.AreEqual((boundary.Id.ToString("D"), boundary.Name, JsonValueKind.Null, 0), (entryMapping.GetProperty("interfaceId").GetString(),
            entryMapping.GetProperty("interfaceName").GetString(), entryMapping.GetProperty("parentLevel").ValueKind,
            entryMapping.GetProperty("parentConnections").GetArrayLength()));
        savedXml = await File.ReadAllTextAsync(created.Path, token);
        await Reloaded("agent-boundary-port", entry, supply, s => Details(s).SequenceEqual(["signals", "type", "endpoints"])
            && EndpointsRow(s) == boundary.Name + " \u2014 " + entryIntent);

        // Power's two signals become a USB power group, beside a new USB data differential pair of two new signals. A refinement that
        // would leave GND out is refused first.
        var enums = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var entryGraph = RecursiveBlockGraphXml.Read(savedXml); var entryLinks = entryGraph.Connections(entryGraph.SelectedRoot.BlockId);
        var powerBefore = entryLinks.Inspect(AgentLink(entryGraph, power));
        Guid vbus = powerBefore.Members[0].ConnectionId, gnd = powerBefore.Members[1].ConnectionId;
        Guid usbPower = Guid.NewGuid(), usbData = Guid.NewGuid(), dPlus = Guid.NewGuid(), dMinus = Guid.NewGuid();
        var refine = With(After(agentEdit, entry), ("connectionPath", new[] { AgentLink(entryGraph, power) }),
            ("sources", new[] { new SourceReference("usb-interface-notes", "draft-1", 1, null, null) }));
        refine.Remove("endpointIndex");
        await AgentTool("kicad_diagram_connection_members_refine", With(refine, ("memberIds", new[] { usbPower }),
            ("newMembers", JsonSerializer.SerializeToElement(new[] { new ConnectionMemberDefinition(usbPower, "USB power", [vbus], DiagramConnectionKind.SignalGroup) }, enums))),
            "agent-refine-leaving-a-signal-out", "ambiguous_connection_edit");
        var refinedMembers = await AgentTool("kicad_diagram_connection_members_refine", With(refine, ("memberIds", new[] { usbPower, usbData }),
            ("newMembers", JsonSerializer.SerializeToElement(new[]
            {
                new ConnectionMemberDefinition(usbPower, "USB power", [vbus, gnd], DiagramConnectionKind.SignalGroup, "Carry the USB supply and its return."),
                new ConnectionMemberDefinition(usbData, "USB data", [dPlus, dMinus], DiagramConnectionKind.DifferentialPair, "", "", "Route D+ and D- as one matched pair."),
                new ConnectionMemberDefinition(dPlus, "D+", []), new ConnectionMemberDefinition(dMinus, "D-", [])
            }, enums))), "agent-refine");
        Assert.IsTrue(refinedMembers.GetProperty("changed").GetBoolean());
        CollectionAssert.AreEqual(new[] { ("USB power", true), ("VBUS", false), ("GND", false), ("USB data", true), ("D+", true), ("D-", true) },
            refinedMembers.GetProperty("members").EnumerateArray().Select(m => (m.GetProperty("revision").GetProperty("name").GetString(),
                m.GetProperty("created").GetBoolean())).ToArray(), "The tool returns Power's members below it, marking the new ones.");
        savedXml = await File.ReadAllTextAsync(created.Path, token);
        var refinedGraph = RecursiveBlockGraphXml.Read(savedXml); var refinedLinks = refinedGraph.Connections(refinedGraph.SelectedRoot.BlockId);
        var powerRefined = refinedLinks.Inspect(AgentLink(refinedGraph, power));
        CollectionAssert.AreEqual(new[] { usbPower, usbData }, powerRefined.Members.Select(m => m.ConnectionId).ToArray());
        CollectionAssert.AreEqual(powerBefore.Members.ToArray(), refinedLinks.Inspect(powerRefined.Members[0]).Members.ToArray(),
            "VBUS and GND keep their exact saved revisions inside the USB power group.");
        foreach (var kept in powerBefore.Members)
            Assert.AreEqual(entryLinks.Requirements(kept), refinedLinks.Requirements(kept), "A kept signal keeps its requirement history.");
        Assert.AreEqual(new DiagramRequirements("", "", "Route D+ and D- as one matched pair."), refinedLinks.Requirements(powerRefined.Members[1]).Requirements);
        Assert.AreEqual("usb-interface-notes", refinedLinks.Inspect(powerRefined.Members[1]).Origin.Sources.Single().DocumentId,
            "The new member records the agent's source.");
        Assert.AreEqual(entryLinks.Requirements(powerBefore.Selection).Requirements, refinedLinks.Requirements(powerRefined.Selection).Requirements,
            "Power's own requirement fields are unchanged.");
        await Reloaded("agent-refined-members", refinedMembers, power, s => Signals(s).SequenceEqual(["USB power", "USB data"])
            && Details(s).SequenceEqual(["signals", "direction", "domain", "type"]));
        Key("w", control: true); await Closed();
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token));
    }

    /// <summary>The row the editor's field-history list shows for an entry saved before the viewed implementation's saved
    /// text (never the saved row): the name of the implementation it was saved in when that is not the viewed one, in the
    /// implementation selector's "name · version" form, then its author.</summary>
    private static string HistoryRowLabel(Func<Guid, (Guid State, string Name)> implementationOf, DiagramFieldHistoryEntry entry, Guid viewedState)
    {
        var (state, implementation) = implementationOf(entry.ContextRevisionId);
        return (state == viewedState ? "" : implementation + " · ") + "v" + entry.ContextVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " · " + entry.Origin.Actor;
    }

    /// <summary>The implementation (its identity and name) a block revision was saved in.</summary>
    private static Func<Guid, (Guid State, string Name)> Contexts(RecursiveBlockGraph graph) => revision =>
    {
        Guid state = graph.Revisions.Single(r => r.Selection.RevisionId == revision).Selection.StateId;
        return (state, graph.States.Single(s => s.Id == state).Name);
    };

    /// <summary>The implementation (its identity and name) a connection or member revision was saved in.</summary>
    private static Func<Guid, (Guid State, string Name)> Contexts(DiagramConnectionArchive archive) => revision =>
    {
        Guid state = archive.Revisions.Single(r => r.Selection.RevisionId == revision).Selection.StateId;
        return (state, archive.States.Single(s => s.Id == state).Name);
    };

    /// <summary>Ledger pa48933d0fe0a5c2f through production MCP servers sharing one state directory and the live instance: an agent
    /// records a prompt with a preserved attachment for the CPU level, reads its context and publishes a proposal, but that call
    /// is cancelled in flight and its server ends. A new server reattaches the saved instance; the context is unchanged, the
    /// operation's receipt is inspected (and resumed if it was interrupted), and repeating the same operation returns the one
    /// recorded candidate. Choosing it is cancelled the same way, and after another reattachment the repeated choice returns its
    /// recorded outcome. Exactly one input, one candidate and one new root revision exist at the end.</summary>
    private static async Task VerifyAgentReattachment(NativeClient native, string stateRoot, string source, string project, Guid documentId,
        Guid cpuBlockId, string instanceId, string evidence, CancellationToken token)
    {
        var arguments = new Dictionary<string, object?> { ["instanceId"] = instanceId, ["repositoryRoot"] = project,
            ["sourcePath"] = source, ["documentId"] = documentId.ToString("D") };
        var record = new List<object>();
        static JsonElement Data(ModelContextProtocol.Protocol.CallToolResult result) => JsonSerializer.SerializeToElement(result).GetProperty("structuredContent");
        async Task<JsonElement> Call(McpClient agent, string tool, Dictionary<string, object?> request, string label)
        {
            var result = await agent.CallToolAsync(tool, request, cancellationToken: token);
            record.Add(new { label, tool, result = Data(result) });
            Assert.IsFalse(result.IsError == true, label + ": " + Data(result).GetRawText());
            return Data(result);
        }
        // A call the agent abandons while the server is carrying it out: the agent cancels as soon as the operation's first
        // recorded phase appears in the server's state directory (the server has started it and has not answered yet), not after
        // a fixed delay that a fast server could beat. The server is told to cancel it, and the answer, if any, is never used.
        async Task<string> Cancelled(McpClient agent, string tool, Dictionary<string, object?> request, Guid operation, string label)
        {
            string started = Path.Combine(stateRoot, "block-proposal-operations", operation.ToString("N") + ".json");
            using var abandon = CancellationTokenSource.CreateLinkedTokenSource(token);
            var call = agent.CallToolAsync(tool, request, cancellationToken: abandon.Token).AsTask();
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                waiting.CancelAfter(TimeSpan.FromSeconds(30));
                while (!File.Exists(started) && !call.IsCompleted) await Task.Delay(1, waiting.Token);
            }
            abandon.Cancel();
            string outcome;
            try { var result = await call; outcome = result.IsError == true ? "refused: " + Data(result).GetRawText() : "answered"; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { outcome = "cancelled"; }
            record.Add(new { label, tool, outcome });
            Assert.AreEqual("cancelled", outcome, label + ": the agent must abandon the call before its answer arrives, or this step proves no cancellation.");
            return outcome;
        }
        // Which of the three server-side outcomes of a cancelled call ran is recorded as evidence (serverBranch): the operation had not
        // started (no receipt), was interrupted between its recorded phases (resumed from its receipt) or completed before the cancel
        // arrived (published receipt). Process death at each phase is proved by BlockProposalInterruptionTests and
        // BlockProposalSelectionInterruptionTests; this journey proves the repeat over the production server for the branch that ran.
        async Task<JsonElement> Receipt(McpClient agent, Guid operation, string label, bool afterCancel)
        {
            var inspected = await agent.CallToolAsync("kicad_diagram_proposal_publication", new Dictionary<string, object?>
                { ["instanceId"] = instanceId, ["expectedInstanceEpoch"] = native.Epoch, ["operationId"] = operation }, cancellationToken: token);
            record.Add(new { label, tool = "kicad_diagram_proposal_publication", result = Data(inspected) });
            if (inspected.IsError == true)
            {
                Assert.AreEqual("missing_block_proposal_receipt", Data(inspected).GetProperty("code").GetString(), label);
                if (afterCancel) record.Add(new { label, serverBranch = "not started: no receipt" });
                return default;
            }
            var receipt = Data(inspected).GetProperty("receipt");
            string stage = receipt.GetProperty("stage").GetString()!;
            if (stage == "Published")
            {
                // Which branch ran is recorded only for a receipt read after a cancelled call.
                if (afterCancel) record.Add(new { label, serverBranch = "completed before the cancel arrived: published receipt" });
                return receipt;
            }
            // An operation interrupted between its recorded phases is completed from its receipt, never repeated as a new write.
            var resumed = await Call(agent, "kicad_diagram_proposal_publication_resume", new Dictionary<string, object?>(arguments)
                { ["expectedInstanceEpoch"] = native.Epoch, ["operationId"] = operation }, label + " resume");
            Assert.AreNotEqual("NeedsReview", resumed.GetProperty("recovery").GetProperty("disposition").GetString(), label);
            if (afterCancel) record.Add(new { label, serverBranch = "interrupted at " + stage + ": resumed from its receipt" });
            return Data(await agent.CallToolAsync("kicad_diagram_proposal_publication", new Dictionary<string, object?>
                { ["instanceId"] = instanceId, ["expectedInstanceEpoch"] = native.Epoch, ["operationId"] = operation }, cancellationToken: token)).GetProperty("receipt");
        }
        async Task<McpClient> Server()
        {
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet", Arguments = [Path.Combine(FindRoot(), "automation", "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll")],
                EnvironmentVariables = new Dictionary<string, string?> { ["KICAD_AUTOMATION_STATE_DIRECTORY"] = stateRoot }
            }), cancellationToken: token);
        }
        async Task Reattach(McpClient agent, string label)
        {
            var reattached = await Call(agent, "kicad_instance_reattach", new Dictionary<string, object?> { ["instanceId"] = instanceId }, label);
            Assert.AreEqual(instanceId, reattached.GetProperty("instanceId").GetString(), label + ": the saved instance.");
        }
        var before = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token)); string beforeToken = await FileToken(source, token);
        var cpu = before.Inspect(before.SelectedRoot).Children.Single(c => c.BlockId == cpuBlockId);
        byte[] notes = System.Text.Encoding.UTF8.GetBytes("Hand notes: keep the CPU power entry on the left edge.\n");
        await File.WriteAllBytesAsync(Path.Combine(project, "cpu-hand-notes.md"), notes, token);
        Guid publishOperation = Guid.NewGuid(), selectOperation = Guid.NewGuid(), chosenRootRevision = Guid.NewGuid();
        DiagramRefinementInput input; BlockProposal proposal; string contextSha, inputToken; Dictionary<string, object?> publish, select; int publishedRevisions;
        await using (var agent = await Server())
        {
            var attached = await Call(agent, "kicad_instance_attach", new Dictionary<string, object?> { ["endpoint"] = native.Endpoint, ["expectedInstanceId"] = instanceId }, "agent attach");
            Assert.AreEqual(instanceId, attached.GetProperty("instanceId").GetString());
            var asset = await Call(agent, "kicad_diagram_refinement_asset_capture", new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = beforeToken, ["attachmentPath"] = "cpu-hand-notes.md",
                ["archiveDirectory"] = "assets/refinement", ["attachmentId"] = Guid.NewGuid(),
                ["expectedSha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(notes)), ["expectedByteCount"] = notes.LongLength,
                ["mediaType"] = "text/markdown"
            }, "attachment");
            input = new DiagramRefinementInput(Guid.NewGuid(), documentId, beforeToken, [before.SelectedRoot, cpu], [],
                "Split the CPU supply into core and I/O rails; values are still unknown.", RecursiveBlockFixture.Origin(),
                [asset.GetProperty("attachment").Deserialize<DiagramRefinementAttachment>(AgentJson)!]);
            var recorded = await Call(agent, "kicad_diagram_refinement_input_record", new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = beforeToken,
                ["input"] = JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            }, "record input");
            Assert.IsTrue(recorded.GetProperty("added").GetBoolean());
            inputToken = recorded.GetProperty("sourceToken").GetString()!;
            var withInput = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var context = await AgentContextOverMcp(agent, arguments, inputToken, token, inputId: input.Id);
            AssertAgentContext(context, withInput, input.BlockPath, input, current: true, "reattachment input");
            contextSha = context.GetProperty("contextSha256").GetString()!;
            proposal = RecursiveBlockProposalTests.CreateFor(withInput, input);
            proposal = proposal with { Blocks = proposal.Blocks.SetItem(0, proposal.Blocks[0] with { ImplementationName = "Reattached agent proposal" }) };
            publish = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = inputToken, ["operationId"] = publishOperation,
                ["proposalJson"] = JsonSerializer.SerializeToElement(proposal, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
            await Cancelled(agent, "kicad_diagram_proposal_publish", publish, publishOperation, "cancelled publication");
        }
        string afterPublishToken;
        await using (var agent = await Server())
        {
            await Reattach(agent, "reattach after cancelled publication");
            var context = await AgentContextOverMcp(agent, arguments, await FileToken(source, token), token, inputId: input.Id);
            Assert.AreEqual(contextSha, context.GetProperty("contextSha256").GetString(), "The reattached agent reads the same context.");
            Assert.AreEqual(native.Epoch, context.GetProperty("instanceEpoch").GetString(), "The reattached server speaks to the same live process.");
            var receipt = await Receipt(agent, publishOperation, "publication receipt after reattachment", afterCancel: true);
            bool completed = receipt.ValueKind == JsonValueKind.Object;
            var repeated = await Call(agent, "kicad_diagram_proposal_publish", publish, "repeated publication");
            Assert.AreEqual(!completed, repeated.GetProperty("added").GetBoolean(), "A publication the receipt records is returned, not repeated.");
            Assert.AreEqual(proposal.Id, repeated.GetProperty("proposal").GetProperty("id").GetGuid());
            Assert.AreEqual("Published", repeated.GetProperty("publication").GetProperty("stage").GetString());
            afterPublishToken = await FileToken(source, token);
            if (completed) Assert.AreEqual(receipt.GetProperty("afterSha256").GetString(), afterPublishToken);
            var again = await Call(agent, "kicad_diagram_proposal_publish", publish, "publication repeated again");
            Assert.IsFalse(again.GetProperty("added").GetBoolean());
            Assert.AreEqual(afterPublishToken, await FileToken(source, token), "Repeating the operation writes nothing.");
            var reRecorded = await Call(agent, "kicad_diagram_refinement_input_record", new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = afterPublishToken,
                ["input"] = JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            }, "input recorded again");
            Assert.IsFalse(reRecorded.GetProperty("added").GetBoolean(), "Repeating the input records nothing new.");
            var published = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual(1, published.RefinementInputs.Count(i => i.Id == input.Id));
            Assert.AreEqual(1, published.Proposals.Count(p => p.Id == proposal.Id));
            Assert.AreEqual(1, published.States.Count(s => s.Id == proposal.Candidate.StateId));
            Assert.HasCount(before.States.Length + proposal.Blocks.Length, published.States, "One implementation per proposed block, once.");
            Assert.HasCount(before.Revisions.Length + proposal.Blocks.Length + proposal.Blocks.Count(b => b.BasedOn is not null), published.Revisions,
                "One revision per proposed block and one copy of the refined block's base, once.");
            Assert.AreEqual(before.SelectedRoot, published.SelectedRoot, "Publishing does not choose.");
            publishedRevisions = published.Revisions.Length;
            select = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = afterPublishToken, ["proposalId"] = proposal.Id,
                ["expectedRoot"] = published.SelectedRoot, ["currentPath"] = new[] { published.SelectedRoot, cpu },
                ["ancestorRevisionIds"] = new[] { chosenRootRevision }, ["operationId"] = selectOperation, ["actor"] = "Reattaching agent fixture"
            };
            await Cancelled(agent, "kicad_diagram_proposal_select", select, selectOperation, "cancelled choice");
        }
        await using (var agent = await Server())
        {
            await Reattach(agent, "reattach after cancelled choice");
            var receipt = await Receipt(agent, selectOperation, "choice receipt after reattachment", afterCancel: true);
            bool completed = receipt.ValueKind == JsonValueKind.Object;
            var repeated = await Call(agent, "kicad_diagram_proposal_select", select, "repeated choice");
            Assert.AreEqual(completed, repeated.GetProperty("recorded").GetBoolean(), "A choice the receipt records is returned, not made again.");
            string chosenToken = await FileToken(source, token);
            Assert.AreEqual(chosenToken, repeated.GetProperty("sourceToken").GetString());
            var again = await Call(agent, "kicad_diagram_proposal_select", select, "choice repeated again");
            Assert.IsTrue(again.GetProperty("recorded").GetBoolean(), "The repeated choice returns its recorded outcome.");
            Assert.IsTrue(again.GetProperty("stillCurrent").GetBoolean());
            Assert.AreEqual(chosenToken, again.GetProperty("sourceToken").GetString());
            Assert.AreEqual(chosenToken, await FileToken(source, token), "Repeating the choice writes nothing.");
            var chosen = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var root = new BlockSelection(before.SelectedRoot.BlockId, before.SelectedRoot.StateId, chosenRootRevision);
            Assert.AreEqual(root, chosen.SelectedRoot);
            Assert.AreEqual(root, again.GetProperty("selectedRoot").Deserialize<BlockSelection>(AgentJson));
            Assert.AreEqual(proposal.Candidate, chosen.Inspect(root).Children.Single(c => c.BlockId == cpuBlockId));
            Assert.HasCount(publishedRevisions + 1, chosen.Revisions, "Choosing adds exactly one root revision, once.");
            Assert.AreEqual(before.SelectedRoot.RevisionId, chosen.Inspect(root).ParentRevisionId);
            Assert.AreEqual(1, chosen.Proposals.Count(p => p.Id == proposal.Id)); Assert.AreEqual(1, chosen.RefinementInputs.Count(i => i.Id == input.Id));
            var receiptAfter = await Receipt(agent, selectOperation, "choice receipt at the end", afterCancel: false);
            Assert.AreEqual(chosenToken, receiptAfter.GetProperty("afterSha256").GetString());
            // The input's context is still the same after the choice; its level is now the replaced implementation.
            var context = await AgentContextOverMcp(agent, arguments, chosenToken, token, inputId: input.Id);
            Assert.AreEqual(contextSha, context.GetProperty("contextSha256").GetString());
            Assert.IsFalse(context.GetProperty("current").GetBoolean());
            var compared = await Call(agent, "kicad_diagram_proposal_compare", new Dictionary<string, object?>(arguments)
                { ["expectedSourceToken"] = chosenToken, ["proposalId"] = proposal.Id }, "comparison after the choice");
            var afterChoice = compared.GetProperty("comparison");
            Assert.IsTrue(afterChoice.GetProperty("candidateSelected").GetBoolean()); Assert.IsTrue(afterChoice.GetProperty("candidateAdopted").GetBoolean());
            Assert.IsFalse(afterChoice.GetProperty("stale").GetBoolean());
            Assert.AreEqual(0, afterChoice.GetProperty("currentChanges").GetArrayLength(), "The chosen proposal's changes are not today's changes.");
            Assert.AreEqual(0, afterChoice.GetProperty("changedOnBothSides").GetArrayLength());
            Assert.AreNotEqual(0, afterChoice.GetProperty("proposalChanges").GetArrayLength());
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-agent-reattachment.json"), JsonSerializer.Serialize(record, AgentJson), token);
    }

    private static readonly JsonSerializerOptions AgentJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static async Task<string> FileToken(string path, CancellationToken token) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, token)));

    /// <summary>An agent's context read through the production MCP server (<c>kicad_diagram_agent_context</c>, ledger
    /// pa48933d0fe0a5c2f), by original input, by root-to-level path, or both; with <paramref name="expectError"/> the refusal code.</summary>
    private static async Task<JsonElement> AgentContextOverMcp(McpClient client, IReadOnlyDictionary<string, object?> arguments, string sourceToken,
        CancellationToken token, Guid? inputId = null, BlockSelection[]? blockPath = null, string? expectError = null)
    {
        var request = new Dictionary<string, object?>(arguments) { ["expectedSourceToken"] = sourceToken };
        if (inputId is { } id) request["inputId"] = id;
        if (blockPath is not null) request["blockPath"] = blockPath;
        var result = await client.CallToolAsync("kicad_diagram_agent_context", request, cancellationToken: token);
        var data = JsonSerializer.SerializeToElement(result).GetProperty("structuredContent");
        if (expectError is null) Assert.IsFalse(result.IsError == true, data.GetRawText());
        else
        {
            Assert.IsTrue(result.IsError == true, "The context request must be refused with " + expectError + ": " + data.GetRawText());
            Assert.AreEqual(expectError, data.GetProperty("code").GetString(), data.GetRawText());
        }
        return data;
    }

    /// <summary>The context names exactly the saved level: the path by exact revision, the level's block and direct children with
    /// their three fields and ports, every connection and member with its fields, every comment of the level as element or
    /// free-space comment, the input's prompt and attachments, and today's position of the level. Expectations are read from the
    /// saved file through the model.</summary>
    private static void AssertAgentContext(JsonElement data, RecursiveBlockGraph graph, IReadOnlyList<BlockSelection> path,
        DiagramRefinementInput? input, bool current, string step)
    {
        var context = data.GetProperty("context");
        Assert.AreEqual(1, context.GetProperty("version").GetInt32(), step);
        Assert.AreEqual(graph.DocumentId, context.GetProperty("documentId").GetGuid(), step);
        Assert.AreEqual(current, data.GetProperty("current").GetBoolean(), step + ": whether the level is on today's design.");
        Assert.AreEqual(graph.SelectedRoot, data.GetProperty("selectedRoot").Deserialize<BlockSelection>(AgentJson), step);
        CollectionAssert.AreEqual(BlockProposalCompiler.FindPath(graph, path[^1].BlockId).ToArray(),
            data.GetProperty("currentPath").Deserialize<BlockSelection[]>(AgentJson), step + ": today's path to the level.");
        StringAssert.Matches(data.GetProperty("contextSha256").GetString(), new System.Text.RegularExpressions.Regex("^[0-9a-f]{64}$"), step);
        var levels = context.GetProperty("path").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(path.ToArray(), levels.Select(l => l.GetProperty("selection").Deserialize<BlockSelection>(AgentJson)).ToArray(), step + ": the exact path.");
        foreach (var (level, selection) in levels.Zip(path)) Assert.AreEqual(graph.Inspect(selection).Name, level.GetProperty("name").GetString(), step);
        // Today's implementation names are beside the context, where a rename can change them without changing the context.
        var implementations = data.GetProperty("implementations").EnumerateArray().ToDictionary(i => i.GetProperty("stateId").GetGuid(),
            i => (Owner: i.GetProperty("ownerId").GetGuid(), Name: i.GetProperty("name").GetString()!, Archived: i.GetProperty("archived").GetBoolean()));
        foreach (var selection in path.Concat(graph.Inspect(path[^1]).Children))
        {
            var state = graph.States.Single(s => s.Id == selection.StateId);
            Assert.AreEqual((state.BlockId, state.Name, state.Archived), implementations[selection.StateId], step + ": today's implementation name.");
        }
        void Block(JsonElement block, BlockSelection selection)
        {
            var revision = graph.Inspect(selection); var fields = graph.Requirements(selection);
            Assert.AreEqual(selection, block.GetProperty("selection").Deserialize<BlockSelection>(AgentJson), step);
            Assert.IsFalse(block.TryGetProperty("implementation", out _), step + ": a renameable name is not part of the context.");
            Assert.AreEqual(revision.Name, block.GetProperty("name").GetString(), step);
            Assert.AreEqual(fields.RevisionId, block.GetProperty("requirementRevisionId").GetGuid(), step + ": the fields' exact requirement revision.");
            Assert.AreEqual(fields.Requirements, block.GetProperty("requirements").Deserialize<DiagramRequirements>(AgentJson), step + ": General, Schematic and Routing.");
            CollectionAssert.AreEqual(revision.LocalDiagram.Interfaces.Select(i => (i.Id, i.Name)).ToArray(),
                block.GetProperty("interfaces").EnumerateArray().Select(i => (i.GetProperty("id").GetGuid(), i.GetProperty("name").GetString()!)).ToArray(), step + ": boundary ports.");
            Assert.AreEqual(revision.Children.Length, block.GetProperty("childCount").GetInt32(), step);
            Assert.AreEqual(revision.LocalDiagram.Connections.Length, block.GetProperty("connectionCount").GetInt32(), step);
            Assert.IsTrue(revision.EffectiveComponentBindings.SameContents(block.GetProperty("componentBindings").Deserialize<BlockComponentBindings>(AgentJson)!), step);
        }
        var scope = graph.Inspect(path[^1]);
        Block(context.GetProperty("block"), path[^1]);
        var children = context.GetProperty("children").EnumerateArray().ToArray();
        Assert.HasCount(scope.Children.Length, children, step + ": the direct children.");
        foreach (var (child, selection) in children.Zip(scope.Children)) Block(child, selection);
        var links = context.GetProperty("connections").EnumerateArray().ToArray();
        if (scope.LocalDiagram.Connections.IsEmpty) Assert.IsEmpty(links, step);
        else
        {
            var archive = graph.Connections(path[^1].BlockId);
            var walked = archive.Walk(scope.LocalDiagram.Connections);
            CollectionAssert.AreEqual(walked.ToArray(), links.Select(l => l.GetProperty("selection").Deserialize<ConnectionSelection>(AgentJson)).ToArray(),
                step + ": every connection and member by exact revision.");
            var parents = walked.SelectMany(w => archive.Inspect(w).Members.Select(m => (m.ConnectionId, Parent: w.ConnectionId))).ToDictionary(p => p.ConnectionId, p => p.Parent);
            foreach (var (link, selection) in links.Zip(walked))
            {
                var fields = archive.Requirements(selection);
                Assert.AreEqual(fields.RevisionId, link.GetProperty("requirementRevisionId").GetGuid(), step);
                Assert.AreEqual(fields.Requirements, link.GetProperty("requirements").Deserialize<DiagramRequirements>(AgentJson), step + ": a connection's three fields.");
                Assert.AreEqual(archive.Inspect(selection).Name, link.GetProperty("name").GetString(), step);
                var linkState = archive.States.Single(s => s.Id == selection.StateId);
                Assert.AreEqual((linkState.ConnectionId, linkState.Name, false), implementations[selection.StateId], step);
                Assert.AreEqual(parents.TryGetValue(selection.ConnectionId, out var parent) ? parent : null,
                    link.TryGetProperty("parentConnectionId", out var named) ? named.GetGuid() : (Guid?)null, step + ": the grouping connection.");
            }
        }
        var comments = context.GetProperty("comments").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(scope.LocalDiagram.Notes.Select(n => (n.Id, n.Text, n.Target.Kind == DiagramAnnotationTargetKind.Canvas ? "FreeSpace" : "Element")).ToArray(),
            comments.Select(c => (c.GetProperty("comment").GetProperty("id").GetGuid(), c.GetProperty("comment").GetProperty("text").GetString()!,
                c.GetProperty("placement").GetString()!)).ToArray(), step + ": every comment of the level, on an element or in free space.");
        if (input is null) Assert.IsFalse(context.TryGetProperty("input", out _), step + ": no input was named.");
        else
        {
            var original = context.GetProperty("input");
            Assert.AreEqual(input.Id, original.GetProperty("id").GetGuid(), step);
            Assert.AreEqual(input.Prompt, original.GetProperty("prompt").GetString(), step + ": the prompt exactly as given.");
            Assert.AreEqual(input.SourceSha256, original.GetProperty("sourceSha256").GetString(), step);
            CollectionAssert.AreEqual(input.BlockPath.ToArray(), original.GetProperty("blockPath").Deserialize<BlockSelection[]>(AgentJson), step);
            var attachments = original.GetProperty("attachments").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(input.Attachments.Select(a => (a.Id, a.AssetPath, a.ContentSha256, a.ByteCount, a.MediaType, a.OriginalName)).ToArray(),
                attachments.Select(a => (a.GetProperty("id").GetGuid(), a.GetProperty("assetPath").GetString()!, a.GetProperty("contentSha256").GetString()!,
                    a.GetProperty("byteCount").GetInt64(), a.GetProperty("mediaType").GetString()!, a.GetProperty("originalName").GetString()!)).ToArray(),
                step + ": attachment references.");
            var assets = data.GetProperty("assets").EnumerateArray().ToArray();
            Assert.HasCount(input.Attachments.Length, assets, step);
            Assert.IsTrue(assets.All(a => a.GetProperty("status").GetString() == "Available"), step + ": every preserved attachment is intact: " + data.GetProperty("assets").GetRawText());
            CollectionAssert.AreEqual(path.Count == input.BlockPath.Length ? input.ConnectionPath.ToArray() : [],
                context.GetProperty("focus").Deserialize<ConnectionSelection[]>(AgentJson), step + ": the input's focus.");
        }
    }

    private static string ChangeKey(IEnumerable<Guid> level, IEnumerable<Guid> links, DiagramHistoryChangeCategory category, Guid objectId,
        DiagramRequirementField? field, string? aspect) => string.Join("/", level.Select(g => g.ToString("D"))) + "|"
        + string.Join("/", links.Select(g => g.ToString("D"))) + "|" + category + "|" + objectId.ToString("D") + "|" + field + "|" + aspect;

    private static string ChangeKey(JsonElement change) => string.Join("/", change.GetProperty("levelPath").EnumerateArray().Select(e => e.GetString())) + "|"
        + string.Join("/", change.GetProperty("connectionPath").EnumerateArray().Select(e => e.GetString())) + "|" + change.GetProperty("category").GetString() + "|"
        + change.GetProperty("objectId").GetString() + "|" + (change.TryGetProperty("field", out var field) ? field.GetString() : "") + "|"
        + (change.TryGetProperty("aspect", out var aspect) ? aspect.GetString() : "");

    /// <summary>One level's history changes as the comparison names them: a definition facet and a list's order are aspects.</summary>
    private static (string Key, string Kind) LevelChange(IEnumerable<Guid> level, DiagramHistoryChange change) => (ChangeKey(level, [], change.Category, change.ObjectId,
        change.Field, change.Kind == DiagramHistoryChangeKind.Reordered ? (change.Category == DiagramHistoryChangeCategory.Block ? "Children" : change.Category + "s")
            : change.Category == DiagramHistoryChangeCategory.Definition ? change.Name : null), change.Kind.ToString());

    /// <summary>A refusal from today's file: its token, the comparison, and one detail per change on each side and per element changed
    /// on both, in the comparison's order.</summary>
    private static void AssertRefusalComparison(JsonElement refusal, string label, string todayToken)
    {
        Assert.AreEqual(todayToken, refusal.GetProperty("sourceToken").GetString(), label + ": the comparison is made against today's file.");
        Assert.AreEqual(JsonValueKind.Null, refusal.GetProperty("comparisonUnavailable").ValueKind, label + ": " + refusal.GetRawText());
        var comparison = refusal.GetProperty("comparison");
        var current = comparison.GetProperty("currentChanges").EnumerateArray().ToArray();
        var proposed = comparison.GetProperty("proposalChanges").EnumerateArray().ToArray();
        var both = comparison.GetProperty("changedOnBothSides").EnumerateArray().ToArray();
        var details = refusal.GetProperty("details").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(current.Select(c => ("current_change", c.GetProperty("objectId").GetString(), c.GetProperty("levelPath").EnumerateArray().Last().GetString()))
                .Concat(proposed.Select(c => ("proposal_change", c.GetProperty("objectId").GetString(), c.GetProperty("levelPath").EnumerateArray().Last().GetString())))
                .Concat(both.Select(c => ("changed_on_both_sides", c.GetProperty("objectId").GetString(), c.GetProperty("levelPath").EnumerateArray().Last().GetString()))).ToArray(),
            details.Select(d => (d.GetProperty("kind").GetString(), d.GetProperty("objectId").GetString(), d.GetProperty("scopeBlockId").GetString())).ToArray(),
            label + ": one detail per changed element.");
        var todayKeys = current.Select(ChangeKey).ToHashSet();
        CollectionAssert.AreEqual(proposed.Where(p => todayKeys.Contains(ChangeKey(p))).Select(ChangeKey).ToArray(),
            both.Select(ChangeKey).ToArray(), label + ": the elements changed on both sides.");
    }

    /// <summary>The comparison of a proposal made from <see cref="RecursiveBlockProposalTests.CreateFor"/> on the original input's root
    /// with today's root. The proposal's side is exactly what that proposal changes; today's side matches, level by level, the saved
    /// history comparison (the root's through the production kicad_diagram_history_compare, each changed child's through the model),
    /// and names the root's native Routing save, its component choice and its physical allocation, which both sides changed where the
    /// proposal did too.</summary>
    private static async Task AssertStaleRootComparison(McpClient client, IReadOnlyDictionary<string, object?> arguments, string sourceToken,
        JsonElement comparison, RecursiveBlockGraph graph, DiagramRefinementInput input, BlockProposal proposal, bool published, CancellationToken token)
    {
        var baseRoot = input.BlockPath[^1]; Guid root = baseRoot.BlockId; Guid[] level = [root];
        Assert.AreEqual(published, comparison.GetProperty("published").GetBoolean());
        Assert.IsTrue(comparison.GetProperty("stale").GetBoolean(), "Today's root is no longer the revision the proposal was built on.");
        Assert.IsFalse(comparison.GetProperty("candidateSelected").GetBoolean());
        Assert.AreEqual(proposal.Id, comparison.GetProperty("proposalId").GetGuid()); Assert.AreEqual(input.Id, comparison.GetProperty("inputId").GetGuid());
        Assert.AreEqual(baseRoot, comparison.GetProperty("baseRevision").Deserialize<BlockSelection>(AgentJson));
        Assert.AreEqual(proposal.Candidate, comparison.GetProperty("candidate").Deserialize<BlockSelection>(AgentJson));
        CollectionAssert.AreEqual(new[] { graph.SelectedRoot }, comparison.GetProperty("currentPath").Deserialize<BlockSelection[]>(AgentJson));
        // The proposal's side: its rewritten fields, the component choice it leaves out, its two new blocks and its new connection.
        Assert.IsFalse(graph.Inspect(baseRoot).EffectiveComponentBindings.SameContents(BlockComponentBindings.Empty), "The base states components.");
        var expected = new List<(string Key, string Kind)>();
        var baseFields = graph.Requirements(baseRoot).Requirements;
        foreach (var field in Enum.GetValues<DiagramRequirementField>())
            if (baseFields.Get(field) != proposal.Blocks[0].Requirements.Get(field))
                expected.Add((ChangeKey(level, [], DiagramHistoryChangeCategory.Requirement, root, field, null), "Changed"));
        expected.Add((ChangeKey(level, [], DiagramHistoryChangeCategory.Definition, root, null, "Components"), "Changed"));
        foreach (var block in proposal.Blocks.Where(b => b.BasedOn is null))
            expected.Add((ChangeKey(level, [], DiagramHistoryChangeCategory.Block, block.Selection.BlockId, null, null), "Added"));
        foreach (var link in proposal.Connections.Where(c => c.BasedOn is null))
            expected.Add((ChangeKey(level, [], DiagramHistoryChangeCategory.Connection, link.Selection.ConnectionId, null, null), "Added"));
        var proposed = comparison.GetProperty("proposalChanges").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(expected.Select(e => e.Key + "=" + e.Kind).ToArray(),
            proposed.Select(c => ChangeKey(c) + "=" + c.GetProperty("kind").GetString()).ToArray(), "Exactly what the proposal changes.");
        foreach (var block in proposal.Blocks.Where(b => b.BasedOn is null))
            Assert.AreEqual(block.Selection.RevisionId, proposed.Single(c => c.GetProperty("objectId").GetGuid() == block.Selection.BlockId).GetProperty("afterRevisionId").GetGuid());
        // Today's side, level by level.
        var current = comparison.GetProperty("currentChanges").EnumerateArray().ToArray();
        var today = graph.SelectedRoot;
        Assert.AreEqual(baseRoot.StateId, today.StateId, "The root kept its implementation, so its saved history compares the two revisions.");
        var history = await client.CallToolAsync("kicad_diagram_history_compare", new Dictionary<string, object?>(arguments)
            { ["context"] = today, ["inspected"] = baseRoot, ["expectedSourceToken"] = sourceToken }, cancellationToken: token);
        Assert.IsFalse(history.IsError == true, JsonSerializer.Serialize(history));
        var saved = P.DiagramHistoryComparisonData.Parser.ParseJson(JsonSerializer.SerializeToElement(history).GetProperty("structuredContent").GetProperty("comparison").GetRawText());
        var rootChanges = saved.Changes.Select(c => LevelChange(level, new((DiagramHistoryChangeCategory)((int)c.Category - 1), (DiagramHistoryChangeKind)((int)c.Kind - 1),
            Guid.Parse(c.ObjectId), c.Name, c.Field == P.RequirementFieldKind.RfkUnknown ? null : (DiagramRequirementField)((int)c.Field - 1)))).ToArray();
        CollectionAssert.AreEquivalent(rootChanges.Select(c => c.Key + "=" + c.Kind).ToArray(),
            current.Where(c => c.GetProperty("levelPath").GetArrayLength() == 1 && c.GetProperty("connectionPath").GetArrayLength() == 0)
                .Select(c => ChangeKey(c) + "=" + c.GetProperty("kind").GetString()).ToArray(), "Today's root level is its saved history comparison.");
        var baseChildren = graph.Inspect(baseRoot).Children.ToDictionary(c => c.BlockId);
        foreach (var child in graph.Inspect(today).Children.Where(c => baseChildren.TryGetValue(c.BlockId, out var was) && was != c))
        {
            Guid[] childLevel = [root, child.BlockId];
            var changed = current.Single(c => c.GetProperty("levelPath").GetArrayLength() == 1 && c.GetProperty("category").GetString() == "Block"
                && c.GetProperty("objectId").GetGuid() == child.BlockId);
            Assert.AreEqual("Changed", changed.GetProperty("kind").GetString());
            Assert.AreEqual(baseChildren[child.BlockId].RevisionId, changed.GetProperty("beforeRevisionId").GetGuid());
            Assert.AreEqual(child.RevisionId, changed.GetProperty("afterRevisionId").GetGuid());
            CollectionAssert.AreEquivalent(DiagramHistoryQuery.Changes(graph, baseChildren[child.BlockId], child).Select(c => LevelChange(childLevel, c)).Select(c => c.Key + "=" + c.Kind).ToArray(),
                current.Where(c => c.GetProperty("levelPath").GetArrayLength() == 2 && c.GetProperty("levelPath")[1].GetGuid() == child.BlockId
                    && c.GetProperty("connectionPath").GetArrayLength() == 0).Select(c => ChangeKey(c) + "=" + c.GetProperty("kind").GetString()).ToArray(),
                "A changed child's own level: " + child.BlockId);
        }
        // Every changed connection names what changed in it.
        foreach (var link in current.Where(c => c.GetProperty("category").GetString() == "Connection" && c.GetProperty("kind").GetString() == "Changed"
            && c.GetProperty("connectionPath").GetArrayLength() == 0))
            Assert.IsTrue(current.Any(c => ChangeKey(c).StartsWith(string.Join("/", link.GetProperty("levelPath").EnumerateArray().Select(e => e.GetString())) + "|"
                + link.GetProperty("objectId").GetString(), StringComparison.Ordinal)), "The parts of changed connection " + link.GetProperty("objectId").GetString());
        string[] todayKeys = [.. current.Select(c => ChangeKey(c) + "=" + c.GetProperty("kind").GetString())];
        CollectionAssert.Contains(todayKeys, ChangeKey(level, [], DiagramHistoryChangeCategory.Requirement, root, DiagramRequirementField.Routing, null) + "=Changed",
            "The native Routing save.");
        CollectionAssert.Contains(todayKeys, ChangeKey(level, [], DiagramHistoryChangeCategory.Definition, root, null, "Components") + "=Changed", "The agent's component choice.");
        CollectionAssert.Contains(todayKeys, ChangeKey(level, [], DiagramHistoryChangeCategory.PhysicalAllocation, root, null, null) + "=Changed", "The physical allocation.");
        var both = comparison.GetProperty("changedOnBothSides").EnumerateArray().Select(ChangeKey).ToArray();
        CollectionAssert.Contains(both, ChangeKey(level, [], DiagramHistoryChangeCategory.Definition, root, null, "Components"));
        if (baseFields.Routing != proposal.Blocks[0].Requirements.Routing)
            CollectionAssert.Contains(both, ChangeKey(level, [], DiagramHistoryChangeCategory.Requirement, root, DiagramRequirementField.Routing, null));
        var todayKeySet = current.Select(ChangeKey).ToHashSet();
        CollectionAssert.AreEqual(proposed.Where(p => todayKeySet.Contains(ChangeKey(p))).Select(ChangeKey).ToArray(), both, "Changed on both sides.");
    }

    /// <summary>A complete field history read through the production MCP server (<c>kicad_diagram_field_history</c>) for an
    /// exact block revision, or for an exact connection or member of its level, page by page (at most 200 entries each)
    /// until every entry is loaded. Every page must name the same saved context and total.</summary>
    private static async Task<IReadOnlyList<JsonElement>> FieldHistoryOverMcp(McpClient client, Dictionary<string, object?> arguments,
        BlockSelection block, string field, ConnectionSelection? connection, CancellationToken token)
    {
        var entries = new List<JsonElement>(); uint? total = null; string? context = null;
        do
        {
            var request = new Dictionary<string, object?>(arguments)
            {
                ["blockId"] = block.BlockId.ToString("D"), ["stateId"] = block.StateId.ToString("D"), ["revisionId"] = block.RevisionId.ToString("D"),
                ["field"] = field, ["offset"] = entries.Count, ["limit"] = 200
            };
            if (connection is not null)
            {
                request["connectionId"] = connection.ConnectionId.ToString("D"); request["connectionStateId"] = connection.StateId.ToString("D");
                request["connectionRevisionId"] = connection.RevisionId.ToString("D");
            }
            var result = await client.CallToolAsync("kicad_diagram_field_history", request, cancellationToken: token);
            var data = JsonSerializer.SerializeToElement(result);
            Assert.IsFalse(result.IsError == true, "The field history read failed: " + data.GetRawText());
            var page = data.GetProperty("structuredContent").GetProperty("history");
            uint pageTotal = page.GetProperty("total").GetUInt32(); string pageContext = page.GetProperty("contextRevisionId").GetString()!;
            Assert.AreEqual(total ?? pageTotal, pageTotal, "Every page reports the same total."); total = pageTotal;
            Assert.AreEqual(context ?? pageContext, pageContext, "Every page belongs to the same saved context."); context = pageContext;
            var rows = page.TryGetProperty("entries", out var list) ? list.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            Assert.IsTrue(rows.Length > 0 || entries.Count == total, "A page before the end returns entries.");
            entries.AddRange(rows);
        }
        while (entries.Count < total);
        return entries;
    }

    /// <summary>Every entry of a field history read from the model, page by page (the query returns at most 200 per page).</summary>
    private static List<DiagramFieldHistoryEntry> AllFieldEntries(Func<int, int, DiagramFieldHistoryPage> read)
    {
        var entries = new List<DiagramFieldHistoryEntry>(); DiagramFieldHistoryPage page;
        do { page = read(entries.Count, 200); entries.AddRange(page.Entries); } while (page.HasMore);
        return entries;
    }

    /// <summary>The newest field-history entry a published proposal adds for its target block (or for one connection it
    /// refines): the agent's rewrite at version 2 of the proposed implementation (version 1 is its copy of the baseline),
    /// with the proposal's origin, sources and the input and proposal it came from.</summary>
    private static DiagramFieldHistoryEntry ProposedFieldEntry(BlockProposal proposal, DiagramRequirementField field, ProposedConnection? connection = null)
    {
        var origin = proposal.Origin with { InputIds = [.. proposal.Origin.InputIds.Append(proposal.InputId).Append(proposal.Id).Distinct()] };
        if (connection is not null)
            return new(connection.RequirementRevisionId, connection.Selection.RevisionId, 2, connection.Name, connection.Requirements.Get(field), origin, true);
        var target = proposal.Blocks.Single(b => b.Selection == proposal.Candidate);
        return new(target.RequirementRevisionId, proposal.Candidate.RevisionId, 2, target.Name, target.Requirements.Get(field), origin, true);
    }

    /// <summary>Asserts that an MCP field-history page lists exactly the expected entries, newest first: each entry's requirement
    /// revision, saved context, the implementation it was saved in and its version there, the owner's name in that revision,
    /// text, saved marker, author (kind, name and time), summary, every source statement and the linked inputs.</summary>
    private static void AssertFieldHistory(IReadOnlyList<JsonElement> rows, IReadOnlyList<DiagramFieldHistoryEntry> expected, string what,
        Func<Guid, (Guid State, string Name)> implementationOf)
    {
        Assert.HasCount(expected.Count, rows, what + ": " + string.Join(", ", rows.Take(3).Select(r => r.GetRawText())));
        static string Optional(JsonElement row, string name) => row.TryGetProperty(name, out var value) ? value.ToString() : "";
        for (int i = 0; i < expected.Count; ++i)
        {
            var row = rows[i]; var want = expected[i]; string at = $"{what}, entry {i}: {row.GetRawText()}";
            Assert.AreEqual(want.RequirementRevisionId.ToString("D"), row.GetProperty("requirementRevisionId").GetString(), at);
            Assert.AreEqual(want.ContextRevisionId.ToString("D"), row.GetProperty("contextRevisionId").GetString(), at);
            Assert.AreEqual(want.ContextVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), Optional(row, "contextVersion"), at);
            // The name the block, connection or member had in that revision.
            Assert.AreEqual(want.OwnerName, Optional(row, "ownerName"), at);
            Assert.AreEqual(want.Text, Optional(row, "text"), at);
            Assert.AreEqual(want.IsSavedText, row.TryGetProperty("isSavedText", out var saved) && saved.GetBoolean(), at);
            // The implementation the entry was saved in, which for an earlier implementation's entry is not the requested one.
            var (state, implementation) = implementationOf(want.ContextRevisionId);
            Assert.AreEqual(state.ToString("D"), row.GetProperty("contextStateId").GetString(), at);
            Assert.AreEqual(implementation, row.GetProperty("contextImplementation").GetString(), at);
            var origin = row.GetProperty("origin");
            // The complete author identity: whether a person, an agent, an import or the editor wrote it, who, and when.
            Assert.AreEqual("DAK_" + want.Origin.ActorKind.ToString().ToUpperInvariant(), Optional(origin, "kind"), at);
            Assert.AreEqual(want.Origin.Actor, origin.GetProperty("actor").GetString(), at);
            Assert.AreEqual(want.Origin.RecordedAt, Google.Protobuf.JsonParser.Default.Parse<Google.Protobuf.WellKnownTypes.Timestamp>(
                JsonSerializer.Serialize(origin.GetProperty("recordedAt").GetString())).ToDateTimeOffset(), at);
            Assert.AreEqual(want.Origin.Summary, Optional(origin, "summary"), at);
            CollectionAssert.AreEqual(want.Origin.InputIds.Select(id => id.ToString("D")).ToArray(),
                origin.TryGetProperty("inputIds", out var inputs) ? inputs.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string>(), at);
            // Each source statement exactly: its document, revision, page, table and part variant.
            CollectionAssert.AreEqual(want.Origin.Sources.Select(s => string.Join("|", s.DocumentId, s.Revision,
                    s.Page?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", s.Table ?? "", s.PartVariant ?? "")).ToArray(),
                origin.TryGetProperty("sources", out var sources) ? sources.EnumerateArray().Select(x => string.Join("|", Optional(x, "documentId"),
                    Optional(x, "revision"), Optional(x, "page"), Optional(x, "table"), Optional(x, "partVariant"))).ToArray() : Array.Empty<string>(), at);
        }
    }

    private static async Task CaptureRecursive(string display, string path, CancellationToken token)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-nostdin", "-y", "-loglevel", "error", "-f", "x11grab", "-video_size", "1600x1150", "-i", display, "-frames:v", "1", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); Assert.AreEqual(0, process.ExitCode, await stderr); await stdout; }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    private static async Task VerifyExpandedCanvasContent(string path, CancellationToken token)
    {
        // This acceptance fixture has two visible peer blocks. Inspect only its
        // canvas, excluding inspector, toolbar, other windows and the cursor at Save.
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        // The crop starts right of the canvas-edge drawing palette, so only diagram content counts.
        foreach (string arg in new[] { "-nostdin", "-loglevel", "error", "-i", path, "-vf", "crop=900:760:120:180",
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var pixels = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(pixels, token); var diagnostics = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token); await copy; Assert.AreEqual(0, process.ExitCode, await diagnostics);
            byte[] image = pixels.ToArray(); Assert.AreEqual(900 * 760 * 3, image.Length);
            int ink = CountCanvasInk(image);
            Assert.IsTrue(ink > 250, $"The known populated diagram rendered as a blank canvas after expansion ({ink} differing pixels).");
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    internal static int CountCanvasInk(byte[] pixels)
    {
        if (pixels.Length < 3 || pixels.Length % 3 != 0) throw new ArgumentException("Supply RGB pixels for the declared canvas region.");
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 3)
            if (Math.Abs(pixels[i] - pixels[0]) + Math.Abs(pixels[i + 1] - pixels[1]) + Math.Abs(pixels[i + 2] - pixels[2]) > 30) ++count;
        return count;
    }

    /// <summary>Design QA P2-1 to P2-4, measured in one capture: the drawing tools in the toolbar strip and the canvas-edge
    /// palette. The active tool shows an accent border (3:1 on the toolbar) with an accent label (4.5:1 on its tile) in the
    /// strip and a solid accent cell (3:1 on the palette) with a 4.5:1 label in the palette; every glyph is about the size of
    /// the toolbar's own icons and every strip label shares the toolbar's label line; the port glyph is monochrome in the
    /// theme's text colour; the palette is one surface with one divider, before Undo.</summary>
    private static void VerifyToolStyling(CapturedWindow shot, P.RecursiveDiagramEditorState at, string step, string activeTool)
    {
        P.DiagramControlRect Find(string name) => at.Controls.Single(c => c.Name == name);
        string[] tools = ["Select", "AddBlock", "Connect", "PlacePort", "Delete"];
        // The drawing tools are the platform's own buttons, which the editor paints: assistive technology reads each tool as a
        // toggle button named by its label and pressed when it is the active tool, and Delete and Undo as push buttons (the
        // editor reads this back from the toolkit's accessibility object, not from its own state).
        foreach (string prefix in new[] { "RecursiveTool", "DiagramPalette" })
            foreach (string tool in prefix == "RecursiveTool" ? tools : tools.Append("Undo").ToArray())
            {
                var button = Find(prefix + tool);
                bool action = tool is "Delete" or "Undo";
                VerifyAccessible(button, action ? "button" : "toggle button", step);
                Assert.AreEqual(!action && tool == activeTool, button.Accessible.Checked,
                    $"{step}: assistive technology reads {button.Name} as {(tool == activeTool ? "pressed" : "not pressed")}.");
            }
        var stripDelete = Find("RecursiveToolDelete");
        var toolbar = shot.At(stripDelete.X + stripDelete.Width + 80, stripDelete.Y + stripDelete.Height / 2);
        string D(ValueTuple<byte, byte, byte> c) => CapturedWindow.Describe(c);

        // Sketch 1 sets Delete apart from the drawing tools (design QA round 2, P3 4): the gap between Place port and Delete holds
        // one thin line, drawn like the toolbar's own separator before Select.
        var placePort = Find("RecursiveToolPlacePort"); var select = Find("RecursiveToolSelect");
        int bandY = stripDelete.Y + stripDelete.Height / 4, bandHeight = stripDelete.Height / 2;
        var beforeDelete = shot.InkRuns(placePort.X + placePort.Width, bandY, stripDelete.X - placePort.X - placePort.Width, bandHeight, toolbar, rows: false, minimum: 1.05);
        var beforeSelect = shot.InkRuns(select.X - 12, bandY, 12, bandHeight, toolbar, rows: false, minimum: 1.05);
        Assert.IsTrue(beforeDelete.Count == 1 && beforeDelete[0].Last - beforeDelete[0].First <= 1 && beforeSelect.Count == 1,
            $"{step}: one thin separator stands between Place port and Delete ({beforeDelete.Count} runs of ink), as before Select ({beforeSelect.Count}).");
        var deleteSeparator = shot.At(beforeDelete[0].First, bandY + bandHeight / 2); var selectSeparator = shot.At(beforeSelect[0].First, bandY + bandHeight / 2);
        Assert.IsTrue(CapturedWindow.Distance(deleteSeparator, selectSeparator) <= 12,
            $"{step}: the separator before Delete {D(deleteSeparator)} is drawn like the one before Select {D(selectSeparator)}.");

        // Strip: the active tool.
        var active = Find("RecursiveTool" + activeTool);
        Assert.IsTrue(active.Active, step + ": the strip shows " + activeTool + " as the active tool.");
        var tile = shot.At(active.X + 6, active.Y + 5);
        var border = shot.MostContrasting(active.X, active.Y + active.Height / 3, 3, active.Height / 3, toolbar);
        Assert.IsTrue(shot.Contrast("P2-1 active strip tile border on the toolbar", border, toolbar) >= 3.0,
            $"{step}: the active strip tool's border {D(border)} stands 3:1 from the toolbar {D(toolbar)}.");
        (byte, byte, byte) Label(P.DiagramControlRect button, (byte, byte, byte) behind) =>
            shot.MostContrasting(button.X + 4, button.Y + button.Height * 2 / 3, button.Width - 8, button.Height / 3 - 4, behind);
        var activeLabel = Label(active, tile);
        Assert.IsTrue(shot.Contrast("P2-1 active strip label on its tile", activeLabel, tile) >= 4.5, $"{step}: the active strip label {D(activeLabel)} reads 4.5:1 on its tile {D(tile)}.");
        var idle = Find("RecursiveTool" + tools.First(t => t != activeTool));
        var idleLabel = Label(idle, toolbar);
        Assert.IsTrue(CapturedWindow.Distance(activeLabel, idleLabel) >= 60,
            $"{step}: the active strip label {D(activeLabel)} is coloured apart from an idle one {D(idleLabel)}.");

        // Strip: glyph size and one label line with the toolbar's own tools (Fit, drawn by the toolkit at x 360 to 395).
        int? labelTop = null;
        foreach (string tool in tools.Where(t => t != activeTool))
        {
            var button = Find("RecursiveTool" + tool);
            // Measured against the button's own surface (the toolbar, or a hover tile under the pointer).
            var behind = shot.At(button.X + 4, button.Y + 4);
            var runs = shot.InkRuns(button.X + 2, button.Y + 2, button.Width - 4, button.Height - 4, behind, rows: true, minimum: 1.5);
            Assert.IsTrue(runs.Count >= 2, $"{step}: the strip's {tool} shows a glyph and a label ({runs.Count} ink runs).");
            var glyph = runs[0];
            var glyphColumns = shot.InkRuns(button.X + 2, glyph.First, button.Width - 4, glyph.Last - glyph.First + 1, behind, rows: false, minimum: 1.5);
            int glyphWidth = glyphColumns[^1].Last - glyphColumns[0].First + 1, glyphHeight = glyph.Last - glyph.First + 1;
            Assert.IsTrue(shot.Record($"P2-2 strip {tool} glyph size (px)", Math.Max(glyphWidth, glyphHeight)) >= 15,
                $"{step}: the strip's {tool} glyph is {glyphWidth} x {glyphHeight} pixels, near the toolbar's icon size.");
            int top = runs[^1].First;
            labelTop ??= top;
            Assert.IsTrue(Math.Abs(top - labelTop.Value) <= 1, $"{step}: the strip's {tool} label starts at row {top}, the others at {labelTop}.");
            if (tool == "PlacePort")
            {
                // A monochrome glyph in the theme's text colour: every inked pixel is a grey.
                for (int y = glyph.First; y <= glyph.Last; ++y)
                    for (int x = button.X + 2; x < button.X + button.Width - 2; ++x)
                    {
                        var pixel = shot.At(x, y);
                        if (CapturedWindow.Contrast(pixel, behind) < 2.0) continue;
                        int spread = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                        Assert.IsTrue(spread <= 40, $"{step}: the Place port glyph is monochrome; pixel ({x}, {y}) is {D(pixel)}.");
                    }
                var ink = shot.MostContrasting(button.X + 2, glyph.First, button.Width - 4, glyphHeight, behind);
                Assert.IsTrue(shot.Contrast("P2-3 Place port glyph on the toolbar", ink, behind) >= 4.5, $"{step}: the Place port glyph {D(ink)} reads 4.5:1 on the toolbar.");
            }
        }
        var native = shot.InkRuns(362, stripDelete.Y, 32, stripDelete.Height, toolbar, rows: true, minimum: 2.0);
        Assert.IsTrue(native.Count >= 2, $"{step}: the toolbar's Fit tool shows an icon and a label.");
        Assert.IsTrue(Math.Abs(shot.Record("P2-2 strip label row minus the toolbar's Fit label row (px)", labelTop!.Value - native[^1].First)) <= 1,
            $"{step}: the strip's labels start at row {labelTop}, the toolbar's own Fit label at {native[^1].First}.");

        // Palette: the active tool's cell, one surface, one divider before Undo.
        var cell = Find("DiagramPalette" + activeTool);
        Assert.IsTrue(cell.Active, step + ": the palette shows " + activeTool + " as the active tool.");
        var surface = shot.At(cell.X + cell.Width / 2, cell.Y + cell.Height + 3);
        var fill = shot.At(cell.X + cell.Width / 2, cell.Y + 4);
        Assert.IsTrue(shot.Contrast("P2-1 active palette cell on the palette", fill, surface) >= 3.0, $"{step}: the active palette cell {D(fill)} stands 3:1 from the palette {D(surface)}.");
        var cellLabel = Label(cell, fill);
        Assert.IsTrue(shot.Contrast("P2-1 active palette label on its cell", cellLabel, fill) >= 4.5, $"{step}: the active palette label {D(cellLabel)} reads 4.5:1 on its cell {D(fill)}.");
        string[] cells = [.. tools, "Undo"];
        foreach (string name in cells.Where(t => t != activeTool))
        {
            var other = Find("DiagramPalette" + name);
            var behind = shot.At(other.X + 1, other.Y + 1);
            Assert.IsTrue(CapturedWindow.Distance(behind, surface) <= 12, $"{step}: the palette's {name} sits on the palette's one surface ({D(behind)} and {D(surface)}).");
            // An unavailable tool (Delete or Undo with nothing to act on) is drawn faintly, so any ink on the flat cell counts.
            var runs = shot.InkRuns(other.X + 2, other.Y + 2, other.Width - 4, other.Height - 4, surface, rows: true, minimum: 1.3);
            Assert.IsTrue(runs.Count >= 2, $"{step}: the palette's {name} shows a glyph and a label ({runs.Count} ink runs).");
            var glyph = runs[0];
            var columns = shot.InkRuns(other.X + 2, glyph.First, other.Width - 4, glyph.Last - glyph.First + 1, surface, rows: false, minimum: 1.3);
            int size = Math.Max(columns[^1].Last - columns[0].First + 1, glyph.Last - glyph.First + 1);
            Assert.IsTrue(shot.Record($"P2-2 palette {name} glyph size (px)", size) >= 15, $"{step}: the palette's {name} glyph is {size} pixels, one size for every tool.");
        }
        var deleteCell = Find("DiagramPaletteDelete"); var undoCell = Find("DiagramPaletteUndo"); var selectCell = Find("DiagramPaletteSelect");
        var divider = shot.InkRuns(deleteCell.X + deleteCell.Width / 2, deleteCell.Y + deleteCell.Height, 1, undoCell.Y - deleteCell.Y - deleteCell.Height,
            surface, rows: true, minimum: 1.1);
        Assert.HasCount(1, divider, step + ": one divider line before Undo.");
        Assert.IsTrue(divider[0].Last - divider[0].First <= 1, step + ": the divider is a thin line.");
        var addCell = Find("DiagramPaletteAddBlock");
        Assert.IsEmpty(shot.InkRuns(selectCell.X + selectCell.Width / 2, selectCell.Y + selectCell.Height, 1, addCell.Y - selectCell.Y - selectCell.Height,
            surface, rows: true, minimum: 1.1), step + ": no divider between the tools.");
        // The card's outline is quiet in both themes (design QA round 2, P3 9: the dark theme's was near-white, 8.4:1 on the canvas,
        // against the light theme's 1.7:1): measured across the card's left edge beside Select.
        var canvasColour = shot.At(at.CanvasWindowX + (int)at.CanvasPixelWidth - 12, at.CanvasWindowY + (int)at.CanvasPixelHeight - 12);
        var outline = shot.MostContrasting(selectCell.X - 9, selectCell.Y + selectCell.Height / 2, 7, 1, canvasColour);
        Assert.IsTrue(shot.Contrast("P3 9 palette outline on the canvas", outline, canvasColour) <= 3.5,
            $"{step}: the palette's outline {D(outline)} is quiet on the canvas {D(canvasColour)}.");
    }

    /// <summary>Design QA P1-1, measured in one capture: the selected block's border and its handles' outlines stand 3:1 or
    /// more from both the canvas and the selected fill, and in the dark theme the handles are filled with the canvas's
    /// foreground, 3:1 or more from both as well.</summary>
    private static void VerifySelectionContrast(CapturedWindow shot, P.RecursiveDiagramEditorState at, string step, string block)
    {
        string D(ValueTuple<byte, byte, byte> c) => CapturedWindow.Describe(c);
        var box = at.BlockTexts.Single(b => b.BlockId == block).Block;
        Assert.HasCount(8, at.SelectionHandles, step + ": the selected block shows eight handles.");
        var canvas = shot.At(at.CanvasWindowX + (int)at.CanvasPixelWidth - 12, at.CanvasWindowY + (int)at.CanvasPixelHeight - 12);
        bool dark = canvas.R + canvas.G + canvas.B < 384;
        var fill = shot.At(box.X + box.Width - 18, box.Y + box.Height - 14);
        var border = shot.MostContrasting(box.X + box.Width / 4 - 2, box.Y - 1, 5, 4, fill);
        Assert.IsTrue(shot.Contrast("P1-1 selected border on the canvas", border, canvas) >= 3.0, $"{step}: the selected border {D(border)} stands 3:1 from the canvas {D(canvas)}.");
        Assert.IsTrue(shot.Contrast("P1-1 selected border on the selected fill", border, fill) >= 3.0, $"{step}: the selected border {D(border)} stands 3:1 from the selected fill {D(fill)}.");
        foreach (var handle in at.SelectionHandles)
        {
            Assert.IsTrue(handle.Width >= 8 && handle.Height >= 8, step + ": a handle is at least 8 pixels.");
            var outline = shot.At(handle.X, handle.Y + handle.Height / 2);
            Assert.IsTrue(shot.Contrast("P1-1 handle outline on the canvas", outline, canvas) >= 3.0 && shot.Contrast("P1-1 handle outline on the selected fill", outline, fill) >= 3.0,
                $"{step}: a handle's outline {D(outline)} stands 3:1 from the canvas {D(canvas)} and the fill {D(fill)}.");
            if (!dark) continue;
            var centre = shot.At(handle.X + handle.Width / 2, handle.Y + handle.Height / 2);
            Assert.IsTrue(shot.Contrast("P1-1 handle fill on the canvas", centre, canvas) >= 3.0 && shot.Contrast("P1-1 handle fill on the selected fill", centre, fill) >= 3.0,
                $"{step}: a handle's fill {D(centre)} stands 3:1 from the canvas {D(canvas)} and the selected fill {D(fill)}.");
        }
    }

    /// <summary>Design QA P2-5, from the paths the editor reports it drew: no two connections share a segment or an end point,
    /// no end sits on a block's corner, vertical legs that run beside each other keep at least 10 units apart, and so do level
    /// runs that overlap (the formal QA asks for 8 pixels; the drawing journey's level is drawn at about a pixel per unit).</summary>
    private static void VerifyPathsApart(string step, (double X, double Y, double W, double H)[] blocks, params (double X, double Y)[][] paths)
    {
        static double Overlap(double a, double b, double c, double d) => Math.Max(0, Math.Min(Math.Max(a, b), Math.Max(c, d)) - Math.Max(Math.Min(a, b), Math.Min(c, d)));
        for (int i = 0; i < paths.Length; ++i)
        {
            foreach (var end in new[] { paths[i][0], paths[i][^1] })
                foreach (var (x, y, w, h) in blocks)
                    Assert.IsFalse((end.X == x || end.X == x + w) && (end.Y == y || end.Y == y + h), $"{step}: no connection starts or ends on a block's corner ({end.X}, {end.Y}).");
            for (int j = i + 1; j < paths.Length; ++j)
            {
                var a = paths[i]; var b = paths[j];
                Assert.IsFalse(new[] { a[0], a[^1] }.Intersect(new[] { b[0], b[^1] }).Any(), $"{step}: two connections never share an end point.");
                for (int m = 1; m < a.Length; ++m)
                    for (int n = 1; n < b.Length; ++n)
                    {
                        var (p, q, r, t) = (a[m - 1], a[m], b[n - 1], b[n]);
                        if (p.Y == q.Y && r.Y == t.Y && p.X != q.X && r.X != t.X && Overlap(p.X, q.X, r.X, t.X) > 0)
                            Assert.IsTrue(Math.Abs(p.Y - r.Y) >= 10, $"{step}: horizontal legs at y {p.Y} and {r.Y} run beside each other less than 10 units apart.");
                        if (p.X == q.X && r.X == t.X && p.Y != q.Y && r.Y != t.Y && Overlap(p.Y, q.Y, r.Y, t.Y) > 0)
                            Assert.IsTrue(Math.Abs(p.X - r.X) >= 10, $"{step}: vertical legs at x {p.X} and {r.X} run beside each other less than 10 units apart.");
                    }
            }
        }
    }

    /// <summary>Design QA P2-5 on a level as an agent observes it (resolved_layout). Every leg leaves a block's left or right
    /// edge, and a boundary port on the level frame's left or right side (an unplaced one sits on the left), along the edge for
    /// at least 20 units before it turns (rule F4c), and a block's top or bottom edge straight up or down for at least 20 units
    /// (rule F4d), never along the block's outline; from a boundary port on the frame's top or bottom side it enters the level
    /// straight down or up for at least 20 units. No two connections run beside each other (rule F4a): level runs on one height
    /// that overlap or end less than 10 units apart, level runs less than 10 units apart in height that overlap (the formal QA
    /// asks for 8 pixels, and the editor draws about a pixel per unit at its default size), or upright runs less than 10 units
    /// apart over heights they share or meet at, unless both runs start at the same end point (two connections from one port).
    /// A pair of connections named in <paramref name="unavoidable"/> may run beside each other only along the 20 units one of
    /// them needs to leave its end: each such run lies within 20 units of an end on its height, and they total 20 units at
    /// most.</summary>
    private static void VerifyRoutesClear(JsonElement layout, string step, params (string First, string Second)[] unavoidable)
    {
        static decimal Unit(JsonElement value) => decimal.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var blocks = layout.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("rect"))
            .Select(r => (X: Unit(r.GetProperty("x")), Y: Unit(r.GetProperty("y")), W: Unit(r.GetProperty("width")), H: Unit(r.GetProperty("height")))).ToArray();
        // A port of the level's own boundary (its owner is not one of the drawn blocks), with the way a leg enters the level from it.
        var children = layout.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("blockId").GetString()).ToHashSet();
        var boundary = layout.GetProperty("ports").EnumerateArray().Where(p => !children.Contains(p.GetProperty("blockId").GetString()))
            .GroupBy(p => (X: Unit(p.GetProperty("anchor").GetProperty("x")), Y: Unit(p.GetProperty("anchor").GetProperty("y"))))
            .ToDictionary(g => g.Key, g => g.First().GetProperty("side").GetString() switch
                { "DPS_RIGHT" => (-1, 0), "DPS_TOP" => (0, 1), "DPS_BOTTOM" => (0, -1), _ => (1, 0) });
        var drawn = layout.GetProperty("routes").EnumerateArray()
            .Select(r => (Id: r.GetProperty("connectionId").GetString()!, Points: r.GetProperty("points").EnumerateArray()
                .Select(p => (X: Unit(p.GetProperty("x")), Y: Unit(p.GetProperty("y")))).ToArray())).ToArray();
        string Describe((decimal X, decimal Y)[] route) => string.Join(" ", route.Select(p => p.X + "," + p.Y));
        foreach (var (_, route) in drawn)
            foreach (var (end, next) in new[] { (route[0], route[1]), (route[^1], route[^2]) })
            {
                // Which way the leg must leave this end: out of the block edge it is on, or into the level from a boundary port.
                (int X, int Y) direction = boundary.TryGetValue(end, out var inward) ? inward : (0, 0);
                foreach (var (x, y, w, h) in blocks)
                {
                    if (end.Y >= y && end.Y <= y + h && (end.X == x || end.X == x + w)) direction = (end.X == x ? -1 : 1, 0);
                    else if (end.X > x && end.X < x + w && (end.Y == y || end.Y == y + h)) direction = (0, end.Y == y ? -1 : 1);
                }
                if (direction == (0, 0)) continue;
                if (direction.Y == 0)
                    Assert.IsTrue(next.Y == end.Y && (next.X - end.X) * direction.X >= 20,
                        $"{step}: the leg {Describe(route)} leaves its end ({end.X}, {end.Y}) along the edge for 20 units or more before it turns.");
                else
                    Assert.IsTrue(next.X == end.X && (next.Y - end.Y) * direction.Y >= 20,
                        $"{step}: the leg {Describe(route)} leaves its end ({end.X}, {end.Y}) on a {(direction.Y < 0 ? "top" : "bottom")} edge straight "
                        + $"{(direction.Y < 0 ? "up" : "down")} for 20 units or more, not along the block's outline.");
            }
        static decimal Overlap(decimal a1, decimal a2, decimal b1, decimal b2) => Math.Min(Math.Max(a1, a2), Math.Max(b1, b2)) - Math.Max(Math.Min(a1, a2), Math.Min(b1, b2));
        for (int i = 0; i < drawn.Length; ++i)
            for (int j = i + 1; j < drawn.Length; ++j)
            {
                var a = drawn[i].Points; var b = drawn[j].Points;
                bool allowed = unavoidable.Any(u => (u.First == drawn[i].Id && u.Second == drawn[j].Id) || (u.First == drawn[j].Id && u.Second == drawn[i].Id));
                var shared = new[] { a[0], a[^1] }.Intersect(new[] { b[0], b[^1] }).ToArray();
                decimal beside = 0;
                for (int m = 1; m < a.Length; ++m)
                    for (int n = 1; n < b.Length; ++n)
                    {
                        var (p, q, r, t) = (a[m - 1], a[m], b[n - 1], b[n]);
                        if (p == q || r == t) continue;
                        bool fromOneEnd = shared.Any(e => (e == p || e == q) && (e == r || e == t));
                        if (fromOneEnd) continue;
                        if (p.Y == q.Y && r.Y == t.Y && p.Y == r.Y)
                            Assert.IsTrue(-Overlap(p.X, q.X, r.X, t.X) >= 10, $"{step}: {Describe(a)} and {Describe(b)} run along one height at y {p.Y}.");
                        if (p.Y == q.Y && r.Y == t.Y && p.Y != r.Y && Math.Abs(p.Y - r.Y) < 10 && Overlap(p.X, q.X, r.X, t.X) > 0)
                        {
                            Assert.IsTrue(allowed, $"{step}: {Describe(a)} and {Describe(b)} run beside each other {Math.Abs(p.Y - r.Y)} units apart at y {p.Y} and {r.Y}.");
                            beside += Overlap(p.X, q.X, r.X, t.X);
                            // The run lies within the 20 units that an end on one of the two heights needs to leave its port or block.
                            decimal from = Math.Max(Math.Min(p.X, q.X), Math.Min(r.X, t.X)), to = Math.Min(Math.Max(p.X, q.X), Math.Max(r.X, t.X));
                            Assert.IsTrue(new[] { a[0], a[^1], b[0], b[^1] }.Any(e => (e.Y == p.Y || e.Y == r.Y) && from >= e.X - 20 && to <= e.X + 20),
                                $"{step}: {Describe(a)} and {Describe(b)} run beside each other from x {from} to {to}, not within the 20 units at an end.");
                        }
                        if (p.X == q.X && r.X == t.X && Math.Abs(p.X - r.X) < 10)
                            Assert.IsTrue(Overlap(p.Y, q.Y, r.Y, t.Y) < 0, $"{step}: {Describe(a)} and {Describe(b)} run upright within 10 units at x {p.X} and {r.X}.");
                    }
                if (allowed)
                    Assert.IsTrue(beside > 0 && beside <= 20,
                        $"{step}: {Describe(a)} and {Describe(b)} run beside each other for {beside} units; only the 20 units one of them needs to leave its end are allowed.");
            }
    }

    /// <summary>Review of the design QA fixes: a control the editor paints itself keeps what assistive technology reads (on
    /// GTK, the control's ATK object): the expected role, and its visible label as its name. ATK 2.36 renamed the role "push
    /// button" to "button"; either name is the same role.</summary>
    private static void VerifyAccessible(P.DiagramControlRect control, string role, string step)
    {
        Assert.IsNotNull(control.Accessible, $"{step}: {control.Name} is exposed to assistive technology.");
        string actual = control.Accessible.Role == "push button" ? "button" : control.Accessible.Role;
        Assert.AreEqual(role, actual, $"{step}: assistive technology reads {control.Name} as a {role}.");
        Assert.IsFalse(string.IsNullOrEmpty(control.Label), $"{step}: {control.Name} has a visible label.");
        Assert.AreEqual(control.Label, control.Accessible.Name, $"{step}: assistive technology names {control.Name} by its visible label.");
    }

    /// <summary>Design QA P2-9, measured in one capture: text in a multi-line box starts at least 7 pixels from its left border
    /// and 6 pixels below its top (the box's padding), not against the border.</summary>
    private static void VerifyTextInset(CapturedWindow shot, P.DiagramControlRect box, string step, string what)
    {
        // The box's own border and focus ring take its outer 3 pixels; the scan starts inside them and covers the left third,
        // where the text starts (clear of the caret at its end and of a pointer left near the box).
        var inside = shot.At(box.X + box.Width - 6, box.Y + box.Height - 5);
        var columns = shot.InkRuns(box.X + 4, box.Y + 4, box.Width / 3, Math.Min(box.Height - 8, 40), inside, rows: false, minimum: 2.0);
        var rows = shot.InkRuns(box.X + 4, box.Y + 4, box.Width / 3, Math.Min(box.Height - 8, 40), inside, rows: true, minimum: 2.0);
        Assert.IsTrue(columns.Count > 0 && rows.Count > 0, $"{step}: {what} shows its text.");
        Assert.IsTrue(shot.Record($"P2-9 {what} text inset from the left border (px)", columns[0].First - box.X) >= 7,
            $"{step}: {what}'s text starts {columns[0].First - box.X} pixels from its left border, at least 7.");
        Assert.IsTrue(shot.Record($"P2-9 {what} text inset from the top border (px)", rows[0].First - box.Y) >= 6,
            $"{step}: {what}'s text starts {rows[0].First - box.Y} pixels below its top, at least 6.");
    }

    /// <summary>Design QA P2-10, measured in one capture: an inspector action drawn as a link is underlined, reads 4.5:1 on the
    /// inspector and is coloured apart from the inspector's static text.</summary>
    private static void VerifyLink(CapturedWindow shot, P.DiagramControlRect link, P.DiagramControlRect text, string step, string what)
    {
        Assert.IsTrue(link.Shown && link.Enabled, $"{step}: {what} is shown and available.");
        VerifyAccessible(link, "link", step);
        var background = shot.At(link.X + 1, link.Y + 1);
        // Review of design QA round 2: a link has no box of its own. Its background is the inspector's surface just left of it,
        // where the earlier build drew each link on the colour wxWidgets reported, a faint box on the surface GTK drew.
        var surface = shot.At(link.X - 3, link.Y + link.Height / 2);
        Assert.IsTrue(CapturedWindow.Distance(background, surface) <= 3,
            $"{step}: {what} has no box of its own: its background {CapturedWindow.Describe(background)} is the inspector's {CapturedWindow.Describe(surface)}.");
        var ink = shot.MostContrasting(link.X, link.Y, link.Width, link.Height, background);
        Assert.IsTrue(shot.Contrast("P2-10 " + what + " on the inspector", ink, background) >= 4.5,
            $"{step}: {what} {CapturedWindow.Describe(ink)} reads 4.5:1 on the inspector {CapturedWindow.Describe(background)}.");
        var textInk = shot.MostContrasting(text.X, text.Y, text.Width, text.Height, background);
        Assert.IsTrue(CapturedWindow.Distance(ink, textInk) >= 60,
            $"{step}: {what} {CapturedWindow.Describe(ink)} is coloured apart from the static text {CapturedWindow.Describe(textInk)}.");
        var columns = shot.InkRuns(link.X, link.Y, link.Width, link.Height, background, rows: false, minimum: 2.0);
        Assert.IsNotEmpty(columns, $"{step}: {what} shows its text.");
        int left = columns[0].First, right = columns[^1].Last;
        bool underlined = false;
        for (int y = link.Y; y < link.Y + link.Height && !underlined; ++y)
        {
            int inked = 0;
            for (int x = left; x <= right; ++x) if (CapturedWindow.Contrast(shot.At(x, y), background) >= 2.0) ++inked;
            underlined = inked >= (right - left + 1) * 0.8;
        }
        Assert.IsTrue(underlined, $"{step}: {what} is underlined.");
    }

    /// <summary>Design QA P2-11, measured in one capture: every shown facet row ends in a chevron at least 9 pixels high and 3:1
    /// or more on the row (its focus ring and bottom rule left out of the measurement). Each row is the platform's own toggle
    /// button: assistive technology reads it as a toggle button named by its facet and value, pressed exactly for the facet
    /// whose detail is open (review of the design QA fixes).</summary>
    private static void VerifyChevrons(CapturedWindow shot, P.RecursiveDiagramEditorState at, string step)
    {
        var rows = at.Controls.Where(c => c.Name.StartsWith("RecursiveFacetRow", StringComparison.Ordinal) && c.Shown).ToArray();
        Assert.IsNotEmpty(rows, step + ": the facet overview lists facets.");
        string open = at.FacetEditor == "" ? "" : "RecursiveFacetRow" + string.Concat(at.FacetEditor.Split('-').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        foreach (var row in rows)
        {
            VerifyAccessible(row, "toggle button", step);
            StringAssert.StartsWith(row.Label, FacetRowLabel(row.Name) + ": ", step + ": " + row.Name + " is named by its facet and value.");
            Assert.AreEqual(row.Name == open, row.Active, step + ": " + row.Name + " is pressed exactly while its facet's detail is open.");
            Assert.AreEqual(row.Name == open, row.Accessible.Checked, step + ": assistive technology reads " + row.Name + " as pressed exactly while its detail is open.");
            var background = shot.At(row.X + 3, row.Y + 3);
            var chevron = shot.MostContrasting(row.X + row.Width - 22, row.Y + 3, 18, row.Height - 6, background);
            Assert.IsTrue(shot.Contrast("P2-11 " + row.Name + " chevron on its row", chevron, background) >= 3.0,
                $"{step}: {row.Name}'s chevron {CapturedWindow.Describe(chevron)} stands 3:1 on the row {CapturedWindow.Describe(background)}.");
            var runs = shot.InkRuns(row.X + row.Width - 22, row.Y + 3, 18, row.Height - 6, background, rows: true, minimum: 2.0);
            Assert.IsTrue(runs.Count > 0 && shot.Record("P2-11 " + row.Name + " chevron height (px)", runs[^1].Last - runs[0].First + 1) >= 9,
                $"{step}: {row.Name}'s chevron is text-sized, at least 9 pixels high.");
        }
    }

    /// <summary>The editor state without its layout statistics. They count the work of the whole editor process (an offscreen view
    /// of another level lays that level out once), so they are left out where a journey compares what a person sees and edits.</summary>
    private static P.RecursiveDiagramEditorState WithoutLayoutStatistics(P.RecursiveDiagramEditorState state)
    {
        var copy = state.Clone();
        copy.RouteLayouts = 0; copy.SlowestRouteLayoutMicros = 0; copy.LatestRouteLayoutMicros = 0;
        return copy;
    }

    /// <summary>The facet a facet row is named for, as its label starts: "Orderable part" for RecursiveFacetRowOrderablePart.</summary>
    private static string FacetRowLabel(string name) => name["RecursiveFacetRow".Length..] switch
    {
        "OrderablePart" => "Orderable part",
        var facet => facet
    };

    /// <summary>Design QA P2-13, measured in one capture: when Comments does not fit the inspector's view, the inspector shows a
    /// scroll bar that stays visible without the pointer over it.</summary>
    private static void VerifyInspectorScroll(CapturedWindow shot, P.RecursiveDiagramEditorState at, string step)
    {
        var inspector = at.Controls.Single(c => c.Name == "RecursiveInspector"); var comments = at.Controls.Single(c => c.Name == "RecursiveComments");
        if (comments.Y + comments.Height <= inspector.Y + inspector.Height) return;
        var background = shot.At(inspector.X + 3, inspector.Y + 3);
        var bar = shot.MostContrasting(inspector.X + inspector.Width - 16, inspector.Y + 4, 16, inspector.Height - 8, background);
        Assert.IsTrue(shot.Contrast("P2-13 inspector scroll bar on the inspector", bar, background) >= 2.0,
            $"{step}: Comments runs below the inspector's view, and a scroll bar {CapturedWindow.Describe(bar)} shows it on {CapturedWindow.Describe(background)}.");
    }

    /// <summary>Design QA round 2: a capture waits this long for GTK's fade of a button that just became available (the round's
    /// refuted "washed-out Save" was one frame of it).</summary>
    private const int FadeSettleMilliseconds = 250;

    private static bool RectsApart(P.DiagramControlRect a, P.DiagramControlRect b) =>
        a.X + a.Width <= b.X || b.X + b.Width <= a.X || a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y;

    /// <summary>Design QA round 2, R2-P2-1 and R2-P2-8, measured: every connection named in <paramref name="connections"/>
    /// (id to caption) shows exactly one caption, drawn inside the canvas in a clear place, and its text's ink lies where the
    /// editor reports it. Each caption in <paramref name="whole"/> is drawn whole; any other is whole or, only when no clear place
    /// holds its whole text, shortened with "…" and naming its whole caption on hover. No caption covers a block, a port or
    /// another caption (<see cref="VerifyCanvasText"/>).</summary>
    private static void VerifyCaptions(CapturedWindow shot, P.RecursiveDiagramEditorState at, string step, IReadOnlyDictionary<string, string> connections,
        params string[] whole)
    {
        var canvas = shot.At(at.CanvasWindowX + (int)at.CanvasPixelWidth - 12, at.CanvasWindowY + (int)at.CanvasPixelHeight - 12);
        foreach (var (id, name) in connections)
        {
            var drawn = at.ConnectionCaptions.Where(c => c.ObjectId == id).ToArray();
            Assert.HasCount(1, drawn, $"{step}: the connection {name} shows one caption.");
            var caption = drawn[0];
            Assert.IsTrue(caption.Shown, $"{step}: the caption of {name} is drawn inside the canvas.");
            Assert.IsTrue(caption.Enabled, $"{step}: the caption of {name} has a clear place.");
            if (caption.Label == name)
                Assert.AreEqual("", caption.Tooltip, $"{step}: the whole caption {name} needs no tooltip.");
            else
            {
                Assert.IsFalse(whole.Contains(name), $"{step}: the caption {name} is drawn whole, not as '{caption.Label}'.");
                Assert.IsTrue(caption.Label.EndsWith('…') && caption.Label.Length >= 5 && name.StartsWith(caption.Label[..^1].TrimEnd(), StringComparison.Ordinal),
                    $"{step}: the caption of {name} is whole or shortened with an ellipsis, not '{caption.Label}'.");
                Assert.AreEqual(name, caption.Tooltip, $"{step}: the shortened caption '{caption.Label}' shows {name} on hover.");
            }
            var ink = shot.InkRuns(caption.X, caption.Y, caption.Width, caption.Height, canvas, rows: false, minimum: 2.0);
            Assert.IsTrue(ink.Count >= 2 && ink[^1].Last - ink[0].First + 1 >= caption.Width / 2,
                $"{step}: the caption '{caption.Label}' ({caption.X}, {caption.Y}, {caption.Width} x {caption.Height}) is drawn where it is reported.");
        }
        VerifyCanvasText(at, step);
    }

    /// <summary>Design QA round 2, R2-P2-4, measured in one capture: each chosen one-click option (a connection's direction,
    /// domain or type) has the drawing tools' accent checked style: an accent border 3:1 or more from the inspector and from its
    /// own pale accent tile, and an accent label 4.5:1 or more on the tile; each option that is not chosen has a label and a
    /// border coloured apart from those. Assistive technology reads every option as a toggle button named by its label and
    /// checked exactly while chosen.</summary>
    private static void VerifyChoiceStyling(CapturedWindow shot, P.RecursiveDiagramEditorState at, string step, string[] chosen, string[] idle)
    {
        P.DiagramControlRect Find(string name) => at.Controls.Single(c => c.Name == "Recursive" + name);
        string D(ValueTuple<byte, byte, byte> c) => CapturedWindow.Describe(c);
        var inspector = at.Controls.Single(c => c.Name == "RecursiveInspector");
        (byte, byte, byte) Surface(P.DiagramControlRect c) => shot.At(inspector.X + 4, c.Y + c.Height / 2);
        (byte, byte, byte) Label(P.DiagramControlRect c, (byte, byte, byte) behind) => shot.MostContrasting(c.X + 8, c.Y + 5, c.Width - 16, c.Height - 10, behind);
        // Review of design QA round 2: outside its rounded tile an option shows the inspector's own surface, not a box drawn in
        // the colour wxWidgets reports for the inspector.
        void VerifyCorner(P.DiagramControlRect c, string name, (byte, byte, byte) surface) =>
            Assert.IsTrue(CapturedWindow.Distance(shot.At(c.X, c.Y), surface) <= 3,
                $"{step}: {name}'s corner {D(shot.At(c.X, c.Y))} shows the inspector's surface {D(surface)}.");
        var chosenLabels = new List<(byte, byte, byte)>(); var chosenBorders = new List<(byte, byte, byte)>();
        foreach (string name in chosen)
        {
            var choice = Find(name);
            Assert.IsTrue(choice.Shown && choice.Active, $"{step}: {name} is shown as chosen.");
            VerifyAccessible(choice, "toggle button", step);
            Assert.IsTrue(choice.Accessible.Checked, $"{step}: assistive technology reads {name} as checked.");
            var surface = Surface(choice); var tile = shot.At(choice.X + 4, choice.Y + choice.Height / 2);
            VerifyCorner(choice, name, surface);
            var border = shot.MostContrasting(choice.X, choice.Y + choice.Height / 3, 3, choice.Height / 3, tile);
            Assert.IsTrue(shot.Contrast("R2-P2-4 chosen option border on the inspector", border, surface) >= 3.0,
                $"{step}: {name}'s border {D(border)} stands 3:1 from the inspector {D(surface)}.");
            Assert.IsTrue(shot.Contrast("R2-P2-4 chosen option border on its tile", border, tile) >= 3.0,
                $"{step}: {name}'s border {D(border)} stands 3:1 from its tile {D(tile)}.");
            var label = Label(choice, tile);
            Assert.IsTrue(shot.Contrast("R2-P2-4 chosen option label on its tile", label, tile) >= 4.5,
                $"{step}: {name}'s label {D(label)} reads 4.5:1 on its tile {D(tile)}.");
            chosenLabels.Add(label); chosenBorders.Add(border);
        }
        foreach (string name in idle)
        {
            var choice = Find(name);
            Assert.IsTrue(choice.Shown && !choice.Active, $"{step}: {name} is shown and not chosen.");
            VerifyAccessible(choice, "toggle button", step);
            Assert.IsFalse(choice.Accessible.Checked, $"{step}: assistive technology reads {name} as not checked.");
            var tile = shot.At(choice.X + 4, choice.Y + choice.Height / 2);
            VerifyCorner(choice, name, Surface(choice));
            var label = Label(choice, tile);
            var border = shot.MostContrasting(choice.X, choice.Y + choice.Height / 3, 3, choice.Height / 3, tile);
            Assert.IsTrue(chosenLabels.All(c => CapturedWindow.Distance(c, label) >= 60),
                $"{step}: {name}'s label {D(label)} is coloured apart from a chosen option's {D(chosenLabels[0])}.");
            Assert.IsTrue(chosenBorders.All(c => CapturedWindow.Distance(c, border) >= 60),
                $"{step}: {name}'s border {D(border)} is coloured apart from a chosen option's {D(chosenBorders[0])}.");
        }
    }

    /// <summary>Design QA round 2, R2-P2-5, measured in one capture: the action that removes the whole Signals detail is a link
    /// that says so ("Remove signals"), drawn as a link and at least 8 pixels above the first signal's own "×"; each signal's
    /// "×" is named after the signal it removes, on hover and for assistive technology, and its glyph is a quarter of the link's
    /// width or less, so the two cannot be taken for each other.</summary>
    private static void VerifySignalRemoves(CapturedWindow shot, P.RecursiveDiagramEditorState at, string step, params string[] signals)
    {
        P.DiagramControlRect Find(string name) => at.Controls.Single(c => c.Name == name);
        var removeAll = Find("RecursiveDetailRemoveSignals");
        Assert.IsTrue(removeAll.Shown, $"{step}: \"Remove signals\" is shown.");
        Assert.AreEqual("Remove signals", removeAll.Label, $"{step}: the whole detail's remove action says what it removes.");
        VerifyLink(shot, removeAll, Find("RecursiveSavedVersion"), step, "Remove signals");
        var page = shot.At(Find("RecursiveInspector").X + 3, removeAll.Y + removeAll.Height / 2);
        var linkInk = shot.InkRuns(removeAll.X, removeAll.Y, removeAll.Width, removeAll.Height, page, rows: false, minimum: 2.0);
        Assert.IsNotEmpty(linkInk, $"{step}: \"Remove signals\" is drawn where it is reported.");
        int linkWidth = linkInk[^1].Last - linkInk[0].First + 1;
        for (int index = 0; index < signals.Length; ++index)
        {
            string name = signals[index];
            var one = Find("RecursiveSignalRemove" + index);
            Assert.AreEqual("×", one.Label, $"{step}: {name}'s own remove button is its \"×\".");
            Assert.AreEqual("Remove signal " + name, one.Tooltip, $"{step}: {name}'s own remove button names its signal on hover.");
            Assert.AreEqual("Remove signal " + name, one.Accessible?.Name, $"{step}: assistive technology names {name}'s own remove button after its signal.");
            var ink = shot.InkRuns(one.X, one.Y, one.Width, one.Height, shot.At(one.X + 1, one.Y + 1), rows: false, minimum: 2.0);
            int glyphWidth = ink.Count == 0 ? 0 : ink[^1].Last - ink[0].First + 1;
            Assert.IsTrue(glyphWidth > 0 && glyphWidth * 4 <= linkWidth,
                $"{step}: {name}'s \"×\" ({glyphWidth} pixels of ink) looks nothing like the {linkWidth}-pixel \"Remove signals\" link.");
        }
        var first = Find("RecursiveSignalRemove0");
        int apart = shot.Record("R2-P2-5 Remove signals above the first signal's remove (px)", first.Y - (removeAll.Y + removeAll.Height));
        Assert.IsTrue(apart >= 8, $"{step}: \"Remove signals\" is {apart} pixels above the first signal's own \"×\", at least the 8 the design QA asks for.");
    }

    /// <summary>Design QA round 2, R2-P2-2, measured: the Connect preview leaves the port it starts from outward (to the right, off
    /// the block's right edge) for 8 pixels or more, runs through no block and across no boundary port's name, and ends on the
    /// port under the pointer. Inside the source block, where the earlier preview struck through the port's name, no dash is
    /// drawn; along the preview's longest run, its accent dash is. Only the preview is measured here: a finished connection
    /// whose target lies behind its source still takes rule F4's three-segment path through the block, a difference that
    /// awaits a contract decision of the integration owner.</summary>
    private static void VerifyConnectPreview(CapturedWindow shot, P.RecursiveDiagramEditorState at, string step, string sourceBlock, string sourcePort, string targetPort)
    {
        var points = at.ConnectPreview.Select(p => (X: p.X, Y: p.Y)).ToArray();
        Assert.IsTrue(points.Length >= 2, step + ": the preview is reported as drawn.");
        string Describe() => string.Join(" ", points.Select(p => p.X + "," + p.Y));
        (int X, int Y) Centre(P.DiagramControlRect mark) => (mark.X + mark.Width / 2, mark.Y + mark.Height / 2);
        var source = Centre(at.PortMarks.Single(p => p.Label == sourcePort)); var target = Centre(at.PortMarks.Single(p => p.Label == targetPort));
        Assert.IsTrue(Math.Abs(points[0].X - source.X) <= 1 && Math.Abs(points[0].Y - source.Y) <= 1, $"{step}: the preview {Describe()} starts on {sourcePort} at {source}.");
        Assert.IsTrue(Math.Abs(points[^1].X - target.X) <= 1 && Math.Abs(points[^1].Y - target.Y) <= 1, $"{step}: the preview {Describe()} ends on {targetPort} at {target}.");
        Assert.IsTrue(points[1].Y == points[0].Y && shot.Record("R2-P2-2 preview leaves its port outward (px)", points[1].X - points[0].X) >= 8,
            $"{step}: the preview {Describe()} leaves {sourcePort} outward, to the right, for 8 pixels or more.");
        foreach (var text in at.BlockTexts)
        {
            var b = text.Block;
            for (int i = 1; i < points.Length; ++i)
            {
                int left = Math.Min(points[i - 1].X, points[i].X), right = Math.Max(points[i - 1].X, points[i].X);
                int top = Math.Min(points[i - 1].Y, points[i].Y), bottom = Math.Max(points[i - 1].Y, points[i].Y);
                Assert.IsTrue(right <= b.X || left >= b.X + b.Width - 1 || bottom <= b.Y || top >= b.Y + b.Height - 1,
                    $"{step}: the preview {Describe()} runs through no block; its run {points[i - 1]} to {points[i]} crosses ({b.X}, {b.Y}, {b.Width} x {b.Height}).");
            }
        }
        foreach (var name in at.BoundaryPortNames)
            for (int i = 1; i < points.Length; ++i)
            {
                int left = Math.Min(points[i - 1].X, points[i].X), right = Math.Max(points[i - 1].X, points[i].X);
                int top = Math.Min(points[i - 1].Y, points[i].Y), bottom = Math.Max(points[i - 1].Y, points[i].Y);
                Assert.IsTrue(right < name.X || left > name.X + name.Width || bottom < name.Y || top > name.Y + name.Height,
                    $"{step}: the preview {Describe()} crosses no port's name ({name.Label}).");
            }
        var texts = at.BlockTexts.Single(b => b.BlockId == sourceBlock);
        var block = texts.Block;
        var fill = shot.At(block.X + block.Width / 2, block.Y + block.Height - 12);
        // From just right of the block's caption to well left of the port's own name.
        int stripFrom = texts.Caption is { } blockCaption ? blockCaption.X + blockCaption.Width + 6 : block.X + 12;
        var inside = shot.MostContrasting(stripFrom, source.Y - 1, Math.Max(1, source.X - 60 - stripFrom), 3, fill);
        Assert.IsTrue(shot.Contrast("R2-P2-2 ink inside the source block on its port's height", inside, fill) < 1.5,
            $"{step}: nothing is drawn through the source block at its port's height ({CapturedWindow.Describe(inside)} on {CapturedWindow.Describe(fill)}).");
        var canvas = shot.At(at.CanvasWindowX + (int)at.CanvasPixelWidth - 12, at.CanvasWindowY + (int)at.CanvasPixelHeight - 12);
        int longest = 1;
        for (int i = 2; i < points.Length; ++i)
            if (Math.Abs(points[i].X - points[i - 1].X) + Math.Abs(points[i].Y - points[i - 1].Y)
                > Math.Abs(points[longest].X - points[longest - 1].X) + Math.Abs(points[longest].Y - points[longest - 1].Y)) longest = i;
        var (mx, my) = ((points[longest].X + points[longest - 1].X) / 2, (points[longest].Y + points[longest - 1].Y) / 2);
        var dash = points[longest].Y == points[longest - 1].Y ? shot.MostContrasting(mx - 8, my - 1, 16, 3, canvas) : shot.MostContrasting(mx - 1, my - 8, 3, 16, canvas);
        Assert.IsTrue(shot.Contrast("R2-P2-2 preview on the canvas", dash, canvas) >= 3.0,
            $"{step}: the preview is drawn along its longest run in the accent {CapturedWindow.Describe(dash)}.");
        Assert.IsNotNull(at.ConnectHint, step + ": the hint is drawn.");
        Assert.IsTrue(at.BlockTexts.All(b => RectsApart(at.ConnectHint, b.Block)), step + ": the hint covers no block (design QA round 2, P3 1).");
    }

    /// <summary>Design QA P2-7: text is drawn whole inside its block, and each shown connection caption keeps clear of every
    /// block (by the handle size plus 4 pixels), every port and every other caption, and does not cross the level's dashed
    /// boundary.</summary>
    private static void VerifyCanvasText(P.RecursiveDiagramEditorState at, string step)
    {
        static bool Inside(P.DiagramControlRect inner, int x, int y, int right, int bottom) =>
            inner.X >= x && inner.Y >= y && inner.X + inner.Width <= right && inner.Y + inner.Height <= bottom;
        static bool Apart(P.DiagramControlRect a, int x, int y, int right, int bottom) =>
            a.X + a.Width <= x || right <= a.X || a.Y + a.Height <= y || bottom <= a.Y;
        foreach (var text in at.BlockTexts)
        {
            var b = text.Block;
            if (text.Caption is { } caption)
                Assert.IsTrue(Inside(caption, b.X + 8, b.Y + 8, b.X + b.Width - 8, b.Y + b.Height - 8), $"{step}: the caption '{caption.Label}' is drawn whole inside its block.");
            if (text.VersionLine is { } version)
                Assert.IsTrue(Inside(version, b.X + 8, b.Y + 8, b.X + b.Width - 8, b.Y + b.Height - 8), $"{step}: the version line '{version.Label}' is drawn whole inside its block.");
        }
        var shown = at.ConnectionCaptions.Where(c => c.Shown).ToArray();
        foreach (var caption in shown)
        {
            foreach (var text in at.BlockTexts)
                Assert.IsTrue(Apart(caption, text.Block.X - 8, text.Block.Y - 8, text.Block.X + text.Block.Width + 8, text.Block.Y + text.Block.Height + 8),
                    $"{step}: the caption '{caption.Label}' keeps clear of a block and its handles.");
            foreach (var port in at.PortMarks)
                Assert.IsTrue(Apart(caption, port.X, port.Y, port.X + port.Width, port.Y + port.Height), $"{step}: the caption '{caption.Label}' keeps clear of a port.");
            foreach (var other in shown.Where(o => !ReferenceEquals(o, caption)))
                Assert.IsTrue(Apart(caption, other.X, other.Y, other.X + other.Width, other.Y + other.Height), $"{step}: the captions '{caption.Label}' and '{other.Label}' do not overlap.");
            // Review of design QA round 2: no caption strikes through the level's dashed boundary. It lies wholly inside the frame
            // or wholly outside it, as a connection to a boundary port may be captioned beside the port's own name.
            if (at.LevelFrame is { } frame)
                Assert.IsTrue(Apart(caption, frame.X, frame.Y, frame.X + frame.Width, frame.Y + frame.Height)
                    || Inside(caption, frame.X + 1, frame.Y + 1, frame.X + frame.Width - 1, frame.Y + frame.Height - 1),
                    $"{step}: the caption '{caption.Label}' ({caption.X}, {caption.Y}, {caption.Width} x {caption.Height}) lies wholly inside or outside the level's boundary ({frame.X}, {frame.Y}, {frame.Width} x {frame.Height}).");
        }
    }

    /// <summary>The editor window's top-left corner on the fixture display, so control rectangles (reported relative to the
    /// window) can be found in a capture of the whole display.</summary>
    private static (int X, int Y) WindowOrigin(string display, int processId, string title)
    {
        (int X, int Y) origin = (0, 0);
        NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, observeGeometry: geometry => origin = (geometry.X, geometry.Y));
        return origin;
    }

    /// <summary>One capture of the fixture display as RGB pixels, for measured design checks (design QA of the per-level
    /// editor): WCAG contrast of what the editor actually drew, and where its ink lies. Coordinates are window-relative.</summary>
    internal sealed class CapturedWindow
    {
        private const int DisplayWidth = 1600, DisplayHeight = 1150;
        private readonly byte[] pixels;
        private readonly (int X, int Y) origin;
        private readonly string name, log;
        private CapturedWindow(byte[] pixels, (int X, int Y) origin, string name, string log) { this.pixels = pixels; this.origin = origin; this.name = name; this.log = log; }

        /// <summary>Loads a capture; every contrast measured through <see cref="Contrast(string, ValueTuple{byte, byte, byte}, ValueTuple{byte, byte, byte})"/>
        /// is appended to the evidence file beside it (<c>*-design-measurements.tsv</c>), so the design record cites the measured values.</summary>
        public static async Task<CapturedWindow> LoadAsync(string path, (int X, int Y) origin, CancellationToken token)
        {
            var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-nostdin", "-loglevel", "error", "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            using var pixels = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(pixels, token); var diagnostics = process.StandardError.ReadToEndAsync(token);
            try
            {
                await process.WaitForExitAsync(token); await copy; Assert.AreEqual(0, process.ExitCode, await diagnostics);
                byte[] image = pixels.ToArray(); Assert.AreEqual(DisplayWidth * DisplayHeight * 3, image.Length, "The capture covers the whole fixture display.");
                string file = Path.GetFileNameWithoutExtension(path);
                string instance = file.Length > 36 ? file[..36] : file;
                return new CapturedWindow(image, origin, file, Path.Combine(Path.GetDirectoryName(path)!, instance + "-design-measurements.tsv"));
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }

        public (byte R, byte G, byte B) At(int x, int y)
        {
            int px = Math.Clamp(origin.X + x, 0, DisplayWidth - 1), py = Math.Clamp(origin.Y + y, 0, DisplayHeight - 1);
            int i = (py * DisplayWidth + px) * 3;
            return (pixels[i], pixels[i + 1], pixels[i + 2]);
        }

        /// <summary>The pixel in the rectangle that contrasts most with a reference colour: the ink of a line, glyph or text
        /// drawn there, whatever the antialiasing around it.</summary>
        public (byte R, byte G, byte B) MostContrasting(int x, int y, int width, int height, (byte R, byte G, byte B) against)
        {
            var best = At(x, y); double bestContrast = 0;
            for (int j = y; j < y + height; ++j)
                for (int i = x; i < x + width; ++i)
                {
                    var here = At(i, j); double c = Contrast(here, against);
                    if (c > bestContrast) { bestContrast = c; best = here; }
                }
            return best;
        }

        /// <summary>The rows (or columns) of the rectangle holding a pixel at least minimum:1 from the reference colour,
        /// grouped into runs.</summary>
        public List<(int First, int Last)> InkRuns(int x, int y, int width, int height, (byte R, byte G, byte B) against, bool rows, double minimum = 1.6)
        {
            var runs = new List<(int First, int Last)>();
            int outer = rows ? height : width, inner = rows ? width : height;
            for (int a = 0; a < outer; ++a)
            {
                bool ink = false;
                for (int b = 0; b < inner && !ink; ++b)
                    ink = Contrast(rows ? At(x + b, y + a) : At(x + a, y + b), against) >= minimum;
                if (!ink) continue;
                int at = (rows ? y : x) + a;
                if (runs.Count > 0 && runs[^1].Last == at - 1) runs[^1] = (runs[^1].First, at);
                else runs.Add((at, at));
            }
            return runs;
        }

        /// <summary>The contrast of two measured colours, recorded with what was measured.</summary>
        public double Contrast(string what, (byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
        {
            double contrast = Contrast(a, b);
            File.AppendAllText(log, $"{name}\t{what}\t{Describe(a)}\t{Describe(b)}\t{contrast:F2}\n");
            return contrast;
        }

        /// <summary>A measured distance in pixels, recorded with what was measured.</summary>
        public int Record(string what, int pixels)
        {
            File.AppendAllText(log, $"{name}\t{what}\t\t\t{pixels}\n");
            return pixels;
        }

        public static double Contrast((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
        {
            static double Channel(byte value) { double v = value / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            static double Luminance((byte R, byte G, byte B) c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        public static int Distance((byte R, byte G, byte B) a, (byte R, byte G, byte B) b) =>
            Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        public static string Describe((byte R, byte G, byte B) c) => $"rgb({c.R},{c.G},{c.B})";
    }
}
