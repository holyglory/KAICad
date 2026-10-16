using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeFootprintLibraryJourney
{
    private const int RestartSigStop = 19;
    private const int RestartSigKill = 9;
    private const int RestartSigContinue = 18;

    [TestMethod]
    public async Task NativeFootprintCheckedWriteSurvivesEditorReplacement()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("KICAD_NATIVE_FOOTPRINT_RESTART_JOURNEY"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set KICAD_NATIVE_FOOTPRINT_RESTART_JOURNEY=1 to run the bounded native replacement journey.");
            return;
        }

        Assert.IsTrue(OperatingSystem.IsLinux());
        DirectoryInfo root = FindRoot();
        string executable = Path.Combine(root.FullName, "automation/artifacts/native/kicad/kicad");
        Assert.IsTrue(File.Exists(executable), executable);
        string evidence = NativeEvidenceDirectory.Begin(Path.Combine(root.FullName, "automation/artifacts/native-footprint-library-restart"));
        string scratch = Directory.CreateTempSubdirectory("kicad-footprint-restart-").FullName;
        var (displayProcess, displayNumber) = await StartDisplay();
        string display = ":" + displayNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string projectDirectory = Directory.CreateDirectory(Path.Combine(scratch, "project")).FullName;
        string project = Path.Combine(projectDirectory, "fixture.kicad_pro");
        string library = Directory.CreateDirectory(Path.Combine(projectDirectory, "Fixture.pretty")).FullName;
        string sourceFootprint = Path.Combine(root.FullName, "qa/data/libraries/Resistor_SMD.pretty/R_0201_0603Metric.kicad_mod");
        string siblingName = "Generated_Test";
        string siblingPath = Path.Combine(library, siblingName + ".kicad_mod");
        string targetPath = Path.Combine(library, CheckedFootprintName + ".kicad_mod");
        string socket = Path.Combine(scratch, "api.sock");
        string stateDirectory = Path.Combine(scratch, "mcp-state");
        string instanceId = Guid.NewGuid().ToString("D");
        string operationId = Guid.NewGuid().ToString("D");
        Process? native = null;
        Process? replacement = null;
        var captures = new List<Task>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        string richDefinition = await File.ReadAllTextAsync(sourceFootprint, deadline.Token);
        await File.WriteAllTextAsync(siblingPath, richDefinition, deadline.Token);
        await File.WriteAllTextAsync(targetPath, CheckedFootprintFixture, deadline.Token);
        await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}\n", deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "fp-lib-table"),
            $"(fp_lib_table (version 7) (lib (name \"Fixture\") (type \"KiCad\") (uri \"{library.Replace("\\", "/", StringComparison.Ordinal)}\") (options \"\") (descr \"\")))\n", deadline.Token);

        byte[] targetBefore = await File.ReadAllBytesAsync(targetPath, deadline.Token);
        byte[] siblingBefore = await File.ReadAllBytesAsync(siblingPath, deadline.Token);

        Process StartNative(string logName)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = projectDirectory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment["DISPLAY"] = display;
            start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(scratch, "config");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(scratch, "cache");
            foreach (string argument in new[] { "--new", "--automation", instanceId, "--api-socket", socket,
                "--automation-log", Path.Combine(evidence, logName), "--software-rendering", project })
                start.ArgumentList.Add(argument);
            Process process = Process.Start(start)!;
            captures.Add(Capture(process.StandardOutput, Path.Combine(evidence, logName + ".stdout.log")));
            captures.Add(Capture(process.StandardError, Path.Combine(evidence, logName + ".stderr.log")));
            return process;
        }

        async Task<NativeClient> Connect(Process process)
        {
            var client = new NativeClient(new NngTransport(), "ipc://" + socket);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                limit.Token.ThrowIfCancellationRequested();
                Assert.IsFalse(process.HasExited, "The disposable KiCad process exited before serving the footprint API.");
                try { await client.HandshakeAsync(limit.Token); return client; }
                catch (NngException) { }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                await Task.Delay(100, limit.Token);
            }
        }

        try
        {
            native = StartNative("footprint-restart-old-native.log");
            NativeClient nativeClient = await Connect(native);
            string oldEpoch = nativeClient.Epoch;

            var registry = new InstanceRegistry(new NngTransport(), Path.Combine(scratch, "registry"));
            await registry.AttachAsync("ipc://" + socket, instanceId, deadline.Token);
            Assert.AreEqual(oldEpoch, registry.Get(instanceId).Epoch);

            Assert.IsTrue(NativeKeyboard.HasWindow(display, native.Id, "KiCad"), "KiCad manager window did not become visible.");
            NativeKeyboard.SchematicShortcut(display, native.Id, "click", "KiCad", false, true,
                clickFromLeft: 330, clickFromTop: 230);
            using var editorWait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            editorWait.CancelAfter(TimeSpan.FromSeconds(20));
            while (!NativeKeyboard.HasWindow(display, native.Id, "Footprint Editor"))
                await Task.Delay(100, editorWait.Token);

            await using var mcp = await RestartMcpClient.StartAsync(stateDirectory,
                Path.Combine(evidence, "footprint-restart-first-mcp.stderr.log"), deadline.Token);
            JsonElement attached = await mcp.Tool("kicad_instance_attach", new { endpoint = "ipc://" + socket, expectedInstanceId = instanceId });
            RequireRestartToolSuccess(attached);
            string epoch = oldEpoch;
            JsonElement document = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(new DocumentSpecifier
            {
                Type = DocumentType.DoctypeFootprint,
                Project = new() { Name = "fixture", Path = projectDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar },
                LibId = new() { LibraryNickname = "Fixture", EntryName = CheckedFootprintName }
            }));
            string documentJson = document.GetRawText();

            JsonElement snapshot = await mcp.Tool("kicad_library_snapshot_read", new
            { instanceId, expectedProcessEpoch = epoch, documentJson, libraryNickname = "Fixture", entryName = CheckedFootprintName, forcedRefresh = true });
            RequireRestartToolSuccess(snapshot);
            JsonElement snapshotData = snapshot.GetProperty("structuredContent");
            var expectedState = SchematicJson.Parser.Parse<NativeCapabilityFootprintLibraryCheckedState>(snapshotData.GetProperty("checkedState").GetRawText());
            JsonElement definition = await mcp.Tool("kicad_footprint_definition_read", new
            { instanceId, expectedProcessEpoch = epoch, documentJson, source = "persisted_library" });
            RequireRestartToolSuccess(definition);
            var candidate = BoardJson.Parser.Parse<FootprintInstance>(definition.GetProperty("structuredContent").GetProperty("footprint").GetRawText());
            candidate.Definition.Attributes.Description = "replacement-write-probe";
            var request = new NativeCapabilityFootprintLibraryCheckedWrite
            {
                Document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson),
                ProcessEpoch = epoch,
                OperationId = operationId,
                ExpectedSnapshotSha256 = expectedState.StateSha256,
                ExpectedState = expectedState,
                Footprint = candidate,
                Overwrite = true
            };
            string requestJson = SchematicJson.Formatter.Format(request);
            await File.WriteAllTextAsync(Path.Combine(evidence, "footprint-restart-original-request.json"), requestJson, deadline.Token);
            await File.WriteAllBytesAsync(Path.Combine(evidence, "footprint-restart-target-before.kicad_mod"), targetBefore, deadline.Token);

            int settledSockets = await mcp.SettledSocketCount(deadline.Token);
            Task<JsonElement> pending = mcp.StartTool("kicad_footprint_library_write_checked", new
            {
                instanceId, expectedProcessEpoch = epoch, documentJson, footprintJson = BoardJson.Formatter.Format(candidate),
                operationId, expectedSnapshotSha256 = expectedState.StateSha256,
                expectedStateJson = SchematicJson.Formatter.Format(expectedState), overwrite = true
            });

            // The checked writer performs an admission handshake and then opens a
            // second native request. Pause as soon as that second exchange appears;
            // this leaves the direct-file write admitted while its native result is
            // still unresolved.
            await WaitUntil(() => mcp.SocketCount() > settledSockets, "the checked footprint admission handshake", deadline.Token);
            await WaitUntil(() => mcp.SocketCount() == settledSockets, "the checked footprint admission handshake to settle", deadline.Token);
            if (!pending.IsCompleted)
            {
                await WaitUntil(() => mcp.SocketCount() > settledSockets, "the checked footprint native write exchange", deadline.Token);
                Assert.AreEqual(0, SendRestartSignal(native.Id, RestartSigStop), "The native footprint process could not be paused.");
                await WaitUntil(() => ReadRestartProcessState(native.Id) == 'T', "the native footprint process to pause", deadline.Token);
            }

            if (!pending.IsCompleted)
                Assert.AreEqual(0, SendRestartSignal(native.Id, RestartSigKill), "The native footprint process could not be killed.");
            await native.WaitForExitAsync(deadline.Token);
            JsonElement lost = await pending.WaitAsync(TimeSpan.FromSeconds(60), deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "footprint-restart-lost-write.json"), lost.GetRawText(), deadline.Token);
            JsonElement lostResult = lost.GetProperty("result");
            Assert.IsTrue(lostResult.GetProperty("isError").GetBoolean(), lost.GetRawText());
            string lostCode = lostResult.GetProperty("structuredContent").GetProperty("errorCode").GetString()!;
            Assert.IsTrue(lostCode is "instance_exited" or "operation_outcome_unknown", lost.GetRawText());
            await mcp.DisposeAsync();

            if (File.Exists(socket)) File.Delete(socket);
            replacement = StartNative("footprint-restart-replacement-native.log");
            NativeClient replacementClient = await Connect(replacement);
            string replacementEpoch = replacementClient.Epoch;
            Assert.AreNotEqual(oldEpoch, replacementEpoch, "The replacement native process must have a new epoch.");

            await using var reconnect = await RestartMcpClient.StartAsync(stateDirectory,
                Path.Combine(evidence, "footprint-restart-reconnect-mcp.stderr.log"), deadline.Token);
            RequireRestartToolSuccess(await reconnect.Tool("kicad_instance_attach", new { endpoint = "ipc://" + socket, expectedInstanceId = instanceId }));
            JsonElement durable = await reconnect.Tool("kicad_footprint_library_checked_receipt", new
            { instanceId, expectedProcessEpoch = replacementEpoch, documentJson, operationId, expectedRequestJson = requestJson });
            await File.WriteAllTextAsync(Path.Combine(evidence, "footprint-restart-durable-receipt.json"), durable.GetRawText(), deadline.Token);
            JsonElement durableData = durable.GetProperty("structuredContent");
            Assert.AreEqual(oldEpoch, durableData.GetProperty("durableProcessEpoch").GetString(), durable.GetRawText());
            Assert.IsFalse(durableData.GetProperty("nativeReceiptAvailable").GetBoolean(), durable.GetRawText());
            Assert.IsFalse(durableData.GetProperty("retryDispatched").GetBoolean(), durable.GetRawText());
            Assert.IsTrue(durableData.GetProperty("durableRecovery").GetBoolean(), durable.GetRawText());
            if (durable.GetProperty("isError").GetBoolean())
            {
                Assert.AreEqual("operation_outcome_unknown", durableData.GetProperty("errorCode").GetString(), durable.GetRawText());
                Assert.AreEqual("unknown", durableData.GetProperty("outcome").GetString(), durable.GetRawText());
            }
            else
            {
                Assert.IsTrue(durableData.GetProperty("found").GetBoolean(), durable.GetRawText());
                Assert.AreEqual("terminal-receipt", durableData.GetProperty("retrySafety").GetString(), durable.GetRawText());
            }

            JsonElement staleNative = await reconnect.Tool("kicad_footprint_receipt", new
            { instanceId, expectedProcessEpoch = oldEpoch, operationId });
            await File.WriteAllTextAsync(Path.Combine(evidence, "footprint-restart-stale-native-lookup.json"), staleNative.GetRawText(), deadline.Token);
            Assert.IsTrue(staleNative.GetProperty("isError").GetBoolean(), staleNative.GetRawText());
            Assert.AreEqual("stale_process_epoch", staleNative.GetProperty("structuredContent").GetProperty("errorCode").GetString(), staleNative.GetRawText());

            CollectionAssert.AreEqual(siblingBefore, await File.ReadAllBytesAsync(siblingPath, deadline.Token),
                "The sibling footprint must remain byte-for-byte unchanged after replacement recovery.");
            byte[] targetAfter = await File.ReadAllBytesAsync(targetPath, deadline.Token);
            bool unchanged = targetAfter.SequenceEqual(targetBefore);
            string targetText = Encoding.UTF8.GetString(targetAfter);
            Assert.AreEqual(1, targetText.Split("(footprint ", StringSplitOptions.None).Length - 1,
                "The target library file must contain exactly one footprint entry after an interrupted write.");
            Assert.IsTrue(unchanged || targetText.Contains("replacement-write-probe", StringComparison.Ordinal),
                "An interrupted write must leave the original complete file or a complete candidate, never a partial mutation.");
            await File.WriteAllBytesAsync(Path.Combine(evidence, "footprint-restart-target-after.kicad_mod"), targetAfter, deadline.Token);
        }
        finally
        {
            if (native is not null && !native.HasExited)
            {
                SendRestartSignal(native.Id, RestartSigContinue);
                native.Kill(entireProcessTree: true);
            }
            if (replacement is not null && !replacement.HasExited) replacement.Kill(entireProcessTree: true);
            if (native is not null) await native.WaitForExitAsync(CancellationToken.None);
            if (replacement is not null) await replacement.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(captures).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            if (!displayProcess.HasExited) displayProcess.Kill(entireProcessTree: true);
            await displayProcess.WaitForExitAsync(CancellationToken.None);
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendRestartSignal(int processId, int signal);

    private static char ReadRestartProcessState(int processId)
    {
        string stat = File.ReadAllText($"/proc/{processId}/stat");
        return stat[stat.LastIndexOf(')') + 2];
    }

    private static async Task WaitUntil(Func<bool> condition, string what, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            try { await Task.Delay(20, limit.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new AssertFailedException($"Timed out waiting for {what}."); }
        }
    }

    private static async Task Capture(StreamReader reader, string path)
    {
        await using var output = File.Create(path);
        await reader.BaseStream.CopyToAsync(output);
    }

    private static void RequireRestartToolSuccess(JsonElement result) =>
        Assert.IsFalse(result.TryGetProperty("isError", out JsonElement error) && error.GetBoolean(), result.GetRawText());

    private sealed class RestartMcpClient : IAsyncDisposable
    {
        private readonly Process process;
        private readonly CancellationToken token;
        private readonly CancellationTokenSource diagnosticsStop = new();
        private readonly Task diagnostics;
        private int nextId;
        private bool disposed;

        private RestartMcpClient(Process process, string stderr, CancellationToken token)
        {
            this.process = process; this.token = token;
            diagnostics = Task.Run(async () =>
            {
                await using var output = File.Create(stderr);
                try { await process.StandardError.BaseStream.CopyToAsync(output, diagnosticsStop.Token); }
                catch (OperationCanceledException) when (diagnosticsStop.IsCancellationRequested) { }
            });
        }

        internal static async Task<RestartMcpClient> StartAsync(string state, string stderr, CancellationToken token)
        {
            ProcessStartInfo start = SyncHarnessProcessTests.ProductionStartInfo();
            start.UseShellExecute = false; start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            start.StandardInputEncoding = new UTF8Encoding(false);
            start.StandardOutputEncoding = new UTF8Encoding(false);
            start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = state;
            var client = new RestartMcpClient(Process.Start(start)!, stderr, token);
            await client.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "kicad-footprint-restart-journey", version = "1" } });
            await client.process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}".AsMemory(), token);
            await client.process.StandardInput.FlushAsync(token);
            return client;
        }

        internal Task<JsonElement> StartTool(string name, object arguments) =>
            Request("tools/call", new { name, arguments });

        internal async Task<JsonElement> Tool(string name, object arguments) =>
            (await StartTool(name, arguments).WaitAsync(TimeSpan.FromSeconds(60), token)).GetProperty("result").Clone();

        internal int SocketCount() => Directory.EnumerateFileSystemEntries($"/proc/{process.Id}/fd").Count(fd =>
        {
            try { return new FileInfo(fd).LinkTarget?.StartsWith("socket:", StringComparison.Ordinal) == true; }
            catch (IOException) { return false; }
        });

        internal async Task<int> SettledSocketCount(CancellationToken cancellation)
        {
            int previous = SocketCount();
            while (true)
            {
                await Task.Delay(100, cancellation);
                int current = SocketCount();
                if (current == previous) return current;
                previous = current;
            }
        }

        private async Task<JsonElement> Request(string method, object parameters)
        {
            int id = ++nextId;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(token);
                if (line is null) throw new IOException("The MCP fixture exited before replying.");
                using JsonDocument parsed = JsonDocument.Parse(line);
                if (!parsed.RootElement.TryGetProperty("id", out JsonElement received)
                    || received.ValueKind != JsonValueKind.Number || received.GetInt32() != id) continue;
                return parsed.RootElement.Clone();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try { process.StandardInput.Close(); }
            catch (ObjectDisposedException) { }
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            diagnosticsStop.Cancel();
            try { await diagnostics.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            process.Dispose(); diagnosticsStop.Dispose();
        }
    }
}
