using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsInstalledPackageTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeWindowsPackage"), DoNotParallelize]
    public async Task PackagedMcpLaunchesRendersAndReattachesTwoDirtyNativeEditors()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires the actual installed Windows package and desktop."); return; }
        string install = Required("KICAD_TEST_WINDOWS_INSTALL");
        Assert.IsTrue(Path.IsPathFullyQualified(install));
        string bin = Path.Combine(install, "bin"), nativeExecutable = Path.Combine(bin, "kicad.exe");
        string mcpExecutable = Path.Combine(bin, "kicad-mcp.exe");
        foreach (string file in new[] { nativeExecutable, mcpExecutable, Path.Combine(bin, "nng.dll") }) Assert.IsTrue(File.Exists(file), file);
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!,
            "windows-installed-editor")).FullName;
        string scratch = Directory.CreateTempSubdirectory("kwpkg-").FullName;
        string state = Path.Combine(scratch, "registry");
        string? priorLibrary = Environment.GetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY");
        // Native-only fixture setup uses the candidate's DLL. The packaged MCP
        // subprocess explicitly removes this override and proves normal loading.
        Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", Path.Combine(bin, "nng.dll"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        WindowsProcessJob? job = null;
        var editors = new List<Editor>();
        var nativeProcesses = new List<Process>();
        try
        {
            job = new WindowsProcessJob();
            await using (var mcp = await Mcp.Start(mcpExecutable, scratch, state, evidence, "first", job, deadline.Token))
            {
                for (int index = 0; index < 2; index++)
                {
                    string project = Path.Combine(scratch, "board-" + index + ".kicad_pro");
                    await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}", deadline.Token);
                    var started = await mcp.Tool("kicad_instance_start", new { executable = nativeExecutable, projectPath = project,
                        softwareRendering = true });
                    string id = McpToolPayload.Object(started).GetProperty("instanceId").GetString()!;
                    Assert.IsTrue(Guid.TryParseExact(id, "D", out _));
                    var record = JsonSerializer.Deserialize<InstanceRecord>(await File.ReadAllTextAsync(Path.Combine(state, id + ".json"), deadline.Token))!;
                    Assert.AreEqual(id, record.InstanceId); Assert.AreEqual(project, record.ProjectPath);
                    Assert.IsNotNull(record.ProcessId);
                    var process = Process.GetProcessById(record.ProcessId.Value);
                    if (!job.Contains(process)) { process.Dispose(); Assert.Fail("Only a child of this test may receive native UI input."); }
                    nativeProcesses.Add(process);
                    Assert.IsTrue(string.Equals(nativeExecutable, process.MainModule!.FileName, StringComparison.OrdinalIgnoreCase));
                    var native = new NativeClient(new NngTransport(), record.Endpoint, record.Epoch);
                    var session = await native.HandshakeAsync(deadline.Token);
                    Assert.AreEqual(id, session.InstanceId); Assert.AreEqual(project, session.ProjectPath);
                    string schematic = Path.ChangeExtension(project, ".kicad_sch");
                    var created = await mcp.Tool("kicad_schematic_create", new { instanceId = id, path = schematic });
                    string documentJson = created.GetProperty("content").EnumerateArray()
                        .Single(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
                    var document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson);
                    var editor = new Editor(process, native, record, document, documentJson, schematic,
                        Guid.NewGuid().ToString("D"), "Unsaved board " + index + " note " + Guid.NewGuid().ToString("N"));
                    editors.Add(editor);
                    await PrepareMarker(editor); // Test setup, not a claim of a public MCP creation tool.
                    await Observe(mcp, editor, "initial-" + index);
                    nint window = await WindowsNativeUi.WaitForWindow(process, "Schematic Editor", deadline.Token);
                    WindowsNativeUi.Capture(process, window, Path.Combine(evidence, "initial-window-" + index + ".png"));
                    await mcp.Tool("kicad_instance_start", new { executable = nativeExecutable, projectPath = project }, expectError: true);
                }
                Assert.AreNotEqual(editors[0].Record.InstanceId, editors[1].Record.InstanceId);
                Assert.AreNotEqual(editors[0].Record.Endpoint, editors[1].Record.Endpoint);
                await mcp.Tool("kicad_schematic_observe", new { instanceId = Guid.NewGuid().ToString("D"),
                    documentJson = editors[0].DocumentJson }, expectError: true);
            }
            foreach (var editor in editors) Assert.IsFalse(editor.Process.HasExited, "MCP disconnection killed an editor.");

            await using (var mcp = await Mcp.Start(mcpExecutable, scratch, state, evidence, "reattached", job, deadline.Token))
            {
                foreach (var editor in editors)
                {
                    await mcp.Tool("kicad_instance_reattach", new { instanceId = editor.Record.InstanceId });
                    Assert.AreEqual(editor.Record.Epoch, (await editor.Native.HandshakeAsync(deadline.Token)).Epoch);
                    await Observe(mcp, editor, "reattached-" + editors.IndexOf(editor));
                }
                for (int index = 0; index < editors.Count; index++)
                {
                    var editor = editors[index];
                    nint schematicWindow = await WindowsNativeUi.WaitForWindow(editor.Process, "Schematic Editor", deadline.Token);
                    WindowsNativeUi.Save(editor.Process, schematicWindow);
                    while (await IsDirty(mcp, editor)) await Task.Delay(250, deadline.Token);
                    string persisted = await File.ReadAllTextAsync(editor.Schematic, deadline.Token);
                    StringAssert.Contains(persisted, editor.MarkerId); StringAssert.Contains(persisted, editor.MarkerText);
                    WindowsNativeUi.Capture(editor.Process, schematicWindow, Path.Combine(evidence, "saved-window-" + index + ".png"));
                    WindowsNativeUi.Close(editor.Process, schematicWindow);
                    await WindowsNativeUi.WaitForClosedWindow(editor.Process, schematicWindow, deadline.Token);
                    nint manager = await WindowsNativeUi.WaitForWindow(editor.Process, "KiCad", deadline.Token);
                    WindowsNativeUi.Close(editor.Process, manager);
                    await editor.Process.WaitForExitAsync(deadline.Token);
                    Assert.AreEqual(0, editor.Process.ExitCode);
                    if (index == 0) await Observe(mcp, editors[1], "other-editor-preserved");
                }
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "passed", normalPackagedMcpLoading = true,
                twoInstancesIsolated = true, mcpRestartPreservedDirtyObjects = true,
                nativeKeyboardSaveAndClose = true, publicMcpObjectCreationVerified = false,
                publicMcpSaveCloseVerified = false, codexDesktopQualified = false, automaticUpdatingQualified = false,
                instances = editors.Select(e => new { instanceId = e.Record.InstanceId, processEpoch = e.Record.Epoch, markerId = e.MarkerId,
                    nativeProcessExitCode = e.Process.ExitCode })
            }), deadline.Token);
            TestContext.AddResultFile(Path.Combine(evidence, "result.json"));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.txt"), error.ToString());
            throw;
        }
        finally
        {
            if (job is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await job.StopAndWaitAsync(cleanup.Token); }
                finally { job.Dispose(); }
            }
            foreach (var process in nativeProcesses)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(cleanup.Token); process.Dispose();
            }
            Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", priorLibrary);
            var runtimeIds = (Directory.Exists(state) ? Directory.GetFiles(state, "*.json", SearchOption.AllDirectories) : [])
                .Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(id => Guid.TryParseExact(id, "D", out _)).Distinct().ToArray();
            foreach (string id in runtimeIds)
            {
                string runtime = Path.Combine(Path.GetTempPath(), "kicad-automation", id);
                if (!Directory.Exists(runtime)) continue;
                foreach (string log in Directory.GetFiles(runtime, "*.log"))
                    File.Copy(log, Path.Combine(evidence, id + "-" + Path.GetFileName(log)), overwrite: false);
                await WindowsFixtureCleanup.RemoveOwnedRuntimeDirectoryAsync(runtime);
            }
            try { await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(scratch); }
            catch (Exception cleanupError)
            {
                var entries = Directory.Exists(scratch)
                    ? Directory.EnumerateFileSystemEntries(scratch, "*", new EnumerationOptions
                    { RecurseSubdirectories = true, MaxRecursionDepth = 16, AttributesToSkip = FileAttributes.ReparsePoint })
                        .Take(512).Select(path => new { path = Path.GetRelativePath(scratch, path), attributes = File.GetAttributes(path).ToString() }).ToArray()
                    : [];
                string report = Path.Combine(evidence, "cleanup-failure.json");
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { error = cleanupError.ToString(), entries }));
                TestContext.AddResultFile(report);
                throw;
            }
        }

        async Task PrepareMarker(Editor editor)
        {
            var snapshot = await editor.Native.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = editor.Document }, deadline.Token);
            var batch = new ApplySchematicItemBatch { Document = editor.Document, ExpectedRevision = snapshot.Revision,
                DocumentEpoch = snapshot.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Package qualification fixture" };
            batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(new SchematicText
            {
                Id = new() { Value = editor.MarkerId }, Text = new() { Text_ = editor.MarkerText,
                    Position = new() { XNm = 100000000, YNm = 80000000 },
                    Attributes = new() { Size = new() { XNm = 2000000, YNm = 2000000 } } }
            }) });
            await editor.Native.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, deadline.Token);
        }

        async Task Observe(Mcp mcp, Editor editor, string name)
        {
            Assert.IsFalse(editor.Process.HasExited);
            var result = await mcp.Tool("kicad_schematic_observe", new { instanceId = editor.Record.InstanceId,
                documentJson = editor.DocumentJson }, allowNotReady: true);
            var content = result.GetProperty("structuredContent");
            Assert.AreEqual(editor.Record.InstanceId, content.GetProperty("instanceId").GetString());
            var observation = SchematicJson.Parser.Parse<SchematicObservation>(content.GetProperty("observation").GetRawText());
            Assert.AreEqual(editor.Document, observation.Preview.Document);
            Assert.AreEqual(observation.Snapshot.Revision, observation.Preview.Revision);
            var marker = observation.Snapshot.Data.Items.Where(x => x.Is(SchematicText.Descriptor))
                .Select(x => x.Unpack<SchematicText>()).Single(x => x.Id.Value == editor.MarkerId);
            Assert.AreEqual(editor.MarkerText, marker.Text.Text_);
            foreach (var other in editors.Where(x => x != editor))
                Assert.IsFalse(observation.Snapshot.Data.Items.Any(x => x.Is(SchematicText.Descriptor)
                    && x.Unpack<SchematicText>().Id.Value == other.MarkerId));
            Assert.IsTrue(await IsDirty(mcp, editor)); Assert.IsFalse(File.Exists(editor.Schematic));
            byte[] png = Convert.FromBase64String(result.GetProperty("content").EnumerateArray()
                .Single(c => c.GetProperty("type").GetString() == "image").GetProperty("data").GetString()!);
            Assert.IsTrue(png.Length > 24 && png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            string image = Path.Combine(evidence, name + ".png"), metadata = Path.Combine(evidence, name + ".json");
            await File.WriteAllBytesAsync(image, png, deadline.Token);
            await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(new { imageSha256 = Convert.ToHexStringLower(SHA256.HashData(png)), state = content }), deadline.Token);
            TestContext.AddResultFile(image); TestContext.AddResultFile(metadata);
        }

        async Task<bool> IsDirty(Mcp mcp, Editor editor)
        {
            var result = await mcp.Tool("kicad_schematic_save_state", new { instanceId = editor.Record.InstanceId,
                documentJson = editor.DocumentJson }, allowNotReady: true);
            return SchematicJson.Parser.Parse<SchematicSaveState>(result.GetProperty("structuredContent").GetProperty("state").GetRawText()).UnsavedSchematicChanges;
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(name + " is required for native package qualification.");
    private sealed record Editor(Process Process, NativeClient Native, InstanceRecord Record, DocumentSpecifier Document,
        string DocumentJson, string Schematic, string MarkerId, string MarkerText);

    internal sealed class Mcp(Process process, Task diagnostics, CancellationTokenSource diagnosticStop,
        string requestStatePath, CancellationToken token) : IMcpToolClient
    {
        private int nextId;
        Task<JsonElement> IMcpToolClient.Tool(string name, object arguments) =>
            Tool(name, arguments, allowNotReady: name is "kicad_schematic_observe" or "kicad_schematic_save_state");
        public async Task<JsonElement> ListTools(string? cursor) =>
            (await Request("tools/list", cursor is null ? new { } : (object)new { cursor })).GetProperty("result").Clone();
        public static async Task<Mcp> Start(string executable, string scratch, string state, string evidence,
            string name, WindowsProcessJob job, CancellationToken token, bool traceUpdates = false)
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = scratch, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = new System.Text.UTF8Encoding(false),
                StandardErrorEncoding = new System.Text.UTF8Encoding(false) };
            start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = state;
            start.Environment["KICAD_CONFIG_HOME"] = Path.Combine(scratch, "config");
            start.Environment["KICAD_CACHE_HOME"] = Path.Combine(scratch, "cache");
            if (traceUpdates)
            {
                start.Environment["WXTRACE"] = "KICAD_AUTOMATION_UPDATES";
                start.Environment["KICAD_ENABLE_WXTRACE"] = "1";
            }
            foreach (string variable in new[] { "KICAD_AUTOMATION_NNG_LIBRARY", "KICAD_RUN_FROM_BUILD_DIR",
                "KICAD_AUTOMATION_UPDATE_HELPER", "KICAD_AUTOMATION_UPDATE_CONFIG" }) start.Environment.Remove(variable);
            var process = Process.Start(start)!;
            try { job.Attach(process); }
            catch { process.Kill(); await process.WaitForExitAsync(); process.Dispose(); throw; }
            var stop = new CancellationTokenSource();
            var client = new Mcp(process, Capture(), stop, Path.Combine(evidence, name + "-mcp-request.json"), token);
            try
            {
                await client.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { },
                    clientInfo = new { name = "windows-installed-package", version = "1" } });
                await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}".AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                return client;
            }
            catch { await client.DisposeAsync(); throw; }
            async Task Capture()
            {
                await using var log = File.Create(Path.Combine(evidence, name + "-mcp.stderr.log"));
                bool eof = false;
                try { await process.StandardError.BaseStream.CopyToAsync(log, stop.Token); eof = true; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                await log.FlushAsync();
                await File.WriteAllTextAsync(Path.Combine(evidence, name + "-mcp-capture.json"),
                    JsonSerializer.Serialize(new { schemaVersion = 1, reachedEof = eof, bytes = log.Length }));
            }
        }

        public async Task<JsonElement> Tool(string name, object arguments, bool expectError = false, bool allowNotReady = false)
        {
            while (true)
            {
                var result = (await Request("tools/call", new { name, arguments })).GetProperty("result");
                bool error = result.TryGetProperty("isError", out var value) && value.GetBoolean();
                if (error && allowNotReady)
                {
                    string text = result.GetProperty("content").EnumerateArray()
                        .Single(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
                    using var detail = JsonDocument.Parse(text);
                    if (detail.RootElement.TryGetProperty("code", out var code) && code.GetString() is "native_status_4" or "native_status_7")
                    { await Task.Delay(250, token); continue; }
                }
                Assert.AreEqual(expectError, error, name + ": " + (error ? result.GetRawText() : "unexpected success"));
                return result.Clone();
            }
        }

        private async Task<JsonElement> Request(string method, object parameters)
        {
            int id = ++nextId;
            await State("writing");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            await State("waiting");
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(token);
                if (line is null) throw new IOException("The packaged MCP process exited without a reply.");
                using var response = JsonDocument.Parse(line);
                if (!response.RootElement.TryGetProperty("id", out var replyId) || replyId.GetInt32() != id) continue;
                await State("received");
                if (response.RootElement.TryGetProperty("error", out var error)) throw new IOException(error.GetRawText());
                return response.RootElement.Clone();
            }
            Task State(string phase) => File.WriteAllTextAsync(requestStatePath,
                JsonSerializer.Serialize(new { schemaVersion = 1, id, method, phase }), token);
        }

        public async ValueTask DisposeAsync()
        {
            process.StandardInput.Close();
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(exit.Token); }
            finally
            {
                try
                {
                    if (!process.HasExited) process.Kill(); // Never kill dirty native children on an MCP disconnect.
                    using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(killed.Token);
                }
                finally
                {
                    // Another inherited writer must not hold the completed
                    // MCP client's diagnostic reader (and test cleanup) open.
                    diagnosticStop.Cancel();
                    try { await diagnostics.WaitAsync(TimeSpan.FromSeconds(5)); }
                    finally { diagnosticStop.Dispose(); process.Dispose(); }
                }
            }
        }
    }
}
