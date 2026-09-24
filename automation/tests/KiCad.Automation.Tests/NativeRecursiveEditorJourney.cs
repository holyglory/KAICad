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
        Task Capture(string name) => CaptureRecursive(display, Path.Combine(evidence, instanceId + "-drawing-" + name + ".png"), token);
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
        await Capture("empty");

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
        await At(260, 200); await Wait("caption-open", s => s.CaptionEditor == "block");
        await Capture("block-caption");
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
        await Wait("connect-started", s => s.CanvasHint == "Click a port to finish connection");
        await At(psuX + 60, psuY + 30);
        await Wait("connect-same-block", s => s.Notice == "Connect two different blocks or ports." && s.CanvasHint == "Click a port to finish connection"
            && s.CaptionEditor == "");
        await At(430, 420); await Wait("connect-empty", s => s.Notice.Contains("Finish the connection", StringComparison.Ordinal)
            && s.CanvasHint == "Click a port to finish connection");
        await Capture("connect-hint");
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
        await At(380, 180); await Wait("port-connect-started", s => s.CanvasHint == "Click a port to finish connection");
        await At(cpuX, cpuY); await Wait("feed-caption", s => s.CaptionEditor == "connection");
        Type("Rail feed"); Key("Return");
        var feedAdded = await Wait("feed-added", s => s.LevelDraft.NewConnections.Count == 2 && s.CaptionEditor == "");
        var feed = feedAdded.LevelDraft.NewConnections[1];
        Assert.AreEqual((P.DiagramEndpointKind.DekInterface, psu, rail), (feed.Endpoints[0].Kind, feed.Endpoints[0].BlockId, feed.Endpoints[0].InterfaceId));

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
        await Wait("fitted", s => s.ViewRevision > beforeFit && s.Rendered);
        await Capture("selected-handles");
        await Drag(800, 330, 840, 360);
        await Wait("resized", s => Placement(s, cpu).Width == "280" && Placement(s, cpu).Height == "170" && Frame(s) == ("100", "90", "780", "310"));
        Key("z", control: true); await Wait("resize-undone", s => Placement(s, cpu).Width == "240" && Frame(s) == ("100", "90", "740", "280"));
        Key("y", control: true); await Wait("resize-redone", s => Placement(s, cpu).Width == "280" && Frame(s) == ("100", "90", "780", "310"));
        // Moving a port along its block's edge.
        await Drag(380, 180, 380, 230);
        var portMoved = await Wait("port-moved", s => s.LevelDraft.Scope.LocalDiagram.Presentation.Ports.Single(p => p.InterfaceId == rail).Offset == "100");
        Assert.AreEqual(rail, portMoved.SelectedInterfaceId);
        // Rail feed and Power both end at the CPU's one anchor (rule F2), so Rail feed was given a stored route whose middle
        // leg runs apart from Power's. The route followed the CPU move, the resize and the port move; Power keeps its
        // computed path. A click near the CPU end of Rail feed's own leg selects Rail feed.
        var feedRoute = portMoved.LevelDraft.Scope.LocalDiagram.Presentation.Routes.Single(r => r.ConnectionId == feed.Selection.ConnectionId);
        Assert.AreEqual((1U, "500", "230", "500", "275"), (feedRoute.EndpointIndex, feedRoute.Waypoints[0].X, feedRoute.Waypoints[0].Y,
            feedRoute.Waypoints[1].X, feedRoute.Waypoints[1].Y));
        Assert.IsFalse(portMoved.LevelDraft.Scope.LocalDiagram.Presentation.Routes.Any(r => r.ConnectionId == power.Selection.ConnectionId));
        await At(500, 262);
        await Wait("feed-near-cpu", s => s.ConnectionDraft?.Baseline.ConnectionId == feed.Selection.ConnectionId && s.SelectedInterfaceId == "");
        // The nearest connection on screen wins. Rail feed's channel (x 500) meets the last leg the two share into the CPU (y 275,
        // x 500 to 560); Power's own last leg starts at x 470 on that line. A press 3 pixels above Power's leg and 5 pixels left of
        // Rail feed's channel selects Power; the mirror press, 3 pixels from the channel and 5 from Power, selects Rail feed.
        (int X, int Y) Pixel(P.RecursiveDiagramEditorState at, double x, double y) =>
            (at.CanvasWindowX + (int)Math.Round((x - at.CanvasOriginX) * at.CanvasScale, MidpointRounding.AwayFromZero),
             at.CanvasWindowY + (int)Math.Round((y - at.CanvasOriginY) * at.CanvasScale, MidpointRounding.AwayFromZero));
        var probe = await Read();
        var channelFoot = Pixel(probe, 500, 275);
        Assert.IsTrue(channelFoot.X - Pixel(probe, 470, 275).X >= 12, "Power's own vertical leg is well away from the probe presses.");
        Click(channelFoot.X - 5, channelFoot.Y - 3);
        await Wait("nearest-power", s => s.ConnectionDraft?.Baseline.ConnectionId == power.Selection.ConnectionId);
        Click(channelFoot.X - 3, channelFoot.Y - 5);
        await Wait("nearest-feed", s => s.ConnectionDraft?.Baseline.ConnectionId == feed.Selection.ConnectionId);
        // On the shared leg both connections are equally near: the press keeps whichever is selected. The editor counts every
        // canvas press, so an unchanged selection is a handled press, not one still on its way.
        var sharedLeg = Pixel(probe, 540, 275);
        foreach (var (stays, step) in new[] { (feed.Selection.ConnectionId, "shared-keeps-feed"), (power.Selection.ConnectionId, "shared-keeps-power") })
        {
            if ((await Read()).ConnectionDraft?.Baseline.ConnectionId != stays)
            {
                Click(channelFoot.X - 5, channelFoot.Y - 3);
                await Wait(step + "-selected", s => s.ConnectionDraft?.Baseline.ConnectionId == stays);
            }
            ulong presses = (await Read()).CanvasPresses;
            Click(sharedLeg.X, sharedLeg.Y);
            var tie = await Wait(step, s => s.CanvasPresses > presses);
            Assert.AreEqual(stays, tie.ConnectionDraft?.Baseline.ConnectionId, "A press on the shared leg keeps the selected connection selected.");
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
        await Wait("general-typed", s => s.Draft.Fields.General == "Supply the CPU.");
        await Capture("inspector-requirement");
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
        var storedRoute = layout.ConnectionRoutes.Single();
        Assert.AreEqual((feed.Selection.ConnectionId, 1), (storedRoute.ConnectionId.ToString("D"), storedRoute.EndpointIndex), "Only Rail feed needed a route.");
        CollectionAssert.AreEqual(new[] { new DiagramPoint(500, 230), new DiagramPoint(500, 275) }, storedRoute.Points.ToArray());
        var archive = graph.Connections(graph.SelectedRoot.BlockId);
        CollectionAssert.AreEqual(new[] { "Power", "Rail feed" }, top.LocalDiagram.Connections.Select(c => archive.Inspect(c).Name).ToArray());
        Assert.IsTrue(top.LocalDiagram.Connections.All(c => archive.Requirements(c).Requirements == DiagramRequirements.Empty));
        Assert.AreEqual("Feed the CPU from the rail.", top.LocalDiagram.Notes.Single().Text);
        await Capture("saved");

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
        // Rail feed's channel was 30 units right of the middle between its ends. Its ends are now the PSU's Rail port (400, 250),
        // moved here, and the CPU's left edge (540, 235), moved by the other editor, so the route runs level from each end to its
        // channel at x 500 = (400 + 540) / 2 + 30. Before the fix the merged route kept the CPU's old height, 275.
        CollectionAssert.AreEqual(new[] { new DiagramPoint(500, 250), new DiagramPoint(500, 235) }, rebasedLayout.ConnectionRoutes.Single().Points.ToArray(),
            "Rail feed's route followed the PSU moved here and the CPU moved elsewhere before the automatic save.");
        async Task<DiagramPoint[]> Resolved(string connection) => (await Route(await Read(), connection)).GetProperty("points").EnumerateArray()
            .Select(p => new DiagramPoint(decimal.Parse(p.GetProperty("x").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(p.GetProperty("y").GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var rebasedPath = await Resolved(feed.Selection.ConnectionId);
        var rebasedRoute = rebasedLayout.ConnectionRoutes.Single().Points;
        Assert.AreEqual((rebasedPath[0].Y, rebasedPath[^1].Y), (rebasedRoute[0].Y, rebasedRoute[1].Y),
            "The saved route's heights are the heights of the ends the editor resolves, so every leg is level or upright.");
        Assert.HasCount(4, rebasedPath);

        // A route an agent locked stays exactly as stored when its ends move; an unlocked route an earlier writer left out of line
        // (here with the ends' old heights, so its legs run diagonally) is put back in line by moving one of its ends.
        async Task<P.RecursiveDiagramEditorState> AgentRoute(string step, bool locked, params DiagramPoint[] waypoints)
        {
            var current = RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(created.Path, token));
            var agentDraft = current.StartDraft(current.SelectedRoot); var agentView = agentDraft.LocalDiagram.Layout;
            agentDraft = agentDraft with { Diagram = agentDraft.LocalDiagram with { Presentation = agentView with { Routes = [.. agentView.ConnectionRoutes
                .Select(r => r with { Waypoints = [.. waypoints], Locked = locked })] } } };
            var written = current.SaveDraft(current.SelectedRoot, [current.SelectedRoot], agentDraft, Guid.NewGuid(), Guid.NewGuid(), [],
                RecursiveBlockFixture.Origin("Another agent")).Graph;
            await File.WriteAllTextAsync(created.Path, RecursiveBlockGraphXml.Write(written), token);
            Key("r", control: true);
            return await Wait(step, s => !s.Dirty && s.LevelDraft.Scope.Baseline.RevisionId == written.SelectedRoot.RevisionId.ToString("D"));
        }
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
        await AgentRoute("route-stale", false, new DiagramPoint(500, 230), new DiagramPoint(500, 275));
        var stalePath = await Resolved(feed.Selection.ConnectionId);
        Assert.IsTrue(stalePath[0].Y != stalePath[1].Y && stalePath[2].Y != stalePath[3].Y, "The stored route arrives out of line with both of its ends.");
        await Drag(cpuNowX, cpuNowY, cpuNowX, cpuNowY + 20);
        var repaired = await Wait("stale-route-repaired", s => s.Dirty && Placement(s, cpu).Y == "170");
        Assert.AreEqual(("500,250", "500,255", "unlocked", feed.Selection.ConnectionId), Waypoints(repaired),
            "Moving the CPU puts the route back in line with both ends: the PSU's Rail port at 250 and the CPU's edge at 170 + 85.");
        ulong beforeRepairSave = repaired.CompletedSaveCount;
        Key("s", control: true);
        saved = await Wait("repaired-saved", s => s.CompletedSaveCount > beforeRepairSave && !s.Dirty);
        Assert.AreEqual("", saved.ErrorCode);
        savedXml = await File.ReadAllTextAsync(created.Path, token);
        var repairedGraph = RecursiveBlockGraphXml.Read(savedXml); var repairedLayout = repairedGraph.Inspect(repairedGraph.SelectedRoot).LocalDiagram.Layout;
        CollectionAssert.AreEqual(new[] { new DiagramPoint(500, 250), new DiagramPoint(500, 255) }, repairedLayout.ConnectionRoutes.Single().Points.ToArray());
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
            Routes = [.. routeView.ConnectionRoutes.Select(r => r with { Waypoints = [new DiagramPoint(520, 250), new DiagramPoint(520, 255)] })] } } };
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
        CollectionAssert.AreEqual(new[] { new DiagramPoint(520, 250), new DiagramPoint(520, 255) }, routeTop.LocalDiagram.Layout.ConnectionRoutes.Single().Points.ToArray(),
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
        await Capture("compact");
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
            CollectionAssert.AreEqual(new[] { "400,250", "520,250", "520,255", "580,255" }, storedPath.GetProperty("points").EnumerateArray()
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
        // The strength choices stay in one row, unclipped and without overlap, at every inspector width; brief tells which labels show.
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
        Task Capture(string name) => CaptureRecursive(display, Path.Combine(evidence, instanceId + "-choices-" + name + ".png"), token);
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
        StrengthRow(unknown, "default-width", null);
        await Capture("detail");

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

        // Clear facet returns Package to unspecified: its row and chip go. Undo brings it back.
        await Press("RecursiveFacetRowPackage");
        await Wait("package-row", s => s.FacetEditor == "package" && s.FocusedControl == "RecursiveFacetStateCandidate");
        await Press("RecursiveFacetClear");
        var cleared = await Wait("package-cleared", s => s.FacetEditor == "" && Facet(s, d => d.Package) is null);
        CollectionAssert.AreEqual(new[] { "type", "manufacturer" }, cleared.ShownFacets.ToArray());
        CollectionAssert.AreEqual(new[] { "Type: linear regulator" }, ChipTexts(cleared, psu));
        Key("z", control: true);
        await Wait("package-restored", s => Facet(s, d => d.Package) is { State: P.DefinitionChoiceStateData.DcsdCandidates } c
            && c.Strength == KiCad.Automation.Protocol.Structural.StructuralGuidanceStrength.SgsPreference);

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
        void VerifyMore(P.RecursiveDiagramEditorState at, string step)
        {
            var chips = Chips(at, psu) ?? throw new AssertFailedException(step + ": the PSU reports its chips.");
            CollectionAssert.AreEqual(new[] { "type", "family" }, chips.Chips.Select(c => c.Facet).ToArray(), step + ": two chips fit the block, in facet order.");
            Assert.AreEqual("Type: linear regulator", chips.Chips[0].Text, step + ": the first chip keeps its whole text.");
            StringAssert.StartsWith(chips.Chips[1].Text, "Family: ", step + ": the second chip names its facet.");
            Assert.IsTrue(chips.More is { Shown: true }, step + ": the rest is shown as one \"+N more\" chip.");
            Assert.AreEqual(1U, chips.HiddenChips, step + ": Package is the one choice behind \"+1 more\".");
            Assert.IsEmpty(chips.Marks, step + ": no state marks while chips fit.");
            Assert.HasCount(1, chips.PortNames, step + ": the PSU names its Rail port inside its edge, and the chips keep clear of it.");
            VerifyChoicesVisible(at, step, psu, three);
        }
        CollectionAssert.AreEqual(new[] { "type", "manufacturer", "family", "package" }, familyAdded.ShownFacets.ToArray(),
            "The overview lists every facet with a value, including Package behind \"+1 more\".");
        VerifyMore(familyAdded, "more");
        await Capture("more");

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
        await Capture("saved");

        // While the whole-diagram history is open the canvas takes no presses, and a previewed past revision is read only: the PSU
        // keeps its chips, but the Review facets link, which would do nothing, is not drawn, before Preview as well as during it. The
        // history opens on the saved revision; Down and Up inspect it again before Preview.
        Key("h", control: true);
        var historyOpen = await Wait("history-open", s => s.DiagramHistory is { Busy: false } h && h.Inspected?.RevisionId == graph.SelectedRoot.RevisionId.ToString("D")
            && s.Rendered);
        var historyOpenChips = Chips(historyOpen, psu) ?? throw new AssertFailedException("history-open: the PSU still shows its chips.");
        CollectionAssert.AreEqual(new[] { "type", "family" }, historyOpenChips.Chips.Select(c => c.Facet).ToArray(), "history-open: the same chips.");
        Assert.IsNull(historyOpenChips.ReviewFacets, "history-open: no Review facets link while the history is open.");
        Key("Down"); await Wait("history-older", s => s.DiagramHistory is { Busy: false } h && h.Inspected?.RevisionId != graph.SelectedRoot.RevisionId.ToString("D"));
        Key("Up"); await Wait("history-saved", s => s.DiagramHistory is { Busy: false } h && h.Inspected?.RevisionId == graph.SelectedRoot.RevisionId.ToString("D"));
        Key("p", alt: true);
        var preview = await Wait("history-preview", s => s.DiagramHistory?.Preview?.RevisionId == graph.SelectedRoot.RevisionId.ToString("D") && s.Rendered);
        var previewChips = Chips(preview, psu) ?? throw new AssertFailedException("history-preview: the previewed PSU still shows its chips.");
        CollectionAssert.AreEqual(new[] { "type", "family" }, previewChips.Chips.Select(c => c.Facet).ToArray(), "history-preview: the same chips.");
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
        await Capture("compact");
        // Between the compact and the full window the PSU is re-fitted at a third scale, where the Rail port's name narrows the chip
        // rows differently: the same rules hold there (no chip narrower than its minimum, the rest behind "+N more" or as marks).
        ulong beforeBetween = compact.ViewRevision;
        NativeKeyboard.SchematicShortcut(display, processId, "", title, false, false, resizeWidth: 1300, resizeHeight: 880);
        var between = await Wait("between", s => s.Rendered && s.ViewRevision > beforeBetween && s.CanvasPixelWidth > compact.CanvasPixelWidth);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-choices-between.json"), SchematicJson.Formatter.Format(between), token);
        VerifyChoicesVisible(between, "between", psu, three);
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
        Assert.AreEqual("No preference recorded.", manufacturer.Draft.Definition.Manufacturer.UnknownReason);
        Assert.IsFalse(manufacturer.Dirty, "Opening a facet's detail changes nothing.");
        await Capture("reopened");
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
        Task Capture(string name) => CaptureRecursive(display, Path.Combine(evidence, instanceId + "-details-" + name + ".png"), token);
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

        var start = await Open("start");
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
        await Capture("new-connection");
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
        await Wait("signals-row", s => Details(s).SequenceEqual(["signals", "direction"]) && s.FocusedControl == "RecursiveSignalEntry");
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
        await Wait("signal-removed", s => Signals(s).SequenceEqual(["GND"]));
        Key("z", control: true); await Wait("signal-restored", s => Signals(s).SequenceEqual(["VBUS", "GND"]));
        await Capture("signals");

        // + Add detail > Type: a connection with signals cannot be a single signal; with exactly two signals it may be a pair.
        await AddDetail("Home", "Down");
        var type = await Wait("type-row", s => Details(s).SequenceEqual(["signals", "direction", "type"]));
        Assert.IsFalse(Find(type, "RecursiveTypeSignal").Enabled, "A connection with signals cannot be a single signal.");
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
        await Capture("all-details");
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
        await Wait("declined", s => !s.Dirty && s.ConnectionDraft?.Direction == P.DiagramConnectionDirection.DcdrBidirectional);
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token));
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
        await Wait("compact-direction", s => s.Dirty && s.ConnectionDraft.Direction == P.DiagramConnectionDirection.DcdrFromFirst);
        await Capture("compact");
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
        Key("w", control: true); await Closed();
        Assert.AreEqual(savedXml, await File.ReadAllTextAsync(created.Path, token));
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
}
