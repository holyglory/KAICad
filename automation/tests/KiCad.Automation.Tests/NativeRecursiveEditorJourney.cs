using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Client;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyRecursiveEditor(NativeClient native, int processId, string display,
        string evidence, string instanceId, CancellationToken token)
    {
        var fixture = LinkedDiagramFixture.Create(); var graph = fixture.Graph;
        string project = Path.GetDirectoryName((await native.HandshakeAsync(token)).ProjectPath)!;
        string source = Path.Combine(project, "system.design.xml");
        await File.WriteAllTextAsync(source, RecursiveBlockGraphXml.Write(graph), token);
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
            var arguments = new Dictionary<string, object?> { ["instanceId"] = instanceId, ["repositoryRoot"] = project,
                ["sourcePath"] = source, ["documentId"] = graph.DocumentId.ToString("D") };
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
            async Task Preview(bool last)
            {
                Key("i", control: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                int count = 0;
                do
                {
                    NativeKeyboard.SchematicShortcut(display, processId, "", "Structural diagram", false, false, observePopupCount: value => count = value);
                    if (count == 0) await Task.Delay(50, timeout.Token);
                } while (count == 0);
                Key(last ? "End" : "Home"); Key("Return");
            }
            var initial = await Wait(s => s.Ready && !s.Busy && s.Rendered);
            Assert.AreEqual(graph.SelectedRoot.BlockId.ToString("D"), initial.DiagramPath.Single().BlockId);
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
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromLeft: 750, clickFromTop: 570);
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
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true, clickFromLeft: 60, clickFromTop: 220);
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
            var multiple = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            var multipleCpu = multiple.Inspect(multiple.SelectedRoot).Children[1];
            Assert.AreEqual(4, multiple.Inspect(multipleCpu).LocalDiagram.Notes.Length);
            Assert.IsTrue(multiple.Inspect(multipleCpu).LocalDiagram.Notes.Any(n => n.Id == savedNote.Id && n.Text == savedNote.Text));
            Key("5", control: true); Key("Home"); Key("4", control: true); Key("a", control: true); Type("Unsaved first-comment edit.");
            await Wait(s => s.Dirty); Key("d", alt: true); await Wait(s => !s.Busy && !s.Dirty);
            Assert.AreEqual(RecursiveBlockGraphXml.Write(multiple), await File.ReadAllTextAsync(source, token));
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
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-compact.png"), token);
            Key("3", control: true); Key("a", control: true); Type("A compact-window routing edit."); await Wait(s => s.Dirty);
            await Save();
            Key("4", control: true);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-compact-comments.png"), token);
            ulong narrowView = (await Read()).ViewRevision;
            NativeKeyboard.SchematicShortcut(display, processId, "", "Structural diagram", false, false, resizeWidth: 1536, resizeHeight: 1024,
                observeGeometry: bounds => { Assert.AreEqual(1536, bounds.Width); Assert.AreEqual(1024, bounds.Height); });
            await Wait(s => s.Rendered && !s.Busy && s.ViewRevision > narrowView);
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-expanded.png"), token);
            Key("w", control: true);
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(token); closing.CancelAfter(TimeSpan.FromSeconds(15));
            while (NativeKeyboard.HasWindow(display, processId, "Structural diagram")) await Task.Delay(50, closing.Token);
            var reopened = await client.CallToolAsync("kicad_diagram_open", arguments, cancellationToken: token);
            Assert.IsFalse(reopened.IsError == true);
            await Wait(s => s.Ready && !s.Busy && s.Draft.Baseline.BlockId == file.SelectedRoot.BlockId.ToString("D"));
            Key("w", control: true);
        }
        finally { Directory.Delete(stateRoot, true); }
        if (interactionFailures.Count != 0) throw new AggregateException("Native input failures were preserved; the remaining safe editor journey was exercised.", interactionFailures);
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
}
