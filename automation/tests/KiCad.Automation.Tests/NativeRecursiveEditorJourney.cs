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
            var savedRead = await client.CallToolAsync("kicad_diagram_read", arguments, cancellationToken: token);
            Assert.IsFalse(savedRead.IsError == true);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-saved-diagram.json"), JsonSerializer.Serialize(savedRead), token);
            var savedReadData = JsonSerializer.SerializeToElement(savedRead).GetProperty("structuredContent");
            Assert.AreEqual(graph.DocumentId.ToString("D"), savedReadData.GetProperty("documentId").GetString());
            Assert.AreEqual(2, savedReadData.GetProperty("children").GetArrayLength());
            Assert.AreEqual(2, savedReadData.GetProperty("connections").GetArrayLength());
            var invalidTargetArguments = new Dictionary<string, object?>(arguments) { ["blockId"] = graph.SelectedRoot.BlockId.ToString("D") };
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_read", invalidTargetArguments, cancellationToken: token)).IsError == true);
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
            async Task Window(string title, bool visible = true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                while (NativeKeyboard.HasWindow(display, processId, title) != visible) await Task.Delay(50, timeout.Token);
            }
            void Name(string value, string title)
            { Key("a", control: true, title: title); foreach (char character in value) Key(character.ToString(), title: title); Key("Return", title: title); }
            var initial = await Wait(s => s.Ready && !s.Busy && s.Rendered);
            Assert.AreEqual(graph.SelectedRoot.BlockId.ToString("D"), initial.DiagramPath.Single().BlockId);
            Assert.AreEqual(openingBytes, await File.ReadAllTextAsync(source, token));
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
            var copiedGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual(beforeManagementGraph.SelectedRoot, copiedGraph.SelectedRoot);
            Assert.AreEqual(sourceCpu, copiedGraph.States.Single(s => s.Id == copyState).ForkedFrom);
            Assert.AreEqual(beforeManagementGraph.Requirements(sourceCpu).Requirements,
                copiedGraph.Requirements(new(sourceCpu.BlockId, copyState, Guid.Parse(duplicated.Draft.Baseline.RevisionId))).Requirements);
            await ImplementationMenu(); Key("r"); await Window("Rename implementation");
            Name("Initial approach", "Rename implementation"); await Window("Invalid implementation name");
            Assert.AreEqual(RecursiveBlockGraphXml.Write(copiedGraph), await File.ReadAllTextAsync(source, token));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-implementation-name-error.png"), token);
            Key("Return", title: "Invalid implementation name"); await Window("Invalid implementation name", false); await Window("Rename implementation");
            Key("a", control: true, title: "Rename implementation"); Key("BackSpace", title: "Rename implementation"); Key("Return", title: "Rename implementation");
            await Window("Invalid implementation name");
            Assert.AreEqual(RecursiveBlockGraphXml.Write(copiedGraph), await File.ReadAllTextAsync(source, token));
            Key("Return", title: "Invalid implementation name"); await Window("Invalid implementation name", false); await Window("Rename implementation");
            Name("Thermal copy", "Rename implementation");
            await Wait(s => !s.Busy && s.ImplementationPreview && s.SourceToken != duplicated.SourceToken);
            var renamedGraph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.AreEqual("Thermal copy", renamedGraph.States.Single(s => s.Id == copyState).Name);
            Assert.AreEqual("Serviceable copy", renamedGraph.ImplementationChanges.Single(c => c.StateId == copyState).BeforeName);
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
            agentArguments["expectedInstanceEpoch"] = "wrong-epoch";
            Assert.IsTrue((await client.CallToolAsync("kicad_diagram_manage_implementation", agentArguments, cancellationToken: token)).IsError == true);
            Key("r", control: true); await Wait(s => !s.Busy && !s.Dirty && s.SourceToken == JsonSerializer.SerializeToElement(agentRestored).GetProperty("structuredContent").GetProperty("sourceToken").GetString());
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

    private static async Task VerifyExpandedCanvasContent(string path, CancellationToken token)
    {
        // This acceptance fixture has two visible peer blocks. Inspect only its
        // canvas, excluding inspector, toolbar, other windows and the cursor at Save.
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-nostdin", "-loglevel", "error", "-i", path, "-vf", "crop=1000:760:20:180",
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var pixels = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(pixels, token); var diagnostics = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token); await copy; Assert.AreEqual(0, process.ExitCode, await diagnostics);
            byte[] image = pixels.ToArray(); Assert.AreEqual(1000 * 760 * 3, image.Length);
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
}
