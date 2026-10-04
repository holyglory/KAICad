using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    [TestMethod, TestCategory("NativePcbKeepout")]
    public Task NativePcbKeepoutsAreAuthoredAndPersistedThroughStdioMcp() => RunNativePcbKeepoutJourney();

    /// <summary>
    /// Uses a disposable Linux KiCad process and a compiled STDIO MCP client to
    /// exercise the task-oriented rule-area adapter against a real PCB.  This
    /// deliberately does not claim replay safety: the keepout adapter currently
    /// exposes operation IDs for correlation only and has no retained native
    /// receipt to settle a lost reply.
    /// </summary>
    private static async Task RunNativePcbKeepoutJourney()
    {
        Assert.IsTrue(OperatingSystem.IsLinux(), "The native keepout fixture is Linux-only.");
        string root = FindRoot();
        string executable = Path.Combine(root, "automation", "artifacts", "native", "kicad", "kicad");
        Assert.IsTrue(File.Exists(executable), "Build the linked native manager before this journey.");

        string evidence = NativeEvidenceDirectory.Begin(Path.Combine(root, "automation", "artifacts", "native-pcb-keepout"));
        string scratch = Directory.CreateTempSubdirectory("kicad-pcb-keepout-").FullName;
        Process? displayProcess = null;
        Process? nativeProcess = null;
        try
        {
            int displayNumber;
            (displayProcess, displayNumber) = await StartKeepoutDisplay();
            string display = ":" + displayNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string projectDirectory = Directory.CreateDirectory(Path.Combine(scratch, "project")).FullName;
            string project = Path.Combine(projectDirectory, "fixture.kicad_pro");
            string boardPath = Path.Combine(projectDirectory, "fixture.kicad_pcb");
            await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}\n");

            string instanceId = Guid.NewGuid().ToString("D");
            string socket = Path.Combine(projectDirectory, "api.sock");
            nativeProcess = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = projectDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            nativeProcess.StartInfo.Environment["DISPLAY"] = display;
            nativeProcess.StartInfo.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
            nativeProcess.StartInfo.Environment["XDG_CONFIG_HOME"] = Path.Combine(projectDirectory, "config");
            nativeProcess.StartInfo.Environment["XDG_CACHE_HOME"] = Path.Combine(projectDirectory, "cache");
            foreach (string argument in new[] { "--new", "--automation", instanceId, "--api-socket", socket,
                                                 "--automation-log", Path.Combine(evidence, "native-keepout.log"),
                                                 "--software-rendering", project })
                nativeProcess.StartInfo.ArgumentList.Add(argument);
            Assert.IsTrue(nativeProcess.Start());
            _ = nativeProcess.StandardOutput.ReadToEndAsync();
            _ = nativeProcess.StandardError.ReadToEndAsync();

            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var registry = new InstanceRegistry(new NngTransport(), Path.Combine(scratch, "registry"));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (nativeProcess.HasExited)
                    Assert.Fail("The native KiCad fixture exited before the automation endpoint was attached.");
                try
                {
                    await registry.AttachAsync("ipc://" + socket, instanceId, deadline.Token);
                    break;
                }
                catch (NngException) { }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                await Task.Delay(100, deadline.Token);
            }

            NativeClient client = registry.Client(instanceId);
            await client.HandshakeAsync(deadline.Token);
            string mcpState = Path.Combine(scratch, "mcp-state");
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(), mcpState,
                Path.Combine(evidence, "keepout-mcp.stderr.log"), deadline.Token);
            KeepoutAssertNoError(await mcp.Tool("kicad_instance_attach",
                new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

            DocumentSpecifier board = KeepoutOpened(await mcp.Tool("kicad_pcb_create",
                new { instanceId, path = boardPath }));
            DocumentLifecycleState baseline = await ObserveLifecycleState(client, board, deadline.Token);
            string boardJson = SchematicJson.Formatter.Format(board);

            string keepoutId = Guid.NewGuid().ToString("D");
            object initial = KeepoutInput(keepoutId, layers: ["F.Cu", "B.Cu", "F.Mask"],
                x0: 10_000_000, y0: 10_000_000, x1: 50_000_000, y1: 50_000_000,
                holeX0: 20_000_000, holeY0: 20_000_000, holeX1: 30_000_000, holeY1: 30_000_000,
                copper: true, vias: true, tracks: true, pads: true, footprints: true);
            JsonElement createdReply = await mcp.Tool("kicad_pcb_keepout_create", new
            {
                instanceId, documentJson = boardJson, keepout = initial,
                expectedStateJson = SchematicJson.Formatter.Format(baseline),
                operationId = Guid.NewGuid().ToString("D")
            });
            JsonElement created = KeepoutToolSuccess(createdReply);
            Assert.AreEqual("create", created.GetProperty("operation").GetString(), createdReply.GetRawText());
            Assert.IsTrue(created.GetProperty("mutationConfirmed").GetBoolean(), createdReply.GetRawText());
            Assert.IsTrue(created.GetProperty("postCommitVerified").GetBoolean(), createdReply.GetRawText());
            Assert.IsFalse(created.GetProperty("nativeReceiptAvailable").GetBoolean(), createdReply.GetRawText());
            Assert.IsTrue(created.GetProperty("operationIdCorrelationOnly").GetBoolean(), createdReply.GetRawText());
            Assert.IsFalse(created.GetProperty("replaySafe").GetBoolean(), createdReply.GetRawText());

            DocumentLifecycleState afterCreate = StateFrom(created, board);
            Zone expectedCreated = KeepoutNative(initial);
            Zone actualCreated = await ReadKeepout(mcp, instanceId, boardJson, keepoutId, afterCreate);
            PcbKeepoutAuthoringContract.ValidateReadback(expectedCreated, actualCreated, afterCreate.NativeIdentity);
            Assert.AreEqual(1, actualCreated.Outline.Polygons.Count);
            Assert.AreEqual(1, actualCreated.Outline.Polygons[0].Holes.Count);
            Assert.AreEqual(3, actualCreated.Layers.Count);

            object changed = KeepoutInput(keepoutId, layers: ["F.Cu"],
                x0: 15_000_000, y0: 15_000_000, x1: 55_000_000, y1: 55_000_000,
                holeX0: 25_000_000, holeY0: 25_000_000, holeX1: 35_000_000, holeY1: 35_000_000,
                copper: true, vias: false, tracks: true, pads: false, footprints: false);
            JsonElement updatedReply = await mcp.Tool("kicad_pcb_keepout_update", new
            {
                instanceId, documentJson = boardJson, keepout = changed,
                expectedStateJson = SchematicJson.Formatter.Format(afterCreate),
                operationId = Guid.NewGuid().ToString("D")
            });
            JsonElement updated = KeepoutToolSuccess(updatedReply);
            Assert.AreEqual("update", updated.GetProperty("operation").GetString(), updatedReply.GetRawText());
            DocumentLifecycleState afterUpdate = StateFrom(updated, board);
            Zone expectedUpdated = KeepoutNative(changed);
            Zone actualUpdated = await ReadKeepout(mcp, instanceId, boardJson, keepoutId, afterUpdate);
            PcbKeepoutAuthoringContract.ValidateReadback(expectedUpdated, actualUpdated, afterUpdate.NativeIdentity);
            Assert.IsFalse(actualUpdated.RuleAreaSettings!.KeepoutVias, updatedReply.GetRawText());

            // A request carrying the prior lifecycle checkpoint must refuse without
            // opening or partially applying a native mutation.
            JsonElement staleReply = await mcp.Tool("kicad_pcb_keepout_update", new
            {
                instanceId, documentJson = boardJson, keepout = initial,
                expectedStateJson = SchematicJson.Formatter.Format(afterCreate),
                operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsTrue(staleReply.GetProperty("isError").GetBoolean(), staleReply.GetRawText());
            Assert.AreEqual("pcb_state_changed", staleReply.GetProperty("structuredContent")
                .GetProperty("errorCode").GetString(), staleReply.GetRawText());
            Assert.AreEqual(afterUpdate, await ObserveLifecycleState(client, board, deadline.Token));
            PcbKeepoutAuthoringContract.ValidateReadback(expectedUpdated,
                await ReadKeepout(mcp, instanceId, boardJson, keepoutId, afterUpdate), afterUpdate.NativeIdentity);

            // Malformed geometry is rejected by the typed adapter before BeginCommit;
            // the native board and lifecycle digest remain unchanged.
            object malformed = new
            {
                id = Guid.NewGuid().ToString("D"), layers = new[] { "F.Cu" },
                polygons = new[] { new { outline = new[]
                {
                    new { xNm = 15_000_000L, yNm = 15_000_000L },
                    new { xNm = 55_000_000L, yNm = 15_000_000L },
                    new { xNm = 55_000_000L, yNm = 55_000_000L },
                    new { xNm = 15_000_000L, yNm = 15_000_000L }
                }, holes = Array.Empty<object>() } },
                name = "malformed", keepoutCopper = true, keepoutVias = false,
                keepoutTracks = true, keepoutPads = false, keepoutFootprints = false,
                borderStyle = "ZBS_SOLID", borderPitchNm = 0L
            };
            JsonElement malformedReply = await mcp.Tool("kicad_pcb_keepout_create", new
            {
                instanceId, documentJson = boardJson, keepout = malformed,
                expectedStateJson = SchematicJson.Formatter.Format(afterUpdate),
                operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsTrue(malformedReply.GetProperty("isError").GetBoolean(), malformedReply.GetRawText());
            StringAssert.Contains(malformedReply.GetRawText(), "invalid_pcb_keepout_outline", StringComparison.Ordinal);
            Assert.AreEqual(afterUpdate, await ObserveLifecycleState(client, board, deadline.Token));

            // Keep the updated shape in the board before exercising KiCad's own
            // undo/redo stack.  A single typed update is one native undo step.
            await FocusKeepoutEditor(client, board, nativeProcess.Id, display, "z", afterUpdate, deadline.Token);
            DocumentLifecycleState undone = await ObserveLifecycleState(client, board, deadline.Token);
            Zone undoneZone = await ReadKeepout(mcp, instanceId, boardJson, keepoutId, undone);
            PcbKeepoutAuthoringContract.ValidateReadback(expectedCreated, undoneZone, undone.NativeIdentity);
            await FocusKeepoutEditor(client, board, nativeProcess.Id, display, "y", undone, deadline.Token);
            DocumentLifecycleState redone = await ObserveLifecycleState(client, board, deadline.Token);
            PcbKeepoutAuthoringContract.ValidateReadback(expectedUpdated,
                await ReadKeepout(mcp, instanceId, boardJson, keepoutId, redone), redone.NativeIdentity);

            // Persist the updated native rule area, close the clean editor and
            // reopen through the same MCP process before reading it back.
            await SaveCheckedThroughMcp(client, board, evidence, deadline.Token, verifyReconnect: false);
            DocumentLifecycleState saved = await ObserveLifecycleState(client, board, deadline.Token);
            string closeOperation = Guid.NewGuid().ToString("D");
            JsonElement closeReply = await mcp.Tool("kicad_document_close", new
            {
                instanceId, expectedStateJson = SchematicJson.Formatter.Format(saved), operationId = closeOperation
            });
            LifecycleOperationResult close = LifecycleResult(closeReply);
            Assert.AreEqual(LifecycleOperationStatus.LosClosed, close.Status, closeReply.GetRawText());
            board = KeepoutOpened(await mcp.Tool("kicad_pcb_open", new { instanceId, path = boardPath }));
            DocumentLifecycleState reopened = await ObserveLifecycleState(client, board, deadline.Token);
            boardJson = SchematicJson.Formatter.Format(board);
            PcbKeepoutAuthoringContract.ValidateReadback(expectedUpdated,
                await ReadKeepout(mcp, instanceId, boardJson, keepoutId, reopened), reopened.NativeIdentity);

            JsonElement deletedReply = await mcp.Tool("kicad_pcb_keepout_delete", new
            {
                instanceId, documentJson = boardJson, keepoutId,
                expectedStateJson = SchematicJson.Formatter.Format(reopened),
                operationId = Guid.NewGuid().ToString("D")
            });
            JsonElement deleted = KeepoutToolSuccess(deletedReply);
            Assert.AreEqual("delete", deleted.GetProperty("operation").GetString(), deletedReply.GetRawText());
            Assert.IsTrue(deleted.GetProperty("mutationConfirmed").GetBoolean(), deletedReply.GetRawText());
            DocumentLifecycleState afterDelete = StateFrom(deleted, board);
            JsonElement missingReply = await mcp.Tool("kicad_pcb_keepout_read", new
            {
                instanceId, documentJson = boardJson, keepoutId,
                expectedStateJson = SchematicJson.Formatter.Format(afterDelete)
            });
            Assert.IsTrue(missingReply.GetProperty("isError").GetBoolean(), missingReply.GetRawText());
            StringAssert.Contains(missingReply.GetRawText(), "pcb_keepout_not_found", StringComparison.Ordinal);

            await SaveCheckedThroughMcp(client, board, evidence, deadline.Token, verifyReconnect: false);
            await File.WriteAllTextAsync(Path.Combine(evidence, "keepout-journey-summary.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    capability = "pcb-keepout-authoring",
                    realNativeBoard = true,
                    polygonWithHole = true,
                    explicitPhysicalLayers = true,
                    restrictionFlagsReadBack = true,
                    staleRequestRejected = true,
                    malformedGeometryRolledBack = true,
                    nativeUndoRedo = true,
                    saveReopenPersistence = true,
                    deleteReadback = true,
                    routingOrDrcEnforcement = "not exercised by this isolated keepout fixture",
                    nativeReceiptAvailable = false,
                    operationIdCorrelationOnly = true,
                    lostReplySettlement = "indeterminate until a retained native receipt exists"
                }, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        }
        finally
        {
            if (nativeProcess is not null)
            {
                try
                {
                    if (!nativeProcess.HasExited) nativeProcess.Kill(entireProcessTree: true);
                    await nativeProcess.WaitForExitAsync();
                }
                catch (InvalidOperationException) { }
                nativeProcess.Dispose();
            }
            if (displayProcess is not null)
            {
                try
                {
                    if (!displayProcess.HasExited) displayProcess.Kill(entireProcessTree: true);
                    await displayProcess.WaitForExitAsync();
                }
                catch (InvalidOperationException) { }
                displayProcess.Dispose();
            }
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task<(Process Process, int Number)> StartKeepoutDisplay()
    {
        var start = new ProcessStartInfo("Xvfb")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" })
            start.ArgumentList.Add(argument);
        Process process = Process.Start(start)!;
        string? output = await process.StandardOutput.ReadLineAsync();
        Assert.IsTrue(int.TryParse(output, out int number), "Xvfb did not allocate a display.");
        return (process, number);
    }

    private static async Task FocusKeepoutEditor(NativeClient client, DocumentSpecifier board,
        int processId, string display, string key, DocumentLifecycleState before, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        while (!NativeKeyboard.HasWindow(display, processId, "PCB Editor"))
            await Task.Delay(100, limit.Token);
        // The PCB canvas can open Page Settings when the generic lower-right
        // focus click lands on the drawing-sheet border after a keepout
        // geometry change. The editor already owns keyboard focus after the
        // window raise, so send undo/redo without that synthetic click.
        NativeKeyboard.SchematicShortcut(display, processId, key, titleMatch: "PCB Editor", focusCanvas: false);
        int delay = 25;
        while (true)
        {
            DocumentLifecycleState after = await ObserveLifecycleState(client, board, limit.Token);
            if (!after.Equals(before)) return;
            await Task.Delay(delay, limit.Token);
            delay = Math.Min(delay * 2, 250);
        }
    }

    private static JsonElement KeepoutToolSuccess(JsonElement reply)
    {
        Assert.IsFalse(reply.TryGetProperty("isError", out JsonElement error) && error.GetBoolean(), reply.GetRawText());
        Assert.IsTrue(reply.TryGetProperty("structuredContent", out JsonElement data), reply.GetRawText());
        return data;
    }

    private static DocumentSpecifier KeepoutOpened(JsonElement reply)
    {
        KeepoutAssertNoError(reply);
        string text = reply.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "text")
            .GetProperty("text").GetString()!;
        return SchematicJson.Parser.Parse<DocumentSpecifier>(text);
    }

    private static void KeepoutAssertNoError(JsonElement reply)
    {
        Assert.IsFalse(reply.TryGetProperty("isError", out JsonElement error) && error.GetBoolean(), reply.GetRawText());
    }

    private static async Task<Zone> ReadKeepout(StdioMcpFixture mcp, string instanceId,
        string documentJson, string keepoutId, DocumentLifecycleState expected)
    {
        JsonElement reply = await mcp.Tool("kicad_pcb_keepout_read", new
        {
            instanceId, documentJson, keepoutId,
            expectedStateJson = SchematicJson.Formatter.Format(expected)
        });
        JsonElement data = KeepoutToolSuccess(reply);
        return BoardJson.Parser.Parse<Zone>(data.GetProperty("keepout").GetRawText());
    }

    private static DocumentLifecycleState StateFrom(JsonElement data, DocumentSpecifier board)
    {
        DocumentLifecycleState state = SchematicJson.Parser.Parse<DocumentLifecycleState>(
            data.GetProperty("afterState").GetString()!);
        Assert.AreEqual(board, state.Document);
        return state;
    }

    private static object KeepoutInput(string id, string[] layers,
        long x0, long y0, long x1, long y1, long holeX0, long holeY0, long holeX1, long holeY1,
        bool copper, bool vias, bool tracks, bool pads, bool footprints) => new
    {
        id,
        layers,
        polygons = new[] { new
        {
            outline = new[] { new { xNm = x0, yNm = y0 }, new { xNm = x1, yNm = y0 },
                              new { xNm = x1, yNm = y1 }, new { xNm = x0, yNm = y1 } },
            holes = new[] { new[] { new { xNm = holeX0, yNm = holeY0 }, new { xNm = holeX1, yNm = holeY0 },
                                   new { xNm = holeX1, yNm = holeY1 }, new { xNm = holeX0, yNm = holeY1 } } }
        } },
        name = "MCP native keepout",
        keepoutCopper = copper, keepoutVias = vias, keepoutTracks = tracks,
        keepoutPads = pads, keepoutFootprints = footprints,
        borderStyle = "ZBS_SOLID", borderPitchNm = 0L
    };

    private static Zone KeepoutNative(object input)
    {
        string json = JsonSerializer.Serialize(input);
        var parsed = JsonSerializer.Deserialize<KiCad.Automation.Mcp.PcbKeepoutAuthoringInput>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return parsed.ToNative();
    }
}
