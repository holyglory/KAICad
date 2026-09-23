using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Kiapi.Common.Types;
using ModelContextProtocol.Client;
using P = KiCad.Automation.Protocol.Structural;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyStructuralEditor(NativeClient native, DocumentSpecifier document, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        var (circuit, structure) = StructuralDiagramTests.Fixture();
        Guid mcu = structure.Blocks[0].Id, memory = structure.Blocks[1].Id, power = Guid.NewGuid();
        Guid pwr1 = Guid.NewGuid(), pwr2 = Guid.NewGuid(), mcuPower = Guid.NewGuid(), memoryPower = Guid.NewGuid();
        structure = structure with
        {
            Blocks = [structure.Blocks[0] with { Name = "MCU", Purpose = "Main controller handling system logic and interfaces.", ComponentIds = [] },
                structure.Blocks[1] with { Name = "Memory", ComponentIds = [] }, new(power, "Power", null, [])],
            Ports = [structure.Ports[0] with { Name = "D0" }, structure.Ports[1] with { Name = "D0" },
                new(pwr1, power, "PWR1"), new(pwr2, power, "PWR2"), new(mcuPower, mcu, "Power"), new(memoryPower, memory, "Power")],
            Connections = [structure.Connections[0] with { Kind = StructuralConnectionKind.Data, Description = "Data", NetIds = [], Direction = StructuralConnectionDirection.Bidirectional },
                new(Guid.NewGuid(), pwr1, mcuPower, StructuralConnectionKind.Power, "Power", [], StructuralConnectionDirection.FirstToSecond),
                new(Guid.NewGuid(), pwr2, memoryPower, StructuralConnectionKind.Power, "Power", [], StructuralConnectionDirection.FirstToSecond)],
            Statements = [new(Guid.NewGuid(), mcu, EngineeringStatementRole.Intent, GuidanceStrength.Preference,
                "Place near the heat sink.", null, [], [new("user requirement", "fixture-v1", null, null, null)])],
            Presentation = new([new(mcu, 25_400_000, 25_400_000, 20_320_000, 15_240_000, false, 0xdcecff),
                new(memory, 81_280_000, 25_400_000, 20_320_000, 15_240_000, false, 0xf0f2e8),
                new(power, 50_800_000, 60_960_000, 25_400_000, 12_700_000, false, 0xf9e3e4)],
                [new(structure.Ports[0].Id, StructuralPortSide.Right, 5_080_000), new(structure.Ports[1].Id, StructuralPortSide.Left, 5_080_000),
                new(pwr1, StructuralPortSide.Top, 5_080_000), new(pwr2, StructuralPortSide.Top, 20_320_000),
                new(mcuPower, StructuralPortSide.Bottom, 10_160_000), new(memoryPower, StructuralPortSide.Bottom, 10_160_000)], [])
        };
        string project = Path.GetDirectoryName((await native.HandshakeAsync(token)).ProjectPath)!;
        string source = Path.Combine(project, "Controller.xml");
        await File.WriteAllTextAsync(source, EngineeringDesignXml.Write(new(circuit, structure, [], []), []), token);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string state = Directory.CreateTempSubdirectory("kicad-structural-mcp-").FullName;
        try
        {
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet", Arguments = [Path.Combine(FindRoot(), "automation", "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll")],
                EnvironmentVariables = new Dictionary<string, string?> { ["KICAD_AUTOMATION_STATE_DIRECTORY"] = state }
            }), cancellationToken: token);
            var attach = await client.CallToolAsync("kicad_instance_attach", new Dictionary<string, object?>
                { ["endpoint"] = native.Endpoint, ["expectedInstanceId"] = instanceId }, cancellationToken: token);
            Assert.IsFalse(attach.IsError == true);
            var opened = await client.CallToolAsync("kicad_structure_open", new Dictionary<string, object?>
                { ["instanceId"] = instanceId, ["repositoryRoot"] = project, ["sourcePath"] = source }, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-structural-open.json"), JsonSerializer.Serialize(opened), token);
            Assert.IsFalse(opened.IsError == true);
            async Task<P.StructuralEditorState> Read() => await native.InvokeAsync<P.ReadStructuralEditor, P.StructuralEditorState>(
                new() { DocumentId = structure.Id.ToString("D") }, token);
            var admitted = await Read();
            foreach (Action<P.StructuralDiagramData> corrupt in new Action<P.StructuralDiagramData>[]
            {
                d => d.Blocks[0].ParentId = d.Blocks[0].Id,
                d => d.Blocks[0].ParentId = Guid.NewGuid().ToString("D"),
                d => d.Blocks.Add(d.Blocks[0].Clone()),
                d => d.Ports[0].BlockId = Guid.NewGuid().ToString("D"),
                d => d.Connections[0].Direction = (P.StructuralLinkDirection)999,
                d => d.Presentation.Blocks[0].WidthNm = -100,
                d => d.Presentation.Blocks[0].Position.XNm = long.MaxValue,
                d => d.Properties.Add(new P.StructuralPropertyData { Id = Guid.NewGuid().ToString("D"), OwnerId = d.Blocks[0].Id,
                    Key = "Invalid", Text = "Invalid enum fixture", Strength = (P.StructuralGuidanceStrength)999 }),
                d => d.Properties.Add(new P.StructuralPropertyData { Id = Guid.NewGuid().ToString("D"), OwnerId = d.Blocks[0].Id,
                    Key = "Invalid", Text = "Invalid quantity fixture", Strength = P.StructuralGuidanceStrength.SgsInformation,
                    Quantity = new() { Kind = (P.StructuralParameterKind)999, Unit = "V", Nominal = "3.3" } }),
            })
            {
                var malformed = admitted.Document.Clone(); corrupt(malformed.Diagram);
                await Assert.ThrowsExactlyAsync<NativeApiException>(() => native.InvokeAsync<P.OpenStructuralEditor, P.StructuralEditorState>(
                    new() { SchemaVersion = 1, Document = malformed, RepositoryRoot = project,
                        HelperPath = Path.Combine(FindRoot(), "automation", "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll") }, token));
                Assert.AreEqual(admitted.Document, (await Read()).Document, "Rejected input must leave the open draft unchanged.");
            }
            async Task<P.StructuralEditorState> Wait(Func<P.StructuralEditorState, bool> predicate)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
                while (true)
                {
                    var current = await Read(); if (predicate(current)) return current;
                    try { await Task.Delay(50, deadline.Token); }
                    catch (OperationCanceledException)
                    {
                        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-structural-timeout-state.json"), SchematicJson.Formatter.Format(current), token);
                        await CaptureStructural(display, Path.Combine(evidence, instanceId + "-structural-timeout.png"), token);
                        throw;
                    }
                }
            }
            void Key(string key, bool control = false, int? x = null, int? y = null, string title = "Structure") =>
                NativeKeyboard.SchematicShortcut(display, processId, key, title, control, focusCanvas: x.HasValue,
                    clickFromLeft: x, clickFromTop: y);
            void Type(string text, string title = "Structure") { foreach (char character in text) Key(character.ToString(), title: title); }
            async Task Window(string title, bool visible = true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
                try { while (NativeKeyboard.HasWindow(display, processId, title) != visible) await Task.Delay(50, deadline.Token); }
                catch (OperationCanceledException)
                {
                    await CaptureStructural(display, Path.Combine(evidence, instanceId + "-structural-dialog-timeout.png"), token);
                    await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-structural-dialog-timeout-state.json"), SchematicJson.Formatter.Format(await Read()), token);
                    throw;
                }
            }
            void AddProperty()
            {
                // Native mnemonic stays usable as the property list grows.
                NativeKeyboard.SchematicShortcut(display, processId, "a", "Structure", false, false, altKey: true);
            }
            async Task Save()
            {
                ulong previous = (await Read()).CompletedSaveCount;
                Key("s", true);
                var completed = await Wait(s => s.CompletedSaveCount > previous && !s.Saving);
                Assert.AreEqual("", completed.LastSaveError);
            }
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(token); ready.CancelAfter(TimeSpan.FromSeconds(15));
            while (!(await Read()).Rendered) await Task.Delay(50, ready.Token);
            Assert.IsTrue(NativeKeyboard.HasWindow(display, processId, "Structure"));
            Key("click", x: 640, y: 400);
            await CaptureStructural(display, Path.Combine(evidence, instanceId + "-structural-initial.png"), token);
            // Observe completed saves, not a transient false Saving value before
            // the input event reaches the GUI loop.
            string bytes = await File.ReadAllTextAsync(source, token);
            await Save();
            Assert.AreEqual(bytes, await File.ReadAllTextAsync(source, token));
            Assert.IsFalse((await Read()).Dirty);

            // Select and type through real native widgets; read IPC only to
            // observe results, never to synthesize a successful UI mutation.
            Key("click", x: 180, y: 160); Key("a", true); Type("Control unit"); Key("Return");
            var renamed = await Wait(s => s.Document.Diagram.Blocks.Any(b => b.Id == mcu.ToString("D") && b.Name == "Control unit"));
            Assert.IsTrue(renamed.Dirty);
            await Save();
            var savedDesign = EngineeringDesignXml.Read(await File.ReadAllTextAsync(source, token), []);
            Assert.AreEqual("Control unit", savedDesign.Structure!.Blocks.Single(b => b.Id == mcu).Name);
            Assert.AreEqual(CircuitXml.Write(circuit), CircuitXml.Write(savedDesign.Circuit));
            Key("click", x: 1050, y: 960); Key("z", true);
            await Wait(s => s.Document.Diagram.Blocks.Single(b => b.Id == mcu.ToString("D")).Name == "MCU");
            Key("y", true);
            await Wait(s => s.Document.Diagram.Blocks.Single(b => b.Id == mcu.ToString("D")).Name == "Control unit");

            var layoutBefore = (await Read()).Document.Diagram.Presentation.Blocks.Single(b => b.BlockId == mcu.ToString("D")).Clone();
            var connectionsBefore = (await Read()).Document.Diagram.Connections.Select(c => c.Clone()).ToArray();
            NativeKeyboard.SchematicShortcut(display, processId, "drag", "Structure", false, false,
                clickFromLeft: 650, clickFromTop: 400, dragToLeft: 680, dragToTop: 430);
            var dragged = await Wait(s => !s.Document.Diagram.Presentation.Blocks.Single(b => b.BlockId == mcu.ToString("D")).Position.Equals(layoutBefore.Position));
            CollectionAssert.AreEqual(connectionsBefore, dragged.Document.Diagram.Connections.ToArray());
            Key("z", true);
            await Wait(s => s.Document.Diagram.Presentation.Blocks.Single(b => b.BlockId == mcu.ToString("D")).Equals(layoutBefore));
            // The selected lower-right handle is rendered in the approved
            // fixed-size fixture. Dragging it changes size, not connectivity.
            NativeKeyboard.SchematicShortcut(display, processId, "drag", "Structure", false, false,
                clickFromLeft: 759, clickFromTop: 473, dragToLeft: 789, dragToTop: 503);
            var resized = await Wait(s => s.Document.Diagram.Presentation.Blocks.Single(b => b.BlockId == mcu.ToString("D")).WidthNm > layoutBefore.WidthNm);
            CollectionAssert.AreEqual(connectionsBefore, resized.Document.Diagram.Connections.ToArray());
            Key("z", true);
            await Wait(s => s.Document.Diagram.Presentation.Blocks.Single(b => b.BlockId == mcu.ToString("D")).Equals(layoutBefore));

            // Add a block using its toolbar action and canvas location. Escape
            // cancels the next placement without inventing another block.
            int originalCount = (await Read()).Document.Diagram.Blocks.Count;
            Key("click", x: 334, y: 48); Key("click", x: 960, y: 840);
            await Wait(s => s.Document.Diagram.Blocks.Count == originalCount + 1);
            Key("click", x: 334, y: 48); Key("Escape"); Key("click", x: 1200, y: 950);
            Assert.AreEqual(originalCount + 1, (await Read()).Document.Diagram.Blocks.Count);

            int linkCount = (await Read()).Document.Diagram.Connections.Count;
            Key("click", x: 428, y: 48); Key("click", x: 640, y: 400); Key("click", x: 1040, y: 900);
            var connected = await Wait(s => s.Document.Diagram.Connections.Count == linkCount + 1);
            Assert.AreEqual(structure.Ports.Count + 2, connected.Document.Diagram.Ports.Count);
            Key("z", true);
            await Wait(s => s.Document.Diagram.Connections.Count == linkCount && s.Document.Diagram.Ports.Count == structure.Ports.Count);

            Key("click", x: 640, y: 400);
            int instructionCount = (await Read()).Document.Diagram.Statements.Count;
            Key("click", x: 539, y: 48); Type("Keep this block near the heat sink.");
            await Save();
            await Wait(s => s.Document.Diagram.Statements.Count == instructionCount + 1
                && s.Document.Diagram.Statements.Any(t => t.TargetId == mcu.ToString("D") && t.Text == "Keep this block near the heat sink."));
            Key("click", x: 1050, y: 960); Key("z", true); Key("z", true);
            await Wait(s => s.Document.Diagram.Statements.Count == instructionCount);

            Key("click", x: 640, y: 400);
            await Wait(s => s.SelectedBlockId == mcu.ToString("D"));
            AddProperty();
            await Window("Add custom property");
            Type("Routing", "Add custom property"); Key("Tab", title: "Add custom property");
            Type("Prefer short connections.", "Add custom property");
            await CaptureStructural(display, Path.Combine(evidence, instanceId + "-structural-property-dialog.png"), token);
            // Invoke the visible OK button rather than assuming the next
            // tab stop is the affirmative button on every native platform.
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Add custom property", false, false,
                clickFromRight: 60, clickFromBottom: 25);
            await Window("Add custom property", false);
            await Wait(s => s.Document.Diagram.Properties.Any(p => p.OwnerId == mcu.ToString("D") && p.Key == "Routing" && p.Text == "Prefer short connections."));
            await Save();
            var withProperty = EngineeringDesignXml.Read(await File.ReadAllTextAsync(source, token), []);
            Assert.AreEqual("Prefer short connections.", withProperty.Structure.Properties!.Single().Statement.Text);
            AddProperty(); await Window("Add custom property");
            Type("Discard this", "Add custom property"); Key("Escape", title: "Add custom property");
            await Window("Add custom property", false);
            Assert.AreEqual(1, (await Read()).Document.Diagram.Properties.Count);
            Key("click", x: 1050, y: 960); Key("z", true);
            await Wait(s => s.Document.Diagram.Properties.Count == 0);

            await Save();
            string savedBytes = await File.ReadAllTextAsync(source, token);
            Key("z", true); await Wait(s => s.Document.Diagram.Blocks.Count == originalCount);

            // Dirty-close cancellation leaves the exact draft available.
            Key("w", true);
            using (var dialog = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                dialog.CancelAfter(TimeSpan.FromSeconds(10));
                while (!NativeKeyboard.HasWindow(display, processId, "Unsaved changes")) await Task.Delay(50, dialog.Token);
            }
            Key("Escape", title: "Unsaved changes");
            Assert.IsTrue((await Read()).Dirty);
            Assert.AreEqual(originalCount, (await Read()).Document.Diagram.Blocks.Count);

            // External modification must win neither by silent overwrite nor by
            // dropping the editor draft. Restore only our own fixture afterward.
            string externalBytes = savedBytes + "\n";
            await File.WriteAllTextAsync(source, externalBytes, token);
            ulong count = (await Read()).CompletedSaveCount;
            Key("s", true);
            var conflict = await Wait(s => s.CompletedSaveCount > count && !s.Saving);
            Assert.IsFalse(string.IsNullOrWhiteSpace(conflict.LastSaveError)); Assert.IsTrue(conflict.Dirty);
            Assert.AreEqual(externalBytes, await File.ReadAllTextAsync(source, token));
            Assert.AreEqual(originalCount, conflict.Document.Diagram.Blocks.Count);
            await File.WriteAllTextAsync(source, savedBytes, token);
            await Save(); Assert.IsFalse((await Read()).Dirty);
            await CaptureStructural(display, Path.Combine(evidence, instanceId + "-structural-edited.png"), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-structural-state.json"), SchematicJson.Formatter.Format(await Read()), token);
            var finalDiagram = (await Read()).Document.Diagram.Clone();
            Key("w", true);
            using (var closed = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                closed.CancelAfter(TimeSpan.FromSeconds(10));
                while (NativeKeyboard.HasWindow(display, processId, "Structure")) await Task.Delay(50, closed.Token);
            }
            var reopened = await client.CallToolAsync("kicad_structure_open", new Dictionary<string, object?>
                { ["instanceId"] = instanceId, ["repositoryRoot"] = project, ["sourcePath"] = source }, cancellationToken: token);
            Assert.IsFalse(reopened.IsError == true);
            Assert.AreEqual(finalDiagram, (await Wait(s => s.Rendered)).Document.Diagram);
            Assert.IsFalse((await Read()).Dirty);
        }
        finally { Directory.Delete(state, true); }
    }

    private static async Task CaptureStructural(string display, string path, CancellationToken token)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-nostdin", "-y", "-loglevel", "error", "-f", "x11grab", "-video_size", "1487x1058", "-i", display, "-frames:v", "1", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); Assert.AreEqual(0, process.ExitCode, await stderr); await stdout; }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
