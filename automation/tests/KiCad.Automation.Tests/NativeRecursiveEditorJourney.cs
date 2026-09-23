using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Client;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // Shared PSU/CPU acceptance journey (psu-cpu-fixture-and-ownership.md §1.9).
    // Lane 2B replaces this body when it delivers the journey.
    private static Task VerifyPsuCpuDiagramCanvas(NativeClient client, PsuCpuNativeContext context, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
        => throw new AssertInconclusiveException("Phase 2 lane 2B has not delivered this journey");

    private static async Task VerifyRecursiveEditor(NativeClient native, int processId, string display,
        string evidence, string instanceId, CancellationToken token)
    {
        var fixture = LinkedDiagramFixture.Create(); var graph = fixture.Graph;
        var initialBindings = RecursiveBlockComponentTests.UnresolvedFixture();
        var definitionDraft = graph.StartDraft(graph.SelectedRoot) with { Definition = RecursiveBlockDefinitionTests.Partial(), ComponentBindings = initialBindings };
        graph = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], definitionDraft, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
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
            var originalInput = RecursiveBlockRefinementInputTests.Input(graph) with { Attachments = [capturedAsset] };
            var inputArguments = new Dictionary<string, object?>(arguments)
            {
                ["expectedInstanceEpoch"] = native.Epoch, ["expectedSourceToken"] = savedReadData.GetProperty("sourceToken").GetString(),
                ["input"] = JsonSerializer.SerializeToElement(originalInput, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
            var inputResult = await client.CallToolAsync("kicad_diagram_refinement_input_record", inputArguments, cancellationToken: token);
            if (inputResult.IsError == true) await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-refinement-input-error.json"), JsonSerializer.Serialize(inputResult), token);
            Assert.IsFalse(inputResult.IsError == true);
            Assert.IsTrue(JsonSerializer.SerializeToElement(inputResult).GetProperty("structuredContent").GetProperty("added").GetBoolean());
            var repeatedInput = await client.CallToolAsync("kicad_diagram_refinement_input_record", inputArguments, cancellationToken: token);
            Assert.IsFalse(repeatedInput.IsError == true);
            Assert.IsFalse(JsonSerializer.SerializeToElement(repeatedInput).GetProperty("structuredContent").GetProperty("added").GetBoolean());
            graph = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(source, token));
            Assert.IsTrue(originalInput.SameContents(graph.RefinementInput(originalInput.Id)));
            savedRead = await client.CallToolAsync("kicad_diagram_read", arguments, cancellationToken: token);
            Assert.IsFalse(savedRead.IsError == true); savedReadData = JsonSerializer.SerializeToElement(savedRead).GetProperty("structuredContent");
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
            publicationArguments["resume"] = true;
            var resumedPublication = await client.CallToolAsync("kicad_diagram_refinement_publication", publicationArguments, cancellationToken: token);
            Assert.IsFalse(resumedPublication.IsError == true);
            Assert.AreEqual("CompletedPreviously", JsonSerializer.SerializeToElement(resumedPublication).GetProperty("structuredContent")
                .GetProperty("inspection").GetProperty("disposition").GetString());
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
            await VerifySchemaTwoDiagramTools(client, native, project, instanceId, evidence, token);
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
            var initial = await Wait(s => s.Ready && !s.Busy && s.Rendered);
            Assert.AreEqual(graph.SelectedRoot.BlockId.ToString("D"), initial.DiagramPath.Single().BlockId);
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
                // Declared schema 2 editor, layout and realization fields are not produced yet; the observation
                // keeps its earlier shape instead of reporting computed defaults such as sourceWritable=false.
                static IEnumerable<string> Names(JsonElement element) => element.ValueKind switch
                {
                    JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Names(p.Value).Prepend(p.Name)),
                    JsonValueKind.Array => element.EnumerateArray().SelectMany(Names),
                    _ => Enumerable.Empty<string>()
                };
                var observed = Names(state).ToHashSet(StringComparer.Ordinal);
                Assert.IsTrue(observed.Contains("viewRevision"), "The observation names were not collected.");
                foreach (string key in new[] { "storedSchemaVersion", "sourceWritable", "levelDraft", "levelViewports", "canvasTool",
                    "selectedInterfaceId", "resolvedLayout", "domain", "direction", "presentation", "interfaceRealizations", "realization" })
                    Assert.IsFalse(observed.Contains(key), "The observation reports undeclared schema 2 field " + key + ".");
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
                Assert.AreEqual(before, await Read(), "Offscreen views must not alter draft, selection, source, view revision or viewport.");
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
                Assert.AreEqual(before, await Read());
                viewArguments["views"] = new[] { new { viewId = "recovered", pixelWidth = 640, pixelHeight = 480 } };
                var recoveredObservation = await client.CallToolAsync("kicad_diagram_observe", viewArguments, cancellationToken: token);
                Assert.IsFalse(recoveredObservation.IsError == true, "A valid native observation must work after rejected and cancelled requests.");
                Assert.AreEqual(before, await Read());
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
            Key("Escape"); await Wait(s => s.DiagramHistory is null); Key("r", control: true); await Wait(s => !s.Busy && !s.Dirty);
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
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Structural diagram", false, true,
                clickFromLeft: 20, clickFromTop: 250);
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
            async Task RejectedSelection(string label, string code)
            {
                string unchanged = await File.ReadAllTextAsync(source, token); var nativeBefore = await Read();
                Guid rejectedOperation = Guid.NewGuid(); selectArguments["operationId"] = rejectedOperation;
                var rejected = await client.CallToolAsync("kicad_diagram_proposal_select", selectArguments, cancellationToken: token);
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-proposal-select-" + label + ".json"), JsonSerializer.Serialize(rejected), token);
                Assert.IsTrue(rejected.IsError == true, "A " + label + " proposal selection must be rejected, not applied over newer work.");
                Assert.AreEqual(code, JsonSerializer.SerializeToElement(rejected).GetProperty("structuredContent").GetProperty("code").GetString());
                Assert.AreEqual(unchanged, await File.ReadAllTextAsync(source, token), "A rejected " + label + " selection must leave the saved design unchanged.");
                var nativeAfter = await Read();
                Assert.AreEqual(nativeBefore.SourceToken, nativeAfter.SourceToken); Assert.AreEqual(nativeBefore.DiagramPath, nativeAfter.DiagramPath);
                Assert.AreEqual(nativeBefore.Draft, nativeAfter.Draft);
                var noReceipt = await client.CallToolAsync("kicad_diagram_proposal_publication", new Dictionary<string, object?>
                    { ["instanceId"] = instanceId, ["expectedInstanceEpoch"] = native.Epoch, ["operationId"] = rejectedOperation }, cancellationToken: token);
                Assert.AreEqual("missing_block_proposal_receipt", JsonSerializer.SerializeToElement(noReceipt).GetProperty("structuredContent").GetProperty("code").GetString(),
                    "A rejected selection must not leave a prepared write behind.");
            }
            // The first proposal refined the original root revision; the native
            // and agent edits above have since advanced that exact target.
            await RejectedSelection("changed-target", "proposal_target_changed");
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
            var psuProposal = RecursiveBlockProposalTests.CreateFor(selectionBase, psuInput);
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
            await RejectedSelection("outdated-token", "block_proposal_source_changed");
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
            await RejectedSelection("reselected-target", "proposal_target_changed");
            Key("w", control: true);
        }
        finally { Directory.Delete(stateRoot, true); }
        if (interactionFailures.Count != 0) throw new AggregateException("Native input failures were preserved; the remaining safe editor journey was exercised.", interactionFailures);
    }

    /// <summary>Schema 2 through the real MCP server and native instance (contract rbg-v2 sections 2.4 and 8):
    /// an agent reads a schema 2 level's layout, realizations, domains and directions; a changed write
    /// upgrades a version 1 file and reports it (R4); and the native editor of this build, which still
    /// speaks schema 1, is never opened on a schema 2 document, so nothing can be dropped or written.</summary>
    private static async Task VerifySchemaTwoDiagramTools(McpClient client, NativeClient native, string project, string instanceId,
        string evidence, CancellationToken token)
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
        await File.WriteAllTextAsync(upgradedPath, RecursiveBlockGraphXml.Write(plain), token);
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
        // The schema 1 native editor is never opened on a schema 2 document: nothing is shown, dropped or written.
        foreach (var (path, documentId, bytes) in new[] { (layered, f.Graph.DocumentId, layeredXml), (upgradedPath, plain.DocumentId, upgradedXml) })
        {
            var open = await client.CallToolAsync("kicad_diagram_open", new Dictionary<string, object?>(target) { ["sourcePath"] = path,
                ["documentId"] = documentId.ToString("D") }, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-schema-two-open-" + Path.GetFileNameWithoutExtension(path) + ".json"),
                JsonSerializer.Serialize(open), token);
            Assert.IsTrue(open.IsError == true, "A schema 1 editor must not open a schema 2 document.");
            Assert.AreEqual("unsupported_diagram_file_request", JsonSerializer.SerializeToElement(open).GetProperty("structuredContent").GetProperty("code").GetString());
            var closed = await Assert.ThrowsAsync<NativeApiException>(() => native.InvokeAsync<P.ReadRecursiveDiagramEditor, P.RecursiveDiagramEditorState>(
                new() { DocumentId = documentId.ToString("D") }, token));
            Assert.IsFalse(string.IsNullOrEmpty(closed.Message));
            Assert.AreEqual(bytes, await File.ReadAllTextAsync(path, token));
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
