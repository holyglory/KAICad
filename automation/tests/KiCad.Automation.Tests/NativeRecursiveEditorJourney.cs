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
            async Task<P.RecursiveDiagramEditorState> Save()
            {
                ulong previous = (await Read()).CompletedSaveCount; Key("s", control: true);
                var saved = await Wait(s => s.CompletedSaveCount > previous && !s.Busy);
                Assert.AreEqual("", saved.ErrorMessage); Assert.IsFalse(saved.Dirty); return saved;
            }
            var initial = await Wait(s => s.Ready && !s.Busy && s.Rendered);
            Assert.AreEqual(graph.SelectedRoot.BlockId.ToString("D"), initial.DiagramPath.Single().BlockId);
            Key("Escape"); Key("Right");
            await Wait(s => s.Draft.Baseline.BlockId == fixture.Blocks["PSU"].BlockId.ToString("D"));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-system.png"), token);
            Key("Return");
            await Wait(s => s.DiagramPath.Count == 2 && s.DiagramPath[^1].BlockId == fixture.Blocks["PSU"].BlockId.ToString("D"));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-psu.png"), token);
            Key("Escape"); Key("BackSpace"); await Wait(s => s.DiagramPath.Count == 1);
            Key("Right"); await Wait(s => s.Draft.Baseline.BlockId == fixture.Blocks["CPU"].BlockId.ToString("D"));
            Key("Return"); await Wait(s => s.DiagramPath.Count == 2 && s.DiagramPath[^1].BlockId == fixture.Blocks["CPU"].BlockId.ToString("D"));
            await CaptureRecursive(display, Path.Combine(evidence, instanceId + "-recursive-cpu.png"), token);
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
            Key("w", control: true);
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(token); closing.CancelAfter(TimeSpan.FromSeconds(15));
            while (NativeKeyboard.HasWindow(display, processId, "Structural diagram")) await Task.Delay(50, closing.Token);
            var reopened = await client.CallToolAsync("kicad_diagram_open", arguments, cancellationToken: token);
            Assert.IsFalse(reopened.IsError == true);
            await Wait(s => s.Ready && !s.Busy && s.Draft.Baseline.BlockId == file.SelectedRoot.BlockId.ToString("D"));
            Key("w", control: true);
        }
        finally { Directory.Delete(stateRoot, true); }
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
